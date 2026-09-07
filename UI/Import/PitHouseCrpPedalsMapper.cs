using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using MozaPlugin.Settings;

namespace MozaPlugin.UI.Import
{
    /// <summary>
    /// Maps a PitHouse Pedals preset onto the CRP / CRP2 / SRP pedal surface —
    /// the passive pedal sets whose calibration lives on <see cref="MozaProfile"/>
    /// (<c>Pedals*</c>) and reaches the device through
    /// <c>HardwareApplier.ApplyPedalsToHardware</c>.
    ///
    /// Distinct from <see cref="PitHousePedalsMapper"/>, which targets the mBooster:
    /// an mBooster is ONE pedal per device with its own settings row, so that mapper
    /// has to pick a subject role and one target pedal. A CRP is one device carrying
    /// all three pedals, so every populated role section here is that device's own
    /// throttle / brake / clutch — all three import, and there is nothing to retarget.
    ///
    /// Field names come from the preset format documented in
    /// <c>docs/protocol/devices/mbooster.md</c> § "PitHouse Pedals preset format";
    /// only the generic per-role calibration keys have a CRP equivalent. Anything
    /// else falls through to <see cref="PitHouseMotorMapper.SweepUnhandled"/> so no
    /// PitHouse setting disappears silently.
    /// </summary>
    public static class PitHouseCrpPedalsMapper
    {
        private static readonly (string Prefix, string Label)[] Roles =
        {
            ("throttle", "Throttle"),
            ("brake",    "Brake"),
            ("clutch",   "Clutch"),
        };

        public static ImportPlan BuildPlan(PitHousePreset preset, MozaProfile profile)
        {
            var plan = new ImportPlan();
            if (preset == null || profile == null) { plan.FatalError = "internal: null preset or profile"; return plan; }

            var dp = preset.DeviceParams;
            if (dp == null) { plan.FatalError = "preset has no deviceParams block"; return plan; }

            plan.ConsideredKeys.Add("version");
            // One device, three pedals — no subject role to pick and nothing to
            // retarget, so the wizard hides the "Apply to" selector.
            plan.AutoMatchedPerRole = true;

            var imported = new List<string>();
            foreach (var (prefix, label) in Roles)
            {
                int before = plan.Diffs.Count;

                // Which physical channel plays this role. The CRP surface is fixed
                // (throttle/brake/clutch), so there is nothing to write it to.
                PitHouseMotorMapper.AddSkipped(plan, dp, prefix + "_channlRoleType",
                                               "no plugin field — CRP pedal roles are fixed");

                AddDirection(plan, dp, prefix, label, profile);
                AddRange(plan, dp, prefix, label, profile);
                AddCurve(plan, dp, prefix, label, profile);

                if (string.Equals(prefix, "brake", StringComparison.Ordinal))
                    AddAngleRatio(plan, dp, label, profile);
                else
                    // pedals-brake-angle-ratio is the only sensor-ratio command in the
                    // CRP block, so a throttle/clutch copy has nowhere to land.
                    PitHouseMotorMapper.AddSkipped(plan, dp, prefix + "_press_combine", "brake-only");

                if (plan.Diffs.Count > before) imported.Add(label);
            }

            plan.SubjectRoleDisplay = imported.Count == 0
                ? "(none)"
                : string.Join(" + ", imported) + " (one pedal set)";

            PitHouseMotorMapper.SweepUnhandled(plan, dp);
            return plan;
        }

        // ------------------------------------------------------------
        //  Per-role field mapping
        // ------------------------------------------------------------

        private static void AddDirection(
            ImportPlan plan, JObject dp, string prefix, string label, MozaProfile profile)
        {
            var v = Num(plan, dp, prefix + "_outdir");
            if (v == null) return;

            int nv = v.Value != 0 ? 1 : 0;
            int ov = GetDir(profile, prefix);
            plan.Diffs.Add(new FieldDiff(
                $"{label} · Direction",
                ov < 0 ? "(unset)" : Dir(ov),
                Dir(nv),
                () => SetDir(profile, prefix, nv)));
        }

        private static string Dir(int v) => v != 0 ? "Reversed" : "Normal";

        /// <summary>
        /// <c>min</c>/<c>max</c> → the pedal's range start/end as ONE row, so the pair
        /// can't land somewhere the UI couldn't produce. Both sides are percent of
        /// travel (0-100): the CRP wire commands take a percent — unlike the mBooster,
        /// whose plugin fields are raw sensor counts, which is why
        /// <see cref="PitHousePedalsMapper"/> has to skip these two.
        /// </summary>
        private static void AddRange(
            ImportPlan plan, JObject dp, string prefix, string label, MozaProfile profile)
        {
            var lo = Num(plan, dp, prefix + "_min");
            var hi = Num(plan, dp, prefix + "_max");
            if (lo == null || hi == null)
            {
                // Half a pair can't be applied: writing one end against an unset
                // other end would invent a range out of the -1 sentinel.
                if (lo != null || hi != null)
                    plan.NotImported.Add($"{prefix}_min/_max    (only one end present — range needs both)");
                return;
            }

            int min = Clamp(lo.Value, 0, 100);
            int max = Clamp(hi.Value, 0, 100);
            // Mirror the UI's own constraint (OnMinMaxSliderChanged clamps each
            // slider to the other bound) rather than persisting an inverted pair.
            if (min > max) min = max;

            // ApplyPedalsToHardware writes max only when > 0 — a zero would update the
            // profile and the tab while the device silently kept its old range.
            if (max <= 0)
            {
                plan.NotImported.Add($"{prefix}_max = {max}    (0 is the plugin's \"unset\" sentinel — not applied)");
                return;
            }

            int oldMin = GetMin(profile, prefix), oldMax = GetMax(profile, prefix);
            plan.Diffs.Add(new FieldDiff(
                $"{label} · Range (min–max)",
                oldMin < 0 || oldMax < 0 ? "(unset)" : $"{oldMin}–{oldMax}%",
                $"{min}–{max}%",
                () => { SetMin(profile, prefix, min); SetMax(profile, prefix, max); }));
        }

