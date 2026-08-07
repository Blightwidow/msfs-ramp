# Design Brief — MSFS Airport Preloader UI

## 1. What this is

A small Windows utility for **Microsoft Flight Simulator 2020** that pre-loads add-on airport files
into RAM before the sim needs them, eliminating the multi-second freeze when approaching an airport
whose files sit on a slow hard drive. It runs quietly in the background (system tray) and reports what
it's doing.

The engineering works. This brief is about making the interface **clear and attractive** — something
a flight-sim enthusiast is happy to keep open on a second monitor.

## 2. Who uses it and how

- **Audience:** flight-sim hobbyists on PC. Technically comfortable, run many add-ons, often already
  use tools like Little Navmap, Navigraph Charts, SPAD.neXt, FSUIPC. They appreciate an "electronic
  flight bag" (EFB) / cockpit-instrument aesthetic.
- **Context of use:** launched once and left running. Watched *glanceably* — usually on a second
  monitor or Alt-Tab during a flight — to confirm "is my destination airport loaded yet?" Not stared
  at. Must read at a glance from a couple of feet away.
- **Emotional goal:** reassurance and control. "It's handling it. My approach won't stutter."

## 3. What exists today (baseline to improve)

A single functional-but-plain window plus a tray icon. Current elements:

- **Toolbar:** buttons — Settings, Rescan, Clear log.
- **Airport list** (the centerpiece): one row per add-on airport package, columns:
  `ICAO | Package name | Distance (NM) | Status | Warmed (MB)`. Sorted by status then distance.
- **Log pane:** scrolling timestamped text of events (connect, queue, prefetch, unload).
- **Status bar:** sim connection state + index counts + tracked-airport count.
- **Settings dialog:** plain form of fields (radius, poll interval, RAM budget, folder paths,
  start-with-Windows, verbose).
- **Tray icon:** right-click menu (Open / Settings / Exit); closing the window hides to tray.

Screenshots of the current build can be provided on request.

## 4. Goals of the redesign

1. **Glanceability** — the one question "which airports are loaded / loading / not yet?" answerable in
   under a second, from across the room.
2. **A sense of state & motion** — warming is a live process; the UI should feel alive (progress,
   transitions) without being noisy.
3. **Polish** — a cohesive visual language (color, type, spacing, iconography) that looks at home
   next to premium sim add-ons, not like a debug tool.
4. **Calm by default** — it runs for hours. No flashing, no attention-grabbing unless something is
   wrong.

## 5. Must-keep content & functionality

The redesign is visual; these must all remain expressible:

- **Per-airport status**, the core data. Each airport package has exactly one of these states:
  | State | Meaning | Today's label |
  |-------|---------|---------------|
  | Loaded | files warm in RAM, ready for the sim's load | `Loaded` |
  | Warming | being read into cache right now | `Loading...` |
  | Queued | in range, waiting its turn | `Queued` |
  | Skipped | in range but RAM budget was full | `Skipped` |
  | Unloaded | out of range / flown away (also the resting state) | `Unloaded` |
- **Per-airport data:** ICAO code, package/airport name, distance (NM), warmed size (MB), and ideally
  a **progress indicator** while Warming (we know warmed-bytes vs total-bytes — currently only shown as
  a number, a bar would be better).
- **Global status:** connected to MSFS? in-flight vs menu/loading (warming is paused in menus)?
  how many airports indexed/tracked? total RAM currently held in cache vs the budget.
- **Event log** — keep it, but it can be de-emphasized/collapsible; it's for troubleshooting, not the
  main view.
- **Settings** — all fields stay (radius, poll, RAM budget, package folder, airports.csv path,
  start-with-Windows, verbose). Outer radius has an enforced minimum (30 NM) — show inline validation.
- **Tray presence** — icon + menu; the tray icon could itself convey state (e.g. count of loaded /
  a subtle badge) — nice-to-have.

## 6. States & moments to design

Please design for all of these, not just the happy path:

1. **First run / not connected** — MSFS closed. "Waiting for MSFS." Indexing packages (a one-time
   scan that can take several seconds — needs a progress/spinner state).
