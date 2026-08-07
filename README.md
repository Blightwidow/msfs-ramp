<p align="center">
  <img src="docs/banners/social-preview.svg" alt="RAMP — RAM Airport Preloader" width="100%">
</p>

# RAMP — RAM Airport Preloader

A background utility for **Microsoft Flight Simulator 2020** that eliminates the freeze/stutter
that happens when you fly within ~25 NM of an add-on airport whose files live on a slow **HDD**.

## Why the freeze happens (and what this actually fixes)

At ~25 NM MSFS starts streaming the airport's scenery: BGLs, model libraries, textures. When those
files sit on a spinning HDD, MSFS's loader thread **blocks on slow random reads**, and the frame
that needs those assets stalls — you get a multi-second freeze.

The problem is **disk I/O latency, not CPU**. Adding threads to MSFS wouldn't help (and isn't
possible — MSFS is closed source). Parallel random reads on an HDD are actually *slower*.

This tool takes a different, safe angle: it **pre-reads the airport's files into the Windows
filesystem page cache before MSFS asks for them**. When MSFS then streams them at 25 NM, the OS
serves the bytes from RAM instead of the HDD, so there's no stall.

It does **not** inject into MSFS, hook its process, or modify any scenery. It only reads files and
discards the bytes; the side effect is a hot OS cache. That's the whole mechanism — it cannot crash
or corrupt the sim.

> Honest caveat: the real cure is putting MSFS on an SSD. This tool exists because a size-constrained
> HDD install can't do that. It targets separate add-on airport **packages** (which map cleanly to an
> ICAO). It cannot isolate handcrafted airports baked into the giant `fs-base` packages.

## How it works

```
SimConnect (aircraft lat/lon, 1 Hz)
      │
      ▼
distance to every indexed airport ── within OuterRadius (default 60 NM)? ──► observe(package, distance)
      │
      ▼
priority queue, sorted by live distance ──► closest airport first
      │
      ▼
background low-priority I/O thread ──► sequential read each file ──► warm OS page cache
      (THREAD_MODE_BACKGROUND_BEGIN so reads yield to MSFS's own I/O; RAM-budget capped)
```

Prefetch fires at 60 NM so files are warm before MSFS's 25 NM load. Pending packages are warmed
**closest-first** — distances refresh every poll, so the airport you're actually approaching (your
destination) always wins over farther enroute airports and can't be starved by them when RAM is tight.
Fly ~10 NM beyond the outer radius and a package is **unloaded** (its RAM-budget share is freed and it
returns to *Unloaded* in the list), so a long flight never exhausts the budget.

Airports closer than an **inner radius** (default 5 NM) are skipped — the sim has already streamed
them (you spawned there), so re-reading is wasted I/O. Any airport you pass within that radius stays
suppressed while you're near it, so a departure isn't pointlessly re-warmed on climb-out; fly far
enough away that it unloads and it re-arms, so a return or divert still gets a warm cache.

The app is a **WinForms tray application**: it keeps running in the background even with MSFS closed,
and connects/reconnects automatically each time the sim starts.

Warming only runs once the sim reports you're **in a flight** (SimConnect `CAMERA STATE`) — never
during the loading screen or menus, so it never competes with MSFS's own load for the disk.

### SimBrief integration (optional)

Enter your numeric **SimBrief Pilot ID** in Settings and RAMP fetches your latest OFP on launch and
at every flight start (menu → aircraft). It **pins the arrival and alternate airports** and warms
them **first, regardless of distance** — so your destination's scenery warms during cruise headroom
instead of racing the 60→25 NM window on approach. Pinned airports show a coral dot in the list and
a `PINNED` counter. If there's no internet or no OFP, RAMP simply falls back to proximity warming.
Departure isn't warmed: you spawn there, so the sim has already streamed it. Blank ID = feature off.

## Build

