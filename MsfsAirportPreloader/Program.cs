using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace MsfsAirportPreloader
{
    internal static class Program
    {
        private static void Main()
        {
            Console.Title = "MSFS Airport Preloader";
            Log("MSFS Airport Preloader — warms the OS file cache for nearby airports ahead of MSFS's 25 NM load.");
            Log("Root cause it targets: HDD read stalls when MSFS streams airport files. Fix: pre-read them into RAM.");
            Log("");

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var config = Config.Load(Path.Combine(baseDirectory, "preloader.ini"));

            // --- Airport coordinate DB ---
            string airportsCsvPath = string.IsNullOrWhiteSpace(config.AirportsCsvPathOverride)
                ? Path.Combine(baseDirectory, "airports.csv")
                : config.AirportsCsvPathOverride;
            var airportDatabase = AirportDatabase.Load(airportsCsvPath);
            if (airportDatabase.Count == 0)
            {
                Log($"ERROR: no airports loaded from {airportsCsvPath}.");
                Log("Download the free OurAirports dataset: https://davidmegginson.github.io/ourairports-data/airports.csv");
                Log("Place it beside the exe as airports.csv (or set AirportsCsvPath in preloader.ini).");
                return;
            }

            Log($"Loaded {airportDatabase.Count} airport coordinates.");

            // --- MSFS package tree ---
            string installedPackagesPath = PackagePathResolver.Resolve(config.InstalledPackagesPathOverride, Log);
            if (installedPackagesPath == null || !Directory.Exists(installedPackagesPath))
            {
                Log("ERROR: could not locate MSFS InstalledPackagesPath.");
                Log("Set InstalledPackagesPath in preloader.ini to your MSFS package folder (the one containing Community/ and Official/).");
                return;
            }

            Log($"Scanning packages under: {installedPackagesPath} (first run can take a moment)...");
            var packageIndex = PackageIndex.Build(installedPackagesPath, airportDatabase, Log);
            Log($"Indexed {packageIndex.PackageCount} scenery packages across {packageIndex.AirportCount} airports.");
            if (packageIndex.AirportCount == 0)
            {
                Log("No airport-attributable scenery packages found. Nothing to preload. Exiting.");
                return;
            }

            using var prefetcher = new Prefetcher(config.RamBudgetMegabytes, Log);

            // --- Position -> distance -> enqueue ---
            double outerRadius = config.OuterRadiusNauticalMiles;
            var evaluated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void OnPosition(AircraftPosition position)
            {
                foreach (string icao in NearbyIndexedAirports(position, packageIndex, airportDatabase, outerRadius))
                {
                    if (!evaluated.Add(icao))
                    {
                        continue; // already queued this session
                    }

                    if (packageIndex.TryGet(icao, out List<PackageEntry> entries))
                    {
                        foreach (PackageEntry entry in entries)
                        {
                            Log($"[approach] {icao} within {outerRadius:0} NM — queueing \"{entry.PackageName}\" " +
                                $"({entry.TotalBytes / (1024 * 1024)} MB).");
                            prefetcher.Enqueue(entry);
                        }
                    }
                }
            }

            // --- Connect + run until Ctrl+C ---
            using var simConnect = new SimConnectClient(Log);
            simConnect.PositionUpdated += OnPosition;

            using var exitEvent = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, args) =>
            {
                args.Cancel = true;
                exitEvent.Set();
            };

            Log("Waiting for MSFS... (start a flight; Ctrl+C to quit)");
            while (!exitEvent.IsSet)
            {
                if (!simConnect.IsConnected && !simConnect.TryConnect())
                {
                    exitEvent.Wait(TimeSpan.FromSeconds(Math.Max(1.0, config.PollSeconds)));
                    continue;
                }

                exitEvent.Wait(TimeSpan.FromSeconds(Math.Max(1.0, config.PollSeconds)));
            }

            Log("Shutting down.");
        }

        private static IEnumerable<string> NearbyIndexedAirports(
            AircraftPosition position,
            PackageIndex packageIndex,
            AirportDatabase airportDatabase,
            double radiusNauticalMiles)
        {
            // Only airports we actually have a package for are worth measuring.
            foreach (string icao in AllIndexedIcaos(packageIndex))
            {
                if (!airportDatabase.TryGet(icao, out AirportLocation location))
                {
                    continue;
                }

                double distance = Geo.DistanceNauticalMiles(
                    position.Latitude, position.Longitude, location.Latitude, location.Longitude);

                if (distance <= radiusNauticalMiles)
                {
                    yield return icao;
                }
            }
        }

        // PackageIndex doesn't expose its keys directly; keep a cached snapshot the first time.
        private static string[] _indexedIcaoCache;

        private static IEnumerable<string> AllIndexedIcaos(PackageIndex packageIndex)
        {
            return _indexedIcaoCache ?? (_indexedIcaoCache = packageIndex.IndexedIcaos());
        }

        private static void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            Console.WriteLine(message.Length == 0 ? "" : $"[{timestamp}] {message}");
        }
    }
}
