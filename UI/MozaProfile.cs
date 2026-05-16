using System;
using System.Collections.Generic;
using System.Windows.Controls;
using MozaPlugin.Devices;
using Newtonsoft.Json;
using SimHub.Plugins.ProfilesCommon;

namespace MozaPlugin
{
    /// <summary>
    /// Per-wheel-page overlay layered on top of a <see cref="MozaProfile"/>'s
    /// baseline. Stored on <see cref="MozaProfile.WheelOverridesByPageGuid"/>,
    /// keyed by the SimHub device-extension's <c>DescriptorUniqueId</c> GUID
    /// (which is what SimHub uses to identify the per-page settings blob).
    ///
    /// Every scalar uses -1 (or null for arrays / strings) to mean "no
    /// override — fall through to the profile baseline". This lets a user
    /// keep e.g. brightness 100 in the GS V2P overlay while another wheel
    /// uses the profile's lower baseline.
    /// </summary>
    public sealed class WheelOverride
    {
        // Captures legacy JSON keys that no longer exist on the class (e.g.
        // pre-schema-v5 TelemetryEnabled, pre-schema-v4 TelemetryMzdashFolder).
        // Migration reads these values into the new schema and clears the dict.
        [Newtonsoft.Json.JsonExtensionData(WriteData = false)]
        internal Dictionary<string, Newtonsoft.Json.Linq.JToken>? LegacyJsonFields;

        // LED / mode
        public int WheelTelemetryMode { get; set; } = -1;
        public int WheelIdleEffect { get; set; } = -1;
        public int WheelButtonsIdleEffect { get; set; } = -1;
        public int WheelKnobIdleEffect { get; set; } = -1;
        public int WheelKnobLedMode { get; set; } = -1;
        public int WheelButtonsLedMode { get; set; } = -1;
        public int WheelTelemetryIdleSpeedMs { get; set; } = -1;
        public int WheelButtonsIdleSpeedMs { get; set; } = -1;
        public int WheelKnobIdleSpeedMs { get; set; } = -1;
        // NOTE: WheelSleep* (mode / timeout / speed / color) moved to
        // MozaPluginSettings.WheelSleepByPageGuid in schema v8 — sleep is a
        // firmware preference, not a per-game-per-wheel decision. Legacy
        // values get drained via LegacyJsonFields during migration.

        // Brightness (-1 = use profile baseline)
        public int WheelRpmBrightness { get; set; } = -1;
        public int WheelButtonsBrightness { get; set; } = -1;
        public int WheelFlagsBrightness { get; set; } = -1;
        public int WheelESRpmBrightness { get; set; } = -1;

        // ES wheel
        public int WheelRpmIndicatorMode { get; set; } = -1;
        public int WheelRpmDisplayMode { get; set; } = -1;

        // Inputs (newer FW silently drops readback — overlay is the source of truth)
        public int WheelPaddlesMode { get; set; } = -1;
        public int WheelClutchPoint { get; set; } = -1;
        public int WheelKnobMode { get; set; } = -1;
        public int WheelStickMode { get; set; } = -1;

        // Colors (packed)
        public int[]? WheelRpmColors { get; set; }
        public int[]? WheelRpmBlinkColors { get; set; }
        public int[]? WheelButtonColors { get; set; }
        public bool[]? WheelButtonDefaultDuringTelemetry { get; set; }
        public int[]? WheelFlagColors { get; set; }
        public int[]? WheelIdleColor { get; set; }
        public int[]? WheelESRpmColors { get; set; }
        public int[]? WheelKnobBackgroundColors { get; set; }
        public int[]? WheelKnobPrimaryColors { get; set; }
        public int[]? WheelKnobRingColors { get; set; }
        public int WheelKnobRingBrightness { get; set; } = -1;

