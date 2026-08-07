using System;
using System.Net;
using System.Xml.Linq;

namespace MsfsAirportPreloader
{
    /// <summary>The airport endpoints extracted from a SimBrief OFP that RAMP force-warms.</summary>
    internal sealed class SimBriefPlan
    {
        public string DestinationIcao { get; set; }
        public string AlternateIcao { get; set; }
    }

    /// <summary>
    /// Fetches the user's latest SimBrief OFP and extracts the arrival + alternate ICAOs.
    ///
    /// Uses the public XML fetcher (no auth beyond the numeric Pilot ID) so we can parse it with
    /// the built-in System.Xml.Linq — no JSON dependency. Every failure (no internet, bad ID,
    /// empty OFP, malformed XML) is logged and returns null; the caller then falls back to the
    /// normal proximity warming.
    /// </summary>
    internal static class SimBriefClient
    {
        private const string FetchUrlFormat = "https://www.simbrief.com/api/xml.fetcher.php?userid={0}";
        private const int TimeoutMilliseconds = 10000;

        public static SimBriefPlan Fetch(string userId, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return null;
            }

            string trimmedId = userId.Trim();
            try
            {
                // .NET Framework 4.8 defaults can omit TLS 1.2, which simbrief.com requires.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

                string url = string.Format(FetchUrlFormat, Uri.EscapeDataString(trimmedId));
                string xml;
                using (var client = new TimedWebClient(TimeoutMilliseconds))
                {
                    client.Encoding = System.Text.Encoding.UTF8; // WebClient defaults to system ANSI
                    client.Headers[HttpRequestHeader.UserAgent] = "RAMP (RAM Airport Preloader)";
                    xml = client.DownloadString(url);
                }

                XElement ofp = XDocument.Parse(xml).Root;
                if (ofp == null)
                {
                    log?.Invoke("SimBrief: empty response — skipping (proximity warming still active).");
                    return null;
                }

                string destination = ReadIcao(ofp, "destination");
                string alternate = ReadIcao(ofp, "alternate");

                if (destination == null && alternate == null)
                {
                    log?.Invoke("SimBrief: OFP had no arrival/alternate ICAO — skipping.");
                    return null;
                }

                return new SimBriefPlan { DestinationIcao = destination, AlternateIcao = alternate };
            }
            catch (Exception ex)
            {
                log?.Invoke($"SimBrief: fetch failed ({ex.Message}) — skipping (proximity warming still active).");
                return null;
            }
        }

        /// <summary>Reads /OFP/&lt;section&gt;/icao_code, upper-cased; null when absent or blank.</summary>
        private static string ReadIcao(XElement ofp, string section)
        {
            string value = ofp.Element(section)?.Element("icao_code")?.Value?.Trim();
            return string.IsNullOrEmpty(value) ? null : value.ToUpperInvariant();
        }

        /// <summary>WebClient with a real timeout — the base class has none for DownloadString.</summary>
        private sealed class TimedWebClient : WebClient
        {
            private readonly int _timeoutMilliseconds;

            public TimedWebClient(int timeoutMilliseconds) => _timeoutMilliseconds = timeoutMilliseconds;

            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request != null)
                {
                    request.Timeout = _timeoutMilliseconds;
                }

                return request;
            }
        }
    }
}
