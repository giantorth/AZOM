using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>
    /// Turns a SimHub item's data sources into mzdash <c>binding</c> entries.
    ///
    /// <para>Three mechanisms feed in, in ascending order of difficulty:</para>
    /// <list type="bullet">
    /// <item><c>Models.BuiltIn.*</c> — the data source is implied by the type, so there is
    /// no formula to parse. The bulk of a typical dashboard and the cleanest conversion.</item>
    /// <item><c>Behavior</c> — a named behaviour object with a settings blob. Only
    /// SimpleDash uses it across the whole stock corpus.</item>
    /// <item><c>Bindings</c> — an NCalc formula per target property, which
    /// <see cref="NCalcToJs"/> transpiles to JavaScript the wheel evaluates.</item>
    /// </list>
    /// </summary>
    public sealed class BindingTranslator
    {
        private readonly ChannelResolver _channels;
        private readonly NCalcToJs _js;
        private readonly ConversionReport _report;

        public BindingTranslator(ChannelResolver channels, NCalcToJs js, ConversionReport report)
        {
            _channels = channels;
            _js = js;
            _report = report;
        }

        /// <summary>How a BuiltIn item's value should be rendered.</summary>
        private enum Fmt { Raw, Int, OneDecimal, TwoDecimals, Gear, LapTime, Delta }

        /// <summary>BuiltIn type leaf → the SimHub property it displays, plus its format.
        /// Expressed as SimHub property paths rather than channel URLs so the whole set
        /// runs through <see cref="ChannelResolver"/> and inherits the alias table.</summary>
        private static readonly Dictionary<string, (string prop, Fmt fmt)> BuiltInSources =
            new Dictionary<string, (string, Fmt)>(StringComparer.OrdinalIgnoreCase)
            {
                ["SpeedText"]                 = ("DataCorePlugin.GameData.SpeedKmh", Fmt.Int),
                ["GearText"]                  = ("DataCorePlugin.GameData.Gear", Fmt.Gear),
                ["RPMText"]                   = ("DataCorePlugin.GameData.Rpms", Fmt.Int),
                ["LapText"]                   = ("DataCorePlugin.GameData.CurrentLap", Fmt.Int),
                ["RacePositionText"]          = ("DataCorePlugin.GameData.Position", Fmt.Int),

                ["CurrentLapTime"]            = ("DataCorePlugin.GameData.CurrentLapTime", Fmt.LapTime),
                ["BestLapTime"]               = ("DataCorePlugin.GameData.BestLapTime", Fmt.LapTime),
                ["LastLapTime"]               = ("DataCorePlugin.GameData.LastLapTime", Fmt.LapTime),
                ["PersonalBestLapTime"]       = ("DataCorePlugin.GameData.AllTimeBest", Fmt.LapTime),

                ["FuelText"]                  = ("DataCorePlugin.GameData.Fuel", Fmt.OneDecimal),
                ["FuelRemainingLapsText"]     = ("DataCorePlugin.GameData.FuelLaps", Fmt.OneDecimal),
                ["FuelLastLapText"]           = ("DataCorePlugin.GameData.FuelConsumeLap", Fmt.TwoDecimals),

                ["TyreWearText"]              = ("DataCorePlugin.GameData.TyresWearAvg", Fmt.Int),
                ["TyreTemperatureText"]       = ("DataCorePlugin.GameData.TyresTemperatureAvg", Fmt.Int),
                ["TyrePressureText"]          = ("DataCorePlugin.GameData.TyrePressureFrontLeft", Fmt.TwoDecimals),
                ["TyreFrontLeftTempText"]     = ("DataCorePlugin.GameData.TyreTemperatureFrontLeft", Fmt.Int),
                ["TyreFrontRightTempText"]    = ("DataCorePlugin.GameData.TyreTemperatureFrontRight", Fmt.Int),
                ["TyreBackLeftTempText"]      = ("DataCorePlugin.GameData.TyreTemperatureRearLeft", Fmt.Int),
                ["TyreBackRightTempText"]     = ("DataCorePlugin.GameData.TyreTemperatureRearRight", Fmt.Int),

                ["OilTemperatureText"]        = ("DataCorePlugin.GameData.OilTemperature", Fmt.Int),
            };

        /// <summary>Behavior <c>$type</c> leaf → source property + format. The behaviour's
        /// own <c>Settings</c> refine the format where it matters (see
        /// <see cref="RefineTimespanFormat"/>).</summary>
        private static readonly Dictionary<string, (string prop, Fmt fmt)> BehaviorSources =
            new Dictionary<string, (string, Fmt)>(StringComparer.OrdinalIgnoreCase)
            {
                ["SessionBestTime"]      = ("DataCorePlugin.GameData.BestLapTime", Fmt.LapTime),
                ["LastLapTime"]          = ("DataCorePlugin.GameData.LastLapTime", Fmt.LapTime),
                ["LapTime"]              = ("DataCorePlugin.GameData.CurrentLapTime", Fmt.LapTime),
                ["Speed"]                = ("DataCorePlugin.GameData.SpeedKmh", Fmt.Int),
                ["RPM"]                  = ("DataCorePlugin.GameData.Rpms", Fmt.Raw),
                ["LapPercentage"]        = ("DataCorePlugin.GameData.TrackPositionPercent", Fmt.Raw),
                ["CurrentGearBehavior"]  = ("DataCorePlugin.GameData.Gear", Fmt.Gear),
            };

        /// <summary>BuiltIn types with no representable data source on the wheel.</summary>
        private static readonly Dictionary<string, string> BuiltInUnsupported =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["GameNotification"]                   = "no event channel in mzdash",
                ["ComputerCPULoadText"]                = "PC statistic, not vehicle telemetry",
                ["ComputerMemoryLoadText"]             = "PC statistic, not vehicle telemetry",
                ["TimeText"]                           = "no host clock channel",
                ["LeaderBoardBackground"]              = "multi-opponent data has no wheel channel",
                ["LeaderboardOpponentPositionText"]    = "multi-opponent data has no wheel channel",
                ["LeaderboardOpponentNameText"]        = "multi-opponent data has no wheel channel",
                ["LeaderboardOpponentGap"]             = "multi-opponent data has no wheel channel",
                ["LeaderboardOpponentBestLap"]         = "multi-opponent data has no wheel channel",
                ["LeaderboardOpponentDelta"]           = "multi-opponent data has no wheel channel",
                ["LeaderboardOpponentCurrentLapText"]  = "multi-opponent data has no wheel channel",
            };

        /// <summary>True when the type is a BuiltIn we know we cannot represent.</summary>
        public static bool IsUnsupportedBuiltIn(string shortType, out string reason)
            => BuiltInUnsupported.TryGetValue(shortType, out reason!);

        // ── Entry point ───────────────────────────────────────────────────────

        /// <summary>Attach every binding this item implies to <paramref name="node"/>.
        /// Returns false when the item's sole purpose was a data source we could not
        /// resolve, so the caller can drop it rather than emit a dead widget.</summary>
        public bool Apply(JObject item, IrNode node, string shortType)
        {
            bool builtInOk = ApplyBuiltIn(item, node, shortType);
            ApplyBehavior(item, node);
            ApplyFormulaBindings(item, node);
            return builtInOk;
        }

        private bool ApplyBuiltIn(JObject item, IrNode node, string shortType)
        {
            if (!BuiltInSources.TryGetValue(shortType, out var src)) return true;

            // GearText carries its own blink settings; mzdash has the same effect natively.
            if (string.Equals(shortType, "GearText", StringComparison.OrdinalIgnoreCase)
                && DjsonReader.Bool(item, "GearBlinkText"))
            {
                node.Effect.BlinkEnabled = true;
                double delay = DjsonReader.Num(item, "GearBlinkDelay", 250);
                if (delay > 0) node.Effect.BlinkDelay = delay;
            }

            return BindValue(node, "text.text", src.prop, src.fmt);
        }

        private void ApplyBehavior(JObject item, IrNode node)
        {
            var behavior = DjsonReader.Obj(item, "Behavior");
            if (behavior == null) return;

            string leaf = DjsonReader.ShortTypeName(behavior);
            if (!BehaviorSources.TryGetValue(leaf, out var src))
            {
                _report.Notes.Add($"behaviour '{leaf}' is not mapped — item left static");
                return;
            }

            var settings = DjsonReader.Obj(behavior, "Settings");
            Fmt fmt = RefineTimespanFormat(src.fmt, settings);

            // A gauge behaviour drives the gauge value; everything else drives text.
            string target = node.Kind == IrKind.LinearGauge ? "linearGauge.value"
                          : node.Kind == IrKind.CircularGauge ? "circularGauge.value"
                          : "text.text";

            BindValue(node, target, src.prop, target == "text.text" ? fmt : Fmt.Raw);
        }

        /// <summary>SimHub's timespan behaviours carry their own precision settings; honour
        /// the ones that change which formatter is right.</summary>
        private static Fmt RefineTimespanFormat(Fmt fmt, JObject? settings)
        {
            if (fmt != Fmt.LapTime || settings == null) return fmt;
            if (DjsonReader.Bool(settings, "AlwaysAppendSign")) return Fmt.Delta;
            if (!DjsonReader.Bool(settings, "ForceMinutes", true)) return Fmt.Delta;
            return fmt;
        }

        private void ApplyFormulaBindings(JObject item, IrNode node)
        {
            var bindings = DjsonReader.Obj(item, "Bindings");
            if (bindings == null) return;

            foreach (var prop in bindings.Properties())
            {
                if (prop.Value is not JObject spec) continue;

                // TargetPropertyName is optional — 59 bindings in the stock corpus omit
                // it and the dictionary key is the only source of the target.
                string sourceTarget = DjsonReader.Str(spec, "TargetPropertyName", prop.Name);
                string? target = MapTarget(sourceTarget, node.Kind);
                if (target == null)
                {
                    _report.Notes.Add($"binding target '{sourceTarget}' has no mzdash equivalent");
                    continue;
                }

                string expression = DjsonReader.Str(DjsonReader.Obj(spec, "Formula"), "Expression");
                if (expression.Length == 0) continue;

                var t = _js.Translate(expression);
                if (!t.Ok)
                {
                    NoteFailure(expression, t);
                    continue;
                }
                foreach (var u in t.Urls) _report.Channels.Add(u);

                int mode = DjsonReader.Int(spec, "Mode", 2);
                if (mode == 4)
                {
                    node.Bindings.Add(new IrBinding
                    {
                        Target = target,
                        Expression = BuildGradient(spec, t.Js),
                        Format = JsFormatters.ChainIdentity,
                    });
                    continue;
                }

                node.Bindings.Add(new IrBinding
                {
                    Target = target,
                    Expression = t.Js,
                    Format = FormatChainFor(spec, target),
                });
            }
        }

        /// <summary>Pick the chain step that renders the value. A <c>FormatString</c> on the
        /// binding decides it; otherwise a text target stringifies and anything else (a
        /// gauge value, a colour, a visibility flag) passes through untouched.</summary>
        private static string FormatChainFor(JObject spec, string target)
        {
            string format = DjsonReader.Str(spec, "FormatString").Trim();
            if (format.Length > 0)
            {
                if (format.IndexOf("mm", StringComparison.Ordinal) >= 0
                    && format.IndexOf("ss", StringComparison.Ordinal) >= 0)
                    return JsFormatters.ChainLapTime;

                int decimals = DecimalsInSpec(format);
                if (decimals >= 0) return JsFormatters.ChainDecimals(decimals);
            }
            return JsFormatters.ChainIdentity;
        }

        private static int DecimalsInSpec(string spec)
        {
            foreach (char c in spec)
                if (c != '0' && c != '#' && c != '.' && c != ',') return -1;
            int dot = spec.IndexOf('.');
            if (dot < 0) return 0;
            int n = 0;
            for (int i = dot + 1; i < spec.Length; i++)
                if (spec[i] == '0' || spec[i] == '#') n++;
            return n;
        }

        /// <summary>Bind one target to a plain property read, with a canned formatter.</summary>
        private bool BindValue(IrNode node, string target, string property, Fmt fmt)
        {
            var r = _channels.Resolve(property);
            if (!r.IsUsable)
            {
                _report.NoteUnresolvedProperty(property);
                return false;
            }
            foreach (var u in r.Urls) _report.Channels.Add(u);

            node.Bindings.Add(new IrBinding
            {
                Target = target,
                Expression = r.Js,
                Format = ChainFor(fmt),
            });
            return true;
        }

        private static string ChainFor(Fmt fmt) => fmt switch
        {
            Fmt.Int => JsFormatters.ChainDecimals(0),
            Fmt.OneDecimal => JsFormatters.ChainDecimals(1),
            Fmt.TwoDecimals => JsFormatters.ChainDecimals(2),
            Fmt.Gear => JsFormatters.ChainGear,
            Fmt.LapTime => JsFormatters.ChainLapTime,
            Fmt.Delta => JsFormatters.ChainDelta,
            _ => JsFormatters.ChainIdentity,
        };

        private void NoteFailure(string expression, TranspileResult t)
        {
            foreach (var p in t.Problems)
            {
                // Pull the property name back out so the report can aggregate by
                // property rather than by the expression that happened to mention it.
                const string marker = "property [";
                int at = p.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0)
                {
                    int close = p.IndexOf(']', at + marker.Length);
                    if (close > 0)
                    {
                        _report.NoteUnresolvedProperty(
                            p.Substring(at + marker.Length, close - at - marker.Length));
                        continue;
                    }
                }
                _report.Notes.Add($"formula dropped: {p} — {Truncate(expression)}");
            }
        }

        private static string Truncate(string s)
            => s.Length <= 90 ? s : s.Substring(0, 87) + "...";

        /// <summary>
        /// SimHub <c>Mode: 4</c> — the formula yields a number that is mapped across a
        /// 2- or 3-stop colour ramp. Emitted as real linear interpolation in wheel JS
        /// rather than discrete bands, so the result matches SimHub rather than
        /// approximating it.
        /// </summary>
        private static string BuildGradient(JObject spec, string valueJs)
        {
            string start = HexRgb(DjsonReader.Str(spec, "StartColor", "#FFFFFFFF"));
            string end = HexRgb(DjsonReader.Str(spec, "EndColor", "#FFFFFFFF"));
            double sv = DjsonReader.Num(spec, "StartColorValue", 0);
            double ev = DjsonReader.Num(spec, "EndColorValue", 1);

            var stops = new List<(double at, string hex)> { (sv, start) };
            if (DjsonReader.Bool(spec, "EnableMiddleColor"))
            {
                stops.Add((DjsonReader.Num(spec, "MiddleColorValue", (sv + ev) / 2),
                           HexRgb(DjsonReader.Str(spec, "MiddleColor", "#FF000000"))));
            }
            stops.Add((ev, end));

            var inv = CultureInfo.InvariantCulture;
            var js = new System.Text.StringBuilder();
            js.Append("((function(v){v=Number(v);var s=[");
            for (int i = 0; i < stops.Count; i++)
            {
                if (i > 0) js.Append(',');
                js.Append('[').Append(stops[i].at.ToString("0.####", inv))
                  .Append(",'").Append(stops[i].hex).Append("']");
            }
            js.Append("];");
            js.Append("if(isNaN(v))return s[0][1];");
            js.Append("if(v<=s[0][0])return s[0][1];");
            js.Append("if(v>=s[s.length-1][0])return s[s.length-1][1];");
            js.Append("var h=function(c){return [parseInt(c.substr(1,2),16),")
              .Append("parseInt(c.substr(3,2),16),parseInt(c.substr(5,2),16)];};");
            js.Append("for(var i=1;i<s.length;i++){if(v<=s[i][0]){");
            js.Append("var span=s[i][0]-s[i-1][0];var f=span===0?0:(v-s[i-1][0])/span;");
            js.Append("var a=h(s[i-1][1]),b=h(s[i][1]);var o='#';");
            js.Append("for(var k=0;k<3;k++){o+=Math.round(a[k]+(b[k]-a[k])*f)")
              .Append(".toString(16).padStart(2,'0');}");
            js.Append("return o;}}return s[s.length-1][1];})(").Append(valueJs).Append("))");
            return js.ToString();
        }

        /// <summary>Reduce a SimHub <c>#AARRGGBB</c> to <c>#RRGGBB</c>.
        /// Bound colours in factory dashboards are always 6-digit; the 8-digit form is
        /// only ever used for the static colour fields, so a runtime value stays inside
        /// proven territory by dropping alpha.</summary>
        internal static string HexRgb(string argb)
        {
            string s = (argb ?? "").Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 8) s = s.Substring(2);       // AARRGGBB -> RRGGBB
            if (s.Length == 4) s = s.Substring(1);       // ARGB -> RGB
            if (s.Length == 3) s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
            if (s.Length != 6) return "#FFFFFF";
            return "#" + s;
        }

        /// <summary>SimHub binding target → the dotted path into an mzdash node's own
        /// property blocks. Null when the wheel has no equivalent.</summary>
        private static string? MapTarget(string simhubTarget, IrKind kind)
        {
            bool linear = kind == IrKind.LinearGauge;
            bool circular = kind == IrKind.CircularGauge;
            string gauge = linear ? "linearGauge" : "circularGauge";

            switch (simhubTarget)
            {
                case "Text": return "text.text";
                case "TextColor": return "text.fontColor";
                case "FontSize": return "text.fontSize";

                case "BackgroundColor":
                    return (linear || circular) ? $"{gauge}.backgroundColor" : "general.backgroundColor";

                case "Visible": return "general.visible";
                case "Left": return "general.x";
                case "Top": return "general.y";
                case "Width": return "general.width";
                case "Height": return "general.height";
                case "Opacity": return "effect.opacity";
                case "Angle":
                case "Rotation": return "effect.rotation";

                case "BorderColor": return "general.borderColor";

                case "Value":
                case "GaugeValue":
                    return (linear || circular) ? $"{gauge}.value" : null;
                case "Minimum": return (linear || circular) ? $"{gauge}.minimum" : null;
                case "Maximum": return (linear || circular) ? $"{gauge}.maximum" : null;
                case "GaugeColor":
                case "ForegroundColor":
                    return (linear || circular) ? $"{gauge}.gaugeColor" : null;

                default: return null;
            }
        }
    }
}
