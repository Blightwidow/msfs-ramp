# RAMP (RAM Airport Preloader) — Ideas & Decision Log

Running log of design decisions, alternatives considered, and future work.
Append here rather than losing context between sessions.

## Problem statement

MSFS 2020 files hosted on a size-constrained **HDD**. At ~25 NM from an airport the sim streams
that airport's scenery (BGLs, model libs, textures). On a spinning HDD the loader thread blocks on
slow random reads → multi-second freeze/stutter on approach.

Goal: eliminate the freeze without moving to SSD (blocked by drive size).

## Root cause (settled)

- Bottleneck is **disk I/O latency**, not CPU.
- MSFS is closed source → cannot inject threads into its streaming pipeline.
- Adding threads would make an HDD *worse* (random-seek thrashing).
- Correct lever: **warm the OS filesystem page cache ahead of the sim** so MSFS reads from RAM at 25 NM.

## Current approach (v1, implemented — C#)

External console app. No injection, no scenery modification. Reads files, discards bytes, side
effect = hot OS cache.

```
SimConnect (aircraft lat/lon @1Hz)
  → distance to every indexed airport
  → within OuterRadius (60 NM)? → queue that airport's package files
  → background low-priority I/O thread → sequential read → warm page cache
```

Components: `Program`, `SimConnectClient`, `PackagePathResolver`, `PackageIndex`,
`AirportDatabase`, `Prefetcher`, `Geo`, `Config`. UI layer (v1.4): `MainForm`, `SettingsForm`,
`AboutForm`, `Theme`, `ThemedControls`.

## Decisions & rationale

### Warm page cache vs inject into MSFS
Chose page-cache warming. Injection impossible (closed source) and unnecessary. Cache warming is
safe (can't crash sim), OS-supported, language-agnostic.

### C# vs C++ vs Python
Chose C#: native managed SimConnect wrapper, clean Win32 interop, single exe. Python = fastest
prototype but ships worse. C++ = max I/O control, most effort.
- Note: **language does not enable macOS compilation.** SimConnect + cache-warming syscalls are
  Windows-only. Only a portable *logic core* (parse/geo/queue) can compile on mac (behind `#ifdef`);
  the runnable deliverable is Windows-only regardless of language.

### CSV airport coords vs SimConnect facilities  (revisit candidate)
Chose bundled OurAirports `airports.csv` for ICAO→lat/lon. Reasoning:
- Real problem is **package→ICAO→files**, not proximity discovery. Coords are just a keyed lookup
  for ICAOs already indexed from packages.
- SimConnect in-range facility subscription uses a **sim-managed radius** — can't force 60 NM, risks
  firing *after* the sim's own 25 NM load (defeats the tool).
- `RequestFacilitiesList(AIRPORT)` historically flaky (empty/HTSE returns, 2020 vs 2024 EX1 diffs).
- Only need coords for the tiny subset of ICAOs we have packages for.
- Downsides accepted: CSV goes stale; external file to ship.

### Prefetch trigger at 60 NM
Must exceed sim's ~25 NM load radius so files are warm in time. Configurable. May need raising on
very slow HDDs / large (2 GB+) airports so warming finishes before 25 NM.

### Closest-first priority queue (implemented v1.1)
Pending packages warmed in ascending **live** distance order, refreshed every position poll
(`Prefetcher.Observe(entry, distanceNm)` → worker `TakeClosest()`). Rationale: answers "what if an
airport is skipped for RAM but I get closer and land there?" — the closest airport (your destination
as you approach) always warms before farther enroute airports, so it can't be starved by them. Budget
is therefore spent nearest-first; when the cap is hit, the airports skipped are the *farthest*, which
is the correct thing to drop. Dedupe/"warm once per session" moved into the Prefetcher (`_done` set).

Residual: the RAM budget is still **monotonic** (never released). Not a problem in practice now that
spend is nearest-first, but a skipped (far) airport stays marked done and won't retry even if you
later divert to it. Full fix = drop the session counter + rely on OS LRU eviction (alt 2, backlog).

### WinForms tray UI + load/unload state machine (implemented v1.2)
Turned the console app into a WinForms tray application (`Engine` orchestrator + `MainForm` +
`SettingsForm`). Chose WinForms over WPF: ships with net48, native `NotifyIcon` tray, zero extra deps.
- **Runs in background, sim on or off.** `SimConnectClient` gained an auto-reconnect lifecycle
  (`EnsureConnected` polled on a loop; clean teardown on sim quit, reconnect on next launch). Fixes the
  old bug where `IsConnected` stayed true after the sim closed and never reconnected. Window X-close
  hides to tray; only the tray Exit item quits.
