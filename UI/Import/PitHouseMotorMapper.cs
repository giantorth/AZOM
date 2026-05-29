using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using MozaPlugin.Resources;

namespace MozaPlugin.UI.Import
{
    /// <summary>
    /// Maps a PitHouse Motor preset's <c>deviceParams</c> onto the active
    /// <see cref="MozaProfile"/>. Field set + transforms ported from
    /// <c>foxblat/foxblat/pithouse_converter.py</c> (GPL-3.0). Plugin-specific
    /// transforms (Limit stored as degrees/2; Equalizer identity) verified
    /// against <c>Hardware/HardwareApplier.cs</c> and <c>MozaData.cs</c>.
    /// </summary>
    public static class PitHouseMotorMapper
    {
        public static ImportPlan BuildPlan(PitHousePreset preset, MozaProfile profile)
        {
            var plan = new ImportPlan();
            if (preset == null || profile == null) { plan.FatalError = "internal: null preset or profile"; return plan; }

            var dp = preset.DeviceParams;

            // ----- Scalar mappings (all sentinel-aware via FieldDiff closure) -----

            AddBoolToInt(plan, dp, "gameForceFeedbackReversal", "FFB Reverse",
                         () => profile.FfbReverse, v => profile.FfbReverse = v);

            AddScaledInt(plan, dp, "gameForceFeedbackStrength", "FFB Strength", "%", 10,
                         () => profile.FfbStrength, v => profile.FfbStrength = v);

            // Limit is stored as degrees/2 in MozaProfile (see MozaProfile.cs:197
            // comment and HardwareApplier.cs:325 `_data.Limit * 2.0`).
            AddInt(plan, dp, "maximumSteeringAngle", "Max Steering Angle", "°",
                   pithouseDeg => pithouseDeg / 2,
                   plug => plug * 2,
                   () => profile.Limit, v => profile.Limit = v);

            AddBoolToInt(plan, dp, "safeDrivingEnabled", "Protection",
                         () => profile.Protection, v => profile.Protection = v);

            // safeDrivingMode has no profile field — MozaData carries it
            // read-only. Surface as Not Imported.
            if (dp["safeDrivingMode"] != null)
                plan.NotImported.Add("safeDrivingMode (no profile field)");

            AddInt(plan, dp, "softLimitGameForceStrength", "Soft Limit Retain", "",
                   v => v, v => v,
                   () => profile.SoftLimitRetain, v => profile.SoftLimitRetain = v);

            AddInt(plan, dp, "softLimitStiffness", "Soft Limit Stiffness", "",
                   v => v, v => v,
                   () => profile.SoftLimitStiffness, v => profile.SoftLimitStiffness = v);

            if (dp["softLimitStrength"] != null)
                plan.NotImported.Add("softLimitStrength (no profile field)");

            AddInt(plan, dp, "speedDependentDamping", "Speed Damping", "",
                   v => v, v => v,
                   () => profile.SpeedDamping, v => profile.SpeedDamping = v);

            AddInt(plan, dp, "initialSpeedDependentDamping", "Speed Damping Point", "",
                   v => v, v => v,
                   () => profile.SpeedDampingPoint, v => profile.SpeedDampingPoint = v);

            // Equalizer bands — PitHouse uses 0..100, plugin stores 0..400 with
            // 100=flat; both have 100 as the flat reference so a 1:1 mapping
            // preserves the user's intent. Plugin sentinel is -1000.
            AddEqualizer(plan, dp, "equalizerGain1", "FFB Equalizer 1", () => profile.Equalizer1, v => profile.Equalizer1 = v);
            AddEqualizer(plan, dp, "equalizerGain2", "FFB Equalizer 2", () => profile.Equalizer2, v => profile.Equalizer2 = v);
            AddEqualizer(plan, dp, "equalizerGain3", "FFB Equalizer 3", () => profile.Equalizer3, v => profile.Equalizer3 = v);
            AddEqualizer(plan, dp, "equalizerGain4", "FFB Equalizer 4", () => profile.Equalizer4, v => profile.Equalizer4 = v);
            AddEqualizer(plan, dp, "equalizerGain5", "FFB Equalizer 5", () => profile.Equalizer5, v => profile.Equalizer5 = v);
            AddEqualizer(plan, dp, "equalizerGain6", "FFB Equalizer 6", () => profile.Equalizer6, v => profile.Equalizer6 = v);

            AddScaledInt(plan, dp, "mechanicalDamper", "Damper", "%", 10,
                         () => profile.Damper, v => profile.Damper = v);
            AddScaledInt(plan, dp, "mechanicalFriction", "Friction", "%", 10,
                         () => profile.Friction, v => profile.Friction = v);
            AddScaledInt(plan, dp, "naturalInertiaV2", "Inertia", "%", 10,
                         () => profile.Inertia, v => profile.Inertia = v);
            AddScaledInt(plan, dp, "mechanicalSpringStrength", "Spring", "%", 10,
                         () => profile.Spring, v => profile.Spring = v);
            AddScaledInt(plan, dp, "maximumSteeringSpeed", "Max Steering Speed", "%", 10,
                         () => profile.Speed, v => profile.Speed = v);

            AddInt(plan, dp, "maximumTorque", "Max Torque", "%",
                   v => v, v => v,
                   () => profile.Torque, v => profile.Torque = v);

            // Game-effect gains: PitHouse 0..100 → plugin 0..255 via ×2.55.
            AddGameGain(plan, dp, "setGameDampingValue", "Game Damper Gain",
                        () => profile.GameDamper, v => profile.GameDamper = v);
            AddGameGain(plan, dp, "setGameFrictionValue", "Game Friction Gain",
                        () => profile.GameFriction, v => profile.GameFriction = v);
            AddGameGain(plan, dp, "setGameInertiaValue", "Game Inertia Gain",
                        () => profile.GameInertia, v => profile.GameInertia = v);
            AddGameGain(plan, dp, "setGameSpringValue", "Game Spring Gain",
                        () => profile.GameSpring, v => profile.GameSpring = v);

            AddSkipped(plan, dp, "constForceExtraMode", "no profile field");
            AddSkipped(plan, dp, "gameForceFeedbackFilter", "no profile field");
            AddSkipped(plan, dp, "gearJoltLevel", "no profile field");
            AddSkipped(plan, dp, "maximumGameSteeringAngle", "no profile field");

            // ----- Wheelbase ambient LED ring -----
            // Newer Motor presets bundle the wheelbase ambient-light ring config
            // (the LEDs on the front face of the base, distinct from the wheel's
            // own RPM LEDs). PitHouse uses the prefix "indicator*" / "*LedEffect"
            // for these — MozaProfile already has matching BaseAmbient* fields,
            // they just hadn't been wired up to the import.

            AddInt(plan, dp, "indicatorGroupBrightness", "Ambient Brightness", "",
                   v => v, v => v,
                   () => profile.BaseAmbientBrightness, v => profile.BaseAmbientBrightness = v);

            AddInt(plan, dp, "indicatorGroupStandbyMode", "Ambient Standby Mode", "",
                   v => v, v => v,
                   () => profile.BaseAmbientStandbyMode, v => profile.BaseAmbientStandbyMode = v);

            AddInt(plan, dp, "indicatorGroupState", "Ambient Indicator State", "",
                   v => v, v => v,
                   () => profile.BaseAmbientIndicatorState, v => profile.BaseAmbientIndicatorState = v);

            AddInt(plan, dp, "indicatorSleepMode", "Ambient Sleep Mode", "",
                   v => v, v => v,
                   () => profile.BaseAmbientSleepMode, v => profile.BaseAmbientSleepMode = v);

            AddInt(plan, dp, "indicatorSleepModeTime", "Ambient Sleep Timeout", " min",
                   v => v, v => v,
                   () => profile.BaseAmbientSleepTimeout, v => profile.BaseAmbientSleepTimeout = v);

            AddHexColor(plan, dp, "bootLedEffectColor", "Ambient Startup Color",
                        () => profile.BaseAmbientStartupColor, v => profile.BaseAmbientStartupColor = v);

            AddHexColor(plan, dp, "shutDownLedEffectColor", "Ambient Shutdown Color",
                        () => profile.BaseAmbientShutdownColor, v => profile.BaseAmbientShutdownColor = v);

            // Standby breath/colored/quicksand/rainbow timing intervals exist in
            // PitHouse but MozaProfile only tracks the mode, not the timings —
            // surface them as Not Imported so the user knows they were skipped.
            AddSkipped(plan, dp, "indicatorSleepInterval", "no profile field");
            AddSkipped(plan, dp, "indicatorGroupStandbyBreathInterval",    "no profile field");
            AddSkipped(plan, dp, "indicatorGroupStandbyColoredInterval",   "no profile field");
            AddSkipped(plan, dp, "indicatorGroupStandbyQuicksandInterval", "no profile field");
            AddSkipped(plan, dp, "indicatorGroupStandbyRainbowInterval",   "no profile field");

            // Wheel-side LED arrays bundled into the Motor preset have no
            // wheelbase counterpart — those configure the wheel head, not the
            // base. List them once as Not Imported rather than per-color.
            foreach (var k in new[] {
                "indicatorLedEffects", "buttonLedEffects", "atmosphereLedEffects", "knobLedEffects",
            })
            {
                plan.ConsideredKeys.Add(k);
                if (dp[k] is JArray arr && arr.Count > 0)
                    plan.NotImported.Add($"{k} = [{arr.Count} entries]    (wheel-head LEDs, base import skips)");
            }
            var palette = dp.Properties().Where(p =>
                p.Name.StartsWith("standbyBreathModeLeftIndicatorColor", StringComparison.Ordinal)
             || p.Name.StartsWith("standbyBreathModeRightIndicatorColor", StringComparison.Ordinal)
             || p.Name.StartsWith("standbyLightOnModeLeftIndicatorColor", StringComparison.Ordinal)
             || p.Name.StartsWith("standbyLightOnModeRightIndicatorColor", StringComparison.Ordinal)).ToList();
            foreach (var p in palette) plan.ConsideredKeys.Add(p.Name);
            if (palette.Count > 0)
                plan.NotImported.Add($"standby*IndicatorColor* = [{palette.Count} per-LED entries]    (no profile field for ambient palette)");

            // FFB curve — packed in a 12-char string per foxblat decode.
            AddFfbCurve(plan, dp, profile);

            // Meta key that's part of every PitHouse preset envelope but isn't
            // a setting the user can change — mark it considered so the
            // catch-all sweep below doesn't surface it.
            plan.ConsideredKeys.Add("version");

            // Catch-all: any deviceParams key the mapper hasn't touched gets
            // surfaced in Not Imported with its value, so a new PitHouse field
            // can't silently disappear during import.
            SweepUnhandled(plan, dp);

            return plan;
        }

