using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MozaControls
{
    /// <summary>
    /// 270° tachometer-style steering arc. Sweeps from top-centre (0°) outward in
    /// either direction depending on sign of <see cref="Angle"/>. Bound to a single
    /// double; updates the arc Path geometry on each value change.
    /// </summary>
    public class SteeringArc : Control
    {
        static SteeringArc()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(SteeringArc),
                new FrameworkPropertyMetadata(typeof(SteeringArc)));
        }

        public static readonly DependencyProperty AngleProperty =
            DependencyProperty.Register(nameof(Angle), typeof(double), typeof(SteeringArc),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    OnVisualChanged));

        public double Angle
        {
            get => (double)GetValue(AngleProperty);
            set => SetValue(AngleProperty, value);
        }

        public static readonly DependencyProperty MaxAngleProperty =
            DependencyProperty.Register(nameof(MaxAngle), typeof(double), typeof(SteeringArc),
                new FrameworkPropertyMetadata(540.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    OnVisualChanged));

        public double MaxAngle
        {
            get => (double)GetValue(MaxAngleProperty);
            set => SetValue(MaxAngleProperty, value);
        }

        private static readonly DependencyPropertyKey AngleTextKey =
            DependencyProperty.RegisterReadOnly(nameof(AngleText), typeof(string), typeof(SteeringArc),
                new PropertyMetadata("0°"));

        public static readonly DependencyProperty AngleTextProperty = AngleTextKey.DependencyProperty;

        public string AngleText => (string)GetValue(AngleTextProperty);

        private static readonly DependencyPropertyKey ArcGeometryKey =
            DependencyProperty.RegisterReadOnly(nameof(ArcGeometry), typeof(Geometry), typeof(SteeringArc),
                new PropertyMetadata(null));

        public static readonly DependencyProperty ArcGeometryProperty = ArcGeometryKey.DependencyProperty;

        public Geometry? ArcGeometry => (Geometry?)GetValue(ArcGeometryProperty);

        private static readonly DependencyPropertyKey DotXKey =
            DependencyProperty.RegisterReadOnly(nameof(DotX), typeof(double), typeof(SteeringArc),
                new PropertyMetadata(0.0));
        public static readonly DependencyProperty DotXProperty = DotXKey.DependencyProperty;
        public double DotX => (double)GetValue(DotXProperty);

        private static readonly DependencyPropertyKey DotYKey =
            DependencyProperty.RegisterReadOnly(nameof(DotY), typeof(double), typeof(SteeringArc),
                new PropertyMetadata(0.0));
        public static readonly DependencyProperty DotYProperty = DotYKey.DependencyProperty;
        public double DotY => (double)GetValue(DotYProperty);

        private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((SteeringArc)d).Recompute();

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            Recompute();
        }

        // Arc spans 270° total: 135° (lower-left) → 270° (top centre) → 405° (lower-right).
        // Pivot at 270° represents 0° steering; positive angle sweeps clockwise.
        private const double Cx = 110;
        private const double Cy = 110;
        private const double R = 92;
        private const double PivotDeg = 270;
        private const double HalfSpan = 135;

        private void Recompute()
        {
            double max = Math.Max(1, MaxAngle);
            double a = Math.Max(-max, Math.Min(max, Angle));
            double targetDeg = PivotDeg + (a / max) * HalfSpan;

            double a1 = PivotDeg * Math.PI / 180.0;
            double a2 = targetDeg * Math.PI / 180.0;
            double x1 = Cx + R * Math.Cos(a1);
            double y1 = Cy + R * Math.Sin(a1);
            double x2 = Cx + R * Math.Cos(a2);
            double y2 = Cy + R * Math.Sin(a2);

            bool largeArc = Math.Abs(targetDeg - PivotDeg) > 180;
            bool sweepCw = targetDeg > PivotDeg;

            var fig = new PathFigure { StartPoint = new Point(x1, y1), IsClosed = false, IsFilled = false };
            fig.Segments.Add(new ArcSegment(
                new Point(x2, y2),
                new Size(R, R),
                rotationAngle: 0,
                isLargeArc: largeArc,
                sweepDirection: sweepCw ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                isStroked: true));
            var geom = new PathGeometry();
            geom.Figures.Add(fig);
            geom.Freeze();

            SetValue(ArcGeometryKey, geom);
            SetValue(DotXKey, x2);
            SetValue(DotYKey, y2);

            string sign = a > 0 ? "+" : "";
            SetValue(AngleTextKey, $"{sign}{(int)Math.Round(a)}°");
        }
    }
}
