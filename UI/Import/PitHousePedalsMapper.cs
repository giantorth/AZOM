using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using MozaPlugin.Devices;

namespace MozaPlugin.UI.Import
{
    /// <summary>
    /// Maps a PitHouse Pedals preset (mBooster-only — non-mBooster pedal
    /// presets have no calibration surface the plugin exposes) onto the
    /// attached mBooster controllers' <see cref="MBoosterDeviceSettings"/>.
    ///
    /// A single PitHouse preset file can carry calibration sections for any
    /// of throttle/brake/clutch — sometimes one role is "real" (the preset's
    /// theme) and the others are just defaults, sometimes the preset is a
    /// full three-pedal reset. We treat each role independently: if its
    /// section is populated (any of outdir/min/max/nonlinear1..5 present)
    /// AND there is at least one attached mBooster carrying that role, we
    /// emit diffs for that pairing. Sections without a matching attached
    /// device are surfaced under "Not imported" so the user knows what was
    /// dropped.
    /// </summary>
    public static class PitHousePedalsMapper
    {
        /// <summary>
        /// Build the apply plan. <paramref name="controllers"/> should be the
        /// live list of detected mBoosters from
        /// <c>MozaPlugin.MBoosterRegistry.Devices</c>. Each controller's
        /// <see cref="MBoosterDeviceController.CurrentSettings"/> is read for
        /// the "before" half of each diff.
        /// </summary>
        public static ImportPlan BuildPlan(
            PitHousePreset preset,
            IReadOnlyList<MBoosterDeviceController> controllers)
        {
            var plan = new ImportPlan();
            if (preset == null) { plan.FatalError = "internal: null preset"; return plan; }
            controllers ??= Array.Empty<MBoosterDeviceController>();

            var dp = preset.DeviceParams;

            // Map PitHouse role prefix to MBoosterRole. The trailing "" entry
            // doubles as a label for the diff rows.
            var roles = new (string Prefix, MBoosterRole Role, string Label)[]
            {
                ("throttle", MBoosterRole.Throttle, "Throttle"),
                ("brake",    MBoosterRole.Brake,    "Brake"),
                ("clutch",   MBoosterRole.Clutch,   "Clutch"),
            };

            foreach (var (prefix, role, label) in roles)
            {
                // _channlRoleType is the role marker — register it considered
                // whether or not the section has calibration data.
                plan.ConsideredKeys.Add(prefix + "_channlRoleType");

                if (!IsRoleSectionPopulated(dp, prefix)) continue;

                var matching = controllers.Where(c =>
                {
                    var s = c.CurrentSettings;
                    return s != null && s.Role == role;
                }).ToList();

                if (matching.Count == 0)
                {
                    plan.NotImported.Add($"{label}: no mBooster currently attached with this role");
                    // Still mark this role's keys considered so the sweep
                    // doesn't double-list them per-key below.
                    MarkRoleSectionConsidered(plan, dp, prefix);
                    continue;
                }

                foreach (var controller in matching)
                    AddPerControllerDiffs(plan, dp, prefix, label, controller);
            }

            // Catch-all: every deviceParams key the mapper hasn't touched gets
            // surfaced in Not Imported with its value, so no PitHouse setting
            // silently disappears. Reuses the motor mapper's sweep helper.
            PitHouseMotorMapper.SweepUnhandled(plan, dp);

            return plan;
        }

        /// <summary>
        /// Mark every <c>&lt;prefix&gt;_*</c> key in <paramref name="dp"/> as
        /// considered, so the catch-all sweep doesn't surface them individually
        /// when an entire role section was skipped (e.g. no attached mBooster
        /// with that role).
        /// </summary>
        private static void MarkRoleSectionConsidered(ImportPlan plan, JObject dp, string prefix)
        {
            var p = prefix + "_";
            foreach (var prop in dp.Properties())
                if (prop.Name.StartsWith(p, StringComparison.Ordinal))
                    plan.ConsideredKeys.Add(prop.Name);
        }

        // ----- Helpers -----

        private static bool IsRoleSectionPopulated(JObject dp, string prefix)
        {
            // Any of the core calibration fields present (and not null) →
            // treat the section as real.
            string[] keys = {
                prefix + "_outdir",
                prefix + "_min",
                prefix + "_max",
                prefix + "_nonlinear1",
                prefix + "_nonlinear2",
                prefix + "_nonlinear3",
                prefix + "_nonlinear4",
                prefix + "_nonlinear5",
            };
            foreach (var k in keys)
            {
                var t = dp[k];
                if (t != null && t.Type != JTokenType.Null) return true;
            }
            return false;
        }

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

