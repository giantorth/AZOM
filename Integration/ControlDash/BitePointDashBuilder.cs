using System;
using System.Linq;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Resources;
using Newtonsoft.Json.Linq;
using static MozaPlugin.Integration.ControlDash.DashItems;

namespace MozaPlugin.Integration.ControlDash
{
    /// <summary>
    /// Generates the clutch bite point overlays from the <c>AZOM.ClutchBitePoint*</c>
    /// properties, display only (click-through), labels in the current UI culture:
    /// "MOZA Clutch Bite Point" — the automatic calibration's state, the bite point
    /// and what it was learned for, the live clutch against the bite point, what a
    /// standing start still needs, and the last launch; and "MOZA Clutch Bite Point
    /// Mini" — just the state and the clutch bar, shown only from the moment a
    /// standing start can be measured until its result has been shown.
    /// </summary>
    internal static class BitePointDashBuilder
    {
        internal const string DashName = "MOZA Clutch Bite Point";
        internal const string MiniDashName = "MOZA Clutch Bite Point Mini";

        // Stable ids keep regeneration byte-identical.
        private const string DashboardId = "6f1d2a9e-3c41-4b7a-9e52-0a8d7c3b1e20";
        private const string ScreenId = "6f1d2a9e-3c41-4b7a-9e52-0a8d7c3b1e21";
        private const string MiniDashboardId = "6f1d2a9e-3c41-4b7a-9e52-0a8d7c3b1e30";
        private const string MiniScreenId = "6f1d2a9e-3c41-4b7a-9e52-0a8d7c3b1e31";

        private const string CardColor = "#E614171C";
        // The plugin tab's status colours.
        private const string ReadyColor = "#FF39FF88";
        private const string MeasuringColor = "#FF00E5FF";
        private const string AmberColor = "#FFFFB84D";
        private const string MutedColor = "#FF6A737C";

        // Static values, shown by Dash Studio's editor and thumbnails (bindings
        // aren't evaluated there): the Ready state, clutch pressed in.
        private const int PreviewBitePct = 45, PreviewClutchPct = 100, PreviewSamples = 4;
        private const int PreviewLastPct = 44, PreviewLastRatePct = 85;

        // What a standing start needs.
        private static readonly (string Property, Func<string> Label)[] Checks =
        {
            ("ClutchBitePointInGame", () => Strings.BiteDash_InGame),
            ("ClutchBitePointHasClutch", () => Strings.BiteDash_ClutchSignal),
            ("ClutchBitePointInGear", () => Strings.BiteDash_InGear),
            ("ClutchBitePointStopped", () => Strings.BiteDash_Stopped),
            ("ClutchBitePointClutchIn", () => Strings.BiteDash_ClutchPressed),
        };

        // ── Full overlay ────────────────────────────────────────────────────

        private const int W = 540, H = 208, Pad = 16;
        private const int PillW = 216, PillH = 30, PillX = W - Pad - PillW, PillY = 10;
        private const int LeftW = 254;
        private const int ChecksX = 290, ChecksTop = 48, CheckH = 21;

        /// <summary>The full overlay djson for the current UI culture.</summary>
        internal static string Build()
        {
            var items = new JArray
            {
                Rect("Card", 0, 0, W, H, CardColor, radius: 12),
                Text("Title", Pad, 8, PillX - Pad - 8, 34, Strings.Section_ClutchBitePoint, 16, TextColor, bold: true),
                Rect("State.Background", PillX, PillY, PillW, PillH, BtnColor, radius: PillH / 2),
                StateDot(PillX + 12, PillY + (PillH - 10) / 2, 10),
                StateText(PillX + 30, PillY, PillW - 38, PillH, 15),
                Value(Pad, 46, LeftW, 58, 44, align: 0),
                Scope(Pad, 104, LeftW, 24, 16, Strings.BiteCal_CarCalibrated, Strings.BiteCal_GameCalibrated),
                LastLaunch(Pad, 130, LeftW, 22, 14),
            };
            for (int i = 0; i < Checks.Length; i++)
                AddAll(items, Check(Checks[i], ChecksX, ChecksTop + i * CheckH, W - Pad - ChecksX, CheckH, 10, 14));
            AddAll(items, ClutchBar(Pad, 170, W - 2 * Pad, 20, 5));

            return Overlay(DashboardId, ScreenId, W, H, items, Strings.BiteDash_Title, Strings.BiteDash_Description);
        }

        // ── Mini overlay ────────────────────────────────────────────────────