        // Telemetry — per-game dashboard selection (per-wheel-page-per-game).
        public string? TelemetryProfileName { get; set; }
        public string? TelemetryMzdashPath { get; set; }
        // NOTE: TelemetryEnabled / TelemetryMzdashFolder / TelemetryWheelEra
        // moved to MozaPluginSettings dicts (per-wheel-page, shared across
        // games) — schema v4/v5/v6.

        public WheelOverride Clone()
        {
            return new WheelOverride
            {
                WheelTelemetryMode = WheelTelemetryMode,
                WheelIdleEffect = WheelIdleEffect,
                WheelButtonsIdleEffect = WheelButtonsIdleEffect,
                WheelKnobIdleEffect = WheelKnobIdleEffect,
                WheelKnobLedMode = WheelKnobLedMode,
                WheelButtonsLedMode = WheelButtonsLedMode,
                WheelTelemetryIdleSpeedMs = WheelTelemetryIdleSpeedMs,
                WheelButtonsIdleSpeedMs = WheelButtonsIdleSpeedMs,
                WheelKnobIdleSpeedMs = WheelKnobIdleSpeedMs,
                WheelRpmBrightness = WheelRpmBrightness,
                WheelButtonsBrightness = WheelButtonsBrightness,
                WheelFlagsBrightness = WheelFlagsBrightness,
                WheelESRpmBrightness = WheelESRpmBrightness,
                WheelRpmIndicatorMode = WheelRpmIndicatorMode,
                WheelRpmDisplayMode = WheelRpmDisplayMode,
                WheelPaddlesMode = WheelPaddlesMode,
                WheelClutchPoint = WheelClutchPoint,
                WheelKnobMode = WheelKnobMode,
                WheelStickMode = WheelStickMode,
                WheelRpmColors = WheelRpmColors != null ? (int[])WheelRpmColors.Clone() : null,
                WheelRpmBlinkColors = WheelRpmBlinkColors != null ? (int[])WheelRpmBlinkColors.Clone() : null,
                WheelButtonColors = WheelButtonColors != null ? (int[])WheelButtonColors.Clone() : null,
                WheelButtonDefaultDuringTelemetry = WheelButtonDefaultDuringTelemetry != null
                    ? (bool[])WheelButtonDefaultDuringTelemetry.Clone() : null,
                WheelFlagColors = WheelFlagColors != null ? (int[])WheelFlagColors.Clone() : null,
                WheelIdleColor = WheelIdleColor != null ? (int[])WheelIdleColor.Clone() : null,
                WheelESRpmColors = WheelESRpmColors != null ? (int[])WheelESRpmColors.Clone() : null,
                WheelKnobBackgroundColors = WheelKnobBackgroundColors != null
                    ? (int[])WheelKnobBackgroundColors.Clone() : null,
                WheelKnobPrimaryColors = WheelKnobPrimaryColors != null
                    ? (int[])WheelKnobPrimaryColors.Clone() : null,
                WheelKnobRingColors = WheelKnobRingColors != null
                    ? (int[])WheelKnobRingColors.Clone() : null,
                WheelKnobRingBrightness = WheelKnobRingBrightness,
                TelemetryProfileName = TelemetryProfileName,
                TelemetryMzdashPath = TelemetryMzdashPath,
            };
        }
    }

    /// <summary>
    /// Persisted AB9 active shifter configuration. All slider values are 0..100
    /// (matching the on-wire single-byte payload range). Stored per-profile so a
    /// user can save game-specific feel presets alongside wheelbase FFB tuning.
    /// </summary>
    public sealed class Ab9Settings
    {
        public Ab9Mode Mode { get; set; } = Ab9Mode.SevenPlusR_L1;
        public byte MechanicalResistance { get; set; } = 50;
        public byte Spring { get; set; }              = 50;
        public byte NaturalDamping { get; set; }      = 50;
        public byte NaturalFriction { get; set; }     = 50;
        public byte MaxTorqueLimit { get; set; }      = 50;

