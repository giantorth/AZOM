using System;
using System.Collections.Generic;
using System.Windows.Controls;
using MozaPlugin.Devices;
using Newtonsoft.Json;
using SimHub.Plugins.ProfilesCommon;
using MozaPlugin.Devices.MBooster;

namespace MozaPlugin.Settings
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
        // LED / mode
        public int WheelTelemetryMode { get; set; } = -1;
        public int WheelKnobLedMode { get; set; } = -1;
        public int WheelButtonsLedMode { get; set; } = -1;
        // NOTE: WheelSleep* (mode / timeout / speed / color) live on
        // MozaPluginSettings.WheelSleepByPageGuid — sleep is a firmware
        // preference, not a per-game-per-wheel decision. WheelIdleEffect /
        // WheelButtonsIdleEffect / WheelKnobIdleEffect and the matching *SpeedMs
        // fields live on MozaPluginSettings.WheelIdleByPageGuid for the same reason.

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
        // Per-knob signal modes (wheels reporting WheelKnobSignalModeSupported);
        // up to 5 knobs, -1 per slot = no override. Overlay-only like WheelKnobMode.
        public int[]? WheelKnobSignalModes { get; set; }

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
        // null = no override (fall through to baseline). Single wheel-wide toggle.
        public bool? WheelKnobDefaultDuringTelemetry { get; set; }
        // Knob static-hold restore timeout (ms). -1 = no override; 0 = off.
        public int WheelKnobStaticTimeoutMs { get; set; } = -1;

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
                WheelKnobLedMode = WheelKnobLedMode,
                WheelButtonsLedMode = WheelButtonsLedMode,
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
                WheelKnobSignalModes = WheelKnobSignalModes != null ? (int[])WheelKnobSignalModes.Clone() : null,
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
                WheelKnobDefaultDuringTelemetry = WheelKnobDefaultDuringTelemetry,
                WheelKnobStaticTimeoutMs = WheelKnobStaticTimeoutMs,
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
    /// <summary>
    /// One user assignment of a SimHub property to an FSR V1 dashboard field, with
    /// the source input range mapped onto the field's full-scale output capability.
    /// </summary>
    public sealed class Fsr1FieldMapping
    {
        /// <summary>SimHub property path (empty = unmapped → field sends 0).</summary>
        public string Property { get; set; } = "";
        /// <summary>Source value mapped to the field's minimum (0).</summary>
        public double InMin { get; set; } = 0;
        /// <summary>Source value mapped to the field's full-scale output.</summary>
        public double InMax { get; set; } = 1;

        // Field GEOMETRY (byte span / bit packing / endianness) is catalog-fixed —
        // only the channel and gain are user-assignable.
        /// <summary>Output gain: raw·Scale + Bias; null = 1.0. (CM1: per-field gain.)</summary>
        public double? Scale { get; set; }
        /// <summary>Output offset added after Scale; null = 0.0.</summary>
        public double? Bias { get; set; }

        public Fsr1FieldMapping Clone() =>
            new Fsr1FieldMapping
            {
                Property = Property, InMin = InMin, InMax = InMax,
                Scale = Scale, Bias = Bias,
            };
    }

    public sealed class Ab9Settings
    {
        public Ab9Mode Mode { get; set; } = Ab9Mode.SevenPlusR_L1;
        public Ab9InputMode InputMode { get; set; } = Ab9InputMode.Shifter;
        public byte MechanicalResistance { get; set; } = 50;
        public byte Spring { get; set; }              = 50;
        public byte NaturalDamping { get; set; }      = 50;
        public byte NaturalFriction { get; set; }     = 50;
        public byte MaxTorqueLimit { get; set; }      = 50;

        // Host-rendered engine vibration. Intensity (0..100 %) linearly scales
        // the 0x0B 0x02/03 engine-pulse-pair amp16 (full scale 0x2328); the
        // 0x0A 0x05 stream's slot ID stays at 0x1996 the whole time the
        // engine is "on" — see Ab9EngineVibrationWorker for why earlier
        // slot-toggle duty-cycle schemes produced an audible LF rumble.
        // Frequency is the literal target Hz (0..200) of the oscillator the
        // AB9 firmware runs from the streamed period field. Neither value is
        // pushed as a stored device setting — they modulate the SimHub
        // plugin's host-side stream generator only.
        public byte EngineVibrationIntensity { get; set; } = 0;
        public ushort EngineVibrationFrequency { get; set; } = 100;

        // Firmware-driven gear-shift vibration. One-shot 0x20/dev 0x12/cmd 0x0A 0x01
        // push when the user moves this slider; AB9 firmware then fires the rumble
        // pattern autonomously on every HID-detected gear engagement.
        public byte GearShiftVibrationIntensity { get; set; } = 0;

        // AB9-specific gear-shift event tuning. Independent of the wheelbase
        // GearshiftVibrateOnNeutral / GearshiftDebounceMs (which gate the
        // base-gearshift-event push), since the AB9 path fires per-shift
        // triggers (0x0D 0x01 + 0x04/0x06) through a different device and
        // users want to tune the two devices separately — e.g. heavier
        // debounce on the wheelbase to avoid double-kicks on H-pattern, but
        // tighter on the AB9 to feel every gate engagement.
        public bool GearShiftVibrateOnNeutral { get; set; } = false;
        public int GearShiftDebounceMs { get; set; } = 500;

        public Ab9Settings Clone()
        {
            return new Ab9Settings
            {
                Mode = Mode,
                InputMode = InputMode,
                MechanicalResistance = MechanicalResistance,
                Spring = Spring,
                NaturalDamping = NaturalDamping,
                NaturalFriction = NaturalFriction,
                MaxTorqueLimit = MaxTorqueLimit,
                EngineVibrationIntensity = EngineVibrationIntensity,
                EngineVibrationFrequency = EngineVibrationFrequency,
                GearShiftVibrationIntensity = GearShiftVibrationIntensity,
                GearShiftVibrateOnNeutral = GearShiftVibrateOnNeutral,
                GearShiftDebounceMs = GearShiftDebounceMs,
            };
        }
    }

    /// <summary>
    /// One wheelbase LFE channel (Engine / ABS / Gearshift). Each of the four
    /// parameters is dual-mode: a static slider value, OR an NCalc/property
    /// formula that overrides it (empty formula → slider). The formula is a bare
    /// SimHub property path or an NCalc/js: expression, evaluated live per tick
    /// via the same resolver the channel-mapper uses. The "trigger" gates the
    /// channel: non-zero = active (level, for Engine/ABS) or fires a burst on
    /// change (edge, for Gearshift). See BaseLfeEffectWorker.
    /// </summary>
    /// <summary>How a channel's trigger gates it.</summary>
    public enum BaseLfeTriggerMode
    {
        /// <summary>Active while the trigger is non-zero (continuous — engine/abs, and partials).</summary>
        Level = 0,
        /// <summary>Fire a burst whenever the trigger value changes (event — gearshift).</summary>
        OnChange = 1,
    }

    /// <summary>
    /// A slot's role. Selecting a mode applies a template (trigger + limits +
    /// character) via BaseLfeSettings.ApplyMode; Custom leaves everything as-is.
    /// The slot's wire effect id / render behavior is fixed (slots must stay on
    /// distinct ids so the base sums them) — the mode only presets the values.
    /// </summary>
    public enum BaseLfeMode
    {
        Engine = 0,
        Abs = 1,
        Gearshift = 2,
        Custom = 3,
    }

    public sealed class BaseLfeChannel
    {
        public bool Enabled { get; set; } = false;

        // Slot role (last-applied template); Custom = freely edited.
        public BaseLfeMode Mode { get; set; } = BaseLfeMode.Custom;

        // Level = continuous while trigger != 0; OnChange = burst on trigger change.
        public BaseLfeTriggerMode TriggerMode { get; set; } = BaseLfeTriggerMode.Level;

        // Trigger: pre-filled with the natural data source. Non-zero → active.
        public string TriggerFormula { get; set; } = "";

        // Frequency (Hz). Slider clamped to the UI range. When a FORMULA drives
        // frequency, its output is clamped to [FrequencyMin, FrequencyMax] (the
        // band the algorithm may operate in, 0..200 = the wire cap). The limits
        // are ignored for a plain slider value.
        public float Frequency { get; set; } = 50;
        public string FrequencyFormula { get; set; } = "";
        public float FrequencyMin { get; set; } = 0;
        public float FrequencyMax { get; set; } = 200;

        // Intensity (0..100 %).
        public int Intensity { get; set; } = 50;
        public string IntensityFormula { get; set; } = "";

        // Smoothness (0..100): 100 = steady amplitude, 0 = full-swing pulse.
        public int Smoothness { get; set; } = 100;
        public string SmoothnessFormula { get; set; } = "";

        // Edge-trigger refinements — only meaningful in OnChange mode: fire when
        // the trigger drops into neutral (≈0), and a minimum ms between bursts.
        public bool VibrateOnNeutral { get; set; } = false;
        public int DebounceMs { get; set; } = 50;

        /// <summary>Map a raw frequency-formula output (0..200 Hz wire domain) into
        /// this channel's [min,max] band — the value actually sent. Shared by the
        /// worker's render and the UI "current value" readout so they never drift.</summary>
        public double RescaleFreq(double rawEval)
        {
            double lo = System.Math.Min(FrequencyMin, FrequencyMax);
            double hi = System.Math.Max(FrequencyMin, FrequencyMax);
            double raw01 = rawEval / 200.0;
            if (raw01 < 0) raw01 = 0; else if (raw01 > 1) raw01 = 1;
            return lo + raw01 * (hi - lo);
        }

        public BaseLfeChannel Clone() => new BaseLfeChannel
        {
            Enabled = Enabled,
            Mode = Mode,
            TriggerMode = TriggerMode,
            TriggerFormula = TriggerFormula,
            Frequency = Frequency,
            FrequencyFormula = FrequencyFormula,
            FrequencyMin = FrequencyMin,
            FrequencyMax = FrequencyMax,
            Intensity = Intensity,
            IntensityFormula = IntensityFormula,
            Smoothness = Smoothness,
            SmoothnessFormula = SmoothnessFormula,
            VibrateOnNeutral = VibrateOnNeutral,
            DebounceMs = DebounceMs,
        };
    }

    /// <summary>
    /// Wheelbase "low-frequency effects" (LFE) — Engine, ABS, and complex
    /// Gearshift vibration, available on base firmware >= 1.2.10.10 (see
    /// MozaData.BaseSupportsLfe). HOST-RENDERED streams (cmd 0x2D/0x77, see
    /// MozaBaseLfeProtocol / BaseLfeEffectWorker) — no device round-trip.
    ///
    /// Each channel exposes trigger + frequency + intensity + smoothness, each a
    /// slider OR a formula. The default formulas reproduce the built-in behavior
    /// (engine tracks RPM, ABS pulses on ABSActive, gearshift fires on gear
    /// change) but are fully editable.
    /// </summary>
    public sealed class BaseLfeSettings
    {
        // Default trigger/frequency formulas — the "vary it" formula is exposed,
        // not hidden. Editable; empty falls back to the slider.
        public const string EngineTrigger = "[DataCorePlugin.GameData.Rpms]";
        public const string EngineFrequency = "[DataCorePlugin.GameData.Rpms] / [DataCorePlugin.GameData.MaxRpm] * 100";
        public const string AbsTrigger = "[DataCorePlugin.GameData.ABSActive]";
        public const string GearshiftTrigger = "[DataCorePlugin.GameData.Gear]";

        // RPM-scaled engine sweep (0..200 domain, rescaled into the slot's limits).
        public const string EngineFrequencySweep = "[DataCorePlugin.GameData.Rpms] / [DataCorePlugin.GameData.MaxRpm] * 200";

        // Slot 1/2/3 default to the Engine/ABS/Gearshift roles (their fixed wire ids).
        public BaseLfeChannel Engine { get; set; } = new BaseLfeChannel
        { Mode = BaseLfeMode.Engine, Frequency = 100, Intensity = 50, Smoothness = 100, TriggerFormula = EngineTrigger, FrequencyFormula = EngineFrequency };
        public BaseLfeChannel Abs { get; set; } = new BaseLfeChannel
        { Mode = BaseLfeMode.Abs, Frequency = 15, Intensity = 50, Smoothness = 40, TriggerFormula = AbsTrigger };
        public BaseLfeChannel Gearshift { get; set; } = new BaseLfeChannel
        { Mode = BaseLfeMode.Gearshift, Frequency = 40, Intensity = 100, Smoothness = 100, TriggerFormula = GearshiftTrigger, TriggerMode = BaseLfeTriggerMode.OnChange };

        /// <summary>
        /// Apply a slot role's template: trigger (formula + Level/OnChange),
        /// frequency band, and character defaults. Custom leaves the slot as-is.
        /// Enabled is preserved. The slot's wire id / render path is unchanged.
        /// </summary>
        public static void ApplyMode(BaseLfeChannel ch, BaseLfeMode mode)
        {
            if (ch == null) return;
            ch.Mode = mode;
            switch (mode)
            {
                case BaseLfeMode.Engine:
                    ch.TriggerFormula = EngineTrigger;
                    ch.TriggerMode = BaseLfeTriggerMode.Level;
                    ch.FrequencyFormula = EngineFrequencySweep;   // rescaled into the band below
                    ch.Frequency = 100; ch.FrequencyMin = 30; ch.FrequencyMax = 130;
                    ch.IntensityFormula = ""; ch.Intensity = 50;
                    ch.SmoothnessFormula = ""; ch.Smoothness = 100;
                    break;
                case BaseLfeMode.Abs:
                    ch.TriggerFormula = AbsTrigger;
                    ch.TriggerMode = BaseLfeTriggerMode.Level;
                    ch.FrequencyFormula = ""; ch.Frequency = 15; ch.FrequencyMin = 5; ch.FrequencyMax = 30;
                    ch.IntensityFormula = ""; ch.Intensity = 50;
                    ch.SmoothnessFormula = ""; ch.Smoothness = 40;   // amplitude pulse
                    break;
                case BaseLfeMode.Gearshift:
                    ch.TriggerFormula = GearshiftTrigger;
                    ch.TriggerMode = BaseLfeTriggerMode.OnChange;
                    ch.FrequencyFormula = ""; ch.Frequency = 40; ch.FrequencyMin = 20; ch.FrequencyMax = 100;
                    ch.IntensityFormula = ""; ch.Intensity = 100;
                    ch.SmoothnessFormula = ""; ch.Smoothness = 100;
                    ch.VibrateOnNeutral = false; ch.DebounceMs = 50;
                    break;
                case BaseLfeMode.Custom:
                    // Unrestricted: full frequency range (slider + formula band).
                    // Trigger / formulas / intensity / smoothness are left untouched.
                    ch.FrequencyMin = 0; ch.FrequencyMax = 200;
                    break;
            }
        }

        public BaseLfeSettings Clone()
        {
            return new BaseLfeSettings
            {
                Engine = Engine?.Clone() ?? new BaseLfeChannel(),
                Abs = Abs?.Clone() ?? new BaseLfeChannel(),
                Gearshift = Gearshift?.Clone() ?? new BaseLfeChannel(),
            };
        }

        // ── Built-in preset factories (the original preset buttons) ───────────
        // The base SUMS concurrent channels, so these drive all 3 slots as summed
        // partials gated on RPM, each scaling by % of RPM so they fit any vehicle.
        // Frequency formula spans the full 0..200 Hz domain; the partial's actual
        // range lives in the [0, freqK] limit band. This keeps the live rescale
        // identical to a bare "× freqK" (RescaleFreq(pct×200) over [0,freqK] = pct×freqK)
        // while making the band == the real range, so the Test button's band-sweep
        // matches the in-game frequency instead of overshooting to 200 Hz.
        private const string PctOfRpm = "[DataCorePlugin.GameData.Rpms] / [DataCorePlugin.GameData.MaxRpm] * 200";
        private const string Throttle = "[DataCorePlugin.GameData.Throttle]";
        private const string SpeedKmh = "[DataCorePlugin.GameData.SpeedKmh]";

        // Intensity that scales with engine load: floorPct % at closed throttle
        // (idle/coasting) up to 100% flat out. SimHub has no universal engine-load
        // channel, so throttle position is the proxy. baseInt is the full-load peak.
        private static string LoadIntensity(int baseInt, int floorPct)
            => baseInt + " * (" + floorPct + " + " + (100 - floorPct) + " * " + Throttle + " / 100.0) / 100.0";

        // Engine partial: pitch tracks % RPM (over the [0, freqK] band); intensity is
        // baseInt scaled by throttle/load. Base SUMS the 3 slots into one engine.
        private static BaseLfeChannel Partial(int freqK, int baseInt, int smoothness, int floorPct) => new BaseLfeChannel
        {
            Enabled = true,
            Mode = BaseLfeMode.Custom,
            TriggerMode = BaseLfeTriggerMode.Level,
            TriggerFormula = EngineTrigger,
            FrequencyFormula = PctOfRpm,
            FrequencyMin = 0,
            FrequencyMax = freqK,
            Frequency = freqK,
            IntensityFormula = LoadIntensity(baseInt, floorPct),
            Intensity = baseInt,
            Smoothness = smoothness,
        };
        // Road partial: fixed low pitch; intensity grows with speed (tyre/road texture).
        private static BaseLfeChannel RoadPartial(int freqHz, string speedScale, int smoothness) => new BaseLfeChannel
        {
            Enabled = true,
            Mode = BaseLfeMode.Custom,
            TriggerMode = BaseLfeTriggerMode.Level,
            TriggerFormula = SpeedKmh,
            Frequency = freqHz,
            IntensityFormula = SpeedKmh + " * " + speedScale,
            Intensity = 40,
            Smoothness = smoothness,
        };
        public static BaseLfeSettings AdditiveEngine() => new BaseLfeSettings { Engine = Partial(100, 30, 100, 35), Abs = Partial(200, 15, 100, 35), Gearshift = Partial(50, 15, 100, 35) };
        public static BaseLfeSettings BigRig() => new BaseLfeSettings { Engine = Partial(45, 40, 100, 50), Abs = Partial(20, 26, 55, 50), Gearshift = Partial(48, 18, 100, 50) };
        public static BaseLfeSettings DetunedV8() => new BaseLfeSettings { Engine = Partial(95, 26, 100, 40), Abs = Partial(102, 22, 100, 40), Gearshift = Partial(28, 18, 40, 40) };
        public static BaseLfeSettings RoadRumble() => new BaseLfeSettings { Engine = RoadPartial(30, "0.40", 100), Abs = RoadPartial(45, "0.30", 80), Gearshift = RoadPartial(22, "0.35", 60) };
    }

    /// <summary>A named snapshot of the three LFE slots. Built-ins are code-generated
    /// (BuiltIn=true, not persisted); user presets live in MozaPluginSettings.BaseLfePresets.</summary>
    public sealed class BaseLfePreset
    {
        public string Name { get; set; } = "";
        public bool BuiltIn { get; set; } = false;
        public BaseLfeSettings Settings { get; set; } = new BaseLfeSettings();
        public BaseLfePreset Clone() => new BaseLfePreset { Name = Name, BuiltIn = BuiltIn, Settings = Settings?.Clone() ?? new BaseLfeSettings() };
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
        public int Limit { get; set; } = -1;               // raw = degrees / 2, mechanical stop
        public int MaxAngle { get; set; } = -1;            // raw = degrees / 2, in-game full lock (<= Limit)
        public int FfbStrength { get; set; } = -1;          // raw = percent * 10
        public int Interpolation { get; set; } = -1;        // raw = display(0-10) * 10
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

        // Wheelbase gear-shift vibration intensity (cmd 0x2E base, 0..5).
        // Per-profile so each game gets its own feel. Distinct from the
        // plugin-side coalescing tuning below (GearshiftVibrateOnNeutral /
        // GearshiftDebounceMs) — this one controls the wheelbase's own rumble.
        public int GearshiftVibration { get; set; } = -1;

        // Performance output (cmd 0x1E base): 0 = Reserved, 1 = Full. Per-profile
        // so each game keeps its own bandwidth-mode preference.
        public int TempStrategy { get; set; } = -1;

        // ===== Wheel LED settings =====
        public int WheelTelemetryMode { get; set; } = -1;
        public int WheelKnobLedMode { get; set; } = -1;
        public int WheelButtonsLedMode { get; set; } = -1;
        // NOTE: WheelSleep* (mode / timeout / speed / color) live on
        // MozaPluginSettings.WheelSleepByPageGuid (sleep is a wheel-level, not
        // per-game, preference). WheelIdleEffect / WheelButtonsIdleEffect /
        // WheelKnobIdleEffect and the matching *SpeedMs fields live on
        // MozaPluginSettings.WheelIdleByPageGuid for the same reason.
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
        // VGS display-rotation mode (0=off, 1=smooth, 2=immediate). Sentinel -1 =
        // fall through to the plugin-settings baseline. Pushed via session-0x02
        // ff-record kind=5; VGS-only (see WheelModelInfo.SupportsDisplayRotation).
        public int DashDisplayRotation { get; set; } = -1;
        // Indicator/display modes (raw device-stored values, sentinel = leave alone).
        public int DashRpmIndicatorMode { get; set; } = -1;
        public int DashRpmDisplayMode { get; set; } = -1;
        public int DashFlagsIndicatorMode { get; set; } = -1;

        // ===== FFB Equalizer (bands 7-10 = 10-band firmware only) =====
        public int Equalizer1 { get; set; } = -1000;
        public int Equalizer2 { get; set; } = -1000;
        public int Equalizer3 { get; set; } = -1000;
        public int Equalizer4 { get; set; } = -1000;
        public int Equalizer5 { get; set; } = -1000;
        public int Equalizer6 { get; set; } = -1000;
        public int Equalizer7 { get; set; } = -1000;
        public int Equalizer8 { get; set; } = -1000;
        public int Equalizer9 { get; set; } = -1000;
        public int Equalizer10 { get; set; } = -1000;
        public int RoadSensitivity { get; set; } = -1;

        // ===== FFB Curve (X input positions of points 1-4 + Y outputs; point 5 fixed at input=100%) =====
        public int FfbCurveX1 { get; set; } = -1;
        public int FfbCurveX2 { get; set; } = -1;
        public int FfbCurveX3 { get; set; } = -1;
        public int FfbCurveX4 { get; set; } = -1;
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
        public int PedalsThrottleMin { get; set; } = -1;   // range start, 0-100
        public int PedalsThrottleMax { get; set; } = -1;   // range end, 0-100
        public int PedalsBrakeDir { get; set; } = -1;
        public int PedalsBrakeMin { get; set; } = -1;
        public int PedalsBrakeMax { get; set; } = -1;
        public int PedalsBrakeAngleRatio { get; set; } = -1; // 0=angle sensor, 100=load cell
        public int PedalsClutchDir { get; set; } = -1;
        public int PedalsClutchMin { get; set; } = -1;
        public int PedalsClutchMax { get; set; } = -1;
        public int[]? PedalsThrottleCurve { get; set; }          // [5] values 0-100
        public int[]? PedalsBrakeCurve { get; set; }             // [5] values 0-100
        public int[]? PedalsClutchCurve { get; set; }            // [5] values 0-100

        // ===== Shifter settings (HGP + SGP are independent devices). -1 = untouched. =====
        // shifter-type (apply-mode, grp 0x52 cmd 0x02) is deliberately NOT profiled:
        // it flips the device between H-pattern and sequential identity, and profile
        // apply re-asserting a captured value is how v1.5.1 flipped users' HGPs.
        // It is device-owned; the UI writes it directly on user action only.
        public int HgpDirection { get; set; } = -1;   // 0=Normal, 1=Reversed
        public int HgpPaddleSync { get; set; } = -1;  // 1/2
        public int HgpHidMode { get; set; } = -1;     // 0/1 game-compat mode
        public int SgpDirection { get; set; } = -1;
        public int SgpPaddleSync { get; set; } = -1;
        public int SgpHidMode { get; set; } = -1;
        public int SgpBrightness { get; set; } = -1;  // SGP LED brightness 0-10
        public int SgpLed1Index { get; set; } = -1;   // SGP LED S1 palette index 0-7
        public int SgpLed2Index { get; set; } = -1;   // SGP LED S2 palette index 0-7

        // ===== Color arrays (packed as R<<16 | G<<8 | B) =====
        public int[]? WheelRpmColors { get; set; }       // [10]
        public int[]? WheelRpmBlinkColors { get; set; }  // [10]
        public int[]? WheelButtonColors { get; set; }     // [16]
        public bool[]? WheelButtonDefaultDuringTelemetry { get; set; } // [16]
        public int[]? WheelFlagColors { get; set; }       // [6]
        public int[]? WheelIdleColor { get; set; }        // [1]
        public int[]? WheelESRpmColors { get; set; }     // [10]
        public int[]? WheelKnobBackgroundColors { get; set; } // [5] — W17/W18
        public int[]? WheelKnobPrimaryColors { get; set; }    // [5] — W17/W18
        public int[]? WheelKnobRingColors { get; set; }       // [56] — Group 3 per-LED ring
        public int WheelKnobRingBrightness { get; set; } = -1;
        // Single wheel-wide "restore stored knob colors when telemetry sends off" toggle.
        public bool WheelKnobDefaultDuringTelemetry { get; set; }
        // Knob static-hold restore timeout in ms (0 = off).
        public int WheelKnobStaticTimeoutMs { get; set; }
        public int[]? DashRpmColors { get; set; }         // [10]
        public int[]? DashRpmBlinkColors { get; set; }   // [10]
        public int[]? DashFlagColors { get; set; }        // [6]

        // ===== AB9 active shifter =====
        // Null until the user touches the AB9 panel for this profile — leaves
        // older serialized profiles untouched on load and avoids pushing default
        // values to a device that isn't attached.
        public Ab9Settings? Ab9 { get; set; }

        // ===== Wheelbase LFE effects (base firmware >= 1.2.10.10) =====
        // Null until the user touches the Wheelbase LFE panel for this profile —
        // leaves older serialized profiles untouched on load. Host-rendered; not
        // captured from device reads.
        public BaseLfeSettings? BaseLfe { get; set; }

        // Name of the LFE preset last selected for this profile (drives the preset
        // dropdown; "" = none/custom). Persisted so the dropdown remembers its pick.
        public string BaseLfePresetName { get; set; } = "";

        // ===== mBooster Pedals (per-device) =====
        // Per-device settings keyed by USB device instance ID (stable across
        // reconnects). One entry per physical mBooster the user has touched
        // settings for. Dict starts empty; mBooster detection populates it
        // lazily via the UI when the user first opens the device's tab.
        public Dictionary<string, MBoosterDeviceSettings> MBoosterSettings { get; set; }
            = new Dictionary<string, MBoosterDeviceSettings>(StringComparer.OrdinalIgnoreCase);

        // ===== Active dashboard for this game profile =====
        // Stable key in the same format ChannelMappingCoordinator.GetActiveDashboardKeyCandidates() emits:
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
        // Middle key = dashboard identity (see ChannelMappingCoordinator.GetActiveDashboardKeyCandidates)
        // Inner key  = channel URL (e.g. "v1/gameData/Rpm")
        // Value      = SimHub property path (empty = use Telemetry.json default)
        //
        // Profile-scoped so each game gets its own mapping set; wheel-scoped because
        // dashboards available on a VGS aren't available on a CS V2.1.
        public Dictionary<Guid, Dictionary<string, Dictionary<string, string>>> TelemetryChannelMappings { get; set; }
            = new Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>();

        // NOTE: the master channel mapper's defaults are deliberately NOT here. They
        // live on their own profile store (MozaChannelDefaultsStore), independent of
        // this one, so a user can pair one set of channel defaults with any device
        // profile without the two lists having to line up.

        // ===== FSR V1 group-0x42 dashboard field mappings =====
        // The FSR V1 display wheel renders fixed-schema records, not tier-def
        // channels, so its per-field assignments need their own store (each value
        // carries an input range for scaling — see Fsr1FieldMapping).
        // Outer key  = wheel page DescriptorUniqueId GUID
        // Middle key = record-type key (Fsr1DashboardCatalog.Key, e.g. "type-02")
        // Inner key  = field id (Fsr1FieldDef.FieldId, e.g. "rpmBar")
        // Absent entry = use the catalog default for that field.
        public Dictionary<Guid, Dictionary<string, Dictionary<string, Fsr1FieldMapping>>> Fsr1DashboardMappings { get; set; }
            = new Dictionary<Guid, Dictionary<string, Dictionary<string, Fsr1FieldMapping>>>();

        // CM1 base-bridged dash (group-0x35) field mappings. Flat — the CM1 streams one
        // keyed field set regardless of selected dashboard, so there is no per-dashboard
        // record-key level:
        //   Outer key = dash device GUID (MozaDeviceConstants.DashCm1Guid)
        //   Inner key = field id (Cm1FieldDef.FieldId, e.g. "f54d")
        // Reuses Fsr1FieldMapping (only Property is used; InMin/InMax unused for CM1).
        public Dictionary<Guid, Dictionary<string, Fsr1FieldMapping>> Cm1FieldMappings { get; set; }
            = new Dictionary<Guid, Dictionary<string, Fsr1FieldMapping>>();

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

        // Per-LED palettes, packed RGB, flat with a fixed stride of
        // BaseModelInfo.MaxLedsPerStrip (index = strip * stride + led) so an
        // entry keeps meaning across 6- and 9-LED bases. -1 = not set, and
        // entries past the real strip length stay unused.
        public int[]? BaseAmbientIdleColorsConstant { get; set; }
        public int[]? BaseAmbientIdleColorsBreath { get; set; }
        public int[]? BaseAmbientSleepColors { get; set; }

        // Animation interval per standby mode, indexed by mode 0..5 (only 2..5
        // have a register). -1 = not set.
        public int[]? BaseAmbientStandbyIntervals { get; set; }
        public int BaseAmbientSleepBreathInterval { get; set; } = -1;  // ms

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
            Limit = p.Limit; MaxAngle = p.MaxAngle;
            FfbStrength = p.FfbStrength; Torque = p.Torque;
            Interpolation = p.Interpolation;
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
            GearshiftVibration = p.GearshiftVibration;
            TempStrategy = p.TempStrategy;

            // Wheel LED
            WheelTelemetryMode = p.WheelTelemetryMode;
            WheelKnobLedMode = p.WheelKnobLedMode;
            WheelButtonsLedMode = p.WheelButtonsLedMode;
            // Idle effect/speed moved to per-wheel-page MozaPluginSettings.WheelIdleByPageGuid
            // in schema v9 — not copied per profile.
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
            DashDisplayRotation = p.DashDisplayRotation;
            DashRpmIndicatorMode = p.DashRpmIndicatorMode; DashRpmDisplayMode = p.DashRpmDisplayMode;
            DashFlagsIndicatorMode = p.DashFlagsIndicatorMode;

            // FFB Equalizer
            Equalizer1 = p.Equalizer1; Equalizer2 = p.Equalizer2; Equalizer3 = p.Equalizer3;
            Equalizer4 = p.Equalizer4; Equalizer5 = p.Equalizer5; Equalizer6 = p.Equalizer6;
            Equalizer7 = p.Equalizer7; Equalizer8 = p.Equalizer8; Equalizer9 = p.Equalizer9;
            Equalizer10 = p.Equalizer10;
            RoadSensitivity = p.RoadSensitivity;

            // FFB Curve
            FfbCurveX1 = p.FfbCurveX1; FfbCurveX2 = p.FfbCurveX2; FfbCurveX3 = p.FfbCurveX3; FfbCurveX4 = p.FfbCurveX4;
            FfbCurveY1 = p.FfbCurveY1; FfbCurveY2 = p.FfbCurveY2; FfbCurveY3 = p.FfbCurveY3; FfbCurveY4 = p.FfbCurveY4; FfbCurveY5 = p.FfbCurveY5;

            // Handbrake
            HandbrakeMode = p.HandbrakeMode;
            HandbrakeButtonThreshold = p.HandbrakeButtonThreshold;
            HandbrakeDirection = p.HandbrakeDirection;
            HandbrakeMin = p.HandbrakeMin; HandbrakeMax = p.HandbrakeMax;
            HandbrakeCurve = CloneArray(p.HandbrakeCurve);

            // Pedals
            PedalsThrottleDir = p.PedalsThrottleDir; PedalsBrakeDir = p.PedalsBrakeDir; PedalsClutchDir = p.PedalsClutchDir;
            PedalsThrottleMin = p.PedalsThrottleMin; PedalsThrottleMax = p.PedalsThrottleMax;
            PedalsBrakeMin = p.PedalsBrakeMin; PedalsBrakeMax = p.PedalsBrakeMax;
            PedalsClutchMin = p.PedalsClutchMin; PedalsClutchMax = p.PedalsClutchMax;
            PedalsBrakeAngleRatio = p.PedalsBrakeAngleRatio;
            PedalsThrottleCurve = CloneArray(p.PedalsThrottleCurve);
            PedalsBrakeCurve = CloneArray(p.PedalsBrakeCurve);
            PedalsClutchCurve = CloneArray(p.PedalsClutchCurve);

            // Shifter (HGP + SGP, independent)
            HgpDirection = p.HgpDirection; HgpPaddleSync = p.HgpPaddleSync;
            HgpHidMode = p.HgpHidMode;
            SgpDirection = p.SgpDirection; SgpPaddleSync = p.SgpPaddleSync;
            SgpHidMode = p.SgpHidMode;
            SgpBrightness = p.SgpBrightness;
            SgpLed1Index = p.SgpLed1Index; SgpLed2Index = p.SgpLed2Index;

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
            WheelKnobDefaultDuringTelemetry = p.WheelKnobDefaultDuringTelemetry;
            WheelKnobStaticTimeoutMs = p.WheelKnobStaticTimeoutMs;
            DashRpmColors = CloneArray(p.DashRpmColors);
            DashRpmBlinkColors = CloneArray(p.DashRpmBlinkColors);
            DashFlagColors = CloneArray(p.DashFlagColors);

            Ab9 = p.Ab9?.Clone();
            BaseLfe = p.BaseLfe?.Clone();
            BaseLfePresetName = p.BaseLfePresetName;

            // mBooster — deep-copy each per-device settings entry.
            MBoosterSettings = new Dictionary<string, MBoosterDeviceSettings>(StringComparer.OrdinalIgnoreCase);
            if (p.MBoosterSettings != null)
            {
                foreach (var kvp in p.MBoosterSettings)
                {
                    if (kvp.Value != null)
                        MBoosterSettings[kvp.Key] = kvp.Value.Clone();
                }
            }

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

            // FSR V1 dashboard field mappings (deep clone)
            Fsr1DashboardMappings = new Dictionary<Guid, Dictionary<string, Dictionary<string, Fsr1FieldMapping>>>();
            if (p.Fsr1DashboardMappings != null)
            {
                foreach (var kvp in p.Fsr1DashboardMappings)
                {
                    if (kvp.Value == null) continue;
                    var middle = new Dictionary<string, Dictionary<string, Fsr1FieldMapping>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rec in kvp.Value)
                    {
                        if (rec.Value == null) continue;
                        var inner = new Dictionary<string, Fsr1FieldMapping>(StringComparer.OrdinalIgnoreCase);
                        foreach (var fld in rec.Value)
                            if (fld.Value != null) inner[fld.Key] = fld.Value.Clone();
                        middle[rec.Key] = inner;
                    }
                    Fsr1DashboardMappings[kvp.Key] = middle;
                }
            }

            // CM1 dash field mappings (deep clone)
            Cm1FieldMappings = new Dictionary<Guid, Dictionary<string, Fsr1FieldMapping>>();
            if (p.Cm1FieldMappings != null)
            {
                foreach (var kvp in p.Cm1FieldMappings)
                {
                    if (kvp.Value == null) continue;
                    var inner = new Dictionary<string, Fsr1FieldMapping>(StringComparer.OrdinalIgnoreCase);
                    foreach (var fld in kvp.Value)
                        if (fld.Value != null) inner[fld.Key] = fld.Value.Clone();
                    Cm1FieldMappings[kvp.Key] = inner;
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
            BaseAmbientIdleColorsConstant = CloneArray(p.BaseAmbientIdleColorsConstant);
            BaseAmbientIdleColorsBreath = CloneArray(p.BaseAmbientIdleColorsBreath);
            BaseAmbientSleepColors = CloneArray(p.BaseAmbientSleepColors);
            BaseAmbientStandbyIntervals = CloneArray(p.BaseAmbientStandbyIntervals);
            BaseAmbientSleepBreathInterval = p.BaseAmbientSleepBreathInterval;

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
        /// <see cref="Telemetry.ChannelMappingCoordinator.GetActiveDashboardKeyCandidates"/> returns.
        /// Pass <c>null</c> to leave <see cref="TelemetryDashboardKey"/> untouched
        /// (e.g. when the plugin can't resolve a key — early init, no profile loaded).
        /// </param>
        public void CaptureFromCurrent(MozaPluginSettings settings, MozaData data, string? activeDashboardKey = null)
        {
            if (!string.IsNullOrEmpty(activeDashboardKey))
                TelemetryDashboardKey = activeDashboardKey;

            // Base/Motor + game effects. These carry the -1 sentinel until the base
            // reports them, so capturing an unread field stores -1 and every
            // downstream write skips it (HardwareApplier's Apply helper). Ungated
            // on purpose: a value the user just set in the UI must reach the
            // profile even on a base that never answered its read sweep.
            Limit = data.Limit; MaxAngle = data.MaxAngle;
            FfbStrength = data.FfbStrength; Torque = data.Torque;
            Interpolation = data.Interpolation;
            Speed = data.Speed; Damper = data.Damper; Friction = data.Friction;
            Inertia = data.Inertia; Spring = data.Spring;
            SpeedDamping = data.SpeedDamping; SpeedDampingPoint = data.SpeedDampingPoint;
            NaturalInertia = data.NaturalInertia; SoftLimitStiffness = data.SoftLimitStiffness;
            SoftLimitRetain = data.SoftLimitRetain; FfbReverse = data.FfbReverse;
            Protection = data.Protection;

            GameDamper = data.GameDamper; GameFriction = data.GameFriction;
            GameInertia = data.GameInertia; GameSpring = data.GameSpring;
            WorkMode = data.WorkMode;
            GearshiftVibration = data.GearshiftVibration;
            TempStrategy = data.TempStrategy;
            RoadSensitivity = data.RoadSensitivity;

            // EQ and the FFB curve default to real neutral values (flat / linear),
            // not a sentinel, so they still need the read gate: capturing them
            // unread would overwrite the base's own curve on the next apply.
            if (data.BaseSettingsRead)
            {
                // Bands 7-10 only on 10-band firmware — an old-fw session must not
                // bake the untouched _data defaults over the sentinel.
                Equalizer1 = data.Equalizer1; Equalizer2 = data.Equalizer2; Equalizer3 = data.Equalizer3;
                Equalizer4 = data.Equalizer4; Equalizer5 = data.Equalizer5; Equalizer6 = data.Equalizer6;
                if (data.BaseSupportsEq10)
                {
                    Equalizer7 = data.Equalizer7; Equalizer8 = data.Equalizer8;
                    Equalizer9 = data.Equalizer9; Equalizer10 = data.Equalizer10;
                }

                FfbCurveX1 = data.FfbCurveX1; FfbCurveX2 = data.FfbCurveX2; FfbCurveX3 = data.FfbCurveX3; FfbCurveX4 = data.FfbCurveX4;
                FfbCurveY1 = data.FfbCurveY1; FfbCurveY2 = data.FfbCurveY2; FfbCurveY3 = data.FfbCurveY3; FfbCurveY4 = data.FfbCurveY4; FfbCurveY5 = data.FfbCurveY5;
            }

            // Handbrake — only once the device has reported its calibration, so a
            // pre-read default (e.g. min/max 0) isn't baked into the profile.
            if (data.HandbrakeSettingsRead)
            {
                HandbrakeMode = data.HandbrakeMode;
                HandbrakeButtonThreshold = data.HandbrakeButtonThreshold;
                HandbrakeDirection = data.HandbrakeDirection;
                HandbrakeMin = data.HandbrakeMin; HandbrakeMax = data.HandbrakeMax;
                HandbrakeCurve = (int[])data.HandbrakeCurve.Clone();
            }

            // Pedals — gated the same way (see handbrake note above).
            if (data.PedalsSettingsRead)
            {
                PedalsThrottleDir = data.PedalsThrottleDir; PedalsBrakeDir = data.PedalsBrakeDir; PedalsClutchDir = data.PedalsClutchDir;
                PedalsThrottleMin = data.PedalsThrottleMin; PedalsThrottleMax = data.PedalsThrottleMax;
                PedalsBrakeMin = data.PedalsBrakeMin; PedalsBrakeMax = data.PedalsBrakeMax;
                PedalsClutchMin = data.PedalsClutchMin; PedalsClutchMax = data.PedalsClutchMax;
                PedalsBrakeAngleRatio = data.PedalsBrakeAngleRatio;
                PedalsThrottleCurve = (int[])data.PedalsThrottleCurve.Clone();
                PedalsBrakeCurve = (int[])data.PedalsBrakeCurve.Clone();
                PedalsClutchCurve = (int[])data.PedalsClutchCurve.Clone();
            }

            // Shifter (HGP + SGP, independent). Device-read fields, like handbrake/pedals
            // above — only read on detect (no telemetry drift), so capturing _data is safe.
            // shifter-type (ApplyMode) is device identity, never captured (see field notes).
            HgpDirection = data.ShifterHgp.Direction; HgpPaddleSync = data.ShifterHgp.PaddleSync;
            HgpHidMode = data.ShifterHgp.HidMode;
            SgpDirection = data.ShifterSgp.Direction; SgpPaddleSync = data.ShifterSgp.PaddleSync;
            SgpHidMode = data.ShifterSgp.HidMode;
            SgpBrightness = data.ShifterSgp.Brightness;
            SgpLed1Index = data.ShifterSgp.Led1Index; SgpLed2Index = data.ShifterSgp.Led2Index;

            // NOTE: wheel-LED / ES-wheel / Dash / Base-ambient / Gearshift / AB9
            // fields are NOT captured here. They are written directly to the
            // profile (or wheel-page overlay) by their UI handlers — re-capturing
            // them from stale flat fields would corrupt the persisted state.
            // Wheel colors / blink colors / knob colors live in WheelOverride;
            // dash colors / dash blink live on the profile via the dash UI handler.
        }

        /// <summary>
        /// Seed this profile's dash / base-ambient / gearshift baselines from the
        /// global <see cref="MozaPluginSettings"/> flat defaults. Sentinel-only
        /// (writes only where the baseline is still at its -1 / null "not set"
        /// marker), so it's idempotent and never overwrites a user value.
        ///
        /// Runs at profile creation (ProfileCoordinator.InitProfileSystem) and on
        /// every dash apply (HardwareApplier.ApplyDashToHardware)
        /// — SimHub auto-creates per-game profiles with all-sentinel fields, and
        /// without seeding the >=0 guards downstream skip every write, leaving the
        /// display dark.
        /// </summary>
        public void SeedBaselineFromFlatFields(MozaPluginSettings settings)
        {
            if (settings == null) return;

            // Dash brightness baselines.
            if (DashRpmBrightness     < 0) DashRpmBrightness     = settings.DashRpmBrightness;
            if (DashFlagsBrightness   < 0) DashFlagsBrightness   = settings.DashFlagsBrightness;
            if (DashDisplayBrightness < 0) DashDisplayBrightness = settings.DashDisplayBrightness;
            if (DashDisplayStandbyMin < 0) DashDisplayStandbyMin = settings.DashDisplayStandbyMin;
            if (DashDisplayRotation   < 0) DashDisplayRotation   = settings.DashDisplayRotation;
            if (DashRpmBlinkColors == null && settings.DashRpmBlinkColors != null)
                DashRpmBlinkColors = (int[])settings.DashRpmBlinkColors.Clone();

            // Base ambient.
            if (BaseAmbientBrightness     < 0) BaseAmbientBrightness     = settings.BaseAmbientBrightness;
            if (BaseAmbientStandbyMode    < 0) BaseAmbientStandbyMode    = settings.BaseAmbientStandbyMode;
            if (BaseAmbientIndicatorState < 0) BaseAmbientIndicatorState = settings.BaseAmbientIndicatorState;
            if (BaseAmbientSleepMode      < 0) BaseAmbientSleepMode      = settings.BaseAmbientSleepMode;
            if (BaseAmbientSleepTimeout   < 0) BaseAmbientSleepTimeout   = settings.BaseAmbientSleepTimeout;
            if (BaseAmbientStartupColor   < 0) BaseAmbientStartupColor   = settings.BaseAmbientStartupColor;
            if (BaseAmbientShutdownColor  < 0) BaseAmbientShutdownColor  = settings.BaseAmbientShutdownColor;

            // Gearshift.
            if (GearshiftVibrateOnNeutral < 0) GearshiftVibrateOnNeutral = settings.GearshiftVibrateOnNeutral ? 1 : 0;
            if (GearshiftDebounceMs       < 0) GearshiftDebounceMs       = settings.GearshiftDebounceMs;
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
