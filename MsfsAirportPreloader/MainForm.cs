using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MsfsAirportPreloader
{
    /// <summary>A flicker-free panel for the custom-painted, animating strips.</summary>
    internal sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    /// <summary>Owner-drawn, double-buffered list — the instrument table's rows.</summary>
    internal sealed class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;
            OwnerDraw = true;
        }
    }

    /// <summary>
    /// The RAMP main window: a dark instrument table. A custom toolbar carries the mark and the
    /// live connection status; a stats strip shows the RAM cache meter and per-state counters; the
    /// owner-drawn list gives every airport a coloured state rail, a chip or an animated warming
    /// bar; a footer summarises the index. The log lives in a panel toggled from the toolbar.
    /// </summary>
    internal sealed class MainForm : Form
    {
        // Column layout for the instrument table (px). Package flexes to fill the remainder.
        private const int ColumnIcao = 74;
        private const int ColumnDistance = 96;
        private const int ColumnWarmed = 104;
        private const int ColumnState = 150;
        private const int RowHeight = 46;

        private readonly Engine _engine;
        private readonly string _iniPath;

        private readonly BufferedPanel _toolbar = new BufferedPanel();
        private readonly BufferedPanel _statsStrip = new BufferedPanel();
        private readonly BufferedPanel _statusBar = new BufferedPanel();
        private readonly BufferedPanel _logPanel = new BufferedPanel();
        private readonly BufferedListView _list = new BufferedListView();
        private readonly TextBox _logBox = new TextBox();
        private Label _logHeader;

        // Full-area takeover (indexing / error / empty) and the inline notice strip.
        private readonly BufferedPanel _overlay = new BufferedPanel();
        private readonly BufferedPanel _banner = new BufferedPanel();
        private RoundedButton _rescanButton;
        private RoundedButton _errorPrimary;
        private RoundedButton _bannerAction;
        private OverlayMode _overlayMode = OverlayMode.None;
        private BannerMode _bannerMode = BannerMode.None;
        private bool _dimList;

        private enum OverlayMode { None, Indexing, Error, Empty }
        private enum BannerMode { None, Menu, Budget }

        private readonly Timer _refreshTimer = new Timer();
        private readonly Timer _animTimer = new Timer();
        private readonly NotifyIcon _tray = new NotifyIcon();

        private bool _reallyExit;
        private int _animFrame;

        // Painted values, refreshed from the engine snapshot each tick.
        private long _cacheUsedBytes;
        private long _cacheBudgetBytes;
        private long _loadedBytes;
        private long _warmingBytes;
        private int _countLoaded;
        private int _countWarming;
        private int _countQueued;
        private int _countSkipped;
        private int _trackedCount;
        private bool _anyWarming;

        // Toolbar glyph: grey when idle, else the state of the most recently added airport.
        private Color _glyphColor = Theme.Unloaded;
        private readonly System.Collections.Generic.HashSet<string> _seenAirportKeys =
            new System.Collections.Generic.HashSet<string>();
        private string _latestAirportKey;

        private Icon _trayIcon;
        private Color _trayColor = Color.Empty;
        private ToolStripLabel _trayHeaderStatus;
        private ToolStripMenuItem _trayPauseItem;
        private bool _balloonedThisFlight;

        private Icon _appIcon;
        private CaptionButton _minButton;
        private CaptionButton _maxButton;
        private CaptionButton _closeButton;

        public MainForm(Engine engine, string iniPath)
        {
            _engine = engine;
            _iniPath = iniPath;

            BuildLayout();
            BuildTray();

            _refreshTimer.Interval = 750;
            _refreshTimer.Tick += (_, __) => Refresh_();
            _refreshTimer.Start();

            // A separate, faster clock only drives the stripe drift and meter pulse, and only
            // repaints while something is warming — "the only continuous motion in the app".
            _animTimer.Interval = 66;
            _animTimer.Tick += (_, __) =>
            {
                // Runs the warming stripe/meter pulse and the indexing spinner.
                if (!_anyWarming && _overlayMode != OverlayMode.Indexing)
                {
                    return;
                }

                _animFrame++;
                if (_overlayMode == OverlayMode.Indexing)
                {
                    _overlay.Invalidate();
                }
                else
                {
                    _list.Invalidate();
                    _statsStrip.Invalidate();
                }
            };
            _animTimer.Start();
        }

        private void BuildLayout()
        {
            Text = "RAMP";
            Width = 900;
            Height = 620;
            MinimumSize = new Size(720, 460);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = Theme.Sans(9f);

            // Custom dark chrome: no OS title bar; the toolbar is the caption. CreateParams keeps the
            // native resize frame + snap + shadow (see CreateParams / WndProc below).
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = true;
            MinimizeBox = true;
            _appIcon = MakeAppIcon();
            Icon = _appIcon;

            BuildToolbar();
            BuildStatsStrip();
            BuildStatusBar();
            BuildLogPanel();
            BuildList();
            BuildBanner();
            BuildOverlay();

            // Docked controls claim space in reverse z-order, so the Fill controls (list, then the
            // overlay on top of it) go FIRST, then each edge band from innermost to outermost.
            Controls.Add(_list);
            Controls.Add(_overlay);    // Fill, above the list; shown only for takeovers
            Controls.Add(_logPanel);   // Bottom, above the footer, hidden by default
            Controls.Add(_statusBar);  // Bottom, outermost
            Controls.Add(_statsStrip); // Top, innermost (just above the list)
            Controls.Add(_banner);     // Top, above the cache/stats strip
            Controls.Add(_toolbar);    // Top, outermost

            ApplyTheme();
            UpdateStateChrome(listEmpty: true); // show the indexing takeover from the first frame
        }

        /// <summary>Re-apply the active theme's colours to every owned control, then repaint.</summary>
        private void ApplyTheme()
        {
            BackColor = Theme.Window;
            ForeColor = Theme.Text;

            _toolbar.BackColor = Theme.Raised;
            foreach (Control control in _toolbar.Controls)
            {
                if (control is RoundedButton button)
                {
                    button.BackColor = Theme.RaisedHover;
                    button.ForeColor = Theme.TextMuted;
                    button.BorderColor = Theme.InputBorder;
                    button.HoverColor = Theme.Border;
                }
            }

            _statsStrip.BackColor = Theme.Window;
            _statusBar.BackColor = Theme.Sunken;
            _list.BackColor = Theme.Window;
            _list.ForeColor = Theme.Text;

            _logPanel.BackColor = Theme.Sunken;
            _logBox.BackColor = Theme.Sunken;
            _logBox.ForeColor = Theme.TextMuted;
            if (_logHeader != null)
            {
                _logHeader.BackColor = Theme.Sunken;
                _logHeader.ForeColor = Theme.TextFaint;
            }

            _overlay.BackColor = Theme.Window;
            _banner.BackColor = Theme.Window;

            Invalidate(true);
            _toolbar.Invalidate();
            _statsStrip.Invalidate();
            _statusBar.Invalidate();
            _logPanel.Invalidate();
            _list.Invalidate();
            _banner.Invalidate();
            _overlay.Invalidate();
        }

        // --- State chrome: takeover overlay + inline banner --------------------------------

        private void BuildBanner()
        {
            _banner.Dock = DockStyle.Top;
            _banner.Height = 68;
            _banner.Visible = false;
            _banner.BackColor = Theme.Window;
            _banner.Paint += PaintBanner;

            _bannerAction = new RoundedButton
            {
                Font = Theme.Sans(8.5f, FontStyle.Bold),
                Height = 28,
                Width = 108,
                TabStop = false,
                Visible = false,
            };
            _bannerAction.Click += (_, __) => RaiseBudget();
            _banner.Controls.Add(_bannerAction);
            _banner.Resize += (_, __) => LayoutBanner();
        }

        private void LayoutBanner()
        {
            _bannerAction.Left = _banner.Width - _bannerAction.Width - 14;
            _bannerAction.Top = (_banner.Height - _bannerAction.Height) / 2;
        }

        private void BuildOverlay()
        {
            _overlay.Dock = DockStyle.Fill;
            _overlay.Visible = false;
            _overlay.BackColor = Theme.Window;
            _overlay.Paint += PaintOverlay;

            _errorPrimary = new RoundedButton
            {
                Text = "Open Settings",
                Font = Theme.Sans(9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0x1A, 0x12, 0x04),
                BackColor = Theme.Skipped,
                BorderColor = Color.Empty,
                Height = 36,
                Width = 168,
                TabStop = false,
                Visible = false,
            };
            _errorPrimary.Click += (_, __) => OpenSettings();

            _overlay.Controls.Add(_errorPrimary);
            _overlay.Resize += (_, __) => LayoutOverlay();
        }

        private void LayoutOverlay()
        {
            if (_overlayMode != OverlayMode.Error)
            {
                return;
            }

            int centerX = _overlay.Width / 2;
            int y = _overlay.Height / 2 + 52;
            _errorPrimary.SetBounds(centerX - _errorPrimary.Width / 2, y, _errorPrimary.Width, _errorPrimary.Height);
        }

        private void UpdateStateChrome(bool listEmpty)
        {
            IndexPhase phase = _engine.Phase;
            OverlayMode overlay;
            var banner = BannerMode.None;
            bool dim = false;
            bool statsVisible = true;

            if (phase == IndexPhase.Building)
            {
                overlay = OverlayMode.Indexing;
                statsVisible = false;
            }
            else if (phase == IndexPhase.Error)
            {
                overlay = OverlayMode.Error;
                statsVisible = false;
            }
            else
            {
                bool inFlight = _engine.IsSimRunning;
                bool inMenu = _engine.IsSimConnected && !inFlight;
                dim = inMenu;
                if (inMenu)
                {
                    banner = BannerMode.Menu;
                }
                else if (inFlight && _countSkipped > 0)
                {
                    banner = BannerMode.Budget;
                }

                // In the menu the dimmed list carries the state; otherwise an empty list means
                // there's nothing in range (or the sim is closed) — say so.
                overlay = listEmpty && !inMenu ? OverlayMode.Empty : OverlayMode.None;
            }

            _rescanButton.Enabled = phase != IndexPhase.Building;
            if (_statsStrip.Visible != statsVisible)
            {
                _statsStrip.Visible = statsVisible;
            }

            SetBannerMode(banner);
            SetOverlayMode(overlay);

            if (_dimList != dim)
            {
                _dimList = dim;
                _list.Invalidate();
            }
        }

        private void SetOverlayMode(OverlayMode mode)
        {
            bool changed = _overlayMode != mode;
            _overlayMode = mode;

            if (mode == OverlayMode.Error)
            {
                _errorPrimary.Visible = true;
                LayoutOverlay();
            }
            else
            {
                _errorPrimary.Visible = false;
            }

            _overlay.Visible = mode != OverlayMode.None;
            if (_overlay.Visible)
            {
                _overlay.BringToFront();
            }

            if (changed || _overlay.Visible)
            {
                _overlay.Invalidate();
            }
        }

        private void SetBannerMode(BannerMode mode)
        {
            _bannerMode = mode;
            _bannerAction.Visible = mode == BannerMode.Budget;
            if (mode == BannerMode.Budget)
            {
                long nextGb = _engine.CurrentConfig.RamBudgetMegabytes / 1024 + 2;
                _bannerAction.Text = $"Raise to {nextGb} GB";
                _bannerAction.ForeColor = Color.FromArgb(0x1A, 0x12, 0x04);
                _bannerAction.BackColor = Theme.Skipped;
                _bannerAction.BorderColor = Color.Empty;
                LayoutBanner();
            }

            bool show = mode != BannerMode.None;
            if (_banner.Visible != show)
            {
                _banner.Visible = show;
            }

            if (show)
            {
                _banner.Invalidate();
            }
        }

        private void PaintBanner(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color accent = _bannerMode == BannerMode.Budget ? Theme.Skipped : Theme.Queued;

            g.Clear(Theme.Window);
            var card = new RectangleF(14, 8, _banner.Width - 28, _banner.Height - 16);
            Theme.FillRoundedRect(g, card, 7f, Color.FromArgb(20, accent));
            Theme.DrawRoundedBorder(g, card, 7f, Color.FromArgb(90, accent));

            // Square status dot, vertically centred in the card.
            float dotSize = 9f;
            float dotX = card.X + 14;
            using (var dot = new SolidBrush(accent))
            {
                g.FillRectangle(dot, dotX, card.Y + (card.Height - dotSize) / 2f, dotSize, dotSize);
            }

            string title, detail;
            if (_bannerMode == BannerMode.Budget)
            {
                title = $"Budget reached — {_countSkipped} skipped";
                detail = "Destination is loaded; skipped fields are alternates.";
            }
            else
            {
                title = "Warming paused — you're in the menu";
                detail = "Reading now would slow the sim's load. RAMP resumes once you're airborne.";
            }

            // Right edge of the text column: clear of the HOLDING chip / Raise action.
            int textLeft = (int)(dotX + dotSize + 12);
            int rightReserve = _bannerMode == BannerMode.Menu ? 100 : (_bannerAction.Width + 24);
            int textWidth = (int)card.Right - rightReserve - textLeft;

            // Measure the two lines and centre the block, so top and bottom padding match.
            Font titleFont = Theme.Sans(9.5f, FontStyle.Bold);
            Font detailFont = Theme.Sans(8.5f);
            int titleHeight = TextRenderer.MeasureText(g, title, titleFont, Size.Empty, TextFormatFlags.NoPadding).Height;
            int detailHeight = TextRenderer.MeasureText(g, detail, detailFont, Size.Empty, TextFormatFlags.NoPadding).Height;
            const int lineGap = 3;
            int blockTop = (int)(card.Y + (card.Height - (titleHeight + lineGap + detailHeight)) / 2f);

            var titleRect = new Rectangle(textLeft, blockTop, textWidth, titleHeight);
            var detailRect = new Rectangle(textLeft, blockTop + titleHeight + lineGap, textWidth, detailHeight);
            TextRenderer.DrawText(g, title, titleFont, titleRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, detail, detailFont, detailRect, Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            if (_bannerMode == BannerMode.Menu)
            {
                var chip = new RectangleF(card.Right - 96, card.Y + (card.Height - 22) / 2f, 82, 22);
                Theme.DrawChip(g, chip, "HOLDING", accent, Theme.Mono(8f, FontStyle.Bold));
            }
        }

        private void PaintOverlay(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = _overlay.Width / 2;
            int cy = _overlay.Height / 2;

            if (_overlayMode == OverlayMode.Indexing)
            {
                DrawSpinner(g, cx, cy - 96, 30, Theme.Skipped);
                DrawCentered(g, "Indexing your add-on airports", Theme.Sans(15f, FontStyle.Bold), Theme.Text, cx, cy - 44);
                string root = string.IsNullOrEmpty(_engine.ScanRootPath) ? "your MSFS package folder" : _engine.ScanRootPath;
                DrawCentered(g, $"One-time scan of {root}.", Theme.Sans(10f), Theme.TextMuted, cx, cy - 18);
                DrawCentered(g, "RAMP starts warming the moment you're airborne.", Theme.Sans(10f), Theme.TextMuted, cx, cy);
                // Indeterminate bar.
                var track = new RectangleF(cx - 220, cy + 38, 440, 8);
                Theme.FillRoundedRect(g, track, 4f, Theme.InputBg);
                Theme.DrawRoundedBorder(g, track, 4f, Theme.Border);
                float seg = 150, span = track.Width + seg;
                float pos = (_animFrame * 7f) % span - seg;
                var lit = RectangleF.Intersect(track, new RectangleF(track.X + pos, track.Y, seg, 8));
                if (lit.Width > 0)
                {
                    Theme.FillRoundedRect(g, lit, 4f, Theme.Skipped);
                }
            }
            else if (_overlayMode == OverlayMode.Error)
            {
                // Warning glyph.
                var glyph = new RectangleF(cx - 28, cy - 150, 56, 56);
                Theme.DrawRoundedBorder(g, glyph, 12f, Color.FromArgb(128, Theme.Skipped), 2f);
                using (var bar = new SolidBrush(Theme.Skipped))
                {
                    g.FillRectangle(bar, cx - 2.5f, cy - 140, 5, 26);
                }
                DrawCentered(g, _engine.ErrorTitle ?? "Something needs attention", Theme.Sans(15f, FontStyle.Bold), Theme.Text, cx, cy - 78);
                foreach ((string line, int i) in WrapLines(_engine.ErrorDetail ?? "", 64))
                {
                    DrawCentered(g, line, Theme.Sans(10f), Theme.TextMuted, cx, cy - 50 + i * 20);
                }

                if (!string.IsNullOrEmpty(_engine.ErrorPath))
                {
                    var box = new RectangleF(cx - 260, cy + 4, 520, 34);
                    Theme.FillRoundedRect(g, box, 6f, Theme.Sunken);
                    Theme.DrawRoundedBorder(g, box, 6f, Theme.Border);
                    TextRenderer.DrawText(g, "EXPECTED", Theme.Mono(7.5f), new Rectangle((int)box.X + 12, (int)box.Y, 70, 34),
                        Theme.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, _engine.ErrorPath, Theme.Mono(9f), new Rectangle((int)box.X + 82, (int)box.Y, (int)box.Width - 94, 34),
                        Theme.Skipped, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
            }
            else if (_overlayMode == OverlayMode.Empty)
            {
                bool connected = _engine.IsSimConnected;
                using (var pen = new Pen(Theme.InputBorder) { DashStyle = DashStyle.Dash })
                {
                    g.DrawEllipse(pen, cx - 18, cy - 44, 36, 36);
                }
                Theme.DrawDot(g, cx, cy - 26, 3, Color.FromArgb(0x3E, 0x51, 0x63));
                string head = connected ? "NO ADD-ON AIRPORTS IN RANGE" : "WAITING FOR MSFS";
                DrawCentered(g, head, Theme.Mono(10.5f, FontStyle.Regular), Theme.TextFaint, cx, cy + 2);
                string sub = connected
                    ? $"Nothing within {_engine.CurrentConfig.OuterRadiusNauticalMiles:0} NM yet — RAMP will pick it up automatically."
                    : "RAMP starts warming once the sim is running and you're airborne.";
                DrawCentered(g, sub, Theme.Sans(10f), Theme.TextDim, cx, cy + 24);
            }
        }

        private static void DrawCentered(Graphics g, string text, Font font, Color color, int cx, int y)
        {
            Size size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, text, font, new Point(cx - size.Width / 2, y), color, TextFormatFlags.NoPadding);
        }

        private void DrawSpinner(Graphics g, int cx, int cy, int radius, Color color)
        {
            var rect = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
            using (var track = new Pen(Color.FromArgb(0x1D, 0x2A, 0x36), 3f))
            {
                g.DrawEllipse(track, rect);
            }

            using var pen = new Pen(color, 3f);
            g.DrawArc(pen, rect, (_animFrame * 9) % 360, 90);
        }

        private static System.Collections.Generic.IEnumerable<(string, int)> WrapLines(string text, int width)
        {
            var words = text.Split(' ');
            var line = new System.Text.StringBuilder();
            int index = 0;
            foreach (string word in words)
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    yield return (line.ToString(), index++);
                    line.Clear();
                }

                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }

            if (line.Length > 0)
            {
                yield return (line.ToString(), index);
            }
        }

        private void RaiseBudget()
        {
            Config updated = _engine.CurrentConfig.Clone();
            updated.RamBudgetMegabytes += 2048;
            updated.Save(_iniPath);
            _engine.ApplyConfig(updated, rescanPackages: false);
            _cacheBudgetBytes = updated.RamBudgetMegabytes * 1024L * 1024L;
            _statsStrip.Invalidate();
        }

        private static Color Blend(Color a, Color b, float t)
            => Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));

        // --- Toolbar -----------------------------------------------------------------------

        private void BuildToolbar()
        {
            _toolbar.Dock = DockStyle.Top;
            _toolbar.Height = 44;
            _toolbar.BackColor = Theme.Raised;
            _toolbar.Paint += PaintToolbar;

            int left = 132; // clears the logo tile and the RAMP wordmark painted behind
            RoundedButton rescan = MakeToolButton("Rescan", ref left);
            _rescanButton = rescan;
            rescan.Click += (_, __) => _engine.ApplyConfig(_engine.CurrentConfig, rescanPackages: true);
            RoundedButton log = MakeToolButton("Log", ref left);
            log.Click += (_, __) => ToggleLog();
            RoundedButton settings = MakeToolButton("Settings", ref left);
            settings.Click += (_, __) => OpenSettings();
            RoundedButton about = MakeToolButton("About", ref left);
            about.Click += (_, __) => OpenAbout();

            _toolbar.Controls.Add(rescan);
            _toolbar.Controls.Add(log);
            _toolbar.Controls.Add(settings);
            _toolbar.Controls.Add(about);

            // Caption buttons (min / max / close) on the right — the toolbar is the title bar.
            _minButton = new CaptionButton(CaptionButton.Glyph.Minimize);
            _minButton.Click += (_, __) => WindowState = FormWindowState.Minimized;
            _maxButton = new CaptionButton(CaptionButton.Glyph.Maximize);
            _maxButton.Click += (_, __) => ToggleMaximize();
            _closeButton = new CaptionButton(CaptionButton.Glyph.Close);
            _closeButton.Click += (_, __) => Close();
            _toolbar.Controls.Add(_minButton);
            _toolbar.Controls.Add(_maxButton);
            _toolbar.Controls.Add(_closeButton);
            LayoutCaptionButtons();

            // Drag / double-click-maximize from the empty toolbar area (child controls are exempt).
            _toolbar.Resize += (_, __) => LayoutCaptionButtons();
            _toolbar.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                }
            };
            _toolbar.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ToggleMaximize();
                }
            };
        }

        private const int CaptionButtonWidth = 46;

        private void LayoutCaptionButtons()
        {
            if (_closeButton == null)
            {
                return;
            }

            int h = _toolbar.Height - 1; // sit above the bottom hairline
            _closeButton.SetBounds(_toolbar.Width - CaptionButtonWidth, 0, CaptionButtonWidth, h);
            _maxButton.SetBounds(_toolbar.Width - CaptionButtonWidth * 2, 0, CaptionButtonWidth, h);
            _minButton.SetBounds(_toolbar.Width - CaptionButtonWidth * 3, 0, CaptionButtonWidth, h);
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
            _maxButton.Kind = WindowState == FormWindowState.Maximized
                ? CaptionButton.Glyph.Restore
                : CaptionButton.Glyph.Maximize;
            _maxButton.Invalidate();
        }

        private RoundedButton MakeToolButton(string text, ref int left)
        {
            var button = new RoundedButton
            {
                Text = text,
                Font = Theme.Sans(9f),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.RaisedHover,
                BorderColor = Theme.InputBorder,
                HoverColor = Theme.Border,
                Height = 28,
                Width = 78,
                Top = 8,
                Left = left,
                TabStop = false,
            };
            left += button.Width + 6;
            return button;
        }

        private void PaintToolbar(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Raised);

            // Bottom hairline.
            using (var pen = new Pen(Theme.Border))
            {
                g.DrawLine(pen, 0, _toolbar.Height - 1, _toolbar.Width, _toolbar.Height - 1);
            }

            // Logo tile + wordmark. The bar takes the current glyph state colour (grey when idle).
            Theme.DrawLogo(g, new RectangleF(14, 12, 20, 20), Theme.RaisedHover, _glyphColor, Theme.Border);
            TextRenderer.DrawText(g, "RAMP", Theme.Mono(9.5f, FontStyle.Bold),
                new Point(44, 15), Theme.Text, TextFormatFlags.NoPadding);

            // Right side: connection dot + status, then flight state — kept clear of the caption buttons.
            (Color dotColor, string status, Color statusColor, string flight) = ConnectionStatus();
            int right = _toolbar.Width - CaptionButtonWidth * 3 - 16;

            Font flightFont = Theme.Mono(8.5f);
            Size flightSize = TextRenderer.MeasureText(flight, flightFont);
            right -= flightSize.Width;
            TextRenderer.DrawText(g, flight, flightFont, new Point(right, 15), Theme.TextDim, TextFormatFlags.NoPadding);
            right -= 18;

            Font statusFont = Theme.Mono(8.5f);
            Size statusSize = TextRenderer.MeasureText(status, statusFont);
            right -= statusSize.Width;
            TextRenderer.DrawText(g, status, statusFont, new Point(right, 15), statusColor, TextFormatFlags.NoPadding);
            right -= 14;
            Theme.DrawDot(g, right, _toolbar.Height / 2f, 3.5f, dotColor);
        }

        private (Color dot, string status, Color statusColor, string flight) ConnectionStatus()
        {
            if (!_engine.IsSimConnected)
            {
                return (Theme.Unloaded, "OFFLINE", Theme.TextDim, "WAITING FOR MSFS");
            }

            return _engine.IsSimRunning
                ? (Theme.Loaded, "CONNECTED", Theme.Loaded, "IN FLIGHT")
                : (Theme.Skipped, "CONNECTED", Theme.TextMuted, "IN MENU");
        }

        // --- Stats strip (cache meter + counters) ------------------------------------------

        private void BuildStatsStrip()
        {
            _statsStrip.Dock = DockStyle.Top;
            _statsStrip.Height = 66;
            _statsStrip.BackColor = Theme.Window;
            _statsStrip.Paint += PaintStatsStrip;
        }

        private void PaintStatsStrip(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var pen = new Pen(Theme.BorderSoft))
            {
                g.DrawLine(pen, 0, _statsStrip.Height - 1, _statsStrip.Width, _statsStrip.Height - 1);
            }

            const int countersWidth = 300;
            int meterLeft = 16;
            int meterRight = _statsStrip.Width - countersWidth - 24;
            if (meterRight < meterLeft + 120)
            {
                meterRight = meterLeft + 120;
            }

            PaintCacheMeter(g, meterLeft, meterRight);
            PaintCounters(g, _statsStrip.Width - countersWidth - 8, countersWidth);

            // Divider between meter and counters.
            using (var pen = new Pen(Theme.BorderSoft))
            {
                int dividerX = _statsStrip.Width - countersWidth - 16;
                g.DrawLine(pen, dividerX, 16, dividerX, _statsStrip.Height - 16);
            }
        }

        private void PaintCacheMeter(Graphics g, int left, int right)
        {
            float usedGb = _cacheUsedBytes / (1024f * 1024f * 1024f);
            float budgetGb = _cacheBudgetBytes / (1024f * 1024f * 1024f);

            TextRenderer.DrawText(g, "CACHE", Theme.Mono(8f), new Point(left, 14), Theme.TextDim, TextFormatFlags.NoPadding);
            int valueLeft = left + TextRenderer.MeasureText("CACHE", Theme.Mono(8f)).Width + 8;
            string used = usedGb.ToString("0.00");
            TextRenderer.DrawText(g, used, Theme.Mono(11.5f, FontStyle.Bold), new Point(valueLeft, 11), Theme.Text, TextFormatFlags.NoPadding);
            valueLeft += TextRenderer.MeasureText(used, Theme.Mono(11.5f, FontStyle.Bold)).Width + 4;
            TextRenderer.DrawText(g, $"/ {budgetGb:0.00} GB", Theme.Mono(8.5f), new Point(valueLeft, 14), Theme.TextDim, TextFormatFlags.NoPadding);

            var bar = new RectangleF(left, 38, right - left, 8);
            Theme.FillRoundedRect(g, bar, 4f, Theme.InputBg);
            Theme.DrawRoundedBorder(g, bar, 4f, Theme.Border);

            if (_cacheBudgetBytes <= 0)
            {
                return;
            }

            float loadedFraction = Clamp01(_loadedBytes / (float)_cacheBudgetBytes);
            float warmingFraction = Clamp01(_warmingBytes / (float)_cacheBudgetBytes);

            GraphicsState saved = g.Save();
            using (GraphicsPath clip = Theme.RoundedRect(bar, 4f))
            {
                g.SetClip(clip);
                float x = bar.X;
                float loadedWidth = bar.Width * loadedFraction;
                using (var brush = new SolidBrush(Theme.Loaded))
                {
                    g.FillRectangle(brush, x, bar.Y, loadedWidth, bar.Height);
                }

                x += loadedWidth;
                // The warming segment pulses; the pulse only advances while _animFrame does.
                int alpha = 150 + (int)(90 * (Math.Sin(_animFrame * 0.14) * 0.5 + 0.5));
                using (var brush = new SolidBrush(Color.FromArgb(alpha, Theme.Warming)))
                {
                    g.FillRectangle(brush, x, bar.Y, bar.Width * warmingFraction, bar.Height);
                }
            }

            g.Restore(saved);
        }

        private void PaintCounters(Graphics g, int left, int width)
        {
            (string label, int value, Color color)[] counters =
            {
                ("LOADED", _countLoaded, Theme.Loaded),
                ("WARMING", _countWarming, Theme.Warming),
                ("QUEUED", _countQueued, Theme.Queued),
                ("SKIPPED", _countSkipped, Theme.Skipped),
            };

            int cellWidth = width / counters.Length;
            Font numberFont = Theme.Mono(15f, FontStyle.Bold);
            Font labelFont = Theme.Mono(7.5f);

            for (int i = 0; i < counters.Length; i++)
            {
                var cell = new Rectangle(left + i * cellWidth, 12, cellWidth, _statsStrip.Height - 20);
                var numberRect = new Rectangle(cell.X, cell.Y, cell.Width, 26);
                TextRenderer.DrawText(g, counters[i].value.ToString(), numberFont, numberRect, counters[i].color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
                var labelRect = new Rectangle(cell.X, cell.Y + 28, cell.Width, 14);
                TextRenderer.DrawText(g, counters[i].label, labelFont, labelRect, Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            }
        }

        // --- Instrument table (list) -------------------------------------------------------

        private void BuildList()
        {
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = false;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.HideSelection = true;
            _list.BackColor = Theme.Window;
            _list.ForeColor = Theme.Text;
            _list.BorderStyle = BorderStyle.None;

            // A blank image list is the reliable way to force a taller row in Details view.
            _list.SmallImageList = new ImageList { ImageSize = new Size(1, RowHeight) };

            _list.Columns.Add("ICAO", ColumnIcao);
            _list.Columns.Add("PACKAGE", 320);
            _list.Columns.Add("DIST NM", ColumnDistance, HorizontalAlignment.Right);
            _list.Columns.Add("WARMED", ColumnWarmed, HorizontalAlignment.Right);
            _list.Columns.Add("STATE", ColumnState, HorizontalAlignment.Right);

            _list.DrawColumnHeader += DrawHeader;
            _list.DrawItem += DrawRow;
            _list.DrawSubItem += (_, __) => { }; // all drawing happens in DrawItem
            _list.ClientSizeChanged += (_, __) => ResizePackageColumn();
        }

        private void ResizePackageColumn()
        {
            int used = ColumnIcao + ColumnDistance + ColumnWarmed + ColumnState;
            int packageWidth = _list.ClientSize.Width - used;
            _list.Columns[1].Width = Math.Max(160, packageWidth);
        }

        private void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var brush = new SolidBrush(Theme.Sunken))
            {
                g.FillRectangle(brush, e.Bounds);
            }

            using (var pen = new Pen(Theme.BorderSoft))
            {
                g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            }

            bool rightAlign = e.ColumnIndex >= 2;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                        (rightAlign ? TextFormatFlags.Right : TextFormatFlags.Left);
            var textRect = Rectangle.Inflate(e.Bounds, -14, 0);
            TextRenderer.DrawText(g, e.Header.Text, Theme.Mono(7.5f), textRect, Theme.TextFaint, flags);
        }

        private void DrawRow(object sender, DrawListViewItemEventArgs e)
        {
            if (!(e.Item.Tag is AirportState state))
            {
                return;
            }

            Graphics g = e.Graphics;
            // Use the full-row geometry rather than e.Bounds, which spans only the first column,
            // and widen the clip to the whole row so later columns aren't cut off.
            Rectangle row = e.Item.GetBounds(ItemBoundsPortion.Entire);
            g.SetClip(row);
            bool warming = state.State == PrefetchState.Warming;

            using (var brush = new SolidBrush(Theme.Window))
            {
                g.FillRectangle(brush, row);
            }

            if (warming)
            {
                using var tint = new SolidBrush(Color.FromArgb(14, Theme.Warming));
                g.FillRectangle(tint, row);
            }

            // Row separator.
            using (var pen = new Pen(Theme.BorderSoft))
            {
                g.DrawLine(pen, row.Left, row.Bottom - 1, row.Right, row.Bottom - 1);
            }

            // 3 px state rail.
            Color railColor = Theme.StateColor(state.State);
            if (_dimList) railColor = Blend(railColor, Theme.Window, 0.55f);
            using (var brush = new SolidBrush(railColor))
            {
                g.FillRectangle(brush, row.Left, row.Top, 3, row.Height);
            }

            bool dim = state.State == PrefetchState.OutOfRange;
            Color icaoColor = dim ? Color.FromArgb(0x6B, 0x81, 0x94) : Theme.Text;
            Color nameColor = dim ? Color.FromArgb(0x5F, 0x77, 0x8A) : Color.FromArgb(0xA9, 0xBE, 0xCE);
            Color warmedColor = state.State == PrefetchState.Loaded ? Theme.Text : Theme.TextDim;
            if (_dimList)
            {
                // In the menu the whole list recedes (~45%) — it stays visible but clearly idle.
                icaoColor = Blend(icaoColor, Theme.Window, 0.5f);
                nameColor = Blend(nameColor, Theme.Window, 0.5f);
                warmedColor = Blend(warmedColor, Theme.Window, 0.5f);
            }

            int x = row.Left;
            var icaoRect = new Rectangle(x + 16, row.Top, ColumnIcao - 16, row.Height);
            TextRenderer.DrawText(g, state.Icao, Theme.Mono(13f, FontStyle.Bold), icaoRect, icaoColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += ColumnIcao;

            int packageWidth = _list.Columns[1].Width;
            var packageRect = new Rectangle(x, row.Top, packageWidth - 16, row.Height);
            TextRenderer.DrawText(g, state.PackageName, Theme.Sans(10f), packageRect, nameColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            x += packageWidth;

            var distRect = new Rectangle(x, row.Top, ColumnDistance - 12, row.Height);
            TextRenderer.DrawText(g, state.DistanceNauticalMiles.ToString("0.0"), Theme.Mono(11f), distRect, nameColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += ColumnDistance;

            var warmedRect = new Rectangle(x, row.Top, ColumnWarmed - 12, row.Height);
            string warmed = state.WarmedBytes > 0 ? (state.WarmedBytes / (1024 * 1024)).ToString("#,0") : "—";
            TextRenderer.DrawText(g, warmed, Theme.Mono(11f), warmedRect, warmedColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += ColumnWarmed;

            var stateRect = new Rectangle(x, row.Top, ColumnState - 12, row.Height);
            if (warming)
            {
                DrawWarmingCell(g, stateRect, state);
            }
            else
            {
                DrawStateChip(g, stateRect, state.State);
            }
        }

        private void DrawWarmingCell(Graphics g, Rectangle cell, AirportState state)
        {
            float fraction = state.TotalBytes > 0 ? state.WarmedBytes / (float)state.TotalBytes : 0f;
            int percent = (int)Math.Round(Clamp01(fraction) * 100f);

            int barWidth = 138;
            int barLeft = cell.Right - barWidth;
            var labelRect = new Rectangle(barLeft, cell.Top + 8, barWidth, 14);
            TextRenderer.DrawText(g, "WARMING", Theme.Mono(7.5f), labelRect, Theme.Warming,
                TextFormatFlags.Left | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, percent + "%", Theme.Mono(7.5f), labelRect, Theme.Warming,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);

            var bar = new RectangleF(barLeft, cell.Top + 24, barWidth, 6);
            Theme.DrawStripedBar(g, bar, fraction, _animFrame, Theme.Warming, Color.FromArgb(0x15, 0x24, 0x30));
        }

        private void DrawStateChip(Graphics g, Rectangle cell, PrefetchState state)
        {
            string label = Theme.StateLabel(state);
            Font font = Theme.Mono(7.5f, FontStyle.Bold);
            int textWidth = TextRenderer.MeasureText(g, label, font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int chipWidth = textWidth + 20;
            int chipHeight = 20;
            var chip = new RectangleF(cell.Right - chipWidth, cell.Top + (cell.Height - chipHeight) / 2f, chipWidth, chipHeight);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.DrawChip(g, chip, label, Theme.StateColor(state), font);
            g.SmoothingMode = SmoothingMode.Default;
        }

        // --- Footer ------------------------------------------------------------------------

        private void BuildStatusBar()
        {
            _statusBar.Dock = DockStyle.Bottom;
            _statusBar.Height = 30;
            _statusBar.BackColor = Theme.Sunken;
            _statusBar.Paint += PaintStatusBar;
        }

        private void PaintStatusBar(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Sunken);
            using (var pen = new Pen(Theme.BorderSoft))
            {
                g.DrawLine(pen, 0, 0, _statusBar.Width, 0);
            }

            string index = _engine.Ready
                ? $"{_engine.IndexedPackageCount:#,0} packages indexed"
                : "indexing…";
            string tracked = $"{_trackedCount} tracked in range";

            Font font = Theme.Mono(8f);
            TextRenderer.DrawText(g, index, font, new Point(16, 8), Theme.TextFaint, TextFormatFlags.NoPadding);
            int sep = 16 + TextRenderer.MeasureText(index, font).Width + 12;
            TextRenderer.DrawText(g, "|", font, new Point(sep, 8), Theme.Border, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, tracked, font, new Point(sep + 12, 8), Theme.TextFaint, TextFormatFlags.NoPadding);

            (string status, Color color) = FooterStatus();
            if (!string.IsNullOrEmpty(status))
            {
                int right = _statusBar.Width - 16 - TextRenderer.MeasureText(status, font).Width;
                TextRenderer.DrawText(g, status, font, new Point(right, 8), color, TextFormatFlags.NoPadding);
            }
        }

        /// <summary>Right-aligned footer readout — one short phrase describing the current state.</summary>
        private (string, Color) FooterStatus()
        {
            switch (_engine.Phase)
            {
                case IndexPhase.Building:
                    return ("scanning packages", Theme.Skipped);
                case IndexPhase.Error:
                    return ("needs attention", Theme.Skipped);
            }

            if (_engine.IsPaused)
            {
                return ("paused", Theme.Skipped);
            }

            if (!_engine.IsSimConnected)
            {
                return ("MSFS not detected", Theme.TextFaint);
            }

            if (!_engine.IsSimRunning)
            {
                return ("paused in menu", Theme.Queued);
            }

            if (_countSkipped > 0)
            {
                return ("budget reached", Theme.Skipped);
            }

            if (_countWarming > 0)
            {
                return ("warming active", Theme.Loaded);
            }

            return ("standing by", Theme.Loaded);
        }

        // --- Log panel ---------------------------------------------------------------------

        private void BuildLogPanel()
        {
            _logPanel.Dock = DockStyle.Bottom;
            _logPanel.Height = 176;
            _logPanel.Visible = false;
            _logPanel.BackColor = Theme.Sunken;
            _logPanel.Padding = new Padding(1, 1, 1, 0);
            _logPanel.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.Border);
                e.Graphics.DrawLine(pen, 0, 0, _logPanel.Width, 0);
            };

            _logHeader = new Label
            {
                Text = "LOG",
                Dock = DockStyle.Top,
                Height = 24,
                Font = Theme.Mono(8f),
                ForeColor = Theme.TextFaint,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(15, 0, 0, 0),
                BackColor = Theme.Sunken,
            };

            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.Dock = DockStyle.Fill;
            _logBox.BorderStyle = BorderStyle.None;
            _logBox.BackColor = Theme.Sunken;
            _logBox.ForeColor = Theme.TextMuted;
            _logBox.Font = Theme.Mono(8f);

            _logPanel.Controls.Add(_logBox);
            _logPanel.Controls.Add(_logHeader);
        }

        private void ToggleLog()
        {
            _logPanel.Visible = !_logPanel.Visible;
        }

        // --- Tray --------------------------------------------------------------------------

        private void BuildTray()
        {
            _tray.Text = "RAMP";
            _tray.DoubleClick += (_, __) => RestoreFromTray();
            UpdateTrayIcon(Theme.Unloaded);
            _tray.Visible = true;

            var menu = new ContextMenuStrip
            {
                Renderer = new DarkMenuRenderer(),
                BackColor = Theme.Raised,
                ForeColor = Theme.Text,
                ShowImageMargin = false,
                Font = Theme.Sans(9f),
            };

            // Header readout — the menu doubles as a status panel (design 2f).
            var headerName = new ToolStripLabel("RAMP")
            {
                Font = Theme.Mono(8f, FontStyle.Bold),
                ForeColor = Theme.TextMuted,
                Enabled = false,
                Margin = new Padding(4, 4, 4, 0),
            };
            _trayHeaderStatus = new ToolStripLabel("idle")
            {
                Font = Theme.Sans(8.5f),
                ForeColor = Theme.Loaded,
                Enabled = false,
                Margin = new Padding(4, 0, 4, 4),
            };

            _trayPauseItem = new ToolStripMenuItem("Pause warming", null, (_, __) => TogglePause());

            menu.Items.Add(headerName);
            menu.Items.Add(_trayHeaderStatus);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open RAMP", null, (_, __) => RestoreFromTray());
            menu.Items.Add("Settings…", null, (_, __) => OpenSettings());
            menu.Items.Add(_trayPauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, __) => { _reallyExit = true; Close(); });
            menu.Opening += (_, __) => UpdateTrayMenu();
            _tray.ContextMenuStrip = menu;
        }

        private void UpdateTrayMenu()
        {
            float usedGb = _cacheUsedBytes / (1024f * 1024f * 1024f);
            _trayHeaderStatus.Text = _engine.IsPaused
                ? "paused"
                : $"{_countLoaded} loaded · {_countWarming} warming · {usedGb:0.00} GB";
            _trayHeaderStatus.ForeColor = _engine.IsPaused ? Theme.Skipped : Theme.Loaded;
            _trayPauseItem.Text = _engine.IsPaused ? "Resume warming" : "Pause warming";
        }

        private void TogglePause()
        {
            _engine.SetPaused(!_engine.IsPaused);
            UpdateTrayMenu();
            _statusBar.Invalidate();
        }

        /// <summary>Recolour the tray glyph to the dominant state so it reads at a glance.</summary>
        private void UpdateTrayIcon(Color color)
        {
            if (color == _trayColor)
            {
                return;
            }

            _trayColor = color;
            using var bitmap = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.DrawLogo(g, new RectangleF(0, 0, 15, 15), Theme.Raised, color, Theme.Border);
            }

            IntPtr handle = bitmap.GetHicon();
            try
            {
                Icon previous = _trayIcon;
                _trayIcon = Icon.FromHandle(handle);
                _tray.Icon = _trayIcon;
                if (previous != null)
                {
                    // Icon.FromHandle doesn't own the handle, so free the GDI icon explicitly.
                    IntPtr previousHandle = previous.Handle;
                    previous.Dispose();
                    DestroyIcon(previousHandle);
                }
            }
            catch
            {
                DestroyIcon(handle);
            }
        }

        private Color DominantStateColor()
        {
            if (_countWarming > 0) return Theme.Warming;
            if (_countLoaded > 0) return Theme.Loaded;
            if (_countQueued > 0) return Theme.Queued;
            if (_countSkipped > 0) return Theme.Skipped;
            return Theme.Unloaded;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        // --- Custom window chrome (borderless, but keep native resize / snap / shadow) -----

        private const int WsMinimizeBox = 0x00020000;
        private const int WsMaximizeBox = 0x00010000;
        private const int WsThickFrame = 0x00040000;
        private const int WsCaption = 0x00C00000;
        private const int WmNcCalcSize = 0x0083;
        private const int WmNcLButtonDown = 0x00A1;
        private const int HtCaption = 0x0002;
        private const int WM_NCLBUTTONDOWN = WmNcLButtonDown;
        private const int HTCAPTION = HtCaption;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NcCalcSizeParams { public Rect Client, Prev, Source; public IntPtr WindowPos; }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // Add the resize frame + caption so DWM gives us snap, shadow and native resize;
                // WM_NCCALCSIZE below reclaims the caption strip as client so no OS title shows.
                cp.Style |= WsMinimizeBox | WsMaximizeBox | WsThickFrame | WsCaption;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcCalcSize && m.WParam != IntPtr.Zero)
            {
                // Let the default compute the standard non-client insets (keeps side/bottom resize
                // borders), then pull the client's top back up over the caption we don't want.
                IntPtr result = DefWindowProc(m.HWnd, m.Msg, m.WParam, m.LParam);
                var p = (NcCalcSizeParams)Marshal.PtrToStructure(m.LParam, typeof(NcCalcSizeParams));
                p.Client.Top -= SystemInformation.CaptionHeight;
                Marshal.StructureToPtr(p, m.LParam, false);
                m.Result = result;
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // Keep the maximise/restore glyph in sync with snap / drag-to-top.
            if (_maxButton != null)
            {
                _maxButton.Kind = WindowState == FormWindowState.Maximized
                    ? CaptionButton.Glyph.Restore
                    : CaptionButton.Glyph.Maximize;
                _maxButton.Invalidate();
            }
        }

        private static Icon MakeAppIcon()
        {
            using var bitmap = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.DrawLogo(g, new RectangleF(1, 1, 30, 30), Theme.Raised, Theme.Accent, Theme.Border);
            }

            IntPtr handle = bitmap.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(handle).Clone(); // Clone owns its own handle
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OpenSettings()
        {
            using var dialog = new SettingsForm(_engine, _iniPath);
            dialog.ShowDialog(this);
            // The theme may have changed on save — re-skin the main window to match.
            ApplyTheme();
        }

        private void OpenAbout()
        {
            using var dialog = new AboutForm();
            dialog.ShowDialog(this);
        }

        // --- Refresh -----------------------------------------------------------------------

        private void Refresh_()
        {
            foreach (string line in _engine.DrainLog())
            {
                _logBox.AppendText(line + Environment.NewLine);
            }

            List<AirportState> states = _engine.Snapshot();
            states.Sort((a, b) =>
            {
                int byState = StateRank(a.State).CompareTo(StateRank(b.State));
                return byState != 0 ? byState : a.DistanceNauticalMiles.CompareTo(b.DistanceNauticalMiles);
            });

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (AirportState state in states)
            {
                var item = new ListViewItem(string.Empty) { Tag = state };
                item.SubItems.Add(string.Empty);
                item.SubItems.Add(string.Empty);
                item.SubItems.Add(string.Empty);
                item.SubItems.Add(string.Empty);
                _list.Items.Add(item);
            }

            _list.EndUpdate();

            RecomputeStats(states);
            UpdateGlyphColor(states);
            UpdateStateChrome(states.Count == 0);
            AnnounceWarmed(states);

            _statsStrip.Invalidate();
            _statusBar.Invalidate();
            _toolbar.Invalidate();
            UpdateTrayIcon(DominantStateColor());
            UpdateTrayText();
        }

        /// <summary>Second tooltip line summarising the current state, alongside the coloured glyph.</summary>
        private void UpdateTrayText()
        {
            string status;
            if (!_engine.IsSimConnected)
            {
                status = "Waiting for MSFS";
            }
            else if (!_engine.IsSimRunning)
            {
                status = "Connected · in menu";
            }
            else
            {
                var parts = new List<string>(4);
                if (_countWarming > 0) parts.Add($"{_countWarming} warming");
                if (_countLoaded > 0) parts.Add($"{_countLoaded} loaded");
                if (_countQueued > 0) parts.Add($"{_countQueued} queued");
                if (_countSkipped > 0) parts.Add($"{_countSkipped} skipped");
                status = parts.Count > 0 ? string.Join(" · ", parts) : "In flight · nothing in range";
            }

            // NotifyIcon.Text is capped at 63 chars; keep "RAMP" plus the status line within it.
            string text = "RAMP\r\n" + status;
            _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private void RecomputeStats(List<AirportState> states)
        {
            _countLoaded = _countWarming = _countQueued = _countSkipped = 0;
            _trackedCount = 0;
            _loadedBytes = _warmingBytes = 0;

            foreach (AirportState state in states)
            {
                switch (state.State)
                {
                    case PrefetchState.Loaded:
                        _countLoaded++;
                        _loadedBytes += state.WarmedBytes;
                        break;
                    case PrefetchState.Warming:
                        _countWarming++;
                        _warmingBytes += state.WarmedBytes;
                        break;
                    case PrefetchState.Queued:
                        _countQueued++;
                        break;
                    case PrefetchState.Skipped:
                        _countSkipped++;
                        break;
                }

                if (state.State != PrefetchState.OutOfRange)
                {
                    _trackedCount++;
                }
            }

            _cacheUsedBytes = _loadedBytes + _warmingBytes;
            _cacheBudgetBytes = _engine.CurrentConfig.RamBudgetMegabytes * 1024L * 1024L;
            _anyWarming = _countWarming > 0;
        }

        /// <summary>
        /// One balloon per flight: when the nearest tracked airport finishes warming, tell the user
        /// their approach is covered. Resets whenever the flight ends. No flight plan exists, so the
        /// nearest Loaded field stands in for "the destination".
        /// </summary>
        private void AnnounceWarmed(List<AirportState> states)
        {
            if (!_engine.IsSimRunning)
            {
                _balloonedThisFlight = false; // armed again for the next flight
                return;
            }

            if (_balloonedThisFlight)
            {
                return;
            }

            AirportState nearestLoaded = null;
            foreach (AirportState state in states)
            {
                if (state.State == PrefetchState.Loaded &&
                    (nearestLoaded == null || state.DistanceNauticalMiles < nearestLoaded.DistanceNauticalMiles))
                {
                    nearestLoaded = state;
                }
            }

            if (nearestLoaded == null)
            {
                return;
            }

            _balloonedThisFlight = true;
            long megabytes = nearestLoaded.WarmedBytes / (1024 * 1024);
            _tray.ShowBalloonTip(4000, $"{nearestLoaded.Icao} is warm",
                $"{megabytes:#,0} MB cached — your approach won't stutter.", ToolTipIcon.Info);
        }

        /// <summary>
        /// Track the most recently added airport (first time its key appears) and colour the
        /// toolbar glyph by that airport's current state — grey whenever nothing is active.
        /// </summary>
        private void UpdateGlyphColor(List<AirportState> states)
        {
            string newest = null;
            double nearest = double.MaxValue;
            foreach (AirportState state in states)
            {
                string key = state.Icao + "|" + state.PackageName;
                if (_seenAirportKeys.Add(key) && state.DistanceNauticalMiles < nearest)
                {
                    nearest = state.DistanceNauticalMiles;
                    newest = key;
                }
            }

            if (newest != null)
            {
                _latestAirportKey = newest;
            }

            PrefetchState? latest = null;
            if (_latestAirportKey != null)
            {
                foreach (AirportState state in states)
                {
                    if (state.Icao + "|" + state.PackageName == _latestAirportKey)
                    {
                        latest = state.State;
                        break;
                    }
                }
            }

            _glyphColor = _engine.IsSimRunning && latest.HasValue && latest.Value != PrefetchState.OutOfRange
                ? Theme.StateColor(latest.Value)
                : Theme.Unloaded;
        }

        private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

        /// <summary>List ordering: readiest first — Loaded, Warming, Queued, Skipped, then Unloaded.</summary>
        private static int StateRank(PrefetchState state)
        {
            switch (state)
            {
                case PrefetchState.Loaded: return 0;
                case PrefetchState.Warming: return 1;
                case PrefetchState.Queued: return 2;
                case PrefetchState.Skipped: return 3;
                default: return 4; // OutOfRange / Unloaded
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing the window (the X) hides to tray so the engine keeps running in the
            // background — even with MSFS off. Real exit only via the tray's Exit item.
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(3000, "Still running",
                    "RAMP keeps running in the tray. Right-click the icon to exit.",
                    ToolTipIcon.Info);
                return;
            }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refreshTimer.Stop();
            _animTimer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            if (_trayIcon != null)
            {
                IntPtr handle = _trayIcon.Handle;
                _trayIcon.Dispose();
                DestroyIcon(handle);
            }

            _appIcon?.Dispose();

            base.OnFormClosed(e);
        }
    }
}