        // Host-rendered engine vibration. Intensity (0..100 %) gates the
        // 0x0A 0x05 stream's slot ID (0 = silent slot 0x0000). Frequency is
        // the literal target Hz (0..300) of the oscillator the AB9 firmware
        // runs from the streamed period field. Neither value is pushed as a
        // stored device setting — they modulate the SimHub plugin's host-side
        // stream generator only.
        public byte EngineVibrationIntensity { get; set; } = 0;
        public ushort EngineVibrationFrequency { get; set; } = 100;

        // Firmware-driven gear-shift vibration. One-shot 0x20/dev 0x12/cmd 0x0A 0x01
        // push when the user moves this slider; AB9 firmware then fires the rumble
        // pattern autonomously on every HID-detected gear engagement.
        public byte GearShiftVibrationIntensity { get; set; } = 0;

        public Ab9Settings Clone()
        {
            return new Ab9Settings
            {
                Mode = Mode,
                MechanicalResistance = MechanicalResistance,
                Spring = Spring,
                NaturalDamping = NaturalDamping,
                NaturalFriction = NaturalFriction,
                MaxTorqueLimit = MaxTorqueLimit,
                EngineVibrationIntensity = EngineVibrationIntensity,
                EngineVibrationFrequency = EngineVibrationFrequency,
                GearShiftVibrationIntensity = GearShiftVibrationIntensity,
            };
        }
    }

    /// <summary>
    /// A named profile snapshot of all Moza device configuration.
    /// Extends SimHub's ProfileBase for native per-game profile switching.
    /// All integer settings use -1 as sentinel for "not included in this profile".
    /// Colors are stored as packed ints (R &lt;&lt; 16 | G &lt;&lt; 8 | B) for clean JSON serialization.
    /// </summary>
    public class MozaProfile : ProfileBase<MozaProfile, MozaProfileStore>, IProfile, IProfile<MozaProfile, MozaProfileStore>
    {
        [JsonIgnore]
        public override Control ProfileContentControl => null!;

        // Captures pre-schema-v8 JSON keys that no longer exist on this class
        // (e.g. WheelSleepMode / WheelSleepTimeoutMin / WheelSleepSpeedMs /
        // WheelSleepColor — moved to MozaPluginSettings.WheelSleepByPageGuid).
        // Migration reads these into the per-wheel-page dict and clears the
        // capture, so subsequent saves don't re-emit them.
        [JsonExtensionData(WriteData = false)]
        internal Dictionary<string, Newtonsoft.Json.Linq.JToken>? LegacyJsonFields;

        // ===== Base/Motor settings (raw device values from MozaData) =====
        public int Limit { get; set; } = -1;               // raw = degrees / 2
        public int FfbStrength { get; set; } = -1;          // raw = percent * 10
        public int Torque { get; set; } = -1;               // percent
        public int Speed { get; set; } = -1;                // raw = percent * 10
        public int Damper { get; set; } = -1;               // raw = percent * 10
        public int Friction { get; set; } = -1;             // raw = percent * 10
        public int Inertia { get; set; } = -1;              // raw = percent * 10
        public int Spring { get; set; } = -1;               // raw = percent * 10
        public int SpeedDamping { get; set; } = -1;
        public int SpeedDampingPoint { get; set; } = -1;
        public int NaturalInertia { get; set; } = -1;
        public int SoftLimitStiffness { get; set; } = -1;   // raw uses formula
        public int SoftLimitRetain { get; set; } = -1;      // 0/1
        public int FfbReverse { get; set; } = -1;           // 0/1
        public int Protection { get; set; } = -1;           // 0/1

        // ===== Game effect gains (raw = percent * 2.55) =====
        public int GameDamper { get; set; } = -1;
        public int GameFriction { get; set; } = -1;
        public int GameInertia { get; set; } = -1;
        public int GameSpring { get; set; } = -1;

        // ===== Work mode =====
        public int WorkMode { get; set; } = -1;             // 0/1

