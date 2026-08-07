using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace MsfsAirportPreloader
{
    /// <summary>Flat prefetch target: a package plus the coordinates of its airport.</summary>
    internal sealed class PrefetchTarget
    {
        public PackageEntry Entry;
        public double Latitude;
        public double Longitude;
    }

    /// <summary>Lifecycle of the one-time package/airport index that gates everything else.</summary>
    internal enum IndexPhase { Building, Ready, Error }

    /// <summary>What's wrong when <see cref="IndexPhase.Error"/> — drives the recovery UI.</summary>
    internal enum IndexErrorKind { None, PackageFolder }

    /// <summary>
    /// Owns the whole pipeline (config, airport DB, package index, SimConnect, prefetcher) and
    /// runs it on a background thread independent of the sim. The UI observes it via Snapshot(),
    /// DrainLog() and IsSimConnected; it never blocks the UI thread.
    /// </summary>
    internal sealed class Engine : IDisposable
    {
        /// <summary>Hysteresis: release a package only once it's this far beyond the outer radius.</summary>
        private const double ReleaseMarginNauticalMiles = 10.0;

        private readonly string _iniPath;
        private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
        private readonly object _configGate = new object();

        private Config _config;
        private AirportDatabase _airportDatabase;
        private Prefetcher _prefetcher;
        private SimConnectClient _simConnect;
        private Thread _connectionThread;
        private volatile bool _running;

        private volatile List<PrefetchTarget> _targets = new List<PrefetchTarget>();

        // Airports the aircraft has been inside the inner radius of during this flight — the sim
        // already has them, so don't warm them again (departure on climb-out, touch-and-go). Cleared
        // when the flight ends. Touched only on the SimConnect callback thread.
        private readonly HashSet<string> _visitedThisFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Engine(Config config, string iniPath)
        {
            _config = config;
            _iniPath = iniPath;
        }

        public bool IsSimConnected => _simConnect?.IsConnected ?? false;
        public bool IsSimRunning => _simConnect?.IsSimRunning ?? false;
        public int IndexedAirportCount { get; private set; }
        public int IndexedPackageCount { get; private set; }

        /// <summary>Index lifecycle for the UI's takeover states. Ready is the happy path.</summary>
        public IndexPhase Phase { get; private set; } = IndexPhase.Building;
        public bool Ready => Phase == IndexPhase.Ready;

        // Populated when Phase == Error, so the window can name the problem and the path it tried.
        public IndexErrorKind ErrorKind { get; private set; }
        public string ErrorTitle { get; private set; }
        public string ErrorDetail { get; private set; }
        public string ErrorPath { get; private set; }

        /// <summary>Folder being scanned — shown under the "indexing" spinner.</summary>
        public string ScanRootPath { get; private set; }

        private volatile bool _paused;

        /// <summary>User-requested pause — stops queuing and warming until resumed.</summary>
        public bool IsPaused => _paused;

        public void SetPaused(bool paused)
        {
            _paused = paused;
            if (_prefetcher != null)
            {
                _prefetcher.Paused = paused;
            }

            Log(paused ? "Warming paused." : "Warming resumed.");
        }

        public void Start()
        {
            _prefetcher = new Prefetcher(_config.RamBudgetMegabytes, Log);
            _simConnect = new SimConnectClient(Log);
            _simConnect.PositionUpdated += OnPosition;
            _simConnect.ConnectionChanged += connected =>
                Log(connected ? "Connected to MSFS." : "Disconnected from MSFS.");
            _simConnect.SimRunningChanged += running =>
            {
                if (!running)
                {
                    _visitedThisFlight.Clear(); // new flight re-arms warming for every airport
                }
            };

            _running = true;
            _connectionThread = new Thread(ConnectionLoop) { IsBackground = true, Name = "EngineConnect" };
            _connectionThread.Start();

            BuildIndexAsync();
        }

        private void ConnectionLoop()
        {
            while (_running)
            {
                try
                {
                    _simConnect.EnsureConnected();
                }
                catch (Exception ex)
                {
                    Log($"Connection attempt failed: {ex.Message}");
                }

                double pollSeconds;
                lock (_configGate)
                {
                    pollSeconds = _config.PollSeconds;
                }

                Thread.Sleep(TimeSpan.FromSeconds(Math.Max(1.0, pollSeconds)));
            }
        }

        private void BuildIndexAsync()
        {
            var thread = new Thread(BuildIndex) { IsBackground = true, Name = "EngineIndex" };
            thread.Start();
        }

        private void BuildIndex()
        {
            Phase = IndexPhase.Building;
            ErrorKind = IndexErrorKind.None;
            try
            {
                Config config;
                lock (_configGate)
                {
                    config = _config;
                }

                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string airportsCsvPath = string.IsNullOrWhiteSpace(config.AirportsCsvPathOverride)
                    ? Path.Combine(baseDirectory, "airports.csv")
                    : config.AirportsCsvPathOverride;

                // airports.csv ships with the app, so an empty load is not treated as a user-facing
                // error state; it just yields an empty index (the UI then shows "nothing in range").
                _airportDatabase = AirportDatabase.Load(airportsCsvPath);
                Log($"Loaded {_airportDatabase.Count} airport coordinates.");

                string installedPackagesPath = PackagePathResolver.Resolve(config.InstalledPackagesPathOverride, Log);
                if (installedPackagesPath == null || !Directory.Exists(installedPackagesPath))
                {
                    Log("ERROR: could not locate MSFS InstalledPackagesPath. Set it in Settings.");
                    Fail(IndexErrorKind.PackageFolder,
                        "Can't find your MSFS packages",
                        "RAMP couldn't locate the folder that holds Community and Official. " +
                        "Set it in Settings and RAMP will scan it.",
                        installedPackagesPath ?? "(auto-detect failed)");
                    return;
                }

                ScanRootPath = installedPackagesPath;
                Log($"Scanning packages under: {installedPackagesPath} ...");
                var packageIndex = PackageIndex.Build(installedPackagesPath, _airportDatabase, Log);

                var targets = new List<PrefetchTarget>();
                foreach (string icao in packageIndex.IndexedIcaos())
                {
                    if (!_airportDatabase.TryGet(icao, out AirportLocation location))
                    {
                        continue;
                    }

                    if (packageIndex.TryGet(icao, out List<PackageEntry> entries))
                    {
                        foreach (PackageEntry entry in entries)
                        {
                            targets.Add(new PrefetchTarget
                            {
                                Entry = entry,
                                Latitude = location.Latitude,
                                Longitude = location.Longitude,
                            });
                        }
                    }
                }

                _targets = targets;
                IndexedPackageCount = packageIndex.PackageCount;
                IndexedAirportCount = packageIndex.AirportCount;
                Phase = IndexPhase.Ready;
                Log($"Indexed {packageIndex.PackageCount} scenery packages across {packageIndex.AirportCount} airports.");
            }
            catch (Exception ex)
            {
                Log($"ERROR building index: {ex.Message}");
                Fail(IndexErrorKind.PackageFolder, "Indexing failed", ex.Message, ScanRootPath ?? "");
            }
        }

        private void Fail(IndexErrorKind kind, string title, string detail, string path)
        {
            ErrorKind = kind;
            ErrorTitle = title;
            ErrorDetail = detail;
            ErrorPath = path;
            Phase = IndexPhase.Error;
        }

        private void OnPosition(AircraftPosition position)
        {
            // Don't warm during the loading screen / menus — it would compete with the sim's own
            // disk reads and slow the load. Only act once the sim reports it's in a flight.
            if (!Ready || _paused || !(_simConnect?.IsSimRunning ?? false))
            {
                return;
            }

            double outerRadius, innerRadius;
            lock (_configGate)
            {
                outerRadius = _config.OuterRadiusNauticalMiles;
                innerRadius = _config.InnerRadiusNauticalMiles;
            }

            double releaseRadius = outerRadius + ReleaseMarginNauticalMiles;
            List<PrefetchTarget> targets = _targets;

            foreach (PrefetchTarget target in targets)
            {
                double distance = Geo.DistanceNauticalMiles(
                    position.Latitude, position.Longitude, target.Latitude, target.Longitude);

                string key = target.Entry.Icao + "|" + target.Entry.PackageName;

                if (distance < innerRadius)
                {
                    // Too close — the sim has already streamed this airport (e.g. you spawned here).
                    // Remember it for the rest of the flight so we don't re-warm on climb-out.
                    _visitedThisFlight.Add(key);
                    continue;
                }

                if (distance > releaseRadius)
                {
                    _prefetcher.Release(target.Entry); // free budget even for visited fields
                    // Flown right away: the sim has unloaded it too, so if you come back it must be
                    // re-warmed — drop the "visited" mark.
                    _visitedThisFlight.Remove(key);
                    continue;
                }

                // Between inner and release: warm only if in range AND not already visited this flight.
                if (distance <= outerRadius && !_visitedThisFlight.Contains(key))
                {
                    _prefetcher.Observe(target.Entry, distance);
                }
                // Otherwise (hysteresis band, or a visited field): leave state untouched.
            }
        }

        public List<AirportState> Snapshot() => _prefetcher?.Snapshot() ?? new List<AirportState>();

        public IEnumerable<string> DrainLog()
        {
            var lines = new List<string>();
            while (_logQueue.TryDequeue(out string line))
            {
                lines.Add(line);
            }

            return lines;
        }

        /// <summary>Apply edited settings. Rebuilds the index if paths changed; warm state resets.</summary>
        public void ApplyConfig(Config newConfig, bool rescanPackages)
        {
            lock (_configGate)
            {
                _config = newConfig;
            }

            if (rescanPackages)
            {
                Log("Settings changed — rescanning packages.");
                BuildIndexAsync();
            }
            else
            {
                Log("Settings applied.");
            }
        }

        public Config CurrentConfig
        {
            get { lock (_configGate) { return _config; } }
        }

        private void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _logQueue.Enqueue($"[{timestamp}] {message}");
            // Keep the queue from growing without bound if the UI is minimized for a long time.
            while (_logQueue.Count > 2000 && _logQueue.TryDequeue(out _)) { }
        }

        public void Dispose()
        {
            _running = false;
            try { _simConnect?.Dispose(); } catch { /* ignore */ }
            try { _prefetcher?.Dispose(); } catch { /* ignore */ }
            _connectionThread?.Join(2000);
        }
    }
}
