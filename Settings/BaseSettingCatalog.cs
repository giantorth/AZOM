using System;
using MozaPlugin.Resources;

namespace MozaPlugin.Settings
{
    /// <summary>
    /// Declarative table of every wheelbase setting reachable from a SimHub
    /// button binding, consumed by <see cref="SimHubRegistrar"/> to generate the
    /// <c>AZOM.*</c> property delegates and step/toggle actions. One row per
    /// setting instead of one hand-written Step method each.
    ///
    /// Scales and ranges mirror the Base-tab slider handlers in
    /// <c>UI/SettingsControl.xaml.cs</c> exactly — display units are what the
    /// slider shows, raw is what goes on the wire. Keep the two in sync: a
    /// mismatch writes a plausible-looking wrong value to the parameter store.
    ///
    /// Every command here is a group 0x28/0x29 (or main group 0x1F) parameter
    /// slot and hits base flash on write, so callers must not stream these —
    /// see the rail guard in <see cref="SimHubRegistrar"/>.
    /// </summary>
    internal static class BaseSettingCatalog
    {
        /// <summary>One steppable numeric wheelbase setting.</summary>
        internal sealed class NumericSetting
        {
            public string Name = "";                     // AZOM.<Name>
            public Func<MozaData, int> GetRaw = _ => 0;
            public Action<MozaData, int> SetRaw = (_, __) => { };
            public string[] Commands = Array.Empty<string>(); // order is load-bearing (Rotation)
            public int Min;
            public int Max;
            public int Fine;
            public int Coarse;
            public Func<int, int> ToDisplay = v => v;
            public Func<int, int> ToRaw = v => v;
            /// <summary>Live max override (EQ band mode, curve neighbours). Null = use <see cref="Max"/>.</summary>
            public Func<MozaData, int>? MaxFor;
            /// <summary>Live min override (curve neighbours). Null = use <see cref="Min"/>.</summary>
            public Func<MozaData, int>? MinFor;
            /// <summary>Firmware capability gate. Null = always available.</summary>
            public Func<MozaData, bool>? Supported;
            /// <summary>Persistence outside the profile capture (raw value). Null = profile only.</summary>
            public Action<MozaPlugin, int>? Commit;
            /// <summary>Localized label, as the Base tab shows it.</summary>
            public Func<string> Label = () => "";
            /// <summary>Display-unit suffix for the value ("%", "°", " kph", "").</summary>
            public string Unit = "";

            public int EffectiveMin(MozaData d) => MinFor?.Invoke(d) ?? Min;
            public int EffectiveMax(MozaData d) => MaxFor?.Invoke(d) ?? Max;
            public bool IsSupported(MozaData d) => Supported?.Invoke(d) ?? true;
        }

        /// <summary>One on/off wheelbase setting.</summary>
        internal sealed class ToggleSetting
        {
            public string Name = "";                     // AZOM.<Name>{On,Off,Toggle}
            public Func<MozaData, int> Get = _ => 0;
            public Action<MozaData, int> Set = (_, __) => { };
            public string Command = "";
            public int OnValue = 1;
            public int OffValue;
            /// <summary>Localized label, as the Base tab shows it.</summary>
            public Func<string> Label = () => "";
            /// <summary>True when the current raw value counts as "on".</summary>
            public bool IsOn(MozaData d) => Get(d) == OnValue;
        }

        // Percent settings stored as percent x 10 (base-ffb-strength, base-damper, ...).
        private static int FromTenths(int raw) => (int)Math.Round(raw / 10.0);
        private static int ToTenths(int display) => display * 10;

        // Game effect gains: 0-100 % stored as 0-255.
        private static int From255(int raw) => (int)Math.Round(raw / 2.55);
        private static int To255(int display) => (int)Math.Round(display * 2.55);

