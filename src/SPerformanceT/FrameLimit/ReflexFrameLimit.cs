using System;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using NVIDIA;

namespace SPerformanceT.FrameLimit
{
    /// <summary>
    /// A frame cap that works with NVIDIA Reflex on.
    ///
    /// EFT drops its own cap in raid whenever Reflex is On or On + Boost
    /// (GraphicsSettings.ChangeFramerate sets Application.targetFrameRate to -1), and greys the
    /// limit out in its settings. NVIDIA's Reflex component has its own limiter, the
    /// <c>intervalUs</c> it passes to NvReflex_Plugin_SetSleepMode as minimumIntervalUs, but
    /// EFT never sets it, so it is always 0.
    ///
    /// The patch is a prefix on Reflex.SleepCoroutine's MoveNext, the one place that reads the
    /// field. Every Reflex instance must carry the same value: the "previous" values it compares
    /// against are static, so two cameras disagreeing would re-send SetSleepMode every frame. And
    /// Reflex.OnDisable stops a new enumerator instead of the running one, so coroutines outlive
    /// their component being disabled. Patching MoveNext covers every running coroutine.
    /// </summary>
    internal sealed class ReflexFrameLimit
    {
        private const string Section = "Frame Limit";

        private static ConfigEntry<int> _fps;
        private static ManualLogSource _log;
        private static AccessTools.FieldRef<object, Reflex> _owner;
        private static uint _target;
        private static uint _lastLogged = uint.MaxValue;

        public static void Start(ConfigFile config, ManualLogSource log, Harmony harmony)
        {
            _log = log;
            _fps = config.Bind(Section, "Reflex frame limit (FPS)", 0, new ConfigDescription(
                "Caps the frame rate with NVIDIA Reflex's own limiter, for use with Reflex On or "
                + "On + Boost. EFT ignores its own frame limit in raid when Reflex is on. 0 = no cap. "
                + "With Reflex off this does nothing; use the game's own limit instead. With G-Sync, "
                + "a few FPS under your refresh rate is the usual choice (e.g. 158 at 165 Hz). "
                + "Applies immediately.",
                new AcceptableValueRange<int>(0, FrameLimitMath.MaxFps)));
            _fps.SettingChanged += (_, __) => _target = FrameLimitMath.IntervalUs(_fps.Value);
            _target = FrameLimitMath.IntervalUs(_fps.Value);

            MethodInfo moveNext;
            try
            {
                moveNext = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(Reflex), "SleepCoroutine"));
                _owner = AccessTools.FieldRefAccess<Reflex>(moveNext.DeclaringType, "<>4__this");
            }
            catch (Exception e)
            {
                log.LogError("Reflex frame limit is off: could not find Reflex.SleepCoroutine. " + e.Message);
                return;
            }

            harmony.Patch(moveNext, prefix: new HarmonyMethod(typeof(ReflexFrameLimit), nameof(SleepCoroutinePrefix)));
            log.LogInfo(_fps.Value > 0
                ? "Reflex frame limit set to " + _fps.Value + " FPS (" + _target + " us)."
                : "Reflex frame limit is off (0).");
        }

        private static void SleepCoroutinePrefix(object __instance)
        {
            Reflex reflex = _owner(__instance);
            if (reflex == null || reflex.intervalUs == _target)
                return;

            reflex.intervalUs = _target;
            if (_lastLogged != _target)
            {
                _lastLogged = _target;
                _log.LogInfo(_target == 0
                    ? "Reflex frame limit removed."
                    : "Reflex frame limit applied: " + _fps.Value + " FPS (" + _target + " us between frames).");
            }
        }
    }
}