- **Airport list shows load/unload.** `Prefetcher` now holds an `AirportState` per package
  (OutOfRange → Queued → Warming → Loaded / Skipped). UI polls `Snapshot()` on a 750 ms timer.
- **Unload on fly-away.** `Prefetcher.Release()` (called by `Engine` when distance > outer + 10 NM
  hysteresis) returns the package to OutOfRange **and decrements the RAM counter** — so the budget is
  no longer monotonic; a long flight can't exhaust it. Resolves the residual from v1.1.
- **Settings panel** edits all ini keys, writes back via `Config.Save`, applies live (radius/poll),
  rescans on path change, and toggles a HKCU Run key (`StartupRegistry`) for start-with-Windows.

### Gate warming on sim-running state (implemented v1.3, revised v1.4)
Observed prefetch firing during the **loading screen**, competing with the sim's own disk reads and
slowing the load. `Engine.OnPosition` no-ops unless the sim reports you're in a flight; background-I/O
priority alone wasn't enough — a full stop during load is what the user wants. Also pauses when
returning to menus; UI shows In flight / in menu / waiting.
- **v1.3:** subscribed to the SimConnect `"Sim"` system event. **Wrong signal** — `"Sim"` reads 1 in
  the main menu too (the menu runs a live background world), so the header said "in flight" and
  warming started during the menu→flight load.
- **v1.4 fix:** gate on **`CAMERA STATE`** instead — in flight only when 2–10 (cockpit/external/
  drone/showcase), excluding 11 (Waiting/menu) and 12 (World Map). Data request switched to a steady
  1 Hz so the gate flips promptly on the transition.

### Background-I/O priority
`THREAD_MODE_BACKGROUND_BEGIN` + `ThreadPriority.Lowest` + `FILE_FLAG_SEQUENTIAL_SCAN` so our reads
yield to MSFS's own foreground I/O. RAM-budget capped to avoid evicting pages MSFS needs.

### Rebrand + UI redesign (implemented v1.4 — from a design handoff)
Rebranded **MSFS Airport Preloader → RAMP** (RAM Airport Preloader; also the aviation "ramp"). Built
a design in Claude Design and implemented it faithfully in WinForms (owner-draw), staying on net48 —
no WPF rewrite, so the SimConnect pipeline was untouched.
- **Visual language** (`Theme`): dark instrument-panel palette + a full **light theme**, IBM Plex
  type pair with system fallback, paint primitives (rounded rects, status chips, striped warming bar,
  logo). **Theme setting**: System / Light / Dark (`Config.Appearance`), applied at startup and
  live-reapplied to the window.
