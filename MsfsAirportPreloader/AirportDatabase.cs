using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MsfsAirportPreloader
{
    internal readonly struct AirportLocation
    {
        public AirportLocation(string icao, double latitude, double longitude)
        {
            Icao = icao;
            Latitude = latitude;
            Longitude = longitude;
        }

        public string Icao { get; }
        public double Latitude { get; }
        public double Longitude { get; }
    }

    /// <summary>
    /// ICAO -> coordinate lookup, sourced from an OurAirports-format airports.csv
    /// (https://ourairports.com/data/ , free public domain).
    /// Only used to know WHERE each installed airport package sits, so we can measure
    /// distance from the aircraft. MSFS itself is never queried for this.
    /// </summary>
    internal sealed class AirportDatabase
    {
        private readonly Dictionary<string, AirportLocation> _byIcao =
            new Dictionary<string, AirportLocation>(StringComparer.OrdinalIgnoreCase);

        public int Count => _byIcao.Count;

        public bool TryGet(string icao, out AirportLocation location) => _byIcao.TryGetValue(icao, out location);

        public static AirportDatabase Load(string csvPath)
        {
            var database = new AirportDatabase();
            if (!File.Exists(csvPath))
            {
                return database;
            }

            using var reader = new StreamReader(csvPath);
            string headerLine = reader.ReadLine();
            if (headerLine == null)
            {
                return database;
            }

            string[] header = ParseCsvLine(headerLine);
            int identColumn = IndexOf(header, "ident");
            int latColumn = IndexOf(header, "latitude_deg");
            int lonColumn = IndexOf(header, "longitude_deg");
            if (identColumn < 0 || latColumn < 0 || lonColumn < 0)
            {
                return database;
            }

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] fields = ParseCsvLine(line);
                if (fields.Length <= Math.Max(identColumn, Math.Max(latColumn, lonColumn)))
                {
                    continue;
                }

                string icao = fields[identColumn].Trim();
                if (icao.Length == 0)
                {
                    continue;
                }

                if (!double.TryParse(fields[latColumn], NumberStyles.Any, CultureInfo.InvariantCulture, out double latitude) ||
                    !double.TryParse(fields[lonColumn], NumberStyles.Any, CultureInfo.InvariantCulture, out double longitude))
                {
                    continue;
                }

                database._byIcao[icao] = new AirportLocation(icao, latitude, longitude);
            }

            return database;
        }

        private static int IndexOf(string[] header, string name)
        {
            for (int i = 0; i < header.Length; i++)
            {
                if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Minimal RFC-4180-ish CSV splitter: handles quoted fields and escaped quotes.</summary>
        private static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char character = line[i];
                if (inQuotes)
                {
                    if (character == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(character);
                    }
                }
                else if (character == '"')
                {
                    inQuotes = true;
                }
                else if (character == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(character);
                }
            }

            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}