        // ===== Wheel LED settings =====
        public int WheelTelemetryMode { get; set; } = -1;
        public int WheelIdleEffect { get; set; } = -1;
        public int WheelButtonsIdleEffect { get; set; } = -1;
        public int WheelKnobIdleEffect { get; set; } = -1;
        public int WheelKnobLedMode { get; set; } = -1;
        public int WheelButtonsLedMode { get; set; } = -1;
        public int WheelTelemetryIdleSpeedMs { get; set; } = -1;
        public int WheelButtonsIdleSpeedMs { get; set; } = -1;
        public int WheelKnobIdleSpeedMs { get; set; } = -1;
        // NOTE: WheelSleep* (mode / timeout / speed / color) moved to
        // MozaPluginSettings.WheelSleepByPageGuid in schema v8 — see the
        // baseline-shared LegacyJsonFields capture above.
        public int WheelRpmBrightness { get; set; } = -1;
        public int WheelButtonsBrightness { get; set; } = -1;
        public int WheelFlagsBrightness { get; set; } = -1;
        // ===== ES/Old wheel settings =====
        public int WheelRpmIndicatorMode { get; set; } = -1;
        public int WheelRpmDisplayMode { get; set; } = -1;
        public int WheelESRpmBrightness { get; set; } = -1;

        // ===== Dashboard settings =====
        public int DashRpmBrightness { get; set; } = -1;
        public int DashFlagsBrightness { get; set; } = -1;
        public int DashDisplayBrightness { get; set; } = -1;
        public int DashDisplayStandbyMin { get; set; } = -1;

        // ===== FFB Equalizer (6 bands) =====
        public int Equalizer1 { get; set; } = -1000;
        public int Equalizer2 { get; set; } = -1000;
        public int Equalizer3 { get; set; } = -1000;
        public int Equalizer4 { get; set; } = -1000;
        public int Equalizer5 { get; set; } = -1000;
        public int Equalizer6 { get; set; } = -1000;

        // ===== FFB Curve (Y outputs at fixed 20/40/60/80/100% breakpoints) =====
        public int FfbCurveY1 { get; set; } = -1;
        public int FfbCurveY2 { get; set; } = -1;
        public int FfbCurveY3 { get; set; } = -1;
        public int FfbCurveY4 { get; set; } = -1;
        public int FfbCurveY5 { get; set; } = -1;

        // ===== Handbrake settings =====
        public int HandbrakeMode { get; set; } = -1;             // 0=Axis, 1=Button
        public int HandbrakeButtonThreshold { get; set; } = -1;  // 0-100
        public int HandbrakeDirection { get; set; } = -1;        // 0=Normal, 1=Reversed
        public int HandbrakeMin { get; set; } = -1;
        public int HandbrakeMax { get; set; } = -1;
        public int[]? HandbrakeCurve { get; set; }               // [5] values 0-100

        // ===== Pedal settings =====
        public int PedalsThrottleDir { get; set; } = -1;
        public int PedalsBrakeDir { get; set; } = -1;
        public int PedalsBrakeAngleRatio { get; set; } = -1; // 0=angle sensor, 100=load cell
        public int PedalsClutchDir { get; set; } = -1;
        public int[]? PedalsThrottleCurve { get; set; }          // [5] values 0-100
        public int[]? PedalsBrakeCurve { get; set; }             // [5] values 0-100
        public int[]? PedalsClutchCurve { get; set; }            // [5] values 0-100

        // ===== Color arrays (packed as R<<16 | G<<8 | B) =====
        public int[]? WheelRpmColors { get; set; }       // [10]
        public int[]? WheelRpmBlinkColors { get; set; }  // [10]
        public int[]? WheelButtonColors { get; set; }     // [14]
        public bool[]? WheelButtonDefaultDuringTelemetry { get; set; } // [14]
        public int[]? WheelFlagColors { get; set; }       // [6]
        public int[]? WheelIdleColor { get; set; }        // [1]
        public int[]? WheelESRpmColors { get; set; }     // [10]
        public int[]? WheelKnobBackgroundColors { get; set; } // [5] — W17/W18
        public int[]? WheelKnobPrimaryColors { get; set; }    // [5] — W17/W18
        public int[]? WheelKnobRingColors { get; set; }       // [56] — Group 3 per-LED ring
        public int WheelKnobRingBrightness { get; set; } = -1;
        public int[]? DashRpmColors { get; set; }         // [10]
        public int[]? DashRpmBlinkColors { get; set; }   // [10]
        public int[]? DashFlagColors { get; set; }        // [6]

