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

### Which CPUs it pins

| CPU | Automatic mode |
| --- | --- |
| Intel 12th-14th gen with E-cores (i5-12600K and up, H-series laptops like the i7-12700H) | Game on the P-cores, server on the E-cores. Tested on an i7-12700K. |
| Intel without E-cores, any AMD Ryzen | Does nothing. Every core is the same kind. |
| Core Ultra 200 (285K, 265K), Lunar Lake, P-series laptops (i7-1260P) | Does nothing. These have fewer than 12 P-core threads, and the game needs their E-cores. |
| Core Ultra 100 laptops (155H: P, E and low-power E-cores) | Game on the P-cores, server on both kinds of E-core. |
| Two-chiplet Ryzen X3D (7950X3D, 9950X3D) | Not detected. Use Mode `Custom` to put the game on the V-Cache chiplet yourself (often `0xFFFF`). |

On Windows 11 it works the same, but helps less: Windows 11 already moves games onto the P-cores.
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
## Garbage collection during raids (experimental, off by default)

EFT is designed to turn garbage collection (GC) off for the whole raid. In SPT it ends up on
anyway: SPT turns it back on the first time you open your inventory, and SkillsExtended keeps it
on by default.

SPerformanceT 0.3.0-0.3.1 tried to keep it off, cleaning up in small slices instead. **On a
Streets run with a big mod list, that was worse.** The mods produced about 20 MB of garbage a
second, so GC can't stay off for a whole raid. Every cleanup of the ~3 GB of live data caused
frames of 100-350 ms, however small the slices were. Unity's own automatic GC, which is what
you get when you leave it alone, handled the same load more smoothly.

So it's now an opt-in experiment, and the time slice defaults to EFT's own value.

| Setting (F12, "Garbage Collection") | Default | |
| --- | --- | --- |
| Experimental: keep GC off during raids | false | Leave this off unless you want to experiment. |
| GC time slice in ms (0 for the game default) | 0 | The game default is 10 ms. |
| Safety settings | | Used only by the experiment: heap growth cleanup, heap limit, free commit. |

If you use SkillsExtended, leave its "Automatic garbage collection during raids" **on** (its default).