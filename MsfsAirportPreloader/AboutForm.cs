using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace MsfsAirportPreloader
{
    /// <summary>
    /// A small About dialog: the mark, what RAMP is, and credit for the airport data. Airport
    /// coordinates come from OurAirports, released into the public domain — linked here.
    /// </summary>
    internal sealed class AboutForm : Form
    {
        private const string OurAirportsUrl = "https://github.com/davidmegginson/ourairports-data";

        public AboutForm()
        {
            Text = "About RAMP";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 300);
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = Theme.Sans(9f);

            BuildLayout();
        }

        private void BuildLayout()
        {
            const int left = 24;

            var header = new BufferedPanel { Left = 0, Top = 0, Width = ClientSize.Width, Height = 96, BackColor = Theme.Window };
            header.Paint += (_, e) =>
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.DrawLogo(g, new RectangleF(left, 24, 48, 48), Theme.Raised, Theme.Accent, Theme.Border);
                TextRenderer.DrawText(g, "RAMP", Theme.Mono(18f, FontStyle.Bold),
                    new Point(left + 62, 28), Theme.Text, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "RAM Airport Preloader", Theme.Sans(9.5f),
                    new Point(left + 64, 58), Theme.TextDim, TextFormatFlags.NoPadding);
            };
            Controls.Add(header);

            Controls.Add(new Label
            {
                Left = left,
                Top = 104,
                Width = ClientSize.Width - left * 2,
                Height = 40,
                Font = Theme.Sans(9.5f),
                ForeColor = Theme.TextMuted,
                Text = "Warms MSFS airport scenery into the Windows file cache before you arrive, " +
                       "so the sim streams it from RAM instead of disk.",
            });

            Controls.Add(new Label
            {
                Left = left,
                Top = 150,
                Width = ClientSize.Width - left * 2,
                Height = 16,
                Font = Theme.Mono(8f),
                ForeColor = Theme.TextFaint,
                Text = $"Version {AppVersion()}",
            });

            var divider = new BufferedPanel
            {
                Left = left,
                Top = 182,
                Width = ClientSize.Width - left * 2,
                Height = 1,
                BackColor = Theme.Border,
            };
            Controls.Add(divider);

            Controls.Add(new Label
            {
                Left = left,
                Top = 196,
                Width = ClientSize.Width - left * 2,
                Height = 16,
                Font = Theme.Sans(9f),
                ForeColor = Theme.TextMuted,
                Text = "Airport coordinates from OurAirports — public-domain data:",
            });

            var link = new LinkLabel
            {
                Left = left,
                Top = 216,
                Width = ClientSize.Width - left * 2,
                Height = 18,
                Font = Theme.Mono(8.5f),
                Text = OurAirportsUrl,
                LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.Text,
                LinkBehavior = LinkBehavior.HoverUnderline,
                BackColor = Theme.Window,
            };
            link.LinkClicked += (_, __) => OpenUrl(OurAirportsUrl);
            Controls.Add(link);

            var close = new RoundedButton
            {
                Text = "Close",
                DialogResult = DialogResult.OK,
                Font = Theme.Sans(9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0x08, 0x12, 0x1A),
                BackColor = Theme.Accent,
                BorderColor = Color.Empty,
                Width = 92,
                Height = 34,
                Top = ClientSize.Height - 50,
                Left = ClientSize.Width - 92 - 24,
                TabStop = false,
            };
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
        }

        private static string AppVersion()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // no default browser / blocked — nothing else to do from here
            }
        }
    }
}