        private const int MiniW = 225, MiniH = 33, MiniPad = 6;
        // Status row: state dot, state text, calibration count right-aligned after it.
        private const int MiniRowY = 3, MiniRowH = 14, MiniDot = 6;
        private const int MiniStateX = MiniPad + 10, MiniStateW = 118, MiniCountX = MiniStateX + MiniStateW;

        /// <summary>The mini overlay djson for the current UI culture.</summary>
        internal static string BuildMini()
        {
            var children = new JArray
            {
                Rect("Card", 0, 0, MiniW, MiniH, CardColor, radius: 6),
                StateDot(MiniPad, MiniRowY + (MiniRowH - MiniDot) / 2, MiniDot),
                StateText(MiniStateX, MiniRowY, MiniStateW, MiniRowH, 10),
                Scope(MiniCountX, MiniRowY, MiniW - MiniPad - MiniCountX, MiniRowH, 9,
                    Strings.BiteCal_CarCount, Strings.BiteCal_GameCount, align: 2),
            };
            AddAll(children, ClutchBar(MiniPad, 20, MiniW - 2 * MiniPad, 8, 2));

            // Hidden while Not ready / Waiting: shown from Ready through the launch's result.
            var mini = Layer("Mini", 0, 0, MiniW, MiniH, children);
            mini["Bindings"] = new JObject
            {
                ["Visible"] = Js("Visible", StateVar + "return " + string.Join(" || ",
                    new[] { BitePointCalState.Armed, BitePointCalState.Releasing, BitePointCalState.Captured, BitePointCalState.TooFast }
                        .Select(s => "s == " + JsStr(s.ToString()))) + ";"),
            };

            return Overlay(MiniDashboardId, MiniScreenId, MiniW, MiniH, new JArray { mini },
                Strings.BiteDash_MiniTitle, Strings.BiteDash_MiniDescription);
        }

        // ── Shared parts ────────────────────────────────────────────────────

        private static string Overlay(string id, string screenId, int w, int h, JArray items,
                                      string title, string description)
        {
            var screen = new JObject
            {
                ["Name"] = Strings.Section_ClutchBitePoint,
                ["InGameScreen"] = true,
                ["IdleScreen"] = true,
                ["PitScreen"] = false,
                ["ScreenId"] = screenId,
                ["IsForegroundLayer"] = false,
                ["IsBackgroundLayer"] = false,
                ["BackgroundColor"] = Transparent,
                ["Background"] = "None",
                ["Items"] = items,
            };
            // Stock overlays pair a black dashboard background with a transparent screen.
            return Dashboard(id, w, h, "#FF000000", new JArray { screen }, title, description, overlay: true);
        }

        private static string StateVar => "var s = " + Prop("ClutchBitePointCalibration") + ";\r\n";

        private static string StateCase(BitePointCalState state, string result) =>
            "if (s == " + JsStr(state.ToString()) + ") return " + JsStr(result) + ";\r\n";

        /// <summary>Calibration state colour.</summary>
        private static JObject StateDot(int x, int y, int size)
        {
            var dot = Rect("State.Dot", x, y, size, size, ReadyColor, radius: size / 2);
            dot["Bindings"] = new JObject
            {
                ["BackgroundColor"] = Js("BackgroundColor", StateVar +
                    StateCase(BitePointCalState.Armed, ReadyColor) +
                    StateCase(BitePointCalState.Releasing, MeasuringColor) +
                    StateCase(BitePointCalState.Captured, ReadyColor) +
                    StateCase(BitePointCalState.TooFast, AmberColor) +
                    "return " + JsStr(MutedColor) + ";"),
            };
            return dot;
        }

        /// <summary>Calibration state name; a stored launch shows its bite point.</summary>
        private static JObject StateText(int x, int y, int w, int h, int size)
        {
            var name = Text("State.Text", x, y, w, h, Strings.BiteCal_Ready, size, TextColor, bold: true);
            name["Bindings"] = new JObject
            {
                ["Text"] = Js("Text", StateVar +
                    StateCase(BitePointCalState.Armed, Strings.BiteCal_Ready) +
                    StateCase(BitePointCalState.Releasing, Strings.BiteCal_Measuring) +
                    "if (s == " + JsStr(nameof(BitePointCalState.Captured)) + ") return " +
                    JsStr(Strings.BiteCal_Captured) + ".replace('{0}', " + Prop("ClutchBitePointLastSample") + ");\r\n" +
                    StateCase(BitePointCalState.TooFast, Strings.BiteCal_TooFast) +
                    StateCase(BitePointCalState.Waiting, Strings.BiteCal_Waiting) +
                    "return " + JsStr(Strings.BiteCal_NotReady) + ";"),
            };
            return name;
        }

