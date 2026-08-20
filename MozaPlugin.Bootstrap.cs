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
    public partial class MozaPlugin
    {

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
                _updateCheck = new UpdateCheckCoordinator(this);
                _fsr1Probe = new Diagnostics.Fsr1ProbeTool(this);

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

                // Publish the master-mapper default overrides before anything can
                // build a profile, so the first cold-start tier-def already carries
                // them (no dashboard switch needed to pick them up).
                PushGlobalChannelDefaults();

                // Migrate the legacy Stable/Dev update channel enum to the
                // channel-id scheme. The dev channel is gone (dev-latest is no
                // longer published), so prior Dev users land on Stable with a
                // clean cache — their LastSeen* referenced dev-latest artifacts.
                if (string.IsNullOrEmpty(_settings.UpdateChannelId))
                {
                    _settings.UpdateChannelId = UpdateCheckService.StableChannelId;
                    if (_settings.UpdateChannel == UpdateChannel.Dev)
                    {
                        _settings.UpdateChannel = UpdateChannel.Stable;
                        _settings.LastSeenLatestVersion = "";
                        _settings.LastSeenReleaseUrl = "";
                        _settings.LastSeenAssetUrl = "";
                        _settings.LastSeenReleaseNotes = "";
                        _settings.LastSkippedVersion = "";
                    }
                }

                // VerboseWireDebugLog shipped defaulting to true and IS serialized,
                // so every existing install has `true` baked into its settings file
                // and would keep frame-rate wire logging after the default flipped.
                // Clear it once. The flag makes this a one-shot: anyone who sets the
                // setting back to true afterwards keeps it.
                if (!_settings.VerboseWireDebugLogDefaultMigrated)
                {
                    _settings.VerboseWireDebugLogDefaultMigrated = true;
                    _settings.VerboseWireDebugLog = false;
                }

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

                // Host platform decides the device-discovery source (registry vs
                // sysfs) and how ports are opened, so record it once per Init —
                // it is the first thing to check on any Linux detection report.
                MozaLog.Info($"[AZOM] Host: {Protocol.WineHost.Describe()}");

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

                // Diagnostic capture is always on (no user toggle): enable before
                // any device traffic so the startup segment covers the full
                // connect/handshake. EnsureRunning() — not Start() — so the
                // dual-segment ring (and its frozen first-minute) survives the
                // plugin reload on game switches.
                {
                    var cap = global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance;
                    bool wasRunning = cap.Enabled;
                    cap.EnsureRunning();
                    MozaLog.Debug(wasRunning
                        ? $"[AZOM] Diagnostic capture preserved across reload ({cap.Count} frames)"
                        : "[AZOM] Diagnostic capture enabled (startup + rolling segments)");
                }

                // Fire-and-forget update check against the GitHub Releases API.
                // Throttled to once per 24h (LastUpdateCheckUtc) and deduped
                // per-process so SimHub game switches
                // don't multiply network calls. Persist-then-render: the
                // result lands in _settings.LastSeenLatestVersion and the
                // About-tab banner reads it on next open. Failures are silent
                // here — the user-facing "Check now" button surfaces errors.
                _updateCheck.MaybeStart();

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
                    if (!string.IsNullOrEmpty(_settings.LastWheelbaseDeviceId))
                        _connection.LastDeviceId = _settings.LastWheelbaseDeviceId;
                    s_persistentConnection = _connection;
                }
                _connection.MessageReceived += OnMessageReceived;
                _connection.Disconnected += OnSerialDisconnected;

                _deviceManager = new MozaDeviceManager(_connection, PendingResponses);

                _ab9Manager = new MozaAb9DeviceManager(disableProbeFallback);
                if (!string.IsNullOrEmpty(_settings.LastAb9Port))
                    _ab9Manager.Connection.LastPortName = _settings.LastAb9Port;
                if (!string.IsNullOrEmpty(_settings.LastAb9DeviceId))
                    _ab9Manager.Connection.LastDeviceId = _settings.LastAb9DeviceId;
                _ab9Manager.MessageReceived += OnAb9MessageReceived;

                // Dedicated connection for a standalone-USB CM2 (PID 0x0025), so it
                // works even when a base holds the wheelbase connection.
                _dashboardManager = new MozaDashboardDeviceManager();
                if (!string.IsNullOrEmpty(_settings.LastDashboardPort))
                    _dashboardManager.Connection.LastPortName = _settings.LastDashboardPort;
                if (!string.IsNullOrEmpty(_settings.LastDashboardDeviceId))
                    _dashboardManager.Connection.LastDeviceId = _settings.LastDashboardDeviceId;
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
                if (!string.IsNullOrEmpty(_settings.LastHubDeviceId))
                    _hubManager.Connection.LastDeviceId = _settings.LastHubDeviceId;
                _hubManager.MessageReceived += OnHubMessageReceived;
                _hubManager.Connection.Disconnected += OnHubDisconnected;

                // Mirror of _hubManager for the broken-base case: a dedicated
                // base pipe that only comes up AFTER the primary has migrated to
                // the hub (PrimaryBoundToHub), carrying base-only telemetry on the
                // base port so motor temps / FFB / ambient survive the migration.
                _baseManager = new MozaBaseDeviceManager();
                if (!string.IsNullOrEmpty(_settings.LastBaseAuxPort))
                    _baseManager.Connection.LastPortName = _settings.LastBaseAuxPort;
                if (!string.IsNullOrEmpty(_settings.LastBaseAuxDeviceId))
                    _baseManager.Connection.LastDeviceId = _settings.LastBaseAuxDeviceId;
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

                // Keep SimHub's Arduino auto-scan off MOZA ports. The scanner asks
                // subscribers before opening each candidate port (CanBeScanned);
                // without a veto it holds the wheelbase CDC port for two full scan
                // passes on startup (~25 s observed) while our probes fail against
                // the busy port, and its 19200-baud Arduino hello is garbage from
                // the wheelbase's point of view. Static singleton event ⇒
                // unsubscribe in End()/CleanupPartialInit (see _arduinoScanHooked).
                try
                {
                    SerialDash.ComportScanner.Instance.CanBeScanned += OnArduinoPortCanBeScanned;
                    Interlocked.Exchange(ref _arduinoScanHooked, 1);
                }
                catch (Exception ex)
                {
                    MozaLog.Debug($"[AZOM] Arduino-scan veto hook unavailable: {ex.Message}");
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
                    onSerialResolved: OnMBoosterSerialResolved,
                    connectivitySeedLookup: LookupMBoosterKnownPedals,
                    onConnectivityResolved: OnMBoosterConnectivityResolved);
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

                // Base-tab temperature-graph history sampler — runs for the
                // plugin's whole life so the graph shows its full window the
                // moment the settings panel opens. Reads the temps that the 5 s
                // _pollTimer refreshes; UI-independent by design.
                _tempHistoryTimer = new Timer(TempHistoryIntervalMs);
                _tempHistoryTimer.Elapsed += SampleTemperatureHistory;
                _tempHistoryTimer.AutoReset = true;
                _tempHistoryTimer.Start();

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
                    if (_dashboardManager != null && _dashboardManager.IsConnected)
                    {
                        try { _dashboardManager.PendingResponses.TickRetransmits(_dashboardManager.Connection.Send); }
                        catch (Exception ex) { MozaLog.Warn($"[AZOM] Dashboard PendingResponseTracker tick failed: {ex.Message}"); }
                    }
                    // Each standalone-peripheral pipe retransmits its own tracked
                    // reads on its own Send (same per-pipe isolation as the hub).
                    try { _peripheralRegistry?.TickRetransmits(); }
                    catch (Exception ex) { MozaLog.Warn($"[AZOM] Standalone peripheral retransmit tick failed: {ex.Message}"); }
                    // Old-protocol (ES/ESX) master LED brightness settle + write. This
                    // steady tick is the only cadence that survives idle (Display() is
                    // bursty, DataUpdate goes quiet) — see TickEsMasterBrightness (#113).
                    try { TickEsMasterBrightness(); }
                    catch (Exception ex) { MozaLog.Warn($"[AZOM] ES master-brightness tick failed: {ex.Message}"); }
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
                // Re-bound on every plugin reload so a reused persistent sender
                // never holds a closure over a disposed plugin instance.
                _telemetrySender.WheelModelInfoProvider = () => WheelModelInfo;
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
            UnhookArduinoScanVeto();
            try { _pollTimer?.Stop(); } catch { }
            try { _tempHistoryTimer?.Stop(); } catch { }
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
            try { _hardwareApplier?.Shutdown(); } catch { }
            // Release the timer-resolution request + power-throttling opt-out.
            try { _responsiveness?.Dispose(); _responsiveness = null; } catch { }
            // Dispose every mBooster controller — same reason: stop workers
            // before the connections they own get torn down.
            try { _mboosterRegistry?.Dispose(); _mboosterRegistry = null; } catch { }
            try { DisposeRoutedMBoosterProbes(); } catch { }
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
            // The in-memory ring stays enabled across the plugin reload (capture
            // is always on) so buffered frames survive game switches — next Init's
            // EnsureRunning is a no-op.
            try { global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.StopFileSink(); } catch { }
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
            try { _tempHistoryTimer?.Dispose(); } catch { }
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
    }
}
