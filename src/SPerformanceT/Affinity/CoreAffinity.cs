using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Jobs.LowLevel.Unsafe;

namespace SPerformanceT.Affinity
{
    /// <summary>
    /// Keeps the game on the P-cores of a hybrid Intel CPU, and optionally the SPT server on the
    /// E-cores. Windows 10 has no Thread Director, so without this the game's main thread can land
    /// on an E-core and that frame runs late.
    ///
    /// The game sets its own affinity in EFT.Utilities.CpuAffinityHelper.SetAffinityToLogicalCoresEnabled,
    /// bound to the "only use physical cores" setting: when the settings load, and again on quit.
    /// With the setting off it hands the process every core, which would undo ours, so a postfix
    /// on that method puts ours back every time it runs.
    /// </summary>
    internal sealed class CoreAffinity
    {
        private const string Section = "CPU Cores";
        private const string ServerExe = "SPT.Server.exe";
        private const string GameMethod = "EFT.Utilities.CpuAffinityHelper:SetAffinityToLogicalCoresEnabled";

        private static CoreAffinity _instance;

        private readonly ManualLogSource _log;
        private readonly List<LogicalCpu> _cpus;
        private readonly ulong _systemMask;
        private readonly ConfigEntry<AffinityMode> _mode;
        private readonly ConfigEntry<string> _customMask;
        private readonly ConfigEntry<bool> _pinServer;
        private readonly ConfigEntry<bool> _matchWorkers;

        private bool _gameTouched;
        private bool _workersTouched;
        private bool _serverTouched;
        private ulong _serverRestoreMask;
        private bool _serverMissingLogged;
        private string _lastProblem;
        private bool _quitting;

        private CoreAffinity(ConfigFile config, ManualLogSource log, List<LogicalCpu> cpus, ulong systemMask)
        {
            _log = log;
            _cpus = cpus;
            _systemMask = systemMask;

            _mode = config.Bind(Section, "Mode", AffinityMode.PerformanceCores, new ConfigDescription(
                "Which CPU threads the game may run on.\n"
                + "PerformanceCores: every P-core thread, no E-cores (does nothing on a CPU without E-cores).\n"
                + "PerformanceCoresNoHyperthreading: one thread per P-core. An experiment; usually slower.\n"
                + "Custom: the mask below.\n"
                + "Off: leave it to the game and Windows. Switching to Off puts back every core straight away."));

            _customMask = config.Bind(Section, "Custom mask", "0xFFFF", new ConfigDescription(
                "Only used when Mode is Custom. A hex mask of logical processors: bit 0 is CPU 0. "
                + "0xFFFF is CPUs 0-15."));

            _pinServer = config.Bind(Section, "Put the SPT server on the E-cores", true, new ConfigDescription(
                "Moves SPT.Server.exe onto the E-cores so its bot generation during a raid does not "
                + "compete with the game. Only on a CPU with E-cores, and only when the server runs on "
                + "this PC. It is given every core back when the game closes or this is turned off."));

            _matchWorkers = config.Bind(Section, "Match Unity worker threads to the pinned cores", true, new ConfigDescription(
                "Unity starts one job worker per CPU thread minus one (19 on a 20-thread CPU). Once the game "
                + "is limited to the P-cores, that is more workers than threads, and the main thread can be "
                + "pushed aside. With this on, the worker count becomes the pinned threads minus one "
                + "(15 for 16 threads). Off restores Unity's default."));

            _mode.SettingChanged += (_, __) => Apply("setting changed", true);
            _customMask.SettingChanged += (_, __) => Apply("setting changed", true);
            _pinServer.SettingChanged += (_, __) => Apply("setting changed", true);
            _matchWorkers.SettingChanged += (_, __) => Apply("setting changed", true);
        }

        public static CoreAffinity Start(ConfigFile config, ManualLogSource log, Harmony harmony)
        {
            List<LogicalCpu> cpus;
            ulong systemMask;
            try
            {
                cpus = Native.ReadTopology();
                Native.GetMasks(Native.CurrentProcess, out _, out systemMask);
            }
            catch (Exception e)
            {
                log.LogError("CPU core pinning is off: could not read the CPU layout. " + e.Message);
                return null;
            }

            var affinity = new CoreAffinity(config, log, cpus, systemMask);
            _instance = affinity;
            log.LogInfo("CPU: " + AffinityPlan.DescribeTopology(cpus) + ".");

            var target = AccessTools.Method(GameMethod);
            if (target == null)
            {
                log.LogWarning("Could not find " + GameMethod + ". Pinning still applies now, but if the "
                               + "game resets its own affinity later it will stay reset.");
            }
            else
            {
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(CoreAffinity), nameof(GameSetAffinityPostfix)));
            }

