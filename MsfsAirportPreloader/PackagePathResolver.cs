using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MsfsAirportPreloader
{
    /// <summary>Finds the MSFS 2020 InstalledPackagesPath from UserCfg.opt (Store + Steam layouts).</summary>
    internal static class PackagePathResolver
    {
        private static readonly Regex InstalledPackagesPathRegex =
            new Regex("InstalledPackagesPath\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Resolve(string overridePath, Action<string> log)
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return overridePath;
            }

            foreach (string userCfgPath in CandidateUserCfgPaths())
            {
                if (!File.Exists(userCfgPath))
                {
                    continue;
                }

                Match match = InstalledPackagesPathRegex.Match(File.ReadAllText(userCfgPath));
                if (match.Success)
                {
                    string path = match.Groups[1].Value;
                    log?.Invoke($"InstalledPackagesPath from {userCfgPath}: {path}");
                    return path;
                }
            }

            return null;
        }

        private static string[] CandidateUserCfgPaths()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            return new[]
            {
                // Microsoft Store / Game Pass
                Path.Combine(localAppData, "Packages",
                    "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
                // Steam
                Path.Combine(appData, "Microsoft Flight Simulator", "UserCfg.opt"),
                // Boxed / other
                Path.Combine(localAppData, "Microsoft Flight Simulator", "UserCfg.opt"),
            };
        }
    }
}
