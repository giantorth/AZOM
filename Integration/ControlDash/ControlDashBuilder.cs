using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MozaPlugin.Resources;
using MozaPlugin.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Integration.ControlDash
{
    /// <summary>
    /// Generates the "MOZA Wheelbase Control" SimHub Dash Studio dashboard: a
    /// touch layout (phone/tablet via SimHub's web dash) that drives the
    /// wheelbase through the <c>AZOM.*</c> step/toggle actions and shows the
    /// matching <c>AZOM.*</c> properties. Rows come from
    /// <see cref="BaseSettingCatalog"/>, so every button targets an action that
    /// exists. Labels use the current UI culture. See docs/simhub.md
    /// "Dash Studio dashboards".
    /// </summary>
    internal static class ControlDashBuilder
    {
        internal const string DashName = "MOZA Wheelbase Control";

        // Stable ids keep regeneration byte-identical.
        private const string DashboardId = "6f1d2a9e-3c41-4b7a-9e52-0a8d7c3b1e01";
        private static readonly string[] ScreenIds =
        {
            "6f1d2a9e-3c41-4b7a-9e52-0a8d7c3b1e10",
            "6f1d2a9e-3c41-4b7a-9e52-0a8d7c3b1e11",
            "6f1d2a9e-3c41-4b7a-9e52-0a8d7c3b1e12",
            "6f1d2a9e-3c41-4b7a-9e52-0a8d7c3b1e13",
            "6f1d2a9e-3c41-4b7a-9e52-0a8d7c3b1e14",
        };

        private const int W = 1280, H = 720;
        private const int Margin = 24, ColGap = 24;
        private const int TopBarH = 56;
        private const int ContentTop = 68, ContentBottom = 650;
        private const int NavTop = 660, NavH = 48;
        private const int HeaderH = 32;
        // One line per setting: label on the left, ◄◄ ◄ value ► ►► on the right.
        private const int RowH = 58, RowBoxH = 52, CtlInset = 4, CtlH = 44;
        private const int BtnW = 52, BtnGap = 4, ValueW = 104, ToggleW = 120;
        private const int CtlW = 4 * BtnW + ValueW + 4 * BtnGap;

        private const string Font = "Roboto";
        // Roboto has no geometric arrows; Cousine (also in DashFonts, also
        // served to the web dash) has ▲►▼◄.
        private const string ArrowFont = "Cousine";
        private const string ArrowLeft = "◄", ArrowRight = "►", ArrowUp = "▲", ArrowDown = "▼";

        private const string BgColor = "#FF14171C";
        private const string BarColor = "#FF1C2027";
        private const string RowColor = "#FF1B1F26";
        private const string BtnColor = "#FF2B313B";
        private const string GridColor = "#FF2A303A";
        private const string GuideColor = "#FF4A5260";
        private const string AccentColor = "#FF3FA9F5";
        private const string OnColor = "#FF2E8B57";
        private const string OffColor = "#FF3A3F48";
        private const string TextColor = "#FFFFFFFF";
        private const string LabelColor = "#FFB8C0CC";
        private const string Transparent = "#00FFFFFF";

        private const string ItemNs = "SimHub.Plugins.OutputPlugins.GraphicalDash.Models.";
        private const string Asm = ", SimHub.Plugins";

        // ── Layout model ────────────────────────────────────────────────────

        private abstract class Entry { }
        private sealed class Header : Entry { public Func<string> Text = () => ""; }
        private sealed class Spacer : Entry { }
        private sealed class Stepper : Entry
        {
            public string Name = "";
            public Func<string> Label = () => "";
            public string Unit = "";
            public string MinExpr = "0", MaxExpr = "0"; // NCalc bounds (constant or AZOM.<Name>Min/Max)
            public bool Gated;   // hide the row when the property reads -1 (unsupported)
        }
        private sealed class Toggle : Entry
        {
            public string Name = "";          // item-name stem
            public Func<string> Label = () => "";
            public string OnJs = "";          // JS boolean expression
            public string Action = "";        // AZOM action name
        }

        private sealed class Page
        {
            public Func<string> Title = () => "";
            public List<List<Entry>> Columns = new List<List<Entry>>();
            /// <summary>Free-form content instead of <see cref="Columns"/>.</summary>
            public Func<IEnumerable<JObject>>? Custom;
        }

        private static Stepper Num(string name)
        {
            var def = BaseSettingCatalog.Numeric.First(n => n.Name == name);
            return new Stepper
            {
                Name = def.Name, Label = def.Label, Unit = def.Unit,
                MinExpr = def.MinFor != null ? Ref(def.Name + "Min") : Int(def.Min),
                MaxExpr = def.MaxFor != null ? Ref(def.Name + "Max") : Int(def.Max),
                Gated = def.Supported != null,
            };
        }

        private static Toggle Tog(string name)
        {
            var def = BaseSettingCatalog.Toggles.First(t => t.Name == name);
            return new Toggle
            {
                Name = def.Name, Label = def.Label,
                OnJs = Prop(def.Name), Action = def.Name + "Toggle",
            };
        }

        private static Stepper RoadSensitivity() => new Stepper
        {
            Name = "RoadSensitivity", Label = () => Strings.Label_EqSensitivity,
            MinExpr = Int(BaseSettingCatalog.RoadSensitivityMinPreset),
            MaxExpr = Int(BaseSettingCatalog.RoadSensitivityMaxPreset),
        };

        private static List<Page> Pages()
        {
            // WorkMode: 0 = running, 1 = standby (cf. AZOM.WorkModeToggle).
            var standby = new Toggle
            {
                Name = "WorkMode", Label = () => Strings.SliderLabel_StandbyMode,
                OnJs = "(" + Prop("WorkMode") + " > 0)", Action = "WorkModeToggle",
            };

            return new List<Page>
            {
                new Page
                {
                    Title = () => Strings.Section_CoreSettings,
                    Columns =
                    {
                        new List<Entry>
                        {
                            new Header { Text = () => Strings.Section_CoreSettings },
                            Num("Rotation"), Num("FfbStrength"), Num("Torque"),
                            Num("WheelSpeedLimit"), Num("Interpolation"),
                        },
                        new List<Entry>
                        {
                            new Header { Text = () => Strings.Section_GearshiftVibration },
                            Num("GearshiftVibration"),
                            new Header { Text = () => Strings.Section_Miscellaneous },
                            standby, Tog("PerformanceOutput"),
                        },
                    },
                },
                new Page
                {
                    Title = () => Strings.Section_Effects,
                    Columns =
                    {
                        new List<Entry>
                        {
                            new Header { Text = () => Strings.Section_WheelbaseEffects },
                            Num("Damper"), Num("Friction"), Num("Inertia"), Num("Spring"),
                        },
                        new List<Entry>
                        {
                            new Header { Text = () => Strings.Section_GameEffects },
                            Num("GameDamper"), Num("GameFriction"), Num("GameInertia"), Num("GameSpring"),
                        },
                    },
                },
                new Page
                {
                    Title = () => Strings.Section_Protection,
                    Columns =
                    {
                        new List<Entry>
                        {
                            new Header { Text = () => Strings.Section_Protection },
                            Tog("Protection"), Num("NaturalInertia"),
                            new Header { Text = () => Strings.Section_SoftLimit },
                            Num("SoftLimitStrength"), Num("SoftLimitStiffness"), Tog("SoftLimitRetain"),
                        },
                        new List<Entry>
                        {
                            new Header { Text = () => Strings.Section_HighSpeedDamping },
                            Num("SpeedDamping"), Num("SpeedDampingPoint"),
                            new Header { Text = () => Strings.Section_Miscellaneous },
                            Tog("FfbReverse"), Tog("BaseStatusLed"), Tog("Bluetooth"),
                        },
                    },
                },
                new Page { Title = () => Strings.Section_FfbEqualizer, Custom = EqualizerPage },
                new Page { Title = () => Strings.Section_FfbOutputCurve, Custom = CurvePage },
            };
        }

        // ── Output ──────────────────────────────────────────────────────────

        internal sealed class Output
        {
            public string Djson = "";
            public string Metadata = "";
        }

        /// <summary>Build the dashboard and its metadata sidecar for the current UI culture.</summary>
        internal static Output Build(bool fahrenheit)
        {
            var pages = Pages();
            var screens = new JArray();
            for (int i = 0; i < pages.Count; i++)
                screens.Add(Screen(pages, i, fahrenheit));

            var meta = Metadata(pages.Count);
            var dash = new JObject
            {
                ["Version"] = 2,
                ["Id"] = DashboardId,
                ["BaseHeight"] = H,
                ["BaseWidth"] = W,
                ["BackgroundColor"] = BgColor,
                ["Screens"] = screens,
                ["Images"] = new JArray(),
                ["Metadata"] = meta,
                ["ShowOnScreenControls"] = false,
                ["IsOverlay"] = false,
                ["EnableClickThroughOverlay"] = false,
                ["EnableOnDashboardMessaging"] = false,
            };

            return new Output
            {
                Djson = dash.ToString(Formatting.Indented),
                Metadata = meta.ToString(Formatting.Indented),
            };
        }

        private static JObject Metadata(int screenCount)
        {
            var all = new JArray(Enumerable.Range(0, screenCount));
            return new JObject
            {
                ["Title"] = Strings.ControlDash_Title,
                ["Description"] = Strings.ControlDash_Description,
                ["Author"] = "AZOM",
                ["ScreenCount"] = (double)screenCount,
                ["InGameScreensIndexs"] = all,
                ["IdleScreensIndexs"] = new JArray(all),
                ["PitScreensIndexs"] = new JArray(),
                ["MainPreviewIndex"] = 0,
                ["IsOverlay"] = false,
                ["Width"] = (double)W,
                ["Height"] = (double)H,
                ["MetadataVersion"] = 2.0,
                ["EnableOnDashboardMessaging"] = false,
            };
        }

        private static JObject Screen(List<Page> pages, int index, bool fahrenheit)
        {
            var page = pages[index];
            var items = new JArray();
            items.Add(TopBar(fahrenheit));

            if (page.Custom != null)
                AddAll(items, page.Custom());
            else
                AddColumns(items, page, index);

            items.Add(NavBar(pages, index));

            return new JObject
            {
                ["Name"] = page.Title(),
                ["InGameScreen"] = true,
                ["IdleScreen"] = true,
                ["PitScreen"] = false,
                ["ScreenId"] = ScreenIds[index],
                ["IsForegroundLayer"] = false,
                ["IsBackgroundLayer"] = false,
                ["BackgroundColor"] = BgColor,
                ["Background"] = "None",
                ["Items"] = items,
            };
        }

        private static void AddColumns(JArray items, Page page, int index)
        {
            int cols = page.Columns.Count;
            int colW = (W - 2 * Margin - ColGap * (cols - 1)) / cols;
            for (int c = 0; c < cols; c++)
            {
                int x = Margin + c * (colW + ColGap);
                int y = ContentTop;
                foreach (var e in page.Columns[c])
                {
                    switch (e)
                    {
                        case Header h:
                            items.Add(Text($"Header.{index}.{c}.{y}", x, y, colW, HeaderH - 6,
                                h.Text(), 16, AccentColor, bold: true));
                            y += HeaderH;
                            break;
                        case Spacer _:
                            y += HeaderH;
                            break;
                        case Stepper s:
                            items.Add(StepperRow(s, x, y, colW));
                            y += RowH;
                            break;
                        case Toggle t:
                            items.Add(ToggleRow(t, x, y, colW));
                            y += RowH;
                            break;
                    }
                }
            }
        }

        // ── Rows ────────────────────────────────────────────────────────────

        /// <summary>Row card plus left-aligned label; returns the children list to append controls to.</summary>
        private static JArray RowShell(string name, Func<string> label, int x, int y, int colW, int ctlW)
            => new JArray
            {
                Rect(name + ".RowBackground", x, y, colW, RowBoxH, RowColor, radius: 8),
                Text(name + ".Label", x + 14, y, colW - ctlW - 2 * CtlInset - 14, RowBoxH, label(), 18, LabelColor),
            };

        private static JObject StepperRow(Stepper s, int x, int y, int colW)
        {
            var children = RowShell(s.Name, s.Label, x, y, colW, CtlW);
            AddAll(children, StepperControls(s, s.Name, x + colW - CtlInset - CtlW, y + CtlInset,
                BtnW, ValueW, CtlH, ArrowLeft, ArrowRight, 22));

            var layer = Layer(s.Name + ".Row", x, y, colW, RowBoxH, children);
            if (s.Gated)
                layer["Bindings"] = new JObject { ["Visible"] = NCalc("Visible", Ref(s.Name) + " >= 0") };
            return layer;
        }

        /// <summary>
        /// Horizontal stepper: coarse-less, less, value, more, coarse-more.
        /// Width = 4·<paramref name="btnW"/> + <paramref name="valueW"/> + 4·<see cref="BtnGap"/>.
        /// </summary>
        private static IEnumerable<JObject> StepperControls(Stepper s, string stem, int x, int y, int btnW, int valueW,
                                                            int h, string less, string more, int valueSize)
        {
            var items = new List<JObject>();
            items.AddRange(StepButton(s, stem, "DownCoarse", less + less, x, y, btnW, h)); x += btnW + BtnGap;
            items.AddRange(StepButton(s, stem, "Down", less, x, y, btnW, h)); x += btnW + BtnGap;
            items.Add(ValueText(s, stem, x, y, valueW, h, valueSize)); x += valueW + BtnGap;
            items.AddRange(StepButton(s, stem, "Up", more, x, y, btnW, h)); x += btnW + BtnGap;
            items.AddRange(StepButton(s, stem, "UpCoarse", more + more, x, y, btnW, h));
            return items;
        }

        private static JObject ValueText(Stepper s, string stem, int x, int y, int w, int h, int size)
        {
            var value = Text(stem + ".Value", x, y, w, h, "—", size, TextColor, bold: true, align: 1);
            value["Bindings"] = new JObject
            {
                ["Text"] = Js("Text",
                    "var v = " + Prop(s.Name) + ";\r\n" +
                    "return (v == null || v < 0) ? '—' : Math.round(v) + " + JsStr(s.Unit) + ";"),
            };
            return value;
        }

        /// <summary>
        /// One step button. Steps are clamped plugin-side; greying the button at
        /// the rail just shows that a tap would do nothing. -1 (unknown, e.g. road
        /// sensitivity before the base reports) leaves both directions live.
        /// </summary>
        private static JObject[] StepButton(Stepper s, string stem, string suffix, string glyph,
                                            int x, int y, int w, int h)
        {
            string v = Ref(s.Name);
            string canStep = suffix.StartsWith("Down", StringComparison.Ordinal)
                ? "(" + v + " < 0 || " + v + " > " + s.MinExpr + ")"
                : "(" + v + " < " + s.MaxExpr + ")";
            var label = Text(stem + "." + suffix + ".Glyph", x, y, w, h, glyph, 18, TextColor,
                bold: true, align: 1, font: ArrowFont);
            return Button(stem + "." + suffix, x, y, w, h, BtnColor, label, s.Name + suffix,
                Ref("BaseConnected") + " && " + canStep);
        }

        private static JObject ToggleRow(Toggle t, int x, int y, int colW)
        {
            var children = RowShell(t.Name, t.Label, x, y, colW, ToggleW);
            int cx = x + colW - CtlInset - ToggleW, cy = y + CtlInset;
            var state = Text(t.Name + ".State", cx, cy, ToggleW, CtlH, Strings.Option_Off, 20, TextColor,
                bold: true, align: 1);
            state["Bindings"] = new JObject
            {
                ["Text"] = Js("Text", "return " + t.OnJs + " ? " + JsStr(Strings.Option_On) + " : " + JsStr(Strings.Option_Off) + ";"),
            };
            var parts = Button(t.Name + ".Toggle", cx, cy, ToggleW, CtlH, OffColor, state, t.Action,
                Ref("BaseConnected"));
            ((JObject)parts[0]["Bindings"]!)["BackgroundColor"] =
                Js("BackgroundColor", "return " + t.OnJs + " ? " + JsStr(OnColor) + " : " + JsStr(OffColor) + ";");

            AddAll(children, parts);
            return Layer(t.Name + ".Row", x, y, colW, RowBoxH, children);
        }

        // ── FFB equalizer ───────────────────────────────────────────────────

        private const int EqTop = ContentTop + RowH + 6;

        /// <summary>
        /// Graphic EQ: one vertical bar per band in frequency order, ▲▲ ▲ above
        /// and ▼ ▼▼ below. Two layouts, picked by firmware: 10-band (bars scaled
        /// to 500 %) and legacy 6-band (400 %). <c>AZOM.Equalizer7</c> reads -1
        /// exactly when the base lacks 10-band support.
        /// </summary>
        private static IEnumerable<JObject> EqualizerPage()
        {
            var tenBand = BaseSettingCatalog.Eq10FreqOrderRegisters.Select(r => r + 1).ToArray();
            var sixBand = Enumerable.Range(1, 6).ToArray(); // registers 1-6 are already in frequency order
            string band7 = Ref("Equalizer7");

            return new[]
            {
                StepperRow(RoadSensitivity(), Margin, ContentTop, (W - 2 * Margin - ColGap) / 2),
                EqBands("Eq10", tenBand, BaseSettingCatalog.Eq10Max, band7 + " >= 0"),
                EqBands("Eq6", sixBand, BaseSettingCatalog.EqLegacyMax, band7 + " < 0"),
            };
        }

        private static JObject EqBands(string layerName, int[] registers, int scale, string visibleWhen)
        {
            const int gap = 12, inset = 6, btnH = 40, valueH = 26, labelH = 22, barW = 28;
            int n = registers.Length;
            int colW = Math.Min(n > 6 ? 112 : 150, (W - 2 * Margin - gap * (n - 1)) / n);
            int x0 = (W - (n * colW + gap * (n - 1))) / 2;

            int top = EqTop, bottom = ContentBottom;
            int valueY = top + inset;
            int upCoarseY = valueY + valueH + 2;
            int upY = upCoarseY + btnH + 4;
            int barTop = upY + btnH + 6;
            int labelY = bottom - inset - labelH;
            int downCoarseY = labelY - 4 - btnH;
            int downY = downCoarseY - 4 - btnH;
            int barBottom = downY - 6;
            int barH = barBottom - barTop;

            var children = new JArray();
            for (int i = 0; i < n; i++)
            {
                var s = Num("Equalizer" + registers[i]);
                string stem = layerName + "." + s.Name;
                int x = x0 + i * (colW + gap);
                int bx = x + inset, bw = colW - 2 * inset;

                children.Add(Rect(stem + ".Background", x, top, colW, bottom - top, RowColor, radius: 8));
                children.Add(ValueText(s, stem, x, valueY, colW, valueH, 18));
                AddAll(children, StepButton(s, stem, "UpCoarse", ArrowUp + ArrowUp, bx, upCoarseY, bw, btnH));
                AddAll(children, StepButton(s, stem, "Up", ArrowUp, bx, upY, bw, btnH));

                int gx = x + (colW - barW) / 2;
                var bar = LinearGauge(stem + ".Bar", gx, barTop, barW, barH, scale);
                bar["Bindings"] = new JObject
                {
                    ["Value"] = NCalc("Value", "if(" + Ref(s.Name) + " < 0, 0, " + Ref(s.Name) + ")"),
                };
                children.Add(bar);
                // 100 % (flat) marker.
                int flatY = barBottom - barH * 100 / scale;
                children.Add(Rect(stem + ".Flat", gx - 8, flatY - 1, barW + 16, 2, LabelColor, radius: 0));

                AddAll(children, StepButton(s, stem, "Down", ArrowDown, bx, downY, bw, btnH));
                AddAll(children, StepButton(s, stem, "DownCoarse", ArrowDown + ArrowDown, bx, downCoarseY, bw, btnH));
                children.Add(Text(stem + ".Label", x, labelY, colW, labelH, s.Label(), 16, LabelColor,
                    bold: true, align: 1));
            }

            var layer = Layer(layerName, 0, top, W, bottom - top, children);
            layer["Bindings"] = new JObject { ["Visible"] = NCalc("Visible", visibleWhen) };
            return layer;
        }

        // ── FFB output curve ────────────────────────────────────────────────

        // Plot card on the left, one card per point on the right.
        private const int PlotCardX = Margin, PlotCardW = 592;
        private const int PlotX = PlotCardX + 52, PlotY = ContentTop + 30, PlotS = 500;
        private const int PointCardX = PlotCardX + PlotCardW + ColGap, PointCardW = W - Margin - PointCardX;
        private const int PointCardH = 108, PointCardGap = 10;
        private const int PtBtnW = 50, PtValueW = 72, PtCtlH = 60;
        private const int PtGroupW = 4 * PtBtnW + PtValueW + 4 * BtnGap;

        /// <summary>
        /// Live plot of the five curve points (input → output) plus one card per
        /// point: ◄ ► moves its input (X), ▼ ▲ its output (Y). Point 5 is pinned
        /// at input 100 % and only moves up/down.
        /// </summary>
        private static IEnumerable<JObject> CurvePage()
        {
            var items = new List<JObject> { CurvePlot() };
            int n = BaseSettingCatalog.CurvePointCount;
            for (int p = 1; p <= n; p++)
            {
                int y = ContentTop + (p - 1) * (PointCardH + PointCardGap);
                items.Add(PointCard(p, p < n ? Num("FfbCurveX" + p) : null, Num("FfbCurveY" + p), y));
            }
            return items;
        }

        private static JObject PointCard(int point, Stepper? x, Stepper y, int top)
        {
            string stem = "CurvePoint" + point;
            int inset = 8, ctlY = top + 40;
            var children = new JArray
            {
                Rect(stem + ".Background", PointCardX, top, PointCardW, PointCardH, RowColor, radius: 8),
                Text(stem + ".Label", PointCardX + 14, top + 6, PointCardW - 28, 30, y.Label(), 18, LabelColor, bold: true),
            };

            int xGroup = PointCardX + inset;
            int yGroup = PointCardX + PointCardW - inset - PtGroupW;
            if (x != null)
                AddAll(children, StepperControls(x, stem + ".X", xGroup, ctlY, PtBtnW, PtValueW, PtCtlH,
                    ArrowLeft, ArrowRight, 20));
            else
                // Fixed input: no buttons, just the value where the input readout sits.
                children.Add(Text(stem + ".X.Value", xGroup + 2 * (PtBtnW + BtnGap), ctlY, PtValueW, PtCtlH,
                    "100%", 20, LabelColor, bold: true, align: 1));
            AddAll(children, StepperControls(y, stem + ".Y", yGroup, ctlY, PtBtnW, PtValueW, PtCtlH,
                ArrowDown, ArrowUp, 20));

            return Layer(stem, PointCardX, top, PointCardW, PointCardH, children);
        }

        private static JObject CurvePlot()
        {
            var children = new JArray
            {
                Rect("Curve.Background", PlotCardX, ContentTop, PlotCardW, ContentBottom - ContentTop, RowColor, radius: 8),
            };

            // Grid at 0/25/50/75/100 % with axis ticks.
            for (int k = 0; k <= 4; k++)
            {
                int gx = PlotX + PlotS * k / 4, gy = PlotY + PlotS - PlotS * k / 4;
                string tick = (k * 25).ToString(CultureInfo.InvariantCulture);
                children.Add(Rect($"Curve.GridV{k}", gx, PlotY, 1, PlotS, GridColor, radius: 0));
                children.Add(Rect($"Curve.GridH{k}", PlotX, gy, PlotS, 1, GridColor, radius: 0));
                children.Add(Text($"Curve.TickX{k}", gx - 30, PlotY + PlotS + 6, 60, 20, tick, 13, LabelColor, align: 1));
                children.Add(Text($"Curve.TickY{k}", PlotX - 48, gy - 10, 40, 20, tick, 13, LabelColor, align: 2));
            }
            // Linear reference (identity) as a dotted diagonal.
            for (int k = 0; k <= 40; k++)
            {
                int d = PlotS * k / 40;
                children.Add(Rect($"Curve.Linear{k}", PlotX + d - 1, PlotY + PlotS - d - 1, 3, 3, GuideColor, radius: 1));
            }

            // Live curve: (0,0) → P1..P4 → (100, Y5), drawn as dots along each
            // segment (positions bound to the point properties).
            int n = BaseSettingCatalog.CurvePointCount;
            var xs = new List<string> { "0" };
            var ys = new List<string> { "0" };
            for (int p = 1; p <= n; p++)
            {
                xs.Add(p < n ? Ref("FfbCurveX" + p) : "100");
                ys.Add(Ref("FfbCurveY" + p));
            }

            var curve = new JArray();
            const int dotsPerSegment = 16, dot = 5;
            for (int seg = 0; seg < n; seg++)
            {
                for (int k = 1; k < dotsPerSegment; k++)
                {
                    double t = (double)k / dotsPerSegment;
                    string px = Lerp(xs[seg], xs[seg + 1], t), py = Lerp(ys[seg], ys[seg + 1], t);
                    var d = Rect($"Curve.Seg{seg}.{k}", PlotX, PlotY + PlotS, dot, dot, AccentColor, radius: 2);
                    d["Bindings"] = PlotPosition(px, py, dot);
                    curve.Add(d);
                }
            }
            for (int p = 1; p <= n; p++)
            {
                const int size = 28;
                var marker = Rect($"Curve.Point{p}", PlotX, PlotY + PlotS, size, size, AccentColor, radius: size / 2);
                marker["Bindings"] = PlotPosition(xs[p], ys[p], size);
                var number = Text($"Curve.Point{p}.Number", PlotX, PlotY + PlotS, size, size,
                    p.ToString(CultureInfo.InvariantCulture), 15, BgColor, bold: true, align: 1);
                number["Bindings"] = PlotPosition(xs[p], ys[p], size);
                curve.Add(marker);
                curve.Add(number);
            }
            var live = Layer("Curve.Live", PlotX, PlotY, PlotS, PlotS, curve);
            live["Bindings"] = new JObject { ["Visible"] = NCalc("Visible", Ref("FfbCurveY1") + " >= 0") };
            children.Add(live);

            return Layer("Curve", PlotCardX, ContentTop, PlotCardW, ContentBottom - ContentTop, children);
        }

        private static string Lerp(string a, string b, double t)
            => "(" + a + " + " + Dbl(t) + " * (" + b + " - " + a + "))";

        /// <summary>Left/Top bindings centring an item of <paramref name="size"/> on plot coordinates (0-100).</summary>
        private static JObject PlotPosition(string xPct, string yPct, int size)
        {
            double scale = PlotS / 100.0, half = size / 2.0;
            return new JObject
            {
                ["Left"] = NCalc("Left", Dbl(PlotX - half) + " + " + Dbl(scale) + " * " + xPct),
                ["Top"] = NCalc("Top", Dbl(PlotY + PlotS - half) + " - " + Dbl(scale) + " * " + yPct),
            };
        }

        // ── Chrome ──────────────────────────────────────────────────────────

        private static JObject TopBar(bool fahrenheit)
        {
            string deg = fahrenheit ? "°F" : "°C";
            var title = Text("TopBar.Title", Margin, 0, 520, TopBarH, Strings.ControlDash_Title, 22, TextColor, bold: true);

            var status = Text("TopBar.Status", 560, 0, W - 560 - Margin, TopBarH, "", 18, LabelColor, align: 2);
            string connected = Prop("BaseConnected");
            status["Bindings"] = new JObject
            {
                ["Text"] = Js("Text",
                    "if (!" + connected + ") return " + JsStr(Strings.Status_Disconnected) + ";\r\n" +
                    "var t = function (n) { return Math.round($prop('" + SimHubRegistrar.FullName("") + "' + n)) + " + JsStr(deg) + "; };\r\n" +
                    "return " + JsStr(Strings.Brand_Mcu + " ") + " + t('McuTemp') + '   ' + " +
                    JsStr(Strings.Brand_Mosfet + " ") + " + t('MosfetTemp') + '   ' + " +
                    JsStr(Strings.Brand_Motor + " ") + " + t('MotorTemp') + '   ' + " +
                    JsStr(Strings.Label_Torque + " ") + " + " + Prop("CurrentTorque") + ".toFixed(1) + ' Nm';"),
                ["TextColor"] = Js("TextColor",
                    "return " + connected + " ? " + JsStr(LabelColor) + " : '#FFE5534B';"),
            };

            var bar = Rect("TopBar.Background", 0, 0, W, TopBarH, BarColor, radius: 0);
            return Layer("TopBar", 0, 0, W, TopBarH, new JArray { bar, title, status });
        }

        private static JObject NavBar(List<Page> pages, int current)
        {
            int n = pages.Count;
            const int gap = 8;
            int bw = (W - 2 * Margin - gap * (n - 1)) / n;
            var children = new JArray();
            for (int i = 0; i < n; i++)
            {
                int x = Margin + i * (bw + gap);
                var label = Text($"Nav.{i}.Label", x, NavTop, bw, NavH, pages[i].Title(), 16,
                    i == current ? TextColor : LabelColor, bold: true, align: 1);
                // Navigation stays live while the base is disconnected.
                var parts = Button($"Nav.{i}", x, NavTop, bw, NavH, i == current ? AccentColor : BtnColor, label,
                    null, enabledWhen: null);
                parts[2]["Behavior"] = new JObject
                {
                    ["$type"] = "SimHub.Plugins.OutputPlugins.GraphicalDash.Behaviors.Buttons.GotoScreenBehavior" + Asm,
                    ["Settings"] = new JObject
                    {
                        ["ScreenId"] = ScreenIds[i],
                        ["DisableButtonIfCurrentScreen"] = false,
                        ["ClearNavigationHistory"] = true,
                    },
                };
                AddAll(children, parts);
            }
            return Layer("Nav", 0, NavTop, W, NavH, children);
        }

        // ── Item primitives ─────────────────────────────────────────────────

        private static JObject Text(string name, int x, int y, int w, int h, string text, int size,
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

        private static JObject Rect(string name, int x, int y, int w, int h, string color, int radius = 10)
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

        /// <summary>Vertical bar gauge filling from the bottom.</summary>
        private static JObject LinearGauge(string name, int x, int y, int w, int h, int max)
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
                ["GaugeOrientation"] = 1,   // Vertical
                ["GaugeAlignment"] = 0,     // LeftOrBottom
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
        private static JObject[] Button(string name, int x, int y, int w, int h, string color, JObject label,
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

        private static void AddAll(JArray target, IEnumerable<JObject> items)
        {
            foreach (var i in items) target.Add(i);
        }

        private static JObject Layer(string name, int x, int y, int w, int h, JArray children)
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

        private static JObject NCalc(string target, string expr) => new JObject
        {
            ["Formula"] = new JObject { ["Expression"] = expr },
            ["Mode"] = 2,
            ["TargetPropertyName"] = target,
        };

        private static JObject Js(string target, string expr) => new JObject
        {
            ["Formula"] = new JObject { ["Interpreter"] = 1, ["Expression"] = expr },
            ["Mode"] = 2,
            ["TargetPropertyName"] = target,
        };

        /// <summary>NCalc property reference.</summary>
        private static string Ref(string azomName) => "[" + SimHubRegistrar.FullName(azomName) + "]";

        /// <summary>JavaScript property read.</summary>
        private static string Prop(string azomName) => "$prop('" + SimHubRegistrar.FullName(azomName) + "')";

        /// <summary>JavaScript string literal (JSON quoting is valid JS).</summary>
        private static string JsStr(string s) => JsonConvert.ToString(s);

        // Formulas must not pick up the UI culture's decimal comma.
        private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);
        private static string Dbl(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
