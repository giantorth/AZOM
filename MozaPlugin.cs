using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Timers;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using MozaPlugin.Devices;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry;
using Timer = System.Timers.Timer;

namespace MozaPlugin
{
    [PluginDescription("Configure MOZA Racing hardware and send SimHub game telemetry to wheel/dashboard RPM LEDs")]
    [PluginAuthor("giantorth")]
    [PluginName("MOZA Control")]
    public class MozaPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        internal static MozaPlugin? Instance { get; private set; }

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
        private TelemetrySender? _telemetrySender;
        internal DashboardProfileStore DashProfileStore { get; } = new DashboardProfileStore();
        internal DashboardCache DashCache { get; private set; } = null!;

        // Device detection state. Written from serial-reader thread
        // (OnMessageReceived → DetectDevices) and read from the poll timer,
        // UI thread, and TelemetrySender.StartTelemetryIfReady — without
        // volatile, readers can stay stuck on stale `false` after detect
        // and never start telemetry / refresh UI.
        private volatile bool _baseDetected;
        private volatile bool _dashDetected;
        private volatile bool _newWheelDetected;
        private volatile bool _oldWheelDetected;
        private volatile bool _handbrakeDetected;
        private volatile bool _pedalsDetected;
        private volatile bool _hubDetected;
        private volatile bool _ab9Detected;
        private volatile bool _ab9SettingsApplied;

        // ===== AB9 host-rendered engine vibration =====
        //
        // PitHouse-style 91 Hz stream of Group 0x20 / cmd 0x0A 0x05 frames
        // encoding period = K / (rpm × freq) — see
        // docs/protocol/devices/ab9-shifter.md. DataUpdate writes the
        // volatiles; the worker thread reads them each tick alongside the
        // user's intensity/freq sliders from the active profile.
        //
        // K calibrated against five corner points captured from PitHouse:
        // idle+100Hz, idle+200Hz, idle+50Hz, redline+200Hz, redline+50Hz —
        // all five collapse to K ≈ 3.95 × 10¹¹ within 3%.
        private const double EngineVibK = 3.95e11;
        private Thread? _ab9EngineVibThread;
        private volatile bool _ab9EngineVibStop;
        private volatile bool _ab9EngineVibActive;
        // double can't be `volatile` in C# — wrap in long with Interlocked.Exchange
        // for a torn-write-free cross-thread RPM read. Same approach as
        // _telemetryStartRequested above.
        private long _latestRpmBits;
        private volatile bool _latestGameRunning;

        // Guard against concurrent/duplicate telemetry Start() dispatch
        private int _telemetryStartRequested;

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

        private static readonly string[] BaseSettingsReadCommands = new[]
        {
            "base-limit", "base-ffb-strength", "base-torque", "base-speed",
            "base-damper", "base-friction", "base-inertia", "base-spring",
            "base-protection", "base-natural-inertia",
            "base-speed-damping", "base-speed-damping-point",
            "base-soft-limit-stiffness", "base-soft-limit-retain",
            "base-ffb-reverse", "base-temp-strategy", "base-gearshift-vibration",
            "main-get-work-mode", "main-get-led-status",
            "main-get-damper-gain", "main-get-friction-gain",
            "main-get-inertia-gain", "main-get-spring-gain",
            "main-get-ble-mode",
            // FFB Equalizer
            "base-equalizer1", "base-equalizer2", "base-equalizer3",
            "base-equalizer4", "base-equalizer5", "base-equalizer6",
            // FFB Curve (Y outputs only; X breakpoints are fixed at 20/40/60/80)
            "base-ffb-curve-y1", "base-ffb-curve-y2", "base-ffb-curve-y3", "base-ffb-curve-y4", "base-ffb-curve-y5",
        };

        private static readonly string[] NewWheelSettingsReadCommands = new[]
        {
            "wheel-telemetry-mode", "wheel-telemetry-idle-effect",
            "wheel-buttons-idle-effect",
            "wheel-knob-idle-effect",
            "wheel-knob-led-mode", "wheel-buttons-led-mode",
            "wheel-idle-mode", "wheel-idle-timeout",
            "wheel-rpm-brightness", "wheel-buttons-brightness", "wheel-flags-brightness",
            "wheel-idle-mode", "wheel-idle-timeout", "wheel-idle-speed",
            "wheel-idle-color",
            "wheel-paddles-mode", "wheel-clutch-point", "wheel-knob-mode", "wheel-stick-mode",
            // Per-encoder signal mode probe — silent on firmware without [42, N] support
            "wheel-knob-signal-mode0", "wheel-knob-signal-mode1", "wheel-knob-signal-mode2",
            "wheel-knob-signal-mode3", "wheel-knob-signal-mode4",
            // RPM colors (up to 18 — KS Pro max)
            "wheel-rpm-color1", "wheel-rpm-color2", "wheel-rpm-color3",
            "wheel-rpm-color4", "wheel-rpm-color5", "wheel-rpm-color6",
            "wheel-rpm-color7", "wheel-rpm-color8", "wheel-rpm-color9",
            "wheel-rpm-color10", "wheel-rpm-color11", "wheel-rpm-color12",
            "wheel-rpm-color13", "wheel-rpm-color14", "wheel-rpm-color15",
            "wheel-rpm-color16", "wheel-rpm-color17", "wheel-rpm-color18",
            // Button colors
            "wheel-button-color1",  "wheel-button-color2",  "wheel-button-color3",
            "wheel-button-color4",  "wheel-button-color5",  "wheel-button-color6",
            "wheel-button-color7",  "wheel-button-color8",  "wheel-button-color9",
            "wheel-button-color10", "wheel-button-color11", "wheel-button-color12",
            "wheel-button-color13", "wheel-button-color14",
            // Flag colors
            "wheel-flag-color1", "wheel-flag-color2", "wheel-flag-color3",
            "wheel-flag-color4", "wheel-flag-color5", "wheel-flag-color6",
            // Extended LED group presence probes (Single/Rotary/Ambient).
            // A brightness response flips IsWheelLedGroupPresent for that group.
            "wheel-single-brightness", "wheel-knob-brightness", "wheel-ambient-brightness",
        };

        private static readonly string[] OldWheelSettingsReadCommands = new[]
        {
            "wheel-rpm-indicator-mode", "wheel-get-rpm-display-mode",
            "wheel-old-rpm-brightness",
            "wheel-old-rpm-color1", "wheel-old-rpm-color2", "wheel-old-rpm-color3",
            "wheel-old-rpm-color4", "wheel-old-rpm-color5", "wheel-old-rpm-color6",
            "wheel-old-rpm-color7", "wheel-old-rpm-color8", "wheel-old-rpm-color9",
            "wheel-old-rpm-color10",
        };

        private static readonly string[] DashSettingsReadCommands = new[]
        {
            "dash-rpm-indicator-mode", "dash-flags-indicator-mode",
            "dash-rpm-display-mode",
            "dash-rpm-brightness", "dash-flags-brightness",
            "dash-rpm-color1", "dash-rpm-color2", "dash-rpm-color3",
            "dash-rpm-color4", "dash-rpm-color5", "dash-rpm-color6",
            "dash-rpm-color7", "dash-rpm-color8", "dash-rpm-color9",
            "dash-rpm-color10",
            "dash-flag-color1", "dash-flag-color2", "dash-flag-color3",
            "dash-flag-color4", "dash-flag-color5", "dash-flag-color6",
        };

        private static readonly string[] HandbrakeSettingsReadCommands = new[]
        {
            "handbrake-direction", "handbrake-min", "handbrake-max",
            "handbrake-mode", "handbrake-button-threshold",
            "handbrake-y1", "handbrake-y2", "handbrake-y3", "handbrake-y4", "handbrake-y5",
        };

        private static readonly string[] PedalsSettingsReadCommands = new[]
        {
            "pedals-throttle-dir", "pedals-throttle-min", "pedals-throttle-max",
            "pedals-brake-dir", "pedals-brake-min", "pedals-brake-max", "pedals-brake-angle-ratio",
            "pedals-clutch-dir", "pedals-clutch-min", "pedals-clutch-max",
            "pedals-throttle-y1", "pedals-throttle-y2", "pedals-throttle-y3", "pedals-throttle-y4", "pedals-throttle-y5",
            "pedals-brake-y1", "pedals-brake-y2", "pedals-brake-y3", "pedals-brake-y4", "pedals-brake-y5",
            "pedals-clutch-y1", "pedals-clutch-y2", "pedals-clutch-y3", "pedals-clutch-y4", "pedals-clutch-y5",
        };

        private static readonly string[] HubReadCommands = new[]
        {
            "hub-base-power", "hub-port1-power", "hub-port2-power", "hub-port3-power",
            "hub-pedals1-power", "hub-pedals2-power", "hub-pedals3-power",
        };

        public PluginManager PluginManager { set => _pluginManager = value; }
        public ImageSource? PictureIcon => null;
        public string LeftMenuTitle => "MOZA";

        internal bool ConnectionEnabled => _settings?.ConnectionEnabled ?? true;

        internal MozaData Data => _data;
        internal MozaDeviceManager DeviceManager => _deviceManager;
        internal MozaPluginSettings Settings => _settings;
        internal bool IsNewWheelDetected => _newWheelDetected;
        internal bool IsOldWheelDetected => _oldWheelDetected;
        internal Devices.WheelModelInfo? WheelModelInfo { get; private set; }

        /// <summary>
        /// Extended LED groups detected on the connected wheel (indices 2..4 for Single,
        /// Rotary, Ambient per rs21_parameter.db). A group is flagged true when the wheel
        /// answers the group's brightness read during the post-connect probe.
        /// Groups 0/1 (RPM/Buttons) are not tracked here — their presence is implied by
        /// any new-protocol wheel.
        /// </summary>
        // Bit `g` set => group g detected. Stored as int so all reads/writes go
        // through Interlocked, giving lock-free atomic visibility between the
        // serial-message thread (which sets bits as group probes respond) and
        // any reader (UI, device extensions, poll timer).
        private int _wheelLedGroupMask;
        private volatile bool _group3ColorsRead;
        internal bool IsWheelLedGroupPresent(int group)
        {
            if (group < 2 || group > 4) return false;
            return (Volatile.Read(ref _wheelLedGroupMask) & (1 << group)) != 0;
        }
        /// <summary>
        /// When true, the device extension owns wheel LED settings via its own profile system.
        /// Plugin profile application skips wheel settings to avoid conflicts.
        /// </summary>
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

        /// <summary>
        /// Tracks model prefixes with an active (loaded) device extension in this SimHub session.
        /// Used by the generic fallback device to yield when a model-specific device is active.
        /// </summary>
        // Copy-on-write for thread safety: reads get a consistent snapshot,
        // mutations (rare — only on device extension init/end) create a new set.
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

        internal bool IsDashDetected => _dashDetected;
        internal bool IsHandbrakeDetected => _handbrakeDetected;
        internal bool IsPedalsDetected => _pedalsDetected;
        internal bool IsHubDetected => _hubDetected;
        internal bool IsAb9Detected => _ab9Detected;
        internal MozaAb9DeviceManager Ab9Manager => _ab9Manager;
        internal MozaSerialConnection Connection => _connection;

        /// <summary>True if the wheel's internal Display sub-device responded to probe.
        /// Reads `_data.DisplayModelName` so detection works as soon as the wheel-detection
        /// display probe (sent via <see cref="MozaDeviceManager.SendDisplayProbe"/>) gets
        /// answered — independent of whether <see cref="TelemetrySender"/> has started.
        /// The dashboard-telemetry UI section is gated on this flag, so detection must
        /// happen BEFORE the user can pick a profile (otherwise the section stays hidden
        /// and there's no way to opt in to telemetry).
        /// Some wheels (e.g. W17) populate display HW/SW/MCU identity fields but return
        /// an empty model-name string — accept any populated identity field as detection.</summary>
        internal bool IsDisplayDetected =>
            !string.IsNullOrEmpty(_data?.DisplayModelName)
            || !string.IsNullOrEmpty(_data?.DisplayHwVersion)
            || !string.IsNullOrEmpty(_data?.DisplaySwVersion)
            || (_data?.DisplayMcuUid?.Length ?? 0) > 0
            || (_telemetrySender?.DisplayDetected ?? false);

        /// <summary>
        /// Whether the plugin should drive the dashboard telemetry pipeline for the
        /// currently-detected wheel. Authoritative answer for known wheel models
        /// (<see cref="Devices.WheelModelInfo.HasDisplay"/> is true/false); for
        /// unknown wheels (HasDisplay==null) falls back to the runtime
        /// <see cref="IsDisplayDetected"/> probe.
        /// Used both to gate <see cref="StartTelemetryIfReady"/> and to control the
        /// dashboard-telemetry UI section visibility.
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
            _pluginManager = pluginManager;

            try
            {
                _data = new MozaData();
                _settings = this.ReadCommonSettings<MozaPluginSettings>("MozaPluginSettings", () => new MozaPluginSettings());

                // Null-guard for upgraded settings missing ProfileStore
                if (_settings.ProfileStore == null)
                    _settings.ProfileStore = new MozaProfileStore();

                // One-shot upgrade: pre-2026-05-09 settings stored channel mappings in
                // TelemetryChannelMappings (single-level, not wheel-scoped). Move them
                // into TelemetryChannelMappingsByWheel[""] so users keep their data
                // when they install this build.
                if (_settings.MigrateLegacyChannelMappingsIfNeeded())
                {
                    MozaLog.Info("[Moza] Migrated legacy TelemetryChannelMappings to per-wheel schema (under empty-wheel slot \"\")");
                    this.SaveCommonSettings("MozaPluginSettings", _settings);
                }


                // Restore blink colors from settings (write-only, can't be polled from device)
                MozaProfile.UnpackColorsInto(_settings.WheelRpmBlinkColors, _data.WheelRpmBlinkColors);
                MozaProfile.UnpackColorsInto(_settings.DashRpmBlinkColors, _data.DashRpmBlinkColors);

                MozaLog.Info("[Moza] Initializing plugin");

                // Bridge-format JSONL wire trace at SimHub/Logs/moza-wire-*.jsonl.
                // Off by default; opt-in via _settings.EnableWireTraceFileSink for
                // PitHouse-vs-plugin diff work (see sim/diff_captures.py). Each
                // launch opens a fresh file when enabled.
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

                // Consume one-shot "start capture on next launch" arm flag. Done before
                // any device connect / probe traffic so the capture covers the full
                // connect handshake. Flag is cleared and persisted immediately so a
                // crash mid-init doesn't leave it armed forever.
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

                // Initialize the native profile system (detects current game, selects profile)
                InitProfileSystem();

                RegisterProperties(pluginManager);
                RegisterActions();

                // Reject the AB9 shifter PID on the wheelbase pipe — both
                // devices enumerate as VID_346E composite. The registry-based
                // discovery in MozaPortDiscovery routes by exact PID and never
                // mis-claims the AB9 port. The serial-probe fallback (only
                // armed when the registry returns zero MOZA devices total)
                // honours the same filter.
                Func<bool> disableProbeFallback = () =>
                    _settings != null && _settings.DisableSerialProbeFallback;
                _connection = new MozaSerialConnection(
                    pid => !MozaUsbIds.IsAb9Pid(pid),
                    MozaProbeTarget.BaseAndHub,
                    disableProbeFallback);
                if (!string.IsNullOrEmpty(_settings.LastWheelbasePort))
                    _connection.LastPortName = _settings.LastWheelbasePort;
                _connection.MessageReceived += OnMessageReceived;
                _connection.Disconnected += OnSerialDisconnected;

                _deviceManager = new MozaDeviceManager(_connection);

                _ab9Manager = new MozaAb9DeviceManager(disableProbeFallback);
                if (!string.IsNullOrEmpty(_settings.LastAb9Port))
                    _ab9Manager.Connection.LastPortName = _settings.LastAb9Port;
                _ab9Manager.MessageReceived += OnAb9MessageReceived;

                // AB9 engine-vibration worker — runs unconditionally; tick
                // body gates on AB9 connection/detection state. The thread
                // exits when _ab9EngineVibStop is set in End/CleanupPartialInit.
                _ab9EngineVibStop = false;
                _ab9EngineVibActive = false;
                _ab9EngineVibThread = new Thread(Ab9EngineVibrationLoop)
                {
                    IsBackground = true,
                    Name = "MozaAb9EngineVib",
                };
                _ab9EngineVibThread.Start();

                // 5 s poll interval. Was 2 s, but the diagnostic reads it issues
                // (base-state, mcu/mosfet/motor temp via 0x2B/0x13 cmd 1/4/5/6 +
                // hub/dash/handbrake/pedal probes) generated ~6 frames/s of plugin-
                // only wire noise vs PitHouse. Slowing to 5 s halves the impact
                // while keeping hot-swap and temperature UI responsive enough.
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
                    // AB9 manager is always probed; with registry-based
                    // discovery the attempt is microseconds when no AB9 is
                    // enumerated, so there's no probe storm to defend against.
                    if (!_ab9Manager.IsConnected)
                        TryConnectAb9();
                };
                _reconnectTimer.AutoReset = true;
                if (_settings.ConnectionEnabled)
                    _reconnectTimer.Start();

                _hidReader = new MozaHidReader(_data);
                _hidReader.Start();

                _telemetrySender = new TelemetrySender(_connection);
                // Reset the start-request gate when the dashboard pipeline parks
                // itself (sess=0x09 retry exhaust). Without this clear, the next
                // wheel hot-swap or user toggle would early-out in
                // StartTelemetryIfReady() because the gate is still latched at 1.
                _telemetrySender.DashboardPipelineParked += (_, __) =>
                {
                    Interlocked.Exchange(ref _telemetryStartRequested, 0);
                };

                // Initialize dashboard cache for download-on-connect.
                string cacheDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MozaSimHubPlugin", "DashboardCache");
                DashCache = new DashboardCache(cacheDir, DashProfileStore);
                DashCache.LoadFromDisk();
                if (!string.IsNullOrEmpty(_settings.TelemetryMzdashFolder))
                    DashCache.LoadFromFolder(_settings.TelemetryMzdashFolder);
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
            try
            {
                _ab9EngineVibStop = true;
                _ab9EngineVibThread?.Join(1000);
                _ab9EngineVibThread = null;
            }
            catch { }
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

