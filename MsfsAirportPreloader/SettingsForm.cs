using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MsfsAirportPreloader
{
    /// <summary>
    /// RAMP settings: the same values as before, laid out as labelled dark sections —
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
        private readonly ToggleSwitch _startWithWindows = new ToggleSwitch();
        private readonly ToggleSwitch _verbose = new ToggleSwitch();

        private BufferedPanel _outerRadiusBox;
        private Label _outerRadiusHint;

        private Config.ThemeMode _appearance;
        private readonly RoundedButton[] _themeButtons = new RoundedButton[3];

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
            ClientSize = new Size(520, 620);
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = Theme.Sans(9f);

            int y = 20;
            const int left = 22;
            int fullWidth = ClientSize.Width - left * 2;

            // --- Range & timing (two columns) ---
            AddSection("RANGE & TIMING", left, ref y);
            int half = (fullWidth - 14) / 2;
            AddFieldLabel("Outer radius", left, y, half);
            AddFieldLabel("Poll interval", left + half + 14, y, half);
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
            AddFieldLabel("RAM budget", left, y, half);
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
            AddFieldLabel("MSFS package folder", left, y, fullWidth);
            AddPathInput(_packagesPath, left, y + 18, fullWidth, browseFolder: true);
            bool indexed = _engine.Ready;
            Controls.Add(new Label
            {
                Left = left,
                Top = y + 56,
                Width = fullWidth,
                Height = 16,
                Font = Theme.Sans(8.5f),
                ForeColor = indexed ? Theme.Loaded : Theme.TextDim,
                Text = indexed
                    ? $"✓ {_engine.IndexedPackageCount:#,0} packages found"
                    : "Scanning packages…",
            });
            y += 78;

            // --- Behaviour (toggles) ---
            AddSection("BEHAVIOUR", left, ref y);
            AddToggle(_startWithWindows, "Start with Windows", left, ref y);
            AddToggle(_verbose, "Verbose logging", left, ref y);

            // --- Appearance (theme selector) ---
            AddSection("APPEARANCE", left, ref y);
            BuildThemeSelector(left, y, fullWidth);
            y += 42;

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

        private void AddFieldLabel(string text, int x, int y, int width)
        {
            // Bound the width to the field's own column — a full-width opaque label would sit in
            // front of and hide the neighbouring column's label.
            Controls.Add(new Label
            {
                Text = text,
                Left = x,
                Top = y,
                Width = width,
                Height = 16,
                Font = Theme.Sans(9f),
                ForeColor = Theme.TextMuted,
            });
        }

        private BufferedPanel AddInput(TextBox input, int x, int y, int width, string suffix)
        {
            int suffixWidth = string.IsNullOrEmpty(suffix) ? 0 : 30;
            var box = new BufferedPanel { Left = x, Top = y, Width = width, Height = 34, BackColor = Theme.Window };
            box.Tag = Theme.InputBorder;
            box.Paint += (_, e) =>
            {
                Graphics g = e.Graphics;
                var rect = new RectangleF(0, 0, box.Width - 1, box.Height - 1);
                Theme.FillRoundedRect(g, rect, 5f, Theme.InputBg);
                Theme.DrawRoundedBorder(g, rect, 5f, (Color)box.Tag);

                // The unit reads as its own right-hand segment: a separator on its left, and the
                // panel's own rounded corners + border already give it the top/right/bottom edges.
                if (suffixWidth > 0)
                {
                    float sepX = box.Width - suffixWidth;
                    using (var pen = new Pen(Theme.InputBorder))
                    {
                        g.DrawLine(pen, sepX, 1, sepX, box.Height - 2);
                    }

                    var unitRect = new Rectangle((int)sepX, 0, suffixWidth, box.Height);
                    TextRenderer.DrawText(g, suffix, Theme.Mono(8.5f), unitRect, Theme.TextDim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            };

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

            var browse = new RoundedButton
            {
                Text = "Browse",
                Font = Theme.Sans(9f),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.RaisedHover,
                BorderColor = Theme.InputBorder,
                HoverColor = Theme.Border,
                Left = x + width - browseWidth,
                Top = y,
                Width = browseWidth,
                Height = 34,
                TabStop = false,
            };
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

        // Segmented three-way theme picker; the enum order (System, Light, Dark) matches button order.
        private void BuildThemeSelector(int left, int y, int width)
        {
            string[] labels = { "Follow system", "Light", "Dark" };
            const int gap = 8;
            int buttonWidth = (width - gap * 2) / 3;

            for (int index = 0; index < _themeButtons.Length; index++)
            {
                int captured = index;
                var button = new RoundedButton
                {
                    Text = labels[index],
                    Font = Theme.Sans(9f),
                    Height = 34,
                    Width = buttonWidth,
                    Left = left + index * (buttonWidth + gap),
                    Top = y,
                    TabStop = false,
                };
                button.Click += (_, __) => SelectTheme(captured);
                _themeButtons[index] = button;
                Controls.Add(button);
            }

            StyleThemeButtons();
        }

        private void SelectTheme(int index)
        {
            _appearance = (Config.ThemeMode)index;
            StyleThemeButtons();
        }

        private void StyleThemeButtons()
        {
            for (int index = 0; index < _themeButtons.Length; index++)
            {
                RoundedButton button = _themeButtons[index];
                if (button == null)
                {
                    continue;
                }

                bool selected = (int)_appearance == index;
                button.BackColor = selected ? Theme.Accent : Theme.RaisedHover;
                button.ForeColor = selected ? Color.FromArgb(0x08, 0x12, 0x1A) : Theme.TextMuted;
                button.BorderColor = selected ? Color.Empty : Theme.InputBorder;
                button.HoverColor = selected ? Theme.Accent : Theme.Border;
                button.Invalidate();
            }
        }

        private void BuildFooter()
        {
            var save = new RoundedButton
            {
                Text = "Save",
                DialogResult = DialogResult.OK,
                Font = Theme.Sans(9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0x08, 0x12, 0x1A),
                BackColor = Theme.Accent,
                BorderColor = Color.Empty,
                Width = 92,
                Height = 34,
                Top = ClientSize.Height - 50,
                Left = ClientSize.Width - 92 - 22,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            save.Click += (_, __) => Apply();

            var cancel = new RoundedButton
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Font = Theme.Sans(9.5f),
                ForeColor = Theme.TextMuted,
                BackColor = Theme.Window,
                BorderColor = Theme.InputBorder,
                HoverColor = Theme.RaisedHover,
                Width = 92,
                Height = 34,
                Top = ClientSize.Height - 50,
                Left = ClientSize.Width - 92 - 22 - 100,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };

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
            // Show the actual folder in use — the saved override if any, otherwise the auto-detected
            // path — rather than an empty box, so the user can see and correct it.
            _packagesPath.Text = PackagePathResolver.Resolve(_original.InstalledPackagesPathOverride, null) ?? string.Empty;
            _startWithWindows.Checked = _original.StartWithWindows;
            _verbose.Checked = _original.Verbose;
            _appearance = _original.Appearance;
            StyleThemeButtons();
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
            // airports.csv path is no longer exposed in the UI; it stays whatever it was (blank =
            // the copy beside the app).
            updated.StartWithWindows = _startWithWindows.Checked;
            updated.Verbose = _verbose.Checked;
            updated.Appearance = _appearance;

            bool rescan = !string.Equals(
                updated.InstalledPackagesPathOverride, _original.InstalledPackagesPathOverride, StringComparison.OrdinalIgnoreCase);

            updated.Save(_iniPath);
            StartupRegistry.Apply(updated.StartWithWindows, null);
            // Activate the chosen theme now; the parent window re-skins itself when this dialog closes.
            Theme.Apply(updated.Appearance);
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
