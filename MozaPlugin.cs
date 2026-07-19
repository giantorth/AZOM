using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using MozaPlugin.Devices;
using MozaPlugin.Devices.StalksTruckSim;
using MozaPlugin.Hardware;
using MozaPlugin.Protocol;
using MozaPlugin.Resources;
using MozaPlugin.Settings;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.TileServer;
using MozaPlugin.UI.UpdateCheck;
using Timer = System.Timers.Timer;

namespace MozaPlugin
{
    [PluginDescription("Configure MOZA Racing hardware and send SimHub game telemetry to wheel/dashboard RPM LEDs")]
    [PluginAuthor("giantorth")]
    [PluginName("AZOM")]
    public class MozaPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        internal static MozaPlugin? Instance { get; private set; }

        // Persistent wire: survives plugin reload on game switch so the
        // wheel never sees the ~10–14s sess=0x09 settle. Disposed only on
        // process exit or on wheel unplug (Init checks "still connected?").
        private static MozaSerialConnection? s_persistentConnection;
        private static TelemetrySender? s_persistentTelemetrySender;
        // One-shot guard so the stale-instance DataUpdate fallback warns once.
        private bool _warnedStaleDataFeed;

        // Auto-standby reconcile state (see ApplyAutoStandby). Work Mode standby
        // fully powers down the wheel/display, so it must only engage after a
        // genuine idle period — never immediately, never on startup, and never
        // while the user is using the wheel or the plugin UI.
        //
        // DataUpdate stamps the feed timestamp + last GameRunning; a fresh feed
        // with GameRunning means a game is active (DataUpdate goes quiet when no
        // game runs, so a stale feed implies no game). _autoStandbyLastActivityTicks
        // is the last-activity clock — bumped by a running game, by physical HID
        // input (wheel/pedals/buttons past a deadband), and by UI interaction
        // (NotifyUserActivity). Standby engages only once now-lastActivity exceeds
        // the user's timeout AND no game is active. _autoStandbyApplied caches the
        // last value written so the reconcile is idempotent (writes only on change).
        private long _autoStandbyLastDataUpdateTicks;
        private volatile bool _autoStandbyLastGameRunning;
        private long _autoStandbyLastActivityTicks; // 0 until first reconcile (lazy baseline)
        private volatile int _autoStandbyApplied = -1; // -1 unknown / 0 active / 1 standby
        private const long AutoStandbyFeedStaleMs = 3000;

        // True while a game is actively feeding telemetry: a fresh DataUpdate (within
        // AutoStandbyFeedStaleMs) AND GameRunning. DataUpdate goes quiet when no game
        // runs, so a stale feed means no game even if the last GameRunning we saw was
        // true. GameRunning stays true through menus/pauses, so this spans the whole
        // session. The LED keepalive reads this to never let the wheel sleep mid-game.
        public bool IsGameActive
        {
            get
            {
                long lastFeed = Interlocked.Read(ref _autoStandbyLastDataUpdateTicks);
                bool feedFresh = (DateTime.UtcNow.Ticks - lastFeed) <= AutoStandbyFeedStaleMs * TimeSpan.TicksPerMillisecond;
                return feedFresh && _autoStandbyLastGameRunning;
            }
        }
        // HID-activity baseline (last sampled positions; change past the deadband
        // counts as physical use). _asHidBaselined gates the first sample so it
        // seeds the baseline instead of registering as activity.
        private bool _asHidBaselined;
        private int _asSteer = -1, _asThrottle, _asBrake, _asClutch, _asHandbrake, _asLeftPaddle, _asRightPaddle, _asButtonHash;

        // CoAP stub child-process manager. Persistent for the same reason the
        // wire is: stopping and restarting the stub on every plugin reload is
        // wasted work (the stub is a long-lived "PitHouse impersonator" child
        // process with no per-plugin-instance state) AND under Wine/Proton the
        // teardown path (Process.Kill + JobObject.Dispose) intermittently
        // hangs — observed 2026-05-25: End() on the SECOND game switch wedged
        // in CoapStubManager.Stop() between RestoreRegistryRedirect (logged)
        // and the "CoAP stub stopped" line (never logged). Leaving the stub
        // alive across reloads avoids the unsafe teardown path entirely.
        //
        // Disposed on full process exit (OnAppDomainProcessExit) AND on cold-
        // start re-entry when SdkEmulationEnabled was toggled off.
        private static Sdk.CoapStubManager? s_persistentSdkStubManager;

        // AppDomain.ProcessExit registration is one-shot per process. End()
        // intentionally leaves the persistent wire alive across plugin
        // reloads (game switches) — the wheel never sees the 10–14 s
        // sess=0x09 settle, so the next Init reuses an already-engaged
        // session. On full SimHub exit that optimization becomes a
        // liability: with no SessionClose 0x01/0x02/0x03 on the way out
        // the wheel retains its host-side sess state, and on the next
        // SimHub launch keeps emitting heartbeat chunks on its old
        // sess=0x09 instead of re-engaging via a fresh SessionOpen 0x81.
        // The s09 watchdog then burns its 56 s retry budget (10 rounds,
        // DisplayWatchdog.S09BackoffMs) and parks the dashboard
        // pipeline — observed as "dashboard display failed to connect"
        // on cold start until the user toggles telemetry.
        //
        // The ProcessExit hook closes those sessions on the persistent
        // wire so the wheel sees a clean shutdown regardless of which
        // End() path ran (keepWireAlive=true is the common case).
        private static int s_processExitHandlerRegistered;
        // Detection-flag bag captured alongside the persistent wire. When a
        // game switch reloads the plugin while the hardware stays physically
        // attached, restoring this preserves sub-device tab visibility
        // (handbrake/pedals/hub/dash) instead of waiting for presence probes
        // to re-ACK on the new instance — which is unreliable on the reused
        // wire and would otherwise leave tabs hidden until SimHub restarts.
        private static DeviceDetectionState? s_persistentDetectionState;

        // Update-check dedupe: the GitHub Releases query is per-process, not
        // per-plugin-Init. SimHub reloads the plugin on every game switch, so
        // without this guard a user switching games could burn through the
        // unauthenticated GitHub rate limit (60 req/hr per IP). Set once
        // when the check kicks off in Init; never cleared. The "Check now"
        // button in the About tab is the only way to force a re-check within
        // the same SimHub process lifetime.
        private static bool s_updateCheckStarted;

        private MozaSerialConnection _connection = null!;
        private MozaData _data = null!;
        private MozaDeviceManager _deviceManager = null!;
        private MozaAb9DeviceManager _ab9Manager = null!;
        private MozaDashboardDeviceManager _dashboardManager = null!;
        // Dedicated connection for a Universal Hub when a wheelbase is also
        // present (base = primary, hub enumerates its own peripherals).
        private MozaHubDeviceManager _hubManager = null!;
        // Dedicated connection for the wheelbase AFTER a base→hub primary
        // migration (broken base + wheel on hub): keeps base-only traffic
        // (temps/state/FFB/ambient) on the base port while the primary drives the
        // wheel over the hub. The mirror of _hubManager. Dormant (never opens)
        // while the base IS the primary — gated on PrimaryBoundToHub.
        private MozaBaseDeviceManager _baseManager = null!;
        private MozaMBoosterRegistry? _mboosterRegistry;
        // Dedicated lane for peripherals plugged STRAIGHT into the PC (their own
        // USB port + PID) rather than through a base/hub — one connection per
        // attached pedal set / handbrake. Config/calibration only; axes stay HID.
        private MozaStandalonePeripheralRegistry? _peripheralRegistry;

        // Captures unsolicited firmware-debug frames (raw wire group 0x0E,
        // subtype 0x05) for the Diagnostics tab. Owned by the plugin so the
        // ring buffer's lifetime matches the connection's: cleared on
        // disconnect so chatter from a prior wheel doesn't leak into a new
        // session's diagnostics view. See OnMessageReceived (0x0E branch)
        // for the capture site and DiagnosticsTextBuilder.BuildFirmwareDebug
        // for the render site.
        private readonly global::MozaPlugin.Diagnostics.FirmwareDebugLog _firmwareDebugLog
            = new global::MozaPlugin.Diagnostics.FirmwareDebugLog();
        internal global::MozaPlugin.Diagnostics.FirmwareDebugLog FirmwareDebugLogForDiagnostics
            => _firmwareDebugLog;

        // Third-party SDK (CoAP-over-UDP) emulation server + name-impersonation
        // stub process. Both are gated on Settings.SdkEmulationEnabled and
        // require a plugin restart to toggle (no runtime enable/disable —
        // see Init()). Null when disabled, so the UI tab uses null-conditional
        // access.
        private Sdk.MozaSdkCoapServer? _sdkServer;
        private Sdk.PitHouseUdp.MozaControlUdpServer? _controlUdpServer;
        private Sdk.CoapStubManager? _sdkStubManager;
        // Serializes runtime start/stop of the SDK-emulation surface so the
        // live UI toggles (which fire on the WPF thread, off-loaded to the
        // ThreadPool) can't race Init()/End() or each other. Guards the
        // _sdkServer / _controlUdpServer / _sdkStubManager fields and the
        // s_persistentSdkStubManager static during transitions.
        private readonly object _sdkLifecycleGate = new object();
        internal global::MozaPlugin.Protocol.PendingResponseTracker PendingResponses { get; }
            = new global::MozaPlugin.Protocol.PendingResponseTracker();
        // Internal: ProfileCoordinator.ClearSettings replaces this field with a
        // fresh instance. Everything else reads it live via the Settings property.
        internal MozaPluginSettings _settings = null!;
        private Timer _pollTimer = null!;
        private Timer _retryTimer = null!;
        private Timer _reconnectTimer = null!;
        // CAS re-entry guard: a 5 s reconnect tick can outlast its interval
        // (probe fallback ~600 ms/port under Wine, Disconnect joins at 1 s) —
        // overlapping ticks must not run TryConnect* concurrently on a lane.
        private int _reconnectTickInProgress;
        // 1 once SystemEvents.PowerModeChanged is subscribed, so End()/
        // CleanupPartialInit unsubscribe exactly once. PowerModeChanged is a
        // STATIC event — a live subscription leaks this plugin instance (and
        // double-fires the resume handler) across the game-switch reload if not
        // detached. See OnPowerModeChanged.
        private int _powerModeHooked;
        // Hub detection belongs ONLY to the dedicated hub connection (_hubManager),
        // which probes for a Universal Hub on the hub's OWN port and skips the
        // wheelbase port. The base/wheelbase connection must NEVER emit hub calls
        // (hub-port-power / cmd 0x64): that device answered the base probe, so it is
        // a known wheelbase and rejects hub commands ("Unexpected cmd: 100").
        private MozaHidReader _hidReader = null!;
        private StalksTruckSimController _stalksController = null!;
        private PluginManager _pluginManager = null!;
        private SimHubPropertyResolver _propertyResolver = null!;
        internal SimHubPropertyResolver PropertyResolver => _propertyResolver;
        private int _headProbeTick;   // diagnostic heading-probe pacing
        private int _headProbeCount;
        private HardwareApplier _hardwareApplier = null!;
        internal HardwareApplier HardwareApplier => _hardwareApplier;
        // SimHub's shared/master LED-brightness slider (0..100), published by the
        // active wheel LED driver from LedModuleSettings.GlobalBrightnessPreset. The
        // driver reads it per-frame off SimHub's LED thread; DataUpdate applies it on
        // the data thread. -1 until the user first moves the slider — the wheel's
        // device-stored brightness is left untouched until then. Drives the firmware
        // group brightness (rpm/buttons/knobs) equally via ApplyMasterWheelLedBrightness.
        internal volatile int WheelLedMasterBrightness = -1;
        private int _masterLedBrightnessApplied = -2; // DataUpdate-thread-local change gate
        private DeviceProber _deviceProber = null!;
        internal DeviceProber DeviceProber => _deviceProber;
        // Peripheral-enumeration prober for the dedicated hub pipe. Shares
        // _data + DetectionState with the primary prober; drivesTelemetry:false
        // so it never touches the singular TelemetrySender.
        private DeviceProber _hubDeviceProber = null!;
        // Base-only prober for the dedicated base-aux pipe (post base→hub
        // migration). Shares _data + DetectionState; drivesTelemetry:false so it
        // never touches the singular TelemetrySender (telemetry runs on the hub).
        private DeviceProber _baseDeviceProber = null!;
        // Multi-connection management + base↔hub migration — see
        // Devices/ConnectionCoordinator.cs. Constructed in Init after the
        // managers/probers it injects; timer/serial call sites null-guard.
        private ConnectionCoordinator? _connectionCoordinator;
        private DashboardBindingCoordinator _dashboardBindingCoordinator = null!;
        internal DashboardBindingCoordinator DashboardBindingCoordinator => _dashboardBindingCoordinator;
        // CM2/CM1 dual-display coordination — see Telemetry/DualDisplayCoordinator.cs.
        // Constructed alongside _dashboardBindingCoordinator (after the persistent
        // DetectionState swap); call sites on timer/serial threads null-guard.
        private DualDisplayCoordinator? _dualDisplay;
        // FSR1/CM1 field mappings + active dashboard index store. Constructed
        // early in Init (right after _settings loads) — before the serial
        // MessageReceived subscription — so the shims below are never hit on a
        // null reference from the read thread.
        private Fsr1Cm1MappingCoordinator _fsr1Cm1Mapping = null!;
        // SimHub property/action registration — see SimHubRegistrar.cs.
        private SimHubRegistrar _simHubRegistrar = null!;
        // Settings persistence + profile system + per-wheel-page accessors —
        // see Settings/ProfileCoordinator.cs. Constructed right after _settings
        // loads, before any serial/timer callback can hit the shims below.
        private ProfileCoordinator _profileCoordinator = null!;

        // ===== DashboardBindingCoordinator shims (external API surface) =====
        internal void ApplyTelemetrySettings()
        {
            _dashboardBindingCoordinator.ApplyTelemetrySettings();
            EnsureCm2Pipeline();
        }

        /// <summary>
        /// Queue a re-apply of the current profile's saved
        /// <c>TelemetryDashboardKey</c> against the currently-attached
        /// wheel. Called from the wheel-hot-swap path so the new wheel ends
        /// up bound to the user's saved choice instead of whatever slot it
        /// boots to. Tries the apply immediately; if the wheel state isn't
        /// ready yet (configJsonList empty), sets the dashboard-binding
        /// coordinator's pending key so the next PollStatus tick retries.
        /// </summary>
        internal void RequestSavedDashboardReapply()
        {
            try
            {
                var profile = _settings?.ProfileStore?.CurrentProfile;
                if (profile == null) return;
                if (string.IsNullOrEmpty(profile.TelemetryDashboardKey)) return;
                bool applied = false;
                try { applied = ApplyTelemetryDashboardFromProfile(profile); }
                catch (Exception ex)
                {
                    MozaLog.Warn(
                        $"[AZOM] RequestSavedDashboardReapply: apply threw — {ex.Message}");
                    return;
                }
                if (!applied)
                {
                    _dashboardBindingCoordinator.SetPendingDashboardKey(profile.TelemetryDashboardKey!);
                    MozaLog.Debug(
                        $"[AZOM] RequestSavedDashboardReapply: deferred " +
                        $"(key={profile.TelemetryDashboardKey}) — PollStatus retry will fire " +
                        "once wheel state is ready");
                }
                else
                {
                    _dashboardBindingCoordinator.ClearPendingDashboardKey();
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] RequestSavedDashboardReapply: outer error — {ex.Message}");
            }
        }
        internal void RestartTelemetry() => _dashboardBindingCoordinator.RestartTelemetry();
        internal bool ApplyTelemetryDashboardFromProfile(MozaProfile profile) => _dashboardBindingCoordinator.ApplyTelemetryDashboardFromProfile(profile);
        internal void OnDashboardSwitched(uint slot) => _dashboardBindingCoordinator.OnDashboardSwitched(slot);
        internal void OnDashboardSwitched() => _dashboardBindingCoordinator.OnDashboardSwitched();
        internal void SetTelemetryEnabled(bool enabled) => _dashboardBindingCoordinator.SetTelemetryEnabled(enabled);
        internal void StartTelemetryIfReady()
        {
            // FSR V1 screen runs on its own driver (independent of the tier-def
            // sender), so a CM2 dash can still use the sender concurrently.
            _dualDisplay?.StartFsr1DriverIfNeeded();
            _dashboardBindingCoordinator.StartTelemetryIfReady();
            EnsureCm2Pipeline();
        }

        // ===== DualDisplayCoordinator shim (external API surface) =====
        // CM2/CM1 dual-display coordination lives in Telemetry/DualDisplayCoordinator.cs.
        // Null-guarded: PollStatus/serial callbacks can fire before the coordinator
        // is constructed in Init (same window as _dashboardBindingCoordinator).
        internal void EnsureCm2Pipeline() => _dualDisplay?.EnsureCm2Pipeline();

        /// <summary>
        /// Raised when the active telemetry dashboard selection is updated
        /// programmatically (profile load / deferred retry). Subscribers must
        /// marshal to the UI thread before touching WPF.
        /// </summary>
        public event EventHandler? DashboardSelectionChanged;

