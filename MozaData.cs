using System;

namespace MozaPlugin
{
    /// <summary>
    /// Holds the latest values read from Moza hardware.
    /// </summary>
    public class MozaData
    {
        // Connection status
        public volatile bool IsBaseConnected;
        public volatile bool IsHubConnected;
        /// <summary>
        /// True when a standalone dashboard (e.g. CM2 Racing Dash on PID 0x0025) is
        /// confirmed on the serial bus — either via USB-PID-driven detection in
        /// <c>MozaPlugin.MarkStandaloneDashboardDetectedFromUsb</c> or because the
        /// dashboard answered a dash-* settings read on the wire. Lets
        /// <see cref="IsConnected"/> turn true without a wheelbase/hub present.
        /// </summary>
        public volatile bool IsDashboardConnected;
        public volatile bool BaseSettingsRead;

        /// <summary>
        /// True when any Moza device is confirmed on the serial bus (base, hub,
        /// or standalone dashboard). Use this as the "can I send commands?" guard
        /// instead of IsBaseConnected, which is only true when a wheelbase is present.
        /// </summary>
        public bool IsConnected => IsBaseConnected || IsHubConnected || IsDashboardConnected;

        // Wheel identity (populated after wheel detection, cleared on disconnect)
        // Volatile: written from serial read thread, read from UI thread.
        public volatile string WheelModelName = "";
        public volatile string WheelSerialNumber = "";
        public volatile string WheelSwVersion = "";
        public volatile string WheelHwVersion = "";
        public volatile string WheelHwSubVersion = "";
        // PitHouse-style extended identity fields (groups 0x02/0x04/0x05/0x06/0x09/0x11).
        public volatile int WheelSubDeviceCount;               // from 0x09 reply first byte
        /// <summary>12-byte STM32 MCU UID (from 0x06 probe). Likely the mcUid PitHouse keys dashboard sync against.</summary>
        public byte[] WheelMcuUid = System.Array.Empty<byte>();
        public byte[] WheelDeviceType = System.Array.Empty<byte>();    // from 0x04 reply, e.g. 01 02 04 06
        public byte[] WheelCapabilities = System.Array.Empty<byte>();  // from 0x05 reply, e.g. 01 02 1f 01
        public byte[] WheelIdentity11 = System.Array.Empty<byte>();    // from 0x11 cmd=04 reply
        public volatile int WheelDevicePresence;                       // from 0x02 reply first byte (protocol ver?)

        // Display sub-device identity (populated by Plugin.SendDisplayProbe responses).
        // Display hangs off group 0x43 wrapper and has its own identity separate from wheel.
        public volatile string DisplayModelName = "";
        public volatile string DisplayHwVersion = "";
        public volatile string DisplaySwVersion = "";
        public volatile string DisplaySerialNumber = "";
        public volatile int DisplaySubDeviceCount;
        public volatile int DisplayDevicePresence;
        public byte[] DisplayMcuUid = System.Array.Empty<byte>();
        public byte[] DisplayDeviceType = System.Array.Empty<byte>();
        public byte[] DisplayCapabilities = System.Array.Empty<byte>();
        public byte[] DisplayIdentity11 = System.Array.Empty<byte>();
        private volatile string _serialPartA = "";
        private volatile string _serialPartB = "";

        // Raw observed reply bytes from group 0x40 cmd 0x28 polls.
        // PitHouse polls 28:00 + 28:01 at ~1 Hz throughout the active phase
        // across all four bridge captures (sim/logs/bridge-20260503-*.jsonl).
        // Reply layouts:
        //   28:00 reply: C0 71 28 00 00 <byte5>
        //   28:01 reply: C0 71 28 01 <byte4> <byte5>
        // Cross-capture analysis shows byte5 of 28:00 takes dominant 00/01 +
        // recurring 0x0b across all captures, with sporadic high values.
        // Same observation applies even when the user is NOT touching wheel
        // controls — the byte still oscillates. Semantics not yet decoded;
        // stored raw without naming so a maintainer can correlate values
        // against game state in subsequent controlled experiments.
        public volatile byte Last28x00Byte5;
        public volatile bool Last28x00ByteValid;
        public volatile byte Last28x01Byte4;
        public volatile byte Last28x01Byte5;
        public volatile bool Last28x01BytesValid;
        public volatile int Last28xReplyTickMs; // Environment.TickCount snapshot

        // Temperatures (raw / 100 = degrees C from device)
        public volatile int McuTemp;
        public volatile int MosfetTemp;
        public volatile int MotorTemp;
        public volatile bool UseFahrenheit;

        // State
        public volatile int BaseState;
        public volatile int BaseStateError;

        // Physical input positions from HID (independent of serial protocol)
        public volatile bool IsHidConnected;
        public volatile int SteeringAngleRaw;
        public volatile int SteeringAngleRawMin;
        public volatile int SteeringAngleRawMax;
        public volatile int ThrottlePosition;   // 0-100
        public volatile int BrakePosition;      // 0-100
        public volatile int ClutchPosition;     // 0-100
        public volatile int HandbrakePosition;  // 0-100
        public volatile int LeftPaddlePosition;     // 0-100
        public volatile int RightPaddlePosition;    // 0-100
        public volatile int CombinedPaddlePosition; // 0-100

