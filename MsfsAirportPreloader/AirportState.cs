namespace MsfsAirportPreloader
{
    internal enum PrefetchState
    {
        /// <summary>Beyond the outer radius (or flown far enough away to be released).</summary>
        OutOfRange,

        /// <summary>In range, waiting for the warming thread.</summary>
        Queued,

        /// <summary>Currently being read into the page cache.</summary>
        Warming,

        /// <summary>Fully warmed — files resident in RAM, ready for MSFS's 25 NM load.</summary>
        Loaded,

        /// <summary>In range but skipped because the RAM budget was exhausted.</summary>
        Skipped,
    }

    /// <summary>UI-facing snapshot of one airport package's prefetch status.</summary>
    internal sealed class AirportState
    {
        public string Icao { get; set; }
        public string PackageName { get; set; }
        public long TotalBytes { get; set; }
        public double DistanceNauticalMiles { get; set; }
        public PrefetchState State { get; set; }
        public long WarmedBytes { get; set; }

        public AirportState Clone() => (AirportState)MemberwiseClone();
    }
}
