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
- **Run in game 2026-10-02:** with Reflex OnAndBoost and a 120 cap, 'Reflex frame limit applied: 120 FPS' was logged, the cap held, and
  frametimes felt smooth (user report). Not tested: whether the cap holds with Reflex On but Boost off.
- The affinity postfix was seen firing at quit (the game's OnApplicationQuit runs before ours). EFT's own
  errors.log shows 'Can't set affinity mask' from its physical-cores option, which confirms that option is broken here.

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

**0.3.0 run in game 2026-10-02 (Streets, ~6 min):** heap 2.83 GB at raid start, peak 3.24 GB. But EFT had
**30.5 GB private / 19.4 GB working set** (no leak: flat while idle afterwards), system commit 50.3 / 56.8 GB, and
3.76 GB of free RAM at raid start. The free-RAM trigger fired at 2.87 GB, freed only 0.23 GB, and fell back to
automatic GC for nothing. That cleanup took 13 frames over 4.5 s (~350 ms a frame), almost certainly because
marking a heap that was partly paged out stalls on page faults.

**0.3.1** drops free RAM as a trigger (it's only a log warning now) and adds: a cleanup every N GB of heap growth
(GC goes back off), and an emergency on low *commit* (`MEMORYSTATUSEX.ullAvailPageFile`) that leaves
automatic GC on. Cleanups log their longest frame, so we can see whether the 2 ms slicing holds. Not run in game yet.

**0.3.1 run in game 2026-10-02 (Streets, 6.5 min): rough, "lots of hitches and freezes".** There were 22 cleanups (17 on
inventory open, 5 at 1 GB of growth). The heap fell back to ~2.9 GB after every one (live data), and cleanups took
2-188 frames with the **longest frame 33-353 ms** despite 2 ms slices. About 8 GB of garbage in 6.5 min is ~20 MB/s,
from the mods. So GC cannot stay off for a raid with this mod list, and cleaning up a 2.9 GB live heap by
hand stalls whatever the slice. The smooth 0.2.0 raid had automatic GC on all raid (SkillsExtended's option) with
EFT's 10 ms slice.

**0.3.2:** raid GC control is opt-in ("Experimental: keep GC off during raids", default false). The slice setting is
"0 = game default" and doesn't change EFT's 10 ms unless set. Both keys were renamed so the old saved values (true / 2 ms)
don't carry over. Joel's SkillsExtended option was set back to true (backup `com.cj.skillsextended.cfg.bak-2026-10-02`).
Next: confirm 0.3.2 feels like 0.2.0. Then, if needed, test "Match Unity worker threads" (19 -> 15) on its own,
since it was also new in 0.3.x.

**0.3.2 had a startup crash:** its key `GC time slice (ms, 0 = game default)` contains `=`, which BepInEx refuses
(`= 
 \t \ " ' [ ]`). The ArgumentException left Awake after the frame limit started, so the GC section never
loaded, and it was visible only in Player.log. The Reserve raid on 0.3.2 therefore ran with GC fully hands-off
(EFT plus SkillsExtended, 10 ms slice), plus core pinning and 15 workers. **0.3.3** renames the key, starts each
feature in its own try/catch (logged to LogOutput), and `ConfigKeyTests` reads every `.Bind(` in src and rejects
forbidden characters.

**0.3.4:** automatic pinning needs at least `MinPerformanceThreads` = 12 P-core threads, so Core Ultra 200 (8 P threads, no HT),
Lunar Lake and P-series laptops are left alone, server pinning included. Custom mode still pins. `Plan.Note` carries the
informational "doing nothing" reason, separate from `Problem` (a warning). `CpuCompatibilityTests` models these layouts
from core counts; only the 12700K was read off hardware. Joel's 0.3.2 Reserve raid "ran fine" (with GC hands-off, pinning
and 15 workers), but frame times climbed over the raid (his words: about 0.3 ms early to 5+ ms late, metric unknown).
That is still open and needs a long raid with ModProfiler open from the hideout and exporting every 60 s (enabled in its cfg).

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
