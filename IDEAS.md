# MSFS Airport Preloader — Ideas & Decision Log

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
`AirportDatabase`, `Prefetcher`, `Geo`, `Config`.

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

### Background-I/O priority
`THREAD_MODE_BACKGROUND_BEGIN` + `ThreadPriority.Lowest` + `FILE_FLAG_SEQUENTIAL_SCAN` so our reads
yield to MSFS's own foreground I/O. RAM-budget capped to avoid evicting pages MSFS needs.

## Alternatives considered (not chosen yet)

### A. BGL-derived coordinates (drop the CSV)  — strongest future candidate
Read each airport's reference lat/lon from the airport BGL header inside the package. Self-contained,
always matches installed content, no stale external file. Cost: BGL binary parsing.
→ Good long-term replacement for the CSV.

### B. SimConnect as coordinate lookup only
Query sim once at startup for indexed ICAOs' coords (not for proximity). Removes CSV, sim = source of
truth. Cost: needs sim running before indexing; inherits facility-API flakiness.

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
- [ ] **BGL-derived coords** (alt A) — remove CSV dependency.
- [ ] **MSFS 2024 support** — path resolver + SDK differences.
- [ ] **Flight-plan aware prefetch** — read active flight plan (dep/enroute/arr), warm along route.
      (Scope was deferred; v1 is approach-only.)
- [ ] **Departure airport warm at sim start** — smooth first taxi/takeoff.
- [ ] **SimBrief integration** (alt E) — `SimBriefUserId` in settings; fetch latest OFP at launch,
      warm departure + destination + alternate immediately. Reuses PackageIndex + Prefetcher.
- [ ] **Settings panel / GUI** — replace/augment `preloader.ini` with a UI: SimBrief ID field,
      OuterRadius, RAM budget, package path, enable toggles. (SimBrief ID is the first driver.)
- [ ] **Adaptive OuterRadius** — scale trigger distance by package size ÷ measured HDD read speed so
      warming always finishes before 25 NM.
- [ ] **Drop monotonic RAM budget** — rely on OS LRU eviction; cap per-airport, not per-session, so a
      late diversion airport isn't permanently skipped. (Residual from the closest-first change.)
- [ ] **Force-warm on close** — at an inner radius (~25 NM) warm the destination even if budget hit.
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
```
