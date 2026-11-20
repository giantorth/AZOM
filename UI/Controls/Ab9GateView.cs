using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MozaPlugin.Devices;

namespace MozaControls
{
    /// <summary>
    /// AB9 / AB6 shift gate for the selected layout, styled after PitHouse's
    /// layout icons, with the engaged gear highlighted. Poll() supplies the
    /// layout, whether the firmware is 1.1.6.2+, the engaged gear
    /// (Ab9Gear.Reverse / Neutral / 1..8) and the
    /// sequential up/down state.
    /// </summary>
    public class Ab9GateView : FrameworkElement
    {
        public Func<(Ab9Mode mode, bool revisedFw, int gear, bool up, bool down)>? Poll { get; set; }

        // Geometry in units of the column pitch.
        private const double RowSpan = 2.4;   // top knob centre → bottom knob centre
        private const double KnobR = 0.32;
        private const double ActiveR = 0.38;
        private const double Pad = 0.15;

        private static readonly Color Gate = Color.FromRgb(0xC8, 0xCC, 0xD2);
        private static readonly Color Knob = Color.FromRgb(0x1E, 0x22, 0x29);
        private static readonly Color Rim = Color.FromRgb(0xE6, 0xE8, 0xEB);
        private static readonly Color Reverse = Color.FromRgb(0xE8, 0x26, 0x4A);
        private static readonly Color Active = Color.FromRgb(0x00, 0xE5, 0xFF);

        private readonly Brush _knob, _reverse, _active, _glow, _label, _activeLabel;
        private readonly Typeface _face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        private bool _hooked;
        private (Ab9Mode mode, bool revisedFw, int gear, bool up, bool down)? _state;

        public Ab9GateView()
        {
            _knob = Frozen(Knob);
            _reverse = Frozen(Reverse);
            _active = Frozen(Active);
            _glow = Frozen(Color.FromArgb(0x66, Active.R, Active.G, Active.B));
            _label = Frozen(Colors.White);
            _activeLabel = Frozen(Knob);
            Loaded += (_, __) => Hook();
            Unloaded += (_, __) => Unhook();
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private void Hook() { if (!_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; } }
        private void Unhook() { if (_hooked) { CompositionTarget.Rendering -= OnFrame; _hooked = false; } }

        private void OnFrame(object? sender, EventArgs e)
        {
            if (!IsVisible || Poll == null) return;
            var s = Poll();
            if (_state == s) return;
            _state = s;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0 || _state == null) return;
            var (mode, revisedFw, gear, up, down) = _state.Value;

            if (mode == Ab9Mode.Sequential)
            {
                RenderSequential(dc, w, h, up, down);
                return;
            }

            var slots = Ab9GateLayouts.Get(mode, revisedFw);
            if (slots == null) return;

            int cols = 0;
            foreach (var sl in slots) cols = Math.Max(cols, sl.Column + 1);
            double extent = 2 * (ActiveR + Pad);
            double pitch = Math.Min(w / (cols - 1 + extent), h / (RowSpan + extent));
            double x0 = (w - (cols - 1) * pitch) / 2;
            double yMid = h / 2;
            double yTop = yMid - RowSpan / 2 * pitch, yBot = yMid + RowSpan / 2 * pitch;

            var gatePen = new Pen(new SolidColorBrush(Gate), Math.Max(2, pitch * 0.08));
            gatePen.Freeze();
            dc.DrawLine(gatePen, new Point(x0, yMid), new Point(x0 + (cols - 1) * pitch, yMid));
            foreach (var sl in slots)
            {
                double x = x0 + sl.Column * pitch;
                dc.DrawLine(gatePen, new Point(x, yMid), new Point(x, sl.Top ? yTop : yBot));
            }

            var rimPen = new Pen(new SolidColorBrush(Rim), Math.Max(1.5, pitch * 0.06));
            rimPen.Freeze();
            foreach (var sl in slots)
            {
                var c = new Point(x0 + sl.Column * pitch, sl.Top ? yTop : yBot);
                string text = sl.Gear == Ab9Gear.Reverse ? "R" : sl.Gear.ToString(CultureInfo.InvariantCulture);
                DrawKnob(dc, c, pitch, text, sl.Gear == Ab9Gear.Reverse ? _reverse : _knob, rimPen, sl.Gear == gear);
            }

            if (gear == Ab9Gear.Neutral)
                dc.DrawEllipse(_active, null, new Point(x0 + (cols - 1) * pitch / 2, yMid), pitch * 0.14, pitch * 0.14);
        }

        // Single rail, D at the top and U at the bottom, as in PitHouse's icon.
        private void RenderSequential(DrawingContext dc, double w, double h, bool up, bool down)
        {
            double extent = 2 * (ActiveR + Pad);
            double pitch = Math.Min(w / extent, h / (RowSpan + extent));
            double x = w / 2, yMid = h / 2;
            double yTop = yMid - RowSpan / 2 * pitch, yBot = yMid + RowSpan / 2 * pitch;

            var gatePen = new Pen(new SolidColorBrush(Gate), Math.Max(2, pitch * 0.08));
            gatePen.Freeze();
            var rimPen = new Pen(new SolidColorBrush(Rim), Math.Max(1.5, pitch * 0.06));
            rimPen.Freeze();
            dc.DrawLine(gatePen, new Point(x, yTop), new Point(x, yBot));
            DrawKnob(dc, new Point(x, yTop), pitch, "D", _knob, rimPen, down);
            DrawKnob(dc, new Point(x, yBot), pitch, "U", _knob, rimPen, up);
        }

        private void DrawKnob(DrawingContext dc, Point c, double pitch, string text, Brush fill, Pen rim, bool active)
        {
            double r = (active ? ActiveR : KnobR) * pitch;
            if (active)
            {
                dc.DrawEllipse(_glow, null, c, r + pitch * 0.08, r + pitch * 0.08);
                dc.DrawEllipse(_active, null, c, r, r);
            }
            else
            {
                dc.DrawEllipse(fill, rim, c, r, r);
            }

            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _face, r * 1.1, active ? _activeLabel : _label, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
        }
    }
}