> **Windows only.** SimConnect and the page-cache warming calls are Windows APIs; there is no
> macOS/Linux build. The language (C#) doesn't change that.

### Prerequisites

- **Windows 10/11, x64.**
- **.NET Framework 4.8 developer pack** — https://dotnet.microsoft.com/download/dotnet-framework/net48
- A build toolchain, either:
  - **Visual Studio 2022** (Community is fine) with the *.NET desktop development* workload, or
  - **.NET SDK** (`dotnet` CLI) — https://dotnet.microsoft.com/download — which includes MSBuild.
- **MSFS 2020 SDK** installed, for the SimConnect libraries (enable Dev Mode in the sim →
  *Help → SDK Installer*, or download the SDK installer).

Two files are **not** in this repo (Microsoft-licensed SDK binaries + a large dataset — both
`.gitignore`d). Fetch them after cloning:

### Step 1 — SimConnect libraries (from the MSFS 2020 SDK)

Copy into `MsfsAirportPreloader/lib/` (create the folder):

| File | Source in SDK | Kind |
|------|---------------|------|
| `Microsoft.FlightSimulator.SimConnect.dll` | `C:\MSFS SDK\SimConnect SDK\lib\managed\` | managed wrapper (referenced at build) |
| `SimConnect.dll` | `C:\MSFS SDK\SimConnect SDK\lib\` | native (copied next to the exe) |

(`C:\MSFS SDK` is the default SDK path; adjust if you installed elsewhere.)

### Step 2 — Airport coordinate database (free, public domain)

Download <https://davidmegginson.github.io/ourairports-data/airports.csv> and save it as
`MsfsAirportPreloader/airports.csv`.

### Step 3 — Compile

**CLI:**
```
cd MsfsAirportPreloader
dotnet build -c Release
```

**Visual Studio:** open `MsfsAirportPreloader/MsfsAirportPreloader.csproj`, set configuration to
`Release`/`x64`, Build.

Output: `bin/Release/net48/RAMP.exe`. `SimConnect.dll`, `airports.csv`, and
`preloader.ini` are copied next to it automatically.

### Build troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| `metadata file '...Microsoft.FlightSimulator.SimConnect.dll' could not be found` | Step 1 not done, or wrong folder. DLL must be at `MsfsAirportPreloader/lib/`. |
| Builds, but at runtime `Unable to load DLL 'SimConnect.dll'` | Native `SimConnect.dll` missing next to the exe. Confirm it's in `lib/` so the build copies it. |
| `The reference assemblies for .NETFramework,Version=v4.8 were not found` | Install the .NET Framework 4.8 **developer pack** (targeting pack), not just the runtime. |
| `BadImageFormatException` at startup | Architecture mismatch — build **x64** (managed + native SimConnect are 64-bit). |
| Runtime: `no airports loaded from ...airports.csv` | Step 2 not done, or wrong header. Use the OurAirports file unmodified. |

## Using it

Launch `RAMP.exe` (with MSFS open or closed). It scans your scenery packages once, then waits for the
sim and warms the airports you approach. Closing the window hides it to the tray; it keeps running.

**→ See [docs/USAGE.md](docs/USAGE.md)** for the full walkthrough: the window, the seven states, the
settings, and the tray. Settings live in `preloader.ini` next to the exe and are managed entirely by
the in-app Settings panel — no hand-editing needed.

## Limitations / notes

- **MSFS 2020 only** (paths + SimConnect SDK). MSFS 2024 would need different UserCfg paths and SDK.
- Warms whole **add-on packages** attributed to an ICAO by folder name / manifest title. Airports
  with no separate package (default handcrafted ones inside `fs-base`) aren't isolated.
- Cache warming is best-effort: if free RAM is tight the OS may evict pages before MSFS reads them.
  Lower `RamBudgetMegabytes` or close other apps if that happens.
- Tune `OuterRadius` up if your HDD can't warm a 2 GB airport in the ~7 minutes between 60 and 25 NM
  at approach speed.

## Project layout

```
MsfsAirportPreloader/
  Program.cs               WinForms entry point
  MainForm.cs              main window: custom chrome, states, airport list, tray
  SettingsForm.cs          editable settings dialog
  AboutForm.cs             About popup (credits + links)
  Theme.cs                 palette (light/dark), fonts, paint primitives
  ThemedControls.cs        toggle, slider, rounded/caption buttons, dark menu renderer
  Engine.cs                orchestrates everything on background threads
  SimConnectClient.cs      SimConnect connection w/ auto-reconnect, position + camera at 1 Hz
  SimBriefClient.cs        fetch latest SimBrief OFP, extract arrival + alternate ICAOs
  PackagePathResolver.cs   find InstalledPackagesPath from UserCfg.opt
  PackageIndex.cs          scan packages, map ICAO → on-disk files (via layout.json)
  AirportDatabase.cs       OurAirports CSV → ICAO coordinates
  Prefetcher.cs            closest-first page-cache warmer w/ load/unload state machine
  AirportState.cs          per-airport status model (Unloaded/Queued/Warming/Loaded/Skipped)
  StartupRegistry.cs       "start with Windows" registry entry
  Geo.cs                   haversine distance
  Config.cs                preloader.ini load/save
```

See also [docs/USAGE.md](docs/USAGE.md) and the design brief / ideas in [docs/](docs/).