        // ===== AB9 active shifter =====
        // Null until the user touches the AB9 panel for this profile — leaves
        // older serialized profiles untouched on load and avoids pushing default
        // values to a device that isn't attached.
        public Ab9Settings? Ab9 { get; set; }

        // ===== Active dashboard for this game profile =====
        // Stable key in the same format MozaPlugin.GetActiveDashboardKeyCandidates() emits:
        //   "wheel:<configJsonId>"     — wheel-resident dashboard, stable across re-uploads
        //   "file:<filename>:<sha1-8>" — custom .mzdash file
        //   "builtin:<name>"           — embedded plugin profile
        // Null = no preference; the wheel keeps whatever dashboard is currently displayed
        // when this profile loads. Captured from the active dashboard at SaveSettings()
        // time; re-applied by ApplyProfile() so each SimHub game gets its own dashboard.
        public string? TelemetryDashboardKey { get; set; }

        // ===== Wheel-page overlays =====
        // Per-wheel-page deltas on top of the profile baseline. Keyed by the
        // SimHub device-extension's DescriptorUniqueId GUID. See
        // <see cref="WheelOverride"/> for the full field set.
        public Dictionary<Guid, WheelOverride> WheelOverridesByPageGuid { get; set; }
            = new Dictionary<Guid, WheelOverride>();

        // ===== Telemetry channel mappings (profile × wheel-page × dashboard × channel) =====
        // Outer key  = wheel page DescriptorUniqueId GUID
        // Middle key = dashboard identity (see MozaPlugin.GetActiveDashboardKeyCandidates)
        // Inner key  = channel URL (e.g. "v1/gameData/Rpm")
        // Value      = SimHub property path (empty = use Telemetry.json default)
        //
        // Profile-scoped so each game gets its own mapping set; wheel-scoped because
        // dashboards available on a VGS aren't available on a CS V2.1.
        public Dictionary<Guid, Dictionary<string, Dictionary<string, string>>> TelemetryChannelMappings { get; set; }
            = new Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>();

        // ===== Wheelbase ambient LED (per-profile) =====
        // Moved out of MozaPluginSettings 2026-05-14 so each game can pick its own
        // ambient palette. -1 sentinel = "leave the persisted base value alone".
        public int BaseAmbientBrightness { get; set; } = -1;
        public int BaseAmbientStandbyMode { get; set; } = -1;
        public int BaseAmbientIndicatorState { get; set; } = -1;
        public int BaseAmbientSleepMode { get; set; } = -1;
        public int BaseAmbientSleepTimeout { get; set; } = -1;
        public int BaseAmbientStartupColor { get; set; } = -1;   // packed RGB
        public int BaseAmbientShutdownColor { get; set; } = -1;  // packed RGB

        // ===== Gearshift event tuning (per-profile) =====
        // Plugin-side gearshift event coalescing. Moved out of MozaPluginSettings
        // 2026-05-14 since users want per-game tuning (H-pattern vs paddles).
        // -1 = unset (fall back to plugin default: VibrateOnNeutral=0, DebounceMs=500).
        public int GearshiftVibrateOnNeutral { get; set; } = -1;  // 0/1
        public int GearshiftDebounceMs { get; set; } = -1;

        // ===== ProfileBase abstract implementation =====