        private static void AddPerControllerDiffs(
            ImportPlan plan, JObject dp, string prefix, string roleLabel,
            MBoosterDeviceController controller)
        {
            var settings = controller.CurrentSettings;
            if (settings == null) return;

            // Device label combines role + user-facing display name so a user
            // with two brake-role mBoosters can tell rows apart.
            string deviceLabel = roleLabel;
            if (!string.IsNullOrEmpty(settings.DisplayName))
                deviceLabel = roleLabel + " — " + settings.DisplayName;

            // Direction
            plan.ConsideredKeys.Add(prefix + "_outdir");
            var dir = IntOrNull(dp, prefix + "_outdir");
            if (dir.HasValue)
            {
                int oldVal = settings.Direction;
                plan.Diffs.Add(new FieldDiff(
                    deviceLabel + " · Direction",
                    oldVal < 0 ? "(unset)" : oldVal.ToString(),
                    dir.Value.ToString(),
                    () => settings.Direction = dir.Value));
                plan.TouchedMBoosters.Add(controller);
            }

            // Min
            plan.ConsideredKeys.Add(prefix + "_min");
            var min = IntOrNull(dp, prefix + "_min");
            if (min.HasValue)
            {
                int oldVal = settings.Min;
                plan.Diffs.Add(new FieldDiff(
                    deviceLabel + " · Min",
                    oldVal < 0 ? "(unset)" : oldVal.ToString(),
                    min.Value.ToString(),
                    () => settings.Min = min.Value));
                plan.TouchedMBoosters.Add(controller);
            }

            // Max
            plan.ConsideredKeys.Add(prefix + "_max");
            var max = IntOrNull(dp, prefix + "_max");
            if (max.HasValue)
            {
                int oldVal = settings.Max;
                plan.Diffs.Add(new FieldDiff(
                    deviceLabel + " · Max",
                    oldVal < 0 ? "(unset)" : oldVal.ToString(),
                    max.Value.ToString(),
                    () => settings.Max = max.Value));
                plan.TouchedMBoosters.Add(controller);
            }

            // Curve Y1..Y5 — emit one combined diff that writes all five
            var y = new float?[5];
            bool anyCurvePoint = false;
            for (int i = 0; i < 5; i++)
            {
                string ck = prefix + "_nonlinear" + (i + 1);
                plan.ConsideredKeys.Add(ck);
                var v = IntOrNull(dp, ck);
                if (v.HasValue) { y[i] = v.Value; anyCurvePoint = true; }
            }
            if (anyCurvePoint)
            {
                // Default missing points to 0 so the array is always length-5
                // (matches what ApplyMBoosterToHardware expects).
                var newCurve = new float[5];
                for (int i = 0; i < 5; i++) newCurve[i] = y[i] ?? 0f;

                var oldCurve = settings.CurveY;
                string oldDisplay = oldCurve == null || oldCurve.Length < 5
                    ? "(unset)"
                    : string.Join("/", oldCurve.Take(5).Select(v => ((int)Math.Round(v)).ToString()));
                string newDisplay = string.Join("/", newCurve.Select(v => ((int)Math.Round(v)).ToString()));

                plan.Diffs.Add(new FieldDiff(
                    deviceLabel + " · Curve (Y at 20/40/60/80/100%)",
                    oldDisplay, newDisplay,
                    () => settings.CurveY = newCurve));
                plan.TouchedMBoosters.Add(controller);
            }

            // Brake-only effects: ABS / Lockup / Threshold — only emit when
            // the preset is for the brake role.
            if (prefix == "brake")
            {
                AddEffect(plan, dp, "brake_abs_switch", "brake_abs_amp",
                          deviceLabel + " · ABS", settings.Abs, controller);
                AddEffect(plan, dp, "brake_lockup_switch", "brake_lockup_amp",
                          deviceLabel + " · Lockup", settings.Lockup, controller);
                AddEffect(plan, dp, "brake_brakethreshold_switch", "brake_brakethreshold_amp",
                          deviceLabel + " · Threshold", settings.Threshold, controller);
            }
        }

        private static void AddEffect(
            ImportPlan plan, JObject dp,
            string switchKey, string ampKey,
            string label, MBoosterEffectSettings target,
            MBoosterDeviceController touched)
        {
            plan.ConsideredKeys.Add(switchKey);
            plan.ConsideredKeys.Add(ampKey);
            var sw = BoolOrNull(dp, switchKey);
            var amp = IntOrNull(dp, ampKey);
            if (sw == null && amp == null) return;
            if (target == null) return;

            int newAmp = amp.HasValue
                ? Math.Max(0, Math.Min(100, amp.Value))
                : target.IntensityPct;
            bool newEnabled = sw ?? target.Enabled;

            string oldDisplay = (target.Enabled ? "On" : "Off") + " @ " + target.IntensityPct + "%";
            string newDisplay = (newEnabled ? "On" : "Off") + " @ " + newAmp + "%";

            plan.Diffs.Add(new FieldDiff(label, oldDisplay, newDisplay,
                () =>
                {
                    target.Enabled = newEnabled;
                    target.IntensityPct = newAmp;
                }));
            plan.TouchedMBoosters.Add(touched);
        }
    }
}
