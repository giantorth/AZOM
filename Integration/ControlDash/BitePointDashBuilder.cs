using System;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Resources;
using Newtonsoft.Json.Linq;
using static MozaPlugin.Integration.ControlDash.DashItems;

namespace MozaPlugin.Integration.ControlDash
{
    /// <summary>
    /// Generates the "MOZA Clutch Bite Point" overlay: the automatic bite point
    /// calibration's state, the bite point and what it was learned for, the live
    /// clutch against the bite point, what a standing start still needs, and the
    /// last launch. Display only (click-through), from the <c>AZOM.ClutchBitePoint*</c>
    /// properties. Labels use the current UI culture.
    /// </summary>
    internal static class BitePointDashBuilder
    {
        internal const string DashName = "MOZA Clutch Bite Point";

        // Stable ids keep regeneration byte-identical.
        private const string DashboardId = "6f1d2a9e-3c41-4b7a-9e52-0a8d7c3b1e20";
        private const string ScreenId = "6f1d2a9e-3c41-4b7a-9e52-0a8d7c3b1e21";

        private const int W = 520, H = 208, Pad = 16;
        private const int PillW = 180, PillH = 30, PillX = W - Pad - PillW, PillY = 10;
        private const int LeftW = 254;
        private const int ChecksX = 290, ChecksTop = 48, CheckH = 21;
        private const int BarX = Pad, BarY = 170, BarW = W - 2 * Pad, BarH = 20;

        private const string CardColor = "#E614171C";
        // The plugin tab's status colours.
        private const string ReadyColor = "#FF39FF88";
        private const string MeasuringColor = "#FF00E5FF";
        private const string AmberColor = "#FFFFB84D";
        private const string MutedColor = "#FF6A737C";

        /// <summary>Build the overlay and its metadata sidecar for the current UI culture.</summary>
        internal static DashOutput Build()
        {
            var items = new JArray
            {
                Rect("Card", 0, 0, W, H, CardColor, radius: 12),
                Text("Title", Pad, 8, PillX - Pad - 8, 34, Strings.Section_ClutchBitePoint, 16, TextColor, bold: true),
            };
            AddAll(items, StatePill());
            AddAll(items, Calibration());
            AddAll(items, Checklist());
            AddAll(items, ClutchBar());

            var screen = new JObject
            {
                ["Name"] = Strings.Section_ClutchBitePoint,
                ["InGameScreen"] = true,
                ["IdleScreen"] = true,
                ["PitScreen"] = false,
                ["ScreenId"] = ScreenId,
                ["IsForegroundLayer"] = false,
                ["IsBackgroundLayer"] = false,
                ["BackgroundColor"] = Transparent,
                ["Background"] = "None",
                ["Items"] = items,
            };
            // Stock overlays pair a black dashboard background with a transparent screen.
            return Dashboard(DashboardId, W, H, "#FF000000", new JArray { screen },
                Strings.BiteDash_Title, Strings.BiteDash_Description, overlay: true);
        }

        /// <summary>Calibration state: dot and name, top right.</summary>
        private static JObject[] StatePill()
        {
            string state = "var s = " + Prop("ClutchBitePointCalibration") + ";\r\n";
            var dot = Rect("State.Dot", PillX + 12, PillY + (PillH - 10) / 2, 10, 10, MutedColor, radius: 5);
            dot["Bindings"] = new JObject
            {
                ["BackgroundColor"] = Js("BackgroundColor", state +
                    StateCase(BitePointCalState.Armed, ReadyColor) +
                    StateCase(BitePointCalState.Releasing, MeasuringColor) +
                    StateCase(BitePointCalState.Captured, ReadyColor) +
                    StateCase(BitePointCalState.TooFast, AmberColor) +
                    "return " + JsStr(MutedColor) + ";"),
            };
            var name = Text("State.Text", PillX + 30, PillY, PillW - 38, PillH, Strings.BiteCal_NotReady, 15, TextColor, bold: true);
            name["Bindings"] = new JObject
            {
                ["Text"] = Js("Text", state +
                    StateCase(BitePointCalState.Armed, Strings.BiteCal_Ready) +
                    StateCase(BitePointCalState.Releasing, Strings.BiteCal_Measuring) +
                    "if (s == " + JsStr(nameof(BitePointCalState.Captured)) + ") return " +
                    JsStr(Strings.BiteCal_Captured) + ".replace('{0}', " + Prop("ClutchBitePointLastSample") + ");\r\n" +
                    StateCase(BitePointCalState.TooFast, Strings.BiteCal_TooFast) +
                    StateCase(BitePointCalState.Waiting, Strings.BiteCal_Waiting) +
                    "return " + JsStr(Strings.BiteCal_NotReady) + ";"),
            };
            return new[]
            {
                Rect("State.Background", PillX, PillY, PillW, PillH, BtnColor, radius: PillH / 2),
                dot,
                name,
            };
        }

        private static string StateCase(BitePointCalState state, string result) =>
            "if (s == " + JsStr(state.ToString()) + ") return " + JsStr(result) + ";\r\n";

