using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MsfsAirportPreloader
{
    /// <summary>A pill toggle in the Apron style — accent when on, sunken when off.</summary>
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

        private const float Handle = 14f;

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
            float usable = Width - Handle;
            float fraction = usable <= 0 ? 0 : (x - Handle / 2f) / usable;
            fraction = Math.Max(0f, Math.Min(1f, fraction));
            Value = Math.Round(_minimum + fraction * (_maximum - _minimum));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float centerY = Height / 2f;
            var track = new RectangleF(Handle / 2f, centerY - 3f, Width - Handle, 6f);
            Theme.FillRoundedRect(g, track, 3f, Theme.InputBg);

            float fraction = (_maximum - _minimum) <= 0 ? 0 : (float)((_value - _minimum) / (_maximum - _minimum));
            float handleX = track.X + fraction * track.Width;
            var fill = new RectangleF(track.X, track.Y, handleX - track.X, track.Height);
            Theme.FillRoundedRect(g, fill, 3f, Theme.Accent);

            using var knob = new SolidBrush(Theme.Text);
            using var ring = new Pen(Theme.Window, 2f);
            g.FillEllipse(knob, handleX - Handle / 2f, centerY - Handle / 2f, Handle, Handle);
            g.DrawEllipse(ring, handleX - Handle / 2f, centerY - Handle / 2f, Handle, Handle);
        }
    }
}
