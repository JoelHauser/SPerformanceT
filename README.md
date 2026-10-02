# SPerformanceT

Performance tweaks for SPT (4.1.x). One BepInEx client plugin, no server mod.

## CPU core pinning (hybrid Intel CPUs)

12th gen and newer Intel CPUs mix fast P-cores with slower E-cores. Windows 10 can't steer
work between them well (Windows 11's Thread Director does that), so Tarkov's main thread
sometimes lands on an E-core and that frame runs late, which you feel as a micro-stutter.

SPerformanceT keeps the game on the P-cores. It also moves the SPT server onto the E-cores
by default, so the server's bot generation during a raid doesn't compete with the game.

- **Detects the cores itself.** It reads the core layout from Windows, so there is no mask to set
  up. On a CPU without E-cores (AMD, older Intel) it does nothing.
- **Stays applied.** The game sets its own affinity when its settings load and when it closes.
  SPerformanceT puts its setting back each time.
- **Gives the server its cores back** when the game closes or the option is turned off.
- **Works on Windows 11 too, but helps less there,** since Thread Director already does most of this.

### Settings (F12, section "CPU Cores")

| Setting | Default | |
| --- | --- | --- |
| Mode | PerformanceCores | `PerformanceCores`, `PerformanceCoresNoHyperthreading` (one thread per P-core, experimental), `Custom`, `Off`. Changes apply immediately. |
| Custom mask | 0xFFFF | Hex mask of logical CPUs, used only in `Custom` mode. |
| Put the SPT server on the E-cores | true | Only when the server runs on the same PC. |
| Match Unity worker threads to the pinned cores | true | Unity starts 19 workers on a 20-thread CPU; pinned to 16 threads, that's more workers than threads. Sets it to 15. |

### Checking it works

`BepInEx\LogOutput.log` should contain lines like:

```
CPU: 8 P-cores (threads 0-15), 4 E-cores (threads 16-19).
Game now runs on threads 0-15 (0xFFFF), was 0-19. Reason: startup.
SPT.Server.exe now runs on threads 16-19 (0xF0000), was 0-19. Reason: startup.
```

You can also check in Task Manager: Details, right-click `EscapeFromTarkov.exe`, Set affinity.

### Measuring it

The difference shows up in 1% lows and frame-time spikes, not average FPS. Use CapFrameX or
PresentMon on the same map and route with Mode `PerformanceCores` and then `Off`.

## Frame limit with NVIDIA Reflex

EFT ignores its own frame limit during a raid whenever Reflex is On or On + Boost, and greys
the setting out. NVIDIA's Reflex includes its own frame limiter, but EFT never turns it on.
SPerformanceT does.

| Setting (F12, "Frame Limit") | Default | |
| --- | --- | --- |
| Reflex frame limit (FPS) | 0 (off) | The cap. Applies immediately. Only works while Reflex is on; with Reflex off, use the game's own limit. |

With G-Sync, set it a few FPS under your refresh rate, for example 158 at 165 Hz. Otherwise,
pick a cap your PC can hold steadily on your heaviest map.

The log confirms it once it takes effect in game:
```
Reflex frame limit applied: 158 FPS (6329 us between frames).
```
If that line never appears, Reflex isn't running. Check that it's On in the graphics settings.
## Garbage collection during raids

EFT is designed to turn garbage collection (GC) off for the whole raid on PCs with 25 GB of RAM
or more. Two things undo that:

- **SPT** turns GC back on the first time you open your inventory in a raid, and it stays on.
- **SkillsExtended** forces GC on for the whole raid by default ("Automatic garbage collection
  during raids"). Turn that off if you use SkillsExtended.

With GC on, each collection step can take up to 10 ms of a frame (EFT's `gc-max-time-slice`).
That's longer than a whole frame at 120 FPS, and you feel it as stutter.

SPerformanceT keeps GC off during raids. The cleanup SPT runs when you open your inventory still
happens, but spread over several frames in small slices instead of one 25 ms frame.

**It won't let you run out of memory.** Garbage collection only manages part of the game's memory.
On Streets with a big mod list, EFT uses about 30 GB, and only ~3 GB of that is the managed heap GC
can clean up. The rest is assets. So the safety checks watch that heap, plus Windows' *commit*
(RAM plus page file), which is what actually crashes a game when it runs out:

- Each time the heap grows by 1 GB, it runs a cleanup in small slices, then GC goes back off.
- If the heap reaches 6 GB and a cleanup can't bring it under 4.5 GB, automatic GC stays on for
  the rest of the raid.
- If free commit falls below 2 GB, it runs a cleanup and leaves automatic GC on for the rest of the
  raid. That's how the game ran before this mod, so the worst case is the old stutter, never a crash.
- Low free RAM only produces a warning in the log. GC can't free asset memory, but closing other
  programs can.

| Setting (F12, "Garbage Collection") | Default | |
| --- | --- | --- |
| Keep GC off during raids | true | Turn GC back off after the inventory cleanup. |
| Time slice (ms) | 2 | The most GC may take in one frame, for this mod's cleanups and for Unity's automatic GC (menus, or after the fallback). |
| Safety: clean up after the heap grows by (GB) | 1 | |
| Safety: managed heap limit (GB) | 6 | |
| Safety: minimum free commit (GB) | 2 | |
The log reports each raid:
```
Raid start: GC Disabled, heap 2.83 GB, free RAM 3.76 GB, free commit 9.10 GB.
Cleanup done (inventory opened): heap 3.40 GB -> 2.95 GB over 41 frame(s), 340 ms, longest frame 10.2 ms. GC off again.
Raid end: peak heap 1.70 GB, 3 inventory cleanup(s), 0 safety cleanup(s), 0 after another mod turned GC on.
```