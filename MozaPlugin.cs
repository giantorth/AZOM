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
using MozaPlugin.Protocol;
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
        // Set true on the first successful base-ambient-brightness response,
        // which proves the connected wheelbase ships the 18-LED ambient strip
        // (R21/R25/R27 family). R9/R12 bases don't respond on the 0x22 group,
        // so the flag stays false and the "MOZA Wheel Base" device definition
        // is never deployed.
        private volatile bool _baseAmbientLedSupported;
        private volatile bool _baseAmbientProbed; // edge guard: only fire the probe once per base detect

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

        // Settings read after the 0x22-group probe confirms the base ships the
        // ambient LED strip. brightness is the probe itself — listed here too so
        // re-syncs cover it; harmless, the second response just refreshes the
        // already-set value.
        private static readonly string[] BaseAmbientReadCommands = new[]
        {
            "base-ambient-brightness",
            "base-ambient-standby-mode",
            "base-ambient-indicator-state",
            "base-ambient-sleep-mode",
            "base-ambient-sleep-timeout",
            "base-ambient-startup-color",
            "base-ambient-shutdown-color",
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

        private volatile bool _baseAmbientDeviceExtensionActive;
        internal bool BaseAmbientDeviceExtensionActive
        {
            get => _baseAmbientDeviceExtensionActive;
            set => _baseAmbientDeviceExtensionActive = value;
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
        internal bool IsBaseAmbientLedSupported => _baseAmbientLedSupported;
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
            // A fresh Init means we don't know what dashboard the wheel is
            // currently bound to — force the next ApplyTelemetryDashboardFromProfile
            // to re-emit kind=4 regardless of saved settings. In the normal
            // SimHub-reloads-plugin path the field is null on a brand new
            // instance; this is the belt-and-braces for the defensive
            // double-Init path above.
            _lastAppliedDashboardKey = null;
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

                // Schema migration: translate legacy UID-keyed / model-keyed
                // storage into the profile-scoped WheelOverride layout, and
                // repair the v6 short-circuit that lost mzdash folder + dash
                // baselines for pre-refactor upgrades. Current target = v7.
                // Registry must be initialized first (needed for page-GUID resolution).
                MozaDeviceConstants.InitializeRegistry();
                if (MigrateSettingsToSchemaV2())
                {
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

                // Accept known wheelbase PIDs on the wheelbase pipe, plus
                // any unknown Moza PID as a fallback probe candidate (future
                // hardware not yet in the inventory). Known non-wheelbase
                // categories (pedals, shifter, handbrake, hub, AB9) are
                // excluded so the wheelbase doesn't waste base/hub probe
                // frames on devices that ignore them. Registry-based
                // discovery in MozaPortDiscovery routes by exact PID; the
                // serial-probe fallback (only armed when the registry
                // returns zero MOZA devices total) honours the same filter.
                // See Protocol/MozaUsbIds.cs and docs/protocol/devices/usb-ids.md.
                Func<bool> disableProbeFallback = () =>
                    _settings != null && _settings.DisableSerialProbeFallback;
                _connection = new MozaSerialConnection(
                    pid => MozaUsbIds.IsWheelbasePid(pid) || !MozaUsbIds.IsKnownMozaPid(pid),
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
                    // AB9 manager is probed by default. With registry-based
                    // discovery the attempt is microseconds when no AB9 is
                    // enumerated. On systems without working registry
                    // enumeration (Wine/Proton) the AB9 probe would fall
                    // back to a full COM-port scan every tick — observed to
                    // lock up SimHub when 30+ wine COM symlinks are present.
                    // Suppress AB9 probe automatically when the MOZA registry
                    // walk returns empty (no Windows USB enumeration) — only
                    // AB9 is affected; the wheelbase keeps its probe
                    // fallback. DisableAb9Detection is an additional explicit
                    // opt-out that wins even when registry discovery works.
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
        // pile-up.
        //
        // The 11 ms tick drives the dominant 0x0A 0x05 stream at ~91 Hz. Other
        // sub-streams (engine-pulse pair, triggers, low-rate signed pair) fire
        // at lower rates derived from RPM. Sub-stream rates from the 2026-05-13
        // PitHouse capture inventory:
        //   0x0A 0x05      ~87 Hz (every tick)
        //   0x0D 0x02/03    9 Hz keepalive pair (every 12 ticks ≈ 132 ms)
        //   0x0B 0x02/03    1.7 → 34.6 Hz with RPM (RPM-scaled)
        //   0x0D 0x05       1.3 → 32 Hz with RPM (RPM-scaled)
        //   0x08 0x04/06    ~0.35 Hz across session (every ~256 ticks)
        //   0x0D 0x01       ~0.10 Hz (sparse, every ~910 ticks)
        // Sub-streams gate on `active` (game running + intensity > 0 + RPM > idle)
        // — when silent we keep the 0x0A 0x05 keepalive going at slot 0x0000 so
        // the device's FFB-stack pipe stays warm, but skip the lower-rate streams.
        private const int Ab9TickPeriodMs = 11;

        // Tick counters between firings of each sub-stream. The "base" intervals
        // are at idle (RPM ~800). At higher RPM, the divisor scales down to fire
        // more often (`base / rpm_factor`, where rpm_factor = rpm / 800).
        private const int KeepalivePairBaseTicks = 12;     // ~9 Hz
        private const int EnginePulsePairBaseTicks = 62;   // ~1.5 Hz at idle
        private const int RpmTrackBaseTicks = 80;          // ~1.1 Hz at idle
        private const int LowRatePairBaseTicks = 260;      // ~0.35 Hz
        private const int SparseTriggerBaseTicks = 920;    // ~0.10 Hz
        private const double Ab9IdleRpm = 800.0;

        // Worker state: tick counter + monotonic phase counter for 0x0B pair.
        // The phase counter is 16-bit BE; PitHouse advances it per emitted pair.
        // We mirror that — pre-increment-by-RPM-scaled-step so the value tracks
        // engine speed.
        private int _ab9TickCount;
        private ushort _ab9PulsePhase;
        // 16-bit signed magnitude accumulator for 0x08 0x04/06. PitHouse drove
        // this monotonically over short windows then reset/decremented at engine-
        // cycle boundaries. The exact trigger rule isn't decoded yet (see
        // ab9-shifter.md), so we approximate by advancing by 100 per emission.
        private short _ab9LowRatePhase;

        private void Ab9EngineVibrationLoop()
        {
            long stopwatchFreq = System.Diagnostics.Stopwatch.Frequency;
            long periodTicks = stopwatchFreq * Ab9TickPeriodMs / 1000;
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

            // ── 0x0A 0x05 engine-vibration refresh — every tick ──────────────
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

            // Lower-rate sub-streams only when actively rumbling. Keeping them off
            // at idle matches what PitHouse does (silent keepalive only).
            if (!active)
            {
                _ab9TickCount++;
                return;
            }

            int tick = ++_ab9TickCount;
            double rpmFactor = Math.Max(1.0, rpm / Ab9IdleRpm);

            // ── 0x0D 0x02/03 keepalive pair ── flat ~9 Hz regardless of RPM
            if (tick % KeepalivePairBaseTicks == 0)
                _ab9Manager.SendKeepalivePair();

            // ── 0x0B 0x02/03 engine-pulse pair ── RPM-scaled rate
            int pulseInterval = Math.Max(2, (int)(EnginePulsePairBaseTicks / rpmFactor));
            if (tick % pulseInterval == 0)
            {
                // Advance phase counter by a step proportional to RPM × freq so it
                // matches PitHouse's "monotonic, RPM-driven" cadence. The captured
                // increments ranged ~32..110 per emitted pair; scale to that range.
                ushort step = (ushort)Math.Min(0xFFFF, (int)(32 + 78 * Math.Min(1.0, rpmFactor / 10.0)));
                unchecked { _ab9PulsePhase += step; }
                _ab9Manager.SendEnginePulsePair(_ab9PulsePhase, intensity);
            }

            // ── 0x0D 0x05 RPM-tracking trigger ── scales with RPM × freq × intensity
            int rpmTrackInterval = Math.Max(2, (int)(RpmTrackBaseTicks / rpmFactor));
            if (tick % rpmTrackInterval == 0)
                _ab9Manager.SendTrigger(MozaAb9DeviceManager.Ab9Trigger.RpmTrack);

            // ── 0x08 0x04/06 low-rate signed pair ── sparse engine-cycle phase
            if (tick % LowRatePairBaseTicks == 0)
            {
                unchecked { _ab9LowRatePhase += 100; }
                // Saturate at ±32000 then wrap — keeps the magnitude in a
                // reasonable range while still tracking long-term phase.
                if (_ab9LowRatePhase > 32000) _ab9LowRatePhase = -32000;
                _ab9Manager.SendLowRatePair(_ab9LowRatePhase);
            }

            // ── 0x0D 0x01 sparse trigger ── purpose unresolved, mirror PitHouse rate
            if (tick % SparseTriggerBaseTicks == 0)
                _ab9Manager.SendTrigger(MozaAb9DeviceManager.Ab9Trigger.Sparse);
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
            // Single source of truth = profile + overlay. UI handlers write
            // overlay/profile directly; CaptureFromCurrent picks up device-read
            // state. No more legacy slot/UID mirror.
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
                _baseAmbientLedSupported = false;
                _baseAmbientProbed = false;
                _data.BaseModelName = "";
                _newWheelDetected = false;
                _oldWheelDetected = false;
                WheelModelInfo = null;
                _handbrakeDetected = false;
                _pedalsDetected = false;
                _hubDetected = false;
                _ab9Detected = false;
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

        /// <summary>Apply telemetry settings from the active wheel overlay to the TelemetrySender.</summary>
        internal void ApplyTelemetrySettings()
        {
            if (_telemetrySender == null) return;

            // Source from the current wheel's overlay (single source of truth).
            // When no wheel is identified yet, ActiveTelemetry* return defaults
            // → era Auto, paths empty, no profile loaded. The sender stays idle
            // until wheel-model-name resolves the page GUID.
            string telemPath = ActiveTelemetryMzdashPath;
            string telemName = ActiveTelemetryProfileName;
            MozaWheelEra era = ActiveTelemetryWheelEra;

            // Build the per-era policy and hand it to the v1 telemetry sender.
            // EraPolicy.For carries every wire-protocol axis (tier-def session,
            // encoding, preamble policy, blind-retransmit, upload header,
            // protocol version). Auto returns a provisional Era2026 policy
            // with IsAuto=true; TelemetrySender.ResolveAutoPolicy at session
            // start replaces it once the wheel reveals itself.
            _telemetrySender.Policy = EraPolicy.For(era);
            // UI for dashboard upload/download is hidden in SettingsControl.xaml while the
            // feature is in development; force both off regardless of the saved settings.
            _telemetrySender.UploadDashboard = false;
            _telemetrySender.SetDownloadEnabled(false);
            if (_settings.EnableAutoTestOnConnect)
                _telemetrySender.EnableAutoTest(this);

            // Resolve the active multi-stream profile and raw mzdash content.
            //
            // Precedence:
            //   1. User picked a custom mzdash file (overlay.TelemetryMzdashPath
            //      set and exists) → parse it, use its channel list + bytes.
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

            if (!string.IsNullOrEmpty(telemPath) && System.IO.File.Exists(telemPath))
            {
                profile = DashProfileStore.ParseMzdash(telemPath);
                mzdashContent = System.IO.File.ReadAllBytes(telemPath);
                mzdashName = System.IO.Path.GetFileNameWithoutExtension(telemPath);
            }
            else if (!string.IsNullOrEmpty(telemName))
            {
                // Try cache first (populated from wheel download or disk).
                if (DashCache != null)
                {
                    profile = DashCache.TryGetByName(telemName);
                    if (profile != null)
                    {
                        mzdashName = profile.Name;
                        mzdashContent = DashCache.TryGetRawContent(telemName);
                        MozaLog.Debug($"[Moza] ApplyTelemetrySettings: found '{telemName}' in cache as '{mzdashName}'");
                    }
                    else
                    {
                        MozaLog.Debug($"[Moza] ApplyTelemetrySettings: '{telemName}' NOT found in cache (folder={DashCache.FolderProfileCount} wheel={DashCache.WheelCacheCount})");
                    }
                }

                // Fall back to builtin embedded profiles when cache misses.
                if (profile == null)
                {
                    var builtins = DashProfileStore.BuiltinProfiles;
                    if (builtins.Count > 0)
                    {
                        profile = FindProfile(builtins, telemName);
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

            // Apply user channel mappings for the selected dashboard. Sourced
            // from the active profile × current wheel page (single source).
            var channelMap = GetActiveChannelMappings();
            if (profile != null && channelMap != null)
            {
                foreach (var dashKey in GetActiveDashboardKeyCandidates())
                {
                    if (channelMap.TryGetValue(dashKey, out var overrides) && overrides != null)
                    {
                        DashboardProfileStore.ApplyUserMappings(profile, overrides);
                        break;
                    }
                }
            }

            var sender = _telemetrySender!;
            sender.PropertyResolver = ResolvePropertyAsDouble;
            sender.PropertyStringResolver = ResolvePropertyAsString;
            int tierCount = profile?.Tiers?.Count ?? 0;
            int chCount = 0;
            if (profile != null)
                foreach (var t in profile.Tiers) chCount += t.Channels.Count;
            MozaLog.Debug(
                $"[Moza] ApplyTelemetrySettings: setting profile=" +
                $"{profile?.Name ?? "null"} tiers={tierCount} channels={chCount} " +
                $"mzdash={mzdashName} settingName={telemName}");
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

        /// <summary>
        /// String-valued sibling of <see cref="ResolvePropertyAsDouble"/>. Used by
        /// the sess=0x01 type=0x05 string-channel emitter to read a SimHub property
        /// (TrackName, CarModel, SessionTypeName, …) as a string. Returns null on
        /// missing path / read exception; caller treats null as empty.
        /// @internal/ paths are formatted invariantly so they can be exercised in
        /// test mode without hitting SimHub at all.
        /// </summary>
        private string? ResolvePropertyAsString(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith("@internal/", StringComparison.Ordinal))
            {
                return ResolveInternalChannel(path)
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            try
            {
                var v = _pluginManager?.GetPropertyValue(path);
                return v?.ToString();
            }
            catch { return null; }
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
        /// Reads from the active wheel's overlay (single source of truth — UI
        /// handlers update the overlay synchronously) and only falls back to
        /// <c>_telemetrySender.Profile.Name</c> when the overlay is empty or
        /// not resolvable yet.
        /// </summary>
        internal IReadOnlyList<string> GetActiveDashboardKeyCandidates()
        {
            string profileName = ActiveTelemetryProfileName;
            string mzdashPath = ActiveTelemetryMzdashPath;

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
            if (!ActiveTelemetryEnabled) return;
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

        // Tracks the TelemetryDashboardKey we've successfully applied to the wheel
        // via ApplyTelemetryDashboardFromProfile in THIS plugin instance. Used as
        // the no-op short-circuit so that repeated profile applies (e.g., multiple
        // OnProfileChanged events for the same profile) don't re-emit kind=4 and
        // re-cycle the pipeline. Reset to null on plugin Init/End and on
        // ResetWheelDetection — the wheel may rebind to a different default after
        // hot-swap, so we must force a fresh apply. Not static: SimHub reloads the
        // plugin on game switch, which is exactly when we DO want to re-emit kind=4
        // to bring the wheel's binding back in sync with the new game's profile.
        private string? _lastAppliedDashboardKey;

        /// <summary>True while a profile-driven dashboard apply is in flight —
        /// the saved key has been seen by ApplyProfile but the apply hasn't
        /// successfully committed yet (sender not Active, or in cooldown, or
        /// wheel state not yet parsed). Used by the UI to keep the dashboard
        /// switcher locked across the whole game-switch transient, instead of
        /// flickering between locked/unlocked during the brief Active windows
        /// between preamble end → probe kind=4 → cooldown clear →
        /// RestartForSwitch's Stop. While true, the next legitimate combo
        /// state is "switching"; once cleared and sender is Active again,
        /// the UI is genuinely ready.</summary>
        internal bool IsPendingDashboardApply => _pendingProfileDashboardKey != null;
        // Diagnostic: last "why am I deferring" reason logged from
        // ApplyTelemetryDashboardFromProfile. Throttles repeat log spam.
        // Set to null whenever the gate passes so the next deferral logs once.
        private string? _lastApplyDeferReason;

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
        /// the active dashboard so each SimHub game/profile gets its own.
        ///
        /// All three key kinds (<c>wheel:</c>, <c>file:</c>, <c>builtin:</c>) are
        /// resolved to a target dashboard <i>name</i>, then looked up in the
        /// wheel's <see cref="WheelDashboardState.ConfigJsonList"/>. If a slot is
        /// found we route through <see cref="OnDashboardSwitched(uint)"/> so a
        /// FF kind=4 emits on the wire — that's the only thing that re-binds the
        /// wheel's currently-displayed dashboard to what the profile wants. Without
        /// it, plugin reload on game switch leaves the wheel rendering whatever
        /// it last showed while the host's tier-def is built for the new profile.
        ///
        /// Falls back to the legacy slotless behavior (<see cref="OnDashboardSwitched()"/>
        /// or <see cref="OnActiveDashboardChanged"/>) only when the wheel catalog
        /// genuinely doesn't contain the target — host-side simulation only.
        ///
        /// Returns true when the key was applied (or dropped permanently); false
        /// to defer, in which case the caller sets <c>_pendingProfileDashboardKey</c>
        /// for PollStatus to retry.
        /// </summary>
        internal bool ApplyTelemetryDashboardFromProfile(MozaProfile profile)
        {
            if (profile == null) return true;
            string? key = profile.TelemetryDashboardKey;
            if (string.IsNullOrEmpty(key)) return true; // no preference recorded

            // Per-plugin-instance no-op: we've already emitted kind=4 for this
            // key in this instance, so the wheel is bound. Plugin reload (game
            // switch in SimHub) resets _lastAppliedDashboardKey, so the first
            // apply after each reload always re-emits — that's the path that
            // brings the wheel back in sync after the reload clobbered its
            // host-side connection state.
            if (_lastAppliedDashboardKey != null
                && string.Equals(_lastAppliedDashboardKey, key, StringComparison.OrdinalIgnoreCase))
            {
                MozaLog.Debug("[Moza] ApplyTelemetryDashboardFromProfile: already applied " +
                              key + " in this plugin instance — no-op");
                return true;
            }

            // Channel-readiness gate: kind=4 emitted before preamble reaches Active
            // is silently dropped by the wheel, and ConfigJsonList isn't populated
            // until then. Also defer during the post-emit cooldown — a previous
            // switch is still in flight, retrying after it clears is correct.
            var sender = _telemetrySender;
            var state = WheelStateForDiagnostics;
            if (sender == null || !sender.IsActive || sender.IsInSilenceCooldown)
            {
                string reason = $"sender={(sender == null ? "null" : (sender.IsActive ? "Active" : "not-Active"))} " +
                                $"cooldown={sender?.IsInSilenceCooldown}";
                if (reason != _lastApplyDeferReason)
                {
                    MozaLog.Debug($"[Moza] ApplyTelemetryDashboardFromProfile deferring (key={key}): {reason}");
                    _lastApplyDeferReason = reason;
                }
                return false;
            }
            if (state == null || state.ConfigJsonList == null || state.ConfigJsonList.Count == 0)
            {
                string reason = $"state={(state == null ? "null" : $"listCount={state.ConfigJsonList?.Count ?? -1}")}";
                if (reason != _lastApplyDeferReason)
                {
                    MozaLog.Debug($"[Moza] ApplyTelemetryDashboardFromProfile deferring (key={key}): wheel state not yet available — {reason}");
                    _lastApplyDeferReason = reason;
                }
                return false;
            }
            // Clear the throttle so the next defer (if any) re-logs.
            _lastApplyDeferReason = null;

            // Resolve target dashboard name + branch-specific side data.
            string targetName;
            string mzdashPath = "";       // populated only by file: when local file resolves
            string sourceTag;             // for diagnostics only

            if (key!.StartsWith("wheel:", StringComparison.OrdinalIgnoreCase))
            {
                string id = key.Substring("wheel:".Length);
                WheelDashboardEntry? match = null;
                if (state.EnabledDashboards != null)
                {
                    foreach (var entry in state.EnabledDashboards)
                    {
                        if (entry != null && string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))
                        {
                            match = entry;
                            break;
                        }
                    }
                }
                if (match == null)
                {
                    MozaLog.Info("[Moza] Profile dashboard key not found in current wheel catalog (id=" +
                                 id + "); leaving current selection");
                    return true; // wheel doesn't have this dashboard at all — stop retrying
                }
                targetName = match.Title;
                sourceTag = $"wheel:{id} ('{match.Title}')";
            }
            else if (key.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                // file:<filename>:<sha1-first-8> — resolve filename → local mzdash
                // (for tier-def channel mapping) and the bare name for slot lookup.
                string remainder = key.Substring("file:".Length);
                int colon = remainder.LastIndexOf(':');
                string filename = colon > 0 ? remainder.Substring(0, colon) : remainder;
                string baseName = System.IO.Path.GetFileNameWithoutExtension(filename);
                string? path = DashCache?.TryGetFolderFilePath(baseName);
                bool localOk = !string.IsNullOrEmpty(path) && System.IO.File.Exists(path);
                targetName = baseName;
                mzdashPath = localOk ? path! : "";
                sourceTag = $"file:{filename}" + (localOk ? $" (local: '{path}')" : " (local file missing)");
            }
            else if (key.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
            {
                targetName = key.Substring("builtin:".Length);
                sourceTag = $"builtin:{targetName}";
            }
            else
            {
                MozaLog.Warn("[Moza] Unknown TelemetryDashboardKey prefix: " + key);
                return true;
            }

            if (string.IsNullOrEmpty(targetName))
            {
                MozaLog.Warn("[Moza] ApplyTelemetryDashboardFromProfile: empty target name for key " + key);
                return true;
            }

            // Slot lookup in the wheel's ConfigJsonList (alphabetical library
            // ordering, slot = index). For wheel: keys we could ALSO match by
            // DirName, but the wheel's ConfigJsonList uses the display name —
            // Title is what the user picks from the dropdown and what we save
            // into ActiveTelemetryProfileName, so matching by name is correct
            // and works uniformly across all three key kinds.
            int slot = -1;
            for (int i = 0; i < state.ConfigJsonList.Count; i++)
            {
                var name = state.ConfigJsonList[i];
                if (string.IsNullOrEmpty(name)) continue;
                if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    slot = i;
                    break;
                }
            }

            if (slot >= 0)
            {
                // Ground-truth slot check: the wheel emits a type-04 record on
                // sess=0x02 b2h after each dashboard switch, telling us its
                // actual current binding (see TelemetrySender.WheelReportedSlot).
                // When that matches our target — OR when we recently emitted a
                // kind=4 to this slot ourselves — there's no functional need
                // for another kind=4. The wheel is already on the right
                // dashboard.
                //
                // Catalog completeness is a separate concern: if the catalog
                // re-sync probe fired in this plugin instance, the wheel's
                // channel catalog was incomplete and the probe alone (kind=4
                // to current slot) doesn't force a re-advertise. Only a
                // Stop+Start cycle does. So in the probe-fired case we still
                // run RestartForSwitch to give the wheel a fresh handshake.
                //
                // Otherwise — wheel on right slot AND catalog was complete —
                // fully skip: no kind=4, no restart, no 11 s of pipeline
                // downtime. This is the desired behavior for a game switch
                // that doesn't change dashboards.
                bool wheelOnTargetSlot = sender.WheelReportedSlot == slot;
                bool weEmittedThisSlot = sender.LastEmittedKind4Slot == slot;
                if (wheelOnTargetSlot || weEmittedThisSlot)
                {
                    string bindEvidence = wheelOnTargetSlot
                        ? "wheel-reported slot"
                        : "prior host kind=4";
                    // If the catalog re-sync probe fired in this plugin
                    // instance, the wheel was on the wrong dashboard at
                    // tier-def emit time (or had an incomplete catalog for
                    // the right one). The probe's kind=4 updates the
                    // wheel's slot-indicator quickly, but observed
                    // 2026-05-14: the wheel does NOT fully load the new
                    // dashboard's widget renderer until the host closes &
                    // re-opens sessions. Without RestartForSwitch, value
                    // frames stream over the wire but render to nothing —
                    // user sees test pattern not appearing despite the
                    // wheel reporting it's on the right slot. So whenever
                    // the probe fired, we MUST cycle the pipeline.
                    //
                    // (Even when IsTierDefFullyBound is true after probe-
                    // nudged catalog re-advertisement — the catalog is
                    // necessary but not sufficient. The wheel only commits
                    // the visual transition through a full session
                    // restart. That's the 22 s "double-wait" cost; can't
                    // be avoided without breaking the user's test pattern.)
                    if (sender.HasCatalogResyncProbeFired)
                    {
                        MozaLog.Info($"[Moza] Profile dashboard '{targetName}' (slot {slot}) bound ({bindEvidence}) but probe fired this instance (source: {sourceTag}); cycling pipeline so wheel commits transition");
                        ActiveTelemetryProfileName = targetName;
                        ActiveTelemetryMzdashPath = mzdashPath;
                        PersistSettings();
                        ApplyTelemetrySettings();
                        sender.RestartForSwitch();
                        RaiseDashboardSelectionChanged();
                        _lastAppliedDashboardKey = key;
                        return true;
                    }
                    MozaLog.Info($"[Moza] Profile dashboard '{targetName}' (slot {slot}) already bound ({bindEvidence}, no probe this instance, source: {sourceTag}); no wire action needed");
                    ActiveTelemetryProfileName = targetName;
                    ActiveTelemetryMzdashPath = mzdashPath;
                    PersistSettings();
                    ApplyTelemetrySettings();
                    RaiseDashboardSelectionChanged();
                    _lastAppliedDashboardKey = key;
                    return true;
                }
                MozaLog.Info($"[Moza] Applying profile dashboard '{targetName}' via wheel slot {slot} (source: {sourceTag})");
                ActiveTelemetryProfileName = targetName;
                ActiveTelemetryMzdashPath = mzdashPath; // empty unless file: branch resolved local path
                PersistSettings();
                OnDashboardSwitched((uint)slot);     // emits FF kind=4 + RestartForSwitch
                RaiseDashboardSelectionChanged();
                _lastAppliedDashboardKey = key;
                return true;
            }

            // No matching wheel slot. Fall back per branch:
            //   - wheel: dashboard wasn't in ConfigJsonList — historically a
            //     "stop retrying" decision (the wheel doesn't expose it for
            //     selection even though it's in EnabledDashboards).
            //   - file: local file exists → slotless restart so host-side
            //     tier-def matches the profile. Wheel keeps showing whatever
            //     it was last bound to.
            //   - file: local file missing AND no wheel slot → nothing useful
            //     we can do; leave current selection.
            //   - builtin: slotless OnActiveDashboardChanged restarts the
            //     pipeline against the named builtin profile.
            if (key.StartsWith("wheel:", StringComparison.OrdinalIgnoreCase))
            {
                MozaLog.Info("[Moza] Profile dashboard '" + targetName +
                             "' missing from configJsonList; leaving current selection");
                return true;
            }

            if (key.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(mzdashPath))
                {
                    MozaLog.Info("[Moza] Profile dashboard file not resolvable and not in wheel catalog (" +
                                 targetName + "); leaving current selection");
                    return true;
                }
                MozaLog.Info("[Moza] Applying profile dashboard (no wheel slot, local file): " + mzdashPath);
                ActiveTelemetryMzdashPath = mzdashPath;
                ActiveTelemetryProfileName = "";
                PersistSettings();
                OnDashboardSwitched(); // slotless — no kind=4
                RaiseDashboardSelectionChanged();
                _lastAppliedDashboardKey = key;
                return true;
            }

            // builtin: fallback
            MozaLog.Info("[Moza] Applying profile dashboard (builtin, no wheel slot): " + targetName);
            ActiveTelemetryProfileName = targetName;
            ActiveTelemetryMzdashPath = "";
            PersistSettings();
            OnActiveDashboardChanged(); // slotless — no kind=4
            RaiseDashboardSelectionChanged();
            _lastAppliedDashboardKey = key;
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
            ActiveTelemetryEnabled = enabled;
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
            if (!ActiveTelemetryEnabled) return;
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
            _baseAmbientLedSupported = false;
            _baseAmbientProbed = false;
            if (_data != null) _data.BaseModelName = "";
            _newWheelDetected = false;
            _oldWheelDetected = false;
            _handbrakeDetected = false;
            _pedalsDetected = false;
            _hubDetected = false;
            _ab9Detected = false;
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
            // Wheel hot-swap re-enumerates from scratch; its currently-bound
            // dashboard may be different from what we last emitted kind=4 for,
            // so force the next ApplyTelemetryDashboardFromProfile to re-emit.
            _lastAppliedDashboardKey = null;
            _telemetrySender?.ResetBindingTracking();
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
                        MozaLog.Debug($"[Moza] Pending profile dashboard apply abandoned — profile/key mismatch (pending={_pendingProfileDashboardKey}, current={(profile == null ? "null" : profile.TelemetryDashboardKey ?? "(empty)")})");
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

            // Base identity reads (group 0x07/01 dispatched against dev 0x12)
            // come back labelled as "wheel-model-name" because they share the
            // same group/cmd shape. Disambiguate by the response device id —
            // wheel responses come from 0x17/0x15/0x13, base/main from 0x12 —
            // and route to the diagnostic BaseModelName field instead of
            // letting MozaData overwrite the wheel identity. Probe is
            // scheduled in the base-mcu-temp handler below.
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

            // Bus-hint "ab9" disambiguates from base-* commands (the AB9 main and
            // wheelbase main share device id 0x12 numerically — without the hint
            // the parser auto-tags as "base" and filters out every ab9-* match).
            var result = MozaResponseParser.Parse(data, busHint: "ab9");
            if (!result.HasValue) return;

            var r = result.Value;
            if (r.Name == null || !r.Name.StartsWith("ab9-", StringComparison.Ordinal))
                return;

            bool rising = !_ab9Detected;
            _ab9Manager.MarkDetected();
            if (rising)
            {
                _ab9Detected = true;
                // Push the PitHouse-style FFB session-init handshake (alloc / init
                // / commit) once on the rising edge. Without it the device's FFB
                // stack is uninitialised and engine-vibration streaming has no
                // effect. The manager guards against re-sending across reconnects.
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
                // Per-wheel state (telemetry settings, mzdash folder, dashboard
                // selection) is page-GUID keyed on the wheel overlay. The
                // wheel-model-name handler is the authoritative entry point —
                // it resolves the page GUID, then ApplyTelemetrySettings reads
                // from the overlay automatically. Nothing to do here besides log.
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

                // Capability probe for the wheelbase ambient LED strip — read
                // base-ambient-brightness on dev 0x12. Bases that ship the
                // strip (R21/R25/R27 family) reply on group 0xA2; bases without
                // it (R9/R12) silently drop the read. The reply, if it arrives,
                // is handled in the "base-ambient-brightness" case below and
                // gates DeviceDefinitionDeployer.DeployBaseAmbient.
                //
                // Diagnostic identity read against dev 0x12 — same group/cmd
                // (0x07/01) as the wheel model name read but routed to the
                // hub/base alias. Response is intercepted ahead of
                // MozaData.UpdateFromCommand to land in BaseModelName. Useful
                // for log forensics regardless of whether the LEDs exist.
                if (!_baseAmbientProbed)
                {
                    _baseAmbientProbed = true;
                    _deviceManager.ReadSetting("base-ambient-brightness");
                    _deviceManager.ReadSettingForDevice("wheel-model-name", MozaProtocol.DeviceMain);
                }
            }

            switch (commandName)
            {
                case "dash-rpm-indicator-mode":
                    if (!_dashDetected)
                    {
                        _dashDetected = true;
                        if (DeviceDefinitionDeployer.DeployDashboard(_connection.DiscoveredPid))
                            DeviceDefinitionDeployed = true;
                        // R6: profile-sourced apply (consolidated, sentinel-guarded).
                        // Legacy ApplySavedDashSettings is gone; the profile is the
                        // single source of truth.
                        ApplyDashToHardware(_settings?.ProfileStore?.CurrentProfile);
                        _deviceManager.ReadSettings(DashSettingsReadCommands);
                        MozaLog.Info("[Moza] Dashboard detected");
                    }
                    break;

                case "base-ambient-brightness":
                    if (!_baseAmbientLedSupported)
                    {
                        _baseAmbientLedSupported = true;
                        if (DeviceDefinitionDeployer.DeployBaseAmbient(_connection.DiscoveredPid))
                            DeviceDefinitionDeployed = true;
                        // R6: profile-sourced apply (consolidated, sentinel-guarded).
                        ApplyBaseAmbientToHardware(_settings?.ProfileStore?.CurrentProfile);
                        _deviceManager.ReadSettings(BaseAmbientReadCommands);
                        MozaLog.Info(
                            $"[Moza] Base ambient LEDs detected (model='{(string.IsNullOrEmpty(_data.BaseModelName) ? "unknown" : _data.BaseModelName)}')");
                    }
                    break;

                case "wheel-telemetry-mode":
                    if (!_newWheelDetected && !_oldWheelDetected)
                    {
                        _newWheelDetected = true;
                        _deviceManager.LockWheelId(deviceId);
                        // Don't apply here — the page GUID isn't resolvable until
                        // wheel-model-name arrives a few hundred ms later. Applying
                        // now would push profile baseline to a wheel that may have
                        // an overlay with different values (audit A.3.3 wheel-swap
                        // race). The wheel-model-name handler below does the apply.
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
                            if (DeviceDefinitionDeployer.DeployForModel(currentModel, _connection.DiscoveredPid))
                                DeviceDefinitionDeployed = true;

                            // Page GUID is now resolvable — apply the overlay-layered
                            // wheel settings (LED/brightness/mode/colors). Single
                            // source of truth = profile + overlay.
                            ApplyWheelToHardware(_settings?.ProfileStore?.CurrentProfile);

                            // Auto-load this wheel's mzdash folder if one is set in
                            // the overlay (per-wheel folder library).
                            var ovFolder = ActiveTelemetryMzdashFolder;
                            if (!string.IsNullOrEmpty(ovFolder) && System.IO.Directory.Exists(ovFolder))
                            {
                                MozaLog.Debug($"[Moza] Loading per-wheel mzdash folder from overlay: {ovFolder}");
                                DashCache?.LoadFromFolder(ovFolder);
                            }

                            // Apply telemetry config from overlay now that the page
                            // GUID is resolvable. ApplyTelemetrySettings reads from
                            // the current wheel's overlay.
                            try { ApplyTelemetrySettings(); }
                            catch (Exception ex)
                            {
                                MozaLog.Warn($"[Moza] ApplyTelemetrySettings after wheel-model-name failed: {ex.Message}");
                            }

                            // Capability gate (ShouldDriveDashboard) now has real
                            // input from WheelModelInfo + IsDisplayDetected, so
                            // StartTelemetryIfReady can make the correct keep-or-skip
                            // decision.
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
                        // R6: profile + overlay apply (audit A.2.1 fix).
                        ApplyWheelToHardware(_settings?.ProfileStore?.CurrentProfile);
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
                        // R6: profile-sourced apply (consolidated, sentinel-guarded).
                        ApplyHandbrakeToHardware(_settings?.ProfileStore?.CurrentProfile);
                        _deviceManager.ReadSettings(HandbrakeSettingsReadCommands);
                        MozaLog.Info("[Moza] Handbrake detected");
                    }
                    break;

                case "pedals-throttle-dir":
                    if (!_pedalsDetected)
                    {
                        _pedalsDetected = true;
                        // R6: profile-sourced apply (consolidated, sentinel-guarded).
                        ApplyPedalsToHardware(_settings?.ProfileStore?.CurrentProfile);
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

        private static void UnpackPackedColor(int packed, byte[] dst)
        {
            dst[0] = (byte)((packed >> 16) & 0xFF);
            dst[1] = (byte)((packed >> 8) & 0xFF);
            dst[2] = (byte)(packed & 0xFF);
        }

        private void WritePackedColor(string command, int packed)
        {
            byte r = (byte)((packed >> 16) & 0xFF);
            byte g = (byte)((packed >> 8) & 0xFF);
            byte b = (byte)(packed & 0xFF);
            _deviceManager.WriteColor(command, r, g, b);
        }

        // ===== Profile system (SimHub native) =====

        /// <summary>
        /// One-shot upgrade from the legacy UID/model-keyed storage to the
        /// profile-scoped <see cref="WheelOverride"/> layout. Runs at most
        /// once per install (gated by <see cref="MozaPluginSettings.SettingsSchemaVersion"/>).
        ///
        /// What migrates:
        ///  - <c>PerWheelSlots[modelName]</c> → for every profile in the store,
        ///    <c>profile.WheelOverridesByPageGuid[resolveGuid(modelPrefix)]</c>.
        ///    Scalar slot fields with non-sentinel values become the overlay's value.
        ///  - <c>TelemetryChannelMappingsByWheel[uidHex][dashKey][channel]</c> →
        ///    <c>profile.TelemetryChannelMappings[pageGuid][dashKey][channel]</c>.
        ///    UID→page-GUID resolution uses the "single model" heuristic: if there is
        ///    exactly one entry in <c>PerWheelSlots</c>, every UID-keyed entry maps to
        ///    that model. Otherwise, entries that can't be resolved are left under
        ///    the legacy dict so a future install with the right wheel attached can
        ///    re-migrate (legacy dict is kept readable through R6).
        ///  - <c>TelemetryByWheelUid[uidHex]</c> (TelemetryEnabled/Profile/Mzdash) →
        ///    same single-model heuristic; lands on the overlay's Telemetry* fields.
        ///  - <c>WheelMzdashFolderByUid[uidHex]</c> → overlay.TelemetryMzdashFolder.
        ///
        /// Migration runs against ALL profiles in the store so that game-switching
        /// preserves the legacy mappings (rather than only landing them on whichever
        /// profile happens to be current at install time). Returns true iff anything
        /// changed (caller should persist <c>_settings</c>).
        /// </summary>
        private bool MigrateSettingsToSchemaV2()
        {
            if (_settings == null || _settings.SettingsSchemaVersion >= 8)
                return false;

            var store = _settings.ProfileStore;
            var profiles = store?.Profiles?.Where(p => p != null).ToList()
                ?? new List<MozaProfile>();

            // ----- v4..v8: per-page dict seeding from flat fields. Hoisted
            // above the empty-profiles branch so that pre-refactor users
            // (no profiles in their JSON) still get their mzdash folder /
            // telemetry-enabled / wheel-era / sleep-light values carried over
            // from the flat _settings fields, per-UID dicts, and (for sleep)
            // profile baselines via JsonExtensionData. Runs through v7 so
            // users stuck at the broken schema-6 short-circuit (and the
            // schema-7 build that didn't yet move sleep) get the repair.
            // Each helper is idempotent (only fills missing entries / only
            // reads flat fields that survive ClearLegacyAfterMigration).
            // The per-overlay drain loops no-op cleanly on an empty profile list.
            bool ranV4Plus = false;
            if (_settings.SettingsSchemaVersion < 8)
            {
                ranV4Plus = true;
                MigrateMzdashFolderToPerPage(profiles);
                MigrateTelemetryEnabledToPerPage(profiles);
                MigrateWheelEraToPerPage(profiles);
                MigrateWheelSleepToPerPage(profiles);
            }

            if (profiles.Count == 0)
            {
                // No profiles yet — InitProfileSystem will create a default in a
                // moment and seed its baselines from the flat fields via
                // SeedProfileBaselineFromFlatFields. Bump straight to v8 so the
                // v6/v7 repair pass below doesn't also fire on the same launch.
                _settings.SettingsSchemaVersion = 8;
                ClearLegacyAfterMigration();
                MozaLog.Debug("[Moza] Schema v8: no profiles present, marking migrated (default profile will be seeded by InitProfileSystem)");
                return true;
            }

            if (_settings.SettingsSchemaVersion >= 3)
            {
                // v3+ → v8 path. The per-page dicts above are the only data-
                // carrying step for users who already had profiles; the
                // additional v7 repair (baseline reseed) runs unconditionally
                // here so users stuck at the broken schema-6 short-circuit
                // get their default profile's dash baselines repaired.
                foreach (var profile in profiles)
                    SeedProfileBaselineFromFlatFields(profile);

                _settings.SettingsSchemaVersion = 8;
                ClearLegacyAfterMigration();
                if (ranV4Plus)
                    MozaLog.Info("[Moza] Schema v8 migration: moved mzdash folder + telemetry-enable + wheel-era + sleep-light to per-wheel-page dicts; reseeded profile baselines from flat fields where sentinel.");
                else
                    MozaLog.Info("[Moza] Schema v8 repair: reseeded profile baselines from flat fields where sentinel.");
                return true;
            }

            // ----- Drain legacy era encodings into the flat TelemetryWheelEra -----
            // Older settings stored the firmware-era via two different fields:
            //   TelemetryFirmwareEraLegacy (int): MozaFirmwareEra enum encoded
            //   TelemetryProtocolVersion (int):   0=URL, 2=compact
            // The new schema lives on WheelOverride.TelemetryWheelEra. Translate
            // here so the per-wheel overlay seeding below picks up the right era.
            if (_settings.TelemetryWheelEra == MozaWheelEra.Auto)
            {
                if (_settings.TelemetryFirmwareEraLegacy >= 0)
                {
                    _settings.TelemetryWheelEra = _settings.TelemetryFirmwareEraLegacy switch
                    {
                        1 /* TierDefV2_Upload8B */ => MozaWheelEra.Era2025,
                        2 /* TierDefV2_Upload6B */ => MozaWheelEra.Era2025,
                        4 /* TierDefV0_Upload6B */ => MozaWheelEra.Era2024,
                        5 /* TierDefV2_Type02   */ => MozaWheelEra.Era2026,
                        _ /* 0 Auto or unknown  */ => MozaWheelEra.Auto,
                    };
                }
                else if (_settings.TelemetryProtocolVersion != -1)
                {
                    _settings.TelemetryWheelEra = _settings.TelemetryProtocolVersion == 0
                        ? MozaWheelEra.Era2024
                        : MozaWheelEra.Era2025;
                }
            }

            // ----- Resolve "single model" for UID-keyed translation -----
            Guid? singleModelGuid = null;
            if (_settings.PerWheelSlots != null && _settings.PerWheelSlots.Count == 1)
            {
                var modelName = _settings.PerWheelSlots.Keys.First();
                var prefix = WheelModelInfo.ExtractPrefix(modelName ?? "");
                if (!string.IsNullOrEmpty(prefix))
                {
                    var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
                    if (Guid.TryParse(guidStr, out var g)) singleModelGuid = g;
                }
            }

            int slotsCount = 0, channelMappingsCount = 0, uidSlotCount = 0, folderCount = 0;

            // ----- Migrate PerWheelSlots -> overlays on every profile -----
            if (_settings.PerWheelSlots != null)
            {
                foreach (var kvp in _settings.PerWheelSlots)
                {
                    var modelName = kvp.Key;
                    var slot = kvp.Value;
                    if (string.IsNullOrEmpty(modelName) || slot == null) continue;

                    var prefix = WheelModelInfo.ExtractPrefix(modelName);
                    if (string.IsNullOrEmpty(prefix)) continue;
                    var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
                    if (!Guid.TryParse(guidStr, out var pageGuid)) continue;

                    foreach (var profile in profiles)
                    {
                        if (profile.WheelOverridesByPageGuid == null)
                            profile.WheelOverridesByPageGuid = new Dictionary<Guid, WheelOverride>();
                        if (!profile.WheelOverridesByPageGuid.TryGetValue(pageGuid, out var ov) || ov == null)
                        {
                            ov = new WheelOverride();
                            profile.WheelOverridesByPageGuid[pageGuid] = ov;
                        }
                        MergeSlotIntoOverlay(slot, ov);
                    }

                    // Schema v8: sleep-light bundle lives on the per-page dict
                    // (shared across profiles), not in the overlay. Merge the
                    // slot's sleep values into the dict keyed by this page GUID.
                    if (slot.WheelSleepMode >= 0 || slot.WheelSleepTimeoutMin >= 0
                        || slot.WheelSleepSpeedMs >= 0 || slot.WheelSleepColor != null)
                    {
                        var bundle = GetOrCreateSleepBundle(pageGuid);
                        if (bundle.Mode       < 0 && slot.WheelSleepMode       >= 0) bundle.Mode       = slot.WheelSleepMode;
                        if (bundle.TimeoutMin < 0 && slot.WheelSleepTimeoutMin >= 0) bundle.TimeoutMin = slot.WheelSleepTimeoutMin;
                        if (bundle.SpeedMs    < 0 && slot.WheelSleepSpeedMs    >= 0) bundle.SpeedMs    = slot.WheelSleepSpeedMs;
                        if (bundle.Color == null && slot.WheelSleepColor != null)
                            bundle.Color = (int[])slot.WheelSleepColor.Clone();
                    }
                    slotsCount++;
                }
            }

            // ----- Migrate TelemetryChannelMappingsByWheel -> profile.TelemetryChannelMappings -----
            if (_settings.TelemetryChannelMappingsByWheel != null)
            {
                foreach (var kvp in _settings.TelemetryChannelMappingsByWheel)
                {
                    var uidHex = kvp.Key;
                    var dashMap = kvp.Value;
                    if (dashMap == null || dashMap.Count == 0) continue;

                    Guid targetGuid;
                    if (singleModelGuid.HasValue)
                    {
                        // Single-model heuristic: every UID belongs to that wheel
                        targetGuid = singleModelGuid.Value;
                    }
                    else if (string.IsNullOrEmpty(uidHex))
                    {
                        // Legacy "" slot (target of the prior intra-schema migration).
                        // Park under Guid.Empty so a future page-GUID-aware load can
                        // surface it; the UI's "what's this?" path can offer the
                        // user a relocation prompt.
                        targetGuid = Guid.Empty;
                    }
                    else
                    {
                        // Unresolvable UID with multiple models — leave under legacy.
                        MozaLog.Warn($"[Moza] Schema v2: cannot resolve UID {uidHex} to a wheel model (PerWheelSlots has {_settings.PerWheelSlots?.Count ?? 0} entries) — leaving channel mappings under legacy key");
                        continue;
                    }

                    foreach (var profile in profiles)
                    {
                        if (profile.TelemetryChannelMappings == null)
                            profile.TelemetryChannelMappings = new Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>();
                        if (!profile.TelemetryChannelMappings.TryGetValue(targetGuid, out var middle) || middle == null)
                        {
                            middle = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                            profile.TelemetryChannelMappings[targetGuid] = middle;
                        }
                        foreach (var dashKvp in dashMap)
                        {
                            if (string.IsNullOrEmpty(dashKvp.Key) || dashKvp.Value == null) continue;
                            // First-wins: don't clobber an existing entry. New saves
                            // post-migration will go via the new path.
                            if (middle.ContainsKey(dashKvp.Key)) continue;
                            middle[dashKvp.Key] = new Dictionary<string, string>(dashKvp.Value, StringComparer.OrdinalIgnoreCase);
                        }
                    }
                    channelMappingsCount++;
                }
            }

            // ----- Migrate TelemetryByWheelUid -> overlay telemetry fields -----
            // Apply only when we have a single-model fallback. The first UID's slot
            // wins (users with multiple physical wheels of the same model are out of
            // scope per the audit's user-stated constraint).
            if (singleModelGuid.HasValue && _settings.TelemetryByWheelUid != null && _settings.TelemetryByWheelUid.Count > 0)
            {
                var firstSlot = _settings.TelemetryByWheelUid
                    .Where(x => x.Value != null)
                    .Select(x => x.Value)
                    .FirstOrDefault();
                if (firstSlot != null)
                {
                    foreach (var profile in profiles)
                    {
                        if (profile.WheelOverridesByPageGuid == null)
                            profile.WheelOverridesByPageGuid = new Dictionary<Guid, WheelOverride>();
                        if (!profile.WheelOverridesByPageGuid.TryGetValue(singleModelGuid.Value, out var ov) || ov == null)
                        {
                            ov = new WheelOverride();
                            profile.WheelOverridesByPageGuid[singleModelGuid.Value] = ov;
                        }
                        if (!string.IsNullOrEmpty(firstSlot.TelemetryProfileName))
                            ov.TelemetryProfileName = firstSlot.TelemetryProfileName;
                        if (!string.IsNullOrEmpty(firstSlot.TelemetryMzdashPath))
                            ov.TelemetryMzdashPath = firstSlot.TelemetryMzdashPath;
                    }
                    // TelemetryEnabled is now per-wheel-page (not per-overlay).
                    if (firstSlot.TelemetryEnabled && singleModelGuid.HasValue)
                        _settings.WheelTelemetryEnabledByPageGuid[singleModelGuid.Value] = true;
                    uidSlotCount = _settings.TelemetryByWheelUid.Count;
                }
            }

            // ----- Migrate flat fields → profile / overlay (single read pass) -----
            // Folder migration (flat field + per-UID dict → per-page dict) has
            // already run via the hoisted MigrateMzdashFolderToPerPage call above.
            // Track the per-UID entry count for the log only.
            folderCount = _settings.WheelMzdashFolderByUid?.Count ?? 0;

            // Profile-level (motor/FFB/handbrake/pedals/AB9 are already on the
            // profile via the pre-refactor CaptureFromCurrent path — those JSON
            // fields just deserialize directly). The new profile-level fields
            // (BaseAmbient*, Gearshift*) plus the dash baselines need to be
            // seeded from the legacy MozaPluginSettings flat fields here. Shared
            // helper so the v7 repair pass and InitProfileSystem use the same logic.
            foreach (var profile in profiles)
                SeedProfileBaselineFromFlatFields(profile);

            // Per-wheel-page overlay seeding from flat Wheel*/Telemetry* fields
            // for users whose PerWheelSlots dict was empty (rare — pre-PerWheelSlots
            // builds). Done last so the slot-derived overlay takes precedence.
            if (singleModelGuid.HasValue)
            {
                foreach (var profile in profiles)
                {
                    if (profile.WheelOverridesByPageGuid == null)
                        profile.WheelOverridesByPageGuid = new Dictionary<Guid, WheelOverride>();
                    if (!profile.WheelOverridesByPageGuid.TryGetValue(singleModelGuid.Value, out var ov) || ov == null)
                    {
                        ov = new WheelOverride();
                        profile.WheelOverridesByPageGuid[singleModelGuid.Value] = ov;
                    }

                    // Wheel-LED / mode / brightness — fallback for empty PerWheelSlots.
                    if (ov.WheelTelemetryMode      < 0) ov.WheelTelemetryMode      = _settings.WheelTelemetryMode;
                    if (ov.WheelIdleEffect         < 0) ov.WheelIdleEffect         = _settings.WheelIdleEffect;
                    if (ov.WheelButtonsIdleEffect  < 0) ov.WheelButtonsIdleEffect  = _settings.WheelButtonsIdleEffect;
                    if (ov.WheelKnobIdleEffect     < 0) ov.WheelKnobIdleEffect     = _settings.WheelKnobIdleEffect;
                    if (ov.WheelKnobLedMode        < 0) ov.WheelKnobLedMode        = _settings.WheelKnobLedMode;
                    if (ov.WheelButtonsLedMode     < 0) ov.WheelButtonsLedMode     = _settings.WheelButtonsLedMode;
                    if (ov.WheelTelemetryIdleSpeedMs < 0) ov.WheelTelemetryIdleSpeedMs = _settings.WheelTelemetryIdleSpeedMs;
                    if (ov.WheelButtonsIdleSpeedMs   < 0) ov.WheelButtonsIdleSpeedMs   = _settings.WheelButtonsIdleSpeedMs;
                    if (ov.WheelKnobIdleSpeedMs      < 0) ov.WheelKnobIdleSpeedMs      = _settings.WheelKnobIdleSpeedMs;
                    // WheelSleep* is no longer on the overlay — handled by
                    // MigrateWheelSleepToPerPage above (which seeds the
                    // per-wheel-page dict directly from _settings flat fields).
                    if (ov.WheelRpmBrightness      < 0) ov.WheelRpmBrightness      = _settings.WheelRpmBrightness;
                    if (ov.WheelButtonsBrightness  < 0) ov.WheelButtonsBrightness  = _settings.WheelButtonsBrightness;
                    if (ov.WheelFlagsBrightness    < 0) ov.WheelFlagsBrightness    = _settings.WheelFlagsBrightness;
                    if (ov.WheelESRpmBrightness    < 0) ov.WheelESRpmBrightness    = _settings.WheelESRpmBrightness;
                    if (ov.WheelRpmIndicatorMode   < 0) ov.WheelRpmIndicatorMode   = _settings.WheelRpmIndicatorMode;
                    if (ov.WheelRpmDisplayMode     < 0) ov.WheelRpmDisplayMode     = _settings.WheelRpmDisplayMode;
                    if (ov.WheelPaddlesMode        < 0) ov.WheelPaddlesMode        = _settings.WheelPaddlesMode;
                    if (ov.WheelClutchPoint        < 0) ov.WheelClutchPoint        = _settings.WheelClutchPoint;
                    if (ov.WheelKnobMode           < 0) ov.WheelKnobMode           = _settings.WheelKnobMode;
                    if (ov.WheelStickMode          < 0) ov.WheelStickMode          = _settings.WheelStickMode;
                    if (ov.WheelRpmBlinkColors == null && _settings.WheelRpmBlinkColors != null)
                        ov.WheelRpmBlinkColors = (int[])_settings.WheelRpmBlinkColors.Clone();
                    if (ov.WheelKnobBackgroundColors == null && _settings.WheelKnobBackgroundColors != null)
                        ov.WheelKnobBackgroundColors = (int[])_settings.WheelKnobBackgroundColors.Clone();
                    if (ov.WheelKnobPrimaryColors == null && _settings.WheelKnobPrimaryColors != null)
                        ov.WheelKnobPrimaryColors = (int[])_settings.WheelKnobPrimaryColors.Clone();
                    if (ov.WheelKnobRingColors == null && _settings.WheelKnobRingColors != null)
                        ov.WheelKnobRingColors = (int[])_settings.WheelKnobRingColors.Clone();
                    if (ov.WheelKnobRingBrightness < 0) ov.WheelKnobRingBrightness = _settings.WheelKnobRingBrightness;

                    // Telemetry (per-wheel-page-per-game). Enabled-flag is
                    // per-wheel-page only (set below outside this profile loop).
                    if (string.IsNullOrEmpty(ov.TelemetryProfileName)
                        && !string.IsNullOrEmpty(_settings.TelemetryProfileName))
                        ov.TelemetryProfileName = _settings.TelemetryProfileName;
                    if (string.IsNullOrEmpty(ov.TelemetryMzdashPath)
                        && !string.IsNullOrEmpty(_settings.TelemetryMzdashPath))
                        ov.TelemetryMzdashPath = _settings.TelemetryMzdashPath;
                }
            }

            // v4..v8 step: per-wheel-page dict seeding (folder + enable + era + sleep).
            // Re-run unconditionally here — the hoisted call at the top happens
            // BEFORE the legacy-era-encoding drain above sets
            // `_settings.TelemetryWheelEra` from `TelemetryFirmwareEraLegacy` /
            // `TelemetryProtocolVersion`, so the era helper needs a second pass
            // to pick that up. The other helpers are idempotent (their flat
            // fields are already cleared, so those become no-ops).
            MigrateMzdashFolderToPerPage(profiles);
            MigrateTelemetryEnabledToPerPage(profiles);
            MigrateWheelEraToPerPage(profiles);
            MigrateWheelSleepToPerPage(profiles);

            _settings.SettingsSchemaVersion = 8;
            MozaLog.Info(
                $"[Moza] Schema v8 migration: PerWheelSlots={slotsCount}, " +
                $"ChannelMappings={channelMappingsCount}, TelemetryByUid={uidSlotCount}, " +
                $"MzdashFolderByUid={folderCount} → applied across {profiles.Count} profile(s); " +
                $"flat-field seeding done");
            ClearLegacyAfterMigration();
            return true;
        }

        /// <summary>
        /// Seed the dash / base-ambient / gearshift baselines on a single profile
        /// from the legacy <see cref="MozaPluginSettings"/> flat fields whenever
        /// the profile field is still at its sentinel (-1 for ints, null for arrays).
        /// Used by:
        ///   - v0 → v7 migration (all profiles in the store)
        ///   - v6 → v7 repair (all profiles, restoring brightness for users who hit
        ///     the broken schema-6 short-circuit)
        ///   - <see cref="InitProfileSystem"/> for a freshly-created default profile
        ///     so its baseline reflects the user's saved settings even when no
        ///     migration logic touched it (pre-refactor upgrades whose JSON had
        ///     no profile entries at all).
        /// Idempotent: only writes when the profile slot is sentinel.
        /// </summary>
        private void SeedProfileBaselineFromFlatFields(MozaProfile profile)
        {
            if (_settings == null || profile == null) return;

            // Dash brightness baselines: copy if the profile hasn't seen them.
            if (profile.DashRpmBrightness     < 0) profile.DashRpmBrightness     = _settings.DashRpmBrightness;
            if (profile.DashFlagsBrightness   < 0) profile.DashFlagsBrightness   = _settings.DashFlagsBrightness;
            if (profile.DashDisplayBrightness < 0) profile.DashDisplayBrightness = _settings.DashDisplayBrightness;
            if (profile.DashDisplayStandbyMin < 0) profile.DashDisplayStandbyMin = _settings.DashDisplayStandbyMin;
            if (profile.DashRpmBlinkColors == null && _settings.DashRpmBlinkColors != null)
                profile.DashRpmBlinkColors = (int[])_settings.DashRpmBlinkColors.Clone();

            // Base ambient.
            if (profile.BaseAmbientBrightness     < 0) profile.BaseAmbientBrightness     = _settings.BaseAmbientBrightness;
            if (profile.BaseAmbientStandbyMode    < 0) profile.BaseAmbientStandbyMode    = _settings.BaseAmbientStandbyMode;
            if (profile.BaseAmbientIndicatorState < 0) profile.BaseAmbientIndicatorState = _settings.BaseAmbientIndicatorState;
            if (profile.BaseAmbientSleepMode      < 0) profile.BaseAmbientSleepMode      = _settings.BaseAmbientSleepMode;
            if (profile.BaseAmbientSleepTimeout   < 0) profile.BaseAmbientSleepTimeout   = _settings.BaseAmbientSleepTimeout;
            if (profile.BaseAmbientStartupColor   < 0) profile.BaseAmbientStartupColor   = _settings.BaseAmbientStartupColor;
            if (profile.BaseAmbientShutdownColor  < 0) profile.BaseAmbientShutdownColor  = _settings.BaseAmbientShutdownColor;

            // Gearshift.
            if (profile.GearshiftVibrateOnNeutral < 0) profile.GearshiftVibrateOnNeutral = _settings.GearshiftVibrateOnNeutral ? 1 : 0;
            if (profile.GearshiftDebounceMs       < 0) profile.GearshiftDebounceMs       = _settings.GearshiftDebounceMs;
        }

        /// <summary>
        /// Schema v8 step: move wheel sleep-light settings (mode / timeout /
        /// speed / color) off the per-game-per-wheel overlay onto
        /// <see cref="MozaPluginSettings.WheelSleepByPageGuid"/>. Sleep is a
        /// firmware preference tied to the wheel, not the game — making it
        /// per-(game × wheel) would force users to copy the same value to
        /// every profile. First non-sentinel value per page-GUID wins.
        ///
        /// Drains from three sources:
        ///   1. <see cref="WheelOverride.LegacyJsonFields"/> "WheelSleepMode"
        ///      / "WheelSleepTimeoutMin" / "WheelSleepSpeedMs" / "WheelSleepColor"
        ///      keys on every overlay across all profiles.
        ///   2. <see cref="MozaProfile.LegacyJsonFields"/> same keys on the
        ///      profile baseline. Applied universally to every known wheel
        ///      page (single-wheel users will see the same values everywhere).
        ///   3. <see cref="MozaPluginSettings.WheelSleepMode"/> etc. flat
        ///      fields. Applied universally same as the profile baseline.
        /// </summary>
        private void MigrateWheelSleepToPerPage(List<MozaProfile> profiles)
        {
            if (_settings == null) return;
            if (_settings.WheelSleepByPageGuid == null)
                _settings.WheelSleepByPageGuid = new Dictionary<Guid, WheelSleepSettings>();

            // 1. Drain pre-v8 per-overlay sleep values from LegacyJsonFields.
            //    First non-sentinel value per page-guid wins.
            foreach (var profile in profiles)
            {
                if (profile.WheelOverridesByPageGuid == null) continue;
                foreach (var kvp in profile.WheelOverridesByPageGuid)
                {
                    var ov = kvp.Value;
                    if (ov?.LegacyJsonFields == null) continue;
                    var bundle = GetOrCreateSleepBundle(kvp.Key);
                    DrainSleepKeysInto(ov.LegacyJsonFields, bundle);
                    if (ov.LegacyJsonFields.Count == 0) ov.LegacyJsonFields = null;
                }
            }

            // 2. Drain pre-v8 profile-baseline sleep values from
            //    MozaProfile.LegacyJsonFields. Applies universally (same value
            //    on every known wheel page) since the baseline wasn't keyed.
            foreach (var profile in profiles)
            {
                if (profile.LegacyJsonFields == null) continue;
                var staged = new WheelSleepSettings();
                bool any = DrainSleepKeysInto(profile.LegacyJsonFields, staged);
                if (any) SeedSleepUniversally(staged);
                if (profile.LegacyJsonFields.Count == 0) profile.LegacyJsonFields = null;
            }

            // 3. Seed every known wheel page GUID from the flat fields when
            //    not yet set. Single-wheel users get their settings preserved.
            var flat = new WheelSleepSettings
            {
                Mode = _settings.WheelSleepMode,
                TimeoutMin = _settings.WheelSleepTimeoutMin,
                SpeedMs = _settings.WheelSleepSpeedMs,
                Color = _settings.WheelSleepColor,
            };
            if (flat.Mode >= 0 || flat.TimeoutMin >= 0 || flat.SpeedMs >= 0 || flat.Color != null)
                SeedSleepUniversally(flat);

            // Clear the flat fields so they don't re-serialize stale values.
            _settings.WheelSleepMode = -1;
            _settings.WheelSleepTimeoutMin = -1;
            _settings.WheelSleepSpeedMs = -1;
            _settings.WheelSleepColor = null;
        }

        /// <summary>
        /// Helper for <see cref="MigrateWheelSleepToPerPage"/>: pull
        /// WheelSleep* keys out of a LegacyJsonFields dict into a bundle. Each
        /// field is merged only if the bundle still has the sentinel value
        /// (so the first non-sentinel value wins across multiple drain calls).
        /// Returns true iff any field was actually populated.
        /// </summary>
        private static bool DrainSleepKeysInto(
            Dictionary<string, Newtonsoft.Json.Linq.JToken> legacy,
            WheelSleepSettings bundle)
        {
            bool any = false;
            if (bundle.Mode < 0 && legacy.TryGetValue("WheelSleepMode", out var mTok) && mTok != null)
            {
                try { var v = mTok.ToObject<int>(); if (v >= 0) { bundle.Mode = v; any = true; } } catch { }
            }
            if (bundle.TimeoutMin < 0 && legacy.TryGetValue("WheelSleepTimeoutMin", out var tTok) && tTok != null)
            {
                try { var v = tTok.ToObject<int>(); if (v >= 0) { bundle.TimeoutMin = v; any = true; } } catch { }
            }
            if (bundle.SpeedMs < 0 && legacy.TryGetValue("WheelSleepSpeedMs", out var sTok) && sTok != null)
            {
                try { var v = sTok.ToObject<int>(); if (v >= 0) { bundle.SpeedMs = v; any = true; } } catch { }
            }
            if (bundle.Color == null && legacy.TryGetValue("WheelSleepColor", out var cTok) && cTok != null)
            {
                try { var v = cTok.ToObject<int[]>(); if (v != null && v.Length > 0) { bundle.Color = v; any = true; } } catch { }
            }
            legacy.Remove("WheelSleepMode");
            legacy.Remove("WheelSleepTimeoutMin");
            legacy.Remove("WheelSleepSpeedMs");
            legacy.Remove("WheelSleepColor");
            return any;
        }

        /// <summary>
        /// Get-or-create a sleep bundle for the given page GUID inside
        /// <see cref="MozaPluginSettings.WheelSleepByPageGuid"/>. Used by
        /// <see cref="MigrateWheelSleepToPerPage"/>.
        /// </summary>
        private WheelSleepSettings GetOrCreateSleepBundle(Guid pageGuid)
        {
            if (!_settings!.WheelSleepByPageGuid.TryGetValue(pageGuid, out var bundle) || bundle == null)
            {
                bundle = new WheelSleepSettings();
                _settings.WheelSleepByPageGuid[pageGuid] = bundle;
            }
            return bundle;
        }

        /// <summary>
        /// Seed every known wheel page GUID's sleep bundle with values from the
        /// given <paramref name="staged"/> bundle when the destination slot is
        /// still at its sentinel. Used by <see cref="MigrateWheelSleepToPerPage"/>
        /// to apply profile-baseline / flat-field values universally.
        /// </summary>
        private void SeedSleepUniversally(WheelSleepSettings staged)
        {
            void Seed(Guid pageGuid)
            {
                var dst = GetOrCreateSleepBundle(pageGuid);
                if (dst.Mode < 0 && staged.Mode >= 0)             dst.Mode       = staged.Mode;
                if (dst.TimeoutMin < 0 && staged.TimeoutMin >= 0) dst.TimeoutMin = staged.TimeoutMin;
                if (dst.SpeedMs < 0 && staged.SpeedMs >= 0)       dst.SpeedMs    = staged.SpeedMs;
                if (dst.Color == null && staged.Color != null)    dst.Color      = (int[])staged.Color.Clone();
            }
            foreach (var (prefix, _, _) in WheelModelInfo.KnownModels)
            {
                var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
                if (Guid.TryParse(guidStr, out var pageGuid)) Seed(pageGuid);
            }
            if (Guid.TryParse(MozaDeviceConstants.WheelGenericGuid, out var gg)) Seed(gg);
            if (Guid.TryParse(MozaDeviceConstants.WheelOldProtoGuid, out var og)) Seed(og);
        }

        /// <summary>
        /// Schema v6 step: move <c>TelemetryWheelEra</c> from per-overlay storage
        /// onto <see cref="MozaPluginSettings.WheelTelemetryEraByPageGuid"/>
        /// (per-wheel-page, shared across profiles). Wheel firmware era is a
        /// property of the wheel/firmware, not the game.
        /// </summary>
        private void MigrateWheelEraToPerPage(List<MozaProfile> profiles)
        {
            if (_settings == null) return;
            if (_settings.WheelTelemetryEraByPageGuid == null)
                _settings.WheelTelemetryEraByPageGuid = new Dictionary<Guid, int>();

            // Drain pre-v6 per-overlay TelemetryWheelEra values from LegacyJsonFields.
            // First non-sentinel value per page-guid wins.
            foreach (var profile in profiles)
            {
                if (profile.WheelOverridesByPageGuid == null) continue;
                foreach (var kvp in profile.WheelOverridesByPageGuid)
                {
                    var ov = kvp.Value;
                    if (ov?.LegacyJsonFields == null) continue;
                    if (ov.LegacyJsonFields.TryGetValue("TelemetryWheelEra", out var tok)
                        && tok != null)
                    {
                        int v;
                        try { v = tok.ToObject<int>(); }
                        catch { v = -1; }
                        if (v >= 0
                            && !_settings.WheelTelemetryEraByPageGuid.ContainsKey(kvp.Key))
                            _settings.WheelTelemetryEraByPageGuid[kvp.Key] = v;
                    }
                    ov.LegacyJsonFields.Remove("TelemetryWheelEra");
                    if (ov.LegacyJsonFields.Count == 0) ov.LegacyJsonFields = null;
                }
            }

            // Seed every known wheel page GUID from the flat field when not yet
            // set. Treats Auto (0) as "no opinion" to avoid stomping user-curated
            // overlay values when both flat and overlay are Auto.
            int flatEra = (int)_settings.TelemetryWheelEra;
            if (flatEra > 0)
            {
                foreach (var (prefix, _, _) in WheelModelInfo.KnownModels)
                {
                    var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
                    if (!Guid.TryParse(guidStr, out var pageGuid)) continue;
                    if (!_settings.WheelTelemetryEraByPageGuid.ContainsKey(pageGuid))
                        _settings.WheelTelemetryEraByPageGuid[pageGuid] = flatEra;
                }
                if (Guid.TryParse(MozaDeviceConstants.WheelGenericGuid, out var gg)
                    && !_settings.WheelTelemetryEraByPageGuid.ContainsKey(gg))
                    _settings.WheelTelemetryEraByPageGuid[gg] = flatEra;
                if (Guid.TryParse(MozaDeviceConstants.WheelOldProtoGuid, out var og)
                    && !_settings.WheelTelemetryEraByPageGuid.ContainsKey(og))
                    _settings.WheelTelemetryEraByPageGuid[og] = flatEra;
            }
            // Clear the flat field so it doesn't re-serialize a stale value.
            _settings.TelemetryWheelEra = MozaWheelEra.Auto;
        }

        /// <summary>
        /// Schema v5 step: move <c>TelemetryEnabled</c> from per-overlay storage
        /// onto <see cref="MozaPluginSettings.WheelTelemetryEnabledByPageGuid"/>
        /// (per-wheel-page, shared across profiles). OR-merges across profiles:
        /// if ANY game's overlay had a wheel enabled, the wheel stays enabled.
        /// </summary>
        private void MigrateTelemetryEnabledToPerPage(List<MozaProfile> profiles)
        {
            if (_settings == null) return;
            if (_settings.WheelTelemetryEnabledByPageGuid == null)
                _settings.WheelTelemetryEnabledByPageGuid = new Dictionary<Guid, bool>();

            // Drain pre-v5 overlay JSON values captured by WheelOverride.LegacyJsonFields.
            foreach (var profile in profiles)
            {
                if (profile.WheelOverridesByPageGuid == null) continue;
                foreach (var kvp in profile.WheelOverridesByPageGuid)
                {
                    var ov = kvp.Value;
                    if (ov?.LegacyJsonFields == null) continue;
                    if (ov.LegacyJsonFields.TryGetValue("TelemetryEnabled", out var tok)
                        && tok != null)
                    {
                        // Stored as int (-1/0/1) before the property removal. 1 = on.
                        int v;
                        try { v = tok.ToObject<int>(); }
                        catch { v = -1; }
                        if (v == 1)
                            _settings.WheelTelemetryEnabledByPageGuid[kvp.Key] = true;
                    }
                    ov.LegacyJsonFields.Remove("TelemetryEnabled");
                    ov.LegacyJsonFields.Remove("TelemetryMzdashFolder");
                    if (ov.LegacyJsonFields.Count == 0) ov.LegacyJsonFields = null;
                }
            }

            // Also seed every known wheel page GUID from the flat field if the
            // dict doesn't have an entry yet AND the flat says true. Migration
            // sets value-true entries only; absence = false (default).
            if (_settings.TelemetryEnabled)
            {
                foreach (var (prefix, _, _) in WheelModelInfo.KnownModels)
                {
                    var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
                    if (!Guid.TryParse(guidStr, out var pageGuid)) continue;
                    if (!_settings.WheelTelemetryEnabledByPageGuid.ContainsKey(pageGuid))
                        _settings.WheelTelemetryEnabledByPageGuid[pageGuid] = true;
                }
                if (Guid.TryParse(MozaDeviceConstants.WheelGenericGuid, out var gg)
                    && !_settings.WheelTelemetryEnabledByPageGuid.ContainsKey(gg))
                    _settings.WheelTelemetryEnabledByPageGuid[gg] = true;
                if (Guid.TryParse(MozaDeviceConstants.WheelOldProtoGuid, out var og)
                    && !_settings.WheelTelemetryEnabledByPageGuid.ContainsKey(og))
                    _settings.WheelTelemetryEnabledByPageGuid[og] = true;
            }
            // Clear the flat field so it doesn't re-serialize a stale value.
            _settings.TelemetryEnabled = false;
        }

        /// <summary>
        /// Schema v4 step: move mzdash folder from per-overlay storage onto
        /// <see cref="MozaPluginSettings.WheelMzdashFolderByPageGuid"/> (one
        /// folder per wheel-page, shared across all profiles for that wheel).
        /// </summary>
        /// <param name="profiles">Already-loaded profile list to drain from.</param>
        private void MigrateMzdashFolderToPerPage(List<MozaProfile> profiles)
        {
            if (_settings == null) return;
            if (_settings.WheelMzdashFolderByPageGuid == null)
                _settings.WheelMzdashFolderByPageGuid = new Dictionary<Guid, string>();

            // 0. Drain pre-v4 per-overlay TelemetryMzdashFolder values (captured
            //    by WheelOverride.LegacyJsonFields). First non-empty value per
            //    page-guid wins (the dict is shared across profiles).
            foreach (var profile in profiles)
            {
                if (profile.WheelOverridesByPageGuid == null) continue;
                foreach (var kvp in profile.WheelOverridesByPageGuid)
                {
                    var ov = kvp.Value;
                    if (ov?.LegacyJsonFields == null) continue;
                    if (ov.LegacyJsonFields.TryGetValue("TelemetryMzdashFolder", out var tok)
                        && tok != null)
                    {
                        string folder = "";
                        try { folder = tok.ToObject<string>() ?? ""; }
                        catch { }
                        if (!string.IsNullOrEmpty(folder)
                            && !_settings.WheelMzdashFolderByPageGuid.ContainsKey(kvp.Key))
                            _settings.WheelMzdashFolderByPageGuid[kvp.Key] = folder;
                    }
                }
            }

            // 1. Seed every known wheel page GUID from the flat folder if the
            //    dict doesn't have an entry yet. This gives every wheel the
            //    user's pre-refactor folder. They can later customize per-wheel.
            //    Falls back to the first non-empty entry in the per-UID dict
            //    (older builds stored the folder there) when the flat field
            //    is empty. Single-wheel users keep their folder regardless
            //    of which storage shape they had.
            string flatFolder = _settings.TelemetryMzdashFolder ?? "";
            if (string.IsNullOrEmpty(flatFolder)
                && _settings.WheelMzdashFolderByUid != null
                && _settings.WheelMzdashFolderByUid.Count > 0)
            {
                flatFolder = _settings.WheelMzdashFolderByUid
                    .Where(x => !string.IsNullOrEmpty(x.Value))
                    .Select(x => x.Value)
                    .FirstOrDefault() ?? "";
            }
            if (!string.IsNullOrEmpty(flatFolder))
            {
                foreach (var (prefix, _, _) in WheelModelInfo.KnownModels)
                {
                    var guidStr = MozaDeviceConstants.ResolveWheelGuid(prefix);
                    if (!Guid.TryParse(guidStr, out var pageGuid)) continue;
                    if (!_settings.WheelMzdashFolderByPageGuid.ContainsKey(pageGuid))
                        _settings.WheelMzdashFolderByPageGuid[pageGuid] = flatFolder;
                }
                // Also the generic / old-protocol page GUIDs.
                if (Guid.TryParse(MozaDeviceConstants.WheelGenericGuid, out var gg)
                    && !_settings.WheelMzdashFolderByPageGuid.ContainsKey(gg))
                    _settings.WheelMzdashFolderByPageGuid[gg] = flatFolder;
                if (Guid.TryParse(MozaDeviceConstants.WheelOldProtoGuid, out var og)
                    && !_settings.WheelMzdashFolderByPageGuid.ContainsKey(og))
                    _settings.WheelMzdashFolderByPageGuid[og] = flatFolder;
            }
            // Clear the flat field so it doesn't re-serialize. The per-UID
            // dict is wiped later by ClearLegacyAfterMigration.
            _settings.TelemetryMzdashFolder = "";
        }

        /// <summary>
        /// Null out / clear every legacy MozaPluginSettings store after the
        /// one-shot migration completes. Subsequent saves serialize them as
        /// null/empty so the on-disk JSON shrinks; subsequent loads have no
        /// data to read back. Single source of truth = profile + overlay.
        /// </summary>
        private void ClearLegacyAfterMigration()
        {
            if (_settings == null) return;
            _settings.PerWheelSlots = new Dictionary<string, PerWheelSlot>(StringComparer.OrdinalIgnoreCase);
            _settings.TelemetryByWheelUid = new Dictionary<string, TelemetryWheelSlot>(StringComparer.OrdinalIgnoreCase);
            _settings.WheelMzdashFolderByUid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _settings.TelemetryChannelMappingsByWheel = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            _settings.TelemetryChannelMappings = null;
            _settings.TelemetryProtocolVersion = -1;
            _settings.TelemetryFirmwareEraLegacy = -1;
            // Clear flat telemetry remnants (the migration drained any data
            // they held into the per-wheel-page dicts).
            _settings.TelemetryProfileName = "";
            _settings.TelemetryMzdashPath = "";
        }

        /// <summary>
        /// Copy non-sentinel fields from a legacy <see cref="PerWheelSlot"/> into a
        /// <see cref="WheelOverride"/>. Existing overlay values are preserved when
        /// the slot field is sentinel (-1 / null) so re-running migration is safe.
        /// </summary>
        private static void MergeSlotIntoOverlay(PerWheelSlot slot, WheelOverride ov)
        {
            if (slot.WheelTelemetryMode     >= 0) ov.WheelTelemetryMode     = slot.WheelTelemetryMode;
            if (slot.WheelIdleEffect        >= 0) ov.WheelIdleEffect        = slot.WheelIdleEffect;
            if (slot.WheelButtonsIdleEffect >= 0) ov.WheelButtonsIdleEffect = slot.WheelButtonsIdleEffect;
            if (slot.WheelKnobIdleEffect    >= 0) ov.WheelKnobIdleEffect    = slot.WheelKnobIdleEffect;
            if (slot.WheelKnobLedMode       >= 0) ov.WheelKnobLedMode       = slot.WheelKnobLedMode;
            if (slot.WheelButtonsLedMode    >= 0) ov.WheelButtonsLedMode    = slot.WheelButtonsLedMode;
            if (slot.WheelTelemetryIdleSpeedMs >= 0) ov.WheelTelemetryIdleSpeedMs = slot.WheelTelemetryIdleSpeedMs;
            if (slot.WheelButtonsIdleSpeedMs   >= 0) ov.WheelButtonsIdleSpeedMs   = slot.WheelButtonsIdleSpeedMs;
            if (slot.WheelKnobIdleSpeedMs      >= 0) ov.WheelKnobIdleSpeedMs      = slot.WheelKnobIdleSpeedMs;
            // WheelSleep* is migrated by the caller into
            // MozaPluginSettings.WheelSleepByPageGuid (not the overlay).
            if (slot.WheelRpmBrightness     >= 0) ov.WheelRpmBrightness     = slot.WheelRpmBrightness;
            if (slot.WheelButtonsBrightness >= 0) ov.WheelButtonsBrightness = slot.WheelButtonsBrightness;
            if (slot.WheelFlagsBrightness   >= 0) ov.WheelFlagsBrightness   = slot.WheelFlagsBrightness;
            if (slot.WheelESRpmBrightness   >= 0) ov.WheelESRpmBrightness   = slot.WheelESRpmBrightness;
            if (slot.WheelRpmIndicatorMode  >= 0) ov.WheelRpmIndicatorMode  = slot.WheelRpmIndicatorMode;
            if (slot.WheelRpmDisplayMode    >= 0) ov.WheelRpmDisplayMode    = slot.WheelRpmDisplayMode;
            if (slot.WheelPaddlesMode       >= 0) ov.WheelPaddlesMode       = slot.WheelPaddlesMode;
            if (slot.WheelClutchPoint       >= 0) ov.WheelClutchPoint       = slot.WheelClutchPoint;
            if (slot.WheelKnobMode          >= 0) ov.WheelKnobMode          = slot.WheelKnobMode;
            if (slot.WheelStickMode         >= 0) ov.WheelStickMode         = slot.WheelStickMode;
            if (slot.WheelRpmBlinkColors    != null) ov.WheelRpmBlinkColors = (int[])slot.WheelRpmBlinkColors.Clone();
            if (slot.WheelKnobBackgroundColors != null) ov.WheelKnobBackgroundColors = (int[])slot.WheelKnobBackgroundColors.Clone();
            if (slot.WheelKnobPrimaryColors    != null) ov.WheelKnobPrimaryColors    = (int[])slot.WheelKnobPrimaryColors.Clone();
            if (slot.WheelKnobRingColors       != null) ov.WheelKnobRingColors       = (int[])slot.WheelKnobRingColors.Clone();
            if (slot.WheelKnobRingBrightness   >= 0) ov.WheelKnobRingBrightness      = slot.WheelKnobRingBrightness;
        }

        // ===== R3: consolidated hardware-apply entry points (2026-05-14) =====
        // Replaces the six ApplySaved*Settings methods + the wheel-LED block inside
        // ApplyProfile + the per-extension hardware-write code paths. Single source
        // of truth: MozaProfile + WheelOverride. Every WriteSetting/WriteColor is
        // detection-gated AND sentinel-guarded (audit A.2.1 fix — no brightness
        // write storm on cold start).
        //
        // Call sites (wired in R5/R6, dead until then):
        //   - DetectDevices: each detection-flag flip calls the matching Apply*ToHardware
        //   - OnProfileChanged / ApplyProfile: calls all seven
        //   - Device-extension SetSettings: calls Apply*ToHardware after ApplyTo

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

        private static int Eff(int overlayVal, int baselineVal) =>
            overlayVal >= 0 ? overlayVal : baselineVal;

        private static int[]? EffArr(int[]? overlayArr, int[]? baselineArr) =>
            overlayArr ?? baselineArr;

        /// <summary>
        /// Push wheel-scoped settings (LED modes, brightness, colors, sleep, input
        /// modes) to the connected wheel. Sources from the profile baseline overlaid
        /// by the current wheel's page-GUID-keyed overlay. No-op if no wheel is
        /// detected. Every write is sentinel-guarded.
        /// </summary>
        internal void ApplyWheelToHardware(MozaProfile? profile)
        {
            if (profile == null) return;
            // No early-out on detection — _data is mirrored regardless so the UI
            // shows correct values when the wheel later connects. The hardware
            // write blocks below are gated on the detection flags.
            bool deviceLive = _data.IsConnected;

            var ov = GetCurrentWheelOverlay(profile);

            // Mirror effective values into _data so the UI reflects what we pushed.
            int telemMode      = Eff(ov?.WheelTelemetryMode ?? -1, profile.WheelTelemetryMode);
            int idleEffect     = Eff(ov?.WheelIdleEffect ?? -1, profile.WheelIdleEffect);
            int btnIdleEffect  = Eff(ov?.WheelButtonsIdleEffect ?? -1, profile.WheelButtonsIdleEffect);
            int knobIdleEffect = Eff(ov?.WheelKnobIdleEffect ?? -1, profile.WheelKnobIdleEffect);
            int knobLedMode    = Eff(ov?.WheelKnobLedMode ?? -1, profile.WheelKnobLedMode);
            int btnLedMode     = Eff(ov?.WheelButtonsLedMode ?? -1, profile.WheelButtonsLedMode);
            int idleSpeed      = Eff(ov?.WheelTelemetryIdleSpeedMs ?? -1, profile.WheelTelemetryIdleSpeedMs);
            int btnIdleSpeed   = Eff(ov?.WheelButtonsIdleSpeedMs ?? -1, profile.WheelButtonsIdleSpeedMs);
            int knobIdleSpeed  = Eff(ov?.WheelKnobIdleSpeedMs ?? -1, profile.WheelKnobIdleSpeedMs);
            // Sleep settings: per-wheel-page dict on MozaPluginSettings, shared
            // across profiles (schema v8). Null bundle = no saved value, leave
            // the wheel's currently-stored sleep config alone.
            var sleepBundle    = ActiveWheelSleep;
            int sleepMode      = sleepBundle?.Mode ?? -1;
            int sleepTimeout   = sleepBundle?.TimeoutMin ?? -1;
            int sleepSpeed     = sleepBundle?.SpeedMs ?? -1;
            int[]? sleepColor  = sleepBundle?.Color;
            int rpmBri         = Eff(ov?.WheelRpmBrightness ?? -1, profile.WheelRpmBrightness);
            int btnBri         = Eff(ov?.WheelButtonsBrightness ?? -1, profile.WheelButtonsBrightness);
            int flagsBri       = Eff(ov?.WheelFlagsBrightness ?? -1, profile.WheelFlagsBrightness);
            int rpmInd         = Eff(ov?.WheelRpmIndicatorMode ?? -1, profile.WheelRpmIndicatorMode);
            int rpmDisp        = Eff(ov?.WheelRpmDisplayMode ?? -1, profile.WheelRpmDisplayMode);
            int esRpmBri       = Eff(ov?.WheelESRpmBrightness ?? -1, profile.WheelESRpmBrightness);
            // Inputs — overlay-only (no profile baseline)
            int paddles        = ov?.WheelPaddlesMode ?? -1;
            int clutchPoint    = ov?.WheelClutchPoint ?? -1;
            int knobMode       = ov?.WheelKnobMode ?? -1;
            int stickMode      = ov?.WheelStickMode ?? -1;
            int knobRingBri    = Eff(ov?.WheelKnobRingBrightness ?? -1, profile.WheelKnobRingBrightness);

            // _data mirror (UI binding)
            if (telemMode      >= 0) _data.WheelTelemetryMode      = telemMode;
            if (idleEffect     >= 0) _data.WheelTelemetryIdleEffect = idleEffect;
            if (btnIdleEffect  >= 0) _data.WheelButtonsIdleEffect  = btnIdleEffect;
            if (knobIdleEffect >= 0) _data.WheelKnobIdleEffect     = knobIdleEffect;
            if (knobLedMode    >= 0) _data.WheelKnobLedMode        = knobLedMode;
            if (btnLedMode     >= 0) _data.WheelButtonsLedMode     = btnLedMode;
            if (idleSpeed      >= 0) _data.WheelTelemetryIdleSpeedMs = idleSpeed;
            if (btnIdleSpeed   >= 0) _data.WheelButtonsIdleSpeedMs = btnIdleSpeed;
            if (knobIdleSpeed  >= 0) _data.WheelKnobIdleSpeedMs    = knobIdleSpeed;
            if (sleepMode      >= 0) _data.WheelIdleMode           = sleepMode;
            if (sleepTimeout   >= 0) _data.WheelIdleTimeout        = sleepTimeout;
            if (sleepSpeed     >= 0) _data.WheelIdleSpeed          = sleepSpeed;
            if (sleepColor != null && sleepColor.Length > 0)
            {
                var rgb = MozaProfile.UnpackColor(sleepColor[0]);
                _data.WheelIdleColor[0] = rgb[0];
                _data.WheelIdleColor[1] = rgb[1];
                _data.WheelIdleColor[2] = rgb[2];
            }
            if (rpmBri    >= 0) _data.WheelRpmBrightness     = rpmBri;
            if (btnBri    >= 0) _data.WheelButtonsBrightness = btnBri;
            if (flagsBri  >= 0) _data.WheelFlagsBrightness   = flagsBri;
            if (esRpmBri  >= 0) _data.WheelESRpmBrightness   = esRpmBri;
            if (rpmInd    >= 0) _data.WheelRpmIndicatorMode  = rpmInd;
            if (rpmDisp   >= 0) _data.WheelRpmDisplayMode    = rpmDisp;
            if (paddles   >= 0) _data.WheelPaddlesMode       = paddles;
            if (clutchPoint >= 0) _data.WheelClutchPoint     = clutchPoint;
            if (knobMode  >= 0) _data.WheelKnobMode          = knobMode;
            if (stickMode >= 0) _data.WheelStickMode         = stickMode;

            int[]? rpmColors          = EffArr(ov?.WheelRpmColors, profile.WheelRpmColors);
            int[]? rpmBlinkColors     = EffArr(ov?.WheelRpmBlinkColors, profile.WheelRpmBlinkColors);
            int[]? buttonColors       = EffArr(ov?.WheelButtonColors, profile.WheelButtonColors);
            bool[]? buttonDefaults    = ov?.WheelButtonDefaultDuringTelemetry
                                        ?? profile.WheelButtonDefaultDuringTelemetry;
            int[]? flagColors         = EffArr(ov?.WheelFlagColors, profile.WheelFlagColors);
            int[]? idleColor          = EffArr(ov?.WheelIdleColor, profile.WheelIdleColor);
            int[]? esRpmColors        = EffArr(ov?.WheelESRpmColors, profile.WheelESRpmColors);
            int[]? knobBgColors       = EffArr(ov?.WheelKnobBackgroundColors, profile.WheelKnobBackgroundColors);
            int[]? knobPrimaryColors  = EffArr(ov?.WheelKnobPrimaryColors, profile.WheelKnobPrimaryColors);
            int[]? knobRingColors     = EffArr(ov?.WheelKnobRingColors, profile.WheelKnobRingColors);

            // Mirror colors into _data (UI uses _data.* for the swatches).
            MozaProfile.UnpackColorsInto(rpmColors, _data.WheelRpmColors);
            MozaProfile.UnpackColorsInto(rpmBlinkColors, _data.WheelRpmBlinkColors);
            MozaProfile.UnpackColorsInto(buttonColors, _data.WheelButtonColors);
            if (buttonDefaults != null)
            {
                int n = Math.Min(buttonDefaults.Length, _data.WheelButtonDefaultDuringTelemetry.Length);
                for (int i = 0; i < n; i++)
                    _data.WheelButtonDefaultDuringTelemetry[i] = buttonDefaults[i];
            }
            MozaProfile.UnpackColorsInto(flagColors, _data.WheelFlagColors);
            if (idleColor != null && idleColor.Length > 0)
            {
                var rgb = MozaProfile.UnpackColor(idleColor[0]);
                _data.WheelIdleColor[0] = rgb[0];
                _data.WheelIdleColor[1] = rgb[1];
                _data.WheelIdleColor[2] = rgb[2];
            }
            MozaProfile.UnpackColorsInto(esRpmColors, _data.WheelESRpmColors);
            MozaProfile.UnpackColorsInto(knobBgColors, _data.WheelKnobBackgroundColors);
            MozaProfile.UnpackColorsInto(knobPrimaryColors, _data.WheelKnobPrimaryColors);
            MozaProfile.UnpackColorsInto(knobRingColors, _data.KnobRingColors);
            if (knobRingBri >= 0) _data.KnobRingBrightness = knobRingBri;

            // ----- Hardware writes (skipped when wheel/connection isn't live) -----
            if (!deviceLive) return;
            if (_newWheelDetected)
            {
                if (telemMode      >= 0) _deviceManager.WriteSetting("wheel-telemetry-mode", telemMode);
                if (idleEffect     >= 0) _deviceManager.WriteSetting("wheel-telemetry-idle-effect", idleEffect);
                if (btnIdleEffect  >= 0) _deviceManager.WriteSetting("wheel-buttons-idle-effect", btnIdleEffect);
                if (knobIdleEffect >= 0) _deviceManager.WriteSetting("wheel-knob-idle-effect", knobIdleEffect);
                if (knobLedMode    >= 0) _deviceManager.WriteSetting("wheel-knob-led-mode", knobLedMode);
                if (btnLedMode     >= 0) _deviceManager.WriteSetting("wheel-buttons-led-mode", btnLedMode);
                if (idleEffect >= 0 && idleSpeed >= 0)
                    _deviceManager.WriteArray("wheel-telemetry-idle-interval",
                        BuildIdleIntervalPayload(idleEffect, idleSpeed));
                if (btnIdleEffect >= 0 && btnIdleSpeed >= 0)
                    _deviceManager.WriteArray("wheel-buttons-idle-interval",
                        BuildIdleIntervalPayload(btnIdleEffect, btnIdleSpeed));
                if (knobIdleEffect >= 0 && knobIdleSpeed >= 0)
                    _deviceManager.WriteArray("wheel-knob-idle-interval",
                        BuildIdleIntervalPayload(knobIdleEffect, knobIdleSpeed));
                if (sleepMode    >= 0) _deviceManager.WriteSetting("wheel-idle-mode", sleepMode);
                if (sleepTimeout >= 0) _deviceManager.WriteSetting("wheel-idle-timeout", sleepTimeout);
                if (sleepMode >= 0 && sleepSpeed >= 0)
                    _deviceManager.WriteArray("wheel-idle-speed",
                        BuildIdleIntervalPayload(sleepMode, sleepSpeed));
                if (sleepColor != null && sleepColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(sleepColor[0]);
                    _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                // Brightness (A.2.1: properly sentinel-gated; old ApplySavedWheelSettings wrote unconditionally)
                if (rpmBri   >= 0) _deviceManager.WriteSetting("wheel-rpm-brightness", rpmBri);
                if (btnBri   >= 0) _deviceManager.WriteSetting("wheel-buttons-brightness", btnBri);
                if (flagsBri >= 0 && _dashDetected)
                    _deviceManager.WriteSetting("dash-flags-brightness", flagsBri);

                // Colors
                WriteColorArray(rpmColors, "wheel-rpm-color", 18);
                WriteColorArray(rpmBlinkColors, "wheel-rpm-blink-color", 10);
                WriteColorArray(buttonColors, "wheel-button-color", 14);
                if (_dashDetected)
                    WriteColorArray(flagColors, "dash-flag-color", 6);
                if (idleColor != null && idleColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(idleColor[0]);
                    _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                WriteKnobColors(knobBgColors, knobPrimaryColors);
                WriteKnobRingColors(knobRingColors, knobRingBri);
            }

            if (_oldWheelDetected)
            {
                if (rpmInd   >= 0) _deviceManager.WriteSetting("wheel-rpm-indicator-mode", rpmInd + 1);
                if (rpmDisp  >= 0) _deviceManager.WriteSetting("wheel-set-rpm-display-mode", rpmDisp);
                if (esRpmBri >= 0) _deviceManager.WriteSetting("wheel-old-rpm-brightness", esRpmBri);
                WriteColorArray(esRpmColors, "wheel-old-rpm-color", 10);
            }
        }

        /// <summary>
        /// Push dashboard-scoped settings (brightness, indicator modes, colors,
        /// display brightness/standby) to the dash. No-op if dash isn't detected.
        /// All scalar writes sentinel-guarded.
        /// </summary>
        internal void ApplyDashToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            // _data mirror (always — UI binds to _data even when device offline)
            if (profile.DashRpmBrightness     >= 0) _data.DashRpmBrightness     = profile.DashRpmBrightness;
            if (profile.DashFlagsBrightness   >= 0) _data.DashFlagsBrightness   = profile.DashFlagsBrightness;
            if (profile.DashDisplayBrightness >= 0) _data.DashDisplayBrightness = profile.DashDisplayBrightness;
            if (profile.DashDisplayStandbyMin >= 0) _data.DashDisplayStandbyMin = profile.DashDisplayStandbyMin;
            MozaProfile.UnpackColorsInto(profile.DashRpmColors, _data.DashRpmColors);
            MozaProfile.UnpackColorsInto(profile.DashRpmBlinkColors, _data.DashRpmBlinkColors);
            MozaProfile.UnpackColorsInto(profile.DashFlagColors, _data.DashFlagColors);

            // Hardware writes only when dash is live
            if (!_dashDetected || !_data.IsConnected) return;
            if (profile.DashRpmBrightness   >= 0) _deviceManager.WriteSetting("dash-rpm-brightness", profile.DashRpmBrightness);
            if (profile.DashFlagsBrightness >= 0) _deviceManager.WriteSetting("dash-flags-brightness", profile.DashFlagsBrightness);
            if (profile.DashDisplayBrightness   >= 0) _telemetrySender?.SendDashDisplayBrightness(profile.DashDisplayBrightness);
            if (profile.DashDisplayStandbyMin >= 0) _telemetrySender?.SendDashDisplayStandbyMinutes(profile.DashDisplayStandbyMin);
            // dash-flags-indicator-mode forced to 1 (flag indicators on) — firmware
            // default 0 silently drops all flag colour writes (legacy behaviour preserved)
            _deviceManager.WriteSetting("dash-flags-indicator-mode", 1);

            WriteColorArray(profile.DashRpmColors, "dash-rpm-color", 10);
            WriteColorArray(profile.DashRpmBlinkColors, "dash-rpm-blink-color", 10);
            WriteColorArray(profile.DashFlagColors, "dash-flag-color", 6);
        }

        /// <summary>
        /// Push wheel-base ambient LED settings to the base. No-op unless the
        /// runtime probe confirmed strip support.
        /// </summary>
        internal void ApplyBaseAmbientToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            // _data mirror always
            if (profile.BaseAmbientBrightness     >= 0) _data.BaseAmbientBrightness     = profile.BaseAmbientBrightness;
            if (profile.BaseAmbientStandbyMode    >= 0) _data.BaseAmbientStandbyMode    = profile.BaseAmbientStandbyMode;
            if (profile.BaseAmbientIndicatorState >= 0) _data.BaseAmbientIndicatorState = profile.BaseAmbientIndicatorState;
            if (profile.BaseAmbientSleepMode      >= 0) _data.BaseAmbientSleepMode      = profile.BaseAmbientSleepMode;
            if (profile.BaseAmbientSleepTimeout   >= 0) _data.BaseAmbientSleepTimeout   = profile.BaseAmbientSleepTimeout;
            if (profile.BaseAmbientStartupColor   >= 0) UnpackPackedColor(profile.BaseAmbientStartupColor, _data.BaseAmbientStartupColor);
            if (profile.BaseAmbientShutdownColor  >= 0) UnpackPackedColor(profile.BaseAmbientShutdownColor, _data.BaseAmbientShutdownColor);

            if (!_baseAmbientLedSupported || !_data.IsConnected) return;
            if (profile.BaseAmbientBrightness     >= 0) _deviceManager.WriteSetting("base-ambient-brightness", profile.BaseAmbientBrightness);
            if (profile.BaseAmbientStandbyMode    >= 0) _deviceManager.WriteSetting("base-ambient-standby-mode", profile.BaseAmbientStandbyMode);
            if (profile.BaseAmbientIndicatorState >= 0) _deviceManager.WriteSetting("base-ambient-indicator-state", profile.BaseAmbientIndicatorState);
            if (profile.BaseAmbientSleepMode      >= 0) _deviceManager.WriteSetting("base-ambient-sleep-mode", profile.BaseAmbientSleepMode);
            if (profile.BaseAmbientSleepTimeout   >= 0) _deviceManager.WriteSetting("base-ambient-sleep-timeout", profile.BaseAmbientSleepTimeout);
            if (profile.BaseAmbientStartupColor   >= 0) WritePackedColor("base-ambient-startup-color", profile.BaseAmbientStartupColor);
            if (profile.BaseAmbientShutdownColor  >= 0) WritePackedColor("base-ambient-shutdown-color", profile.BaseAmbientShutdownColor);
        }

        /// <summary>
        /// Push handbrake settings to the device. No-op unless _handbrakeDetected.
        /// </summary>
        internal void ApplyHandbrakeToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            // _data mirror always (UI binds to _data even when device offline).
            if (profile.HandbrakeMode            >= 0) _data.HandbrakeMode            = profile.HandbrakeMode;
            if (profile.HandbrakeButtonThreshold >= 0) _data.HandbrakeButtonThreshold = profile.HandbrakeButtonThreshold;
            if (profile.HandbrakeDirection       >= 0) _data.HandbrakeDirection       = profile.HandbrakeDirection;
            if (profile.HandbrakeMin             >= 0) _data.HandbrakeMin             = profile.HandbrakeMin;
            if (profile.HandbrakeMax             >= 0) _data.HandbrakeMax             = profile.HandbrakeMax;
            if (profile.HandbrakeCurve != null)
            {
                for (int i = 0; i < Math.Min(5, profile.HandbrakeCurve.Length); i++)
                    _data.HandbrakeCurve[i] = profile.HandbrakeCurve[i];
            }

            if (!_handbrakeDetected) return;
            if (profile.HandbrakeMode            >= 0) _deviceManager.WriteSetting("handbrake-mode", profile.HandbrakeMode);
            if (profile.HandbrakeButtonThreshold >= 0) _deviceManager.WriteSetting("handbrake-button-threshold", profile.HandbrakeButtonThreshold);
            if (profile.HandbrakeDirection       >= 0) _deviceManager.WriteSetting("handbrake-direction", profile.HandbrakeDirection);
            if (profile.HandbrakeMin             >= 0) _deviceManager.WriteSetting("handbrake-min", profile.HandbrakeMin);
            if (profile.HandbrakeMax             >= 0) _deviceManager.WriteSetting("handbrake-max", profile.HandbrakeMax);
            if (profile.HandbrakeCurve != null)
            {
                for (int i = 0; i < Math.Min(5, profile.HandbrakeCurve.Length); i++)
                    _deviceManager.WriteFloat($"handbrake-y{i + 1}", profile.HandbrakeCurve[i]);
            }
        }

        /// <summary>
        /// Push pedal settings to the device. No-op unless _pedalsDetected.
        /// </summary>
        internal void ApplyPedalsToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            if (profile.PedalsThrottleDir      >= 0) _data.PedalsThrottleDir      = profile.PedalsThrottleDir;
            if (profile.PedalsBrakeDir         >= 0) _data.PedalsBrakeDir         = profile.PedalsBrakeDir;
            if (profile.PedalsClutchDir        >= 0) _data.PedalsClutchDir        = profile.PedalsClutchDir;
            if (profile.PedalsBrakeAngleRatio  >= 0) _data.PedalsBrakeAngleRatio  = profile.PedalsBrakeAngleRatio;
            if (profile.PedalsThrottleCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsThrottleCurve.Length); i++)
                    _data.PedalsThrottleCurve[i] = profile.PedalsThrottleCurve[i];
            if (profile.PedalsBrakeCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsBrakeCurve.Length); i++)
                    _data.PedalsBrakeCurve[i] = profile.PedalsBrakeCurve[i];
            if (profile.PedalsClutchCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsClutchCurve.Length); i++)
                    _data.PedalsClutchCurve[i] = profile.PedalsClutchCurve[i];

            if (!_pedalsDetected) return;
            if (profile.PedalsThrottleDir      >= 0) _deviceManager.WriteSetting("pedals-throttle-dir", profile.PedalsThrottleDir);
            if (profile.PedalsBrakeDir         >= 0) _deviceManager.WriteSetting("pedals-brake-dir", profile.PedalsBrakeDir);
            if (profile.PedalsClutchDir        >= 0) _deviceManager.WriteSetting("pedals-clutch-dir", profile.PedalsClutchDir);
            if (profile.PedalsBrakeAngleRatio  >= 0) _deviceManager.WriteFloat("pedals-brake-angle-ratio", profile.PedalsBrakeAngleRatio);
            if (profile.PedalsThrottleCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsThrottleCurve.Length); i++)
                    _deviceManager.WriteFloat($"pedals-throttle-y{i + 1}", profile.PedalsThrottleCurve[i]);
            if (profile.PedalsBrakeCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsBrakeCurve.Length); i++)
                    _deviceManager.WriteFloat($"pedals-brake-y{i + 1}", profile.PedalsBrakeCurve[i]);
            if (profile.PedalsClutchCurve != null)
                for (int i = 0; i < Math.Min(5, profile.PedalsClutchCurve.Length); i++)
                    _deviceManager.WriteFloat($"pedals-clutch-y{i + 1}", profile.PedalsClutchCurve[i]);
        }

        /// <summary>
        /// Push base/FFB settings (motor limits, FFB curve breakpoints) to the
        /// wheelbase. No-op unless _data.IsBaseConnected. Uses the existing
        /// ApplyBaseSettingIfSet / ApplyEq helpers internally for parity with
        /// ApplyProfile.
        /// </summary>
        internal void ApplyBaseToHardware(MozaProfile? profile)
        {
            if (profile == null) return;

            // ApplyBaseSettingIfSet always mirrors _data and only writes to hardware
            // when _data.IsBaseConnected — safe to call regardless of connection.
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
            ApplyBaseSettingIfSet(profile.GameDamper, v => _data.GameDamper = v, "main-set-damper-gain");
            ApplyBaseSettingIfSet(profile.GameFriction, v => _data.GameFriction = v, "main-set-friction-gain");
            ApplyBaseSettingIfSet(profile.GameInertia, v => _data.GameInertia = v, "main-set-inertia-gain");
            ApplyBaseSettingIfSet(profile.GameSpring, v => _data.GameSpring = v, "main-set-spring-gain");
            ApplyBaseSettingIfSet(profile.WorkMode, v => _data.WorkMode = v, "main-set-work-mode");

            // FFB Equalizer (sentinel = -1000) — mirror _data always, write only when base live.
            void ApplyEq(int val, Action<int> setData, string cmd)
            {
                if (val <= -1000) return;
                setData(val);
                if (_data.IsBaseConnected) _deviceManager.WriteSetting(cmd, val);
            }
            ApplyEq(profile.Equalizer1, v => _data.Equalizer1 = v, "base-equalizer1");
            ApplyEq(profile.Equalizer2, v => _data.Equalizer2 = v, "base-equalizer2");
            ApplyEq(profile.Equalizer3, v => _data.Equalizer3 = v, "base-equalizer3");
            ApplyEq(profile.Equalizer4, v => _data.Equalizer4 = v, "base-equalizer4");
            ApplyEq(profile.Equalizer5, v => _data.Equalizer5 = v, "base-equalizer5");
            ApplyEq(profile.Equalizer6, v => _data.Equalizer6 = v, "base-equalizer6");

            // FFB Curve Y values: mirror _data always, write only when base live.
            if (profile.FfbCurveY1 >= 0) _data.FfbCurveY1 = profile.FfbCurveY1;
            if (profile.FfbCurveY2 >= 0) _data.FfbCurveY2 = profile.FfbCurveY2;
            if (profile.FfbCurveY3 >= 0) _data.FfbCurveY3 = profile.FfbCurveY3;
            if (profile.FfbCurveY4 >= 0) _data.FfbCurveY4 = profile.FfbCurveY4;
            if (profile.FfbCurveY5 >= 0) _data.FfbCurveY5 = profile.FfbCurveY5;
            if (!_data.IsBaseConnected) return;
            // X breakpoints always written when base is live (device doesn't persist them).
            _deviceManager.WriteSetting("base-ffb-curve-x1", 20);
            _deviceManager.WriteSetting("base-ffb-curve-x2", 40);
            _deviceManager.WriteSetting("base-ffb-curve-x3", 60);
            _deviceManager.WriteSetting("base-ffb-curve-x4", 80);
            _deviceManager.WriteSetting("base-ffb-curve-y1", _data.FfbCurveY1);
            _deviceManager.WriteSetting("base-ffb-curve-y2", _data.FfbCurveY2);
            _deviceManager.WriteSetting("base-ffb-curve-y3", _data.FfbCurveY3);
            _deviceManager.WriteSetting("base-ffb-curve-y4", _data.FfbCurveY4);
            _deviceManager.WriteSetting("base-ffb-curve-y5", _data.FfbCurveY5);
        }

        /// <summary>
        /// Push AB9 active-shifter settings to the AB9 manager. No-op unless
        /// AB9 is detected and the profile carries an Ab9 block.
        /// </summary>
        internal void ApplyAb9ToHardware(MozaProfile? profile)
        {
            if (profile?.Ab9 == null) return;
            if (!_ab9Detected || _ab9Manager == null || !_ab9Manager.IsConnected) return;

            var ab9 = profile.Ab9;
            _ab9Manager.SendMode(ab9.Mode);
            _ab9Manager.SendSlider(Devices.Ab9Slider.MechanicalResistance, ab9.MechanicalResistance);
            _ab9Manager.SendSlider(Devices.Ab9Slider.Spring,               ab9.Spring);
            _ab9Manager.SendSlider(Devices.Ab9Slider.NaturalDamping,       ab9.NaturalDamping);
            _ab9Manager.SendSlider(Devices.Ab9Slider.NaturalFriction,      ab9.NaturalFriction);
            _ab9Manager.SendSlider(Devices.Ab9Slider.MaxTorqueLimit,       ab9.MaxTorqueLimit);
            _ab9Manager.SendGearShiftVibrationIntensity(ab9.GearShiftVibrationIntensity);
        }

        // ===== WriteIf* helpers (R4 — UI handler hardening) =====
        // Used by UI event handlers to write to hardware only when the matching
        // detection flag is set. When the flag is false, the call is a no-op so
        // a slider drag while the device is disconnected doesn't queue against
        // nothing. UI handlers still update the profile/overlay + persist; the
        // hardware write is the conditional part.

        internal void WriteIfWheelDetected(string command, int value)
        {
            if (value < 0) return;
            if (_newWheelDetected || _oldWheelDetected)
                _deviceManager.WriteSetting(command, value);
        }
        internal void WriteIfDashDetected(string command, int value)
        {
            if (value < 0) return;
            if (_dashDetected) _deviceManager.WriteSetting(command, value);
        }
        internal void WriteIfBaseConnected(string command, int value)
        {
            if (value < 0) return;
            if (_data.IsBaseConnected) _deviceManager.WriteSetting(command, value);
        }
        internal void WriteFloatIfBaseConnected(string command, int value)
        {
            if (value < 0) return;
            if (_data.IsBaseConnected) _deviceManager.WriteFloat(command, value);
        }
        internal void WriteIfHandbrakeDetected(string command, int value)
        {
            if (value < 0) return;
            if (_handbrakeDetected) _deviceManager.WriteSetting(command, value);
        }
        internal void WriteFloatIfHandbrakeDetected(string command, int value)
        {
            if (value < 0) return;
            if (_handbrakeDetected) _deviceManager.WriteFloat(command, value);
        }
        internal void WriteIfPedalsDetected(string command, int value)
        {
            if (value < 0) return;
            if (_pedalsDetected) _deviceManager.WriteSetting(command, value);
        }
        internal void WriteFloatIfPedalsDetected(string command, int value)
        {
            if (value < 0) return;
            if (_pedalsDetected) _deviceManager.WriteFloat(command, value);
        }
        internal void WriteIfBaseAmbientSupported(string command, int value)
        {
            if (value < 0) return;
            if (_baseAmbientLedSupported) _deviceManager.WriteSetting(command, value);
        }
        internal void WriteColorIfWheelDetected(string command, byte r, byte g, byte b)
        {
            if (_newWheelDetected || _oldWheelDetected)
                _deviceManager.WriteColor(command, r, g, b);
        }
        internal void WriteColorIfDashDetected(string command, byte r, byte g, byte b)
        {
            if (_dashDetected) _deviceManager.WriteColor(command, r, g, b);
        }
        internal void WriteColorIfBaseAmbientSupported(string command, byte r, byte g, byte b)
        {
            if (_baseAmbientLedSupported) _deviceManager.WriteColor(command, r, g, b);
        }
        internal void WriteArrayIfWheelDetected(string command, byte[] payload)
        {
            if (_newWheelDetected || _oldWheelDetected)
                _deviceManager.WriteArray(command, payload);
        }

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

        // ===== Active telemetry view (sourced from the current wheel's overlay) =====
        // Telemetry settings used to live on MozaPluginSettings flat fields. After
        // the 2026-05-14 refactor they live on WheelOverride (per-wheel-page).
        // These accessors resolve the current wheel's overlay and surface its
        // telemetry fields. When no wheel has identified yet (or no profile is
        // loaded), reads return defaults that translate to "telemetry off".

        /// <summary>
        /// True iff telemetry is enabled for the currently-identified wheel page.
        /// Per-wheel-page (shared across profiles) on
        /// <see cref="MozaPluginSettings.WheelTelemetryEnabledByPageGuid"/>.
        /// Returns false when no wheel has identified yet or the dict has no entry.
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

        /// <summary>
        /// Mzdash folder for the currently-identified wheel page. Sourced from
        /// <see cref="MozaPluginSettings.WheelMzdashFolderByPageGuid"/> — shared
        /// across all profiles for the same wheel (folder is a wheel-library
        /// setting, not per-game). Returns "" when no wheel has identified yet
        /// or the dict has no entry for this page.
        /// </summary>
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
        /// Sleep-light bundle for the currently-identified wheel page. Sourced
        /// from <see cref="MozaPluginSettings.WheelSleepByPageGuid"/> — shared
        /// across all profiles for the same wheel (sleep is a firmware
        /// preference, not per-game). Returns null when no wheel has
        /// identified yet or the dict has no entry; callers should treat that
        /// as "leave the wheel's currently-stored value alone".
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

        /// <summary>
        /// Get or create the sleep-light bundle for the currently-identified
        /// wheel page. Returns null only when no wheel has been identified
        /// (no page GUID yet). UI handlers use this to commit per-field
        /// edits without re-reading the dict each time.
        /// </summary>
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
        /// Firmware era for the currently-identified wheel page. Per-wheel-page
        /// (shared across profiles) on
        /// <see cref="MozaPluginSettings.WheelTelemetryEraByPageGuid"/>.
        /// Returns Auto when no wheel has identified yet or the dict has no
        /// entry — the auto-resolver then picks from the wheel's response.
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
            // defaults — particularly DashDisplayBrightness, which would
            // otherwise sit at the -1 sentinel and skip the brightness push
            // in ApplyDashToHardware, leaving the wheel display dark on
            // first launch.
            if (store.Profiles.Count == 0)
            {
                var defaultProfile = new MozaProfile { Name = "Default" };
                SeedProfileBaselineFromFlatFields(defaultProfile);
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
        /// Apply a profile by routing through the consolidated Apply*ToHardware
        /// methods. Each method mirrors profile/overlay values into _data (always)
        /// and writes to hardware when the matching device is detected.
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

            // Single source of truth: route each device through its Apply*ToHardware
            // method. Each method mirrors profile/overlay values into _data (always,
            // so UI stays in sync even when the device isn't connected) and writes
            // to hardware only when the matching detection flag is set.
            ApplyBaseToHardware(profile);
            ApplyWheelToHardware(profile);
            ApplyDashToHardware(profile);
            ApplyBaseAmbientToHardware(profile);
            ApplyHandbrakeToHardware(profile);
            ApplyPedalsToHardware(profile);
            ApplyAb9ToHardware(profile);

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

            // Sync the telemetry pipeline to the new overlay's enable state.
            // Without this, the sender keeps running with the old game's config
            // (when new overlay has it off) or stays idle when new overlay wants
            // it on. Also re-stages the sender's profile so its tier-def matches
            // the new game's overlay dashboard selection.
            try
            {
                bool wantOn = ActiveTelemetryEnabled;
                var sender = _telemetrySender;
                if (wantOn)
                {
                    ApplyTelemetrySettings();
                    StartTelemetryIfReady();
                }
                else if (sender != null && sender.Enabled)
                {
                    MozaLog.Debug("[Moza] New profile has telemetry disabled — stopping sender");
                    sender.Stop();
                    Interlocked.Exchange(ref _telemetryStartRequested, 0);
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] Telemetry sync after profile apply failed: {ex.Message}");
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
        /// Updates _settings, _data, the active profile's wheel overlay, and
        /// writes to hardware if connected.
        /// </summary>
        internal void ApplyWheelExtensionSettings(MozaWheelExtensionSettings extSettings, string? pageModelPrefix = null)
        {
            MozaLog.Debug($"[Moza] Applying wheel device extension settings (prefix={pageModelPrefix ?? "(null)"})");

            // Update _settings, _data, and (when prefix is provided) the
            // profile's wheel overlay for this page GUID. ApplyTo also routes
            // into the legacy per-model slot for the R5 transition.
            var profile = _settings?.ProfileStore?.CurrentProfile;
            extSettings.ApplyTo(_settings!, _data, profile, pageModelPrefix);

            // Gate hardware-side mutations on model match — extensions for other
            // wheel models must not poke the active wheel's hardware.
            string extModel = extSettings.WheelModelName ?? "";
            string activeModel = _data.WheelModelName ?? "";
            bool hasExtModel = !string.IsNullOrEmpty(extModel);
            bool modelMatches = hasExtModel &&
                string.Equals(extModel, activeModel, StringComparison.OrdinalIgnoreCase);
            bool writeHardware = !hasExtModel || modelMatches;

            // Hardware writes go through the consolidated ApplyWheelToHardware path
            // — single source (profile + overlay), sentinel-guarded, no duplicates.
            // ApplyTo already wrote the extension's values into the overlay above,
            // so this picks them up.
            if (writeHardware)
                ApplyWheelToHardware(profile);

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
                if (_settings!.TelemetryEnabled)
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

            // Update _settings, _data, and the active profile in-memory
            extSettings.ApplyTo(_settings!, _data, _settings?.ProfileStore?.CurrentProfile);

            // Hardware writes go through ApplyDashToHardware (single source +
            // sentinel guards). ApplyTo above already mirrored the extension's
            // values into the profile, so this picks them up.
            ApplyDashToHardware(_settings?.ProfileStore?.CurrentProfile);

            PersistSettings();
        }

        /// <summary>
        /// Apply base ambient LED settings from the SimHub device extension
        /// profile system. Mirror of <see cref="ApplyDashExtensionSettings"/>.
        /// </summary>
        internal void ApplyBaseExtensionSettings(MozaBaseExtensionSettings extSettings)
        {
            MozaLog.Debug("[Moza] Applying base ambient device extension settings");

            extSettings.ApplyTo(_settings!, _data, _settings?.ProfileStore?.CurrentProfile);
            ApplyBaseAmbientToHardware(_settings?.ProfileStore?.CurrentProfile);

            PersistSettings();
        }

    }
}