        public override void CopyProfilePropertiesFrom(MozaProfile p)
        {
            // Base/Motor
            Limit = p.Limit; FfbStrength = p.FfbStrength; Torque = p.Torque;
            Speed = p.Speed; Damper = p.Damper; Friction = p.Friction;
            Inertia = p.Inertia; Spring = p.Spring;
            SpeedDamping = p.SpeedDamping; SpeedDampingPoint = p.SpeedDampingPoint;
            NaturalInertia = p.NaturalInertia; SoftLimitStiffness = p.SoftLimitStiffness;
            SoftLimitRetain = p.SoftLimitRetain; FfbReverse = p.FfbReverse;
            Protection = p.Protection;

            // Game effects
            GameDamper = p.GameDamper; GameFriction = p.GameFriction;
            GameInertia = p.GameInertia; GameSpring = p.GameSpring;
            WorkMode = p.WorkMode;

            // Wheel LED
            WheelTelemetryMode = p.WheelTelemetryMode; WheelIdleEffect = p.WheelIdleEffect;
            WheelButtonsIdleEffect = p.WheelButtonsIdleEffect;
            WheelKnobIdleEffect = p.WheelKnobIdleEffect;
            WheelKnobLedMode = p.WheelKnobLedMode;
            WheelButtonsLedMode = p.WheelButtonsLedMode;
            WheelTelemetryIdleSpeedMs = p.WheelTelemetryIdleSpeedMs;
            WheelButtonsIdleSpeedMs = p.WheelButtonsIdleSpeedMs;
            WheelKnobIdleSpeedMs = p.WheelKnobIdleSpeedMs;
            // WheelSleep* now lives on MozaPluginSettings.WheelSleepByPageGuid
            // (per-wheel, shared across profiles) — not copied here.
            WheelRpmBrightness = p.WheelRpmBrightness; WheelButtonsBrightness = p.WheelButtonsBrightness;
            WheelFlagsBrightness = p.WheelFlagsBrightness;

            // ES wheel
            WheelRpmIndicatorMode = p.WheelRpmIndicatorMode; WheelRpmDisplayMode = p.WheelRpmDisplayMode;
            WheelESRpmBrightness = p.WheelESRpmBrightness;

            // Dashboard
            DashRpmBrightness = p.DashRpmBrightness; DashFlagsBrightness = p.DashFlagsBrightness;
            DashDisplayBrightness = p.DashDisplayBrightness; DashDisplayStandbyMin = p.DashDisplayStandbyMin;

            // FFB Equalizer
            Equalizer1 = p.Equalizer1; Equalizer2 = p.Equalizer2; Equalizer3 = p.Equalizer3;
            Equalizer4 = p.Equalizer4; Equalizer5 = p.Equalizer5; Equalizer6 = p.Equalizer6;

            // FFB Curve
            FfbCurveY1 = p.FfbCurveY1; FfbCurveY2 = p.FfbCurveY2; FfbCurveY3 = p.FfbCurveY3; FfbCurveY4 = p.FfbCurveY4; FfbCurveY5 = p.FfbCurveY5;

            // Handbrake
            HandbrakeMode = p.HandbrakeMode;
            HandbrakeButtonThreshold = p.HandbrakeButtonThreshold;
            HandbrakeDirection = p.HandbrakeDirection;
            HandbrakeMin = p.HandbrakeMin; HandbrakeMax = p.HandbrakeMax;
            HandbrakeCurve = CloneArray(p.HandbrakeCurve);

            // Pedals
            PedalsThrottleDir = p.PedalsThrottleDir; PedalsBrakeDir = p.PedalsBrakeDir; PedalsClutchDir = p.PedalsClutchDir;
            PedalsBrakeAngleRatio = p.PedalsBrakeAngleRatio;
            PedalsThrottleCurve = CloneArray(p.PedalsThrottleCurve);
            PedalsBrakeCurve = CloneArray(p.PedalsBrakeCurve);
            PedalsClutchCurve = CloneArray(p.PedalsClutchCurve);

            // Colors (deep copy)
            WheelRpmColors = CloneArray(p.WheelRpmColors);
            WheelRpmBlinkColors = CloneArray(p.WheelRpmBlinkColors);
            WheelButtonColors = CloneArray(p.WheelButtonColors);
            WheelButtonDefaultDuringTelemetry = p.WheelButtonDefaultDuringTelemetry != null
                ? (bool[])p.WheelButtonDefaultDuringTelemetry.Clone() : null;
            WheelFlagColors = CloneArray(p.WheelFlagColors);
            WheelIdleColor = CloneArray(p.WheelIdleColor);
            WheelESRpmColors = CloneArray(p.WheelESRpmColors);
            WheelKnobBackgroundColors = CloneArray(p.WheelKnobBackgroundColors);
            WheelKnobPrimaryColors = CloneArray(p.WheelKnobPrimaryColors);
            WheelKnobRingColors = CloneArray(p.WheelKnobRingColors);
            WheelKnobRingBrightness = p.WheelKnobRingBrightness;
            DashRpmColors = CloneArray(p.DashRpmColors);
            DashRpmBlinkColors = CloneArray(p.DashRpmBlinkColors);
            DashFlagColors = CloneArray(p.DashFlagColors);

            Ab9 = p.Ab9?.Clone();

            TelemetryDashboardKey = p.TelemetryDashboardKey;

            // Wheel-page overlays (deep clone)
            WheelOverridesByPageGuid = new Dictionary<Guid, WheelOverride>();
            if (p.WheelOverridesByPageGuid != null)
            {
                foreach (var kvp in p.WheelOverridesByPageGuid)
                    if (kvp.Value != null) WheelOverridesByPageGuid[kvp.Key] = kvp.Value.Clone();
            }

            // Telemetry channel mappings (deep clone)
            TelemetryChannelMappings = new Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>();
            if (p.TelemetryChannelMappings != null)
            {
                foreach (var kvp in p.TelemetryChannelMappings)
                {
                    if (kvp.Value == null) continue;
                    var middle = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var dash in kvp.Value)
                    {
                        if (dash.Value == null) continue;
                        middle[dash.Key] = new Dictionary<string, string>(dash.Value, StringComparer.OrdinalIgnoreCase);
                    }
                    TelemetryChannelMappings[kvp.Key] = middle;
                }
            }

