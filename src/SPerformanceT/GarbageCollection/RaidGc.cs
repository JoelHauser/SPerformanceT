using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Scripting;

namespace SPerformanceT.GarbageCollection
{
    /// <summary>
    /// Keeps garbage collection off during raids, as EFT intends, without risking running out of memory.
    ///
    /// EFT turns GC off at raid start (InGameMemoryManagement.GCEnabled = false, in GameSpawned) on
    /// machines with 25 GB of RAM or more. Two things undo that:
    /// - SPT's MemoryCollectionPatch sets GarbageCollector.GCMode = Enabled and runs a 25 ms
    ///   CollectIncremental every time the inventory opens in raid, and never turns GC off again.
    /// - SkillsExtended's "Automatic garbage collection during raids" (default true) rewrites EFT's
    ///   Disabled into Enabled in a prefix on the GCMode setter.
    /// With GC on, boot.config's gc-max-time-slice=10 lets each collection take up to 10 ms of a
    /// frame, which is the stutter.
    ///
    /// Cleanups run in Manual mode (only explicit collections), one small CollectIncremental slice
    /// per frame, then GC goes back to Disabled. They run when the inventory opens (replacing SPT's
    /// single 25 ms one), when the heap has grown by a set amount, and when it reaches a limit. If
    /// Windows' commit runs low, or a heap-limit cleanup can't get the heap down, automatic GC stays
    /// on for the rest of the raid. That is how it ran before this mod, so the worst case is the old
    /// stutter, never running out of memory. See GcPolicy for why free physical RAM is not a trigger.
    /// </summary>
    internal sealed class RaidGc
    {
        private const string Section = "Garbage Collection";
        private const string SptInventoryCleanup = "SPT.Custom.Patches.MemoryCollectionPatch:PatchPostfix";
        private const double LowRamWarningGb = 2.0;

        private static RaidGc _instance;

        private readonly ManualLogSource _log;
        private readonly FieldInfo _inRaid;
        private readonly ConfigEntry<bool> _keepOff;
        private readonly ConfigEntry<float> _sliceMs;
        private readonly ConfigEntry<float> _heapLimitGb;
        private readonly ConfigEntry<float> _growthGb;
        private readonly ConfigEntry<float> _minFreeCommitGb;

        /// <summary>The game's latest request through InGameMemoryManagement.GCEnabled.</summary>
        private bool _gameWantsOff;

        private bool _managing;
        private bool _fallback;
        private bool _overridden;
        private bool _applying;
        private bool _turnedOnByOther;
        private bool _inventoryOpened;

        private bool _cleaning;
        private string _cleanTrigger;
        private SafetyReason _cleanReason;
        private long _heapBefore;
        private float _cleanStarted;
        private int _cleanFrames;
        private float _cleanLongestFrameMs;

        private float _nextCheck;
        private float _lastSafety = float.NegativeInfinity;
        private long _heapBaseline;
        private long _peakHeap;
        private int _inventoryCleanups;
        private int _safetyCleanups;
        private int _otherCleanups;
        private bool _lowRamWarned;

        private RaidGc(ConfigFile config, ManualLogSource log, FieldInfo inRaid)
        {
            _log = log;
            _inRaid = inRaid;

            _keepOff = config.Bind(Section, "Keep GC off during raids", true, new ConfigDescription(
                "EFT turns garbage collection off for the whole raid, but SPT turns it back on the first "
                + "time you open your inventory, and it then stays on. With this on, that inventory cleanup "
                + "still happens, spread over a few frames, and GC goes back off afterwards. The memory "
                + "safety limits below apply either way."));

            _sliceMs = config.Bind(Section, "Time slice (ms)", 2f, new ConfigDescription(
                "The most time garbage collection may take in one frame, for this mod's cleanups and for "
                + "Unity's automatic GC whenever it is on (menus, or a raid where the safety fallback kicked "
                + "in). EFT ships with 10 ms, more than a whole frame at 120 FPS. Lower is smoother, but "
                + "each cleanup takes more frames.",
                new AcceptableValueRange<float>(0.5f, 10f)));

            _growthGb = config.Bind(Section, "Safety: clean up after the heap grows by (GB)", 1f, new ConfigDescription(
                "With GC off, the game's managed memory only grows. Each time it has grown by this much since "
                + "the last cleanup, a cleanup runs in small slices and GC goes back off. This keeps it "
                + "bounded without turning automatic GC on.",
                new AcceptableValueRange<float>(0.25f, 8f)));

            _heapLimitGb = config.Bind(Section, "Safety: managed heap limit (GB)", 6f, new ConfigDescription(
                "If the game's managed memory reaches this during a raid, a cleanup runs. If it can't bring "
                + "it under 75% of this, automatic GC stays on for the rest of the raid.",
                new AcceptableValueRange<float>(2f, 24f)));

            _minFreeCommitGb = config.Bind(Section, "Safety: minimum free commit (GB)", 2f, new ConfigDescription(
                "Commit is the memory Windows has promised to programs, RAM plus page file. Running out of "
                + "it is what crashes a game. If less than this is left during a raid, a cleanup runs and "
                + "automatic GC stays on for the rest of the raid.",
                new AcceptableValueRange<float>(0.5f, 16f)));

            _sliceMs.SettingChanged += (_, __) => ApplySlice();
        }

