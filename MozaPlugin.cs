using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Timers;
using System.Windows.Media;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
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
        // Greenfield Telemetry2 pipeline. Constructed in Init() when
        // _settings.UseNewTelemetryPipeline is true; otherwise null and the old
        // _telemetrySender path runs. Both implement IMozaTelemetry; lifecycle
        // dispatch goes through the _activeTelemetry accessor below.
        private global::MozaPlugin.Telemetry2.MozaTelemetryHost? _telemetryHost;
        private global::MozaPlugin.Telemetry2.IMozaTelemetry? _activeTelemetry =>
            (global::MozaPlugin.Telemetry2.IMozaTelemetry?)_telemetryHost ?? _telemetrySender;

        // V2-side auto-test instance (pipeline-agnostic harness driven via IMozaTelemetry).
        // The old pipeline owns its own _autoTest field on TelemetrySender; v2 carries it
        // here because MozaTelemetryHost has no pollable internal-loop equivalent — the
        // tick fires from MozaPlugin.DataUpdate.
        private global::MozaPlugin.Telemetry.DashboardSwitchAutoTest? _hostAutoTest;
        private long _hostAutoTestLastTickUtc;
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
            "base-ffb-reverse", "base-temp-strategy",
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
            "wheel-group2-brightness", "wheel-group3-brightness", "wheel-group4-brightness",
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

                // Reject the AB9 shifter PID on the wheelbase pipe — both devices
                // enumerate as VID_346E composite, and without this filter the
                // wheelbase code path can grab the AB9 port and stall on probes.
                // Probe-based discovery (PID = null, Wine/Proton) still works.
                _connection = new MozaSerialConnection(pid => !MozaUsbIds.IsAb9Pid(pid));
                if (!string.IsNullOrEmpty(_settings.LastWheelbasePort))
                    _connection.LastPortName = _settings.LastWheelbasePort;
                _connection.MessageReceived += OnMessageReceived;
                _connection.Disconnected += OnSerialDisconnected;

                _deviceManager = new MozaDeviceManager(_connection);

                _ab9Manager = new MozaAb9DeviceManager();
                if (!string.IsNullOrEmpty(_settings.LastAb9Port))
                    _ab9Manager.Connection.LastPortName = _settings.LastAb9Port;
                _ab9Manager.MessageReceived += OnAb9MessageReceived;

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
                    if (_settings.EnableAb9 && !_ab9Manager.IsConnected)
                        TryConnectAb9();
                };
                _reconnectTimer.AutoReset = true;
                if (_settings.ConnectionEnabled)
                    _reconnectTimer.Start();

                _hidReader = new MozaHidReader(_data);
                _hidReader.Start();

                if (_settings.UseNewTelemetryPipeline)
                {
                    Action<byte[]> send = _connection.Send;
                    Action<int, byte[]> sendStream = (kind, frame) =>
                        _connection.SendStream((global::MozaPlugin.Protocol.StreamKind)kind, frame);
                    if (_settings.EnableWireTraceFileSink)
                    {
                        InitTelemetry2WireTrace();
                        Action<byte[]> origSend = send;
                        send = (frame) =>
                        {
                            origSend(frame);
                            try { WriteTelemetry2WireTrace("h2b", frame); } catch { }
                        };
                        Action<int, byte[]> origStream = sendStream;
                        sendStream = (kind, frame) =>
                        {
                            origStream(kind, frame);
                            try { WriteTelemetry2WireTrace("h2b", frame); } catch { }
                        };
                    }
                    _telemetryHost = new global::MozaPlugin.Telemetry2.MozaTelemetryHost(send: send, sendStream: sendStream);
                    _telemetryHost.CatalogProfileBuilder = (catalog, name) =>
                        DashProfileStore.BuildProfileFromCatalog(catalog, name);
                    _connection.MessageReceived += OnInboundForHost;
                    SimHub.Logging.Current.Info("[Moza] Telemetry pipeline: NEW (Telemetry2 greenfield)");
                }
                else
                {
                    _telemetrySender = new TelemetrySender(_connection);
                }

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
            try { _telemetryHost?.Stop(); } catch { }
            try
            {
                if (_connection != null)
                {
                    _connection.MessageReceived -= OnMessageReceived;
                    _connection.MessageReceived -= OnInboundForHost;
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
            CloseTelemetry2WireTrace();
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

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (IsShuttingDown) return;
            _activeTelemetry?.UpdateGameData(data.NewData);
            _activeTelemetry?.SetGameRunning(data.GameRunning);
            // Drive the new pipeline's tick — pumps tier-def emissions, heartbeat cadence,
            // and value-frame bursts. The old TelemetrySender uses its own internal timer
            // and doesn't need a tick driver here.
            _telemetryHost?.Tick(System.DateTime.UtcNow.Ticks);

            // Drive the v2-side auto-test (if armed). Computes a real tick-ms delta from
            // last invocation since DataUpdate cadence varies with game data rate.
            if (_hostAutoTest != null)
            {
                long nowUtc = System.DateTime.UtcNow.Ticks;
                int dtMs = _hostAutoTestLastTickUtc == 0
                    ? 16
                    : (int)((nowUtc - _hostAutoTestLastTickUtc) / System.TimeSpan.TicksPerMillisecond);
                _hostAutoTestLastTickUtc = nowUtc;
                if (dtMs > 0 && dtMs < 1000) _hostAutoTest.Tick(dtMs);
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
                    _connection.MessageReceived -= OnInboundForHost;
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
            try { _telemetryHost?.Stop(); } catch { }
            try { _telemetryHost?.Dispose(); } catch { }
            CloseTelemetry2WireTrace();

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
            _settings.ProfileStore?.CurrentProfile?.CaptureFromCurrent(_settings, _data);
            // Mirror the active flat Wheel* fields into the per-wheel-model slot
            // so each physical wheel keeps its own brightness/mode/input settings
            // across reloads (see MozaPluginSettings.PerWheelSlots).
            _settings.MirrorActiveToSlot(_data?.WheelModelName);
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
            _telemetryHost?.Stop();
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
                _telemetryHost?.Stop();
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

        /// <summary>
        /// Toggle AB9 shifter detection at runtime. When disabled, disconnect
        /// any active AB9 connection and clear detection state so the device
        /// extension UI hides cleanly. When re-enabled, the reconnect timer
        /// (5 s cadence) picks it up automatically — no explicit start needed.
        /// </summary>
        internal void SetAb9Enabled(bool enabled)
        {
            _settings.EnableAb9 = enabled;
            SaveSettings();
            if (enabled)
            {
                MozaLog.Info("[Moza] AB9 detection enabled — next reconnect tick will probe");
            }
            else
            {
                _ab9Detected = false;
                _ab9SettingsApplied = false;
                try { _ab9Manager?.Disconnect(); } catch { }
                MozaLog.Info("[Moza] AB9 detection disabled — disconnected");
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

        // Pipeline-agnostic accessor for UI controls that need to call into the active
        // telemetry implementation (e.g. SendDashboardSwitch from the wheel-settings combo).
        // Returns whichever pipeline is currently instantiated; null if neither is up yet.
        internal global::MozaPlugin.Telemetry2.IMozaTelemetry? ActiveTelemetry =>
            (global::MozaPlugin.Telemetry2.IMozaTelemetry?)_telemetryHost ?? _telemetrySender;

        // Surface configJson wheel state from whichever pipeline is active, so the
        // Diagnostics tab populates regardless of UseNewTelemetryPipeline. Old pipeline:
        // _telemetrySender.WheelState. New pipeline: _telemetryHost.LastWheelState
        // (populated by ConfigJsonOp from b2h session 0x09 chunks).
        internal WheelDashboardState? WheelStateForDiagnostics =>
            _telemetrySender?.WheelState ?? _telemetryHost?.LastWheelState;

        // Tile-server state (b2h session 0x03 parse) routed from whichever pipeline is active.
        internal TileServerState? TileServerStateForDiagnostics =>
            _telemetrySender?.TileServerState ?? _telemetryHost?.TileServerState;

        // Wheel channel catalog (host side: collected by CatalogConsumer from tag=0x04 records).
        internal System.Collections.Generic.IReadOnlyList<string>? WheelChannelCatalogForDiagnostics =>
            _telemetrySender?.WheelChannelCatalog ?? _telemetryHost?.WheelChannelCatalog;

        // Catalog-parser internals for the diag tab. Surfaces buffer/parse/CRC
        // counters so we can tell at a glance why a missing catalog is missing.
        // Old pipeline only — new pipeline uses CatalogConsumer with a different
        // shape, which already surfaces its state via WheelChannelCatalog above.
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
            (System.Collections.Generic.IReadOnlyDictionary<byte, (int In, int Out)>?)_telemetrySender?.SessionCounts
            ?? _telemetryHost?.SessionCounts;

        // Active telemetry running flag — true if either pipeline reports Enabled.
        internal bool TelemetryEnabledForDiagnostics =>
            (_telemetrySender?.Enabled ?? false) || (_telemetryHost?.Enabled ?? false);

        // Frame-counter readout from active pipeline.
        internal int FramesSentForDiagnostics =>
            _telemetrySender?.FramesSent ?? _telemetryHost?.FramesSent ?? 0;

        // Bandwidth + wire-error counters from the serial connection. Surfaced
        // in the Diagnostics tab so the user can see when the link approaches
        // saturation, and how many parse failures the read path is shedding.
        internal global::MozaPlugin.Protocol.WriteBudget.Snapshot SerialBudgetForDiagnostics
            => _connection?.CurrentBudget ?? default;
        internal global::MozaPlugin.Protocol.MozaSerialConnection.WireErrorCounters SerialWireErrorsForDiagnostics
            => _connection?.WireErrors ?? default;

        // Subscription diagnostics for the "Subscription" section of the Diagnostics tab.
        // Old pipeline returns its TelemetrySender.SubscriptionDiagnostics directly. New
        // pipeline materialises an equivalent from MozaTelemetryHost.LastSubscriptionRaw +
        // ActiveSubscription (channels). Reverse-resolves wheel-catalog idx → URL for the
        // Channels list display.
        internal TelemetrySender.SubscriptionDiagnostics? SubscriptionForDiagnostics
        {
            get
            {
                if (_telemetrySender != null) return _telemetrySender.LastSubscription;
                if (_telemetryHost == null) return null;
                var raw = _telemetryHost.LastSubscriptionRaw;
                if (raw == null) return null;
                var (bytes, capturedAt) = raw.Value;
                // Find preamble end: first 14 bytes are preamble (PROTO_VER + FLAG_BASE)
                // when present; otherwise the message starts straight in section ENABLEs.
                int preambleLen = 0;
                if (bytes.Length >= 14 && bytes[0] == 0x07 && bytes[5] == 0x02 && bytes[9] == 0x03)
                    preambleLen = 14;
                byte[] preamble = new byte[preambleLen];
                if (preambleLen > 0) Array.Copy(bytes, 0, preamble, 0, preambleLen);
                byte[] body = new byte[bytes.Length - preambleLen];
                Array.Copy(bytes, preambleLen, body, 0, body.Length);

                // Reverse catalog: idx → URL.
                var catalog = _telemetryHost.WheelChannelCatalog;
                string LookupUrl(uint idx)
                {
                    if (catalog == null || idx == 0 || idx > catalog.Count) return "";
                    return catalog[(int)idx - 1] ?? "";
                }

                // Parse TIER records to extract channel list.
                var channels = new System.Collections.Generic.List<(int Idx, string Url, uint Comp, uint Width)>();
                int pos = preambleLen;
                while (pos + 5 <= bytes.Length)
                {
                    byte tag = bytes[pos];
                    uint size = (uint)(bytes[pos + 1] | (bytes[pos + 2] << 8) | (bytes[pos + 3] << 16) | (bytes[pos + 4] << 24));
                    if (size > (uint)(bytes.Length - pos - 5)) break;
                    if (tag == 0x01 && size >= 1) // TIER
                    {
                        int n = ((int)size - 1) / 16;
                        for (int c = 0; c < n; c++)
                        {
                            int off = pos + 5 + 1 + c * 16;
                            uint idx = (uint)(bytes[off] | (bytes[off + 1] << 8) | (bytes[off + 2] << 16) | (bytes[off + 3] << 24));
                            uint comp = (uint)(bytes[off + 4] | (bytes[off + 5] << 8) | (bytes[off + 6] << 16) | (bytes[off + 7] << 24));
                            uint bw = (uint)(bytes[off + 8] | (bytes[off + 9] << 8) | (bytes[off + 10] << 16) | (bytes[off + 11] << 24));
                            channels.Add(((int)idx, LookupUrl(idx), comp, bw));
                        }
                    }
                    pos += 5 + (int)size;
                }

                return new TelemetrySender.SubscriptionDiagnostics
                {
                    SessionByte = "0x01",
                    Format = "v2-type02",
                    PreambleBytes = preamble,
                    BodyBytes = body,
                    Channels = channels,
                    CapturedAt = capturedAt,
                };
            }
        }

        // Inbound s02 chunks captured in 5s window after last subscription send.
        internal System.Collections.Generic.IReadOnlyList<byte[]>? SubscriptionResponseForDiagnostics =>
            _telemetrySender?.LastSubscriptionResponse ?? _telemetryHost?.LastSubscriptionResponse;

        /// <summary>Apply settings from MozaPluginSettings to the TelemetrySender.</summary>
        internal void ApplyTelemetrySettings()
        {
            if (_activeTelemetry == null) return;
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

            // V2-side auto-test wiring. Old pipeline owns its own auto-test on
            // TelemetrySender; v2 lives here because the host has no internal pollable
            // loop. Construct lazily on first ApplyTelemetrySettings, reset on
            // subsequent calls so the harness re-arms after each settings reload.
            if (_telemetryHost != null && s.EnableAutoTestOnConnect)
            {
                if (_hostAutoTest == null)
                {
                    _hostAutoTest = new global::MozaPlugin.Telemetry.DashboardSwitchAutoTest(
                        _telemetryHost,
                        ResolveDashboardProfileByName,
                        () => DashCache,
                        name => { _settings.TelemetryProfileName = name; });
                    MozaLog.Info("[Moza] Telemetry2 host: auto-test armed");
                }
                else
                {
                    _hostAutoTest.Reset();
                    MozaLog.Debug("[Moza] Telemetry2 host: auto-test re-armed");
                }
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
            if (profile != null)
            {
                // Resolve the file path for GetDashboardKey — single-file override,
                // folder library, or null (builtin).
                string? keyPath = s.TelemetryMzdashPath;
                if (string.IsNullOrEmpty(keyPath) && DashCache != null)
                    keyPath = DashCache.TryGetFolderFilePath(profile.Name);

                string dashboardKey = DashboardProfileStore.GetDashboardKey(
                    keyPath, profile);
                if (s.TelemetryChannelMappings != null &&
                    s.TelemetryChannelMappings.TryGetValue(dashboardKey, out var overrides))
                {
                    DashboardProfileStore.ApplyUserMappings(profile, overrides);
                }
            }

            _activeTelemetry.PropertyResolver = ResolvePropertyAsDouble;
            int tierCount = profile?.Tiers?.Count ?? 0;
            int chCount = 0;
            if (profile != null)
                foreach (var t in profile.Tiers) chCount += t.Channels.Count;
            MozaLog.Debug(
                $"[Moza] ApplyTelemetrySettings: setting profile=" +
                $"{profile?.Name ?? "null"} tiers={tierCount} channels={chCount} " +
                $"mzdash={mzdashName} settingName={s.TelemetryProfileName}");
            _activeTelemetry.Profile = profile;
            _activeTelemetry.MzdashContent = mzdashContent;
            _activeTelemetry.MzdashName = mzdashName;



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

        /// <summary>Stable identity key for the currently-loaded dashboard.</summary>
        internal string CurrentDashboardKey()
        {
            var profile = _activeTelemetry?.Profile;
            if (profile == null) return "";
            string? keyPath = _settings.TelemetryMzdashPath;
            if (string.IsNullOrEmpty(keyPath) && DashCache != null)
                keyPath = DashCache.TryGetFolderFilePath(profile.Name);
            return DashboardProfileStore.GetDashboardKey(keyPath, profile);
        }

        /// <summary>Set or clear a per-channel SimHub property override for the current dashboard.</summary>
        internal void SetChannelMapping(string channelUrl, string propertyPath)
        {
            if (string.IsNullOrEmpty(channelUrl)) return;
            string key = CurrentDashboardKey();
            if (string.IsNullOrEmpty(key)) return;

            _settings.TelemetryChannelMappings ??=
                new System.Collections.Generic.Dictionary<string,
                    System.Collections.Generic.Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            if (!_settings.TelemetryChannelMappings.TryGetValue(key, out var inner))
            {
                inner = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _settings.TelemetryChannelMappings[key] = inner;
            }

            string trimmed = (propertyPath ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed))
                inner.Remove(channelUrl);
            else
                inner[channelUrl] = trimmed;

            SaveSettings();
        }

        /// <summary>Clear all per-channel overrides for the currently-loaded dashboard.</summary>
        internal void ClearCurrentDashboardMappings()
        {
            string key = CurrentDashboardKey();
            if (string.IsNullOrEmpty(key)) return;
            if (_settings.TelemetryChannelMappings != null &&
                _settings.TelemetryChannelMappings.Remove(key))
            {
                SaveSettings();
            }
        }

        /// <summary>
        /// Restart the telemetry session with current settings. Called when protocol version,
        /// flag byte mode, or other send options change in the UI.
        /// </summary>
        internal void RestartTelemetry()
        {
            var t = _activeTelemetry;
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

        internal void OnActiveDashboardChanged()
        {
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

            var host = _telemetryHost;
            if (host != null && host.Enabled)
            {
                MozaLog.Debug("[Moza] OnActiveDashboardChanged (v2): applying telemetry settings");
                ApplyTelemetrySettings();
            }
        }

        internal void OnDashboardSwitched()
        {
            var sender = _telemetrySender;
            if (sender != null && sender.Enabled)
            {
                MozaLog.Debug("[Moza] OnDashboardSwitched: scheduling Stop+Start pipeline cycle");

                // Stage the new dashboard's profile + mzdash content INTO the
                // sender first so the post-Start cold-start sequence builds
                // tier-def from the right channels. ApplyTelemetrySettings
                // sets Profile + MzdashContent + MzdashName as side effects.
                ApplyTelemetrySettings();

                // Cycle the pipeline. RestartForSwitch handles the kind=4 drain
                // wait (so the UI knob's just-emitted FF kind=4 actually lands
                // before Stop discards the queue), then Stop+Start. The
                // ~10–14s wheel sess=0x09 timeout is enforced inside StartInner
                // via _lastStopUtcTicks, so we don't need an explicit settle
                // here — Start automatically waits the right amount before
                // opening sessions. Don't re-send FF kind=4 here; the wheel-UI
                // knob already did.
                sender.RestartForSwitch();
                return;
            }

            var host = _telemetryHost;
            if (host != null && host.Enabled)
            {
                MozaLog.Debug("[Moza] OnDashboardSwitched (v2): applying telemetry settings");
                ApplyTelemetrySettings();
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
                _telemetryHost?.Stop();
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
            var t = _activeTelemetry;
            if (t == null) return;
            if (!_settings.TelemetryEnabled) return;
            if (!_connection.IsConnected) return;
            if (!_newWheelDetected && !_oldWheelDetected) return;
            // Profile may be null when no .mzdash is loaded and no builtin
            // profiles are bundled. Sender starts anyway; preamble parses the
            // wheel-advertised catalog (Type02 firmware pushes it unconditionally)
            // and MaybeSwapProfileForCatalog synthesises a WheelCatalog profile
            // post-preamble.

            // Already running — don't restart (avoids re-probing ports mid-session).
            // For the new pipeline, t.FramesSent only counts value frames, but the
            // host's Tick may have produced some before Start fires. Skip the gate
            // and rely on the host's own re-entry guard (state != Disconnected).
            if (_telemetryHost == null && t.FramesSent > 0) return;

            // Prevent duplicate dispatch (multiple callers may pass the guards above
            // before the background thread increments FramesSent)
            if (Interlocked.CompareExchange(ref _telemetryStartRequested, 1, 0) != 0) return;

            MozaLog.Info("[Moza] Wheel detected and telemetry enabled — starting telemetry sender");
            ThreadPool.QueueUserWorkItem(_ => t.Start());
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
            // Defense-in-depth: skip if AB9 detection has been disabled by
            // the user. The reconnect-timer gate at line 422 should already
            // prevent the call, but guard here too in case future callers
            // bypass that check.
            if (!_settings.EnableAb9) return;
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
            _telemetryHost?.Stop();
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
                    cmds[0] = "wheel-group3-brightness";
                    for (int i = 0; i < total; i++)
                        cmds[i + 1] = $"wheel-group3-color{i + 1}";
                    _deviceManager.ReadSettingsPaced(cmds);
                    MozaLog.Debug($"[Moza] Reading Group 3 ring LED colors ({total} LEDs)");
                }
            }

            // Poll hub port status while hub is connected (read-only, no settings to save)
            if (_hubDetected)
                _deviceManager.ReadSettings(HubReadCommands);
        }

        private volatile int _unmatched;

        // Inbound frame router for the new Telemetry2 pipeline. Parses session-data
        // chunks from raw frames (grp=0xC3 dev=0x71 7C 00 [ses] [typ] [seq] [body])
        // and forwards them to the host. Old pipeline does its own subscription
        // inside TelemetrySender.Start, so this handler runs only when the new
        // pipeline is active (subscribed in Init when _telemetryHost != null).
        private void OnInboundForHost(byte[] data)
        {
            if (IsShuttingDown || _telemetryHost == null) return;
            if (data == null || data.Length < 4) return;
            if (data[0] != 0xC3 || data[1] != 0x71) return;

            // Wire-trace tee: capture ALL 0xC3/0x71 frames (session data + FC acks).
            if (_telemetry2WireTracePath != null)
            {
                try { WriteTelemetry2WireTrace("b2h", data); } catch { }
            }

            // Session-layer dispatch: 7C 00 prefix = session data/open/close.
            if (data.Length < 9 || data[2] != 0x7C || data[3] != 0x00) return;
            byte session = data[4];
            byte type = data[5];
            int seq = data[6] | (data[7] << 8);
            int bodyLen = data.Length - 8;
            byte[] payload = new byte[bodyLen];
            Array.Copy(data, 8, payload, 0, bodyLen);
            try { _telemetryHost.OnInboundChunk(session, type, seq, payload); }
            catch { /* never let inbound routing crash the read thread */ }
        }

        // Telemetry2 wire trace state. Path resolved at host construction; appends
        // one JSONL line per emitted/received frame so post-mortem can diff against
        // PitHouse bridge captures.
        private string? _telemetry2WireTracePath;
        private readonly object _telemetry2WireTraceLock = new object();
        private System.IO.StreamWriter? _telemetry2WireTraceWriter;

        private void InitTelemetry2WireTrace()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                    "Logs");
                System.IO.Directory.CreateDirectory(dir);
                string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                _telemetry2WireTracePath = System.IO.Path.Combine(dir, $"moza-wire-{ts}.jsonl");
                _telemetry2WireTraceWriter = new System.IO.StreamWriter(
                    _telemetry2WireTracePath, append: true, System.Text.Encoding.UTF8)
                { AutoFlush = true };
                MozaLog.Info($"[Moza] Telemetry2 wire trace → {_telemetry2WireTracePath}");
            }
            catch
            {
                _telemetry2WireTracePath = null;
                _telemetry2WireTraceWriter = null;
            }
        }

        private void CloseTelemetry2WireTrace()
        {
            lock (_telemetry2WireTraceLock)
            {
                _telemetry2WireTraceWriter?.Dispose();
                _telemetry2WireTraceWriter = null;
            }
        }

        private void WriteTelemetry2WireTrace(string dir, byte[] frame)
        {
            if (frame == null || frame.Length == 0) return;
            var sb = new System.Text.StringBuilder(frame.Length * 2 + 64);
            double t = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            sb.Append("{\"t\":").Append(t.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"dir\":\"").Append(dir).Append("\",\"len\":").Append(frame.Length);
            sb.Append(",\"hex\":\"");
            for (int i = 0; i < frame.Length; i++) sb.Append(frame[i].ToString("x2"));
            sb.Append("\"}");
            lock (_telemetry2WireTraceLock)
            {
                _telemetry2WireTraceWriter?.WriteLine(sb.ToString());
            }
        }

        private void OnMessageReceived(byte[] data)
        {
            // Bail out during shutdown — serial reader thread may deliver a frame
            // after End() began detaching state. Without this, _data/_deviceManager
            // accesses below could hit a half-disposed object.
            if (IsShuttingDown) return;

            // Filter firmware debug noise before parsing/logging
            if (data.Length >= 1 && data[0] == 0x0E)
                return;

            // Filter SerialStream control frames (group 0xC3 response to 0x43,
            // payload starts with 7C/FC + 00). These are session-management
            // chunks (fc:00 session opens/acks, 7c:00 data) handled by
            // TelemetrySender's session-layer handlers — not command responses.
            // Without this, sessions 0x01..0x0E opens spam Unmatched log lines.
            if (data.Length >= 4 && data[0] == 0xC3 &&
                (data[2] == 0x7C || data[2] == 0xFC) && data[3] == 0x00)
                return;

            // Filter wheel's `7c:23` dashboard-activate advertisements (group
            // 0xC3 device 0x71, payload starts with `7C 23`). Wheel broadcasts
            // active display config periodically — informational, not a command
            // response. Absorbed by TelemetrySender.
            if (data.Length >= 4 && data[0] == 0xC3 && data[2] == 0x7C && data[3] == 0x23)
                return;

            // Filter group 0x40 channel-config burst echoes (0xC0 response):
            //   1E 00 XX / 1E 01 XX — channel enable read per page
            //   28 00 / 28 01 / 28 02 — WheelGetCfg_GetMultiFunction{Switch,Num,Left}
            // Part of the channel configuration burst; wheel returns the stored
            // EEPROM value per channel/query. Not actionable at plugin level —
            // just confirms the probe landed. Mark wheel alive so watchdog
            // doesn't reset detection.
            if (data.Length >= 4 && data[0] == 0xC0 && data[1] == 0x71 &&
                (data[2] == 0x1E || data[2] == 0x28))
            {
                // Capture raw 28:00 / 28:01 reply bytes before swallowing.
                // PitHouse polls these at ~1 Hz across all four bridge captures
                // (sim/logs/bridge-20260503-*.jsonl). Semantics not yet decoded
                // — store raw so future controlled experiments can correlate
                // values against game state.
                if (data.Length >= 6 && data[2] == 0x28)
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

            // Extended LED group presence — any response to groupN brightness/mode/color
            // from this wheel proves the group exists in firmware.
            if (r.Name != null && r.Name.StartsWith("wheel-group", StringComparison.Ordinal))
            {
                int g = r.Name.Length > 11 ? r.Name[11] - '0' : -1;
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

            // Filter firmware debug noise (group 0x0E) before parsing.
            if (data[0] == 0x0E) return;

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
                        DeployDeviceDefinition("MOZA Dashboard", "MozaPlugin.Devices.Dash.device.json");
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
                        StartTelemetryIfReady();
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
                            DeployDeviceDefinitionForModel(currentModel);

                            // Refresh _data knob colours from the slot we just loaded
                            // and push to hardware (W17/W18 only — KnobCount gate).
                            MozaProfile.UnpackColorsInto(_settings.WheelKnobBackgroundColors, _data.WheelKnobBackgroundColors);
                            MozaProfile.UnpackColorsInto(_settings.WheelKnobPrimaryColors,    _data.WheelKnobPrimaryColors);
                            WriteKnobColors(_settings.WheelKnobBackgroundColors, _settings.WheelKnobPrimaryColors);
                            // Ring colors pushed after Group 3 is detected (via PollStatus read trigger)
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
                        MozaLog.Debug($"[Moza] Display model: {_data.DisplayModelName}");
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
                        DeployDeviceDefinitionForOldProto();
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
            }

            // Persist _settings without re-capturing _data into the profile.
            // The profile already has the values we just applied; capturing _data here
            // would corrupt the profile if concurrent device reads have overwritten _data
            // with stale values before the device has processed our writes.
            PersistSettings();
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
        /// Push per-knob background + primary colors to the wheel. No-op unless the
        /// active wheel model exposes knob LED rings (W17 CS Pro / W18 KS Pro).
        /// Source arrays are packed R&lt;&lt;16|G&lt;&lt;8|B per knob; null = skip.
        /// </summary>
        private void WriteKnobColors(int[]? packedBackground, int[]? packedPrimary)
        {
            int knobs = WheelModelInfo?.KnobCount ?? 0;
            if (knobs <= 0) return;
            WriteKnobRoleArray(packedBackground, "bg-color", knobs);
            WriteKnobRoleArray(packedPrimary,    "primary-color", knobs);
        }

        private void WriteKnobRoleArray(int[]? packedColors, string roleSuffix, int count)
        {
            if (packedColors == null) return;
            int len = Math.Min(packedColors.Length, count);
            for (int i = 0; i < len; i++)
            {
                var rgb = MozaProfile.UnpackColor(packedColors[i]);
                _deviceManager.WriteColor($"wheel-knob{i + 1}-{roleSuffix}", rgb[0], rgb[1], rgb[2]);
            }
        }

        /// <summary>
        /// Push Group 3 per-LED ring colors to the wheel. No-op unless the active
        /// wheel model has KnobRingLeds and Group 3 is present.
        /// </summary>
        private void WriteKnobRingColors(int[]? packedColors, int brightness)
        {
            var model = WheelModelInfo;
            if (model?.KnobRingLeds == null || !IsWheelLedGroupPresent(3)) return;
            if (brightness >= 0)
                _deviceManager.WriteSetting("wheel-group3-brightness", brightness);
            if (packedColors == null) return;
            int total = Math.Min(packedColors.Length, model.KnobRingLedTotal);
            for (int i = 0; i < total; i++)
            {
                var rgb = MozaProfile.UnpackColor(packedColors[i]);
                _deviceManager.WriteColor($"wheel-group3-color{i + 1}", rgb[0], rgb[1], rgb[2]);
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

            // Apply telemetry settings if present in this profile
            if (extSettings.TelemetrySettingsPresent)
            {
                if (_settings.TelemetryEnabled)
                {
                    ApplyTelemetrySettings();
                    StartTelemetryIfReady();
                }
                else
                {
                    _telemetrySender?.Stop();
                    _telemetryHost?.Stop();
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

        /// <summary>
        /// Deploy a dynamically generated device definition for a new-protocol wheel.
        /// Uses WheelModelInfo for button count (defaults for unknown models) and
        /// deterministic GUIDs for device identity.
        /// Called once when the wheel model name is first received from firmware.
        /// </summary>
        private void DeployDeviceDefinitionForModel(string modelName)
        {
            var prefix = WheelModelInfo.ExtractPrefix(modelName);
            var friendlyName = WheelModelInfo.GetFriendlyName(prefix);
            var guid = MozaDeviceConstants.ResolveWheelGuid(prefix);
            var modelInfo = WheelModelInfo.FromModelName(modelName);
            var deviceName = "MOZA " + friendlyName;

            DeployGeneratedWheelDefinition(deviceName, guid, friendlyName, modelInfo.RpmLedCount, modelInfo.HasFlagLeds, modelInfo.ButtonLedCount, modelInfo.KnobCount);
        }

        private void DeployGeneratedWheelDefinition(string deviceName, string guid, string productName, int rpmCount, bool hasFlagLeds, int buttonCount, int knobCount)
        {
            try
            {
                var simHubDir = AppDomain.CurrentDomain.BaseDirectory;
                var userDefsDir = Path.Combine(simHubDir, "DevicesDefinitions", "User");
                var deviceDir = Path.Combine(userDefsDir, deviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");

                int expectedTelemetryCount = rpmCount + (hasFlagLeds ? 6 : 0);
                bool fileExists = File.Exists(deviceJsonPath);
                bool stale = false;

                if (fileExists)
                {
                    // Compare existing LogicalTelemetryLeds.LedCount + LogicalButtonsSection.Items
                    // against expected. Mismatch = layout changed in a plugin update; rewrite.
                    try
                    {
                        var existing = JObject.Parse(File.ReadAllText(deviceJsonPath));
                        int existingLed = existing.SelectToken("LedsFeature.LogicalTelemetryLeds.LedCount")?.Value<int>() ?? -1;
                        int existingButtons = (existing.SelectToken("LedsFeature.LogicalButtonsSection.Items") as JArray)?.Count ?? -1;
                        int existingExtra = existing.SelectToken("LedsFeature.LogicalExtraSection.LedCount")?.Value<int>() ?? 0;
                        stale = existingLed != expectedTelemetryCount || existingButtons != buttonCount || existingExtra != knobCount;
                    }
                    catch (Exception parseEx)
                    {
                        MozaLog.Warn(
                            $"[Moza] Could not parse existing device.json for '{deviceName}', rewriting: {parseEx.Message}");
                        stale = true;
                    }

                    if (!stale)
                        return;
                }

                Directory.CreateDirectory(deviceDir);

                var pid = _connection.DiscoveredPid ?? "0x0004";
                var json = GenerateWheelDeviceJson(guid, productName, rpmCount, hasFlagLeds, buttonCount, knobCount, pid);
                File.WriteAllText(deviceJsonPath, json);

                DeviceDefinitionDeployed = true;
                string action = stale ? "Refreshed" : "Deployed";
                MozaLog.Debug(
                    $"[Moza] {action} device definition: {deviceName} " +
                    $"(guid={guid}, telemetryLeds={expectedTelemetryCount}, rpm={rpmCount}, flags={hasFlagLeds}, " +
                    $"buttons={buttonCount}, knobs={knobCount}, pid={pid}, restart SimHub to pick up changes)");
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] Error deploying device definition '{deviceName}': {ex.Message}");
            }
        }

        private static string GenerateWheelDeviceJson(string guid, string productName, int rpmCount, bool hasFlagLeds, int buttonCount, int knobCount, string pid)
        {
            var physItems = new JArray();

            // Telemetry LEDs: single contiguous sequence. When the wheel has flag LEDs
            // they are 3-on-each-side of the RPM strip, so SimHub sees (rpmCount + 6)
            // LEDs as one logical run: [flag 1..3][rpm 1..N][flag 4..6].
            int telemetryCount = rpmCount + (hasFlagLeds ? 6 : 0);
            physItems.Add(new JObject
            {
                ["SourceRole"] = 1,
                ["SourceIndex"] = 0,
                ["RepeatCount"] = telemetryCount,
                ["RepeatMode"] = 1
            });
            for (int i = 1; i < telemetryCount; i++)
                physItems.Add(new JObject());

            // Button LEDs: buttonCount slots
            physItems.Add(new JObject
            {
                ["SourceRole"] = 2,
                ["SourceIndex"] = 0,
                ["RepeatCount"] = buttonCount,
                ["RepeatMode"] = 1
            });
            for (int i = 1; i < buttonCount; i++)
                physItems.Add(new JObject());

            // Knob indicator LEDs (Extra/encoders channel): one per rotary knob
            if (knobCount > 0)
            {
                physItems.Add(new JObject
                {
                    ["SourceRole"] = 3,
                    ["SourceIndex"] = 0,
                    ["RepeatCount"] = knobCount,
                    ["RepeatMode"] = 1
                });
                for (int i = 1; i < knobCount; i++)
                    physItems.Add(new JObject());
            }

            var buttonItems = new JArray();
            for (int i = 0; i < buttonCount; i++)
            {
                buttonItems.Add(new JObject
                {
                    ["Left"] = 20,
                    ["Top"] = 20,
                    ["Width"] = 40
                });
            }

            var device = new JObject
            {
                ["DescriptorUniqueId"] = guid,
                ["SchemaVersion"] = 1,
                ["MinimumSimHubVersion"] = "9.11.8",
                ["DeviceDescription"] = new JObject
                {
                    ["BrandName"] = "MOZA",
                    ["ProductName"] = productName
                },
                ["LedsFeature"] = new JObject
                {
                    ["IsIndividualLedsSectionEnabled"] = true,
                    ["PhysicalLedsMappings"] = new JObject { ["Items"] = physItems },
                    ["LogicalTelemetryLeds"] = new JObject
                    {
                        ["LedCount"] = telemetryCount,
                        ["Segments"] = hasFlagLeds
                            ? new JArray(new JObject { ["Size"] = 3 })
                            : new JArray(),
                        ["IsEnabled"] = true
                    },
                    ["LogicalButtonsSection"] = new JObject
                    {
                        ["IsButtonEditorEnabled"] = false,
                        ["Items"] = buttonItems,
                        ["IsEnabled"] = true
                    },
                    ["LogicalExtraSection"] = knobCount > 0
                        ? new JObject
                        {
                            ["LedCount"] = knobCount,
                            ["TitleOverride"] = "Knob Indicators",
                            ["IsEnabled"] = true
                        }
                        : new JObject { ["IsEnabled"] = false },
                    ["IsEnabled"] = true
                },
                ["HardwareInterface"] = new JObject
                {
                    ["HardwareInterface"] = new JObject
                    {
                        ["TypeName"] = "LedsStandardHIDProtocol",
                        ["HIDUsagePage"] = "0xFF00",
                        ["HIDUsage"] = "0x77",
                        ["HIDReportId"] = "0x68",
                        ["HIDReportSize"] = 64,
                        ["DeviceDetection"] = new JObject
                        {
                            ["Vid"] = "0x346E",
                            ["Pid"] = pid
                        }
                    }
                },
                ["IsLocked"] = true
            };

            return device.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>
        /// Deploy the old-protocol wheel device definition.
        /// Called once when an ES wheel is detected.
        /// </summary>
        private void DeployDeviceDefinitionForOldProto()
        {
            DeployDeviceDefinition("MOZA Old Protocol Wheel", "MozaPlugin.Devices.WheelOldProto.device.json");
        }

        private void DeployDeviceDefinition(string deviceName, string resourceName)
        {
            try
            {
                var simHubDir = AppDomain.CurrentDomain.BaseDirectory;
                var userDefsDir = Path.Combine(simHubDir, "DevicesDefinitions", "User");
                var deviceDir = Path.Combine(userDefsDir, deviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");

                if (File.Exists(deviceJsonPath))
                    return;

                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        MozaLog.Warn($"[Moza] Embedded resource not found: {resourceName}");
                        return;
                    }

                    Directory.CreateDirectory(deviceDir);

                    // Read the template JSON and patch the PID if we discovered one
                    string json;
                    using (var reader = new StreamReader(stream))
                    {
                        json = reader.ReadToEnd();
                    }

                    var discoveredPid = _connection.DiscoveredPid;
                    if (discoveredPid != null)
                    {
                        json = json.Replace("__DETECT_PID__", discoveredPid);
                        MozaLog.Debug($"[Moza] Patched device PID to {discoveredPid} for {deviceName}");
                    }
                    else
                    {
                        // Fallback: no PID discovered (e.g. probe-based discovery under Wine).
                        // Use 0x0004 as a reasonable default.
                        json = json.Replace("__DETECT_PID__", "0x0004");
                        MozaLog.Debug($"[Moza] No PID discovered, using fallback 0x0004 for {deviceName}");
                    }

                    File.WriteAllText(deviceJsonPath, json);
                }

                DeviceDefinitionDeployed = true;
                MozaLog.Info($"[Moza] Deployed device definition: {deviceName} (restart SimHub to add it)");
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] Error deploying device definition '{deviceName}': {ex.Message}");
            }
        }

    }
}
