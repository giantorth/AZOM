using System;
using System.Windows.Controls;
using MozaPlugin.Devices;
using Newtonsoft.Json;
using SimHub.Plugins.ProfilesCommon;

namespace MozaPlugin
{
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
        public int WheelSleepMode { get; set; } = -1;
        public int WheelSleepTimeoutMin { get; set; } = -1;
        public int WheelSleepSpeedMs { get; set; } = -1;
        public int[]? WheelSleepColor { get; set; }
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
            WheelSleepMode = p.WheelSleepMode;
            WheelSleepTimeoutMin = p.WheelSleepTimeoutMin;
            WheelSleepSpeedMs = p.WheelSleepSpeedMs;
            WheelSleepColor = p.WheelSleepColor;
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

            // Wheel LED (from settings, since these use -1 sentinel)
            WheelTelemetryMode = settings.WheelTelemetryMode;
            WheelIdleEffect = settings.WheelIdleEffect;
            WheelButtonsIdleEffect = settings.WheelButtonsIdleEffect;
            WheelKnobIdleEffect = settings.WheelKnobIdleEffect;
            WheelKnobLedMode = settings.WheelKnobLedMode;
            WheelButtonsLedMode = settings.WheelButtonsLedMode;
            WheelTelemetryIdleSpeedMs = settings.WheelTelemetryIdleSpeedMs;
            WheelButtonsIdleSpeedMs = settings.WheelButtonsIdleSpeedMs;
            WheelKnobIdleSpeedMs = settings.WheelKnobIdleSpeedMs;
            WheelSleepMode = settings.WheelSleepMode;
            WheelSleepTimeoutMin = settings.WheelSleepTimeoutMin;
            WheelSleepSpeedMs = settings.WheelSleepSpeedMs;
            WheelSleepColor = settings.WheelSleepColor;
            WheelRpmBrightness = settings.WheelRpmBrightness;
            WheelButtonsBrightness = settings.WheelButtonsBrightness;
            WheelFlagsBrightness = settings.WheelFlagsBrightness;

            // ES wheel
            WheelRpmIndicatorMode = settings.WheelRpmIndicatorMode;
            WheelRpmDisplayMode = settings.WheelRpmDisplayMode;
            WheelESRpmBrightness = settings.WheelESRpmBrightness;

            // Dashboard
            DashRpmBrightness = settings.DashRpmBrightness;
            DashFlagsBrightness = settings.DashFlagsBrightness;
            DashDisplayBrightness = settings.DashDisplayBrightness;
            DashDisplayStandbyMin = settings.DashDisplayStandbyMin;

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

            // Colors
            WheelRpmColors = PackColors(data.WheelRpmColors);
            WheelRpmBlinkColors = PackColors(data.WheelRpmBlinkColors);
            WheelButtonColors = PackColors(data.WheelButtonColors);
            WheelButtonDefaultDuringTelemetry = (bool[])data.WheelButtonDefaultDuringTelemetry.Clone();
            WheelFlagColors = PackColors(data.WheelFlagColors);
            WheelIdleColor = new[] { PackColor(data.WheelIdleColor) };
            WheelESRpmColors = PackColors(data.WheelESRpmColors);
            WheelKnobBackgroundColors = PackColors(data.WheelKnobBackgroundColors);
            WheelKnobPrimaryColors = PackColors(data.WheelKnobPrimaryColors);
            WheelKnobRingColors = PackColors(data.KnobRingColors);
            WheelKnobRingBrightness = data.KnobRingBrightness;
            DashRpmColors = PackColors(data.DashRpmColors);
            DashRpmBlinkColors = PackColors(data.DashRpmBlinkColors);
            DashFlagColors = PackColors(data.DashFlagColors);
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