        /// <summary>
        /// Add every key in <paramref name="dp"/> not yet in
        /// <see cref="ImportPlan.ConsideredKeys"/> to <see cref="ImportPlan.NotImported"/>
        /// with its formatted value. Lets the user see PitHouse settings that
        /// the import couldn't carry, instead of dropping them silently.
        /// </summary>
        internal static void SweepUnhandled(ImportPlan plan, JObject dp)
        {
            var leftovers = dp.Properties()
                              .Where(p => !plan.ConsideredKeys.Contains(p.Name))
                              .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var p in leftovers)
            {
                plan.NotImported.Add($"{p.Name} = {FormatJTokenValue(p.Value)}    (no plugin equivalent)");
            }
        }

        /// <summary>
        /// Append a "key = value (reason)" line to plan.NotImported when the
        /// PitHouse key is present. Lets the user see the value they configured
        /// in PitHouse that we couldn't carry over, not just the field name.
        /// </summary>
        internal static void AddSkipped(ImportPlan plan, JObject dp, string key, string reason)
        {
            plan.ConsideredKeys.Add(key);
            var t = dp[key];
            if (t == null || t.Type == JTokenType.Null) return;
            plan.NotImported.Add($"{key} = {FormatJTokenValue(t)}    ({reason})");
        }

