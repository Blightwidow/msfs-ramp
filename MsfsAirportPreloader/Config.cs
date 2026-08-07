using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MsfsAirportPreloader
{
    /// <summary>Runtime settings, persisted to preloader.ini next to the executable.</summary>
    internal sealed class Config
    {
        /// <summary>Which colour theme the UI uses. System follows the Windows apps-theme setting.</summary>
        public enum ThemeMode { System, Light, Dark }

        /// <summary>MSFS starts streaming airport scenery around here; warming must begin before it.</summary>
        public const double MsfsLoadRadiusNauticalMiles = 25.0;

        /// <summary>
        /// Floor for the outer radius: comfortably above the 25 NM load radius so warming both
        /// starts and has time to finish before MSFS reads the files.
        /// </summary>
        public const double MinimumOuterRadiusNauticalMiles = 30.0;

        /// <summary>Start warming an airport's files once the aircraft is within this range.</summary>
        public double OuterRadiusNauticalMiles { get; set; } = 60.0;

        /// <summary>How often to poll aircraft position and re-evaluate the prefetch queue.</summary>
        public double PollSeconds { get; set; } = 2.0;

        /// <summary>Upper bound on how much we hold in page cache at once (freed as airports unload).</summary>
        public long RamBudgetMegabytes { get; set; } = 4096;

        /// <summary>Explicit MSFS InstalledPackagesPath. Empty = auto-detect from UserCfg.opt.</summary>
        public string InstalledPackagesPathOverride { get; set; } = "";

        /// <summary>Path to the OurAirports-format airports.csv. Empty = airports.csv beside exe.</summary>
        public string AirportsCsvPathOverride { get; set; } = "";

        /// <summary>Launch the app automatically at Windows login (registry Run key).</summary>
        public bool StartWithWindows { get; set; } = false;

        public bool Verbose { get; set; } = true;

        /// <summary>UI colour theme. Defaults to following the Windows apps-theme setting.</summary>
        public ThemeMode Appearance { get; set; } = ThemeMode.System;

        public Config Clone() => (Config)MemberwiseClone();

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

                values[line.Substring(0, equalsIndex).Trim()] = line.Substring(equalsIndex + 1).Trim();
            }

            config.OuterRadiusNauticalMiles = Math.Max(
                MinimumOuterRadiusNauticalMiles,
                ReadDouble(values, "OuterRadiusNauticalMiles", config.OuterRadiusNauticalMiles));
            config.PollSeconds = ReadDouble(values, "PollSeconds", config.PollSeconds);
            config.RamBudgetMegabytes = (long)ReadDouble(values, "RamBudgetMegabytes", config.RamBudgetMegabytes);
            config.InstalledPackagesPathOverride = ReadString(values, "InstalledPackagesPath", config.InstalledPackagesPathOverride);
            config.AirportsCsvPathOverride = ReadString(values, "AirportsCsvPath", config.AirportsCsvPathOverride);
            config.StartWithWindows = ReadBool(values, "StartWithWindows", config.StartWithWindows);
            config.Verbose = ReadBool(values, "Verbose", config.Verbose);
            config.Appearance = ReadEnum(values, "Theme", config.Appearance);
            return config;
        }

        public void Save(string iniPath)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# MSFS Airport Preloader configuration");
            builder.AppendLine("# Managed by the app's Settings panel; hand-edits are preserved on next save.");
            builder.AppendLine();
            builder.AppendLine("# Distance (NM) at which warming starts. Keep larger than MSFS's ~25 NM load radius.");
            builder.AppendLine(FormatDouble("OuterRadiusNauticalMiles", OuterRadiusNauticalMiles));
            builder.AppendLine();
            builder.AppendLine("# Position re-check interval (seconds).");
            builder.AppendLine(FormatDouble("PollSeconds", PollSeconds));
            builder.AppendLine();
            builder.AppendLine("# Max MB held in page cache at once (freed as airports leave range).");
            builder.AppendLine($"RamBudgetMegabytes = {RamBudgetMegabytes.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine();
            builder.AppendLine("# MSFS package folder (contains Community\\ and Official\\). Blank = auto-detect.");
            builder.AppendLine($"InstalledPackagesPath = {InstalledPackagesPathOverride}");
            builder.AppendLine();
            builder.AppendLine("# OurAirports airports.csv path. Blank = airports.csv beside the exe.");
            builder.AppendLine($"AirportsCsvPath = {AirportsCsvPathOverride}");
            builder.AppendLine();
            builder.AppendLine($"StartWithWindows = {(StartWithWindows ? "true" : "false")}");
            builder.AppendLine($"Verbose = {(Verbose ? "true" : "false")}");
            builder.AppendLine();
            builder.AppendLine("# UI theme: System (follow Windows), Light, or Dark.");
            builder.AppendLine($"Theme = {Appearance}");

            File.WriteAllText(iniPath, builder.ToString());
        }

        private static string FormatDouble(string key, double value)
            => $"{key} = {value.ToString(CultureInfo.InvariantCulture)}";

        private static string ReadString(Dictionary<string, string> values, string key, string fallback)
            => values.TryGetValue(key, out string value) && value.Length > 0 ? value : fallback;

        private static double ReadDouble(Dictionary<string, string> values, string key, double fallback)
            => values.TryGetValue(key, out string value) &&
               double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : fallback;

        private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
            => values.TryGetValue(key, out string value) && bool.TryParse(value, out bool parsed) ? parsed : fallback;

        private static ThemeMode ReadEnum(Dictionary<string, string> values, string key, ThemeMode fallback)
            => values.TryGetValue(key, out string value) && Enum.TryParse(value, ignoreCase: true, out ThemeMode parsed)
                ? parsed
                : fallback;
    }
}