        // Button states from HID (0-based index, true = pressed)
        public const int MaxButtons = 128;
        public readonly bool[] ButtonStates = new bool[MaxButtons];
        public volatile int ButtonCount;

        // Handbrake button (separate HID device, only fires in button mode)
        public volatile bool HandbrakeButtonPressed;

        // Core settings
        public volatile int Limit;
        public volatile int MaxAngle;
        public volatile int FfbStrength;
        public volatile int Torque;
        public volatile int Speed;

        // Wheelbase effects
        public volatile int Damper;
        public volatile int Friction;
        public volatile int Inertia;
        public volatile int Spring;

        // Protection
        public volatile int Protection;
        public volatile int ProtectionMode;
        public volatile int NaturalInertia;

        // High speed damping
        public volatile int SpeedDamping;
        public volatile int SpeedDampingPoint;

        // Soft limit
        public volatile int SoftLimitStiffness;
        public volatile int SoftLimitStrength;
        public volatile int SoftLimitRetain;

        // FFB misc
        public volatile int FfbReverse;
        public volatile int FfbDisable;
        public volatile int TempStrategy;        // cmd 0x1E base — also exposed in
                                                  // PitHouse as "Performance output"
                                                  // (0 = Reserved, 1 = Full).
        public volatile int GearshiftVibration;  // cmd 0x2E base — 0..5 intensity.

        // Game effects
        public volatile int GameDamper;
        public volatile int GameFriction;
        public volatile int GameInertia;
        public volatile int GameSpring;

        // Main device
        public volatile int WorkMode;
        public volatile int LedStatus;
        public volatile int Interpolation;

        // ===== Wheel LED settings =====
        public volatile int WheelTelemetryMode;     // 0=Off, 1=Telemetry, 2=Static
        public volatile int WheelTelemetryIdleEffect;
        public volatile int WheelButtonsIdleEffect;
        public volatile int WheelKnobIdleEffect;
        public volatile int WheelKnobLedMode;
        public volatile int WheelButtonsLedMode;
        // Per-group idle-effect SPEED (cmd 0x1E [group] [effect_id] [BE u16 ms]).
        // We track only the last ms value committed for each group; the effect_id
        // byte is always paired from the corresponding *IdleEffect field at write
        // time, so a single int per group is sufficient.
        public volatile int WheelTelemetryIdleSpeedMs;
        public volatile int WheelButtonsIdleSpeedMs;
        public volatile int WheelKnobIdleSpeedMs;
        public volatile int WheelRpmBrightness;
        public volatile int WheelButtonsBrightness;
        public volatile int WheelFlagsBrightness;
        public volatile int WheelIdleMode;
        public volatile int WheelIdleTimeout;
        public volatile int WheelIdleSpeed;
        public volatile int WheelPaddlesMode;
        public volatile int WheelClutchPoint;
        public volatile int WheelKnobMode;
        // Per-rotary-encoder signal mode (newer firmware). 0=Buttons, 1=Knob. -1 = unknown/no response yet.
        public readonly int[] WheelKnobSignalModes = { -1, -1, -1, -1, -1 };
        // True once at least one per-knob response has arrived, indicating firmware supports [42, N].
        public volatile bool WheelKnobSignalModeSupported;
        public volatile int WheelStickMode;
        // True when firmware uses the new 1-byte stick mode (0=none,1=left,2=right,3=both).
        // False when firmware uses old 2-byte format (left stick toggle only).
        public volatile bool WheelDualStickSupported;
        public volatile int WheelRpmDisplayMode;

        // Single lock guarding all wheel/dash LED colour byte[][] arrays for
        // multi-byte RGB read/write atomicity. The arrays themselves are readonly
        // refs (`byte[][]`), so the lock only needs to cover the sequence of byte
        // accesses inside one [i] slot. Display() reads `sc[0]; sc[1]; sc[2]` for
        // the WheelButtonDefaultDuringTelemetry override on the SimHub effect
        // thread; UI handlers write `arr[i][0]=r; arr[i][1]=g; arr[i][2]=b;` on
        // the WPF dispatcher. Without this lock a click during active telemetry
        // can produce a single torn-RGB frame on the wheel (one byte from the new
        // colour, two from the old). The window is small but real; the lock is
        // free in the no-contention case (UI clicks are infrequent vs the 60 Hz
        // Display tick).
        public readonly object LedColorLock = new object();

        /// <summary>
        /// Atomic 3-byte RGB write into <paramref name="dst"/> under <see cref="LedColorLock"/>.
        /// Use from UI handlers in place of three separate <c>dst[0]=…; dst[1]=…; dst[2]=…</c>
        /// assignments.
        /// </summary>
        public void WriteLedColor(byte[] dst, byte r, byte g, byte b)
        {
            lock (LedColorLock)
            {
                dst[0] = r;
                dst[1] = g;
                dst[2] = b;
            }
        }

        // Wheel RPM colors (10 LEDs, [R, G, B] each)
        public readonly byte[][] WheelRpmColors = InitWheelRpmColorArray();
        public readonly byte[][] WheelRpmBlinkColors = InitRpmColorArray();
        public readonly byte[][] WheelButtonColors = InitColorArray(14);
        // Per-button "default during telemetry" flags. When true, any 'off' (0,0,0) value
        // sent through the live button-color telemetry pipeline is replaced with that
        // button's configured static color (see WheelButtonColors).
        public readonly bool[] WheelButtonDefaultDuringTelemetry = new bool[14];
        public readonly byte[][] WheelFlagColors = InitFlagColorArray();
        public readonly byte[] WheelIdleColor = new byte[] { 255, 255, 255 };

