using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MsfsAirportPreloader;
using Xunit;

namespace MsfsAirportPreloader.Tests
{
    public sealed class PrefetcherTests : IDisposable
    {
        private const long OneMegabyte = 1024L * 1024L;
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

        private readonly string _temporaryDirectory =
            Path.Combine(Path.GetTempPath(), "ramp-tests-" + Guid.NewGuid().ToString("N"));

        public PrefetcherTests()
        {
            Directory.CreateDirectory(_temporaryDirectory);
        }

        public void Dispose()
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        [Fact]
        public void ObservedPackageIsWarmedToLoaded()
        {
            using var prefetcher = new Prefetcher(ramBudgetMegabytes: 4, log: null);
            PackageEntry entry = CreatePackage("LFPG", sizeInMegabytes: 1);

            prefetcher.Observe(entry, distanceNauticalMiles: 40);

            Assert.Equal(PrefetchState.Loaded, WaitForSettledState(prefetcher, "LFPG"));
        }

        [Fact]
        public void PackageBeyondBudgetIsSkipped()
        {
            using var prefetcher = new Prefetcher(ramBudgetMegabytes: 1, log: null);
            prefetcher.Paused = true;
            prefetcher.Observe(CreatePackage("LFPG", sizeInMegabytes: 1), distanceNauticalMiles: 10);
            prefetcher.Observe(CreatePackage("LFPO", sizeInMegabytes: 1), distanceNauticalMiles: 50);

            prefetcher.Paused = false;

            Assert.Equal(PrefetchState.Skipped, WaitForSettledState(prefetcher, "LFPO"));
        }

        [Fact]
        public void ClosestQueuedPackageWinsTheBudget()
        {
            using var prefetcher = new Prefetcher(ramBudgetMegabytes: 1, log: null);
            prefetcher.Paused = true;
            prefetcher.Observe(CreatePackage("LFPO", sizeInMegabytes: 1), distanceNauticalMiles: 50);
            prefetcher.Observe(CreatePackage("LFPG", sizeInMegabytes: 1), distanceNauticalMiles: 10);

            prefetcher.Paused = false;

            Assert.Equal(PrefetchState.Loaded, WaitForSettledState(prefetcher, "LFPG"));
        }

        [Fact]
        public void ReleaseReturnsLoadedPackageToOutOfRange()
        {
            using var prefetcher = new Prefetcher(ramBudgetMegabytes: 4, log: null);
            PackageEntry entry = CreatePackage("LFPG", sizeInMegabytes: 1);
            prefetcher.Observe(entry, distanceNauticalMiles: 40);
            WaitForSettledState(prefetcher, "LFPG");

            prefetcher.Release(entry);

            Assert.Equal(PrefetchState.OutOfRange, StateOf(prefetcher, "LFPG").State);
        }

        [Fact]
        public void RaisingBudgetWarmsSkippedPackage()
        {
            using var prefetcher = new Prefetcher(ramBudgetMegabytes: 1, log: null);
            prefetcher.Paused = true;
            prefetcher.Observe(CreatePackage("LFPG", sizeInMegabytes: 1), distanceNauticalMiles: 10);
            prefetcher.Observe(CreatePackage("LFPO", sizeInMegabytes: 1), distanceNauticalMiles: 50);
            prefetcher.Paused = false;
            WaitForSettledState(prefetcher, "LFPO");

            prefetcher.SetRamBudget(ramBudgetMegabytes: 2);

            Assert.Equal(PrefetchState.Loaded, WaitForSettledState(prefetcher, "LFPO"));
        }

        [Fact]
        public void LoweringBudgetSkipsNextPackage()
        {
            using var prefetcher = new Prefetcher(ramBudgetMegabytes: 4, log: null);
            prefetcher.Observe(CreatePackage("LFPG", sizeInMegabytes: 1), distanceNauticalMiles: 10);
            WaitForSettledState(prefetcher, "LFPG");

            prefetcher.SetRamBudget(ramBudgetMegabytes: 1);
            prefetcher.Observe(CreatePackage("LFPO", sizeInMegabytes: 1), distanceNauticalMiles: 50);

            Assert.Equal(PrefetchState.Skipped, WaitForSettledState(prefetcher, "LFPO"));
        }

        [Fact]
        public void ResumedPackageCountsEachByteOnce()
        {
            // 2 MB budget: LFPG takes 1 MB, so LFPO (2 MB) is cut off after its first 1 MB.
            using var prefetcher = new Prefetcher(ramBudgetMegabytes: 2, log: null);
            prefetcher.Paused = true;
            prefetcher.Observe(CreatePackage("LFPG", sizeInMegabytes: 1), distanceNauticalMiles: 10);
            prefetcher.Observe(CreatePackage("LFPO", sizeInMegabytes: 2), distanceNauticalMiles: 50);
            prefetcher.Paused = false;
            WaitForSettledState(prefetcher, "LFPO");

            // Resuming must read only the unread second half, not re-read and re-charge the first.
            prefetcher.SetRamBudget(ramBudgetMegabytes: 8);
            WaitForSettledState(prefetcher, "LFPO");

            Assert.Equal(2 * OneMegabyte, StateOf(prefetcher, "LFPO").WarmedBytes);
        }

        private PackageEntry CreatePackage(string icao, int sizeInMegabytes)
        {
            string filePath = Path.Combine(_temporaryDirectory, icao + ".bgl");
            using (FileStream stream = File.Create(filePath))
            {
                stream.SetLength(sizeInMegabytes * OneMegabyte);
            }

            return new PackageEntry(icao, icao.ToLowerInvariant() + "-airport",
                new List<string> { filePath }, sizeInMegabytes * OneMegabyte);
        }

        internal static AirportState StateOf(Prefetcher prefetcher, string icao)
            => prefetcher.Snapshot().Single(state => state.Icao == icao);

        /// <summary>Polls until the package leaves Queued/Warming (the worker runs on its own thread).</summary>
        internal static PrefetchState WaitForSettledState(Prefetcher prefetcher, string icao)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < WaitTimeout)
            {
                PrefetchState state = StateOf(prefetcher, icao).State;
                if (state != PrefetchState.Queued && state != PrefetchState.Warming)
                {
                    return state;
                }

                Thread.Sleep(10);
            }

            throw new TimeoutException($"{icao} never settled");
        }
    }
}
