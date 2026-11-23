using System;
using System.Collections.Generic;
using System.Linq;
using MozaPlugin.Resources;
using MozaPlugin.Settings;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Devices.Led
{
    internal enum LedDefaultsKind { Wheel, Dash, Base }

    /// <summary>
    /// Default SimHub LED effects for MOZA devices, mirroring Pit House's official
    /// presets (docs/simhub.md § Default LED effects). Generated from the telemetry
    /// layout a device.json declares and shipped as the definition's defaults.json,
    /// which SimHub applies only to a newly added device. Only the telemetry (S2)
    /// driver gets effects: button, knob and individual-LED profiles keep SimHub's
    /// own defaults so the plugin's button/knob colour handling is untouched.
    /// </summary>
    internal static class LedDefaultProfiles
    {
        public const string DefaultsFileName = "defaults.json";

        // Pit House puts its status and flag effects on 3-LED flag segments. A
        // strip without them (or with a narrower brow, like the SCV12's 1/8/1)
        // gets RPM across every LED and nothing else.
        private const int FlagSegmentSize = 3;

        // Colours in the form SimHub's ColorConverter writes them.
        private const string Green = "Lime", Red = "Red", Blue = "Blue", Yellow = "Yellow", White = "White";
        private const string TcPink = "248, 86, 206";

        // Pit House RPM presets by LED count: per-LED threshold (% of SimHub's
        // displayed RPM range, the same signal Pit House reads) and colour
        // (G/R/M = green/red/magenta).
        private static readonly Dictionary<int, (int[] Percent, string Colors)> RpmPresets =
            new Dictionary<int, (int[], string)>
            {
                [6] = (new[] { 75, 79, 83, 87, 91, 95 }, "GGRRMM"),
                [9] = (new[] { 75, 79, 82, 85, 87, 88, 89, 90, 92 }, "GGGRRRMMM"),
                [10] = (new[] { 75, 79, 82, 85, 87, 88, 89, 90, 92, 94 }, "GGGRRRRMMM"),
                [11] = (new[] { 75, 77, 79, 81, 83, 85, 87, 88, 91, 93, 94 }, "GGGGRRRRMMM"),
                [12] = (new[] { 75, 77, 79, 81, 83, 85, 87, 88, 91, 93, 94, 95 }, "GGGGRRRRMMMM"),
                [15] = (new[] { 75, 77, 79, 81, 83, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94 }, "GGGGRRRRRRMMMMM"),
            };

        public static LedDefaultsKind? KindFor(string deviceTypeId)
        {
            if (string.IsNullOrEmpty(deviceTypeId)) return null;
            if (MozaDeviceConstants.IsDashDevice(deviceTypeId)) return LedDefaultsKind.Dash;
            if (MozaDeviceConstants.IsBaseDevice(deviceTypeId)) return LedDefaultsKind.Base;
            if (MozaDeviceConstants.GetWheelModelPrefix(deviceTypeId) != null) return LedDefaultsKind.Wheel;
            return null;
        }

        /// <summary>
        /// The default profile (a serialized SimHub <c>Profile</c>) for a device.json,
        /// or null when the device is not a MOZA wheel/dash/base or has no telemetry LEDs.
        /// </summary>
        public static JObject? BuildProfile(JObject deviceJson)
        {
            var id = deviceJson.SelectToken("DescriptorUniqueId")?.Value<string>();
            if (string.IsNullOrEmpty(id)) return null;
            var kind = KindFor(id!);
            if (kind == null) return null;

            if (!(deviceJson["LedsFeature"] is JObject leds) || !IsEnabled(leds)) return null;
            if (!(leds["LogicalTelemetryLeds"] is JObject telemetry) || !IsEnabled(telemetry)) return null;
            int count = (int?)telemetry["LedCount"] ?? 0;
            if (count <= 0) return null;

            int side = 0;
            if (kind == LedDefaultsKind.Dash)
                side = FlagSegmentSize;     // Pit House's CM2 preset; the dash device.json declares no segments
            else if (telemetry["Segments"] is JArray segments)
                side = segments.Sum(s => Math.Max(0, (int?)s["Size"] ?? 0));
            // SimHub mirrors the side segments around the centre band
            // (TelemetryLedSection.LedSplit) and ignores a split that doesn't fit.
            if (count - 2 * side <= 0) side = 0;

            return Build(kind.Value, MozaDeviceConstants.LedDefaultsProfileId(id!), count, side);
        }

        /// <summary>defaults.json body: the LED sub-device only, so every other
        /// sub-device and every extension keeps SimHub's LoadDefaultSettings path.</summary>
        public static JObject BuildDefaults(JObject profile) => new JObject
        {
            ["Settings"] = new JObject
            {
                ["LEDS"] = new JObject
                {
                    // LedModuleDevice.SetSettings indexes this key unconditionally.
                    ["ledModuleSettings"] = new JObject(),
                    ["leds"] = new JObject
                    {
                        ["Profiles"] = new JArray(profile),
                        ["activeProfileId"] = (string?)profile["ProfileId"],
                    },
                },
            },
        };

        // Pit House priorities, lowest first: SimHub merges later containers over
        // earlier ones (LedResult.Merge), and an inactive effect is transparent.
        private static JObject Build(LedDefaultsKind kind, Guid id, int count, int side)
        {
            var containers = new JArray();
            if (kind == LedDefaultsKind.Base)
            {
                // Ultra-base preset: whole strip, no RPM.
                containers.Add(Limiter(1, count));
                containers.Add(WheelLock(1, count));
                containers.Add(Status("Status.TCActive", TcPink, 1, count));
                containers.Add(Status("Status.AbsActive", Yellow, 1, count));
            }
            else if (side < FlagSegmentSize)
            {
                containers.Add(Rpm(1, count));
            }
            else
            {
                int band = count - 2 * side;
                var segments = new[] { 1, side + band + 1 };
                void OnSegments(Func<int, JObject> make)
                {
                    foreach (int start in segments) containers.Add(make(start));
                }

                OnSegments(s => Status("Flags.WhiteFlag", White, s, side));
                OnSegments(s => Status("Flags.BlueFlag", Blue, s, side));
                OnSegments(s => Status("Flags.YellowFlag", Yellow, s, side));
                OnSegments(s => Status("Flags.GreenFlag", Green, s, side));
                containers.Add(Rpm(side + 1, band));
                containers.Add(Limiter(1, count));
                OnSegments(s => Status("Status.DrsOn", Green, s, side));
                OnSegments(s => Status("Status.DrsAvailable", Green, s, side));
                OnSegments(s => WheelLock(s, side));
                OnSegments(s => Status("Status.TCActive", TcPink, s, side));
                OnSegments(s => Status("Status.AbsActive", Yellow, s, side));
            }

            return new JObject
            {
                ["Name"] = Strings.LedDefaults_ProfileName,
                ["ProfileId"] = id.ToString(),
                ["CarChoices"] = new JArray(),
                ["UseStrictJSIsolation"] = true,
                ["UseProfileBrightness"] = false,
                ["LedContainers"] = containers,
            };
        }

        // One single-LED segment per LED so every threshold and colour is Pit
        // House's. SimHub blinks at the car's redline (Car settings) rather than
        // Pit House's fixed 94-96 %.
        private static JObject Rpm(int start, int count)
        {
            var segments = new JArray();
            for (int i = 0; i < count; i++)
            {
                var (percent, color) = RpmStep(count, i);
                string hex = color == 'G' ? "#00FF00" : color == 'R' ? "#FF0000" : "#FF00FF";
                segments.Add($"1;{percent};{hex};1;#FF0000");
            }
            return new JObject
            {
                ["BlinkDelay"] = 125,
                ["Segments"] = segments,
                ["SegmentsCount"] = count,
                ["BlinkEnabled"] = true,
                ["RpmMode"] = 0,
                ["RelativeToRedline"] = false,
                ["BlinkOnLastGear"] = true,
                ["StartPosition"] = start,
                ["IsEnabled"] = true,
                ["ContainerType"] = "RPMSegments",
            };
        }

        // Counts Pit House has no preset for resample the 10-LED one.
        private static (int Percent, char Color) RpmStep(int count, int i)
        {
            if (RpmPresets.TryGetValue(count, out var preset)) return (preset.Percent[i], preset.Colors[i]);
            var ten = RpmPresets[10];
            int j = count == 1 ? 9 : (int)Math.Round(i * 9.0 / (count - 1));
            return (ten.Percent[j], ten.Colors[j]);
        }

        // Blue/red alternating every 500 ms over the whole strip.
        private static JObject Limiter(int start, int count)
        {
            var limiter = Status("Status.SpeedLimiter", Blue, start, count);
            limiter["BlinkingColor"] = Red;
            limiter["BlinkEnabled"] = true;
            limiter["BlinkDelay"] = 500;
            return limiter;
        }

        // SimHub has no wheel-lock effect, so a formula one reads the plugin's own
        // lockup signal. isnull: a missing property would otherwise count as on.
        private static JObject WheelLock(int start, int count)
        {
            var wheelLock = Status("CustomStatus", Red, start, count);
            wheelLock["EnabledFormula"] = new JObject { ["Expression"] = $"isnull({PedalHapticsSettings.LockupTrigger}, 0)" };
            wheelLock["BlinkFormula"] = new JObject { ["Expression"] = "" };
            wheelLock["Description"] = Strings.LedDefaults_WheelLock;
            return wheelLock;
        }

        private static JObject Status(string type, string color, int start, int count) => new JObject
        {
            ["IdleColor"] = "Transparent",
            ["IdleBlinkingColor"] = "Transparent",
            ["EnableIdleBlinking"] = false,
            ["LedCount"] = count,
            ["Color"] = color,
            ["BlinkingColor"] = "Transparent",
            ["BlinkEnabled"] = false,
            ["StartPosition"] = start,
            ["IsEnabled"] = true,
            ["ContainerType"] = type,
        };

        private static bool IsEnabled(JObject section) => (bool?)section["IsEnabled"] ?? true;
    }
}