        /// <summary>Bite point in use.</summary>
        private static JObject Value(int x, int y, int w, int h, int size, int align)
        {
            var value = Text("BitePoint.Value", x, y, w, h, PreviewBitePct + "%", size, TextColor, bold: true, align: align);
            value["Bindings"] = new JObject
            {
                ["Text"] = Js("Text",
                    "var b = " + Prop("ClutchBitePoint") + ";\r\n" +
                    "return (b == null || b < 0) ? '—' : b + '%';"),
            };
            return value;
        }

        /// <summary>Whether the car or the game is calibrated, and from how many samples:
        /// <paramref name="carFormat"/> / <paramref name="gameFormat"/> with the count as {0}.</summary>
        private static JObject Scope(int x, int y, int w, int h, int size, string carFormat, string gameFormat, int align = 0)
        {
            var scope = Text("BitePoint.Calibration", x, y, w, h,
                string.Format(carFormat, PreviewSamples), size, LabelColor, align: align);
            scope["Bindings"] = new JObject
            {
                ["Text"] = Js("Text",
                    "var s = " + Prop("ClutchBitePointScope") + ", n = " + Prop("ClutchBitePointSamples") + ";\r\n" +
                    "if (s == 'Car') return " + JsStr(carFormat) + ".replace('{0}', n);\r\n" +
                    "if (s == 'Game') return " + JsStr(gameFormat) + ".replace('{0}', n);\r\n" +
                    "return " + JsStr(Strings.BiteCal_NotCalibrated) + ";"),
            };
            return scope;
        }

        /// <summary>Last launch's bite point and release rate; amber when it was too fast to count.</summary>
        private static JObject LastLaunch(int x, int y, int w, int h, int size)
        {
            var last = Text("LastLaunch", x, y, w, h,
                string.Format(Strings.BiteDash_LastLaunch, PreviewLastPct, PreviewLastRatePct), size, LabelColor);
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
            return last;
        }

        /// <summary>One standing-start condition: dot and label, lit when met.</summary>
        private static JObject[] Check((string Property, Func<string> Label) check, int x, int y, int w, int h,
                                       int dotSize, int size)
        {
            string met = Prop(check.Property);
            var dot = Rect("Check." + check.Property + ".Dot", x, y + (h - dotSize) / 2, dotSize, dotSize, ReadyColor,
                radius: dotSize / 2);
            dot["Bindings"] = new JObject
            {
                ["BackgroundColor"] = Js("BackgroundColor",
                    "return " + met + " ? " + JsStr(ReadyColor) + " : " + JsStr(MutedColor) + ";"),
            };
            int textX = x + dotSize + 8;
            var text = Text("Check." + check.Property + ".Label", textX, y, x + w - textX, h, check.Label(), size, TextColor);
            text["Bindings"] = new JObject
            {
                ["TextColor"] = Js("TextColor",
                    "return " + met + " ? " + JsStr(TextColor) + " : " + JsStr(MutedColor) + ";"),
            };
            return new[] { dot, text };
        }

        /// <summary>Live clutch (0 = released) with the bite point and the clutch-in level marked;
        /// the bite point marker overhangs the bar by <paramref name="overhang"/> above and below.</summary>
        private static JObject[] ClutchBar(int x, int y, int w, int h, int overhang)
        {
            var bar = LinearGauge("Clutch.Bar", x, y, w, h, 100, horizontal: true);
            bar["Value"] = (double)PreviewClutchPct;
            string clutch = Ref("ClutchBitePointClutch");
            bar["Bindings"] = new JObject
            {
                ["Value"] = NCalc("Value", "if(" + clutch + " < 0, 0, " + clutch + ")"),
            };

            int inX = x + (int)Math.Round(w * MBoosterBitePointDetector.ClutchInLevel);
            var clutchIn = Rect("Clutch.InLevel", inX - 1, y, 2, h, LabelColor, radius: 0);

            string bite = Ref("ClutchBitePoint");
            var marker = Rect("Clutch.BitePoint", x - 2 + (int)Math.Round(w * PreviewBitePct / 100.0), y - overhang,
                4, h + 2 * overhang, AmberColor, radius: 2);
            marker["Bindings"] = new JObject
            {
                ["Left"] = NCalc("Left", Dbl(x - 2) + " + " + Dbl(w / 100.0) + " * " + bite),
                ["Visible"] = NCalc("Visible", bite + " >= 0"),
            };
            return new[] { bar, clutchIn, marker };
        }
    }
}
