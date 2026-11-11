# scats-overlay

A minimal hardware monitor that lives on the Windows desktop, plus a small pixel cat that sits on
your taskbar.

The stats are the opposite of a gaming OSD: thin, low-contrast text with tiny sparklines, sitting
*behind* your windows like part of the wallpaper. An accent color only shows up when something runs
hot or maxes out.

```
cpu    3%   ⎽⎽⎼⎽   45° · 4.35 GHz · 33 W
gpu    3%   ⎽⎽⎽⎽   34° · 1.9 / 12 GB · 19 W
ram   49%   ⎼⎼⎼⎼   15 / 31 GB
disk   0%   ⎽⎽⎽⎽   r 0 KB/s · w 2.1 MB/s
free               c 309 GB · d 1.1 TB
net                ↓ 0 KB/s · ↑ 0 KB/s
fans               1300 · 1300 rpm
up                 11d 15h
```

## Features

**The widget**
- CPU load/temp/clock/power, GPU load/temp/VRAM/power, RAM, disk activity and free space,
  network up/down, fan speeds and uptime. Sensors your hardware doesn't report are hidden.
- Transparent, click-through, never takes focus, not in the taskbar or Alt+Tab, kept behind all
  windows, and survives Win+D / "show desktop" and Alt+Tab peek.
- One widget per monitor, anchored anywhere (corners, edges, center).
- Low overhead: about 1% of one CPU core with everything on.
  - NVIDIA GPUs are read through NVML (fast) instead of LibreHardwareMonitor's slower path.
  - Disk activity comes from Windows performance counters, so it never wakes or ticks a hard drive.
  - Only the sensors you actually show are polled.
- Everything is configured in `config.json`, and edits apply live.

**The cat**
- A blocky pixel cat that sits on the taskbar and types little thoughts into a terminal-style bubble
  (`sudo pet`, `rm -rf mice`, or a live stat like `gpu 34°`).
- Takes random naps; hover the mouse over it to wake it up.
- Near-white over apps, a softer gray over the desktop and terminals.
- When a fullscreen app (game, video) covers its screen, it gallops off to the other monitor and naps
  there while the app has focus, then heads home a minute after you leave it. With a single monitor
  it runs off the screen instead and comes back a few seconds after you leave the app.
- When a notification or tray flyout covers it, it peeks over the top edge with its paws on it,
  rides it down if it slides away, and gets back up on the taskbar once it's gone.
- Can be mirrored to sit at the left end of the taskbar instead.
- Optional kitten: a tiny second cat that sits by the big one and now and then leaps over it
  to the other side. It naps when the big cat naps.

## Requirements

- Windows 10/11, x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- Admin rights and the [PawnIO](https://pawnio.eu/) driver for CPU temperature/clock/power and
  motherboard fans (`winget install namazso.PawnIO`). Without them the widget still runs, just with
  fewer sensors.

## Build and run

```powershell
cd src\Overlay
dotnet build -c Release
.\bin\Release\net8.0-windows\Overlay.exe              # asks for admin (UAC)
.\bin\Release\net8.0-windows\Overlay.exe --no-elevate  # no UAC prompt, fewer sensors
.\bin\Release\net8.0-windows\Overlay.exe --dump        # write every sensor to sensors.txt
```

The tray icon is the only way to interact with it (it's click-through). Double-click it (or pick
**Settings...**) for a settings window where everything can be changed or switched off, applied
live. The menu also has edit/reload config.json, dump sensors, preview the game overlay, exit.

## Start with Windows

```powershell
.\scripts\install-autostart.ps1     # Task Scheduler task: runs at logon, elevated, no UAC prompt
.\scripts\uninstall-autostart.ps1   # removes it
```

The task points at the Release build in this repo. Pass `-ExePath` to point it somewhere else.

## Configuration

`config.json` is created next to the exe on first run. Highlights:

| Setting | What it does |
|---|---|
| `showWidget` | Show the stats widget at all |
| `catOnly` | Just the cat: no widget, no admin prompt, only CPU load read (no PawnIO needed) |
| `position`, `offsetX`, `offsetY` | Anchor (`TopLeft` … `Center` … `BottomRight`) and nudge |
| `monitors` | `"primary"`, `"all"`, or `"1,2"` |
| `opacity`, `textColor`, `accentColor`, `fontFamily`, `fontSize` | The look |
| `metrics` | Which rows to show, in order: `cpu gpu ram disk drives net fans uptime` |
| `thresholds` | When values turn the accent color |
| `cat.enabled`, `cat.monitors`, `cat.offsetX`, `cat.scale` | The cat and where it sits |
| `cat.pose` | `"auto"` (random naps), `"sit"`, or `"sleep"` |
| `cat.awakeMinutes`, `cat.napMinutes` | Nap timing ranges `[min, max]` |
| `cat.avoidFullscreen`, `cat.runBetweenScreens` | Screen hopping around fullscreen apps |
| `cat.thoughts`, `cat.clickReactions`, `cat.moods` | Thought bubble, squish on click, CPU-driven mood |
| `cat.peekOverPopups` | Peek over notifications and flyouts that cover it |
| `cat.flip` | Mirror it and sit it at the left end of the taskbar |
| `cat.kitten.enabled`, `cat.kitten.scale` | The kitten, and its size (0.5 by default) |
| `cat.kitten.jumpEverySeconds` | How often it leaps over the big cat: random within `[min, max]` |

## Project layout

```
src/Overlay/
  Program.cs, WidgetHost.cs   startup and wiring
  Config/                     config model + live-reloading store
  Sensors/                    background polling: LibreHardwareMonitor, NVML, PDH, network
  UI/                         widget window, presenter, sparklines, tray icon, the cat
  Interop/                    Win32: click-through windows, fullscreen detection, window events
  Modes/                      desktop vs. game-overlay mode (auto-detection not done yet)
scripts/                      autostart install/uninstall
```

## Not done yet

- **Game mode:** an automatic compact always-on-top overlay when a fullscreen game runs. The
  structure and a manual preview exist (tray → Preview game overlay); detection is a stub.
- **Motherboard fans** on some boards (e.g. Gigabyte B650 with an ITE chip) aren't picked up yet.

## Credits

- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0)
  for sensor access.
- [PawnIO](https://pawnio.eu/) for the kernel driver it uses.
