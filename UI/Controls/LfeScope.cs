using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MozaControls
{
    /// <summary>
    /// Time-domain visualizer for the 3 wheelbase-LFE slots, one lane each. Rather
    /// than synthesising the raw carrier (unresolvable at high Hz over a multi-second
    /// window), each slot is drawn as a RIBBON: vertical position = carrier frequency
    /// (0..200 Hz up the lane), thickness = amplitude (intensity × envelope). A steady
    /// tone is a flat ribbon, a rev is a rising one, a strong effect is thick, an idle
    /// slot collapses to a thin line at the floor. Poll() supplies live (freq, amp).
    /// </summary>
    public class LfeScope : FrameworkElement
    {
        public Func<(double freq, double amp)[]>? Poll { get; set; }

        private const double WindowSec = 8.0;
        private const double MaxHz = 200.0;
        private static readonly Color[] SlotColors =
        {
            Color.FromRgb(0x3C, 0xE0, 0xE0),   // slot 1 — cyan
            Color.FromRgb(0xFF, 0xB8, 0x4D),   // slot 2 — amber
            Color.FromRgb(0x8B, 0xD8, 0x66),   // slot 3 — green
        };

        private struct Sample { public double T, Freq, Amp; }

        /// <summary>One lane's sample window: a ring buffer that grows to fit
        /// <see cref="WindowSec"/> at the display's frame rate, so trimming the
        /// oldest samples is O(1) instead of a list shift.</summary>
        private sealed class Lane
        {
            private Sample[] _ring = new Sample[1024];
            private int _head, _count;
            // Time of the newest sample whose value differs from the one before it.
            private double _lastChangeT = double.NegativeInfinity;
            // Set once samples have aged out, i.e. the window is fully covered.
            private bool _full;

            public int Count => _count;
            public Sample this[int k] => _ring[(_head + k) % _ring.Length];
            public Sample Newest => this[_count - 1];

            /// <summary>Full window holding one unchanging value — it renders the same
            /// image every frame.</summary>
            public bool Static => _full && _count > 0 && _lastChangeT <= _ring[_head].T;

            public void Clear()
            {
                _head = 0;
                _count = 0;
                _full = false;
                _lastChangeT = double.NegativeInfinity;
            }

            public void Add(double t, double freq, double amp)
            {
                if (_count == 0 || Newest.Freq != freq || Newest.Amp != amp) _lastChangeT = t;
                if (_count == _ring.Length) Grow();
                _ring[(_head + _count) % _ring.Length] = new Sample { T = t, Freq = freq, Amp = amp };
                _count++;
            }

            public void DropBefore(double cutoff)
            {
                while (_count > 0 && _ring[_head].T < cutoff)
                {
                    _head = (_head + 1) % _ring.Length;
                    _count--;
                    _full = true;
                }
            }

            private void Grow()
            {
                var next = new Sample[_ring.Length * 2];
                for (int k = 0; k < _count; k++) next[k] = this[k];
                _ring = next;
                _head = 0;
            }
        }

        private readonly Lane[] _lanes = { new Lane(), new Lane(), new Lane() };
        // Outline scratch, reused across lanes and frames.
        private readonly List<Point> _top = new List<Point>();
        private readonly List<Point> _bot = new List<Point>();
        private readonly Brush[] _fill = new Brush[3];
        private readonly Pen[] _stroke = new Pen[3];
        private readonly Brush[] _label = new Brush[3];
        private readonly Typeface _face = new Typeface("Segoe UI");
        private readonly Brush _bg;
        private readonly Pen _grid;
        private readonly Pen _sep;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _hooked;
        // The current static image has been drawn; further frames are skipped until
        // something changes.
        private bool _staticDrawn;

        public LfeScope()
        {
            for (int i = 0; i < 3; i++)
            {
                var c = SlotColors[i];
                _fill[i] = new SolidColorBrush(Color.FromArgb(0x9A, c.R, c.G, c.B)); _fill[i].Freeze();
                _stroke[i] = new Pen(new SolidColorBrush(c), 1.0); _stroke[i].Freeze();
                _label[i] = new SolidColorBrush(c); _label[i].Freeze();
            }
            _bg = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)); _bg.Freeze();
            _grid = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1); _grid.Freeze();
            _sep = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1); _sep.Freeze();
            Loaded += (_, __) => Hook();
            Unloaded += (_, __) => Unhook();
        }

        private void Hook() { if (!_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; } }
        private void Unhook() { if (_hooked) { CompositionTarget.Rendering -= OnFrame; _hooked = false; } }

        private void OnFrame(object? sender, EventArgs e)
        {
            if (!IsVisible || Poll == null || ActualWidth <= 0) return;
            double t = _clock.Elapsed.TotalSeconds;
            var vals = Poll();
            bool allStatic = true;
            for (int i = 0; i < 3; i++)
            {
                var lane = _lanes[i];
                double amp = i < vals.Length ? vals[i].amp : 0;
                double freq = i < vals.Length ? vals[i].freq : 0;
                if (lane.Count > 0 && t - lane.Newest.T > 0.3) lane.Clear();   // hidden/hitched → restart
                lane.Add(t, freq, amp);
                lane.DropBefore(t - WindowSec);
                allStatic &= lane.Static;
            }
            // A full, unchanging window only drifts its left edge by under a frame.
            if (allStatic && _staticDrawn) return;
            _staticDrawn = allStatic;
            InvalidateVisual();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            dc.DrawRectangle(_bg, null, new Rect(0, 0, w, h));

            double laneH = h / 3.0;
            double t1 = _clock.Elapsed.TotalSeconds, t0 = t1 - WindowSec;
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (int i = 0; i < 3; i++)
            {
                double laneTop = laneH * i, laneBot = laneTop + laneH;
                double floor = laneBot - laneH * 0.08;              // freq 0 sits here
                double span = laneH * 0.84;                         // freq 0..MaxHz spans this
                double maxHalf = laneH * 0.30;                      // full-amplitude half-thickness

                if (i > 0) dc.DrawLine(_sep, new Point(0, laneTop), new Point(w, laneTop));
                dc.DrawLine(_grid, new Point(0, floor), new Point(w, floor));   // freq-0 baseline

                var lane = _lanes[i];
                if (lane.Count >= 2)
                {
                    _top.Clear();
                    _bot.Clear();
                    for (int k = 0; k < lane.Count; k++)
                    {
                        var s = lane[k];
                        double x = (s.T - t0) / WindowSec * w;
                        double fN = Math.Max(0, Math.Min(1, s.Freq / MaxHz));
                        double cy = floor - fN * span;
                        double half = s.Amp * maxHalf;
                        _top.Add(new Point(x, Math.Max(laneTop + 1, cy - half)));
                        _bot.Add(new Point(x, Math.Min(laneBot - 1, cy + half)));
                    }
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(_top[0], true, true);
                        g.PolyLineTo(_top, true, false);
                        for (int k = _bot.Count - 1; k >= 0; k--) g.LineTo(_bot[k], true, false);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(_fill[i], _stroke[i], geo);

                    // current-frequency readout at the right of the lane
                    var last = lane.Newest;
                    if (last.Amp > 0.01)
                    {
                        var txt = new FormattedText(
                            ((int)Math.Round(last.Freq)).ToString(CultureInfo.InvariantCulture) + " Hz",
                            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, 10.5,
                            _label[i], pixelsPerDip);
                        dc.DrawText(txt, new Point(w - txt.Width - 4, laneTop + 3));
                    }
                }
            }
        }
    }
}
