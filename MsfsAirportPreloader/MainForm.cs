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
    /// The Apron main window: a dark instrument table. A custom toolbar carries the mark and the
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

        private Icon _trayIcon;
        private Color _trayColor = Color.Empty;

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
                if (!_anyWarming)
                {
                    return;
                }

                _animFrame++;
                _list.Invalidate();
                _statsStrip.Invalidate();
            };
            _animTimer.Start();
        }

        private void BuildLayout()
        {
            Text = "Apron";
            Width = 900;
            Height = 620;
            MinimumSize = new Size(720, 460);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = Theme.Sans(9f);

            BuildToolbar();
            BuildStatsStrip();
            BuildStatusBar();
            BuildLogPanel();
            BuildList();

            // Docked controls claim space in reverse z-order, so the Fill control (the list) must
            // be added FIRST, then each edge band from innermost to outermost.
            Controls.Add(_list);
            Controls.Add(_logPanel);   // Bottom, above the footer, hidden by default
            Controls.Add(_statusBar);  // Bottom, outermost
            Controls.Add(_statsStrip); // Top, below the toolbar
            Controls.Add(_toolbar);    // Top, outermost
        }

        // --- Toolbar -----------------------------------------------------------------------

        private void BuildToolbar()
        {
            _toolbar.Dock = DockStyle.Top;
            _toolbar.Height = 44;
            _toolbar.BackColor = Theme.Raised;
            _toolbar.Paint += PaintToolbar;

            int left = 132; // clears the logo tile and the APRON wordmark painted behind
            Button rescan = MakeToolButton("Rescan", ref left);
            rescan.Click += (_, __) => _engine.ApplyConfig(_engine.CurrentConfig, rescanPackages: true);
            Button log = MakeToolButton("Log", ref left);
            log.Click += (_, __) => ToggleLog();
            Button settings = MakeToolButton("Settings", ref left);
            settings.Click += (_, __) => OpenSettings();

            _toolbar.Controls.Add(rescan);
            _toolbar.Controls.Add(log);
            _toolbar.Controls.Add(settings);
        }

        private Button MakeToolButton(string text, ref int left)
        {
            var button = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Font = Theme.Sans(9f),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.RaisedHover,
                Height = 28,
                Width = 78,
                Top = 8,
                Left = left,
                TabStop = false,
            };
            button.FlatAppearance.BorderColor = Theme.InputBorder;
            button.FlatAppearance.MouseOverBackColor = Theme.Border;
            button.FlatAppearance.MouseDownBackColor = Theme.Border;
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

            // Logo tile + wordmark.
            Theme.DrawLogo(g, new RectangleF(14, 12, 20, 20), Theme.RaisedHover, Theme.Accent, Theme.Border);
            TextRenderer.DrawText(g, "APRON", Theme.Mono(9.5f, FontStyle.Bold),
                new Point(44, 15), Theme.Text, TextFormatFlags.NoPadding);

            // Right side: connection dot + status, then flight state.
            (Color dotColor, string status, Color statusColor, string flight) = ConnectionStatus();
            int right = _toolbar.Width - 16;

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
            using (var brush = new SolidBrush(Theme.StateColor(state.State)))
            {
                g.FillRectangle(brush, row.Left, row.Top, 3, row.Height);
            }

            bool dim = state.State == PrefetchState.OutOfRange;
            Color icaoColor = dim ? Color.FromArgb(0x6B, 0x81, 0x94) : Theme.Text;
            Color nameColor = dim ? Color.FromArgb(0x5F, 0x77, 0x8A) : Color.FromArgb(0xA9, 0xBE, 0xCE);
            Color warmedColor = state.State == PrefetchState.Loaded ? Theme.Text : Theme.TextDim;

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

            if (_engine.IsSimRunning)
            {
                string live = "warming active";
                int right = _statusBar.Width - 16 - TextRenderer.MeasureText(live, font).Width;
                TextRenderer.DrawText(g, live, font, new Point(right, 8), Theme.Loaded, TextFormatFlags.NoPadding);
            }
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

            var header = new Label
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
            _logPanel.Controls.Add(header);
        }

        private void ToggleLog()
        {
            _logPanel.Visible = !_logPanel.Visible;
        }

        // --- Tray --------------------------------------------------------------------------

        private void BuildTray()
        {
            _tray.Text = "Apron";
            _tray.DoubleClick += (_, __) => RestoreFromTray();
            UpdateTrayIcon(Theme.Unloaded);
            _tray.Visible = true;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (_, __) => RestoreFromTray());
            menu.Items.Add("Settings", null, (_, __) => OpenSettings());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, __) => { _reallyExit = true; Close(); });
            _tray.ContextMenuStrip = menu;
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

            _statsStrip.Invalidate();
            _statusBar.Invalidate();
            _toolbar.Invalidate();
            UpdateTrayIcon(DominantStateColor());
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
                    "Apron keeps running in the tray. Right-click the icon to exit.",
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

            base.OnFormClosed(e);
        }
    }
}
