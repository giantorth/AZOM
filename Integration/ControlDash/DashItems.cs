using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Integration.ControlDash
{
    /// <summary>A generated dashboard and its metadata sidecar.</summary>
    internal sealed class DashOutput
    {
        public string Djson = "";
        public string Metadata = "";
    }

    /// <summary>
    /// Dash Studio item, binding and envelope primitives shared by the generated
    /// dashboards (see docs/simhub.md "Dash Studio dashboards"), and their palette.
    /// </summary>
    internal static class DashItems
    {
        internal const string Font = "Roboto";

        internal const string BgColor = "#FF14171C";
        internal const string BarColor = "#FF1C2027";
        internal const string RowColor = "#FF1B1F26";
        internal const string BtnColor = "#FF2B313B";
        internal const string GridColor = "#FF2A303A";
        internal const string GuideColor = "#FF4A5260";
        internal const string AccentColor = "#FF3FA9F5";
        internal const string OnColor = "#FF2E8B57";
        internal const string OffColor = "#FF3A3F48";
        internal const string TextColor = "#FFFFFFFF";
        internal const string LabelColor = "#FFB8C0CC";
        internal const string Transparent = "#00FFFFFF";

        private const string ItemNs = "SimHub.Plugins.OutputPlugins.GraphicalDash.Models.";
        internal const string Asm = ", SimHub.Plugins";

        // ── Envelope ────────────────────────────────────────────────────────

        /// <summary>The dashboard and its metadata; every screen shows in game and idle.</summary>
        internal static DashOutput Dashboard(string id, int w, int h, string background, JArray screens,
                                             string title, string description, bool overlay)
        {
            var meta = Metadata(screens.Count, w, h, title, description, overlay);
            var dash = new JObject
            {
                ["Version"] = 2,
                ["Id"] = id,
                ["BaseHeight"] = h,
                ["BaseWidth"] = w,
                ["BackgroundColor"] = background,
                ["Screens"] = screens,
                ["Images"] = new JArray(),
                ["Metadata"] = meta,
                ["ShowOnScreenControls"] = false,
                ["IsOverlay"] = overlay,
                // An overlay without buttons lets clicks through to the game.
                ["EnableClickThroughOverlay"] = overlay,
                ["EnableOnDashboardMessaging"] = false,
            };

            return new DashOutput
            {
                Djson = dash.ToString(Formatting.Indented),
                Metadata = meta.ToString(Formatting.Indented),
            };
        }

        private static JObject Metadata(int screenCount, int w, int h, string title, string description, bool overlay)
        {
            var all = new JArray(Enumerable.Range(0, screenCount));
            return new JObject
            {
                ["Title"] = title,
                ["Description"] = description,
                ["Author"] = "AZOM",
                ["ScreenCount"] = (double)screenCount,
                ["InGameScreensIndexs"] = all,
                ["IdleScreensIndexs"] = new JArray(all),
                ["PitScreensIndexs"] = new JArray(),
                ["MainPreviewIndex"] = 0,
                ["IsOverlay"] = overlay,
                ["Width"] = (double)w,
                ["Height"] = (double)h,
                ["MetadataVersion"] = 2.0,
                ["EnableOnDashboardMessaging"] = false,
            };
        }

        // ── Items ───────────────────────────────────────────────────────────

        internal static JObject Text(string name, int x, int y, int w, int h, string text, int size,
                                     string color, bool bold = false, int align = 0, string font = Font)
        {
            var o = new JObject
            {
                ["$type"] = ItemNs + "TextItem" + Asm,
                ["IsTextItem"] = true,
                ["Font"] = font,
                ["FontSize"] = (double)size,
                ["Text"] = text,
                ["TextColor"] = color,
                ["HorizontalAlignment"] = align,   // 0 left, 1 center, 2 right
                ["VerticalAlignment"] = 1,         // center
                ["BackgroundColor"] = Transparent,
            };
            if (bold) o["FontWeight"] = "Bold";
            Geometry(o, name, x, y, w, h);
            return o;
        }

        internal static JObject Rect(string name, int x, int y, int w, int h, string color, int radius = 10)
        {
            var o = new JObject
            {
                ["$type"] = ItemNs + "RectangleItem" + Asm,
                ["IsRectangleItem"] = true,
                ["BackgroundColor"] = color,
                ["BorderStyle"] = Border(radius),
            };
            Geometry(o, name, x, y, w, h);
            return o;
        }

        /// <summary>Bar gauge from 0 to <paramref name="max"/>, filling from the bottom, or from the left when <paramref name="horizontal"/>.</summary>
        internal static JObject LinearGauge(string name, int x, int y, int w, int h, int max, bool horizontal = false)
        {
            var o = new JObject
            {
                ["$type"] = ItemNs + "LinearGaugeItem" + Asm,
                ["AutoSize"] = false,
                ["AutoSizeScale"] = 1.0,
                ["GaugeColor"] = AccentColor,
                ["Minimum"] = 0.0,
                ["Maximum"] = (double)max,
                ["Value"] = 0.0,
                ["Steps"] = 0.0,
                ["GaugeOrientation"] = horizontal ? 0 : 1,   // Horizontal / Vertical
                ["GaugeAlignment"] = 0,                      // LeftOrBottom
                ["BackgroundColor"] = BtnColor,
            };
            Geometry(o, name, x, y, w, h);
            return o;
        }

        /// <summary>
        /// Visible button: background rectangle and label, then a transparent
        /// <c>ButtonItem</c> on top as the hit area (later items draw above earlier
        /// ones). Children nested in a ButtonItem don't render, hence the overlay —
        /// the same pattern the stock dashboards use. The ctor defaults
        /// SimulateKey=true / TriggerSimHubInputName="MyButton" are cleared. With
        /// <paramref name="enabledWhen"/> (NCalc boolean), the button is disabled
        /// and its visuals dimmed while the expression is false.
        /// </summary>
        internal static JObject[] Button(string name, int x, int y, int w, int h, string color, JObject label,
                                         string? azomAction, string? enabledWhen)
        {
            var bg = Rect(name + ".Background", x, y, w, h, color);
            var hit = new JObject
            {
                ["$type"] = ItemNs + "ButtonItem" + Asm,
                ["SimulatedKey"] = 0,
                ["SimulatedKeyV2"] = new JObject { ["Win"] = false, ["Ctrl"] = false, ["Alt"] = false, ["Shift"] = false },
                ["SimulateKey"] = false,
                ["TriggerAction"] = azomAction == null ? "" : SimHubRegistrar.FullName(azomAction),
                ["TriggerSimHubInputName"] = "",
                ["AutoSize"] = false,
                ["IsEnabled"] = true,
                ["BackgroundColor"] = Transparent,
            };
            Geometry(hit, name, x, y, w, h);

            if (enabledWhen != null)
            {
                hit["Bindings"] = new JObject { ["IsEnabled"] = NCalc("IsEnabled", enabledWhen) };
                foreach (var visual in new[] { bg, label })
                {
                    var b = visual["Bindings"] as JObject ?? new JObject();
                    b["Opacity"] = NCalc("Opacity", "if(" + enabledWhen + ", 100, 30)");
                    visual["Bindings"] = b;
                }
            }
            return new[] { bg, label, hit };
        }

        internal static void AddAll(JArray target, IEnumerable<JObject> items)
        {
            foreach (var i in items) target.Add(i);
        }

        internal static JObject Layer(string name, int x, int y, int w, int h, JArray children)
        {
            var o = new JObject
            {
                ["$type"] = ItemNs + "Layer" + Asm,
                ["Group"] = true,
                ["BackgroundColor"] = Transparent,
            };
            Geometry(o, name, x, y, w, h);
            o["Childrens"] = children;
            return o;
        }

        private static void Geometry(JObject o, string name, int x, int y, int w, int h)
        {
            o["Left"] = (double)x;
            o["Top"] = (double)y;
            o["Width"] = (double)w;
            o["Height"] = (double)h;
            o["Visible"] = true;
            o["Name"] = name;
        }

        private static JObject Border(int radius) => new JObject
        {
            ["RadiusTopLeft"] = radius,
            ["RadiusTopRight"] = radius,
            ["RadiusBottomLeft"] = radius,
            ["RadiusBottomRight"] = radius,
        };

        // ── Bindings ────────────────────────────────────────────────────────

        internal static JObject NCalc(string target, string expr) => new JObject
        {
            ["Formula"] = new JObject { ["Expression"] = expr },
            ["Mode"] = 2,
            ["TargetPropertyName"] = target,
        };

        internal static JObject Js(string target, string expr) => new JObject
        {
            ["Formula"] = new JObject { ["Interpreter"] = 1, ["Expression"] = expr },
            ["Mode"] = 2,
            ["TargetPropertyName"] = target,
        };

        /// <summary>NCalc property reference.</summary>
        internal static string Ref(string azomName) => "[" + SimHubRegistrar.FullName(azomName) + "]";

        /// <summary>JavaScript property read.</summary>
        internal static string Prop(string azomName) => "$prop('" + SimHubRegistrar.FullName(azomName) + "')";

        /// <summary>JavaScript string literal (JSON quoting is valid JS).</summary>
        internal static string JsStr(string s) => JsonConvert.ToString(s);

        // Formulas must not pick up the UI culture's decimal comma.
        internal static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
        internal static string Dbl(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