2. **Connected, in menu/loading** — warming intentionally paused. Communicate *why* it's idle.
3. **In flight, nothing nearby** — connected, list mostly empty/Unloaded. The calm resting state.
4. **In flight, actively warming** — one airport Warming with progress, others Queued/Loaded. The hero
   state; make it satisfying.
5. **RAM budget full** — some airports Skipped; the budget meter is maxed. Needs a clear, non-alarming
   treatment.
6. **Error / misconfig** — no airports.csv, package folder not found, etc. Actionable, not scary.
7. **Minimized to tray** — tray icon + optional balloon/notification design.

## 7. Interactions & behavior

- List refreshes ~1–2×/second. State changes should animate gently (row recolor, progress fill), not
  jump.
- Sort order is state-grouped then distance; a redesign could use visual grouping/section headers
  instead of a flat sortable table if that reads better.
- Consider a **map or radial/range view** as an alternative or companion to the table — airports as
  dots around the aircraft with warm/cold coloring. Optional, but could be the "sexy" centerpiece.
  (We have aircraft lat/lon and each airport's lat/lon, so a relative-bearing/range plot is feasible.)
- Row click could reveal detail (file count, path, exact MB / total). Not required.

## 8. Technical constraints — please read

The current app is **C# WinForms on .NET Framework 4.8**, Windows-only. This matters:

- **WinForms severely limits native styling** — no built-in theming, rounded corners, animation, or
  modern controls without heavy owner-draw/custom-control work. A visually rich design likely means
  one of:
  - **(a) Stay WinForms**, implement custom-drawn controls (feasible but effort-heavy; animation is
    awkward). Good for incremental polish.
  - **(b) Migrate the UI layer to WPF or WinUI 3** — proper styling, data binding, animation, vector
    graphics, dark theme. Much better ceiling for "sexy"; the engine/back-end stays as-is. This is the
    likely right call if the design is ambitious.
- **The back-end is decoupled** (an `Engine` that exposes a data snapshot + a log stream + settings),
  so swapping the UI framework does **not** touch the prefetch logic. Designer should feel free to
  design for a modern stack; note anywhere your design assumes capabilities WinForms lacks.
- Self-contained desktop app, no web view assumed (though a WebView2-hosted HTML UI is a third option
  if the designer prefers web tooling).
- Must remain usable **windowed and small** (it shares screen space) and **DPI-scaling clean** on 1080p
  through 4K.

**Decision needed from the team, informed by your design:** which stack (a/b/webview). Design first,
we'll pick the stack to match ambition vs effort.

## 9. Visual direction (starting point, not prescriptive)

- **Mood:** modern cockpit / EFB. Dark theme as default (people fly at night, dark rooms, second
  monitor). A light theme is a plus but not required.
- **Reference points:** MSFS in-game toolbar & EFB tablets, Navigraph Charts, Little Navmap, ForeFlight,
  aviation instrument dials. Avoid generic "Windows utility" and avoid gamer-RGB.
- **Color as data:** state is the primary information — a clear, colorblind-safe status palette
  (loaded/warming/queued/skipped/unloaded) is central. Green=ready is expected; pick the rest
  deliberately.
- **Typography:** legible at a glance; consider a technical/monospaced accent for codes (ICAO) and
  numbers (distance, MB) as on real instruments.
- **Iconography:** an app/tray icon that reads at 16px and conveys "airport / loaded." Room for a
  distinct product identity/name/logo if you want to propose one.

## 10. Deliverables requested

- **Wireframes** for the states in §6 (low-fi is fine first).
- **High-fidelity mockups** of the main window (hero: in-flight warming) + settings + tray, dark theme.
- **A component/style spec:** color tokens (incl. the status palette), type scale, spacing, iconography,
  the airport-row and progress/meter components.
- **App + tray icon.**
- Optional: the map/radial view concept (§7), a light theme, motion notes for state transitions.
- A short note flagging anything that requires stack (b) WPF/WinUI to build.

## 11. Open questions for the designer / team

- Table vs grouped-cards vs map-centric as the primary layout?
- How prominent should the log be — collapsible drawer, separate tab, or gone from the main view?
- Is a range/radar view worth the build, or is a clean list enough?
- Product name & identity — keep "MSFS Airport Preloader" (functional) or brand it?
- Which target stack, given the ambition of the chosen direction?