        internal void RaiseDashboardSelectionChangedInternal()
        {
            int subs = DashboardSelectionChanged?.GetInvocationList().Length ?? 0;
            MozaLog.Debug(
                $"[AZOM] Raising DashboardSelectionChanged (subscribers={subs}, " +
                $"profileName='{_settings?.TelemetryProfileName}', " +
                $"mzdash='{_settings?.TelemetryMzdashPath}')");
            try { DashboardSelectionChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { MozaLog.Warn("[AZOM] DashboardSelectionChanged subscriber threw: " + ex.Message); }
        }

        // ===== HardwareApplier shims (external API surface) =====
        // Keep callsites in Devices/UI files compiling without churn.
        internal void WriteIfWheelDetected(string command, int value) => _hardwareApplier.WriteIfWheelDetected(command, value);
        internal void WriteIfDashDetected(string command, int value) => _hardwareApplier.WriteIfDashDetected(command, value);
        internal void WriteIfBaseConnected(string command, int value) => _hardwareApplier.WriteIfBaseConnected(command, value);
        internal void WriteFloatIfBaseConnected(string command, int value) => _hardwareApplier.WriteFloatIfBaseConnected(command, value);
        internal void WriteIfHandbrakeDetected(string command, int value) => _hardwareApplier.WriteIfHandbrakeDetected(command, value);
        internal void WriteFloatIfHandbrakeDetected(string command, int value) => _hardwareApplier.WriteFloatIfHandbrakeDetected(command, value);
        internal void WriteIfPedalsDetected(string command, int value) => _hardwareApplier.WriteIfPedalsDetected(command, value);
        internal void WriteFloatIfPedalsDetected(string command, int value) => _hardwareApplier.WriteFloatIfPedalsDetected(command, value);
        internal void WriteIfShifterDetected(string command, int value) => _hardwareApplier.WriteIfShifterDetected(command, value);
        internal void WriteArrayIfShifterDetected(string command, byte[] payload) => _hardwareApplier.WriteArrayIfShifterDetected(command, payload);
        internal void WriteIfBaseAmbientSupported(string command, int value) => _hardwareApplier.WriteIfBaseAmbientSupported(command, value);
        internal void WriteColorIfWheelDetected(string command, byte r, byte g, byte b) => _hardwareApplier.WriteColorIfWheelDetected(command, r, g, b);
        internal void WriteColorIfDashDetected(string command, byte r, byte g, byte b) => _hardwareApplier.WriteColorIfDashDetected(command, r, g, b);
        internal void WriteColorIfBaseAmbientSupported(string command, byte r, byte g, byte b) => _hardwareApplier.WriteColorIfBaseAmbientSupported(command, r, g, b);
        internal void WriteArrayIfWheelDetected(string command, byte[] payload) => _hardwareApplier.WriteArrayIfWheelDetected(command, payload);
        internal void ApplyWheelToHardware(MozaProfile? profile) => _hardwareApplier.ApplyWheelToHardware(profile);
        internal void ApplyDashToHardware(MozaProfile? profile) => _hardwareApplier.ApplyDashToHardware(profile);
        internal void ApplyBaseToHardware(MozaProfile? profile) => _hardwareApplier.ApplyBaseToHardware(profile);
        internal void ApplyBaseAmbientToHardware(MozaProfile? profile) => _hardwareApplier.ApplyBaseAmbientToHardware(profile);
        internal void ApplyHandbrakeToHardware(MozaProfile? profile) => _hardwareApplier.ApplyHandbrakeToHardware(profile);
        internal void ApplyPedalsToHardware(MozaProfile? profile) => _hardwareApplier.ApplyPedalsToHardware(profile);
        internal void ApplyShifterToHardware(MozaProfile? profile) => _hardwareApplier.ApplyShifterToHardware(profile);
        internal void ApplyAb9ToHardware(MozaProfile? profile) => _hardwareApplier.ApplyAb9ToHardware(profile);
        internal void ApplyWheelExtensionSettings(MozaWheelExtensionSettings extSettings, string? pageModelPrefix = null) => _hardwareApplier.ApplyWheelExtensionSettings(extSettings, pageModelPrefix);
        /// <summary>
        /// LED-colour write from UI handlers, gated on the live telemetry pipeline (see
        /// <see cref="Hardware.HardwareApplier.WriteLedColorIfWheelDetected"/>). Skip
        /// during active telemetry so per-click cmd 0x27 / cmd 0x1F writes don't
        /// flicker the live overlay; the persisted overlay (set by the caller) is
        /// pushed via the next ApplyWheelToHardware after telemetry stops.
        /// </summary>
        internal void WriteLedColorIfWheelDetected(string command, byte r, byte g, byte b, Devices.LedKind kind)
            => _hardwareApplier.WriteLedColorIfWheelDetected(command, r, g, b, kind);
        /// <summary>
        /// Re-push the stored static palette for an LED group from MozaData to
        /// the wheel's EEPROM. Called from the per-group mode combo handlers on
        /// transition to Static (val=2) to bring EEPROM back in sync with any
        /// edits the user made while the group was in SimHub mode (those edits
        /// land in _data + overlay but the wheel write is suppressed by the
        /// per-group gate in WriteLedColorIfWheelDetected).
        /// </summary>
        internal void RepushStaticPalette(Devices.LedKind kind) => _hardwareApplier.RepushStaticPalette(kind);
        internal void ApplyDashExtensionSettings(MozaDashExtensionSettings extSettings) => _hardwareApplier.ApplyDashExtensionSettings(extSettings);
        internal void ApplyBaseExtensionSettings(MozaBaseExtensionSettings extSettings) => _hardwareApplier.ApplyBaseExtensionSettings(extSettings);
        internal void ClearLedsOnHardware() => _hardwareApplier.ClearLedsOnHardware();

        private TelemetrySender? _telemetrySender;

        // Standalone FSR V1 group-0x42 display driver (dev 0x17), independent of the
        // tier-def _telemetrySender so an FSR1 screen + a CM2 dash run concurrently.
        internal Telemetry.Fsr1DisplayDriver? _fsr1Driver;

        // Dedicated tier-def sender that drives a CM2 dash whenever a CM2 is present —
        // regardless of the wheel (display wheel, screenless wheel, or no wheel at all).
        // Targets dev 0x14 on the shared wheelbase connection (lane base 18) or dev 0x12
        // on the CM2's own USB connection (lane base 0). Null until a CM2 is detected.
        internal TelemetrySender? _cm2Sender;

        // Standalone CM1 base-bridged dash driver (group-0x35 → dev 0x14). Used instead
        // of the tier-def _cm2Sender when a bridged dash is a CM1 (no tier-def catalog).
        internal Telemetry.Cm1DisplayDriver? _cm1Driver;
        // True if Init reused the persistent connection/sender from a
        // prior plugin instance. End() respects this flag and skips
        // disposing them so the next Init can pick up where we left off.
        private bool _usingPersistentWire;
        internal DashboardProfileStore DashProfileStore { get; } = new DashboardProfileStore();
        internal DashboardCache DashCache { get; private set; } = null!;

        // Device detection state shared with serial-reader, poll timer, UI, telemetry.
        internal DeviceDetectionState DetectionState { get; private set; } = new DeviceDetectionState();

        // AB9 host-rendered engine-vibration worker. See Devices/Ab9EngineVibrationWorker.cs.
        private Ab9EngineVibrationWorker? _ab9Worker;

        // Wheelbase LFE worker (complex gearshift / engine / ABS on base fw
        // >= 1.2.10.10). See Devices/BaseLfeEffectWorker.cs.
        private BaseLfeEffectWorker? _baseLfeWorker;

        // Keeps the process responsive in the background: opts out of EcoQoS
        // throttling during active gameplay, and holds the 1 ms timer during gameplay
        // (or whenever the FFB Lag Fix override is on). See ProcessResponsivenessManager.
        private ProcessResponsivenessManager? _responsiveness;

        // Control Mapper IVariantProvider bridge — see ControlMapper/. Registration
        // is reflection-based against an internal SimHub API, so the bridge is wrapped
        // in defensive guards and gated on MozaPluginSettings.EnableControlMapperVariants.
        // Constructed in Init when the toggle is on; null otherwise.
        private ControlMapper.ControlMapperBridge? _controlMapperBridge;
        // Tick budget for retrying registration in DataUpdate when ControlMapperPlugin
        // wasn't loaded yet at Init time. ~50 ticks (~0.8 s at 60 Hz). 0 = stop trying.
        private int _controlMapperRetryTicks;
        private const int ControlMapperRegisterRetryTickBudget = 50;

        // Guard against concurrent/duplicate telemetry Start() dispatch.
        // Internal so DashboardBindingCoordinator can Interlocked.* against it.
        internal int _telemetryStartRequested;

        // Set during End() so in-flight callbacks can bail out.
        internal static volatile bool IsShuttingDown;

        internal static readonly string[] StatusPollCommands = new[]
        {
            "base-mcu-temp", "base-mosfet-temp", "base-motor-temp",
            "base-state",
        };

        // --- Per-device settings read commands ---
        // These are sent only after the corresponding device is detected,
        // rather than blasting all commands on connect.

        // Settings read after the 0x22-group probe confirms the base ships the
        // ambient LED strip. brightness is the probe itself — listed here too so
        // re-syncs cover it; harmless, the second response just refreshes the
        // already-set value.
        
        public PluginManager PluginManager { set => _pluginManager = value; }
        public ImageSource? PictureIcon => NavIcon.Value;
        public string LeftMenuTitle => "AZOM";

        // Wheel-with-screen nav icon. Cyan tint for SimHub's dark nav.
        private static readonly Lazy<ImageSource> NavIcon = new Lazy<ImageSource>(BuildNavIcon);

        private static ImageSource BuildNavIcon()
        {
            // Paths from tools/icon-smooth/smooth.py. EvenOdd makes the screen + grips holes.
            const string silhouette =
                "M120 59.796 C138.96 59.796 162.619 61.39 176.88 63.24 C185.6 64.371 192.128 65.002 197.77 67.42 C202.233 69.332 205.348 71.131 208.68 75.08 C213.705 81.036 219.158 93.188 221.68 102.47 C224.041 111.16 224.598 120.366 224 128.94 C223.422 137.219 221.451 145.183 218.43 153.08 C215.197 161.531 209.84 173.848 204.73 177.92 C201.674 180.354 197.823 180.838 194.98 180.71 C192.71 180.608 190.703 179.682 188.95 178.38 C187.008 176.938 184.96 174.482 184.07 172.12 C183.182 169.761 182.957 167.317 183.61 164.22 C184.683 159.129 191.556 150.924 193.36 145.19 C194.708 140.903 196.706 135.328 194.98 133.35 C193.245 131.362 186.24 132.564 182.91 133.35 C180.326 133.96 178.176 134.53 176.41 136.6 C173.65 139.836 173.746 149.614 171.07 153.54 C169.14 156.372 167.635 158.023 163.88 159.35 C155.7 162.242 134.627 159.35 120 159.35 C105.373 159.35 84.3 162.242 76.12 159.35 C72.365 158.023 70.86 156.372 68.93 153.54 C66.254 149.614 66.35 139.836 63.59 136.6 C61.824 134.53 59.674 133.96 57.09 133.35 C53.76 132.564 46.755 131.362 45.02 133.35 C43.294 135.328 45.292 140.903 46.64 145.19 C48.444 150.924 55.317 159.129 56.39 164.22 C57.043 167.317 56.818 169.761 55.93 172.12 C55.04 174.482 52.992 176.938 51.05 178.38 C49.297 179.682 47.29 180.608 45.02 180.71 C42.177 180.838 38.326 180.354 35.27 177.92 C30.16 173.848 24.803 161.531 21.57 153.08 C18.549 145.183 16.578 137.219 16 128.94 C15.402 120.366 15.959 111.16 18.32 102.47 C20.842 93.188 26.295 81.036 31.32 75.08 C34.652 71.131 37.767 69.332 42.23 67.42 C47.872 65.002 54.4 64.371 63.12 63.24 C77.381 61.39 101.04 59.796 120 59.796 Z";
            const string rightGrip =
                "M181.52 90.63 C183.754 90.597 186.679 90.747 188.48 92.03 C190.368 93.376 191.199 96.014 192.43 98.76 C194.143 102.582 196.537 109.76 197.07 113.15 C197.35 114.932 197.609 116.149 197.07 117.33 C196.504 118.57 195.366 119.687 193.59 120.35 C190.16 121.631 179.84 121.631 176.41 120.35 C174.634 119.687 173.725 119.072 172.93 117.33 C171.2 113.539 171.607 99.554 172.93 95.51 C173.518 93.712 174.253 92.839 175.48 92.03 C176.951 91.06 179.432 90.661 181.52 90.63 Z";
            const string leftGrip =
                "M64.52 92.03 C65.747 92.839 66.482 93.712 67.07 95.51 C68.393 99.554 68.8 113.539 67.07 117.33 C66.275 119.072 65.366 119.687 63.59 120.35 C60.16 121.631 49.84 121.631 46.41 120.35 C44.634 119.687 43.496 118.57 42.93 117.33 C42.391 116.149 42.65 114.932 42.93 113.15 C43.463 109.76 45.857 102.582 47.57 98.76 C48.801 96.014 49.632 93.376 51.52 92.03 C53.321 90.747 56.246 90.597 58.48 90.63 C60.568 90.661 63.049 91.06 64.52 92.03 Z";

            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(Geometry.Parse(silhouette));
            group.Children.Add(new RectangleGeometry(new System.Windows.Rect(74, 72, 92, 49), 5, 5));
            group.Children.Add(Geometry.Parse(rightGrip));
            group.Children.Add(Geometry.Parse(leftGrip));

            var brush = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
            var wheel = new GeometryDrawing(brush, null, group);

            // Transparent backing rect gives square bounds so SimHub scales the whole
            // wheel uniformly instead of stretching the bare geometry box.
            var canvas = new GeometryDrawing(
                System.Windows.Media.Brushes.Transparent, null,
                new RectangleGeometry(new System.Windows.Rect(16, 16, 208, 208)));

            var root = new DrawingGroup();
            root.Children.Add(canvas);
            root.Children.Add(wheel);

            var image = new DrawingImage(root);
            image.Freeze();
            return image;
        }

        internal bool ConnectionEnabled => _settings?.ConnectionEnabled ?? true;

        internal MozaData Data => _data;
        internal MozaDeviceManager DeviceManager => _deviceManager;
        internal MozaPluginSettings Settings => _settings;
        internal bool IsNewWheelDetected => DetectionState.NewWheelDetected;
        internal bool IsOldWheelDetected => DetectionState.OldWheelDetected;
        internal Devices.WheelModelInfo? WheelModelInfo { get; set; }
        /// <summary>True once the wheel has reported its model name and a per-page
        /// guid can be resolved. UI handlers that persist into per-page bundles
        /// (sleep / idle / wheel overlay) must gate on this — without a guid the
        /// dict write silently drops, and the value is lost on restart.</summary>
        internal bool IsWheelPageReady => GetCurrentWheelPageGuid().HasValue;

        /// <summary>Wheel LED group g (2=Single, 3=Rotary, 4=Ambient). Detected on brightness read.</summary>
        internal bool IsWheelLedGroupPresent(int group) => DetectionState.IsWheelLedGroupPresent(group);
        /// <summary>Device extension owns wheel LED settings; plugin profile-apply skips wheel writes.</summary>
        private volatile bool _deviceExtensionActive;
        internal bool DeviceExtensionActive
        {
            get => _deviceExtensionActive;
            set => _deviceExtensionActive = value;
        }

        private volatile bool _dashDeviceExtensionActive;
        internal bool DashDeviceExtensionActive
        {
            get => _dashDeviceExtensionActive;
            set => _dashDeviceExtensionActive = value;
        }

        private volatile bool _baseAmbientDeviceExtensionActive;
        internal bool BaseAmbientDeviceExtensionActive
        {
            get => _baseAmbientDeviceExtensionActive;
            set => _baseAmbientDeviceExtensionActive = value;
        }

        /// <summary>
        /// Model prefixes with an active device extension. Copy-on-write: reads see a
        /// consistent snapshot; mutations (extension init/end only) allocate a new set.
        /// </summary>
        private volatile HashSet<string> _activeModelPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal void RegisterActiveModelPrefix(string prefix)
        {
            if (!string.IsNullOrEmpty(prefix) && prefix != MozaDeviceConstants.OldProtocolMarker)
            {
                var newSet = new HashSet<string>(_activeModelPrefixes, StringComparer.OrdinalIgnoreCase);
                newSet.Add(prefix);
                _activeModelPrefixes = newSet;
            }
        }

        internal void UnregisterActiveModelPrefix(string prefix)
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                var newSet = new HashSet<string>(_activeModelPrefixes, StringComparer.OrdinalIgnoreCase);
                newSet.Remove(prefix);
                _activeModelPrefixes = newSet;
            }
        }

        /// <summary>
        /// Returns true if a model-specific device extension is active for the given wheel model.
        /// </summary>
        internal bool IsModelSpecificExtensionActive(string modelName)
        {
            if (string.IsNullOrEmpty(modelName) || _activeModelPrefixes.Count == 0)
                return false;

            foreach (var prefix in _activeModelPrefixes)
            {
                if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Set to true when a new device definition is deployed at runtime.
        /// The plugin settings panel shows a restart notice when this is true.
        /// </summary>
        internal volatile bool DeviceDefinitionDeployed;

        /// <summary>
        /// UTC timestamp of the last <see cref="Init"/> call. The UI hint-builder
        /// uses this as a settling reference so banners ("profile not added",
        /// "port in use") don't flash during the first few seconds of plugin
        /// startup before discovery and probe responses have arrived.
        /// </summary>
        internal DateTime StartupUtc { get; private set; } = DateTime.UtcNow;

        /// <summary>
        /// True when a standalone-USB dashboard (CM2 = 0x0025) is connected on
        /// its own dedicated port. Lets dashboard detection flip on USB PID
        /// alone, without waiting for a wheelbase relay or wheel-side ack.
        /// </summary>
        private bool IsStandaloneDashboardUsbConnection => DashboardUsbConnected;

        internal bool IsDashDetected =>
            DetectionState.DashDetected || IsStandaloneDashboardUsbConnection;

        /// <summary>
        /// True when a CM2 is present at all — on its own USB cable OR bridged through
        /// the wheelbase — independent of whether a wheel (and what kind) is attached.
        /// This is the "a CM2 exists, so manage it" predicate, distinct from the
        /// retired "should the MAIN sender drive the CM2" routing question: the CM2 is
        /// always driven by the dedicated <see cref="_cm2Sender"/> now. Used for UI tab
        /// visibility, CM2 meter-config gating, and diagnostics.
        /// </summary>
        internal bool IsCm2Present =>
            DashboardUsbConnected
            || (_connection?.IsConnected == true
                && DetectionState.BaseDetected
                && DetectionState.DashDetected);

        /// <summary>
        /// Wire dev_id of the CM2: a standalone-USB CM2 bridges as 0x12 (DeviceMain on
        /// its own pipe); a CM2 behind the wheelbase is the meter at 0x14 (DeviceDash).
        /// The <see cref="_cm2Sender"/>'s <c>TargetDeviceId</c> equals this; the CM2 LED
        /// writes and meter-config commands route here.
        /// </summary>
        internal byte Cm2TargetDeviceId =>
            DashboardUsbConnected ? MozaProtocol.DeviceMain : MozaProtocol.DeviceDash;

        /// <summary>
        /// A CM2 (external display) wired through the wheelbase bus (dash sub-device
        /// at 0x14), as opposed to a standalone-USB CM2. DECOUPLED: this is now a pure
        /// "bus CM2 present" predicate — independent of the wheel's screen — since the
        /// CM2 is always driven by the dedicated <see cref="_cm2Sender"/> regardless of
        /// the wheel. Used by detection (probe the dash at 0x14) and the CM2 meter-config
        /// re-assert. Equivalent to <c>IsCm2Present &amp;&amp; !DashboardUsbConnected</c>.
        /// </summary>
        internal bool IsCm2BehindBaseCandidate =>
            IsCm2Present && !DashboardUsbConnected;


        /// <summary>
        /// Push the dashboard's live RPM LED bitmask (dash-send-telemetry,
        /// group 0x41 / FD DE) to the active dashboard sink, routed by connection
        /// path so the frame reaches the right device on the right pipe:
        ///   • standalone-USB CM2 → dedicated dashboard pipe, dev 0x12
        ///   • CM2 behind the wheelbase → main pipe, dev 0x14
        ///   • base-bridged dash (e.g. CM1) → main pipe, dev 0x14
        /// Called from <see cref="Devices.MozaDashLedDeviceManager"/> per frame.
        /// </summary>
        internal bool WriteDashLedBitmask(int bitmask)
        {
            // Stream lane (latest-wins, coalescing) — keep the per-frame CM2 LED
            // bitmask off the throttled one-shot FIFO so a shared-bus value stream
            // can't starve it. Idempotent end-state, safe to coalesce. Routed to the
            // CM2's connection + device (Cm2TargetDeviceId: 0x12 USB / 0x14 bus) —
            // the same place the dedicated _cm2Sender lives.
            if (DashboardUsbConnected)
                return _dashboardManager.WriteSettingForDeviceStream(
                    "dash-send-telemetry", Cm2TargetDeviceId, bitmask, Protocol.StreamKind.DashRpmBitmask);
            return _deviceManager.WriteSettingForDeviceStream(
                "dash-send-telemetry", Cm2TargetDeviceId, bitmask, Protocol.StreamKind.DashRpmBitmask);
        }

        /// <summary>
        /// Push the CM2's 6 flag-LED colours as the live dash-flag-colors array
        /// (group 0x32 cmd 08 00, 6×RGB, black = off). PitHouse drives the bus
        /// CM2's flag LEDs exactly this way — streamed per frame, the firmware
        /// lights each non-black flag (verified cm2t.pcapng). Routed to the same
        /// device/connection as the RPM bitmask (standalone-USB CM2 → 0x12 on the
        /// dedicated pipe, behind-base CM2 → 0x14 on the base).
        /// </summary>
        internal bool WriteDashFlagColors(byte[] rgb18)
        {
            if (DashboardUsbConnected)
                return _dashboardManager.WriteArrayForDeviceStream(
                    "dash-flag-colors", Cm2TargetDeviceId, rgb18, Protocol.StreamKind.DashFlagColors);
            return _deviceManager.WriteArrayForDeviceStream(
                "dash-flag-colors", Cm2TargetDeviceId, rgb18, Protocol.StreamKind.DashFlagColors);
        }

        /// <summary>
        /// Push a single RPM LED's colour to the dash's live indicator-colour
        /// register (wire 0B 00). Routed/named per topology like the bitmask:
        /// standalone-USB CM2 → cm2-indicator-color on 0x12, behind-base CM2 →
        /// dash-rpm-color on 0x14. <paramref name="index"/> is 0-based.
        /// </summary>
        internal bool WriteDashRpmColor(int index, byte r, byte g, byte b)
        {
            var rgb = new byte[] { r, g, b };
            // One coalescing stream slot per RPM index (DashRpmColor0..9) bounds the
            // per-frame SyncRpmColors write-amplifier (up to 10 writes/frame) and
            // keeps it off the throttled one-shot lane. index is 0-based, 0..9.
            var slot = (Protocol.StreamKind)((int)Protocol.StreamKind.DashRpmColor0 + index);
            bool inRange = index >= 0
                && (int)slot <= (int)Protocol.StreamKind.DashRpmColor9;
            if (DashboardUsbConnected)
                return inRange
                    ? _dashboardManager.WriteArrayForDeviceStream(
                        $"cm2-indicator-color{index + 1}", Cm2TargetDeviceId, rgb, slot)
                    : _dashboardManager.WriteArrayForDevice(
                        $"cm2-indicator-color{index + 1}", Cm2TargetDeviceId, rgb);

            return inRange
                ? _deviceManager.WriteArrayForDeviceStream($"dash-rpm-color{index + 1}", Cm2TargetDeviceId, rgb, slot)
                : _deviceManager.WriteArrayForDevice($"dash-rpm-color{index + 1}", Cm2TargetDeviceId, rgb);
        }

        /// <summary>True when the CM2's meter firmware is the 2026-06 indicator
        /// stack that takes wheel-style group-0x3F live LED commands instead of
        /// the legacy 41 FD DE / 32 0B registers. Auto-detected + persisted; see
        /// <see cref="DetectCm2LedFirmwareEra"/>.</summary>
        internal bool Cm2HasNewLedFirmware => Settings?.Cm2NewLedFirmware ?? false;

        /// <summary>
        /// CM2 meter firmware era detection from the meter's 0x0E heartbeat text
        /// (src=0x41). The 2026-06 firmware rework replaced the autonomous
        /// threshold RPM ramp (RpmMode / RpmNumber[0~9] / RpmPercent[0~9]) with
        /// the wheel-style indicator-group stack (IndicatorMode / StandbyMode,
        /// meter_diag.c:89 → :88) and stopped honoring the legacy live LED
        /// registers. Both directions are detected so a firmware downgrade
        /// recovers too. Persisted because the heartbeat only arrives ~1/min —
        /// the next boot starts on the right LED path immediately.
        /// </summary>
        private void DetectCm2LedFirmwareEra(string text)
        {
            bool isNew;
            if (text.Contains("RpmNumber[") || text.Contains("RpmMode:")) isNew = false;
            else if (text.Contains("IndicatorMode:") || text.Contains("StandbyMode:")) isNew = true;
            else return;
            if (Settings == null || Settings.Cm2NewLedFirmware == isNew) return;
            Settings.Cm2NewLedFirmware = isNew;
            PersistSettings();
            MozaLog.Info("[AZOM] CM2 meter firmware era detected: " +
                         (isNew ? "indicator stack — wheel-style LED commands" : "legacy RPM ramp") +
                         " — dash LED path switched");
        }

        /// <summary>New-firmware CM2 live LED colour chunk (group 0x32 cmd 13 00,
        /// idx/R/G/B records) addressed to the CM2. Rides the same coalescing slots
        /// the legacy per-LED colour path used (DashRpmColor0+).</summary>
        internal bool WriteCm2LiveLedColorChunk(byte[] chunk, int chunkIdx)
        {
            var slot = (Protocol.StreamKind)((int)Protocol.StreamKind.DashRpmColor0 + chunkIdx);
            bool inRange = chunkIdx >= 0 && (int)slot <= (int)Protocol.StreamKind.DashRpmColor9;
            if (DashboardUsbConnected)
                return inRange
                    ? _dashboardManager.WriteArrayForDeviceStream("cm2-live-colors", Cm2TargetDeviceId, chunk, slot)
                    : _dashboardManager.WriteArrayForDevice("cm2-live-colors", Cm2TargetDeviceId, chunk);
            return inRange
                ? _deviceManager.WriteArrayForDeviceStream("cm2-live-colors", Cm2TargetDeviceId, chunk, slot)
                : _deviceManager.WriteArrayForDevice("cm2-live-colors", Cm2TargetDeviceId, chunk);
        }

        /// <summary>New-firmware CM2 live LED bitmask (group 0x32 cmd 14 00, 8-byte
        /// active(u32 LE) + window(u32 LE) form) addressed to the CM2.</summary>
        internal bool WriteCm2LiveLedBitmask(byte[] activeWindow8)
        {
            if (DashboardUsbConnected)
                return _dashboardManager.WriteArrayForDeviceStream(
                    "cm2-live-bitmask", Cm2TargetDeviceId, activeWindow8, Protocol.StreamKind.DashRpmBitmask);
            return _deviceManager.WriteArrayForDeviceStream(
                "cm2-live-bitmask", Cm2TargetDeviceId, activeWindow8, Protocol.StreamKind.DashRpmBitmask);
        }

        /// <summary>
        /// Route a one-shot CM2 meter-config write (group 0x32: modes, thresholds,
        /// indicator brightness, stored idle colours) to the CM2's OWN pipe + device.
        /// A standalone-USB CM2 lives on the dedicated _dashboardManager connection,
        /// NOT the wheelbase _deviceManager — so a name-based _deviceManager.WriteSetting
        /// would land on the wheelbase's own 0x12 (the base main, which drops group-0x32)
        /// and never reach the USB CM2. These mirror the per-frame WriteDash* LED routing.
        /// </summary>
        internal bool WriteCm2Config(string commandName, int value) =>
            DashboardUsbConnected
                ? _dashboardManager.WriteSettingForDevice(commandName, Cm2TargetDeviceId, value)
                : _deviceManager.WriteSettingForDevice(commandName, Cm2TargetDeviceId, value);

        internal bool WriteCm2Config(string commandName, byte[] payload) =>
            DashboardUsbConnected
                ? _dashboardManager.WriteArrayForDevice(commandName, Cm2TargetDeviceId, payload)
                : _deviceManager.WriteArrayForDevice(commandName, Cm2TargetDeviceId, payload);

        internal bool IsBaseAmbientLedSupported => DetectionState.BaseAmbientLedSupported;
        internal bool IsHandbrakeDetected => DetectionState.HandbrakeDetected;
        internal bool IsPedalsDetected => DetectionState.PedalsDetected;
        internal bool IsShifterDetected => DetectionState.ShifterDetected;
        internal bool IsShifterHasLeds => DetectionState.ShifterHasLeds;
        // Positive per-model gates for the dedicated HGP / SGP tabs (Unknown → neither).
        internal bool IsHgpShifterDetected => DetectionState.ShifterModel == Devices.ShifterModelKind.Hgp;
        internal bool IsSgpShifterDetected => DetectionState.ShifterModel == Devices.ShifterModelKind.Sgp;
        internal bool IsHubDetected => DetectionState.HubDetected;
        internal bool IsAb9Detected => DetectionState.Ab9Detected;
        internal MozaAb9DeviceManager Ab9Manager => _ab9Manager;
        internal MozaMBoosterRegistry? MBoosterRegistry => _mboosterRegistry;
        internal MozaSerialConnection Connection => _connection;

        /// <summary>The standalone-USB dashboard connection (CM2 on its own cable), or null.</summary>
        internal MozaSerialConnection? DashboardConnection => _dashboardManager?.Connection;

        /// <summary>The dedicated Universal Hub connection (present when a base + hub coexist), or null.</summary>
        internal MozaSerialConnection? HubConnection => _hubManager?.Connection;

        /// <summary>The dedicated base-aux connection (present only after a base→hub
        /// primary migration — broken base, wheel on hub), or null.</summary>
        internal MozaSerialConnection? BaseAuxConnection => _baseManager?.Connection;

        /// <summary>True when a standalone-USB dashboard (CM2, PID 0x0025) is connected on its own port.</summary>
        internal bool DashboardUsbConnected =>
            _dashboardManager?.IsConnected == true
            && MozaUsbIds.IsDashboardPid(_dashboardManager.Connection.DiscoveredPid);

        /// <summary>
        /// Live SDK CoAP server when emulation is enabled; null otherwise.
        /// Surfaced for the Settings UI's SDK tab to read its status and
        /// recent-requests buffer.
        /// </summary>
        internal Sdk.MozaSdkCoapServer? SdkServer => _sdkServer;

        /// <summary>
        /// PitHouse-compatible plain-UDP control server (port 40288 by default).
        /// Started/stopped alongside <see cref="SdkServer"/> — both are part of
        /// the third-party SDK emulation surface.
        /// </summary>
        internal Sdk.PitHouseUdp.MozaControlUdpServer? ControlUdpServer => _controlUdpServer;

        /// <summary>
        /// Live CoAP-stub child-process manager when SDK emulation is
        /// enabled; null otherwise. Same UI consumer as <see cref="SdkServer"/>.
        /// </summary>
        internal Sdk.CoapStubManager? SdkStubManager => _sdkStubManager;

        /// <summary>True if the wheel's internal Display sub-device responded to probe.
        /// Accepts any populated identity field — some wheels (e.g. W17) return an
        /// empty model-name string but populate HW/SW/MCU UID.</summary>
        internal bool IsDisplayDetected =>
            !string.IsNullOrEmpty(_data?.DisplayModelName)
            || !string.IsNullOrEmpty(_data?.DisplayHwVersion)
            || !string.IsNullOrEmpty(_data?.DisplaySwVersion)
            || (_data?.DisplayMcuUid?.Length ?? 0) > 0
            || (_telemetrySender?.DisplayDetected ?? false);

        // UtcTicks at which the wheel was first detected (wheel-telemetry-mode or
        // wheel-rpm-value1 response). 0 = no wheel detected. Read by PollStatus's
        // display-wedge watchdog to bound how long we'll wait for the display
        // sub-device to come up after wheel-MCU detection. Cleared on
        // ResetWheelDetection.
        private long _wheelDetectedUtcTicks;
        internal long WheelDetectedUtcTicks => Interlocked.Read(ref _wheelDetectedUtcTicks);

        // One-shot latch: true once the display-wedge watchdog has forced a
        // serial reconnect. Stays true until a future successful display
        // detection re-arms it — so a permanently-wedged display can't loop
        // the connection. ResetWheelDetection does NOT clear this; only
        // ClearDisplayWedgeRecovery (called from DeviceProber's
        // display-model-name handler) does. SetConnectionEnabled(true) also
        // clears it so the user can manually retry from the UI.
        internal volatile bool DisplayWedgeRecoveryFired;

        /// <summary>Stamp the wheel-detection timestamp for the wedge watchdog.
        /// Idempotent — multiple calls leave the first-detect timestamp in
        /// place so the watchdog measures elapsed time since the rising edge,
        /// not since the most recent probe response.</summary>
        internal void NoteWheelDetected()
        {
            Interlocked.CompareExchange(ref _wheelDetectedUtcTicks, DateTime.UtcNow.Ticks, 0);
        }

        /// <summary>Clear the wedge-recovery one-shot after a successful display
        /// detection. Subsequent wedges (e.g., on a future wheel hot-swap) can
        /// then trigger recovery again.</summary>
        internal void ClearDisplayWedgeRecovery() => DisplayWedgeRecoveryFired = false;

        /// <summary>
        /// Whether the plugin should drive the dashboard telemetry pipeline for the
        /// currently-detected wheel. Trusts <see cref="Devices.WheelModelInfo.HasDisplay"/>
        /// when known; falls back to the probe result for unknown models.
        /// </summary>
        /// <summary>
        /// True when the detected wheel is the FSR V1 display wheel (box "FSR1";
        /// firmware model-name "FSR", hw-version "RS21-D03-*"). This wheel does not
        /// speak the standard tier-definition telemetry protocol — its screen is
        /// driven by the group-0x42 fixed-schema value push instead. Keyed primarily
        /// on the hw-version (most specific; distinguishes it from FSR V2 "W13"),
        /// with the model-name as corroboration. Used to (a) bypass the standard
        /// display-probe gates in StartTelemetryIfReady and (b) put TelemetrySender
        /// into <see cref="Telemetry.TelemetrySender.Fsr1Mode"/>.
        /// </summary>
        internal bool IsFsr1DisplayWheel =>
            (_data?.WheelHwVersion?.StartsWith("RS21-D03", StringComparison.OrdinalIgnoreCase) ?? false)
            || string.Equals(_data?.WheelModelName, "FSR", StringComparison.OrdinalIgnoreCase);

        internal bool ShouldDriveDashboard()
        {
            // DECOUPLED: this gates ONLY the MAIN (wheel-screen) sender. A bus CM2 on a
            // screenless wheel is driven by _cm2Sender, not the main sender, so the old
            // `if (IsCm2BehindBaseCandidate) return true;` short-circuit is gone — the
            // main sender must NOT start on a screenless wheel just because a CM2 is on
            // the bus (that would put it on 0x17 with no screen, or collide on 0x14).
            bool? hasDisplay = WheelModelInfo?.HasDisplay;
            if (hasDisplay == false) return false;   // known no-display: never
            if (hasDisplay == true)  return true;    // known display: don't wait for probe
            // Unknown model: trust the display probe — EXCEPT when a bus CM2 is present.
            // That CM2's own display-identity probe (sent to 0x14) populates the SAME
            // _data.Display* fields IsDisplayDetected reads, falsely implying the WHEEL
            // has a screen. Treat an unknown wheel + bus CM2 as screenless: the CM2 is
            // the display (driven by _cm2Sender at 0x14), and the main sender stays idle
            // rather than co-reside on 0x17 with SharesConnection=false (whose Stop()
            // would FlushPendingWrites and blank the CM2). A real display wheel resolves
            // HasDisplay==true above. (A USB CM2 is on its own pipe and doesn't
            // contaminate _data.Display*, so it's excluded here.)
            if (IsCm2Present && !DashboardUsbConnected) return false;
            return IsDisplayDetected;                // unknown model, no bus CM2: trust the probe
        }

        /// <summary>Display sub-device model name (e.g. "W18 Display"), or empty.</summary>
        internal string DisplayModelName =>
            !string.IsNullOrEmpty(_data?.DisplayModelName)
                ? _data!.DisplayModelName
                : (_telemetrySender?.DisplayModelName ?? "");
        internal MozaProfileStore ProfileStore => _settings?.ProfileStore!;

        public void Init(PluginManager pluginManager)
        {
            // Register the AppDomain.ProcessExit hook on first Init in the
            // process so a clean SessionClose 0x01/0x02/0x03 reaches the
            // wheel on full SimHub exit even when End() takes the
            // keepWireAlive=true branch (the common case).
            EnsureProcessExitHandlerRegistered();

            // Defensive: if Init() is called twice without End() (host reload path
            // or upgrade-in-place), tear down any live resources from the prior
            // init before re-creating them. CleanupPartialInit is idempotent and
            // tolerates already-disposed objects, so calling it on a fully-set-up
            // plugin is safe — the next allocations below replace the now-disposed
            // references with fresh instances.
            if (_connection != null || _telemetrySender != null || _hidReader != null)
            {
                MozaLog.Warn("[AZOM] Init() called with prior state still live — tearing down before re-init");
                try { CleanupPartialInit(); } catch { }
            }

            // Clear shutdown flag from any previous plugin instance in this process.
            // SimHub may load+unload plugins without restarting, leaving this true.
            IsShuttingDown = false;
            Interlocked.Exchange(ref _telemetryStartRequested, 0);
            // Refresh startup timestamp on every Init (covers SimHub load+unload+load
            // sequences) so banner settling windows are measured from the current
            // plugin lifetime, not from process launch.
            StartupUtc = DateTime.UtcNow;
            // Reset detection flags so a plugin reload doesn't carry over stale
            // "device detected" state from the prior session.
            ResetDetectionFlags();
            // Belt-and-braces for the defensive double-Init path above. Coordinator
            // is null on a brand-new instance; only fires after a re-Init.
            _dashboardBindingCoordinator?.ClearLastAppliedDashboardKey();
            _pluginManager = pluginManager;

            try
            {
                _data = new MozaData();
                _settings = this.ReadCommonSettings<MozaPluginSettings>("MozaPluginSettings",
                    () => new MozaPluginSettings { TelemetryEnabledDefaultForNewWheels = true });
                _fsr1Cm1Mapping = new Fsr1Cm1MappingCoordinator(this);
                _profileCoordinator = new ProfileCoordinator(this);

                // Sweep leftover install artifacts before doing anything
                // heavyweight. After a successful in-app update + SimHub
                // restart, we land here with the NEW DLL loaded and the
                // PREVIOUS DLL renamed to MozaPlugin.dll.old next to us;
                // it's safe to delete because nothing holds a handle to it
                // anymore. Also cleans up MozaPlugin.dll.new (interrupted
                // install) and MozaPlugin.update.zip (interrupted download).
                UpdateInstallService.CleanupLeftoverArtifacts(MozaLog.Debug);

                // Set the UI culture BEFORE any WPF control is constructed —
                // x:Static bindings in SettingsControl.xaml evaluate against
                // Thread.CurrentUICulture at parse time, so a later assignment
                // wouldn't retroactively re-translate the UI. Resolver checks
                // the explicit picker pref first, then falls back to SimHub's
                // own language, then the OS culture. (SimHub doesn't propagate
                // its chosen language onto plugin threads, hence reading the
                // setting ourselves in LanguageResolver.)
                var resolvedCulture = LanguageResolver.Resolve(_settings.PreferredLanguage);
                Thread.CurrentThread.CurrentUICulture = resolvedCulture;
                CultureInfo.DefaultThreadCurrentUICulture = resolvedCulture;

                // Null-guard for upgraded settings missing ProfileStore
                if (_settings.ProfileStore == null)
                    _settings.ProfileStore = new MozaProfileStore();

                // Initialise the GUID↔model registry up front — page-GUID
                // resolution (current-wheel page lookup, per-page settings dicts)
                // depends on it throughout runtime.
                MozaDeviceConstants.InitializeRegistry();

                // Restore blink colors from settings (write-only, can't be polled from device)
                MozaProfile.UnpackColorsInto(_settings.WheelRpmBlinkColors, _data.WheelRpmBlinkColors);
                MozaProfile.UnpackColorsInto(_settings.DashRpmBlinkColors, _data.DashRpmBlinkColors);

                MozaLog.Info("[AZOM] Initializing plugin");
                // Build marker so we can confirm WHICH DLL SimHub actually loaded
                // (plugin DLLs load only at SimHub process start; a telemetry toggle /
                // game switch re-runs Init on the already-loaded assembly). Bump on
                // each radar build so a restart is verifiable from the log.
                MozaLog.Info("[AZOM] BUILD radar-2026-06-29Y: suppress radar/track-map channels (patch/Location*, patch/riN) from the channel mapper UI");

                MozaLog.WireDebugEnabled = _settings.VerboseWireDebugLog;

                // Bridge-format JSONL wire trace at SimHub/Logs/moza-wire-*.jsonl.
                // Opt-in via _settings.EnableWireTraceFileSink. Fresh file per launch.
                if (_settings.EnableWireTraceFileSink)
                {
                    try
                    {
                        string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
                        string logsDir = System.IO.Path.Combine(baseDir, "Logs");
                        string sinkPath = System.IO.Path.Combine(logsDir, $"moza-wire-{ts}.jsonl");
                        global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.StartFileSink(sinkPath);
                        MozaLog.Debug($"[AZOM] Wire trace sink → {sinkPath}");
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Warn($"[AZOM] Wire trace sink open failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Persistent always-capture: ensure capture is on before any device traffic
                // so it covers the full connect/handshake. EnsureRunning() — not Start() —
                // so the buffer survives plugin reload on game switches (Start clears the ring).
                if (_settings.AlwaysCaptureOnStartup)
                {
                    var cap = global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance;
                    bool wasRunning = cap.Enabled;
                    cap.EnsureRunning();
                    MozaLog.Debug(wasRunning
                        ? $"[AZOM] Serial traffic capture preserved across reload — AlwaysCaptureOnStartup is on ({cap.Count} entries)"
                        : "[AZOM] Serial traffic capture auto-started — AlwaysCaptureOnStartup is on");
                }

                // Fire-and-forget update check against the GitHub Releases API.
                // Throttled to once per 24h (LastUpdateCheckUtc) and deduped
                // per-process (s_updateCheckStarted) so SimHub game switches
                // don't multiply network calls. Persist-then-render: the
                // result lands in _settings.LastSeenLatestVersion and the
                // About-tab banner reads it on next open. Failures are silent
                // here — the user-facing "Check now" button surfaces errors.
                MaybeStartUpdateCheck();

                // Read SimHub's global temperature unit preference (set at first launch)
                var tempUnit = pluginManager.GetPropertyValue("DataCorePlugin.GameData.TemperatureUnit");
                _data.UseFahrenheit = string.Equals(tempUnit as string, "Fahrenheit", StringComparison.OrdinalIgnoreCase);
                MozaLog.Debug($"[AZOM] Temperature unit: {(_data.UseFahrenheit ? "Fahrenheit" : "Celsius")}");

                // Profile system init is deferred until after the collaborators
                // (HardwareApplier, DeviceProber, PropertyResolver) are constructed,
                // because AutoApplyProfile calls ApplyProfile which delegates to
                // _hardwareApplier. See further down in Init.

                _simHubRegistrar = new SimHubRegistrar(this);
                _simHubRegistrar.RegisterProperties(pluginManager);
                _simHubRegistrar.RegisterActions();

                // Wheelbase + Universal HUB + unknown Moza PIDs. Excludes
                // pedals/shifter/handbrake/AB9 (they ignore base probes).
                // See Protocol/MozaUsbIds.cs and docs/protocol/devices/usb-ids.md.
                Func<bool> disableProbeFallback = () =>
                    _settings != null && _settings.DisableSerialProbeFallback;
                // Reuse the persistent connection from a prior plugin
                // instance if it's still connected — this keeps wheel
                // sessions alive across SimHub game-switch plugin reloads
                // and avoids the ~10 s sess=0x09 settle wait.
                if (s_persistentConnection != null && s_persistentConnection.IsConnected)
                {
                    _connection = s_persistentConnection;
                    _usingPersistentWire = true;
                    // Reuse the prior instance's detection bag so sub-device
                    // tab visibility survives the reload. ResetDetectionFlags()
                    // above already cleared our new-instance bag; swapping the
                    // reference is safe because the collaborators that capture
                    // it (HardwareApplier / DeviceProber / coordinator) are
                    // constructed later in Init, after this swap.
                    if (s_persistentDetectionState != null)
                    {
                        DetectionState = s_persistentDetectionState;
                        // Drop the stale miss counter: the new instance has a
                        // fresh _deviceManager (_wheelDetected=false until
                        // ProbeWheelDetection re-locks), so its
                        // WheelRespondedSinceLastPoll flag starts at false and
                        // PollStatus would otherwise immediately add another
                        // miss to whatever count survived the prior instance.
                        // Without this reset, three rapid SimHub plugin
                        // reloads can push the persisted miss counter past
                        // WheelMissThreshold and fire a spurious hot-swap
                        // ResetWheelDetection (with its 11 s silence gate)
                        // even though the wheel never stopped responding.
                        DetectionState.WheelPollMisses = 0;

                        // Re-derive WheelModelInfo from the persisted model
                        // name. WheelModelInfo itself is per-instance (set by
                        // DeviceProber when the wheel-model-name response
                        // arrives), but the model name is persisted on
                        // DeviceDetectionState.LastKnownWheelModel — so on a
                        // SimHub-driven plugin reload we know the model
                        // immediately and don't have to wait for re-detection.
                        //
                        // The cost of NOT doing this: SimHub starts calling
                        // the LED Display() callback within milliseconds of
                        // plugin Init, BEFORE wheel-model-name finishes its
                        // round-trip (~280 ms post wheel-locked on W17). With
                        // WheelModelInfo still null in that window, rpmN
                        // falls back to MozaDeviceConstants.RpmLedCount (10),
                        // button and knob branches are gated out by the
                        // `modelInfo != null` checks, and the wheel's
                        // physical 16 RPM / 8 button / 4 knob LEDs collapse
                        // to "10 RPM, no buttons, no knobs" for the entire
                        // lifetime of the plugin instance. Manifests as the
                        // last 6 RPM LEDs and all button/knob LEDs going
                        // dark after a game switch — verified W17 capture
                        // 2026-05-24.
                        var savedModel = DetectionState.LastKnownWheelModel;
                        if (!string.IsNullOrEmpty(savedModel))
                        {
                            WheelModelInfo = Devices.WheelModelInfo.FromModelName(savedModel);
                            // Also restore _data.WheelModelName.
                            _data.WheelModelName = savedModel;
                            MozaLog.Debug(
                                $"[AZOM] Restored WheelModelInfo from persistent state: {savedModel} " +
                                $"(rpm={WheelModelInfo?.RpmLedCount}, buttons={WheelModelInfo?.ButtonLedCount}, " +
                                $"knobs={WheelModelInfo?.KnobCount}, flags={WheelModelInfo?.HasFlagLeds})");
                        }
                    }
                    MozaLog.Info("[AZOM] Reusing persistent serial connection from prior plugin instance");
                }
                else
                {
                    if (s_persistentConnection != null)
                    {
                        // Stale handle — connection lost between reloads.
                        try { s_persistentConnection.Dispose(); } catch { }
                        s_persistentConnection = null;
                    }
                    // Wire is being rebuilt from scratch — drop any captured
                    // detection state so the new instance re-probes everything
                    // (the device may have changed during the gap).
                    s_persistentDetectionState = null;
                    _connection = new MozaSerialConnection(
                        // Dashboard PIDs (CM2 0x0025) are claimed by the dedicated
                        // _dashboardManager connection so a standalone CM2 works
                        // alongside a base; the wheelbase no longer admits them.
                        pid => MozaUsbIds.IsWheelbasePid(pid)
                               || MozaUsbIds.IsHubPid(pid)
                               || !MozaUsbIds.IsKnownMozaPid(pid),
                        MozaProbeTarget.BaseAndHub,
                        disableProbeFallback);
                    if (!string.IsNullOrEmpty(_settings.LastWheelbasePort))
                        _connection.LastPortName = _settings.LastWheelbasePort;
                    s_persistentConnection = _connection;
                }
                _connection.MessageReceived += OnMessageReceived;
                _connection.Disconnected += OnSerialDisconnected;

                _deviceManager = new MozaDeviceManager(_connection);

                _ab9Manager = new MozaAb9DeviceManager(disableProbeFallback);
                if (!string.IsNullOrEmpty(_settings.LastAb9Port))
                    _ab9Manager.Connection.LastPortName = _settings.LastAb9Port;
                _ab9Manager.MessageReceived += OnAb9MessageReceived;

                // Dedicated connection for a standalone-USB CM2 (PID 0x0025), so it
                // works even when a base holds the wheelbase connection.
                _dashboardManager = new MozaDashboardDeviceManager();
                if (!string.IsNullOrEmpty(_settings.LastDashboardPort))
                    _dashboardManager.Connection.LastPortName = _settings.LastDashboardPort;
                _dashboardManager.MessageReceived += OnDashboardMessageReceived;
                _dashboardManager.Connection.Disconnected += OnDashboardDisconnected;

                // Dedicated connection for a Universal Hub (PID 0x0020) on its
                // own COM port. Brought up alongside the wheelbase so a base with
                // no pedal port + a hub-for-pedals enumerates the hub's peripherals
                // (pedals / handbrake / port-power) in parallel. Like the dashboard
                // manager it's a fresh instance each Init (not part of the
                // persistent-connection reuse, which is wheel-session-scoped).
                _hubManager = new MozaHubDeviceManager();
                if (!string.IsNullOrEmpty(_settings.LastHubPort))
                    _hubManager.Connection.LastPortName = _settings.LastHubPort;
                _hubManager.MessageReceived += OnHubMessageReceived;
                _hubManager.Connection.Disconnected += OnHubDisconnected;

                // Mirror of _hubManager for the broken-base case: a dedicated
                // base pipe that only comes up AFTER the primary has migrated to
                // the hub (PrimaryBoundToHub), carrying base-only telemetry on the
                // base port so motor temps / FFB / ambient survive the migration.
                _baseManager = new MozaBaseDeviceManager();
                if (!string.IsNullOrEmpty(_settings.LastBaseAuxPort))
                    _baseManager.Connection.LastPortName = _settings.LastBaseAuxPort;
                _baseManager.MessageReceived += OnBaseMessageReceived;
                _baseManager.Connection.Disconnected += OnBaseDisconnected;

                // System sleep/resume recovery. On resume the wheel firmware has
                // power-cycled and silently dropped its display/telemetry sessions,
                // but the host serial tty can stay .IsOpen==true (half-open) — or
                // the wheel resumes talking before the connection's ~30 s half-open
                // detector fires — so neither the reconnect timer nor the dead-tty
                // detector would rebuild the session and the display stays blank.
                // The resume handler forces a clean reconnect to rebuild it. Static
                // event ⇒ unsubscribe in End()/CleanupPartialInit (see _powerModeHooked).
                try
                {
                    Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
                    Interlocked.Exchange(ref _powerModeHooked, 1);
                }
                catch (Exception ex)
                {
                    // SystemEvents needs a message pump; under Wine/Proton it can be
                    // absent. Harmless — a Linux host's sleep doesn't raise Windows
                    // power events anyway.
                    MozaLog.Debug($"[AZOM] PowerModeChanged hook unavailable: {ex.Message}");
                }

                // AB9 engine-vibration worker — tick gates on connection/detection state.
                _ab9Worker = new Ab9EngineVibrationWorker(
                    _ab9Manager,
                    DetectionState,
                    () => _settings?.ProfileStore?.CurrentProfile?.Ab9,
                    () => IsShuttingDown);
                _ab9Worker.Start();

                // Wheelbase LFE worker — tick gates on connection/detection +
                // BaseSupportsLfe (base fw >= 1.2.10.10). Emits on the base
                // primary pipe (_deviceManager).
                _baseLfeWorker = new BaseLfeEffectWorker(
                    _deviceManager,
                    DetectionState,
                    _data,
                    () => _settings?.ProfileStore?.CurrentProfile?.BaseLfe,
                    CreateHapticsFormulaResolver(),   // NCalc/property → double, own engine
                    () => IsShuttingDown);
                _baseLfeWorker.Start();

                // mBooster Pedals registry — multi-device owner. Refresh() is
                // called from the reconnect timer alongside TryConnectAb9. Each
                // detected mBooster spawns its own controller + 50 Hz worker.
                _mboosterRegistry = new MozaMBoosterRegistry(
                    _data,
                    settingsLookup: id => GetOrCreateMBoosterSettings(id),
                    isShuttingDown: () => IsShuttingDown,
                    onDeviceDetectedEdge: OnMBoosterDeviceDetected,
                    customEffectFormulaEvaluator: CreateHapticsFormulaResolver(),
                    onSerialResolved: OnMBoosterSerialResolved);
                // Initial walk so any mBooster plugged in BEFORE SimHub launched
                // appears immediately — without this, the user waits up to 5 s
                // for the reconnect timer to fire.
                try { _mboosterRegistry.Refresh(); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Initial refresh: {ex.Message}"); }

                // Standalone-peripheral registry — one dedicated connection per
                // pedal set / handbrake plugged directly into the PC. Refresh()
                // runs on the reconnect timer; the initial walk is deferred to
                // just after _hardwareApplier/_deviceProber are constructed (a
                // connect immediately marks the peripheral detected, which calls
                // ApplyPedalsToHardware — that NREs if the applier isn't up yet).
                _peripheralRegistry = new MozaStandalonePeripheralRegistry(
                    this, _data, DetectionState, () => IsShuttingDown);

                // 5 s poll interval — balances wire noise vs hot-swap / temp UI responsiveness.
                _pollTimer = new Timer(5000);
                _pollTimer.Elapsed += PollStatus;
                _pollTimer.AutoReset = true;
                _pollTimer.Start();

                // 250ms < shortest ReadRetryBackoffMs (200) so a dropped probe
                // gets retried within ~one backoff window.
                _retryTimer = new Timer(250);
                _retryTimer.Elapsed += (s, e) =>
                {
                    if (IsShuttingDown) return;
                    // Each pipe retransmits its own tracked reads on its own Send,
                    // independently — the hub's reads must NOT go out on the base
                    // port and vice versa. Ticked separately so one pipe being
                    // down doesn't stall the other's retransmits.
                    if (_connection.IsConnected)
                    {
                        try { PendingResponses.TickRetransmits(_connection.Send); }
                        catch (Exception ex) { MozaLog.Warn($"[AZOM] PendingResponseTracker tick failed: {ex.Message}"); }
                    }
                    if (_hubManager != null && _hubManager.IsConnected)
                    {
                        try { _hubManager.PendingResponses?.TickRetransmits(_hubManager.Connection.Send); }
                        catch (Exception ex) { MozaLog.Warn($"[AZOM] Hub PendingResponseTracker tick failed: {ex.Message}"); }
                    }
                    if (_baseManager != null && _baseManager.IsConnected)
                    {
                        try { _baseManager.PendingResponses?.TickRetransmits(_baseManager.Connection.Send); }
                        catch (Exception ex) { MozaLog.Warn($"[AZOM] Base-aux PendingResponseTracker tick failed: {ex.Message}"); }
                    }
                    // Each standalone-peripheral pipe retransmits its own tracked
                    // reads on its own Send (same per-pipe isolation as the hub).
                    try { _peripheralRegistry?.TickRetransmits(); }
                    catch (Exception ex) { MozaLog.Warn($"[AZOM] Standalone peripheral retransmit tick failed: {ex.Message}"); }
                };
                _retryTimer.AutoReset = true;
                _retryTimer.Start();

                _reconnectTimer = new Timer(5000);
                _reconnectTimer.Elapsed += (s, e) =>
                {
                    if (IsShuttingDown) return;
                    if (Interlocked.CompareExchange(ref _reconnectTickInProgress, 1, 0) != 0) return;
                    try { ReconnectTick(); }
                    finally { Interlocked.Exchange(ref _reconnectTickInProgress, 0); }
                };
                _reconnectTimer.AutoReset = true;
                if (_settings.ConnectionEnabled)
                    _reconnectTimer.Start();

                _hidReader = new MozaHidReader(_data);
                // Slice G: HID event subscription re-enabled.
                if (_mboosterRegistry != null)
                {
                    _hidReader.MBoosterAxisChanged += (identity, containerId, axisIndex, pos01) =>
                    {
                        try { _mboosterRegistry.OnHidAxisUpdate(identity, containerId, axisIndex, pos01); }
                        catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] HID dispatch: {ex.Message}"); }
                    };
                }
                // Truck-sim stalk keyboard emulation: subscribe before Start() so no
                // button edges are missed. Only acts when the Stalks mode is TruckSim
                // and an ETS2/ATS game is foreground (gated inside the controller).
                _stalksController = new StalksTruckSimController();
                _stalksController.ApplySettings(_settings.StalksMode, _settings.StalksTruckSim);
                _hidReader.StalksButtonChanged += _stalksController.OnStalkButton;
                _hidReader.Start();
                _propertyResolver = new SimHubPropertyResolver(_pluginManager, _data, _hidReader);
                _hardwareApplier = new HardwareApplier(this, _data, _deviceManager, _ab9Manager, DetectionState);
                _deviceProber = new DeviceProber(this, _connection, _deviceManager, _data, DetectionState);
                // Now that the hardware applier exists, do the initial standalone
                // peripheral walk — surfaces a pedal set / handbrake attached
                // before SimHub launched without waiting for the 5 s reconnect tick.
                try { _peripheralRegistry.Refresh(); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM] Standalone peripheral initial refresh: {ex.Message}"); }
                // Hub-pipe peripheral prober: same _data + DetectionState, but
                // bound to the hub connection + hub device manager so its reads
                // and Mark*Detected ownership go out on the hub pipe.
                // drivesTelemetry:false keeps it off the primary TelemetrySender.
                _hubDeviceProber = new DeviceProber(
                    this, _hubManager.Connection, _hubManager.DeviceManager, _data, DetectionState,
                    drivesTelemetry: false);
                // Base-pipe prober: same _data + DetectionState, bound to the
                // base-aux connection + DM so its base detection cascade and base
                // settings reads go out on the base pipe and record BaseOwner there.
                // drivesTelemetry:false keeps it off the hub-bound TelemetrySender.
                _baseDeviceProber = new DeviceProber(
                    this, _baseManager.Connection, _baseManager.DeviceManager, _data, DetectionState,
                    drivesTelemetry: false);
                _dashboardBindingCoordinator = new DashboardBindingCoordinator(this, _data, _connection, DetectionState);
                _dualDisplay = new DualDisplayCoordinator(this, DetectionState);
                _connectionCoordinator = new ConnectionCoordinator(
                    this, _data, DetectionState, _connection, _deviceManager,
                    _ab9Manager, _dashboardManager, _hubManager, _baseManager,
                    _hubDeviceProber, _baseDeviceProber);

                // Control Mapper variant-provider integration. Construction is in
                // a try/catch so a TypeLoadException from a missing/renamed SimHub
                // internal type cannot poison plugin Init. Registration is attempted
                // immediately; if ControlMapperPlugin isn't loaded yet, DataUpdate
                // retries up to ControlMapperRegisterRetryTickBudget ticks.
                if (_settings != null && _settings.EnableControlMapperVariants)
                {
                    try
                    {
                        _controlMapperBridge = new ControlMapper.ControlMapperBridge();
                        if (!_controlMapperBridge.TryRegister(_pluginManager))
                            _controlMapperRetryTicks = ControlMapperRegisterRetryTickBudget;
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Warn(
                            $"[AZOM] ControlMapper bridge construction failed — {ex.GetBaseException().Message}");
                        _controlMapperBridge = null;
                    }
                }

                // Top up artwork on already-deployed wheel definitions. The
                // per-model deploy only reaches the attached wheel, so other
                // wheels the user owns would never get a picture.
                DeviceDefinitionDeployer.RefreshDeployedThumbnails();

                // Now safe to initialize the profile system — ApplyProfile (called
                // by AutoApplyProfile on the initially selected game's profile)
                // delegates to _hardwareApplier which is now constructed.
                _profileCoordinator.InitProfileSystem();

                // Reuse the persistent telemetry sender from a prior
                // plugin instance if it's alive and the connection it
                // was using is the same one we just reused. Sessions stay
                // open across plugin reload — no Stop+Start cycle, no
                // 11 s settle wait.
                if (s_persistentTelemetrySender != null
                    && !s_persistentTelemetrySender.IsDisposedFlag
                    && _usingPersistentWire)
                {
                    _telemetrySender = s_persistentTelemetrySender;
                    MozaLog.Info(
                        "[AZOM] Reusing persistent telemetry sender from prior plugin instance " +
                        $"(state={_telemetrySender.State}, sessions kept alive)");
                }
                else
                {
                    if (s_persistentTelemetrySender != null)
                    {
                        try { s_persistentTelemetrySender.Dispose(); } catch { }
                        s_persistentTelemetrySender = null;
                    }
                    _telemetrySender = new TelemetrySender(_connection);
                    s_persistentTelemetrySender = _telemetrySender;
                }
                // FSR V1 display driver — own timer/lane on the wheelbase connection,
                // started lazily once an FSR1 wheel is detected (StartFsr1DriverIfNeeded).
                _fsr1Driver = new Telemetry.Fsr1DisplayDriver(_connection, _propertyResolver.ResolveAsDouble);
                // CM1 base-bridged dash driver — own timer/lane on the wheelbase connection,
                // started lazily once a bridged dash is confirmed CM1 (TickCm1Discriminator).
                _cm1Driver = new Telemetry.Cm1DisplayDriver(_connection, _propertyResolver.ResolveAsDouble);
                // Propagate the hot-renegotiation feature flag from settings.
                // Reading from settings here (rather than via a callback) is
                // fine because the flag is JSON-ignored and only set
                // programmatically at runtime — see MozaPluginSettings.
                // _settings is assigned earlier in this method (line ~580); the
                // null-forgiving operator silences CS8602 without a runtime check.
                _telemetrySender.EnableHotRenegotiation = _settings!.EnableHotRenegotiation;
                MozaLog.Info(
                    $"[AZOM] Hot re-negotiation feature flag: " +
                    $"settings={_settings.EnableHotRenegotiation} " +
                    $"sender={_telemetrySender.EnableHotRenegotiation}");
                // Reset the start-request gate when the dashboard pipeline parks
                // itself (sess=0x09 retry exhaust). Without this clear, the next
                // wheel hot-swap or user toggle would early-out in
                // StartTelemetryIfReady() because the gate is still latched at 1.
                _telemetrySender.DashboardPipelineParked += _dashboardBindingCoordinator.OnDashboardPipelineParked;

                // Mirror wheel-initiated dashboard switches (user pressed a
                // wheel-side knob/button). TelemetrySender has already armed
                // its hot-reneg burst at the new slot; we just need to sync
                // our profile state + UI to match what the wheel committed.
                _telemetrySender.WheelInitiatedSwitch += _dashboardBindingCoordinator.OnWheelInitiatedSwitch;

                // Initialize dashboard cache for download-on-connect.
                string cacheDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MozaSimHubPlugin", "DashboardCache");
                DashCache = new DashboardCache(cacheDir, DashProfileStore);
                DashCache.LoadFromDisk();
                // Per-wheel folder is loaded on wheel-model-name detection via
                // overlay.TelemetryMzdashFolder. Init only loads the disk cache.
                if (_telemetrySender != null)
                {
                    _telemetrySender.DashCache = DashCache;
                    // UI for dashboard upload/download is hidden in SettingsControl.xaml while the
                    // feature is in development; force the download path off regardless of the
                    // saved setting. Setting is preserved on disk so re-enabling the UI restores
                    // the user's prior preference automatically.
                    _telemetrySender.SetDownloadEnabled(false);
                }

                ApplyTelemetrySettings();
                // Don't start telemetry here — defer until wheel is detected.
                // The session open probe requires the wheel to be present and responsive.
                // StartTelemetryIfReady() is called from DetectDevices() when the wheel
                // is first detected, and from profile application callbacks.

                // Background-responsiveness holder (EcoQoS opt-out + 1 ms timer).
                // Constructed unconditionally (no device dependency); gated at apply-time
                // by IsConnected && IsGameActive. Released in End()/CleanupPartialInit().
                _responsiveness = new ProcessResponsivenessManager();

                // Publish Instance only after all resources are wired so a partial-init
                // throw can't leave a half-built plugin reachable from background callbacks.
                Instance = this;

                // Standalone dashboard reuse path: if the persistent
                // serial connection is still alive and the open port is
                // a Dashboard PID (CM2 = 0x0025), flip detection + deploy
                // device.json + apply profile + start telemetry without
                // waiting for the TryConnect tick. Covers SimHub reload-
                // without-restart and the cold-init-with-already-open-port
                // case. The call is idempotent and safe on every Init.
                if (_connection != null && _connection.IsConnected)
                    _connectionCoordinator?.MarkStandaloneDashboardDetectedFromUsb("init");

                // Third-party SDK emulation. Two independent toggles:
                //   - SdkEmulationEnabled gates the CoAP server (40266) and
                //     the name-impersonation stub the official MOZA SDK DLL
                //     looks for in process enumeration.
                //   - UdpControlEnabled gates the plain-UDP-CBOR control
                //     surface (40288) third-party wheel-config tools use.
                // Either or both can be on. Both go through the same runtime
                // start/stop helpers the live UI toggles use, so startup and a
                // mid-session toggle take exactly the same code path. Each
                // helper catches its own failures so one bad port doesn't take
                // the other down.
                SetSdkEmulationEnabled(_settings.SdkEmulationEnabled);
                SetUdpControlEnabled(_settings.UdpControlEnabled);
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Init failed: {ex}");
                CleanupPartialInit();
                throw;
            }
        }

        /// <summary>
        /// Tear down any resources allocated by Init() before it threw. Mirrors End()
        /// but tolerates null fields and never sets IsShuttingDown (caller may retry).
        ///
        /// Persistent-wire safety: if Init() reused the persistent statics
        /// (<see cref="s_persistentConnection"/> / <see cref="s_persistentTelemetrySender"/>)
        /// and then threw later in the same Init, this method MUST NOT dispose
        /// them — the next Init expects to inherit them. Disposal is gated on
        /// !ReferenceEquals(field, static).
        /// </summary>
        private void CleanupPartialInit()
        {
            UnhookPowerMode();
            try { _pollTimer?.Stop(); } catch { }
            try { _retryTimer?.Stop(); } catch { }
            try { _reconnectTimer?.Stop(); } catch { }
            try { _profileCoordinator?.StopSaveDebounceTimer(); } catch { }

            bool ownConnection = _connection != null && !ReferenceEquals(_connection, s_persistentConnection);
            bool ownTelemetrySender = _telemetrySender != null && !ReferenceEquals(_telemetrySender, s_persistentTelemetrySender);

            if (ownTelemetrySender)
            {
                try { _telemetrySender?.Stop(); } catch { }
            }

            // Halt the AB9 engine-vib worker before disposing the AB9 manager.
            try { _ab9Worker?.Stop(); _ab9Worker = null; } catch { }
            try { _baseLfeWorker?.Stop(); _baseLfeWorker = null; } catch { }
            // Release the timer-resolution request + power-throttling opt-out.
            try { _responsiveness?.Dispose(); _responsiveness = null; } catch { }
            // Dispose every mBooster controller — same reason: stop workers
            // before the connections they own get torn down.
            try { _mboosterRegistry?.Dispose(); _mboosterRegistry = null; } catch { }
            // Dispose every standalone-peripheral connection (drops ownership +
            // closes each dedicated pipe).
            try { _peripheralRegistry?.Dispose(); _peripheralRegistry = null; } catch { }

            // Tear down SDK emulation BEFORE the wire / data layers so the
            // CoAP receive thread can't dispatch into half-disposed handlers.
            try { _sdkServer?.Stop(); _sdkServer?.Dispose(); _sdkServer = null; }
            catch (Exception ex) { MozaLog.Warn($"[Sdk] server stop: {ex.Message}"); }
            try { _controlUdpServer?.Stop(); _controlUdpServer?.Dispose(); _controlUdpServer = null; }
            catch (Exception ex) { MozaLog.Warn($"[PitHouseUdp] server stop: {ex.Message}"); }
            // Mirror End()'s persistent-stub policy: if this Init reused the
            // persistent stub, do NOT Stop it here — the next Init expects to
            // inherit it. Only drop our local ref. Disposal gated on
            // !ReferenceEquals matches the connection/sender pattern above.
            // Bounded TryStop so a Wine-side wedge can't block CleanupPartialInit.
            bool ownStubManager = _sdkStubManager != null
                && !ReferenceEquals(_sdkStubManager, s_persistentSdkStubManager);
            if (ownStubManager)
            {
                try { _sdkStubManager?.TryStop(1500); }
                catch (Exception ex) { MozaLog.Warn($"[Sdk] stub stop: {ex.Message}"); }
            }
            _sdkStubManager = null;

            // Mirror End()'s detach: these are subscribed early in Init (before
            // throw-prone steps), and _telemetrySender may be the process-lifetime
            // persistent instance that survives a failed Init — so a missed -=
            // here leaks the coordinator and the whole plugin graph it roots onto
            // the persistent sender's invocation list.
            try
            {
                if (_telemetrySender != null && _dashboardBindingCoordinator != null)
                {
                    _telemetrySender.DashboardPipelineParked -= _dashboardBindingCoordinator.OnDashboardPipelineParked;
                    _telemetrySender.WheelInitiatedSwitch -= _dashboardBindingCoordinator.OnWheelInitiatedSwitch;
                }
            }
            catch { }
            // Remove the Control Mapper variant provider from SimHub's global
            // VariantHelper list (same reason as End()).
            try { _controlMapperBridge?.Unregister(); _controlMapperBridge = null; } catch { }

            try
            {
                if (_connection != null)
                {
                    _connection.MessageReceived -= OnMessageReceived;
                    _connection.Disconnected -= OnSerialDisconnected;
                }
            }
            catch { }
            try { _profileCoordinator?.DetachProfileStore(); } catch { }
            try { _deviceManager?.Dispose(); } catch { }
            try { _hidReader?.Dispose(); } catch { }
            try { _stalksController?.Dispose(); } catch { }
            if (ownTelemetrySender)
            {
                try { _telemetrySender?.Dispose(); } catch { }
                try { _fsr1Driver?.Dispose(); } catch { }
                try { _cm2Sender?.Dispose(); } catch { }
                try { _cm1Driver?.Dispose(); } catch { }
            }
            // File sink always closes on teardown — new file per Init by design.
            // In-memory ring stays enabled across plugin reload when always-capture is on,
            // so buffered frames survive game switches (next Init's EnsureRunning is a no-op).
            try { global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.StopFileSink(); } catch { }
            if (_settings?.AlwaysCaptureOnStartup != true)
            {
                try { global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.Stop(); } catch { }
            }
            if (ownConnection)
            {
                try { _connection?.Dispose(); } catch { }
            }
            try
            {
                if (_ab9Manager != null)
                    _ab9Manager.MessageReceived -= OnAb9MessageReceived;
            }
            catch { }
            try { _ab9Manager?.Dispose(); } catch { }
            try { _dashboardManager?.Dispose(); } catch { }
            try
            {
                if (_hubManager != null)
                {
                    _hubManager.MessageReceived -= OnHubMessageReceived;
                    _hubManager.Connection.Disconnected -= OnHubDisconnected;
                }
                if (_baseManager != null)
                {
                    _baseManager.MessageReceived -= OnBaseMessageReceived;
                    _baseManager.Connection.Disconnected -= OnBaseDisconnected;
                }
            }
            catch { }
            try { _hubManager?.Dispose(); } catch { }
            try { _baseManager?.Dispose(); } catch { }
            try { _pollTimer?.Dispose(); } catch { }
            try { _retryTimer?.Dispose(); } catch { }
            try { _reconnectTimer?.Dispose(); } catch { }
            try { _profileCoordinator?.DisposeSaveDebounceTimer(); } catch { }

            // Drop our refs so a successive Init re-entry doesn't see them as
            // "prior state". If we kept the persistent statics alive above, the
            // statics themselves still hold them — the next Init will pick them
            // back up via the s_persistentConnection / s_persistentTelemetrySender
            // reuse path.
            if (!ownConnection) _connection = null!;
            if (!ownTelemetrySender) _telemetrySender = null;
        }

        // Gearshift trigger state. Fires base-gearshift-event (grp 0x2D cmd 0x76)
        // on gear-string transitions; null initial value suppresses warm-up.
        private string? _lastGearString;
        private DateTime _lastGearShiftSendUtc = DateTime.MinValue;

        // AB9 per-shift trigger state. Separate gear-string latch and debounce
        // timer from the wheelbase path so both devices can fire independently
        // even if game-side debounce settings change.
        private string? _lastAb9GearString;
        private DateTime _lastAb9GearShiftSendUtc = DateTime.MinValue;

        // mBooster per-shift edge state. Separate gear-string latch from the
        // wheelbase's/AB9's own — only the raw "did the gear string change
        // this tick" edge + whether the new gear is neutral are computed
        // here (once, globally); each mBooster device's own Gear Shift
        // effect applies its own VibrateOnNeutral/DebounceMs on top of that
        // raw edge in MBoosterEffectWorker.UpdateGearShiftRequest, so no
        // debounce timestamp is needed at this layer.
        private string? _lastMBoosterGearString;
        // Monotonic shift counter for the mBooster Gear Shift effect — see
        // MBoosterTelemetrySnapshot.GearShiftSeq. Advanced once per detected
        // gear-string change; the per-device workers each track the last
        // value they acted on so none can miss a shift the way a one-tick
        // bool edge would when sampled by their slower ~20ms timer.
        private int _mboosterShiftSeq;

        // Wheelbase LFE momentary test triggers (from the UI test buttons). No-op
        // when the firmware doesn't support LFE. Each plays a fixed pattern:
        // engine = 2 s sweep, ABS = 1 s burst, gearshift = two rapid bumps.
        public void TriggerBaseLfeEngineTest() { if (_data.BaseSupportsLfe) _baseLfeWorker?.PostEngineTest(); }
        public void TriggerBaseLfeAbsTest() { if (_data.BaseSupportsLfe) _baseLfeWorker?.PostAbsTest(); }
        public void TriggerBaseLfeGearshiftTest() { if (_data.BaseSupportsLfe) _baseLfeWorker?.PostGearshiftTest(); }

        /// <summary>Latest (carrier freq Hz, amplitude 0..1) for the 3 LFE slots — drives the settings scope.</summary>
        public (double freq, double amp)[] GetLfeScopeSamples()
        {
            var w = _baseLfeWorker;
            if (w == null) return new[] { (0.0, 0.0), (0.0, 0.0), (0.0, 0.0) };
            return new[] { (w.ScopeEngineFreq, w.ScopeEngineAmp), (w.ScopeAbsFreq, w.ScopeAbsAmp), (w.ScopeGearFreq, w.ScopeGearAmp) };
        }

        // Fire a one-shot base-gearshift-event on gear change. Gated by
        // GearshiftVibration > 0 and a debounce. By default, transitions
        // *into* neutral don't fire (H-pattern produces two transitions
        // "1"→"N"→"2"; we want the engagement bump only).
        // GearshiftVibrateOnNeutral opts in.
        private void CheckGearshiftEvent(GameData data)
        {
            if (!_data.IsConnected) return;
            string? gear = data?.NewData?.Gear;
            if (string.IsNullOrEmpty(gear)) return;

            // LFE-capable firmware: the complex gearshift (cmd 0x77, LFE channel
            // id 0) handles gear-shift feedback ONLY while that channel is enabled
            // and edge-triggered (OnChange) — then the worker fires it and we skip
            // the classic bump to avoid a double buzz. But if the channel is
            // repurposed as a continuous partial (Level mode, e.g. the Additive
            // Engine preset) or disabled, fall through to the classic bump (cmd
            // 0x76), which coexists with the three LFE channels, so gear shifts are
            // still felt while all three channels drive the engine.
            if (_data.BaseSupportsLfe)
            {
                var lfeGear = _settings?.ProfileStore?.CurrentProfile?.BaseLfe?.Gearshift;
                bool lfeHandlesGearshift = lfeGear != null && lfeGear.Enabled
                    && lfeGear.TriggerMode == BaseLfeTriggerMode.OnChange;
                if (lfeHandlesGearshift) return;
            }

            if (_data.GearshiftVibration <= 0) return;
            if (_lastGearString == null)
            {
                _lastGearString = gear;
                return; // warm-up: record the first observed value, don't fire
            }
            if (gear == _lastGearString) return;
            // Update the latch on every change so we don't compare against a
            // stale value on the next tick. Whether we *fire* is decided after.
            _lastGearString = gear;
            // Skip dis-engagement transitions (anything → neutral) unless the
            // user has opted in. Some games report neutral as "0" instead of
            // "N" — treat both as neutral.
            bool isNeutral = (gear == "N" || gear == "0");
            // Source from the active profile (single source of truth). Falls back
            // to safe defaults when the profile field is sentinel (-1 = unset).
            var gsProfile = _settings?.ProfileStore?.CurrentProfile;
            bool vibrateOnNeutral = gsProfile?.GearshiftVibrateOnNeutral == 1;
            int debounceMs = gsProfile?.GearshiftDebounceMs ?? -1;
            if (debounceMs < 0) debounceMs = 500;
            if (isNeutral && !vibrateOnNeutral) return;
            var now = DateTime.UtcNow;
            if (debounceMs > 0 && (now - _lastGearShiftSendUtc).TotalMilliseconds < debounceMs) return;
            _lastGearShiftSendUtc = now;
            _deviceManager.WriteSetting("base-gearshift-event", 1);
        }

        // Fire AB9 per-shift triggers (0x0D 0x01 + 0x0D 0x04 engage, or
        // 0x0D 0x06 for transitions into neutral). AB9 firmware fires its
        // stored gear-shift-vibration rumble pattern in response — see
        // docs/protocol/devices/ab9-shifter.md and usb-capture/AB9/
        // {all_gears,1-N}.pcapng for the empirical observation. The previous
        // hypothesis that the AB9 fires rumble autonomously from its
        // mechanical sensor without host involvement was wrong; without
        // these triggers gear engagement produces zero haptic feedback.
        //
        // Gated by AB9-scoped knobs (Ab9Settings.GearShiftVibrateOnNeutral /
        // GearShiftDebounceMs), separate from the wheelbase gearshift card —
        // users tune the two devices independently (e.g. heavier debounce on
        // the wheelbase to absorb H-pattern double-transitions, but tighter
        // on the AB9 so every gate engagement registers).
        private void CheckAb9GearshiftEvent(GameData data)
        {
            if (_ab9Manager == null || !_ab9Manager.IsConnected) return;
            if (!DetectionState.Ab9Detected) return;
            var ab9Settings = _settings?.ProfileStore?.CurrentProfile?.Ab9;
            if (ab9Settings == null || ab9Settings.GearShiftVibrationIntensity <= 0) return;

            string? gear = data?.NewData?.Gear;
            if (string.IsNullOrEmpty(gear)) return;
            if (_lastAb9GearString == null)
            {
                _lastAb9GearString = gear;
                return; // warm-up: record first value, don't fire
            }
            if (gear == _lastAb9GearString) return;
            _lastAb9GearString = gear;

            bool isNeutral = (gear == "N" || gear == "0");
            bool vibrateOnNeutral = ab9Settings.GearShiftVibrateOnNeutral;
            int debounceMs = ab9Settings.GearShiftDebounceMs;
            if (debounceMs < 0) debounceMs = 0;
            if (isNeutral && !vibrateOnNeutral) return;

            var now = DateTime.UtcNow;
            if (debounceMs > 0 && (now - _lastAb9GearShiftSendUtc).TotalMilliseconds < debounceMs) return;
            _lastAb9GearShiftSendUtc = now;

            // engage trigger (0x0D 0x04) for any non-neutral gear,
            // disengage (0x0D 0x06) for transitions into neutral.
            _ab9Manager.SendGearShiftTrigger(engageNotDisengage: !isNeutral);
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (IsShuttingDown) return;
            // Stamp the game-data feed for the auto-standby reconcile (see
            // ApplyAutoStandby). Done first so a stale-instance early-out below
            // doesn't make the feed look quiet.
            Interlocked.Exchange(ref _autoStandbyLastDataUpdateTicks, DateTime.UtcNow.Ticks);
            _autoStandbyLastGameRunning = data.GameRunning;
            // Feed the truck-sim stalk controller the current game context so it can
            // gate keyboard output to a running ETS2/ATS session.
            try { _stalksController?.SetGameContext(pluginManager.GameName, data.GameRunning); } catch { }
            // Heading probe (diagnostic): dump SimHub heading/radar/spotter props a
            // handful of times while a game runs so we can identify AC's live heading
            // source for the radar preamble. Self-limits to ~20 logs (~once/sec).
            if (data.GameRunning && _headProbeCount < 20 && ++_headProbeTick >= 60)
            {
                _headProbeTick = 0;
                _headProbeCount++;
                try { _propertyResolver?.LogHeadingProbe(); } catch { }
            }
            // Keep the process responsive in the background (EcoQoS opt-out + 1 ms timer)
            // the moment a game is active. Idempotent; the PollStatus backstop handles
            // release if DataUpdate goes quiet on game exit.
            ApplyResponsivenessState();
            // Persistent-wire reload guard. On a SimHub plugin reload, End()
            // keeps the telemetry sender alive in s_persistentTelemetrySender
            // (the next Init reuses it) but nulls the reloaded instance's
            // _telemetrySender. If SimHub then keeps driving DataUpdate on a
            // stale instance, _telemetrySender is null and the game-data feed
            // silently stops — the persistent sender keeps emitting its last
            // snapshot forever, freezing the dashboard on stale data while the
            // wire/binding look healthy (observed 2026-06-06, W13). Route the
            // feed to the persistent sender so it always reaches the emitter.
            var sender = _telemetrySender ?? s_persistentTelemetrySender;
            if (_telemetrySender == null && sender != null && !_warnedStaleDataFeed)
            {
                _warnedStaleDataFeed = true;
                MozaLog.Warn("[AZOM] DataUpdate fired with _telemetrySender=null — routing game " +
                             "data to the persistent sender (stale post-reload instance).");
            }
            sender?.UpdateGameData(data.NewData);
            sender?.SetGameRunning(data.GameRunning);
            _fsr1Driver?.UpdateGameData(data.NewData);
            _fsr1Driver?.SetGameRunning(data.GameRunning);
            _cm2Sender?.UpdateGameData(data.NewData);
            _cm2Sender?.SetGameRunning(data.GameRunning);
            _cm1Driver?.UpdateGameData(data.NewData);
            _cm1Driver?.SetGameRunning(data.GameRunning);
            CheckGearshiftEvent(data);
            CheckAb9GearshiftEvent(data);

            // Push SimHub's shared/master LED brightness to the wheel firmware group
            // brightness (rpm/buttons/knobs) when the user moves the slider. The wheel
            // LED driver publishes the settled value into WheelLedMasterBrightness off
            // the LED thread; apply it here (change-gated) so the firmware write runs on
            // the data thread and shares HardwareApplier's per-wheel cfg cache with the
            // connect/profile brightness path (no fight, no redundant flash).
            int masterLedBri = WheelLedMasterBrightness;
            if (masterLedBri != _masterLedBrightnessApplied)
            {
                _masterLedBrightnessApplied = masterLedBri;
                if (masterLedBri >= 0)
                    _hardwareApplier?.ApplyMasterWheelLedBrightness(masterLedBri);
            }

            // Hand the latest RPM, MaxRpm + engine-on flag to the AB9 engine-vib
            // worker. GameRunning stays true while paused or in menu, so we'd
            // keep streaming buzz frames the whole time the user is in the
            // pause menu without this gate. GamePaused / GameInMenu collapse
            // the stream to silent-keepalive within one tick of the user
            // pressing Esc / returning to the menu. MaxRpm drives the worker's
            // rpm/redline intensity scaling — games that don't report it fall
            // back to flat (unscaled) amplitude.
            double rpm = data.NewData?.Rpms ?? 0.0;
            double maxRpm = data.NewData?.MaxRpm ?? 0.0;
            bool engineOn = data.GameRunning && !data.GamePaused && !data.GameInMenu;
            _ab9Worker?.PostFrame(rpm, maxRpm, engineOn);

            // Wheelbase LFE worker: just feed liveness (running & not paused/in-menu).
            // All per-channel values (RPM, ABSActive, Gear, …) come from the
            // channels' own formulas, evaluated live via the property resolver.
            _baseLfeWorker?.PostFrame(engineOn);

            // Control Mapper variant-provider bridge: drive wheel-change detection
            // each tick when registered; otherwise retry registration up to the
            // tick budget (ControlMapperPlugin may not be loaded at Init time).
            if (_controlMapperBridge != null)
            {
                if (_controlMapperBridge.IsRegistered)
                {
                    _controlMapperBridge.Poll();
                }
                else if (_controlMapperRetryTicks > 0 && !_controlMapperBridge.IsGivenUp)
                {
                    _controlMapperRetryTicks--;
                    if (_controlMapperBridge.TryRegister(pluginManager))
                        _controlMapperRetryTicks = 0;
                    else if (_controlMapperRetryTicks == 0 && !_controlMapperBridge.IsGivenUp)
                        MozaLog.Warn(
                            "[AZOM] ControlMapper bridge: ControlMapperPlugin never became available — " +
                            "giving up retry. Variant integration disabled this session.");
                }
            }

            // Slice F: DataUpdate hook re-enabled.
            // Fan-out fresh telemetry to every mBooster's effect worker.
            // Lock-free fast path: when no mBoosters are registered (the
            // common case for users without the device), skip the entire
            // snapshot build + LockedDict traversal. HasControllers reads a
            // volatile int updated only on Refresh().
            if (_mboosterRegistry != null && _mboosterRegistry.HasControllers)
            {
                var nd = data.NewData;
                double brake01 = (nd?.Brake ?? 0.0) / 100.0;
                if (brake01 < 0) brake01 = 0; if (brake01 > 1) brake01 = 1;
                double throttle01 = (nd?.Throttle ?? 0.0) / 100.0;
                if (throttle01 < 0) throttle01 = 0; if (throttle01 > 1) throttle01 = 1;
                // ABSActive/TCActive are SimHub's loosely-typed properties —
                // games supply bool / int / sbyte / byte / short / long
                // depending on backend. Pattern-match the common shapes to
                // skip Convert.ToInt32's InvariantCulture lookup and the
                // try/catch on the hot path (DataUpdate runs at SimHub's
                // data rate, ~60Hz+). Unknown types fall through to false —
                // same observable behaviour as the catch-and-default that
                // lived here previously.
                object? rawAbs = nd?.ABSActive;
                bool absActive = rawAbs switch
                {
                    bool b   => b,
                    int i    => i != 0,
                    byte by  => by != 0,
                    sbyte sb => sb != 0,
                    short sh => sh != 0,
                    long lo  => lo != 0,
                    _ => false,
                };
                object? rawTc = nd?.TCActive;
                bool tcActive = rawTc switch
                {
                    bool b   => b,
                    int i    => i != 0,
                    byte by  => by != 0,
                    sbyte sb => sb != 0,
                    short sh => sh != 0,
                    long lo  => lo != 0,
                    _ => false,
                };
                double vehicleMs = (nd?.SpeedKmh ?? 0.0) / 3.6;
                double avgWheelMs = 0.0;
                double idleRpm = 800.0;
                // No generic suspension-travel telemetry exists in SimHub;
                // AccelerationHeave (vertical G) is the closest proxy for
                // road-surface roughness. Nullable — 0 for games that don't
                // report it, same fail-soft style as the rest of this block.
                double suspensionHeaveG = nd?.AccelerationHeave ?? 0.0;
                // Brake Fade's temperature signal — peak across all 4
                // corners (any one wheel overheating should trigger the
                // warning, not just the average). BrakesTemperatureMax is
                // nullable — 0 for games that don't report it. Normalized
                // to Celsius: TemperatureUnit is a per-game display hint
                // (Celsius/Fahrenheit), same "unit gotcha" the protocol
                // note warns about for speed fields elsewhere in this
                // method — fail-soft substring match rather than an exact
                // string comparison, since the real set of values SimHub's
                // game plugins actually write isn't documented anywhere.
                double brakeTempRaw = nd?.BrakesTemperatureMax ?? 0.0;
                string tempUnit = nd?.TemperatureUnit ?? "";
                double brakeTempC = tempUnit.IndexOf("F", StringComparison.OrdinalIgnoreCase) >= 0
                    ? (brakeTempRaw - 32.0) * 5.0 / 9.0
                    : brakeTempRaw;
                // Gear-change edge for the mBooster's Gear Shift effect —
                // same string-latch + warm-up-guard pattern as
                // CheckGearshiftEvent (wheelbase) / CheckAb9GearshiftEvent,
                // but with its own independent latch and no debounce here
                // (each mBooster device applies its own debounce/neutral
                // settings in MBoosterEffectWorker.UpdateGearShiftRequest).
                string? gearForMBooster = nd?.Gear;
                if (!string.IsNullOrEmpty(gearForMBooster))
                {
                    if (_lastMBoosterGearString == null)
                    {
                        _lastMBoosterGearString = gearForMBooster; // warm-up: don't fire on the first observed value
                    }
                    else if (gearForMBooster != _lastMBoosterGearString)
                    {
                        _lastMBoosterGearString = gearForMBooster;
                        _mboosterShiftSeq++; // monotonic — the worker samples this on its own timer; a bool edge would be dropped when DataUpdate outruns it
                    }
                }
                // Level (not edge): true whenever the current gear is Neutral,
                // so the worker reads valid neutral-ness even if it samples a
                // tick or two after _mboosterShiftSeq advanced.
                bool gearIsNeutral = gearForMBooster == "N" || gearForMBooster == "0";
                var snap = new MBoosterTelemetrySnapshot(
                    gameRunning: data.GameRunning,
                    rpm: rpm,
                    maxRpm: maxRpm,
                    idleRpm: idleRpm,
                    brake: brake01,
                    throttle: throttle01,
                    absActive: absActive,
                    tcActive: tcActive,
                    vehicleSpeedMs: vehicleMs,
                    avgWheelSpeedMs: avgWheelMs,
                    suspensionHeaveG: suspensionHeaveG,
                    brakeTempC: brakeTempC,
                    gearShiftSeq: _mboosterShiftSeq,
                    gearIsNeutral: gearIsNeutral);
                _mboosterRegistry.OnDataUpdate(snap);
            }

            // Auto-standby: wake the base the instant a game starts; standby is
            // deferred to the idle-timeout reconcile below.
            ApplyAutoStandby();
        }

        /// <summary>
        /// Mark the user as actively using the plugin (e.g. interacting with the
        /// settings UI). Bumps the auto-standby activity clock so the wheel does
        /// not power down mid-configuration. Cheap and safe to call regardless of
        /// whether auto-standby is enabled.
        /// </summary>
        internal void NotifyUserActivity()
        {
            Interlocked.Exchange(ref _autoStandbyLastActivityTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Called when the user turns auto-standby off. If auto-standby had put
        /// the base to sleep, wake it — disabling the feature should never leave
        /// the wheel powered down. No-op if we didn't cause the standby.
        /// </summary>
        internal void CancelAutoStandby()
        {
            bool weStandbyed = _autoStandbyApplied == 1;
            _autoStandbyApplied = -1;
            if (!weStandbyed || !DetectionState.BaseDetected) return;
            if (_data != null) _data.WorkMode = 0;
            WriteIfBaseConnected("main-set-work-mode", 0);
            MozaLog.Info("[AZOM] Auto-standby disabled — waking base");
        }

        /// <summary>
        /// Opt-in auto-standby (see the field block above). With
        /// <see cref="MozaPluginSettings.AutoStandbyWhenNoGame"/> enabled, send
        /// <c>main-set-work-mode</c>=1 (standby) once the wheel has been idle for
        /// <see cref="MozaPluginSettings.AutoStandbyTimeoutMinutes"/> with no game
        /// and no activity, and =0 (active) the moment a game runs or the user
        /// interacts. Idempotent — writes only when the desired value changes —
        /// so it is safe to call every DataUpdate tick and from PollStatus.
        /// Never standbys on the first reconcile (lazy activity baseline), so the
        /// plugin never boots the wheel straight into standby. No-op without a
        /// detected base.
        /// </summary>
        internal void ApplyAutoStandby()
        {
            if (IsShuttingDown) return;
            var settings = _settings;
            if (settings == null || !settings.AutoStandbyWhenNoGame) { _autoStandbyApplied = -1; return; }
            if (!DetectionState.BaseDetected) { _autoStandbyApplied = -1; return; }

            long now = DateTime.UtcNow.Ticks;
            // Lazy baseline: the first reconcile after startup/reload seeds the
            // activity clock so we can never standby immediately ("never start in
            // standby") — the first write below is therefore always wake=0.
            if (Interlocked.Read(ref _autoStandbyLastActivityTicks) == 0)
                Interlocked.Exchange(ref _autoStandbyLastActivityTicks, now);

            // A game is "active" only with a fresh data feed AND GameRunning —
            // DataUpdate goes quiet when no game runs, so a stale feed means no
            // game even if the last GameRunning we saw was true.
            bool gameActive = IsGameActive;

            if (gameActive)
                Interlocked.Exchange(ref _autoStandbyLastActivityTicks, now); // running game keeps it awake
            else
                MaybeStampHidActivity(now); // physical wheel/pedal/button use keeps it awake

            long idleMs = (now - Interlocked.Read(ref _autoStandbyLastActivityTicks)) / TimeSpan.TicksPerMillisecond;
            int timeoutMin = settings.AutoStandbyTimeoutMinutes;
            if (timeoutMin < 1) timeoutMin = 1;
            long timeoutMs = (long)timeoutMin * 60_000L;

            int desired = (!gameActive && idleMs >= timeoutMs) ? 1 : 0; // 1 = standby, 0 = active
            if (_autoStandbyApplied == desired) return; // write only on change

            _autoStandbyApplied = desired;
            if (_data != null) _data.WorkMode = desired; // keep the UI toggle in sync
            WriteIfBaseConnected("main-set-work-mode", desired);
            MozaLog.Info($"[AZOM] Auto-standby: {(desired == 1 ? $"standby (idle {idleMs / 1000}s >= {timeoutMin}m)" : "wake (active)")}");
        }

        /// <summary>
        /// Bump the activity clock when physical input (steering, pedals,
        /// paddles, handbrake, or buttons) has changed past a small deadband
        /// since the last sample. The HID reader runs continuously on its own
        /// thread, so this works with no game and the settings pane closed. The
        /// first sample only seeds the baseline (never counts as activity).
        /// </summary>
        private void MaybeStampHidActivity(long nowTicks)
        {
            var data = _data;
            if (data == null || !data.IsHidConnected) return;

            double steerD = _hidReader?.GetSteeringPositionPercent() ?? -1.0;
            int steer = steerD < 0 ? -1 : (int)Math.Round(steerD);
            int thr = data.ThrottlePosition, brk = data.BrakePosition, clu = data.ClutchPosition;
            int hb = data.HandbrakePosition, lp = data.LeftPaddlePosition, rp = data.RightPaddlePosition;
            int btnHash = ComputeButtonActivityHash(data);

            if (!_asHidBaselined)
            {
                _asSteer = steer; _asThrottle = thr; _asBrake = brk; _asClutch = clu;
                _asHandbrake = hb; _asLeftPaddle = lp; _asRightPaddle = rp; _asButtonHash = btnHash;
                _asHidBaselined = true;
                return;
            }

            const int Dead = 3; // percent units — above sensor jitter, below deliberate movement
            bool active = false;
            // Rebaseline per axis only when it moves past the deadband, so slow
            // deliberate movement still registers (each Dead% of travel) while
            // resting jitter never does.
            if (steer >= 0 && (_asSteer < 0 || Math.Abs(steer - _asSteer) >= Dead)) { _asSteer = steer; active = true; }
            if (Math.Abs(thr - _asThrottle) >= Dead) { _asThrottle = thr; active = true; }
            if (Math.Abs(brk - _asBrake) >= Dead) { _asBrake = brk; active = true; }
            if (Math.Abs(clu - _asClutch) >= Dead) { _asClutch = clu; active = true; }
            if (Math.Abs(hb - _asHandbrake) >= Dead) { _asHandbrake = hb; active = true; }
            if (Math.Abs(lp - _asLeftPaddle) >= Dead) { _asLeftPaddle = lp; active = true; }
            if (Math.Abs(rp - _asRightPaddle) >= Dead) { _asRightPaddle = rp; active = true; }
            if (btnHash != _asButtonHash) { _asButtonHash = btnHash; active = true; }

            if (active) Interlocked.Exchange(ref _autoStandbyLastActivityTicks, nowTicks);
        }

        private static int ComputeButtonActivityHash(MozaData data)
        {
            int h = data.HandbrakeButtonPressed ? 1 : 0;
            var b = data.ButtonStates;
            for (int i = 0; i < b.Length; i++)
                if (b[i]) h = (h * 31) + (i + 2);
            // Stalks live on their own button surface (see MozaData.StalksButtonStates)
            // but pressing them is still user activity — keep it counting toward standby.
            var s = data.StalksButtonStates;
            for (int i = 0; i < s.Length; i++)
                if (s[i]) h = (h * 31) + (i + 1000);
            return h;
        }

        /// <summary>
        /// Look up (or lazily create) the per-device mBooster settings entry
        /// in the current profile. Called by the registry and the effect
        /// worker on every tick — must be allocation-free for known devices.
        /// </summary>
        // Transport-identity → "mbooster:<serial>" once a lane's serial is
        // interrogated. Populated on OnMBoosterSerialResolved; read lock-free in
        // GetOrCreateMBoosterSettings. Deliberately NOT resolved via the
        // registry there — MergePositions calls in while holding the registry
        // lock, so consulting the registry under _mboosterSettingsLock would
        // invert the lock order.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _mboosterSerialByIdentity =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _mboosterSettingsLock = new object();

        internal MBoosterDeviceSettings GetOrCreateMBoosterSettings(string identity)
        {
            // Resolve a transport identity to the device's stable serial key so
            // per-device settings follow the physical unit across USB ports.
            string key = identity ?? "";
            string original = key;
            if (!string.IsNullOrEmpty(key) && _mboosterSerialByIdentity.TryGetValue(key, out var serialKey))
                key = serialKey;

            lock (_mboosterSettingsLock)
            {
                var profile = _settings?.ProfileStore?.CurrentProfile;
                if (profile == null) return new MBoosterDeviceSettings();
                if (profile.MBoosterSettings == null)
                    profile.MBoosterSettings = new Dictionary<string, MBoosterDeviceSettings>(StringComparer.OrdinalIgnoreCase);
                var dict = profile.MBoosterSettings;

                // Lazily migrate a transient transport-keyed entry to the serial
                // key in the current profile. A serial-keyed entry, if one
                // already exists (the user's saved config from a prior session),
                // wins — the transport entry is a just-created placeholder.
                if (!string.Equals(original, key, StringComparison.OrdinalIgnoreCase)
                    && dict.TryGetValue(original, out var stale))
                {
                    if (!dict.ContainsKey(key)) dict[key] = stale;
                    dict.Remove(original);
                }

                if (!dict.TryGetValue(key, out var s) || s == null)
                {
                    s = new MBoosterDeviceSettings();
                    dict[key] = s;
                }
                return s;
            }
        }

        /// <summary>
        /// A lane's 32-char Moza serial has been interrogated. Record the
        /// identity→serial mapping (so settings lookups re-key to it), migrate
        /// the current profile's entry, and re-apply the now serial-keyed
        /// settings to the device — at detect we applied the transient
        /// transport-keyed entry, but the real config may live under the serial
        /// key from a prior session. Runs on the connection read thread.
        /// </summary>
        private void OnMBoosterSerialResolved(string identity, string serial)
        {
            if (IsShuttingDown || string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(serial)) return;
            _mboosterSerialByIdentity[identity] = "mbooster:" + serial;
            try
            {
                var settings = GetOrCreateMBoosterSettings(identity); // resolves + migrates current profile
                var controller = _mboosterRegistry?.FindByIdentity(identity);
                if (controller != null) ApplyMBoosterToHardware(controller, settings);
            }
            catch (Exception ex) { MozaLog.Warn($"[AZOM/mBooster] serial re-key for {MBoosterDeviceController.ShortIdentity(identity)}: {ex.Message}"); }
        }

        /// <summary>
        /// Called once per detection rising edge by the registry. Pushes any
        /// saved calibration values to the device and kicks off a read-back
        /// for unset calibration fields. The doc warns this surface may not
        /// be honored by mBooster firmware — we attempt it anyway since the
        /// user opted in.
        /// </summary>
        private void OnMBoosterDeviceDetected(MBoosterDeviceController controller)
        {
            if (IsShuttingDown || controller == null) return;
            try
            {
                MozaLog.Info($"[AZOM/mBooster] Applying settings for {MBoosterDeviceController.ShortIdentity(controller.Identity)} (experimental calibration surface)");
                var s = GetOrCreateMBoosterSettings(controller.Identity);
                ApplyMBoosterToHardware(controller, s);
                // Always issue a calibration read burst on detect so the panel
                // can populate (or so we learn the device ignored them).
                controller.RequestCalibrationReads();
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM/mBooster] OnDetected for {controller.Identity}: {ex.Message}");
            }
        }

        /// <summary>
        /// Push calibration values (direction / min / max / curve) for one
        /// mBooster to its device. Sentinel-guarded — values left at -1 (or
        /// null array) are skipped, so a fresh profile with no overrides
        /// produces zero hardware writes. Per protocol note § 6 these
        /// commands are "likely but unverified" on mBooster firmware.
        /// </summary>
        internal void ApplyMBoosterToHardware(MBoosterDeviceController controller, MBoosterDeviceSettings s)
        {
            if (controller == null || s == null || !controller.IsConnected) return;

            // Route Direction / Min / Max / output-curve to the command slot
            // matching the pedal's ROLE. This used to be hardcoded to the
            // "throttle" slot, which is wrong for a mBooster used as a brake or
            // clutch (and for a chain whose master pedal isn't the throttle) —
            // the calibration silently landed on the wrong pedal's command.
            // The role is the master pedal's (axis 0): ResolveAxisRole gives the
            // legacy Role for a single unit or the chain default/override
            // otherwise. Per-pedal calibration for the OTHER chained pedals is a
            // follow-up (needs a per-pedal settings UI); this fixes the routing
            // for the pedal the current single calibration set configures.
            int axisCount = controller.AxisCount > 0 ? controller.AxisCount : 1;
            // Apply EACH hosted pedal's calibration to its role-specific command.
            // Pedal 0 (master) keeps its calibration in the flat fields (the
            // existing UI); the additional chained pedals (axes 1+) store theirs
            // in s.Pedals[axis]. An unconfigured pedal (all -1 / null) writes
            // nothing.
            for (int axis = 0; axis < axisCount && axis < global::MozaPlugin.Devices.MBoosterDeviceController.MaxAxes; axis++)
            {
                var role = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResolveAxisRole(s, axis, axisCount);
                string? prefix =
                    role == global::MozaPlugin.Devices.MBoosterRole.Throttle ? "throttle"
                    : role == global::MozaPlugin.Devices.MBoosterRole.Brake ? "brake"
                    : role == global::MozaPlugin.Devices.MBoosterRole.Clutch ? "clutch"
                    : null;
                if (prefix == null) continue;

                // This pedal's full config: master flat fields (axis 0) or its
                // per-pedal entry. An unconfigured chained pedal writes nothing.
                global::MozaPlugin.Devices.IMBoosterPedalConfig cfg;
                if (axis == 0) cfg = s;
                else if (s.Pedals != null && s.Pedals.TryGetValue(axis, out var p) && p != null) cfg = p;
                else continue;

                bool wroteAnyCalibration = false;

                // Every per-pedal calibration here is a PHYSICAL setting stored
                // on that pedal's own mBooster unit (confirmed on hardware: each
                // unit reports only its own pedal's calibration, under its own
                // role register). Address it by the pedal's ROLE through the
                // calibration-derived chain map (same as the effects — see
                // MBoosterEffectWorker.TargetDevice), NOT the raw HID axis: the
                // motor/config device id follows the chain plug position, which
                // doesn't match the HID axis order, so an axis-index device
                // sends these writes to the wrong physical pedal. Falls back to
                // the axis mapping (0x12 for a standalone) until the map resolves.
                int roleIdx = role == global::MozaPlugin.Devices.MBoosterRole.Throttle ? 0
                            : role == global::MozaPlugin.Devices.MBoosterRole.Brake ? 1
                            : role == global::MozaPlugin.Devices.MBoosterRole.Clutch ? 2 : -1;
                byte dev = controller.MotorDeviceForRole(roleIdx, axis);

                if (cfg.Direction >= 0) { controller.SendIntWrite($"mbooster-{prefix}-dir", cfg.Direction, dev); wroteAnyCalibration = true; }
                if (cfg.Min >= 0) { controller.SendIntWrite($"mbooster-{prefix}-min", cfg.Min, dev); wroteAnyCalibration = true; }
                if (cfg.Max >= 0) { controller.SendIntWrite($"mbooster-{prefix}-max", cfg.Max, dev); wroteAnyCalibration = true; }
                if (cfg.CurveY != null && cfg.CurveY.Length == 5)
                {
                    wroteAnyCalibration = true;
                    // Resample at the fixed 20/40/60/80/100 breakpoints in case
                    // CurveX has been horizontally dragged (see
                    // MozaMBoosterRegistry.ResampleCurveAtFixedBreakpoints) —
                    // identity when it hasn't.
                    var resampled = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResampleCurveAtFixedBreakpoints(cfg.CurveX, cfg.CurveY);
                    for (int k = 0; k < 5; k++)
                        controller.SendFloatWrite($"mbooster-{prefix}-y{k + 1}", resampled[k], dev);
                }
                if (cfg.TravelStartMm >= 0)
                {
                    controller.SendIntWrite("mbooster-brake-travel-start",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeTravelMm(cfg.TravelStartMm), dev);
                    wroteAnyCalibration = true;
                }
                if (cfg.TravelEndMm >= 0)
                {
                    controller.SendIntWrite("mbooster-brake-travel-end",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeTravelMm(cfg.TravelEndMm), dev);
                    wroteAnyCalibration = true;
                }
                if (cfg.EndstopFrontStiffness >= 0)
                {
                    controller.SendIntWrite("mbooster-brake-endstop-front",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeEndstopStiffness(cfg.EndstopFrontStiffness), dev);
                    wroteAnyCalibration = true;
                }
                if (cfg.EndstopEndStiffness >= 0)
                {
                    controller.SendIntWrite("mbooster-brake-endstop-end",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeEndstopStiffness(cfg.EndstopEndStiffness), dev);
                    wroteAnyCalibration = true;
                }
                if (role == global::MozaPlugin.Devices.MBoosterRole.Brake)
                {
                    if (cfg.SensorOutputRatioPct >= 0)
                    {
                        controller.SendFloatWrite("mbooster-brake-angle-ratio", cfg.SensorOutputRatioPct, dev);
                        wroteAnyCalibration = true;
                    }
                    if (cfg.MaxThresholdKg >= 0)
                    {
                        controller.SendIntWrite("mbooster-brake-threshold",
                            global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeThresholdKg(cfg.MaxThresholdKg), dev);
                        wroteAnyCalibration = true;
                    }
                }

                // EXPERIMENTAL / unverified — confirmed on hardware to be
                // required for a Travel edit to actually take effect; applied
                // here too on the theory the same firmware requirement covers
                // every write above, not just Travel. See
                // MBoosterDeviceController.PushCurve7Resync. Guarded like the
                // writes above (not unconditional) to preserve this method's
                // "fresh profile with no overrides produces zero hardware
                // writes" guarantee.
                if (wroteAnyCalibration)
                    controller.PushCurve7Resync(cfg.CurveX, cfg.CurveY, dev);
            }
        }

        // Resolve a dashboard name to its parsed MultiStreamProfile without firing
        // Resolves a profile by name (cache → builtin) without touching the
        // current telemetry profile — used by SwitchToProfile to avoid racing
        // ApplyTelemetrySettings's full-stack reload.
        internal MultiStreamProfile? ResolveDashboardProfileByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (DashCache != null)
            {
                var p = DashCache.TryGetByName(name);
                if (p != null) return p;
            }
            var builtins = DashProfileStore.BuiltinProfiles;
            foreach (var p in builtins)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        public void End(PluginManager pluginManager)
        {
            IsShuttingDown = true;
            MozaLog.Info("[AZOM] Shutting down plugin");

            // 1. Stop timers first so no new callbacks fire against disposed state.
            _profileCoordinator?.StopSaveDebounceTimer();
            _pollTimer?.Stop();
            _retryTimer?.Stop();
            _reconnectTimer?.Stop();

            // Stop the AB9 engine-vib worker before the AB9 manager / connection
            // are disposed; the tick gates on connection state but joining here
            // keeps shutdown deterministic.
            try { _ab9Worker?.Stop(); _ab9Worker = null; } catch { }
            try { _baseLfeWorker?.Stop(); _baseLfeWorker = null; } catch { }
            // Release the timer-resolution request + power-throttling opt-out on
            // shutdown/reload so neither leaks past the plugin's lifetime.
            try { _responsiveness?.Dispose(); _responsiveness = null; } catch { }

            // Remove the Control Mapper variant provider so a plugin reload
            // (game switch) doesn't leave a dead provider in VariantHelper's
            // list. The bridge is null when the toggle was off or construction
            // failed in Init.
            try { _controlMapperBridge?.Unregister(); _controlMapperBridge = null; } catch { }

            // Burst silent-slot frames + an engine-pulse OFF to stop the AB9
            // effect immediately on shutdown. Without this the firmware keeps
            // the last-streamed buzz running until its ~10 s keepalive timeout,
            // which means users hear vibrations for ten seconds after closing
            // SimHub mid-session. Worker is stopped above so this can't race.
            try { _ab9Manager?.SendEngineSilence(); } catch { }
            // Flush LFE effects so the base doesn't latch a waveform on teardown.
            try
            {
                _deviceManager?.SendBaseLfeDisable(Protocol.MozaBaseLfeProtocol.LfeEffect.Engine);
                _deviceManager?.SendBaseLfeDisable(Protocol.MozaBaseLfeProtocol.LfeEffect.Abs);
                _deviceManager?.SendBaseLfeDisable(Protocol.MozaBaseLfeProtocol.LfeEffect.Gearshift);
            }
            catch { }

            // Dispose every mBooster controller — fires disable frames + closes
            // each connection. Must happen before MozaData is torn down so the
            // position-merge path (which writes to _data) doesn't race.
            try { _mboosterRegistry?.Dispose(); _mboosterRegistry = null; } catch { }
            // Standalone pedals/handbrake connections — close before MozaData
            // teardown so the response path (which writes to _data) can't race.
            try { _peripheralRegistry?.Dispose(); _peripheralRegistry = null; } catch { }

            // Tear down SDK emulation up-front. The CoAP receive thread holds
            // references into MozaData and HardwareApplier; stop it before the
            // rest of the wire stack disposes those out from under it. The
            // PitHouse UDP control server holds the same references and uses
            // the same shutdown pattern.
            try { _sdkServer?.Stop(); _sdkServer?.Dispose(); _sdkServer = null; }
            catch (Exception ex) { MozaLog.Warn($"[Sdk] server stop: {ex.Message}"); }
            try { _controlUdpServer?.Stop(); _controlUdpServer?.Dispose(); _controlUdpServer = null; }
            catch (Exception ex) { MozaLog.Warn($"[PitHouseUdp] server stop: {ex.Message}"); }

            // Decide up-front whether the persistent wire will survive this
            // teardown — same condition the dispose step below uses. We need
            // it now so detection state can be captured (vs. wiped) in lock-
            // step with the wire AND so the CoAP stub manager teardown below
            // can match the wire's persistence decision.
            bool keepWireAlive = _usingPersistentWire
                                 || (_connection != null && _connection == s_persistentConnection
                                     && _telemetrySender != null
                                     && _telemetrySender == s_persistentTelemetrySender);

            // CoAP stub manager: persist across game-switch reloads alongside
            // the wire. Stopping the stub on every End()+Init() cycle (a) is
            // wasted work (the stub holds no per-plugin-instance state) and
            // (b) intermittently HANGS under Wine/Proton — Stop() wedged on
            // the 2026-05-25 second-game-switch path between the registry
            // restore log and the Process.Kill / JobObject.Dispose call.
            //
            // FSR1 driver + CM2 sender are per-instance (recreated each Init), never
            // persistent — always stop them on End so a keepWireAlive game-switch
            // doesn't leave two ticking the same connection after re-Init.
            try { _fsr1Driver?.Dispose(); } catch { }
            _fsr1Driver = null;
            try { _cm2Sender?.Dispose(); } catch { }
            _cm2Sender = null;
            try { _cm1Driver?.Dispose(); } catch { }
            _cm1Driver = null;

            // When keepWireAlive=true we just drop the instance ref; the
            // persistent static keeps the child process alive for the next
            // plugin instance to reuse via the IsRunning check in Init.
            // When the wire is being torn down (true cold-start reset, not a
            // game switch), stop the stub too and clear the static.
            if (keepWireAlive)
            {
                _sdkStubManager = null;
            }
            else
            {
                // Bounded so the End() flow (often runs on the SimHub UI
                // thread) can't get pinned by a Wine-side wedge in
                // Process.Kill / JobObject.Dispose.
                try { _sdkStubManager?.TryStop(1500); }
                catch (Exception ex) { MozaLog.Warn($"[Sdk] stub stop: {ex.Message}"); }
                if (_sdkStubManager != null
                    && ReferenceEquals(_sdkStubManager, s_persistentSdkStubManager))
                    s_persistentSdkStubManager = null;
                _sdkStubManager = null;
            }

            if (keepWireAlive)
            {
                // Wire stays; the device(s) on the other end are still those
                // we already probed. Hand the detection bag to the next plugin
                // instance so sub-device tabs (handbrake/pedals/hub/dash) stay
                // visible across the reload — presence probes don't reliably
                // re-ACK on the reused wire and would otherwise leave tabs
                // permanently hidden until SimHub restarts.
                s_persistentDetectionState = DetectionState;
            }
            else
            {
                // Clear detection flags so a future cold-start Init() doesn't
                // see stale state from a wire that's about to be torn down.
                ResetDetectionFlags();
            }

            // 2. Persist settings and clear LEDs while connection is still alive.
            try { this.SaveCommonSettings("MozaPluginSettings", _settings); } catch { }
            try { ClearLedsOnHardware(); } catch { }

            // 3. Detach event subscriptions so any in-flight callback from a still-running
            //    background thread (HID/serial reader) cannot reach the plugin during teardown.
            //    PowerModeChanged is static — detach first so a resume mid-teardown can't
            //    schedule a ForceReconnect against tearing-down state.
            UnhookPowerMode();
            try
            {
                if (_connection != null)
                {
                    _connection.MessageReceived -= OnMessageReceived;
                    _connection.Disconnected -= OnSerialDisconnected;
                }
            }
            catch { }
            try
            {
                if (_telemetrySender != null)
                {
                    _telemetrySender.DashboardPipelineParked -= _dashboardBindingCoordinator.OnDashboardPipelineParked;
                    _telemetrySender.WheelInitiatedSwitch -= _dashboardBindingCoordinator.OnWheelInitiatedSwitch;
                }
            }
            catch { }
            try { _profileCoordinator?.DetachProfileStore(); } catch { }

            // 4. Persistent wire: skip Stop+Dispose if we own the static refs
            //    so the next Init picks up open sessions without the settle wait.
            //    (keepWireAlive was computed earlier so detection-state capture
            //    could happen before ResetDetectionFlags() wiped the bag.)
            if (!keepWireAlive)
            {
                _telemetrySender?.Stop();
            }

            // Release the wire-trace file handle (new file per Init by design).
            // Keep the in-memory ring enabled across plugin reload when always-capture
            // is on, so buffered frames survive game switches.
            try { global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.StopFileSink(); } catch { }
            if (_settings?.AlwaysCaptureOnStartup != true)
            {
                try { global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.Stop(); } catch { }
            }

            // 5. Cancel paced setting-reads (avoids tasks running past teardown).
            try { _deviceManager?.Dispose(); } catch { }

            // 6. Dispose I/O sources; skip sender+connection if keeping wire alive.
            _hidReader?.Dispose();
            try { _stalksController?.Dispose(); } catch { }
            if (!keepWireAlive)
            {
                _telemetrySender?.Dispose();
                _fsr1Driver?.Dispose();
                _cm1Driver?.Dispose();
                _connection?.Dispose();
                // Clear static refs so the next Init takes the cold-start path.
                if (_connection == s_persistentConnection)
                    s_persistentConnection = null;
                if (_telemetrySender == s_persistentTelemetrySender)
                    s_persistentTelemetrySender = null;
                // Wire is gone — discard the captured detection bag too so the
                // next Init re-probes against whatever's actually attached.
                s_persistentDetectionState = null;
            }
            else
            {
                MozaLog.Info(
                    "[AZOM] End: keeping persistent wire (connection + telemetry sender) alive " +
                    "across plugin reload — wheel sessions remain open, no settle wait on next Init");
            }
            try
            {
                if (_ab9Manager != null)
                    _ab9Manager.MessageReceived -= OnAb9MessageReceived;
            }
            catch { }
            _ab9Manager?.Dispose();

            try
            {
                if (_dashboardManager != null)
                {
                    _dashboardManager.MessageReceived -= OnDashboardMessageReceived;
                    _dashboardManager.Connection.Disconnected -= OnDashboardDisconnected;
                }
            }
            catch { }
            _dashboardManager?.Dispose();

            try
            {
                if (_hubManager != null)
                {
                    _hubManager.MessageReceived -= OnHubMessageReceived;
                    _hubManager.Connection.Disconnected -= OnHubDisconnected;
                }
                if (_baseManager != null)
                {
                    _baseManager.MessageReceived -= OnBaseMessageReceived;
                    _baseManager.Connection.Disconnected -= OnBaseDisconnected;
                }
            }
            catch { }
            _hubManager?.Dispose();
            _baseManager?.Dispose();

            // 7. Dispose timers after I/O is gone.
            _profileCoordinator?.DisposeSaveDebounceTimer();
            _pollTimer?.Dispose();
            _retryTimer?.Dispose();
            _reconnectTimer?.Dispose();

            // 8. Null Instance last so any straggler callback can still no-op via IsShuttingDown.
            Instance = null;
        }

        // One-shot registration of the AppDomain.ProcessExit handler. Safe
        // to call from every Init() — only the first crosses the gate.
        private static void EnsureProcessExitHandlerRegistered()
        {
            if (Interlocked.Exchange(ref s_processExitHandlerRegistered, 1) != 0) return;
            try { AppDomain.CurrentDomain.ProcessExit += OnAppDomainProcessExit; }
            catch (Exception ex)
            {
                try { MozaLog.Warn($"[AZOM] ProcessExit handler registration failed: {ex.Message}"); } catch { }
            }
        }

        // Fires when the SimHub process is terminating. End() ran earlier
        // for each plugin instance but the keepWireAlive branch leaves the
        // persistent telemetry sender / serial connection alive (so plugin
        // reloads on game switch don't pay the sess=0x09 settle wait). On
        // full exit we still need a clean SessionClose 0x01/0x02/0x03
        // burst so the wheel doesn't carry stale host-side session state
        // into the next SimHub launch — see s_processExitHandlerRegistered
        // doc for the failure mode.
        //
        // ProcessExit has a ~2 s budget before the runtime is killed. Stop()
        // takes ~110 ms (timer dispose + FlushPendingWrites + 3 close frames
        // + 100 ms drain sleep), well inside budget. Connection Dispose then
        // closes the serial port cleanly so the close frames actually leave
        // the OS write buffer before the FTDI handle goes away.
        private static void OnAppDomainProcessExit(object? sender, EventArgs e)
        {
            try
            {
                var ts = s_persistentTelemetrySender;
                if (ts != null && !ts.IsDisposedFlag)
                {
                    try { ts.Stop(); }
                    catch (Exception ex)
                    {
                        try { MozaLog.Warn($"[AZOM] ProcessExit Stop(): {ex.GetType().Name}: {ex.Message}"); } catch { }
                    }
                }
            }
            catch { }
            try
            {
                var conn = s_persistentConnection;
                conn?.Dispose();
            }
            catch { }
            // Stop the persistent CoAP stub on full process exit so its child
            // process (and the registry redirect) don't outlive SimHub.
            // TryStop bounds the call at 1.5 s — well inside the ~2 s
            // ProcessExit budget — so a Wine-side wedge in Process.Kill or
            // JobObject.Dispose can't keep us from returning. If TryStop
            // times out the JobObject's KILL_ON_JOB_CLOSE backstops the
            // child cleanup on process exit, and the next launch's
            // orphan sweep handles the case where even that didn't fire.
            try { s_persistentSdkStubManager?.TryStop(1500); }
            catch { }
        }

        /// <summary>
        /// Start or stop the CoAP SDK emulation surface (port 40266 server +
        /// the <c>MOZA Pit House.exe</c> impersonation stub) at runtime. Called
        /// both from <see cref="Init"/> (to honour the persisted setting) and
        /// from the live UI toggle, so startup and a mid-session flip share one
        /// path. Serialized by <see cref="_sdkLifecycleGate"/>; safe to call
        /// repeatedly (idempotent in each direction).
        ///
        /// <para>Disabling stops the stub via <c>TryStop</c> (bounded — never
        /// wedges the caller under Wine) which restores
        /// <c>HKCU\Software\MOZA\PitHouse\path</c> to the user's original value
        /// before the child is killed, and clears the persistent static so the
        /// redirect can't be re-applied after an explicit "off".</para>
        /// </summary>
        internal void SetSdkEmulationEnabled(bool enabled)
        {
            lock (_sdkLifecycleGate)
            {
                if (enabled)
                {
                    try
                    {
                        // Stub manager is persistent across plugin reloads (the
                        // child holds no per-instance state and its Wine teardown
                        // is the path that intermittently hangs). Reuse a live
                        // one; otherwise reap a dead husk and spawn fresh.
                        if (_sdkStubManager != null && _sdkStubManager.IsRunning)
                        {
                            // Already running for this instance — nothing to do.
                        }
                        else if (s_persistentSdkStubManager != null
                                 && s_persistentSdkStubManager.IsRunning)
                        {
                            _sdkStubManager = s_persistentSdkStubManager;
                            MozaLog.Info(
                                "[Sdk] Reusing persistent CoAP stub " +
                                $"(status={_sdkStubManager.Status})");
                        }
                        else
                        {
                            // Persistent reference exists but its child is gone
                            // (crashed / killed externally). Tear down the husk
                            // before allocating a fresh manager so its job/process
                            // handles don't leak. Bounded so a Wine-side
                            // JobObject.Dispose wedge can't block the caller.
                            if (s_persistentSdkStubManager != null)
                            {
                                try { s_persistentSdkStubManager.TryStop(1500); } catch { }
                                s_persistentSdkStubManager = null;
                            }
                            _sdkStubManager = new Sdk.CoapStubManager();
                            _sdkStubManager.Start();
                            s_persistentSdkStubManager = _sdkStubManager;
                        }

                        // SDK server holds refs to _data + _hardwareApplier (both
                        // per-instance), so it lives and dies with this instance —
                        // it cannot be persistent like the stub manager. Create
                        // only when not already up (idempotent re-enable).
                        if (_sdkServer == null)
                        {
                            _sdkServer = new Sdk.MozaSdkCoapServer(_data, _hardwareApplier);
                            _sdkServer.Start();
                            MozaLog.Info("[Sdk] CoAP SDK server enabled");
                        }
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Error($"[Sdk] Failed to start CoAP SDK server: {ex.Message}");
                        try { _sdkServer?.Stop(); } catch { /* swallow */ }
                        // Don't Stop() the stub manager from this catch — it may
                        // be the persistent one and a Wine-side Stop() hang is
                        // exactly the failure we're avoiding. Leave it running;
                        // the next transition re-evaluates via IsRunning.
                        _sdkServer = null;
                        _sdkStubManager = null;
                    }
                }
                else
                {
                    // Stop the CoAP server, then the stub. Stopping the stub
                    // restores the registry redirect (before the kill, so it
                    // survives a Wine-side hang). Clear the persistent static so
                    // nothing re-applies the redirect after an explicit "off".
                    try { _sdkServer?.Stop(); _sdkServer?.Dispose(); }
                    catch (Exception ex) { MozaLog.Warn($"[Sdk] server stop: {ex.Message}"); }
                    _sdkServer = null;

                    var stub = _sdkStubManager ?? s_persistentSdkStubManager;
                    if (stub != null)
                    {
                        try { stub.TryStop(1500); }
                        catch (Exception ex) { MozaLog.Warn($"[Sdk] stub stop: {ex.Message}"); }
                        MozaLog.Info("[Sdk] CoAP SDK emulation disabled — stub stopped, registry restored");
                    }
                    _sdkStubManager = null;
                    s_persistentSdkStubManager = null;
                }
            }
        }

        /// <summary>
        /// Start or stop the PitHouse-compatible plain-UDP control server
        /// (port 40288) at runtime. Parallel to
        /// <see cref="SetSdkEmulationEnabled"/>; shares the same lifecycle gate
        /// and is driven from both <see cref="Init"/> and the live UI toggle.
        /// </summary>
        internal void SetUdpControlEnabled(bool enabled)
        {
            lock (_sdkLifecycleGate)
            {
                if (enabled)
                {
                    if (_controlUdpServer != null) return; // already running
                    try
                    {
                        _controlUdpServer = new Sdk.PitHouseUdp.MozaControlUdpServer(
                            _data, _hardwareApplier);
                        _controlUdpServer.Start();
                        MozaLog.Info("[Sdk] UDP control server enabled");
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Error($"[Sdk] Failed to start UDP control server: {ex.Message}");
                        try { _controlUdpServer?.Stop(); } catch { /* swallow */ }
                        _controlUdpServer = null;
                    }
                }
                else
                {
                    try { _controlUdpServer?.Stop(); _controlUdpServer?.Dispose(); }
                    catch (Exception ex) { MozaLog.Warn($"[PitHouseUdp] server stop: {ex.Message}"); }
                    _controlUdpServer = null;
                }
            }
        }

        internal MozaHidReader HidReader => _hidReader;

        /// <summary>Live truck-sim stalk controller (for the settings UI's
        /// "Re-sync wipers" action). Null until Init runs.</summary>
        internal StalksTruckSimController StalksController => _stalksController;

        /// <summary>Re-apply the current Stalks mode + truck-sim config to the live
        /// controller. Call from the settings UI after editing + SaveSettings().</summary>
        internal void ApplyStalksSettings()
        {
            try { _stalksController?.ApplySettings(_settings.StalksMode, _settings.StalksTruckSim); } catch { }
        }

        // ===== ProfileCoordinator shims (external API surface) =====
        // Settings persistence + profile system live in Settings/ProfileCoordinator.cs.
        internal void SaveSettings() => _profileCoordinator.SaveSettings();
        internal void PersistSettings() => _profileCoordinator.PersistSettings();

        /// <summary>
        /// Requests SimHub to exit and relaunch — used after an in-app plugin
        /// update is installed so the freshly-swapped DLL gets loaded. Drives
        /// the supported SimHub lifecycle hook
        /// <c>PluginManager.RequestApplicationExit(restart: true)</c> (see
        /// docs/simhub.md § Application Lifecycle). Best-effort: logs and
        /// returns false if the call is unavailable or throws, leaving SimHub
        /// running so the user can restart manually.
        /// </summary>
        public bool RestartSimHub()
        {
            // Flush any pending settings synchronously-ish before we ask SimHub
            // to tear down — ScheduleSave is debounced, but SimHub's own
            // shutdown also persists plugin settings, so this is belt-and-braces.
            try { PersistSettings(); } catch { /* best-effort */ }

            var pm = _pluginManager;
            if (pm == null)
            {
                MozaLog.Warn("[UpdateInstall] restart requested but PluginManager is null");
                return false;
            }

            try
            {
                MozaLog.Info("[UpdateInstall] requesting SimHub restart to load updated plugin");
                pm.RequestApplicationExit(true);
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[UpdateInstall] RequestApplicationExit failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // Kicks off the background GitHub Releases query on a thread-pool
        // thread, with a 24h throttle (LastUpdateCheckUtc) and a per-process
        // dedupe (s_updateCheckStarted). Returns immediately; the result is
        // persisted into _settings on completion. Failures swallow silently
        // — the user can still trigger a foreground check from the About tab.
        private void MaybeStartUpdateCheck()
        {
            try
            {
                if (_settings == null || !_settings.UpdateCheckEnabled) return;
                if (s_updateCheckStarted) return;
                // The dev channel tracks the rolling 'dev-latest' tag, so a
                // version cached in a prior session can't be trusted across a
                // restart: its 7-char SHA differs from any freshly-installed
                // build, which the dev comparator reads as "newer" and paints a
                // phantom "update available" banner. Re-check dev on every
                // launch (still once per process via s_updateCheckStarted) so
                // the cache re-syncs to the live dev-latest. Stable versions are
                // SHA-stable and directly comparable, so they keep the 24h
                // throttle.
                if (_settings.UpdateChannel != UpdateChannel.Dev
                    && DateTime.UtcNow - _settings.LastUpdateCheckUtc < TimeSpan.FromHours(24))
                {
                    MozaLog.Debug("[UpdateCheck] skipped — last check less than 24h ago");
                    return;
                }
                s_updateCheckStarted = true;

                var channel = _settings.UpdateChannel;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await UpdateCheckService
                            .CheckAsync(channel, CancellationToken.None)
                            .ConfigureAwait(false);
                        _settings.LastUpdateCheckUtc = DateTime.UtcNow;

                        if (result.Success && !string.IsNullOrEmpty(result.LatestVersion))
                        {
                            _settings.LastSeenLatestVersion = result.LatestVersion;
                            _settings.LastSeenReleaseUrl = result.ReleaseUrl;
                            _settings.LastSeenAssetUrl = result.AssetUrl;
                            _settings.LastSeenReleaseNotes = result.ReleaseNotes;
                            MozaLog.Debug(
                                $"[UpdateCheck] {channel}: latest={result.LatestVersion} asset={(string.IsNullOrEmpty(result.AssetUrl) ? "(none)" : "ok")}");
                        }
                        else if (!result.Success)
                        {
                            MozaLog.Debug(
                                $"[UpdateCheck] {channel} failed: {result.ErrorKind} {result.ErrorMessage}");
                        }

                        try { this.SaveCommonSettings("MozaPluginSettings", _settings); }
                        catch { /* persistence is best-effort */ }

                        // Repaint the settings pane if it's open so a fresh
                        // result lands immediately — without this the About-card
                        // banner + release notes would only update on the next
                        // tab reopen or manual "Check now" (the header banner
                        // already self-refreshes on its 500ms tick).
                        try
                        {
                            var ctrl = SettingsControl.Instance;
                            ctrl?.Dispatcher?.BeginInvoke(new Action(() =>
                            {
                                try { ctrl.RefreshUpdateNotifications(); } catch { }
                            }));
                        }
                        catch { /* UI refresh is best-effort */ }
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Debug($"[UpdateCheck] background task threw: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[UpdateCheck] scheduler threw: {ex.Message}");
            }
        }

        internal void ScheduleSave() => _profileCoordinator.ScheduleSave();

        internal void ClearSettings() => _profileCoordinator.ClearSettings();

        internal void SetConnectionEnabled(bool enabled)
        {
            _settings.ConnectionEnabled = enabled;
            SaveSettings();

            if (enabled)
            {
                _reconnectTimer.Start();
                // Manual re-enable re-arms the display-wedge recovery one-shot
                // so a user who toggled Connection off then on after a wedge
                // gets a fresh recovery attempt.
                DisplayWedgeRecoveryFired = false;
                // Re-arm base→hub migration: a user toggling Connection wants a
                // clean re-evaluation of where the wheel actually is.
                _connectionCoordinator?.ResetHubWheelMigrationState();
                MozaLog.Info("[AZOM] Connection enabled");
            }
            else
            {
                _reconnectTimer.Stop();
                ClearLedsOnHardware();
                _telemetrySender?.Stop();
                _connection?.Disconnect();
                // Deliberate disable — clear any classified failure so the UI
                // doesn't keep showing a "port in use" banner after the user
                // has intentionally turned the connection off.
                _connection?.ResetFailureState();
                _data.IsBaseConnected = false;
                _data.IsHubConnected = false;
                _data.ClearWheelIdentity();
                DetectionState.BaseDetected = false;
                _data.BaseSettingsRead = false;
                DetectionState.DashDetected = false;
                DetectionState.BaseAmbientLedSupported = false;
                DetectionState.BaseAmbientProbed = false;
                _data.BaseModelName = "";
                DetectionState.NewWheelDetected = false;
                DetectionState.OldWheelDetected = false;
                WheelModelInfo = null;
                DetectionState.HandbrakeDetected = false;
                DetectionState.PedalsDetected = false;
                DetectionState.HubDetected = false;
                DetectionState.Ab9Detected = false;
                DetectionState.PedalsOwner = null;
                DetectionState.HandbrakeOwner = null;
                DetectionState.BaseOwner = null;
                _ab9Manager?.Disconnect();
                _hubManager?.Disconnect();
                _baseManager?.Disconnect();
                _connectionCoordinator?.ResetHubWheelMigrationState();
                if (_telemetrySender != null)
                {
                    _telemetrySender.DetectedDeviceMask = 0;
                }
                _deviceManager.ResetWheelDetection();
                Interlocked.Exchange(ref _telemetryStartRequested, 0);
                DetectionState.WheelPollMisses = 0;
                DetectionState.LastKnownWheelModel = "";
                MozaLog.Info("[AZOM] Connection disabled");
            }
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            // The WPF UI thread predates plugin Init, so the CurrentUICulture
            // we assigned in Init lives on a different thread. Re-apply it here
            // (we are on the UI thread) so that {x:Static res:Strings.X} bindings
            // in SettingsControl.xaml resolve against the resolved language
            // rather than the default thread culture.
            var c = LanguageResolver.Resolve(_settings?.PreferredLanguage);
            if (!Thread.CurrentThread.CurrentUICulture.Equals(c))
            {
                MozaLog.Info($"[AZOM] GetWPFSettingsControl: switching UI thread culture from " +
                             $"'{Thread.CurrentThread.CurrentUICulture.Name}' to '{c.Name}' " +
                             $"(PreferredLanguage='{_settings?.PreferredLanguage ?? "<auto>"}')");
                Thread.CurrentThread.CurrentUICulture = c;
            }
            return new SettingsControl(this);
        }

        /// <summary>
        /// Send all-off to wheel and dash LEDs via device manager.
        /// </summary>
                // ClearLedsOnHardware moved to HardwareApplier; shim further down.

        // ===== Telemetry =====

        internal TelemetrySender? TelemetrySender => _telemetrySender;

        /// <summary>The dedicated CM2-dash tier-def sender (drives any attached CM2, bus
        /// or USB, independent of the wheel), or null when no CM2 is present.</summary>
        internal TelemetrySender? Cm2Sender => _cm2Sender;

        // "Send Test Pattern" toggle, shared across every display pipeline. The
        // tier-def senders consume it via their own TestMode; the standalone FSR1
        // (0x42) / CM1 (0x35) drivers read this flag in their tick and synthesise a
        // sweep (see Telemetry/DashboardTestPattern). Volatile: written from the UI
        // thread, read from the driver/sender timer threads.
        private volatile bool _dashboardTestPattern;

        /// <summary>True while the dashboard test pattern is active (any display type).</summary>
        internal bool DashboardTestPatternActive => _dashboardTestPattern;

        /// <summary>Toggle the test pattern across every display pipeline: the
        /// tier-def senders (wheel + CM2) via their <see cref="TelemetrySender.TestMode"/>,
        /// and the standalone FSR1/CM1 drivers via <see cref="DashboardTestPatternActive"/>.</summary>
        internal void SetDashboardTestPattern(bool on)
        {
            _dashboardTestPattern = on;
            if (_telemetrySender != null) _telemetrySender.TestMode = on;
            if (_cm2Sender != null) _cm2Sender.TestMode = on;
            if (on) { _fsr1ProbeStep = -1; _fsr1FieldProbe = null; } // exclusive with both probes
        }

        // FSR V1 single-byte probe diagnostic. The driver streams an all-zero record with
        // exactly ONE data byte ramping 0..255, isolating one payload offset at a time so
        // the user can see which on-screen box animates (boundary = where the active box
        // changes; width = the run of consecutive offsets driving the same box; scale =
        // displayed value ÷ byte value). _fsr1ProbeStep is the global step index across the
        // active page's record(s); -1 = probe off. Volatile: UI writes, driver reads.
        private volatile int _fsr1ProbeStep = -1;

        // Page index captured when the byte probe is armed. The wheel streams its
        // "Table 7, Param 6 Written: N" page-report log continuously, which the plugin
        // follows live (NoteFsr1WheelIndex) — so the active index can move WHILE the user
        // steps the probe, scrambling the step→(record,byte) mapping every refresh. Freezing
        // the index for the probe's lifetime keeps stepping stable and contiguous; -1 = no
        // freeze (probe off). See GetActiveFsr1Index.
        private volatile int _fsr1ProbeFrozenIndex = -1;

        /// <summary>Page index the byte probe is locked to while armed, or -1 when the probe
        /// is off — <see cref="Telemetry.Fsr1Cm1MappingCoordinator.GetActiveFsr1Index"/> returns
        /// this (instead of the live, log-followed index) so a stepping sweep can't be
        /// derailed by a mid-probe page-report.</summary>
        internal int Fsr1ProbeFrozenIndex => _fsr1ProbeStep >= 0 ? _fsr1ProbeFrozenIndex : -1;

        /// <summary>True while EITHER FSR V1 probe diagnostic is active — the toolbar
        /// single-byte stepper or the row-driven field-span probe. The two are mutually
        /// exclusive; the driver gates its probe override on this.</summary>
        internal bool Fsr1ProbeActive => _fsr1ProbeStep >= 0 || _fsr1FieldProbe != null;

        /// <summary>Current 0-based probe step across the active page's data bytes.</summary>
        internal int Fsr1ProbeStepIndex => _fsr1ProbeStep;

        /// <summary>The record(s) the probe walks — the active page's type(s), or the full
        /// live set as a fallback when the active index is unmapped (mirrors the driver's
        /// own active/fallback selection so the probe targets what is actually streaming).</summary>
        internal Telemetry.Fsr1Dashboard[] Fsr1ProbeRecords()
        {
            var active = Telemetry.Fsr1DashboardCatalog.ByIndex(GetActiveFsr1Index());
            return active.Length > 0 ? active : Telemetry.Fsr1DashboardCatalog.LiveDashboards;
        }

        /// <summary>Total probe steps = sum of data-byte counts (PayloadLen-5) across the
        /// active page's record(s).</summary>
        internal int Fsr1ProbeStepCount()
        {
            int n = 0;
            foreach (var d in Fsr1ProbeRecords())
                n += System.Math.Max(0, d.PayloadLen - 5);
            return n;
        }

        /// <summary>Map the current step to a (record type, payload offset) target. Returns
        /// <c>(0, -1)</c> when the probe is off or the step is out of range.</summary>
        internal (byte type, int offset) Fsr1ProbeTarget()
        {
            int step = _fsr1ProbeStep;
            if (step < 0) return (0, -1);
            foreach (var d in Fsr1ProbeRecords())
            {
                int count = System.Math.Max(0, d.PayloadLen - 5);
                if (step < count) return (d.RecordType, 5 + step);
                step -= count;
            }
            return (0, -1);
        }

        /// <summary>Human-readable description of the byte the probe currently targets,
        /// annotated with the catalog field that — per the CURRENT decode — owns it and
        /// whether the byte is that field's first byte (an assumed field boundary). This
        /// surfaces the hypothesized boundaries while stepping so the user can spot where
        /// the on-screen box disagrees with the catalog's field layout.</summary>
        internal string Fsr1ProbeTargetLabel()
        {
            var (type, off) = Fsr1ProbeTarget();
            if (off < 0) return "—";
            string where = $"record 0x{type:X2}, byte {off}  ({_fsr1ProbeStep + 1}/{Fsr1ProbeStepCount()})";
            var dash = Fsr1DashboardCatalog.ByType(type);
            var f = dash?.Fields.FirstOrDefault(x => System.Array.IndexOf(x.Offsets, off) >= 0);
            if (f == null) return where + "  — unmapped byte";
            bool boundary = f.Offsets.Length > 0 && f.Offsets[0] == off;
            int width = f.Offsets.Length;
            return $"{where}  — {f.FieldId} \"{f.Label}\" " +
                   (boundary ? $"[◀ field start, {width}B]" : "[cont]");
        }

        /// <summary>Toggle the FSR V1 probe (starts at the first data byte, offset 5).
        /// FSR1-only; mutually exclusive with the sweep test pattern.</summary>
        internal void SetFsr1Probe(bool on)
        {
            if (on)
            {
                // Capture the live page BEFORE arming (step still -1, so GetActiveFsr1Index
                // returns the real log-followed index, not a stale freeze), then lock to it.
                _fsr1ProbeFrozenIndex = GetActiveFsr1Index();
                _fsr1ProbeStep = 0;
                _fsr1FieldProbe = null;            // exclusive with the field probe
                SetDashboardTestPattern(false);
            }
            else
            {
                _fsr1ProbeStep = -1;
                _fsr1ProbeFrozenIndex = -1;
            }
        }

        /// <summary>Step the probe offset by <paramref name="delta"/>, wrapping within the
        /// active page's total data-byte count. No-op when the probe is off.</summary>
        internal void StepFsr1Probe(int delta)
        {
            if (_fsr1ProbeStep < 0) return;
            int total = Fsr1ProbeStepCount();
            if (total <= 0) { _fsr1ProbeStep = 0; return; }
            int s = (_fsr1ProbeStep + delta) % total;
            if (s < 0) s += total;
            _fsr1ProbeStep = s;
        }

        // Row-driven field-span probe. Armed while a field's inline editor is open so the
        // user watches the on-screen box for that field as they step its boundary edges.
        // Distinct from the byte-stepper (_fsr1ProbeStep) and mutually exclusive with it;
        // holds the record + field id and resolves to the field's CURRENT span on demand.
        private sealed class Fsr1FieldProbe { public string RecordKey = ""; public string FieldId = ""; }
        private volatile Fsr1FieldProbe? _fsr1FieldProbe;

        /// <summary>Arm the row-driven field-span probe on one FSR1 field (disarms the
        /// byte-stepper and the test pattern). Re-call as the field's span changes.</summary>
        internal void SetFsr1FieldProbe(string recordKey, string fieldId)
        {
            if (string.IsNullOrEmpty(recordKey) || string.IsNullOrEmpty(fieldId)) return;
            _fsr1ProbeStep = -1;
            SetDashboardTestPattern(false);
            _fsr1FieldProbe = new Fsr1FieldProbe { RecordKey = recordKey, FieldId = fieldId };
        }

        /// <summary>Disarm the field-span probe (row editor closed).</summary>
        internal void ClearFsr1FieldProbe() => _fsr1FieldProbe = null;

        /// <summary>The field-span probe's CURRENT resolved target — record type, the contiguous
        /// byte span (start..end inclusive), and (for a bit-packed field) its exact bit run — after
        /// applying its user override, or null when not armed / unresolvable. <c>packed</c> selects
        /// the overlay probe (ramp only the field's bits over live data) vs the byte-span probe.</summary>
        internal (byte type, int startOff, int endOff, bool packed, int bitOffset, int bitWidth, bool msbFirst)? Fsr1FieldProbeTarget()
        {
            var p = _fsr1FieldProbe;
            if (p == null) return null;
            var dash = Telemetry.Fsr1DashboardCatalog.ByKey(p.RecordKey);
            if (dash == null) return null;
            // Resolve through the SAME gapless partition the driver emits — so the lit span
            // matches the wire exactly, and synthetic split fields (absent from dash.Fields)
            // are found too. Using the raw override here diverged from the tiled output.
            foreach (var slot in Telemetry.Fsr1DashboardCatalog.ResolvePartition(this, dash))
                if (slot.Field.FieldId == p.FieldId)
                    return (dash.RecordType, slot.ByteStart, slot.ByteEnd,
                            !slot.IsByteAligned, slot.BitOffset, slot.BitWidth, slot.MsbFirst);
            return null;
        }

        // ── FSR1 live numeric visualization channel ─────────────────────────
        // When the channel-mapping panel is showing an FSR1 wheel, it asks the driver to
        // publish a per-tick snapshot of the data it streams (each field's resolved span,
        // raw bytes, post-scale value) so the UI can draw a live byte strip. Volatile
        // single-writer (driver) / single-reader (UI 2 Hz timer), matching driver threading.
        private volatile bool _fsr1VizActive;
        private volatile Telemetry.Fsr1VizSnapshot? _fsr1Viz;

        /// <summary>True while the channel-mapping panel wants the FSR1 viz snapshot.</summary>
        internal bool Fsr1VizActive => _fsr1VizActive;

        /// <summary>Arm/disarm FSR1 viz capture (panel load/teardown). Clears the last
        /// snapshot on disarm so a stale strip never lingers.</summary>
        internal void SetFsr1VizActive(bool on)
        {
            _fsr1VizActive = on;
            if (!on) _fsr1Viz = null;
        }

        /// <summary>Driver publishes the latest streamed-data snapshot (or null).</summary>
        internal void SetFsr1VizSnapshot(Telemetry.Fsr1VizSnapshot? snap) => _fsr1Viz = snap;

        /// <summary>UI reads the latest FSR1 viz snapshot, or null when none yet.</summary>
        internal Telemetry.Fsr1VizSnapshot? GetFsr1VizSnapshot() => _fsr1Viz;

        /// <summary>True when some display pipeline is live and can render a test
        /// pattern: a tier-def sender is Active, or a standalone FSR1/CM1 driver runs.</summary>
        internal bool IsAnyDashboardDisplayRunning =>
            (_telemetrySender?.IsActive ?? false)
            || (_cm2Sender?.IsActive ?? false)
            || (_fsr1Driver?.IsRunning ?? false)
            || (_cm1Driver?.IsRunning ?? false);

        /// <summary>True when the FSR V1 standalone 0x42 display driver is running
        /// (connected FSR1 wheel). The tier-def sender never goes Active for an FSR1,
        /// so the dashboard UI gates the selector/status on this instead.</summary>
        internal bool IsFsr1DriverRunning => _fsr1Driver?.IsRunning ?? false;

        /// <summary>True when the wheel's OWN screen is driven by the tier-def
        /// <see cref="_telemetrySender"/> (a display wheel like W17/W18) rather than
        /// the standalone FSR1 0x42 driver — so the test button may safely start it.</summary>
        internal bool WheelUsesTierDefDisplaySender =>
            !IsFsr1DisplayWheel && (WheelModelInfo?.HasDisplay == true);

        /// <summary>The sender that drives the CM2 dashboard. DECOUPLED: the CM2 is
        /// ALWAYS driven by the dedicated <see cref="_cm2Sender"/> (created whenever a
        /// CM2 is present, regardless of the wheel), so this is simply that sender —
        /// null when no CM2 is attached. The CM2 dash UI reads its WheelState/
        /// ConfigJsonList. (Previously this fell back to the MAIN sender for a
        /// screenless wheel, because the main sender drove the CM2 then.)</summary>
        internal TelemetrySender? ActiveCm2Sender => _cm2Sender;

        /// <summary>The CM2's selected dashboard name (independent of the wheel's).</summary>
        internal string ActiveCm2DashboardName
        {
            get => _settings?.Cm2SelectedDashboard ?? "";
            set { if (_settings != null) _settings.Cm2SelectedDashboard = value ?? ""; }
        }

        /// <summary>Switch the CM2 dash to a dashboard slot (FF kind=4 on the CM2
        /// sender), independent of the wheel.</summary>
        internal void OnCm2DashboardSwitched(uint slot) =>
            _dashboardBindingCoordinator.OnDashboardSwitched(slot, ActiveCm2Sender);

        // Surface configJson wheel state for the Diagnostics tab.
        internal WheelDashboardState? WheelStateForDiagnostics =>
            _telemetrySender?.WheelState;

        // Tile-server state (b2h session 0x03 parse).
        internal TileServerState? TileServerStateForDiagnostics =>
            _telemetrySender?.TileServerState;

        // Wheel channel catalog.
        internal System.Collections.Generic.IReadOnlyList<string>? WheelChannelCatalogForDiagnostics =>
            _telemetrySender?.WheelChannelCatalog;

        // Catalog-parser internals for the diag tab. Surfaces buffer/parse/CRC
        // counters so we can tell at a glance why a missing catalog is missing.
        internal (int BufferBytes, int LastParsedBufferBytes, int CrcRejects, int LastActivityMsAgo)
            CatalogParserDiagnostics
        {
            get
            {
                var s = _telemetrySender;
                if (s == null) return (0, 0, 0, -1);
                int lastAct = s.CatalogLastActivityTickMs;
                int ago = lastAct == 0 ? -1 : Environment.TickCount - lastAct;
                return (s.CatalogBufferLength, s.CatalogLastParsedBufferLen,
                        s.CatalogCrcRejects, ago);
            }
        }

        // Per-session traffic counters (in/out chunk counts).
        internal System.Collections.Generic.IReadOnlyDictionary<byte, (int In, int Out)>? SessionCountsForDiagnostics =>
            _telemetrySender?.SessionCounts;

        // Active telemetry running flag.
        internal bool TelemetryEnabledForDiagnostics =>
            _telemetrySender?.Enabled ?? false;

        // Frame-counter readout.
        internal int FramesSentForDiagnostics =>
            _telemetrySender?.FramesSent ?? 0;

        // Bandwidth + wire-error counters surfaced in the Diagnostics tab.
        internal global::MozaPlugin.Protocol.WriteBudget.Snapshot SerialBudgetForDiagnostics
            => _connection?.CurrentBudget ?? default;
        internal global::MozaPlugin.Protocol.MozaSerialConnection.WireErrorCounters SerialWireErrorsForDiagnostics
            => _connection?.WireErrors ?? default;

        // Subscription diagnostics for the "Subscription" section of the Diagnostics tab.
        internal TelemetrySender.SubscriptionDiagnostics? SubscriptionForDiagnostics =>
            _telemetrySender?.LastSubscription;

        // Inbound s02 chunks captured in 5s window after last subscription send.
        internal System.Collections.Generic.IReadOnlyList<byte[]>? SubscriptionResponseForDiagnostics =>
            _telemetrySender?.LastSubscriptionResponse;

        /// <summary>Apply telemetry settings from the active wheel overlay to the TelemetrySender.</summary>
        
        public IReadOnlyList<string> GetAllSimHubPropertyNames() => _propertyResolver.GetAllSimHubPropertyNames();
        public object? GetPropertyValueForDisplay(string? path) => _propertyResolver.GetValueForDisplay(path);
        internal string CurrentWheelKey() => _propertyResolver.CurrentWheelKey();

        /// <summary>
        /// Build a formula/property → double resolver for a 50 Hz haptics worker
        /// (LFE, mBooster). Same dialect and property resolution as the telemetry
        /// channel-mapper, but formulas evaluate on the returned closure's OWN
        /// engine instance — SimHub's NCalcEngineBase is not safe for concurrent
        /// evaluation, so each evaluator serializes internally, and a private
        /// instance keeps haptics ticks from queueing behind the 30 Hz telemetry
        /// evaluations (see SimHubPropertyResolver.ResolveAsDouble overload).
        /// Late-binds <see cref="_propertyResolver"/>: workers are constructed
        /// before the resolver exists.
        /// </summary>
        private Func<string?, double> CreateHapticsFormulaResolver()
        {
            var formula = new Telemetry.NCalcExpressionEvaluator();
            return f =>
            {
                if (string.IsNullOrWhiteSpace(f)) return 0.0;
                var resolver = _propertyResolver;
                if (resolver == null) return 0.0;
                return resolver.ResolveAsDouble(f!, formula);
            };
        }

        /// <summary>SimHub's shared formula engine for the channel-mapper's formula
        /// picker; null if engine construction failed (formulas then read as default).</summary>
        internal SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon.NCalcEngineBase? ChannelFormulaEngine
            => _propertyResolver?.FormulaEngine;

        // UI-thread formula preview (LFE "current value" readouts). Own evaluator
        // instance so it never contends with the haptics worker's engine; UI-thread
        // only, so no locking needed beyond ResolveAsDouble's internal serialization.
        private Telemetry.NCalcExpressionEvaluator? _uiFormulaEvaluator;

        /// <summary>Evaluate a haptics formula/property to a double for a UI preview. 0 if unavailable.</summary>
        internal double EvalHapticsFormula(string? formula)
        {
            if (string.IsNullOrWhiteSpace(formula)) return 0.0;
            var resolver = _propertyResolver;
            if (resolver == null) return 0.0;
            _uiFormulaEvaluator ??= new Telemetry.NCalcExpressionEvaluator();
            return resolver.ResolveAsDouble(formula!, _uiFormulaEvaluator);
        }

        /// <summary>
        /// Candidate dashboard keys (highest priority first):
        /// <c>wheel:&lt;id&gt;</c>, <c>file:&lt;filename&gt;:&lt;sha1-8&gt;</c>, <c>builtin:&lt;name&gt;</c>.
        /// Caller iterates; primary writer uses index 0.
        /// </summary>
        internal IReadOnlyList<string> GetActiveDashboardKeyCandidates()
        {
            string profileName = ActiveTelemetryProfileName;
            string mzdashPath = ActiveTelemetryMzdashPath;

            // Cold launch before any selection → fall back to running profile name.
            if (string.IsNullOrEmpty(profileName) && string.IsNullOrEmpty(mzdashPath))
            {
                profileName = _telemetrySender?.Profile?.Name ?? "";
            }

            if (string.IsNullOrEmpty(profileName) && string.IsNullOrEmpty(mzdashPath))
                return Array.Empty<string>();

            var result = new List<string>(3);

            // 1) wheel:<id> — match selected name against configJson catalog
            if (!string.IsNullOrEmpty(profileName))
            {
                var state = WheelStateForDiagnostics;
                if (state != null && state.EnabledDashboards != null)
                {
                    foreach (var entry in state.EnabledDashboards)
                    {
                        if (entry == null || string.IsNullOrEmpty(entry.Id)) continue;
                        bool nameMatch =
                            string.Equals(entry.Title, profileName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(entry.DirName, profileName, StringComparison.OrdinalIgnoreCase);
                        if (nameMatch)
                        {
                            result.Add("wheel:" + entry.Id);
                            break;
                        }
                    }
                }
            }

            // 2) file:<filename>:<sha1>
            string? keyPath = mzdashPath;
            if (string.IsNullOrEmpty(keyPath) && DashCache != null && !string.IsNullOrEmpty(profileName))
                keyPath = DashCache.TryGetFolderFilePath(profileName);
            if (!string.IsNullOrEmpty(keyPath))
            {
                // keyPath is non-empty so profile.Name branch is unreachable.
                string fileKey = DashboardProfileStore.GetDashboardKey(keyPath, _telemetrySender?.Profile!);
                if (!string.IsNullOrEmpty(fileKey) && !result.Contains(fileKey))
                    result.Add(fileKey);
            }

            // 3) builtin:<name>
            if (!string.IsNullOrEmpty(profileName))
            {
                string builtinKey = "builtin:" + profileName;
                if (!result.Contains(builtinKey))
                    result.Add(builtinKey);
            }

            return result;
        }

        /// <summary>
        /// Live-rewire a channel's <see cref="ChannelDefinition.SimHubProperty"/>
        /// in place; new value applies on the next telemetry frame. Safe while running.
        /// </summary>
        internal void UpdateActiveChannelMapping(string channelUrl, string propertyPath, TelemetrySender? sender = null)
        {
            var profile = (sender ?? _telemetrySender)?.Profile;
            if (profile == null || string.IsNullOrEmpty(channelUrl)) return;
            string trimmed = (propertyPath ?? "").Trim();
            foreach (var tier in profile.Tiers)
            {
                foreach (var ch in tier.Channels)
                {
                    if (string.Equals(ch.Url, channelUrl, StringComparison.OrdinalIgnoreCase))
                        ch.SimHubProperty = trimmed;
                }
            }
        }

        /// <summary>Set or clear a per-channel SimHub property override. Defaults to the
        /// current wheel + active dashboard; the CM2 page passes its own page GUID +
        /// fixed key + sender so its config is independent of the wheel's.</summary>
        internal void SetChannelMapping(string channelUrl, string propertyPath,
            Guid? pageGuid = null, string? fixedDashKey = null, TelemetrySender? sender = null)
        {
            if (string.IsNullOrEmpty(channelUrl)) return;
            string dashKey;
            if (!string.IsNullOrEmpty(fixedDashKey))
            {
                dashKey = fixedDashKey!;
            }
            else
            {
                var candidates = GetActiveDashboardKeyCandidates();
                if (candidates.Count == 0) return;
                dashKey = candidates[0]; // write to the highest-priority key
            }

            // Profile × page × dashboard × channel → SimHub property path.
            // COW: the serial-read/tick threads walk these dicts mid-apply
            // (ApplyUserChannelMappings) and the save path serializes them —
            // rebuild each level and reference-swap, never mutate in place.
            var profile = _settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return;
            var g = pageGuid ?? GetCurrentWheelPageGuid();
            if (!g.HasValue) return; // no profile/page resolvable yet

            var outer = profile.TelemetryChannelMappings;
            var newMiddle = (outer != null && outer.TryGetValue(g.Value, out var oldMiddle) && oldMiddle != null)
                ? new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>(oldMiddle, StringComparer.OrdinalIgnoreCase)
                : new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var newInner = (newMiddle.TryGetValue(dashKey, out var oldInner) && oldInner != null)
                ? new System.Collections.Generic.Dictionary<string, string>(oldInner, StringComparer.OrdinalIgnoreCase)
                : new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string trimmed = (propertyPath ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                newInner.Remove(channelUrl);
                // Tidy: drop empty inner dict so the JSON doesn't accumulate
                // empty objects after every reset-to-default.
                if (newInner.Count == 0) newMiddle.Remove(dashKey);
                else newMiddle[dashKey] = newInner;
            }
            else
            {
                newInner[channelUrl] = trimmed;
                newMiddle[dashKey] = newInner;
            }

            var newOuter = outer != null
                ? new System.Collections.Generic.Dictionary<Guid, System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>>(outer)
                : new System.Collections.Generic.Dictionary<Guid, System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>>();
            newOuter[g.Value] = newMiddle;
            profile.TelemetryChannelMappings = newOuter;

            // Live-rewire the matching channel on the target sender's profile so the
            // next frame uses the new property. No tier-def restart.
            UpdateActiveChannelMapping(channelUrl, trimmed, sender);

            SaveSettings();
        }

        /// <summary>Clear all per-channel overrides for a page + its dashboard key(s).
        /// Defaults to the current wheel page across all candidate keys.
        /// COW like <see cref="SetChannelMapping"/> — readers walk these dicts
        /// on the serial-read/tick threads.</summary>
        internal void ClearCurrentDashboardMappings(Guid? pageGuid = null, string? fixedDashKey = null)
        {
            var profile = _settings?.ProfileStore?.CurrentProfile;
            var outer = profile?.TelemetryChannelMappings;
            if (profile == null || outer == null) return;
            var g = pageGuid ?? GetCurrentWheelPageGuid();
            if (!g.HasValue || !outer.TryGetValue(g.Value, out var middle) || middle == null) return;

            var newMiddle = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>(middle, StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            if (!string.IsNullOrEmpty(fixedDashKey))
            {
                if (newMiddle.Remove(fixedDashKey!)) changed = true;
            }
            else
            {
                foreach (var key in GetActiveDashboardKeyCandidates())
                    if (newMiddle.Remove(key)) changed = true;
            }
            if (!changed) return;

            var newOuter = new System.Collections.Generic.Dictionary<Guid, System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>>(outer);
            newOuter[g.Value] = newMiddle;
            profile.TelemetryChannelMappings = newOuter;
            SaveSettings();
        }

        // Dashboard binding state moved to DashboardBindingCoordinator.
        internal bool IsPendingDashboardApply => _dashboardBindingCoordinator?.IsPendingDashboardApply ?? false;
        internal string? PendingDashboardApplyDescription => _dashboardBindingCoordinator?.PendingDashboardApplyDescription;

        // ===== ConnectionCoordinator forwarders =====
        // Multi-connection management + hub/base pipes live in
        // Devices/ConnectionCoordinator.cs. These 1-line private handlers keep
        // Init's event-subscription order untouched (the hub/base managers
        // subscribe before the coordinator exists) and null-guard that window.
        private void OnHubMessageReceived(byte[] data) => _connectionCoordinator?.OnHubMessageReceived(data);
        private void OnHubDisconnected() => _connectionCoordinator?.OnHubDisconnected();
        private void OnBaseMessageReceived(byte[] data) => _connectionCoordinator?.OnBaseMessageReceived(data);
        private void OnBaseDisconnected() => _connectionCoordinator?.OnBaseDisconnected();

        /// <summary>Inbound from the dashboard connection — same command-parse path as
        /// the wheelbase. (The telemetry inbound dispatcher follows the sender's
        /// Rebind, so dashboard session frames reach it once the sender is bound here.)</summary>
        private void OnDashboardMessageReceived(byte[] data) => OnMessageReceived(data);

        /// <summary>Dashboard USB unplugged — pause the sender so the next tick rebinds
        /// it back to the wheelbase (and the base-bridged 0x14 path takes over if present).</summary>
        private void OnDashboardDisconnected()
        {
            if (IsShuttingDown) return;
            try { _telemetrySender?.Pause(); } catch { }
            DetectionState.DashDetected = false;
            _data.IsDashboardConnected = false;
        }

        private const int WheelMissThreshold = 3;

        // wheel-model-name recheck cadence once identity is resolved; per-tick
        // liveness then comes from the 0x00 presence ACK. Kept strictly below
        // WheelMissThreshold so that even if a wheel model never ACKs 0x00 and
        // emits no 0x0E logs, the model-name response still resets the miss
        // counter before a false re-detect. Unresolved wheels read every tick
        // (fast identity). See the hot-swap block in PollStatus.
        private const int WheelModelRecheckInterval = WheelMissThreshold - 1;
        private int _wheelModelRecheckTick;

        // Fires from MozaSerialConnection.HandleIoFailure on the read or
        // write thread once the port has been force-closed. Pause telemetry
        // and reset wheel detection right now rather than waiting for the
        // next reconnect-timer tick — otherwise the sender keeps firing and
        // accumulating ack waiters / catalog state for ~5 s.
        // System power-state notification (sleep/resume). Fires on the dedicated
        // SystemEvents notification thread — keep it non-blocking. Only Resume is
        // handled: after sleep the wheel power-cycles and drops its display/
        // telemetry sessions while the host tty can stay half-open, so we force a
        // clean reconnect on the display-bearing pipes (primary wheel + standalone
        // CM2). ForceReconnect raises Disconnected → OnSerialDisconnected /
        // OnDashboardDisconnected, which reset detection + Stop the sender; the
        // reconnect timer then reopens a fresh port and the session pipeline
        // rebuilds. Config-only lanes (hub/base-aux/AB9/peripherals) self-heal via
        // the connection's ~30 s half-open detector — a stale config lane is benign.
        private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (IsShuttingDown) return;
            if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
            MozaLog.Info("[AZOM] System resume — forcing reconnect to rebuild display sessions");
            // ForceReconnect can take a beat (raises the full detection/telemetry
            // reset chain); get off the SystemEvents thread so we don't stall other
            // power-event subscribers.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (IsShuttingDown) return;
                try { _connection?.ForceReconnect("System resume"); }
                catch (Exception ex) { MozaLog.Warn($"[AZOM] Resume reconnect (primary): {ex.Message}"); }
                try { _dashboardManager?.Connection?.ForceReconnect("System resume"); }
                catch (Exception ex) { MozaLog.Warn($"[AZOM] Resume reconnect (dashboard): {ex.Message}"); }
            });
        }

        // Detach the static PowerModeChanged subscription exactly once. Safe to
        // call from both End() and CleanupPartialInit (the Interlocked.Exchange
        // gate makes the second call a no-op).
        private void UnhookPowerMode()
        {
            if (Interlocked.Exchange(ref _powerModeHooked, 0) == 1)
            {
                try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; }
                catch (Exception ex) { MozaLog.Debug($"[AZOM] PowerModeChanged unhook: {ex.Message}"); }
            }
        }

        private void OnSerialDisconnected()
        {
            if (IsShuttingDown) return;
            // Pause first so the sender's next tick sees the timer stopped
            // before ResetWheelDetection issues its full Stop().
            try { _telemetrySender?.Pause(); } catch { }
            // Drop pending response watches — the wheel/port we sent to is
            // gone; their pending responses will never arrive on this
            // connection. They'd otherwise keep retrying after reconnect
            // against a fresh wheel that may not even speak the same protocol.
            try { PendingResponses.Clear(); } catch { }
            // The primary pipe dropped. If we were in the migrated (hub-primary)
            // state, tear down the dedicated base-aux pipe and clear the migration
            // latch so the next reconnect re-evaluates from scratch (base reverts
            // to primary, the wheel is re-probed on it). Harmless no-op when the
            // base is the primary (base-aux isn't connected).
            try { _baseManager?.Disconnect(); } catch { }
            _connectionCoordinator?.ResetHubWheelMigrationState();
            if (DetectionState.NewWheelDetected || DetectionState.OldWheelDetected || DetectionState.DashDetected)
                ResetWheelDetection("Serial disconnect — resetting wheel detection");
        }

        /// <summary>
        /// Clear ALL device-detection flags. Called by Init() and End() so a plugin
        /// reload doesn't carry over stale detected state. See <see cref="ResetWheelDetection"/>
        /// for the hot-swap-scoped reset that preserves base/hub/handbrake/pedals.
        /// </summary>
        private void ResetDetectionFlags()
        {
            DetectionState.ResetAll();
            if (_data != null) _data.BaseModelName = "";
        }

        internal void ResetWheelDetection(string reason)
        {
            MozaLog.Debug($"[AZOM] {reason}");
            _telemetrySender?.Stop();
            // Preserve dash detection across a WHEEL-rim reset: the dash (CM2/CM1)
            // is reached through the CONNECTION, not the wheel rim, so a hot-swap or
            // presence-miss of the rim must NOT blank it. Two cases:
            //   • standalone-USB dash — lives on its own pipe (IsStandaloneDashboardUsbConnection);
            //   • base-bridged (bus) dash — lives on the still-live wheelbase connection.
            // ResetWheel() clears DashDetected unconditionally; re-assert it for both.
            // CRITICAL: a bus CM2 behind the base is independent of which rim is
            // attached. Letting a rim miss clear DashDetected flips the dual-display
            // `want` false, and the periodic EnsureCm2Pipeline reconcile then tears
            // down a perfectly healthy CM2 dashboard (LEDs survive on a separate path,
            // so the symptom is "CM2 dash dead, LEDs fine"). Only a real connection
            // loss (Connection.IsConnected false) should drop a bus dash.
            bool preserveDash = IsStandaloneDashboardUsbConnection
                || (DetectionState.DashDetected && _connection?.IsConnected == true);
            DetectionState.ResetWheel();
            if (preserveDash)
                DetectionState.DashDetected = true;
            WheelModelInfo = null;
            _data.ClearWheelIdentity();
            // ClearWheelIdentity above blanks _data.Display* fields, but
            // TelemetrySender keeps its own _displayDetected / _displayModelName
            // latch (see SetDisplayDetected) — clear that too so the next
            // wheel's StartTelemetryIfReady display gate doesn't read stale
            // detection and bypass the ~20 s display-boot wait.
            _telemetrySender?.ResetDisplayDetection();
            // Clear the wedge-watchdog timestamp so elapsed-since-detect is
            // measured against the NEXT wheel's rising edge, not a stale one
            // from the wheel we just disconnected. DisplayWedgeRecoveryFired
            // intentionally NOT reset here — only cleared on a successful
            // display detection (or manual Connection-enable toggle), which
            // is what prevents the auto-recovery from looping when the
            // display is permanently wedged.
            Interlocked.Exchange(ref _wheelDetectedUtcTicks, 0);
            _deviceManager.ResetWheelDetection();
            if (_telemetrySender != null)
                _telemetrySender.DetectedDeviceMask = 0;
            Interlocked.Exchange(ref _telemetryStartRequested, 0);
            // Hot-swap may bind a different default dashboard; force kind=4 re-emit.
            _dashboardBindingCoordinator?.ClearLastAppliedDashboardKey();
            _telemetrySender?.ResetBindingTracking();
            // Drop sunsets — the newly-attached wheel may support commands the
            // previous one didn't, and any cross-device entries should re-try.
            try { PendingResponses.Clear(); } catch { }
        }

        // Background-responsiveness reconcile. Idempotent + thread-safe, so it is driven
        // from DataUpdate (responsive raise), PollStatus (release backstop for when
        // DataUpdate goes quiet on game exit), and the UI FFB-Lag-Fix toggle handler.
        //
        //  - EcoQoS execution-speed opt-out: ON only while a game is active (and a device
        //    is connected), so mid-game control writes (e.g. RSF steering lock over UDP)
        //    reach AND engage the base while SimHub is the background app. PitHouse does
        //    the same — without it the base only adopts the change on alt-tab, when the
        //    foreground lifts the Windows background throttle. Scoped to gameplay so we
        //    never hold the process un-throttled while sitting idle on the desktop.
        //  - 1 ms timer (kept honoured in the background): during active gameplay — the
        //    legacy "FFB Lag Fix" (e.g. Forza/FH6 half-fps), now always-on when it matters.
        internal void ApplyResponsivenessState()
        {
            var mgr = _responsiveness;
            if (mgr == null) return;
            bool wanted = !IsShuttingDown && _data != null && _data.IsConnected && IsGameActive;
            mgr.SetExecutionThrottleOptOut(wanted);
            mgr.SetTimerResolution(wanted);
        }

        private void ReconnectTick()
        {
            if (!_connection.IsConnected)
                _connectionCoordinator?.TryConnect();
            else
            {
                // Primary already latched. Two complementary self-heals:
                //  1. base→hub: the base has no wheel but one answered on
                //     the hub (broken base) → run the wheel pipeline over
                //     the hub. Runs FIRST and sets _wheellessBasePort so (2)
                //     can't immediately undo it.
                //  2. hub→base: the primary grabbed a hub before the
                //     wheelbase enumerated (wrong latch order) → hand it
                //     back to the base. Runs before TryConnectHub so the
                //     freed hub port is claimed by the hub manager this tick.
                _connectionCoordinator?.MigratePrimaryToHubIfNeeded();
                _connectionCoordinator?.MigratePrimaryToWheelbaseIfNeeded();
            }
            // AB9 probe is microseconds when registry is populated.
            // On Wine/Proton (no registry) the fallback would scan
            // every wine COM symlink and lock up SimHub; suppress when
            // registry is empty. DisableAb9Detection wins regardless.
            bool registryHasMoza =
                Protocol.MozaPortDiscovery.Instance.Enumerate().Count > 0;
            if (!_settings.DisableAb9Detection
                && registryHasMoza
                && !_ab9Manager.IsConnected)
                _connectionCoordinator?.TryConnectAb9();

            // Standalone-USB CM2 on its own port (0x0025) — same Wine guard.
            if (registryHasMoza && !_dashboardManager.IsConnected)
                _connectionCoordinator?.TryConnectDashboard();

            // Universal Hub on its own port (0x0020) — registry-only, same
            // Wine guard. The hub-only case is handled by the primary
            // (BaseAndHub) connection; this dedicated connection only takes
            // a hub the primary didn't claim (i.e. a base is the primary),
            // and no-ops when the hub port is already held by the primary.
            if (registryHasMoza && !_hubManager.IsConnected)
                _connectionCoordinator?.TryConnectHub();

            // Dedicated base-aux pipe — ONLY after a DELIBERATE base→hub
            // migration (broken base), identified by the _wheellessBasePort
            // latch. Must NOT gate on PrimaryBoundToHub alone: the primary
            // can be TRANSIENTLY on the hub during wrong-latch-order cold
            // start (hub enumerated before the base). If base-aux grabbed
            // the base then, it would hold the port and permanently block
            // MigratePrimaryToWheelbaseIfNeeded from reclaiming it — leaving
            // the primary stuck on the hub (wheel still works via the hub,
            // but the port is mislabeled "Wheelbase"). The latch is set only
            // by a real migration, so a transient hub latch never trips it.
            if (registryHasMoza && _connectionCoordinator?.WheellessBasePort != null && !_baseManager.IsConnected)
                _connectionCoordinator?.TryConnectBase();

            // Slice I: reconnect-timer mBooster Refresh re-enabled.
            try { _mboosterRegistry?.Refresh(); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Refresh: {ex.Message}"); }

            // Standalone pedals/handbrake on their own ports (registry-
            // only, same Wine guard as the other dedicated lanes).
            if (registryHasMoza)
            {
                try { _peripheralRegistry?.Refresh(); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM] Standalone peripheral refresh: {ex.Message}"); }
            }
        }

        private void PollStatus(object sender, ElapsedEventArgs e)
        {
            if (IsShuttingDown) return;
            // System.Timers.Timer swallows exceptions silently — without this
            // wrapper a deterministic throw halts the whole 5 s detection state
            // machine with no log evidence.
            try { PollStatusCore(); }
            catch (Exception ex) { MozaLog.Warn($"[AZOM] PollStatus tick failed: {ex}"); }
        }

        private void PollStatusCore()
        {
            // Responsiveness backstop: SimHub stops calling DataUpdate when a game
            // exits, so the timer raise + EcoQoS opt-out can only be released here once
            // IsGameActive goes false via feed-staleness (~3 s) — within one 5 s poll.
            ApplyResponsivenessState();

            // The dedicated hub pipe is polled independently of the primary
            // (base) connection — a Universal Hub can be present with the base
            // unplugged, or vice versa. No-op when the hub isn't connected.
            _connectionCoordinator?.PollHubPeripherals();
            // Likewise the dedicated base-aux pipe (post base→hub migration) is
            // polled independently for base temps/state. No-op unless connected.
            _connectionCoordinator?.PollBaseAux();

            // CM2 / dashboard-pipeline reconcile runs REGARDLESS of the wheelbase
            // connection. DECOUPLED: a standalone-USB CM2 is driven by the dedicated
            // _cm2Sender on its own pipe with no wheelbase, so gating these behind the
            // wheelbase guard below would never service it. EnsureCm2Pipeline is the
            // periodic reconcile (start when a CM2 appears, reconfigure, and complete
            // the debounced teardown when the CM2 is gone) — it MUST run on a timer for
            // the teardown dwell to elapse; the event-driven apply/detect callers alone
            // can't advance it. Each call is a no-op / idempotent when nothing changed
            // (the Start gate prevents restart churn; the CM1 discriminator early-returns
            // for a non-bus CM2). For a bus CM2 the wheelbase IS connected so the guard
            // below passes anyway — running them here only adds the USB-only case.
            _dualDisplay?.EnsureCm2Pipeline();
            _dashboardBindingCoordinator?.TickPendingDashboardRetry();
            _dualDisplay?.TickCm2DashboardReassert();
            _dualDisplay?.TickCm1Discriminator();
            // FSR1 self-heal: _fsr1Driver is per-instance (unlike the persistent
            // tier-def sender) and is only started via the event-driven
            // StartTelemetryIfReady, which a persistent-wire reload skips for an
            // already-known wheel (LastKnownWheelModel still set → DeviceProber
            // first-sight block gated out) — orphaning the group-0x42 push into a
            // dark screen with nothing to restart it. Reconcile it here like the
            // CM2/CM1 drivers above. Idempotent: no-op unless a stopped FSR1.
            _dualDisplay?.StartFsr1DriverIfNeeded();

            if (!_connection.IsConnected) return;

            // Auto-standby backstop: enters standby when idle (covers the case
            // where SimHub stops calling DataUpdate with no game running) and
            // re-applies the desired work mode after a base reconnect.
            ApplyAutoStandby();

            // Hot-swap detection: track whether the locked wheel is still responding
            // and periodically verify the model name hasn't changed.
            if (DetectionState.NewWheelDetected || DetectionState.OldWheelDetected)
            {
                if (_deviceManager.WheelRespondedSinceLastPoll)
                {
                    DetectionState.WheelPollMisses = 0;
                }
                else
                {
                    DetectionState.WheelPollMisses++;
                    if (DetectionState.WheelPollMisses >= WheelMissThreshold)
                    {
                        ResetWheelDetection(
                            $"Wheel on ID {_deviceManager.WheelDeviceId} not responding " +
                            $"({DetectionState.WheelPollMisses} misses) — resetting for hot-swap");
                    }
                }
                _deviceManager.ResetWheelResponseFlag();

                // Active wheel maintenance. The 0x00 presence poll and the 1-byte
                // 0x43 keepalive (below) replicate PitHouse's idle footprint to
                // 0x17. The group-0x0E param poll is deliberately NOT sent: group
                // 0x0E is the param-manager channel (param_manage.c) itself, and
                // poking it on the wheel provokes the Table-8 "Failed to Read
                // Parameter" storm. On the matching R9 + bare-"CS" rig PitHouse
                // never sends 0e→0x17 (verified cs v2(1).pcapng — it polls 0x0E
                // only on the base, 0e12/0e13). Liveness is driven by the 0x00
                // presence ACK (OnPresenceProbeAck → MarkWheelAlive) plus the
                // wheel's continuous unsolicited 0x0e logs.
                _deviceManager.SendPresenceProbe(MozaProtocol.DeviceWheel);

                // 1-byte 0x43 keepalive — sent to new-protocol wheels regardless of
                // display capability. PitHouse sends this exact frame to the
                // screenless R5 (and to 0x14/0x15) and the wheel stays healthy; the
                // documented screenless hazard is the 11-frame display PROBE
                // (SendDisplayProbe), NOT this keepalive. FSR1 streams its own via
                // Fsr1DisplayDriver, so exclude it to avoid a double-send. Old ES
                // wheels (id 0x13) are excluded — PitHouse never keepalives them.
                if (DetectionState.NewWheelDetected && !IsFsr1DisplayWheel)
                    _deviceManager.SendWheelKeepalive();

                // wheel-model-name recheck: triggers initial identity resolution
                // and hot-swap model-change detection. Every tick while unresolved
                // (fast identity, as before); once resolved the presence ACK is the
                // heartbeat so we recheck only every WheelModelRecheckInterval ticks
                // (kept below WheelMissThreshold so the response still resets the
                // miss counter even if 0x00/0x0e fall silent).
                if (WheelModelInfo == null
                    || ++_wheelModelRecheckTick >= WheelModelRecheckInterval)
                {
                    _wheelModelRecheckTick = 0;
                    _deviceManager.ReadSetting("wheel-model-name");
                    // ES wheels carry their real model at module id 0x18 (the
                    // locked-id read above returns the base/motor name on ES), so
                    // re-read it on the same cadence — a rim swap to a different
                    // model is then caught by model-name hot-swap. No-op on a non-ES
                    // old wheel (0x18 silent); modern wheels skip this branch.
                    if (DetectionState.OldWheelDetected)
                        _deviceManager.ReadSetting("es-wheel-model-name");
                }

                // Probe other wheel IDs for hot-swap detection.
                // Handles ES → new-protocol case where the base keeps responding
                // on the locked ID (19) so miss counter never fires.
                _deviceManager.ProbeOtherWheelIds();
            }

            // Base temps/state are dev-0x13 reads the base main controller answers.
            // A hub-bound primary (post base→hub migration) can't reach the base
            // over the hub — the dedicated base-aux pipe polls them instead (see
            // PollBaseAux). A base-bound primary keeps polling them as before.
            if (!(_connectionCoordinator?.PrimaryBoundToHub ?? false))
                _deviceManager.ReadSettings(StatusPollCommands);

            // Device detection probes — only sent until each device is found.
            //
            // For dash / handbrake / pedals we now use PitHouse-style empty
            // presence probes (`0x00 dev=<id>` → `0x80 dev=<swap>`). The prior
            // approach re-issued the first settings read (`dash-rpm-indicator-mode`
            // etc.) every PollStatus tick; with no device attached the read
            // never got a response and PendingResponseTracker amplified each
            // probe by its 3-retry budget (200/400/800 ms backoff). Net result:
            // 9 frames/tick of pure noise per absent sub-device. Empty probes
            // are NOT tracked, so absent devices cost exactly one 5-byte frame
            // per tick. ACK handling lives in OnPresenceProbeAck (called from
            // OnMessageReceived) which flips DetectionState and kicks off the
            // existing per-device settings read batch.
            //
            // Hub stays on the cmd-specific read path because hub shares
            // device id 0x12 with the wheelbase main controller — an empty
            // probe to 0x12 always ACKs from the base and can't distinguish.
            if (!DetectionState.NewWheelDetected && !DetectionState.OldWheelDetected)
                _deviceManager.ProbeWheelDetection();
            if (!DetectionState.DashDetected)
                _deviceManager.SendPresenceProbe(MozaProtocol.DeviceDash);
            if (!DetectionState.HandbrakeDetected)
                _deviceManager.SendPresenceProbe(MozaProtocol.DeviceHandbrake);
            if (!DetectionState.PedalsDetected)
                _deviceManager.SendPresenceProbe(MozaProtocol.DevicePedals);
            // HGP/SGP attached to the base's peripheral port (dev 0x1A). A shifter on
            // its own USB port is found by MozaStandalonePeripheralRegistry instead;
            // one behind the Universal Hub answers on the dedicated hub pipe.
            if (!DetectionState.ShifterDetected)
                _deviceManager.SendPresenceProbe(MozaProtocol.DeviceHPattern);
            // No hub-port-power poll on the wheelbase connection — a Universal Hub
            // is found by the dedicated hub connection on its own port, never by
            // sending hub commands to a device we already know is a wheelbase.

            // Re-probe display sub-device until fully identified — initial probe
            // can race power-up and return only partial identity. Skip for wheels
            // we already know have no display: the probe sends 11 frames on the
            // dashboard session group (0x43 dev=0x17), and screenless wheels
            // (CS V2.1 / KS / GS V2P / TSW / RS V2 / "CS") may interpret those as
            // dashboard-pipeline traffic and stop servicing settings reads.
            // For resolved-but-unknown wheels (Default model, HasDisplay==null)
            // the re-probe still runs so the UI can light the dashboard section.
            //
            // WheelModelInfo MUST be resolved (non-null) before probing: a bare
            // `WheelModelInfo?.HasDisplay != false` reads `null != false` == true
            // when WheelModelInfo is null, so the probe fired during the
            // unresolved window — which ResetWheelDetection re-opens every time it
            // nulls WheelModelInfo. On a screenless CS V2 that intermittently
            // misses the model-name poll, the model never re-resolves, the probe
            // re-fires each PollStatus tick, and its 0x43 burst drives the wheel
            // into the Table-8 read-fail storm that makes it miss the next poll —
            // a self-sustaining detect→storm→teardown loop. Gate on a resolved
            // model so the unresolved window can't poke a wheel we can't yet
            // confirm has a display.
            if (DetectionState.NewWheelDetected
                && !IsDisplayDetected
                && WheelModelInfo != null
                && WheelModelInfo.HasDisplay != false)
                _deviceManager.SendDisplayProbe();

            // Display-boot wedge watchdog. The W17 (CS Pro) takes ~20 s for its
            // display sub-device to come up after a hot-attach; KS Pro and other
            // displayed wheels are similar. StartTelemetryIfReady's
            // display-detected gate (DashboardBindingCoordinator.cs) defers
            // pipeline start until the display probe answers — correct under
            // normal conditions, but if the display sub-device is genuinely
            // stuck (firmware wedge, mid-USB-enumeration glitch) the gate would
            // sit forever and the user has no signal that anything's wrong.
            // After DisplayWedgeTimeoutMs of waiting we treat the wheel as
            // wedged and force a serial disconnect; the 5 s reconnect timer
            // reopens the port, which gives the wheel's USB stack a chance to
            // re-enumerate and the display a fresh boot. One-shot per attach:
            // DisplayWedgeRecoveryFired stays set until the next successful
            // display detection (cleared in DeviceProber's display-model-name
            // case) or a manual Connection-enable toggle, so a permanently
            // wedged display can't loop the connection.
            // Gated to NewWheelDetected only: old-protocol (ES) wheels never
            // resolve WheelModelInfo (the wheel-model-name resolve is gated on
            // NewWheelDetected because dev 0x13's model name is the base's, not
            // the rim's), so WheelModelInfo stays null and `?.HasDisplay != false`
            // reads null!=false == true — which would otherwise force a one-shot
            // disconnect on a screenless ES wheel that has no display sub-device
            // to wait for. Old wheels have no display; exclude them outright.
            const long DisplayWedgeTimeoutMs = 60_000;
            long wheelDetectedTicks = WheelDetectedUtcTicks;
            if (!DisplayWedgeRecoveryFired
                && DetectionState.NewWheelDetected
                && WheelModelInfo?.HasDisplay != false
                && !IsDisplayDetected
                && wheelDetectedTicks != 0)
            {
                long elapsedMs = (DateTime.UtcNow.Ticks - wheelDetectedTicks)
                    / TimeSpan.TicksPerMillisecond;
                if (elapsedMs >= DisplayWedgeTimeoutMs)
                {
                    DisplayWedgeRecoveryFired = true;
                    var hasDisplayStr = WheelModelInfo?.HasDisplay?.ToString() ?? "unknown";
                    MozaLog.Warn(
                        $"[AZOM] Display sub-device wedge: wheel detected " +
                        $"{elapsedMs}ms ago (HasDisplay={hasDisplayStr}) but " +
                        "display has not responded. Forcing serial disconnect — " +
                        "reconnect timer (5 s) will reopen the port and give the " +
                        "wheel's USB stack a chance to re-enumerate. " +
                        "If this recurs, the display firmware is likely stuck; " +
                        "physically detaching the wheel and reattaching is the next step.");
                    try { _connection?.Disconnect(); } catch { }
                }
            }

            // Group 3 (knob ring) brightness read once after group detected +
            // model resolved. The per-LED ring COLORS (wheel-knob-bg-color{N})
            // are no longer read on the PollStatus path — they're driven by
            // tab activation in MozaWheelSettingsControl.WheelTabs_SelectionChanged
            // (gated on WheelKnobLedMode == 2 / Static), same policy as the
            // RPM and Button color reads. Brightness is a single non-color
            // status read, kept here as part of capability discovery.
            if (!DetectionState.Group3ColorsRead && DetectionState.NewWheelDetected && IsWheelLedGroupPresent(3))
            {
                var model = WheelModelInfo;
                if (model?.KnobRingLeds != null && model.KnobRingLedTotal > 0)
                {
                    DetectionState.Group3ColorsRead = true;
                    _deviceManager.ReadSetting("wheel-knob-brightness");
                    MozaLog.Debug($"[AZOM] Read knob ring brightness (color reads deferred to Knobs-tab activation)");
                }
            }

            // Poll hub port status while hub is connected (read-only, no settings to save).
            // When the primary is itself bound to a hub (hub-only setup) and the hub
            // hasn't been detected yet, keep issuing the hub-port1-power presence read
            // — the connect-time read is tracked/retried, but this also recovers if the
            // hub re-enumerates without a full reconnect. Once detected, poll the full
            // port-power set so the Hub-tab indicators stay current. Mirrors
            // PollHubPeripherals' trigger/full-set split for the dedicated hub pipe.
            if (DetectionState.HubDetected)
                _deviceManager.ReadSettings(DeviceProber.HubReadCommands);
            else if (_connectionCoordinator?.PrimaryBoundToHub == true)
                _deviceManager.ReadSetting("hub-port1-power");
        }

        internal volatile int _unmatched;

        private void OnMessageReceived(byte[] data)
        {
            // Shutdown guard: serial reader may deliver frames after End() begins.
            if (IsShuttingDown) return;

            // Firmware debug frames (raw wire group 0x0E, subtype 0x05) carry
            // unsolicited ASCII status / log lines from the wheel-bus firmware
            // (main bridge / wheel / display). They're not part of the
            // request/response protocol — capture for diagnostics visibility,
            // then short-circuit so MozaResponseParser doesn't waste cycles
            // trying to match them against the command database.
            if (data.Length >= 4
                && data[0] == MozaProtocol.FirmwareDebugGroup
                && data[2] == 0x05)
            {
                byte rawDeviceId = data[1];
                string text;
                try
                {
                    // Body is ASCII text starting at data[3]. Trim trailing
                    // newline / null padding so the ring buffer entries are
                    // compact and don't contain control chars that mess up
                    // the diagnostics text layout. Non-printable bytes
                    // become '?' under the ASCII decode's error replacement.
                    text = System.Text.Encoding.ASCII
                        .GetString(data, 3, data.Length - 3)
                        .TrimEnd('\n', '\r', '\0');
                }
                catch
                {
                    text = $"<{data.Length - 3} bytes>";
                }
                _firmwareDebugLog.Record(rawDeviceId, text);
                // The wheel streams these 0x0E logs (dev 0x71) continuously whenever it
                // is physically connected — they only stop on a real rim/link drop. So
                // they are positive "wheel is alive" evidence: count them against the
                // hot-swap miss counter. Marked unconditionally (not via the id-matched
                // MarkWheelResponse) because the 0x71 log channel identifies the wheel
                // by itself, regardless of whether it's locked on 23/21/19. Without
                // this, a wheel that keeps logging but stops answering the periodic
                // wheel-model-name poll (the FSR1 firmware does exactly this after
                // initial detection) trips the poll-miss watchdog and gets re-detected
                // on a ~20 s loop — a phantom "disconnection". A genuine disconnect
                // still fires the watchdog (the logs stop too). Verified against
                // "Disconnection issue.pcapng".
                if (rawDeviceId == 0x71)
                    _deviceManager.MarkWheelAlive();
                // FSR V1 reports its current dashboard/page index via this log on
                // every switch (incl. wheel-side HID combo): "Table 7, Param 6
                // Written: <N>". Parse it so the plugin follows wheel-initiated
                // switches. See docs/protocol/devices/wheel-0x17.md § Group 0x42.
                if (rawDeviceId == 0x71 && IsFsr1DisplayWheel)
                    _fsr1Cm1Mapping.TryFollowFsr1DashboardLog(text);
                // A CM1 base-bridged dash reports its current page via the byte-identical
                // "Table 7, Param 6 Written: N" log on dev 0x41 (0x14 swapped). Follow it.
                if (rawDeviceId == 0x41 && DashIsCm1)
                    _fsr1Cm1Mapping.TryFollowCm1DashboardLog(text);
                // CM2 meter heartbeat vocabulary identifies its firmware era (LED
                // command family) — see DetectCm2LedFirmwareEra.
                if (rawDeviceId == 0x41 && !DashIsCm1)
                    DetectCm2LedFirmwareEra(text);
                // The main bridge logs steering-wheel (rim) attach/detach edges
                // here as "steer_connected <N>" / "Gpw Wheel Disconnected". A rim
                // pull is NOT a USB/serial disconnect, so the poll-miss hot-swap
                // path never fires — this is the only signal that tears down the
                // stale cached identity/catalog. See TryHandleWheelConnectionLog.
                if (rawDeviceId == 0x21)
                    TryHandleWheelConnectionLog(text);
                if (MozaLog.WireDebugEnabled)
                    MozaLog.Debug(
                        $"[AZOM] firmware-debug src={(rawDeviceId == 0x21 ? "main" : rawDeviceId == 0x71 ? "wheel" : rawDeviceId == 0xB1 ? "display" : $"0x{rawDeviceId:X2}")}: {text}");
                return;
            }
            // Other 0x0E variants we don't yet know how to decode — drop
            // silently (preserves prior behaviour for unknown subtypes).
            if (data.Length >= 1 && data[0] == MozaProtocol.FirmwareDebugGroup)
                return;

            // Filter SerialStream control frames (0xC3 / 7C/FC + 00) — session-
            // management chunks handled by TelemetrySender, not command responses.
            if (data.Length >= 4 && data[0] == MozaProtocol.SerialStreamRespGroup &&
                (data[2] == MozaProtocol.SerialStreamOpcodeData ||
                 data[2] == MozaProtocol.SerialStreamOpcodeCtrl) && data[3] == 0x00)
                return;

            // Filter wheel's 7c:23 dashboard-activate advertisements — informational,
            // absorbed by TelemetrySender.
            if (data.Length >= 4 && data[0] == MozaProtocol.SerialStreamRespGroup
                && data[2] == MozaProtocol.SerialStreamOpcodeData && data[3] == 0x23)
                return;

            // Filter group 0x40 channel-config burst echoes (1E XX, 28 XX).
            // Wheel returns stored EEPROM values; mark wheel-alive and swallow.
            if (data.Length >= 4 && data[0] == MozaProtocol.WheelChannelCfgRespGroup
                && data[1] == MozaProtocol.WheelDeviceIdSwapped
                && (data[2] == MozaProtocol.WheelCfgOpcodeChannelEnable ||
                    data[2] == MozaProtocol.WheelCfgOpcodeMultiFunction))
            {
                // Capture raw 28:00 / 28:01 reply bytes; semantics not yet
                // decoded — stored raw for offline correlation against game state.
                if (data.Length >= 6 && data[2] == MozaProtocol.WheelCfgOpcodeMultiFunction)
                {
                    if (data[3] == 0x00 && _data != null)
                    {
                        _data.Last28x00Byte5 = data[5];
                        _data.Last28x00ByteValid = true;
                        _data.Last28xReplyTickMs = Environment.TickCount;
                    }
                    else if (data[3] == 0x01 && _data != null)
                    {
                        _data.Last28x01Byte4 = data[4];
                        _data.Last28x01Byte5 = data[5];
                        _data.Last28x01BytesValid = true;
                        _data.Last28xReplyTickMs = Environment.TickCount;
                    }
                }
                _deviceManager.MarkWheelResponse(MozaProtocol.SwapNibbles(data[1]));
                return;
            }

            // Empty presence-probe ACK (PitHouse-style): host sent
            // `7e 00 00 dev_id chk`, device replied `7e 00 80 swap(dev_id) chk`.
            // The on-wire frame has been stripped of its 0x7e + length and
            // checksum by MozaSerialConnection, so `data` here is
            // {group=0x80, dev_id_swapped} — 2 bytes total. Route to the per-id
            // first-sight detection helper. SendPresenceProbe in PollStatus is
            // the only caller that emits empty probes today; the wheel itself
            // never spontaneously sends these.
            if (data.Length == 2 && data[0] == 0x80)
            {
                byte deviceId = MozaProtocol.SwapNibbles(data[1]);
                OnPresenceProbeAck(deviceId);
                return;
            }

            // CM1 param-read reply: the discriminator probe (SendCm1ParamProbe,
            // group 0x0E → dev 0x14) was answered with a group-0x8E frame from the
            // dash (dev 0x41). A tier-def CM2 doesn't answer it, so this is a
            // positive CM1 signal for TickCm1Discriminator's fast path. Not a
            // command-DB entry — flag and short-circuit.
            if (data.Length >= 2 && data[0] == 0x8E && data[1] == 0x41)
            {
                _dualDisplay?.NoteDashParamReadAnswered();
                return;
            }

            var result = MozaResponseParser.Parse(data);
            if (!result.HasValue)
            {
                // Known wheel write echoes with no command DB entry — treat as
                // keepalive from the wheel device id. See MozaProtocol.WheelEchoPrefixes.
                if (MozaProtocol.IsWheelEcho(data))
                {
                    _deviceManager.MarkWheelResponse(MozaProtocol.SwapNibbles(data[1]));
                    return;
                }

                // Any wheel-targeted response counts as "wheel is alive" even if
                // we can't decode the specific command. Prior behavior only
                // marked alive on parsed reads / known echo prefixes — wheel
                // read-responses outside those two paths (e.g. LED state poll
                // group 2 with payload prefix `1F 03 02`) were logged as
                // Unmatched and never reset PollStatus's miss counter. The
                // wheel kept answering at ~5 s cadence, but every response
                // looked like silence to the hot-swap detector, which
                // incorrectly tripped after 3 ticks (15 s) and triggered an
                // unnecessary Stop+silence-gate+restart cycle.
                if (data.Length >= 2)
                {
                    byte dev = MozaProtocol.SwapNibbles(data[1]);
                    if (dev == MozaProtocol.DeviceWheel)
                        _deviceManager.MarkWheelResponse(dev);
                }

                _unmatched++;
                if (_unmatched <= 20 && data.Length >= 2)
                {
                    byte grp = MozaProtocol.ToggleBit7(data[0]);
                    byte dev = MozaProtocol.SwapNibbles(data[1]);
                    // BitConverter.ToString rejects startIndex == value.Length on
                    // .NET Framework even when length == 0, so guard the
                    // payload-only frames (e.g. bare `c0 71` wheel ACKs).
                    int showLen = Math.Min(data.Length - 2, 8);
                    string payload = showLen > 0
                        ? BitConverter.ToString(data, 2, showLen)
                        : "(empty)";
                    MozaLog.Debug(
                        $"[AZOM] Unmatched #{_unmatched}: rawGroup=0x{data[0]:X2} group=0x{grp:X2} " +
                        $"rawDev=0x{data[1]:X2} dev={dev} len={data.Length} " +
                        $"payload={payload}");
                }
                return;
            }

            var r = result.Value;

            PendingResponses.NoteResponse(r.Name);

            // Normalize stick-mode: old firmware sends 2-byte value (0 or 256),
            // new firmware sends 1-byte enum (0=none, 1=left, 2=right, 3=both).
            if (r.Name == "wheel-stick-mode")
            {
                if (r.PayloadLength <= 1)
                {
                    _data.WheelDualStickSupported = true;
                }
                else
                {
                    // Old 2-byte format: 0x0100 (256) = left D-pad on
                    r.IntValue = r.IntValue >= 256 ? 1 : 0;
                }
            }

            // Base identity reads share grp/cmd shape with wheel identity;
            // disambiguate by device id (base=0x12, wheel=0x13/15/17).
            if (r.Name == "wheel-model-name" && r.DeviceId == MozaProtocol.DeviceMain
                && r.ArrayValue != null)
            {
                var baseName = MozaData.ParseNullTerminatedString(r.ArrayValue);
                if (!string.IsNullOrEmpty(baseName) && _data.BaseModelName != baseName)
                {
                    _data.BaseModelName = baseName;
                    MozaLog.Debug($"[AZOM] Base identity: {baseName}");
                }
                return;
            }

            _data.UpdateFromCommand(r.Name, r.IntValue);
            if (r.ArrayValue != null)
                _data.UpdateFromArray(r.Name, r.ArrayValue);

            // Persist wheel-reported sleep-bundle values so next launch reapplies them.
            _profileCoordinator.SeedSleepBundleFromResponse(r);

            // Extended LED group presence: any response from a group proves it exists.
            if (r.Name != null)
            {
                int g = -1;
                if (r.Name.StartsWith("wheel-single-",  StringComparison.Ordinal)) g = 2;
                else if (r.Name.StartsWith("wheel-knob-",    StringComparison.Ordinal)) g = 3;
                else if (r.Name.StartsWith("wheel-ambient-", StringComparison.Ordinal)) g = 4;
                if (g >= 2 && g <= 4 && DetectionState.TrySetWheelLedGroupPresent(g))
                    MozaLog.Debug($"[AZOM] Wheel LED group {g} detected");
            }

            _deviceManager.MarkWheelResponse(r.DeviceId);
            if (r.Name != null)
                _deviceProber.DetectDevices(r.Name, r.IntValue, r.DeviceId);
        }

        /// <summary>
        /// Dispatch an empty presence-probe ACK to the first-sight detection
        /// helper for the matching sub-device. Handles devices probed via
        /// <see cref="MozaDeviceManager.SendPresenceProbe"/> from PollStatus —
        /// dash / handbrake / pedals (first-sight detection) and the locked
        /// wheel (liveness heartbeat → <see cref="MozaDeviceManager.MarkWheelAlive"/>).
        /// Other device IDs (e.g. AB9, Booster) reach this path harmlessly and
        /// are ignored.
        /// </summary>
        private void OnPresenceProbeAck(byte deviceId)
        {
            switch (deviceId)
            {
                case MozaProtocol.DeviceWheel:
                    // Presence ACK from the locked wheel — the active liveness
                    // heartbeat that replaced the per-tick model-name read.
                    _deviceManager.MarkWheelAlive();
                    break;
                case MozaProtocol.DeviceDash:
                    _deviceProber.MarkDashDetected();
                    break;
                case MozaProtocol.DeviceHandbrake:
                    _deviceProber.MarkHandbrakeDetected();
                    break;
                case MozaProtocol.DevicePedals:
                    _deviceProber.MarkPedalsDetected();
                    break;
                // DeviceHPattern == DeviceSequential (0x1A) — one case covers both.
                case MozaProtocol.DeviceHPattern:
                    _deviceProber.MarkShifterDetected();
                    break;
            }
        }

        /// <summary>
        /// Serial-message handler for the AB9 shifter's dedicated pipe. Marks the
        /// device as detected on the first parseable response, pushes saved
        /// profile settings once, and lets the AB9 settings UI snapshot the
        /// latest device-reported values via <see cref="MozaAb9DeviceManager"/>.
        /// </summary>
        private void OnAb9MessageReceived(byte[] data)
        {
            if (IsShuttingDown) return;
            if (data == null || data.Length < 2) return;

            // Filter firmware debug noise before parsing.
            if (data[0] == MozaProtocol.FirmwareDebugGroup) return;

            // Bus-hint "ab9" disambiguates from base-* commands (the AB9 main and
            // wheelbase main share device id 0x12 numerically — without the hint
            // the parser auto-tags as "base" and filters out every ab9-* match).
            var result = MozaResponseParser.Parse(data, busHint: "ab9");
            if (!result.HasValue) return;

            var r = result.Value;
            if (r.Name == null || !r.Name.StartsWith("ab9-", StringComparison.Ordinal))
                return;

            bool rising = !DetectionState.Ab9Detected;
            _ab9Manager.MarkDetected();
            if (rising)
            {
                DetectionState.Ab9Detected = true;
                // Push the FFB session-init handshake (alloc/init/commit) once on
                // the rising edge. Manager guards against re-sending across reconnects.
                try { _ab9Manager.SendFfbInitSequence(); }
                catch (Exception ex) { MozaLog.Warn($"[AZOM/AB9] FFB init failed: {ex.Message}"); }
                ApplyAb9ToHardware(_settings?.ProfileStore?.CurrentProfile);
            }

            MozaLog.Debug($"[AZOM/AB9] {r.Name} = {r.IntValue}");
        }

        /// <summary>
        /// Auto-detect connected devices based on response commands.
        ///   - dash-rpm-indicator-mode responds -> dashboard present
        ///   - wheel-telemetry-mode responds -> new protocol wheel (GS/FSR/CS/RS/TSW)
        ///   - wheel-rpm-value1 responds (but not telemetry-mode) -> old protocol wheel (ES)
        /// </summary>
        
        // Hardware-apply entry points moved to HardwareApplier. Profile + WheelOverride
        // are the single source of truth; every write is detection-gated AND
        // sentinel-guarded (no brightness write storm on cold start).

        /// <summary>
        /// Resolve the wheel-page GUID for the currently-connected wheel, or null
        /// if no wheel model is known yet (hardware not identified).
        /// </summary>
        internal Guid? GetCurrentWheelPageGuid()
        {
            var modelName = _data?.WheelModelName;
            if (string.IsNullOrEmpty(modelName)) return null;
            var prefix = WheelModelInfo.ExtractPrefix(modelName!);
            if (string.IsNullOrEmpty(prefix)) return null;
            var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
            if (!Guid.TryParse(guidStr, out var g)) return null;
            return g;
        }

        // ===== ProfileCoordinator accessor shims (external API surface) =====
        // Wheel overlay + per-wheel-page telemetry/sleep/idle/era accessors live
        // in Settings/ProfileCoordinator.cs.
        internal WheelOverride? GetCurrentWheelOverlay(MozaProfile? profile) => _profileCoordinator.GetCurrentWheelOverlay(profile);
        internal WheelOverride? GetOrCreateCurrentWheelOverlay(MozaProfile? profile) => _profileCoordinator.GetOrCreateCurrentWheelOverlay(profile);
        internal void UpdateActiveWheelOverlay(Action<WheelOverride> mutator) => _profileCoordinator.UpdateActiveWheelOverlay(mutator);
        internal void UpdateActiveProfile(Action<MozaProfile> mutator) => _profileCoordinator.UpdateActiveProfile(mutator);
        internal bool ActiveTelemetryEnabled
        {
            get => _profileCoordinator.ActiveTelemetryEnabled;
            set => _profileCoordinator.ActiveTelemetryEnabled = value;
        }
        internal string ActiveTelemetryProfileName
        {
            get => _profileCoordinator.ActiveTelemetryProfileName;
            set => _profileCoordinator.ActiveTelemetryProfileName = value;
        }
        internal string ActiveTelemetryMzdashPath
        {
            get => _profileCoordinator.ActiveTelemetryMzdashPath;
            set => _profileCoordinator.ActiveTelemetryMzdashPath = value;
        }
        internal string ActiveTelemetryMzdashFolder
        {
            get => _profileCoordinator.ActiveTelemetryMzdashFolder;
            set => _profileCoordinator.ActiveTelemetryMzdashFolder = value;
        }
        internal WheelSleepSettings? ActiveWheelSleep => _profileCoordinator.ActiveWheelSleep;
        internal WheelSleepSettings? GetOrCreateActiveWheelSleep() => _profileCoordinator.GetOrCreateActiveWheelSleep();
        internal WheelIdleSettings? ActiveWheelIdle => _profileCoordinator.ActiveWheelIdle;
        internal WheelIdleSettings? GetOrCreateActiveWheelIdle() => _profileCoordinator.GetOrCreateActiveWheelIdle();
        internal MozaWheelEra ActiveTelemetryWheelEra
        {
            get => _profileCoordinator.ActiveTelemetryWheelEra;
            set => _profileCoordinator.ActiveTelemetryWheelEra = value;
        }

        /// <summary>
        /// Channel-mapping dict for the active profile × current wheel page. Null
        /// when no profile/wheel is resolvable. Caller must not mutate returned
        /// dict directly — use the channel-mapping write helpers in MozaPlugin.cs.
        /// </summary>
        // CM2 dash settings are keyed under a fixed page GUID with a single
        // dashboard key, so the CM2's dashboard/channel config is fully independent
        // of the wheel's. pageGuid==null means "the current wheel page".
        // This literal is the GUID of the retired SHDP "MOZA Dashboard" device; it
        // is retained verbatim as the CM2 persistence key so existing users' saved
        // CM2 dashboard/channel mappings (keyed under it) survive the SHDP removal.
        // It is a persistence key only — not a live SimHub device id.
        internal static readonly Guid Cm2PageGuid = Guid.Parse("c97a4d00-a66d-4e2f-a9b4-e7fc348dcc33");
        internal const string Cm2DashKey = "cm2";

        // CM1 base-bridged dash gets its OWN page GUID so its field mappings,
        // active-dashboard selection and the CM1/CM2 discriminator never share a
        // key with the CM2 dash (which uses Cm2PageGuid). A user can run a CM1 and
        // a CM2 simultaneously; keeping the identities disjoint is what lets both
        // persist independently.
        internal static readonly Guid Cm1PageGuid = Guid.Parse(Devices.MozaDeviceConstants.DashCm1Guid);

        internal Dictionary<string, Dictionary<string, string>>? GetActiveChannelMappings(Guid? pageGuid = null)
        {
            var profile = _settings?.ProfileStore?.CurrentProfile;
            if (profile?.TelemetryChannelMappings == null) return null;
            var g = pageGuid ?? GetCurrentWheelPageGuid();
            if (!g.HasValue) return null;
            return profile.TelemetryChannelMappings.TryGetValue(g.Value, out var m) ? m : null;
        }

        // ===== FSR1/CM1 field-mapping + index shims (external API surface) =====
        // FSR V1 (group-0x42) + CM1 (group-0x35) field mappings and the active
        // dashboard/page index store live in Telemetry/Fsr1Cm1MappingCoordinator.cs.
        internal Fsr1FieldMapping? GetFsr1FieldMapping(string recordKey, string fieldId) => _fsr1Cm1Mapping.GetFsr1FieldMapping(recordKey, fieldId);
        internal void SetFsr1FieldMapping(string recordKey, string fieldId, Fsr1FieldMapping? mapping) => _fsr1Cm1Mapping.SetFsr1FieldMapping(recordKey, fieldId, mapping);
        internal int GetActiveFsr1Index() => _fsr1Cm1Mapping.GetActiveFsr1Index();
        internal void SetActiveFsr1Index(int index, bool sendToWheel) => _fsr1Cm1Mapping.SetActiveFsr1Index(index, sendToWheel);
        internal int TakePendingFsr1Select() => _fsr1Cm1Mapping.TakePendingFsr1Select();

        // FSR1 synthetic split fields (net-new fields carved out of a catalog field).
        internal System.Collections.Generic.List<Fsr1SyntheticField> GetSyntheticFields(string recordKey) => _fsr1Cm1Mapping.GetSyntheticFields(recordKey);
        internal bool SplitFsr1Field(string recordKey, string fieldId) => _fsr1Cm1Mapping.SplitFsr1Field(recordKey, fieldId);
        internal bool BitSplitFsr1Field(string recordKey, string fieldId) => _fsr1Cm1Mapping.BitSplitFsr1Field(recordKey, fieldId);
        internal bool RemoveFsr1Split(string recordKey, string fieldId) => _fsr1Cm1Mapping.RemoveFsr1Split(recordKey, fieldId);
        internal bool MergeFsr1Field(string recordKey, string fieldId, bool mergeNext) => _fsr1Cm1Mapping.MergeFsr1Field(recordKey, fieldId, mergeNext);
        internal void ClearSyntheticFields(string recordKey) => _fsr1Cm1Mapping.ClearSyntheticFields(recordKey);
        internal void ClearFsr1FieldOverrides(string recordKey) => _fsr1Cm1Mapping.ClearFsr1FieldOverrides(recordKey);
        internal Fsr1FieldDef? FindFsr1Field(string recordKey, string fieldId) => Fsr1FieldComposer.FindField(this, recordKey, fieldId);

        internal Fsr1FieldMapping? GetCm1FieldMapping(string fieldId) => _fsr1Cm1Mapping.GetCm1FieldMapping(fieldId);
        internal void SetCm1FieldMapping(string fieldId, string property, double? scale) => _fsr1Cm1Mapping.SetCm1FieldMapping(fieldId, property, scale);
        internal void ClearCm1Mappings() => _fsr1Cm1Mapping.ClearCm1Mappings();
        internal int GetActiveCm1Index() => _fsr1Cm1Mapping.GetActiveCm1Index();
        internal void SetActiveCm1Index(int index, bool sendToWheel) => _fsr1Cm1Mapping.SetActiveCm1Index(index, sendToWheel);
        internal int TakePendingCm1Select() => _fsr1Cm1Mapping.TakePendingCm1Select();

        /// <summary>True once this dash is confirmed a CM1 (group-0x35). Persisted per
        /// dash GUID so later boots skip the tier-def probe.</summary>
        internal bool DashIsCm1
        {
            get => _fsr1Cm1Mapping.DashIsCm1;
            set => _fsr1Cm1Mapping.DashIsCm1 = value;
        }

        /// <summary>Raised when the active FSR1 dashboard index changes (either the
        /// user picked it or the wheel reported a self-switch). UI re-selects.</summary>
        internal event EventHandler? Fsr1ActiveIndexChanged;

        internal void RaiseFsr1ActiveIndexChanged()
        {
            try { Fsr1ActiveIndexChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        /// <summary>Raised when the active CM1 dashboard index changes (user pick or the
        /// dash reported a self-switch via its Param-6 log). UI re-selects.</summary>
        internal event EventHandler? Cm1ActiveIndexChanged;

        internal void RaiseCm1ActiveIndexChanged()
        {
            try { Cm1ActiveIndexChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        // Match "steer_connected <N>" in a main-bridge firmware-debug line. The
        // wheel-bus firmware emits this on the edge of the steering-wheel (rim)
        // attach state: "steer_connected 1" when a rim is seated on the
        // quick-release, "steer_connected 0" when it's pulled off (alongside
        // "Gpw Wheel Disconnected").
        private static readonly System.Text.RegularExpressions.Regex _steerConnectedRe =
            new System.Text.RegularExpressions.Regex(
                @"steer_connected\s+(\d+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Last rim attach-state parsed from the main bridge firmware-debug log;
        // -1 = not yet observed. Read/written only on the serial read thread
        // (OnMessageReceived), so no synchronisation needed.
        private int _lastSteerConnected = -1;

        // Tear down cached wheel/display identity + channel catalog when the rim
        // is detached. A rim pull keeps the wheelbase COM port open and the base
        // keeps answering wheel-model-name on the locked ID (see PollStatus
        // hot-swap notes), so the poll-miss path never fires and the diagnostics
        // tab / dashboard gating would otherwise report a phantom wheel
        // indefinitely. We act only on the 1→0 (or unknown→0) falling edge;
        // reseating the rim re-detects automatically via the PollStatus
        // ProbeWheelDetection loop, so no connect-edge handling is needed.
        private void TryHandleWheelConnectionLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var m = _steerConnectedRe.Match(text);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int state)) return;

            int prev = _lastSteerConnected;
            _lastSteerConnected = state;
            if (state == prev || state != 0) return;   // ignore re-prints and the attach edge

            if (DetectionState.NewWheelDetected || DetectionState.OldWheelDetected)
                ResetWheelDetection(
                    "Rim detached (firmware steer_connected 0) — resetting wheel detection");
        }

        /// <summary>Apply a profile via the consolidated Apply*ToHardware methods —
        /// logic lives in Settings/ProfileCoordinator.cs.</summary>
        internal void ApplyProfile(MozaProfile profile) => _profileCoordinator.ApplyProfile(profile);

        // Build the 3-byte payload shared by per-effect speed commands:
        //   wheel-{telemetry,buttons,knob}-idle-interval — `[effect_id, ms_msb, ms_lsb]`
        //   wheel-idle-speed                              — `[mode,      ms_msb, ms_lsb]`
        // The first byte selects which effect/mode the slider applies to;
        // the remaining two bytes encode the ms value big-endian.
        
        /// <summary>
        /// Apply dash settings from the SimHub device extension profile system.
        /// Updates _settings, _data, and writes to hardware if connected.
        /// </summary>
        
        /// <summary>
        /// Apply base ambient LED settings from the SimHub device extension
        /// profile system. Mirror of <see cref="ApplyDashExtensionSettings"/>.
        /// </summary>
        
    }
}