- **Main window**: custom toolbar (logo glyph coloured by the latest airport's state), RAM **cache
  meter** + LOADED/WARMING/QUEUED/SKIPPED counters, owner-drawn airport list (state rail, chip, live
  warming bar), footer state readout.
- **Seven states** driven off a new `Engine.Phase` (Building/Ready/Error): indexing takeover, in-menu
  paused banner + dimmed list, nothing-nearby empty state, warming, budget-full amber banner with a
  Raise-budget action, package-folder error takeover, tray.
- **Custom window chrome**: no OS title bar; toolbar is the caption (drag + double-click maximise) with
  in-app min/max/close. Native resize/snap/shadow kept via `WS_THICKFRAME`+`WS_CAPTION` and
  `WM_NCCALCSIZE` (reclaims the whole top inset so no white bar).
- **Tray** (design state 07): dark-rendered menu with a live status readout header, **Pause warming**
  (real engine pause via `Prefetcher.Paused`), one **completion balloon** per flight, glyph recolours
  to the dominant state.
- **About** popup: free/open-source, repo link, OurAirports credit. **Banners** (`docs/banners/`,
  SVG). Taskbar/window **icon** set to the RAMP glyph.
- **Settings** reworked into labelled dark sections with a RAM slider and inline validation. The
  **airports.csv path field was dropped** from the UI (the CSV ships with the app) — and with it the
  "airport database not found" error state; the override key still exists in the ini for edge cases.

### Inner radius + already-visited suppression (implemented v1.5)
Observed RAMP re-warming the airport you spawned at (e.g. LFPG): the sim streamed it during the
loading screen, so re-reading is pure waste.
- **Inner radius** (`InnerRadiusNauticalMiles`, default 5): `Engine.OnPosition` skips warming any
  airport closer than this.
- **Visited-this-flight set**: airports passed inside the inner radius are remembered and not
  re-warmed while you stay near — stops a departure being re-read on climb-out.
- **Re-arm on release**: when an airport passes the release radius (outer + 10) it's unloaded *and*
  dropped from the visited set — the sim has evicted it too, so a return/divert warms it again.
- **Reset per flight** via a new `SimConnectClient.SimRunningChanged` event (fires on the camera-state
  transition); a fresh flight re-arms every airport.

### SimBrief integration (implemented — alt E)
`SimBriefUserId` (numeric Pilot ID) added to config + Settings panel. On launch and on each
flight-start transition (`SimRunningChanged == true`), `Engine.RefreshSimBrief` fetches the latest
OFP on a background thread and pins the endpoint ICAOs.

- **Fetch/parse**: `SimBriefClient` hits the public XML fetcher
  (`xml.fetcher.php?userid=<id>`) and reads `destination/icao_code` + `alternate/icao_code` with the
  built-in `System.Xml.Linq` — **no JSON dependency added** (keeps the net48 zero-extra-deps stance).
  TLS 1.2 forced (net48 default omits it). 10 s timeout. Every failure (no net, bad ID, empty OFP)
  logs and returns null → silent fall back to proximity warming.
- **Arrival + alternate only, NOT departure** (differs from the alt E sketch): departure is where you
  spawn, so the sim has already streamed it and the inner-radius logic suppresses it anyway — warming
  it would be wasted I/O.
- **"Regardless of distance, first"**: `Engine.OnPosition` short-circuits pinned ICAOs past the
  inner/outer/release/visited gates and calls `Prefetcher.Observe(entry, distance, pinned: true)`.
  A new `pinned` flag on `AirportState` makes `TakeClosestQueued` pick pinned packages before
  proximity ones, so they claim the RAM budget first — effective priority without a separate budget.
- **Still in-flight-gated**: pinned warming obeys the existing `IsSimRunning` gate, so it never
  competes with the loading screen; the destination warms during cruise. On flight end the pin set is
  cleared and re-fetched next flight (picks up a regenerated OFP). `Release` also clears the pin so a
  changed plan across flights doesn't leave a stale pinned row.
- **UI**: coral `Theme.Pinned` colour; a dot in the ICAO gutter, a `PINNED` counter, and pinned rows
  sorted to the top.
- Doc caveat corrected: the method is `Prefetcher.Observe(entry, distance)`, not `Enqueue`.

## Alternatives considered (not chosen yet)

### A. BGL-derived coordinates (drop the CSV)  — strongest future candidate
Read each airport's reference lat/lon from the airport BGL header inside the package. Self-contained,
always matches installed content, no stale external file. Also yields the **authoritative ICAO**
(fixes the folder-name-guess heuristic). Cost: BGL binary parsing.
→ Good long-term replacement for the CSV.

**Confidence: NOT yet verified — do not replace CSV until proven.**
- Confident: an *airport-definition* BGL carries an airport reference point (lat/lon/alt) in its
  airport-record header. Well-documented in legacy FSX/P3D; MSFS airports compile XML→BGL keeping it.
  Little Navmap (open-source, albar965) parses MSFS 2020/2024 airport BGLs — proof it's doable and a
  reference for the exact record layout + fixed-point coord encoding.
- Uncertain: (1) NOT every BGL is an airport BGL — most are object/model/terrain/exclusion BGLs with
  no reference point; must find the airport record, not any BGL. (2) MSFS record layout/encoding may
  differ from legacy; public docs thin. (3) fixed-point coord decode is easy to get subtly wrong
  (scale/sign/projection).

**Verification probe (build BEFORE committing to alt A):** the CSV is the oracle. One-shot scan:
parse airport records in each installed package's BGLs, extract coord + ICAO, compare to OurAirports
CSV for the same ICAO. Report: coverage (% indexed airports where an airport record was found),
agreement (distance BGL↔CSV, expect <1 NM), mismatches/misses. High coverage + tight agreement →
switch to BGL; else keep CSV. Zero risk (read-only, CSV stays the source of truth during the probe).
Crib record offsets/encoding from Little Navmap rather than reverse-engineering from scratch.

### E. SimBrief integration — pre-fetch flight endpoints at launch
Pull the user's latest OFP from the free SimBrief API and warm the **departure + destination**
(and **alternate**) airports at startup, before the flight even begins — independent of aircraft
position. Complements proximity prefetch: endpoints warmed immediately, enroute/arrival handled by
the 60 NM proximity logic as usual.

- API: `https://www.simbrief.com/api/xml.fetcher.php?userid=<SIMBRIEF_ID>` (also `&json=1`).
  Returns latest generated OFP; fields `origin.icao_code`, `destination.icao_code`,
  `alternate.icao_code`. No auth beyond the numeric user ID.
- Config: add `SimBriefUserId` to `preloader.ini`; blank = feature off.
- Flow at launch: fetch OFP → resolve each ICAO to package(s) via existing `PackageIndex` →
  `Prefetcher.Enqueue`. Reuses everything already built; only adds a fetch + parse step.
- Ties into a future **settings panel / GUI** (see backlog) where the user pastes their SimBrief ID.
- Edge cases: no internet at launch (skip, fall back to proximity); OFP older than current flight
  (warm anyway — cheap); ICAO not installed as a package (skip silently).
- Value: kills the *departure*-taxi freeze too, and warms destination during cruise headroom rather
  than racing the 60→25 NM window on approach.

### C. RAM disk for hottest airports
Mount a RAM disk, copy most-used airport packages there, symlink. Bypasses cache eviction entirely.
Cost: RAM budget, manual curation, symlink management. Overlaps with OS cache but guarantees residency.

### D. Cross-compile from macOS (mingw-w64)
Produce a Windows exe from mac. Fails today: no `SimConnect.lib`/headers on mac, mingw↔MSVC `.lib`
linking fragile. Not pursued.

## Known limitations (current)

- MSFS **2020 only** (paths + SDK). 2024 = different UserCfg paths + SDK.
- Warms whole add-on **packages** attributed to an ICAO by folder name / manifest title. Default
  handcrafted airports baked into `fs-base` cannot be isolated.
- Cache warming is best-effort — tight RAM may evict pages before MSFS reads them.
- ICAO-token matching against DB may over-match (publisher/version tokens) → over-warm; bounded by
  RAM budget.

## Future work / backlog

- [ ] **Heading/ETA filtering** — only warm airports you're flying *toward* (bearing + closing speed),
      not every airport within 60 NM. Cuts wasted I/O.
- [ ] **BGL coord verification probe** (alt A, gate) — scan packages, compare BGL airport-record
      coords/ICAO to CSV oracle, report coverage + agreement. Decides whether alt A is viable.
- [ ] **BGL-derived coords** (alt A) — remove CSV dependency. ONLY after the probe passes.
- [ ] **MSFS 2024 support** — path resolver + SDK differences.
- [ ] **Flight-plan aware prefetch** — read active flight plan (dep/enroute/arr), warm along route.
      (Scope was deferred; v1 is approach-only.)
- [ ] **Departure airport warm at sim start** — smooth first taxi/takeoff.
- [x] **SimBrief integration** (alt E) — done. `SimBriefUserId` in settings; fetch latest OFP at
      launch AND each flight start; pin **arrival + alternate** (NOT departure — you spawn there, the
      sim already has it) and warm them first regardless of distance. See decision below.
- [x] **Settings panel / GUI** — done in v1.2 (WinForms). SimBrief ID field still to add when alt E
      lands; everything else (radius, poll, RAM, paths, start-with-Windows, verbose) is editable.
- [ ] **Adaptive OuterRadius** — scale trigger distance by package size ÷ measured HDD read speed so
      warming always finishes before 25 NM.
- [x] **Drop monotonic RAM budget** — done in v1.2: budget freed on `Release()` when flying away.
      (Could still add OS-LRU awareness / per-airport cap later.)
- [x] **Rebrand to RAMP** — done v1.4.
- [x] **Full UI redesign** — done v1.4 (dark instrument table, seven states, cache meter, banners).
- [x] **Light / dark / system theme** — done v1.4 (`Config.Appearance`).
- [x] **Pause warming** — done v1.4 (tray toggle; `Prefetcher.Paused`).
- [x] **Custom dark window chrome + app icon** — done v1.4.
- [x] **Completion balloon** — done v1.4 (one per flight; approximates "destination" as nearest
      Loaded — see below for the heading-aware refinement).
- [ ] **Force-warm on close** — at an inner radius (~25 NM) warm the destination even if budget hit.
- [ ] **RAM budget live-apply** — currently needs restart (set in `Prefetcher` ctor); make mutable.
- [ ] **Persist window size/position + column widths.**
- [ ] **Re-warm on eviction** — detect if pages likely evicted (time/other-activity) and re-read.
- [ ] **Portable logic core** — extract parse/geo/queue behind `#ifdef`/interface so it compiles &
      unit-tests off-Windows.
- [ ] **Verify effectiveness** — before/after frame-time capture on approach to prove the freeze is gone.
- [ ] **Handcrafted-airport handling** — investigate whether sub-ranges of `fs-base` can be targeted.
- [ ] **Package→ICAO matching** — replace token heuristic with reading airport ICAO from BGL directly.

## Open questions

- Does the OS reliably keep 1–2 GB of warmed airport pages resident through approach, or does normal
  MSFS streaming churn evict them? (Needs measurement.)
- What's the real HDD sequential read rate → how much lead time (NM) does a 2 GB airport need?
- Do MSFS facility APIs fire early enough to ever be viable for proximity? (Measure trigger radius.)
- Best signal that a warm actually helped — frametime graph? loader thread wait? SimConnect can't see it.
