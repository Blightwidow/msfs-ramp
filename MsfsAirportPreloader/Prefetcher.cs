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
    /// This does NOT inject into MSFS and does NOT parse/alter scenery. It only reads
    /// bytes and throws them away; the side effect is a hot OS cache. That is the whole
    /// mechanism, and it is why it cannot crash or corrupt the sim.
    /// </summary>
    internal sealed class Prefetcher : IDisposable
    {
        private readonly long _ramBudgetBytes;
        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private readonly Queue<PackageEntry> _queue = new Queue<PackageEntry>();
        private readonly HashSet<string> _alreadyDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        /// <summary>Queue a package for warming. No-op if already warmed this session.</summary>
        public void Enqueue(PackageEntry entry)
        {
            lock (_gate)
            {
                string key = entry.Icao + "|" + entry.PackageName;
                if (_alreadyDone.Contains(key))
                {
                    return;
                }

                _alreadyDone.Add(key);
                _queue.Enqueue(entry);
            }

            _signal.Set();
        }

        private void WorkerLoop()
        {
            BeginBackgroundIoMode();
            byte[] buffer = new byte[1024 * 1024]; // 1 MB sequential reads

            while (_running)
            {
                PackageEntry entry = null;
                lock (_gate)
                {
                    if (_queue.Count > 0)
                    {
                        entry = _queue.Dequeue();
                    }
                }

                if (entry == null)
                {
                    _signal.WaitOne(1000);
                    continue;
                }

                WarmPackage(entry, buffer);
            }

            EndBackgroundIoMode();
        }

        private void WarmPackage(PackageEntry entry, byte[] buffer)
        {
            if (_bytesWarmed >= _ramBudgetBytes)
            {
                _log?.Invoke($"[prefetch] RAM budget reached; skipping {entry.Icao} ({entry.PackageName})");
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
                $"[prefetch] {entry.Icao} \"{entry.PackageName}\": warmed {fileCount} files, " +
                $"{warmedThisPackage / (1024 * 1024)} MB (session total {_bytesWarmed / (1024 * 1024)} MB)");
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