        // Per-knob LED ring colors — W17 CS Pro (4 knobs) / W18 KS Pro (5 knobs).
        // Background = idle colour shown when the knob is not being turned;
        // primary = colour flashed on rotation. Wire: [0x27, group, role] + RGB,
        // group 0..KnobCount-1, role 0=background, 1=primary.
        public const int WheelKnobMax = 5;
        public readonly byte[][] WheelKnobBackgroundColors = InitColorArray(WheelKnobMax);
        public readonly byte[][] WheelKnobPrimaryColors = InitColorArray(WheelKnobMax);

        // Per-LED knob ring (Inactive / background) colors. Up to 56 LEDs
        // (CS Pro 48, KS Pro 56). Readable + writable via wheel-knob-bg-color{1..56}.
        // Wire cmd: 0x1F 0x03 0x01 [N-1] [RGB] (cmd 0x1F sub 0x03 sub 0x01).
        public const int KnobRingLedMax = 56;
        public readonly byte[][] KnobRingColors = InitColorArray(KnobRingLedMax);
        public volatile int KnobRingBrightness = -1;

        // ES wheel
        public volatile int WheelESRpmBrightness;
        public readonly byte[][] WheelESRpmColors = InitRpmColorArray();
        public volatile int WheelRpmIndicatorMode;

        // ===== Dash LED settings =====
        public volatile int DashRpmIndicatorMode;
        public volatile int DashFlagsIndicatorMode;
        public volatile int DashRpmDisplayMode;
        public volatile int DashRpmBrightness;
        public volatile int DashFlagsBrightness;
        public volatile int DashDisplayBrightness = -1;
        public volatile int DashDisplayStandbyMin;

        public readonly byte[][] DashRpmColors = InitRpmColorArray();
        public readonly byte[][] DashRpmBlinkColors = InitRpmColorArray();
        public readonly byte[][] DashFlagColors = InitFlagColorArray();

        // ===== Base ambient LED settings (R21/R25/R27 family — 18 LEDs / 2 strips) =====
        // -1 = not yet read from device.
        public volatile int BaseAmbientBrightness = -1;     // 0..255
        public volatile int BaseAmbientStandbyMode = -1;    // 0=const, 1=?, 2=breath, 3=cycle, 4=rainbow, 5=flow
        public volatile int BaseAmbientIndicatorState = -1; // 0=off, 1=on
        public volatile int BaseAmbientSleepMode = -1;      // 0=disabled, 1=enabled
        public volatile int BaseAmbientSleepTimeout = -1;   // value range TBD
        public readonly byte[] BaseAmbientStartupColor = new byte[] { 0, 0, 0 };
        public readonly byte[] BaseAmbientShutdownColor = new byte[] { 0, 0, 0 };
        // Diagnostic only — stitched from group 0x07 cmd 0x01 + cmd 0x02 reads
        // against dev 0x12 (e.g. "R25 Black # MOT-1 -V01"). Not used for gating.
        public volatile string BaseModelName = "";
        // ===== Base identity (device 0x13 — direct probes, mirror of the
        // Wheel identity fields). Populated by base-model-name / base-sw-version
        // / base-hw-version / base-hw-sub / base-mcu-uid / base-identity-11
        // responses. DeviceCatalog consumes these to synthesise the Motor +
        // Wheel Base manifest entries iRacing's CoAP client expects. =====
        public volatile string BaseSwVersion = "";
        public volatile string BaseHwVersion = "";
        public volatile string BaseHwSubVersion = "";
        public byte[] BaseMcuUid = System.Array.Empty<byte>();
        public byte[] BaseIdentity11 = System.Array.Empty<byte>();

        // ===== FFB Equalizer (6 bands: 10/15/25/40/60/100 Hz, 0-400% where 100% = flat) =====
        public volatile int Equalizer1 = 100;
        public volatile int Equalizer2 = 100;
        public volatile int Equalizer3 = 100;
        public volatile int Equalizer4 = 100;
        public volatile int Equalizer5 = 100;
        public volatile int Equalizer6 = 100;

        // ===== FFB Curve (5 Y output values at fixed 20/40/60/80/100% input breakpoints) =====
        // X breakpoints are always written as [20, 40, 60, 80] — not user-adjustable.
        public volatile int FfbCurveY1 = 20, FfbCurveY2 = 40, FfbCurveY3 = 60, FfbCurveY4 = 80, FfbCurveY5 = 100;

        // ===== Main device =====
        public volatile int BleMode;               // 0=On, 85=Off

        // ===== Pedals settings =====
        public volatile int PedalsThrottleDir;
        public volatile int PedalsThrottleMin;
        public volatile int PedalsThrottleMax;
        public volatile int PedalsBrakeDir;
        public volatile int PedalsBrakeMin;
        public volatile int PedalsBrakeMax;
        public volatile int PedalsBrakeAngleRatio = 50; // 0=angle sensor, 100=load cell
        public volatile int PedalsClutchDir;
        public volatile int PedalsClutchMin;
        public volatile int PedalsClutchMax;

