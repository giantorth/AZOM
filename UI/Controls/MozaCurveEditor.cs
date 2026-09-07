using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MozaControls
{
    /// <summary>
    /// Draggable-node line graph. Used for the 5-point output curves on the
    /// Base / Handbrake / Pedals tabs, and (with the MozaEqualizerLineStyle
    /// variants in Themes/MozaTheme.xaml) for the 6/10-band FFB equalizer.
    ///
    /// Renders a cubic Catmull-Rom spline through fixed X positions. The
    /// Y1..Y10 DPs are intended to bind two-way to the underlying
    /// FfbCurveYNSlider.Value / EqNSlider.Value etc., so existing slider
    /// ValueChanged handlers continue to fire and MozaProfile persistence is
    /// unchanged.
    ///
    /// Configurations in use:
    ///   * 5-node curve (default): NodeCount=5, YMin=0, YMax=100, no reference
    ///     line, nodes at X=20/40/60/80/100% of plot width.
    ///   * 6-node EQ: NodeCount=6, YMin=0, YMax=400, ReferenceLineY=100, nodes
    ///     evenly spaced at column centres (1/12..11/12 of plot width).
    ///   * 10-node EQ: NodeCount=10, YMax=500, LastNodeYMax=100 (the 100 Hz
    ///     band keeps its 0-100% cap).
    /// </summary>
    public class MozaCurveEditor : Control
    {
        static MozaCurveEditor()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(typeof(MozaCurveEditor)));
        }

        // -------- Y values (one per node, up to 10) --------
        public static readonly DependencyProperty Y1Property = RegisterY(nameof(Y1), 20);
        public static readonly DependencyProperty Y2Property = RegisterY(nameof(Y2), 40);
        public static readonly DependencyProperty Y3Property = RegisterY(nameof(Y3), 60);
        public static readonly DependencyProperty Y4Property = RegisterY(nameof(Y4), 80);
        public static readonly DependencyProperty Y5Property = RegisterY(nameof(Y5), 100);
        public static readonly DependencyProperty Y6Property = RegisterY(nameof(Y6), 100);
        public static readonly DependencyProperty Y7Property = RegisterY(nameof(Y7), 100);
        public static readonly DependencyProperty Y8Property = RegisterY(nameof(Y8), 100);
        public static readonly DependencyProperty Y9Property = RegisterY(nameof(Y9), 100);
        public static readonly DependencyProperty Y10Property = RegisterY(nameof(Y10), 100);

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
        public double Y6 { get => (double)GetValue(Y6Property); set => SetValue(Y6Property, value); }
        public double Y7 { get => (double)GetValue(Y7Property); set => SetValue(Y7Property, value); }
        public double Y8 { get => (double)GetValue(Y8Property); set => SetValue(Y8Property, value); }
        public double Y9 { get => (double)GetValue(Y9Property); set => SetValue(Y9Property, value); }
        public double Y10 { get => (double)GetValue(Y10Property); set => SetValue(Y10Property, value); }

        // -------- X values (data-space 0..100, only meaningful when
        // AllowHorizontalDrag is true — 5-node curves default to the fixed
        // 20/40/60/80/100 breakpoints every other curve in this app uses;
        // the 6-node Sim Input Mapping curve overwrites X1-X6 from its own
        // seeding code (100/6 * k for k=1..6) immediately on load, so X6's
        // own DP default below is cosmetic. A fresh instance renders
        // identically to one driven by NodeXFractions until the user
        // actually drags a node sideways. --------
        public static readonly DependencyProperty X1Property = RegisterX(nameof(X1), 20);
        public static readonly DependencyProperty X2Property = RegisterX(nameof(X2), 40);
        public static readonly DependencyProperty X3Property = RegisterX(nameof(X3), 60);
        public static readonly DependencyProperty X4Property = RegisterX(nameof(X4), 80);
        public static readonly DependencyProperty X5Property = RegisterX(nameof(X5), 100);
        public static readonly DependencyProperty X6Property = RegisterX(nameof(X6), 600.0 / 6.0);

        private static DependencyProperty RegisterX(string name, double dflt)
            => DependencyProperty.Register(name, typeof(double), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(dflt,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));

        public double X1 { get => (double)GetValue(X1Property); set => SetValue(X1Property, value); }
        public double X2 { get => (double)GetValue(X2Property); set => SetValue(X2Property, value); }
        public double X3 { get => (double)GetValue(X3Property); set => SetValue(X3Property, value); }
        public double X4 { get => (double)GetValue(X4Property); set => SetValue(X4Property, value); }
        public double X5 { get => (double)GetValue(X5Property); set => SetValue(X5Property, value); }
        public double X6 { get => (double)GetValue(X6Property); set => SetValue(X6Property, value); }

        // When true, nodes can be dragged horizontally (within their
        // neighbours' bounds) as well as vertically — used only by the
        // Sim Input Mapping output curve, so a moved node means "100%
        // output is reached before 100% input" without needing a
        // (nonexistent) hardware X-breakpoint command. Off by default so
        // every other curve in the app (FFB, Handbrake, Pedals, Pedal Feel)
        // keeps its existing fixed-X behaviour unchanged.
        public static readonly DependencyProperty AllowHorizontalDragProperty =
            DependencyProperty.Register(nameof(AllowHorizontalDrag), typeof(bool), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public bool AllowHorizontalDrag { get => (bool)GetValue(AllowHorizontalDragProperty); set => SetValue(AllowHorizontalDragProperty, value); }

        // When true (with AllowHorizontalDrag), the LAST node is pinned in X and
        // can only move vertically — used by the wheelbase FFB output curve,
        // whose final point is fixed at input=100 (the hardware has x1..x4
        // commands but no x5). Off by default so the mBooster curve, which
        // resamples all five nodes host-side, keeps dragging its last node.
        public static readonly DependencyProperty LockLastNodeXProperty =
            DependencyProperty.Register(nameof(LockLastNodeX), typeof(bool), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public bool LockLastNodeX { get => (bool)GetValue(LockLastNodeXProperty); set => SetValue(LockLastNodeXProperty, value); }

        // When true (with AllowHorizontalDrag), only the FIRST and LAST nodes
        // may move horizontally — every node in between is Y-only. The first
        // node is additionally locked in Y (X-only movement), since its sole
        // role is to mark where the curve's usable input range begins; the
        // last node keeps moving on both axes. Dragging either endpoint
        // horizontally rescales all the in-between nodes' X in proportion to
        // their old position between the two (old) endpoints, so the curve's
        // shape (relative node spacing) is preserved rather than left behind.
        // Used only by the Sim Input Mapping curve (MBoosterCurveEditor) —
        // every other curve using AllowHorizontalDrag (e.g. the wheelbase FFB
        // output curve) keeps its existing per-node drag behaviour unchanged.
        public static readonly DependencyProperty EndpointsOnlyDraggableInXProperty =
            DependencyProperty.Register(nameof(EndpointsOnlyDraggableInX), typeof(bool), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public bool EndpointsOnlyDraggableInX { get => (bool)GetValue(EndpointsOnlyDraggableInXProperty); set => SetValue(EndpointsOnlyDraggableInXProperty, value); }

        // When true, a node's Y is ALSO clamped between its immediate
        // neighbours' current Y (index-adjacent, same convention as the
        // existing X neighbour-clamp below) — the first/last node clamp
        // against YMin/YMax instead. Used by the Pedal Feel curve, where
        // both axes are freely draggable (unlike Sim Input Mapping's
        // endpoint-only-X nodes) so nothing else stops a node from being
        // dragged past its neighbour's Y. Off by default so every other
        // curve keeps its existing unconstrained Y-drag behaviour.
        public static readonly DependencyProperty ClampYToAdjacentNodesProperty =
            DependencyProperty.Register(nameof(ClampYToAdjacentNodes), typeof(bool), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public bool ClampYToAdjacentNodes { get => (bool)GetValue(ClampYToAdjacentNodesProperty); set => SetValue(ClampYToAdjacentNodesProperty, value); }

        // Per-node Y cap for the LAST node only (NaN = disabled) — the 10-band
        // EQ's 100 Hz band stays 0-100% while every other band runs to YMax=500.
        public static readonly DependencyProperty LastNodeYMaxProperty =
            DependencyProperty.Register(nameof(LastNodeYMax), typeof(double), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public double LastNodeYMax { get => (double)GetValue(LastNodeYMaxProperty); set => SetValue(LastNodeYMaxProperty, value); }

        // -------- Configuration DPs --------

        public static readonly DependencyProperty NodeCountProperty =
            DependencyProperty.Register(nameof(NodeCount), typeof(int), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public int NodeCount { get => (int)GetValue(NodeCountProperty); set => SetValue(NodeCountProperty, value); }

        public static readonly DependencyProperty YMinProperty =
            DependencyProperty.Register(nameof(YMin), typeof(double), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public double YMin { get => (double)GetValue(YMinProperty); set => SetValue(YMinProperty, value); }

        public static readonly DependencyProperty YMaxProperty =
            DependencyProperty.Register(nameof(YMax), typeof(double), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public double YMax { get => (double)GetValue(YMaxProperty); set => SetValue(YMaxProperty, value); }

        // Use double.NaN as the "unset" sentinel so the dashed reference line
        // is only drawn when explicitly opted in (e.g. the 6-band EQ neutral
        // marker at 100%). WPF DP defaults don't play well with nullables.
        public static readonly DependencyProperty ReferenceLineYProperty =
            DependencyProperty.Register(nameof(ReferenceLineY), typeof(double), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public double ReferenceLineY { get => (double)GetValue(ReferenceLineYProperty); set => SetValue(ReferenceLineYProperty, value); }

        public static readonly DependencyProperty NodeXFractionsProperty =
            DependencyProperty.Register(nameof(NodeXFractions), typeof(string), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public string? NodeXFractions { get => (string?)GetValue(NodeXFractionsProperty); set => SetValue(NodeXFractionsProperty, value); }

        // When true (default — output-curve behaviour), the spline is anchored
        // at the plot's lower-left corner so the visual line implies a
        // (0,0) → first-node segment. When false (EQ behaviour), the spline
        // starts AT the first node and ends AT the last node with both
        // endpoints free-floating.
        public static readonly DependencyProperty AnchorAtOriginProperty =
            DependencyProperty.Register(nameof(AnchorAtOrigin), typeof(bool), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public bool AnchorAtOrigin { get => (bool)GetValue(AnchorAtOriginProperty); set => SetValue(AnchorAtOriginProperty, value); }

        // When true, the spline is ALSO anchored at the plot's upper-right
        // corner (data-space 100,100) as a real drawn point, symmetric to
        // AnchorAtOrigin's lower-left corner — so the visible line reaches
        // (100,100) even when the last draggable node doesn't sit exactly
        // there. Used by the Pedal Feel curve (MBoosterInputCurveEditor,
        // both axes now draggable): its domain is 0-100% of the Deadzone→Max
        // Force span on BOTH axes, so the curve should visually span that
        // whole square. Off by default — every other curve already ends at
        // its own last node/point with no separate corner anchor.
        public static readonly DependencyProperty AnchorAtTopRightProperty =
            DependencyProperty.Register(nameof(AnchorAtTopRight), typeof(bool), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public bool AnchorAtTopRight { get => (bool)GetValue(AnchorAtTopRightProperty); set => SetValue(AnchorAtTopRightProperty, value); }

        // Diagonal y=x reference line from plot lower-left to upper-right —
        // the "nominal" / linear response. Shown on output curves to make it
        // easy to read deviation. Off on the EQ where a y=x line is
        // meaningless.
        public static readonly DependencyProperty ShowIdentityLineProperty =
            DependencyProperty.Register(nameof(ShowIdentityLine), typeof(bool), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public bool ShowIdentityLine { get => (bool)GetValue(ShowIdentityLineProperty); set => SetValue(ShowIdentityLineProperty, value); }

        public static readonly DependencyProperty XLabelFractionsProperty =
            DependencyProperty.Register(nameof(XLabelFractions), typeof(string), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata("0.0,0.196,0.392,0.588,0.784,0.98", FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public string XLabelFractions { get => (string)GetValue(XLabelFractionsProperty); set => SetValue(XLabelFractionsProperty, value); }

        public static readonly DependencyProperty XAxisLabelsProperty =
            DependencyProperty.Register(nameof(XAxisLabels), typeof(string), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata("0,20,40,60,80,100", FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public string XAxisLabels { get => (string)GetValue(XAxisLabelsProperty); set => SetValue(XAxisLabelsProperty, value); }

        public static readonly DependencyProperty YAxisLabelsProperty =
            DependencyProperty.Register(nameof(YAxisLabels), typeof(string), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata("100,75,50,25,0", FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).Recompute()));
        public string YAxisLabels { get => (string)GetValue(YAxisLabelsProperty); set => SetValue(YAxisLabelsProperty, value); }

        public static readonly DependencyProperty AccentBrushProperty =
            DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(MozaCurveEditor),
                new PropertyMetadata(null));
        public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

        // Live position indicator — a dot drawn ON the spline at the given
        // data-space X (same domain as XAxisLabels, e.g. 0..100), showing
        // where the pedal currently is exactly like the position bar does,
        // plus what the curve currently outputs for that input. NaN (default)
        // hides it. The caller (SettingsControl) is responsible for pushing
        // live values in at the same cadence as the position bar.
        public static readonly DependencyProperty LiveXProperty =
            DependencyProperty.Register(nameof(LiveX), typeof(double), typeof(MozaCurveEditor),
                new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender,
                    (d, e) => ((MozaCurveEditor)d).OnLiveXChanged()));
        public double LiveX { get => (double)GetValue(LiveXProperty); set => SetValue(LiveXProperty, value); }

        // The spline/grid/labels only depend on the nodes and the size; the live
        // marker (pushed at 30 Hz while a pedal moves) reuses what the last
        // Recompute() built instead of rebuilding the whole editor per sample.
        private (Point p1, Point c1, Point c2, Point p2)[]? _liveSegments;
        private Point[]? _liveNodePts;
        private double _livePlotW;
        private double _liveAxisBottomY;

        private void OnLiveXChanged()
        {
            if (_liveSegments == null || _liveNodePts == null) { Recompute(); return; }
            UpdateLiveMarker(_liveSegments, _liveNodePts, _livePlotW, _liveAxisBottomY);
        }

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

        private static readonly DependencyPropertyKey ReferenceLineGeometryKey =
            DependencyProperty.RegisterReadOnly(nameof(ReferenceLineGeometry), typeof(Geometry),
                typeof(MozaCurveEditor), new PropertyMetadata(null));
        public static readonly DependencyProperty ReferenceLineGeometryProperty = ReferenceLineGeometryKey.DependencyProperty;
        public Geometry? ReferenceLineGeometry => (Geometry?)GetValue(ReferenceLineGeometryProperty);

        private static readonly DependencyPropertyKey IdentityLineGeometryKey =
            DependencyProperty.RegisterReadOnly(nameof(IdentityLineGeometry), typeof(Geometry),
                typeof(MozaCurveEditor), new PropertyMetadata(null));
        public static readonly DependencyProperty IdentityLineGeometryProperty = IdentityLineGeometryKey.DependencyProperty;
        public Geometry? IdentityLineGeometry => (Geometry?)GetValue(IdentityLineGeometryProperty);

        private static readonly DependencyPropertyKey LiveGuideLineGeometryKey =
            DependencyProperty.RegisterReadOnly(nameof(LiveGuideLineGeometry), typeof(Geometry),
                typeof(MozaCurveEditor), new PropertyMetadata(null));
        public static readonly DependencyProperty LiveGuideLineGeometryProperty = LiveGuideLineGeometryKey.DependencyProperty;
        public Geometry? LiveGuideLineGeometry => (Geometry?)GetValue(LiveGuideLineGeometryProperty);

        private static readonly DependencyPropertyKey LiveMarkerVisibleKey =
            DependencyProperty.RegisterReadOnly(nameof(LiveMarkerVisible), typeof(Visibility),
                typeof(MozaCurveEditor), new PropertyMetadata(Visibility.Collapsed));
        public static readonly DependencyProperty LiveMarkerVisibleProperty = LiveMarkerVisibleKey.DependencyProperty;
        public Visibility LiveMarkerVisible => (Visibility)GetValue(LiveMarkerVisibleProperty);

        private static readonly DependencyPropertyKey LiveMarkerLeftKey =
            DependencyProperty.RegisterReadOnly(nameof(LiveMarkerLeft), typeof(double),
                typeof(MozaCurveEditor), new PropertyMetadata(-10000.0));
        public static readonly DependencyProperty LiveMarkerLeftProperty = LiveMarkerLeftKey.DependencyProperty;
        public double LiveMarkerLeft => (double)GetValue(LiveMarkerLeftProperty);

        private static readonly DependencyPropertyKey LiveMarkerTopKey =
            DependencyProperty.RegisterReadOnly(nameof(LiveMarkerTop), typeof(double),
                typeof(MozaCurveEditor), new PropertyMetadata(-10000.0));
        public static readonly DependencyProperty LiveMarkerTopProperty = LiveMarkerTopKey.DependencyProperty;
        public double LiveMarkerTop => (double)GetValue(LiveMarkerTopProperty);

        // 10 node centre positions exposed individually for Canvas-positioned ellipses
        private static readonly DependencyPropertyKey[] NodeXKeys = new DependencyPropertyKey[10];
        private static readonly DependencyPropertyKey[] NodeYKeys = new DependencyPropertyKey[10];
        public static readonly DependencyProperty[] NodeXProperties = new DependencyProperty[10];
        public static readonly DependencyProperty[] NodeYProperties = new DependencyProperty[10];

        // 10 X-axis label Canvas.Left positions (already offset by -LabelWidth/2)
        private static readonly DependencyPropertyKey[] TickLabelXKeys = new DependencyPropertyKey[10];
        public static readonly DependencyProperty[] TickLabelXProperties = new DependencyProperty[10];

        // 10 X-axis label visibility flags (controls whether the slot has a string)
        private static readonly DependencyPropertyKey[] XAxisLabelKeys = new DependencyPropertyKey[10];
        public static readonly DependencyProperty[] XAxisLabelProperties = new DependencyProperty[10];

        // 10 in-circle value labels (stringified current Y value, integer)
        private static readonly DependencyPropertyKey[] NodeValueKeys = new DependencyPropertyKey[10];
        public static readonly DependencyProperty[] NodeValueProperties = new DependencyProperty[10];

        // Y-axis labels (text + Canvas.Top position) — 5 slots for the classic
        // ranges, 6 for the 10-band EQ's 0-500-in-100s axis.
        private static readonly DependencyPropertyKey[] YAxisLabelKeys = new DependencyPropertyKey[6];
        public static readonly DependencyProperty[] YAxisLabelProperties = new DependencyProperty[6];
        private static readonly DependencyPropertyKey[] YLabelYKeys = new DependencyPropertyKey[6];
        public static readonly DependencyProperty[] YLabelYProperties = new DependencyProperty[6];

        // Single shared DPs for label container positioning
        private static readonly DependencyPropertyKey XLabelCanvasTopKey =
            DependencyProperty.RegisterReadOnly(nameof(XLabelCanvasTop), typeof(double),
                typeof(MozaCurveEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty XLabelCanvasTopProperty = XLabelCanvasTopKey.DependencyProperty;
        public double XLabelCanvasTop => (double)GetValue(XLabelCanvasTopProperty);

        private static readonly DependencyPropertyKey YLabelCanvasLeftKey =
            DependencyProperty.RegisterReadOnly(nameof(YLabelCanvasLeft), typeof(double),
                typeof(MozaCurveEditor), new PropertyMetadata(0.0));
        public static readonly DependencyProperty YLabelCanvasLeftProperty = YLabelCanvasLeftKey.DependencyProperty;
        public double YLabelCanvasLeft => (double)GetValue(YLabelCanvasLeftProperty);

        static void RegisterPerSlotProps()
        {
            for (int i = 0; i < 10; i++)
            {
                NodeXKeys[i] = DependencyProperty.RegisterReadOnly($"Node{i + 1}X", typeof(double),
                    typeof(MozaCurveEditor), new PropertyMetadata(0.0));
                NodeXProperties[i] = NodeXKeys[i].DependencyProperty;
                NodeYKeys[i] = DependencyProperty.RegisterReadOnly($"Node{i + 1}Y", typeof(double),
                    typeof(MozaCurveEditor), new PropertyMetadata(0.0));
                NodeYProperties[i] = NodeYKeys[i].DependencyProperty;

                TickLabelXKeys[i] = DependencyProperty.RegisterReadOnly($"TickLabel{i}X", typeof(double),
                    typeof(MozaCurveEditor), new PropertyMetadata(0.0));
                TickLabelXProperties[i] = TickLabelXKeys[i].DependencyProperty;

                XAxisLabelKeys[i] = DependencyProperty.RegisterReadOnly($"XAxisLabel{i}", typeof(string),
                    typeof(MozaCurveEditor), new PropertyMetadata(string.Empty));
                XAxisLabelProperties[i] = XAxisLabelKeys[i].DependencyProperty;

                NodeValueKeys[i] = DependencyProperty.RegisterReadOnly($"Node{i + 1}Value", typeof(string),
                    typeof(MozaCurveEditor), new PropertyMetadata(string.Empty));
                NodeValueProperties[i] = NodeValueKeys[i].DependencyProperty;
            }
            for (int i = 0; i < 6; i++)
            {
                YAxisLabelKeys[i] = DependencyProperty.RegisterReadOnly($"YAxisLabel{i}", typeof(string),
                    typeof(MozaCurveEditor), new PropertyMetadata(string.Empty));
                YAxisLabelProperties[i] = YAxisLabelKeys[i].DependencyProperty;

                YLabelYKeys[i] = DependencyProperty.RegisterReadOnly($"YLabel{i}Y", typeof(double),
                    typeof(MozaCurveEditor), new PropertyMetadata(-10000.0));
                YLabelYProperties[i] = YLabelYKeys[i].DependencyProperty;
            }
        }

        // Static initializer used so RegisterPerSlotProps runs after the
        // explicit DP fields above are initialised.
        private static readonly bool _staticInit = StaticInit();
        private static bool StaticInit()
        {
            RegisterPerSlotProps();
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
        public double Node6X => (double)GetValue(NodeXProperties[5]);
        public double Node6Y => (double)GetValue(NodeYProperties[5]);
        public double Node7X => (double)GetValue(NodeXProperties[6]);
        public double Node7Y => (double)GetValue(NodeYProperties[6]);
        public double Node8X => (double)GetValue(NodeXProperties[7]);
        public double Node8Y => (double)GetValue(NodeYProperties[7]);
        public double Node9X => (double)GetValue(NodeXProperties[8]);
        public double Node9Y => (double)GetValue(NodeYProperties[8]);
        public double Node10X => (double)GetValue(NodeXProperties[9]);
        public double Node10Y => (double)GetValue(NodeYProperties[9]);

        public double TickLabel0X => (double)GetValue(TickLabelXProperties[0]);
        public double TickLabel1X => (double)GetValue(TickLabelXProperties[1]);
        public double TickLabel2X => (double)GetValue(TickLabelXProperties[2]);
        public double TickLabel3X => (double)GetValue(TickLabelXProperties[3]);
        public double TickLabel4X => (double)GetValue(TickLabelXProperties[4]);
        public double TickLabel5X => (double)GetValue(TickLabelXProperties[5]);
        public double TickLabel6X => (double)GetValue(TickLabelXProperties[6]);
        public double TickLabel7X => (double)GetValue(TickLabelXProperties[7]);
        public double TickLabel8X => (double)GetValue(TickLabelXProperties[8]);
        public double TickLabel9X => (double)GetValue(TickLabelXProperties[9]);

        public string XAxisLabel0 => (string)GetValue(XAxisLabelProperties[0]);
        public string XAxisLabel1 => (string)GetValue(XAxisLabelProperties[1]);
        public string XAxisLabel2 => (string)GetValue(XAxisLabelProperties[2]);
        public string XAxisLabel3 => (string)GetValue(XAxisLabelProperties[3]);
        public string XAxisLabel4 => (string)GetValue(XAxisLabelProperties[4]);
        public string XAxisLabel5 => (string)GetValue(XAxisLabelProperties[5]);
        public string XAxisLabel6 => (string)GetValue(XAxisLabelProperties[6]);
        public string XAxisLabel7 => (string)GetValue(XAxisLabelProperties[7]);
        public string XAxisLabel8 => (string)GetValue(XAxisLabelProperties[8]);
        public string XAxisLabel9 => (string)GetValue(XAxisLabelProperties[9]);

        public string Node1Value => (string)GetValue(NodeValueProperties[0]);
        public string Node2Value => (string)GetValue(NodeValueProperties[1]);
        public string Node3Value => (string)GetValue(NodeValueProperties[2]);
        public string Node4Value => (string)GetValue(NodeValueProperties[3]);
        public string Node5Value => (string)GetValue(NodeValueProperties[4]);
        public string Node6Value => (string)GetValue(NodeValueProperties[5]);
        public string Node7Value => (string)GetValue(NodeValueProperties[6]);
        public string Node8Value => (string)GetValue(NodeValueProperties[7]);
        public string Node9Value => (string)GetValue(NodeValueProperties[8]);
        public string Node10Value => (string)GetValue(NodeValueProperties[9]);

        public string YAxisLabel0 => (string)GetValue(YAxisLabelProperties[0]);
        public string YAxisLabel1 => (string)GetValue(YAxisLabelProperties[1]);
        public string YAxisLabel2 => (string)GetValue(YAxisLabelProperties[2]);
        public string YAxisLabel3 => (string)GetValue(YAxisLabelProperties[3]);
        public string YAxisLabel4 => (string)GetValue(YAxisLabelProperties[4]);
        public string YAxisLabel5 => (string)GetValue(YAxisLabelProperties[5]);

        public double YLabel0Y => (double)GetValue(YLabelYProperties[0]);
        public double YLabel1Y => (double)GetValue(YLabelYProperties[1]);
        public double YLabel2Y => (double)GetValue(YLabelYProperties[2]);
        public double YLabel3Y => (double)GetValue(YLabelYProperties[3]);
        public double YLabel4Y => (double)GetValue(YLabelYProperties[4]);
        public double YLabel5Y => (double)GetValue(YLabelYProperties[5]);

        // -------- Layout constants --------
        private const double PadLeft = 36;
        private const double PadRight = 14;
        private const double PadTop = 14;
        private const double PadBottom = 32;
        private const double XLabelWidth = 32;      // Width of each X-axis label TextBlock
        private const double YLabelWidth = 26;      // Width of each Y-axis label TextBlock
        private const double XLabelTopOffset = 4;   // Pixels below plot bottom
        // Draggable node diameter. Big enough to host the current value
        // (e.g. "100", "400") at FontMono 11pt inside the circle while
        // visually occluding the spline that passes through the centre.
        // Keep in sync with the Border Width/Height in MozaCurveEditorTemplate.
        private const double NodeSize = 28;
        private const double NodeHalf = NodeSize / 2.0;

        // Live position marker — deliberately smaller than the draggable
        // nodes so it doesn't visually compete with them.
        private const double LiveMarkerSize = 10;
        private const double LiveMarkerHalf = LiveMarkerSize / 2.0;

        // The visual X axis is uniformly compressed by 0.98 so the 28-px node
        // circle (plus its glow) on the last node clears the outer Border's
        // rounded corner. Every node is shifted by the same scale factor —
        // i.e. data X=k% lands at visual fraction (k/100) × 0.98 — so the
        // axis stays linear and the LINEAR preset's dots all sit exactly on
        // the y=x identity line below (which also uses 0.98 as its right
        // endpoint). Hardware-side X breakpoints (20/40/60/80/100 — written
        // by the curve presets) are unchanged; this is purely a visual shift.
        private static readonly double[] Default5NodeFractions = { 0.196, 0.392, 0.588, 0.784, 0.98 };

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

        // Endpoint-drag rescale baseline (see EndpointsOnlyDraggableInX) —
        // captured ONCE at the start of an endpoint drag, not re-derived
        // every tick from the current (already-rescaled, already-rounded)
        // positions. Re-deriving it every tick let a middle node's fraction
        // collapse to exactly 0 or 1 once heavy compression rounded its X
        // onto an endpoint's own X: every later tick read frac=0 (or 1)
        // again from that same now-stuck position, so the curve could
        // compress but never re-expand — this fixes that by keeping the
        // reference fractions stable for the whole drag gesture.
        private double[]? _dragBaseFracs;
        private double _dragBaseFirstX;
        private double _dragBaseSpan;

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
                int lastNode = ClampedNodeCount() - 1;
                if (EndpointsOnlyDraggableInX && (_dragNode == 0 || _dragNode == lastNode))
                    CaptureEndpointDragBaseline(lastNode);
                _canvas.CaptureMouse();
                ApplyDrag(p);
                e.Handled = true;
            }
        }

        private void CaptureEndpointDragBaseline(int lastNode)
        {
            _dragBaseFirstX = GetX(0);
            _dragBaseSpan = GetX(lastNode) - _dragBaseFirstX;
            _dragBaseFracs = new double[lastNode + 1];
            if (_dragBaseSpan > 0.0001)
                for (int m = 1; m < lastNode; m++)
                    _dragBaseFracs[m] = (GetX(m) - _dragBaseFirstX) / _dragBaseSpan;
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
            int n = ClampedNodeCount();
            int best = -1;
            // Visual radius is NodeHalf; pad outward so the user has some
            // forgiveness when clicking near (but not exactly on) the circle.
            double r = NodeHalf + 4.0;
            double bestDist = r * r;
            for (int i = 0; i < n; i++)
            {
                // NodeXProperties stores the Border's top-left — re-centre
                // before measuring distance so the hit area is concentric
                // with the visible circle.
                double cx = (double)GetValue(NodeXProperties[i]) + NodeHalf;
                double cy = (double)GetValue(NodeYProperties[i]) + NodeHalf;
                double dx = p.X - cx;
                double dy = p.Y - cy;
                double d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        private void ApplyDrag(Point p)
        {
            int lastNode = ClampedNodeCount() - 1;
            bool isEndpoint = _dragNode == 0 || _dragNode == lastNode;

            // Vertical drag — locked for the first node when
            // EndpointsOnlyDraggableInX is set (see its doc comment): that
            // node moves horizontally only.
            if (!(EndpointsOnlyDraggableInX && _dragNode == 0))
            {
                double h = _canvas?.ActualHeight ?? ActualHeight;
                double plotH = Math.Max(1, h - PadTop - PadBottom);
                double y01 = (h - PadBottom - p.Y) / plotH;
                double range = Math.Max(1, YMax - YMin);
                double v = Math.Max(YMin, Math.Min(YMax, Math.Round(YMin + y01 * range)));
                if (!double.IsNaN(LastNodeYMax) && _dragNode == lastNode)
                    v = Math.Min(v, LastNodeYMax);
                // Clamp to the neighbours' Y too — same crossing-prevention
                // reasoning as the X neighbour-clamp below, just per-axis.
                if (ClampYToAdjacentNodes)
                {
                    double loY = _dragNode == 0 ? YMin : GetY(_dragNode - 1);
                    double hiY = _dragNode == lastNode ? YMax : GetY(_dragNode + 1);
                    if (hiY < loY) hiY = loY;
                    v = Math.Max(loY, Math.Min(hiY, v));
                }
                SetY(_dragNode, v);
            }

            // Horizontal drag (output curve only — see AllowHorizontalDrag).
            // Clamped between immediate neighbours (min 1-unit gap) so nodes
            // can never cross, which would make the curve's X non-monotonic
            // and the Bezier-inversion evaluator
            // (MozaMBoosterRegistry.EvaluateCurveArbitraryX) ill-defined.
            if (AllowHorizontalDrag && _dragNode >= 0 && _dragNode < 6
                && !(LockLastNodeX && _dragNode == lastNode)
                && !(EndpointsOnlyDraggableInX && !isEndpoint))
            {
                double w = _canvas?.ActualWidth ?? ActualWidth;
                double plotW = Math.Max(1, w - PadLeft - PadRight);
                double x01 = (p.X - PadLeft) / (0.98 * plotW);
                double dataX = x01 * 100.0;

                double lo = _dragNode == 0 ? 1.0 : GetX(_dragNode - 1) + 1.0;
                double hi = _dragNode == lastNode ? 100.0 : GetX(_dragNode + 1) - 1.0;
                if (hi < lo) hi = lo;
                dataX = Math.Round(Math.Max(lo, Math.Min(hi, dataX)));

                if (EndpointsOnlyDraggableInX && isEndpoint)
                {
                    // Rescale every in-between node's X to keep its
                    // fractional position — captured once at drag start in
                    // _dragBaseFracs, see CaptureEndpointDragBaseline — between
                    // the two endpoints, so the curve's shape follows the
                    // endpoint being dragged instead of being left bunched up
                    // behind it.
                    SetX(_dragNode, dataX);
                    if (_dragBaseFracs != null && _dragBaseSpan > 0.0001)
                    {
                        double newFirstX = GetX(0);
                        double newLastX = GetX(lastNode);
                        double newSpan = newLastX - newFirstX;
                        for (int m = 1; m < lastNode; m++)
                            SetX(m, Math.Round(newFirstX + _dragBaseFracs[m] * newSpan));
                    }
                }
                else
                {
                    SetX(_dragNode, dataX);
                }
            }
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
                case 5: Y6 = v; break;
                case 6: Y7 = v; break;
                case 7: Y8 = v; break;
                case 8: Y9 = v; break;
                case 9: Y10 = v; break;
            }
        }

        private double GetY(int i)
        {
            switch (i)
            {
                case 0: return Y1;
                case 1: return Y2;
                case 2: return Y3;
                case 3: return Y4;
                case 4: return Y5;
                case 5: return Y6;
                case 6: return Y7;
                case 7: return Y8;
                case 8: return Y9;
                case 9: return Y10;
                default: return 0;
            }
        }

        private double GetX(int i)
        {
            switch (i)
            {
                case 0: return X1;
                case 1: return X2;
                case 2: return X3;
                case 3: return X4;
                case 4: return X5;
                case 5: return X6;
                default: return 0;
            }
        }

        private void SetX(int i, double v)
        {
            switch (i)
            {
                case 0: X1 = v; break;
                case 1: X2 = v; break;
                case 2: X3 = v; break;
                case 3: X4 = v; break;
                case 4: X5 = v; break;
                case 5: X6 = v; break;
            }
        }

        // -------- Geometry recomputation --------

        private int ClampedNodeCount()
        {
            int n = NodeCount;
            if (n < 5) return 5;
            if (n > 10) return 10;
            return n;
        }

        private static double[] ParseFractions(string? csv, double[] fallback)
        {
            if (string.IsNullOrWhiteSpace(csv)) return fallback;
            var parts = csv!.Split(',');
            var result = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                    return fallback;
            }
            return result;
        }

        private static string[] ParseLabels(string? csv)
        {
            if (string.IsNullOrEmpty(csv)) return Array.Empty<string>();
            var parts = csv!.Split(',');
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            return parts;
        }

        private void Recompute()
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double plotW = Math.Max(1, w - PadLeft - PadRight);
            double plotH = Math.Max(1, h - PadTop - PadBottom);
            int nodeCount = ClampedNodeCount();

            // ---- Node X fractions / Y values ----
            double[] nodeFracs;
            if (AllowHorizontalDrag && nodeCount <= 6)
            {
                // Nodes are user-draggable in X (see ApplyDrag) — derive
                // fractions from X1..X6 instead of the fixed NodeXFractions
                // string. Same 0.98 compression as Default5NodeFractions so
                // a never-dragged node lands exactly where it always has.
                double[] xs = { X1, X2, X3, X4, X5, X6 };
                nodeFracs = new double[nodeCount];
                for (int i = 0; i < nodeCount; i++)
                    nodeFracs[i] = Math.Max(0, Math.Min(1, (xs[i] / 100.0) * 0.98));
            }
            else
            {
                nodeFracs = ParseFractions(NodeXFractions, Default5NodeFractions);
                if (nodeFracs.Length < nodeCount)
                {
                    // Caller didn't supply enough fractions; fall back to evenly
                    // spaced column centres so we render something sensible.
                    nodeFracs = new double[nodeCount];
                    for (int i = 0; i < nodeCount; i++) nodeFracs[i] = (2.0 * i + 1) / (2.0 * nodeCount);
                }
            }
            double[] ys = { Y1, Y2, Y3, Y4, Y5, Y6, Y7, Y8, Y9, Y10 };
            double range = Math.Max(1, YMax - YMin);

            // ---- Node pixel positions + in-circle value strings ----
            var pts = new Point[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                double frac = Math.Max(0, Math.Min(1, nodeFracs[i]));
                double x = PadLeft + frac * plotW;
                double yClamped = Math.Max(YMin, Math.Min(YMax, ys[i]));
                if (!double.IsNaN(LastNodeYMax) && i == nodeCount - 1)
                    yClamped = Math.Min(yClamped, LastNodeYMax);
                double y = PadTop + (1 - (yClamped - YMin) / range) * plotH;
                pts[i] = new Point(x, y);
                // Canvas.Left/Top are top-left corner — back the centre off by
                // half the node diameter so the circle is centred on (x, y).
                SetValue(NodeXKeys[i], x - NodeHalf);
                SetValue(NodeYKeys[i], y - NodeHalf);
                SetValue(NodeValueKeys[i], ((int)Math.Round(yClamped)).ToString(CultureInfo.InvariantCulture));
            }
            // Park unused node slots well off-canvas (also clears any stale
            // value string the template might still display).
            for (int i = nodeCount; i < 10; i++)
            {
                SetValue(NodeXKeys[i], -10000.0);
                SetValue(NodeYKeys[i], -10000.0);
                SetValue(NodeValueKeys[i], string.Empty);
            }

            // ---- Catmull-Rom spline ----
            // `real` holds every point the visible curve actually passes
            // through, in order — the draggable nodes, optionally prefixed
            // with the plot's lower-left corner (AnchorAtOrigin) and/or
            // suffixed with its upper-right corner (AnchorAtTopRight), both
            // as genuine drawn points rather than mere tangent helpers.
            // Standard Catmull-Rom flat-tangent endpoints: `allPts` pads
            // `real` with a duplicate of its own first/last point on each
            // side purely so the p0/p3 tangent terms have something to read
            // — this reproduces the pre-existing "flat tangent at the first/
            // last drawn point" behaviour for every current curve (anchored
            // or not) and extends the same rule to the new top-right anchor.
            bool anchorStart = AnchorAtOrigin;
            bool anchorEnd = AnchorAtTopRight;
            int realCount = nodeCount + (anchorStart ? 1 : 0) + (anchorEnd ? 1 : 0);
            var real = new Point[realCount];
            int wi = 0;
            if (anchorStart) real[wi++] = new Point(PadLeft, PadTop + plotH);
            for (int i = 0; i < nodeCount; i++) real[wi++] = pts[i];
            if (anchorEnd) real[wi++] = new Point(PadLeft + 0.98 * plotW, PadTop);

            var allPts = new Point[realCount + 2];
            allPts[0] = real[0];
            for (int i = 0; i < realCount; i++) allPts[i + 1] = real[i];
            allPts[realCount + 1] = real[realCount - 1];

            var fig = new PathFigure
            {
                StartPoint = real[0],
                IsClosed = false,
                IsFilled = false,
            };
            // Cached alongside geometry construction so the live-position
            // marker (below) can locate the exact pixel point ON the spline
            // for a given data-space X, without re-deriving the Catmull-Rom
            // tangents a second time.
            int segCount = realCount - 1;
            var segments = new (Point p1, Point c1, Point c2, Point p2)[segCount];
            for (int i = 0; i < segCount; i++)
            {
                Point p0 = allPts[i];
                Point p1 = allPts[i + 1];
                Point p2 = allPts[i + 2];
                Point p3 = allPts[i + 3];
                Point c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                Point c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
                segments[i] = (p1, c1, c2, p2);
            }
            var geom = new PathGeometry();
            geom.Figures.Add(fig);
            geom.Freeze();
            SetValue(CurveGeometryKey, geom);

            _liveSegments = segments;
            _liveNodePts = pts;
            _livePlotW = plotW;
            _liveAxisBottomY = PadTop + plotH;
            UpdateLiveMarker(segments, pts, plotW, PadTop + plotH);

            // ---- Background grid (4 interior horizontal + 4 vertical lines) ----
            // Vertical lines scale with the rightmost node fraction so they
            // stay under the dots/labels when the X axis is compressed; the
            // horizontal lines stay evenly spaced (Y axis is always linear).
            // Exception: AllowHorizontalDrag curves (Sim Input Mapping,
            // Pedal Feel, the FFB output curve) always use the full 0-100%
            // span instead — their nodes can end up anywhere along X, so
            // tying the grid to wherever the LAST node currently happens to
            // sit would shrink/shift the whole grid as it's dragged, right
            // when a stable position reference matters most. Every
            // fixed-X curve (EQ, Handbrake, Throttle, Brake, Clutch) keeps
            // its existing behaviour unchanged.
            double xScale = AllowHorizontalDrag ? 0.98 : Math.Max(0, Math.Min(1, nodeFracs[nodeCount - 1]));
            var grid = new GeometryGroup();
            for (int i = 1; i <= 4; i++)
            {
                double frac = i / 5.0;
                grid.Children.Add(new LineGeometry(
                    new Point(PadLeft, PadTop + frac * plotH),
                    new Point(PadLeft + plotW, PadTop + frac * plotH)));
                double vFrac = frac * xScale;
                grid.Children.Add(new LineGeometry(
                    new Point(PadLeft + vFrac * plotW, PadTop),
                    new Point(PadLeft + vFrac * plotW, PadTop + plotH)));
            }
            grid.Freeze();
            SetValue(GridGeometryKey, grid);

            // ---- Optional reference line (e.g. EQ neutral at 100%) ----
            double refY = ReferenceLineY;
            if (!double.IsNaN(refY) && refY >= YMin && refY <= YMax)
            {
                double yPix = PadTop + (1 - (refY - YMin) / range) * plotH;
                var refLine = new LineGeometry(
                    new Point(PadLeft, yPix),
                    new Point(PadLeft + plotW, yPix));
                refLine.Freeze();
                SetValue(ReferenceLineGeometryKey, refLine);
            }
            else
            {
                SetValue(ReferenceLineGeometryKey, null);
            }

            // ---- Optional y=x identity / nominal line (output curves only) ----
            // Always the TRUE diagonal from data (0,0) to (100,100) — same
            // 0.98 inset every node/anchor pixel position uses (so the line
            // doesn't get clipped at the plot's true right edge), but that's
            // a rendering-only margin, not a stand-in for data-space X.
            // BUG (fixed): this used to end at the rightmost NODE's own
            // fraction instead of a fixed 0.98 — harmless for curves whose
            // last node always sits at data (100,100) by construction (FFB,
            // Sim Input Mapping, Throttle/Brake/Clutch), but for a curve
            // like Pedal Feel — AnchorAtTopRight, whose last DRAGGABLE node
            // legitimately defaults short of 100% (e.g. ~98%) while the
            // curve itself still runs on to a separate fixed (100,100)
            // corner anchor — the old logic stopped the dashed reference
            // short of that corner AND pinned its Y to YMax at the node's
            // (too-far-left) X, producing a line that wasn't really y=x at
            // all and visibly diverged from the actual plotted curve near
            // the top-right (see linear.png).
            if (ShowIdentityLine)
            {
                var ident = new LineGeometry(
                    new Point(PadLeft, PadTop + plotH),
                    new Point(PadLeft + 0.98 * plotW, PadTop));
                ident.Freeze();
                SetValue(IdentityLineGeometryKey, ident);
            }
            else
            {
                SetValue(IdentityLineGeometryKey, null);
            }

            // ---- X-axis labels ----
            double[] labelFracs = ParseFractions(XLabelFractions, new[] { 0.0, 0.2, 0.4, 0.6, 0.8, 1.0 });
            string[] xLabels = ParseLabels(XAxisLabels);
            for (int i = 0; i < 10; i++)
            {
                if (i < labelFracs.Length && i < xLabels.Length && !string.IsNullOrEmpty(xLabels[i]))
                {
                    double frac = Math.Max(0, Math.Min(1, labelFracs[i]));
                    double centerX = PadLeft + frac * plotW;
                    SetValue(TickLabelXKeys[i], centerX - XLabelWidth / 2.0);
                    SetValue(XAxisLabelKeys[i], xLabels[i]);
                }
                else
                {
                    // Park unused slots off-canvas so their TextBlocks don't
                    // ghost-render even if they pick up a stray binding.
                    SetValue(TickLabelXKeys[i], -1000.0);
                    SetValue(XAxisLabelKeys[i], string.Empty);
                }
            }
            SetValue(XLabelCanvasTopKey, PadTop + plotH + XLabelTopOffset);

            // ---- Y-axis labels (evenly spaced, top=YMax to bottom=YMin; 5
            // slots for the classic styles, 6 when the style supplies six) ----
            string[] yLabels = ParseLabels(YAxisLabels);
            int yCount = Math.Min(Math.Max(yLabels.Length, 5), 6);
            for (int i = 0; i < 6; i++)
            {
                if (i < yCount)
                {
                    double frac = (double)i / (yCount - 1);
                    double yPix = PadTop + frac * plotH - 8; // 8 ≈ half line-height
                    SetValue(YLabelYKeys[i], yPix);
                    SetValue(YAxisLabelKeys[i], i < yLabels.Length ? yLabels[i] : string.Empty);
                }
                else
                {
                    SetValue(YLabelYKeys[i], -10000.0);
                    SetValue(YAxisLabelKeys[i], string.Empty);
                }
            }
            SetValue(YLabelCanvasLeftKey, 6.0); // matches existing left padding of 6
        }

        /// <summary>
        /// Position the live indicator (see <see cref="LiveX"/>) exactly ON
        /// the already-built spline: map the data-space X to a pixel X, find
        /// which segment contains it, then invert that segment's Bezier X(t)
        /// via bisection (same approach as
        /// MozaMBoosterRegistry.EvaluateCurveArbitraryX) to read off both the
        /// pixel X and Y at that point — i.e. the dot always sits ON the
        /// curve as currently configured, not just sliding horizontally.
        /// </summary>
        private void UpdateLiveMarker((Point p1, Point c1, Point c2, Point p2)[] segments, Point[] nodePts, double plotW, double axisBottomY)
        {
            double liveX = LiveX;
            bool placed = false;

            if (!double.IsNaN(liveX) && segments.Length > 0)
            {
                bool haveTarget;
                double targetPixelX = 0;

                if (AllowHorizontalDrag && nodePts.Length > 0)
                {
                    // Nodes are user-draggable in X (see ApplyDrag) — the
                    // fixed XAxisLabels/XLabelFractions correspondence below
                    // only matches the DEFAULT (undragged) breakpoints, so
                    // once the user configures a node's X, that mapping no
                    // longer reflects the actual plotted curve. Map liveX to
                    // a pixel X from the node's OWN current (dataX, pixelX)
                    // pairs instead — both axes are affine in a node's own
                    // fraction, so linear interpolation between two known
                    // node pairs reproduces the true mapping exactly whether
                    // or not it's been dragged from its default.
                    double[] dataXs = { X1, X2, X3, X4, X5, X6 };
                    int n = Math.Min(nodePts.Length, dataXs.Length);
                    double clampedX = Math.Max(0, Math.Min(dataXs[n - 1], liveX));
                    double x0 = 0, px0 = PadLeft, x1 = dataXs[0], px1 = nodePts[0].X;
                    for (int i = 0; i < n - 1; i++)
                    {
                        if (clampedX >= dataXs[i] && clampedX <= dataXs[i + 1])
                        {
                            x0 = dataXs[i]; px0 = nodePts[i].X;
                            x1 = dataXs[i + 1]; px1 = nodePts[i + 1].X;
                            break;
                        }
                    }
                    targetPixelX = x1 > x0 ? px0 + (clampedX - x0) / (x1 - x0) * (px1 - px0) : px0;
                    haveTarget = true;
                }
                else
                {
                    double[] fracs = ParseFractions(XLabelFractions, new[] { 0.0, 0.2, 0.4, 0.6, 0.8, 1.0 });
                    string[] rawLabels = ParseLabels(XAxisLabels);
                    int n = Math.Min(fracs.Length, rawLabels.Length);
                    var values = new double[n];
                    bool parsedOk = n >= 2;
                    for (int i = 0; parsedOk && i < n; i++)
                        parsedOk = double.TryParse(rawLabels[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]);

                    if (parsedOk)
                    {
                        double clampedX = Math.Max(values[0], Math.Min(values[n - 1], liveX));
                        int lo = 0;
                        for (int i = 0; i < n - 1; i++)
                        {
                            if (clampedX >= values[i] && clampedX <= values[i + 1]) { lo = i; break; }
                        }
                        double t0 = values[lo], t1 = values[lo + 1];
                        double f0 = fracs[lo], f1 = fracs[lo + 1];
                        double frac = t1 > t0 ? f0 + (clampedX - t0) / (t1 - t0) * (f1 - f0) : f0;
                        targetPixelX = PadLeft + Math.Max(0, Math.Min(1, frac)) * plotW;
                    }
                    haveTarget = parsedOk;
                }

                if (haveTarget)
                {
                    int segIdx = segments.Length - 1;
                    for (int i = 0; i < segments.Length; i++)
                    {
                        if (targetPixelX <= segments[i].p2.X) { segIdx = i; break; }
                    }
                    var seg = segments[segIdx];

                    double loT = 0, hiT = 1;
                    for (int iter = 0; iter < 24; iter++)
                    {
                        double tm = (loT + hiT) / 2.0;
                        double bx = CubicBezierPoint(seg.p1.X, seg.c1.X, seg.c2.X, seg.p2.X, tm);
                        if (bx < targetPixelX) loT = tm; else hiT = tm;
                    }
                    double finalT = (loT + hiT) / 2.0;
                    double markerX = CubicBezierPoint(seg.p1.X, seg.c1.X, seg.c2.X, seg.p2.X, finalT);
                    double markerY = CubicBezierPoint(seg.p1.Y, seg.c1.Y, seg.c2.Y, seg.p2.Y, finalT);

                    SetValue(LiveMarkerVisibleKey, Visibility.Visible);
                    SetValue(LiveMarkerLeftKey, markerX - LiveMarkerHalf);
                    SetValue(LiveMarkerTopKey, markerY - LiveMarkerHalf);

                    var guide = new LineGeometry(new Point(markerX, axisBottomY), new Point(markerX, markerY));
                    guide.Freeze();
                    SetValue(LiveGuideLineGeometryKey, guide);
                    placed = true;
                }
            }

            if (!placed)
            {
                SetValue(LiveMarkerVisibleKey, Visibility.Collapsed);
                SetValue(LiveMarkerLeftKey, -10000.0);
                SetValue(LiveMarkerTopKey, -10000.0);
                SetValue(LiveGuideLineGeometryKey, null);
            }
        }

        private static double CubicBezierPoint(double p0, double c1, double c2, double p1, double t)
        {
            double mt = 1 - t;
            return mt * mt * mt * p0 + 3 * mt * mt * t * c1 + 3 * mt * t * t * c2 + t * t * t * p1;
        }
    }
}
