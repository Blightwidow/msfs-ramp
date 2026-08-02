using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MsfsAirportPreloader
{
    /// <summary>Runtime settings, loaded from preloader.ini next to the executable.</summary>
    internal sealed class Config
    {
        /// <summary>Start warming an airport's files once the aircraft is within this range.</summary>
        public double OuterRadiusNauticalMiles { get; private set; } = 60.0;

        /// <summary>How often to poll aircraft position and re-evaluate the prefetch queue.</summary>
        public double PollSeconds { get; private set; } = 2.0;

        /// <summary>Upper bound on how much we read into page cache per session (soft guard).</summary>
        public long RamBudgetMegabytes { get; private set; } = 4096;

        /// <summary>Explicit MSFS InstalledPackagesPath. Empty = auto-detect from UserCfg.opt.</summary>
        public string InstalledPackagesPathOverride { get; private set; } = "";

        /// <summary>Path to the OurAirports-format airports.csv. Empty = airports.csv beside exe.</summary>
        public string AirportsCsvPathOverride { get; private set; } = "";

        public bool Verbose { get; private set; } = true;

        public static Config Load(string iniPath)
        {
            var config = new Config();
            if (!File.Exists(iniPath))
            {
                return config;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in File.ReadAllLines(iniPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("["))
                {
                    continue;
                }

                int equalsIndex = line.IndexOf('=');
                if (equalsIndex <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, equalsIndex).Trim();
                string value = line.Substring(equalsIndex + 1).Trim();
                values[key] = value;
            }

            config.OuterRadiusNauticalMiles = ReadDouble(values, "OuterRadiusNauticalMiles", config.OuterRadiusNauticalMiles);
            config.PollSeconds = ReadDouble(values, "PollSeconds", config.PollSeconds);
            config.RamBudgetMegabytes = (long)ReadDouble(values, "RamBudgetMegabytes", config.RamBudgetMegabytes);
            config.InstalledPackagesPathOverride = ReadString(values, "InstalledPackagesPath", config.InstalledPackagesPathOverride);
            config.AirportsCsvPathOverride = ReadString(values, "AirportsCsvPath", config.AirportsCsvPathOverride);
            config.Verbose = ReadBool(values, "Verbose", config.Verbose);
            return config;
        }

        private static string ReadString(Dictionary<string, string> values, string key, string fallback)
            => values.TryGetValue(key, out string value) && value.Length > 0 ? value : fallback;

        private static double ReadDouble(Dictionary<string, string> values, string key, double fallback)
            => values.TryGetValue(key, out string value) &&
               double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : fallback;

        private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
            => values.TryGetValue(key, out string value) && bool.TryParse(value, out bool parsed) ? parsed : fallback;
    }
}
