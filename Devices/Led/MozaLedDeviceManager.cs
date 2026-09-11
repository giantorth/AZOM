using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using MozaPlugin.Protocol;
using SerialDash;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace MozaPlugin.Devices.Led
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

        // Catalog-negotiation LED throttle. During cold-start / post-switch catalog
        // (re)advertisement the half-duplex 115200 link is contended (our tier-def
        // burst + the wheel's inbound catalog chunks); a ~60 Hz LED stream on top
        // saturates it and drops inbound catalog chunks (radar/track-map dashes →
        // missing channels / stuck gear). While the wheel is negotiating we cap LED
        // writes to one per this interval so chunks get through; racing LEDs resume
        // full-rate the instant negotiation completes (a few seconds).
        private const int CatalogNegotiationLedThrottleMs = 150;
        private int _lastNegotiationLedTickMs;

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

        // "dash-flag-color1".."dash-flag-color6" — built once; these were
        // interpolated per flag LED per frame on the 60 Hz Display() path.
        private static readonly string[] s_dashFlagColorCommands = BuildDashFlagColorCommands();

        private static string[] BuildDashFlagColorCommands()
        {
            var names = new string[MozaDeviceConstants.FlagLedCount];
            for (int i = 0; i < names.Length; i++) names[i] = "dash-flag-color" + (i + 1);
            return names;
        }

        private static string DashFlagColorCommand(int i)
            => i < s_dashFlagColorCommands.Length ? s_dashFlagColorCommands[i] : "dash-flag-color" + (i + 1);

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
        private LedDeviceState _lastState = SimHubLedCompat.CreateState(
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            1.0, 1.0, 1.0, 1.0);

        private Color[]? _lastKnobs;

        // Static-hold restore tracking (WheelKnobStaticTimeoutMs). _lastKnobRawColors
        // is the last incoming (post-brightness) knob frame, used purely to detect when
        // the displayed colours actually change; _lastKnobColorChangeTime stamps that
        // change; _knobStaticHoldReleased latches once we've released ownership due to a
        // static hold and stays set (suppressing re-engagement) until the colours change.
        private Color[]? _lastKnobRawColors;
        // UTC ticks (0 = never), Interlocked: written on the data thread and zeroed
        // from the UI thread (InvalidateLiveCache); a DateTime tears on x86.
        private long _lastKnobColorChangeUtcTicks;
        private bool _knobStaticHoldReleased;

        // Unassigned-encoders detection. The generated device.json enables
        // LogicalExtraSection for every wheel with knob LEDs, so SimHub hands back a
        // full-length encoders array whether or not the user assigned anything to it —
        // an unconfigured KS/CS Pro yields KnobCount blacks, indistinguishable from an
        // effect sitting in its "off" state on any single frame. They differ over time:
        // an effect lights, an unassigned channel never does. _knobChannelEverLit
        // latches on the first lit frame (after which every pre-existing hold/release
        // rule applies unchanged); _knobBlackSinceUtc stamps the start of the current
        // unbroken black run so a channel that has never lit can be handed back to the
        // wheel's stored palette instead of being pinned dark (bundle TMS4EP8B).
        private bool _knobChannelEverLit;
        private DateTime _knobBlackSinceUtc = DateTime.MinValue;
        // How long an encoders channel that has NEVER lit may stay black before the live
        // pipeline releases the rings. Covers an effect that starts in its off state
        // (which lights well inside this) without leaving an unassigned channel dark for
        // the session. Applies once per connection: after the release, only a genuinely
        // lit frame re-claims the rings.
        private const double KnobUnassignedGraceSeconds = 4.0;

        // Per-component bitmask tracking (avoid redundant bitmask sends)
        private int _lastRpmBitmask = -1;
        private int _lastButtonBitmask = -1;
        private int _lastKnobBitmask = -1;

        // Keepalive. _lastSendTime = last "any live send" (per-model FPS throttle).
        private DateTime _lastSendTime = DateTime.MinValue;
        // Unified per-section keepalive table. *FedUtc = when the keepalive last re-fed
        // a section (1 Hz pacing). The firmware renders live LEDs only WHILE the bitmask
        // is fed; stop feeding and the section reverts to its stored/idle render. So each
        // section is re-fed while it is "engaged", but engaged differs by section because
        // SimHub treats the channels differently when an effect halts:
        //   • RPM / buttons — SimHub keeps sending them (black) after a halt, so "channel
        //     present" can't detect a halt. Engaged = currently lit, OR within the hold
        //     window since the content last CHANGED (_rpm/_btnChangedUtc). A steadily-
        //     black section ages out and reverts; their "off" is just dark, no hold needed.
        //   • Knobs — SimHub STOPS the encoder channel when the knob effect halts, and the
        //     knob "off" must be fed (active=window) to render dark instead of reverting to
        //     the ring's stored colours. So engaged = within the hold window since SimHub
        //     last drove the channel (_knobDrivenUtc, stamped every frame the channel is
        //     present, lit or black). Keyed on CHANGE it would time out a steady-off-but-
        //     active effect after the hold; keyed on channel-present it holds the off while
        //     the effect runs and reverts only once SimHub stops sending it.
        private DateTime _rpmChangedUtc = DateTime.MinValue;
        private DateTime _rpmFedUtc = DateTime.MinValue;
        private DateTime _btnChangedUtc = DateTime.MinValue;
        private DateTime _btnFedUtc = DateTime.MinValue;
        private DateTime _knobDrivenUtc = DateTime.MinValue;
        private DateTime _knobFedUtc = DateTime.MinValue;
        // The firmware drops live-LED ownership 1000 ms after the last feed. Scheduling
        // the feed AT 1.0 s guaranteed a late arrival once Display() sampling jitter was
        // added (+98 ms observed), reverting the knob ring to stored colours ~0.7x/s.
        // Must satisfy: interval + jitter < 1000 ms.
        private const double KeepaliveIntervalSeconds = 0.75;
        // Default per-section hold (seconds) when the wheel page has no explicit
        // WheelKeepaliveTimeoutSec; the Options slider overrides it. 0 = no hold.
        private const double KeepaliveHoldSeconds = 45.0;

        // ES wheel wake-up
        private bool _ledsAwake;

        // Latched while the live pipeline is standing down for a dashboard
        // upload, so the resume edge can re-arm the caches. See the upload
        // guard in Display().
        private bool _uploadPaused;

        /// <summary>
        /// Expected wheel model prefix for this device instance.
        /// Null = unknown (don't connect). Empty string = generic fallback (any wheel).
        /// Specific prefix (e.g. "W17") = only connect when that model is detected.
        /// </summary>
        public string? ExpectedModelPrefix { get; set; }

        public LedModuleSettings LedModuleSettings { get; set; } = null!;

        public LedDeviceState LastState => _lastState;

        private bool _wasConnected;

        // The MozaPlugin instance this driver last saw. SimHub's game-switch plugin
        // reload builds a new MozaPlugin, but this driver is owned by the device
        // extension and survives — so a reload is invisible to _wasConnected.
        private MozaPlugin? _lastPluginInstance;

        // Shared/master LED-brightness tracking. SimHub's per-device master slider is
        // LedModuleSettings.GlobalBrightnessPreset.Brightness (0..100) — the value the
        // per-channel Display() factors are all scaled by. We publish settled changes
        // to MozaPlugin.WheelLedMasterBrightness so the data thread can write the wheel
        // firmware group brightness. The first observed value is a BASELINE (never
        // written) so connecting doesn't overwrite the wheel's device-stored brightness
        // with SimHub's default; only a subsequent change engages the firmware write.
        // Changes are debounced so a slider drag doesn't spam flash-backed writes.
        private bool _masterBriSeeded;
        private int _masterBriBaseline = -1;
        private bool _masterEngaged;
        private int _masterBriRaw = -1;
        private DateTime _masterBriRawUtc = DateTime.MinValue;
        private int _masterBriPublished = -1;
        private const double MasterBrightnessDebounceMs = 350.0;

        // Per-zone firmware-brightness tracking, one slot per wheel LED group that has
        // its own 1B [G] FF register: 0 = rpm (group 0), 1 = buttons (group 1),
        // 2 = knob rings (group 3). The value comes from the EFFECTIVE factor SimHub
        // hands Display() for that zone (globalMaster/100 x zoneBalance/100), so
        // round(factor * 100) is the firmware percentage. Same discipline as the master
        // tracker above — first observation is a baseline that is never written, a zone
        // only engages once the user moves it off that baseline, and changes are
        // debounced so a drag lands one flash write rather than one per tick.
        private const int ZoneRpm = 0;
        private const int ZoneButtons = 1;
        private const int ZoneKnob = 2;
        private const int ZoneCount = 3;
        private readonly bool[] _zoneBriSeeded = new bool[ZoneCount];
        private readonly int[] _zoneBriBaseline = { -1, -1, -1 };
        private readonly bool[] _zoneEngaged = new bool[ZoneCount];
        private readonly int[] _zoneBriRaw = { -1, -1, -1 };
        private readonly DateTime[] _zoneBriRawUtc =
            { DateTime.MinValue, DateTime.MinValue, DateTime.MinValue };
        private readonly int[] _zoneBriPublished = { -1, -1, -1 };
        // Last-seen firmware LED mode per group, for the live-cache invalidation edge.
        private int _lastButtonsLedMode = int.MinValue;
        private int _lastKnobLedMode = int.MinValue;

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
            // Plugin-reload edge. SimHub swaps in a new MozaPlugin on a game switch
            // while this driver (and its device extension) lives on, and the outgoing
            // End() blanked the wheel LEDs on the still-open wire via
            // HardwareApplier.ClearLedsOnHardware. Every cache below therefore
            // describes a state the wheel no longer holds, and the change-detection
            // guards in Display() would suppress the first re-send. Detection never
            // dropped, so _wasConnected sees no edge — key off plugin identity
            // instead and treat it exactly like a reconnect. Null (the window
            // between End() and Init()) is not an edge.
            var plugin = MozaPlugin.Instance;
            if (plugin != null && !ReferenceEquals(plugin, _lastPluginInstance))
            {
                if (_lastPluginInstance != null)
                {
                    MozaLog.Debug("[AZOM] LED driver: plugin instance changed (reload) — re-arming LED caches");
                    ResetCachedLedState();
                }
                _lastPluginInstance = plugin;
            }

            bool connected = IsConnected();
            if (connected == _wasConnected) return;
            _wasConnected = connected;

            if (connected)
            {
                OnConnect?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ResetCachedLedState();
                OnDisconnect?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Drop every cached LED frame / bitmask / keepalive stamp so the next
        /// <c>Display()</c> re-initializes from scratch. Also clears the ES wake
        /// latch, so the <c>0x3FF</c> → <c>0</c> live-LED-mode pulse is re-issued.
        /// Called on a detection-loss edge and on a plugin-reload edge — both leave
        /// these caches describing hardware state that no longer holds.
        /// </summary>
        private void ResetCachedLedState()
        {
            _lastLeds = null;
            _lastButtons = null;
            _lastKnobs = null;
            _lastFlagColorsPrimed = false;
            _lastRpmBitmask = -1;
            _lastButtonBitmask = -1;
            _lastKnobBitmask = -1;
            _lastKnobRawColors = null;
            Interlocked.Exchange(ref _lastKnobColorChangeUtcTicks, 0L);
            _knobStaticHoldReleased = false;
            // Detection-loss / reload re-opens the grace window: the next connection
            // may be a differently-configured wheel or profile.
            _knobChannelEverLit = false;
            _knobBlackSinceUtc = DateTime.MinValue;
            _rpmChangedUtc = _rpmFedUtc = DateTime.MinValue;
            _btnChangedUtc = _btnFedUtc = DateTime.MinValue;
            _knobDrivenUtc = _knobFedUtc = DateTime.MinValue;
            _ledsAwake = false;
            _uploadPaused = false;
        }

        public bool IsConnected() => IsModelConnected(MozaPlugin.Instance, ExpectedModelPrefix);

        /// <summary>
        /// Detection-based connection verdict for a wheel device extension whose
        /// device-type resolved to <paramref name="expectedPrefix"/>. Reads only
        /// plugin detection state (set at Init/probe), so it is valid even when no
        /// virtual LED driver has been injected yet (the injected driver is created
        /// lazily in the extension's DataUpdate). The settings-control connection
        /// gate calls this directly so the tab reflects detection rather than the
        /// LED-injection lifecycle.
        /// </summary>
        internal static bool IsModelConnected(MozaPlugin? p, string? expectedPrefix)
        {
            if (expectedPrefix == null)
                return false;

            if (p == null)
                return false;

            // Generic old-protocol fallback device — matches an old wheel only
            // when it did NOT resolve a model-specific identity (a model-less
            // rim). An ES wheel resolves model "ES" from id 0x18 and is served by
            // its own model-specific device below, so the marker device steps
            // aside for it.
            if (expectedPrefix == MozaDeviceConstants.OldProtocolMarker)
                return p.IsOldWheelDetected && string.IsNullOrEmpty(p.Data.WheelModelName);

            // Any other device requires a detected wheel — new OR old protocol.
            // (ES is an identified OLD-protocol wheel with a specific prefix, so
            // a specific prefix no longer implies new-protocol.)
            if (!p.IsNewWheelDetected && !p.IsOldWheelDetected)
                return false;

            // Empty prefix = generic new-protocol fallback, matches any
            // new-protocol wheel UNLESS a model-specific device extension is
            // active for this wheel.
            if (expectedPrefix.Length == 0)
                return p.IsNewWheelDetected
                    && !p.IsModelSpecificExtensionActive(p.Data.WheelModelName);

            // Specific model — match against the detected wheel's firmware model
            // name. Works for new-protocol (0x17) wheels and old-protocol ES
            // (@ 0x18) alike.
            var modelName = p.Data.WheelModelName;
            if (string.IsNullOrEmpty(modelName))
                return false;

            return modelName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
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

        // True if any entry is non-black. Used to keep the live LED stream quiet
        // when there's nothing lit to show — re-sending all-black frames would
        // hold the wheel in live-render mode and block its firmware sleep light.
        private static bool AnyLit(Color[]? colors)
        {
            if (colors == null) return false;
            for (int i = 0; i < colors.Length; i++)
                if (colors[i].R != 0 || colors[i].G != 0 || colors[i].B != 0) return true;
            return false;
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
                _lastKnobRawColors = null;
                Interlocked.Exchange(ref _lastKnobColorChangeUtcTicks, 0L);
                _knobStaticHoldReleased = false;
                // _knobChannelEverLit / _knobBlackSinceUtc deliberately survive: this
                // runs when a STATIC write just repainted the rings, which is exactly
                // when an unassigned encoders channel must not re-claim them with black.
                // Only ResetCachedLedState (detection loss / reload) re-opens the window.
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

        /// <summary>
        /// SimHub &lt;= 9.11.x <c>ILedDeviceManager.Display</c>, which has no
        /// <c>overrideState</c> channel. Declared alongside the current overload so one
        /// DLL serves both host generations — the CLR binds an implicitly-implemented
        /// interface method by name and signature at type-load time, so each SimHub
        /// build picks the overload its own interface declares. Dead code on 9.12+.
        /// Drop this once 9.11.x is no longer supported (see SimHubLedCompat).
        ///
        /// <c>virtual</c> is load-bearing, not style: the CLR fills an interface slot only
        /// from a public *virtual* method (ECMA-335 II.12.2). Roslyn marks the overload
        /// matching the compile-time interface virtual automatically, but this one matches
        /// no interface we compile against, so without the keyword it stays non-virtual and
        /// 9.11.x still fails type load with the error this whole shim exists to avoid.
        /// </summary>
        public virtual void Display(
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
            => Display(leds, buttons, encoders, matrix, rawState, SimHubLedCompat.NoOverrides,
                forceRefresh, extraData,
                rpmBrightness, buttonsBrightness, encodersBrightness, matrixBrightness);

        public void Display(
            Func<Color[]> leds,
            Func<Color[]> buttons,
            Func<Color[]> encoders,
            Func<Color[]> matrix,
            Func<Color[]> rawState,
            Func<Color[]> overrideState,
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
                var overrideColors = overrideState?.Invoke() ?? Array.Empty<Color>();

                _lastState = SimHubLedCompat.CreateState(
                    ledColors, buttonColors, encoderColors, matrixColors, rawColors, overrideColors,
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

                // Dashboard upload standing the pipeline down: the RPM bar
                // becomes the transfer's progress meter (UploadProgressLedBar,
                // fed from the telemetry tick). Two reasons to pause rather
                // than interleave — the upload and a 60 Hz LED stream contend
                // for the same half-duplex link (the contention the negotiation
                // throttle below exists for, only worse: the wheel processes
                // upload rounds at a few hundred B/s), and both writers would
                // otherwise fight over the group-0 frame buffer. _lastState is
                // already captured above, so SimHub's own LED preview keeps
                // updating; only the hardware writes stop.
                //
                // The gate is IsStandDownActive, NOT the raw in-flight flag: a
                // wedged upload holds that flag for minutes before its attempt
                // terminates (bundle C4KX4GKK: 6 min 17 s with no byte
                // progress), and the LEDs must not be hostage to it. See
                // UploadProgressLedBar.StallReleaseSeconds.
                //
                // On the trailing edge every cached frame describes state the
                // wheel no longer holds — the progress bar overwrote the RPM
                // buffer and the button / knob / flag groups reverted to their
                // stored palettes once their keepalives stopped — so re-arm the
                // whole cache.
                if (UploadProgressLedBar.IsStandDownActive)
                {
                    _uploadPaused = true;
                    return;
                }
                if (_uploadPaused)
                {
                    _uploadPaused = false;
                    InvalidateLiveCache(LedKind.All);
                }

                // Catalog-negotiation LED throttle: while the wheel is (re)advertising
                // its catalog (cold-start or a post-switch hot-reneg burst) the link is
                // saturated by our tier-def burst + the wheel's inbound catalog chunks.
                // A 60 Hz LED stream on top drops those chunks — the root of radar/track
                // channel loss on a saturated link. Cap LED writes to ~7/s during the
                // window; full rate resumes the instant negotiation completes. _lastState
                // is already captured above, so the UI preview is unaffected.
                if (plugin.TelemetrySender?.WheelInCatalogNegotiation == true)
                {
                    int nowMs = Environment.TickCount;
                    if (nowMs - _lastNegotiationLedTickMs < CatalogNegotiationLedThrottleMs)
                        return;
                    _lastNegotiationLedTickMs = nowMs;
                }

                // IsConnected() above already matched this device to the connected
                // wheel, so the global detection flag tells us its protocol. ES is
                // an identified old-protocol wheel with a specific prefix, so derive
                // old/new from the flag rather than the OldProtocolMarker prefix.
                bool isOldWheel = plugin.IsOldWheelDetected;
                bool isNewWheel = !isOldWheel && plugin.IsNewWheelDetected;
                if (!isNewWheel && !isOldWheel)
                    return;

                // Track SimHub's shared/master LED-brightness slider for this (active)
                // wheel and publish settled changes to the firmware group brightness.
                // Runs before the empty-channel / throttle early-returns below so a
                // master change is caught even while the wheel sits idle.
                TrackMasterBrightness(plugin);

                // Same for the per-zone "Brightness limiter and balance" sliders — the
                // effective factors already carry the master term, so each zone's
                // firmware register gets master x balance. Also before the early-returns:
                // a Static-mode zone sends no live frames at all, and its slider must
                // still reach the register (that is the whole point of the zone push).
                if (isNewWheel)
                    TrackZoneBrightness(plugin, rpmBrightness, buttonsBrightness, encodersBrightness);

                // A group's LED mode swaps ownership between the live pipeline (mode 1 =
                // SimHub) and the firmware's static palette (0 = Off, 2 = Static). Drop the
                // live cache on every transition so the first frame after a switch back to
                // SimHub mode actually goes out instead of dedup'ing against a frame the
                // wheel stopped showing while it was rendering its palette.
                int btnLedMode = plugin.Data.WheelButtonsLedMode;
                if (btnLedMode != _lastButtonsLedMode)
                {
                    _lastButtonsLedMode = btnLedMode;
                    InvalidateLiveCache(LedKind.Button);
                }
                int knobLedMode = plugin.Data.WheelKnobLedMode;
                if (knobLedMode != _lastKnobLedMode)
                {
                    _lastKnobLedMode = knobLedMode;
                    InvalidateLiveCache(LedKind.Knob);
                }

                // Merge SimHub's two physical-index colour layers (Individual LEDs on
                // rawState, dashboard "Device LEDs override" components on overrideState)
                // over the per-segment logical channels. Physical order per device.json:
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
                //
                // rawState is merged first and overrideState on top, matching the blend
                // order in SimHub's own PhysicalMapper.GetColor.
                var modelInfo = plugin.WheelModelInfo;
                if (rawColors.Length > 0 || overrideColors.Length > 0)
                {
                    // LogRawDiagnostic(rawColors, ledColors.Length, buttonColors.Length);

                    int telemetryPhys = modelInfo != null
                        ? modelInfo.RpmLedCount + (modelInfo.HasFlagLeds ? MozaDeviceConstants.FlagLedCount : 0)
                        : ledColors.Length;
                    int buttonPhys = modelInfo?.ButtonLedCount ?? buttonColors.Length;
                    int knobPhys = modelInfo?.KnobCount ?? 0;
                    int knobPhysOffset = telemetryPhys + (modelInfo?.ButtonLedCount ?? 0);

                    if (rawColors.Length > 0)
                    {
                        ledColors = ApplyOverrides(ledColors, rawColors, 0, telemetryPhys);
                        buttonColors = ApplyOverrides(buttonColors, rawColors, telemetryPhys, buttonPhys);
                        if (knobPhys > 0)
                            encoderColors = ApplyOverrides(encoderColors, rawColors, knobPhysOffset, knobPhys);
                    }

                    if (overrideColors.Length > 0)
                    {
                        ledColors = ApplyOverrides(ledColors, overrideColors, 0, telemetryPhys);
                        buttonColors = ApplyOverrides(buttonColors, overrideColors, telemetryPhys, buttonPhys);
                        if (knobPhys > 0)
                            encoderColors = ApplyOverrides(encoderColors, overrideColors, knobPhysOffset, knobPhys);
                    }
                }

                // After the physical-layer merges: if every channel is still empty there's
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
                    MozaLog.Debug("[AZOM] ES wheel LED wake-up sent");
                }

                bool anySent = false;

                // Per-model live LED wire-rate cap (frames/sec; 0 = unlimited).
                // SimHub drives this at 60 Hz; some rims (the wireless bare-"CS")
                // can't take the RPM stream at the full radio cadence and wedge
                // their param manager. When throttled we skip this tick's LED
                // sends WITHOUT updating _lastLeds/_lastButtons/_lastKnobs, so the
                // change is re-evaluated next tick and the latest colour state
                // still goes out — just no faster than the cap. The keepalive
                // below is seconds-scale (gated on _lastSendTime) and unaffected.
                int maxLedFps = modelInfo?.MaxLedFps ?? 0;
                bool ledThrottled = maxLedFps > 0
                    && (DateTime.UtcNow - _lastSendTime).TotalMilliseconds < 1000.0 / maxLedFps;

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
                // ZoneCompensated divides the firmware value back out once this zone's
                // group brightness is tracking the slider, so the register — a hardware
                // dimmer that also scales these live frames — isn't applied twice.
                rpmColors = ScaleColorsForBrightness(
                    rpmColors,
                    ZoneCompensated(rpmBrightness,
                        plugin.WheelLedAppliedBrightnessRpm, plugin.WheelLedMasterBrightness));

                // --- RPM LEDs ---
                bool rpmChanged = !ColorsEqual(rpmColors, _lastLeds);
                // forceRefresh resends only when something is lit: an all-off frame
                // is sent once via rpmChanged (lit->off) and then left quiet, so
                // forceRefresh can't re-flood the wheel with all-black frames at idle.
                bool shouldSendRpm = !ledThrottled && (rpmChanged || (forceRefresh && AnyLit(rpmColors)));

                if (shouldSendRpm)
                {
                    _lastLeds = (Color[])rpmColors.Clone();
                    _rpmChangedUtc = DateTime.UtcNow;

                    int count = Math.Min(rpmColors.Length, rpmN);

                    // Build bitmask: bit i set if LED i has any color
                    int bitmask = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (rpmColors[i].R > 0 || rpmColors[i].G > 0 || rpmColors[i].B > 0)
                            bitmask |= (1 << i);
                    }

                    // Every live LED write below rides the PACED one-shot FIFO — see the
                    // isNewWheel branch for why the stream lane lost frames at the rim.
                    // TRADE-OFF: a co-resident CM2 value stream on a shared bus can starve
                    // this lane (the measured 111->238ms rim-cadence regression the stream
                    // lane was originally added to fix). Revisit if a bus-CM2 user reports
                    // slow rim LEDs; the rim's pacing need wins for the single-display case.
                    if (isNewWheel && modelInfo?.UsesLegacyRpmTelemetry == true)
                    {
                        // PitHouse "old colour-capable rim" path (bare "CS"): per-LED
                        // colours plus the lit-state via both the new windowed bitmask
                        // (0x1a) and the old-protocol bitmask (0x41 fd de) — PitHouse
                        // streams the 0x41 path heavily to this rim. (Colour-rate
                        // capping was tried and ruled out as the storm cause.)
                        //
                        // STAYS on the paced one-shot lane (NOT the stream lane): this
                        // wireless rim drops unpaced bursts, and the 4ms inter-write
                        // pacing is what spaces its chunk+bitmask writes. It's also a
                        // single-display rim — never the bus-CM2 contention case the
                        // stream lane exists for — so it gains nothing from streaming.
                        SendColorChunks(plugin, rpmColors, count, "wheel-telemetry-rpm-colors");
                        if (bitmask != _lastRpmBitmask)
                        {
                            _lastRpmBitmask = bitmask;
                            plugin.DeviceManager.WriteArray("wheel-send-rpm-telemetry",
                                BuildWindowedBitmaskBytes(bitmask, (1 << rpmN) - 1));
                            plugin.DeviceManager.WriteSetting("wheel-old-send-telemetry", bitmask);
                        }
                        anySent = true;
                    }
                    else if (isNewWheel)
                    {
                        // Live LED writes ride the PACED one-shot FIFO, not the stream lane.
                        // The rim drops unpaced bursts: streaming the set emitted it in <1ms
                        // host-side (measured) but the wheel lost frames, reading as laggy /
                        // off-time animation and knob rings reverting to stored colours. The
                        // FIFO's 4ms spacing is what the rim needs, and its ordering keeps
                        // every colour ahead of the bitmask that lights it.
                        SendColorChunks(plugin, rpmColors, count, "wheel-telemetry-rpm-colors");

                        if (bitmask != _lastRpmBitmask)
                        {
                            _lastRpmBitmask = bitmask;
                            // 8-byte active+window form, matching PitHouse on every wheel
                            // captured (CS V2.1, CS Pro). window = the full RPM-LED set;
                            // the old 2-byte form (no window) left CS V2.1's first LED
                            // stuck lit. See docs/protocol/leds/color-commands.md.
                            plugin.DeviceManager.WriteArray("wheel-send-rpm-telemetry",
                                BuildWindowedBitmaskBytes(bitmask, (1 << rpmN) - 1));
                        }
                        anySent = true;
                    }
                    else if (isOldWheel)
                    {
                        // ES wheels: can't set colors per-frame, just send the bitmask.
                        // Stays on the one-shot lane (same as the ES wake pulse above):
                        // an ES rim is single-display, so it needs no stream-lane
                        // protection, and lane parity keeps the wake-pulse OFF from
                        // landing after the lit bitmask and blanking the rim.
                        if (bitmask != _lastRpmBitmask)
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
                        if (changed || (forceRefresh && (c.R | c.G | c.B) != 0))
                        {
                            _lastFlagColors[i] = c;
                            _rpmChangedUtc = DateTime.UtcNow; // flags ride the RPM keepalive row
                            plugin.DeviceManager.WriteArray(
                                DashFlagColorCommand(i),
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
                if (isNewWheel && buttonColors.Length > 0 && modelInfo != null
                    && GroupRendersLiveFrames(btnLedMode))
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
                        // overridden is SimHub-logical (0..ButtonLedCount-1); the static
                        // arrays are protocol-indexed (14 slots). Map logical → protocol
                        // via ButtonLedMap so a non-contiguous wheel (CS V2.1 → 0,1,3,6,8,9)
                        // reads each button's own flag/colour instead of the wrong slot.
                        var buttonMap = modelInfo.ButtonLedMap;
                        int lim = Math.Min(overridden.Length, modelInfo.ButtonLedCount);
                        // B4: read every static-colour triplet under the colour lock —
                        // UI handlers may be writing concurrently and a torn read
                        // would push a 1-frame wrong-colour to the wheel.
                        lock (plugin.Data.LedColorLock)
                        {
                            for (int i = 0; i < lim; i++)
                            {
                                int p = buttonMap != null ? buttonMap[i] : i;
                                if (p < 0 || p >= defaultFlags.Length || p >= staticColors.Length) continue;
                                if (!defaultFlags[p]) continue;
                                var c = overridden[i];
                                if (c.R != 0 || c.G != 0 || c.B != 0) continue;
                                var sc = staticColors[p];
                                overridden[i] = Color.FromArgb(sc[0], sc[1], sc[2]);
                            }
                        }
                        buttonColors = overridden;
                    }

                    // Per-frame brightness (SimHub's buttons LED-brightness
                    // slider). Applied after the default-during-telemetry
                    // override so the static fallback colours dim too.
                    buttonColors = ScaleColorsForBrightness(
                        buttonColors,
                        ZoneCompensated(buttonsBrightness,
                            plugin.WheelLedAppliedBrightnessButtons, plugin.WheelLedMasterBrightness));

                    bool buttonsChanged = !ColorsEqual(buttonColors, _lastButtons);
                    bool shouldSendButtons = !ledThrottled && (buttonsChanged || (forceRefresh && AnyLit(buttonColors)));

                    if (shouldSendButtons)
                    {
                        _lastButtons = (Color[])buttonColors.Clone();
                        _btnChangedUtc = DateTime.UtcNow;

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

                        if (buttonBitmask != _lastButtonBitmask)
                        {
                            _lastButtonBitmask = buttonBitmask;
                            // 8-byte form: active_mask(u32 LE) + window_mask(u32 LE).
                            // window is the wheel's full button set for non-contiguous
                            // layouts (CS V2.1 → 0x034B; its firmware leaves buttons dark
                            // when window=0), and 0 for contiguous-button wheels — exactly
                            // what PitHouse sends per wheel. See WheelModelInfo.ButtonWindowMask.
                            plugin.DeviceManager.WriteArray("wheel-send-buttons-telemetry",
                                BuildWindowedBitmaskBytes(buttonBitmask, modelInfo.ButtonWindowMask));
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
                if (isNewWheel && modelInfo != null && modelInfo.KnobCount > 0 && encoderColors.Length > 0
                    && GroupRendersLiveFrames(knobLedMode))
                {
                    // SimHub is feeding the knob channel this frame (lit or black) — stamp
                    // it so the keepalive holds the knob "off" while the effect runs and
                    // only lets it revert once SimHub stops sending the channel.
                    _knobDrivenUtc = DateTime.UtcNow;

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
                    knobColors = ScaleColorsForBrightness(
                        knobColors,
                        ZoneCompensated(encodersBrightness,
                            plugin.WheelLedAppliedBrightnessKnob, plugin.WheelLedMasterBrightness));

                    int count = Math.Min(knobColors.Length, knobCount);
                    int knobBitmask = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (knobColors[i].R > 0 || knobColors[i].G > 0 || knobColors[i].B > 0)
                            knobBitmask |= (1 << i);
                    }


                    // We're in this block because SimHub is feeding the knob channel, so
                    // own/drive the knobs every frame — including when the very first frame
                    // is all-black (an effect that starts in its "off" state). The old gate
                    // (knobBitmask != 0 || _lastKnobBitmask > 0) required a lit frame first,
                    // so a start-in-off animation was ignored until it lit once. The explicit
                    // release paths (Default-during-telemetry toggle / static-hold timeout /
                    // never-lit grace window) are what hand the ring back to its stored colours.
                    bool knobsActive = true;

                    // Static-hold restore (WheelKnobStaticTimeoutMs): when the live knob
                    // colours stay unchanged for longer than the timeout, release telemetry
                    // ownership so the wheel shows its native per-position colours — lets a
                    // colour held a long time be ignored. 0 = off. The release stays latched
                    // (_knobStaticHoldReleased) until the colours actually change, so we
                    // don't immediately re-engage on the very next identical frame.
                    var nowUtc = DateTime.UtcNow;

                    // Track the current unbroken black run so an encoders channel that
                    // has never lit can be told from an effect that is momentarily off.
                    // A single lit frame latches _knobChannelEverLit for the rest of the
                    // connection, after which knobsUnassigned is permanently false and
                    // the release rules below behave exactly as before.
                    if (knobBitmask != 0)
                    {
                        _knobChannelEverLit = true;
                        _knobBlackSinceUtc = DateTime.MinValue;
                    }
                    else if (_knobBlackSinceUtc == DateTime.MinValue)
                    {
                        _knobBlackSinceUtc = nowUtc;
                    }
                    bool knobsUnassigned = !_knobChannelEverLit
                        && _knobBlackSinceUtc != DateTime.MinValue
                        && (nowUtc - _knobBlackSinceUtc).TotalSeconds >= KnobUnassignedGraceSeconds;

                    int knobStaticTimeoutMs = plugin.Data.WheelKnobStaticTimeoutMs;
                    if (!ColorsEqual(knobColors, _lastKnobRawColors))
                    {
                        _lastKnobRawColors = (Color[])knobColors.Clone();
                        Interlocked.Exchange(ref _lastKnobColorChangeUtcTicks, nowUtc.Ticks);
                        _knobStaticHoldReleased = false;
                    }
                    bool knobStaticTimedOut = knobStaticTimeoutMs > 0
                        && (nowUtc.Ticks - Interlocked.Read(ref _lastKnobColorChangeUtcTicks)) / TimeSpan.TicksPerMillisecond >= knobStaticTimeoutMs;

                    // Release telemetry ownership of the knobs (active_mask=0 AND
                    // window_mask=0 — exactly the form PitHouse uses; 286/286 knob writes
                    // are active=0/window=0) so the firmware renders the native per-position
                    // colours. Three independent triggers:
                    //   • "Default during telemetry" toggle + the frame is fully off.
                    //   • Static-hold timeout above.
                    //   • The encoders channel has never lit and its opening black run
                    //     outlived the grace window — nothing is assigned to it, so
                    //     driving it means pinning the rings dark for the session.
                    // These knobs store a separate colour per rotation position, so the only
                    // correct "show original" is to stop driving them entirely. A non-zero
                    // window leaves telemetry owning the knobs (all-off → dark), and sending
                    // any colour overrides the per-position state — both wrong. Reset
                    // _lastKnobs/_lastKnobBitmask so the keepalive below doesn't re-claim the
                    // knobs; a returning (or changed) frame re-engages through the normal path.
                    bool releaseForOff = plugin.Data.WheelKnobDefaultDuringTelemetry && knobBitmask == 0;
                    if (releaseForOff || knobStaticTimedOut || knobsUnassigned)
                    {
                        // Hand the ring back to its stored colours. Only emit the release
                        // frame (active=0/window=0) if we currently OWN the knobs; if we
                        // never claimed them the firmware is already showing static, so we
                        // just don't drive. Crucially, taking THIS branch (not the drive
                        // else-if) for every off frame — even after _lastKnobBitmask was
                        // reset to -1 by the release — is what stops the release ↔ re-drive
                        // flicker that knobsActive=true would otherwise cause.
                        if (_lastKnobBitmask > 0 && !ledThrottled)
                        {
                            plugin.DeviceManager.WriteArray("wheel-send-knob-telemetry",
                                BuildWindowedBitmaskBytes(0, 0));
                            _lastKnobBitmask = -1;
                            _lastKnobs = null;
                            anySent = true;
                        }
                        if (knobStaticTimedOut) _knobStaticHoldReleased = true;
                    }
                    else if (knobsActive && !_knobStaticHoldReleased)
                    {
                        bool knobsChanged = !ColorsEqual(knobColors, _lastKnobs);
                        bool shouldSendKnobs = !ledThrottled && (knobsChanged || (forceRefresh && AnyLit(knobColors)));

                        if (shouldSendKnobs)
                        {
                            _lastKnobs = (Color[])knobColors.Clone();

                            SendColorChunks(plugin, knobColors, count, "wheel-telemetry-knob-colors");

                            int windowMask = (1 << knobCount) - 1;
                            // The CS Pro re-renders the knob ring ONLY on a bitmask write — a
                            // colour-only frame updates the buffer but is never shown (verified
                            // across three bundles: the animation's all-black "off" carries no
                            // bitmask change, so without this it's silently dropped and the ring
                            // keeps the last lit frame). So send the mask on EVERY colour frame to
                            // latch it. Telemetry owns ALL knobs (active=window); per-knob on/off
                            // is carried by the COLOURS (black = off). Never active=0 or a partial
                            // mask — that reverts un-owned knobs to their EEPROM defaults.
                            _lastKnobBitmask = windowMask;
                            plugin.DeviceManager.WriteArray("wheel-send-knob-telemetry", BuildWindowedBitmaskBytes(windowMask, windowMask));
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

                if (anySent)
                {
                    _lastSendTime = DateTime.UtcNow;
                    // Mark the live-path active for the cross-instance gate that
                    // suppresses static writes (HardwareApplier, UI handlers).
                    NoteLiveSend();
                }

                // --- Unified per-section keepalive ---
                // The firmware renders live LEDs only WHILE their bitmask is fed; stop
                // feeding and the group reverts to its stored/idle render. So re-feed each
                // section's last frame (colour + bitmask) at ~1 Hz while it's CURRENTLY LIT
                // (hold the lit frame indefinitely) OR within the hold window since it last
                // CHANGED (render an "off" — knobs store active=window so it goes dark —
                // for the hold, then let it revert). Keying on content (lit / recent change)
                // rather than "SimHub is sending the channel" is what lets a steadily-black
                // section time out: after an effect halt SimHub keeps sending black RPM/
                // buttons but stops the knob channel, and a channel-presence key held the
                // RPM/buttons off forever while knobs reverted. 0 = no hold. *FedUtc paces
                // each section independently.
                var kaNow = DateTime.UtcNow;
                int holdSec = plugin.Settings?.WheelKeepaliveTimeoutSec ?? (int)KeepaliveHoldSeconds;
                // While a game is actively feeding telemetry, NEVER pause the keepalive —
                // the wheel must stay live for the whole session (incl. menus/pauses) and
                // only sleep once the game is closed. The lit/hold gate (which lets a
                // steadily-black section time out) applies only when no game is active.
                bool gameActive = plugin.IsGameActive;
                if (_lastLeds != null && (gameActive || AnyLit(_lastLeds) || WithinHold(kaNow, _rpmChangedUtc, holdSec))
                    && (kaNow - _rpmFedUtc).TotalSeconds >= KeepaliveIntervalSeconds)
                {
                    _rpmFedUtc = kaNow; _lastSendTime = kaNow;
                    ResendRpmFlags(plugin, isNewWheel, isOldWheel);
                    NoteLiveSend();
                }
                // Mode predicate here too: a group in Off/Static renders its stored
                // palette and discards live frames, so re-feeding them is pure wire
                // traffic. (The transition edge above also nulls the cache, so this is
                // the belt to that braces.)
                if (isNewWheel && _lastButtons != null && GroupRendersLiveFrames(btnLedMode)
                    && (gameActive || AnyLit(_lastButtons) || WithinHold(kaNow, _btnChangedUtc, holdSec))
                    && (kaNow - _btnFedUtc).TotalSeconds >= KeepaliveIntervalSeconds)
                {
                    _btnFedUtc = kaNow; _lastSendTime = kaNow;
                    ResendButtons(plugin);
                    NoteLiveSend();
                }
                if (isNewWheel && _lastKnobs != null && modelInfo?.KnobCount > 0
                    && GroupRendersLiveFrames(knobLedMode)
                    && (gameActive || AnyLit(_lastKnobs) || WithinHold(kaNow, _knobDrivenUtc, holdSec))
                    && (kaNow - _knobFedUtc).TotalSeconds >= KeepaliveIntervalSeconds)
                {
                    _knobFedUtc = kaNow; _lastSendTime = kaNow;
                    ResendKnobs(plugin, modelInfo);
                    NoteLiveSend();
                }
            }
            finally
            {
                AfterDisplay?.Invoke(this, EventArgs.Empty);
            }
        }

        // True while a section is still inside its keepalive hold window measured from
        // its last change. holdSec 0 (UI: pause immediately) or a never-changed section
        // (MinValue) → not held.
        private static bool WithinHold(DateTime now, DateTime changedUtc, int holdSec)
            => holdSec > 0 && changedUtc != DateTime.MinValue && (now - changedUtc).TotalSeconds < holdSec;

        /// <summary>Re-feed the last RPM (and flag) frame — colour + bitmask — to keep
        /// the firmware rendering it.</summary>
        private void ResendRpmFlags(MozaPlugin plugin, bool isNewWheel, bool isOldWheel)
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
                        BuildWindowedBitmaskBytes(_lastRpmBitmask, (1 << rpmN) - 1));

                // Flag colours stay on the one-shot lane (low-rate, change-gated, and
                // also driven by MozaDashLedDeviceManager — keep a single lane to avoid
                // a two-driver desync).
                if (modelInfo?.HasFlagLeds == true && plugin.IsDashDetected && _lastFlagColorsPrimed)
                    for (int i = 0; i < MozaDeviceConstants.FlagLedCount; i++)
                    {
                        var c = _lastFlagColors[i];
                        plugin.DeviceManager.WriteArray(DashFlagColorCommand(i), new byte[] { c.R, c.G, c.B });
                    }
            }
            else if (isOldWheel)
            {
                if (_lastRpmBitmask >= 0)
                    plugin.DeviceManager.WriteSetting("wheel-old-send-telemetry", _lastRpmBitmask);
            }
        }

        /// <summary>Re-feed the last button frame — colour + bitmask (new-protocol wheels).</summary>
        private void ResendButtons(MozaPlugin plugin)
        {
            if (_lastButtons == null) return;
            var modelInfo = plugin.WheelModelInfo;
            if (modelInfo == null) return;
            int count = Math.Min(_lastButtons.Length, modelInfo.ButtonLedCount);
            SendColorChunks(plugin, _lastButtons, count, "wheel-telemetry-button-colors", modelInfo.ButtonLedMap);
            if (_lastButtonBitmask >= 0)
                plugin.DeviceManager.WriteArray("wheel-send-buttons-telemetry",
                    BuildWindowedBitmaskBytes(_lastButtonBitmask, modelInfo.ButtonWindowMask));
        }

        /// <summary>Re-feed the last knob frame — colour + bitmask (active=window, so an
        /// all-black "off" renders dark instead of reverting to EEPROM).</summary>
        private void ResendKnobs(MozaPlugin plugin, WheelModelInfo modelInfo)
        {
            if (_lastKnobs == null) return;
            int count = Math.Min(_lastKnobs.Length, modelInfo.KnobCount);
            SendColorChunks(plugin, _lastKnobs, count, "wheel-telemetry-knob-colors");
            if (_lastKnobBitmask >= 0)
                plugin.DeviceManager.WriteArray("wheel-send-knob-telemetry",
                    BuildWindowedBitmaskBytes(_lastKnobBitmask, (1 << modelInfo.KnobCount) - 1));
        }

        /// <summary>
        /// Build the 8-byte active+window LED bitmask payload:
        /// active_mask(u32 LE) + window_mask(u32 LE). This is the form PitHouse
        /// sends on every wheel captured — for the RPM strip (group 0), button
        /// matrix (group 1) and knob rings (group 3) alike. <paramref name="windowMask"/>
        /// is the set of LED indices the firmware should treat as addressable
        /// (e.g. 0x03FF = 10 RPM LEDs, 0x034B = CS V2.1's six mapped buttons);
        /// <paramref name="activeMask"/> is the lit subset.
        /// </summary>
        internal static byte[] BuildWindowedBitmaskBytes(int activeMask, int windowMask)
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
        /// Observe SimHub's shared/master LED-brightness slider for this wheel and
        /// publish settled changes to <see cref="MozaPlugin.WheelLedMasterBrightness"/>
        /// so the data thread can push it to the firmware group brightness. The master
        /// is <c>GlobalBrightnessPreset.Brightness</c> (0..100) — the value SimHub scales
        /// every per-channel Display() factor by, distinct from the per-frame factors
        /// (which transiently drop to 0 and must never reach EEPROM — see
        /// <see cref="ScaleColorsForBrightness"/>). The first observation seeds a baseline
        /// and is never written, so connecting leaves the wheel's device-stored brightness
        /// alone; only a later change engages the firmware write. Debounced so a drag
        /// doesn't spam flash writes. Full 0..100 is honoured (SimHub brightness-mode
        /// automation that dims to 0 is written through as chosen).
        /// </summary>
        private void TrackMasterBrightness(MozaPlugin plugin)
        {
            int cur;
            try
            {
                var preset = LedModuleSettings?.GlobalBrightnessPreset;
                if (preset == null) return;
                cur = (int)Math.Round(preset.Brightness);
            }
            catch { return; }
            if (cur < 0) cur = 0; else if (cur > 100) cur = 100;

            if (!_masterBriSeeded)
            {
                // First sample = baseline; do not write (preserve device brightness).
                _masterBriSeeded = true;
                _masterBriBaseline = cur;
                _masterBriRaw = cur;
                _masterBriPublished = cur;
                return;
            }
            // Stay silent until the user actually moves the slider off its baseline,
            // so connecting never overwrites the wheel's device-stored brightness.
            if (!_masterEngaged)
            {
                if (cur == _masterBriBaseline) return;
                _masterEngaged = true;
            }

            // Old-protocol wheels (ES/ESX): dimming is possible ONLY via the firmware
            // brightness register, and this Display() path fires in bursts at idle —
            // too sparse to time a trailing-edge settle here (that lagged the applied
            // value a whole gesture behind, issue #113). Publish the LIVE value; the
            // steady 250 ms poll timer in MozaPlugin debounces and writes it, so it
            // can't depend on Display()/DataUpdate cadence.
            if (plugin.IsOldWheelDetected)
            {
                plugin.WheelLedMasterBrightnessRaw = cur;
                return;
            }

            // New-protocol wheels: firmware group brightness is a secondary refinement
            // (per-frame colour scaling already dims live), so the debounced settled
            // publish on this thread is sufficient. DataUpdate applies it.
            var now = DateTime.UtcNow;
            if (cur != _masterBriRaw)
            {
                _masterBriRaw = cur;
                _masterBriRawUtc = now;
            }
            if (cur != _masterBriPublished
                && (now - _masterBriRawUtc).TotalMilliseconds >= MasterBrightnessDebounceMs)
            {
                _masterBriPublished = cur;
                plugin.WheelLedMasterBrightness = cur;
            }
        }

        /// <summary>
        /// Observe SimHub's per-zone "Brightness limiter and balance" sliders and publish
        /// settled changes to <see cref="MozaPlugin.WheelLedBrightnessRpm"/> /
        /// <c>…Buttons</c> / <c>…Knob</c> so the data thread can write each zone's own
        /// firmware register (<c>1B [G] FF</c>, G = 0/1/3). The factors arrive already
        /// multiplied by the global master (<c>GetEffectiveButtonsBrightness()</c> etc.),
        /// so <c>round(factor * 100)</c> is the firmware percentage for that zone and the
        /// global slider keeps working through the same path.
        ///
        /// Why this exists: before it, the per-zone sliders were consumed ONLY as
        /// per-frame RGB scaling of live colour frames, so a zone the firmware renders
        /// from its static palette (Button / Knob LED mode = Static) had no reachable
        /// dimmer at all — its slider looked dead while the live-driven RPM zone's worked.
        ///
        /// Discipline mirrors <see cref="TrackMasterBrightness"/>: the first observation
        /// per zone is a BASELINE that is never written (connecting must not overwrite the
        /// wheel's device-stored brightness), a zone stays silent until the user moves it
        /// off that baseline, and publishes are debounced so a drag costs one flash write.
        /// New-protocol wheels only — ES/ESX have a single legacy brightness register and
        /// ride <see cref="MozaPlugin.WheelLedMasterBrightnessRaw"/> instead.
        /// </summary>
        private void TrackZoneBrightness(
            MozaPlugin plugin, double rpmFactor, double buttonsFactor, double encodersFactor)
        {
            if (TrackOneZone(ZoneRpm, rpmFactor, out int rpm)) plugin.WheelLedBrightnessRpm = rpm;
            if (TrackOneZone(ZoneButtons, buttonsFactor, out int btn)) plugin.WheelLedBrightnessButtons = btn;
            if (TrackOneZone(ZoneKnob, encodersFactor, out int knob)) plugin.WheelLedBrightnessKnob = knob;
        }

        /// <summary>True (with the value to publish) when this zone's settled brightness
        /// changed. See <see cref="TrackZoneBrightness"/> for the baseline/engage rules.</summary>
        private bool TrackOneZone(int zone, double factor, out int value)
        {
            value = -1;
            if (double.IsNaN(factor) || double.IsInfinity(factor)) return false;
            int cur = (int)Math.Round(factor * 100.0);
            if (cur < 0) cur = 0; else if (cur > 100) cur = 100;

            if (!_zoneBriSeeded[zone])
            {
                _zoneBriSeeded[zone] = true;
                _zoneBriBaseline[zone] = cur;
                _zoneBriRaw[zone] = cur;
                _zoneBriPublished[zone] = cur;
                return false;
            }
            if (!_zoneEngaged[zone])
            {
                if (cur == _zoneBriBaseline[zone]) return false;
                _zoneEngaged[zone] = true;
            }

            var now = DateTime.UtcNow;
            if (cur != _zoneBriRaw[zone])
            {
                _zoneBriRaw[zone] = cur;
                _zoneBriRawUtc[zone] = now;
            }
            if (cur == _zoneBriPublished[zone]
                || (now - _zoneBriRawUtc[zone]).TotalMilliseconds < MasterBrightnessDebounceMs)
                return false;

            _zoneBriPublished[zone] = cur;
            value = cur;
            return true;
        }

        /// <summary>
        /// Per-frame factor for one zone with the firmware's own dimming divided back out.
        /// Once <see cref="TrackZoneBrightness"/> has this zone's register tracking its
        /// slider, the firmware applies that percentage to the live frame buffer AND the
        /// static palette, so applying it in software too would dim twice.
        /// <paramref name="zoneApplied"/> is what the applier actually wrote for THIS zone
        /// (-1 = never written, or the zone has no writable register on this wheel); in
        /// that case we fall back to <see cref="MasterCompensated"/> so the pre-feature
        /// master-only behaviour is preserved. Result is clamped downstream in
        /// <see cref="ScaleColorsForBrightness"/>.
        /// </summary>
        private static double ZoneCompensated(double effectiveFactor, int zoneApplied, int master)
        {
            if (zoneApplied < 0) return MasterCompensated(effectiveFactor, master);
            if (zoneApplied > 0) return effectiveFactor * 100.0 / zoneApplied;
            return effectiveFactor;
        }

        /// <summary>
        /// Does this group's firmware LED mode render the LIVE frame buffer? Modes are
        /// 0 = Off, 1 = SimHub, 2 = Static (see the wheel page's Button/Knob LED Mode
        /// selector); only mode 1 consumes the <c>19 [G]</c>/<c>1A [G]</c> live frames —
        /// in Off/Static the firmware renders its stored palette and discards them.
        ///
        /// <c>-1</c> (mode not yet read back from the wheel, the state at every cold
        /// connect) must return TRUE: treating unknown as "not SimHub" would blank
        /// button and knob LEDs for every user until the readback lands.
        /// </summary>
        private static bool GroupRendersLiveFrames(int groupMode) => groupMode != 0 && groupMode != 2;

        /// <summary>
        /// Compensate SimHub's per-frame LED-brightness factor for the wheel firmware
        /// master control. SimHub hands us <c>effective = (globalMaster/100) ×
        /// (perChannel/100)</c>. When the firmware group brightness is tracking the
        /// master (<paramref name="master"/> >= 0 — the value we pushed via
        /// <c>1B [G] FF</c>), the firmware already applies that master to these live
        /// frames, so software must apply only the per-channel term — otherwise the
        /// master dims the wheel twice. Dividing by the PUBLISHED master (not the live
        /// global) keeps software and firmware referencing the same value, so the
        /// hand-off across the debounce window is smooth. <paramref name="master"/> &lt; 0
        /// (user hasn't engaged the master) or 0 (firmware already fully dark) → apply
        /// the factor unchanged, matching the pre-feature behaviour. The result is
        /// clamped downstream in <see cref="ScaleColorsForBrightness"/>.
        /// </summary>
        private static double MasterCompensated(double effectiveFactor, int master)
        {
            if (master > 0) return effectiveFactor * 100.0 / master;
            return effectiveFactor;
        }

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

        // When streamBase is set, each 20-byte chunk is sent to its OWN coalescing
        // stream slot (streamBase + chunkIndex) instead of the throttled one-shot
        // FIFO — so a co-resident value stream can't starve the colour stream. Each
        // chunk coalesces INDEPENDENTLY (a new chunk-0 supersedes only the old
        // chunk-0; later chunks are never dropped), which is why one slot PER CHUNK
        // is required. maxStreamChunks bounds the slot range; any chunk beyond it
        // falls back to the one-shot lane (defensive — no shipped model exceeds it).
        internal static void SendColorChunks(MozaPlugin plugin, Color[] colors, int count,
            string command, int[]? indexMap = null,
            StreamKind? streamBase = null, int maxStreamChunks = 0)
        {
            int dataLen = count * 4;
            var colorData = new byte[dataLen];

            for (int i = 0; i < count; i++)
            {
                int offset = i * 4;
                colorData[offset] = (byte)(indexMap != null ? indexMap[i] : i);
                colorData[offset + 1] = colors[i].R;
                colorData[offset + 2] = colors[i].G;
                colorData[offset + 3] = colors[i].B;
            }

            // Emit variable-length chunks of up to 5 LEDs (20 bytes) each, matching
            // PitHouse byte-for-byte: the wheel frames on the length byte, so the final
            // partial chunk carries ONLY its real LEDs and is short (14 buttons → 5+5+4,
            // last frame 16 bytes). Do NOT pad the last chunk up to 20 bytes with a
            // filler record — a trailing index-0xFF record corrupts the button-input
            // matrix on stricter wheel firmware (issue #100: TSW on FW U-V01 — buttons
            // stop registering after the first button-LED frame), and PitHouse never
            // emits one (0/90 live colour frames on this wheel). Sending only the real
            // LEDs also avoids the original zero-pad "button 0 flicker" the 0xFF padding
            // was working around. Chunk COUNT is unchanged (ceil(dataLen/20)), so the
            // per-chunk stream-slot assignment below is unaffected.
            int chunkIdx = 0;
            for (int pos = 0; pos < dataLen; pos += 20)
            {
                int len = Math.Min(20, dataLen - pos);
                var chunk = new byte[len];
                Array.Copy(colorData, pos, chunk, 0, len);
                if (streamBase.HasValue && chunkIdx < maxStreamChunks)
                    plugin.DeviceManager.WriteArrayStream(
                        command, chunk, (StreamKind)((int)streamBase.Value + chunkIdx));
                else
                    plugin.DeviceManager.WriteArray(command, chunk);
                chunkIdx++;
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
            MozaLog.Debug($"[AZOM] IndividualLEDs diag {key}");
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
