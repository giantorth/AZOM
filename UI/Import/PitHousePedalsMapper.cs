using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using MozaPlugin.Devices;
using MozaPlugin.Devices.MBooster;

namespace MozaPlugin.UI.Import
{
    /// <summary>
    /// One pedal a Pedals preset can be imported into — a (controller, HID axis)
    /// pair, since a chained mBooster hosts several pedals on one lane. Doubles
    /// as the import wizard's "Apply to" combo item (<see cref="ToString"/> is
    /// the display text).
    /// </summary>
    public sealed class MBoosterImportTarget
    {
        public MBoosterDeviceController Controller { get; }
        public int AxisIndex { get; }
        public MBoosterRole Role { get; }
        /// <summary>Device+pedal text for the combo, e.g. "front-brake — a1b2c3d4 (COM7) — Pedal 2 · Brake".</summary>
        public string Label { get; }
        /// <summary>Short "Brake — front-brake" form used to prefix diff rows.</summary>
        public string RowPrefix { get; }
        /// <summary>Passive pedal (no motor, e.g. a CRP2) — vibration effects don't apply.</summary>
        public bool IsPassive { get; }

        public MBoosterImportTarget(MBoosterDeviceController controller, int axisIndex,
            MBoosterRole role, string label, string rowPrefix, bool isPassive)
        {
            Controller = controller;
            AxisIndex = axisIndex;
            Role = role;
            Label = label ?? "";
            RowPrefix = rowPrefix ?? "";
            IsPassive = isPassive;
        }

        public override string ToString() => Label;
    }

    /// <summary>
    /// Maps a PitHouse Pedals preset (mBooster-only — non-mBooster pedal presets
    /// have no calibration surface the plugin exposes) onto ONE attached pedal's
    /// <see cref="IMBoosterPedalConfig"/>.
    ///
    /// PitHouse writes a preset per pedal role, but the file still carries all
    /// three <c>throttle_</c>/<c>brake_</c>/<c>clutch_</c> sections: only the
    /// preset's own role gets the extended block (effects, travel limits, force
    /// curves), the other two hold just the device-wide
    /// <c>_channlRoleType/_min/_max/_nonlinear1..5/_outdir</c> snapshot. Importing
    /// all three rewrote the other two pedals' calibration from what is really
    /// filler, so the mapper picks the SUBJECT role — the section carrying any
    /// non-generic key — and imports only that, into the one pedal the user
    /// targets. See docs/protocol/devices/mbooster.md "PitHouse Pedals preset
    /// format".
    /// </summary>
    public static class PitHousePedalsMapper
    {
        // PitHouse role prefixes, in throttle/brake/clutch order.
        private static readonly (string Prefix, MBoosterRole Role, string Label)[] Roles =
        {
            ("throttle", MBoosterRole.Throttle, "Throttle"),
            ("brake",    MBoosterRole.Brake,    "Brake"),
            ("clutch",   MBoosterRole.Clutch,   "Clutch"),
        };

        // Keys every section carries regardless of which pedal the preset is
        // for — presence of these alone does NOT make a section the subject.
        private static readonly HashSet<string> GenericSuffixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "channlRoleType", "outdir", "min", "max",
            "nonlinear1", "nonlinear2", "nonlinear3", "nonlinear4", "nonlinear5",
            "press_combine",
        };

        // ------------------------------------------------------------
        //  Target enumeration (also feeds the wizard's "Apply to" combo)
        // ------------------------------------------------------------

        /// <summary>
        /// Every pedal an import could land on: one entry per wired HID axis of
        /// every attached mBooster. Uses the same
        /// <see cref="MBoosterDeviceController.ConnectedAxisIndices"/> +
        /// <see cref="MozaMBoosterRegistry.ResolveAxisRole"/> pair the mBooster
        /// tab's row list uses, so both show the same pedals with the same roles
        /// — including chained lanes, whose per-axis roles live in
        /// <see cref="MBoosterDeviceSettings.AxisRoles"/> rather than the legacy
        /// flat <c>Role</c>.
        /// </summary>
        public static List<MBoosterImportTarget> EnumerateTargets(
            IReadOnlyList<MBoosterDeviceController>? controllers)
        {
            var targets = new List<MBoosterImportTarget>();
            if (controllers == null) return targets;

            foreach (var c in controllers)
            {
                if (c == null) continue;
                var s = c.CurrentSettings;
                if (s == null) continue;

                // Roles resolve against the CONNECTED pedal count, matching
                // the mBooster tab's own row list — raw AxisCount is 3 on any
                // chain-capable hub however many pedals are wired, and would
                // label a sole pedal by axis order instead of its own Role.
                int axisCount = c.ConnectedAxisCount;
                var axes = c.ConnectedAxisIndices();
                var types = c.AxisTypes;

                string deviceLabel = $"{MBoosterDeviceController.ShortIdentity(c.Identity)} ({c.PortName})";

                bool multiplePedals = axes.Count > 1;
                int shown = 0;
                foreach (int axis in axes)
                {
                    ++shown;
                    var role = MozaMBoosterRegistry.ResolveAxisRole(s, axis, axisCount);
                    string pedalPart = multiplePedals
                        ? $"{deviceLabel} — {string.Format(global::MozaPlugin.Resources.Strings.Label_PedalAxis, shown)}"
                        : deviceLabel;
                    bool passive = types != null && axis < types.Length && types[axis] == 2;

                    // Row prefix leads with the role so a diff list reads
                    // "Brake · ABS"; the pedal number disambiguates two pedals
                    // sharing a role.
                    string rowPrefix = RoleName(role);
                    if (multiplePedals) rowPrefix += $" — {string.Format(global::MozaPlugin.Resources.Strings.Label_PedalAxis, shown)}";

                    targets.Add(new MBoosterImportTarget(
                        c, axis, role, $"{pedalPart} · {RoleName(role)}", rowPrefix, passive));
                }
            }
            return targets;
        }

