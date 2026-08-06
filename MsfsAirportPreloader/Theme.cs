using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace MsfsAirportPreloader
{
    /// <summary>
    /// The "Apron" visual language: a dark instrument-panel palette, the IBM Plex type pair
    /// (with graceful fallback to fonts that ship with Windows), and the handful of custom
    /// paint primitives — rounded surfaces, status chips, the striped warming bar — that the
    /// design needs and stock WinForms controls can't draw. Everything here is static and
    /// stateless; the forms own their own controls and call in to paint.
    /// </summary>
    internal static class Theme
    {
        // --- Surfaces (dark theme) -------------------------------------------------------
        public static readonly Color Canvas = FromHex("#080C11"); // desktop behind the window
        public static readonly Color Sunken = FromHex("#0B1219"); // header/footer bars
        public static readonly Color Window = FromHex("#0E151D"); // main window body
        public static readonly Color Raised = FromHex("#111A24"); // toolbar, cards, inputs
        public static readonly Color RaisedHover = FromHex("#16222E");
        public static readonly Color Border = FromHex("#1F2E3C");
        public static readonly Color BorderSoft = FromHex("#1A2732");
        public static readonly Color InputBg = FromHex("#131E28");
        public static readonly Color InputBorder = FromHex("#26333F");

        // --- Text ------------------------------------------------------------------------
        public static readonly Color Text = FromHex("#E4EDF5");
        public static readonly Color TextMuted = FromHex("#9DB2C4");
        public static readonly Color TextDim = FromHex("#6E869A");
        public static readonly Color TextFaint = FromHex("#5C7A90");

        // --- Status palette — distinct in hue AND lightness (safe for deutan/protan) -----
        public static readonly Color Loaded = FromHex("#46D08A");
        public static readonly Color Warming = FromHex("#3FC7F4");
        public static readonly Color Queued = FromHex("#9B8CFA");
        public static readonly Color Skipped = FromHex("#F0B429");
        public static readonly Color Unloaded = FromHex("#5C6E7E");

        public static readonly Color Accent = Warming;

        /// <summary>Rail / chip / dot colour for a prefetch state.</summary>
        public static Color StateColor(PrefetchState state)
        {
            switch (state)
            {
                case PrefetchState.Loaded: return Loaded;
                case PrefetchState.Warming: return Warming;
                case PrefetchState.Queued: return Queued;
                case PrefetchState.Skipped: return Skipped;
                default: return Unloaded;
            }
        }

        /// <summary>Short all-caps label used in the state chip.</summary>
        public static string StateLabel(PrefetchState state)
        {
            switch (state)
            {
                case PrefetchState.Loaded: return "LOADED";
                case PrefetchState.Warming: return "WARMING";
                case PrefetchState.Queued: return "QUEUED";
                case PrefetchState.Skipped: return "SKIPPED";
                default: return "UNLOADED";
            }
        }

        // --- Type ------------------------------------------------------------------------
        // IBM Plex is the intended pair; fall back to fonts guaranteed on Windows so the app
        // still renders correctly on machines that don't have Plex installed.
        private static readonly string SansFamily = ResolveFamily("IBM Plex Sans", "Segoe UI");
        private static readonly string MonoFamily = ResolveFamily("IBM Plex Mono", "Consolas");
        private static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();

        public static Font Sans(float size, FontStyle style = FontStyle.Regular) => Get(SansFamily, size, style);
        public static Font Mono(float size, FontStyle style = FontStyle.Regular) => Get(MonoFamily, size, style);

        private static Font Get(string family, float size, FontStyle style)
        {
            string key = family + "|" + size.ToString("0.##") + "|" + (int)style;
            if (!FontCache.TryGetValue(key, out Font font))
            {
                font = new Font(family, size, style, GraphicsUnit.Point);
                FontCache[key] = font;
            }

            return font;
        }

        private static string ResolveFamily(string preferred, string fallback)
        {
            try
            {
                using var installed = new InstalledFontCollection();
                foreach (FontFamily family in installed.Families)
                {
                    if (string.Equals(family.Name, preferred, StringComparison.OrdinalIgnoreCase))
                    {
                        return preferred;
                    }
                }
            }
            catch
            {
                // enumeration can throw on locked-down machines — just take the fallback
            }

            return fallback;
        }

        // --- Paint primitives ------------------------------------------------------------

        public static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            float diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
            var path = new GraphicsPath();
            if (diameter <= 0f)
            {
                path.AddRectangle(rect);
                return path;
            }

            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRoundedRect(Graphics g, RectangleF rect, float radius, Color fill)
        {
            using GraphicsPath path = RoundedRect(rect, radius);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        public static void DrawRoundedBorder(Graphics g, RectangleF rect, float radius, Color color, float width = 1f)
        {
            // Inset by half the pen width so the stroke stays inside the rect and reads crisp.
            rect.Inflate(-width / 2f, -width / 2f);
            using GraphicsPath path = RoundedRect(rect, radius);
            using var pen = new Pen(color, width);
            g.DrawPath(pen, path);
        }

        /// <summary>A status chip: state colour text on a faint tint of the same colour.</summary>
        public static void DrawChip(Graphics g, RectangleF rect, string text, Color color, Font font)
        {
            FillRoundedRect(g, rect, 4f, Color.FromArgb(31, color));
            TextRenderer.DrawText(
                g, text, font, Rectangle.Round(rect), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        /// <summary>
        /// The warming progress bar: a track, a filled portion in the warming colour, and the
        /// diagonal stripe overlay that drifts left-to-right. <paramref name="frame"/> is a
        /// monotonically increasing animation counter; motion stops when it stops advancing.
        /// </summary>
        public static void DrawStripedBar(Graphics g, RectangleF rect, float fraction, int frame,
            Color fill, Color track)
        {
            fraction = Math.Max(0f, Math.Min(1f, fraction));
            FillRoundedRect(g, rect, rect.Height / 2f, track);

            float fillWidth = rect.Width * fraction;
            if (fillWidth < 1f)
            {
                return;
            }

            var fillRect = new RectangleF(rect.X, rect.Y, fillWidth, rect.Height);
            using GraphicsPath clip = RoundedRect(rect, rect.Height / 2f);
            GraphicsState saved = g.Save();
            g.SetClip(clip);
            g.IntersectClip(fillRect);

            using (var brush = new SolidBrush(fill))
            {
                g.FillRectangle(brush, fillRect);
            }

            // Translucent diagonal bands, offset by the frame so they appear to travel.
            const int period = 14;
            float offset = frame % period;
            using (var stripe = new SolidBrush(Color.FromArgb(56, Color.White)))
            {
                for (float x = rect.X - rect.Height - period + offset; x < rect.Right; x += period)
                {
                    PointF[] band =
                    {
                        new PointF(x, rect.Bottom),
                        new PointF(x + rect.Height, rect.Y),
                        new PointF(x + rect.Height + period / 2f, rect.Y),
                        new PointF(x + period / 2f, rect.Bottom),
                    };
                    g.FillPolygon(stripe, band);
                }
            }

            g.Restore(saved);
        }

        /// <summary>The Apron mark: a rounded tile with a tilted "runway" bar through it.</summary>
        public static void DrawLogo(Graphics g, RectangleF rect, Color tile, Color bar, Color barBorder)
        {
            SmoothingMode previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float radius = rect.Height * 0.26f;
            FillRoundedRect(g, rect, radius, tile);
            DrawRoundedBorder(g, rect, radius, barBorder);

            GraphicsState saved = g.Save();
            using (GraphicsPath clip = RoundedRect(rect, radius))
            {
                g.SetClip(clip);
                float barWidth = rect.Width * 0.2f;
                var barRect = new RectangleF(-barWidth / 2f, rect.Height * 0.1f, barWidth, rect.Height * 0.8f);
                g.TranslateTransform(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                g.RotateTransform(28f);
                g.TranslateTransform(0, -rect.Height / 2f);
                using var brush = new SolidBrush(bar);
                using GraphicsPath barPath = RoundedRect(barRect, barWidth / 2f);
                g.FillPath(brush, barPath);
            }

            g.Restore(saved);
            g.SmoothingMode = previous;
        }

        /// <summary>A filled circle — used for the connection status dot.</summary>
        public static void DrawDot(Graphics g, float centerX, float centerY, float radius, Color color)
        {
            SmoothingMode previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, centerX - radius, centerY - radius, radius * 2f, radius * 2f);
            g.SmoothingMode = previous;
        }

        private static Color FromHex(string hex)
        {
            hex = hex.TrimStart('#');
            int red = Convert.ToInt32(hex.Substring(0, 2), 16);
            int green = Convert.ToInt32(hex.Substring(2, 2), 16);
            int blue = Convert.ToInt32(hex.Substring(4, 2), 16);
            return Color.FromArgb(red, green, blue);
        }
    }
}
