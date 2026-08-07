using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MsfsAirportPreloader
{
    /// <summary>
    /// A flat button with rounded corners, matching the design's 5 px radius. Still a Button so it
    /// keeps DialogResult / AcceptButton behaviour; it just owner-paints its own rounded surface.
    /// </summary>
    internal sealed class RoundedButton : Button
    {
        public Color BorderColor { get; set; } = Theme.InputBorder;
        public Color HoverColor { get; set; } = Color.Empty; // Empty = no hover change
        public int CornerRadius { get; set; } = 5;

        private bool _hovered;

        public RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            // Paint the corners with the parent's colour so the rounding reads as transparent.
            using (var backdrop = new SolidBrush(Parent?.BackColor ?? BackColor))
            {
                g.FillRectangle(backdrop, ClientRectangle);
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill = _hovered && HoverColor != Color.Empty ? HoverColor : BackColor;
            Theme.FillRoundedRect(g, rect, CornerRadius, fill);
            if (BorderColor != Color.Empty)
            {
                Theme.DrawRoundedBorder(g, rect, CornerRadius, BorderColor);
            }

            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Dark colour table for the tray context menu.</summary>
    internal sealed class DarkMenuColors : System.Windows.Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Raised;
        public override Color ImageMarginGradientBegin => Theme.Raised;
        public override Color ImageMarginGradientMiddle => Theme.Raised;
        public override Color ImageMarginGradientEnd => Theme.Raised;
        public override Color MenuItemSelected => Theme.RaisedHover;
        public override Color MenuItemSelectedGradientBegin => Theme.RaisedHover;
        public override Color MenuItemSelectedGradientEnd => Theme.RaisedHover;
        public override Color MenuItemBorder => Theme.Border;
        public override Color MenuBorder => Theme.Border;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color CheckBackground => Theme.RaisedHover;
        public override Color CheckSelectedBackground => Theme.Accent;
    }

    /// <summary>Renders the tray menu dark, honouring any per-item ForeColor (e.g. the readout).</summary>
    internal sealed class DarkMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.ForeColor != System.Drawing.SystemColors.ControlText)
            {
                e.TextColor = e.Item.ForeColor; // header labels set their own colour
            }
            else
            {
                e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDim;
            }

            base.OnRenderItemText(e);
        }
    }

    /// <summary>A pill toggle in the RAMP style — accent when on, sunken when off.</summary>
    internal sealed class ToggleSwitch : Control
    {
        private bool _checked;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(34, 19);
            Cursor = Cursors.Hand;
        }

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value)
                {
                    return;
                }

                _checked = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(0, 0, Width - 1, Height - 1);

            if (_checked)
            {
                Theme.FillRoundedRect(g, track, Height / 2f, Theme.Accent);
            }
            else
            {
                Theme.FillRoundedRect(g, track, Height / 2f, Color.FromArgb(0x1E, 0x2C, 0x38));
                Theme.DrawRoundedBorder(g, track, Height / 2f, Color.FromArgb(0x2C, 0x3E, 0x4F));
            }

            float knobSize = Height - 5;
            float knobX = _checked ? Width - knobSize - 2 : 2;
            using var knob = new SolidBrush(_checked ? Theme.Sunken : Theme.TextDim);
            g.FillEllipse(knob, knobX, 2.5f, knobSize, knobSize);
        }
    }

    /// <summary>A draggable horizontal slider with an accent fill — the RAM budget control.</summary>
    internal sealed class Slider : Control
    {
        private double _minimum = 1;
        private double _maximum = 32;
        private double _value = 4;
        private bool _dragging;

        public Slider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            Height = 22;
            Cursor = Cursors.Hand;
        }

        public event EventHandler ValueChanged;

        public double Minimum { get => _minimum; set { _minimum = value; Invalidate(); } }
        public double Maximum { get => _maximum; set { _maximum = value; Invalidate(); } }

        public double Value
        {
            get => _value;
            set
            {
                double clamped = Math.Max(_minimum, Math.Min(_maximum, value));
                if (Math.Abs(clamped - _value) < 0.0001)
                {
                    return;
                }

                _value = clamped;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private const float ThumbSize = 14f;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _dragging = true;
            SetValueFromX(e.X);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                SetValueFromX(e.X);
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            base.OnMouseUp(e);
        }

        private void SetValueFromX(int x)
        {
            float usable = Width - ThumbSize;
            float fraction = usable <= 0 ? 0 : (x - ThumbSize / 2f) / usable;
            fraction = Math.Max(0f, Math.Min(1f, fraction));
            Value = Math.Round(_minimum + fraction * (_maximum - _minimum));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float centerY = Height / 2f;
            var track = new RectangleF(ThumbSize / 2f, centerY - 3f, Width - ThumbSize, 6f);
            Theme.FillRoundedRect(g, track, 3f, Theme.InputBg);

            float fraction = (_maximum - _minimum) <= 0 ? 0 : (float)((_value - _minimum) / (_maximum - _minimum));
            float handleX = track.X + fraction * track.Width;
            var fill = new RectangleF(track.X, track.Y, handleX - track.X, track.Height);
            Theme.FillRoundedRect(g, fill, 3f, Theme.Accent);

            using var knob = new SolidBrush(Theme.Text);
            using var ring = new Pen(Theme.Window, 2f);
            g.FillEllipse(knob, handleX - ThumbSize / 2f, centerY - ThumbSize / 2f, ThumbSize, ThumbSize);
            g.DrawEllipse(ring, handleX - ThumbSize / 2f, centerY - ThumbSize / 2f, ThumbSize, ThumbSize);
        }
    }
}