        private static string RoleName(MBoosterRole role)
        {
            switch (role)
            {
                case MBoosterRole.Throttle: return "Throttle";
                case MBoosterRole.Brake:    return "Brake";
                case MBoosterRole.Clutch:   return "Clutch";
                default:                    return "Disabled";
            }
        }

        // ------------------------------------------------------------
        //  Subject-role detection
        // ------------------------------------------------------------

        /// <summary>
        /// The role prefixes this preset actually configures. Normally exactly
        /// one — the section carrying any key outside
        /// <see cref="GenericSuffixes"/>. A preset with no extended block
        /// anywhere (a plain calibration snapshot) has no discernible subject;
        /// every populated section is returned instead, and
        /// <paramref name="isCalibrationOnly"/> is set so the caller can auto-match
        /// each section to its own pedal rather than asking the user to pick one.
        /// </summary>
        public static List<string> DetectSubjectPrefixes(JObject? dp, out bool isCalibrationOnly)
        {
            isCalibrationOnly = false;
            var subjects = new List<string>();
            if (dp == null) return subjects;

            foreach (var (prefix, _, _) in Roles)
                if (HasNonGenericKey(dp, prefix)) subjects.Add(prefix);

            if (subjects.Count > 0) return subjects;

            isCalibrationOnly = true;
            foreach (var (prefix, _, _) in Roles)
                if (HasAnyPopulatedKey(dp, prefix)) subjects.Add(prefix);
            return subjects;
        }

        private static bool HasNonGenericKey(JObject dp, string prefix)
        {
            string p = prefix + "_";
            foreach (var prop in dp.Properties())
            {
                if (!prop.Name.StartsWith(p, StringComparison.Ordinal)) continue;
                if (prop.Value == null || prop.Value.Type == JTokenType.Null) continue;
                if (!GenericSuffixes.Contains(prop.Name.Substring(p.Length))) return true;
            }
            return false;
        }

