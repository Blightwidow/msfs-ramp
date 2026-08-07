# Using RAMP

RAMP runs quietly in the background and warms your add-on airports into RAM before you reach them.
Most people set it once and forget it. This guide covers the window, the states you'll see, and the
settings.

## Getting started

1. Launch **RAMP.exe**. It can run with MSFS open or closed.
2. On first launch it does a **one-time scan** of your MSFS package folder (auto-detected from
   `UserCfg.opt` for Store/Game Pass and Steam installs). You'll see the indexing screen while it
   works — this only happens again if you rescan or move your packages.
3. Leave it running. Start a flight and, once you're **airborne**, RAMP begins warming the airports
   you're approaching. Closing the window (**X**) hides it to the tray; it keeps running.

Optional: turn on **Start with Windows** in Settings so it's always ready.

RAMP won't waste effort on airports the sim already has: anything within ~5 NM (where you spawned, or
just landed) is skipped, and a departure won't be re-read on climb-out. Fly far enough away that it
unloads and a later return or divert warms it again normally.

## The window

- **Toolbar** — the RAMP mark (its colour tracks the current activity), `Rescan`, `Log`, `Settings`,
  `About`, and the window controls. The right side shows the live connection: `CONNECTED · IN FLIGHT`
  / `IN MENU`, or `OFFLINE · WAITING FOR MSFS`.
- **Cache meter** — how much RAM is currently held (green = loaded, blue = warming) against your
  budget, plus counters for **LOADED / WARMING / QUEUED / SKIPPED**.
- **Airport list** — every airport package you've flown near, with a coloured state rail, distance,
  MB warmed, and either a state chip or a live warming bar.
- **Footer** — packages indexed, airports tracked in range, and a short state readout on the right.
- **Log** — toggle the connection / queue / prefetch event log from the toolbar.

### Status colours

| State | Colour | Meaning |
|-------|--------|---------|
| **Loaded** | green | Warm in RAM, ready for MSFS's ~25 NM load. |
| **Warming** | blue | Being read into the page cache right now (animated). |
| **Queued** | violet | In range, waiting its turn (closest airport warms first). |
| **Skipped** | amber | In range but the RAM budget was full. Nothing is lost. |
| **Unloaded** | grey | Out of range, or flown far enough away to be released. |

A **coral dot** left of the ICAO (and a **PINNED** counter) marks airports from your SimBrief
flight plan — the arrival and alternate. They warm first, regardless of distance, and sort to the
top of the list.

## The states you'll see

RAMP shows one window that adapts to the situation:

1. **First run — indexing.** A full-screen scan with progress and the folder being read. Only on first
   launch or after a rescan.
2. **Connected, in menu.** A violet "warming paused" banner; the list dims. RAMP won't read files
   while the sim is loading or in menus — that would slow MSFS's own load. It resumes once you're
   airborne.
3. **In flight, nothing nearby.** A calm empty state naming how far out the nearest airport is; RAMP
   picks it up automatically as you close in.
4. **Warming.** The busy state — the approaching airport carries a progress bar, queued airports sit
   beneath it in distance order.
5. **RAM budget full.** An amber banner: the budget is reached and some fields were skipped. Your
   destination is loaded first; a **Raise to N GB** button bumps the budget (applies next launch).
6. **Needs attention.** If RAMP can't find your MSFS package folder, it says so with the path it tried
   and an **Open Settings** button. (Amber, never red — nothing is lost.)
7. **Minimised to tray.** The tray glyph recolours to the dominant state; hover it for a live readout.
   Right-click for **Open / Settings / Pause warming / Exit**. One balloon per flight tells you when
   your approach is covered.

## Settings

- **Range & timing** — *Outer radius* (how far out warming starts; minimum 30 NM, since MSFS loads
  scenery near 25 NM) and *Poll interval* (how often position is checked). Both apply live.
- **Memory** — a *RAM budget* slider (with your free RAM shown for reference). Applies on next launch.
- **Paths** — your MSFS package folder, pre-filled with the detected path; edit it if auto-detect was
  wrong. Changing it triggers a rescan.
- **SimBrief** — your numeric *Pilot ID*. Set it and RAMP fetches your latest OFP on launch and at
  each flight start, then warms the **arrival + alternate** airports first, regardless of distance —
  so your destination warms during cruise instead of racing the 60→25 NM window on approach. Blank
  disables it; proximity warming is unaffected either way.
- **Behaviour** — *Start with Windows* and *Verbose logging*.
- **Appearance** — theme: *Follow system*, *Light*, or *Dark*.

## Tray

Right-click the tray icon:

- **Open RAMP** — restore the window.
- **Settings…** / **About**.
- **Pause warming** — stop all warming until you resume (useful if you want the disk free for
  something else). The footer shows `paused` while active.
- **Exit** — the only real quit; closing the window just hides to the tray.

## The config file

Settings are stored in `preloader.ini` next to the executable and are fully managed by the Settings
panel — you shouldn't need to edit it by hand. Hand-edits are preserved on the next save.
