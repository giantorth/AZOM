using System;
using Newtonsoft.Json.Linq;
using MozaPlugin.Resources;
using MozaPlugin.Settings;

namespace MozaPlugin.UI.Import
{
    /// <summary>
    /// Maps a Motion Manager preset (<c>tags.deviceType = "GMotion"</c>, e.g. the HMA150)
    /// onto the active profile's <see cref="MozaProfile.MotionEffects"/>. <c>Strength</c> is the
    /// slot intensity and <c>Flatness</c> its smoothness: the "iRacing gt3" and "iRacing
    /// caterham" presets match their captured <c>0x1E/05</c> / <c>0x1E/06</c> writes field for
    /// field (docs/protocol/motion/effect-parameters.md). Everything else in the preset has no
    /// write in the captures or drives host-rendered haptics the plugin doesn't send.
    /// </summary>
    public static class PitHouseMotionMapper
    {
        // Preset section per MotionEffectsSettings index.
        private static readonly string[] Sections = { "Pitch", "Roll", "Heave", "AccToPitch", "AccToRoll", "RoadDetail" };

        public static ImportPlan BuildPlan(PitHousePreset preset, MozaProfile profile)
        {
            var plan = new ImportPlan();
            if (preset == null || profile == null) { plan.FatalError = "internal: null preset or profile"; return plan; }
            var dp = preset.DeviceParams;
            var labels = new[]
            {
                Strings.Motion_Effect_Pitch, Strings.Motion_Effect_Roll, Strings.Motion_Effect_Heave,
                Strings.Motion_Effect_AccelPitch, Strings.Motion_Effect_AccelRoll, Strings.Motion_Effect_RoadDetail,
            };

            for (int i = 0; i < Sections.Length; i++)
            {
                plan.ConsideredKeys.Add(Sections[i]);
                if (!(dp[Sections[i]] is JObject section)) continue;
                int? strength = IntOrNull(section, "Strength");
                int? flatness = IntOrNull(section, "Flatness");
                if (strength == null || flatness == null) continue;
                // A disabled effect: assumed to be written as intensity 0 (no capture of one).
                bool enabled = (bool?)section["enabled"] ?? true;
                int intensity = enabled ? Clamp(strength.Value) : 0;
                int smoothness = Clamp(flatness.Value);

                int index = i;
                var (oldI, oldS) = profile.MotionEffects?.Get(index) ?? (-1, -1);
                plan.Diffs.Add(new FieldDiff($"{labels[i]} — {Strings.Motion_Panel_Intensity}",
                    Display(oldI), Display(intensity),
                    () => SetIntensity(profile, index, intensity)));
                plan.Diffs.Add(new FieldDiff($"{labels[i]} — {Strings.Motion_Panel_Smoothness}",
                    Display(oldS), Display(smoothness),
                    () => SetSmoothness(profile, index, smoothness)));

                foreach (var p in section.Properties())
                {
                    if (p.Name == "Strength" || p.Name == "Flatness" || p.Name == "enabled") continue;
                    plan.NotImported.Add($"{Sections[i]}.{p.Name} = {p.Value}    (no controller setting)");
                }
            }

            foreach (var haptic in new[] { "ABSTrigger", "EngineVibration", "GearShift", "TractionControl", "WheelSlip" })
            {
                plan.ConsideredKeys.Add(haptic);
                if (dp[haptic] != null) plan.NotImported.Add($"{haptic}    (haptic effects are not sent)");
            }
            plan.ConsideredKeys.Add("platform");
            if (dp["platform"] is JObject platform)
                foreach (var p in platform.Properties())
                    plan.NotImported.Add($"platform.{p.Name} = {p.Value}");

            PitHouseMotorMapper.SweepUnhandled(plan, dp);
            return plan;
        }

        private static void SetIntensity(MozaProfile profile, int index, int value)
        {
            var effects = profile.MotionEffects ??= new MotionEffectsSettings();
            var (_, s) = effects.Get(index);
            effects.Set(index, value, s);
        }

        private static void SetSmoothness(MozaProfile profile, int index, int value)
        {
            var effects = profile.MotionEffects ??= new MotionEffectsSettings();
            var (i, _) = effects.Get(index);
            effects.Set(index, i, value);
        }

        private static string Display(int v) => v < 0 ? "(unset)" : v + "%";

        private static int Clamp(int v) => Math.Max(0, Math.Min(100, v));

        private static int? IntOrNull(JObject o, string key)
        {
            var t = o[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return (int)Math.Round((double)t); }
            catch { return null; }
        }
    }
}
