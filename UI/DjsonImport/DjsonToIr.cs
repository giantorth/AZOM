using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>
    /// Maps SimHub's item tree onto the format-neutral <see cref="IrNode"/> tree.
    ///
    /// <para>Layer children carry <b>absolute</b> screen coordinates in <c>.djson</c> — a
    /// layer's own Left/Top is just the bounding box of its children, not an origin. That
    /// matches mzdash, whose <c>Layer.qml</c> has no geometry at all, so coordinates pass
    /// through untouched and only <c>Repetitions</c> needs an offset applied.</para>
    /// </summary>
    public sealed class DjsonToIr
    {
        private readonly BindingTranslator _bindings;
        private readonly FontMap _fonts;
        private readonly ConversionReport _report;
        private readonly IReadOnlyDictionary<string, string> _images;
        private readonly string _sourceDir;

        /// <summary>Guards against a widget cycle (A includes B includes A), which would
        /// otherwise recurse until the stack gives out.</summary>
        private readonly HashSet<string> _widgetStack =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private const int MaxRepetitions = 64;

        public DjsonToIr(BindingTranslator bindings, FontMap fonts, ConversionReport report,
                         IReadOnlyDictionary<string, string> images, string sourceDir)
        {
            _bindings = bindings;
            _fonts = fonts;
            _report = report;
            _images = images;
            _sourceDir = sourceDir;
        }

        /// <summary>Item types this converter does not emit, and why.
        ///
        /// <para>Note that mzdash <b>does</b> have native <c>Map.qml</c> and
        /// <c>Radar.qml</c> widgets — MOZA's own factory dashboards use them. They are
        /// out of scope here because they are not driven by per-element geometry the way
        /// SimHub's are: the wheel feeds them from the track-geometry / opponent-location
        /// channel space (the <c>location_t</c> block in Telemetry.json), which is a
        /// separate pipeline from anything a converted dashboard binds.</para></summary>
        private static readonly Dictionary<string, string> Unsupported =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MapItem"] = "the wheel's map widget is fed from the track-geometry channels, not per-frame geometry",
                ["GeneratedMapItem"] = "the wheel's map widget is fed from the track-geometry channels, not per-frame geometry",
                ["GeneratedStaticMapItem"] = "the wheel's map widget is fed from the track-geometry channels, not per-frame geometry",
                ["ETSMap"] = "the wheel's map widget is fed from the track-geometry channels, not per-frame geometry",
                ["RadarItem"] = "the wheel's radar widget is fed from the opponent-location channels, not per-frame geometry",
                ["LeaderBoardItem"] = "multi-opponent data has no wheel channel",
                ["ComponentItem"] = "SimHub-only composite component",
                ["RelayCommand"] = "an input command, not a visual element",
            };

        // ── Entry points ──────────────────────────────────────────────────────

        /// <summary>Convert every screen in the document.</summary>
        public IrDashboard Convert(JObject root, string dashboardName)
        {
            var dash = new IrDashboard { Name = dashboardName };

            foreach (var screen in DjsonReader.Items(root["Screens"]))
            {
                var node = new IrNode
                {
                    Kind = IrKind.Screen,
                    Name = DjsonReader.Str(screen, "Name", "Screen"),
                    Background = ColorOf(screen, "BackgroundColor", "#FF000000"),
                };
                foreach (var item in DjsonReader.Items(screen["Items"]))
                    AddConverted(node.Children, item);
                dash.Screens.Add(node);
            }

            if (dash.Screens.Count == 0)
                _report.Notes.Add("no screens found — the file may use an unrecognised layout");

            return dash;
        }

        private void AddConverted(List<IrNode> into, JObject item)
        {
            foreach (var n in ConvertItem(item))
                into.Add(n);
        }

        /// <summary>Convert one source item. Returns zero nodes when dropped, and more than
        /// one for the substitutions that expand (a button, a repeated layer).</summary>
        private IEnumerable<IrNode> ConvertItem(JObject item)
        {
            string shortType = DjsonReader.ShortTypeName(item);
            string name = DjsonReader.Str(item, "Name", shortType);

            if (shortType.Length == 0) yield break;

            if (Unsupported.TryGetValue(shortType, out var why))
            {
                _report.Record(shortType, name, ItemOutcome.Dropped, why);
                yield break;
            }
            if (BindingTranslator.IsUnsupportedBuiltIn(shortType, out var builtInWhy))
            {
                _report.Record(shortType, name, ItemOutcome.Dropped, builtInWhy);
                yield break;
            }

            switch (shortType)
            {
                case "Layer":
                    foreach (var n in ConvertLayer(item, name)) yield return n;
                    yield break;

                case "WidgetItem":
                    foreach (var n in ConvertWidget(item, name)) yield return n;
                    yield break;

                case "ShiftLightItem":
                    foreach (var n in ConvertShiftLight(item, name)) yield return n;
                    yield break;

                case "ButtonItem":
                    foreach (var n in ConvertButton(item, name)) yield return n;
                    yield break;
            }

            IrNode? node = shortType switch
            {
                "RectangleItem" => Rectangle(item),
                "EllipseItem" => Ellipse(item),
                "ImageItem" => Image(item, name),
                "GradientItem" or "LinearShadeItem" => Gradient(item),
                "LinearGaugeItem" or "ProgressBarItem" or "VerticalLinearGaugeItem"
                    => LinearGauge(item, shortType),
                "CircularGaugeItem" => CircularGauge(item),
                "DialGaugeItem" => DialGauge(item),
                "GaugeNeedleItem" => Needle(item),
                _ => null,
            };

            // Everything else that renders text — a plain TextItem or any of the ~30
            // BuiltIn readouts, which are ordinary text items with an implied source.
            if (node == null)
            {
                if (!DjsonReader.Bool(item, "IsTextItem") && !shortType.EndsWith("Text", StringComparison.Ordinal)
                    && !shortType.EndsWith("Time", StringComparison.Ordinal)
                    && !string.Equals(shortType, "TextItem", StringComparison.Ordinal))
                {
                    _report.Record(shortType, name, ItemOutcome.Dropped, "unrecognised item type");
                    yield break;
                }
                node = Text(item);
            }

            node.Name = name;
            ApplyCommon(item, node);

            bool sourceOk = _bindings.Apply(item, node, shortType);
            if (!sourceOk)
            {
                _report.Record(shortType, name, ItemOutcome.Dropped,
                    "its telemetry source has no MOZA channel");
                yield break;
            }

            _report.Record(shortType, name, OutcomeFor(shortType));
            yield return node;
        }

        private static ItemOutcome OutcomeFor(string shortType) => shortType switch
        {
            "DialGaugeItem" or "GaugeNeedleItem" or "ProgressBarItem"
                or "LinearShadeItem" or "GradientItem" => ItemOutcome.Substituted,
            _ => ItemOutcome.Converted,
        };

        // ── Containers ────────────────────────────────────────────────────────

        private IEnumerable<IrNode> ConvertLayer(JObject item, string name)
        {
            // Repetitions clones the layer's children N times at a fixed offset — how
            // SimHub builds leaderboard rows and segmented RPM bars. mzdash has no such
            // concept, so expand to concrete copies here.
            int reps = Math.Max(1, DjsonReader.Int(item, "Repetitions", 1));
            double dx = DjsonReader.Num(item, "RepeatLeftOffset");
            double dy = DjsonReader.Num(item, "RepeatTopOffset");

            if (reps > MaxRepetitions)
            {
                _report.Notes.Add(
                    $"layer '{name}' repeats {reps}x — capped at {MaxRepetitions}");
                reps = MaxRepetitions;
            }

            for (int i = 0; i < reps; i++)
            {
                var layer = new IrNode
                {
                    Kind = IrKind.Layer,
                    Name = reps > 1 ? $"{name} {i + 1}" : name,
                    Visible = DjsonReader.Bool(item, "Visible", true),
                };
                layer.Effect.Opacity = Opacity(item);

                foreach (var child in DjsonReader.Items(item["Childrens"]))
                    AddConverted(layer.Children, child);

                if (i > 0) Translate(layer, dx * i, dy * i);

                // A layer that converted to nothing is noise in the output tree.
                if (layer.Children.Count > 0) yield return layer;
            }

            _report.Record("Layer", name,
                reps > 1 ? ItemOutcome.Substituted : ItemOutcome.Converted,
                reps > 1 ? $"expanded {reps} repetitions to concrete copies" : "");
        }

        private static void Translate(IrNode node, double dx, double dy)
        {
            node.X += dx;
            node.Y += dy;
            foreach (var c in node.Children) Translate(c, dx, dy);
        }

        /// <summary>A <c>WidgetItem</c> includes another <c>.djson</c>. mzdash is a single
        /// self-contained file, so the reference is resolved and inlined at convert time.</summary>
        private IEnumerable<IrNode> ConvertWidget(JObject item, string name)
        {
            string fileName = DjsonReader.Str(item, "FileName");
            if (fileName.Length == 0)
            {
                _report.Record("WidgetItem", name, ItemOutcome.Dropped, "no FileName");
                yield break;
            }

            string? path = ResolveWidgetPath(fileName);
            if (path == null)
            {
                _report.Record("WidgetItem", name, ItemOutcome.Dropped,
                    $"referenced file '{fileName}' not found");
                yield break;
            }

            if (!_widgetStack.Add(path))
            {
                _report.Record("WidgetItem", name, ItemOutcome.Dropped,
                    $"'{fileName}' includes itself");
                yield break;
            }

            try
            {
                var (root, error) = DjsonReader.Load(path);
                if (root == null)
                {
                    _report.Record("WidgetItem", name, ItemOutcome.Dropped,
                        $"'{fileName}' failed to parse: {error}");
                    yield break;
                }

                var host = new IrNode
                {
                    Kind = IrKind.Layer,
                    Name = name,
                    Visible = DjsonReader.Bool(item, "Visible", true),
                };
                host.Effect.Opacity = Opacity(item);

                // A widget file carries its own Screens[]; the item embeds the first one.
                foreach (var screen in DjsonReader.Items(root["Screens"]))
                {
                    foreach (var child in DjsonReader.Items(screen["Items"]))
                        AddConverted(host.Children, child);
                    break;
                }

                // Widget contents are authored against the widget's own canvas origin, so
                // shift them to where the host item sits.
                double left = DjsonReader.Num(item, "Left");
                double top = DjsonReader.Num(item, "Top");
                if (left != 0 || top != 0)
                    foreach (var c in host.Children) Translate(c, left, top);

                _report.Record("WidgetItem", name, ItemOutcome.Converted,
                    $"inlined '{fileName}'");
                if (host.Children.Count > 0) yield return host;
            }
            finally
            {
                _widgetStack.Remove(path);
            }
        }

        private string? ResolveWidgetPath(string fileName)
        {
            // Same folder first, then SimHub's shared _Library tree one level up.
            string direct = Path.Combine(_sourceDir, fileName);
            if (File.Exists(direct)) return direct;

            try
            {
                var parent = Directory.GetParent(_sourceDir);
                if (parent == null) return null;
                string library = Path.Combine(parent.FullName, "_Library");
                if (!Directory.Exists(library)) return null;

                foreach (var hit in Directory.GetFiles(library, fileName, SearchOption.AllDirectories))
                    return hit;
            }
            catch (Exception ex)
            {
                _report.Notes.Add($"widget lookup for '{fileName}' failed: {ex.Message}");
            }
            return null;
        }

        // ── Leaf items ────────────────────────────────────────────────────────

        private IrNode Rectangle(JObject item) => new IrNode
        {
            Kind = IrKind.Rectangle,
            Background = ColorOf(item, "BackgroundColor", "#00000000"),
        };

        private IrNode Ellipse(JObject item)
        {
            var node = new IrNode
            {
                Kind = IrKind.Ellipse,
                Background = ColorOf(item, "FillColor", "#00000000"),
            };
            // EllipseColor/Thickness are the ellipse's own stroke, separate from the
            // generic BorderStyle every item carries.
            node.Border.Color = ColorOf(item, "EllipseColor", "#00000000");
            double thickness = DjsonReader.Num(item, "EllipseThickness");
            node.Border.Top = node.Border.Bottom = node.Border.Left = node.Border.Right = thickness;
            node.BorderWidth = thickness;
            node.BorderColor = node.Border.Color;
            return node;
        }

        private IrNode Image(JObject item, string name)
        {
            var node = new IrNode { Kind = IrKind.Image };
            string imageName = DjsonReader.Str(item, "Image");
            if (imageName.Length > 0 && _images.TryGetValue(imageName, out var src))
                node.ImageSrc = src;
            else if (imageName.Length > 0)
                _report.Notes.Add($"image '{imageName}' (item '{name}') not found in the resource archive");
            return node;
        }

        private IrNode Text(JObject item)
        {
            string family = DjsonReader.Str(item, "Font", "");
            string resolved = _fonts.Resolve(family, out bool mapped);
            if (!mapped && family.Length > 0) _report.NoteUnmappedFont(family);

            var node = new IrNode
            {
                Kind = IrKind.Text,
                Background = ColorOf(item, "BackgroundColor", "#00000000"),
                Text = new IrText
                {
                    // BuiltIn readouts carry a design-time placeholder under DesignerText;
                    // it is what shows before the first telemetry frame arrives.
                    Text = DjsonReader.Has(item, "Text")
                        ? DjsonReader.Str(item, "Text")
                        : DjsonReader.Str(item, "DesignerText"),
                    FontFamily = resolved,
                    FontSize = DjsonReader.Num(item, "FontSize", 20),
                    FontWeight = ParseWeight(DjsonReader.Str(item, "FontWeight", "Normal")),
                    Color = ColorOf(item, "TextColor", "#FFFFFFFF"),
                    HorizontalAlignment = HAlign(DjsonReader.Int(item, "HorizontalAlignment", 1)),
                    VerticalAlignment = VAlign(DjsonReader.Int(item, "VerticalAlignment", 1)),
                    WrapText = DjsonReader.Int(item, "TextWrapping", 0) != 0,
                },
            };

            var pad = DjsonReader.Obj(item, "TextPadding");
            if (pad != null)
            {
                node.Text!.PaddingTop = DjsonReader.Num(pad, "PaddingTop");
                node.Text.PaddingBottom = DjsonReader.Num(pad, "PaddingBottom");
                node.Text.PaddingLeft = DjsonReader.Num(pad, "PaddingLeft");
                node.Text.PaddingRight = DjsonReader.Num(pad, "PaddingRight");
            }

            // Text shadows are native in mzdash's textEffect block.
            node.Effect.ShadowDepth = DjsonReader.Num(item, "ShadowDepth");
            node.Effect.ShadowBlur = DjsonReader.Num(item, "ShadowBlur");
            if (DjsonReader.Has(item, "ShadowColor"))
                node.Effect.ShadowColor = ColorOf(item, "ShadowColor", "#00000000");

            return node;
        }

        private IrNode Gradient(JObject item)
        {
            var node = new IrNode
            {
                Kind = IrKind.Rectangle,
                Background = ParseGradient(item),
            };
            // LinearShadeItem carries its corner radii directly rather than in BorderStyle.
            node.Border.RadiusTopLeft = DjsonReader.Num(item, "RadiusTopLeft");
            node.Border.RadiusTopRight = DjsonReader.Num(item, "RadiusTopRight");
            node.Border.RadiusBottomLeft = DjsonReader.Num(item, "RadiusBottomLeft");
            node.Border.RadiusBottomRight = DjsonReader.Num(item, "RadiusBottomRight");
            return node;
        }

        private IrNode LinearGauge(JObject item, string shortType)
        {
            bool vertical = string.Equals(shortType, "VerticalLinearGaugeItem", StringComparison.Ordinal)
                         || DjsonReader.Int(item, "GaugeOrientation", 0) == 1;

            string colorKey = DjsonReader.Has(item, "ProgressBarColor") ? "ProgressBarColor" : "GaugeColor";
            int alignRaw = DjsonReader.Has(item, "ProgressBarAlignment")
                ? DjsonReader.Int(item, "ProgressBarAlignment")
                : DjsonReader.Int(item, "GaugeAlignment");

            return new IrNode
            {
                Kind = IrKind.LinearGauge,
                Background = ColorOf(item, "BackgroundColor", "#00000000"),
                Gauge = new IrGauge
                {
                    Minimum = DjsonReader.Num(item, "Minimum", 0),
                    Maximum = DjsonReader.Num(item, "Maximum", 100),
                    Value = DjsonReader.Num(item, "Value", 0),
                    GaugeColor = ColorOf(item, colorKey, "#FFFFFFFF"),
                    BackgroundColor = ColorOf(item, "BackgroundColor", "#00000000"),
                    Vertical = vertical,
                    Alignment = GaugeAlign(alignRaw),
                },
            };
        }

        private IrNode CircularGauge(JObject item)
        {
            double minAngle = DjsonReader.Num(item, "MinAngle", 300);
            double maxAngle = DjsonReader.Num(item, "MaxAngle", minAngle + 360);

            return new IrNode
            {
                Kind = IrKind.CircularGauge,
                Background = ColorOf(item, "BackgroundColor", "#00000000"),
                Gauge = new IrGauge
                {
                    Minimum = DjsonReader.Num(item, "MinValue", 0),
                    Maximum = DjsonReader.Num(item, "MaxValue", 100),
                    Value = DjsonReader.Num(item, "Value", 0),
                    GaugeColor = ColorOf(item, "CircleGaugeColor", "#FFFFFFFF"),
                    BackgroundColor = ParseColor(
                        DjsonReader.Str(item, "CircleGaugeBackgroundColor", "#00000000")),
                    StartAngle = minAngle,
                    SweepAngle = maxAngle - minAngle,
                    StrokeThickness = DjsonReader.Num(item, "StrokeThickness", 4.5),
                },
            };
        }

        /// <summary>A dial gauge's arc survives as a CircularGauge; its tick marks, scale
        /// labels and glass effect do not.</summary>
        private IrNode DialGauge(JObject item)
        {
            var node = new IrNode
            {
                Kind = IrKind.CircularGauge,
                Background = ColorOf(item, "DialBackgroundColor", "#00000000"),
                Gauge = new IrGauge
                {
                    Minimum = DjsonReader.Num(item, "Minimum", 0),
                    Maximum = DjsonReader.Num(item, "Maximum", 100),
                    Value = DjsonReader.Num(item, "Value", 0),
                    GaugeColor = ColorOf(item, "RadialGaugeColor", "#FFFFFFFF"),
                    StartAngle = DjsonReader.Num(item, "StartAngle", 300),
                    SweepAngle = DjsonReader.Num(item, "SweepAngle", 360),
                    StrokeThickness = DjsonReader.Num(item, "RadialGaugeThickness", 8),
                },
            };
            // mzdash has a native DialGauge.qml (MOZA's own factory dashboards use it),
            // with its own value/radialGauge/dial/majorTicks/scale blocks. Mapping onto
            // it would preserve the ticks and scale labels this substitution loses;
            // CircularGauge is the smaller, already-proven target for now.
            _report.Notes.Add(
                $"dial gauge '{DjsonReader.Str(item, "Name", "DialGaugeItem")}': "
                + "arc kept as a circular gauge, tick marks and scale labels dropped");
            return node;
        }

        /// <summary>
        /// A needle becomes a thin rectangle pivoted by <c>effect.rotation</c>.
        ///
        /// <para>Binding <c>effect.rotation</c> is <b>unverified against firmware</b> — no
        /// factory dashboard binds it, though <c>general.x</c> is bound in two of them, so
        /// arbitrary dotted paths do appear bindable. Emitted so the hardware pass can
        /// settle it; if the wheel rejects it the needle simply sits at its design angle.</para>
        /// </summary>
        private IrNode Needle(JObject item)
        {
            var node = new IrNode
            {
                Kind = IrKind.Rectangle,
                Background = ColorOf(item, "BackgroundColor", "#FFFFFFFF"),
            };
            node.Effect.Rotation = DjsonReader.Num(item, "ActualAngle");
            _report.Notes.Add("needle emitted as a rotated rectangle — "
                            + "binding effect.rotation is unverified on firmware");
            return node;
        }

        /// <summary>One SimHub shift light is one LED. Rendered as an ellipse whose
        /// visibility tracks the RPM percentage crossing this LED's share of the bar.</summary>
        private IEnumerable<IrNode> ConvertShiftLight(JObject item, string name)
        {
            int index = Math.Max(1, DjsonReader.Int(item, "LedNumber", 1));
            int total = Math.Max(1, DjsonReader.Int(item, "TotalLeds", 1));
            double threshold = (double)index / (total + 1) * 100.0;

            var node = new IrNode
            {
                Kind = IrKind.Ellipse,
                Name = name,
                Background = ColorOf(item, "BackgroundColor", "#FFFF0000"),
            };
            ApplyCommon(item, node);

            node.Bindings.Add(new IrBinding
            {
                Target = "general.visible",
                Expression = string.Format(CultureInfo.InvariantCulture,
                    "(({0})>={1})",
                    ChannelResolver.ChannelRead("v1/gameData/CarSettings_CurrentDisplayedRPMPercent"),
                    threshold.ToString("0.##", CultureInfo.InvariantCulture)),
                Format = JsFormatters.ChainIdentity,
            });
            _report.Channels.Add("v1/gameData/CarSettings_CurrentDisplayedRPMPercent");

            _report.Record("ShiftLightItem", name, ItemOutcome.Substituted,
                "LED image replaced by an RPM-threshold ellipse "
                + "(the wheel's own RPM LEDs usually cover this already)");
            yield return node;
        }

        /// <summary>A button has no click target on the wheel, so only its face survives:
        /// the box, plus its caption when it has one.</summary>
        private IEnumerable<IrNode> ConvertButton(JObject item, string name)
        {
            var box = Rectangle(item);
            box.Name = name;
            ApplyCommon(item, box);
            _report.Record("ButtonItem", name, ItemOutcome.Substituted,
                "rendered as a static box — the wheel has no click target");
            yield return box;

            string caption = DjsonReader.Str(item, "Text");
            if (caption.Length == 0) yield break;

            var label = Text(item);
            label.Name = name + " caption";
            ApplyCommon(item, label);
            label.Background = IrColor.Transparent();
            yield return label;
        }

        // ── Shared property mapping ───────────────────────────────────────────

        private void ApplyCommon(JObject item, IrNode node)
        {
            node.X = DjsonReader.Num(item, "Left");
            node.Y = DjsonReader.Num(item, "Top");
            node.Width = DjsonReader.Num(item, "Width");
            node.Height = DjsonReader.Num(item, "Height");
            node.Visible = DjsonReader.Bool(item, "Visible", true);

            node.Effect.Opacity = Opacity(item);
            node.Effect.Rotation = node.Effect.Rotation != 0
                ? node.Effect.Rotation
                : DjsonReader.Num(item, "Rotation");
            if (DjsonReader.Bool(item, "EnableBlur"))
                node.Effect.BlurRadius = DjsonReader.Num(item, "BlurRadius");
            if (DjsonReader.Bool(item, "BlinkEnabled"))
            {
                node.Effect.BlinkEnabled = true;
                double delay = DjsonReader.Num(item, "BlinkDelay", 250);
                if (delay > 0) node.Effect.BlinkDelay = delay;
            }

            ApplyBorder(item, node);
        }

        /// <summary>Borders arrive two ways: a <c>BorderStyle</c> object on newer items, or
        /// flat <c>BorderTop</c>/<c>BorderLeft</c>… properties on older ones. Prefer the
        /// object, since items carrying both leave the flat ones stale.</summary>
        private void ApplyBorder(JObject item, IrNode node)
        {
            var style = DjsonReader.Obj(item, "BorderStyle");
            JObject src = (style != null && style.HasValues) ? style : item;

            double all = DjsonReader.Num(src, "AllBorders");
            node.Border.Top = DjsonReader.Num(src, "BorderTop", all);
            node.Border.Bottom = DjsonReader.Num(src, "BorderBottom", all);
            node.Border.Left = DjsonReader.Num(src, "BorderLeft", all);
            node.Border.Right = DjsonReader.Num(src, "BorderRight", all);

            double allRadius = DjsonReader.Num(src, "AllCornerRadius");
            node.Border.RadiusTopLeft = DjsonReader.Num(src, "RadiusTopLeft", allRadius);
            node.Border.RadiusTopRight = DjsonReader.Num(src, "RadiusTopRight", allRadius);
            node.Border.RadiusBottomLeft = DjsonReader.Num(src, "RadiusBottomLeft", allRadius);
            node.Border.RadiusBottomRight = DjsonReader.Num(src, "RadiusBottomRight", allRadius);

            if (DjsonReader.Has(src, "BorderColor"))
                node.Border.Color = ColorOf(src, "BorderColor", "#00000000");
            else if (DjsonReader.Has(item, "BorderColor"))
                node.Border.Color = ColorOf(item, "BorderColor", "#00000000");

            // general.borderWidth/borderRadius are the uniform shorthand the mzdash
            // renderer uses when every side matches; borderStyle covers the rest.
            node.BorderColor = node.Border.Color;
            node.BorderWidth = node.Border.IsUniformWidth ? node.Border.Top : 0;
            node.BorderRadius = node.Border.IsUniformRadius ? node.Border.RadiusTopLeft : 0;
        }

        /// <summary>SimHub is inconsistent: some items store opacity as 0..1, others as
        /// 0..100. Anything at or below 1 is read as a fraction — a genuine 1% opacity is
        /// invisible and nobody authors it.</summary>
        private static double Opacity(JObject item)
        {
            if (!DjsonReader.Has(item, "Opacity")) return 100;
            double v = DjsonReader.Num(item, "Opacity", 100);
            if (v <= 1.0) v *= 100.0;
            return Math.Max(0, Math.Min(100, v));
        }

        /// <summary>SimHub writes <c>#AARRGGBB</c> and so does mzdash — same channel order,
        /// no swap. Bare <c>#RRGGBB</c> is promoted to fully opaque.</summary>
        internal static IrColor ParseColor(string? value)
        {
            string s = (value ?? "").Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);

            if (s.Length == 6) return IrColor.Solid("#FF" + s.ToUpperInvariant());
            if (s.Length == 8) return IrColor.Solid("#" + s.ToUpperInvariant());
            return IrColor.Transparent();
        }

        /// <summary>
        /// Read a colour property that may be either a hex string or a serialised WPF
        /// brush. SimHub uses the object form for gradient fills, and reading it as a
        /// string used to throw — it is what made five stock dashboards unconvertible.
        /// </summary>
        internal static IrColor ColorOf(JObject? item, string name, string fallback = "#00000000")
        {
            var token = item?[name];
            if (token == null || token.Type == JTokenType.Null) return ParseColor(fallback);
            if (token is JValue) return ParseColor(DjsonReader.Str(item, name, fallback));

            var brush = ParseBrush(token);
            return brush ?? ParseColor(fallback);
        }

        /// <summary>Read a XAML <c>LinearGradientBrush</c> as Newtonsoft rendered it —
        /// attributes prefixed with <c>@</c>, and a <c>GradientStop</c> that is an array
        /// for two or more stops but a bare object for exactly one. Null when the token
        /// is not a brush we recognise.</summary>
        private static IrColor? ParseBrush(JToken? token)
        {
            var brush = DjsonReader.Obj(token as JObject, "LinearGradientBrush");
            var stopsHolder = DjsonReader.Obj(brush, "LinearGradientBrush.GradientStops");
            var raw = stopsHolder?["GradientStop"];

            var stops = new List<IrGradientStop>();
            if (raw is JArray arr)
            {
                foreach (var s in arr)
                    if (s is JObject o) AddStop(stops, o);
            }
            else if (raw is JObject single)
            {
                AddStop(stops, single);
            }

            if (stops.Count == 0) return null;

            stops.Sort((a, b) => a.Position.CompareTo(b.Position));
            var gradient = new IrColor { IsGradient = true, Hex = stops[0].Hex };
            gradient.Stops.AddRange(stops);
            return gradient;
        }

        private IrColor ParseGradient(JObject item)
        {
            var fromBrush = ParseBrush(item["Color"]);
            if (fromBrush != null) return fromBrush;

            // LinearShadeItem also carries flat StartColor/EndColor.
            if (!DjsonReader.Has(item, "StartColor") && !DjsonReader.Has(item, "EndColor"))
                return ColorOf(item, "BackgroundColor");

            var color = new IrColor { IsGradient = true };
            color.Stops.Add(new IrGradientStop
            {
                Hex = ColorOf(item, "StartColor", "#FF000000").Hex,
                Position = 0,
            });
            color.Stops.Add(new IrGradientStop
            {
                Hex = ColorOf(item, "EndColor", "#FF000000").Hex,
                Position = 1,
            });
            color.Hex = color.Stops[0].Hex;
            return color;
        }

        private static void AddStop(List<IrGradientStop> into, JObject stop)
        {
            string hex = DjsonReader.Str(stop, "@Color", DjsonReader.Str(stop, "Color", ""));
            if (hex.Length == 0) return;
            string offsetText = DjsonReader.Str(stop, "@Offset", DjsonReader.Str(stop, "Offset", "0"));
            double.TryParse(offsetText, NumberStyles.Float, CultureInfo.InvariantCulture, out double offset);
            into.Add(new IrGradientStop
            {
                Hex = ParseColor(hex).Hex,
                Position = Math.Max(0, Math.Min(1, offset)),
            });
        }

        /// <summary>SimHub stores font weight as a WPF <c>FontWeight</c> name.</summary>
        private static int ParseWeight(string name) => name.Trim().ToLowerInvariant() switch
        {
            "thin" => 100,
            "extralight" or "ultralight" => 200,
            "light" => 300,
            "normal" or "regular" => 400,
            "medium" => 500,
            "semibold" or "demibold" => 600,
            "bold" => 700,
            "extrabold" or "ultrabold" => 800,
            "black" or "heavy" => 900,
            _ => int.TryParse(name, out int n) ? n : 400,
        };

        // System.Windows.HorizontalAlignment / VerticalAlignment: the .djson property
        // names match those WPF types exactly, and Center being the most common value in
        // the corpus (378 of 832 / 505 of 832) is consistent with that reading.
        private static string HAlign(int v) => v switch
        {
            0 => "AlignLeft",
            2 => "AlignRight",
            _ => "AlignCenter",
        };

        private static string VAlign(int v) => v switch
        {
            0 => "AlignTop",
            2 => "AlignBottom",
            _ => "AlignCenter",
        };

        /// <summary>mzdash's <c>linearGauge.gaugeAlignment</c> enum is
        /// <c>AlignLeft</c>/<c>AlignHCenter</c>/<c>AlignRight</c> — it has no vertical
        /// spellings even for a vertical gauge, where the renderer reads Left as "grow
        /// from the near edge". Emitting AlignTop/AlignBottom puts a value outside the
        /// enum into the file (MOZA Dashboard Studio's own element metadata,
        /// <c>:/elementMetaProperty</c>, is the authority here).</summary>
        private static string GaugeAlign(int v) => v switch
        {
            1 => "AlignHCenter",
            2 => "AlignRight",
            _ => "AlignLeft",
        };
    }
}