        // Pedal output curves (values 0-100, stored as ints; device uses 4-byte floats)
        public readonly int[] PedalsThrottleCurve = new int[] { 20, 40, 60, 80, 100 };
        public readonly int[] PedalsBrakeCurve    = new int[] { 20, 40, 60, 80, 100 };
        public readonly int[] PedalsClutchCurve   = new int[] { 20, 40, 60, 80, 100 };

        // ===== Handbrake settings =====
        public volatile int HandbrakeDirection;      // 0=Normal, 1=Reversed
        public volatile int HandbrakeMin;
        public volatile int HandbrakeMax;
        public volatile int HandbrakeMode;           // 0=Axis, 1=Button
        public volatile int HandbrakeButtonThreshold; // 0-100 (percent)

        // Handbrake output curve (values 0-100, stored as ints; device uses 4-byte floats)
        public readonly int[] HandbrakeCurve = new int[] { 20, 40, 60, 80, 100 };

        // ===== Hub port power status (-1 = not read yet) =====
        public volatile int HubBasePower = -1;
        public volatile int HubPort1Power = -1;
        public volatile int HubPort2Power = -1;
        public volatile int HubPort3Power = -1;
        public volatile int HubPedals1Power = -1;
        public volatile int HubPedals2Power = -1;
        public volatile int HubPedals3Power = -1;

        private static byte[][] InitColorArray(int count)
        {
            var arr = new byte[count][];
            for (int i = 0; i < count; i++)
                arr[i] = new byte[] { 0, 0, 0 };
            return arr;
        }

        /// <summary>
        /// Default RPM LED colors: 1-3 green, 4-7 red, 8-10 magenta.
        /// </summary>
        private static byte[][] InitRpmColorArray()
        {
            return new byte[][]
            {
                new byte[] { 0, 255, 0 }, new byte[] { 0, 255, 0 }, new byte[] { 0, 255, 0 },
                new byte[] { 255, 0, 0 }, new byte[] { 255, 0, 0 }, new byte[] { 255, 0, 0 }, new byte[] { 255, 0, 0 },
                new byte[] { 255, 0, 255 }, new byte[] { 255, 0, 255 }, new byte[] { 255, 0, 255 },
            };
        }

        // Wheels up to 25 RPM LEDs (KS Pro = 18). First 10 match legacy defaults; 11+ default black.
        private const int WheelRpmLedMax = 25;
        private static byte[][] InitWheelRpmColorArray()
        {
            var baseColors = InitRpmColorArray();
            var arr = new byte[WheelRpmLedMax][];
            for (int i = 0; i < WheelRpmLedMax; i++)
                arr[i] = i < baseColors.Length ? baseColors[i] : new byte[] { 0, 0, 0 };
            return arr;
        }

        /// <summary>
        /// Default flag LED colors: all magenta.
        /// </summary>
        private static byte[][] InitFlagColorArray()
        {
            var arr = new byte[6][];
            for (int i = 0; i < 6; i++)
                arr[i] = new byte[] { 255, 0, 255 };
            return arr;
        }

