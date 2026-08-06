using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MsfsAirportPreloader
{
    /// <summary>
    /// Apron settings: the same values as before, laid out as labelled dark sections —
    /// Range &amp; Timing, Memory (a draggable RAM budget slider), Paths, and Behaviour toggles.
    /// The outer-radius field validates inline in amber and is clamped up to the minimum on save,
    /// never blocking with a dialog — "amber, never red; nothing is lost".
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly Engine _engine;
        private readonly string _iniPath;
        private readonly Config _original;

        private readonly TextBox _outerRadius = new TextBox();
        private readonly TextBox _pollSeconds = new TextBox();
        private readonly Slider _ramSlider = new Slider();
        private readonly Label _ramValue = new Label();
        private readonly TextBox _packagesPath = new TextBox();
        private readonly TextBox _airportsCsv = new TextBox();
        private readonly ToggleSwitch _startWithWindows = new ToggleSwitch();
        private readonly ToggleSwitch _verbose = new ToggleSwitch();

        private BufferedPanel _outerRadiusBox;
        private Label _outerRadiusHint;

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
            ClientSize = new Size(520, 580);
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = Theme.Sans(9f);

            int y = 20;
            const int left = 22;
            int fullWidth = ClientSize.Width - left * 2;

            // --- Range & timing (two columns) ---
            AddSection("RANGE & TIMING", left, ref y);
            int half = (fullWidth - 14) / 2;
            AddFieldLabel($"Outer radius", left, y);
            AddFieldLabel("Poll interval", left + half + 14, y);
            _outerRadiusBox = AddInput(_outerRadius, left, y + 18, half, "NM");
            AddInput(_pollSeconds, left + half + 14, y + 18, half, "s");
            _outerRadiusHint = new Label
            {
                Left = left,
                Top = y + 56,
                Width = fullWidth,
                Height = 16,
                Font = Theme.Sans(8f),
                ForeColor = Theme.TextDim,
                Text = "Warming starts within this distance.",
            };
            Controls.Add(_outerRadiusHint);
            _outerRadius.TextChanged += (_, __) => ValidateOuterRadius();
            y += 84;

            // --- Memory (slider) ---
            AddSection("MEMORY", left, ref y);
            AddFieldLabel("RAM budget", left, y);
            _ramValue.SetBounds(left, y, fullWidth, 16);
            _ramValue.Font = Theme.Mono(10f, FontStyle.Bold);
            _ramValue.ForeColor = Theme.Text;
            _ramValue.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_ramValue);

            _ramSlider.SetBounds(left, y + 22, fullWidth, 22);
            _ramSlider.Minimum = 1;
            _ramSlider.Maximum = 32;
            _ramSlider.ValueChanged += (_, __) => UpdateRamLabel();
            Controls.Add(_ramSlider);

            Controls.Add(new Label
            {
                Left = left,
                Top = y + 48,
                Width = fullWidth / 2,
                Height = 16,
                Font = Theme.Mono(8f),
                ForeColor = Theme.TextFaint,
                Text = "1 GB",
                TextAlign = ContentAlignment.MiddleLeft,
            });
            Controls.Add(new Label
            {
                Left = left + fullWidth / 2,
                Top = y + 48,
                Width = fullWidth - fullWidth / 2,
                Height = 16,
                Font = Theme.Mono(8f),
                ForeColor = Theme.TextFaint,
                Text = $"32 GB · {AvailablePhysicalGb():0.0} GB free",
                TextAlign = ContentAlignment.MiddleRight,
            });
            y += 78;

            // --- Paths ---
            AddSection("PATHS", left, ref y);
            AddFieldLabel("MSFS package folder (blank = auto-detect)", left, y);
            AddPathInput(_packagesPath, left, y + 18, fullWidth, browseFolder: true);
            y += 58;
            AddFieldLabel("airports.csv (blank = beside the app)", left, y);
            AddPathInput(_airportsCsv, left, y + 18, fullWidth, browseFolder: false);
            y += 58;

            // --- Behaviour (toggles) ---
            AddSection("BEHAVIOUR", left, ref y);
            AddToggle(_startWithWindows, "Start with Windows", left, ref y);
            AddToggle(_verbose, "Verbose logging", left, ref y);

            BuildFooter();
        }

        private void AddSection(string text, int left, ref int y)
        {
            var label = new Label
            {
                Text = text,
                Left = left,
                Top = y,
                Width = ClientSize.Width - left * 2,
                Height = 18,
                Font = Theme.Mono(8f),
                ForeColor = Theme.TextFaint,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            Controls.Add(label);
            y += 24;
        }

        private void AddFieldLabel(string text, int x, int y)
        {
            Controls.Add(new Label
            {
                Text = text,
                Left = x,
                Top = y,
                Width = 320,
                Height = 16,
                Font = Theme.Sans(9f),
                ForeColor = Theme.TextMuted,
            });
        }

        private BufferedPanel AddInput(TextBox input, int x, int y, int width, string suffix)
        {
            var box = new BufferedPanel { Left = x, Top = y, Width = width, Height = 34, BackColor = Theme.Window };
            box.Tag = Theme.InputBorder;
            box.Paint += (_, e) =>
            {
                var rect = new RectangleF(0, 0, box.Width - 1, box.Height - 1);
                Theme.FillRoundedRect(e.Graphics, rect, 5f, Theme.InputBg);
                Theme.DrawRoundedBorder(e.Graphics, rect, 5f, (Color)box.Tag);
            };

            int suffixWidth = 0;
            if (!string.IsNullOrEmpty(suffix))
            {
                suffixWidth = 26;
                box.Controls.Add(new Label
                {
                    Text = suffix,
                    Font = Theme.Mono(8.5f),
                    ForeColor = Theme.TextDim,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Left = width - suffixWidth,
                    Top = 0,
                    Width = suffixWidth,
                    Height = box.Height,
                    BackColor = Theme.InputBg,
                });
            }

            input.BorderStyle = BorderStyle.None;
            input.BackColor = Theme.InputBg;
            input.ForeColor = Theme.Text;
            input.Font = Theme.Mono(11f);
            input.Left = 11;
            input.Top = 9;
            input.Width = width - 22 - suffixWidth;
            box.Controls.Add(input);

            Controls.Add(box);
            return box;
        }

        private void AddPathInput(TextBox input, int x, int y, int width, bool browseFolder)
        {
            const int browseWidth = 76;
            AddInput(input, x, y, width - browseWidth - 8, null);

            var browse = new Button
            {
                Text = "Browse",
                FlatStyle = FlatStyle.Flat,
                Font = Theme.Sans(9f),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.RaisedHover,
                Left = x + width - browseWidth,
                Top = y,
                Width = browseWidth,
                Height = 34,
                TabStop = false,
            };
            browse.FlatAppearance.BorderColor = Theme.InputBorder;
            browse.Click += (_, __) => Browse(input, browseFolder);
            Controls.Add(browse);
        }

        private void AddToggle(ToggleSwitch toggle, string text, int left, ref int y)
        {
            toggle.Left = left;
            toggle.Top = y;
            Controls.Add(toggle);

            Controls.Add(new Label
            {
                Text = text,
                Left = left + 46,
                Top = y + 1,
                Width = 300,
                Height = 18,
                Font = Theme.Sans(9.5f),
                ForeColor = Theme.Text,
            });
            y += 32;
        }

        private void BuildFooter()
        {
            var save = new Button
            {
                Text = "Save",
                DialogResult = DialogResult.OK,
                FlatStyle = FlatStyle.Flat,
                Font = Theme.Sans(9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0x08, 0x12, 0x1A),
                BackColor = Theme.Accent,
                Width = 92,
                Height = 34,
                Top = ClientSize.Height - 50,
                Left = ClientSize.Width - 92 - 22,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            save.FlatAppearance.BorderSize = 0;
            save.Click += (_, __) => Apply();

            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                FlatStyle = FlatStyle.Flat,
                Font = Theme.Sans(9.5f),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.Window,
                Width = 92,
                Height = 34,
                Top = ClientSize.Height - 50,
                Left = ClientSize.Width - 92 - 22 - 100,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            cancel.FlatAppearance.BorderColor = Theme.InputBorder;

            var divider = new BufferedPanel
            {
                Left = 0,
                Top = ClientSize.Height - 66,
                Width = ClientSize.Width,
                Height = 1,
                BackColor = Theme.Border,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };

            Controls.Add(divider);
            Controls.Add(save);
            Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;
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
            _ramSlider.Value = Math.Round(_original.RamBudgetMegabytes / 1024.0);
            UpdateRamLabel();
            _packagesPath.Text = _original.InstalledPackagesPathOverride;
            _airportsCsv.Text = _original.AirportsCsvPathOverride;
            _startWithWindows.Checked = _original.StartWithWindows;
            _verbose.Checked = _original.Verbose;
            ValidateOuterRadius();
        }

        private void UpdateRamLabel()
        {
            _ramValue.Text = $"{_ramSlider.Value:0.#} GB";
        }

        private void ValidateOuterRadius()
        {
            if (_outerRadiusBox == null)
            {
                return;
            }

            double value = ParseDouble(_outerRadius.Text, _original.OuterRadiusNauticalMiles);
            bool tooLow = value < Config.MinimumOuterRadiusNauticalMiles;
            _outerRadiusBox.Tag = tooLow ? Theme.Skipped : Theme.InputBorder;
            _outerRadiusBox.Invalidate();

            if (tooLow)
            {
                _outerRadiusHint.ForeColor = Theme.Skipped;
                _outerRadiusHint.Text =
                    $"Minimum is {Config.MinimumOuterRadiusNauticalMiles:0} NM — will be raised to " +
                    $"{Config.MinimumOuterRadiusNauticalMiles:0} on save (MSFS loads scenery near " +
                    $"{Config.MsfsLoadRadiusNauticalMiles:0} NM).";
            }
            else
            {
                _outerRadiusHint.ForeColor = Theme.TextDim;
                _outerRadiusHint.Text = "Warming starts within this distance.";
            }
        }

        private void Apply()
        {
            // Forgiving: clamp the outer radius up to the minimum rather than rejecting the save.
            double outerRadius = Math.Max(
                Config.MinimumOuterRadiusNauticalMiles,
                ParseDouble(_outerRadius.Text, _original.OuterRadiusNauticalMiles));

            var updated = _original.Clone();
            updated.OuterRadiusNauticalMiles = outerRadius;
            updated.PollSeconds = ParseDouble(_pollSeconds.Text, _original.PollSeconds);
            updated.RamBudgetMegabytes = (long)Math.Round(_ramSlider.Value * 1024);
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

        // --- Available physical memory (for the slider's context) ---

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        private static double AvailablePhysicalGb()
        {
            try
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                if (GlobalMemoryStatusEx(ref status))
                {
                    return status.AvailablePhysical / (1024.0 * 1024.0 * 1024.0);
                }
            }
            catch
            {
                // ignore — the note is informational only
            }

            return 0;
        }
    }
}