        private static bool HasAnyPopulatedKey(JObject dp, string prefix)
        {
            string p = prefix + "_";
            foreach (var prop in dp.Properties())
            {
                if (!prop.Name.StartsWith(p, StringComparison.Ordinal)) continue;
                if (prop.Value == null || prop.Value.Type == JTokenType.Null) continue;
                if (string.Equals(prop.Name, p + "channlRoleType", StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------
        //  Plan building
        // ------------------------------------------------------------

        /// <summary>
        /// Build the apply plan. <paramref name="targetOverride"/> retargets the
        /// import onto a pedal the user picked in the wizard; when null the
        /// subject role's own pedal is used (the first attached pedal whose
        /// resolved role matches). A calibration-only preset ignores the override
        /// and auto-matches each populated section to its own pedal.
        /// </summary>
        public static ImportPlan BuildPlan(
            PitHousePreset preset,
            IReadOnlyList<MBoosterDeviceController>? controllers,
            MBoosterImportTarget? targetOverride = null)
        {
            var plan = new ImportPlan();
            if (preset == null) { plan.FatalError = "internal: null preset"; return plan; }

            var dp = preset.DeviceParams;
            if (dp == null) { plan.FatalError = "preset has no deviceParams block"; return plan; }

            var targets = EnumerateTargets(controllers);
            var subjects = DetectSubjectPrefixes(dp, out bool calibrationOnly);

            plan.ConsideredKeys.Add("version");

            if (subjects.Count == 0)
            {
                plan.SubjectRoleDisplay = "(none)";
                PitHouseMotorMapper.SweepUnhandled(plan, dp);
                return plan;
            }

            string subjectLabels = string.Join(" + ",
                subjects.Select(p => Roles.First(r => r.Prefix == p).Label));

            // Retargeting only makes sense when one section drives one pedal.
            // A calibration-only preset (no subject at all) and the rare preset
            // with extended blocks under two roles both auto-match each section
            // to the pedal carrying that role instead.
            bool autoMatchPerRole = calibrationOnly || subjects.Count > 1;
            plan.AutoMatchedPerRole = autoMatchPerRole;
            plan.SubjectRoleDisplay = calibrationOnly
                ? "all pedals (per role)"
                : subjectLabels + (autoMatchPerRole ? " (per role)" : "");

            // Pair each subject section with the pedal it writes to.
            var pairs = new List<(string Prefix, string RoleLabel, MBoosterImportTarget Target)>();
            foreach (var (prefix, role, roleLabel) in Roles)
            {
                plan.ConsideredKeys.Add(prefix + "_channlRoleType");

                if (!subjects.Contains(prefix))
                {
                    // Not this preset's subject — the section is the device-wide
                    // snapshot PitHouse writes into every preset. Importing it
                    // would rewrite a pedal the user isn't configuring.
                    if (HasAnyPopulatedKey(dp, prefix))
                    {
                        MarkRoleSectionConsidered(plan, dp, prefix);
                        plan.NotImported.Add($"{prefix}_*    (not this preset's role)");
                    }
                    continue;
                }

                var target = autoMatchPerRole
                    ? targets.FirstOrDefault(t => t.Role == role)
                    : targetOverride ?? targets.FirstOrDefault(t => t.Role == role);

                if (target == null)
                {
                    MarkRoleSectionConsidered(plan, dp, prefix);
                    plan.NotImported.Add(targets.Count == 0
                        ? $"{roleLabel}: no mBooster pedal attached"
                        : $"{roleLabel}: no pedal with this role — pick one above");
                    continue;
                }

                pairs.Add((prefix, roleLabel, target));
                if (!autoMatchPerRole) plan.ResolvedTarget = target;
            }

            foreach (var (prefix, _, target) in pairs)
                AddSectionDiffs(plan, dp, prefix, target);

            // Un-prefixed device-wide duplicates of the per-pedal keys. PitHouse
            // writes both; the per-pedal ones win because they say which pedal
            // they belong to.
            foreach (var key in DeviceWideDuplicates)
                PitHouseMotorMapper.AddSkipped(plan, dp, key, "device-wide copy");
            foreach (var (key, reason) in UnsupportedGlobals)
                PitHouseMotorMapper.AddSkipped(plan, dp, key, reason);

            // Catch-all: every deviceParams key the mapper hasn't touched gets
            // surfaced in Not Imported with its value, so no PitHouse setting
            // silently disappears. Reuses the motor mapper's sweep helper.
            PitHouseMotorMapper.SweepUnhandled(plan, dp);

            // One hardware push per touched device, after the whole plan is
            // built — adding per-diff would re-push a device whose every row is
            // a no-op.
            if (plan.HasChanges)
                foreach (var (_, _, target) in pairs)
                    plan.TouchedMBoosters.Add(target.Controller);

            return plan;
        }

        // Un-prefixed keys that repeat a per-pedal value.
        private static readonly string[] DeviceWideDuplicates =
        {
            "machinelimit_min", "machinelimit_max",
            "softlimit_hardness_press", "softlimit_hardness_release",
            "damping_press", "damping_release",
            "friction_press", "friction_release",
            "forcelimit_min",
        };

        // Un-prefixed keys with no plugin surface at all.
        private static readonly (string Key, string Reason)[] UnsupportedGlobals =
        {
            ("force_max_coef",   "no wire command"),
            ("pressure_weight",  "no wire command"),
            ("enter_sleep_time", "no wire command"),
            ("game_mode",        "PitHouse-only"),
        };

        /// <summary>
        /// Mark every <c>&lt;prefix&gt;_*</c> key in <paramref name="dp"/> as
        /// considered, so the catch-all sweep doesn't surface them individually
        /// when an entire role section was skipped.
        /// </summary>
        private static void MarkRoleSectionConsidered(ImportPlan plan, JObject dp, string prefix)
        {
            var p = prefix + "_";
            foreach (var prop in dp.Properties())
                if (prop.Name.StartsWith(p, StringComparison.Ordinal))
                    plan.ConsideredKeys.Add(prop.Name);
        }

        // ------------------------------------------------------------
        //  Section mapping
        // ------------------------------------------------------------

        private static void AddSectionDiffs(
            ImportPlan plan, JObject dp, string prefix, MBoosterImportTarget target)
        {
            var settings = target.Controller.CurrentSettings;
            if (settings == null) return;

            var m = new SectionWriter(plan, dp, prefix, target, settings);

            // ----- Calibration (group 35/36 dir + output curve) -----
            m.Int("outdir", "Direction", 0, 1,
                  c => c.Direction, (c, v) => c.Direction = v, unsetBelowZero: true);
            m.OutputCurve();

            // min/max are deliberately NOT imported: PitHouse states them as
            // percentages (0/3/16/99/100 across the sample presets) while
            // MBoosterDeviceSettings.Min/Max are the device's own RAW counts —
            // the "Min (raw)"/"Max (raw)" sliders run 0..65535 and are seeded
            // from the device read-back. Writing 99 into a raw field caps the
            // pedal at ~0.15 % of full scale. The scale factor between the two
            // is unverified, so the values are surfaced instead of guessed.
            m.SkipKey("min", "percent vs raw counts — unverified");
            m.SkipKey("max", "percent vs raw counts — unverified");

            // ----- Vibration effects -----
            // PitHouse only writes ABS/Lockup/Threshold under the brake role and
            // TC/WheelSpin/GearShift/RoadTexture under any role; absent keys are
            // skipped, so no per-role gating is needed here.
            m.Effect("ABS", c => c.Abs, "abs", "amp",
                     freqSuffix: "freq", MBoosterUiConstants.AbsFreqMinHz, MBoosterUiConstants.AbsFreqMaxHz,
                     smoothSuffix: "smoothness");
            m.Effect("Lockup", c => c.Lockup, "lockup", "amp",
                     freqSuffix: "freq", MBoosterUiConstants.LockupFreqMinHz, MBoosterUiConstants.LockupFreqMaxHz);
            m.Effect("Threshold", c => c.Threshold, "brakethreshold", "amp",
                     freqSuffix: "freq", MBoosterUiConstants.ThresholdFreqMinHz, MBoosterUiConstants.ThresholdFreqMaxHz,
                     triggerSuffix: "trigger_input", decaySuffix: "fade_amount");
            m.Effect("Traction Control", c => c.TractionControl, "tc", "amp",
                     freqSuffix: "freq", MBoosterUiConstants.TractionControlFreqMinHz, MBoosterUiConstants.TractionControlFreqMaxHz);
            m.Effect("Wheel Spin", c => c.WheelSpin, "wheel_slip", "amp",
                     freqSuffix: "freq", MBoosterUiConstants.WheelSpinFreqMinHz, MBoosterUiConstants.WheelSpinFreqMaxHz);
            m.Effect("Gear Shift", c => c.GearShift, "gear_shift_vibration", "amp",
                     freqSuffix: "freq", MBoosterUiConstants.GearShiftFreqMinHz, MBoosterUiConstants.GearShiftFreqMaxHz);
            m.Effect("Road Texture", c => c.RoadTexture, "road_texture", "intensity",
                     smoothSuffix: "smoothness");

            // ----- Pedal Feel / Sim Input Mapping hardware calibration -----
            // Unit mapping inferred from value range, not from a wire capture —
            // see docs/protocol/devices/mbooster.md. Rows carry a * so the
            // confirm list shows which ones rest on that inference.
            m.TravelRange();
            m.Float("softlimit_hardness_press", "Endstop front stiffness *", 1f, 10f, "F0",
                    c => c.EndstopFrontStiffness, (c, v) => c.EndstopFrontStiffness = v);
            m.Float("softlimit_hardness_release", "Endstop end stiffness *", 1f, 10f, "F0",
                    c => c.EndstopEndStiffness, (c, v) => c.EndstopEndStiffness = v);

            if (target.Role == MBoosterRole.Brake)
            {
                m.Float("press_combine", "Sensor output ratio (%) *", 0f, 100f, "F0",
                        c => c.SensorOutputRatioPct, (c, v) => c.SensorOutputRatioPct = v);
            }
            else
            {
                // mbooster-brake-angle-ratio is written only for the brake role
                // (MozaPlugin.ApplyMBoosterToHardware), so importing it onto a
                // throttle/clutch pedal would never reach the device.
                PitHouseMotorMapper.AddSkipped(plan, dp, prefix + "_press_combine", "brake-only");
            }

            // ----- Motor-only: Pedal Feel, damping, friction, G-Force -----
            // All brake-named singleton registers on the motorized unit
            // (HardwareApplier skips them for a passive pedal), so a passive
            // target has nothing to receive them.
            if (target.IsPassive)
            {
                foreach (var family in new[] { "forcelimit", "forces_curve", "stroke_curve", "damping", "friction", "gforce" })
                    m.SkipFamily(family, "passive pedal — no motor");
            }
            else
            {
                m.PedalFeel();
                m.Damping();
                m.Friction();
                m.GForce();
            }
            m.SkipFamily("motor_vibration", "PitHouse motor test");

            if (target.IsPassive)
                plan.NotImported.Add($"{target.RowPrefix}: passive pedal — effects won't play");
        }

        /// <summary>
        /// Per-section diff emitter. Reads "before" values from a non-creating
        /// peek (so previewing an import never persists an empty per-pedal
        /// entry) and defers the create-on-demand to each diff's apply closure.
        /// </summary>
        private sealed class SectionWriter
        {
            private readonly ImportPlan _plan;
            private readonly JObject _dp;
            private readonly string _prefix;
            private readonly string _rowPrefix;
            private readonly IMBoosterPedalConfig _read;
            private readonly MBoosterDeviceSettings _settings;
            private readonly int _axis;
            private readonly int _soleAxis;

            public SectionWriter(ImportPlan plan, JObject dp, string prefix,
                MBoosterImportTarget target, MBoosterDeviceSettings settings)
            {
                _plan = plan;
                _dp = dp;
                _prefix = prefix;
                _rowPrefix = target.RowPrefix;
                _settings = settings;
                _axis = target.AxisIndex;
                _soleAxis = target.Controller.SoleConnectedAxis();
                // Defaults stand in when a chained pedal has no entry yet —
                // that IS the config it currently runs with.
                _read = MozaMBoosterRegistry.PeekPedalConfig(settings, _axis, _soleAxis)
                        ?? new MBoosterPedalSettings();
            }

            private IMBoosterPedalConfig? Write() =>
                MozaMBoosterRegistry.GetOrCreatePedalConfig(_settings, _axis, _soleAxis);

            private string Key(string suffix) => _prefix + "_" + suffix;

            // Pit House's own preset import reads the _v128 travel / force /
            // curve keys when present (its "MBoost - 导入预设 - v128…" range
            // checks) and the plain key otherwise; the two can differ (a user's
            // 3.8–28.3 mm profile carried 3.8–19.8 in the plain key). The plain
            // key is then listed as superseded, once.
            private readonly HashSet<string> _superseded = new HashSet<string>(StringComparer.Ordinal);
            private string Pick(string plain, params string[] v128)
            {
                foreach (var v in v128)
                {
                    var t = _dp[Key(v)];
                    if (t == null || t.Type == JTokenType.Null) continue;
                    if (_superseded.Add(plain))
                        PitHouseMotorMapper.AddSkipped(_plan, _dp, Key(plain), $"superseded by {Key(v)}");
                    return v;
                }
                return plain;
            }

            private string TravelMinKey() => Pick("machinelimit_min", "machinelimit_min_v128");
            private string TravelMaxKey() => Pick("machinelimit_max", "machinelimit_max_v128");

            private JToken? Token(string suffix)
            {
                _plan.ConsideredKeys.Add(Key(suffix));
                var t = _dp[Key(suffix)];
                return t == null || t.Type == JTokenType.Null ? null : t;
            }

            private double? Num(string suffix)
            {
                var t = Token(suffix);
                if (t == null) return null;
                try { return (double)t; } catch { return null; }
            }

            private bool? Bool(string suffix)
            {
                var t = Token(suffix);
                if (t == null) return null;
                try { return (bool)t; } catch { return null; }
            }

            private double[]? NumArray(string suffix)
            {
                if (Token(suffix) is not JArray a) return null;
                var r = new double[a.Count];
                for (int i = 0; i < a.Count; i++)
                {
                    try { r[i] = (double)a[i]; } catch { return null; }
                }
                return r;
            }

            private static string Pct(float v) =>
                v < 0 ? "(unset)" : v.ToString("F0", CultureInfo.InvariantCulture) + "%";

            private static string Nodes(float[]? a) =>
                a == null ? "(default)" : string.Join("/", a.Select(FormatCurvePoint));

            private void Add(string label, string oldDisplay, string newDisplay, Action<IMBoosterPedalConfig> apply)
            {
                _plan.Diffs.Add(new FieldDiff($"{_rowPrefix} · {label}", oldDisplay, newDisplay,
                    () => { var cfg = Write(); if (cfg != null) apply(cfg); }));
            }

            // ----- scalar helpers -----

            public void Int(string suffix, string label, int lo, int hi,
                Func<IMBoosterPedalConfig, int> get, Action<IMBoosterPedalConfig, int> set,
                bool unsetBelowZero = false)
            {
                var v = Num(suffix);
                if (v == null) return;
                int nv = (int)Math.Round(Clamp(v.Value, lo, hi));
                int ov = get(_read);
                Add(label,
                    unsetBelowZero && ov < 0 ? "(unset)" : ov.ToString(CultureInfo.InvariantCulture),
                    nv.ToString(CultureInfo.InvariantCulture),
                    c => set(c, nv));
            }

            public void Float(string suffix, string label, float lo, float hi, string fmt,
                Func<IMBoosterPedalConfig, float> get, Action<IMBoosterPedalConfig, float> set)
            {
                var v = Num(suffix);
                if (v == null) return;
                float nv = (float)Clamp(v.Value, lo, hi);
                float ov = get(_read);
                Add(label,
                    ov < 0 ? "(unset)" : ov.ToString(fmt, CultureInfo.InvariantCulture),
                    nv.ToString(fmt, CultureInfo.InvariantCulture),
                    c => set(c, nv));
            }

            // PitHouse's own preset file format is fixed at 5 points
            // (nonlinear1..5) at 20/40/60/80/100% — that's external and
            // won't change. AZOM's CurveY is now 6 points at 100/6 * k
            // (see MozaMBoosterRegistry.EvaluateCurveArbitraryX /
            // MBoosterUiConstants.SimInputMappingNodeCount), so the
            // imported 5-point shape is resampled at the 6 new breakpoints
            // rather than mapped 1:1 onto the first 5 of 6 slots.
            private static readonly float[] PitHouseOutputCurveX = { 20, 40, 60, 80, 100 };

            /// <summary>Output curve: nonlinear1..5 → CurveY (resampled to 6 nodes).</summary>
            public void OutputCurve()
            {
                var y5 = new float[5];
                bool any = false;
                for (int i = 0; i < 5; i++)
                {
                    var v = Num("nonlinear" + (i + 1));
                    if (v == null) continue;
                    y5[i] = (float)Clamp(v.Value, 0, 100);
                    any = true;
                }
                if (!any) return;

                int n = global::MozaPlugin.Devices.MBooster.MBoosterUiConstants.SimInputMappingNodeCount;
                var y = new float[n];
                for (int i = 0; i < n; i++)
                {
                    double x = (i + 1) * 100.0 / 6.0;
                    y[i] = (float)global::MozaPlugin.Devices.MBooster.MozaMBoosterRegistry.EvaluateCurveArbitraryX(PitHouseOutputCurveX, y5, x);
                }

                var oldCurve = _read.CurveY;
                string oldDisplay = oldCurve == null || oldCurve.Length < n
                    ? "(unset)"
                    : string.Join("/", oldCurve.Take(n).Select(FormatCurvePoint));
                string newDisplay = string.Join("/", y.Select(FormatCurvePoint));

                Add("Output curve (Y at 100/6% breakpoints)", oldDisplay, newDisplay,
                    c => c.CurveY = (float[])y.Clone());
            }

            private static string FormatCurvePoint(float v) =>
                ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);

            /// <summary>
            /// machinelimit_min/max → TravelStartMm/TravelEndMm as one row, so
            /// the pair stays inside the range slider's own min/max gap. Values
            /// are mm: Pit House stores the pedal's 0x84/0x85 read-back under
            /// these keys on the same 0–53.5 mm scale the plugin writes.
            /// </summary>
            public void TravelRange()
            {
                var lo = Num(TravelMinKey());
                var hi = Num(TravelMaxKey());
                if (lo == null && hi == null) return;

                // Half a pair is only usable when the other end already has a
                // real value to pin against — otherwise the clamp below would
                // invent one out of the -1 "unset" sentinel.
                if (lo == null && _read.TravelStartMm < 0) return;
                if (hi == null && _read.TravelEndMm < 0) return;

                float start = (float)Clamp(lo ?? _read.TravelStartMm,
                    MBoosterUiConstants.TravelMinMm, MBoosterUiConstants.TravelMaxMm);
                float end = (float)Clamp(hi ?? _read.TravelEndMm,
                    MBoosterUiConstants.TravelMinMm, MBoosterUiConstants.TravelMaxMm);

                // Honour the slider's own gap constraints so an imported pair
                // can never land somewhere the UI couldn't produce.
                if (end - start < MBoosterUiConstants.TravelMinGapMm)
                    end = Math.Min(MBoosterUiConstants.TravelMaxMm, start + MBoosterUiConstants.TravelMinGapMm);

                string oldDisplay = _read.TravelStartMm < 0 || _read.TravelEndMm < 0
                    ? "(unset)"
                    : $"{_read.TravelStartMm.ToString("F1", CultureInfo.InvariantCulture)}–{_read.TravelEndMm.ToString("F1", CultureInfo.InvariantCulture)} mm";
                string newDisplay = $"{start.ToString("F1", CultureInfo.InvariantCulture)}–{end.ToString("F1", CultureInfo.InvariantCulture)} mm";

                float s = start, e = end;
                Add("Travel start–end (mm)", oldDisplay, newDisplay,
                    c => { c.TravelStartMm = s; c.TravelEndMm = e; });
            }

            /// <summary>
            /// forcelimit_min/max → Deadzone/Max Force; forces_curve (kg, the 6
            /// nodes then the Max Force point) → InputCurveY as % of that span;
            /// stroke_curve (mm, inside machinelimit_min..max) → InputCurveX as
            /// % of travel. A default PitHouse preset has both curves at exact
            /// k/7 steps of those ranges — the same k/7 the Pedal Feel wire
            /// uses — which is what ties the keys to these fields.
            /// </summary>
            public void PedalFeel()
            {
                int n = MBoosterUiConstants.PedalFeelNodeCount;
                var dzRaw = Num(Pick("forcelimit_min", "forcelimit_min_v128"));
                var mfRaw = Num(Pick("forcelimit_max", "forcelimit_max_v128"));
                string forcesKey = Pick("forces_curve", "forces_curve_cache_v128", "forces_curve_v128_cache");
                var forces = NumArray(forcesKey);
                if (mfRaw == null && forces != null && forces.Length == n + 1) mfRaw = forces[n];

                MBoosterUiConstants.ForceRanges(true,
                    out float mfMin, out float mfMax, out float dzMin, out float dzMax);
                float? dz = dzRaw.HasValue ? (float)Math.Round(Clamp(dzRaw.Value, dzMin, dzMax), 1) : (float?)null;
                // Whole kg, like the Max Force slider.
                float? mf = mfRaw.HasValue ? (float)Math.Round(Clamp(mfRaw.Value, mfMin, mfMax)) : (float?)null;

                if (dz.HasValue)
                {
                    float v = dz.Value;
                    Add("Deadzone (kg)",
                        _read.DeadzoneKg < 0 ? "(unset)" : _read.DeadzoneKg.ToString("F1", CultureInfo.InvariantCulture),
                        v.ToString("F1", CultureInfo.InvariantCulture), c => c.DeadzoneKg = v);
                }
                if (mf.HasValue)
                {
                    float v = mf.Value;
                    Add("Max force (kg)",
                        _read.MaxForceKg < 0 ? "(unset)" : _read.MaxForceKg.ToString("F0", CultureInfo.InvariantCulture),
                        v.ToString("F0", CultureInfo.InvariantCulture), c => c.MaxForceKg = v);
                }

                if (forces != null && forces.Length >= n)
                {
                    if (!dz.HasValue || !mf.HasValue || mf.Value <= dz.Value)
                    {
                        PitHouseMotorMapper.AddSkipped(_plan, _dp, Key(forcesKey), "needs forcelimit_min < forcelimit_max");
                    }
                    else
                    {
                        // Relative to the preset's own span, before clamping —
                        // that is the span PitHouse sampled the nodes on.
                        double lo = dzRaw!.Value, span = mfRaw!.Value - dzRaw.Value;
                        var y = new float[n];
                        for (int i = 0; i < n; i++)
                        {
                            double pct = span > 0 ? (forces[i] - lo) / span * 100.0 : 0;
                            y[i] = (float)Math.Round(Clamp(pct, 0, 100));
                            // Nodes can't cross (the editor clamps Y to neighbours).
                            if (i > 0 && y[i] < y[i - 1]) y[i] = y[i - 1];
                        }
                        Add("Pedal feel curve (Y, % of deadzone→max force)", Nodes(_read.InputCurveY), Nodes(y),
                            c => c.InputCurveY = (float[])y.Clone());
                    }
                }

                string strokeKey = Pick("stroke_curve", "stroke_curve_cache_v128", "stroke_curve_v128_cache");
                var stroke = NumArray(strokeKey);
                if (stroke != null && stroke.Length >= n)
                {
                    var tLo = Num(TravelMinKey());
                    var tHi = Num(TravelMaxKey());
                    if (tLo == null || tHi == null || tHi.Value <= tLo.Value)
                    {
                        PitHouseMotorMapper.AddSkipped(_plan, _dp, Key(strokeKey), "needs machinelimit_min < machinelimit_max");
                    }
                    else
                    {
                        double span = tHi.Value - tLo.Value;
                        var x = new float[n];
                        for (int i = 0; i < n; i++)
                            x[i] = (float)Math.Round(Clamp((stroke[i] - tLo.Value) / span * 100.0, 1, 99));
                        // Same 1-unit neighbour gap the editor enforces, and
                        // the last node stays short of the fixed Max Force
                        // point at 100.
                        for (int i = 1; i < n; i++)
                            if (x[i] < x[i - 1] + 1) x[i] = x[i - 1] + 1;
                        for (int i = n - 1; i >= 0; i--)
                        {
                            float cap = i == n - 1 ? 99 : x[i + 1] - 1;
                            if (x[i] > cap) x[i] = cap;
                        }
                        Add("Pedal feel curve (X, % of travel) *", Nodes(_read.InputCurveX), Nodes(x),
                            c => c.InputCurveX = (float[])x.Clone());
                    }
                }
            }

            /// <summary>
            /// damping_press/_release → Virtual Damping (0xAD), a switched-off
            /// direction importing as 0% (how Pit House turns a register off —
            /// see Natural Friction); damping_[release_]segment{1,2}_position +
            /// damping_{press,release}_segment{1..3}_value → Segmented Damping
            /// (0xB7); damping_switch → Segmented Damping on/off. A 2026-09-08
            /// Pit House capture wrote 40%/25% to both registers — the same
            /// values the sample preset holds under these keys.
            /// </summary>
            public void Damping()
            {
                VirtualDamping("press", "Virtual damping press *",
                    c => c.DampingPressPct, (c, v) => c.DampingPressPct = v);
                VirtualDamping("release", "Virtual damping release *",
                    c => c.DampingReleasePct, (c, v) => c.DampingReleasePct = v);

                var sd = _read.SegmentedDamping ?? new MBoosterSegmentedDampingSettings();
                SegmentedSide("Segmented damping pressed *",
                    Num("damping_segment1_position"), Num("damping_segment2_position"),
                    Num("damping_press_segment1_value"), Num("damping_press_segment2_value"), Num("damping_press_segment3_value"),
                    sd.Divider1Pressed, sd.Divider2Pressed, sd.Seg1Pressed, sd.Seg2Pressed, sd.Seg3Pressed,
                    MBoosterUiConstants.SegDampDivider1PressedDefaultPct, MBoosterUiConstants.SegDampDivider2PressedDefaultPct,
                    (t, d1, d2, s1, s2, s3) =>
                    {
                        t.Divider1Pressed = d1; t.Divider2Pressed = d2;
                        t.Seg1Pressed = s1; t.Seg2Pressed = s2; t.Seg3Pressed = s3;
                    });
                SegmentedSide("Segmented damping released *",
                    Num("damping_release_segment1_position"), Num("damping_release_segment2_position"),
                    Num("damping_release_segment1_value"), Num("damping_release_segment2_value"), Num("damping_release_segment3_value"),
                    sd.Divider1Released, sd.Divider2Released, sd.Seg1Released, sd.Seg2Released, sd.Seg3Released,
                    MBoosterUiConstants.SegDampDivider1ReleasedDefaultPct, MBoosterUiConstants.SegDampDivider2ReleasedDefaultPct,
                    (t, d1, d2, s1, s2, s3) =>
                    {
                        t.Divider1Released = d1; t.Divider2Released = d2;
                        t.Seg1Released = s1; t.Seg2Released = s2; t.Seg3Released = s3;
                    });

                var sw = Bool("damping_switch");
                if (sw.HasValue)
                {
                    bool v = sw.Value;
                    Add("Segmented damping *", sd.DampingEnabled ? "On" : "Off", v ? "On" : "Off",
                        c => (c.SegmentedDamping ??= new MBoosterSegmentedDampingSettings()).DampingEnabled = v);
                }
            }

            private void VirtualDamping(string dir, string label,
                Func<IMBoosterPedalConfig, float> get, Action<IMBoosterPedalConfig, float> set)
            {
                var v = Num("damping_" + dir);
                var sw = Bool("damping_" + dir + "_switch");
                if (sw != false && v == null) return;
                float nv = sw == false ? 0f : (float)Math.Round(Clamp(v!.Value, 0, 100));
                Add(label + " (%)", Pct(get(_read)), Pct(nv), c => set(c, nv));
            }

            private void SegmentedSide(string label,
                double? d1, double? d2, double? s1, double? s2, double? s3,
                float cd1, float cd2, float cs1, float cs2, float cs3,
                float defD1, float defD2,
                Action<MBoosterSegmentedDampingSettings, float, float, float, float, float> set)
            {
                if (d1 == null && d2 == null && s1 == null && s2 == null && s3 == null) return;
                float Cur(float v, float dflt) => v >= 0 ? v : dflt;
                float Seg(double? v, float cur) =>
                    v.HasValue ? (float)Math.Round(Clamp(v.Value, 0, 100)) : Cur(cur, MBoosterUiConstants.SegDampSegDefaultPct);

                // The divider editor's bounds: [10,80] / [20,90], ≥10 apart.
                double a = Clamp(d1 ?? Cur(cd1, defD1), MBoosterUiConstants.SegDampDivider1MinPct, MBoosterUiConstants.SegDampDivider1MaxPct);
                double b = Clamp(d2 ?? Cur(cd2, defD2), MBoosterUiConstants.SegDampDivider2MinPct, MBoosterUiConstants.SegDampDivider2MaxPct);
                if (b - a < MBoosterUiConstants.SegDampDividerMinGapPct)
                {
                    b = Math.Min(MBoosterUiConstants.SegDampDivider2MaxPct, a + MBoosterUiConstants.SegDampDividerMinGapPct);
                    a = Math.Min(a, b - MBoosterUiConstants.SegDampDividerMinGapPct);
                }
                float na = (float)Math.Round(a), nb = (float)Math.Round(b);
                float n1 = Seg(s1, cs1), n2 = Seg(s2, cs2), n3 = Seg(s3, cs3);

                string Describe(float x1, float x2, float y1, float y2, float y3) =>
                    x1 < 0 || x2 < 0 || y1 < 0 || y2 < 0 || y3 < 0
                        ? "(unset)"
                        : $"{x1:F0}/{x2:F0}% · {y1:F0}/{y2:F0}/{y3:F0}%";
                Add(label, Describe(cd1, cd2, cs1, cs2, cs3), Describe(na, nb, n1, n2, n3),
                    c => set(c.SegmentedDamping ??= new MBoosterSegmentedDampingSettings(), na, nb, n1, n2, n3));
            }

            /// <summary>
            /// friction_press (+ _switch) → Natural Friction. The plugin writes
            /// one value to both 0xAE selectors, as every Pit House capture did;
            /// a preset whose release side differs is noted, not merged.
            /// </summary>
            public void Friction()
            {
                var p = Num("friction_press");
                var ps = Bool("friction_press_switch");
                var r = Num("friction_release");
                var rs = Bool("friction_release_switch");
                if (p == null && ps == null) return;

                bool curEn = _read.NaturalFrictionEnabled;
                float curPct = _read.NaturalFrictionPct;
                bool nEn = ps ?? curEn;
                float nPct = p.HasValue ? (float)Math.Round(Clamp(p.Value, 0, 100)) : curPct;
                string Describe(bool en, float pct) => (en ? "On" : "Off") + " · " + Pct(pct);
                Add("Natural friction *", Describe(curEn, curPct), Describe(nEn, nPct), c =>
                {
                    c.NaturalFrictionEnabled = nEn;
                    if (nPct >= 0) c.NaturalFrictionPct = nPct;
                });

                if ((r.HasValue && p.HasValue && Math.Abs(r.Value - p.Value) > 0.5)
                    || (rs.HasValue && ps.HasValue && rs.Value != ps.Value))
                    _plan.NotImported.Add($"{Key("friction_release")} = {(rs == false ? "off" : r?.ToString(CultureInfo.InvariantCulture) ?? "?")}    (plugin sets press and release together — press value used)");
            }

            /// <summary>gforce_switch / _max_pedal_movement (mm) / _response_speed
            /// (%) → the G-Force effect's Enabled / MaxTravelMm / ResponseSpeedPct —
            /// the same controls, named as in Pit House's own UI.</summary>
            public void GForce()
            {
                var cur = _read.GForce;
                if (cur == null) return;
                var sw = Bool("gforce_switch");
                var mv = Num("gforce_max_pedal_movement");
                var sp = Num("gforce_response_speed");
                if (sw == null && mv == null && sp == null) return;

                bool nEn = sw ?? cur.Enabled;
                float nMv = mv.HasValue
                    ? (float)Math.Round(Clamp(mv.Value, MBoosterUiConstants.GForceMaxTravelMinMm, MBoosterUiConstants.GForceMaxTravelMaxMm), 1)
                    : cur.MaxTravelMm;
                int nSp = sp.HasValue
                    ? (int)Math.Round(Clamp(sp.Value, MBoosterUiConstants.GForceResponseSpeedMinPct, MBoosterUiConstants.GForceResponseSpeedMaxPct))
                    : cur.ResponseSpeedPct;
                string Describe(bool en, float m, int s) =>
                    $"{(en ? "On" : "Off")} · {m.ToString("F1", CultureInfo.InvariantCulture)}mm · speed {s.ToString(CultureInfo.InvariantCulture)}%";
                Add("G-Force", Describe(cur.Enabled, cur.MaxTravelMm, cur.ResponseSpeedPct), Describe(nEn, nMv, nSp), c =>
                {
                    var t = c.GForce;
                    if (t == null) return;
                    t.Enabled = nEn;
                    t.MaxTravelMm = nMv;
                    t.ResponseSpeedPct = nSp;
                });
            }

            /// <summary>
            /// One row per vibration effect covering enable + intensity and
            /// whichever of frequency / smoothness / trigger / decay PitHouse
            /// carries for it. Absent sub-keys keep the current value.
            /// </summary>
            public void Effect(
                string label,
                Func<IMBoosterPedalConfig, MBoosterEffectSettings?> pick,
                string effectKey,
                string ampSuffix,
                string? freqSuffix = null, float freqLo = 0f, float freqHi = 0f,
                string? smoothSuffix = null,
                string? triggerSuffix = null,
                string? decaySuffix = null)
            {
                var current = pick(_read);
                if (current == null) return;

                var sw = Bool(effectKey + "_switch");
                var amp = Num(effectKey + "_" + ampSuffix);
                var freq = freqSuffix == null ? null : Num(effectKey + "_" + freqSuffix);
                var smooth = smoothSuffix == null ? null : Num(effectKey + "_" + smoothSuffix);
                var trigger = triggerSuffix == null ? null : Num(effectKey + "_" + triggerSuffix);
                var decay = decaySuffix == null ? null : Num(effectKey + "_" + decaySuffix);

                if (sw == null && amp == null && freq == null && smooth == null && trigger == null && decay == null)
                    return;

                bool newEnabled = sw ?? current.Enabled;
                int newAmp = amp.HasValue ? (int)Math.Round(Clamp(amp.Value, 0, 100)) : current.IntensityPct;
                float newFreq = freq.HasValue ? (float)Clamp(freq.Value, freqLo, freqHi) : current.FrequencyHz;
                int newSmooth = smooth.HasValue ? (int)Math.Round(Clamp(smooth.Value, 0, 100)) : current.SmoothnessPct;
                int newTrigger = trigger.HasValue
                    ? (int)Math.Round(Clamp(trigger.Value,
                        MBoosterUiConstants.ThresholdTriggerMinPct, MBoosterUiConstants.ThresholdTriggerMaxPct))
                    : current.TriggerLevelPct;
                int newDecay = decay.HasValue ? (int)Math.Round(Clamp(decay.Value, 0, 100)) : current.DecayPct;

                string Describe(bool en, int a, float f, int sm, int tr, int dc)
                {
                    var parts = new List<string> { en ? "On" : "Off", a.ToString(CultureInfo.InvariantCulture) + "%" };
                    if (freqSuffix != null) parts.Add(f.ToString("F0", CultureInfo.InvariantCulture) + "Hz");
                    if (smoothSuffix != null) parts.Add("smooth " + sm.ToString(CultureInfo.InvariantCulture));
                    if (triggerSuffix != null) parts.Add("trigger " + tr.ToString(CultureInfo.InvariantCulture) + "%");
                    if (decaySuffix != null) parts.Add("decay " + dc.ToString(CultureInfo.InvariantCulture));
                    return string.Join(" · ", parts);
                }

                string oldDisplay = Describe(current.Enabled, current.IntensityPct, current.FrequencyHz,
                    current.SmoothnessPct, current.TriggerLevelPct, current.DecayPct);
                string newDisplay = Describe(newEnabled, newAmp, newFreq, newSmooth, newTrigger, newDecay);

                Add(label, oldDisplay, newDisplay, c =>
                {
                    var t = pick(c);
                    if (t == null) return;
                    t.Enabled = newEnabled;
                    t.IntensityPct = newAmp;
                    if (freqSuffix != null) t.FrequencyHz = newFreq;
                    if (smoothSuffix != null) t.SmoothnessPct = newSmooth;
                    if (triggerSuffix != null) t.TriggerLevelPct = newTrigger;
                    if (decaySuffix != null) t.DecayPct = newDecay;
                });
            }

            /// <summary>Note every <c>&lt;prefix&gt;_&lt;family&gt;*</c> key as skipped, with a reason.</summary>
            public void SkipFamily(string family, string reason)
            {
                string p = _prefix + "_" + family;
                var keys = _dp.Properties()
                              .Where(x => x.Name.StartsWith(p, StringComparison.Ordinal))
                              .Select(x => x.Name)
                              .ToList();
                foreach (var k in keys)
                    PitHouseMotorMapper.AddSkipped(_plan, _dp, k, reason);
            }

            public void SkipKey(string suffix, string reason) =>
                PitHouseMotorMapper.AddSkipped(_plan, _dp, Key(suffix), reason);

            private static double Clamp(double v, double lo, double hi) =>
                v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
