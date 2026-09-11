using System.Collections.Generic;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>Element kinds the converter can emit. One per mzdash <c>type</c>
    /// observed in ground truth (<c>~/dashes</c>); nothing else renders on the wheel.</summary>
    public enum IrKind
    {
        Screen,
        Layer,
        Rectangle,
        Ellipse,
        Text,
        Image,
        LinearGauge,
        CircularGauge,
    }

    /// <summary>A fill: solid <c>#AARRGGBB</c>, or a linear gradient.
    /// SimHub and mzdash use the SAME channel order, so hex strings copy through
    /// verbatim — there is no byte swap (verified against every ground-truth root
    /// node: <c>#00000000</c> is transparent, <c>#FF000000</c> opaque black).</summary>
    public sealed class IrColor
    {
        public string Hex { get; set; } = "#00000000";
        public bool IsGradient { get; set; }
        public List<IrGradientStop> Stops { get; } = new List<IrGradientStop>();

        public static IrColor Solid(string hex) => new IrColor { Hex = hex };
        public static IrColor Transparent() => new IrColor { Hex = "#00000000" };
    }

    public sealed class IrGradientStop
    {
        public string Hex { get; set; } = "#FF000000";
        /// <summary>0..1 along the gradient axis.</summary>
        public double Position { get; set; }
    }

    /// <summary>Per-side border widths + per-corner radii. mzdash models these
    /// natively (<c>borderStyle.bordersTop</c> …, <c>radiusTopLeft</c> …), so
    /// SimHub's <c>BorderStyle</c> maps across 1:1 with no approximation.</summary>
    public sealed class IrBorder
    {
        public IrColor Color { get; set; } = IrColor.Transparent();
        public double Top { get; set; }
        public double Bottom { get; set; }
        public double Left { get; set; }
        public double Right { get; set; }
        public double RadiusTopLeft { get; set; }
        public double RadiusTopRight { get; set; }
        public double RadiusBottomLeft { get; set; }
        public double RadiusBottomRight { get; set; }

        public bool IsUniformWidth => Top == Bottom && Bottom == Left && Left == Right;
        public bool IsUniformRadius => RadiusTopLeft == RadiusTopRight
            && RadiusTopRight == RadiusBottomLeft && RadiusBottomLeft == RadiusBottomRight;
    }

    /// <summary>Opacity/rotation/blur/blink/shadow. All native in mzdash's
    /// <c>effect</c> block — SimHub's ShadowDepth/ShadowBlur/ShadowColor and the
    /// BuiltIn gear-blink settings land here rather than being dropped.</summary>
    public sealed class IrEffect
    {
        /// <summary>0..100, matching mzdash. SimHub's 0..1 opacity is scaled on import.</summary>
        public double Opacity { get; set; } = 100;
        public double Rotation { get; set; }
        public double BlurRadius { get; set; }
        public bool BlinkEnabled { get; set; }
        public double BlinkDelay { get; set; } = 250;
        public double ShadowBlur { get; set; }
        public double ShadowDepth { get; set; }
        public double ShadowDirection { get; set; }
        public IrColor ShadowColor { get; set; } = IrColor.Transparent();
    }

    public sealed class IrText
    {
        public string Text { get; set; } = "";
        public string FontFamily { get; set; } = "Lato";
        public double FontSize { get; set; } = 20;
        public int FontWeight { get; set; } = 400;
        public IrColor Color { get; set; } = IrColor.Solid("#FFFFFFFF");
        /// <summary>mzdash spelling: AlignLeft / AlignCenter / AlignRight.</summary>
        public string HorizontalAlignment { get; set; } = "AlignLeft";
        /// <summary>mzdash spelling: AlignTop / AlignCenter / AlignBottom.</summary>
        public string VerticalAlignment { get; set; } = "AlignCenter";
        public double PaddingTop { get; set; }
        public double PaddingBottom { get; set; }
        public double PaddingLeft { get; set; }
        public double PaddingRight { get; set; }
        public bool WrapText { get; set; }
    }

    /// <summary>Linear and circular gauge geometry. <c>CircularGauge.qml</c> is a
    /// native mzdash widget (26 instances in ground truth), so SimHub's
    /// CircularGaugeItem/DialGaugeItem need no pre-rendered image.</summary>
    public sealed class IrGauge
    {
        public double Minimum { get; set; }
        public double Maximum { get; set; } = 100;
        public double Value { get; set; }
        public IrColor GaugeColor { get; set; } = IrColor.Solid("#FFFFFFFF");
        public IrColor BackgroundColor { get; set; } = IrColor.Transparent();

        // LinearGauge
        public bool Vertical { get; set; }
        /// <summary>mzdash spelling: AlignLeft / AlignRight / AlignTop / AlignBottom.</summary>
        public string Alignment { get; set; } = "AlignLeft";

        // CircularGauge
        public double StartAngle { get; set; } = 300;
        public double SweepAngle { get; set; } = 360;
        public double StrokeThickness { get; set; } = 4.5;
    }

    /// <summary>One entry in an mzdash node's <c>binding</c> map.
    /// <see cref="Target"/> is the dotted path into the node's own property blocks
    /// (<c>text.text</c>, <c>general.visible</c>, <c>linearGauge.value</c>, …).
    /// The wire form is always a 2-element <c>methods</c> chain, the second step
    /// formatting the first's output as <c>_result</c>.</summary>
    public sealed class IrBinding
    {
        public string Target { get; set; } = "";
        /// <summary>methods[0] — JavaScript evaluated on the wheel.</summary>
        public string Expression { get; set; } = "";
        /// <summary>methods[1] — a JS expression over <c>_result</c>. <c>"_result"</c>
        /// is the identity and the most common value in ground truth.</summary>
        public string Format { get; set; } = "_result";
    }

    /// <summary>Format-neutral element tree. The djson reader fills it, the canvas
    /// fitter transforms it in place, and the mzdash writer serialises it — so
    /// neither end needs to know about the other's schema.</summary>
    public sealed class IrNode
    {
        public IrKind Kind { get; set; }
        public string Name { get; set; } = "";

        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Visible { get; set; } = true;
        public bool Locked { get; set; }

        public IrColor Background { get; set; } = IrColor.Transparent();
        public IrColor BorderColor { get; set; } = IrColor.Transparent();
        public double BorderWidth { get; set; }
        public double BorderRadius { get; set; }
        public IrBorder Border { get; set; } = new IrBorder();
        public IrEffect Effect { get; set; } = new IrEffect();

        /// <summary>Set when <see cref="Kind"/> is <see cref="IrKind.Text"/>.</summary>
        public IrText? Text { get; set; }
        /// <summary>Set for Linear/Circular gauge kinds.</summary>
        public IrGauge? Gauge { get; set; }
        /// <summary>Set when <see cref="Kind"/> is <see cref="IrKind.Image"/>:
        /// the mzdash-relative <c>MD5/&lt;md5&gt;.&lt;ext&gt;</c> path.</summary>
        public string? ImageSrc { get; set; }

        public List<IrBinding> Bindings { get; } = new List<IrBinding>();
        public List<IrNode> Children { get; } = new List<IrNode>();
    }

    /// <summary>A converted dashboard: the screens, plus the image resources the
    /// element tree references.</summary>
    public sealed class IrDashboard
    {
        public string Name { get; set; } = "";
        public List<IrNode> Screens { get; } = new List<IrNode>();
        /// <summary>mzdash <c>imageResources</c> — <c>MD5/&lt;md5&gt;.&lt;ext&gt;</c> paths.</summary>
        public List<string> ImageResources { get; } = new List<string>();
    }
}