        /// <summary>Bite point, what it was learned for, and the last launch.</summary>
        private static JObject[] Calibration()
        {
            var value = Text("BitePoint.Value", Pad, 46, LeftW, 58, "—", 44, TextColor, bold: true);
            value["Bindings"] = new JObject
            {
                ["Text"] = Js("Text",
                    "var b = " + Prop("ClutchBitePoint") + ";\r\n" +
                    "return (b == null || b < 0) ? '—' : b + '%';"),
            };

            var scope = Text("BitePoint.Calibration", Pad, 104, LeftW, 24, Strings.BiteCal_NotCalibrated, 16, LabelColor);
            scope["Bindings"] = new JObject
            {
                ["Text"] = Js("Text",
                    "var s = " + Prop("ClutchBitePointScope") + ", n = " + Prop("ClutchBitePointSamples") + ";\r\n" +
                    "if (s == 'Car') return " + JsStr(Strings.BiteCal_CarCalibrated) + ".replace('{0}', n);\r\n" +
                    "if (s == 'Game') return " + JsStr(Strings.BiteCal_GameCalibrated) + ".replace('{0}', n);\r\n" +
                    "return " + JsStr(Strings.BiteCal_NotCalibrated) + ";"),
            };

            var last = Text("LastLaunch", Pad, 130, LeftW, 22, "", 14, LabelColor);
            last["Bindings"] = new JObject
            {
                ["Text"] = Js("Text",
                    "var p = " + Prop("ClutchBitePointLastSample") + ";\r\n" +
                    "if (p == null || p < 0) return '';\r\n" +
                    "return " + JsStr(Strings.BiteDash_LastLaunch) +
                    ".replace('{0}', p).replace('{1}', " + Prop("ClutchBitePointLastRate") + ");"),
                ["TextColor"] = Js("TextColor",
                    "return " + Prop("ClutchBitePointLastSkipped") + " ? " + JsStr(AmberColor) + " : " + JsStr(LabelColor) + ";"),
            };
            return new[] { value, scope, last };
        }

        /// <summary>What a standing start needs: lit when met.</summary>
        private static JObject[] Checklist()
        {
            var checks = new (string Property, string Label)[]
            {
                ("ClutchBitePointInGame", Strings.BiteDash_InGame),
                ("ClutchBitePointHasClutch", Strings.BiteDash_ClutchSignal),
                ("ClutchBitePointInGear", Strings.BiteDash_InGear),
                ("ClutchBitePointStopped", Strings.BiteDash_Stopped),
                ("ClutchBitePointClutchIn", Strings.BiteDash_ClutchPressed),
            };
            var items = new JObject[checks.Length * 2];
            for (int i = 0; i < checks.Length; i++)
            {
                var (property, label) = checks[i];
                string met = Prop(property);
                int y = ChecksTop + i * CheckH;
                var dot = Rect("Check." + property + ".Dot", ChecksX, y + (CheckH - 10) / 2, 10, 10, MutedColor, radius: 5);
                dot["Bindings"] = new JObject
                {
                    ["BackgroundColor"] = Js("BackgroundColor",
                        "return " + met + " ? " + JsStr(ReadyColor) + " : " + JsStr(MutedColor) + ";"),
                };
                var text = Text("Check." + property + ".Label", ChecksX + 18, y, W - Pad - ChecksX - 18, CheckH,
                    label, 14, MutedColor);
                text["Bindings"] = new JObject
                {
                    ["TextColor"] = Js("TextColor",
                        "return " + met + " ? " + JsStr(TextColor) + " : " + JsStr(MutedColor) + ";"),
                };
                items[2 * i] = dot;
                items[2 * i + 1] = text;
            }
            return items;
        }

        /// <summary>Live clutch (0 = released) with the bite point and the clutch-in level marked.</summary>
        private static JObject[] ClutchBar()
        {
            var bar = LinearGauge("Clutch.Bar", BarX, BarY, BarW, BarH, 100, horizontal: true);
            string clutch = Ref("ClutchBitePointClutch");
            bar["Bindings"] = new JObject
            {
                ["Value"] = NCalc("Value", "if(" + clutch + " < 0, 0, " + clutch + ")"),
            };

            int inX = BarX + (int)Math.Round(BarW * MBoosterBitePointDetector.ClutchInLevel);
            var clutchIn = Rect("Clutch.InLevel", inX - 1, BarY, 2, BarH, LabelColor, radius: 0);

            string bite = Ref("ClutchBitePoint");
            var marker = Rect("Clutch.BitePoint", BarX - 2, BarY - 5, 4, BarH + 10, AmberColor, radius: 2);
            marker["Bindings"] = new JObject
            {
                ["Left"] = NCalc("Left", Dbl(BarX - 2) + " + " + Dbl(BarW / 100.0) + " * " + bite),
                ["Visible"] = NCalc("Visible", bite + " >= 0"),
            };
            return new[] { bar, clutchIn, marker };
        }
    }
}