        // Soft-limit stiffness: display 1-10 <-> raw 100-500 (affine, cf.
        // SoftLimitStiffnessSlider_ValueChanged / RefreshBaseTab).
        private const double SoftLimitStep = 400.0 / 9.0;
        private static int FromSoftLimit(int raw) => (int)Math.Round((raw / SoftLimitStep) - 2.25 + 1.0);
        private static int ToSoftLimit(int display) => (int)Math.Round(display * SoftLimitStep - SoftLimitStep + 100.0);

        // ===== Road sensitivity + EQ presets ==============================
        // Shared with UI/SettingsControl.xaml.cs so the button macro and the
        // AZOM step actions drive identical values.
        //
        // ORDER IS LOAD-BEARING: static field initializers run in textual
        // order, and the Numeric table below calls EqBand(), which indexes
        // EqRegisterCommands. Declaring these after Numeric leaves them null
        // during its initializer — a NullReferenceException inside the static
        // constructor, surfacing as a TypeInitializationException that kills
        // plugin Init (and, via SettingsControl.EqCommands, the settings pane).
        // Keep every static array Numeric depends on above it.

        /// <summary>EQ write commands in register order (band 1..10).</summary>
        internal static readonly string[] EqRegisterCommands =
        {
            "base-equalizer1", "base-equalizer2", "base-equalizer3",
            "base-equalizer4", "base-equalizer5", "base-equalizer6",
            "base-equalizer7", "base-equalizer8", "base-equalizer9",
            "base-equalizer10"
        };

        /// <summary>
        /// EQ registers in FREQUENCY order (5/10/15/25/30/40/50/60/80/100 Hz) —
        /// the 10-band registers interleave. Preset rows are in this order.
        /// </summary>
        internal static readonly string[] Eq10FreqOrderCommands =
        {
            "base-equalizer1", "base-equalizer7", "base-equalizer2",
            "base-equalizer3", "base-equalizer8", "base-equalizer4",
            "base-equalizer9", "base-equalizer5", "base-equalizer10",
            "base-equalizer6"
        };

        /// <summary>Register index (0-based) for each frequency column, in frequency order.</summary>
        internal static readonly int[] Eq10FreqOrderRegisters = { 0, 6, 1, 2, 7, 3, 8, 4, 9, 5 };

        /// <summary>Band frequency in Hz for each register (band 1..10, 0-based).</summary>
        internal static readonly int[] EqRegisterFrequencies = { 5, 15, 25, 40, 60, 100, 10, 30, 50, 80 };

        /// <summary>
        /// Frequency-order columns carried by the legacy registers Eq1..Eq6
        /// (5/15/25/40/60/100 Hz).
        /// </summary>
        internal static readonly int[] Eq6FreqColumns = { 0, 2, 3, 5, 7, 9 };

        /// <summary>
        /// PitHouse "sensitivity" presets 0..10 — one-shot macros writing
        /// road-sensitivity (0x0C = 10 + 4*N) plus a canned EQ curve; no
        /// dedicated sensitivity register exists, so the buttons are momentary.
        /// Values in frequency order 5/10/15/25/30/40/50/60/80/100 Hz. On
        /// legacy firmware only the six old registers are written (columns via
        /// <see cref="Eq6FreqColumns"/>) — the four new bands are skipped.
        /// </summary>
        internal static readonly int[][] EqSensitivityPresets =
        {
            new[] { 100, 100,  30,  10,   0,   0,   0,   0,   0,   0 },
            new[] { 100, 100,  60,  20,  10,   0,   0,   0,   0,   0 },
            new[] { 100, 100,  70,  40,  30,  10,   0,   0,   0,   0 },
            new[] { 100, 100,  80,  50,  40,  20,  10,  10,   0,   0 },
            new[] { 100, 100,  90,  60,  50,  30,  20,  20,  10,   0 },
            new[] { 100, 100, 100,  70,  60,  40,  30,  30,  10,   0 },
            new[] { 100, 100, 100,  90,  80,  50,  40,  40,  20,   0 },
            new[] { 100, 100, 100, 100,  90,  60,  60,  60,  40,   0 },
            new[] { 100, 100, 100, 100,  90,  80,  80,  80,  60,   0 },
            new[] { 100, 100, 100, 100, 100, 100, 100, 100,  80,   0 },
            new[] { 100, 100, 100, 100, 100, 100, 100, 100, 100, 100 },
        };

