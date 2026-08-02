using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MsfsAirportPreloader
{
    /// <summary>One installed scenery package associated with a specific airport ICAO.</summary>
    internal sealed class PackageEntry
    {
        public PackageEntry(string icao, string packageName, List<string> absoluteFilePaths, long totalBytes)
        {
            Icao = icao;
            PackageName = packageName;
            AbsoluteFilePaths = absoluteFilePaths;
            TotalBytes = totalBytes;
        }

        public string Icao { get; }
        public string PackageName { get; }
        public List<string> AbsoluteFilePaths { get; }
        public long TotalBytes { get; }
    }

    /// <summary>
    /// Scans the MSFS Community + Official trees for SCENERY packages, extracts the ICAO
    /// each package belongs to (from its folder name / manifest title), and reads layout.json
    /// to get the concrete on-disk files. Result: ICAO -> the files we should warm.
    /// </summary>
    internal sealed class PackageIndex
    {
        private readonly Dictionary<string, List<PackageEntry>> _byIcao =
            new Dictionary<string, List<PackageEntry>>(StringComparer.OrdinalIgnoreCase);

        public int PackageCount { get; private set; }
        public int AirportCount => _byIcao.Count;

        public bool TryGet(string icao, out List<PackageEntry> entries) => _byIcao.TryGetValue(icao, out entries);

        /// <summary>Snapshot of every ICAO we have at least one package for.</summary>
        public string[] IndexedIcaos()
        {
            var icaos = new string[_byIcao.Count];
            _byIcao.Keys.CopyTo(icaos, 0);
            return icaos;
        }

        private static readonly Regex ContentTypeRegex =
            new Regex("\"content_type\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TitleRegex =
            new Regex("\"title\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches each layout.json content entry, capturing the file path and its byte size.
        private static readonly Regex LayoutEntryRegex =
            new Regex("\"path\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"[^}]*?\"size\"\\s*:\\s*(\\d+)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex IcaoTokenRegex =
            new Regex("[A-Za-z0-9]{3,4}", RegexOptions.Compiled);

        public static PackageIndex Build(string installedPackagesPath, AirportDatabase airportDatabase, Action<string> log)
        {
            var index = new PackageIndex();

            foreach (string rootName in new[] { "Community", "Official" })
            {
                string root = Path.Combine(installedPackagesPath, rootName);
                if (!Directory.Exists(root))
                {
                    continue;
                }

                IEnumerable<string> manifestFiles;
                try
                {
                    manifestFiles = Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  warn: cannot enumerate {root}: {ex.Message}");
                    continue;
                }

                foreach (string manifestPath in manifestFiles)
                {
                    try
                    {
                        index.TryAddPackage(manifestPath, airportDatabase);
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"  warn: skipped package {manifestPath}: {ex.Message}");
                    }
                }
            }

            return index;
        }

        private void TryAddPackage(string manifestPath, AirportDatabase airportDatabase)
        {
            string packageRoot = Path.GetDirectoryName(manifestPath);
            if (packageRoot == null)
            {
                return;
            }

            string manifestText = File.ReadAllText(manifestPath);
            Match contentTypeMatch = ContentTypeRegex.Match(manifestText);
            if (!contentTypeMatch.Success ||
                contentTypeMatch.Groups[1].Value.IndexOf("SCENERY", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return; // only scenery packages hold airport BGLs/models
            }

            string title = TitleRegex.Match(manifestText).Groups[1].Value;
            string packageName = Path.GetFileName(packageRoot);

            HashSet<string> icaoCandidates = ExtractIcaoCandidates(packageName + " " + title, airportDatabase);
            if (icaoCandidates.Count == 0)
            {
                return; // cannot attribute this package to a known airport
            }

            string layoutPath = Path.Combine(packageRoot, "layout.json");
            if (!File.Exists(layoutPath))
            {
                return;
            }

            (List<string> files, long totalBytes) = ReadLayoutFiles(layoutPath, packageRoot);
            if (files.Count == 0)
            {
                return;
            }

            foreach (string icao in icaoCandidates)
            {
                var entry = new PackageEntry(icao, packageName, files, totalBytes);
                if (!_byIcao.TryGetValue(icao, out List<PackageEntry> list))
                {
                    list = new List<PackageEntry>();
                    _byIcao[icao] = list;
                }

                list.Add(entry);
            }

            PackageCount++;
        }

        private static HashSet<string> ExtractIcaoCandidates(string source, AirportDatabase airportDatabase)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in IcaoTokenRegex.Matches(source))
            {
                string token = match.Value.ToUpperInvariant();
                // Only keep tokens that are real airports in the DB; this filters publisher
                // names, version tokens, etc. ICAO codes are 4 alphanumerics; keep 3-char too
                // for the handful of DB idents that are shorter.
                if (airportDatabase.TryGet(token, out _))
                {
                    candidates.Add(token);
                }
            }

            return candidates;
        }

        private static (List<string> files, long totalBytes) ReadLayoutFiles(string layoutPath, string packageRoot)
        {
            var files = new List<string>();
            long totalBytes = 0;

            string layoutText = File.ReadAllText(layoutPath);
            foreach (Match match in LayoutEntryRegex.Matches(layoutText))
            {
                string relativePath = JsonUnescape(match.Groups[1].Value).Replace('/', Path.DirectorySeparatorChar);
                if (!long.TryParse(match.Groups[2].Value, out long size))
                {
                    size = 0;
                }

                string absolutePath = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
                files.Add(absolutePath);
                totalBytes += size;
            }

            return (files, totalBytes);
        }

        private static string JsonUnescape(string value)
        {
            if (value.IndexOf('\\') < 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (character == '\\' && i + 1 < value.Length)
                {
                    char next = value[++i];
                    switch (next)
                    {
                        case 'n': builder.Append('\n'); break;
                        case 't': builder.Append('\t'); break;
                        case 'r': builder.Append('\r'); break;
                        case '/': builder.Append('/'); break;
                        case '\\': builder.Append('\\'); break;
                        case '"': builder.Append('"'); break;
                        default: builder.Append(next); break;
                    }
                }
                else
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }
    }
}