        public void UpdateFromCommand(string commandName, int value)
        {
            switch (commandName)
            {
                // Temperatures
                case "base-mcu-temp":       McuTemp = value; IsBaseConnected = true; break;
                case "base-mosfet-temp":    MosfetTemp = value; break;
                case "base-motor-temp":     MotorTemp = value; break;

                // State
                case "base-state":          BaseState = value; break;
                case "base-state-err":      BaseStateError = value; break;

                // Core settings
                case "base-limit":          Limit = value; BaseSettingsRead = true; break;
                case "base-max-angle":      MaxAngle = value; break;
                case "base-ffb-strength":   FfbStrength = value; break;
                case "base-torque":         Torque = value; break;
                case "base-speed":          Speed = value; break;

                // Effects
                case "base-damper":         Damper = value; break;
                case "base-friction":       Friction = value; break;
                case "base-inertia":        Inertia = value; break;
                case "base-spring":         Spring = value; break;

                // Protection
                case "base-protection":         Protection = value; break;
                case "base-protection-mode":    ProtectionMode = value; break;
                case "base-natural-inertia":    NaturalInertia = value; break;

                // High speed damping
                case "base-speed-damping":       SpeedDamping = value; break;
                case "base-speed-damping-point": SpeedDampingPoint = value; break;

                // Soft limit
                case "base-soft-limit-stiffness": SoftLimitStiffness = value; break;
                case "base-soft-limit-strength":  SoftLimitStrength = value; break;
                case "base-soft-limit-retain":    SoftLimitRetain = value; break;

                // FFB misc
                case "base-ffb-reverse":    FfbReverse = value; break;
                case "base-ffb-disable":    FfbDisable = value; break;
                case "base-temp-strategy":  TempStrategy = value; break;
                case "base-gearshift-vibration": GearshiftVibration = value; break;

                // Game effects
                case "main-get-damper-gain":   GameDamper = value; break;
                case "main-get-friction-gain": GameFriction = value; break;
                case "main-get-inertia-gain":  GameInertia = value; break;
                case "main-get-spring-gain":   GameSpring = value; break;

                // Main device
                case "main-get-work-mode":     WorkMode = value; break;
                case "main-get-led-status":    LedStatus = value; break;
                case "main-get-interpolation": Interpolation = value; break;
                case "main-get-ble-mode":      BleMode = value; break;

                // Wheel LED settings
                case "wheel-telemetry-mode":        WheelTelemetryMode = value; break;
                case "wheel-telemetry-idle-effect":  WheelTelemetryIdleEffect = value; break;
                case "wheel-buttons-idle-effect":    WheelButtonsIdleEffect = value; break;
                case "wheel-knob-idle-effect":       WheelKnobIdleEffect = value; break;
                case "wheel-knob-led-mode":          WheelKnobLedMode = value; break;
                case "wheel-buttons-led-mode":       WheelButtonsLedMode = value; break;
                case "wheel-rpm-brightness":         WheelRpmBrightness = value; break;
                case "wheel-buttons-brightness":     WheelButtonsBrightness = value; break;
                case "wheel-flags-brightness":       WheelFlagsBrightness = value; break;
                case "wheel-idle-mode":              WheelIdleMode = value; break;
                case "wheel-idle-timeout":           WheelIdleTimeout = value; break;
                // wheel-idle-speed handled in UpdateFromArray — payload is
                // [mode, ms_msb, ms_lsb], so the ParseIntValue of all 3 bytes
                // (mode<<16)|ms is wrong. The array path extracts ms.
                case "wheel-paddles-mode":           WheelPaddlesMode = value - 1; break; // raw 1/2/3 → display 0/1/2
                case "wheel-clutch-point":           WheelClutchPoint = value; break;
                case "wheel-knob-mode":              WheelKnobMode = value; break;
                case "wheel-knob-signal-mode0":      WheelKnobSignalModes[0] = value; WheelKnobSignalModeSupported = true; break;
                case "wheel-knob-signal-mode1":      WheelKnobSignalModes[1] = value; WheelKnobSignalModeSupported = true; break;
                case "wheel-knob-signal-mode2":      WheelKnobSignalModes[2] = value; WheelKnobSignalModeSupported = true; break;
                case "wheel-knob-signal-mode3":      WheelKnobSignalModes[3] = value; WheelKnobSignalModeSupported = true; break;
                case "wheel-knob-signal-mode4":      WheelKnobSignalModes[4] = value; WheelKnobSignalModeSupported = true; break;
                case "wheel-stick-mode":             WheelStickMode = value; break;
                case "wheel-rpm-indicator-mode":     WheelRpmIndicatorMode = value - 1; break; // raw 1/2/3 → display 0/1/2
                case "wheel-get-rpm-display-mode":  WheelRpmDisplayMode = value; break;
                case "wheel-old-rpm-brightness":     WheelESRpmBrightness = value; break;
                case "wheel-knob-brightness":        KnobRingBrightness = value; break;

                // Dash settings — receiving any of these confirms a dashboard
                // is on the bus (whether wheel-bridged or standalone USB).
                case "dash-rpm-indicator-mode":   DashRpmIndicatorMode = value; IsDashboardConnected = true; break;
                case "dash-flags-indicator-mode": DashFlagsIndicatorMode = value; IsDashboardConnected = true; break;
                case "dash-rpm-display-mode":     DashRpmDisplayMode = value; IsDashboardConnected = true; break;
                case "dash-rpm-brightness":       DashRpmBrightness = value; IsDashboardConnected = true; break;
                case "dash-flags-brightness":     DashFlagsBrightness = value; IsDashboardConnected = true; break;

                // Base ambient LED settings
                case "base-ambient-brightness":      BaseAmbientBrightness = value; break;
                case "base-ambient-standby-mode":    BaseAmbientStandbyMode = value; break;
                case "base-ambient-indicator-state": BaseAmbientIndicatorState = value; break;
                case "base-ambient-sleep-mode":      BaseAmbientSleepMode = value; break;
                case "base-ambient-sleep-timeout":   BaseAmbientSleepTimeout = value; break;

                // FFB Equalizer
                case "base-equalizer1": Equalizer1 = value; break;
                case "base-equalizer2": Equalizer2 = value; break;
                case "base-equalizer3": Equalizer3 = value; break;
                case "base-equalizer4": Equalizer4 = value; break;
                case "base-equalizer5": Equalizer5 = value; break;
                case "base-equalizer6": Equalizer6 = value; break;

                // FFB Curve (X breakpoints are fixed, only Y values read back)
                case "base-ffb-curve-y1": FfbCurveY1 = value; break;
                case "base-ffb-curve-y2": FfbCurveY2 = value; break;
                case "base-ffb-curve-y3": FfbCurveY3 = value; break;
                case "base-ffb-curve-y4": FfbCurveY4 = value; break;
                case "base-ffb-curve-y5": FfbCurveY5 = value; break;

                // Pedals settings
                case "pedals-throttle-dir": PedalsThrottleDir = value; break;
                case "pedals-throttle-min": PedalsThrottleMin = value; break;
                case "pedals-throttle-max": PedalsThrottleMax = value; break;
                case "pedals-brake-dir":    PedalsBrakeDir    = value; break;
                case "pedals-brake-min":    PedalsBrakeMin    = value; break;
                case "pedals-brake-max":    PedalsBrakeMax    = value; break;
                case "pedals-brake-angle-ratio": PedalsBrakeAngleRatio = value; break;
                case "pedals-clutch-dir":   PedalsClutchDir   = value; break;
                case "pedals-clutch-min":   PedalsClutchMin   = value; break;
                case "pedals-clutch-max":   PedalsClutchMax   = value; break;

                // Pedal curves (float values cast to int, 0-100 range)
                case "pedals-throttle-y1": PedalsThrottleCurve[0] = value; break;
                case "pedals-throttle-y2": PedalsThrottleCurve[1] = value; break;
                case "pedals-throttle-y3": PedalsThrottleCurve[2] = value; break;
                case "pedals-throttle-y4": PedalsThrottleCurve[3] = value; break;
                case "pedals-throttle-y5": PedalsThrottleCurve[4] = value; break;
                case "pedals-brake-y1":    PedalsBrakeCurve[0]    = value; break;
                case "pedals-brake-y2":    PedalsBrakeCurve[1]    = value; break;
                case "pedals-brake-y3":    PedalsBrakeCurve[2]    = value; break;
                case "pedals-brake-y4":    PedalsBrakeCurve[3]    = value; break;
                case "pedals-brake-y5":    PedalsBrakeCurve[4]    = value; break;
                case "pedals-clutch-y1":   PedalsClutchCurve[0]   = value; break;
                case "pedals-clutch-y2":   PedalsClutchCurve[1]   = value; break;
                case "pedals-clutch-y3":   PedalsClutchCurve[2]   = value; break;
                case "pedals-clutch-y4":   PedalsClutchCurve[3]   = value; break;
                case "pedals-clutch-y5":   PedalsClutchCurve[4]   = value; break;

                // Handbrake settings
                case "handbrake-direction":        HandbrakeDirection        = value; break;
                case "handbrake-min":              HandbrakeMin              = value; break;
                case "handbrake-max":              HandbrakeMax              = value; break;
                case "handbrake-mode":             HandbrakeMode             = value; break;
                case "handbrake-button-threshold": HandbrakeButtonThreshold  = value; break;

                // Handbrake curve
                case "handbrake-y1": HandbrakeCurve[0] = value; break;
                case "handbrake-y2": HandbrakeCurve[1] = value; break;
                case "handbrake-y3": HandbrakeCurve[2] = value; break;
                case "handbrake-y4": HandbrakeCurve[3] = value; break;
                case "handbrake-y5": HandbrakeCurve[4] = value; break;

                // Hub port power status
                case "hub-base-power":    HubBasePower    = value; IsHubConnected = true; break;
                case "hub-port1-power":   HubPort1Power   = value; IsHubConnected = true; break;
                case "hub-port2-power":   HubPort2Power   = value; break;
                case "hub-port3-power":   HubPort3Power   = value; break;
                case "hub-pedals1-power": HubPedals1Power = value; break;
                case "hub-pedals2-power": HubPedals2Power = value; break;
                case "hub-pedals3-power": HubPedals3Power = value; break;
            }

        }

