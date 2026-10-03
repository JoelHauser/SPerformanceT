using System;
using BepInEx;
using HarmonyLib;
using SPerformanceT.Affinity;
using SPerformanceT.FrameLimit;
using SPerformanceT.GarbageCollection;

namespace SPerformanceT
{
    /// <summary>
    /// Performance tweaks for SPT. Each feature lives in its own folder with its own config
    /// section: <see cref="CoreAffinity"/> (CPU core pinning for hybrid Intel CPUs, and matching
    /// Unity's job workers to it), <see cref="ReflexFrameLimit"/> (a frame cap that works with
    /// NVIDIA Reflex on) and <see cref="RaidGc"/> (experimental, opt-in raid GC control).
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SPerformanceTPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.mybutthasarash.sperformancet";
        public const string PluginName = "SPerformanceT";

        /// <summary>Must match the csproj's Version. pack.ps1 refuses to pack if they disagree.</summary>
        public const string PluginVersion = "0.3.4";

        private CoreAffinity _affinity;
        private RaidGc _raidGc;

        private void Awake()
        {
            var harmony = new Harmony(PluginGuid);

            // Each feature starts on its own. An exception escaping Awake is logged only to
            // Player.log, not LogOutput.log, and stops every feature after it. 0.3.2 lost its GC
            // section that way to an invalid config key.
            _affinity = StartFeature("CPU core pinning", () => CoreAffinity.Start(Config, Logger, harmony));
            StartFeature("Reflex frame limit", () => { ReflexFrameLimit.Start(Config, Logger, harmony); return this; });
            _raidGc = StartFeature("Raid GC control", () => RaidGc.Start(Config, Logger, harmony));
        }

        private T StartFeature<T>(string name, Func<T> start) where T : class
        {
            try
            {
                return start();
            }
            catch (Exception e)
            {
                Logger.LogError(name + " failed to start and is off: " + e);
                return null;
            }
        }

        private void Update()
        {
            _raidGc?.Tick();
        }

        private void OnApplicationQuit()
        {
            _affinity?.OnQuit();
        }
    }
}