            // Base ambient
            BaseAmbientBrightness = p.BaseAmbientBrightness;
            BaseAmbientStandbyMode = p.BaseAmbientStandbyMode;
            BaseAmbientIndicatorState = p.BaseAmbientIndicatorState;
            BaseAmbientSleepMode = p.BaseAmbientSleepMode;
            BaseAmbientSleepTimeout = p.BaseAmbientSleepTimeout;
            BaseAmbientStartupColor = p.BaseAmbientStartupColor;
            BaseAmbientShutdownColor = p.BaseAmbientShutdownColor;

            // Gearshift
            GearshiftVibrateOnNeutral = p.GearshiftVibrateOnNeutral;
            GearshiftDebounceMs = p.GearshiftDebounceMs;
        }

        // ===== Capture current state =====

        /// <summary>
        /// Populate this profile by capturing all current device state.
        /// </summary>
        /// <param name="activeDashboardKey">
        /// Identity of the currently-loaded dashboard, in the same format
        /// <see cref="MozaPlugin.GetActiveDashboardKeyCandidates"/> returns.
        /// Pass <c>null</c> to leave <see cref="TelemetryDashboardKey"/> untouched
        /// (e.g. when the plugin can't resolve a key — early init, no profile loaded).
        /// </param>
        public void CaptureFromCurrent(MozaPluginSettings settings, MozaData data, string? activeDashboardKey = null)
        {
            if (!data.BaseSettingsRead) return;
            if (!string.IsNullOrEmpty(activeDashboardKey))
                TelemetryDashboardKey = activeDashboardKey;

            // Base/Motor
            Limit = data.Limit; FfbStrength = data.FfbStrength; Torque = data.Torque;
            Speed = data.Speed; Damper = data.Damper; Friction = data.Friction;
            Inertia = data.Inertia; Spring = data.Spring;
            SpeedDamping = data.SpeedDamping; SpeedDampingPoint = data.SpeedDampingPoint;
            NaturalInertia = data.NaturalInertia; SoftLimitStiffness = data.SoftLimitStiffness;
            SoftLimitRetain = data.SoftLimitRetain; FfbReverse = data.FfbReverse;
            Protection = data.Protection;

            // Game effects
            GameDamper = data.GameDamper; GameFriction = data.GameFriction;
            GameInertia = data.GameInertia; GameSpring = data.GameSpring;
            WorkMode = data.WorkMode;

            // FFB Equalizer
            Equalizer1 = data.Equalizer1; Equalizer2 = data.Equalizer2; Equalizer3 = data.Equalizer3;
            Equalizer4 = data.Equalizer4; Equalizer5 = data.Equalizer5; Equalizer6 = data.Equalizer6;

            // FFB Curve
            FfbCurveY1 = data.FfbCurveY1; FfbCurveY2 = data.FfbCurveY2; FfbCurveY3 = data.FfbCurveY3; FfbCurveY4 = data.FfbCurveY4; FfbCurveY5 = data.FfbCurveY5;

            // Handbrake
            HandbrakeMode = data.HandbrakeMode;
            HandbrakeButtonThreshold = data.HandbrakeButtonThreshold;
            HandbrakeDirection = data.HandbrakeDirection;
            HandbrakeMin = data.HandbrakeMin; HandbrakeMax = data.HandbrakeMax;
            HandbrakeCurve = (int[])data.HandbrakeCurve.Clone();

            // Pedals
            PedalsThrottleDir = data.PedalsThrottleDir; PedalsBrakeDir = data.PedalsBrakeDir; PedalsClutchDir = data.PedalsClutchDir;
            PedalsBrakeAngleRatio = data.PedalsBrakeAngleRatio;
            PedalsThrottleCurve = (int[])data.PedalsThrottleCurve.Clone();
            PedalsBrakeCurve = (int[])data.PedalsBrakeCurve.Clone();
            PedalsClutchCurve = (int[])data.PedalsClutchCurve.Clone();

            // NOTE: wheel-LED / ES-wheel / Dash / Base-ambient / Gearshift / AB9
            // fields are NOT captured here. They are written directly to the
            // profile (or wheel-page overlay) by their UI handlers — re-capturing
            // them from stale flat fields would corrupt the persisted state.
            // Wheel colors / blink colors / knob colors live in WheelOverride;
            // dash colors / dash blink live on the profile via the dash UI handler.
        }

        // ===== Color packing helpers =====

        public static int PackColor(byte[] rgb)
        {
            return (rgb[0] << 16) | (rgb[1] << 8) | rgb[2];
        }

        public static byte[] UnpackColor(int packed)
        {
            return new byte[]
            {
                (byte)((packed >> 16) & 0xFF),
                (byte)((packed >> 8) & 0xFF),
                (byte)(packed & 0xFF)
            };
        }

        public static int[] PackColors(byte[][] colors)
        {
            var packed = new int[colors.Length];
            for (int i = 0; i < colors.Length; i++)
                packed[i] = PackColor(colors[i]);
            return packed;
        }

        public static void UnpackColorsInto(int[]? packed, byte[][] target)
        {
            if (packed == null) return;
            int count = Math.Min(packed.Length, target.Length);
            for (int i = 0; i < count; i++)
            {
                var rgb = UnpackColor(packed[i]);
                target[i][0] = rgb[0];
                target[i][1] = rgb[1];
                target[i][2] = rgb[2];
            }
        }

        private static int[]? CloneArray(int[]? source)
        {
            return source != null ? (int[])source.Clone() : null;
        }
    }
}