        /// <summary>
        /// <c>nonlinear1..5</c> → the 5-point output curve (both sides 0-100), one row.
        /// All five must be present: filling a gap with 0 would flatten that segment.
        /// </summary>
        private static void AddCurve(
            ImportPlan plan, JObject dp, string prefix, string label, MozaProfile profile)
        {
            var y = new int[5];
            int found = 0;
            for (int i = 0; i < 5; i++)
            {
                var v = Num(plan, dp, prefix + "_nonlinear" + (i + 1));
                if (v == null) continue;
                y[i] = Clamp(v.Value, 0, 100);
                found++;
            }
            if (found == 0) return;
            if (found < 5)
            {
                plan.NotImported.Add($"{prefix}_nonlinear1..5    ({found} of 5 points present — curve needs all five)");
                return;
            }

            var old = GetCurve(profile, prefix);
            string oldDisplay = old == null || old.Length < 5
                ? "(unset)"
                : $"{old[0]}/{old[1]}/{old[2]}/{old[3]}/{old[4]}";
            plan.Diffs.Add(new FieldDiff(
                $"{label} · Output curve (Y at 20/40/60/80/100%)",
                oldDisplay,
                $"{y[0]}/{y[1]}/{y[2]}/{y[3]}/{y[4]}",
                () => SetCurve(profile, prefix, (int[])y.Clone())));
        }

        /// <summary>
        /// <c>brake_press_combine</c> → <c>PedalsBrakeAngleRatio</c>
        /// (<c>pedals-brake-angle-ratio</c>): the CRP2's angle-sensor ↔ load-cell
        /// blend, 0-100 on both sides.
        /// </summary>
        private static void AddAngleRatio(ImportPlan plan, JObject dp, string label, MozaProfile profile)
        {
            var v = Num(plan, dp, "brake_press_combine");
            if (v == null) return;

            int nv = Clamp(v.Value, 0, 100);
            int ov = profile.PedalsBrakeAngleRatio;
            plan.Diffs.Add(new FieldDiff(
                $"{label} · Sensor output ratio",
                ov < 0 ? "(unset)" : ov.ToString(CultureInfo.InvariantCulture) + "%",
                nv.ToString(CultureInfo.InvariantCulture) + "%",
                () => profile.PedalsBrakeAngleRatio = nv));
        }

        // ------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------

        /// <summary>Read a numeric deviceParams key, marking it considered either way
        /// so the catch-all sweep doesn't surface a key this mapper handled. A present
        /// but non-numeric value is reported rather than dropped — being considered
        /// means the sweep can no longer be the backstop for it.</summary>
        private static double? Num(ImportPlan plan, JObject dp, string key)
        {
            plan.ConsideredKeys.Add(key);
            var t = dp[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return (double)t; }
            catch
            {
                plan.NotImported.Add(
                    $"{key} = {PitHouseMotorMapper.FormatJTokenValue(t)}    (expected a number)");
                return null;
            }
        }

        private static int Clamp(double v, int lo, int hi)
        {
            int i = (int)Math.Round(v);
            return i < lo ? lo : (i > hi ? hi : i);
        }

        // Per-role profile accessors. A switch beats three parallel delegate sets:
        // MozaProfile stores the nine pedal fields flat, not per-role.

        private static int GetDir(MozaProfile p, string prefix) => prefix switch
        {
            "throttle" => p.PedalsThrottleDir,
            "brake"    => p.PedalsBrakeDir,
            _          => p.PedalsClutchDir,
        };

        private static void SetDir(MozaProfile p, string prefix, int v)
        {
            switch (prefix)
            {
                case "throttle": p.PedalsThrottleDir = v; break;
                case "brake":    p.PedalsBrakeDir    = v; break;
                default:         p.PedalsClutchDir   = v; break;
            }
        }

        private static int GetMin(MozaProfile p, string prefix) => prefix switch
        {
            "throttle" => p.PedalsThrottleMin,
            "brake"    => p.PedalsBrakeMin,
            _          => p.PedalsClutchMin,
        };

        private static void SetMin(MozaProfile p, string prefix, int v)
        {
            switch (prefix)
            {
                case "throttle": p.PedalsThrottleMin = v; break;
                case "brake":    p.PedalsBrakeMin    = v; break;
                default:         p.PedalsClutchMin   = v; break;
            }
        }

        private static int GetMax(MozaProfile p, string prefix) => prefix switch
        {
            "throttle" => p.PedalsThrottleMax,
            "brake"    => p.PedalsBrakeMax,
            _          => p.PedalsClutchMax,
        };

        private static void SetMax(MozaProfile p, string prefix, int v)
        {
            switch (prefix)
            {
                case "throttle": p.PedalsThrottleMax = v; break;
                case "brake":    p.PedalsBrakeMax    = v; break;
                default:         p.PedalsClutchMax   = v; break;
            }
        }

        private static int[]? GetCurve(MozaProfile p, string prefix) => prefix switch
        {
            "throttle" => p.PedalsThrottleCurve,
            "brake"    => p.PedalsBrakeCurve,
            _          => p.PedalsClutchCurve,
        };

        private static void SetCurve(MozaProfile p, string prefix, int[] v)
        {
            switch (prefix)
            {
                case "throttle": p.PedalsThrottleCurve = v; break;
                case "brake":    p.PedalsBrakeCurve    = v; break;
                default:         p.PedalsClutchCurve   = v; break;
            }
        }
    }
}