        // Gearshift trigger state. When the SimHub-reported gear value changes
        // and the user has the base "Gearshift vibration intensity" set above
        // zero, fire `base-gearshift-event` (cmd 0x76 on grp 0x2D) to make the
        // wheelbase rumble. PitHouse uses the same fire-and-forget event;
        // verified live 2026-05-10.
        // _lastGearString holds the most recently observed Gear value (string
        // — "R"/"N"/"1".."6"). Initial null suppresses the warm-up frame so we
        // don't fire on the first DataUpdate just because the previous value
        // was unset. Debounce window comes from settings (default 500 ms) to
        // coalesce ratcheting / rapid shifts.
        private string? _lastGearString;
        private DateTime _lastGearShiftSendUtc = DateTime.MinValue;

        // Fire a one-shot `base-gearshift-event` when the SimHub-reported gear
        // value changes, gated by `_data.GearshiftVibration > 0` (the user's
        // intensity slider — 0 disables the effect entirely) and a user-tunable
        // debounce so quick ratcheting up/down of gears doesn't pile triggers
        // on the wire.
        //
        // By default, transitions *into* neutral ("N" or "0") never fire — they
        // represent dis-engagement, not a shift. An H-pattern shift produces
        // two transitions ("1"→"N"→"2"); we want the bump on engagement (N→2),
        // not on the stick leaving the prior gear (1→N). Sequential / paddle
        // shifts go directly between gears and still fire normally. The user
        // can opt in to bumping on neutral via `GearshiftVibrateOnNeutral`.
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
            if (isNeutral && !_settings.GearshiftVibrateOnNeutral) return;
            int debounceMs = _settings.GearshiftDebounceMs;
            if (debounceMs < 0) debounceMs = 0;
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

