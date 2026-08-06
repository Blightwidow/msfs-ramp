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
    /// Pending packages are warmed in ascending distance order: the closest airport
    /// (your destination as you approach) always warms before farther enroute airports,
    /// so it can never be starved by them. Distances are refreshed on every position poll.
    ///
    /// This does NOT inject into MSFS and does NOT parse/alter scenery. It only reads
    /// bytes and throws them away; the side effect is a hot OS cache.
    /// </summary>
    internal sealed class Prefetcher : IDisposable
    {
        private sealed class Pending
        {
            public PackageEntry Entry;
            public double DistanceNauticalMiles;
        }

        private readonly long _ramBudgetBytes;
        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        /// <summary>
        /// Report that <paramref name="entry"/> is currently <paramref name="distanceNauticalMiles"/>
        /// away. Adds it to the pending set (or refreshes its distance so the priority order stays
        /// current). No-op once the package has been warmed this session.
        /// </summary>
        public void Observe(PackageEntry entry, double distanceNauticalMiles)
        {
            string key = Key(entry);
            lock (_gate)
            {
                if (_done.Contains(key))
                {
                    return;
                }

                if (_pending.TryGetValue(key, out Pending existing))
                {
                    existing.DistanceNauticalMiles = distanceNauticalMiles;
                    return;
                }

                _pending[key] = new Pending { Entry = entry, DistanceNauticalMiles = distanceNauticalMiles };
                _log?.Invoke(
                    $"[queue] {entry.Icao} \"{entry.PackageName}\" at {distanceNauticalMiles:0} NM " +
                    $"({entry.TotalBytes / (1024 * 1024)} MB) — {_pending.Count} pending.");
            }

            _signal.Set();
        }

        private static string Key(PackageEntry entry) => entry.Icao + "|" + entry.PackageName;

        private void WorkerLoop()
        {
            BeginBackgroundIoMode();
            byte[] buffer = new byte[1024 * 1024]; // 1 MB sequential reads

            while (_running)
            {
                Pending next = TakeClosest();
                if (next == null)
                {
                    _signal.WaitOne(1000);
                    continue;
                }

                WarmPackage(next, buffer);
            }

            EndBackgroundIoMode();
        }

        /// <summary>Remove and return the closest pending package, marking it done. Null if none.</summary>
        private Pending TakeClosest()
        {
            lock (_gate)
            {
                string closestKey = null;
                Pending closest = null;
                foreach (KeyValuePair<string, Pending> pair in _pending)
                {
                    if (closest == null || pair.Value.DistanceNauticalMiles < closest.DistanceNauticalMiles)
                    {
                        closest = pair.Value;
                        closestKey = pair.Key;
                    }
                }

                if (closestKey == null)
                {
                    return null;
                }

                _pending.Remove(closestKey);
                _done.Add(closestKey);
                return closest;
            }
        }

        private void WarmPackage(Pending pending, byte[] buffer)
        {
            PackageEntry entry = pending.Entry;
            if (_bytesWarmed >= _ramBudgetBytes)
            {
                _log?.Invoke(
                    $"[prefetch] RAM budget reached; skipping {entry.Icao} ({entry.PackageName}) " +
                    $"at {pending.DistanceNauticalMiles:0} NM");
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

                warmedThisPackage += WarmFile(filePath, buffer);
                fileCount++;
            }

            _log?.Invoke(
                $"[prefetch] {entry.Icao} \"{entry.PackageName}\" ({pending.DistanceNauticalMiles:0} NM): " +
                $"warmed {fileCount} files, {warmedThisPackage / (1024 * 1024)} MB " +
                $"(session total {_bytesWarmed / (1024 * 1024)} MB)");
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
            try
            {
                SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
            }
            catch
            {
                // Non-fatal: falls back to plain ThreadPriority.Lowest.
            }
        }

        private void EndBackgroundIoMode()
        {
            try
            {
                SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
            }
            catch
            {
                // ignore
            }
        }
    }
}