        /// <summary>
        /// Steppable settings. Each row generates one <c>AZOM.&lt;Name&gt;</c>
        /// property (display units) plus <c>Up</c>/<c>Down</c>/<c>UpCoarse</c>/
        /// <c>DownCoarse</c> actions.
        /// </summary>
        internal static readonly NumericSetting[] Numeric =
        {
            // ── Base / motor ────────────────────────────────────────────────
            new NumericSetting {
                Name = "FfbStrength", Commands = new[] { "base-ffb-strength" },
                GetRaw = d => d.FfbStrength, SetRaw = (d, v) => d.FfbStrength = v,
                Min = 0, Max = 200, Fine = 5, Coarse = 10,   // PitHouse goes to 200 % (raw 2000)
                ToDisplay = FromTenths, ToRaw = ToTenths,
                Label = () => Strings.SliderLabel_GameFfbStrength, Unit = "%" },

            new NumericSetting {
                Name = "Torque", Commands = new[] { "base-torque" },
                GetRaw = d => d.Torque, SetRaw = (d, v) => d.Torque = v,
                Min = 50, Max = 100, Fine = 5, Coarse = 10,
                Label = () => Strings.SliderLabel_BaseTorqueOutput, Unit = "%" },

            // Rotation writes both slots; base-limit must precede base-max-angle.
            new NumericSetting {
                Name = "Rotation", Commands = new[] { "base-limit", "base-max-angle" },
                GetRaw = d => d.Limit, SetRaw = (d, v) => { d.Limit = v; d.MaxAngle = v; },
                Min = 60, Max = 2700, Fine = 90, Coarse = 180,
                ToDisplay = raw => raw * 2, ToRaw = deg => deg / 2,
                Label = () => Strings.SliderLabel_WheelRotationAngle, Unit = "°" },

            new NumericSetting {
                Name = "WheelSpeedLimit", Commands = new[] { "base-speed" },
                GetRaw = d => d.Speed, SetRaw = (d, v) => d.Speed = v,
                Min = 0, Max = 200, Fine = 5, Coarse = 10,
                ToDisplay = FromTenths, ToRaw = ToTenths,
                Label = () => Strings.SliderLabel_MaximumWheelSpeed, Unit = "%" },

            new NumericSetting {
                Name = "Interpolation", Commands = new[] { "main-set-interpolation" },
                GetRaw = d => d.Interpolation, SetRaw = (d, v) => d.Interpolation = v,
                Min = 0, Max = 10, Fine = 1, Coarse = 2,
                ToDisplay = FromTenths, ToRaw = ToTenths,
                Label = () => Strings.SliderLabel_Interpolation },

            new NumericSetting {
                Name = "GearshiftVibration", Commands = new[] { "base-gearshift-vibration" },
                GetRaw = d => d.GearshiftVibration, SetRaw = (d, v) => d.GearshiftVibration = v,
                Min = 0, Max = 5, Fine = 1, Coarse = 2,
                Label = () => Strings.SliderLabel_ShiftIntensity },

            // ── Wheelbase effects ───────────────────────────────────────────
            new NumericSetting {
                Name = "Damper", Commands = new[] { "base-damper" },
                GetRaw = d => d.Damper, SetRaw = (d, v) => d.Damper = v,
                Min = 0, Max = 100, Fine = 5, Coarse = 10,
                ToDisplay = FromTenths, ToRaw = ToTenths,
                Label = () => Strings.SliderLabel_WheelDamper, Unit = "%" },

            new NumericSetting {
                Name = "Friction", Commands = new[] { "base-friction" },
                GetRaw = d => d.Friction, SetRaw = (d, v) => d.Friction = v,
                Min = 0, Max = 100, Fine = 5, Coarse = 10,
                ToDisplay = FromTenths, ToRaw = ToTenths,
                Label = () => Strings.SliderLabel_WheelFriction, Unit = "%" },

            // "Natural Inertia" on the Wheelbase Effects card (cmd 0x04) — not
            // to be confused with NaturalInertia below (cmd 0x13).
            new NumericSetting {
                Name = "Inertia", Commands = new[] { "base-inertia" },
                GetRaw = d => d.Inertia, SetRaw = (d, v) => d.Inertia = v,
                Min = 100, Max = 500, Fine = 10, Coarse = 50,
                ToDisplay = FromTenths, ToRaw = ToTenths,
                Label = () => Strings.SliderLabel_NaturalInertia },

            // "Wheel Spring" — the base's own mechanical centering spring.
            new NumericSetting {
                Name = "Spring", Commands = new[] { "base-spring" },
                GetRaw = d => d.Spring, SetRaw = (d, v) => d.Spring = v,
                Min = 0, Max = 100, Fine = 5, Coarse = 10,
                ToDisplay = FromTenths, ToRaw = ToTenths,
                Label = () => Strings.SliderLabel_WheelSpring, Unit = "%" },

            // ── Game effect gains (DirectInput effect scaling, main group) ───
            new NumericSetting {
                Name = "GameDamper", Commands = new[] { "main-set-damper-gain" },
                GetRaw = d => d.GameDamper, SetRaw = (d, v) => d.GameDamper = v,
                Min = 0, Max = 100, Fine = 5, Coarse = 10,
                ToDisplay = From255, ToRaw = To255,
                Label = () => Strings.SliderLabel_GameDamper, Unit = "%" },

            new NumericSetting {
                Name = "GameFriction", Commands = new[] { "main-set-friction-gain" },
                GetRaw = d => d.GameFriction, SetRaw = (d, v) => d.GameFriction = v,
                Min = 0, Max = 100, Fine = 5, Coarse = 10,
                ToDisplay = From255, ToRaw = To255,
                Label = () => Strings.SliderLabel_GameFriction, Unit = "%" },

            new NumericSetting {
                Name = "GameInertia", Commands = new[] { "main-set-inertia-gain" },
                GetRaw = d => d.GameInertia, SetRaw = (d, v) => d.GameInertia = v,
                Min = 0, Max = 100, Fine = 5, Coarse = 10,
                ToDisplay = From255, ToRaw = To255,
                Label = () => Strings.SliderLabel_GameInertia, Unit = "%" },

            // "Game Spring" — gain on the game's own spring effect. Distinct
            // from Spring above; both are centering-ish, neither supersedes it.
            new NumericSetting {
                Name = "GameSpring", Commands = new[] { "main-set-spring-gain" },
                GetRaw = d => d.GameSpring, SetRaw = (d, v) => d.GameSpring = v,
                Min = 0, Max = 100, Fine = 5, Coarse = 10,
                ToDisplay = From255, ToRaw = To255,
                Label = () => Strings.SliderLabel_GameSpring, Unit = "%" },

            // ── Protection / soft limit / high-speed damping ────────────────
            // "Steering Wheel Inertia" on the Protection card (cmd 0x13).
            new NumericSetting {
                Name = "NaturalInertia", Commands = new[] { "base-natural-inertia" },
                GetRaw = d => d.NaturalInertia, SetRaw = (d, v) => d.NaturalInertia = v,
                Min = 100, Max = 4000, Fine = 50, Coarse = 200,
                Commit = (p, v) => p.SetActiveWheelNaturalInertia(v),
                Label = () => Strings.SliderLabel_SteeringWheelInertia },

            new NumericSetting {
                Name = "SoftLimitStiffness", Commands = new[] { "base-soft-limit-stiffness" },
                GetRaw = d => d.SoftLimitStiffness, SetRaw = (d, v) => d.SoftLimitStiffness = v,
                Min = 1, Max = 10, Fine = 1, Coarse = 2,
                ToDisplay = FromSoftLimit, ToRaw = ToSoftLimit,
                Label = () => Strings.SliderLabel_Stiffness },

            // % of max. PitHouse only offers Soft 50 / Middle 75 / Hard 100,
            // so steps of 25 land on those three.
            new NumericSetting {
                Name = "SoftLimitStrength", Commands = new[] { "base-soft-limit-strength" },
                GetRaw = d => d.SoftLimitStrength, SetRaw = (d, v) => d.SoftLimitStrength = v,
                Min = 50, Max = 100, Fine = 25, Coarse = 25,
                Label = () => Strings.SliderLabel_SoftLimitStrength, Unit = "%" },

            new NumericSetting {
                Name = "SpeedDamping", Commands = new[] { "base-speed-damping" },
                GetRaw = d => d.SpeedDamping, SetRaw = (d, v) => d.SpeedDamping = v,
                Min = 0, Max = 100, Fine = 5, Coarse = 10,
                Label = () => Strings.SliderLabel_DampingLevel, Unit = "%" },

            new NumericSetting {
                Name = "SpeedDampingPoint", Commands = new[] { "base-speed-damping-point" },
                GetRaw = d => d.SpeedDampingPoint, SetRaw = (d, v) => d.SpeedDampingPoint = v,
                Min = 0, Max = 400, Fine = 10, Coarse = 50,
                Label = () => Strings.SliderLabel_TriggerSpeed, Unit = " kph" },

            // ── FFB equalizer (register order; see EqRegisterFrequencies) ────
            // Legacy firmware caps every band at 400 %; 10-band firmware raises
            // 1-5 to 500 % and drops band 6 (100 Hz) to 100 % — cf. ApplyEqBandMode.
            EqBand(1, d => d.Equalizer1, (d, v) => d.Equalizer1 = v),
            EqBand(2, d => d.Equalizer2, (d, v) => d.Equalizer2 = v),
            EqBand(3, d => d.Equalizer3, (d, v) => d.Equalizer3 = v),
            EqBand(4, d => d.Equalizer4, (d, v) => d.Equalizer4 = v),
            EqBand(5, d => d.Equalizer5, (d, v) => d.Equalizer5 = v),
            EqBand(6, d => d.Equalizer6, (d, v) => d.Equalizer6 = v),
            EqBand(7, d => d.Equalizer7, (d, v) => d.Equalizer7 = v),
            EqBand(8, d => d.Equalizer8, (d, v) => d.Equalizer8 = v),
            EqBand(9, d => d.Equalizer9, (d, v) => d.Equalizer9 = v),
            EqBand(10, d => d.Equalizer10, (d, v) => d.Equalizer10 = v),

            // ── FFB output curve ────────────────────────────────────────────
            // The base has x1..x4 but no x5 and does not persist the X
            // breakpoints; the profile-apply path therefore rides all nine
            // together. These per-node actions match what the UI curve sliders
            // already do (one command per dragged node).
            // Points 1-4 move in X and Y; point 5 sits at input 100 % and only
            // moves in Y. X stays 1 unit clear of its neighbours (0 and 100 at
            // the ends), as the curve editor's drag clamp does, so points
            // never cross.
            CurveX(1, d => d.FfbCurveX1, (d, v) => d.FfbCurveX1 = v, lo: _ => 0,             hi: d => d.FfbCurveX2),
            CurveX(2, d => d.FfbCurveX2, (d, v) => d.FfbCurveX2 = v, lo: d => d.FfbCurveX1, hi: d => d.FfbCurveX3),
            CurveX(3, d => d.FfbCurveX3, (d, v) => d.FfbCurveX3 = v, lo: d => d.FfbCurveX2, hi: d => d.FfbCurveX4),
            CurveX(4, d => d.FfbCurveX4, (d, v) => d.FfbCurveX4 = v, lo: d => d.FfbCurveX3, hi: _ => 100),
            CurveY(1, d => d.FfbCurveY1, (d, v) => d.FfbCurveY1 = v),
            CurveY(2, d => d.FfbCurveY2, (d, v) => d.FfbCurveY2 = v),
            CurveY(3, d => d.FfbCurveY3, (d, v) => d.FfbCurveY3 = v),
            CurveY(4, d => d.FfbCurveY4, (d, v) => d.FfbCurveY4 = v),
            CurveY(5, d => d.FfbCurveY5, (d, v) => d.FfbCurveY5 = v),
        };

