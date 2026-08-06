using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MsfsAirportPreloader
{
    internal sealed class MainForm : Form
    {
        private readonly Engine _engine;
        private readonly string _iniPath;

        private readonly ListView _airportList = new ListView();
        private readonly TextBox _logBox = new TextBox();
        private readonly Label _statusLabel = new Label();
        private readonly Timer _refreshTimer = new Timer();
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly SplitContainer _split = new SplitContainer();

        private bool _reallyExit;

        public MainForm(Engine engine, string iniPath)
        {
            _engine = engine;
            _iniPath = iniPath;
            BuildLayout();
            BuildTray();

            _refreshTimer.Interval = 750;
            _refreshTimer.Tick += (_, __) => Refresh_();
            _refreshTimer.Start();
        }

        private void BuildLayout()
        {
            Text = "MSFS Airport Preloader";
            Width = 780;
            Height = 560;
            MinimumSize = new Size(560, 400);
            StartPosition = FormStartPosition.CenterScreen;

            var toolbar = new Panel { Dock = DockStyle.Top, Height = 40 };
            var settingsButton = new Button { Text = "Settings", Left = 8, Top = 6, Width = 90 };
            settingsButton.Click += (_, __) => OpenSettings();
            var rescanButton = new Button { Text = "Rescan", Left = 104, Top = 6, Width = 90 };
            rescanButton.Click += (_, __) => _engine.ApplyConfig(_engine.CurrentConfig, rescanPackages: true);
            var clearLogButton = new Button { Text = "Clear log", Left = 200, Top = 6, Width = 90 };
            clearLogButton.Click += (_, __) => _logBox.Clear();
            toolbar.Controls.Add(settingsButton);
            toolbar.Controls.Add(rescanButton);
            toolbar.Controls.Add(clearLogButton);

            _statusLabel.Dock = DockStyle.Bottom;
            _statusLabel.Height = 24;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.Padding = new Padding(8, 0, 0, 0);

            _airportList.Dock = DockStyle.Fill;
            _airportList.View = View.Details;
            _airportList.FullRowSelect = true;
            _airportList.GridLines = true;
            _airportList.HideSelection = true;
            _airportList.Columns.Add("ICAO", 70);
            _airportList.Columns.Add("Package", 300);
            _airportList.Columns.Add("Distance (NM)", 100, HorizontalAlignment.Right);
            _airportList.Columns.Add("Status", 100);
            _airportList.Columns.Add("Warmed (MB)", 100, HorizontalAlignment.Right);

            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.Dock = DockStyle.Fill;
            _logBox.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            _logBox.BackColor = Color.White;

            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Horizontal;
            _split.Panel1.Controls.Add(_airportList);
            _split.Panel2.Controls.Add(_logBox);

            // Add Top/Bottom docked controls first, Fill control last, so it takes the
            // remaining space instead of overlapping the toolbar/status bar.
            Controls.Add(toolbar);
            Controls.Add(_statusLabel);
            Controls.Add(_split);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Set the splitter only once the container has a real height (setting it in the
            // initializer throws because the control is still at its default size).
            try
            {
                _split.SplitterDistance = (int)(_split.Height * 0.6);
            }
            catch
            {
                // ignore — default splitter position is fine
            }
        }

        private void BuildTray()
        {
            _tray.Icon = System.Drawing.SystemIcons.Application;
            _tray.Text = "MSFS Airport Preloader";
            _tray.Visible = true;
            _tray.DoubleClick += (_, __) => RestoreFromTray();

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, (_, __) => RestoreFromTray());
            menu.Items.Add("Settings", null, (_, __) => OpenSettings());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, __) => { _reallyExit = true; Close(); });
            _tray.ContextMenuStrip = menu;
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
        }

        private void Refresh_()
        {
            // Drain log first so events line up with the airport states below.
            foreach (string line in _engine.DrainLog())
            {
                _logBox.AppendText(line + Environment.NewLine);
            }

            List<AirportState> states = _engine.Snapshot();
            states.Sort((a, b) => a.DistanceNauticalMiles.CompareTo(b.DistanceNauticalMiles));

            _airportList.BeginUpdate();
            _airportList.Items.Clear();
            foreach (AirportState state in states)
            {
                var item = new ListViewItem(state.Icao);
                item.SubItems.Add(state.PackageName);
                item.SubItems.Add($"{state.DistanceNauticalMiles:0}");
                item.SubItems.Add(StatusText(state.State));
                item.SubItems.Add($"{state.WarmedBytes / (1024 * 1024)}");
                item.ForeColor = StatusColor(state.State);
                _airportList.Items.Add(item);
            }

            _airportList.EndUpdate();

            _statusLabel.Text = BuildStatusText(states.Count);
            _statusLabel.ForeColor = _engine.IsSimConnected ? Color.Green : Color.DimGray;
        }

        private string BuildStatusText(int trackedCount)
        {
            string sim = _engine.IsSimConnected ? "MSFS connected" : "Waiting for MSFS";
            string index = _engine.Ready
                ? $"{_engine.IndexedPackageCount} packages / {_engine.IndexedAirportCount} airports indexed"
                : "indexing...";
            return $"{sim}   |   {index}   |   {trackedCount} airports tracked";
        }

        private static string StatusText(PrefetchState state)
        {
            switch (state)
            {
                case PrefetchState.Loaded: return "Loaded";
                case PrefetchState.Warming: return "Loading...";
                case PrefetchState.Queued: return "Queued";
                case PrefetchState.Skipped: return "Skipped";
                default: return "Unloaded";
            }
        }

        private static Color StatusColor(PrefetchState state)
        {
            switch (state)
            {
                case PrefetchState.Loaded: return Color.SeaGreen;
                case PrefetchState.Warming: return Color.DarkOrange;
                case PrefetchState.Queued: return Color.SteelBlue;
                case PrefetchState.Skipped: return Color.Firebrick;
                default: return Color.DimGray;
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
                    "MSFS Airport Preloader keeps running in the tray. Right-click the icon to exit.",
                    ToolTipIcon.Info);
                return;
            }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refreshTimer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            base.OnFormClosed(e);
        }
    }
}
