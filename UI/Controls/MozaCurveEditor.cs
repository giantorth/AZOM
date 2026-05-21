using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MozaControls
{
    /// <summary>
    /// Curve editor for the 5-point output curves on the Base / Handbrake /
    /// Pedals tabs. Drag the 5 nodes (Y axis 0–100) to shape response.
    /// Renders a cubic Catmull-Rom spline through fixed X = 20/40/60/80/100.
    ///
    /// The editor's Y1..Y5 DPs are intended to bind two-way to the underlying
    /// FfbCurveYNSlider.Value (etc.) so existing slider ValueChanged handlers
    /// continue to fire and MozaProfile persistence is unchanged.
    /// </summary>
    public class MozaCurveEditor : Control
    {
        static MozaCurveEditor()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(typeof(MozaCurveEditor)));
        }

        // -------- Y values (one per node) --------
        public static readonly DependencyProperty Y1Property = RegisterY(nameof(Y1), 20);
        public static readonly DependencyProperty Y2Property = RegisterY(nameof(Y2), 40);
        public static readonly DependencyProperty Y3Property = RegisterY(nameof(Y3), 60);
        public static readonly DependencyProperty Y4Property = RegisterY(nameof(Y4), 80);
        public static readonly DependencyProperty Y5Property = RegisterY(nameof(Y5), 100);

        private static DependencyProperty RegisterY(string name, double dflt)
            => DependencyProperty.Register(name, typeof(double), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(dflt,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));

        public double Y1 { get => (double)GetValue(Y1Property); set => SetValue(Y1Property, value); }
        public double Y2 { get => (double)GetValue(Y2Property); set => SetValue(Y2Property, value); }
        public double Y3 { get => (double)GetValue(Y3Property); set => SetValue(Y3Property, value); }
        public double Y4 { get => (double)GetValue(Y4Property); set => SetValue(Y4Property, value); }
        public double Y5 { get => (double)GetValue(Y5Property); set => SetValue(Y5Property, value); }

        public static readonly DependencyProperty AccentBrushProperty =
            DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(MozaCurveEditor),
                new PropertyMetadata(null));
        public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

        // -------- Read-only geometry / node positions surfaced to template --------

        private static readonly DependencyPropertyKey CurveGeometryKey =
            DependencyProperty.RegisterReadOnly(nameof(CurveGeometry), typeof(Geometry),
                typeof(MozaCurveEditor), new PropertyMetadata(null));
        public static readonly DependencyProperty CurveGeometryProperty = CurveGeometryKey.DependencyProperty;
        public Geometry? CurveGeometry => (Geometry?)GetValue(CurveGeometryProperty);

        private static readonly DependencyPropertyKey GridGeometryKey =
            DependencyProperty.RegisterReadOnly(nameof(GridGeometry), typeof(Geometry),
                typeof(MozaCurveEditor), new PropertyMetadata(null));
        public static readonly DependencyProperty GridGeometryProperty = GridGeometryKey.DependencyProperty;
        public Geometry? GridGeometry => (Geometry?)GetValue(GridGeometryProperty);

        // 5 node centre positions exposed individually for Canvas-positioned ellipses
        private static readonly DependencyPropertyKey[] NodeXKeys = new DependencyPropertyKey[5];
        private static readonly DependencyPropertyKey[] NodeYKeys = new DependencyPropertyKey[5];
        public static readonly DependencyProperty[] NodeXProperties = new DependencyProperty[5];
        public static readonly DependencyProperty[] NodeYProperties = new DependencyProperty[5];

        static void RegisterNodeProps()
        {
            for (int i = 0; i < 5; i++)
            {
                NodeXKeys[i] = DependencyProperty.RegisterReadOnly($"Node{i + 1}X", typeof(double),
                    typeof(MozaCurveEditor), new PropertyMetadata(0.0));
                NodeXProperties[i] = NodeXKeys[i].DependencyProperty;
                NodeYKeys[i] = DependencyProperty.RegisterReadOnly($"Node{i + 1}Y", typeof(double),
                    typeof(MozaCurveEditor), new PropertyMetadata(0.0));
                NodeYProperties[i] = NodeYKeys[i].DependencyProperty;
            }
        }

        // Static initializer used so the static ctor + DefaultStyleKey + node prop registration can be ordered
        private static readonly bool _staticInit = StaticInit();
        private static bool StaticInit()
        {
            RegisterNodeProps();
            return true;
        }

        // Convenience accessors for the template via Bind path strings
        public double Node1X => (double)GetValue(NodeXProperties[0]);
        public double Node1Y => (double)GetValue(NodeYProperties[0]);
        public double Node2X => (double)GetValue(NodeXProperties[1]);
        public double Node2Y => (double)GetValue(NodeYProperties[1]);
        public double Node3X => (double)GetValue(NodeXProperties[2]);
        public double Node3Y => (double)GetValue(NodeYProperties[2]);
        public double Node4X => (double)GetValue(NodeXProperties[3]);
        public double Node4Y => (double)GetValue(NodeYProperties[3]);
        public double Node5X => (double)GetValue(NodeXProperties[4]);
        public double Node5Y => (double)GetValue(NodeYProperties[4]);

        // -------- Layout constants --------
        private const double PadLeft = 36;
        private const double PadRight = 14;
        private const double PadTop = 14;
        private const double PadBottom = 32;
        private static readonly double[] Xs = { 20, 40, 60, 80, 100 };

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            HookCanvas();
            Recompute();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            Recompute();
        }

        // -------- Drag state --------
        private int _dragNode = -1;
        private Canvas? _canvas;

        private void HookCanvas()
        {
            _canvas = GetTemplateChild("PART_Canvas") as Canvas;
            if (_canvas != null)
            {
                _canvas.MouseLeftButtonDown += OnMouseDown;
                _canvas.MouseMove += OnMouseMove;
                _canvas.MouseLeftButtonUp += OnMouseUp;
                _canvas.LostMouseCapture += (_, __) => _dragNode = -1;
            }
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_canvas == null) return;
            var p = e.GetPosition(_canvas);
            _dragNode = FindClosestNode(p);
            if (_dragNode >= 0)
            {
                _canvas.CaptureMouse();
                ApplyDrag(p);
                e.Handled = true;
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragNode < 0 || _canvas == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragNode = -1; _canvas.ReleaseMouseCapture(); return; }
            ApplyDrag(e.GetPosition(_canvas));
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_canvas != null && _canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
            _dragNode = -1;
        }

        private int FindClosestNode(Point p)
        {
            int best = -1;
            double bestDist = 256; // require within 16 px
            for (int i = 0; i < 5; i++)
            {
                double dx = p.X - (double)GetValue(NodeXProperties[i]);
                double dy = p.Y - (double)GetValue(NodeYProperties[i]);
                double d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        private void ApplyDrag(Point p)
        {
            double w = _canvas?.ActualWidth ?? ActualWidth;
            double h = _canvas?.ActualHeight ?? ActualHeight;
            double plotH = Math.Max(1, h - PadTop - PadBottom);
            // Invert: 0% at bottom, 100% at top.
            double y01 = (h - PadBottom - p.Y) / plotH;
            double v = Math.Max(0, Math.Min(100, Math.Round(y01 * 100)));
            SetY(_dragNode, v);
        }

        private void SetY(int i, double v)
        {
            switch (i)
            {
                case 0: Y1 = v; break;
                case 1: Y2 = v; break;
                case 2: Y3 = v; break;
                case 3: Y4 = v; break;
                case 4: Y5 = v; break;
            }
        }

        // -------- Geometry recomputation --------

        private void Recompute()
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double plotW = Math.Max(1, w - PadLeft - PadRight);
            double plotH = Math.Max(1, h - PadTop - PadBottom);

            // Node positions in canvas coordinates
            double[] ys = { Y1, Y2, Y3, Y4, Y5 };
            var pts = new Point[5];
            for (int i = 0; i < 5; i++)
            {
                double x = PadLeft + Xs[i] / 100.0 * plotW;
                double y = PadTop + (1 - Math.Max(0, Math.Min(100, ys[i])) / 100.0) * plotH;
                pts[i] = new Point(x, y);
                SetValue(NodeXKeys[i], x - 7); // ellipse top-left offset (centre at x)
                SetValue(NodeYKeys[i], y - 7);
            }

            // Anchor at origin (0,0) so curve starts at lower-left
            var startPt = new Point(PadLeft, PadTop + plotH);
            var allPts = new Point[7];
            allPts[0] = startPt;
            for (int i = 0; i < 5; i++) allPts[i + 1] = pts[i];
            allPts[6] = pts[4]; // duplicate endpoint for tangent

            var fig = new PathFigure { StartPoint = startPt, IsClosed = false, IsFilled = false };
            for (int i = 0; i < allPts.Length - 1; i++)
            {
                Point p0 = i == 0 ? allPts[0] : allPts[i - 1];
                Point p1 = allPts[i];
                Point p2 = allPts[i + 1];
                Point p3 = i + 2 >= allPts.Length ? allPts[i + 1] : allPts[i + 2];
                // Catmull-Rom → cubic Bezier conversion (tension=0.5)
                Point c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                Point c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
            }
            var geom = new PathGeometry();
            geom.Figures.Add(fig);
            geom.Freeze();
            SetValue(CurveGeometryKey, geom);

            // Grid: dashed lines at 20/40/60/80/100 on both axes
            var grid = new GeometryGroup();
            for (int i = 1; i <= 4; i++)
            {
                double frac = i / 5.0;
                grid.Children.Add(new LineGeometry(
                    new Point(PadLeft, PadTop + frac * plotH),
                    new Point(PadLeft + plotW, PadTop + frac * plotH)));
                grid.Children.Add(new LineGeometry(
                    new Point(PadLeft + frac * plotW, PadTop),
                    new Point(PadLeft + frac * plotW, PadTop + plotH)));
            }
            grid.Freeze();
            SetValue(GridGeometryKey, grid);
        }

    }
}
