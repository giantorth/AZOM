using System;
using MozaPlugin.Devices.Led;

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
        // Set once the device has reported its pedal / handbrake calibration
        // (a *-max read landed). Gates CaptureFromCurrent so the pre-read default
        // isn't persisted, mirroring BaseSettingsRead.
        public volatile bool PedalsSettingsRead;
        public volatile bool HandbrakeSettingsRead;

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

        /// <summary>
        /// FSR V1 (hw "RS21-D03-*", model "FSR") — a distinct, older product from
        /// FSR V2 ("W13"). Keyed primarily on the hw-version (most specific), with the
        /// model name as corroboration. Lives here rather than on MozaPlugin so the
        /// hardware-write path can consult it without a plugin back-reference;
        /// <c>MozaPlugin.IsFsr1DisplayWheel</c> forwards to this.
        /// </summary>
        public bool IsFsr1DisplayWheel =>
            (WheelHwVersion?.StartsWith("RS21-D03", StringComparison.OrdinalIgnoreCase) ?? false)
            || string.Equals(WheelModelName, "FSR", StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// Byte sequences that appear verbatim on the wire and identify this
        /// specific hardware — MCU UIDs (raw wire bytes) and serial-number ASCII.
        /// <see cref="MozaPlugin.Diagnostics.CaptureRedactor"/> masks any
        /// occurrence of these inside an uploaded/exported serial capture. Only
        /// sequences of ≥ 6 bytes are returned so a short value can't
        /// false-positive against ordinary telemetry bytes.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<byte[]> GetIdentityByteSequences()
        {
            var list = new System.Collections.Generic.List<byte[]>();
            void AddBytes(byte[] b) { if (b != null && b.Length >= 6) list.Add(b); }
            void AddAscii(string s)
            {
                if (!string.IsNullOrEmpty(s) && s.Length >= 6)
                    list.Add(System.Text.Encoding.ASCII.GetBytes(s));
            }
            AddBytes(WheelMcuUid);
            AddBytes(DisplayMcuUid);
            AddBytes(BaseMcuUid);
            AddAscii(_serialPartA);
            AddAscii(_serialPartB);
            AddAscii(DisplaySerialNumber);
            return list;
        }

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

        // Live motor torque, raw wire value: BE16 biased by +500 (500 = zero),
        // 0.1 Nm per count. The sign is direction only — plot the magnitude via
        // LiveTorqueNm. 500 (= 0.0 Nm) is the correct pre-read default, since a
        // never-read register must not graph as a phantom 50 Nm.
        public volatile int LiveTorqueRaw = LiveTorqueZeroBias;

        public const int LiveTorqueZeroBias = 500;

        /// <summary>Unsigned live torque in Nm. Direction is discarded — torque
        /// is torque whichever way the wheel is turning, which is how PitHouse
        /// graphs it too.</summary>
        public double LiveTorqueNm =>
            Math.Abs(LiveTorqueRaw - LiveTorqueZeroBias) / 10.0;

        /// <summary>Signed live torque in Nm; the sign is which way the base is
        /// pulling. Feeds AZOM.CurrentTorqueRaw.</summary>
        public double LiveTorqueSignedNm =>
            (LiveTorqueRaw - LiveTorqueZeroBias) / 10.0;

        // Peak |torque| since the last ResetLiveTorquePeak(), in wire counts so
        // the compare-and-store below stays an int (no Interlocked on a double).
        // Reset per game session by MozaPlugin.DataUpdate; feeds AZOM.MaxTorque.
        public volatile int LiveTorquePeakDeviation;

        // Rejected as garbage by the peak-hold: 100 Nm, ~4x the largest MOZA base.
        private const int LiveTorqueMaxPlausibleDeviation = 1000;

        /// <summary>Session peak torque in Nm; 0 until the first reading.</summary>
        public double LiveTorquePeakNm => LiveTorquePeakDeviation / 10.0;

        public void ResetLiveTorquePeak() => LiveTorquePeakDeviation = 0;

        // AB9 status registers, read off the AB9's own pipe on group 0x2B (dev 0x12).
        // Only the ones that carry data: the wheelbase's mosfet/motor/torque registers
        // reply a constant zero on an AB9 and aren't asked. NoAb9Reading = never
        // answered. Temperature is raw/100 degrees C, same scaling as the wheelbase's.
        public const int NoAb9Reading = -1;

        public volatile int Ab9State2b = NoAb9Reading;
        public volatile int Ab9StateErr2b = NoAb9Reading;
        public volatile int Ab9McuTemp2b = NoAb9Reading;

        /// <summary>Layout byte the AB9 reports for itself (group 0x1E cmd 0xD3) —
        /// the only way to tell whether a written layout stuck or the firmware
        /// substituted something. <see cref="NoAb9Reading"/> until it answers.</summary>
        public volatile int Ab9ModeReadback = NoAb9Reading;

        /// <summary>Clear every probe reading back to "never answered". Called at
        /// AB9 connect so an unplug/swap can't leave the previous device's numbers
        /// standing next to the new one's silence.</summary>
        public void ResetAb9Probe()
        {
            Ab9State2b = Ab9StateErr2b = Ab9McuTemp2b = NoAb9Reading;
            Ab9ModeReadback = NoAb9Reading;
        }

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

        // MOZA Stalks buttons — kept on a separate surface from the wheel's so
        // the 28-button stalks never collide with wheel button indices. Written
        // only by the Stalks HID device; consumed by the truck-sim keyboard feature.
        public const int MaxStalksButtons = 32;
        public readonly bool[] StalksButtonStates = new bool[MaxStalksButtons];
        public volatile int StalksButtonCount;
        // True while a MOZA Stalks HID device is present on the bus (drives the
        // Stalks settings tab's visibility).
        public volatile bool IsStalksConnected;

        // Handbrake button (separate HID device, only fires in button mode)
        public volatile bool HandbrakeButtonPressed;

        // Core settings
        public volatile int Limit = -1;
        public volatile int MaxAngle = -1;
        public volatile int FfbStrength = -1;
        public volatile int Torque = -1;
        public volatile int Speed = -1;

        // Wheelbase effects
        public volatile int Damper = -1;
        public volatile int Friction = -1;
        public volatile int Inertia = -1;
        public volatile int Spring = -1;

        // Protection
        public volatile int Protection = -1;
        public volatile int ProtectionMode;
        public volatile int NaturalInertia = -1;

        // High speed damping
        public volatile int SpeedDamping = -1;
        public volatile int SpeedDampingPoint = -1;

        // Soft limit
        public volatile int SoftLimitStiffness = -1;
        public volatile int SoftLimitStrength;
        public volatile int SoftLimitRetain = -1;

        // FFB misc
        public volatile int FfbReverse = -1;
        public volatile int FfbDisable;
        public volatile int TempStrategy = -1;   // cmd 0x1E base — also exposed in
                                                  // PitHouse as "Performance output"
                                                  // (0 = Reserved, 1 = Full).
        public volatile int GearshiftVibration = -1;  // cmd 0x2E base — 0..5 intensity.

        // Game effects
        public volatile int GameDamper = -1;
        public volatile int GameFriction = -1;
        public volatile int GameInertia = -1;
        public volatile int GameSpring = -1;

        // Main device
        public volatile int WorkMode = -1;
        public volatile int LedStatus;
        public volatile int Interpolation = -1;

        // ===== Wheel LED settings =====
        // Per-group LED mode: 0=Off, 1=SimHub/Telemetry, 2=Static. -1 = NOT YET READ
        // BACK from the wheel — the sentinel every other layer already uses (profile,
        // overlay, extension settings). It must not default to 0: "Off" is a real user
        // choice that suppresses the live colour stream for that group, so a 0 default
        // would blank button and knob LEDs on every cold connect until the readback
        // landed. Consumers gate on an explicit 0/2 and treat -1 as "no opinion".
        public volatile int WheelTelemetryMode = -1;
        public volatile int WheelTelemetryIdleEffect;
        public volatile int WheelButtonsIdleEffect;
        public volatile int WheelKnobIdleEffect;
        public volatile int WheelKnobLedMode = -1;
        public volatile int WheelButtonsLedMode = -1;
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
        // True once the wheel has answered a wheel-knob-mode read. WheelKnobMode is a
        // plain int defaulting to 0, so "answered 0 (Buttons)" and "never answered" are
        // otherwise indistinguishable — and that distinction is what decides whether the
        // legacy All-Rotaries selector is offered on a wheel with no per-knob support.
        public volatile bool WheelKnobModeSupported;
        // Per-rotary-encoder signal mode (newer firmware). 0=Buttons, 1=Knob. -1 = unknown/no response yet.
        public readonly int[] WheelKnobSignalModes = { -1, -1, -1, -1, -1 };
        // True once at least one per-knob response has arrived, indicating firmware supports [42, N].
        public volatile bool WheelKnobSignalModeSupported;

        // Bit n set => the wheel answered wheel-knob-signal-mode for LOGICAL knob n,
        // i.e. that encoder exists and its input mode is configurable. This is the
        // encoder capability; WheelModelInfo.KnobCount is the knob-LED capability and
        // is 0 on most rims that do have encoders. Discovered by sweeping the reads
        // (DeviceProber.BuildNewWheelLedReadCommands) rather than catalogued per model.
        // Written on the serial read thread, read on the UI thread — Interlocked CAS,
        // no lock, per the threading rules.
        private int _wheelKnobSignalModeMask;

        public int WheelKnobSignalModeMask => System.Threading.Volatile.Read(ref _wheelKnobSignalModeMask);

        private void SetKnobSignalModePresent(int logicalKnob)
        {
            if (logicalKnob < 0 || logicalKnob >= WheelKnobMax) return;
            int bit = 1 << logicalKnob;
            int prev;
            do
            {
                prev = _wheelKnobSignalModeMask;
                if ((prev & bit) != 0) return;
            } while (System.Threading.Interlocked.CompareExchange(
                         ref _wheelKnobSignalModeMask, prev | bit, prev) != prev);
        }

        // Store a wheel-knob-signal-mode{firmwareIndex} response into the slot for
        // the LOGICAL knob it controls. Most wheels are identity; the KS Pro
        // firmware addresses signal modes in a different order than its LED groups
        // (firmware 0..4 → physical knobs 1,4,5,3,2), so map through the model's
        // KnobSignalModeOrder. WheelModelName is always resolved before signal-mode
        // reads are issued (DeviceProber gates them on the known model).
        private void StoreKnobSignalMode(int firmwareIndex, int value)
        {
            int logical = Devices.WheelModelInfo.FromModelName(WheelModelName)
                .SignalModeLogicalKnob(firmwareIndex);
            if (logical >= 0 && logical < WheelKnobSignalModes.Length)
                WheelKnobSignalModes[logical] = value;
            SetKnobSignalModePresent(logical);
            WheelKnobSignalModeSupported = true;
        }
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

        // Armed the first time the user commits an LED color via WriteLedColor. Until then,
        // detection-time color read responses must always seed _data even while telemetry is
        // live (otherwise the A5 gate eats the initial seed and swatches come up empty on a
        // profile with no saved colors). Reset on ClearWheelIdentity so a hot-swapped wheel
        // re-seeds. volatile — read on the serial thread, written on the UI thread.
        private volatile bool _ledColorEditArmed;

        /// <summary>
        /// Atomic 3-byte RGB write into <paramref name="dst"/> under <see cref="LedColorLock"/>.
        /// Use from UI handlers in place of three separate <c>dst[0]=…; dst[1]=…; dst[2]=…</c>
        /// assignments.
        /// </summary>
        public void WriteLedColor(byte[] dst, byte r, byte g, byte b)
        {
            // Arm the A5 read-suppression gate: now there is a user pick worth protecting.
            _ledColorEditArmed = true;
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
        // Group 1 (button matrix) spec max = 16 addressable LEDs (W11 has 16).
        public const int WheelButtonMax = 16;
        public readonly byte[][] WheelButtonColors = InitColorArray(WheelButtonMax);
        // Per-button "default during telemetry" flags. When true, any 'off' (0,0,0) value
        // sent through the live button-color telemetry pipeline is replaced with that
        // button's configured static color (see WheelButtonColors).
        public readonly bool[] WheelButtonDefaultDuringTelemetry = new bool[WheelButtonMax];
        // Single "default during telemetry" toggle for the knob ring LEDs. When true,
        // an all-off knob frame from the live telemetry pipeline releases telemetry
        // ownership (active_mask=0) so the firmware restores the wheel's stored knob
        // colours (per-knob Active + per-LED ring Inactive) instead of holding black.
        // Unlike the per-button flags this is a single wheel-wide switch.
        public volatile bool WheelKnobDefaultDuringTelemetry;
        // Max time (ms) the live knob colours may stay unchanged before telemetry
        // ownership is released so the wheel shows its native per-position colours.
        // Lets a colour held a long time be ignored. 0 = off; re-engages on the next
        // colour change. Independent of WheelKnobDefaultDuringTelemetry.
        public volatile int WheelKnobStaticTimeoutMs;
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
        // VGS display-rotation mode (0=off, 1=smooth, 2=immediate). Sentinel -1 =
        // not yet populated; UI mirror only (push-only setting, wheel never reports it).
        public volatile int DashDisplayRotation = -1;

        public readonly byte[][] DashRpmColors = InitRpmColorArray();
        public readonly byte[][] DashRpmBlinkColors = InitRpmColorArray();
        public readonly byte[][] DashFlagColors = InitFlagColorArray();

        // ===== Base ambient LED settings (2 strips; 6 LEDs each on R16 Ultra, 9 on R21/R25/R27 — see BaseModelInfo) =====
        // -1 = not yet read from device.
        public volatile int BaseAmbientBrightness = -1;     // 0..100 (percent)
        public volatile int BaseAmbientStandbyMode = -1;    // 0=off, 1=constant, 2=breathing, 3=cycle, 4=rainbow, 5=sand flow
        public volatile int BaseAmbientIndicatorState = -1; // 0=off, 1=on
        public volatile int BaseAmbientSleepMode = -1;      // sleep light effect: 0=off, 1=breathe
        public volatile int BaseAmbientSleepTimeout = -1;   // minutes
        // Animation interval in ms per standby mode, indexed by mode 0..5.
        // Only modes 2..5 have a register — off and constant have nothing to
        // time, and `1E 00` / `1E 01` are never read or written.
        public readonly int[] BaseAmbientStandbyIntervals = NewNegativeOnes(6);
        // Sleep breathing speed in ms (`0x23 [sleep-mode]`). Distinct from the
        // standby breath interval, which is `1E 02`.
        public volatile int BaseAmbientSleepBreathInterval = -1;
        public readonly byte[] BaseAmbientStartupColor = new byte[] { 0, 0, 0 };
        public readonly byte[] BaseAmbientShutdownColor = new byte[] { 0, 0, 0 };

        // Per-LED palettes. Indexed [strip 0..1][led], sized to the largest
        // known strip (9) so the arrays never need resizing when the base model
        // resolves; only the first LedsPerStrip entries are read or written.
        //
        // Idle palettes are per standby mode: the mode byte in
        // `0x20 [strip] [mode] [led]` is the standby-mode number, and only
        // modes 1 (constant) and 2 (breathing) have a stored palette — the
        // animated modes generate their own colours. Sleep has a single palette
        // (`0x25 [strip] 01 [led]`, sleep effect 1 = breathe).
        // See docs/protocol/leds/base-ambient-0x20-0x22.md.
        public readonly byte[][] BaseAmbientIdleColorsConstant = InitBaseAmbientPalette();
        public readonly byte[][] BaseAmbientIdleColorsBreath = InitBaseAmbientPalette();
        public readonly byte[][] BaseAmbientSleepColors = InitBaseAmbientPalette();
        // Stitched from group 0x07 cmd 0x01 + cmd 0x02 reads against dev 0x12
        // (e.g. "R16 Black # MOT-3-V01"). Selects the ambient strip geometry via
        // BaseModelInfo — see BaseAmbientLedsPerStrip below.
        public volatile string BaseModelName = "";

        // Resolved ambient LEDs-per-strip, latched once the base model name is
        // known. 0 = not resolved yet.
        //
        // STATIC-backed and deliberately NOT cleared by ClearWheelIdentity, for
        // the same reason as BaseFwVersion: that method fires on rim swaps AND
        // transient reconnects, where the BASE is unchanged — but it blanks
        // BaseModelName. The LED emitter picks its chunk shapes and bitmask width
        // from this every frame, so losing it mid-session silently reverted a
        // 6-LED base to the 9-LED wire layout: chunk 2 addressed LEDs 5..8, and
        // the three that don't exist went dark while the bar spread over 9
        // positions instead of 6 (bundle JCFNRS7W).
        private static volatile int s_baseAmbientLedsPerStrip;
        public int BaseAmbientLedsPerStrip
        {
            get => s_baseAmbientLedsPerStrip;
            set => s_baseAmbientLedsPerStrip = value;
        }

        /// <summary>
        /// Ambient LEDs per strip to use right now: the latched value when the
        /// model has been identified, else derived from whatever model name is
        /// currently held (which falls back to the 9-LED default when empty).
        /// Every ambient consumer — wire emitter, UI, device definition — must go
        /// through this rather than reading BaseModelName directly.
        /// </summary>
        public int ResolvedAmbientLedsPerStrip
            => s_baseAmbientLedsPerStrip > 0
                ? s_baseAmbientLedsPerStrip
                : Devices.BaseModelInfo.LedsPerStrip(BaseModelName);
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
        // Numeric base firmware version, packed (maj<<24)|(min<<16)|(patch<<8)|build;
        // 0 = not yet read/unknown. Read via base-fw-version (dev 0x12, group 0x04).
        // Distinct from BaseSwVersion above (group 0x0F), which is the hardware
        // model STRING (e.g. "RS21-D05-MC WB"), not a numeric version. Gates the
        // wheelbase LFE effects — see BaseSupportsLfe.
        //
        // STATIC-backed so it survives the game-switch plugin reload (which builds
        // a fresh MozaData) — the base isn't re-detected over the persistent wire,
        // so base-fw-version isn't re-read; without persistence BaseSupportsLfe
        // would drop to false after every game switch. Deliberately NOT cleared by
        // ClearWheelIdentity or ClearBaseIdentity: both can fire while the BASE is
        // unchanged (rim swap, or a tty that drops and re-opens on sleep/wake), and
        // zeroing it there makes the LFE card vanish on wake. It persists and is
        // overwritten by the next base-fw-version read, which the prober re-issues on
        // every base re-detection — DeviceDetectionState.ResetBase clears the latches
        // that gate it, so a physically-swapped base re-reads the correct value.
        private static volatile int s_baseFwVersion;
        public int BaseFwVersion { get => s_baseFwVersion; set => s_baseFwVersion = value; }

        // Human-readable note on which base-fw probe answered, or "unanswered".
        // Static for the same reason as the version itself. A bug bundle needs this
        // to tell a SILENT base apart from a genuinely old one — both leave
        // BaseSupportsLfe false, and only one of them is a bug (bundle 65HZBQJT: an
        // R12 whose dev-0x12 group-0x04 probe was never answered at all, which took
        // hex archaeology on the wire capture to establish).
        private static volatile string s_baseFwVersionSource = "unanswered";
        public string BaseFwVersionSource
        {
            get => s_baseFwVersionSource;
            set => s_baseFwVersionSource = value ?? "unanswered";
        }

        // Minimum base firmware for the wheelbase LFE effects (complex gearshift,
        // engine vibration, ABS). Captured on 1.2.10.10; the prior non-LFE build
        // was 1.2.9.24.
        public const int BaseFwLfeMin = (1 << 24) | (2 << 16) | (10 << 8) | 10; // 1.2.10.10
        public bool BaseSupportsLfe => BaseFwVersion != 0 && BaseFwVersion >= BaseFwLfeMin;

        /// <summary>Packed <see cref="BaseFwVersion"/> as PitHouse displays it
        /// (major.minor.patch.build), or "unknown" when no probe has answered.</summary>
        public string BaseFwVersionText
        {
            get
            {
                int v = BaseFwVersion;
                if (v == 0) return "unknown";
                return $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
            }
        }

        // The 10-band EQ (equalizer7-10, cmds 0x32..0x35) ships in the same
        // 1.2.10.10 firmware as LFE.
        public bool BaseSupportsEq10 => BaseSupportsLfe;

        // ===== FFB Equalizer =====
        // Legacy 6-band UI (fw < 1.2.10.10): 10/15/25/40/60/100 Hz, 0-400%.
        // 10-band UI (fw >= 1.2.10.10) relabels band 1 to 5 Hz and interleaves
        // 7..10 as 10/30/50/80 Hz; 0-500% except Equalizer6 (100 Hz, 0-100%).
        // 100% = flat either way.
        public volatile int Equalizer1 = 100;
        public volatile int Equalizer2 = 100;
        public volatile int Equalizer3 = 100;
        public volatile int Equalizer4 = 100;
        public volatile int Equalizer5 = 100;
        public volatile int Equalizer6 = 100;
        public volatile int Equalizer7 = 100;
        public volatile int Equalizer8 = 100;
        public volatile int Equalizer9 = 100;
        public volatile int Equalizer10 = 100;
        // Wheelbase road sensitivity (cmd 0x0C), range 10..50; -1 = not yet read.
        public volatile int RoadSensitivity = -1;

        // ===== FFB Curve (5 output points; point 5 fixed at input=100%) =====
        // X1..X4 are the input-axis positions of the first four points, sent via
        // base-ffb-curve-x1..x4 (default 20/40/60/80); Y1..Y5 the output values.
        public volatile int FfbCurveX1 = 20, FfbCurveX2 = 40, FfbCurveX3 = 60, FfbCurveX4 = 80;
        public volatile int FfbCurveY1 = 20, FfbCurveY2 = 40, FfbCurveY3 = 60, FfbCurveY4 = 80, FfbCurveY5 = 100;

        // ===== Main device =====
        public volatile int BleMode;               // 0=On, 85=Off
        // Forza Horizon compatibility. Plain polarity: 1=On, 0=Off — do NOT copy
        // BleMode's inverted convention. -1 = not read back yet.
        public volatile int CompatMode = -1;

        // ===== Pedals settings =====
        public volatile int PedalsThrottleDir;
        public volatile int PedalsThrottleMin;
        public volatile int PedalsThrottleMax = 100; // default full range until device read
        public volatile int PedalsBrakeDir;
        public volatile int PedalsBrakeMin;
        public volatile int PedalsBrakeMax = 100;
        public volatile int PedalsBrakeAngleRatio = 50; // 0=angle sensor, 100=load cell
        public volatile int PedalsClutchDir;
        public volatile int PedalsClutchMin;
        public volatile int PedalsClutchMax = 100;

        // Pedal output curves (values 0-100, stored as ints; device uses 4-byte floats)
        public readonly int[] PedalsThrottleCurve = new int[] { 20, 40, 60, 80, 100 };
        public readonly int[] PedalsBrakeCurve    = new int[] { 20, 40, 60, 80, 100 };
        public readonly int[] PedalsClutchCurve   = new int[] { 20, 40, 60, 80, 100 };

        // ===== Handbrake settings =====
        public volatile int HandbrakeDirection;      // 0=Normal, 1=Reversed
        public volatile int HandbrakeMin;
        public volatile int HandbrakeMax = 100; // default full range until device read
        public volatile int HandbrakeMode;           // 0=Axis, 1=Button
        public volatile int HandbrakeButtonThreshold; // 0-100 (percent)

        // Handbrake output curve (values 0-100, stored as ints; device uses 4-byte floats)
        public readonly int[] HandbrakeCurve = new int[] { 20, 40, 60, 80, 100 };

        // ===== Shifter settings (HGP/SGP, bus dev 0x1A) =====
        // HGP and SGP are independent devices (both can be attached at once), so each
        // has its own settings mirror. -1 = not read yet. Populated model-aware via
        // UpdateShifter/UpdateShifterArray (the wire command names are shared, so the
        // connection/owner disambiguates which device a reply belongs to).
        public sealed class ShifterState
        {
            public volatile int Direction = -1;   // 0=Normal, 1=Reversed
            public volatile int PaddleSync = -1;  // 1/2
            public volatile int HidMode = -1;     // 0/1 game-compat mode
            public volatile int ApplyMode = -1;   // 0/1
            public volatile int Brightness = -1;  // SGP LED brightness 0-10
            public volatile int Led1Index = -1;   // SGP LED S1 palette index 0-7
            public volatile int Led2Index = -1;   // SGP LED S2 palette index 0-7
            public volatile int Theta = -1;       // read-only raw axis (output-x)
        }
        public readonly ShifterState ShifterHgp = new ShifterState();
        public readonly ShifterState ShifterSgp = new ShifterState();
        // Relay-only scratch: the generic device-type identity reply (grp 0x04 →
        // `01 02 XX 06`) from a base/hub-relayed shifter, used to tell HGP from SGP
        // where the PID isn't visible. Model-agnostic — it's what RESOLVES the model.
        public volatile byte[] RelayShifterDeviceType = System.Array.Empty<byte>();
        // Relay-only scratch: model-name (grp 0x07) / hw-version (grp 0x08) replies from
        // a base/hub-relayed shifter, if it answers those groups at all. Model-agnostic
        // like RelayShifterDeviceType — logged by DeviceProber so a support bundle shows
        // whether 0x1A self-describes; nothing depends on them yet.
        public volatile string RelayShifterModelName = string.Empty;
        public volatile string RelayShifterHwVersion = string.Empty;

        private ShifterState? ShifterFor(Devices.ShifterModelKind model) =>
            model == Devices.ShifterModelKind.Hgp ? ShifterHgp :
            model == Devices.ShifterModelKind.Sgp ? ShifterSgp : null;

        /// <summary>Store a parsed shifter setting into the given model's mirror.
        /// No-op for Unknown (a relayed reply seen before the model resolves).</summary>
        public void UpdateShifter(Devices.ShifterModelKind model, string name, int value)
        {
            var s = ShifterFor(model);
            if (s == null) return;
            switch (name)
            {
                case "shifter-hid-mode":    s.HidMode = value; break;
                case "shifter-apply-mode":  s.ApplyMode = value; break;
                case "shifter-brightness":  s.Brightness = value; break;
                case "shifter-direction":   s.Direction = value; break;
                case "shifter-paddle-sync": s.PaddleSync = value; break;
                case "shifter-theta":       s.Theta = value; break;
            }
        }

        public void UpdateShifterArray(Devices.ShifterModelKind model, string name, byte[] data)
        {
            var s = ShifterFor(model);
            if (s == null) return;
            if (name == "shifter-colors" && data.Length >= 2)
            {
                s.Led1Index = data[0];
                s.Led2Index = data[1];
            }
        }

        /// <summary>Model-aware dispatch for a parsed <c>shifter-*</c> reply. Returns
        /// false for non-shifter commands (caller falls back to UpdateFromCommand/Array).
        /// The device-type reply is relay identity used to resolve the model, so it's
        /// stored model-agnostically in <see cref="RelayShifterDeviceType"/>.</summary>
        public bool TryUpdateShifter(Devices.ShifterModelKind model, string? name, int intValue, byte[]? arrayValue)
        {
            if (name == null || !name.StartsWith("shifter-", StringComparison.Ordinal)) return false;
            if (name == "shifter-device-type")
            {
                if (arrayValue != null) RelayShifterDeviceType = (byte[])arrayValue.Clone();
                return true;
            }
            if (name == "shifter-model-name" || name == "shifter-hw-version")
            {
                if (arrayValue != null)
                {
                    var s = ParseNullTerminatedString(arrayValue);
                    if (name == "shifter-model-name") RelayShifterModelName = s;
                    else RelayShifterHwVersion = s;
                }
                return true;
            }
            UpdateShifter(model, name, intValue);
            if (arrayValue != null) UpdateShifterArray(model, name, arrayValue);
            return true;
        }

        // ===== Hub port power status (-1 = not read yet) =====
        public volatile int HubBasePower = -1;
        public volatile int HubPort1Power = -1;
        public volatile int HubPort2Power = -1;
        public volatile int HubPort3Power = -1;
        public volatile int HubPedals1Power = -1;
        public volatile int HubPedals2Power = -1;
        public volatile int HubPedals3Power = -1;

        private static int[] NewNegativeOnes(int count)
        {
            var arr = new int[count];
            for (int i = 0; i < count; i++) arr[i] = -1;
            return arr;
        }

        private static byte[][] InitColorArray(int count)
        {
            var arr = new byte[count][];
            for (int i = 0; i < count; i++)
                arr[i] = new byte[] { 0, 0, 0 };
            return arr;
        }

        // Base ambient per-LED palettes are flat arrays with a FIXED stride of
        // MaxLedsPerStrip, so an index stays valid whether the resolved base has
        // 6 or 9 LEDs per strip. Entries past the real strip length are unused.
        private static byte[][] InitBaseAmbientPalette()
            => InitColorArray(2 * Devices.BaseModelInfo.MaxLedsPerStrip);

        // Pull strip / mode / led out of a base-ambient per-LED command name.
        // Shapes: "base-ambient-led-color-strip0-mode1-led4" and
        // "base-ambient-sleep-led-color-strip1-led2" (no mode segment; mode
        // comes back as -1). Names are generated by MozaCommandDatabase, so the
        // segments are always present and single-digit.
        private static bool TryParseBaseAmbientLedName(
            string commandName, out int strip, out int mode, out int led)
        {
            strip = -1; mode = -1; led = -1;
            strip = ParseSegmentDigit(commandName, "-strip");
            mode = ParseSegmentDigit(commandName, "-mode");
            led = ParseSegmentDigit(commandName, "-led");
            return strip >= 0 && led >= 0;
        }

        // Digits immediately following the last occurrence of <marker>.
        private static int ParseSegmentDigit(string s, string marker)
        {
            int at = s.LastIndexOf(marker, System.StringComparison.Ordinal);
            if (at < 0) return -1;
            int i = at + marker.Length;
            int value = -1;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
            {
                value = (value < 0 ? 0 : value * 10) + (s[i] - '0');
                i++;
            }
            return value;
        }

        /// <summary>
        /// Flat index into the base ambient palettes for a (strip, led) pair.
        /// Returns -1 when either coordinate is out of range.
        /// </summary>
        public static int BaseAmbientPaletteIndex(int strip, int led)
        {
            if (strip < 0 || strip > 1) return -1;
            if (led < 0 || led >= Devices.BaseModelInfo.MaxLedsPerStrip) return -1;
            return strip * Devices.BaseModelInfo.MaxLedsPerStrip + led;
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

                // Live torque. Deliberately does NOT set IsBaseConnected: it
                // shares read group 43 with base-mcu-temp, which is the base
                // detection trigger (DeviceProber), and a graph/property feed
                // must not become a second detection source.
                case "base-live-torque":
                    LiveTorqueRaw = value;
                    // Peak-hold only. A spurious BE16 costs the live value one
                    // sample but would latch the session peak forever, so the
                    // plausibility bound guards the peak alone — the stored raw
                    // stays verbatim, since full scale is unmeasured (see
                    // docs/protocol/devices/wheelbase-0x13.md).
                    int torqueDev = Math.Abs(value - LiveTorqueZeroBias);
                    if (torqueDev > LiveTorquePeakDeviation
                        && torqueDev <= LiveTorqueMaxPlausibleDeviation)
                        LiveTorquePeakDeviation = torqueDev;
                    break;

                // AB9 status registers (diagnostic). Routed here only from the AB9
                // pipe's own inbound handler, which parses with busHint "ab9" —
                // these can never be fed a wheelbase reply.
                case "ab9-2b-state":        Ab9State2b = value; break;
                case "ab9-2b-state-err":    Ab9StateErr2b = value; break;
                case "ab9-2b-mcu-temp":     Ab9McuTemp2b = value; break;
                case "ab9-mode-read":       Ab9ModeReadback = value; break;

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
                case "main-get-compat-mode":   CompatMode = value; break;

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
                case "wheel-knob-mode":              WheelKnobMode = value; WheelKnobModeSupported = true; break;
                case "wheel-knob-signal-mode0":      StoreKnobSignalMode(0, value); break;
                case "wheel-knob-signal-mode1":      StoreKnobSignalMode(1, value); break;
                case "wheel-knob-signal-mode2":      StoreKnobSignalMode(2, value); break;
                case "wheel-knob-signal-mode3":      StoreKnobSignalMode(3, value); break;
                case "wheel-knob-signal-mode4":      StoreKnobSignalMode(4, value); break;
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
                case "base-ambient-sleep-breath-interval": BaseAmbientSleepBreathInterval = value; break;
                case "base-ambient-standby-interval-mode0":
                case "base-ambient-standby-interval-mode1":
                case "base-ambient-standby-interval-mode2":
                case "base-ambient-standby-interval-mode3":
                case "base-ambient-standby-interval-mode4":
                case "base-ambient-standby-interval-mode5":
                {
                    int mode = commandName[commandName.Length - 1] - '0';
                    if (mode >= 0 && mode < BaseAmbientStandbyIntervals.Length)
                        BaseAmbientStandbyIntervals[mode] = value;
                    break;
                }

                // FFB Equalizer
                case "base-equalizer1": Equalizer1 = value; break;
                case "base-equalizer2": Equalizer2 = value; break;
                case "base-equalizer3": Equalizer3 = value; break;
                case "base-equalizer4": Equalizer4 = value; break;
                case "base-equalizer5": Equalizer5 = value; break;
                case "base-equalizer6": Equalizer6 = value; break;
                case "base-equalizer7": Equalizer7 = value; break;
                case "base-equalizer8": Equalizer8 = value; break;
                case "base-equalizer9": Equalizer9 = value; break;
                case "base-equalizer10": Equalizer10 = value; break;
                case "base-road-sensitivity": RoadSensitivity = value; break;

                // FFB Curve (X input positions + Y output values)
                case "base-ffb-curve-x1": FfbCurveX1 = value; break;
                case "base-ffb-curve-x2": FfbCurveX2 = value; break;
                case "base-ffb-curve-x3": FfbCurveX3 = value; break;
                case "base-ffb-curve-x4": FfbCurveX4 = value; break;
                case "base-ffb-curve-y1": FfbCurveY1 = value; break;
                case "base-ffb-curve-y2": FfbCurveY2 = value; break;
                case "base-ffb-curve-y3": FfbCurveY3 = value; break;
                case "base-ffb-curve-y4": FfbCurveY4 = value; break;
                case "base-ffb-curve-y5": FfbCurveY5 = value; break;

                // Pedals settings
                case "pedals-throttle-dir": PedalsThrottleDir = value; break;
                case "pedals-throttle-min": PedalsThrottleMin = value; break;
                case "pedals-throttle-max": PedalsThrottleMax = value; PedalsSettingsRead = true; break;
                case "pedals-brake-dir":    PedalsBrakeDir    = value; break;
                case "pedals-brake-min":    PedalsBrakeMin    = value; break;
                case "pedals-brake-max":    PedalsBrakeMax    = value; PedalsSettingsRead = true; break;
                case "pedals-brake-angle-ratio": PedalsBrakeAngleRatio = value; break;
                case "pedals-clutch-dir":   PedalsClutchDir   = value; break;
                case "pedals-clutch-min":   PedalsClutchMin   = value; break;
                case "pedals-clutch-max":   PedalsClutchMax   = value; PedalsSettingsRead = true; break;

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
                case "handbrake-max":              HandbrakeMax              = value; HandbrakeSettingsRead = true; break;
                case "handbrake-mode":             HandbrakeMode             = value; break;
                case "handbrake-button-threshold": HandbrakeButtonThreshold  = value; break;

                // Handbrake curve
                case "handbrake-y1": HandbrakeCurve[0] = value; break;
                case "handbrake-y2": HandbrakeCurve[1] = value; break;
                case "handbrake-y3": HandbrakeCurve[2] = value; break;
                case "handbrake-y4": HandbrakeCurve[3] = value; break;
                case "handbrake-y5": HandbrakeCurve[4] = value; break;

                // Shifter settings (HGP/SGP) are model-aware — routed via
                // UpdateShifter/TryUpdateShifter, not this shared switch.

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
            //
            // Carve-out: the gate stays disarmed until the user's first LED-color
            // edit (`_ledColorEditArmed`, set by WriteLedColor). Before any edit
            // there is no pick to clobber, so the detection-time seed reads must
            // always land — otherwise telemetry that starts before the seed
            // responses arrive leaves `_data` at hardcoded defaults and the
            // swatches come up empty on a profile with no saved colors.
            if (_ledColorEditArmed
                && Devices.Led.MozaLedDeviceManager.IsLiveAnywhere()
                && IsWheelLedColorCommand(commandName))
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
                if (idx >= 0 && idx < WheelButtonMax && data.Length >= 3)
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
            // Per-LED idle palette: base-ambient-led-color-strip{s}-mode{m}-led{l}.
            // Mode is the standby-mode number; only 1 (constant) and 2 (breathing)
            // carry a palette.
            else if (commandName.StartsWith("base-ambient-led-color-strip"))
            {
                if (data.Length >= 3
                    && TryParseBaseAmbientLedName(commandName, out int strip, out int mode, out int led))
                {
                    int i = BaseAmbientPaletteIndex(strip, led);
                    if (i >= 0)
                    {
                        if (mode == 1) SetColor(BaseAmbientIdleColorsConstant[i], data);
                        else if (mode == 2) SetColor(BaseAmbientIdleColorsBreath[i], data);
                    }
                }
            }
            // Per-LED sleep palette: base-ambient-sleep-led-color-strip{s}-led{l}.
            else if (commandName.StartsWith("base-ambient-sleep-led-color-strip"))
            {
                if (data.Length >= 3
                    && TryParseBaseAmbientLedName(commandName, out int strip, out _, out int led))
                {
                    int i = BaseAmbientPaletteIndex(strip, led);
                    if (i >= 0)
                        SetColor(BaseAmbientSleepColors[i], data);
                }
            }
            // Shifter (SGP) LEDs + relayed device-type are model-aware — routed via
            // UpdateShifterArray/TryUpdateShifter, not this shared method.
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
            // ES (old-protocol) wheel identity, read from the wheel's own module
            // id 0x18 (0x17 is silent on ES). These populate the same Wheel*
            // fields a modern wheel fills from 0x17 — so an ES wheel gets a real
            // model ("ES") that drives model→GUID→profile resolution, plus correct
            // diagnostics + SDK manifest values. dev 0x13 separately fills Base*
            // with the motor identity ("R5 Black # MOT-1").
            else if (commandName == "es-wheel-model-name")
            {
                WheelModelName = ParseNullTerminatedString(data);
            }
            else if (commandName == "es-wheel-hw-version")
            {
                WheelHwVersion = ParseNullTerminatedString(data);
            }
            else if (commandName == "es-wheel-sw-version")
            {
                WheelSwVersion = ParseNullTerminatedString(data);
            }
            else if (commandName == "es-wheel-mcu-uid")
            {
                WheelMcuUid = (byte[])data.Clone();
            }
            else if (commandName == "es-wheel-device-type")
            {
                WheelDeviceType = (byte[])data.Clone();
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
            else if (commandName == "base-fw-version" || commandName == "base-fw-version-b")
            {
                // The dev-0x13 fallback only fills a still-unknown version — if
                // dev 0x12 (the PitHouse-canonical target) ever answers, its value
                // wins. On ES hardware 0x13 is the wheel module, but it shares the
                // base MCU's firmware, so the value is the same either way.
                if (commandName == "base-fw-version-b" && BaseFwVersion != 0)
                    return;
                // Reply payload is 4 version bytes in WIRE order [major, minor,
                // build, patch] (no cmd echo — group 0x04). MOZA DISPLAYS the last
                // two swapped: wire 01 02 18 09 is shown "1.2.9.24", wire 01 02 0A
                // 0A is "1.2.10.10". Pack in display/semver order (maj.min.patch.
                // build) — swap data[2]/data[3] — so the >= threshold compare in
                // BaseSupportsLfe orders pre-LFE (1.2.9.24) BELOW LFE (1.2.10.10).
                // Packing in raw wire order would misgate (0x01021809 > 0x01020A0A).
                //
                // BaseFwVersionSource records which probe won, for the diagnostics
                // dump. Both dev-0x12 request shapes (len-4 and the zero-length
                // short form) parse under the name "base-fw-version", so it names
                // the DEVICE, not the request shape; the reply payload length rides
                // along so a bundle can show whether the two ever differ.
                if (data.Length >= 4)
                {
                    BaseFwVersion = (data[0] << 24) | (data[1] << 16) | (data[3] << 8) | data[2];
                    BaseFwVersionSource = commandName == "base-fw-version"
                        ? $"base-fw-version (dev 0x12, {data.Length}B reply)"
                        : $"base-fw-version-b (dev 0x13, {data.Length}B reply)";
                }
                else if (BaseFwVersion == 0)
                {
                    // Answered, but too short to decode — materially different from
                    // "unanswered" when triaging why LFE is off. Only recorded while
                    // the version is still unknown, so a runt reply can't overwrite
                    // the note for a probe that already succeeded.
                    BaseFwVersionSource = $"{commandName} answered with {data.Length}B — too short to decode";
                }
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
            // Re-arm the LED-color seed: a hot-swapped wheel must re-read its own
            // colors into _data before the A5 gate suppresses reads again.
            _ledColorEditArmed = false;
            WheelModelName = "";
            WheelSerialNumber = "";
            WheelSwVersion = "";
            WheelHwVersion = "";
            WheelHwSubVersion = "";
            WheelSubDeviceCount = 0;
            WheelDevicePresence = 0;
            // Knob-encoder capability is per-rim and must not survive a swap: a CS Pro's
            // four answers would otherwise draw four selectors on a 2-encoder rim. The
            // mode VALUES (WheelKnobSignalModes / WheelKnobMode) are deliberately left
            // alone — the per-wheel overlay is their truth, not this mirror.
            System.Threading.Interlocked.Exchange(ref _wheelKnobSignalModeMask, 0);
            WheelKnobSignalModeSupported = false;
            WheelKnobModeSupported = false;
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
            // Base identity is NOT cleared here — see ClearBaseIdentity. This reset
            // fires on rim hot-swaps and presence misses, where the base is still
            // attached; blanking base identity there empties the SDK DeviceCatalog
            // and the prober never re-reads it.
            Last28x00Byte5 = 0;
            Last28x00ByteValid = false;
            Last28x01Byte4 = 0;
            Last28x01Byte5 = 0;
            Last28x01BytesValid = false;
            Last28xReplyTickMs = 0;
            _serialPartA = "";
            _serialPartB = "";
        }

        /// <summary>
        /// Clear wheelbase identity. Separate from <see cref="ClearWheelIdentity"/>
        /// because that one is rim-scoped: only a caller that knows the BASE went
        /// away (connection loss, deliberate disable, pipe migration) may blank this.
        /// <para>
        /// Callers MUST also call <see cref="Devices.DeviceDetectionState.ResetBase"/>
        /// — DeviceProber re-reads these fields only when the base-detect latches are
        /// clear, and <c>DeviceCatalog</c> suppresses the Motor / Wheel Base manifest
        /// entries iRacing needs whenever <see cref="BaseMcuUid"/> is empty.
        /// </para>
        /// </summary>
        public void ClearBaseIdentity()
        {
            BaseModelName = "";
            BaseSwVersion = "";
            BaseHwVersion = "";
            BaseHwSubVersion = "";
            BaseMcuUid = System.Array.Empty<byte>();
            BaseIdentity11 = System.Array.Empty<byte>();
            // BaseFwVersion is intentionally NOT cleared — see its field comment.
            // It is static-backed and survives resets so the LFE card doesn't blink
            // out; the prober overwrites it on the next base detect.
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