        internal static string FormatJTokenValue(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Boolean: return ((bool)t) ? "true" : "false";
                case JTokenType.Integer: return ((long)t).ToString(System.Globalization.CultureInfo.InvariantCulture);
                case JTokenType.Float:   return ((double)t).ToString(System.Globalization.CultureInfo.InvariantCulture);
                case JTokenType.String:  return $"\"{(string?)t}\"";
                case JTokenType.Array:
                {
                    var arr = (JArray)t;
                    return $"[{arr.Count} entries]";
                }
                case JTokenType.Object:
                {
                    var obj = (JObject)t;
                    return $"{{{obj.Count} keys}}";
                }
                case JTokenType.Null: return "null";
                default: return t.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        /// <summary>
        /// Parse a "#rrggbb" string and return the packed-RGB int
        /// (R&lt;&lt;16 | G&lt;&lt;8 | B) used by MozaProfile color fields.
        /// </summary>
        private static int? ParseHexColor(string? hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            var s = hex!.TrimStart('#');
            if (s.Length == 3)  // #rgb shorthand
                s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
            if (s.Length != 6) return null;
            if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out int rgb))
                return null;
            return rgb & 0xFFFFFF;
        }

        private static string FormatHexColor(int packed)
        {
            return $"#{(packed & 0xFFFFFF):x6}";
        }

        private static void AddHexColor(
            ImportPlan plan, JObject dp, string pithouseKey, string label,
            Func<int> getProfile, Action<int> setProfile)
        {
            plan.ConsideredKeys.Add(pithouseKey);
            var t = dp[pithouseKey];
            if (t == null || t.Type == JTokenType.Null) return;
            var hex = (string?)t;
            var parsed = ParseHexColor(hex);
            if (parsed == null) return;

            int newVal = parsed.Value;
            int oldRaw = getProfile();
            string oldDisplay = oldRaw < 0 ? "(unset)" : FormatHexColor(oldRaw);
            string newDisplay = FormatHexColor(newVal);
            plan.Diffs.Add(new FieldDiff(label, oldDisplay, newDisplay, () => setProfile(newVal)));
        }

        // ----- Helpers -----

        private static int? IntOrNull(JObject dp, string key)
        {
            var t = dp[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return Convert.ToInt32((double)t); } catch { return null; }
        }

        private static bool? BoolOrNull(JObject dp, string key)
        {
            var t = dp[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return (bool)t; } catch { return null; }
        }

        private static void AddInt(
            ImportPlan plan, JObject dp, string pithouseKey,
            string label, string unit,
            Func<int, int> presetToProfile,
            Func<int, int> profileToPreset,
            Func<int> getProfile, Action<int> setProfile)
        {
            plan.ConsideredKeys.Add(pithouseKey);
            var pithouse = IntOrNull(dp, pithouseKey);
            if (pithouse == null) return;

            int newVal = presetToProfile(pithouse.Value);
            int oldRaw = getProfile();
            string oldDisplay = oldRaw < 0
                ? "(unset)"
                : profileToPreset(oldRaw).ToString() + unit;
            string newDisplay = pithouse.Value.ToString() + unit;

            plan.Diffs.Add(new FieldDiff(label, oldDisplay, newDisplay, () => setProfile(newVal)));
        }

        private static void AddScaledInt(
            ImportPlan plan, JObject dp, string pithouseKey,
            string label, string unit, int scale,
            Func<int> getProfile, Action<int> setProfile)
        {
            AddInt(plan, dp, pithouseKey, label, unit,
                   v => v * scale, v => v / scale,
                   getProfile, setProfile);
        }

        private static void AddBoolToInt(
            ImportPlan plan, JObject dp, string pithouseKey,
            string label,
            Func<int> getProfile, Action<int> setProfile)
        {
            plan.ConsideredKeys.Add(pithouseKey);
            var b = BoolOrNull(dp, pithouseKey);
            if (b == null) return;

            int newVal = b.Value ? 1 : 0;
            int oldRaw = getProfile();
            string oldDisplay = oldRaw < 0 ? "(unset)" : (oldRaw != 0 ? "On" : "Off");
            string newDisplay = newVal != 0 ? "On" : "Off";
            plan.Diffs.Add(new FieldDiff(label, oldDisplay, newDisplay, () => setProfile(newVal)));
        }

        private static void AddEqualizer(
            ImportPlan plan, JObject dp, string pithouseKey, string label,
            Func<int> getProfile, Action<int> setProfile)
        {
            plan.ConsideredKeys.Add(pithouseKey);
            var pithouse = IntOrNull(dp, pithouseKey);
            if (pithouse == null) return;

            int newVal = pithouse.Value;          // identity
            int oldRaw = getProfile();
            string oldDisplay = oldRaw <= -1000 ? "(unset)" : oldRaw.ToString();
            string newDisplay = newVal.ToString();
            plan.Diffs.Add(new FieldDiff(label, oldDisplay, newDisplay, () => setProfile(newVal)));
        }

        private static void AddGameGain(
            ImportPlan plan, JObject dp, string pithouseKey, string label,
            Func<int> getProfile, Action<int> setProfile)
        {
            plan.ConsideredKeys.Add(pithouseKey);
            var pithouse = IntOrNull(dp, pithouseKey);
            if (pithouse == null) return;

            int newVal = Math.Min(255, (int)Math.Round(2.55 * pithouse.Value));
            int oldRaw = getProfile();
            string oldDisplay = oldRaw < 0 ? "(unset)" : oldRaw.ToString();
            string newDisplay = newVal.ToString();
            plan.Diffs.Add(new FieldDiff(label, oldDisplay, newDisplay, () => setProfile(newVal)));
        }

        /// <summary>
        /// Decode the 12-char <c>forceFeedbackMaping</c> string. Foxblat reads
        /// 5 Y points at byte offsets [3,5,7,9,11]; X breakpoints are fixed at
        /// 20/40/60/80% in both PitHouse and the plugin. We emit one combined
        /// diff row + one apply closure that writes all five Y fields.
        /// </summary>
        private static void AddFfbCurve(ImportPlan plan, JObject dp, MozaProfile profile)
        {
            plan.ConsideredKeys.Add("forceFeedbackMaping");
            var t = dp["forceFeedbackMaping"];
            if (t == null || t.Type == JTokenType.Null) return;
            var s = (string?)t ?? "";
            if (s.Length < 12)
            {
                plan.NotImported.Add("forceFeedbackMaping (string too short to decode)");
                return;
            }

            int y1 = s[3], y2 = s[5], y3 = s[7], y4 = s[9], y5 = s[11];

            int o1 = profile.FfbCurveY1, o2 = profile.FfbCurveY2, o3 = profile.FfbCurveY3,
                o4 = profile.FfbCurveY4, o5 = profile.FfbCurveY5;
            string Old(int v) => v < 0 ? "?" : v.ToString();
            string oldDisplay = $"{Old(o1)}/{Old(o2)}/{Old(o3)}/{Old(o4)}/{Old(o5)}";
            string newDisplay = $"{y1}/{y2}/{y3}/{y4}/{y5}";

            plan.Diffs.Add(new FieldDiff("FFB Curve (Y at 20/40/60/80/100%)",
                oldDisplay, newDisplay,
                () =>
                {
                    profile.FfbCurveY1 = y1;
                    profile.FfbCurveY2 = y2;
                    profile.FfbCurveY3 = y3;
                    profile.FfbCurveY4 = y4;
                    profile.FfbCurveY5 = y5;
                }));
        }
    }
}
