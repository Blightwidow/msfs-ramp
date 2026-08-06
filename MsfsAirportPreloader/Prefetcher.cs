using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace MsfsAirportPreloader
{
    /// <summary>
    /// Warms the Windows filesystem page cache. Reads an airport's package files
    /// sequentially on a low-priority background-I/O thread so that when MSFS later
    /// streams the same files (at ~25 NM) they are served from RAM instead of the HDD.
    ///
    /// Holds a state machine per package (OutOfRange → Queued → Warming → Loaded, or
    /// Skipped when the RAM budget is full). Warms the CLOSEST queued package first,
    /// using distances refreshed on every position poll. Flying far enough away calls
    /// Release(), which returns the package to OutOfRange AND frees its share of the RAM
    /// budget so later airports aren't starved.
    ///
    /// This does NOT inject into MSFS and does NOT parse/alter scenery. It only reads
    /// bytes and throws them away; the side effect is a hot OS cache.
    /// </summary>
    internal sealed class Prefetcher : IDisposable
    {
        private readonly long _ramBudgetBytes;
        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private readonly Dictionary<string, AirportState> _states = new Dictionary<string, AirportState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PackageEntry> _entries = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Thread _worker;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private volatile bool _running = true;
        private long _bytesWarmed;

        public Prefetcher(long ramBudgetMegabytes, Action<string> log)
        {
            _ramBudgetBytes = ramBudgetMegabytes * 1024L * 1024L;
            _log = log;
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "AirportPrefetch",
                Priority = ThreadPriority.Lowest,
            };
            _worker.Start();
        }

        private static string Key(PackageEntry entry) => entry.Icao + "|" + entry.PackageName;

        /// <summary>Report a package's current distance. Queues it for warming if it isn't already.</summary>
        public void Observe(PackageEntry entry, double distanceNauticalMiles)
        {
            string key = Key(entry);
            lock (_gate)
            {
                _entries[key] = entry;
                if (!_states.TryGetValue(key, out AirportState state))
                {
                    state = new AirportState
                    {
                        Icao = entry.Icao,
                        PackageName = entry.PackageName,
                        TotalBytes = entry.TotalBytes,
                        State = PrefetchState.OutOfRange,
                    };
                    _states[key] = state;
                }

                state.DistanceNauticalMiles = distanceNauticalMiles;

                // Re-arm from OutOfRange or a previous budget Skip; leave Warming/Loaded alone.
                if (state.State == PrefetchState.OutOfRange || state.State == PrefetchState.Skipped)
                {
                    state.State = PrefetchState.Queued;
                    _log?.Invoke($"[queue] {entry.Icao} \"{entry.PackageName}\" at {distanceNauticalMiles:0} NM " +
                                 $"({entry.TotalBytes / (1024 * 1024)} MB)");
                    _signal.Set();
                }
            }
        }

        /// <summary>Flown far enough away: unload the package and free its RAM budget share.</summary>
        public void Release(PackageEntry entry)
        {
            string key = Key(entry);
            lock (_gate)
            {
                if (!_states.TryGetValue(key, out AirportState state))
                {
                    return;
                }

                if (state.State == PrefetchState.Warming || state.State == PrefetchState.OutOfRange)
                {
                    return; // don't interrupt an in-progress warm; nothing to do if already out
                }

                if (state.WarmedBytes > 0)
                {
                    Interlocked.Add(ref _bytesWarmed, -state.WarmedBytes);
                    _log?.Invoke($"[unload] {state.Icao} \"{state.PackageName}\" — freed " +
                                 $"{state.WarmedBytes / (1024 * 1024)} MB budget");
                }

                state.WarmedBytes = 0;
                state.State = PrefetchState.OutOfRange;
            }
        }

        /// <summary>Thread-safe copy of every known airport's status, for the UI.</summary>
        public List<AirportState> Snapshot()
        {
            lock (_gate)
            {
                var list = new List<AirportState>(_states.Count);
                foreach (AirportState state in _states.Values)
                {
                    list.Add(state.Clone());
                }

                return list;
            }
        }

        private void WorkerLoop()
        {
            BeginBackgroundIoMode();
            byte[] buffer = new byte[1024 * 1024]; // 1 MB sequential reads

            while (_running)
            {
                (AirportState state, PackageEntry entry) = TakeClosestQueued();
                if (state == null)
                {
                    _signal.WaitOne(1000);
                    continue;
                }

                WarmPackage(state, entry, buffer);
            }

            EndBackgroundIoMode();
        }

        /// <summary>Pick the nearest Queued package, mark it Warming, and return it.</summary>
        private (AirportState, PackageEntry) TakeClosestQueued()
        {
            lock (_gate)
            {
                string closestKey = null;
                AirportState closest = null;
                foreach (KeyValuePair<string, AirportState> pair in _states)
                {
                    if (pair.Value.State != PrefetchState.Queued)
                    {
                        continue;
                    }

                    if (closest == null || pair.Value.DistanceNauticalMiles < closest.DistanceNauticalMiles)
                    {
                        closest = pair.Value;
                        closestKey = pair.Key;
                    }
                }

                if (closestKey == null)
                {
                    return (null, null);
                }

                closest.State = PrefetchState.Warming;
                return (closest, _entries[closestKey]);
            }
        }

        private void WarmPackage(AirportState state, PackageEntry entry, byte[] buffer)
        {
            if (_bytesWarmed >= _ramBudgetBytes)
            {
                lock (_gate)
                {
                    state.State = PrefetchState.Skipped;
                }

                _log?.Invoke($"[prefetch] RAM budget full; skipping {entry.Icao} ({entry.PackageName}) " +
                             $"at {state.DistanceNauticalMiles:0} NM");
                return;
            }

            long warmedThisPackage = 0;
            int fileCount = 0;

            foreach (string filePath in entry.AbsoluteFilePaths)
            {
                if (!_running || _bytesWarmed >= _ramBudgetBytes)
                {
                    break;
                }

                long warmed = WarmFile(filePath, buffer);
                warmedThisPackage += warmed;
                lock (_gate)
                {
                    state.WarmedBytes += warmed;
                }

                fileCount++;
            }

            lock (_gate)
            {
                // Only finalize if it wasn't released mid-warm.
                if (state.State == PrefetchState.Warming)
                {
                    state.State = _bytesWarmed >= _ramBudgetBytes && warmedThisPackage < entry.TotalBytes
                        ? PrefetchState.Skipped
                        : PrefetchState.Loaded;
                }
            }

            _log?.Invoke($"[prefetch] {entry.Icao} \"{entry.PackageName}\" ({state.DistanceNauticalMiles:0} NM): " +
                         $"warmed {fileCount} files, {warmedThisPackage / (1024 * 1024)} MB " +
                         $"(cache total {_bytesWarmed / (1024 * 1024)} MB)");
        }

        private long WarmFile(string filePath, byte[] buffer)
        {
            long warmed = 0;
            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    buffer.Length,
                    FileOptions.SequentialScan);

                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    warmed += read;
                    Interlocked.Add(ref _bytesWarmed, read);
                    if (!_running || _bytesWarmed >= _ramBudgetBytes)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"  warn: cannot warm {filePath}: {ex.Message}");
            }

            return warmed;
        }

        public void Dispose()
        {
            _running = false;
            _signal.Set();
            _worker.Join(2000);
            _signal.Dispose();
        }

        // --- Windows background-I/O priority: reads yield to foreground (MSFS) I/O ---

        private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
        private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

        private void BeginBackgroundIoMode()
        {
            try { SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN); }
            catch { /* falls back to ThreadPriority.Lowest */ }
        }

        private void EndBackgroundIoMode()
        {
            try { SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END); }
            catch { /* ignore */ }
        }
    }
}