        // EQ band ceilings (display %). Legacy firmware: every band 400.
        // 10-band firmware: 500, except the 100 Hz band (register 6) at 100.
        internal const int EqLegacyMax = 400, Eq10Max = 500, Eq10TopBandMax = 100;
        internal const int CurvePointCount = 5;

        private static NumericSetting EqBand(int band, Func<MozaData, int> get, Action<MozaData, int> set)
            => new NumericSetting
            {
                Name = "Equalizer" + band,
                Commands = new[] { EqRegisterCommands[band - 1] },
                GetRaw = get, SetRaw = set,
                Min = 0, Max = EqLegacyMax, Fine = 5, Coarse = 25,
                // Band 6 is the 100 Hz band, capped at 100 % on 10-band firmware.
                MaxFor = band == 6
                    ? (Func<MozaData, int>)(d => d.BaseSupportsEq10 ? Eq10TopBandMax : EqLegacyMax)
                    : d => d.BaseSupportsEq10 ? Eq10Max : EqLegacyMax,
                // Bands 7-10 exist only on 10-band firmware — old bases must
                // never see cmds 0x32..0x35.
                Supported = band >= 7 ? (Func<MozaData, bool>)(d => d.BaseSupportsEq10) : null,
                Label = () => EqRegisterFrequencies[band - 1] + " Hz",
                Unit = "%",
            };