        /// <summary>
        /// Update from a parsed array response (colors, timings).
        /// </summary>
        public void UpdateFromArray(string commandName, byte[] data)
        {
            if (data == null) return;

            // **A5 gate**: drop wheel-LED colour responses while live telemetry is
            // actively flowing. Even though writes are no longer gated (cmd 0x27 / cmd
            // 0x1F land on the wheel as the user clicks), a read response that was
            // already in flight before the write landed will carry the wheel's pre-
            // write EEPROM value. If that response then writes into `_data`, the
            // user's pick is silently clobbered in the in-memory mirror until the
            // next read returns the post-write value. The race is small but real
            // (interval between read send and read response, vs UI write landing).
            // Disk + overlay still hold the user's pick correctly; the gate is
            // only protecting the live `_data` mirror used by UI swatches.
            if (Devices.MozaLedDeviceManager.IsLiveAnywhere() && IsWheelLedColorCommand(commandName))
                return;

            // Color commands need at least 3 bytes (R, G, B)
            // Wheel RPM colors
            if (commandName.StartsWith("wheel-rpm-color") && !commandName.Contains("blink"))
            {
                int idx = ParseTrailingIndex(commandName, "wheel-rpm-color");
                if (idx >= 0 && idx < WheelRpmColors.Length && data.Length >= 3)
                    SetColor(WheelRpmColors[idx], data);
            }
            // Wheel button colors
            else if (commandName.StartsWith("wheel-button-color"))
            {
                int idx = ParseTrailingIndex(commandName, "wheel-button-color");
                if (idx >= 0 && idx < 14 && data.Length >= 3)
                    SetColor(WheelButtonColors[idx], data);
            }
            // Wheel flag colors
            else if (commandName.StartsWith("wheel-flag-color"))
            {
                int idx = ParseTrailingIndex(commandName, "wheel-flag-color");
                if (idx >= 0 && idx < 6 && data.Length >= 3)
                    SetColor(WheelFlagColors[idx], data);
            }
            // Old wheel RPM colors
            else if (commandName.StartsWith("wheel-old-rpm-color"))
            {
                int idx = ParseTrailingIndex(commandName, "wheel-old-rpm-color");
                if (idx >= 0 && idx < 10 && data.Length >= 3)
                    SetColor(WheelESRpmColors[idx], data);
            }
            // Wheel idle color
            else if (commandName == "wheel-idle-color")
            {
                if (data.Length >= 3)
                    SetColor(WheelIdleColor, data);
            }
            // Wheel sleep-light speed: 3-byte payload [mode, ms_msb, ms_lsb].
            // The slider in the UI stores a single ms value (for whichever mode
            // is currently selected on the wheel), so we extract only the ms
            // portion. Storing the raw 3-byte big-endian int would yield
            // (mode<<16)|ms, which the slider clamps and the bundle would
            // round-trip incorrectly on next launch.
            else if (commandName == "wheel-idle-speed")
            {
                if (data.Length >= 3)
                    WheelIdleSpeed = (data[1] << 8) | data[2];
            }
            // Base ambient startup / shutdown colors
            else if (commandName == "base-ambient-startup-color")
            {
                if (data.Length >= 3)
                    SetColor(BaseAmbientStartupColor, data);
            }
            else if (commandName == "base-ambient-shutdown-color")
            {
                if (data.Length >= 3)
                    SetColor(BaseAmbientShutdownColor, data);
            }
            // Dash RPM colors
            else if (commandName.StartsWith("dash-rpm-color") && !commandName.Contains("blink"))
            {
                int idx = ParseTrailingIndex(commandName, "dash-rpm-color");
                if (idx >= 0 && idx < 10 && data.Length >= 3)
                    SetColor(DashRpmColors[idx], data);
            }
            // Dash flag colors
            else if (commandName.StartsWith("dash-flag-color") && commandName != "dash-flag-colors")
            {
                int idx = ParseTrailingIndex(commandName, "dash-flag-color");
                if (idx >= 0 && idx < 6 && data.Length >= 3)
                    SetColor(DashFlagColors[idx], data);
            }
            // Per-LED knob ring background colors (cmd 0x1F 0x03 0x01).
            else if (commandName.StartsWith("wheel-knob-bg-color"))
            {
                int idx = ParseTrailingIndex(commandName, "wheel-knob-bg-color");
                if (idx >= 0 && idx < KnobRingLedMax && data.Length >= 3)
                    SetColor(KnobRingColors[idx], data);
            }
            // Per-knob Active LED color (cmd 0x27, role=0). Command name shape
            // is "wheel-knob{N}-active-color" with N in 1..5. Cheap StartsWith
            // gate keeps the parse off the hot path for unrelated frames.
            else if (commandName.StartsWith("wheel-knob") && commandName.EndsWith("-active-color"))
            {
                // Extract the knob index between "wheel-knob" (10 chars) and
                // "-active-color" (13 chars).
                int start = "wheel-knob".Length;
                int end = commandName.Length - "-active-color".Length;
                if (end > start && data.Length >= 3
                    && int.TryParse(commandName.Substring(start, end - start), out int knob1)
                    && knob1 >= 1 && knob1 <= WheelKnobPrimaryColors.Length)
                {
                    SetColor(WheelKnobPrimaryColors[knob1 - 1], data);
                }
            }
            // Wheel identity strings (work with any data length)
            else if (commandName == "wheel-model-name")
            {
                WheelModelName = ParseNullTerminatedString(data);
            }
            else if (commandName == "wheel-sw-version")
            {
                WheelSwVersion = ParseNullTerminatedString(data);
            }
            else if (commandName == "wheel-hw-version")
            {
                WheelHwVersion = ParseNullTerminatedString(data);
            }
            else if (commandName == "wheel-hw-sub")
            {
                WheelHwSubVersion = ParseNullTerminatedString(data);
            }
            else if (commandName == "wheel-serial-a")
            {
                _serialPartA = ParseNullTerminatedString(data);
                WheelSerialNumber = _serialPartA + _serialPartB;
            }
            else if (commandName == "wheel-serial-b")
            {
                _serialPartB = ParseNullTerminatedString(data);
                WheelSerialNumber = _serialPartA + _serialPartB;
            }
            else if (commandName == "wheel-presence")
            {
                // Reply: 2 bytes. First byte = sub-device count.
                if (data.Length >= 1) WheelSubDeviceCount = data[0];
            }
            else if (commandName == "wheel-device-presence")
            {
                if (data.Length >= 1) WheelDevicePresence = data[0];
            }
            else if (commandName == "wheel-device-type")
            {
                WheelDeviceType = (byte[])data.Clone();
            }
            else if (commandName == "wheel-capabilities")
            {
                WheelCapabilities = (byte[])data.Clone();
            }
            else if (commandName == "wheel-mcu-uid")
            {
                WheelMcuUid = (byte[])data.Clone();
            }
            // Base identity (parallel to wheel identity, dev 0x13). Drives the
            // Motor + Wheel Base manifest entries served at
            // /MOZARacing/ProductDevice/{id} so iRacing's CoAP client engages
            // beyond the device-list probe.
            else if (commandName == "base-model-name")
            {
                BaseModelName = ParseNullTerminatedString(data);
            }
            else if (commandName == "base-sw-version")
            {
                BaseSwVersion = ParseNullTerminatedString(data);
            }
            else if (commandName == "base-hw-version")
            {
                BaseHwVersion = ParseNullTerminatedString(data);
            }
            else if (commandName == "base-hw-sub")
            {
                BaseHwSubVersion = ParseNullTerminatedString(data);
            }
            else if (commandName == "base-mcu-uid")
            {
                BaseMcuUid = (byte[])data.Clone();
            }
            else if (commandName == "base-identity-11")
            {
                BaseIdentity11 = (byte[])data.Clone();
            }
            else if (commandName == "wheel-identity-11")
            {
                WheelIdentity11 = (byte[])data.Clone();
            }
            // Display sub-device responses
            else if (commandName == "display-model-name")
            {
                DisplayModelName = ParseNullTerminatedString(data);
            }
            else if (commandName == "display-hw-version")
            {
                DisplayHwVersion = ParseNullTerminatedString(data);
            }
            else if (commandName == "display-sw-version")
            {
                DisplaySwVersion = ParseNullTerminatedString(data);
            }
            else if (commandName == "display-serial")
            {
                DisplaySerialNumber = ParseNullTerminatedString(data);
            }
            else if (commandName == "display-presence")
            {
                if (data.Length >= 1) DisplaySubDeviceCount = data[0];
            }
            else if (commandName == "display-device-presence")
            {
                if (data.Length >= 1) DisplayDevicePresence = data[0];
            }
            else if (commandName == "display-mcu-uid")
            {
                DisplayMcuUid = (byte[])data.Clone();
            }
            else if (commandName == "display-device-type")
            {
                DisplayDeviceType = (byte[])data.Clone();
            }
            else if (commandName == "display-capabilities")
            {
                DisplayCapabilities = (byte[])data.Clone();
            }
            else if (commandName == "display-identity-11")
            {
                DisplayIdentity11 = (byte[])data.Clone();
            }
        }

