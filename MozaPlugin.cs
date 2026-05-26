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
    [PluginName("MOZA Control")]
    public class MozaPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        internal static MozaPlugin? Instance { get; private set; }

        // Persistent wire: survives plugin reload on game switch so the
        // wheel never sees the ~10–14s sess=0x09 settle. Disposed only on
        // process exit or on wheel unplug (Init checks "still connected?").
        private static MozaSerialConnection? s_persistentConnection;
        private static TelemetrySender? s_persistentTelemetrySender;

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
        // SessionWatchdogManager.S09BackoffMs) and parks the dashboard
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
        private MozaMBoosterRegistry? _mboosterRegistry;

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
        internal global::MozaPlugin.Protocol.PendingResponseTracker PendingResponses { get; }
            = new global::MozaPlugin.Protocol.PendingResponseTracker();
        private MozaPluginSettings _settings = null!;
        private Timer _pollTimer = null!;
        private Timer _retryTimer = null!;
        private Timer _reconnectTimer = null!;
        private int _connectingFlag;
        private MozaHidReader _hidReader = null!;
        private PluginManager _pluginManager = null!;
        private SimHubPropertyResolver _propertyResolver = null!;
        internal SimHubPropertyResolver PropertyResolver => _propertyResolver;
        private HardwareApplier _hardwareApplier = null!;
        internal HardwareApplier HardwareApplier => _hardwareApplier;
        private DeviceProber _deviceProber = null!;
        internal DeviceProber DeviceProber => _deviceProber;
        private DashboardBindingCoordinator _dashboardBindingCoordinator = null!;
        internal DashboardBindingCoordinator DashboardBindingCoordinator => _dashboardBindingCoordinator;

        // ===== DashboardBindingCoordinator shims (external API surface) =====
        internal void ApplyTelemetrySettings() => _dashboardBindingCoordinator.ApplyTelemetrySettings();

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
                        $"[Moza] RequestSavedDashboardReapply: apply threw — {ex.Message}");
                    return;
                }
                if (!applied)
                {
                    _dashboardBindingCoordinator.SetPendingDashboardKey(profile.TelemetryDashboardKey!);
                    MozaLog.Debug(
                        $"[Moza] RequestSavedDashboardReapply: deferred " +
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
                MozaLog.Warn($"[Moza] RequestSavedDashboardReapply: outer error — {ex.Message}");
            }
        }
        internal void RestartTelemetry() => _dashboardBindingCoordinator.RestartTelemetry();
        internal bool ApplyTelemetryDashboardFromProfile(MozaProfile profile) => _dashboardBindingCoordinator.ApplyTelemetryDashboardFromProfile(profile);
        internal void OnDashboardSwitched(uint slot) => _dashboardBindingCoordinator.OnDashboardSwitched(slot);
        internal void OnDashboardSwitched() => _dashboardBindingCoordinator.OnDashboardSwitched();
        internal void SetTelemetryEnabled(bool enabled) => _dashboardBindingCoordinator.SetTelemetryEnabled(enabled);
        internal void StartTelemetryIfReady() => _dashboardBindingCoordinator.StartTelemetryIfReady();

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
                $"[Moza] Raising DashboardSelectionChanged (subscribers={subs}, " +
                $"profileName='{_settings?.TelemetryProfileName}', " +
                $"mzdash='{_settings?.TelemetryMzdashPath}')");
            try { DashboardSelectionChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { MozaLog.Warn("[Moza] DashboardSelectionChanged subscriber threw: " + ex.Message); }
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
        internal void ApplyDashExtensionSettings(MozaDashExtensionSettings extSettings) => _hardwareApplier.ApplyDashExtensionSettings(extSettings);
        internal void ApplyBaseExtensionSettings(MozaBaseExtensionSettings extSettings) => _hardwareApplier.ApplyBaseExtensionSettings(extSettings);
        internal void ClearLedsOnHardware() => _hardwareApplier.ClearLedsOnHardware();

        private TelemetrySender? _telemetrySender;
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

        // Guard against concurrent/duplicate telemetry Start() dispatch.
        // Internal so DashboardBindingCoordinator can Interlocked.* against it.
        internal int _telemetryStartRequested;

        // Set during End() so in-flight callbacks can bail out.
        internal static volatile bool IsShuttingDown;

        // Debounce disk writes during rapid slider changes
        private Timer? _saveDebounceTimer;

        // Tracks the ProfileStore we subscribed CurrentProfileChanged on, so we can
        // detach when ClearSettings replaces _settings (orphaned subscription would
        // otherwise mutate plugin state via captured `this` from a dead store).
        private MozaProfileStore? _subscribedProfileStore;

        private static readonly string[] StatusPollCommands = new[]
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
        public ImageSource? PictureIcon => null;
        public string LeftMenuTitle => "MOZA";

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
        /// True when the open serial port belongs to a Moza dashboard PID
        /// (CM2 = 0x0025). Lets dashboard detection flip on USB PID alone,
        /// without waiting for a wheelbase relay or wheel-side ack.
        /// </summary>
        private bool IsStandaloneDashboardUsbConnection =>
            _connection?.IsConnected == true
            && MozaUsbIds.IsDashboardPid(_connection.DiscoveredPid);

        internal bool IsDashDetected =>
            DetectionState.DashDetected || IsStandaloneDashboardUsbConnection;

        /// <summary>
        /// True iff the active dashboard pipeline must address a standalone
        /// dashboard (CM2 bridge/main at dev=0x12) rather than a wheel-hosted
        /// display at dev=0x17. Requires: dashboard detected, no wheel detected,
        /// and the open USB PID classified as Dashboard.
        /// </summary>
        internal bool ShouldUseStandaloneDashboardTarget()
        {
            if (!DetectionState.DashDetected && !IsStandaloneDashboardUsbConnection) return false;
            if (DetectionState.NewWheelDetected || DetectionState.OldWheelDetected) return false;
            return MozaUsbIds.IsDashboardPid(_connection?.DiscoveredPid);
        }

        /// <summary>
        /// Target dev_id for screen telemetry / session-control frames when
        /// driving a standalone dashboard. Pinned to <see cref="MozaProtocol.DeviceMain"/>
        /// (0x12 = CM2 bridge/main) — the verified target for CM2; a future
        /// second standalone dash model can override here.
        /// </summary>
        internal byte PreferredStandaloneDashboardTargetDeviceId => MozaProtocol.DeviceMain;

        internal bool IsBaseAmbientLedSupported => DetectionState.BaseAmbientLedSupported;
        internal bool IsHandbrakeDetected => DetectionState.HandbrakeDetected;
        internal bool IsPedalsDetected => DetectionState.PedalsDetected;
        internal bool IsHubDetected => DetectionState.HubDetected;
        internal bool IsAb9Detected => DetectionState.Ab9Detected;
        internal MozaAb9DeviceManager Ab9Manager => _ab9Manager;
        internal MozaMBoosterRegistry? MBoosterRegistry => _mboosterRegistry;
        internal MozaSerialConnection Connection => _connection;

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
        internal bool ShouldDriveDashboard()
        {
            bool? hasDisplay = WheelModelInfo?.HasDisplay;
            if (hasDisplay == false) return false;   // known no-display: never
            if (hasDisplay == true)  return true;    // known display: don't wait for probe
            return IsDisplayDetected;                // unknown model: trust the probe
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
                MozaLog.Warn("[Moza] Init() called with prior state still live — tearing down before re-init");
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
            // A fresh Init means we don't know what dashboard the wheel is
            // Belt-and-braces for the defensive double-Init path above. Coordinator
            // is null on a brand-new instance; only fires after a re-Init.
            _dashboardBindingCoordinator?.ClearLastAppliedDashboardKey();
            _pluginManager = pluginManager;

            try
            {
                _data = new MozaData();
                _settings = this.ReadCommonSettings<MozaPluginSettings>("MozaPluginSettings", () => new MozaPluginSettings());

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

                // Legacy upgrade: hoist flat TelemetryChannelMappings into the
                // per-wheel schema (empty key) so existing users keep their data.
                if (_settings.MigrateLegacyChannelMappingsIfNeeded())
                {
                    MozaLog.Info("[Moza] Migrated legacy TelemetryChannelMappings to per-wheel schema (under empty-wheel slot \"\")");
                    this.SaveCommonSettings("MozaPluginSettings", _settings);
                }

                // Schema migration to v8 (legacy UID/model → profile-scoped
                // WheelOverride). Registry must initialise first for page-GUID
                // resolution. See SettingsMigrator.
                MozaDeviceConstants.InitializeRegistry();
                if (new SettingsMigrator(_settings).MigrateToSchemaV2())
                {
                    this.SaveCommonSettings("MozaPluginSettings", _settings);
                }

                // Restore blink colors from settings (write-only, can't be polled from device)
                MozaProfile.UnpackColorsInto(_settings.WheelRpmBlinkColors, _data.WheelRpmBlinkColors);
                MozaProfile.UnpackColorsInto(_settings.DashRpmBlinkColors, _data.DashRpmBlinkColors);

                MozaLog.Info("[Moza] Initializing plugin");

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
                        MozaLog.Debug($"[Moza] Wire trace sink → {sinkPath}");
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Warn($"[Moza] Wire trace sink open failed: {ex.GetType().Name}: {ex.Message}");
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
                        ? $"[Moza] Serial traffic capture preserved across reload — AlwaysCaptureOnStartup is on ({cap.Count} entries)"
                        : "[Moza] Serial traffic capture auto-started — AlwaysCaptureOnStartup is on");
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
                MozaLog.Debug($"[Moza] Temperature unit: {(_data.UseFahrenheit ? "Fahrenheit" : "Celsius")}");

                // Profile system init is deferred until after the collaborators
                // (HardwareApplier, DeviceProber, PropertyResolver) are constructed,
                // because AutoApplyProfile calls ApplyProfile which delegates to
                // _hardwareApplier. See further down in Init.

                RegisterProperties(pluginManager);
                RegisterActions();

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
                                $"[Moza] Restored WheelModelInfo from persistent state: {savedModel} " +
                                $"(rpm={WheelModelInfo?.RpmLedCount}, buttons={WheelModelInfo?.ButtonLedCount}, " +
                                $"knobs={WheelModelInfo?.KnobCount}, flags={WheelModelInfo?.HasFlagLeds})");
                        }
                    }
                    MozaLog.Info("[Moza] Reusing persistent serial connection from prior plugin instance");
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
                        pid => MozaUsbIds.IsWheelbasePid(pid)
                               || MozaUsbIds.IsHubPid(pid)
                               || MozaUsbIds.IsDashboardPid(pid)
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

                // AB9 engine-vibration worker — tick gates on connection/detection state.
                _ab9Worker = new Ab9EngineVibrationWorker(
                    _ab9Manager,
                    DetectionState,
                    () => _settings?.ProfileStore?.CurrentProfile?.Ab9,
                    () => IsShuttingDown);
                _ab9Worker.Start();

                // mBooster Pedals registry — multi-device owner. Refresh() is
                // called from the reconnect timer alongside TryConnectAb9. Each
                // detected mBooster spawns its own controller + 50 Hz worker.
                _mboosterRegistry = new MozaMBoosterRegistry(
                    _data,
                    settingsLookup: id => GetOrCreateMBoosterSettings(id),
                    isShuttingDown: () => IsShuttingDown,
                    onDeviceDetectedEdge: OnMBoosterDeviceDetected);
                // Initial walk so any mBooster plugged in BEFORE SimHub launched
                // appears immediately — without this, the user waits up to 5 s
                // for the reconnect timer to fire.
                try { _mboosterRegistry.Refresh(); }
                catch (Exception ex) { MozaLog.Debug($"[Moza/mBooster] Initial refresh: {ex.Message}"); }

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
                    if (!_connection.IsConnected) return;
                    try { PendingResponses.TickRetransmits(_connection.Send); }
                    catch (Exception ex) { MozaLog.Warn($"[Moza] PendingResponseTracker tick failed: {ex.Message}"); }
                };
                _retryTimer.AutoReset = true;
                _retryTimer.Start();

                _reconnectTimer = new Timer(5000);
                _reconnectTimer.Elapsed += (s, e) =>
                {
                    if (IsShuttingDown) return;
                    if (!_connection.IsConnected)
                        TryConnect();
                    // AB9 probe is microseconds when registry is populated.
                    // On Wine/Proton (no registry) the fallback would scan
                    // every wine COM symlink and lock up SimHub; suppress when
                    // registry is empty. DisableAb9Detection wins regardless.
                    bool registryHasMoza =
                        Protocol.MozaPortDiscovery.Instance.Enumerate().Count > 0;
                    if (!_settings.DisableAb9Detection
                        && registryHasMoza
                        && !_ab9Manager.IsConnected)
                        TryConnectAb9();

                    // Slice I: reconnect-timer mBooster Refresh re-enabled.
                    try { _mboosterRegistry?.Refresh(); }
                    catch (Exception ex) { MozaLog.Debug($"[Moza/mBooster] Refresh: {ex.Message}"); }
                };
                _reconnectTimer.AutoReset = true;
                if (_settings.ConnectionEnabled)
                    _reconnectTimer.Start();

                _hidReader = new MozaHidReader(_data);
                // Slice G: HID event subscription re-enabled.
                if (_mboosterRegistry != null)
                {
                    _hidReader.MBoosterAxisChanged += (identity, pos01) =>
                    {
                        try { _mboosterRegistry.OnHidAxisUpdate(identity, pos01); }
                        catch (Exception ex) { MozaLog.Debug($"[Moza/mBooster] HID dispatch: {ex.Message}"); }
                    };
                }
                _hidReader.Start();
                _propertyResolver = new SimHubPropertyResolver(_pluginManager, _data, _hidReader);
                _hardwareApplier = new HardwareApplier(this, _data, _deviceManager, _ab9Manager, DetectionState);
                _deviceProber = new DeviceProber(this, _connection, _deviceManager, _data, DetectionState);
                _dashboardBindingCoordinator = new DashboardBindingCoordinator(this, _data, _connection, DetectionState);

                // Now safe to initialize the profile system — ApplyProfile (called
                // by AutoApplyProfile on the initially selected game's profile)
                // delegates to _hardwareApplier which is now constructed.
                InitProfileSystem();

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
                        "[Moza] Reusing persistent telemetry sender from prior plugin instance " +
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
                // Propagate the hot-renegotiation feature flag from settings.
                // Reading from settings here (rather than via a callback) is
                // fine because the flag is JSON-ignored and only set
                // programmatically at runtime — see MozaPluginSettings.
                _telemetrySender.EnableHotRenegotiation = _settings.EnableHotRenegotiation;
                MozaLog.Info(
                    $"[Moza] Hot re-negotiation feature flag: " +
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
                    MarkStandaloneDashboardDetectedFromUsb("init");

                // Third-party SDK emulation. Two independent toggles:
                //   - SdkEmulationEnabled gates the CoAP server (40266) and
                //     the name-impersonation stub the official MOZA SDK DLL
                //     looks for in process enumeration.
                //   - UdpControlEnabled gates the plain-UDP-CBOR control
                //     surface (40288) third-party wheel-config tools use.
                // Either or both can be on. Each start path catches its own
                // failures so one bad port doesn't take the other down.
                // Toggling either requires a plugin restart for the change
                // to take effect (matches the description shown in the UI).
                if (_settings.SdkEmulationEnabled)
                {
                    try
                    {
                        // Reuse the persistent stub manager from a prior
                        // plugin instance if its child process is still
                        // alive. Avoids Stop+Restart thrash (registry
                        // re-redirect, CreateProcess, AssignProcessToJobObject)
                        // on every game switch — and sidesteps the Wine/Proton
                        // teardown hang that wedges Stop() between registry
                        // restore and Process.Kill / JobObject.Dispose.
                        if (s_persistentSdkStubManager != null
                            && s_persistentSdkStubManager.IsRunning)
                        {
                            _sdkStubManager = s_persistentSdkStubManager;
                            MozaLog.Info(
                                "[Sdk] Reusing persistent CoAP stub from prior plugin instance " +
                                $"(status={_sdkStubManager.Status})");
                        }
                        else
                        {
                            // Persistent reference exists but the child is gone
                            // (crashed / killed externally). Tear down the husk
                            // before allocating a fresh manager so its job-handle
                            // / process-handle don't leak. Bounded so a Wine-side
                            // JobObject.Dispose wedge can't block Init.
                            if (s_persistentSdkStubManager != null)
                            {
                                try { s_persistentSdkStubManager.TryStop(1500); } catch { }
                                s_persistentSdkStubManager = null;
                            }
                            _sdkStubManager = new Sdk.CoapStubManager();
                            _sdkStubManager.Start();
                            s_persistentSdkStubManager = _sdkStubManager;
                        }
                        // SDK server holds refs to _data + _hardwareApplier
                        // (both per-instance), so it MUST be recreated each
                        // Init — it cannot be persistent like the stub manager.
                        _sdkServer = new Sdk.MozaSdkCoapServer(_data, _hardwareApplier);
                        _sdkServer.Start();
                        MozaLog.Info("[Sdk] CoAP SDK server enabled");
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Error($"[Sdk] Failed to start CoAP SDK server: {ex.Message}");
                        try { _sdkServer?.Stop(); } catch { /* swallow */ }
                        // Don't Stop() the stub manager from this catch — it
                        // may be the persistent one and a Wine-side Stop()
                        // hang is exactly the failure we're avoiding. Leave
                        // it running; the next Init re-evaluates via IsRunning.
                        _sdkServer = null;
                        _sdkStubManager = null;
                    }
                }
                else
                {
                    // SDK emulation toggled OFF. If a prior session left a
                    // persistent stub running, stop it now so the registry
                    // redirect doesn't outlive the user's intent. Bounded —
                    // a Wine-side wedge in Stop()/JobObject.Dispose can't
                    // block Init; JobObject's KILL_ON_JOB_CLOSE backstops
                    // the child cleanup on process exit if Stop() times out.
                    if (s_persistentSdkStubManager != null)
                    {
                        try { s_persistentSdkStubManager.TryStop(1500); } catch { }
                        s_persistentSdkStubManager = null;
                    }
                }

                if (_settings.UdpControlEnabled)
                {
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
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] Init failed: {ex}");
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
            try { _pollTimer?.Stop(); } catch { }
            try { _retryTimer?.Stop(); } catch { }
            try { _reconnectTimer?.Stop(); } catch { }
            try { _saveDebounceTimer?.Stop(); } catch { }

            bool ownConnection = _connection != null && !ReferenceEquals(_connection, s_persistentConnection);
            bool ownTelemetrySender = _telemetrySender != null && !ReferenceEquals(_telemetrySender, s_persistentTelemetrySender);

            if (ownTelemetrySender)
            {
                try { _telemetrySender?.Stop(); } catch { }
            }

            // Halt the AB9 engine-vib worker before disposing the AB9 manager.
            try { _ab9Worker?.Stop(); _ab9Worker = null; } catch { }
            // Dispose every mBooster controller — same reason: stop workers
            // before the connections they own get torn down.
            try { _mboosterRegistry?.Dispose(); _mboosterRegistry = null; } catch { }

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
                if (_subscribedProfileStore != null)
                {
                    _subscribedProfileStore.CurrentProfileChanged -= OnProfileChanged;
                    _subscribedProfileStore = null;
                }
            }
            catch { }
            try { _deviceManager?.Dispose(); } catch { }
            try { _hidReader?.Dispose(); } catch { }
            if (ownTelemetrySender)
            {
                try { _telemetrySender?.Dispose(); } catch { }
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
            try { _pollTimer?.Dispose(); } catch { }
            try { _retryTimer?.Dispose(); } catch { }
            try { _reconnectTimer?.Dispose(); } catch { }
            try { _saveDebounceTimer?.Dispose(); } catch { }
            _saveDebounceTimer = null;

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

        // Fire a one-shot base-gearshift-event on gear change. Gated by
        // GearshiftVibration > 0 and a debounce. By default, transitions
        // *into* neutral don't fire (H-pattern produces two transitions
        // "1"→"N"→"2"; we want the engagement bump only).
        // GearshiftVibrateOnNeutral opts in.
        private void CheckGearshiftEvent(GameData data)
        {
            if (!_data.IsConnected) return;
            if (_data.GearshiftVibration <= 0) return;
            string? gear = data?.NewData?.Gear;
            if (string.IsNullOrEmpty(gear)) return;
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
            _telemetrySender?.UpdateGameData(data.NewData);
            _telemetrySender?.SetGameRunning(data.GameRunning);
            CheckGearshiftEvent(data);
            CheckAb9GearshiftEvent(data);

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
                // ABSActive is SimHub's loosely-typed property — games supply
                // bool / int / sbyte / byte / short / long depending on backend.
                // Pattern-match the common shapes to skip Convert.ToInt32's
                // InvariantCulture lookup and the try/catch on the hot path
                // (DataUpdate runs at SimHub's data rate, ~60Hz+). Unknown
                // types fall through to false — same observable behaviour as
                // the catch-and-default that lived here previously.
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
                double vehicleMs = (nd?.SpeedKmh ?? 0.0) / 3.6;
                double avgWheelMs = 0.0;
                double idleRpm = 800.0;
                var snap = new MBoosterTelemetrySnapshot(
                    gameRunning: data.GameRunning,
                    rpm: rpm,
                    idleRpm: idleRpm,
                    brake: brake01,
                    absActive: absActive,
                    vehicleSpeedMs: vehicleMs,
                    avgWheelSpeedMs: avgWheelMs);
                _mboosterRegistry.OnDataUpdate(snap);
            }
        }

        /// <summary>
        /// Look up (or lazily create) the per-device mBooster settings entry
        /// in the current profile. Called by the registry and the effect
        /// worker on every tick — must be allocation-free for known devices.
        /// </summary>
        internal MBoosterDeviceSettings GetOrCreateMBoosterSettings(string identity)
        {
            var profile = _settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return new MBoosterDeviceSettings();
            if (profile.MBoosterSettings == null)
                profile.MBoosterSettings = new Dictionary<string, MBoosterDeviceSettings>(StringComparer.OrdinalIgnoreCase);
            if (!profile.MBoosterSettings.TryGetValue(identity, out var s) || s == null)
            {
                s = new MBoosterDeviceSettings();
                profile.MBoosterSettings[identity] = s;
            }
            return s;
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
                MozaLog.Info($"[Moza/mBooster] Applying settings for {MBoosterDeviceController.ShortIdentity(controller.Identity)} (experimental calibration surface)");
                var s = GetOrCreateMBoosterSettings(controller.Identity);
                ApplyMBoosterToHardware(controller, s);
                // Always issue a calibration read burst on detect so the panel
                // can populate (or so we learn the device ignored them).
                controller.RequestCalibrationReads();
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza/mBooster] OnDetected for {controller.Identity}: {ex.Message}");
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
            // Direction / min / max — write only if the user has set them.
            if (s.Direction >= 0)
            {
                // Direction is a per-pedal field on real Moza pedals; on
                // mBooster (single-axis) we route through the throttle dir
                // slot. Brake/clutch dirs are reserved for symmetry but
                // unlikely to be wired on a single-axis unit.
                controller.SendIntWrite("mbooster-throttle-dir", s.Direction);
            }
            if (s.Min >= 0) controller.SendIntWrite("mbooster-throttle-min", s.Min);
            if (s.Max >= 0) controller.SendIntWrite("mbooster-throttle-max", s.Max);
            if (s.CurveY != null && s.CurveY.Length == 5)
            {
                controller.SendFloatWrite("mbooster-throttle-y1", s.CurveY[0]);
                controller.SendFloatWrite("mbooster-throttle-y2", s.CurveY[1]);
                controller.SendFloatWrite("mbooster-throttle-y3", s.CurveY[2]);
                controller.SendFloatWrite("mbooster-throttle-y4", s.CurveY[3]);
                controller.SendFloatWrite("mbooster-throttle-y5", s.CurveY[4]);
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
            MozaLog.Info("[Moza] Shutting down plugin");

            // 1. Stop timers first so no new callbacks fire against disposed state.
            _saveDebounceTimer?.Stop();
            _pollTimer?.Stop();
            _retryTimer?.Stop();
            _reconnectTimer?.Stop();

            // Stop the AB9 engine-vib worker before the AB9 manager / connection
            // are disposed; the tick gates on connection state but joining here
            // keeps shutdown deterministic.
            try { _ab9Worker?.Stop(); _ab9Worker = null; } catch { }

            // Burst silent-slot frames + an engine-pulse OFF to stop the AB9
            // effect immediately on shutdown. Without this the firmware keeps
            // the last-streamed buzz running until its ~10 s keepalive timeout,
            // which means users hear vibrations for ten seconds after closing
            // SimHub mid-session. Worker is stopped above so this can't race.
            try { _ab9Manager?.SendEngineSilence(); } catch { }

            // Dispose every mBooster controller — fires disable frames + closes
            // each connection. Must happen before MozaData is torn down so the
            // position-merge path (which writes to _data) doesn't race.
            try { _mboosterRegistry?.Dispose(); _mboosterRegistry = null; } catch { }

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
            try
            {
                if (_subscribedProfileStore != null)
                    _subscribedProfileStore.CurrentProfileChanged -= OnProfileChanged;
                _subscribedProfileStore = null;
            }
            catch { }

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
            if (!keepWireAlive)
            {
                _telemetrySender?.Dispose();
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
                    "[Moza] End: keeping persistent wire (connection + telemetry sender) alive " +
                    "across plugin reload — wheel sessions remain open, no settle wait on next Init");
            }
            try
            {
                if (_ab9Manager != null)
                    _ab9Manager.MessageReceived -= OnAb9MessageReceived;
            }
            catch { }
            _ab9Manager?.Dispose();

            // 7. Dispose timers after I/O is gone.
            _saveDebounceTimer?.Dispose();
            _saveDebounceTimer = null;
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
                try { MozaLog.Warn($"[Moza] ProcessExit handler registration failed: {ex.Message}"); } catch { }
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
                        try { MozaLog.Warn($"[Moza] ProcessExit Stop(): {ex.GetType().Name}: {ex.Message}"); } catch { }
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

        internal MozaHidReader HidReader => _hidReader;

        internal void SaveSettings()
        {
            // Resolve the current dashboard key (wheel:<id> > file:<...> > builtin:<name>)
            // so the active SimHub profile records which dashboard the user picked.
            // Re-applied on profile load so each game keeps its own dashboard selection.
            string? activeDashKey = null;
            try
            {
                var cands = GetActiveDashboardKeyCandidates();
                if (cands.Count > 0) activeDashKey = cands[0];
            }
            catch { /* candidate resolver is conservative; ignore early-init errors */ }
            _settings.ProfileStore?.CurrentProfile?.CaptureFromCurrent(_settings, _data, activeDashKey);
            // Single source of truth = profile + overlay. UI handlers write
            // overlay/profile directly; CaptureFromCurrent picks up device-read
            // state. No more legacy slot/UID mirror.
            ScheduleSave();
        }

        internal void PersistSettings()
        {
            ScheduleSave();
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
                if (DateTime.UtcNow - _settings.LastUpdateCheckUtc < TimeSpan.FromHours(24))
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

        // Trace log helper — emit the active wheel page's sleep bundle state
        // so we can correlate disk-write contents with what the user reported.
        // Cheap (single string format) and only fires at save points, not per-tick.
        private void LogSleepBundleStateForSaveTrace(string trigger)
        {
            try
            {
                var g = GetCurrentWheelPageGuid();
                if (!g.HasValue) { MozaLog.Debug($"[Moza] SLEEP-TRACE [{trigger}]: page guid unresolvable"); return; }
                var dict = _settings?.WheelSleepByPageGuid;
                if (dict == null || !dict.TryGetValue(g.Value, out var b) || b == null)
                {
                    MozaLog.Debug($"[Moza] SLEEP-TRACE [{trigger}]: page={g.Value.ToString().Substring(0,8)} bundle=null");
                    return;
                }
                MozaLog.Info($"[Moza] SLEEP-TRACE [{trigger}]: page={g.Value.ToString().Substring(0,8)} Mode={b.Mode} TimeoutMin={b.TimeoutMin} SpeedMs={b.SpeedMs}");
            }
            catch (Exception ex) { MozaLog.Debug($"[Moza] SLEEP-TRACE failed: {ex.Message}"); }
        }

        private readonly object _saveDebounceLock = new object();

        /// <summary>
        /// Debounce disk writes: restart a 500ms timer on each call.
        /// Prevents dozens of writes per second during rapid slider drags.
        /// </summary>
        internal void ScheduleSave()
        {
            // Lazy-create under a lock — concurrent callers (UI thread + profile-change
            // thread) would otherwise both see null, each create a Timer, and the loser's
            // instance would leak (unstopped, unwatched, still referencing _settings).
            lock (_saveDebounceLock)
            {
                if (_saveDebounceTimer == null)
                {
                    _saveDebounceTimer = new Timer(500) { AutoReset = false };
                    _saveDebounceTimer.Elapsed += (s, e) =>
                    {
                        LogSleepBundleStateForSaveTrace("debounced-save");
                        this.SaveCommonSettings("MozaPluginSettings", _settings);
                    };
                }
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }
        }

        internal void ClearSettings()
        {
            _telemetrySender?.Stop();
            _settings = new MozaPluginSettings();
            this.SaveCommonSettings("MozaPluginSettings", _settings);
            InitProfileSystem();
        }

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
                MozaLog.Info("[Moza] Connection enabled");
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
                _ab9Manager?.Disconnect();
                if (_telemetrySender != null)
                {
                    _telemetrySender.DetectedDeviceMask = 0;
                }
                _deviceManager.ResetWheelDetection();
                Interlocked.Exchange(ref _telemetryStartRequested, 0);
                DetectionState.WheelPollMisses = 0;
                DetectionState.LastKnownWheelModel = "";
                MozaLog.Info("[Moza] Connection disabled");
            }
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            // The WPF UI thread predates plugin Init, so the CurrentUICulture
            // we assigned in Init lives on a different thread. Re-apply it here
            // (we are on the UI thread) so that {x:Static res:Strings.X} bindings
            // in SettingsControl.xaml resolve against the user's choice rather
            // than the default thread culture.
            // The WPF UI thread predates plugin Init, so the CurrentUICulture
            // we assigned in Init lives on a different thread. Re-apply it here
            // (we are on the UI thread) so that {x:Static res:Strings.X} bindings
            // in SettingsControl.xaml resolve against the resolved language
            // rather than the default thread culture.
            var c = LanguageResolver.Resolve(_settings?.PreferredLanguage);
            if (!Thread.CurrentThread.CurrentUICulture.Equals(c))
            {
                MozaLog.Info($"[Moza] GetWPFSettingsControl: switching UI thread culture from " +
                             $"'{Thread.CurrentThread.CurrentUICulture.Name}' to '{c.Name}' " +
                             $"(PreferredLanguage='{_settings?.PreferredLanguage ?? "<auto>"}')");
                Thread.CurrentThread.CurrentUICulture = c;
            }
            return new SettingsControl(this);
        }

        private void RegisterProperties(PluginManager pluginManager)
        {
            // Null-guard each delegate: SimHub may invoke property getters during
            // plugin reload windows where _data is unset, or after End() left fields
            // intact but mid-teardown. A throw inside a property getter destabilises
            // SimHub's property polling, so each getter returns a sentinel default.
            this.AttachDelegate("Moza.BaseConnected", () => _data?.IsBaseConnected ?? false);
            this.AttachDelegate("Moza.McuTemp", () => _data == null ? 0.0 : _propertyResolver.ConvertTemp(_data.McuTemp));
            this.AttachDelegate("Moza.MosfetTemp", () => _data == null ? 0.0 : _propertyResolver.ConvertTemp(_data.MosfetTemp));
            this.AttachDelegate("Moza.MotorTemp", () => _data == null ? 0.0 : _propertyResolver.ConvertTemp(_data.MotorTemp));
            this.AttachDelegate("Moza.BaseState", () => _data?.BaseState ?? 0);
            this.AttachDelegate("Moza.FfbStrength", () => (_data?.FfbStrength ?? 0) / 10);
            this.AttachDelegate("Moza.MaxAngle", () => (_data?.MaxAngle ?? 0) * 2);
        }

        private void RegisterActions()
        {
            this.AddAction("Moza.ClearLeds", (a, b) =>
            {
                ClearLedsOnHardware();
                MozaLog.Debug("[Moza] LEDs cleared via action");
            });
        }

        /// <summary>
        /// Send all-off to wheel and dash LEDs via device manager.
        /// </summary>
                // ClearLedsOnHardware moved to HardwareApplier; shim further down.

        // ===== Telemetry =====

        internal TelemetrySender? TelemetrySender => _telemetrySender;

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
        internal void UpdateActiveChannelMapping(string channelUrl, string propertyPath)
        {
            var profile = _telemetrySender?.Profile;
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

        /// <summary>Set or clear a per-channel SimHub property override for the current wheel + dashboard.</summary>
        internal void SetChannelMapping(string channelUrl, string propertyPath)
        {
            if (string.IsNullOrEmpty(channelUrl)) return;
            var candidates = GetActiveDashboardKeyCandidates();
            if (candidates.Count == 0) return;
            string dashKey = candidates[0]; // write to the highest-priority key

            // Profile × page × dashboard × channel → SimHub property path.
            var middle = GetOrCreateActiveChannelMappings();
            if (middle == null) return; // no profile/wheel resolvable yet

            if (!middle.TryGetValue(dashKey, out var inner))
            {
                inner = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                middle[dashKey] = inner;
            }

            string trimmed = (propertyPath ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                inner.Remove(channelUrl);
                // Tidy: drop empty inner dict, then empty middle dict, so the JSON
                // doesn't accumulate empty objects after every reset-to-default.
                if (inner.Count == 0) middle.Remove(dashKey);
            }
            else
            {
                inner[channelUrl] = trimmed;
            }

            // Live-rewire the active profile's matching channel so the next
            // frame uses the new property. No tier-def restart — we already
            // negotiated the wire format with the wheel; we're just changing
            // where the host pulls each channel's value from.
            UpdateActiveChannelMapping(channelUrl, trimmed);

            SaveSettings();
        }

        /// <summary>Clear all per-channel overrides for the current wheel + dashboard, across every candidate key.</summary>
        internal void ClearCurrentDashboardMappings()
        {
            var candidates = GetActiveDashboardKeyCandidates();
            if (candidates.Count == 0) return;
            var middle = GetActiveChannelMappings();
            if (middle == null) return;

            bool changed = false;
            foreach (var key in candidates)
            {
                if (middle.Remove(key)) changed = true;
            }
            if (changed) SaveSettings();
        }

        // Dashboard binding state moved to DashboardBindingCoordinator.
        internal bool IsPendingDashboardApply => _dashboardBindingCoordinator?.IsPendingDashboardApply ?? false;
        internal string? PendingDashboardApplyDescription => _dashboardBindingCoordinator?.PendingDashboardApplyDescription;

        private void TryConnect()
        {
            if (Interlocked.CompareExchange(ref _connectingFlag, 1, 0) != 0)
                return;

            try
            {
                // If we had a wheel detected before reconnecting, reset it.
                // The serial port may have dropped during a wheel swap.
                if (DetectionState.NewWheelDetected || DetectionState.OldWheelDetected)
                    ResetWheelDetection("Serial reconnecting — resetting wheel detection");

                if (_connection.Connect())
                {
                    _unmatched = 0;
                    // Drop any firmware-debug chatter captured from a prior
                    // (possibly different) wheel — the diagnostics tab should
                    // only show what THIS connection has produced.
                    _firmwareDebugLog.Clear();
                    MozaLog.Info("[Moza] Connected to MOZA device");
                    MarkStandaloneDashboardDetectedFromUsb("serial connect");
                    _deviceManager.ReadSettings(StatusPollCommands);
                    _deviceManager.ProbeWheelDetection();
                    _deviceManager.ReadSetting("dash-rpm-indicator-mode");
                    _deviceManager.ReadSetting("handbrake-direction");
                    _deviceManager.ReadSetting("pedals-throttle-dir");
                    _deviceManager.ReadSetting("hub-port1-power");

                    // Persist successful port for next launch
                    var port = _connection.LastPortName;
                    if (!string.IsNullOrEmpty(port) && _settings.LastWheelbasePort != port)
                    {
                        _settings.LastWheelbasePort = port!;
                        ScheduleSave();
                    }
                }
                else if (!string.IsNullOrEmpty(_settings.LastWheelbasePort)
                         && string.IsNullOrEmpty(_connection.LastPortName))
                {
                    // Connect() cleared the cached port (stale / wrong
                    // device after USB port change). Wipe the persisted
                    // setting so we don't repeat the stale-port check on
                    // every reconnect tick.
                    MozaLog.Info(
                        $"[Moza] Cleared stale saved port {_settings.LastWheelbasePort}");
                    _settings.LastWheelbasePort = "";
                    ScheduleSave();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _connectingFlag, 0);
            }
        }

        /// <summary>
        /// Flip dashboard detection on USB-PID alone when the open port is a
        /// Moza dashboard PID (CM2 = 0x0025). Idempotent. On rising edge
        /// deploys the CM2 device.json, reads CM2 settings, applies the
        /// current dash profile, and asks the binding coordinator to apply
        /// telemetry settings + start the sender. Each phase is wrapped in
        /// try/catch so a single failed phase doesn't abort the rest of the
        /// detection sequence. Called from <see cref="Init"/> (covers
        /// persistent-connection reuse and reload-without-restart) and from
        /// <see cref="TryConnect"/> (covers normal first-connect).
        /// </summary>
        private bool MarkStandaloneDashboardDetectedFromUsb(string reason)
        {
            if (!IsStandaloneDashboardUsbConnection)
                return false;

            bool rising = !DetectionState.DashDetected;
            DetectionState.DashDetected = true;
            _data.IsDashboardConnected = true;

            if (DeviceDefinitionDeployer.DeployDashboard(_connection.DiscoveredPid))
                DeviceDefinitionDeployed = true;

            if (rising)
            {
                MozaLog.Info(
                    $"[Moza] Standalone dashboard detected from USB PID " +
                    $"{_connection.DiscoveredPid} ({MozaUsbIds.Describe(_connection.DiscoveredPid)}; {reason})");
                try { _deviceManager.ReadSettings(Devices.DeviceProber.DashSettingsReadCommands); }
                catch (Exception ex) { MozaLog.Debug($"[Moza] Standalone dashboard settings probe skipped: {ex.Message}"); }
            }

            try { ApplyDashToHardware(_settings?.ProfileStore?.CurrentProfile); }
            catch (Exception ex) { MozaLog.Debug($"[Moza] Standalone dashboard profile apply skipped: {ex.Message}"); }

            try
            {
                ApplyTelemetrySettings();
                StartTelemetryIfReady();
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Moza] Standalone dashboard telemetry start skipped: {ex.Message}");
            }

            return true;
        }

        /// <summary>Open the AB9 shifter's dedicated CDC port (PID 0x1000) and probe identity.</summary>
        private void TryConnectAb9()
        {
            if (_ab9Manager == null) return;
            if (DetectionState.Ab9Detected)
            {
                // Connection dropped after a successful detection — clear so the
                // next read response can re-trigger profile push.
                DetectionState.Ab9Detected = false;
            }
            if (_ab9Manager.TryConnect())
            {
                _ab9Manager.SendIdentityProbe();
                _ab9Manager.RequestAllStoredSettings();

                // Persist successful port for next launch
                var port = _ab9Manager.Connection.LastPortName;
                if (!string.IsNullOrEmpty(port) && _settings.LastAb9Port != port)
                {
                    _settings.LastAb9Port = port!;
                    ScheduleSave();
                }
            }
            else if (!string.IsNullOrEmpty(_settings.LastAb9Port)
                     && string.IsNullOrEmpty(_ab9Manager.Connection.LastPortName))
            {
                MozaLog.Info(
                    $"[Moza/AB9] Cleared stale saved port {_settings.LastAb9Port}");
                _settings.LastAb9Port = "";
                ScheduleSave();
            }
        }

        private const int WheelMissThreshold = 3;

        // Fires from MozaSerialConnection.HandleIoFailure on the read or
        // write thread once the port has been force-closed. Pause telemetry
        // and reset wheel detection right now rather than waiting for the
        // next reconnect-timer tick — otherwise the sender keeps firing and
        // accumulating ack waiters / catalog state for ~5 s.
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
            MozaLog.Debug($"[Moza] {reason}");
            _telemetrySender?.Stop();
            // Preserve dash detection when this serial connection is a
            // standalone dashboard (CM2). Wheel hot-swap shouldn't blank a
            // CM2's detection — the dash is the connection, not a wheel
            // peripheral. ResetWheel() clears DashDetected unconditionally,
            // so re-assert it for standalone-USB dashboards.
            bool preserveStandaloneDash = IsStandaloneDashboardUsbConnection;
            DetectionState.ResetWheel();
            if (preserveStandaloneDash)
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

        private void PollStatus(object sender, ElapsedEventArgs e)
        {
            if (IsShuttingDown) return;
            if (!_connection.IsConnected) return;

            _dashboardBindingCoordinator?.TickPendingDashboardRetry();

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
                _deviceManager.ReadSetting("wheel-model-name");

                // Probe other wheel IDs for hot-swap detection.
                // Handles ES → new-protocol case where the base keeps responding
                // on the locked ID (19) so miss counter never fires.
                _deviceManager.ProbeOtherWheelIds();
            }

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
            if (!DetectionState.HubDetected)
                _deviceManager.ReadSetting("hub-port1-power");

            // Re-probe display sub-device until fully identified — initial probe
            // can race power-up and return only partial identity. Skip for wheels
            // we already know have no display: the probe sends 11 frames on the
            // dashboard session group (0x43 dev=0x17), and screenless wheels
            // (CS V2.1 / KS / GS V2P / TSW / RS V2 / "CS") may interpret those as
            // dashboard-pipeline traffic and stop servicing settings reads.
            // For unknown wheels (HasDisplay==null) the re-probe still runs.
            if (DetectionState.NewWheelDetected
                && !IsDisplayDetected
                && WheelModelInfo?.HasDisplay != false)
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
            const long DisplayWedgeTimeoutMs = 60_000;
            if (!DisplayWedgeRecoveryFired
                && (DetectionState.NewWheelDetected || DetectionState.OldWheelDetected)
                && WheelModelInfo?.HasDisplay != false
                && !IsDisplayDetected
                && _wheelDetectedUtcTicks != 0)
            {
                long elapsedMs = (DateTime.UtcNow.Ticks - _wheelDetectedUtcTicks)
                    / TimeSpan.TicksPerMillisecond;
                if (elapsedMs >= DisplayWedgeTimeoutMs)
                {
                    DisplayWedgeRecoveryFired = true;
                    var hasDisplayStr = WheelModelInfo?.HasDisplay?.ToString() ?? "unknown";
                    MozaLog.Warn(
                        $"[Moza] Display sub-device wedge: wheel detected " +
                        $"{elapsedMs}ms ago (HasDisplay={hasDisplayStr}) but " +
                        "display has not responded. Forcing serial disconnect — " +
                        "reconnect timer (5 s) will reopen the port and give the " +
                        "wheel's USB stack a chance to re-enumerate. " +
                        "If this recurs, the display firmware is likely stuck; " +
                        "physically detaching the wheel and reattaching is the next step.");
                    try { _connection?.Disconnect(); } catch { }
                }
            }

            // Read Group 3 ring LED colors once after group detected + model resolved
            if (!DetectionState.Group3ColorsRead && DetectionState.NewWheelDetected && IsWheelLedGroupPresent(3))
            {
                var model = WheelModelInfo;
                if (model?.KnobRingLeds != null && model.KnobRingLedTotal > 0)
                {
                    DetectionState.Group3ColorsRead = true;
                    int total = model.KnobRingLedTotal;
                    var cmds = new string[total + 1];
                    cmds[0] = "wheel-knob-brightness";
                    for (int i = 0; i < total; i++)
                        cmds[i + 1] = $"wheel-knob-bg-color{i + 1}";
                    _deviceManager.ReadSettingsPaced(cmds);
                    MozaLog.Debug($"[Moza] Reading knob ring LED colors ({total} LEDs)");
                }
            }

            // Poll hub port status while hub is connected (read-only, no settings to save)
            if (DetectionState.HubDetected)
                _deviceManager.ReadSettings(DeviceProber.HubReadCommands);
        }

        private volatile int _unmatched;

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
                MozaLog.Debug(
                    $"[Moza] firmware-debug src={(rawDeviceId == 0x21 ? "main" : rawDeviceId == 0x71 ? "wheel" : rawDeviceId == 0xB1 ? "display" : $"0x{rawDeviceId:X2}")}: {text}");
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
                    MozaLog.Debug(
                        $"[Moza] Unmatched #{_unmatched}: rawGroup=0x{data[0]:X2} group=0x{grp:X2} " +
                        $"rawDev=0x{data[1]:X2} dev={dev} len={data.Length} " +
                        $"payload={BitConverter.ToString(data, 2, Math.Min(data.Length - 2, 8))}");
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
                    MozaLog.Debug($"[Moza] Base identity: {baseName}");
                }
                return;
            }

            _data.UpdateFromCommand(r.Name, r.IntValue);
            if (r.ArrayValue != null)
                _data.UpdateFromArray(r.Name, r.ArrayValue);

            // Persist wheel-reported sleep-bundle values so next launch reapplies them.
            SeedSleepBundleFromResponse(r);

            // Extended LED group presence: any response from a group proves it exists.
            if (r.Name != null)
            {
                int g = -1;
                if (r.Name.StartsWith("wheel-single-",  StringComparison.Ordinal)) g = 2;
                else if (r.Name.StartsWith("wheel-knob-",    StringComparison.Ordinal)) g = 3;
                else if (r.Name.StartsWith("wheel-ambient-", StringComparison.Ordinal)) g = 4;
                if (g >= 2 && g <= 4 && DetectionState.TrySetWheelLedGroupPresent(g))
                    MozaLog.Debug($"[Moza] Wheel LED group {g} detected");
            }

            _deviceManager.MarkWheelResponse(r.DeviceId);
            if (r.Name != null)
                _deviceProber.DetectDevices(r.Name, r.IntValue, r.DeviceId);
        }

        /// <summary>
        /// Dispatch an empty presence-probe ACK to the first-sight detection
        /// helper for the matching sub-device. Only handles devices probed
        /// via <see cref="MozaDeviceManager.SendPresenceProbe"/> from
        /// PollStatus — currently dash / handbrake / pedals. Other device IDs
        /// (e.g. wheel-on-base ACKs, AB9, Booster) reach this path harmlessly
        /// and are ignored.
        /// </summary>
        private void OnPresenceProbeAck(byte deviceId)
        {
            switch (deviceId)
            {
                case MozaProtocol.DeviceDash:
                    _deviceProber.MarkDashDetected();
                    break;
                case MozaProtocol.DeviceHandbrake:
                    _deviceProber.MarkHandbrakeDetected();
                    break;
                case MozaProtocol.DevicePedals:
                    _deviceProber.MarkPedalsDetected();
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
                catch (Exception ex) { MozaLog.Warn($"[Moza/AB9] FFB init failed: {ex.Message}"); }
                ApplyAb9ToHardware(_settings?.ProfileStore?.CurrentProfile);
            }

            MozaLog.Debug($"[Moza/AB9] {r.Name} = {r.IntValue}");
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
        private Guid? GetCurrentWheelPageGuid()
        {
            var modelName = _data?.WheelModelName;
            if (string.IsNullOrEmpty(modelName)) return null;
            var prefix = WheelModelInfo.ExtractPrefix(modelName!);
            if (string.IsNullOrEmpty(prefix)) return null;
            var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
            if (!Guid.TryParse(guidStr, out var g)) return null;
            return g;
        }

        /// <summary>
        /// Look up the wheel overlay for the currently-connected wheel in the given
        /// profile. Returns null if either the page GUID can't be resolved or the
        /// overlay isn't present.
        /// </summary>
        internal WheelOverride? GetCurrentWheelOverlay(MozaProfile? profile)
        {
            if (profile == null) return null;
            var g = GetCurrentWheelPageGuid();
            if (!g.HasValue) return null;
            if (profile.WheelOverridesByPageGuid == null) return null;
            return profile.WheelOverridesByPageGuid.TryGetValue(g.Value, out var ov) ? ov : null;
        }

        /// <summary>
        /// Get or create the wheel overlay for the currently-connected wheel.
        /// Returns null only when the wheel hasn't identified itself yet.
        /// </summary>
        internal WheelOverride? GetOrCreateCurrentWheelOverlay(MozaProfile? profile)
        {
            if (profile == null) return null;
            var g = GetCurrentWheelPageGuid();
            if (!g.HasValue) return null;
            if (profile.WheelOverridesByPageGuid == null)
                profile.WheelOverridesByPageGuid = new Dictionary<Guid, WheelOverride>();
            if (!profile.WheelOverridesByPageGuid.TryGetValue(g.Value, out var ov) || ov == null)
            {
                ov = new WheelOverride();
                profile.WheelOverridesByPageGuid[g.Value] = ov;
            }
            return ov;
        }

        // Hardware-apply (Apply*ToHardware) and WriteIf* helpers live in HardwareApplier.

        /// <summary>
        /// Apply <paramref name="mutator"/> to the active wheel's overlay on the
        /// current profile. No-op if no profile is selected or no wheel is
        /// identified. Used by UI handlers to mirror their edits into the
        /// profile-scoped overlay alongside the legacy flat-field write during
        /// the R4 transition.
        /// </summary>
        internal void UpdateActiveWheelOverlay(Action<WheelOverride> mutator)
        {
            if (mutator == null) return;
            var profile = _settings?.ProfileStore?.CurrentProfile;
            var overlay = GetOrCreateCurrentWheelOverlay(profile);
            if (overlay == null) return;
            mutator(overlay);
        }

        /// <summary>
        /// Apply <paramref name="mutator"/> to the current profile (or no-op if
        /// no profile is selected). Used by UI handlers that own profile-level
        /// fields (motor/FFB/handbrake/pedals/dash/base-ambient).
        /// </summary>
        internal void UpdateActiveProfile(Action<MozaProfile> mutator)
        {
            if (mutator == null) return;
            var profile = _settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return;
            mutator(profile);
        }

        // ===== Active telemetry view — current wheel's overlay accessors =====
        // Returns "telemetry off" defaults when no wheel/profile yet.

        /// <summary>
        /// True iff telemetry is enabled for the current wheel page. Per-wheel-page
        /// (shared across profiles); reads return false when wheel not identified.
        /// </summary>
        internal bool ActiveTelemetryEnabled
        {
            get
            {
                var g = GetCurrentWheelPageGuid();
                if (!g.HasValue || _settings?.WheelTelemetryEnabledByPageGuid == null) return false;
                return _settings.WheelTelemetryEnabledByPageGuid.TryGetValue(g.Value, out var v) && v;
            }
            set
            {
                var g = GetCurrentWheelPageGuid();
                if (!g.HasValue) return;
                if (_settings == null) return;
                if (_settings.WheelTelemetryEnabledByPageGuid == null)
                    _settings.WheelTelemetryEnabledByPageGuid = new Dictionary<Guid, bool>();
                _settings.WheelTelemetryEnabledByPageGuid[g.Value] = value;
            }
        }

        /// <summary>Active wheel's dashboard profile name (cache key / builtin name). "" when unset.</summary>
        internal string ActiveTelemetryProfileName
        {
            get
            {
                var ov = GetCurrentWheelOverlay(_settings?.ProfileStore?.CurrentProfile);
                return ov?.TelemetryProfileName ?? "";
            }
            set
            {
                var ov = GetOrCreateCurrentWheelOverlay(_settings?.ProfileStore?.CurrentProfile);
                if (ov != null) ov.TelemetryProfileName = value ?? "";
            }
        }

        /// <summary>Active wheel's user-loaded .mzdash file path (empty = none).</summary>
        internal string ActiveTelemetryMzdashPath
        {
            get
            {
                var ov = GetCurrentWheelOverlay(_settings?.ProfileStore?.CurrentProfile);
                return ov?.TelemetryMzdashPath ?? "";
            }
            set
            {
                var ov = GetOrCreateCurrentWheelOverlay(_settings?.ProfileStore?.CurrentProfile);
                if (ov != null) ov.TelemetryMzdashPath = value ?? "";
            }
        }

        /// <summary>Mzdash folder for the current wheel page (shared across profiles).</summary>
        internal string ActiveTelemetryMzdashFolder
        {
            get
            {
                var g = GetCurrentWheelPageGuid();
                if (!g.HasValue || _settings?.WheelMzdashFolderByPageGuid == null) return "";
                return _settings.WheelMzdashFolderByPageGuid.TryGetValue(g.Value, out var folder)
                    ? folder ?? "" : "";
            }
            set
            {
                var g = GetCurrentWheelPageGuid();
                if (!g.HasValue) return;
                if (_settings == null) return;
                if (_settings.WheelMzdashFolderByPageGuid == null)
                    _settings.WheelMzdashFolderByPageGuid = new Dictionary<Guid, string>();
                _settings.WheelMzdashFolderByPageGuid[g.Value] = value ?? "";
            }
        }

        /// <summary>
        /// Sleep-light bundle for the current wheel page (shared across profiles).
        /// null means "leave the wheel's stored value alone".
        /// </summary>
        internal WheelSleepSettings? ActiveWheelSleep
        {
            get
            {
                var g = GetCurrentWheelPageGuid();
                if (!g.HasValue || _settings?.WheelSleepByPageGuid == null) return null;
                return _settings.WheelSleepByPageGuid.TryGetValue(g.Value, out var v) ? v : null;
            }
        }

        /// <summary>Get-or-create the per-page sleep bundle. Null only if no wheel identified.</summary>
        internal WheelSleepSettings? GetOrCreateActiveWheelSleep()
        {
            var g = GetCurrentWheelPageGuid();
            if (!g.HasValue || _settings == null) return null;
            if (_settings.WheelSleepByPageGuid == null)
                _settings.WheelSleepByPageGuid = new Dictionary<Guid, WheelSleepSettings>();
            if (!_settings.WheelSleepByPageGuid.TryGetValue(g.Value, out var bundle) || bundle == null)
            {
                bundle = new WheelSleepSettings();
                _settings.WheelSleepByPageGuid[g.Value] = bundle;
            }
            return bundle;
        }

        /// <summary>
        /// Idle effect/speed bundle for the current wheel page (shared across profiles).
        /// null means "leave the wheel's stored value alone".
        /// </summary>
        internal WheelIdleSettings? ActiveWheelIdle
        {
            get
            {
                var g = GetCurrentWheelPageGuid();
                if (!g.HasValue || _settings?.WheelIdleByPageGuid == null) return null;
                return _settings.WheelIdleByPageGuid.TryGetValue(g.Value, out var v) ? v : null;
            }
        }

        /// <summary>Get-or-create the per-page idle bundle. Null only if no wheel identified.</summary>
        internal WheelIdleSettings? GetOrCreateActiveWheelIdle()
        {
            var g = GetCurrentWheelPageGuid();
            if (!g.HasValue || _settings == null) return null;
            if (_settings.WheelIdleByPageGuid == null)
                _settings.WheelIdleByPageGuid = new Dictionary<Guid, WheelIdleSettings>();
            if (!_settings.WheelIdleByPageGuid.TryGetValue(g.Value, out var bundle) || bundle == null)
            {
                bundle = new WheelIdleSettings();
                _settings.WheelIdleByPageGuid[g.Value] = bundle;
            }
            return bundle;
        }

        /// <summary>
        /// Seed wheel-reported sleep-light + idle-effect/speed values into the
        /// per-page bundles. Only fills sentinel (-1/null) fields — user UI
        /// selections win. Without this, the wheel's current state is mirrored
        /// into _data but never persisted, so on the next launch the bundles
        /// are empty for unset fields and ApplyWheelToHardware leaves the
        /// wheel's mode/speed/color/idle-effect untouched even though we just
        /// observed them.
        /// </summary>
        private void SeedSleepBundleFromResponse(ParsedResponse r)
        {
            if (r.Name == null) return;
            switch (r.Name)
            {
                case "wheel-idle-mode":
                case "wheel-idle-timeout":
                case "wheel-idle-speed":
                case "wheel-idle-color":
                    SeedSleepBundleField(r);
                    return;
                case "wheel-telemetry-idle-effect":
                case "wheel-buttons-idle-effect":
                case "wheel-knob-idle-effect":
                case "wheel-telemetry-idle-interval":
                case "wheel-buttons-idle-interval":
                case "wheel-knob-idle-interval":
                    SeedIdleBundleField(r);
                    return;
            }
        }

        private void SeedSleepBundleField(ParsedResponse r)
        {
            var bundle = GetOrCreateActiveWheelSleep();
            if (bundle == null) return;
            bool changed = false;
            switch (r.Name)
            {
                case "wheel-idle-mode":
                    if (bundle.Mode < 0 && r.IntValue >= 0)
                    {
                        bundle.Mode = r.IntValue;
                        changed = true;
                    }
                    break;
                case "wheel-idle-timeout":
                    if (bundle.TimeoutMin < 0 && r.IntValue > 0)
                    {
                        MozaLog.Info($"[Moza] SLEEP-SEED: bundle.TimeoutMin {bundle.TimeoutMin} -> {r.IntValue} (from wheel response)");
                        bundle.TimeoutMin = r.IntValue;
                        changed = true;
                    }
                    else
                    {
                        MozaLog.Debug($"[Moza] SLEEP-SEED skipped: bundle.TimeoutMin={bundle.TimeoutMin}, wheel reported {r.IntValue}");
                    }
                    break;
                case "wheel-idle-speed":
                    // Payload [mode, ms_msb, ms_lsb] — store only the ms part to
                    // match the slider's single-value contract.
                    if (bundle.SpeedMs < 0 && r.ArrayValue != null && r.ArrayValue.Length >= 3)
                    {
                        int ms = (r.ArrayValue[1] << 8) | r.ArrayValue[2];
                        if (ms > 0)
                        {
                            bundle.SpeedMs = ms;
                            changed = true;
                        }
                    }
                    break;
                case "wheel-idle-color":
                    if (bundle.Color == null && r.ArrayValue != null && r.ArrayValue.Length >= 3)
                    {
                        int packed = (r.ArrayValue[0] << 16) | (r.ArrayValue[1] << 8) | r.ArrayValue[2];
                        bundle.Color = new[] { packed };
                        changed = true;
                    }
                    break;
            }
            if (changed) PersistSettings();
        }

        private void SeedIdleBundleField(ParsedResponse r)
        {
            var bundle = GetOrCreateActiveWheelIdle();
            if (bundle == null) return;
            bool changed = false;
            switch (r.Name)
            {
                case "wheel-telemetry-idle-effect":
                    if (bundle.TelemetryEffect < 0 && r.IntValue >= 0)
                    {
                        bundle.TelemetryEffect = r.IntValue;
                        changed = true;
                    }
                    break;
                case "wheel-buttons-idle-effect":
                    if (bundle.ButtonsEffect < 0 && r.IntValue >= 0)
                    {
                        bundle.ButtonsEffect = r.IntValue;
                        changed = true;
                    }
                    break;
                case "wheel-knob-idle-effect":
                    if (bundle.KnobEffect < 0 && r.IntValue >= 0)
                    {
                        bundle.KnobEffect = r.IntValue;
                        changed = true;
                    }
                    break;
                case "wheel-telemetry-idle-interval":
                case "wheel-buttons-idle-interval":
                case "wheel-knob-idle-interval":
                    // Payload [effect_id, ms_msb, ms_lsb] — store only the ms.
                    if (r.ArrayValue != null && r.ArrayValue.Length >= 3)
                    {
                        int ms = (r.ArrayValue[1] << 8) | r.ArrayValue[2];
                        if (ms > 0)
                        {
                            if (r.Name == "wheel-telemetry-idle-interval" && bundle.TelemetrySpeedMs < 0)
                            {
                                bundle.TelemetrySpeedMs = ms;
                                changed = true;
                            }
                            else if (r.Name == "wheel-buttons-idle-interval" && bundle.ButtonsSpeedMs < 0)
                            {
                                bundle.ButtonsSpeedMs = ms;
                                changed = true;
                            }
                            else if (r.Name == "wheel-knob-idle-interval" && bundle.KnobSpeedMs < 0)
                            {
                                bundle.KnobSpeedMs = ms;
                                changed = true;
                            }
                        }
                    }
                    break;
            }
            if (changed) PersistSettings();
        }

        /// <summary>
        /// Firmware era for the current wheel page. Returns Auto when wheel
        /// not identified — the auto-resolver picks from the wheel's response.
        /// </summary>
        internal MozaWheelEra ActiveTelemetryWheelEra
        {
            get
            {
                var g = GetCurrentWheelPageGuid();
                if (!g.HasValue || _settings?.WheelTelemetryEraByPageGuid == null) return MozaWheelEra.Auto;
                if (!_settings.WheelTelemetryEraByPageGuid.TryGetValue(g.Value, out var v) || v < 0)
                    return MozaWheelEra.Auto;
                return (MozaWheelEra)v;
            }
            set
            {
                var g = GetCurrentWheelPageGuid();
                if (!g.HasValue) return;
                if (_settings == null) return;
                if (_settings.WheelTelemetryEraByPageGuid == null)
                    _settings.WheelTelemetryEraByPageGuid = new Dictionary<Guid, int>();
                _settings.WheelTelemetryEraByPageGuid[g.Value] = (int)value;
            }
        }

        /// <summary>
        /// Channel-mapping dict for the active profile × current wheel page. Null
        /// when no profile/wheel is resolvable. Caller must not mutate returned
        /// dict directly — use the channel-mapping write helpers in MozaPlugin.cs.
        /// </summary>
        internal Dictionary<string, Dictionary<string, string>>? GetActiveChannelMappings()
        {
            var profile = _settings?.ProfileStore?.CurrentProfile;
            if (profile?.TelemetryChannelMappings == null) return null;
            var g = GetCurrentWheelPageGuid();
            if (!g.HasValue) return null;
            return profile.TelemetryChannelMappings.TryGetValue(g.Value, out var m) ? m : null;
        }

        /// <summary>
        /// Get or create the channel-mapping dict for the active profile × current
        /// wheel page. Returns null only when no profile is selected or no wheel
        /// has identified yet.
        /// </summary>
        internal Dictionary<string, Dictionary<string, string>>? GetOrCreateActiveChannelMappings()
        {
            var profile = _settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return null;
            if (profile.TelemetryChannelMappings == null)
                profile.TelemetryChannelMappings = new Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>();
            var g = GetCurrentWheelPageGuid();
            if (!g.HasValue) return null;
            if (!profile.TelemetryChannelMappings.TryGetValue(g.Value, out var m) || m == null)
            {
                m = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                profile.TelemetryChannelMappings[g.Value] = m;
            }
            return m;
        }

        /// <summary>
        /// Initialize the native SimHub profile system.
        /// ProfileSettingsBase.Init() reads the current game from PluginManager and selects the right profile.
        /// </summary>
        private void InitProfileSystem()
        {
            var store = _settings.ProfileStore;

            // Ensure at least one default profile exists. Seed its baselines
            // from the legacy MozaPluginSettings flat fields so pre-refactor
            // users (whose JSON has no profile entries at all) get sane
            // Seed the baseline so first-launch writes (e.g. DashDisplayBrightness)
            // don't sit at the -1 sentinel and leave the display dark.
            if (store.Profiles.Count == 0)
            {
                var defaultProfile = new MozaProfile { Name = "Default" };
                new SettingsMigrator(_settings).SeedProfileBaselineFromFlatFields(defaultProfile);
                store.Profiles.Add(defaultProfile);
            }

            // Init reads PluginManager.Instance.GameName and selects the matching profile
            store.Init();

            // Detach prior subscription before re-subscribing (ClearSettings replaces _settings).
            if (_subscribedProfileStore != null && !ReferenceEquals(_subscribedProfileStore, store))
                _subscribedProfileStore.CurrentProfileChanged -= OnProfileChanged;

            // Subscribe to profile changes (game switch, manual selection)
            store.CurrentProfileChanged += OnProfileChanged;
            _subscribedProfileStore = store;

            // Apply the initially selected profile
            if (store.CurrentProfile != null)
            {
                MozaLog.Debug($"[Moza] Initial profile: {store.CurrentProfile.Name}");
                if (_settings.AutoApplyProfileOnLaunch)
                    ApplyProfile(store.CurrentProfile);
                else
                    MozaLog.Debug("[Moza] Skipping auto-apply (disabled in Options)");
            }
        }

        private void OnProfileChanged(object sender, EventArgs e)
        {
            var profile = _settings.ProfileStore.CurrentProfile;
            if (profile != null)
            {
                MozaLog.Info($"[Moza] Profile changed: {profile.Name}");
                ApplyProfile(profile);
            }
        }

        /// <summary>
        /// Apply a profile by routing through the consolidated Apply*ToHardware
        /// methods. Each method mirrors profile/overlay values into _data (always)
        /// and writes to hardware when the matching device is detected.
        /// </summary>
                internal void ApplyProfile(MozaProfile profile)
        {
            MozaLog.Debug($"[Moza] Applying profile: {profile.Name}");
            _hardwareApplier.ApplyProfileHardware(profile);

            // Persist without re-capturing _data — profile already has the values
            // we just applied; concurrent device reads could have overwritten _data
            // before our writes were processed.
            PersistSettings();

            // Apply profile-recorded dashboard preference after wheel settings are
            // in place. Defer to next PollStatus tick when wheel catalog isn't ready.
            if (!string.IsNullOrEmpty(profile.TelemetryDashboardKey))
            {
                bool applied = false;
                try { applied = ApplyTelemetryDashboardFromProfile(profile); }
                catch (Exception ex)
                {
                    MozaLog.Warn("[Moza] ApplyTelemetryDashboardFromProfile threw: " + ex.Message);
                    applied = true;
                }
                if (!applied)
                {
                    _dashboardBindingCoordinator.SetPendingDashboardKey(profile.TelemetryDashboardKey!);
                    MozaLog.Debug("[Moza] Profile dashboard apply deferred — wheel state not ready");
                }
                else
                {
                    _dashboardBindingCoordinator.ClearPendingDashboardKey();
                }
            }

            // Sync telemetry pipeline to the new overlay's enable state. We do NOT
            // stop/pause the sender here — parity polls keep the wheel engaged;
            // ProfileTelemetryEnabled gates value/string emission inside the sender.
            try
            {
                bool wantOn = ActiveTelemetryEnabled;
                var sender = _telemetrySender;
                if (sender != null)
                    sender.ProfileTelemetryEnabled = wantOn;
                if (wantOn)
                {
                    ApplyTelemetrySettings();
                    StartTelemetryIfReady();
                }
                else
                {
                    Interlocked.Exchange(ref _telemetryStartRequested, 0);
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Telemetry sync after profile apply failed: {ex.Message}");
            }
        }

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