        private static NumericSetting CurveX(int point, Func<MozaData, int> get, Action<MozaData, int> set,
                                             Func<MozaData, int> lo, Func<MozaData, int> hi)
            => new NumericSetting
            {
                Name = "FfbCurveX" + point, Commands = new[] { "base-ffb-curve-x" + point },
                GetRaw = get, SetRaw = set,
                Min = 1, Max = 99, Fine = 5, Coarse = 10,
                MinFor = d => lo(d) + 1,
                MaxFor = d => hi(d) - 1,
                Label = () => string.Format(Strings.ControlDash_CurvePoint, point),
                Unit = "%",
            };

        private static NumericSetting CurveY(int point, Func<MozaData, int> get, Action<MozaData, int> set)
            => new NumericSetting
            {
                Name = "FfbCurveY" + point, Commands = new[] { "base-ffb-curve-y" + point },
                GetRaw = get, SetRaw = set,
                Min = 0, Max = 100, Fine = 5, Coarse = 10,
                Label = () => string.Format(Strings.ControlDash_CurvePoint, point),
                Unit = "%",
            };

        /// <summary>
        /// On/off settings. Each row generates one <c>AZOM.&lt;Name&gt;</c>
        /// bool property plus <c>On</c>/<c>Off</c>/<c>Toggle</c> actions.
        /// <c>WorkMode</c> is deliberately absent — its On/Off actions predate
        /// this table and are registered by hand so the names don't collide.
        /// </summary>
        internal static readonly ToggleSetting[] Toggles =
        {
            new ToggleSetting {
                Name = "Protection", Command = "base-protection",
                Get = d => d.Protection, Set = (d, v) => d.Protection = v,
                Label = () => Strings.SliderLabel_HandsOffProtection },

            new ToggleSetting {
                Name = "FfbReverse", Command = "base-ffb-reverse",
                Get = d => d.FfbReverse, Set = (d, v) => d.FfbReverse = v,
                Label = () => Strings.SliderLabel_ForceFeedbackReversal },

            // cmd 0x1C: PitHouse writes 0 = off, 20 = on.
            new ToggleSetting {
                Name = "SoftLimitRetain", Command = "base-soft-limit-retain",
                Get = d => d.SoftLimitRetain, Set = (d, v) => d.SoftLimitRetain = v,
                OnValue = 20,
                Label = () => Strings.SliderLabel_RetainGameFfb },

            // cmd 0x1E: 0 = Reserved, 1 = Full. "On" = full output.
            new ToggleSetting {
                Name = "PerformanceOutput", Command = "base-temp-strategy",
                Get = d => d.TempStrategy, Set = (d, v) => d.TempStrategy = v,
                Label = () => Strings.Label_PerformanceOutput },

            new ToggleSetting {
                Name = "BaseStatusLed", Command = "main-set-led-status",
                Get = d => d.LedStatus, Set = (d, v) => d.LedStatus = v,
                Label = () => Strings.SliderLabel_BaseStatusLed },

            // BLE is inverted on the wire: 0 = on, 85 = off.
            new ToggleSetting {
                Name = "Bluetooth", Command = "main-set-ble-mode",
                Get = d => d.BleMode, Set = (d, v) => d.BleMode = v,
                OnValue = 0, OffValue = 85,
                Label = () => Strings.SliderLabel_Bluetooth },
        };

