# SPerformanceT: working notes for Claude

Performance tweaks for SPT 4.1.x as one BepInEx plugin. Each feature gets its own folder under
`src/SPerformanceT/` and its own config section. So far there is one feature: `Affinity/`.

## CPU core pinning (Affinity/)

Built 2026-10-02 for Joel's i7-12700K on **Windows 10** (no Thread Director). The game goes on
the P-cores and `SPT.Server.exe` on the E-cores.

What it rests on, read from the live client (`H:\SPT4.1.X`, patched assembly, 16,233,472 bytes):

- `EFT.Utilities.CpuAffinityHelper.SetAffinityToLogicalCoresEnabled(bool)` is the game's own
  affinity call. It is bound to the "only use physical cores" setting
  (`GameSettingsGroup.CreateBindings`, so it fires when the settings load) and called with `false`
  from `OnApplicationQuit`. With `false` it sets every core, so a postfix on it puts ours back.
  The name is readable, so it is resolved by name and Assembly-CSharp is not referenced.
- That setting is **broken on hybrid CPUs**. It assumes two threads per core, so it builds bits up to
  2 x 12 cores = bit 22 on a 20-thread CPU, and Windows rejects the mask. Even if it worked it
  would include the E-cores.
- The game already sets **High priority** itself (on focus and at raid start), so the plugin doesn't.
- Windows 10 **does** report EfficiencyClass on Alder Lake (P = 1, E = 0), via
  `GetSystemCpuSetInformation`. Verified on this machine. Struct offsets are in `Native.cs`.

Verified: 29 unit tests (`AffinityPlan.cs` is pure and linked into the test project). The built
DLL's native half was driven from Windows PowerShell 5.1 (same .NET Framework) against a `ping`
process: topology read, find by exe name, and `SetProcessAffinityMask` all worked as
Windows reported them.

**Run in game 2026-10-02 (0.1.0):** all three startup lines logged (topology, game to 0-15, server to 16-19)
and a raid was played. No reset line ever appeared, so the game's settings bind ran before our Awake.
The postfix is installed but has not been seen firing. Whether it helps still needs CapFrameX 1% lows, on vs Off.

Ideas not built: match Unity's job worker count (`JobsUtility.JobWorkerCount`, sized for 20
threads at startup) to the 16 pinned threads; server-side `HeapPreAllocationEnabled` (EFT
disables GC in raid when RAM >= 25 GB, see `InGameMemoryManagement` and `GameSpawned`).

## Reflex frame limit (FrameLimit/), 0.2.0

- `GraphicsSettings.ChangeFramerate(bool inSession)`: in raid, with Reflex On or OnAndBoost, it sets
  `Application.targetFrameRate = -1`. The settings UI blocks the limit field while Reflex is on.
- `NVIDIA.Reflex` (com.nvidia.reflex.Runtime.dll, not touched by the delta, so referenced directly)
  has `public uint intervalUs`, sent as minimumIntervalUs to `NvReflex_Plugin_SetSleepMode`. EFT
  never writes it. `ReflexController.CreateReflex` puts one Reflex component on the main camera and one on the optic camera.
- `SleepCoroutine` compares against **static** previous values, so every instance must agree, or
  SetSleepMode is re-sent every frame. `OnDisable` does `StopCoroutine(SleepCoroutine())` on a new
  enumerator, so coroutines survive disabling. Hence the prefix on `<SleepCoroutine>d__23.MoveNext`,
  which sets `<>4__this.intervalUs` (both checked with Cecil).
- **Run in game 2026-10-02:** with Reflex OnAndBoost and a 120 cap, 'Reflex frame limit applied: 120 FPS' was logged, the cap held, and\n  frametimes felt smooth (user report). Not tested: whether the cap holds with Reflex On but Boost off.\n- The affinity postfix was seen firing at quit (the game's OnApplicationQuit runs before ours). EFT's own\n  errors.log shows 'Can't set affinity mask' from its physical-cores option, which confirms that option is broken here.

## Raid GC (GarbageCollection/), 0.3.0

Read off the live client and mods on 2026-10-02:
- EFT: `GameSpawned` sets `InGameMemoryManagement.GCEnabled = false` (refused under 25 GB of RAM). The main menu
  sets it true. `Collect(force)` flips it true then back within one call, so raid state is
  `_gameWantsOff && EFT.InGameStatus.InRaid`, sampled once per frame, never the setter alone.
- `boot.config`: `gc-max-time-slice=10` (incremental GC, 10 ms per frame) and `job-worker-count=19`.
- SPT `SPT.Custom.Patches.MemoryCollectionPatch.PatchPostfix(EEftScreenType)`, a postfix on
  `MenuTaskBar.OnScreenChanged`: on Inventory with a GameWorld it does `GCMode = Enabled; CollectIncremental(25 ms)`
  and never turns GC off again. RaidGc prefixes it (returns false while managing) and runs a sliced cleanup itself.
- SkillsExtended `AutomaticCollection`: Harmony prefixes on the `GarbageCollector.GCMode` and `GCEnabled` setters.
  With its option "Automatic garbage collection during raids" (default **true**) it rewrites Disabled into
  Enabled for the whole raid. Joel turned it off. RaidGc doesn't fight it: SetMode reads back, and on a
  mismatch it warns and stops managing that raid.
- Cleanups run in `GarbageCollector.Mode.Manual` (explicit collections only), `CollectIncremental(slice)`
  once per frame until it returns false, then Disabled.
- Safety (`GcPolicy`, pure and tested): heap >= limit, or free RAM < floor, starts a cleanup (30 s cooldown).
  Afterwards GC goes back off only if the heap is under 75% of the limit (and, for low RAM, at least 25% was
  freed). Otherwise automatic GC stays on for the rest of the raid.
- `GarbageCollector.GCModeChanged` only sets a flag. The mode is never changed inside the event.

Not run in game yet. Watch the raid start, cleanup and raid end lines, and whether the heap grows in long raids.

## Build

```
scripts\pack.ps1 -SPTPath H:\SPT4.1.X            # build, test, reference check, zip
scripts\pack.ps1 -SPTPath H:\SPT4.1.X -Install   # and copy into BepInEx\plugins
```

Run it from PowerShell, not Bash. The version is in the csproj and in `SPerformanceTPlugin.PluginVersion`.

## Publishing

- Remote https://github.com/JoelHauser/SPerformanceT.git, `main`. It was empty when cloned.
- GUID `com.mybutthasarash.sperformancet`.
- Commit as `Joel Hauser <jhauser@bostonlightsource.com>`, with messages written to a file and passed by `git commit -F`.
- 1.0.0 is for after a live run.
