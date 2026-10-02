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
    /// NVIDIA Reflex on) and <see cref="RaidGc"/> (garbage collection kept off in raids, with
    /// memory safety limits).
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SPerformanceTPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.mybutthasarash.sperformancet";
        public const string PluginName = "SPerformanceT";

        /// <summary>Must match the csproj's Version. pack.ps1 refuses to pack if they disagree.</summary>
        public const string PluginVersion = "0.3.0";

        private CoreAffinity _affinity;
        private RaidGc _raidGc;

        private void Awake()
        {
            var harmony = new Harmony(PluginGuid);
            _affinity = CoreAffinity.Start(Config, Logger, harmony);
            ReflexFrameLimit.Start(Config, Logger, harmony);
            _raidGc = RaidGc.Start(Config, Logger, harmony);
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
