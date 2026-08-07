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
        private const string RepoUrl = "https://github.com/Blightwidow/msfs-ramp";

        public AboutForm()
        {
            Text = "About RAMP";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(430, 384);
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

            int width = ClientSize.Width - left * 2;

            Controls.Add(new Label
            {
                Left = left,
                Top = 102,
                Width = width,
                Height = 34,
                Font = Theme.Sans(9.5f),
                ForeColor = Theme.TextMuted,
                Text = "Warms MSFS airport scenery into the Windows file cache before you arrive, " +
                       "so the sim streams it from RAM instead of disk.",
            });

            // Free & open source + project repo.
            Controls.Add(new Label
            {
                Left = left,
                Top = 142,
                Width = width,
                Height = 18,
                Font = Theme.Sans(9.5f, FontStyle.Bold),
                ForeColor = Theme.Loaded,
                Text = "Free and open source",
            });
            AddLink(left, 162, width, RepoUrl);

            AddDivider(left, 190, width);

            // Airport data credit + thanks.
            Controls.Add(new Label
            {
                Left = left,
                Top = 202,
                Width = width,
                Height = 34,
                Font = Theme.Sans(9f),
                ForeColor = Theme.TextMuted,
                Text = "Airport coordinates come from OurAirports, generously released into the " +
                       "public domain. Huge thanks to its contributors — RAMP wouldn't work without it.",
            });
            AddLink(left, 244, width, OurAirportsUrl);

            Controls.Add(new Label
            {
                Left = left,
                Top = 296,
                Width = width,
                Height = 16,
                Font = Theme.Mono(8f),
                ForeColor = Theme.TextFaint,
                Text = $"Version {AppVersion()}",
            });

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

        private void AddLink(int left, int top, int width, string url)
        {
            var link = new LinkLabel
            {
                Left = left,
                Top = top,
                Width = width,
                Height = 18,
                Font = Theme.Mono(8.5f),
                Text = url,
                LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.Text,
                LinkBehavior = LinkBehavior.HoverUnderline,
                BackColor = Theme.Window,
            };
            link.LinkClicked += (_, __) => OpenUrl(url);
            Controls.Add(link);
        }

        private void AddDivider(int left, int top, int width)
        {
            Controls.Add(new BufferedPanel { Left = left, Top = top, Width = width, Height = 1, BackColor = Theme.Border });
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