            affinity.Apply("startup", true);
            return affinity;
        }

        private static void GameSetAffinityPostfix()
        {
            if (_instance != null && !_instance._quitting)
                _instance.Apply("the game set its own affinity", false);
        }

        public void OnQuit()
        {
            _quitting = true;
            if (!_serverTouched)
                return;
            try
            {
                IntPtr server = Native.OpenByExeName(ServerExe);
                try
                {
                    if (server != IntPtr.Zero)
                        Native.SetMask(server, _serverRestoreMask);
                }
                finally
                {
                    Native.Close(server);
                }
            }
            catch (Exception e)
            {
                _log.LogWarning("Could not give " + ServerExe + " its cores back: " + e.Message);
            }
        }

        private void Apply(string reason, bool reportProblems)
        {
            try
            {
                Plan plan = AffinityPlan.Build(_cpus, _systemMask, _mode.Value, _customMask.Value, _pinServer.Value);

                if (reportProblems && plan.Problem != null && plan.Problem != _lastProblem)
                    _log.LogWarning(plan.Problem);
                _lastProblem = plan.Problem;

                if (reportProblems && plan.GameMask == 0 && _mode.Value != AffinityMode.Off
                    && _mode.Value != AffinityMode.Custom && !_gameTouched)
                {
                    _log.LogInfo("No E-cores on this CPU, so there is nothing to pin. Doing nothing.");
                }

                ApplyGame(plan.GameMask, reason);
                ApplyWorkers(plan.GameMask);
                ApplyServer(plan.ServerMask, reason);
            }
            catch (Exception e)
            {
                _log.LogError("Setting CPU affinity failed (" + reason + "): " + e);
            }
        }

        private void ApplyGame(ulong mask, string reason)
        {
            ulong want;
            if (mask != 0)
                want = mask;
            else if (_gameTouched)
                want = _systemMask; // turned off: hand back every core, as the game itself would
            else
                return;

            Native.GetMasks(Native.CurrentProcess, out ulong current, out _);
            if (current != want)
            {
                Native.SetMask(Native.CurrentProcess, want);
                _log.LogInfo("Game now runs on threads " + AffinityPlan.Describe(want) + " (" + AffinityPlan.Hex(want)
                             + "), was " + AffinityPlan.Describe(current) + ". Reason: " + reason + ".");
            }
            _gameTouched = mask != 0;
        }

        private void ApplyWorkers(ulong gameMask)
        {
            if (_matchWorkers.Value && gameMask != 0)
            {
                int want = Math.Min(AffinityPlan.WorkerCount(gameMask), JobsUtility.JobWorkerMaximumCount);
                int current = JobsUtility.JobWorkerCount;
                if (current != want)
                {
                    JobsUtility.JobWorkerCount = want;
                    _log.LogInfo("Unity job workers: " + current + " -> " + JobsUtility.JobWorkerCount
                                 + ", to fit the " + (AffinityPlan.WorkerCount(gameMask) + 1) + " pinned threads.");
                }
                _workersTouched = true;
            }
            else if (_workersTouched)
            {
                JobsUtility.ResetJobWorkerCount();
                _workersTouched = false;
                _log.LogInfo("Unity job workers back to the default: " + JobsUtility.JobWorkerCount + ".");
            }
        }

        private void ApplyServer(ulong mask, string reason)
        {
            if (mask == 0 && !_serverTouched)
                return;

            IntPtr server = Native.OpenByExeName(ServerExe);
            if (server == IntPtr.Zero)
            {
                if (!_serverMissingLogged)
                {
                    _log.LogInfo(ServerExe + " is not running on this PC, so only the game is pinned.");
                    _serverMissingLogged = true;
                }
                return;
            }

            try
            {
                Native.GetMasks(server, out ulong current, out ulong serverSystem);
                ulong want = mask != 0 ? mask : _serverRestoreMask;
                if (mask != 0 && !_serverTouched)
                    _serverRestoreMask = serverSystem;

                if (current != want)
                {
                    Native.SetMask(server, want);
                    _log.LogInfo(ServerExe + " now runs on threads " + AffinityPlan.Describe(want) + " ("
                                 + AffinityPlan.Hex(want) + "), was " + AffinityPlan.Describe(current)
                                 + ". Reason: " + reason + ".");
                }
                _serverTouched = mask != 0;
            }
            finally
            {
                Native.Close(server);
            }
        }
    }
}