        public void ClearWheelIdentity()
        {
            WheelModelName = "";
            WheelSerialNumber = "";
            WheelSwVersion = "";
            WheelHwVersion = "";
            WheelHwSubVersion = "";
            WheelSubDeviceCount = 0;
            WheelDevicePresence = 0;
            WheelMcuUid = System.Array.Empty<byte>();
            WheelDeviceType = System.Array.Empty<byte>();
            WheelCapabilities = System.Array.Empty<byte>();
            WheelIdentity11 = System.Array.Empty<byte>();
            DisplayModelName = "";
            DisplayHwVersion = "";
            DisplaySwVersion = "";
            DisplaySerialNumber = "";
            DisplaySubDeviceCount = 0;
            DisplayDevicePresence = 0;
            DisplayMcuUid = System.Array.Empty<byte>();
            DisplayDeviceType = System.Array.Empty<byte>();
            DisplayCapabilities = System.Array.Empty<byte>();
            DisplayIdentity11 = System.Array.Empty<byte>();
            // Base identity — clear alongside wheel/display so a fresh
            // connection re-probes and DeviceCatalog doesn't serve stale
            // Motor / Wheel Base manifest entries from a previous session.
            BaseModelName = "";
            BaseSwVersion = "";
            BaseHwVersion = "";
            BaseHwSubVersion = "";
            BaseMcuUid = System.Array.Empty<byte>();
            BaseIdentity11 = System.Array.Empty<byte>();
            Last28x00Byte5 = 0;
            Last28x00ByteValid = false;
            Last28x01Byte4 = 0;
            Last28x01Byte5 = 0;
            Last28x01BytesValid = false;
            Last28xReplyTickMs = 0;
            _serialPartA = "";
            _serialPartB = "";
        }

