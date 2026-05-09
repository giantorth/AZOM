using System;
using System.Drawing;
using System.Linq;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using SerialDash;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// A virtual ILedDeviceManager that always reports as connected.
    /// SimHub's effects UI requires a connected device driver to enable LED configuration.
    /// This implementation captures the computed LED colors from Display() and forwards them
    /// to MOZA hardware via the plugin's serial protocol.
    /// </summary>
    internal class MozaLedDeviceManager : ILedDeviceManager
    {
        private Color[]? _lastLeds;
        private Color[]? _lastButtons;
        private readonly Color[] _lastFlagColors = new Color[MozaDeviceConstants.FlagLedCount];
        private bool _lastFlagColorsPrimed;
        private LedDeviceState _lastState = new LedDeviceState(
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            Array.Empty<Color>(), Array.Empty<Color>(), 1.0, 1.0, 1.0, 1.0);
        private double _lastBrightness = -1;
        private double _lastButtonsBrightness = -1;

        private Color[]? _lastKnobs;

        // Per-component bitmask tracking (avoid redundant bitmask sends)
        private int _lastRpmBitmask = -1;
        private int _lastButtonBitmask = -1;
        private int _lastKnobBitmask = -1;

        // Diagnostic: log rawColors shape once per distinct (length, non-empty pattern)
        private string? _lastRawDiagKey;

        // Keepalive timer
        private DateTime _lastSendTime = DateTime.MinValue;
        // Tracked separately so knob frames are refreshed even while RPM is updating
        // every frame (RPM activity keeps the RPM controller alive but not the knob
        // controller, which forgets its state if not periodically retold).
        private DateTime _lastKnobSendTime = DateTime.MinValue;
        private const double KeepaliveIntervalSeconds = 1.0;

        // ES wheel wake-up
        private bool _ledsAwake;

        /// <summary>
        /// Expected wheel model prefix for this device instance.
        /// Null = unknown (don't connect). Empty string = generic fallback (any wheel).
        /// Specific prefix (e.g. "W17") = only connect when that model is detected.
        /// </summary>
        public string? ExpectedModelPrefix { get; set; }

        public LedModuleSettings LedModuleSettings { get; set; } = null!;

        public LedDeviceState LastState => _lastState;

        private bool _wasConnected;

        public event EventHandler? BeforeDisplay;
        public event EventHandler? AfterDisplay;
        public event EventHandler? OnConnect;
#pragma warning disable CS0067 // Required by ILedDeviceManager interface
        public event EventHandler? OnError;
#pragma warning restore CS0067
        public event EventHandler? OnDisconnect;

        /// <summary>
        /// Check current detection state and fire OnConnect/OnDisconnect if it changed.
        /// Called from device extension's DataUpdate() every frame.
        /// </summary>
        internal void UpdateConnectionState()
        {
            bool connected = IsConnected();
            if (connected == _wasConnected) return;
            _wasConnected = connected;

            if (connected)
            {
                OnConnect?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // Reset cached state so everything re-initializes on reconnect
                _lastLeds = null;
                _lastButtons = null;
                _lastKnobs = null;
                _lastFlagColorsPrimed = false;
                _lastRpmBitmask = -1;
                _lastButtonBitmask = -1;
                _lastKnobBitmask = -1;
                _lastBrightness = -1;
                _lastButtonsBrightness = -1;
                _lastKnobSendTime = DateTime.MinValue;
                _ledsAwake = false;
                OnDisconnect?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool IsConnected()
        {
            if (ExpectedModelPrefix == null)
                return false;

            var p = MozaPlugin.Instance;
            if (p == null)
                return false;

            // Old-protocol device — only match old-protocol wheels
            if (ExpectedModelPrefix == MozaDeviceConstants.OldProtocolMarker)
                return p.IsOldWheelDetected;

            // All other prefixes require a new-protocol wheel
            if (!p.IsNewWheelDetected)
                return false;

            // Empty prefix = generic fallback, matches any new-protocol wheel
            // UNLESS a model-specific device extension is active for this wheel
            if (ExpectedModelPrefix.Length == 0)
                return !p.IsModelSpecificExtensionActive(p.Data.WheelModelName);

            // Specific model — match against detected wheel's firmware model name
            var modelName = p.Data.WheelModelName;
            if (string.IsNullOrEmpty(modelName))
                return false;

            return modelName.StartsWith(ExpectedModelPrefix, StringComparison.OrdinalIgnoreCase);
        }

        public string GetSerialNumber() => "MOZA-VIRTUAL";

        public string GetFirmwareVersion() =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        public object GetDriverInstance() => this;

        public void Close() { }

        public void ResetDetection() { }

        public void SerialPortCanBeScanned(object sender, SerialDashController.ScanArgs e) { }

        public IPhysicalMapper GetPhysicalMapper() => new NeutralLedsMapper();

        public ILedDriverBase? GetLedDriver() => null;

        public void Display(
            Func<Color[]> leds,
            Func<Color[]> buttons,
            Func<Color[]> encoders,
            Func<Color[]> matrix,
            Func<Color[]> rawState,
            bool forceRefresh,
            Func<object>? extraData = null,
            double rpmBrightness = 1.0,
            double buttonsBrightness = 1.0,
            double encodersBrightness = 1.0,
            double matrixBrightness = 1.0)
        {
            BeforeDisplay?.Invoke(this, EventArgs.Empty);

            try
            {
                var ledColors = leds?.Invoke() ?? Array.Empty<Color>();
                var buttonColors = buttons?.Invoke() ?? Array.Empty<Color>();
                var encoderColors = encoders?.Invoke() ?? Array.Empty<Color>();
                var matrixColors = matrix?.Invoke() ?? Array.Empty<Color>();
                var rawColors = rawState?.Invoke() ?? Array.Empty<Color>();

                _lastState = new LedDeviceState(
                    ledColors, buttonColors, encoderColors, matrixColors, rawColors,
                    rpmBrightness, buttonsBrightness, encodersBrightness, matrixBrightness);

                if (ledColors.Length == 0)
                    return;

                var plugin = MozaPlugin.Instance;
                if (plugin == null || !plugin.Data.IsConnected)
                    return;

                bool isOldWheel = ExpectedModelPrefix == MozaDeviceConstants.OldProtocolMarker
                    && plugin.IsOldWheelDetected;
                bool isNewWheel = !isOldWheel && plugin.IsNewWheelDetected;
                if (!isNewWheel && !isOldWheel)
                    return;

                // Merge SimHub Individual-LED overrides (rawState channel) over the
                // per-segment logical channels. Physical order per device.json:
                // [telemetry 0..telemetryPhys-1][button 0..buttonPhys-1].
                var modelInfo = plugin.WheelModelInfo;
                if (rawColors.Length > 0)
                {
                    // LogRawDiagnostic(rawColors, ledColors.Length, buttonColors.Length);

                    int telemetryPhys = modelInfo != null
                        ? modelInfo.RpmLedCount + (modelInfo.HasFlagLeds ? MozaDeviceConstants.FlagLedCount : 0)
                        : ledColors.Length;
                    int buttonPhys = modelInfo?.ButtonLedCount ?? buttonColors.Length;
                    ledColors = ApplyOverrides(ledColors, rawColors, 0, telemetryPhys);
                    buttonColors = ApplyOverrides(buttonColors, rawColors, telemetryPhys, buttonPhys);
                }

                // ES wheel wake-up: flash all LEDs on then off to enter telemetry mode
                if (!_ledsAwake && isOldWheel)
                {
                    _ledsAwake = true;
                    plugin.DeviceManager.WriteSetting("wheel-old-send-telemetry", 0x3FF);
                    plugin.DeviceManager.WriteSetting("wheel-old-send-telemetry", 0);
                    MozaLog.Debug("[Moza] ES wheel LED wake-up sent");
                }

                bool limitUpdates = plugin.Settings.LimitWheelUpdates;
                bool alwaysResendBitmask = plugin.Settings.AlwaysResendBitmask;
                bool anySent = false;

                // Wheels with flag LEDs receive a single (rpmN + 6)-LED telemetry
                // sequence from SimHub laid out as [flag 1..3][rpm 1..N][flag 4..6].
                // Pre-detection (modelInfo null) we fall back to pure RPM handling.
                bool hasFlagLeds = isNewWheel && modelInfo?.HasFlagLeds == true;
                int rpmN = modelInfo?.RpmLedCount ?? MozaDeviceConstants.RpmLedCount;
                int flagLeft = hasFlagLeds ? 3 : 0;

                Color[] rpmColors;
                if (hasFlagLeds && ledColors.Length >= flagLeft + rpmN)
                {
                    rpmColors = new Color[rpmN];
                    Array.Copy(ledColors, flagLeft, rpmColors, 0, rpmN);
                }
                else
                {
                    rpmColors = ledColors;
                }

                // --- RPM LEDs ---
                bool rpmChanged = _lastLeds == null || !rpmColors.SequenceEqual(_lastLeds);
                bool shouldSendRpm = rpmChanged || (!limitUpdates && forceRefresh);

                if (shouldSendRpm)
                {
                    _lastLeds = (Color[])rpmColors.Clone();

                    int count = Math.Min(rpmColors.Length, rpmN);

                    // Build bitmask: bit i set if LED i has any color
                    int bitmask = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (rpmColors[i].R > 0 || rpmColors[i].G > 0 || rpmColors[i].B > 0)
                            bitmask |= (1 << i);
                    }

                    if (isNewWheel)
                    {
                        SendColorChunks(plugin, rpmColors, count, "wheel-telemetry-rpm-colors");

                        if (alwaysResendBitmask || bitmask != _lastRpmBitmask)
                        {
                            _lastRpmBitmask = bitmask;
                            plugin.DeviceManager.WriteArray("wheel-send-rpm-telemetry",
                                BuildRpmBitmaskBytes(bitmask, count));
                        }
                        anySent = true;
                    }
                    else if (isOldWheel)
                    {
                        // ES wheels: can't set colors per-frame, just send bitmask
                        if (alwaysResendBitmask || bitmask != _lastRpmBitmask)
                        {
                            _lastRpmBitmask = bitmask;
                            plugin.DeviceManager.WriteSetting("wheel-old-send-telemetry", bitmask);
                            anySent = true;
                        }
                    }
                }

                // --- Flag LEDs ---
                // Wheels with 3/N/3 flag layout: SimHub indices 0..2 drive flag 1..3,
                // indices rpmN+3..rpmN+5 drive flag 4..6. Per-LED static color writes
                // with change detection keep wire traffic low.
                // Flag LEDs live on the Meter sub-device (device 0x14) per RS21 DB;
                // gate on dash detection so writes only fire once that sub-device answers.
                if (hasFlagLeds && plugin.IsDashDetected && ledColors.Length >= flagLeft + rpmN + 3)
                {
                    for (int i = 0; i < MozaDeviceConstants.FlagLedCount; i++)
                    {
                        int srcIdx = i < 3 ? i : rpmN + i;  // 0,1,2, rpmN+3, rpmN+4, rpmN+5
                        var c = ledColors[srcIdx];
                        bool changed = !_lastFlagColorsPrimed || _lastFlagColors[i] != c;
                        if (changed || (!limitUpdates && forceRefresh))
                        {
                            _lastFlagColors[i] = c;
                            plugin.DeviceManager.WriteArray(
                                $"dash-flag-color{i + 1}",
                                new byte[] { c.R, c.G, c.B });
                            anySent = true;
                        }
                    }
                    _lastFlagColorsPrimed = true;
                }

                // --- Button LEDs (new-protocol wheels only) ---
                // Gate on WheelModelInfo being known: sending with the fallback mapping
                // before the model-name response arrives would push wrong-index state that
                // the cache then treats as current, leaving the wheel misaligned until a
                // power cycle or forced color change.
                if (isNewWheel && buttonColors.Length > 0 && modelInfo != null)
                {
                    // "Default during telemetry" override: per-button flags (Data.WheelButtonDefaultDuringTelemetry)
                    // replace 'off' (0,0,0) in the incoming SimHub frame with the button's configured static color.
                    // Runs unconditionally while SimHub is feeding button colors — the frame itself IS the telemetry
                    // signal, so no extra "is telemetry running" gate is needed.
                    var defaultFlags = plugin.Data.WheelButtonDefaultDuringTelemetry;
                    var staticColors = plugin.Data.WheelButtonColors;
                    bool anyOverride = false;
                    for (int i = 0; i < defaultFlags.Length; i++)
                    {
                        if (defaultFlags[i]) { anyOverride = true; break; }
                    }
                    if (anyOverride)
                    {
                        var overridden = (Color[])buttonColors.Clone();
                        int lim = Math.Min(overridden.Length, Math.Min(defaultFlags.Length, staticColors.Length));
                        for (int i = 0; i < lim; i++)
                        {
                            if (!defaultFlags[i]) continue;
                            var c = overridden[i];
                            if (c.R != 0 || c.G != 0 || c.B != 0) continue;
                            var sc = staticColors[i];
                            overridden[i] = Color.FromArgb(sc[0], sc[1], sc[2]);
                        }
                        buttonColors = overridden;
                    }

                    bool buttonsChanged = _lastButtons == null || !buttonColors.SequenceEqual(_lastButtons);
                    bool shouldSendButtons = buttonsChanged || (!limitUpdates && forceRefresh);

                    if (shouldSendButtons)
                    {
                        _lastButtons = (Color[])buttonColors.Clone();

                        int buttonCount = Math.Min(buttonColors.Length, modelInfo.ButtonLedCount);
                        var buttonMap = modelInfo.ButtonLedMap;

                        int buttonBitmask = 0;
                        for (int i = 0; i < buttonCount; i++)
                        {
                            int protocolIndex = buttonMap != null ? buttonMap[i] : i;
                            if (buttonColors[i].R > 0 || buttonColors[i].G > 0 || buttonColors[i].B > 0)
                                buttonBitmask |= (1 << protocolIndex);
                        }

                        SendColorChunks(plugin, buttonColors, buttonCount, "wheel-telemetry-button-colors", buttonMap);

                        if (alwaysResendBitmask || buttonBitmask != _lastButtonBitmask)
                        {
                            _lastButtonBitmask = buttonBitmask;
                            // 8-byte form matching PitHouse: active_mask(u32 LE) + window_mask(u32 LE).
                            // PitHouse sends window=0 for buttons.
                            plugin.DeviceManager.WriteArray("wheel-send-buttons-telemetry",
                                BuildKnobBitmaskBytes(buttonBitmask, 0));
                        }
                        anySent = true;
                    }
                }

                // --- Knob indicator LEDs (new-protocol wheels with knob ring LEDs) ---
                // SimHub feeds knob colors via the Extra/encoders channel (SourceRole 3).
                // Only send knob frames when at least one knob has color — sending the
                // window mask with all-black active wakes up the knob LED controller.
                if (isNewWheel && encoderColors.Length > 0 && modelInfo != null && modelInfo.KnobCount > 0)
                {
                    int knobCount = modelInfo.KnobCount;
                    Color[] knobColors;
                    if (encoderColors.Length >= knobCount)
                    {
                        knobColors = new Color[knobCount];
                        Array.Copy(encoderColors, 0, knobColors, 0, knobCount);
                    }
                    else
                    {
                        knobColors = encoderColors;
                    }

                    // Apply rawState overrides for knob region
                    // Physical layout: [telemetry][buttons][knobs]
                    if (rawColors.Length > 0)
                    {
                        int knobPhysOffset = (modelInfo.RpmLedCount + (modelInfo.HasFlagLeds ? MozaDeviceConstants.FlagLedCount : 0))
                                           + modelInfo.ButtonLedCount;
                        knobColors = ApplyOverrides(knobColors, rawColors, knobPhysOffset, knobCount);
                    }

                    int count = Math.Min(knobColors.Length, knobCount);
                    int knobBitmask = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (knobColors[i].R > 0 || knobColors[i].G > 0 || knobColors[i].B > 0)
                            knobBitmask |= (1 << i);
                    }

                    // Skip sending when all knobs are black and we haven't previously
                    // sent a non-zero bitmask — avoids waking the knob LED controller.
                    bool knobsActive = knobBitmask != 0 || _lastKnobBitmask > 0;

                    if (knobsActive)
                    {
                        bool knobsChanged = _lastKnobs == null || !knobColors.SequenceEqual(_lastKnobs);
                        bool shouldSendKnobs = knobsChanged || (!limitUpdates && forceRefresh);

                        if (shouldSendKnobs)
                        {
                            _lastKnobs = (Color[])knobColors.Clone();

                            SendColorChunks(plugin, knobColors, count, "wheel-telemetry-knob-colors");

                            if (alwaysResendBitmask || knobBitmask != _lastKnobBitmask)
                            {
                                _lastKnobBitmask = knobBitmask;
                                int windowMask = (1 << knobCount) - 1;
                                plugin.DeviceManager.WriteArray("wheel-send-knob-telemetry", BuildKnobBitmaskBytes(knobBitmask, windowMask));
                            }
                            _lastKnobSendTime = DateTime.UtcNow;
                            anySent = true;
                        }
                    }
                }

                // --- Brightness (existing change detection) ---
                if (rpmBrightness != _lastBrightness)
                {
                    _lastBrightness = rpmBrightness;
                    if (isNewWheel)
                        plugin.DeviceManager.WriteSetting("wheel-rpm-brightness", (int)(rpmBrightness * 100));
                    else if (isOldWheel)
                        plugin.DeviceManager.WriteSetting("wheel-old-rpm-brightness", (int)(rpmBrightness * 15));
                    anySent = true;
                }

                // Flag brightness was previously slaved to SimHub's per-frame rpmBrightness
                // here. Removed: SimHub passes 0 during scene transitions / no-game states,
                // which would blank the flag LEDs (and was the source of the dashboard
                // "incorrectly sending 0 brightness" bug). SimHub brightness now applies to
                // wheel RPM + button LEDs only; flag LEDs use the stored config written via
                // ApplySavedDashSettings on connect / plugin UI slider.

                if (isNewWheel && buttonsBrightness != _lastButtonsBrightness)
                {
                    _lastButtonsBrightness = buttonsBrightness;
                    plugin.DeviceManager.WriteSetting("wheel-buttons-brightness", (int)(buttonsBrightness * 100));
                    anySent = true;
                }

                // --- Keepalive: resend last state periodically for ES wheel compat ---
                if (anySent)
                {
                    _lastSendTime = DateTime.UtcNow;
                }
                else if (_lastLeds != null)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastSendTime).TotalSeconds >= KeepaliveIntervalSeconds)
                    {
                        _lastSendTime = now;
                        ResendLastState(plugin, isNewWheel, isOldWheel);
                    }
                }

                // Independent knob keepalive: knob colors rarely change, so the
                // change-detection path above stops re-sending them once SimHub
                // stabilizes. The shared keepalive above only fires when nothing
                // else moved — under live RPM that never happens. Refresh knob
                // frame on its own cadence so the knob LED controller doesn't
                // forget the state.
                if (isNewWheel && _lastKnobs != null && modelInfo?.KnobCount > 0)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastKnobSendTime).TotalSeconds >= KeepaliveIntervalSeconds)
                    {
                        _lastKnobSendTime = now;
                        int knobCount = Math.Min(_lastKnobs.Length, modelInfo.KnobCount);
                        SendColorChunks(plugin, _lastKnobs, knobCount, "wheel-telemetry-knob-colors");
                        if (_lastKnobBitmask >= 0)
                        {
                            int windowMask = (1 << modelInfo.KnobCount) - 1;
                            plugin.DeviceManager.WriteArray("wheel-send-knob-telemetry",
                                BuildKnobBitmaskBytes(_lastKnobBitmask, windowMask));
                        }
                    }
                }
            }
            finally
            {
                AfterDisplay?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Resend the last known LED state as a keepalive.
        /// </summary>
        private void ResendLastState(MozaPlugin plugin, bool isNewWheel, bool isOldWheel)
        {
            if (_lastLeds == null) return;

            var modelInfo = plugin.WheelModelInfo;
            int rpmN = modelInfo?.RpmLedCount ?? MozaDeviceConstants.RpmLedCount;
            int count = Math.Min(_lastLeds.Length, rpmN);

            if (isNewWheel)
            {
                SendColorChunks(plugin, _lastLeds, count, "wheel-telemetry-rpm-colors");
                if (_lastRpmBitmask >= 0)
                    plugin.DeviceManager.WriteArray("wheel-send-rpm-telemetry",
                        BuildRpmBitmaskBytes(_lastRpmBitmask, count));

                if (modelInfo?.HasFlagLeds == true && plugin.IsDashDetected && _lastFlagColorsPrimed)
                {
                    for (int i = 0; i < MozaDeviceConstants.FlagLedCount; i++)
                    {
                        var c = _lastFlagColors[i];
                        plugin.DeviceManager.WriteArray(
                            $"dash-flag-color{i + 1}",
                            new byte[] { c.R, c.G, c.B });
                    }
                }

                if (_lastKnobs != null && modelInfo?.KnobCount > 0)
                {
                    int knobCount = Math.Min(_lastKnobs.Length, modelInfo.KnobCount);
                    SendColorChunks(plugin, _lastKnobs, knobCount, "wheel-telemetry-knob-colors");
                    if (_lastKnobBitmask >= 0)
                    {
                        int windowMask = (1 << modelInfo.KnobCount) - 1;
                        plugin.DeviceManager.WriteArray("wheel-send-knob-telemetry",
                            BuildKnobBitmaskBytes(_lastKnobBitmask, windowMask));
                    }
                }
            }
            else if (isOldWheel)
            {
                if (_lastRpmBitmask >= 0)
                    plugin.DeviceManager.WriteSetting("wheel-old-send-telemetry", _lastRpmBitmask);
            }
        }

        /// <summary>
        /// Build RPM active-LED bitmask payload sized for the wheel's LED count.
        /// 16 or fewer LEDs → 2 bytes (legacy wire format). 17+ LEDs → 4 bytes.
        /// KS Pro (18 LEDs) requires the extended form to light bits 16-17.
        /// </summary>
        internal static byte[] BuildRpmBitmaskBytes(int bitmask, int ledCount)
        {
            if (ledCount > 16)
            {
                return new byte[] {
                    (byte)(bitmask & 0xFF),
                    (byte)((bitmask >> 8) & 0xFF),
                    (byte)((bitmask >> 16) & 0xFF),
                    (byte)((bitmask >> 24) & 0xFF)
                };
            }
            return new byte[] {
                (byte)(bitmask & 0xFF),
                (byte)((bitmask >> 8) & 0xFF)
            };
        }

        /// <summary>
        /// Build knob indicator bitmask payload — always 8-byte form:
        /// active_mask(u32 LE) + window_mask(u32 LE).
        /// </summary>
        internal static byte[] BuildKnobBitmaskBytes(int activeMask, int windowMask)
        {
            return new byte[] {
                (byte)(activeMask & 0xFF),
                (byte)((activeMask >> 8) & 0xFF),
                (byte)((activeMask >> 16) & 0xFF),
                (byte)((activeMask >> 24) & 0xFF),
                (byte)(windowMask & 0xFF),
                (byte)((windowMask >> 8) & 0xFF),
                (byte)((windowMask >> 16) & 0xFF),
                (byte)((windowMask >> 24) & 0xFF),
            };
        }

        /// <summary>
        /// Pack colors into 4-byte-per-LED format and send in 20-byte chunks.
        /// When <paramref name="indexMap"/> is provided, each entry maps the source array
        /// position to the protocol LED index (for non-contiguous button layouts).
        /// </summary>
        internal static void SendColorChunks(MozaPlugin plugin, Color[] colors, int count,
            string command, int[]? indexMap = null)
        {
            int dataLen = count * 4;
            // Round up to next multiple of 20 for chunk alignment
            int bufferLen = ((dataLen + 19) / 20) * 20;
            var colorData = new byte[bufferLen];

            for (int i = 0; i < count; i++)
            {
                int offset = i * 4;
                colorData[offset] = (byte)(indexMap != null ? indexMap[i] : i);
                colorData[offset + 1] = colors[i].R;
                colorData[offset + 2] = colors[i].G;
                colorData[offset + 3] = colors[i].B;
            }

            // Fill padding entries with unused index 0xFF so firmware doesn't
            // interpret zero-padding as "set LED 0 to black" (causes button 0 flicker)
            for (int pos = dataLen; pos < bufferLen; pos += 4)
                colorData[pos] = 0xFF;

            // Reuse one 20-byte chunk buffer across the loop. WriteArray's
            // BuildWriteMessage copies `payload` into its own list before this
            // method returns, so overwriting `chunk` on the next iteration is
            // safe and saves N-1 allocations per LED update at 60Hz.
            var chunk = new byte[20];
            for (int pos = 0; pos < bufferLen; pos += 20)
            {
                Array.Copy(colorData, pos, chunk, 0, 20);
                plugin.DeviceManager.WriteArray(command, chunk);
            }
        }

        // Diagnostic: log rawColors length and per-slot state once per distinct pattern.
        // Helps verify SimHub's Individual-LEDs output shape (physical-indexed vs other).
        private void LogRawDiagnostic(Color[] rawColors, int ledsLen, int buttonsLen)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"rawLen={rawColors.Length} leds={ledsLen} buttons={buttonsLen} nonEmpty=[");
            for (int i = 0; i < rawColors.Length; i++)
            {
                var c = rawColors[i];
                if (c.A != 0 || c.R != 0 || c.G != 0 || c.B != 0)
                    sb.Append($"{i}:A{c.A}R{c.R}G{c.G}B{c.B} ");
            }
            sb.Append(']');
            string key = sb.ToString();
            if (key == _lastRawDiagKey) return;
            _lastRawDiagKey = key;
            // Very chatty when animation is running
            MozaLog.Debug($"[Moza] IndividualLEDs diag {key}");
        }

        // Merge physical-layer Individual-LED overrides onto a logical-channel array.
        // A raw slot with Alpha != 0 replaces the corresponding dst slot.
        // rawColors.Length is SimHub's max-end-position across Individual-LED
        // entries, not the declared physical LED count — clip to the available
        // window so short rawColors still apply overrides to the slots it covers.
        internal static Color[] ApplyOverrides(Color[] dst, Color[] rawColors, int offset, int length)
        {
            if (length <= 0 || offset >= rawColors.Length) return dst;
            int available = Math.Min(length, rawColors.Length - offset);

            bool anyOverride = false;
            for (int i = 0; i < available; i++)
            {
                if (rawColors[offset + i].A != 0) { anyOverride = true; break; }
            }
            if (!anyOverride) return dst;

            int outLen = Math.Max(dst.Length, length);
            var merged = new Color[outLen];
            Array.Copy(dst, merged, Math.Min(dst.Length, outLen));
            for (int i = 0; i < available; i++)
            {
                var r = rawColors[offset + i];
                if (r.A != 0) merged[i] = Color.FromArgb(r.R, r.G, r.B);
            }
            return merged;
        }
    }
}