        public static RaidGc Start(ConfigFile config, ManualLogSource log, Harmony harmony)
        {
            Type memory = AccessTools.TypeByName("InGameMemoryManagement");
            MethodInfo setter = memory == null ? null : AccessTools.PropertySetter(memory, "GCEnabled");
            FieldInfo inRaid = AccessTools.Field(AccessTools.TypeByName("EFT.InGameStatus"), "InRaid");
            if (setter == null || inRaid == null)
            {
                log.LogError("Raid GC control is off: could not find InGameMemoryManagement.GCEnabled or "
                             + "EFT.InGameStatus.InRaid.");
                return null;
            }

            var gc = new RaidGc(config, log, inRaid);
            _instance = gc;
            harmony.Patch(setter, postfix: new HarmonyMethod(typeof(RaidGc), nameof(GcEnabledPostfix)));

            MethodInfo sptCleanup = AccessTools.Method(SptInventoryCleanup);
            if (sptCleanup != null)
                harmony.Patch(sptCleanup, prefix: new HarmonyMethod(typeof(RaidGc), nameof(SptInventoryCleanupPrefix)));
            else
                log.LogInfo("SPT's inventory GC patch was not found; its cleanups (if any) are caught when they turn GC on.");

            GarbageCollector.GCModeChanged += gc.OnModeChanged;
            gc.ApplySlice();

            log.LogInfo("GC: incremental " + (GarbageCollector.isIncremental ? "yes" : "NO (cleanups will be one blocking pass)")
                        + ", time slice " + gc._sliceMs.Value + " ms. Raid cleanups every "
                        + gc._growthGb.Value + " GB of heap growth; safety limits: heap " + gc._heapLimitGb.Value
                        + " GB, free commit " + gc._minFreeCommitGb.Value + " GB.");
            return gc;
        }

        private static void GcEnabledPostfix(bool value)
        {
            if (_instance != null)
                _instance._gameWantsOff = !value;
        }

        /// <summary>
        /// SPT's postfix on MenuTaskBar.OnScreenChanged. On opening the inventory in raid it turns GC
        /// on and runs one 25 ms CollectIncremental in a single frame. While this mod is managing the
        /// raid, it is skipped and the same cleanup runs here in small slices instead. Otherwise SPT's
        /// own code runs untouched.
        /// </summary>
        private static bool SptInventoryCleanupPrefix(object[] __args)
        {
            RaidGc gc = _instance;
            if (gc == null || !gc._managing || gc._overridden || gc._fallback || !gc._keepOff.Value)
                return true;
            if (__args != null && __args.Length > 0 && __args[0] != null && __args[0].ToString() == "Inventory")
                gc._inventoryOpened = true;
            return false;
        }

        private void OnModeChanged(GarbageCollector.Mode mode)
        {
            // Only a flag here: changing the mode from inside its own change event is asking for trouble.
            if (!_applying && mode == GarbageCollector.Mode.Enabled)
                _turnedOnByOther = true;
        }

