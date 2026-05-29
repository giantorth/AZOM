using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using SerialDash;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Which sub-component of the wheel LED state is being invalidated. Used to tell
    /// <see cref="MozaLedDeviceManager"/> that an out-of-band write (static settings push,
    /// UI swatch click, profile apply) has clobbered the wheel's wire state for one or
    /// more LED groups so the next live <c>Display()</c> frame must re-send instead of
    /// being deduplicated against the now-stale <c>_last*</c> cache.
    /// </summary>
    [Flags]
    internal enum LedKind
    {
        None   = 0,
        Rpm    = 1 << 0,
        Button = 1 << 1,
        Knob   = 1 << 2,
        Flag   = 1 << 3,
        All    = Rpm | Button | Knob | Flag,
    }

    /// <summary>
    /// A virtual ILedDeviceManager that always reports as connected.
    /// SimHub's effects UI requires a connected device driver to enable LED configuration.
    /// This implementation captures the computed LED colors from Display() and forwards them
    /// to MOZA hardware via the plugin's serial protocol.
    /// </summary>
    internal class MozaLedDeviceManager : ILedDeviceManager
    {
        // ===== Cross-instance LED driver registry =====
        //
        // SimHub may instantiate multiple wheel device extensions (one per known wheel
        // model the user has used); each owns its own MozaLedDeviceManager. Only one is
        // ever "live" — the one whose ExpectedModelPrefix matches the currently
        // connected wheel — but the static writers (HardwareApplier, UI handlers) need
        // to invalidate the *live* one's cache without knowing which instance that is.
        // The registry plus IsLiveAnywhere() / InvalidateLiveCacheAny() give them a
        // single chokepoint that DTRT regardless of which driver is currently
        // forwarding frames.
        private static readonly List<MozaLedDeviceManager> s_instances = new List<MozaLedDeviceManager>();
        private static readonly object s_instancesLock = new object();

        // Last UTC tick at which any live (non-keepalive) wire frame went out from any
        // MozaLedDeviceManager. Used by HardwareApplier and UI handlers to gate static
        // writes — while telemetry is actively pumping, static colour writes (cmd 0x27,
        // wheel-knob-bg-color, wheel-button-color, etc.) clobber the live frame buffer
        // and the user sees the wheel revert to its stored EEPROM colours for ~1
        // keepalive interval. Skipping those writes while live is active preserves the
        // live overlay; the next ApplyWheelToHardware run after telemetry stops will
        // push the persisted static colours.
        private static long s_lastLiveSendUtcTicks;
        // Window during which static writes are suppressed after the last live send.
        // 750 ms gives headroom over the 1 s keepalive cadence — long enough that a
        // run of unchanged frames (suppressed by the change-detection cache, only the
        // keepalive fires) still keeps the gate engaged.
        internal static readonly TimeSpan LivePathActiveWindow = TimeSpan.FromMilliseconds(750);

        /// <summary>
        /// True if any live LED frame went out within <see cref="LivePathActiveWindow"/>.
        /// Static writers (HardwareApplier, UI handlers) check this before touching wheel
        /// LED registers — the live pipeline owns those registers while it's active.
        /// </summary>
        internal static bool IsLiveAnywhere()
        {
            long t = Interlocked.Read(ref s_lastLiveSendUtcTicks);
            if (t == 0) return false;
            return (DateTime.UtcNow - new DateTime(t, DateTimeKind.Utc)) <= LivePathActiveWindow;
        }

        /// <summary>
        /// Invalidate the live cache on every registered LED driver instance. Forces the
        /// next Display() frame to re-send instead of dedup'ing against a now-stale
        /// <c>_last*</c>. Called by every code path that writes to the same wheel wire
        /// registers as the live pipeline (static settings push, UI swatch handlers,
        /// profile apply).
        /// </summary>
        internal static void InvalidateLiveCacheAny(LedKind kind)
        {
            lock (s_instancesLock)
            {
                foreach (var inst in s_instances)
                    inst.InvalidateLiveCache(kind);
            }
        }

        private void RegisterInstance()
        {
            lock (s_instancesLock)
            {
                if (!s_instances.Contains(this)) s_instances.Add(this);
            }
        }

        private void UnregisterInstance()
        {
            lock (s_instancesLock)
            {
                s_instances.Remove(this);
            }
        }

        private void NoteLiveSend()
        {
            Interlocked.Exchange(ref s_lastLiveSendUtcTicks, DateTime.UtcNow.Ticks);
        }

        public MozaLedDeviceManager()
        {
            RegisterInstance();
        }

        private Color[]? _lastLeds;
        private Color[]? _lastButtons;
        private readonly Color[] _lastFlagColors = new Color[MozaDeviceConstants.FlagLedCount];
        private bool _lastFlagColorsPrimed;
        private LedDeviceState _lastState = new LedDeviceState(
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            Array.Empty<Color>(), Array.Empty<Color>(), 1.0, 1.0, 1.0, 1.0);

        private Color[]? _lastKnobs;

        // Per-component bitmask tracking (avoid redundant bitmask sends)
        private int _lastRpmBitmask = -1;
        private int _lastButtonBitmask = -1;
        private int _lastKnobBitmask = -1;

        // Keepalive timer
        private DateTime _lastSendTime = DateTime.MinValue;
        // Tracked separately so knob frames are refreshed even while RPM is updating
        // every frame (RPM activity keeps the RPM controller alive but not the knob
        // controller, which forgets its state if not periodically retold).
        private DateTime _lastKnobSendTime = DateTime.MinValue;
        // Last time SimHub fed a non-black knob frame. The keepalive runs only
        // while this is recent — once SimHub goes idle for KnobIdleTimeoutSeconds
        // the keepalive pauses, letting the wheel revert to its stored static
        // Active / per-LED background colors (wheel-knob{N}-active-color +
        // wheel-knob-bg-color{N}). Resumes the moment SimHub drives a knob
        // active again.
        private DateTime _lastKnobActivityTime = DateTime.MinValue;
        private const double KeepaliveIntervalSeconds = 1.0;
        private const double KnobIdleTimeoutSeconds = 30.0;

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
                _lastKnobSendTime = DateTime.MinValue;
                _lastKnobActivityTime = DateTime.MinValue;
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

        public void Close() { UnregisterInstance(); }

        // Allocation-free Color[] equality. SequenceEqual allocates two
        // enumerators per call; this runs on every Display() frame.
        private static bool ColorsEqual(Color[]? a, Color[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// Drop the cached "last-sent" state for one or more LED groups so the next
        /// <see cref="Display"/> frame re-sends instead of being deduplicated against
        /// the cache. Callers: anything that writes to the wheel's LED registers
        /// outside the live pipeline (HardwareApplier.WriteKnobColors / WriteKnobRingColors
        /// / WriteColorArray for LED commands, UI swatch handlers that fire
        /// WriteColorIfWheelDetected). Without this, after a static write blanks the
        /// wheel back to stock colours, the live pipeline waits up to a keepalive
        /// interval before re-asserting (which the user sees as a flicker).
        /// </summary>
        internal void InvalidateLiveCache(LedKind kind)
        {
            if ((kind & LedKind.Rpm) != 0)
            {
                _lastLeds = null;
                _lastRpmBitmask = -1;
            }
            if ((kind & LedKind.Button) != 0)
            {
                _lastButtons = null;
                _lastButtonBitmask = -1;
            }
            if ((kind & LedKind.Knob) != 0)
            {
                _lastKnobs = null;
                _lastKnobBitmask = -1;
            }
            if ((kind & LedKind.Flag) != 0)
            {
                _lastFlagColorsPrimed = false;
            }
        }

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

                var plugin = MozaPlugin.Instance;
                if (plugin == null || !plugin.Data.IsConnected)
                    return;

                // Model-match gate: only the extension whose ExpectedModelPrefix
                // matches the currently-attached wheel writes to the hardware.
                // The existing isNewWheel / isOldWheel check below only confirms
                // SOME wheel is detected, not THIS extension's wheel — so without
                // this guard a stale extension (e.g. the W17 extension after the
                // user hot-swapped to a KS) would happily push 16-RPM-shaped
                // frames at a 10-LED wheel, painting the first 10 LEDs with the
                // wrong colours and leaving stale tail-LED state on the wire.
                // Runs AFTER _lastState assignment above so SimHub's UI preview
                // for the inactive extension still reflects whatever SimHub
                // computed for it; only the hardware write is suppressed.
                if (!IsConnected())
                    return;

                bool isOldWheel = ExpectedModelPrefix == MozaDeviceConstants.OldProtocolMarker
                    && plugin.IsOldWheelDetected;
                bool isNewWheel = !isOldWheel && plugin.IsNewWheelDetected;
                if (!isNewWheel && !isOldWheel)
                    return;

                // Merge SimHub Individual-LED overrides (rawState channel) over the
                // per-segment logical channels. Physical order per device.json:
                // [telemetry 0..telemetryPhys-1][button 0..buttonPhys-1][knob 0..knobCount-1].
                //
                // Must run BEFORE the per-channel length checks below: in SimHub's
                // "Individual LEDs Exclusive" mode the logical leds/buttons/encoders
                // callbacks return Color[0], and only rawState carries effect output
                // (see LedModuleSettings.Display: `exclusive ? new Color[0] : ...`).
                // ApplyOverrides extends a short/empty dst up to `length` when any
                // raw slot in its window is non-transparent, so an empty channel
                // becomes a populated one and the per-channel processing below fires
                // off the merged array.
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
                    if (modelInfo != null && modelInfo.KnobCount > 0)
                    {
                        int knobPhysOffset = telemetryPhys + modelInfo.ButtonLedCount;
                        encoderColors = ApplyOverrides(encoderColors, rawColors, knobPhysOffset, modelInfo.KnobCount);
                    }
                }

                // After the rawState merge: if every channel is still empty there's
                // nothing to send this frame. Each per-channel block below has its
                // own length gate too, but this avoids walking through brightness /
                // keepalive paths when SimHub is genuinely idle (game not running,
                // no individual LEDs configured).
                if (ledColors.Length == 0 && buttonColors.Length == 0 && encoderColors.Length == 0)
                    return;

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

                // Per-frame brightness from SimHub's wheel LED-brightness slider.
                // Scales the outgoing RGB rather than writing the wheel's stored
                // firmware brightness — see ScaleColorsForBrightness for why.
                rpmColors = ScaleColorsForBrightness(rpmColors, rpmBrightness);

                // --- RPM LEDs ---
                bool rpmChanged = !ColorsEqual(rpmColors, _lastLeds);
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
                        // B4: read every static-colour triplet under the colour lock —
                        // UI handlers may be writing concurrently and a torn read
                        // would push a 1-frame wrong-colour to the wheel.
                        lock (plugin.Data.LedColorLock)
                        {
                            for (int i = 0; i < lim; i++)
                            {
                                if (!defaultFlags[i]) continue;
                                var c = overridden[i];
                                if (c.R != 0 || c.G != 0 || c.B != 0) continue;
                                var sc = staticColors[i];
                                overridden[i] = Color.FromArgb(sc[0], sc[1], sc[2]);
                            }
                        }
                        buttonColors = overridden;
                    }

                    // Per-frame brightness (SimHub's buttons LED-brightness
                    // slider). Applied after the default-during-telemetry
                    // override so the static fallback colours dim too.
                    buttonColors = ScaleColorsForBrightness(buttonColors, buttonsBrightness);

                    bool buttonsChanged = !ColorsEqual(buttonColors, _lastButtons);
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
                //
                // The `encoderColors.Length > 0` gate is intentionally checked AFTER
                // the rawState merge above (which extends encoderColors up to
                // KnobCount when any raw slot in the knob window is non-transparent),
                // so SimHub's "Individual LEDs Exclusive" mode — which passes Color[0]
                // on the encoders callback — still drives knob LEDs through the
                // merged array.
                if (isNewWheel && modelInfo != null && modelInfo.KnobCount > 0 && encoderColors.Length > 0)
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

                    // Per-frame brightness (SimHub's encoders/knob LED-brightness slider).
                    knobColors = ScaleColorsForBrightness(knobColors, encodersBrightness);

                    int count = Math.Min(knobColors.Length, knobCount);
                    int knobBitmask = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (knobColors[i].R > 0 || knobColors[i].G > 0 || knobColors[i].B > 0)
                            knobBitmask |= (1 << i);
                    }

                    // **B2**: drop `_lastKnobActivityTime` as the keepalive gate (see the
                    // keepalive block below) — the gate now uses _lastSendTime so any LED
                    // activity (RPM/buttons/knobs) keeps the knob keepalive armed. This
                    // avoids the case where a momentary all-black knob frame during
                    // pause/transition stales the gate and lets the wheel revert to
                    // stored static colours mid-game. _lastKnobActivityTime is kept as a
                    // diagnostic field but no longer load-bearing.
                    if (knobBitmask != 0)
                        _lastKnobActivityTime = DateTime.UtcNow;

                    // Stay engaged once we've ever sent a non-zero bitmask: keep
                    // streaming colour chunks (including all-black frames) so the
                    // firmware's buffer always reflects the current animation
                    // state. The bitmask is sticky — transitions from non-zero
                    // back to zero are suppressed in the bitmask-write block
                    // below to avoid the static-default flash described there.
                    bool knobsActive = knobBitmask != 0 || _lastKnobBitmask > 0;

                    if (knobsActive)
                    {
                        bool knobsChanged = !ColorsEqual(knobColors, _lastKnobs);
                        bool shouldSendKnobs = knobsChanged || (!limitUpdates && forceRefresh);

                        if (shouldSendKnobs)
                        {
                            _lastKnobs = (Color[])knobColors.Clone();

                            SendColorChunks(plugin, knobColors, count, "wheel-telemetry-knob-colors");

                            // Hold the bitmask sticky once telemetry is engaged:
                            // animation frames that drive every knob black would
                            // otherwise emit active_mask=0, which the firmware reads
                            // as "telemetry no longer owns the knobs" and briefly
                            // reverts the ring to the stored static EEPROM colours
                            // before the next non-zero frame arrives — the user
                            // sees this as a default-colour flash. Colour chunks
                            // still go out above so the firmware's buffer reflects
                            // the animation's black frame and renders black with
                            // the prior active_mask still in effect. The bitmask
                            // is released back to -1 only via explicit teardown
                            // (Disconnect / InvalidateLiveCache), not mid-animation.
                            if (knobBitmask != 0 && (alwaysResendBitmask || knobBitmask != _lastKnobBitmask))
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

                // Two distinct "brightness" concepts apply to these channels:
                //
                //  1. The wheel's PERSISTENT firmware brightness setting
                //     (wheel-rpm-brightness / wheel-buttons-brightness). This is
                //     stored config — written via the plugin's UI sliders and
                //     re-applied on connect through ApplyWheelToHardware /
                //     WriteKnobRingColors. It is deliberately NOT driven per-frame:
                //     SimHub passes 0 during scene transitions / no-game states /
                //     plugin-disabled idles, and writing that into EEPROM left the
                //     LEDs dark until SimHub recovered (the "randomly went to 0"
                //     symptom that motivated removing the old per-frame setting write).
                //
                //  2. SimHub's per-frame LED-brightness sliders (rpmBrightness /
                //     buttonsBrightness / encodersBrightness Display params). These
                //     ARE honoured, but as RGB scaling on the outgoing colour frame
                //     (applied at each channel's send site above via
                //     ScaleColorsForBrightness) — the same approach the base-LED
                //     pipeline uses. A transient 0 just sends a black frame; nothing
                //     persists, so the stuck-dark bug can't recur.

                // --- Keepalive: resend last state periodically for ES wheel compat ---
                if (anySent)
                {
                    _lastSendTime = DateTime.UtcNow;
                    // Mark the live-path active for the cross-instance gate that
                    // suppresses static writes (HardwareApplier, UI handlers).
                    NoteLiveSend();
                }
                else if (_lastLeds != null)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastSendTime).TotalSeconds >= KeepaliveIntervalSeconds)
                    {
                        _lastSendTime = now;
                        ResendLastState(plugin, isNewWheel, isOldWheel);
                        // Keepalive is also a live send for the gate's purposes — the
                        // wheel's render path can't distinguish change-driven from
                        // keepalive frames, and a static write between them would
                        // visibly flicker just as much.
                        NoteLiveSend();
                    }
                }

                // Independent knob keepalive: knob colors rarely change, so the
                // change-detection path above stops re-sending them once SimHub
                // stabilizes. The shared keepalive above only fires when nothing
                // else moved — under live RPM that never happens. Refresh knob
                // frame on its own cadence so the knob LED controller doesn't
                // forget the state.
                //
                // **B2**: keepalive is now gated on `_lastSendTime` (any LED activity
                // within KnobIdleTimeoutSeconds) instead of `_lastKnobActivityTime`
                // (last non-black knob frame only). The old gate stopped firing if
                // SimHub sent a few all-black knob frames during a pause/transition,
                // even though RPM/buttons were still flowing — the wheel would then
                // revert to stored static knob colours, which the user sees as flicker.
                if (isNewWheel && _lastKnobs != null && modelInfo?.KnobCount > 0)
                {
                    var now = DateTime.UtcNow;
                    bool dataFlowing = (now - _lastSendTime).TotalSeconds <= KnobIdleTimeoutSeconds;
                    if (dataFlowing && (now - _lastKnobSendTime).TotalSeconds >= KeepaliveIntervalSeconds)
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
                        NoteLiveSend();
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
        /// <summary>
        /// Scale a per-frame colour array by SimHub's 0..1 LED-brightness factor
        /// (the wheel's SimHub LED-brightness sliders feed this via the
        /// rpmBrightness / buttonsBrightness / encodersBrightness Display params).
        /// Returns the source array unchanged when brightness is full (1.0 — the
        /// untouched-slider default and hot path, so no allocation), otherwise a
        /// new scaled array (SimHub's source array is never mutated).
        ///
        /// This is the per-frame RGB-scaling approach the base-LED pipeline uses
        /// (MozaBaseLedDeviceManager.ProcessStrip). It deliberately does NOT touch
        /// the wheel's persistent firmware brightness setting (wheel-rpm-brightness):
        /// SimHub passes 0 during scene transitions / no-game states, and writing
        /// that into EEPROM left the LEDs stuck dark until SimHub recovered. A
        /// transient 0 here just produces a black frame.
        ///
        /// Because the scaled result feeds both the change-detection compare
        /// (ColorsEqual against the last scaled frame) and the bitmask, dragging
        /// the slider re-sends correctly and an LED scaled to black drops out of
        /// the bitmask — matching the base pipeline's behaviour exactly.
        /// </summary>
        private static Color[] ScaleColorsForBrightness(Color[] colors, double brightness)
        {
            if (brightness < 0) brightness = 0;
            if (brightness > 1) brightness = 1;
            if (brightness >= 1.0) return colors;

            var result = new Color[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                var c = colors[i];
                byte r = (byte)Math.Round(c.R * brightness);
                byte g = (byte)Math.Round(c.G * brightness);
                byte b = (byte)Math.Round(c.B * brightness);
                result[i] = Color.FromArgb(r, g, b);
            }
            return result;
        }

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
#if MOZA_RAW_LED_DIAG
        private string? _lastRawDiagKey;
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
#endif

        // Merge physical-layer Individual-LED overrides onto a logical-channel array.
        // A raw slot with Alpha != 0 replaces the corresponding dst slot.
        //
        // rawColors.Length is SimHub's max-end-position across Individual-LED
        // entries, not the declared physical LED count — clip to the available
        // window so short rawColors still apply overrides to the slots it covers.
        //
        // **Critical invariant for the bitmask + chunk encoder downstream:** the
        // returned array MUST have at least `length` slots, and EVERY slot in
        // [0, length) must be initialised to a deterministic value (the SimHub
        // logical-channel value from `dst` if present, otherwise Color.Black). The
        // bitmask loop in Display() iterates `i < count = Min(buttons.Length, ButtonLedCount)`,
        // so a tail slot left as default Color.Empty looks like "off" (R=G=B=0) on the
        // wire but came from uninitialised memory. The chunk encoder writes [idx, 0, 0, 0]
        // for those slots, which is correct *if* the user actually wanted them off —
        // but if their Individual-LED effect covers a window shorter than the physical
        // count (very common when an effect was authored for a lower-button-count wheel
        // like CS Pro and then loaded on KS Pro), they expected those tail slots to
        // either retain the prior frame's colours or render the effect's "off" output.
        // The current implementation silently drops them; we make the off explicit so
        // (a) the bitmask is deterministic and (b) the chunk encoder always writes a
        // full physical-count frame, never a truncated one that leaves stale LED state
        // on the wheel.
        internal static Color[] ApplyOverrides(Color[] dst, Color[] rawColors, int offset, int length)
        {
            if (length <= 0 || offset >= rawColors.Length) return dst;
            int available = Math.Min(length, rawColors.Length - offset);

            bool anyOverride = false;
            for (int i = 0; i < available; i++)
            {
                if (rawColors[offset + i].A != 0) { anyOverride = true; break; }
            }
            // Honour "nothing is driving this channel right now" — don't manufacture an
            // empty frame and wake the wheel into thinking telemetry started.
            if (!anyOverride) return dst;

            int outLen = Math.Max(dst.Length, length);
            var merged = new Color[outLen];
            Array.Copy(dst, merged, Math.Min(dst.Length, outLen));
            // **A1 fix**: fill the tail slots [dst.Length, length) with explicit black.
            // In exclusive mode dst is Color[0], so without this step the bitmask loop
            // in Display() sees default Color.Empty (alpha=0, R=G=B=0) for every slot
            // past `available` — same wire output (off) but produced from uninitialised
            // memory rather than a deliberate choice. The chunk encoder iterates `count
            // = Min(colors.Length, ButtonLedCount)`, so a short return here makes the
            // wheel never receive entries for the tail LEDs; if a previous frame had
            // lit them, the wheel retains that stale state until something drives them
            // explicitly. Color.Black writes [idx, 0, 0, 0] in the chunk (same as
            // Color.Empty would) but clears any prior live state in the wheel's frame
            // buffer.
            for (int i = dst.Length; i < length; i++)
                merged[i] = Color.Black;
            for (int i = 0; i < available; i++)
            {
                var r = rawColors[offset + i];
                if (r.A != 0) merged[i] = Color.FromArgb(r.R, r.G, r.B);
            }
            return merged;
        }
    }
}
