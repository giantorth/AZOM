using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Timers;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using MozaPlugin.Devices;
using MozaPlugin.Hardware;
using MozaPlugin.Protocol;
using MozaPlugin.Settings;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.TileServer;
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

        private MozaSerialConnection _connection = null!;
        private MozaData _data = null!;
        private MozaDeviceManager _deviceManager = null!;
        private MozaAb9DeviceManager _ab9Manager = null!;
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
        internal DeviceDetectionState DetectionState { get; } = new DeviceDetectionState();

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

        internal bool IsDashDetected => DetectionState.DashDetected;
        internal bool IsBaseAmbientLedSupported => DetectionState.BaseAmbientLedSupported;
        internal bool IsHandbrakeDetected => DetectionState.HandbrakeDetected;
        internal bool IsPedalsDetected => DetectionState.PedalsDetected;
        internal bool IsHubDetected => DetectionState.HubDetected;
        internal bool IsAb9Detected => DetectionState.Ab9Detected;
        internal MozaAb9DeviceManager Ab9Manager => _ab9Manager;
        internal MozaSerialConnection Connection => _connection;

        /// <summary>True if the wheel's internal Display sub-device responded to probe.
        /// Accepts any populated identity field — some wheels (e.g. W17) return an
        /// empty model-name string but populate HW/SW/MCU UID.</summary>
        internal bool IsDisplayDetected =>
            !string.IsNullOrEmpty(_data?.DisplayModelName)
            || !string.IsNullOrEmpty(_data?.DisplayHwVersion)
            || !string.IsNullOrEmpty(_data?.DisplaySwVersion)
            || (_data?.DisplayMcuUid?.Length ?? 0) > 0
            || (_telemetrySender?.DisplayDetected ?? false);

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

                // One-shot "start capture on next launch" arm flag. Cleared
                // before any device traffic so the capture covers the full connect.
                if (_settings.StartCaptureOnNextLaunch)
                {
                    _settings.StartCaptureOnNextLaunch = false;
                    try { this.SaveCommonSettings("MozaPluginSettings", _settings); } catch { }
                    global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.Start();
                    MozaLog.Debug("[Moza] Serial traffic capture armed from previous session — capturing now");
                }

                MozaDeviceConstants.InitializeRegistry();

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
                    _connection = new MozaSerialConnection(
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

                // AB9 engine-vibration worker — tick gates on connection/detection state.
                _ab9Worker = new Ab9EngineVibrationWorker(
                    _ab9Manager,
                    DetectionState,
                    () => _settings?.ProfileStore?.CurrentProfile?.Ab9,
                    () => IsShuttingDown);
                _ab9Worker.Start();

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
                };
                _reconnectTimer.AutoReset = true;
                if (_settings.ConnectionEnabled)
                    _reconnectTimer.Start();

                _hidReader = new MozaHidReader(_data);
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
        /// </summary>
        private void CleanupPartialInit()
        {
            try { _pollTimer?.Stop(); } catch { }
            try { _retryTimer?.Stop(); } catch { }
            try { _reconnectTimer?.Stop(); } catch { }
            try { _saveDebounceTimer?.Stop(); } catch { }
            try { _telemetrySender?.Stop(); } catch { }

            // Halt the AB9 engine-vib worker before disposing the AB9 manager.
            try { _ab9Worker?.Stop(); _ab9Worker = null; } catch { }
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
            try { _telemetrySender?.Dispose(); } catch { }
            try { global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.StopFileSink(); } catch { }
            try { global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.Stop(); } catch { }
            try { _connection?.Dispose(); } catch { }
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
        }

        // Gearshift trigger state. Fires base-gearshift-event (grp 0x2D cmd 0x76)
        // on gear-string transitions; null initial value suppresses warm-up.
        private string? _lastGearString;
        private DateTime _lastGearShiftSendUtc = DateTime.MinValue;

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

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (IsShuttingDown) return;
            _telemetrySender?.UpdateGameData(data.NewData);
            _telemetrySender?.SetGameRunning(data.GameRunning);
            CheckGearshiftEvent(data);

            // Hand the latest RPM + game-running flag to the AB9 engine-vib worker.
            double rpm = data.NewData?.Rpms ?? 0.0;
            _ab9Worker?.PostFrame(rpm, data.GameRunning);
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

            // Clear detection flags up-front. If SimHub re-uses this plugin instance
            // across load/unload, the next Init() also clears them — together this
            // makes detection state deterministic on a fresh start.
            ResetDetectionFlags();

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
            bool keepWireAlive = _usingPersistentWire
                                 || (_connection != null && _connection == s_persistentConnection
                                     && _telemetrySender != null
                                     && _telemetrySender == s_persistentTelemetrySender);

            if (!keepWireAlive)
            {
                _telemetrySender?.Stop();
            }

            // Release the wire-trace file handle.
            try { global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.StopFileSink(); } catch { }
            try { global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.Stop(); } catch { }

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
                        this.SaveCommonSettings("MozaPluginSettings", _settings);
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
                MozaLog.Info("[Moza] Connection enabled");
            }
            else
            {
                _reconnectTimer.Stop();
                ClearLedsOnHardware();
                _telemetrySender?.Stop();
                _connection?.Disconnect();
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
                    MozaLog.Info("[Moza] Connected to MOZA device");
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
            DetectionState.ResetWheel();
            WheelModelInfo = null;
            _data.ClearWheelIdentity();
            _deviceManager.ResetWheelDetection();
            if (_telemetrySender != null)
                _telemetrySender.DetectedDeviceMask = 0;
            Interlocked.Exchange(ref _telemetryStartRequested, 0);
            // Hot-swap may bind a different default dashboard; force kind=4 re-emit.
            _dashboardBindingCoordinator?.ClearLastAppliedDashboardKey();
            _telemetrySender?.ResetBindingTracking();
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

            // Device detection probes — only sent until each device is found
            if (!DetectionState.NewWheelDetected && !DetectionState.OldWheelDetected)
                _deviceManager.ProbeWheelDetection();
            if (!DetectionState.DashDetected)
                _deviceManager.ReadSetting("dash-rpm-indicator-mode");
            if (!DetectionState.HandbrakeDetected)
                _deviceManager.ReadSetting("handbrake-direction");
            if (!DetectionState.PedalsDetected)
                _deviceManager.ReadSetting("pedals-throttle-dir");
            if (!DetectionState.HubDetected)
                _deviceManager.ReadSetting("hub-port1-power");

            // Re-probe display sub-device until fully identified — initial probe
            // can race power-up and return only partial identity.
            if (DetectionState.NewWheelDetected && !IsDisplayDetected)
                _deviceManager.SendDisplayProbe();

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

            // Filter firmware debug noise before parsing/logging
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
                        bundle.TimeoutMin = r.IntValue;
                        changed = true;
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