        public static string ParseNullTerminatedString(byte[] data)
        {
            int end = Array.IndexOf(data, (byte)0);
            return System.Text.Encoding.ASCII.GetString(data, 0, end < 0 ? data.Length : end).Trim();
        }

        private static int ParseTrailingIndex(string commandName, string prefix)
        {
            var numStr = commandName.Substring(prefix.Length);
            if (int.TryParse(numStr, out int num))
                return num - 1; // Convert 1-based to 0-based
            return -1;
        }

        // Lock around the 3-byte copy so Display() / PackColors callers never
        // observe a torn RGB. Source for wheel-response paths is the parser-
        // allocated array, so it's safe to read outside the lock.
        private void SetColor(byte[] target, byte[] source)
        {
            lock (LedColorLock)
            {
                target[0] = source[0];
                target[1] = source[1];
                target[2] = source[2];
            }
        }

        // A5: identifies wheel-side LED colour commands whose UpdateFromArray
        // responses should be suppressed while live telemetry is active. Dash
        // (`dash-rpm-color*` / `dash-flag-color*`) and base-ambient colours are
        // *not* included — they don't conflict with the wheel's live pipeline.
        // ES wheel (`wheel-old-*`) excluded — old-protocol wheel has no live
        // colour pipeline that could race.
        private static bool IsWheelLedColorCommand(string commandName)
        {
            // wheel-rpm-color{N} (but not wheel-rpm-blink-color)
            if (commandName.StartsWith("wheel-rpm-color") && !commandName.Contains("blink"))
                return true;
            if (commandName.StartsWith("wheel-button-color")) return true;
            if (commandName.StartsWith("wheel-flag-color")) return true;
            if (commandName.StartsWith("wheel-knob-bg-color")) return true;
            // wheel-knob{N}-active-color
            if (commandName.StartsWith("wheel-knob") && commandName.EndsWith("-active-color"))
                return true;
            return false;
        }
    }
}
