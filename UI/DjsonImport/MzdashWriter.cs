using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>
    /// Serialises an <see cref="IrDashboard"/> as a <c>.mzdash</c> file.
    ///
    /// <para>Every node carries the same envelope — <c>actions</c>, <c>binding</c>,
    /// <c>children</c>, <c>effect</c>, <c>general</c>, <c>id</c>, <c>macros</c>,
    /// <c>name</c>, <c>plugins</c>, <c>type</c> — plus one block named after the widget.
    /// The exact per-type key sets here were taken from all 24 factory dashboards, and the
    /// differences between them are real: <c>Layer.qml</c> has no geometry at all,
    /// <c>Window.qml</c>'s effect block has only two keys, and only Rectangle, Ellipse and
    /// CircularGauge carry an <c>innerShadow</c>. Emitting a block the renderer does not
    /// expect for that type is the likeliest way to get a silently blank widget.</para>
    ///
    /// <para>Keys are inserted alphabetically and the file is written with 4-space
    /// indentation, matching factory files so a converted dashboard diffs cleanly
    /// against one.</para>
    /// </summary>
    public sealed class MzdashWriter
    {
        /// <summary>Factory dashboards all carry this schema version.</summary>
        public const string SchemaVersion = "1.1.1";

        /// <summary>Dashboard Studio's own schema caps element ids below 10000
        /// (<c>:/schema/dashboard_schema.json</c>, <c>exclusiveMaximum</c>), so a
        /// converted dashboard has at most this many nodes counting the Window.</summary>
        public const int MaxElementId = 9999;

        private int _nextId = 1;   // 0 is reserved for the Window node

        /// <summary>Number of nodes dropped because the id space ran out. Only a
        /// pathologically large source can trigger it; the caller reports it rather than
        /// writing a file the editor would reject.</summary>
        public int OverflowedNodes { get; private set; }

        /// <summary>Build the document. <paramref name="idealDeviceInfos"/> comes from the
        /// connected wheel and declares the display the dashboard was authored for; pass
        /// null when no wheel is connected rather than inventing one, since the built-in
        /// literal in Dashboard Studio describes one specific wheel.</summary>
        public JObject Build(IrDashboard dashboard, int canvasWidth, int canvasHeight,
                             JArray? idealDeviceInfos)
        {
            _nextId = 1;

            var window = new JObject
            {
                ["actions"] = new JObject(),
                ["binding"] = new JObject(),
                ["borderStyle"] = BorderStyle(new IrBorder()),
                ["children"] = new JArray(),
                ["effect"] = new JObject
                {
                    // The Window's effect block really is just these two.
                    ["gravitySensorEnabled"] = false,
                    ["rotation"] = 0,
                },
                ["general"] = General(new IrNode
                {
                    Width = canvasWidth,
                    Height = canvasHeight,
                    Background = IrColor.Solid("#FF000000"),
                }),
                ["id"] = 0,
                ["imageResources"] = new JArray(dashboard.ImageResources),
                ["lastModified"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["macros"] = new JObject(),
                ["name"] = dashboard.Name,
                ["plugins"] = new JArray(),
                ["type"] = "Window.qml",
                ["version"] = SchemaVersion,
                ["window"] = new JObject
                {
                    ["GUID"] = "{" + Guid.NewGuid().ToString("D") + "}",
                    ["defaultScreenId"] = 0,
                    ["idealDeviceInfos"] = idealDeviceInfos ?? new JArray(),
                },
            };

            var children = (JArray)window["children"]!;
            foreach (var screen in dashboard.Screens)
                children.Add(Node(screen));

            return window;
        }

        /// <summary>Write the document with factory formatting.</summary>
        public static void Save(JObject document, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // UTF-8 without a BOM: factory files carry none and the upload path hashes
            // the bytes, so a stray BOM would change the content MD5.
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            using var json = new JsonTextWriter(writer)
            {
                Formatting = Formatting.Indented,
                Indentation = 4,
                IndentChar = ' ',
            };
            document.WriteTo(json);
        }

        // ── Nodes ─────────────────────────────────────────────────────────────

        private JObject Node(IrNode node)
        {
            var o = new JObject
            {
                ["actions"] = new JObject(),
                ["binding"] = Binding(node),
            };

            // Layer is the one type with neither geometry nor a border block.
            if (node.Kind != IrKind.Layer)
                o["borderStyle"] = BorderStyle(node.Border);

            var children = new JArray();
            foreach (var c in node.Children)
            {
                if (_nextId > MaxElementId) { OverflowedNodes++; continue; }
                children.Add(Node(c));
            }
            o["children"] = children;

            if (node.Kind == IrKind.CircularGauge) o["circularGauge"] = CircularGauge(node);
            o["effect"] = Effect(node);
            if (node.Kind == IrKind.Ellipse) o["ellipse"] = Ellipse(node);
            o["general"] = node.Kind == IrKind.Layer ? LayerGeneral(node) : General(node);

            if (node.Kind == IrKind.Screen) o["guides"] = new JArray();
            o["id"] = _nextId++;
            if (node.Kind == IrKind.Image || node.Kind == IrKind.Screen)
                o["image"] = new JObject { ["src"] = node.ImageSrc ?? "" };
            if (node.Kind == IrKind.LinearGauge) o["linearGauge"] = LinearGauge(node);

            o["macros"] = new JObject();
            o["name"] = node.Name;
            o["plugins"] = new JArray();

            if (node.Kind == IrKind.Text)
            {
                o["text"] = Text(node);
                o["textEffect"] = TextEffect(node);
            }

            o["type"] = TypeName(node.Kind);
            return SortKeys(o);
        }

        private static string TypeName(IrKind kind) => kind switch
        {
            IrKind.Screen => "Screen.qml",
            IrKind.Layer => "Layer.qml",
            IrKind.Rectangle => "Rectangle.qml",
            IrKind.Ellipse => "Ellipse.qml",
            IrKind.Text => "Text.qml",
            IrKind.Image => "Image.qml",
            IrKind.LinearGauge => "LinearGauge.qml",
            IrKind.CircularGauge => "CircularGauge.qml",
            _ => "Rectangle.qml",
        };

        private static JObject Binding(IrNode node)
        {
            var o = new JObject();
            foreach (var b in node.Bindings)
            {
                if (string.IsNullOrEmpty(b.Target) || string.IsNullOrEmpty(b.Expression)) continue;
                o[b.Target] = new JObject
                {
                    // Always a 2-step chain: compute, then format. 630 of the 633 bindings
                    // in factory dashboards have exactly this shape.
                    ["methods"] = new JArray(b.Expression, b.Format),
                    ["type"] = "METHOD_CHAINING",
                };
            }
            return o;
        }

        private static JObject General(IrNode node) => new JObject
        {
            ["backgroundColor"] = Color(node.Background),
            ["borderColor"] = Color(node.BorderColor),
            ["borderRadius"] = Round(node.BorderRadius),
            ["borderWidth"] = Round(node.BorderWidth),
            ["height"] = Round(node.Height),
            ["locked"] = node.Locked,
            ["visible"] = node.Visible,
            ["width"] = Round(node.Width),
            ["x"] = Round(node.X),
            ["y"] = Round(node.Y),
        };

        private static JObject LayerGeneral(IrNode node) => new JObject
        {
            ["locked"] = node.Locked,
            ["visible"] = node.Visible,
        };

        private static JObject Effect(IrNode node)
        {
            var e = node.Effect;

            if (node.Kind == IrKind.Layer)
            {
                return new JObject
                {
                    ["blinkDelay"] = Round(e.BlinkDelay),
                    ["blinkEnabled"] = e.BlinkEnabled,
                    ["opacity"] = Round(e.Opacity),
                };
            }

            var o = new JObject
            {
                ["blinkDelay"] = Round(e.BlinkDelay),
                ["blinkEnabled"] = e.BlinkEnabled,
                ["blurRadius"] = Round(e.BlurRadius),
                ["gravitySensorEnabled"] = false,
                ["opacity"] = Round(e.Opacity),
                ["rotation"] = Round(e.Rotation),
            };

            // Only these three types carry an inner shadow in factory files.
            if (node.Kind is IrKind.Rectangle or IrKind.Ellipse or IrKind.CircularGauge)
            {
                o["innerShadow"] = new JObject
                {
                    ["blur"] = 0,
                    ["color"] = Color(IrColor.Solid("#FFFFFF")),
                    ["size"] = 0,
                };
            }

            // Text, Rectangle, Ellipse and CircularGauge carry an outer shadow;
            // Image, LinearGauge and Screen do not.
            if (node.Kind is IrKind.Text or IrKind.Rectangle or IrKind.Ellipse or IrKind.CircularGauge)
            {
                o["outerShadow"] = new JObject
                {
                    ["blur"] = Round(e.ShadowBlur),
                    ["color"] = Color(e.ShadowColor),
                    ["depth"] = Round(e.ShadowDepth),
                    ["direction"] = Round(e.ShadowDirection),
                };
            }

            return SortKeys(o);
        }

        private static JObject BorderStyle(IrBorder b) => new JObject
        {
            ["allBorders"] = Round(b.IsUniformWidth ? b.Top : 0),
            ["allCornerRadius"] = Round(b.IsUniformRadius ? b.RadiusTopLeft : 0),
            ["borderColor"] = Color(b.Color),
            ["bordersBottom"] = Round(b.Bottom),
            ["bordersLeft"] = Round(b.Left),
            ["bordersRight"] = Round(b.Right),
            ["bordersTop"] = Round(b.Top),
            ["radiusBottomLeft"] = Round(b.RadiusBottomLeft),
            ["radiusBottomRight"] = Round(b.RadiusBottomRight),
            ["radiusTopLeft"] = Round(b.RadiusTopLeft),
            ["radiusTopRight"] = Round(b.RadiusTopRight),
        };

        private static JObject Text(IrNode node)
        {
            var t = node.Text ?? new IrText();
            return new JObject
            {
                ["fontColor"] = Color(t.Color),
                ["fontFamily"] = t.FontFamily,
                ["fontSize"] = Round(t.FontSize),
                ["fontWeight"] = t.FontWeight,
                ["horizontalAlignment"] = t.HorizontalAlignment,
                ["paddingBottom"] = Round(t.PaddingBottom),
                ["paddingLeft"] = Round(t.PaddingLeft),
                ["paddingRight"] = Round(t.PaddingRight),
                ["paddingTop"] = Round(t.PaddingTop),
                ["text"] = t.Text,
                ["verticalAlignment"] = t.VerticalAlignment,
                ["wrapText"] = t.WrapText,
            };
        }

        private static JObject TextEffect(IrNode node) => new JObject
        {
            ["shadowBlur"] = Round(node.Effect.ShadowBlur),
            ["shadowColor"] = Color(node.Effect.ShadowColor),
            ["shadowDepth"] = Round(node.Effect.ShadowDepth),
            ["shadowDirection"] = Round(node.Effect.ShadowDirection),
        };

        private static JObject Ellipse(IrNode node) => new JObject
        {
            ["backgroundColor"] = Color(node.Background),
            ["borderColor"] = Color(node.Border.Color),
            ["borderWidth"] = Round(node.Border.Top),
        };

        private static JObject LinearGauge(IrNode node)
        {
            var g = node.Gauge ?? new IrGauge();
            return new JObject
            {
                ["alternateStyle"] = new JObject
                {
                    ["gaugeColor"] = Color(g.GaugeColor),
                    ["gaugeImage"] = "",
                    ["useAlternateStyle"] = false,
                },
                ["backgroundImage"] = "",
                ["gaugeAlignment"] = g.Alignment,
                ["gaugeColor"] = Color(g.GaugeColor),
                ["gaugeImage"] = "",
                ["gaugeOrientation"] = g.Vertical ? "Vertical" : "Horizontal",
                ["maximum"] = Round(g.Maximum),
                ["minimum"] = Round(g.Minimum),
                ["value"] = Round(g.Value),
            };
        }

        private static JObject CircularGauge(IrNode node)
        {
            var g = node.Gauge ?? new IrGauge();
            return new JObject
            {
                ["backgroundColor"] = Color(g.BackgroundColor),
                ["backgroundImage"] = "",
                ["gaugeColor"] = Color(g.GaugeColor),
                ["gaugeImage"] = "",
                ["maximum"] = Round(g.Maximum),
                ["minimum"] = Round(g.Minimum),
                ["startAngle"] = Round(g.StartAngle),
                ["strokeThickness"] = Round(g.StrokeThickness),
                ["sweepAngle"] = Round(g.SweepAngle),
                ["value"] = Round(g.Value),
            };
        }

        /// <summary>Factory gradients all carry this same handle triple — it is the
        /// editor's default and the renderer expects three entries.</summary>
        private static JArray DefaultGradientHandles() => new JArray
        {
            new JObject { ["x"] = 0, ["y"] = 0 },
            new JObject { ["x"] = 1, ["y"] = 0 },
            new JObject { ["x"] = 0.25, ["y"] = 5 },
        };

        private static JObject Color(IrColor c)
        {
            if (!c.IsGradient || c.Stops.Count == 0)
            {
                return new JObject
                {
                    ["color"] = c.Hex,
                    ["type"] = "SOLID",
                };
            }

            var stops = new JArray();
            foreach (var s in c.Stops)
            {
                stops.Add(new JObject
                {
                    ["color"] = s.Hex,
                    ["position"] = Round(s.Position, 6),
                });
            }

            return new JObject
            {
                // The solid colour stays alongside the stops as the renderer's fallback,
                // exactly as factory files carry it.
                ["color"] = c.Stops[0].Hex,
                ["gradientHandlePositions"] = DefaultGradientHandles(),
                ["gradientStops"] = stops,
                ["type"] = "GRADIENT_LINEAR",
            };
        }

        /// <summary>Keep the fractional noise out of the file. Factory dashboards carry
        /// long doubles where the editor computed them, but a converted file reads far
        /// better — and diffs against ground truth far better — without them.</summary>
        private static JValue Round(double v, int digits = 3)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return new JValue(0);
            double r = Math.Round(v, digits, MidpointRounding.AwayFromZero);
            if (Math.Abs(r - Math.Round(r)) < 1e-9) return new JValue((long)Math.Round(r));
            return new JValue(r);
        }

        /// <summary>Reorder an object's keys alphabetically, matching factory files.</summary>
        private static JObject SortKeys(JObject o)
        {
            var names = new List<string>();
            foreach (var p in o.Properties()) names.Add(p.Name);
            names.Sort(StringComparer.Ordinal);

            var sorted = new JObject();
            foreach (var n in names) sorted[n] = o[n];
            return sorted;
        }

        /// <summary>The wheel derives a dashboard's folder and display name from the file
        /// name, so strip anything the firmware's path handling would choke on.</summary>
        public static string SafeName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in (name ?? "").Trim())
            {
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_') sb.Append(c);
            }
            string s = sb.ToString().Trim();
            return s.Length == 0 ? "Converted" : s;
        }

        /// <summary>Formatting helper for the report.</summary>
        internal static string Fmt(double v)
            => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