        public void Tick()
        {
            try
            {
                bool shouldManage = _gameWantsOff && (bool)_inRaid.GetValue(null);
                if (shouldManage != _managing)
                {
                    if (shouldManage)
                        BeginRaid();
                    else
                        EndRaid();
                }
                if (!_managing || _overridden)
                {
                    _turnedOnByOther = false;
                    return;
                }

                if (_cleaning)
                {
                    StepCleanup();
                    return;
                }

                if (_inventoryOpened)
                {
                    _inventoryOpened = false;
                    _inventoryCleanups++;
                    StartCleanup("inventory opened", SafetyReason.None);
                    return;
                }

                if (_turnedOnByOther)
                {
                    _turnedOnByOther = false;
                    if (_keepOff.Value && !_fallback && GarbageCollector.GCMode == GarbageCollector.Mode.Enabled)
                    {
                        _otherCleanups++;
                        StartCleanup("GC was turned on by another mod", SafetyReason.None);
                        return;
                    }
                }

                if (Time.realtimeSinceStartup >= _nextCheck)
                {
                    _nextCheck = Time.realtimeSinceStartup + 1f;
                    SafetyCheck();
                }
            }
            catch (Exception e)
            {
                _log.LogError("Raid GC control failed and is off for this raid: " + e);
                _overridden = true;
                SetMode(GarbageCollector.Mode.Enabled);
            }
        }

        private void BeginRaid()
        {
            _managing = true;
            _fallback = false;
            _overridden = false;
            _cleaning = false;
            _turnedOnByOther = false;
            _inventoryOpened = false;
            _inventoryCleanups = 0;
            _safetyCleanups = 0;
            _otherCleanups = 0;
            _lowRamWarned = false;
            _lastSafety = float.NegativeInfinity;
            _nextCheck = 0f;
            long heap = GC.GetTotalMemory(false);
            _peakHeap = heap;
            _heapBaseline = heap;

            ReadMemory(out long freeRam, out long freeCommit);
            GarbageCollector.Mode mode = GarbageCollector.GCMode;
            if (mode == GarbageCollector.Mode.Enabled)
            {
                // EFT asked for off and it is on. Either EFT refused (under 25 GB of RAM) or another
                // mod's setter prefix rewrote it. Fighting a prefix on the setter would loop, so leave it.
                _overridden = true;
                _log.LogWarning("Raid start: the game asked for GC off but it is still on. Either another mod "
                                + "is forcing it (SkillsExtended's 'Automatic garbage collection during raids' "
                                + "does this) or the PC has under 25 GB of RAM, where EFT keeps it on. Leaving "
                                + "GC alone for this raid. Heap " + GcPolicy.Gb(heap) + ".");
                return;
            }
            _log.LogInfo("Raid start: GC " + mode + ", heap " + GcPolicy.Gb(heap) + ", free RAM "
                         + GcPolicy.Gb(freeRam) + ", free commit " + GcPolicy.Gb(freeCommit) + ".");
        }

        private void EndRaid()
        {
            _managing = false;
            _cleaning = false;
            if (GarbageCollector.GCMode == GarbageCollector.Mode.Manual)
            {
                // Ours, from a cleanup cut short. Hand back what the game last asked for.
                SetMode(_gameWantsOff ? GarbageCollector.Mode.Disabled : GarbageCollector.Mode.Enabled);
            }
            _log.LogInfo("Raid end: peak heap " + GcPolicy.Gb(_peakHeap) + ", " + _inventoryCleanups
                         + " inventory cleanup(s), " + _safetyCleanups + " safety cleanup(s), "
                         + _otherCleanups + " after another mod turned GC on"
                         + (_fallback ? ", automatic GC was left on after a safety cleanup." : "."));
        }