            // Stash the freshest RPM + game-running flag for the AB9 engine-vib
            // worker thread (read at ~91 Hz from a separate thread). Interlocked
            // exchange because `double` cannot be `volatile` in C#.
            double rpm = data.NewData?.Rpms ?? 0.0;
            Interlocked.Exchange(ref _latestRpmBits, BitConverter.DoubleToInt64Bits(rpm));
            _latestGameRunning = data.GameRunning;
        }

        // ===== AB9 engine-vibration worker =====
        //
        // Runs as a dedicated background thread (not a System.Timers.Timer) because
        // the latter only delivers ~64 Hz under the default 15.6 ms Windows clock
        // granularity. Latest-wins stream lane in MozaSerialConnection means a
        // late tick just discards the previous frame from the slot — no FIFO
        // pile-up. We target 91 Hz; the actual wire rate is whatever the OS
        // scheduler delivers and that is harmless.
        private void Ab9EngineVibrationLoop()
        {
            long stopwatchFreq = System.Diagnostics.Stopwatch.Frequency;
            long periodTicks = stopwatchFreq * 11 / 1000;   // ~11 ms = 91 Hz
            long next = System.Diagnostics.Stopwatch.GetTimestamp() + periodTicks;
            while (!_ab9EngineVibStop)
            {
                try { ProcessEngineVibTick(); }
                catch (Exception ex)
                {
                    MozaLog.Debug($"[Moza/AB9] engine-vib tick: {ex.Message}");
                }

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                long deltaTicks = next - now;
                if (deltaTicks <= 0)
                {
                    // Fell behind by more than one tick — reset the deadline so
                    // we don't busy-loop firing back-to-back catch-up frames.
                    next = now + periodTicks;
                    continue;
                }
                int sleepMs = (int)Math.Min(50, Math.Max(1, deltaTicks * 1000 / stopwatchFreq));
                Thread.Sleep(sleepMs);
                next += periodTicks;
            }
        }

        private void ProcessEngineVibTick()
        {
            if (IsShuttingDown) return;
            if (_ab9Manager == null || !_ab9Manager.IsConnected || !_ab9Detected) return;

            var ab9 = _settings?.ProfileStore?.CurrentProfile?.Ab9;
            if (ab9 == null) return;

            int intensity = ab9.EngineVibrationIntensity;
            double freqHz = ab9.EngineVibrationFrequency;   // literal Hz, 0..300
            double rpm = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _latestRpmBits));
            bool gameOn = _latestGameRunning;

            bool active = gameOn && intensity > 0 && rpm > 100.0 && freqHz > 0.0;
            uint period;
            if (active)
            {
                double p = EngineVibK / (rpm * freqHz);
                if (p < MozaAb9DeviceManager.MinPeriodTicks)
                    p = MozaAb9DeviceManager.MinPeriodTicks;
                if (p > MozaAb9DeviceManager.MaxPeriodTicks)
                    p = MozaAb9DeviceManager.MaxPeriodTicks;
                period = (uint)p;
            }
            else
            {
                // Mid-range filler when silent. PitHouse leaves the last active
                // value in the bytes; we pick a stable midpoint so the frame
                // payload stays well-formed even if a future AB9 firmware
                // revision starts validating the period field strictly.
                period = 0x100000;
            }

            _ab9Manager.SendEngineVibrationStream(active, period);
            if (active != _ab9EngineVibActive)
            {
                _ab9EngineVibActive = active;
                MozaLog.Debug($"[Moza/AB9] engine-vib {(active ? "active" : "silent")} "
                              + $"(rpm={rpm:F0} freq={freqHz:F1}Hz period={period})");
            }
        }

        // Resolve a dashboard name to its parsed MultiStreamProfile without firing
        // the ApplyTelemetrySettings full-stack reload (which sets Profile + Mzdash
        // bytes synchronously, racing the renegotiate state machine when used to
        // trigger a switch). Mirror of the resolution precedence in
        // ApplyTelemetrySettings (cache → builtin), minus the mzdash-bytes loading.
        // Used by DashboardSwitchAutoTest to atomically pass a profile through
        // SwitchToProfile without touching telemetry.Profile beforehand.
        internal MultiStreamProfile? ResolveDashboardProfileByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (DashCache != null)
            {
                var p = DashCache.TryGetByName(name);
                if (p != null) return p;
            }
            var builtins = DashProfileStore.BuiltinProfiles;
            if (builtins.Count > 0)
                return FindProfile(builtins, name);
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
            // are disposed; the tick already gates on _ab9Manager.IsConnected
            // but joining here keeps shutdown deterministic.
            _ab9EngineVibStop = true;
            try { _ab9EngineVibThread?.Join(1000); } catch { }
            _ab9EngineVibThread = null;

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
                if (_subscribedProfileStore != null)
                    _subscribedProfileStore.CurrentProfileChanged -= OnProfileChanged;
                _subscribedProfileStore = null;
            }
            catch { }

            // 4. Stop telemetry send loop before tearing down connection.
            _telemetrySender?.Stop();

            // Stop the wire-traffic capture singleton so its file handle is
            // released and the ring stops accumulating across plugin reloads.
            try { global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.StopFileSink(); } catch { }
            try { global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.Stop(); } catch { }

            // 5. Cancel paced setting-read tasks so they bail out of their
            //    inter-read sleeps instead of running ~300 ms past teardown.
            try { _deviceManager?.Dispose(); } catch { }

            // 6. Dispose I/O sources before dropping Instance so late callbacks
            //    see a live (but shutting-down) instance, not null.
            _hidReader?.Dispose();
            _telemetrySender?.Dispose();
            _connection?.Dispose();
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
            // Mirror the active flat Wheel* fields into the per-wheel-model slot
            // so each physical wheel keeps its own brightness/mode/input settings
            // across reloads (see MozaPluginSettings.PerWheelSlots).
            _settings.MirrorActiveToSlot(_data?.WheelModelName);
            // Mirror dashboard telemetry settings into the per-UID slot so the
            // active profile follows the physical wheel across hot-swaps. Skipped
            // entirely if the wheel hasn't identified itself (UID empty) — we'd
            // otherwise collapse to the "" key and bleed settings between wheels.
            var uid = _data?.WheelMcuUid;
            if (uid != null && uid.Length == 12 && _settings.TelemetryByWheelUid != null)
            {
                string uidHex = BitConverter.ToString(uid).Replace("-", "").ToLowerInvariant();
                _settings.TelemetryByWheelUid[uidHex] = new TelemetryWheelSlot
                {
                    TelemetryEnabled     = _settings.TelemetryEnabled,
                    TelemetryProfileName = _settings.TelemetryProfileName ?? "",
                    TelemetryMzdashPath  = _settings.TelemetryMzdashPath  ?? "",
                };
            }
            ScheduleSave();
        }

        private void PersistSettings()
        {
            ScheduleSave();
        }

        private readonly object _saveDebounceLock = new object();

        /// <summary>
        /// Debounce disk writes: restart a 500ms timer on each call.
        /// Prevents dozens of writes per second during rapid slider drags.
        /// </summary>
        private void ScheduleSave()
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
                _baseDetected = false;
                _data.BaseSettingsRead = false;
                _dashDetected = false;
                _newWheelDetected = false;
                _oldWheelDetected = false;
                WheelModelInfo = null;
                _handbrakeDetected = false;
                _pedalsDetected = false;
                _hubDetected = false;
                _ab9Detected = false;
                _ab9SettingsApplied = false;
                _ab9Manager?.Disconnect();
                if (_telemetrySender != null)
                {
                    _telemetrySender.DetectedDeviceMask = 0;
                }
                _deviceManager.ResetWheelDetection();
                Interlocked.Exchange(ref _telemetryStartRequested, 0);
                _wheelPollMisses = 0;
                _lastKnownWheelModel = "";
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
            this.AttachDelegate("Moza.McuTemp", () => _data == null ? 0.0 : ConvertTemp(_data.McuTemp));
            this.AttachDelegate("Moza.MosfetTemp", () => _data == null ? 0.0 : ConvertTemp(_data.MosfetTemp));
            this.AttachDelegate("Moza.MotorTemp", () => _data == null ? 0.0 : ConvertTemp(_data.MotorTemp));
            this.AttachDelegate("Moza.BaseState", () => _data?.BaseState ?? 0);
            this.AttachDelegate("Moza.FfbStrength", () => (_data?.FfbStrength ?? 0) / 10);
            this.AttachDelegate("Moza.MaxAngle", () => (_data?.MaxAngle ?? 0) * 2);
        }

        private double ConvertTemp(int raw)
        {
            double celsius = raw / 100.0;
            return (_data?.UseFahrenheit ?? false) ? celsius * 9.0 / 5.0 + 32.0 : celsius;
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
        private void ClearLedsOnHardware()
        {
            if (!_connection.IsConnected) return;
            int rpmCount = WheelModelInfo?.RpmLedCount ?? 0;
            _deviceManager.WriteArray("wheel-send-rpm-telemetry",
                Devices.MozaLedDeviceManager.BuildRpmBitmaskBytes(0, rpmCount));
            _deviceManager.WriteArray("wheel-send-buttons-telemetry", new byte[] { 0, 0 });
            _deviceManager.WriteSetting("wheel-old-send-telemetry", 0);
            _deviceManager.WriteSetting("dash-send-telemetry", 0);
        }

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

        // Bandwidth + wire-error counters from the serial connection. Surfaced
        // in the Diagnostics tab so the user can see when the link approaches
        // saturation, and how many parse failures the read path is shedding.
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

        /// <summary>Apply settings from MozaPluginSettings to the TelemetrySender.</summary>
        internal void ApplyTelemetrySettings()
        {
            if (_telemetrySender == null) return;
            var s = _settings;

            // One-shot legacy migration drainers. Both run only when the
            // current TelemetryWheelEra is still Auto (= no explicit pick) so
            // a user who has already chosen an era doesn't get reset.
            if (s.TelemetryWheelEra == MozaWheelEra.Auto)
            {
                // Old MozaFirmwareEra integer values, captured by the
                // [JsonProperty("TelemetryFirmwareEra")] mapping on
                // TelemetryFirmwareEraLegacy. -1 = sentinel (no legacy value).
                if (s.TelemetryFirmwareEraLegacy >= 0)
                {
                    s.TelemetryWheelEra = s.TelemetryFirmwareEraLegacy switch
                    {
                        1 /* TierDefV2_Upload8B */ => MozaWheelEra.Era2025,
                        2 /* TierDefV2_Upload6B */ => MozaWheelEra.Era2025,
                        4 /* TierDefV0_Upload6B */ => MozaWheelEra.Era2024,
                        5 /* TierDefV2_Type02   */ => MozaWheelEra.Era2026,
                        _ /* 0 Auto or unknown  */ => MozaWheelEra.Auto,
                    };
                    s.TelemetryFirmwareEraLegacy = -1;
                }
                // Even older saved settings used the int property
                // TelemetryProtocolVersion (0=URL, 2=compact). The plan
                // corrects the prior 6B mapping so 0.8.0 VGS users land on
                // Era2025 (working) instead of Era2026 (broken).
                else if (s.TelemetryProtocolVersion != -1)
                {
                    s.TelemetryWheelEra = s.TelemetryProtocolVersion == 0
                        ? MozaWheelEra.Era2024
                        : MozaWheelEra.Era2025;
                    s.TelemetryProtocolVersion = -1;
                }
            }

            // Build the per-era policy and hand it to the v1 telemetry sender.
            // EraPolicy.For carries every wire-protocol axis (tier-def session,
            // encoding, preamble policy, blind-retransmit, upload header,
            // protocol version). Auto returns a provisional Era2026 policy
            // with IsAuto=true; TelemetrySender.ResolveAutoPolicy at session
            // start replaces it once the wheel reveals itself.
            if (_telemetrySender != null)
            {
                _telemetrySender.Policy = EraPolicy.For(s.TelemetryWheelEra);
                // UI for dashboard upload/download is hidden in SettingsControl.xaml while the
                // feature is in development; force both off regardless of the saved settings.
                // Saved values (s.TelemetryUploadDashboard / s.TelemetryDownloadDashboard) are
                // preserved on disk so re-enabling the UI restores the user's prior preference.
                _telemetrySender.UploadDashboard = false;
                _telemetrySender.SetDownloadEnabled(false);
                if (s.EnableAutoTestOnConnect)
                    _telemetrySender.EnableAutoTest(this);
            }

            // Resolve the active multi-stream profile and raw mzdash content.
            //
            // Precedence:
            //   1. User picked a custom mzdash file (TelemetryMzdashPath set
            //      and exists) → parse it, use its channel list + bytes.
            //   2. User picked a dashboard by name → look up in DashboardCache
            //      (populated from wheel download via session 0x0B), then fall
            //      back to builtin embedded profiles.
            //   3. Default → leave profile null. TelemetrySender's
            //      MaybeSwapProfileForCatalog builds a synthetic
            //      "WheelCatalog" profile from the wheel's session-0x02
            //      catalog push.
            MultiStreamProfile? profile = null;
            byte[]? mzdashContent = null;
            string mzdashName = "";

            if (!string.IsNullOrEmpty(s.TelemetryMzdashPath) && System.IO.File.Exists(s.TelemetryMzdashPath))
            {
                profile = DashProfileStore.ParseMzdash(s.TelemetryMzdashPath);
                mzdashContent = System.IO.File.ReadAllBytes(s.TelemetryMzdashPath);
                mzdashName = System.IO.Path.GetFileNameWithoutExtension(s.TelemetryMzdashPath);
            }
            else if (!string.IsNullOrEmpty(s.TelemetryProfileName))
            {
                // Try cache first (populated from wheel download or disk).
                if (DashCache != null)
                {
                    profile = DashCache.TryGetByName(s.TelemetryProfileName);
                    if (profile != null)
                    {
                        mzdashName = profile.Name;
                        mzdashContent = DashCache.TryGetRawContent(s.TelemetryProfileName);
                        MozaLog.Debug($"[Moza] ApplyTelemetrySettings: found '{s.TelemetryProfileName}' in cache as '{mzdashName}'");
                    }
                    else
                    {
                        MozaLog.Debug($"[Moza] ApplyTelemetrySettings: '{s.TelemetryProfileName}' NOT found in cache (folder={DashCache.FolderProfileCount} wheel={DashCache.WheelCacheCount})");
                    }
                }

                // Fall back to builtin embedded profiles when cache misses.
                if (profile == null)
                {
                    var builtins = DashProfileStore.BuiltinProfiles;
                    if (builtins.Count > 0)
                    {
                        profile = FindProfile(builtins, s.TelemetryProfileName);
                        if (profile != null && mzdashContent == null)
                        {
                            mzdashName = profile.Name;
                            string resourceName = $"MozaPlugin.Data.Dashes.{profile.Name.Replace(" ", "_")}.mzdash";
                            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                            using var stream = assembly.GetManifestResourceStream(resourceName);
                            if (stream != null)
                            {
                                using var ms = new System.IO.MemoryStream();
                                stream.CopyTo(ms);
                                mzdashContent = ms.ToArray();
                            }
                        }
                    }
                }
            }
            // else: catalog-only mode — profile stays null, sender will
            // synthesise from wheel-advertised channels post-preamble.

            // Apply user channel mappings for the selected dashboard (overrides
            // each channel's SimHubProperty string by URL). Must run before
            // assigning Profile so the frame builder binds resolvers correctly.
            // Walk candidate dashboard keys (wheel:<id>, file:<...>, builtin:<name>)
            // under the current wheel UID; first hit wins so a freshly-saved
            // wheel:<id> entry overrides any orphaned legacy file:<...> entry.
            if (profile != null && s.TelemetryChannelMappingsByWheel != null)
            {
                string wheelKey = CurrentWheelKey();
                if (s.TelemetryChannelMappingsByWheel.TryGetValue(wheelKey, out var middle) &&
                    middle != null)
                {
                    foreach (var dashKey in GetActiveDashboardKeyCandidates())
                    {
                        if (middle.TryGetValue(dashKey, out var overrides) && overrides != null)
                        {
                            DashboardProfileStore.ApplyUserMappings(profile, overrides);
                            break;
                        }
                    }
                }
            }

            var sender = _telemetrySender!;
            sender.PropertyResolver = ResolvePropertyAsDouble;
            int tierCount = profile?.Tiers?.Count ?? 0;
            int chCount = 0;
            if (profile != null)
                foreach (var t in profile.Tiers) chCount += t.Channels.Count;
            MozaLog.Debug(
                $"[Moza] ApplyTelemetrySettings: setting profile=" +
                $"{profile?.Name ?? "null"} tiers={tierCount} channels={chCount} " +
                $"mzdash={mzdashName} settingName={s.TelemetryProfileName}");
            sender.Profile = profile;
            sender.MzdashContent = mzdashContent;
            sender.MzdashName = mzdashName;



            // Advertise dashboard library to the wheel on session 0x09.
            // Wheel echoes names in its next configJson state blob.
            // Use cached dashboard names (from wheel download) plus any
            // builtin profiles, with cache winning on overlap.
            var libraryNames = new System.Collections.Generic.List<string>();
            if (DashCache != null)
            {
                foreach (var name in DashCache.CachedNames)
                    libraryNames.Add(name);
            }
            foreach (var p in DashProfileStore.BuiltinProfiles)
            {
                if (!libraryNames.Contains(p.Name))
                    libraryNames.Add(p.Name);
            }
            if (!string.IsNullOrEmpty(mzdashName) && !libraryNames.Contains(mzdashName))
                libraryNames.Add(mzdashName);
            if (_telemetrySender != null)
                _telemetrySender.CanonicalDashboardList = libraryNames;
        }

        /// <summary>
        /// Per-frame resolver for channels with a user-mapped SimHubProperty.
        /// Paths starting with <c>@internal/</c> are plugin-computed values
        /// (e.g. live wheel angle from the HID reader) and bypass SimHub.
        /// All other paths resolve via <c>PluginManager.GetPropertyValue</c>.
        /// </summary>
        private double ResolvePropertyAsDouble(string path)
        {
            if (!string.IsNullOrEmpty(path) && path.StartsWith("@internal/", StringComparison.Ordinal))
                return ResolveInternalChannel(path);

            return PropertyCoercion.Coerce(
                _pluginManager?.GetPropertyValue(path), path);
        }

        // Latched once per plugin lifetime so a SimHub API change doesn't spam the log.
        private bool _allPropertiesNamesWarned;

        /// <summary>
        /// Snapshot of every property name SimHub currently exposes (DataCorePlugin.GameData.*,
        /// game-specific Acc.Physics.* / R3E.* / etc., plus paths registered by other plugins).
        /// Used by the channel-mappings ComboBox autocomplete. Sorted case-insensitively.
        /// Falls back to the curated <see cref="KnownSimHubProperties.Paths"/> list when
        /// the live API is unavailable (PluginManager null, exception, or missing method).
        /// </summary>
        public IReadOnlyList<string> GetAllSimHubPropertyNames()
        {
            try
            {
                var pm = _pluginManager;
                if (pm != null)
                {
                    // PluginManager.GetAllPropertiesNames() returns IEnumerable<string> — verified
                    // in libs/SimHub/SimHub.Plugins.dll. Guarded by reflection so older SimHub
                    // builds without this method degrade to the static fallback.
                    var mi = pm.GetType().GetMethod("GetAllPropertiesNames",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                    if (mi != null)
                    {
                        var names = mi.Invoke(pm, null) as System.Collections.IEnumerable;
                        if (names != null)
                        {
                            // SimHub can register the same property under multiple
                            // plugins (DataCorePlugin / Plugins.PersistantTrackerPlugin
                            // overlap, Custom Series), and the autocomplete dropdown
                            // would otherwise show duplicates. Dedupe here so every
                            // consumer of the live list gets a unique-name snapshot.
                            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var list = new List<string>(256);
                            foreach (var n in names)
                            {
                                if (n is string s && !string.IsNullOrEmpty(s) && seen.Add(s))
                                    list.Add(s);
                            }
                            list.Sort(StringComparer.OrdinalIgnoreCase);
                            return list;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_allPropertiesNamesWarned)
                {
                    _allPropertiesNamesWarned = true;
                    MozaLog.Warn("[Moza] GetAllPropertiesNames failed; falling back to static list: " + ex.Message);
                }
            }
            return KnownSimHubProperties.Paths;
        }

        /// <summary>
        /// Resolve the current raw value of a SimHub property for UI display in the
        /// channel-mappings grid. Returns null when the path is empty, internal-only,
        /// or unresolvable. The caller formats for display.
        /// </summary>
        public object? GetPropertyValueForDisplay(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            // @internal/ values bypass SimHub — surface them as the resolved double
            // so the UI shows the live wheel angle etc. instead of "(not found)".
            if (path!.StartsWith("@internal/", StringComparison.Ordinal))
                return ResolveInternalChannel(path);
            try { return _pluginManager?.GetPropertyValue(path); }
            catch { return null; }
        }

        private double ResolveInternalChannel(string path)
        {
            switch (path)
            {
                case "@internal/SteeringWheelAngle":
                {
                    // Live wheel angle in degrees, centred at 0. Uses the base's
                    // reported max-angle (half-range ± maxAngleDeg/2). Falls back
                    // to 0 when HID is disconnected or max angle hasn't been read.
                    var hid = _hidReader;
                    int maxAngleDeg = _data?.MaxAngle * 2 ?? 0;
                    if (hid == null || maxAngleDeg <= 0) return 0.0;
                    return hid.GetCurrentAngleDegrees(maxAngleDeg);
                }
                default:
                    return 0.0;
            }
        }

        /// <summary>
        /// Stable per-physical-wheel key. 24-char lowercase hex of the wheel's STM32 MCU
        /// UID (firmware-resident, persists across plugin reload and SimHub restart).
        /// Returns "" when the UID hasn't been read yet (cold start before wheel detect)
        /// or when all bytes are zero — both treated as "unknown wheel" so mappings still
        /// land somewhere instead of being dropped on the floor.
        /// </summary>
        internal string CurrentWheelKey()
        {
            var uid = _data?.WheelMcuUid;
            if (uid == null || uid.Length != 12) return "";
            bool allZero = true;
            for (int i = 0; i < 12; i++) { if (uid[i] != 0) { allZero = false; break; } }
            if (allZero) return "";
            var sb = new StringBuilder(24);
            for (int i = 0; i < 12; i++) sb.Append(uid[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Candidate dashboard keys for the user's currently-selected dashboard,
        /// highest priority first:
        /// <list type="number">
        ///   <item><c>wheel:&lt;id&gt;</c> — when configJson reports a matching enabled-dashboard entry with a non-empty Id (stable across re-uploads)</item>
        ///   <item><c>file:&lt;filename&gt;:&lt;sha1-first-8&gt;</c> — when a file path is resolvable</item>
        ///   <item><c>builtin:&lt;name&gt;</c> — fallback for embedded profiles</item>
        /// </list>
        /// Caller iterates the list when looking up; primary writer uses index 0.
        ///
        /// Reads from <c>_settings.TelemetryProfileName</c> / <c>TelemetryMzdashPath</c>
        /// (the user-intent source of truth — updates synchronously in the dropdown
        /// SelectionChanged handler) and only falls back to <c>_telemetrySender.Profile.Name</c>
        /// when settings is empty. Earlier the candidate list mirrored the running
        /// pipeline's profile, which lags settings between SelectionChanged and the
        /// async ApplyTelemetrySettings → caused stale TelemetryDashboardKey saves
        /// (every-other-switch loss across game switches) and a redundant Stop+Start
        /// at startup when the saved key didn't appear to match the (still-null)
        /// active profile.
        /// </summary>
        internal IReadOnlyList<string> GetActiveDashboardKeyCandidates()
        {
            var s = _settings;
            string profileName = s?.TelemetryProfileName ?? "";
            string mzdashPath = s?.TelemetryMzdashPath ?? "";

            // Settings empty (cold launch before any selection) → fall back to
            // the running profile's name so we still produce candidates if
            // telemetry happens to be assembled from a non-settings source.
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
                // GetDashboardKey reads profile?.Name only in the loadedPath==null
                // branch (which we don't take here — keyPath is non-empty). Pass
                // the running profile if we have one but it's unused.
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
        /// Live-rewire the host-side value source for a single channel without
        /// touching the wire — finds the matching channel in the active profile by
        /// URL and updates its <see cref="ChannelDefinition.SimHubProperty"/> in place.
        /// The frame builder's resolver lambdas read this field per-frame (see
        /// <see cref="TelemetryFrameBuilder"/>), so the new property is used on the
        /// very next telemetry frame. Safe to call while telemetry is running.
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
            string wheelKey = CurrentWheelKey();

            if (_settings.TelemetryChannelMappingsByWheel == null)
                _settings.TelemetryChannelMappingsByWheel =
                    new System.Collections.Generic.Dictionary<string,
                        System.Collections.Generic.Dictionary<string,
                            System.Collections.Generic.Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

            if (!_settings.TelemetryChannelMappingsByWheel.TryGetValue(wheelKey, out var middle))
            {
                middle = new System.Collections.Generic.Dictionary<string,
                    System.Collections.Generic.Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                _settings.TelemetryChannelMappingsByWheel[wheelKey] = middle;
            }

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
                if (middle.Count == 0) _settings.TelemetryChannelMappingsByWheel.Remove(wheelKey);
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
            string wheelKey = CurrentWheelKey();
            if (_settings.TelemetryChannelMappingsByWheel == null) return;
            if (!_settings.TelemetryChannelMappingsByWheel.TryGetValue(wheelKey, out var middle)) return;

            bool changed = false;
            foreach (var key in candidates)
            {
                if (middle.Remove(key)) changed = true;
            }
            if (middle.Count == 0)
                _settings.TelemetryChannelMappingsByWheel.Remove(wheelKey);
            if (changed) SaveSettings();
        }

        /// <summary>
        /// Restart the telemetry session with current settings. Called when protocol version,
        /// flag byte mode, or other send options change in the UI.
        /// </summary>
        internal void RestartTelemetry()
        {
            var t = _telemetrySender;
            if (t == null) return;
            Interlocked.Exchange(ref _telemetryStartRequested, 0);
            ApplyTelemetrySettings();
            if (!_settings.TelemetryEnabled) return;
            // Bypass StartTelemetryIfReady() — its FramesSent > 0 guard
            // rejects restarts when the sender is already running.
            // StartInner() calls Stop() as its first action, resetting all
            // state (sessions, flag base, preamble) for a true cold-start.
            if (Interlocked.CompareExchange(ref _telemetryStartRequested, 1, 0) != 0) return;
            MozaLog.Info("[Moza] Restarting telemetry sender (full cold-start)");
            ThreadPool.QueueUserWorkItem(_ => t.Start());
        }

        // Set when ApplyProfile sees a TelemetryDashboardKey but the wheel state isn't
        // ready yet (cold start, hub re-enumeration, etc.). PollStatus retries once
        // the wheel catalog arrives. Cleared after a successful apply or when the user
        // manually picks a dashboard (manual action wins over a stale profile setting).
        private string? _pendingProfileDashboardKey;
        private long _pendingProfileDashboardKeyDeadlineTicks;
        // Stop retrying after this long so a profile authored against a different wheel
        // catalog doesn't pin the pending-key forever.
        private static readonly TimeSpan PendingProfileKeyTimeout = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Raised when the active telemetry dashboard selection is updated
        /// programmatically (profile load / deferred retry). UI controls that
        /// mirror Settings.TelemetryProfileName / TelemetryMzdashPath should
        /// subscribe and re-sync. Fired on the thread that updated settings —
        /// subscribers must marshal to the UI thread before touching WPF.
        /// Not fired for UI-originated changes (combo SelectionChanged etc.)
        /// because the UI is already in sync there.
        /// </summary>
        public event EventHandler? DashboardSelectionChanged;

        private void RaiseDashboardSelectionChanged()
        {
            int subs = DashboardSelectionChanged?.GetInvocationList().Length ?? 0;
            MozaLog.Debug(
                $"[Moza] Raising DashboardSelectionChanged (subscribers={subs}, " +
                $"profileName='{_settings?.TelemetryProfileName}', " +
                $"mzdash='{_settings?.TelemetryMzdashPath}')");
            try { DashboardSelectionChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { MozaLog.Warn("[Moza] DashboardSelectionChanged subscriber threw: " + ex.Message); }
        }

        /// <summary>
        /// Apply <see cref="MozaProfile.TelemetryDashboardKey"/> to the wheel: switch
        /// the active dashboard so each SimHub game/profile gets its own. Defers to
        /// the next poll tick when the wheel state isn't ready.
        /// Returns true when the key was applied or dropped (no further retry needed).
        /// </summary>
        internal bool ApplyTelemetryDashboardFromProfile(MozaProfile profile)
        {
            if (profile == null) return true;
            string? key = profile.TelemetryDashboardKey;
            if (string.IsNullOrEmpty(key)) return true; // no preference recorded

            // Already on the requested dashboard? No-op — avoid a redundant
            // Stop+Start. The candidate list reads from _settings (the user-intent
            // source of truth), so this matches at plugin startup when the saved
            // profile key equals the user's last selection — no needless restart
            // cycle that would arm IsInSilenceCooldown and silently suppress the
            // user's first manual SendDashboardSwitch.
            try
            {
                var current = GetActiveDashboardKeyCandidates();
                foreach (var c in current)
                {
                    if (string.Equals(c, key, StringComparison.OrdinalIgnoreCase))
                    {
                        MozaLog.Debug("[Moza] ApplyTelemetryDashboardFromProfile: already on " + key + " — no-op");
                        return true;
                    }
                }
            }
            catch { /* fall through to apply attempt */ }

            if (key!.StartsWith("wheel:", StringComparison.OrdinalIgnoreCase))
            {
                string id = key.Substring("wheel:".Length);
                var sender = _telemetrySender;
                var state = WheelStateForDiagnostics;
                if (state == null || state.EnabledDashboards == null || sender == null)
                    return false; // not ready — caller defers

                WheelDashboardEntry? match = null;
                foreach (var entry in state.EnabledDashboards)
                {
                    if (entry != null && string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        match = entry;
                        break;
                    }
                }
                if (match == null)
                {
                    MozaLog.Info("[Moza] Profile dashboard key not found in current wheel catalog (id=" +
                                 id + "); leaving current selection");
                    return true; // wheel doesn't have this dashboard; stop retrying
                }

                // Locate the slot in ConfigJsonList (UI-ordered library names) by name.
                int slot = -1;
                if (state.ConfigJsonList != null)
                {
                    for (int i = 0; i < state.ConfigJsonList.Count; i++)
                    {
                        var name = state.ConfigJsonList[i];
                        if (string.IsNullOrEmpty(name)) continue;
                        if (string.Equals(name, match.Title, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, match.DirName, StringComparison.OrdinalIgnoreCase))
                        {
                            slot = i;
                            break;
                        }
                    }
                }
                if (slot < 0)
                {
                    MozaLog.Info("[Moza] Profile dashboard '" + match.Title +
                                 "' missing from configJsonList; leaving current selection");
                    return true;
                }

                MozaLog.Info($"[Moza] Applying profile dashboard '{match.Title}' (id={id}, slot={slot})");
                _settings.TelemetryProfileName = match.Title;
                _settings.TelemetryMzdashPath = "";
                PersistSettings();
                OnDashboardSwitched((uint)slot);
                RaiseDashboardSelectionChanged();
                return true;
            }

            if (key.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                // file:<filename>:<sha1-first-8> — try to resolve from DashCache (folder
                // library or wheel-download cache) by filename. If we can't find the
                // exact file we don't fail loudly — the user can always re-pick.
                string remainder = key.Substring("file:".Length);
                int colon = remainder.LastIndexOf(':');
                string filename = colon > 0 ? remainder.Substring(0, colon) : remainder;
                string? path = DashCache?.TryGetFolderFilePath(System.IO.Path.GetFileNameWithoutExtension(filename));
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    MozaLog.Info("[Moza] Applying profile dashboard file: " + path);
                    _settings.TelemetryMzdashPath = path!;
                    _settings.TelemetryProfileName = "";
                    PersistSettings();
                    OnDashboardSwitched();
                    RaiseDashboardSelectionChanged();
                    return true;
                }
                MozaLog.Info("[Moza] Profile dashboard file not resolvable (" + filename +
                             "); leaving current selection");
                return true;
            }

            if (key.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
            {
                string name = key.Substring("builtin:".Length);
                MozaLog.Info("[Moza] Applying profile dashboard (builtin): " + name);
                _settings.TelemetryProfileName = name;
                _settings.TelemetryMzdashPath = "";
                PersistSettings();
                OnActiveDashboardChanged();
                RaiseDashboardSelectionChanged();
                return true;
            }

            MozaLog.Warn("[Moza] Unknown TelemetryDashboardKey prefix: " + key);
            return true;
        }

        internal void OnActiveDashboardChanged()
        {
            // Manual action wins: user picked a dashboard from the UI dropdown,
            // so abandon any pending profile-driven switch waiting for the catalog.
            _pendingProfileDashboardKey = null;

            var sender = _telemetrySender;
            if (sender != null && sender.Enabled)
            {
                MozaLog.Debug("[Moza] OnActiveDashboardChanged: scheduling Stop+Start pipeline cycle");

                // Same pattern as OnDashboardSwitched. Builtin-profile fallback
                // path (user picked "(none)" or a non-Custom profile name with
                // no wheel-state available) — re-stage settings and cycle the
                // pipeline. Silence gate inside StartInner enforces the
                // ~11s wheel sess=0x09 timeout wait automatically.
                ApplyTelemetrySettings();
                sender.RestartForSwitch();
                return;
            }
        }

        /// <summary>
        /// Slot-aware dashboard switch entry point. Used when the wheel's
        /// configJsonList provides a 0-based slot index (UI dropdown selection,
        /// profile-driven switch by name). The TelemetrySender emits FF kind=4
        /// on session 0x02 and waits for the wheel's b2h kind=4 echo (with
        /// retry) before tearing the pipeline down for the new tier-def.
        /// </summary>
        internal void OnDashboardSwitched(uint slot)
        {
            _pendingProfileDashboardKey = null;

            var sender = _telemetrySender;
            if (sender != null && sender.Enabled)
            {
                MozaLog.Debug(
                    $"[Moza] OnDashboardSwitched(slot={slot}): scheduling switch + Stop+Start pipeline cycle");

                // Stage the new dashboard's profile + mzdash content INTO the
                // sender first so the post-Start cold-start sequence builds
                // tier-def from the right channels. ApplyTelemetrySettings
                // sets Profile + MzdashContent + MzdashName as side effects.
                ApplyTelemetrySettings();

                // SwitchToProfile(slot, null) emits the FF kind=4 then runs
                // Stop+Start. Profile was already staged above, so we pass
                // null and the existing Profile sticks.
                sender.SwitchToProfile(slot, null);
                return;
            }
        }

        /// <summary>
        /// Slot-less dashboard switch entry point. Used by file-mode and
        /// builtin-fallback paths where the wheel's configJsonList doesn't
        /// expose a slot for the new dashboard — no FF kind=4 is emitted; the
        /// sender just cycles Stop+Start so the new profile takes effect.
        /// </summary>
        internal void OnDashboardSwitched()
        {
            _pendingProfileDashboardKey = null;

            var sender = _telemetrySender;
            if (sender != null && sender.Enabled)
            {
                MozaLog.Debug("[Moza] OnDashboardSwitched: scheduling Stop+Start pipeline cycle (no slot)");
                ApplyTelemetrySettings();
                sender.RestartForSwitch();
                return;
            }
        }

        internal void SetTelemetryEnabled(bool enabled)
        {
            _settings.TelemetryEnabled = enabled;
            SaveSettings();
            if (enabled)
            {
                ApplyTelemetrySettings();
                StartTelemetryIfReady();
            }
            else
            {
                _telemetrySender?.Stop();
                // Reset guards so re-enable can start a fresh session.
                // Without this, FramesSent > 0 and _telemetryStartRequested == 1
                // cause StartTelemetryIfReady() to bail out on re-enable.
                Interlocked.Exchange(ref _telemetryStartRequested, 0);
            }
        }

        /// <summary>
        /// Start the telemetry sender only if preconditions are met:
        /// connection is up, a wheel is detected, telemetry is enabled, and
        /// a profile is loaded. Called from device detection and profile application.
        /// The session open probe requires the wheel to be present and responsive —
        /// starting before detection wastes time and may send to an uninitialized device.
        ///
        /// Dispatches Start() to a background thread because ProbeAndOpenSessions()
        /// blocks waiting for ack responses delivered by the serial read thread.
        /// Calling Start() directly on the read thread would deadlock.
        /// </summary>
        private void StartTelemetryIfReady()
        {
            var t = _telemetrySender;
            if (t == null) return;
            if (!_settings.TelemetryEnabled) return;
            if (!_connection.IsConnected) return;
            if (!_newWheelDetected && !_oldWheelDetected) return;
            // Capability gate: known displayless wheels (CS V2.1, GS V2P, etc.)
            // never get the dashboard pipeline. For unknown models falls back to
            // the runtime display probe; if the probe also stays silent the
            // sess=0x09 retry-exhaust path in TelemetrySender parks the pipeline
            // before it can wedge the port.
            if (!ShouldDriveDashboard())
            {
                MozaLog.Info(
                    $"[Moza] Wheel '{_data?.WheelModelName}' has no display " +
                    $"(HasDisplay={WheelModelInfo?.HasDisplay?.ToString() ?? "unknown"}, " +
                    $"probe={IsDisplayDetected}) — skipping dashboard telemetry start");
                return;
            }
            // Profile may be null when no .mzdash is loaded and no builtin
            // profiles are bundled. Sender starts anyway; preamble parses the
            // wheel-advertised catalog (Type02 firmware pushes it unconditionally)
            // and MaybeSwapProfileForCatalog synthesises a WheelCatalog profile
            // post-preamble.

            // Already running — don't restart (avoids re-probing ports mid-session).
            if (t.FramesSent > 0) return;

            // Prevent duplicate dispatch (multiple callers may pass the guards above
            // before the background thread increments FramesSent)
            if (Interlocked.CompareExchange(ref _telemetryStartRequested, 1, 0) != 0) return;

            MozaLog.Info("[Moza] Wheel detected and telemetry enabled — starting telemetry sender");
            // Wrap the work item in a top-level catch so any exception from
            // Start() (e.g. ObjectDisposedException from a race against
            // plugin Dispose during game-switch reload, IOException from a
            // serial port that closed mid-probe) is logged instead of
            // propagating up through ThreadPool — on .NET Framework 4.8
            // unhandled exceptions in ThreadPool callbacks can take down
            // the SimHub host process.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { t.Start(); }
                catch (ObjectDisposedException) { /* plugin disposed mid-start */ }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] Telemetry start failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        private static MultiStreamProfile? FindProfile(
            System.Collections.Generic.IReadOnlyList<MultiStreamProfile> profiles, string name)
        {
            foreach (var p in profiles)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        private void TryConnect()
        {
            if (Interlocked.CompareExchange(ref _connectingFlag, 1, 0) != 0)
                return;

            try
            {
                // If we had a wheel detected before reconnecting, reset it.
                // The serial port may have dropped during a wheel swap.
                if (_newWheelDetected || _oldWheelDetected)
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

        /// <summary>
        /// Attempt to open the AB9 shifter's COM port. Independent of the
        /// wheelbase pipe — the AB9 enumerates as its own VID_346E composite
        /// (PID 0x1000) and runs on a dedicated CDC port. Identity probe runs
        /// on every successful (re-)connect; saved settings are pushed once
        /// the first read response confirms the device is alive.
        /// </summary>
        private void TryConnectAb9()
        {
            if (_ab9Manager == null) return;
            if (_ab9Detected)
            {
                // Connection dropped after a successful detection — clear so the
                // next read response can re-trigger profile push.
                _ab9Detected = false;
                _ab9SettingsApplied = false;
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

        private int _wheelPollMisses;
        private const int WheelMissThreshold = 3;
        private volatile string _lastKnownWheelModel = "";

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
            if (_newWheelDetected || _oldWheelDetected || _dashDetected)
                ResetWheelDetection("Serial disconnect — resetting wheel detection");
        }

        /// <summary>
        /// Clear ALL device-detection flags. Called at the top of both Init() and
        /// End() so a plugin reload (load → unload → load same process) doesn't
        /// carry over stale "device detected" state from a prior session. Differs
        /// from <see cref="ResetWheelDetection"/> which is scoped to wheel hot-swap
        /// recovery and intentionally preserves base/hub/handbrake/pedals state.
        /// </summary>
        private void ResetDetectionFlags()
        {
            _baseDetected = false;
            _dashDetected = false;
            _newWheelDetected = false;
            _oldWheelDetected = false;
            _handbrakeDetected = false;
            _pedalsDetected = false;
            _hubDetected = false;
            _ab9Detected = false;
            _ab9SettingsApplied = false;
        }

        private void ResetWheelDetection(string reason)
        {
            MozaLog.Debug($"[Moza] {reason}");
            _telemetrySender?.Stop();
            _newWheelDetected = false;
            _oldWheelDetected = false;
            _dashDetected = false;
            WheelModelInfo = null;
            Interlocked.Exchange(ref _wheelLedGroupMask, 0);
            _group3ColorsRead = false;
            _data.ClearWheelIdentity();
            _deviceManager.ResetWheelDetection();
            if (_telemetrySender != null)
                _telemetrySender.DetectedDeviceMask = 0;
            Interlocked.Exchange(ref _telemetryStartRequested, 0);
            _wheelPollMisses = 0;
            _lastKnownWheelModel = "";
        }


        private void PollStatus(object sender, ElapsedEventArgs e)
        {
            if (IsShuttingDown) return;
            if (!_connection.IsConnected) return;

            // Retry a deferred profile-driven dashboard switch once the wheel catalog
            // arrives. Stops retrying after PendingProfileKeyTimeout so a profile
            // authored against a different wheel doesn't pin this state forever.
            if (_pendingProfileDashboardKey != null)
            {
                if (DateTime.UtcNow.Ticks > _pendingProfileDashboardKeyDeadlineTicks)
                {
                    MozaLog.Info("[Moza] Pending profile dashboard apply timed out (key=" +
                                 _pendingProfileDashboardKey + "); giving up");
                    _pendingProfileDashboardKey = null;
                }
                else
                {
                    var profile = _settings.ProfileStore?.CurrentProfile;
                    if (profile != null &&
                        string.Equals(profile.TelemetryDashboardKey, _pendingProfileDashboardKey, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (ApplyTelemetryDashboardFromProfile(profile))
                                _pendingProfileDashboardKey = null;
                        }
                        catch (Exception ex)
                        {
                            MozaLog.Warn("[Moza] Pending dashboard apply retry threw: " + ex.Message);
                            _pendingProfileDashboardKey = null;
                        }
                    }
                    else
                    {
                        // Profile changed under us — drop the stale pending key.
                        _pendingProfileDashboardKey = null;
                    }
                }
            }

            // Hot-swap detection: track whether the locked wheel is still responding
            // and periodically verify the model name hasn't changed.
            if (_newWheelDetected || _oldWheelDetected)
            {
                if (_deviceManager.WheelRespondedSinceLastPoll)
                {
                    _wheelPollMisses = 0;
                }
                else
                {
                    _wheelPollMisses++;
                    if (_wheelPollMisses >= WheelMissThreshold)
                    {
                        ResetWheelDetection(
                            $"Wheel on ID {_deviceManager.WheelDeviceId} not responding " +
                            $"({_wheelPollMisses} misses) — resetting for hot-swap");
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
            if (!_newWheelDetected && !_oldWheelDetected)
                _deviceManager.ProbeWheelDetection();
            if (!_dashDetected)
                _deviceManager.ReadSetting("dash-rpm-indicator-mode");
            if (!_handbrakeDetected)
                _deviceManager.ReadSetting("handbrake-direction");
            if (!_pedalsDetected)
                _deviceManager.ReadSetting("pedals-throttle-dir");
            if (!_hubDetected)
                _deviceManager.ReadSetting("hub-port1-power");

            // Re-probe wheel display sub-device until detected. The initial
            // probe at wheel-detect time can race wheel-side power-up and
            // return only partial identity (presence + identity-11 with
            // model/HW/FW/MCU still empty), which leaves IsDisplayDetected
            // false and hides the dashboard-telemetry UI section. PitHouse
            // re-probes periodically until the display answers fully — mirror
            // that here so the section auto-appears once the display is awake.
            if (_newWheelDetected && !IsDisplayDetected)
                _deviceManager.SendDisplayProbe();

            // Read Group 3 ring LED colors once after group detected + model resolved
            if (!_group3ColorsRead && _newWheelDetected && IsWheelLedGroupPresent(3))
            {
                var model = WheelModelInfo;
                if (model?.KnobRingLeds != null && model.KnobRingLedTotal > 0)
                {
                    _group3ColorsRead = true;
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
            if (_hubDetected)
                _deviceManager.ReadSettings(HubReadCommands);
        }

        private volatile int _unmatched;

        private void OnMessageReceived(byte[] data)
        {
            // Bail out during shutdown — serial reader thread may deliver a frame
            // after End() began detaching state. Without this, _data/_deviceManager
            // accesses below could hit a half-disposed object.
            if (IsShuttingDown) return;

            // Filter firmware debug noise before parsing/logging
            if (data.Length >= 1 && data[0] == MozaProtocol.FirmwareDebugGroup)
                return;

            // Filter SerialStream control frames (group 0xC3 response to 0x43,
            // payload starts with 7C/FC + 00). These are session-management
            // chunks (fc:00 session opens/acks, 7c:00 data) handled by
            // TelemetrySender's session-layer handlers — not command responses.
            // Without this, sessions 0x01..0x0E opens spam Unmatched log lines.
            if (data.Length >= 4 && data[0] == MozaProtocol.SerialStreamRespGroup &&
                (data[2] == MozaProtocol.SerialStreamOpcodeData ||
                 data[2] == MozaProtocol.SerialStreamOpcodeCtrl) && data[3] == 0x00)
                return;

            // Filter wheel's `7c:23` dashboard-activate advertisements (group
            // 0xC3 device 0x71, payload starts with `7C 23`). Wheel broadcasts
            // active display config periodically — informational, not a command
            // response. Absorbed by TelemetrySender.
            if (data.Length >= 4 && data[0] == MozaProtocol.SerialStreamRespGroup
                && data[2] == MozaProtocol.SerialStreamOpcodeData && data[3] == 0x23)
                return;

            // Filter group 0x40 channel-config burst echoes (0xC0 response):
            //   1E 00 XX / 1E 01 XX — channel enable read per page
            //   28 00 / 28 01 / 28 02 — WheelGetCfg_GetMultiFunction{Switch,Num,Left}
            // Part of the channel configuration burst; wheel returns the stored
            // EEPROM value per channel/query. Not actionable at plugin level —
            // just confirms the probe landed. Mark wheel alive so watchdog
            // doesn't reset detection.
            if (data.Length >= 4 && data[0] == MozaProtocol.WheelChannelCfgRespGroup
                && data[1] == MozaProtocol.WheelDeviceIdSwapped
                && (data[2] == MozaProtocol.WheelCfgOpcodeChannelEnable ||
                    data[2] == MozaProtocol.WheelCfgOpcodeMultiFunction))
            {
                // Capture raw 28:00 / 28:01 reply bytes before swallowing.
                // PitHouse polls these at ~1 Hz across all four bridge captures
                // (sim/logs/bridge-20260503-*.jsonl). Semantics not yet decoded
                // — store raw so future controlled experiments can correlate
                // values against game state.
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
                // Known wheel write echoes that have no command DB entry: silently
                // treat as a keepalive from the wheel device id. Avoids unmatched-log
                // spam and keeps wheel-alive tracking accurate for LED/page-config
                // writes that firmware echoes verbatim (see MozaProtocol.WheelEchoPrefixes).
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

            _data.UpdateFromCommand(r.Name, r.IntValue);
            if (r.ArrayValue != null)
                _data.UpdateFromArray(r.Name, r.ArrayValue);

            // Extended LED group presence — any response to a brightness/mode/color
            // command from one of the named groups proves it exists in firmware.
            // Maps the user-facing prefix back to the rs21_parameter.db group ID.
            if (r.Name != null)
            {
                int g = -1;
                if (r.Name.StartsWith("wheel-single-",  StringComparison.Ordinal)) g = 2;
                else if (r.Name.StartsWith("wheel-knob-",    StringComparison.Ordinal)) g = 3;
                else if (r.Name.StartsWith("wheel-ambient-", StringComparison.Ordinal)) g = 4;
                if (g >= 2 && g <= 4)
                {
                    int bit = 1 << g;
                    int prev;
                    do
                    {
                        prev = _wheelLedGroupMask;
                        if ((prev & bit) != 0) break;
                    } while (Interlocked.CompareExchange(ref _wheelLedGroupMask, prev | bit, prev) != prev);
                    if ((prev & bit) == 0)
                        MozaLog.Debug($"[Moza] Wheel LED group {g} detected");
                }
            }

            _deviceManager.MarkWheelResponse(r.DeviceId);
            if (r.Name != null)
                DetectDevices(r.Name, r.IntValue, r.DeviceId);
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

            var result = MozaResponseParser.Parse(data);
            if (!result.HasValue) return;

            var r = result.Value;
            if (r.Name == null || !r.Name.StartsWith("ab9-", StringComparison.Ordinal))
                return;

            _ab9Manager.MarkDetected();
            if (!_ab9Detected)
            {
                _ab9Detected = true;
                ApplySavedAb9Settings();
            }

            MozaLog.Debug($"[Moza/AB9] {r.Name} = {r.IntValue}");
        }

        /// <summary>
        /// Push the current profile's AB9 settings (mode + 5 sliders) to the
        /// device. Runs once per AB9 connection — the device retains values in
        /// flash, so re-pushing on every response would just create flash wear.
        /// </summary>
        private void ApplySavedAb9Settings()
        {
            if (_ab9SettingsApplied) return;
            var profile = _settings?.ProfileStore?.CurrentProfile;
            var ab9 = profile?.Ab9;
            if (ab9 == null)
            {
                _ab9SettingsApplied = true;
                return;
            }

            MozaLog.Debug("[Moza/AB9] Applying saved AB9 settings");
            _ab9Manager.SendMode(ab9.Mode);
            _ab9Manager.SendSlider(Devices.Ab9Slider.MechanicalResistance, ab9.MechanicalResistance);
            _ab9Manager.SendSlider(Devices.Ab9Slider.Spring, ab9.Spring);
            _ab9Manager.SendSlider(Devices.Ab9Slider.NaturalDamping, ab9.NaturalDamping);
            _ab9Manager.SendSlider(Devices.Ab9Slider.NaturalFriction, ab9.NaturalFriction);
            _ab9Manager.SendSlider(Devices.Ab9Slider.MaxTorqueLimit, ab9.MaxTorqueLimit);
            // Push the persisted gear-shift-vibration intensity. AB9 firmware
            // stores it and applies it autonomously on every HID-detected gear
            // engagement — no host trigger needed per shift.
            _ab9Manager.SendGearShiftVibrationIntensity(ab9.GearShiftVibrationIntensity);
            _ab9SettingsApplied = true;
        }

        /// <summary>
        /// Auto-detect connected devices based on response commands.
        ///   - dash-rpm-indicator-mode responds -> dashboard present
        ///   - wheel-telemetry-mode responds -> new protocol wheel (GS/FSR/CS/RS/TSW)
        ///   - wheel-rpm-value1 responds (but not telemetry-mode) -> old protocol wheel (ES)
        /// </summary>
        private void DetectDevices(string commandName, int value, byte deviceId)
        {
            // wheel-mcu-uid response starts with 0xBE... which parses to a negative
            // int32 via ParseIntValue(BE). Log it before the `value < 0` guard
            // below, because UpdateFromArray has already stored the raw 12 bytes.
            if (commandName == "wheel-mcu-uid" && _data.WheelMcuUid.Length > 0)
            {
                MozaLog.Debug(
                    $"[Moza] Wheel MCU UID ({_data.WheelMcuUid.Length}B): " +
                    MozaLog.RedactBytesHex(_data.WheelMcuUid));

                // Per-wheel mzdash folder auto-switch (mapping populated by the
                // Auto-detect button). Runs on the serial-reader thread; never
                // touch WPF here — the 500 ms RefreshWheel tick repopulates the
                // settings UI. Telemetry restart is unnecessary; dash-detection
                // below triggers ApplyTelemetrySettings on the new folder.
                if (_data.WheelMcuUid.Length == 12 && _settings.WheelMzdashFolderByUid != null)
                {
                    string uidHex = BitConverter.ToString(_data.WheelMcuUid).Replace("-", "").ToLowerInvariant();
                    if (_settings.WheelMzdashFolderByUid.TryGetValue(uidHex, out var mappedFolder)
                        && !string.IsNullOrEmpty(mappedFolder)
                        && System.IO.Directory.Exists(mappedFolder)
                        && !string.Equals(mappedFolder, _settings.TelemetryMzdashFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        MozaLog.Debug($"[Moza] Auto-switching mzdash folder for wheel {uidHex}: {mappedFolder}");
                        _settings.TelemetryMzdashFolder = mappedFolder;
                        SaveSettings();
                        DashCache?.LoadFromFolder(mappedFolder);
                    }
                }

                // Per-wheel dashboard telemetry slot. UID-keyed so the VGS profile
                // doesn't follow a CS V2.1 across hot-swaps. This is the EARLIEST
                // point where it's safe to read/write the slot — the wheel has now
                // self-identified, so no risk of collapsing onto the "" key.
                if (_data.WheelMcuUid.Length == 12 && _settings.TelemetryByWheelUid != null)
                {
                    string uidHex = BitConverter.ToString(_data.WheelMcuUid).Replace("-", "").ToLowerInvariant();
                    bool slotChanged = false;
                    if (_settings.TelemetryByWheelUid.TryGetValue(uidHex, out var slot) && slot != null)
                    {
                        // Existing slot wins over the in-memory flat fields, which
                        // may still belong to the previously-connected wheel.
                        MozaLog.Debug(
                            $"[Moza] Per-wheel telemetry slot loaded for {uidHex}: " +
                            $"enabled={slot.TelemetryEnabled}, profile='{slot.TelemetryProfileName}', mzdash='{slot.TelemetryMzdashPath}'");
                        _settings.TelemetryEnabled     = slot.TelemetryEnabled;
                        _settings.TelemetryProfileName = slot.TelemetryProfileName ?? "";
                        _settings.TelemetryMzdashPath  = slot.TelemetryMzdashPath  ?? "";
                        slotChanged = true;
                    }
                    else if (!string.IsNullOrEmpty(_settings.TelemetryProfileName)
                             || !string.IsNullOrEmpty(_settings.TelemetryMzdashPath)
                             || _settings.TelemetryEnabled)
                    {
                        // First-touch seeding: upgrading from a pre-UID-slot build
                        // (or first time this wheel has been connected since the
                        // feature shipped). Attach the current flat selection to
                        // this wheel's UID once, then save. Future connects of a
                        // different wheel won't inherit this selection because the
                        // flat fields will have been overwritten by THAT wheel's
                        // slot (or left empty if it has none).
                        _settings.TelemetryByWheelUid[uidHex] = new TelemetryWheelSlot
                        {
                            TelemetryEnabled     = _settings.TelemetryEnabled,
                            TelemetryProfileName = _settings.TelemetryProfileName ?? "",
                            TelemetryMzdashPath  = _settings.TelemetryMzdashPath  ?? "",
                        };
                        MozaLog.Debug(
                            $"[Moza] Per-wheel telemetry slot seeded for {uidHex} from flat: " +
                            $"enabled={_settings.TelemetryEnabled}, profile='{_settings.TelemetryProfileName}', mzdash='{_settings.TelemetryMzdashPath}'");
                        slotChanged = true;
                    }

                    // HasDisplay==false override. Forces telemetry off for known
                    // displayless wheels regardless of what's in the slot — defends
                    // against a slot hand-edited to enable telemetry on a wheel
                    // that physically can't render it.
                    if (WheelModelInfo?.HasDisplay == false && _settings.TelemetryEnabled)
                    {
                        MozaLog.Info(
                            $"[Moza] Wheel '{_data.WheelModelName}' has no display (UID {uidHex}) " +
                            $"— forcing TelemetryEnabled=false");
                        _settings.TelemetryEnabled = false;
                        if (_settings.TelemetryByWheelUid.TryGetValue(uidHex, out var s) && s != null && s.TelemetryEnabled)
                        {
                            s.TelemetryEnabled = false;
                            slotChanged = true;
                        }
                    }

                    if (slotChanged)
                    {
                        SaveSettings();
                        try { ApplyTelemetrySettings(); }
                        catch (Exception ex)
                        {
                            MozaLog.Warn($"[Moza] ApplyTelemetrySettings after UID slot load failed: {ex.Message}");
                        }
                    }
                }

                return;
            }
            if (commandName == "display-mcu-uid" && _data.DisplayMcuUid.Length > 0)
            {
                MozaLog.Debug(
                    $"[Moza] Display MCU UID ({_data.DisplayMcuUid.Length}B): " +
                    MozaLog.RedactBytesHex(_data.DisplayMcuUid));
                return;
            }

            if (value < 0) return; // No valid response

            // Update telemetry sender's heartbeat mask so it only pings detected devices.
            // (DetectedDeviceMask is a TelemetrySender-only field — new pipeline doesn't
            // use the same heartbeat-gating; it sends keepalives unconditionally.)
            if (deviceId >= 18 && deviceId <= 30 && _telemetrySender != null)
                _telemetrySender.DetectedDeviceMask |= (1 << (deviceId - 18));

            // Base detection: IsBaseConnected was just set to true by UpdateFromCommand.
            // Re-apply the profile so base settings (FFB, damper, limit, etc.) are written to device.
            if (commandName == "base-mcu-temp" && !_baseDetected)
            {
                _baseDetected = true;
                MozaLog.Info("[Moza] Base detected");
                // Apply profile first (queues writes), then read settings (queues reads).
                // Since the write queue is FIFO, the device processes writes before reads,
                // so read responses reflect the values we just wrote.
                var profile = _settings.ProfileStore.CurrentProfile;
                if (profile != null)
                    ApplyProfile(profile);
                _deviceManager.ReadSettings(BaseSettingsReadCommands);
            }

            switch (commandName)
            {
                case "dash-rpm-indicator-mode":
                    if (!_dashDetected)
                    {
                        _dashDetected = true;
                        if (DeviceDefinitionDeployer.DeployDashboard(_connection.DiscoveredPid))
                            DeviceDefinitionDeployed = true;
                        ApplySavedDashSettings();
                        _deviceManager.ReadSettings(DashSettingsReadCommands);
                        MozaLog.Info("[Moza] Dashboard detected");
                    }
                    break;

                case "wheel-telemetry-mode":
                    if (!_newWheelDetected && !_oldWheelDetected)
                    {
                        _newWheelDetected = true;
                        _deviceManager.LockWheelId(deviceId);
                        // Writes first so device has saved values before reads return
                        ApplySavedWheelSettings();
                        _deviceManager.ReadSetting("wheel-model-name");
                        _deviceManager.ReadSetting("wheel-sw-version");
                        _deviceManager.ReadSetting("wheel-hw-version");
                        _deviceManager.ReadSetting("wheel-serial-a");
                        _deviceManager.ReadSetting("wheel-serial-b");
                        // Match PitHouse's full 12-frame identity handshake (adds the 7 probes
                        // ReadSetting doesn't cover: 0x09/0x02/0x04/0x05/0x06/0x08-sub2/0x11).
                        _deviceManager.SendPithouseIdentityProbe(deviceId);
                        // Probe the wheel's Display sub-device. Runs at wheel detect
                        // — independent of TelemetrySender — so IsDisplayDetected
                        // flips before the user picks a profile. The dashboard-
                        // telemetry UI section is gated on detection; without this
                        // probe, that section stays hidden and the user can never
                        // opt in.
                        _deviceManager.SendDisplayProbe();
                        _deviceManager.ReadSettingsPaced(NewWheelSettingsReadCommands);
                        MozaLog.Info($"[Moza] New-protocol wheel detected on ID {deviceId}");
                        // Telemetry start is deferred until the wheel-model-name
                        // response is processed (see the "wheel-model-name" case
                        // below). Calling StartTelemetryIfReady() here would run
                        // ShouldDriveDashboard() against a null WheelModelInfo and
                        // an unanswered display probe, so the gate would skip
                        // dashboard for every new-protocol wheel — including ones
                        // with a display.
                    }
                    else if (deviceId != _deviceManager.WheelDeviceId)
                    {
                        // Hot-swap: a wheel responded on a different ID than the locked one.
                        ResetWheelDetection(
                            $"New wheel responded on ID {deviceId} (was locked on " +
                            $"{_deviceManager.WheelDeviceId}) — hot-swap detected");
                    }
                    break;

                case "wheel-model-name":
                    // Only resolve per-model LED config for new-protocol wheels.
                    // ES wheels share device 0x13 with the base, so the model name
                    // response is the base name, not the wheel name.
                    if (_newWheelDetected)
                    {
                        var currentModel = _data.WheelModelName;

                        // Ignore empty/truncated responses — would falsely trigger reset.
                        if (string.IsNullOrEmpty(currentModel))
                            break;

                        // Hot-swap: if model name changed, a different wheel was attached.
                        if (!string.IsNullOrEmpty(_lastKnownWheelModel) &&
                            _lastKnownWheelModel != currentModel)
                        {
                            ResetWheelDetection(
                                $"Wheel model changed from '{_lastKnownWheelModel}' " +
                                $"to '{currentModel}' — hot-swap detected");
                            break;
                        }

                        // First time seeing this model — resolve LED layout and deploy
                        if (string.IsNullOrEmpty(_lastKnownWheelModel))
                        {
                            _lastKnownWheelModel = currentModel;
                            WheelModelInfo = Devices.WheelModelInfo.FromModelName(currentModel);
                            MozaLog.Debug(
                                $"[Moza] Wheel model: {currentModel} " +
                                $"(rpm={WheelModelInfo.RpmLedCount}, buttons={WheelModelInfo.ButtonLedCount}, flags={WheelModelInfo.HasFlagLeds}, knobs={WheelModelInfo.KnobCount})");
                            // Load this wheel model's persisted slot (brightness/modes/inputs)
                            // into the active flat fields before any hardware writes fire.
                            // Seeds a new slot from current flat values on first encounter.
                            if (_settings.PerWheelSlots.ContainsKey(currentModel))
                                _settings.LoadSlotIntoActive(currentModel);
                            else
                                _settings.MirrorActiveToSlot(currentModel);
                            if (DeviceDefinitionDeployer.DeployForModel(currentModel, _connection.DiscoveredPid))
                                DeviceDefinitionDeployed = true;

                            // Refresh _data knob colours from the slot we just loaded
                            // and push to hardware (W17/W18 only — KnobCount gate).
                            MozaProfile.UnpackColorsInto(_settings.WheelKnobBackgroundColors, _data.WheelKnobBackgroundColors);
                            MozaProfile.UnpackColorsInto(_settings.WheelKnobPrimaryColors,    _data.WheelKnobPrimaryColors);
                            WriteKnobColors(_settings.WheelKnobBackgroundColors, _settings.WheelKnobPrimaryColors);
                            // Ring colors pushed after Group 3 is detected (via PollStatus read trigger)

                            // Now that the wheel has identified itself, the
                            // capability gate (ShouldDriveDashboard) has real
                            // input and StartTelemetryIfReady can make the
                            // correct keep-or-skip decision. Deferred from
                            // the deviceId-detection site above which fires
                            // before this response lands.
                            StartTelemetryIfReady();
                        }
                    }
                    else
                    {
                        MozaLog.Debug(
                            $"[Moza] Wheel model (ES/base): {_data.WheelModelName}");
                    }
                    break;

                case "wheel-sw-version":
                    MozaLog.Debug($"[Moza] Wheel FW: {_data.WheelSwVersion}");
                    break;

                case "wheel-serial-b":
                    if (!string.IsNullOrEmpty(_data.WheelSerialNumber))
                        MozaLog.Debug($"[Moza] Wheel serial: {MozaLog.RedactId(_data.WheelSerialNumber)}");
                    break;

                case "wheel-hw-sub":
                    if (!string.IsNullOrEmpty(_data.WheelHwSubVersion))
                        MozaLog.Debug($"[Moza] Wheel HW sub: {_data.WheelHwSubVersion}");
                    break;

                case "wheel-mcu-uid":
                    if (_data.WheelMcuUid.Length > 0)
                        MozaLog.Debug(
                            $"[Moza] Wheel MCU UID ({_data.WheelMcuUid.Length}B): " +
                            MozaLog.RedactBytesHex(_data.WheelMcuUid));
                    break;

                case "wheel-device-type":
                    if (_data.WheelDeviceType.Length > 0)
                        MozaLog.Debug(
                            $"[Moza] Wheel device type: {BitConverter.ToString(_data.WheelDeviceType)}");
                    break;

                case "wheel-capabilities":
                    if (_data.WheelCapabilities.Length > 0)
                        MozaLog.Debug(
                            $"[Moza] Wheel capabilities: {BitConverter.ToString(_data.WheelCapabilities)}");
                    break;

                case "wheel-presence":
                    MozaLog.Debug(
                        $"[Moza] Wheel presence/ready: sub_device_count={_data.WheelSubDeviceCount}");
                    break;

                case "wheel-device-presence":
                    MozaLog.Debug(
                        $"[Moza] Wheel device presence byte: 0x{_data.WheelDevicePresence:X2}");
                    break;

                case "wheel-identity-11":
                    if (_data.WheelIdentity11.Length > 0)
                        MozaLog.Debug(
                            $"[Moza] Wheel identity-11: {BitConverter.ToString(_data.WheelIdentity11)}");
                    break;

                // Display sub-device identity responses (wrapped via 0x43)
                case "display-model-name":
                    if (!string.IsNullOrEmpty(_data.DisplayModelName))
                    {
                        MozaLog.Debug($"[Moza] Display model: {_data.DisplayModelName}");
                        // For wheels not in KnownModels (WheelModelInfo.HasDisplay==null),
                        // the display probe response is the authoritative "wheel has a
                        // display" signal. Trigger StartTelemetryIfReady here so the
                        // ShouldDriveDashboard() fallback path (HasDisplay==null →
                        // IsDisplayDetected) actually starts the pipeline once the probe
                        // lands. No-op for known wheels — those started in the
                        // wheel-model-name handler.
                        StartTelemetryIfReady();
                    }
                    break;
                case "display-hw-version":
                    if (!string.IsNullOrEmpty(_data.DisplayHwVersion))
                        MozaLog.Debug($"[Moza] Display HW: {_data.DisplayHwVersion}");
                    break;
                case "display-sw-version":
                    if (!string.IsNullOrEmpty(_data.DisplaySwVersion))
                        MozaLog.Debug($"[Moza] Display FW: {_data.DisplaySwVersion}");
                    break;
                case "display-serial":
                    if (!string.IsNullOrEmpty(_data.DisplaySerialNumber))
                        MozaLog.Debug($"[Moza] Display serial: {MozaLog.RedactId(_data.DisplaySerialNumber)}");
                    break;
                case "display-presence":
                    MozaLog.Debug(
                        $"[Moza] Display presence/ready: sub_device_count={_data.DisplaySubDeviceCount}");
                    break;
                case "display-device-presence":
                    MozaLog.Debug(
                        $"[Moza] Display device presence byte: 0x{_data.DisplayDevicePresence:X2}");
                    break;
                case "display-device-type":
                    if (_data.DisplayDeviceType.Length > 0)
                        MozaLog.Debug(
                            $"[Moza] Display device type: {BitConverter.ToString(_data.DisplayDeviceType)}");
                    break;
                case "display-capabilities":
                    if (_data.DisplayCapabilities.Length > 0)
                        MozaLog.Debug(
                            $"[Moza] Display capabilities: {BitConverter.ToString(_data.DisplayCapabilities)}");
                    break;
                case "display-identity-11":
                    if (_data.DisplayIdentity11.Length > 0)
                        MozaLog.Debug(
                            $"[Moza] Display identity-11: {BitConverter.ToString(_data.DisplayIdentity11)}");
                    break;
                case "display-mcu-uid":
                    // Logged before value<0 guard (see top of DetectDevices). Not hit here.
                    break;

                case "wheel-rpm-value1":
                    if (!_newWheelDetected && !_oldWheelDetected)
                    {
                        _oldWheelDetected = true;
                        _deviceManager.LockWheelId(deviceId);
                        ApplySavedWheelSettings();
                        _deviceManager.ReadSetting("wheel-model-name");
                        _deviceManager.ReadSetting("wheel-sw-version");
                        _deviceManager.ReadSetting("wheel-hw-version");
                        _deviceManager.ReadSetting("wheel-serial-a");
                        _deviceManager.ReadSetting("wheel-serial-b");
                        _deviceManager.SendPithouseIdentityProbe(deviceId);
                        _deviceManager.ReadSettingsPaced(OldWheelSettingsReadCommands);
                        if (DeviceDefinitionDeployer.DeployOldProtoWheel(_connection.DiscoveredPid))
                            DeviceDefinitionDeployed = true;
                        MozaLog.Info($"[Moza] Old-protocol wheel detected on ID {deviceId}");
                        StartTelemetryIfReady();
                    }
                    else if (deviceId != _deviceManager.WheelDeviceId)
                    {
                        ResetWheelDetection(
                            $"New wheel responded on ID {deviceId} (was locked on " +
                            $"{_deviceManager.WheelDeviceId}) — hot-swap detected");
                    }
                    break;

                case "handbrake-direction":
                    if (!_handbrakeDetected)
                    {
                        _handbrakeDetected = true;
                        ApplySavedHandbrakeSettings();
                        _deviceManager.ReadSettings(HandbrakeSettingsReadCommands);
                        MozaLog.Info("[Moza] Handbrake detected");
                    }
                    break;

                case "pedals-throttle-dir":
                    if (!_pedalsDetected)
                    {
                        _pedalsDetected = true;
                        ApplySavedPedalSettings();
                        _deviceManager.ReadSettings(PedalsSettingsReadCommands);
                        MozaLog.Info("[Moza] Pedals detected");
                    }
                    break;

                case "hub-port1-power":
                    if (!_hubDetected)
                    {
                        _hubDetected = true;
                        _deviceManager.ReadSettings(HubReadCommands);
                        // Mirror to the connection so TelemetrySender's hub-only
                        // 5-slot enumeration burst still fires for hub-attached
                        // wheels. Pre-registry, this flag was set by the probe
                        // path (HubProbeSucceeded) at port-discovery time; with
                        // registry-based discovery we don't probe, so the first
                        // 0xE4 hub reply is now the trigger.
                        try { _connection.MarkHubDetected(); } catch { }
                        MozaLog.Info("[Moza] Universal Hub detected");
                    }
                    break;
            }
        }
        /// <summary>
        /// Send saved RPM timing settings to the wheel after detection.
        /// These values aren't retained by the wheel hardware.
        /// </summary>
        private void ApplySavedWheelSettings()
        {
            MozaLog.Debug("[Moza] Applying saved wheel settings");

            // Pre-populate _data from saved settings so the UI shows correct values
            // immediately, before device responses arrive.
            if (_settings.WheelTelemetryMode >= 0)
                _data.WheelTelemetryMode = _settings.WheelTelemetryMode;
            if (_settings.WheelIdleEffect >= 0)
                _data.WheelTelemetryIdleEffect = _settings.WheelIdleEffect;
            if (_settings.WheelButtonsIdleEffect >= 0)
                _data.WheelButtonsIdleEffect = _settings.WheelButtonsIdleEffect;
            if (_settings.WheelKnobIdleEffect >= 0)
                _data.WheelKnobIdleEffect = _settings.WheelKnobIdleEffect;
            if (_settings.WheelKnobLedMode >= 0)
                _data.WheelKnobLedMode = _settings.WheelKnobLedMode;
            if (_settings.WheelButtonsLedMode >= 0)
                _data.WheelButtonsLedMode = _settings.WheelButtonsLedMode;
            if (_settings.WheelTelemetryIdleSpeedMs >= 0)
                _data.WheelTelemetryIdleSpeedMs = _settings.WheelTelemetryIdleSpeedMs;
            if (_settings.WheelButtonsIdleSpeedMs >= 0)
                _data.WheelButtonsIdleSpeedMs = _settings.WheelButtonsIdleSpeedMs;
            if (_settings.WheelKnobIdleSpeedMs >= 0)
                _data.WheelKnobIdleSpeedMs = _settings.WheelKnobIdleSpeedMs;
            if (_settings.WheelSleepMode >= 0)
                _data.WheelIdleMode = _settings.WheelSleepMode;
            if (_settings.WheelSleepTimeoutMin >= 0)
                _data.WheelIdleTimeout = _settings.WheelSleepTimeoutMin;
            if (_settings.WheelSleepSpeedMs >= 0)
                _data.WheelIdleSpeed = _settings.WheelSleepSpeedMs;
            if (_settings.WheelSleepColor != null && _settings.WheelSleepColor.Length > 0)
            {
                var rgb = MozaProfile.UnpackColor(_settings.WheelSleepColor[0]);
                _data.WheelIdleColor[0] = rgb[0];
                _data.WheelIdleColor[1] = rgb[1];
                _data.WheelIdleColor[2] = rgb[2];
            }
            if (_settings.WheelRpmIndicatorMode >= 0)
                _data.WheelRpmIndicatorMode = _settings.WheelRpmIndicatorMode;
            if (_settings.WheelRpmDisplayMode >= 0)
                _data.WheelRpmDisplayMode = _settings.WheelRpmDisplayMode;
            _data.WheelRpmBrightness = _settings.WheelRpmBrightness;
            _data.WheelButtonsBrightness = _settings.WheelButtonsBrightness;
            _data.WheelFlagsBrightness = _settings.WheelFlagsBrightness;
            _data.WheelESRpmBrightness = _settings.WheelESRpmBrightness;

            // Input settings — preload from saved values so the UI shows the
            // last-known state even when the wheel silently ignores the read
            // (newer KS firmware doesn't respond to clutch-point / knob-mode).
            if (_settings.WheelPaddlesMode >= 0) _data.WheelPaddlesMode = _settings.WheelPaddlesMode;
            if (_settings.WheelClutchPoint >= 0) _data.WheelClutchPoint = _settings.WheelClutchPoint;
            if (_settings.WheelKnobMode    >= 0) _data.WheelKnobMode    = _settings.WheelKnobMode;
            if (_settings.WheelStickMode   >= 0) _data.WheelStickMode   = _settings.WheelStickMode;

            // Knob ring colors — write-only on the wire so the only persisted copy is here.
            // Unpack now so the UI picker reflects the saved colors even before the model
            // is resolved; hardware push happens in the wheel-model-name handler once we
            // know the wheel actually exposes knob rings.
            MozaProfile.UnpackColorsInto(_settings.WheelKnobBackgroundColors, _data.WheelKnobBackgroundColors);
            MozaProfile.UnpackColorsInto(_settings.WheelKnobPrimaryColors,    _data.WheelKnobPrimaryColors);

            // LED mode (only if previously saved)
            if (_settings.WheelTelemetryMode >= 0)
                _deviceManager.WriteSetting("wheel-telemetry-mode", _settings.WheelTelemetryMode);
            if (_settings.WheelIdleEffect >= 0)
                _deviceManager.WriteSetting("wheel-telemetry-idle-effect", _settings.WheelIdleEffect);
            if (_settings.WheelButtonsIdleEffect >= 0)
                _deviceManager.WriteSetting("wheel-buttons-idle-effect", _settings.WheelButtonsIdleEffect);
            if (_settings.WheelKnobIdleEffect >= 0)
                _deviceManager.WriteSetting("wheel-knob-idle-effect", _settings.WheelKnobIdleEffect);
            if (_settings.WheelKnobLedMode >= 0)
                _deviceManager.WriteSetting("wheel-knob-led-mode", _settings.WheelKnobLedMode);
            if (_settings.WheelButtonsLedMode >= 0)
                _deviceManager.WriteSetting("wheel-buttons-led-mode", _settings.WheelButtonsLedMode);
            // Per-effect speed sliders (cmd 0x1E [group] [effect_id] [BE u16 ms]).
            // Only fire when both effect and speed are saved — without an effect_id
            // we'd be writing a per-effect timer with effect=0 (Off) which has no
            // animation to time and therefore no observable effect on the wheel.
            if (_settings.WheelIdleEffect >= 0 && _settings.WheelTelemetryIdleSpeedMs >= 0)
                _deviceManager.WriteArray("wheel-telemetry-idle-interval",
                    BuildIdleIntervalPayload(_settings.WheelIdleEffect, _settings.WheelTelemetryIdleSpeedMs));
            if (_settings.WheelButtonsIdleEffect >= 0 && _settings.WheelButtonsIdleSpeedMs >= 0)
                _deviceManager.WriteArray("wheel-buttons-idle-interval",
                    BuildIdleIntervalPayload(_settings.WheelButtonsIdleEffect, _settings.WheelButtonsIdleSpeedMs));
            if (_settings.WheelKnobIdleEffect >= 0 && _settings.WheelKnobIdleSpeedMs >= 0)
                _deviceManager.WriteArray("wheel-knob-idle-interval",
                    BuildIdleIntervalPayload(_settings.WheelKnobIdleEffect, _settings.WheelKnobIdleSpeedMs));
            // Wheel sleep-light settings (cmd 0x20/0x21/0x22/0x24).
            if (_settings.WheelSleepMode >= 0)
                _deviceManager.WriteSetting("wheel-idle-mode", _settings.WheelSleepMode);
            if (_settings.WheelSleepTimeoutMin >= 0)
                _deviceManager.WriteSetting("wheel-idle-timeout", _settings.WheelSleepTimeoutMin);
            if (_settings.WheelSleepMode >= 0 && _settings.WheelSleepSpeedMs >= 0)
                _deviceManager.WriteArray("wheel-idle-speed",
                    BuildIdleIntervalPayload(_settings.WheelSleepMode, _settings.WheelSleepSpeedMs));
            if (_settings.WheelSleepColor != null && _settings.WheelSleepColor.Length > 0)
            {
                var rgb = MozaProfile.UnpackColor(_settings.WheelSleepColor[0]);
                _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
            }

            // ES/Old wheel modes
            if (_settings.WheelRpmIndicatorMode >= 0)
                _deviceManager.WriteSetting("wheel-rpm-indicator-mode", _settings.WheelRpmIndicatorMode + 1); // display→raw
            if (_settings.WheelRpmDisplayMode >= 0)
                _deviceManager.WriteSetting("wheel-set-rpm-display-mode", _settings.WheelRpmDisplayMode);

            // Brightness
            _deviceManager.WriteSetting("wheel-rpm-brightness", _settings.WheelRpmBrightness);
            _deviceManager.WriteSetting("wheel-buttons-brightness", _settings.WheelButtonsBrightness);
            // Flag brightness routes to the Meter sub-device via dash-flags-brightness
            // (RS21 DB: MeterSetCfg_SetFlagGroupBrightness_o). Only write when the dash
            // sub-device has responded; otherwise the write targets a device that's not present.
            if (_dashDetected)
                _deviceManager.WriteSetting("dash-flags-brightness", _settings.WheelFlagsBrightness);
            _deviceManager.WriteSetting("wheel-old-rpm-brightness", _settings.WheelESRpmBrightness);
        }

        /// <summary>
        /// Send saved dash brightness settings after detection.
        /// </summary>
        private void ApplySavedDashSettings()
        {
            MozaLog.Debug("[Moza] Applying saved dash settings");

            // Pre-populate _data from saved settings so the UI shows correct values
            _data.DashRpmBrightness = _settings.DashRpmBrightness;
            _data.DashFlagsBrightness = _settings.DashFlagsBrightness;
            _data.DashDisplayBrightness = _settings.DashDisplayBrightness;
            _data.DashDisplayStandbyMin = _settings.DashDisplayStandbyMin;

            // Brightness
            _deviceManager.WriteSetting("dash-rpm-brightness", _settings.DashRpmBrightness);
            _deviceManager.WriteSetting("dash-flags-brightness", _settings.DashFlagsBrightness);

            // Wheel-integrated display brightness + standby ride session-0x01
            // ff-record property push (see findings/2026-04-29-session-01-property-push.md).
            _telemetrySender?.SendDashDisplayBrightness(_settings.DashDisplayBrightness);
            _telemetrySender?.SendDashDisplayStandbyMinutes(_settings.DashDisplayStandbyMin);

            // Enable flag indicator mode (0=Off, 1=Flags, 2=On). Firmware default is 0,
            // which silently drops all flag colour/bitmask writes. Set to 1 so the plugin's
            // bitmask-driven LEDs actually display. Subsequent read of dash-flags-indicator-mode
            // (via DashSettingsReadCommands) refreshes _data for the UI combo.
            _deviceManager.WriteSetting("dash-flags-indicator-mode", 1);
        }

        /// <summary>
        /// Apply saved handbrake settings from the current profile after detection.
        /// Previously handbrake settings were only written if the handbrake was
        /// already detected when ApplyProfile ran — now they're written on detection.
        /// </summary>
        private void ApplySavedHandbrakeSettings()
        {
            var profile = _settings.ProfileStore.CurrentProfile;
            if (profile == null) return;
            MozaLog.Debug("[Moza] Applying saved handbrake settings");

            if (profile.HandbrakeMode >= 0)
            {
                _data.HandbrakeMode = profile.HandbrakeMode;
                _deviceManager.WriteSetting("handbrake-mode", profile.HandbrakeMode);
            }
            if (profile.HandbrakeButtonThreshold >= 0)
            {
                _data.HandbrakeButtonThreshold = profile.HandbrakeButtonThreshold;
                _deviceManager.WriteSetting("handbrake-button-threshold", profile.HandbrakeButtonThreshold);
            }
            if (profile.HandbrakeDirection >= 0)
            {
                _data.HandbrakeDirection = profile.HandbrakeDirection;
                _deviceManager.WriteSetting("handbrake-direction", profile.HandbrakeDirection);
            }
            if (profile.HandbrakeMin >= 0)
            {
                _data.HandbrakeMin = profile.HandbrakeMin;
                _deviceManager.WriteSetting("handbrake-min", profile.HandbrakeMin);
            }
            if (profile.HandbrakeMax >= 0)
            {
                _data.HandbrakeMax = profile.HandbrakeMax;
                _deviceManager.WriteSetting("handbrake-max", profile.HandbrakeMax);
            }
            if (profile.HandbrakeCurve != null)
            {
                for (int i = 0; i < Math.Min(5, profile.HandbrakeCurve.Length); i++)
                {
                    _data.HandbrakeCurve[i] = profile.HandbrakeCurve[i];
                    _deviceManager.WriteFloat($"handbrake-y{i + 1}", profile.HandbrakeCurve[i]);
                }
            }
        }

        /// <summary>
        /// Apply saved pedal settings from the current profile after detection.
        /// </summary>
        private void ApplySavedPedalSettings()
        {
            var profile = _settings.ProfileStore.CurrentProfile;
            if (profile == null) return;
            MozaLog.Debug("[Moza] Applying saved pedal settings");

            if (profile.PedalsThrottleDir >= 0)
            {
                _data.PedalsThrottleDir = profile.PedalsThrottleDir;
                _deviceManager.WriteSetting("pedals-throttle-dir", profile.PedalsThrottleDir);
            }
            if (profile.PedalsBrakeDir >= 0)
            {
                _data.PedalsBrakeDir = profile.PedalsBrakeDir;
                _deviceManager.WriteSetting("pedals-brake-dir", profile.PedalsBrakeDir);
            }
            if (profile.PedalsClutchDir >= 0)
            {
                _data.PedalsClutchDir = profile.PedalsClutchDir;
                _deviceManager.WriteSetting("pedals-clutch-dir", profile.PedalsClutchDir);
            }
            if (profile.PedalsBrakeAngleRatio >= 0)
            {
                _data.PedalsBrakeAngleRatio = profile.PedalsBrakeAngleRatio;
                _deviceManager.WriteFloat("pedals-brake-angle-ratio", profile.PedalsBrakeAngleRatio);
            }
            ApplyCurveIfSet(profile.PedalsThrottleCurve, _data.PedalsThrottleCurve, "pedals-throttle-y", true);
            ApplyCurveIfSet(profile.PedalsBrakeCurve, _data.PedalsBrakeCurve, "pedals-brake-y", true);
            ApplyCurveIfSet(profile.PedalsClutchCurve, _data.PedalsClutchCurve, "pedals-clutch-y", true);
        }

        // ===== Profile system (SimHub native) =====

        /// <summary>
        /// Initialize the native SimHub profile system.
        /// ProfileSettingsBase.Init() reads the current game from PluginManager and selects the right profile.
        /// </summary>
        private void InitProfileSystem()
        {
            var store = _settings.ProfileStore;

            // Ensure at least one default profile exists
            if (store.Profiles.Count == 0)
            {
                var defaultProfile = new MozaProfile { Name = "Default" };
                store.Profiles.Add(defaultProfile);
            }

            // Init reads PluginManager.Instance.GameName and selects the matching profile
            store.Init();

            // Detach any prior subscription before re-subscribing (ClearSettings
            // replaces _settings, leaving the old store with a stale handler that
            // would otherwise fire and mutate the new state via `this`).
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
        /// Apply a profile: copy values into _settings and _data, write to device if connected.
        /// </summary>
        internal void ApplyProfile(MozaProfile profile)
        {
            MozaLog.Debug($"[Moza] Applying profile: {profile.Name}");

            // Guard: a profile with all core base settings at zero was captured from
            // uninitialized device data (first-launch race condition). Reset them to
            // sentinels so they're skipped — the device keeps its own values.
            if (profile.Limit == 0 && profile.FfbStrength == 0 && profile.Torque == 0 && profile.Speed == 0)
            {
                MozaLog.Warn("[Moza] Profile has zeroed base settings — resetting to sentinels");
                profile.Limit = -1; profile.FfbStrength = -1; profile.Torque = -1; profile.Speed = -1;
                profile.Damper = -1; profile.Friction = -1; profile.Inertia = -1; profile.Spring = -1;
                profile.SpeedDamping = -1; profile.SpeedDampingPoint = -1;
                profile.NaturalInertia = -1; profile.SoftLimitStiffness = -1;
                profile.SoftLimitRetain = -1; profile.FfbReverse = -1; profile.Protection = -1;
                profile.GameDamper = -1; profile.GameFriction = -1;
                profile.GameInertia = -1; profile.GameSpring = -1;
                profile.WorkMode = -1;
            }

            // --- Base/Motor settings → _data + device ---
            ApplyBaseSettingIfSet(profile.Limit, v => { _data.Limit = v; _data.MaxAngle = v; }, "base-limit", "base-max-angle");
            ApplyBaseSettingIfSet(profile.FfbStrength, v => _data.FfbStrength = v, "base-ffb-strength");
            ApplyBaseSettingIfSet(profile.Torque, v => _data.Torque = v, "base-torque");
            ApplyBaseSettingIfSet(profile.Speed, v => _data.Speed = v, "base-speed");
            ApplyBaseSettingIfSet(profile.Damper, v => _data.Damper = v, "base-damper");
            ApplyBaseSettingIfSet(profile.Friction, v => _data.Friction = v, "base-friction");
            ApplyBaseSettingIfSet(profile.Inertia, v => _data.Inertia = v, "base-inertia");
            ApplyBaseSettingIfSet(profile.Spring, v => _data.Spring = v, "base-spring");
            ApplyBaseSettingIfSet(profile.SpeedDamping, v => _data.SpeedDamping = v, "base-speed-damping");
            ApplyBaseSettingIfSet(profile.SpeedDampingPoint, v => _data.SpeedDampingPoint = v, "base-speed-damping-point");
            ApplyBaseSettingIfSet(profile.NaturalInertia, v => _data.NaturalInertia = v, "base-natural-inertia");
            ApplyBaseSettingIfSet(profile.SoftLimitStiffness, v => _data.SoftLimitStiffness = v, "base-soft-limit-stiffness");
            ApplyBaseSettingIfSet(profile.SoftLimitRetain, v => _data.SoftLimitRetain = v, "base-soft-limit-retain");
            ApplyBaseSettingIfSet(profile.FfbReverse, v => _data.FfbReverse = v, "base-ffb-reverse");
            ApplyBaseSettingIfSet(profile.Protection, v => _data.Protection = v, "base-protection");

            // Game effect gains
            ApplyBaseSettingIfSet(profile.GameDamper, v => _data.GameDamper = v, "main-set-damper-gain");
            ApplyBaseSettingIfSet(profile.GameFriction, v => _data.GameFriction = v, "main-set-friction-gain");
            ApplyBaseSettingIfSet(profile.GameInertia, v => _data.GameInertia = v, "main-set-inertia-gain");
            ApplyBaseSettingIfSet(profile.GameSpring, v => _data.GameSpring = v, "main-set-spring-gain");

            // Work mode
            ApplyBaseSettingIfSet(profile.WorkMode, v => _data.WorkMode = v, "main-set-work-mode");

            // --- Wheel LED settings → _settings + _data ---
            // When the device extension is active, it owns wheel LED settings
            // via SetSettings()/GetSettings() — skip to avoid conflicts.
            if (!DeviceExtensionActive)
            {
                if (profile.WheelTelemetryMode >= 0)
                {
                    _settings.WheelTelemetryMode = profile.WheelTelemetryMode;
                    _data.WheelTelemetryMode = profile.WheelTelemetryMode;
                }
                if (profile.WheelIdleEffect >= 0)
                {
                    _settings.WheelIdleEffect = profile.WheelIdleEffect;
                    _data.WheelTelemetryIdleEffect = profile.WheelIdleEffect;
                }
                if (profile.WheelButtonsIdleEffect >= 0)
                {
                    _settings.WheelButtonsIdleEffect = profile.WheelButtonsIdleEffect;
                    _data.WheelButtonsIdleEffect = profile.WheelButtonsIdleEffect;
                }
                if (profile.WheelKnobIdleEffect >= 0)
                {
                    _settings.WheelKnobIdleEffect = profile.WheelKnobIdleEffect;
                    _data.WheelKnobIdleEffect = profile.WheelKnobIdleEffect;
                }
                if (profile.WheelKnobLedMode >= 0)
                {
                    _settings.WheelKnobLedMode = profile.WheelKnobLedMode;
                    _data.WheelKnobLedMode = profile.WheelKnobLedMode;
                }
                if (profile.WheelButtonsLedMode >= 0)
                {
                    _settings.WheelButtonsLedMode = profile.WheelButtonsLedMode;
                    _data.WheelButtonsLedMode = profile.WheelButtonsLedMode;
                }
                if (profile.WheelTelemetryIdleSpeedMs >= 0)
                {
                    _settings.WheelTelemetryIdleSpeedMs = profile.WheelTelemetryIdleSpeedMs;
                    _data.WheelTelemetryIdleSpeedMs = profile.WheelTelemetryIdleSpeedMs;
                }
                if (profile.WheelButtonsIdleSpeedMs >= 0)
                {
                    _settings.WheelButtonsIdleSpeedMs = profile.WheelButtonsIdleSpeedMs;
                    _data.WheelButtonsIdleSpeedMs = profile.WheelButtonsIdleSpeedMs;
                }
                if (profile.WheelKnobIdleSpeedMs >= 0)
                {
                    _settings.WheelKnobIdleSpeedMs = profile.WheelKnobIdleSpeedMs;
                    _data.WheelKnobIdleSpeedMs = profile.WheelKnobIdleSpeedMs;
                }
                if (profile.WheelSleepMode >= 0)
                {
                    _settings.WheelSleepMode = profile.WheelSleepMode;
                    _data.WheelIdleMode = profile.WheelSleepMode;
                }
                if (profile.WheelSleepTimeoutMin >= 0)
                {
                    _settings.WheelSleepTimeoutMin = profile.WheelSleepTimeoutMin;
                    _data.WheelIdleTimeout = profile.WheelSleepTimeoutMin;
                }
                if (profile.WheelSleepSpeedMs >= 0)
                {
                    _settings.WheelSleepSpeedMs = profile.WheelSleepSpeedMs;
                    _data.WheelIdleSpeed = profile.WheelSleepSpeedMs;
                }
                if (profile.WheelSleepColor != null && profile.WheelSleepColor.Length > 0)
                {
                    _settings.WheelSleepColor = profile.WheelSleepColor;
                    var rgb = MozaProfile.UnpackColor(profile.WheelSleepColor[0]);
                    _data.WheelIdleColor[0] = rgb[0];
                    _data.WheelIdleColor[1] = rgb[1];
                    _data.WheelIdleColor[2] = rgb[2];
                }
                if (profile.WheelRpmBrightness >= 0)
                {
                    _settings.WheelRpmBrightness = profile.WheelRpmBrightness;
                    _data.WheelRpmBrightness = profile.WheelRpmBrightness;
                }
                if (profile.WheelButtonsBrightness >= 0)
                {
                    _settings.WheelButtonsBrightness = profile.WheelButtonsBrightness;
                    _data.WheelButtonsBrightness = profile.WheelButtonsBrightness;
                }
                if (profile.WheelFlagsBrightness >= 0)
                {
                    _settings.WheelFlagsBrightness = profile.WheelFlagsBrightness;
                    _data.WheelFlagsBrightness = profile.WheelFlagsBrightness;
                }
                if (profile.WheelRpmIndicatorMode >= 0)
                {
                    _settings.WheelRpmIndicatorMode = profile.WheelRpmIndicatorMode;
                    _data.WheelRpmIndicatorMode = profile.WheelRpmIndicatorMode;
                }
                if (profile.WheelRpmDisplayMode >= 0)
                {
                    _settings.WheelRpmDisplayMode = profile.WheelRpmDisplayMode;
                    _data.WheelRpmDisplayMode = profile.WheelRpmDisplayMode;
                }
                if (profile.WheelESRpmBrightness >= 0)
                {
                    _settings.WheelESRpmBrightness = profile.WheelESRpmBrightness;
                    _data.WheelESRpmBrightness = profile.WheelESRpmBrightness;
                }

            }

            // Dashboard brightness
            if (profile.DashRpmBrightness >= 0)
            {
                _settings.DashRpmBrightness = profile.DashRpmBrightness;
                _data.DashRpmBrightness = profile.DashRpmBrightness;
            }
            if (profile.DashFlagsBrightness >= 0)
            {
                _settings.DashFlagsBrightness = profile.DashFlagsBrightness;
                _data.DashFlagsBrightness = profile.DashFlagsBrightness;
            }
            if (profile.DashDisplayBrightness >= 0)
            {
                _settings.DashDisplayBrightness = profile.DashDisplayBrightness;
                _data.DashDisplayBrightness = profile.DashDisplayBrightness;
            }
            if (profile.DashDisplayStandbyMin >= 0)
            {
                _settings.DashDisplayStandbyMin = profile.DashDisplayStandbyMin;
                _data.DashDisplayStandbyMin = profile.DashDisplayStandbyMin;
            }

            // --- FFB Equalizer ---
            // Equalizer uses -1000 as sentinel (valid range is 0 to 400, where 100 = default/flat)
            void ApplyEq(int val, System.Action<int> set, string cmd) { if (val > -1000) { set(val); if (_data.IsBaseConnected) _deviceManager.WriteSetting(cmd, val); } }
            ApplyEq(profile.Equalizer1, v => _data.Equalizer1 = v, "base-equalizer1");
            ApplyEq(profile.Equalizer2, v => _data.Equalizer2 = v, "base-equalizer2");
            ApplyEq(profile.Equalizer3, v => _data.Equalizer3 = v, "base-equalizer3");
            ApplyEq(profile.Equalizer4, v => _data.Equalizer4 = v, "base-equalizer4");
            ApplyEq(profile.Equalizer5, v => _data.Equalizer5 = v, "base-equalizer5");
            ApplyEq(profile.Equalizer6, v => _data.Equalizer6 = v, "base-equalizer6");

            // --- FFB Curve (X breakpoints always fixed at 20/40/60/80) ---
            // Always write fixed X breakpoints when base is connected (device may not persist them)
            MozaLog.Debug($"[Moza] ApplyProfile curve: IsBaseConnected={_data.IsBaseConnected} Y1={profile.FfbCurveY1} Y2={profile.FfbCurveY2} Y3={profile.FfbCurveY3} Y4={profile.FfbCurveY4} Y5={profile.FfbCurveY5}");
            if (_data.IsBaseConnected)
            {
                _deviceManager.WriteSetting("base-ffb-curve-x1", 20);
                _deviceManager.WriteSetting("base-ffb-curve-x2", 40);
                _deviceManager.WriteSetting("base-ffb-curve-x3", 60);
                _deviceManager.WriteSetting("base-ffb-curve-x4", 80);
            }
            // Apply Y values from profile, or write linear defaults if none saved yet
            if (profile.FfbCurveY1 >= 0) ApplyBaseSettingIfSet(profile.FfbCurveY1, v => _data.FfbCurveY1 = v, "base-ffb-curve-y1");
            else if (_data.IsBaseConnected) _deviceManager.WriteSetting("base-ffb-curve-y1", _data.FfbCurveY1);
            if (profile.FfbCurveY2 >= 0) ApplyBaseSettingIfSet(profile.FfbCurveY2, v => _data.FfbCurveY2 = v, "base-ffb-curve-y2");
            else if (_data.IsBaseConnected) _deviceManager.WriteSetting("base-ffb-curve-y2", _data.FfbCurveY2);
            if (profile.FfbCurveY3 >= 0) ApplyBaseSettingIfSet(profile.FfbCurveY3, v => _data.FfbCurveY3 = v, "base-ffb-curve-y3");
            else if (_data.IsBaseConnected) _deviceManager.WriteSetting("base-ffb-curve-y3", _data.FfbCurveY3);
            if (profile.FfbCurveY4 >= 0) ApplyBaseSettingIfSet(profile.FfbCurveY4, v => _data.FfbCurveY4 = v, "base-ffb-curve-y4");
            else if (_data.IsBaseConnected) _deviceManager.WriteSetting("base-ffb-curve-y4", _data.FfbCurveY4);
            if (profile.FfbCurveY5 >= 0) ApplyBaseSettingIfSet(profile.FfbCurveY5, v => _data.FfbCurveY5 = v, "base-ffb-curve-y5");
            else if (_data.IsBaseConnected) _deviceManager.WriteSetting("base-ffb-curve-y5", _data.FfbCurveY5);

            // --- Handbrake settings → _data + device ---
            ApplyHandbrakeSettingIfSet(profile.HandbrakeMode, v => _data.HandbrakeMode = v, "handbrake-mode");
            ApplyHandbrakeSettingIfSet(profile.HandbrakeButtonThreshold, v => _data.HandbrakeButtonThreshold = v, "handbrake-button-threshold");
            ApplyHandbrakeSettingIfSet(profile.HandbrakeDirection, v => _data.HandbrakeDirection = v, "handbrake-direction");
            ApplyHandbrakeSettingIfSet(profile.HandbrakeMin, v => _data.HandbrakeMin = v, "handbrake-min");
            ApplyHandbrakeSettingIfSet(profile.HandbrakeMax, v => _data.HandbrakeMax = v, "handbrake-max");
            if (profile.HandbrakeCurve != null)
            {
                for (int i = 0; i < Math.Min(5, profile.HandbrakeCurve.Length); i++)
                {
                    int idx = i; int val = profile.HandbrakeCurve[i];
                    _data.HandbrakeCurve[idx] = val;
                    if (_handbrakeDetected) _deviceManager.WriteFloat($"handbrake-y{idx + 1}", val);
                }
            }

            // --- Pedal settings → _data + device ---
            ApplyPedalSettingIfSet(profile.PedalsThrottleDir, v => _data.PedalsThrottleDir = v, "pedals-throttle-dir");
            ApplyPedalSettingIfSet(profile.PedalsBrakeDir, v => _data.PedalsBrakeDir = v, "pedals-brake-dir");
            if (profile.PedalsBrakeAngleRatio >= 0) { _data.PedalsBrakeAngleRatio = profile.PedalsBrakeAngleRatio; if (_pedalsDetected) _deviceManager.WriteFloat("pedals-brake-angle-ratio", profile.PedalsBrakeAngleRatio); }
            ApplyPedalSettingIfSet(profile.PedalsClutchDir, v => _data.PedalsClutchDir = v, "pedals-clutch-dir");
            ApplyCurveIfSet(profile.PedalsThrottleCurve, _data.PedalsThrottleCurve, "pedals-throttle-y", _pedalsDetected);
            ApplyCurveIfSet(profile.PedalsBrakeCurve, _data.PedalsBrakeCurve, "pedals-brake-y", _pedalsDetected);
            ApplyCurveIfSet(profile.PedalsClutchCurve, _data.PedalsClutchCurve, "pedals-clutch-y", _pedalsDetected);

            // --- Colors → _data ---
            if (!DeviceExtensionActive)
            {
                MozaProfile.UnpackColorsInto(profile.WheelRpmColors, _data.WheelRpmColors);
                MozaProfile.UnpackColorsInto(profile.WheelRpmBlinkColors, _data.WheelRpmBlinkColors);
                MozaProfile.UnpackColorsInto(profile.WheelButtonColors, _data.WheelButtonColors);
                if (profile.WheelButtonDefaultDuringTelemetry != null)
                {
                    int n = Math.Min(profile.WheelButtonDefaultDuringTelemetry.Length, _data.WheelButtonDefaultDuringTelemetry.Length);
                    for (int i = 0; i < n; i++)
                        _data.WheelButtonDefaultDuringTelemetry[i] = profile.WheelButtonDefaultDuringTelemetry[i];
                }
                MozaProfile.UnpackColorsInto(profile.WheelFlagColors, _data.WheelFlagColors);
                if (profile.WheelIdleColor != null && profile.WheelIdleColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(profile.WheelIdleColor[0]);
                    _data.WheelIdleColor[0] = rgb[0];
                    _data.WheelIdleColor[1] = rgb[1];
                    _data.WheelIdleColor[2] = rgb[2];
                }
                MozaProfile.UnpackColorsInto(profile.WheelESRpmColors, _data.WheelESRpmColors);
                MozaProfile.UnpackColorsInto(profile.WheelKnobBackgroundColors, _data.WheelKnobBackgroundColors);
                MozaProfile.UnpackColorsInto(profile.WheelKnobPrimaryColors,    _data.WheelKnobPrimaryColors);
                _settings.WheelRpmBlinkColors = profile.WheelRpmBlinkColors;
                _settings.WheelKnobBackgroundColors = profile.WheelKnobBackgroundColors;
                _settings.WheelKnobPrimaryColors    = profile.WheelKnobPrimaryColors;
            }
            if (!DashDeviceExtensionActive)
            {
                MozaProfile.UnpackColorsInto(profile.DashRpmColors, _data.DashRpmColors);
                MozaProfile.UnpackColorsInto(profile.DashRpmBlinkColors, _data.DashRpmBlinkColors);
                MozaProfile.UnpackColorsInto(profile.DashFlagColors, _data.DashFlagColors);

                // Persist dash blink colors to settings (write-only, not polled from device)
                _settings.DashRpmBlinkColors = profile.DashRpmBlinkColors;
            }

            // --- Write to device if connected ---
            if (_data.IsConnected)
            {
                WriteProfileWheelSettingsToDevice(profile);
                WriteProfileColorsToDevice(profile);
            }

            // --- AB9 active shifter (independent serial pipe) ---
            if (profile.Ab9 != null && _ab9Detected)
            {
                var ab9 = profile.Ab9;
                _ab9Manager.SendMode(ab9.Mode);
                _ab9Manager.SendSlider(Devices.Ab9Slider.MechanicalResistance, ab9.MechanicalResistance);
                _ab9Manager.SendSlider(Devices.Ab9Slider.Spring, ab9.Spring);
                _ab9Manager.SendSlider(Devices.Ab9Slider.NaturalDamping, ab9.NaturalDamping);
                _ab9Manager.SendSlider(Devices.Ab9Slider.NaturalFriction, ab9.NaturalFriction);
                _ab9Manager.SendSlider(Devices.Ab9Slider.MaxTorqueLimit, ab9.MaxTorqueLimit);
                // Engine vibration intensity/frequency are host-rendered — the
                // worker thread picks them up live from this profile. Only the
                // gear-shift-vibration intensity needs a device-side push.
                _ab9Manager.SendGearShiftVibrationIntensity(ab9.GearShiftVibrationIntensity);
            }

            // Persist _settings without re-capturing _data into the profile.
            // The profile already has the values we just applied; capturing _data here
            // would corrupt the profile if concurrent device reads have overwritten _data
            // with stale values before the device has processed our writes.
            PersistSettings();

            // Apply the profile-recorded dashboard preference last so the wheel
            // settings are in place before we ask it to switch dashboards. If the
            // wheel catalog isn't ready yet (cold start before configJson arrives),
            // queue the switch for the next PollStatus tick.
            if (!string.IsNullOrEmpty(profile.TelemetryDashboardKey))
            {
                bool applied = false;
                try { applied = ApplyTelemetryDashboardFromProfile(profile); }
                catch (Exception ex)
                {
                    MozaLog.Warn("[Moza] ApplyTelemetryDashboardFromProfile threw: " + ex.Message);
                    applied = true; // don't infinitely retry on a broken key
                }
                if (!applied)
                {
                    _pendingProfileDashboardKey = profile.TelemetryDashboardKey;
                    _pendingProfileDashboardKeyDeadlineTicks =
                        DateTime.UtcNow.Add(PendingProfileKeyTimeout).Ticks;
                    MozaLog.Debug("[Moza] Profile dashboard apply deferred — wheel state not ready");
                }
                else
                {
                    _pendingProfileDashboardKey = null;
                }
            }
        }

        private void ApplyBaseSettingIfSet(int value, Action<int> setData, params string[] commands)
        {
            if (value < 0) return;
            setData(value);
            if (_data.IsBaseConnected)
            {
                foreach (var cmd in commands)
                    _deviceManager.WriteSetting(cmd, value);
            }
        }

        private void ApplyHandbrakeSettingIfSet(int value, Action<int> setData, string command)
        {
            if (value < 0) return;
            setData(value);
            if (_handbrakeDetected)
                _deviceManager.WriteSetting(command, value);
        }

        private void ApplyPedalSettingIfSet(int value, Action<int> setData, string command)
        {
            if (value < 0) return;
            setData(value);
            if (_pedalsDetected)
                _deviceManager.WriteSetting(command, value);
        }

        private void ApplyCurveIfSet(int[]? curve, int[] dataArray, string commandPrefix, bool deviceConnected)
        {
            if (curve == null) return;
            for (int i = 0; i < Math.Min(5, curve.Length); i++)
            {
                dataArray[i] = curve[i];
                if (deviceConnected)
                    _deviceManager.WriteFloat($"{commandPrefix}{i + 1}", curve[i]);
            }
        }

        private void WriteProfileWheelSettingsToDevice(MozaProfile profile)
        {
            // When device extension is active, it owns wheel LED settings
            if (!DeviceExtensionActive)
            {
                // New wheel settings
                if (_newWheelDetected)
                {
                    if (profile.WheelTelemetryMode >= 0)
                        _deviceManager.WriteSetting("wheel-telemetry-mode", profile.WheelTelemetryMode);
                    if (profile.WheelIdleEffect >= 0)
                        _deviceManager.WriteSetting("wheel-telemetry-idle-effect", profile.WheelIdleEffect);
                    if (profile.WheelButtonsIdleEffect >= 0)
                        _deviceManager.WriteSetting("wheel-buttons-idle-effect", profile.WheelButtonsIdleEffect);
                    if (profile.WheelKnobIdleEffect >= 0)
                        _deviceManager.WriteSetting("wheel-knob-idle-effect", profile.WheelKnobIdleEffect);
                    if (profile.WheelKnobLedMode >= 0)
                        _deviceManager.WriteSetting("wheel-knob-led-mode", profile.WheelKnobLedMode);
                    if (profile.WheelButtonsLedMode >= 0)
                        _deviceManager.WriteSetting("wheel-buttons-led-mode", profile.WheelButtonsLedMode);
                    if (profile.WheelIdleEffect >= 0 && profile.WheelTelemetryIdleSpeedMs >= 0)
                        _deviceManager.WriteArray("wheel-telemetry-idle-interval",
                            BuildIdleIntervalPayload(profile.WheelIdleEffect, profile.WheelTelemetryIdleSpeedMs));
                    if (profile.WheelButtonsIdleEffect >= 0 && profile.WheelButtonsIdleSpeedMs >= 0)
                        _deviceManager.WriteArray("wheel-buttons-idle-interval",
                            BuildIdleIntervalPayload(profile.WheelButtonsIdleEffect, profile.WheelButtonsIdleSpeedMs));
                    if (profile.WheelKnobIdleEffect >= 0 && profile.WheelKnobIdleSpeedMs >= 0)
                        _deviceManager.WriteArray("wheel-knob-idle-interval",
                            BuildIdleIntervalPayload(profile.WheelKnobIdleEffect, profile.WheelKnobIdleSpeedMs));
                    if (profile.WheelSleepMode >= 0)
                        _deviceManager.WriteSetting("wheel-idle-mode", profile.WheelSleepMode);
                    if (profile.WheelSleepTimeoutMin >= 0)
                        _deviceManager.WriteSetting("wheel-idle-timeout", profile.WheelSleepTimeoutMin);
                    if (profile.WheelSleepMode >= 0 && profile.WheelSleepSpeedMs >= 0)
                        _deviceManager.WriteArray("wheel-idle-speed",
                            BuildIdleIntervalPayload(profile.WheelSleepMode, profile.WheelSleepSpeedMs));
                    if (profile.WheelSleepColor != null && profile.WheelSleepColor.Length > 0)
                    {
                        var rgb = MozaProfile.UnpackColor(profile.WheelSleepColor[0]);
                        _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                    }
                    if (profile.WheelRpmBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-rpm-brightness", profile.WheelRpmBrightness);
                    if (profile.WheelButtonsBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-buttons-brightness", profile.WheelButtonsBrightness);
                    // Flag brightness → Meter sub-device (dash-flags-brightness). Gate on dash detected.
                    if (profile.WheelFlagsBrightness >= 0 && _dashDetected)
                        _deviceManager.WriteSetting("dash-flags-brightness", profile.WheelFlagsBrightness);
                }

                // ES/Old wheel settings
                if (_oldWheelDetected)
                {
                    if (profile.WheelRpmIndicatorMode >= 0)
                        _deviceManager.WriteSetting("wheel-rpm-indicator-mode", profile.WheelRpmIndicatorMode + 1); // display→raw
                    if (profile.WheelRpmDisplayMode >= 0)
                        _deviceManager.WriteSetting("wheel-set-rpm-display-mode", profile.WheelRpmDisplayMode);
                    if (profile.WheelESRpmBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-old-rpm-brightness", profile.WheelESRpmBrightness);
                }
            }

            // Dashboard brightness (skip when dash device extension owns settings)
            if (!DashDeviceExtensionActive && _dashDetected)
            {
                if (profile.DashRpmBrightness >= 0)
                    _deviceManager.WriteSetting("dash-rpm-brightness", profile.DashRpmBrightness);
                if (profile.DashFlagsBrightness >= 0)
                    _deviceManager.WriteSetting("dash-flags-brightness", profile.DashFlagsBrightness);
                if (profile.DashDisplayBrightness >= 0)
                    _telemetrySender?.SendDashDisplayBrightness(profile.DashDisplayBrightness);
                if (profile.DashDisplayStandbyMin >= 0)
                    _telemetrySender?.SendDashDisplayStandbyMinutes(profile.DashDisplayStandbyMin);
            }
        }

        private void WriteProfileColorsToDevice(MozaProfile profile)
        {
            // New-protocol wheel colors
            if (!DeviceExtensionActive && _newWheelDetected)
            {
                WriteColorArray(profile.WheelRpmColors, "wheel-rpm-color", 18);
                WriteColorArray(profile.WheelRpmBlinkColors, "wheel-rpm-blink-color", 10);
                WriteColorArray(profile.WheelButtonColors, "wheel-button-color", 14);
                // Flag colors route to Meter sub-device via dash-flag-color*. Gate on dash detection.
                if (_dashDetected)
                    WriteColorArray(profile.WheelFlagColors, "dash-flag-color", 6);
                if (profile.WheelIdleColor != null && profile.WheelIdleColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(profile.WheelIdleColor[0]);
                    _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                WriteKnobColors(profile.WheelKnobBackgroundColors, profile.WheelKnobPrimaryColors);
                WriteKnobRingColors(profile.WheelKnobRingColors, profile.WheelKnobRingBrightness);
            }

            // Old-protocol (ES) wheel colors
            if (!DeviceExtensionActive && _oldWheelDetected)
            {
                WriteColorArray(profile.WheelESRpmColors, "wheel-old-rpm-color", 10);
            }

            // Dash colors
            if (!DashDeviceExtensionActive && _dashDetected)
            {
                WriteColorArray(profile.DashRpmColors, "dash-rpm-color", 10);
                WriteColorArray(profile.DashRpmBlinkColors, "dash-rpm-blink-color", 10);
                WriteColorArray(profile.DashFlagColors, "dash-flag-color", 6);
            }
        }

        // Build the 3-byte payload shared by per-effect speed commands:
        //   wheel-{telemetry,buttons,knob}-idle-interval — `[effect_id, ms_msb, ms_lsb]`
        //   wheel-idle-speed                              — `[mode,      ms_msb, ms_lsb]`
        // The first byte selects which effect/mode the slider applies to;
        // the remaining two bytes encode the ms value big-endian.
        private static byte[] BuildIdleIntervalPayload(int selector, int ms)
        {
            ms = System.Math.Max(0, System.Math.Min(0xFFFF, ms));
            return new byte[] {
                (byte)(selector & 0xFF),
                (byte)((ms >> 8) & 0xFF),
                (byte)(ms & 0xFF),
            };
        }

        private void WriteColorArray(int[]? packedColors, string commandPrefix, int count)
        {
            if (packedColors == null) return;
            int len = Math.Min(packedColors.Length, count);
            for (int i = 0; i < len; i++)
            {
                var rgb = MozaProfile.UnpackColor(packedColors[i]);
                _deviceManager.WriteColor($"{commandPrefix}{i + 1}", rgb[0], rgb[1], rgb[2]);
            }
        }

        /// <summary>
        /// Push per-knob "bulk Inactive default" + per-knob "Active" colors to the
        /// wheel. No-op unless the active wheel model exposes knob LED rings
        /// (W17 CS Pro / W18 KS Pro). Source arrays are packed R&lt;&lt;16|G&lt;&lt;8|B
        /// per knob; null = skip. The bulk Inactive value for each knob is fanned
        /// out to all of that knob's ring LEDs via the per-LED wheel-knob-bg-color{N}
        /// writes (cmd 0x1F 0x03 0x01); the Active value drives cmd 0x27 ROLE=0.
        /// </summary>
        private void WriteKnobColors(int[]? packedBulkInactive, int[]? packedActive)
        {
            var model = WheelModelInfo;
            int knobs = model?.KnobCount ?? 0;
            if (knobs <= 0) return;

            // Per-knob Active LED color (cmd 0x27 ROLE=0).
            if (packedActive != null)
            {
                int len = Math.Min(packedActive.Length, knobs);
                for (int i = 0; i < len; i++)
                {
                    var rgb = MozaProfile.UnpackColor(packedActive[i]);
                    _deviceManager.WriteColor($"wheel-knob{i + 1}-active-color", rgb[0], rgb[1], rgb[2]);
                }
            }

            // Per-knob "bulk Inactive default" — fan one color out to all of the
            // knob's ring LEDs via per-LED writes (cmd 0x1F 0x03 0x01).
            if (packedBulkInactive != null
                && model?.KnobRingLeds != null
                && IsWheelLedGroupPresent(3))
            {
                int kLen = Math.Min(packedBulkInactive.Length, knobs);
                for (int k = 0; k < kLen; k++)
                {
                    var rgb = MozaProfile.UnpackColor(packedBulkInactive[k]);
                    int startIdx = model.KnobRingStartIndex(k);
                    int count = model.KnobRingLeds[k];
                    for (int i = 0; i < count; i++)
                    {
                        int ledIdx = startIdx + i;
                        _deviceManager.WriteColor($"wheel-knob-bg-color{ledIdx + 1}", rgb[0], rgb[1], rgb[2]);
                    }
                }
            }
        }

        /// <summary>
        /// Push per-LED ring colors (cmd 0x1F 0x03 0x01) to the wheel. No-op
        /// unless the active wheel model has KnobRingLeds and Group 3 is present.
        /// `packedColors` is indexed across all knobs contiguously (LED 1..56);
        /// brightness &lt; 0 skips the brightness write.
        /// </summary>
        private void WriteKnobRingColors(int[]? packedColors, int brightness)
        {
            var model = WheelModelInfo;
            if (model?.KnobRingLeds == null || !IsWheelLedGroupPresent(3)) return;
            if (brightness >= 0)
                _deviceManager.WriteSetting("wheel-knob-brightness", brightness);
            if (packedColors == null) return;
            int total = Math.Min(packedColors.Length, model.KnobRingLedTotal);
            for (int i = 0; i < total; i++)
            {
                var rgb = MozaProfile.UnpackColor(packedColors[i]);
                _deviceManager.WriteColor($"wheel-knob-bg-color{i + 1}", rgb[0], rgb[1], rgb[2]);
            }
        }

        /// <summary>
        /// Apply wheel settings from the SimHub device extension profile system.
        /// Updates _settings, _data, and writes to hardware if connected.
        /// </summary>
        internal void ApplyWheelExtensionSettings(MozaWheelExtensionSettings extSettings)
        {
            MozaLog.Debug("[Moza] Applying wheel device extension settings");

            // Update _settings and _data in-memory. ApplyTo already routes into
            // the correct per-model slot and only updates flat fields when this
            // extension's captured model matches the currently-connected wheel.
            extSettings.ApplyTo(_settings, _data);

            // Gate hardware writes + _data mutations on model match — extensions
            // for other wheel models must not poke the active wheel's hardware.
            string extModel = extSettings.WheelModelName ?? "";
            string activeModel = _data.WheelModelName ?? "";
            bool hasExtModel = !string.IsNullOrEmpty(extModel);
            bool modelMatches = hasExtModel &&
                string.Equals(extModel, activeModel, StringComparison.OrdinalIgnoreCase);
            bool writeHardware = !hasExtModel || modelMatches;

            // Persist blink colors only when this extension owns the active wheel.
            if (writeHardware)
                _settings.WheelRpmBlinkColors = extSettings.WheelRpmBlinkColors;

            // Write to hardware if connected and this extension matches the active wheel
            if (writeHardware && _data.IsConnected)
            {
                // Wheel mode/brightness settings
                if (_newWheelDetected)
                {
                    if (extSettings.WheelTelemetryMode >= 0)
                        _deviceManager.WriteSetting("wheel-telemetry-mode", extSettings.WheelTelemetryMode);
                    if (extSettings.WheelIdleEffect >= 0)
                        _deviceManager.WriteSetting("wheel-telemetry-idle-effect", extSettings.WheelIdleEffect);
                    if (extSettings.WheelButtonsIdleEffect >= 0)
                        _deviceManager.WriteSetting("wheel-buttons-idle-effect", extSettings.WheelButtonsIdleEffect);
                    if (extSettings.WheelKnobIdleEffect >= 0)
                        _deviceManager.WriteSetting("wheel-knob-idle-effect", extSettings.WheelKnobIdleEffect);
                    if (extSettings.WheelKnobLedMode >= 0)
                        _deviceManager.WriteSetting("wheel-knob-led-mode", extSettings.WheelKnobLedMode);
                    if (extSettings.WheelButtonsLedMode >= 0)
                        _deviceManager.WriteSetting("wheel-buttons-led-mode", extSettings.WheelButtonsLedMode);
                    if (extSettings.WheelIdleEffect >= 0 && extSettings.WheelTelemetryIdleSpeedMs >= 0)
                        _deviceManager.WriteArray("wheel-telemetry-idle-interval",
                            BuildIdleIntervalPayload(extSettings.WheelIdleEffect, extSettings.WheelTelemetryIdleSpeedMs));
                    if (extSettings.WheelButtonsIdleEffect >= 0 && extSettings.WheelButtonsIdleSpeedMs >= 0)
                        _deviceManager.WriteArray("wheel-buttons-idle-interval",
                            BuildIdleIntervalPayload(extSettings.WheelButtonsIdleEffect, extSettings.WheelButtonsIdleSpeedMs));
                    if (extSettings.WheelKnobIdleEffect >= 0 && extSettings.WheelKnobIdleSpeedMs >= 0)
                        _deviceManager.WriteArray("wheel-knob-idle-interval",
                            BuildIdleIntervalPayload(extSettings.WheelKnobIdleEffect, extSettings.WheelKnobIdleSpeedMs));
                    if (extSettings.WheelSleepMode >= 0)
                        _deviceManager.WriteSetting("wheel-idle-mode", extSettings.WheelSleepMode);
                    if (extSettings.WheelSleepTimeoutMin >= 0)
                        _deviceManager.WriteSetting("wheel-idle-timeout", extSettings.WheelSleepTimeoutMin);
                    if (extSettings.WheelSleepMode >= 0 && extSettings.WheelSleepSpeedMs >= 0)
                        _deviceManager.WriteArray("wheel-idle-speed",
                            BuildIdleIntervalPayload(extSettings.WheelSleepMode, extSettings.WheelSleepSpeedMs));
                    if (extSettings.WheelSleepColor != null && extSettings.WheelSleepColor.Length > 0)
                    {
                        var rgb = MozaProfile.UnpackColor(extSettings.WheelSleepColor[0]);
                        _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                    }
                    if (extSettings.WheelRpmBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-rpm-brightness", extSettings.WheelRpmBrightness);
                    if (extSettings.WheelButtonsBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-buttons-brightness", extSettings.WheelButtonsBrightness);
                    // Flag brightness → Meter sub-device (dash-flags-brightness). Gate on dash detected.
                    if (extSettings.WheelFlagsBrightness >= 0 && _dashDetected)
                        _deviceManager.WriteSetting("dash-flags-brightness", extSettings.WheelFlagsBrightness);
                }

                if (_oldWheelDetected)
                {
                    if (extSettings.WheelRpmIndicatorMode >= 0)
                        _deviceManager.WriteSetting("wheel-rpm-indicator-mode", extSettings.WheelRpmIndicatorMode + 1);
                    if (extSettings.WheelRpmDisplayMode >= 0)
                        _deviceManager.WriteSetting("wheel-set-rpm-display-mode", extSettings.WheelRpmDisplayMode);
                    if (extSettings.WheelESRpmBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-old-rpm-brightness", extSettings.WheelESRpmBrightness);
                }

                // Colors
                WriteColorArray(extSettings.WheelRpmColors, "wheel-rpm-color", 18);
                WriteColorArray(extSettings.WheelRpmBlinkColors, "wheel-rpm-blink-color", 10);
                WriteColorArray(extSettings.WheelButtonColors, "wheel-button-color", 14);
                // Flag colors → Meter sub-device (dash-flag-color*). Gate on dash detection.
                if (_dashDetected)
                    WriteColorArray(extSettings.WheelFlagColors, "dash-flag-color", 6);
                if (extSettings.WheelIdleColor != null && extSettings.WheelIdleColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(extSettings.WheelIdleColor[0]);
                    _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                WriteColorArray(extSettings.WheelESRpmColors, "wheel-old-rpm-color", 10);
                WriteKnobColors(extSettings.WheelKnobBackgroundColors, extSettings.WheelKnobPrimaryColors);
                WriteKnobRingColors(extSettings.WheelKnobRingColors, extSettings.WheelKnobRingBrightness);
            }

            PersistSettings();

            // Telemetry settings carried inside the extension blob were the
            // historical bleed source: SimHub invokes SetSettings on EVERY
            // registered wheel extension at startup, each one calling here
            // before any wheel has self-identified. The first extension would
            // push the global TelemetryProfileName into the active sender,
            // even if the physical wheel turned out to be a different model.
            //
            // Gate strictly on modelMatches: only an extension that OWNS the
            // currently-connected wheel may push live telemetry state. The
            // init-time case (no wheel connected, activeModel empty) falls
            // through here without touching the sender; the wheel-mcu-uid
            // handler in DetectDevices is the authoritative entry point that
            // loads the per-UID slot once the wheel identifies itself.
            if (extSettings.TelemetrySettingsPresent && modelMatches)
            {
                if (_settings.TelemetryEnabled)
                {
                    ApplyTelemetrySettings();
                    StartTelemetryIfReady();
                }
                else
                {
                    _telemetrySender?.Stop();
                }
            }
        }

        /// <summary>
        /// Apply dash settings from the SimHub device extension profile system.
        /// Updates _settings, _data, and writes to hardware if connected.
        /// </summary>
        internal void ApplyDashExtensionSettings(MozaDashExtensionSettings extSettings)
        {
            MozaLog.Debug("[Moza] Applying dash device extension settings");

            // Update _settings and _data in-memory
            extSettings.ApplyTo(_settings, _data);

            // Persist blink colors
            _settings.DashRpmBlinkColors = extSettings.DashRpmBlinkColors;

            // Write to hardware if connected
            if (_data.IsConnected && _dashDetected)
            {
                if (extSettings.DashRpmBrightness >= 0)
                    _deviceManager.WriteSetting("dash-rpm-brightness", extSettings.DashRpmBrightness);
                if (extSettings.DashFlagsBrightness >= 0)
                    _deviceManager.WriteSetting("dash-flags-brightness", extSettings.DashFlagsBrightness);
                if (extSettings.DashDisplayBrightness >= 0)
                    _telemetrySender?.SendDashDisplayBrightness(extSettings.DashDisplayBrightness);
                if (extSettings.DashDisplayStandbyMin >= 0)
                    _telemetrySender?.SendDashDisplayStandbyMinutes(extSettings.DashDisplayStandbyMin);
                if (extSettings.DashRpmIndicatorMode >= 0)
                    _deviceManager.WriteSetting("dash-rpm-indicator-mode", extSettings.DashRpmIndicatorMode);
                if (extSettings.DashFlagsIndicatorMode >= 0)
                    _deviceManager.WriteSetting("dash-flags-indicator-mode", extSettings.DashFlagsIndicatorMode);
                if (extSettings.DashRpmDisplayMode >= 0)
                    _deviceManager.WriteSetting("dash-rpm-display-mode", extSettings.DashRpmDisplayMode);

                // Colors
                WriteColorArray(extSettings.DashRpmColors, "dash-rpm-color", 10);
                WriteColorArray(extSettings.DashRpmBlinkColors, "dash-rpm-blink-color", 10);
                WriteColorArray(extSettings.DashFlagColors, "dash-flag-color", 6);
            }

            PersistSettings();
        }

    }
}