        internal const int RoadSensitivityMinPreset = 0;
        internal const int RoadSensitivityMaxPreset = 10;

        /// <summary>Preset index 0..10 from the stored register value, or -1 when unread.</summary>
        internal static int RoadSensitivityPresetFromRaw(int raw)
        {
            if (raw < 10) return -1;
            int n = (int)Math.Round((raw - 10) / 4.0);
            return n < 0 ? 0 : (n > 10 ? 10 : n);
        }

        internal static int RoadSensitivityRawFromPreset(int preset) => 10 + 4 * preset;

        /// <summary>Selector position after presets 0..10: values match no preset.</summary>
        internal const int EqSensitivityCustom = 11;

        /// <summary>
        /// Sensitivity selector position derived like PitHouse's: preset N only
        /// when 0x0C and every EQ band match row N, else
        /// <see cref="EqSensitivityCustom"/>; -1 while 0x0C is unread. Legacy
        /// firmware compares just the six old registers.
        /// </summary>
        internal static int EqSensitivitySelection(MozaData d)
        {
            int raw = d.RoadSensitivity;
            if (raw < 0) return -1;
            int n = (raw - 10) / 4;
            if (raw < 10 || RoadSensitivityRawFromPreset(n) != raw || n > RoadSensitivityMaxPreset)
                return EqSensitivityCustom;

            int[] p = EqSensitivityPresets[n];
            if (d.BaseSupportsEq10)
            {
                for (int i = 0; i < 10; i++)
                    if (GetEqRegister(d, Eq10FreqOrderRegisters[i]) != p[i]) return EqSensitivityCustom;
            }
            else
            {
                for (int i = 0; i < 6; i++)
                    if (GetEqRegister(d, i) != p[Eq6FreqColumns[i]]) return EqSensitivityCustom;
            }
            return n;
        }