        private void SafetyCheck()
        {
            long heap = GC.GetTotalMemory(false);
            if (heap > _peakHeap)
                _peakHeap = heap;
            ReadMemory(out long freeRam, out long freeCommit);

            if (!_lowRamWarned && freeRam >= 0 && freeRam < LowRamWarningGb * GcPolicy.BytesPerGb)
            {
                _lowRamWarned = true;
                _log.LogWarning("Free RAM is down to " + GcPolicy.Gb(freeRam) + " (the game has "
                                + GcPolicy.Gb(heap) + " of managed heap; the rest of its memory is assets, which "
                                + "GC can't free). Windows is paging, which causes hitches. Closing the browser, "
                                + "editor and Discord while playing helps.");
            }

            if (_fallback || GarbageCollector.GCMode == GarbageCollector.Mode.Enabled)
                return; // GC is collecting by itself; nothing to guard

            SafetyReason reason = GcPolicy.Check(heap, _heapBaseline, freeCommit,
                _heapLimitGb.Value, _growthGb.Value, _minFreeCommitGb.Value);
            if (reason == SafetyReason.None)
                return;
            if (Time.realtimeSinceStartup - _lastSafety < GcPolicy.SafetyCooldownSeconds)
                return;

            _lastSafety = Time.realtimeSinceStartup;
            _safetyCleanups++;
            string trigger;
            switch (reason)
            {
                case SafetyReason.LowCommit:
                    trigger = "safety: free commit down to " + GcPolicy.Gb(freeCommit) + " (minimum " + _minFreeCommitGb.Value + " GB)";
                    break;
                case SafetyReason.HeapLimit:
                    trigger = "safety: heap reached " + GcPolicy.Gb(heap) + " (limit " + _heapLimitGb.Value + " GB)";
                    break;
                default:
                    trigger = "heap grew " + GcPolicy.Gb(heap - _heapBaseline) + " since the last cleanup";
                    break;
            }
            StartCleanup(trigger, reason);
        }

        private void StartCleanup(string trigger, SafetyReason reason)
        {
            _cleaning = true;
            _cleanTrigger = trigger;
            _cleanReason = reason;
            _heapBefore = GC.GetTotalMemory(false);
            _cleanStarted = Time.realtimeSinceStartup;
            _cleanFrames = 0;
            _cleanLongestFrameMs = 0f;
            SetMode(GarbageCollector.Mode.Manual);
            StepCleanup();
        }

        private void StepCleanup()
        {
            // unscaledDeltaTime is the previous frame, which held the previous slice.
            if (_cleanFrames > 0)
                _cleanLongestFrameMs = Math.Max(_cleanLongestFrameMs, Time.unscaledDeltaTime * 1000f);
            _cleanFrames++;

            bool more = GarbageCollector.CollectIncremental(GcPolicy.SliceNanoseconds(_sliceMs.Value));
            if (more)
                return;

            _cleaning = false;
            long after = GC.GetTotalMemory(false);
            _heapBaseline = after;
            string took = _cleanFrames + " frame(s), "
                          + ((Time.realtimeSinceStartup - _cleanStarted) * 1000f).ToString("0") + " ms, longest frame "
                          + _cleanLongestFrameMs.ToString("0.0") + " ms";

            if (GcPolicy.SafeToTurnOffAgain(_cleanReason, after, _heapLimitGb.Value))
            {
                SetMode(GarbageCollector.Mode.Disabled);
                _log.LogInfo("Cleanup done (" + _cleanTrigger + "): heap " + GcPolicy.Gb(_heapBefore) + " -> "
                             + GcPolicy.Gb(after) + " over " + took + ". GC off again.");
            }
            else
            {
                _fallback = true;
                SetMode(GarbageCollector.Mode.Enabled);
                _log.LogWarning("Cleanup done (" + _cleanTrigger + "): heap " + GcPolicy.Gb(_heapBefore) + " -> "
                                + GcPolicy.Gb(after) + " over " + took + ". To be safe, automatic GC stays on "
                                + "for the rest of this raid.");
            }
        }

        private void SetMode(GarbageCollector.Mode mode)
        {
            _applying = true;
            try
            {
                GarbageCollector.GCMode = mode;
            }
            finally
            {
                _applying = false;
            }
            if (GarbageCollector.GCMode != mode && !_overridden)
            {
                _overridden = true;
                _cleaning = false;
                _log.LogWarning("Another mod overrode the GC mode (asked for " + mode + ", got "
                                + GarbageCollector.GCMode + "). Leaving GC alone for the rest of this raid.");
            }
        }

        private void ApplySlice()
        {
            GarbageCollector.incrementalTimeSliceNanoseconds = GcPolicy.SliceNanoseconds(_sliceMs.Value);
        }

        /// <summary>Free physical RAM and free commit (limit minus charge). -1 each if unknown.</summary>
        private static void ReadMemory(out long freeRam, out long freeCommit)
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (GlobalMemoryStatusEx(ref status))
            {
                freeRam = (long)status.AvailPhys;
                freeCommit = (long)status.AvailPageFile;
            }
            else
            {
                freeRam = -1;
                freeCommit = -1;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
