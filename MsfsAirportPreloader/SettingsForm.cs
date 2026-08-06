using System;
using System.Globalization;
using System.Windows.Forms;

namespace MsfsAirportPreloader
{
    internal sealed class SettingsForm : Form
    {
        private readonly Engine _engine;
        private readonly string _iniPath;
        private readonly Config _original;

        private readonly TextBox _outerRadius = new TextBox();
        private readonly TextBox _pollSeconds = new TextBox();
        private readonly TextBox _ramBudget = new TextBox();
        private readonly TextBox _packagesPath = new TextBox();
        private readonly TextBox _airportsCsv = new TextBox();
        private readonly CheckBox _startWithWindows = new CheckBox();
        private readonly CheckBox _verbose = new CheckBox();

        public SettingsForm(Engine engine, string iniPath)
        {
            _engine = engine;
            _iniPath = iniPath;
            _original = engine.CurrentConfig.Clone();
            BuildLayout();
            LoadValues();
        }

        private void BuildLayout()
        {
            Text = "Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Width = 560;
            Height = 360;

            int y = 16;
            AddField("Outer radius (NM) — start warming within this distance:", _outerRadius, ref y);
            AddField("Poll interval (seconds):", _pollSeconds, ref y);
            AddField("RAM budget (MB) — max held in cache (applies after restart):", _ramBudget, ref y);
            AddPathField("MSFS package folder (blank = auto-detect):", _packagesPath, browseFolder: true, ref y);
            AddPathField("airports.csv path (blank = beside exe):", _airportsCsv, browseFolder: false, ref y);

            _startWithWindows.Text = "Start with Windows";
            _startWithWindows.Left = 16;
            _startWithWindows.Top = y;
            _startWithWindows.Width = 200;
            Controls.Add(_startWithWindows);

            _verbose.Text = "Verbose logging";
            _verbose.Left = 240;
            _verbose.Top = y;
            _verbose.Width = 200;
            Controls.Add(_verbose);
            y += 40;

            var okButton = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 90, Top = y, Left = 350 };
            okButton.Click += (_, __) => Apply();
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Top = y, Left = 450 };
            Controls.Add(okButton);
            Controls.Add(cancelButton);
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private void AddField(string label, TextBox box, ref int y)
        {
            Controls.Add(new Label { Text = label, Left = 16, Top = y, Width = 520 });
            box.Left = 16;
            box.Top = y + 20;
            box.Width = 220;
            Controls.Add(box);
            y += 52;
        }

        private void AddPathField(string label, TextBox box, bool browseFolder, ref int y)
        {
            Controls.Add(new Label { Text = label, Left = 16, Top = y, Width = 520 });
            box.Left = 16;
            box.Top = y + 20;
            box.Width = 420;
            Controls.Add(box);

            var browse = new Button { Text = "...", Left = 444, Top = y + 18, Width = 40 };
            browse.Click += (_, __) => Browse(box, browseFolder);
            Controls.Add(browse);
            y += 52;
        }

        private void Browse(TextBox box, bool browseFolder)
        {
            if (browseFolder)
            {
                using var dialog = new FolderBrowserDialog();
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    box.Text = dialog.SelectedPath;
                }
            }
            else
            {
                using var dialog = new OpenFileDialog { Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*" };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    box.Text = dialog.FileName;
                }
            }
        }

        private void LoadValues()
        {
            _outerRadius.Text = _original.OuterRadiusNauticalMiles.ToString(CultureInfo.InvariantCulture);
            _pollSeconds.Text = _original.PollSeconds.ToString(CultureInfo.InvariantCulture);
            _ramBudget.Text = _original.RamBudgetMegabytes.ToString(CultureInfo.InvariantCulture);
            _packagesPath.Text = _original.InstalledPackagesPathOverride;
            _airportsCsv.Text = _original.AirportsCsvPathOverride;
            _startWithWindows.Checked = _original.StartWithWindows;
            _verbose.Checked = _original.Verbose;
        }

        private void Apply()
        {
            var updated = _original.Clone();
            updated.OuterRadiusNauticalMiles = ParseDouble(_outerRadius.Text, _original.OuterRadiusNauticalMiles);
            updated.PollSeconds = ParseDouble(_pollSeconds.Text, _original.PollSeconds);
            updated.RamBudgetMegabytes = (long)ParseDouble(_ramBudget.Text, _original.RamBudgetMegabytes);
            updated.InstalledPackagesPathOverride = _packagesPath.Text.Trim();
            updated.AirportsCsvPathOverride = _airportsCsv.Text.Trim();
            updated.StartWithWindows = _startWithWindows.Checked;
            updated.Verbose = _verbose.Checked;

            bool rescan =
                !string.Equals(updated.InstalledPackagesPathOverride, _original.InstalledPackagesPathOverride, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(updated.AirportsCsvPathOverride, _original.AirportsCsvPathOverride, StringComparison.OrdinalIgnoreCase);

            updated.Save(_iniPath);
            StartupRegistry.Apply(updated.StartWithWindows, null);
            _engine.ApplyConfig(updated, rescan);
        }

        private static double ParseDouble(string text, double fallback)
            => double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double value) ? value : fallback;
    }
}