        /// <summary>Read one EQ register's value from <paramref name="d"/> by 0-based register index.</summary>
        internal static int GetEqRegister(MozaData d, int index0)
        {
            switch (index0)
            {
                case 0: return d.Equalizer1;
                case 1: return d.Equalizer2;
                case 2: return d.Equalizer3;
                case 3: return d.Equalizer4;
                case 4: return d.Equalizer5;
                case 5: return d.Equalizer6;
                case 6: return d.Equalizer7;
                case 7: return d.Equalizer8;
                case 8: return d.Equalizer9;
                case 9: return d.Equalizer10;
                default: return -1;
            }
        }

        /// <summary>Write one EQ register's value into <paramref name="d"/> by 0-based register index.</summary>
        internal static void SetEqRegister(MozaData d, int index0, int value)
        {
            switch (index0)
            {
                case 0: d.Equalizer1 = value; break;
                case 1: d.Equalizer2 = value; break;
                case 2: d.Equalizer3 = value; break;
                case 3: d.Equalizer4 = value; break;
                case 4: d.Equalizer5 = value; break;
                case 5: d.Equalizer6 = value; break;
                case 6: d.Equalizer7 = value; break;
                case 7: d.Equalizer8 = value; break;
                case 8: d.Equalizer9 = value; break;
                case 9: d.Equalizer10 = value; break;
            }
        }
    }
}
