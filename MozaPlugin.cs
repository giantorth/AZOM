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
using MozaPlugin.Telemetry.Display;
using MozaPlugin.UI;
using MozaPlugin.Integration;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Devices.Haptics;

namespace MozaPlugin
{
    [PluginDescription("Configure MOZA Racing hardware and send SimHub game telemetry to wheel/dashboard RPM LEDs")]
    [PluginAuthor("giantorth")]
    [PluginName("AZOM")]
    public partial class MozaPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        internal static MozaPlugin? Instance { get; private set; }

        // Persistent wire: survives plugin reload on game switch so the
        // wheel never sees the ~10–14s sess=0x09 settle. Disposed only on
        // process exit or on wheel unplug (Init checks "still connected?").
        private static MozaSerialConnection? s_persistentConnection;
        private static TelemetrySender? s_persistentTelemetrySender;
        // One-shot guard so the stale-instance DataUpdate fallback warns once.
        private bool _warnedStaleDataFeed;

        // Auto-standby (idle-timeout Work Mode standby) + the HID-activity
        // baseline that feeds it — see Devices/StandbyCoordinator.cs.
        private StandbyCoordinator? _standby;
        // Per-channel + global-default SimHub property mapping —
        // see Telemetry/ChannelMappingCoordinator.cs.
        private ChannelMappingCoordinator _channelMapping = null!;
        internal ChannelMappingCoordinator ChannelMapping => _channelMapping;

        // True while a game is actively feeding telemetry. The LED keepalive reads
        // this to never let the wheel sleep mid-game; false before Init completes.
        public bool IsGameActive => _standby?.IsGameActive ?? false;

        /// <summary>Auto-standby coordinator. Null until Init constructs it.</summary>
        internal StandbyCoordinator? Standby => _standby;

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

        // Wheel-display application log, pulled over the session layer (FF
        // kind=14 request / kind=15 receipt) by TelemetrySender's slow path.
        // Distinct source from the group-0x0E ring above: that is base/wheel
        // MCU chatter, this is the display's own MOZADash logger.
        private readonly global::MozaPlugin.Diagnostics.DeviceLogStore _deviceLog
            = new global::MozaPlugin.Diagnostics.DeviceLogStore();
        internal global::MozaPlugin.Diagnostics.DeviceLogStore DeviceLogForDiagnostics
            => _deviceLog;

        // Third-party SDK (CoAP-over-UDP) emulation server + name-impersonation
        // stub process. Both are gated on Settings.SdkEmulationEnabled and
        // require a plugin restart to toggle (no runtime enable/disable —
        // see Init()). Null when disabled, so the UI tab uses null-conditional
        // access.
        // Owns the servers, the stub child process and their lifecycle gate —
        // see Sdk/SdkLifecycleCoordinator.cs. Null until Init constructs it.
        // Gear-change edge detection for wheelbase + AB9 one-shot shift effects —
        // see Devices/GearshiftDetector.cs.
        private GearshiftDetector? _gearshift;
        private Sdk.SdkLifecycleCoordinator? _sdk;
        /// <summary>SDK-emulation lifecycle (CoAP server, stub child, UDP control
        /// server). Null until Init constructs it.</summary>
        internal Sdk.SdkLifecycleCoordinator? SdkLifecycle => _sdk;
        internal global::MozaPlugin.Protocol.PendingResponseTracker PendingResponses { get; }
            = new global::MozaPlugin.Protocol.PendingResponseTracker();
        // Internal: ProfileCoordinator.ClearSettings replaces this field with a
        // fresh instance. Everything else reads it live via the Settings property.
        internal MozaPluginSettings _settings = null!;
        private Timer _pollTimer = null!;
        private Timer _retryTimer = null!;
        private Timer _reconnectTimer = null!;
        // Base-tab temperature-graph history. Sampled every 500 ms by a
        // plugin-lifetime timer (independent of the settings panel) so the graph
        // shows the full 5-minute window the moment the panel opens rather than
        // starting empty. 600 × 0.5 s = 5 min; the temps themselves refresh on
        // the 5 s _pollTimer via PollBaseAux, so 500 ms just reproduces the
        // graph's existing staircase without adding real resolution.
        private const int TempHistorySamples = 600;
        private const int TempHistoryIntervalMs = 500;
        private readonly UI.TemperatureHistory _tempHistory =
            new UI.TemperatureHistory(TempHistorySamples);
        private Timer _tempHistoryTimer = null!;
        internal UI.TemperatureHistory TemperatureHistory => _tempHistory;

        // Live-torque history for the Base-tab graph, and the sole feed for the
        // AZOM.CurrentTorque* / MaxTorque properties. Sampled on a background
        // timer, NOT on the WPF dispatcher: the per-sample work (a wire read plus
        // a geometry rebuild) is far too much to put on the UI thread, which is
        // what the first cut of this got wrong. 600 × 200 ms = 2 min window.
        //
        // One uniform rate whether or not the panel is open: a UI-gated rate left
        // the properties reading at the 5 s status sweep for every setup that
        // never opens the Base tab, and a dual rate would make the ring's time
        // axis lie. Costs ~80 B/s, ~0.7% of the 11520 B/s ceiling.
        private const int TorqueHistorySamples = 600;
        private const int TorqueHistoryIntervalMs = 200;
        private readonly UI.TorqueHistory _torqueHistory =
            new UI.TorqueHistory(TorqueHistorySamples);
        private Timer _torqueHistoryTimer = null!;
        internal UI.TorqueHistory TorqueHistory => _torqueHistory;

        // Game-start edge latch for the per-session torque peak (AZOM.MaxTorque).
        private bool _lastGameRunningForTorquePeak;
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
        // ComportScanner.Instance.CanBeScanned is likewise a process-wide event
        // on a static singleton — detach on End()/CleanupPartialInit or the
        // game-switch reload leaks this instance and stacks handlers. See
        // OnArduinoPortCanBeScanned.
        private int _arduinoScanHooked;
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
        // Per-zone LED brightness (SimHub's "Brightness limiter and balance" panel:
        // Telemetry Leds / Buttons / Encoders). SimHub hands Display() an EFFECTIVE
        // factor per zone (globalMaster/100 x zoneBalance/100), so round(factor*100) is
        // that zone's firmware value for cmd 1B [G] FF (G = 0 rpm, 1 buttons, 3 knob).
        // The wheel LED driver publishes settled values here off the LED thread;
        // DataUpdate applies them on the data thread via ApplyWheelLedZoneBrightness.
        // -1 = the user has not moved that zone's slider, so the wheel's device-stored
        // brightness is left untouched. Without this the per-zone sliders could only
        // scale LIVE colour frames, so a zone rendering its static palette (Button /
        // Knob LED mode = Static) had no reachable dimmer at all.
        internal volatile int WheelLedBrightnessRpm = -1;
        internal volatile int WheelLedBrightnessButtons = -1;
        internal volatile int WheelLedBrightnessKnob = -1;
        // Mirror of what the applier believes is actually in the zone's firmware
        // register (-1 = never written / zone not writable on this wheel). The LED
        // driver divides its per-frame factor by this so the firmware's dimming is not
        // applied a second time in software. Written on the data thread by
        // ApplyWheelLedZoneBrightness, read per-frame on the LED thread.
        internal volatile int WheelLedAppliedBrightnessRpm = -1;
        internal volatile int WheelLedAppliedBrightnessButtons = -1;
        internal volatile int WheelLedAppliedBrightnessKnob = -1;
        private int _zoneLedBrightnessApplied0 = -2;  // DataUpdate-thread-local change gates
        private int _zoneLedBrightnessApplied1 = -2;
        private int _zoneLedBrightnessApplied3 = -2;
        // Old-protocol (ES/ESX) master brightness: the LED thread publishes the LIVE
        // slider value here (0..100, -1 until first moved); the steady 250 ms poll
        // timer settle-detects + writes wheel-old-rpm-brightness. Old wheels can't use
        // the DataUpdate path above — it goes quiet at idle (issue #113). Direct
        // device-manager write on the timer, NOT via HardwareApplier's cfg cache
        // (that dictionary is data-thread-only / unlocked).
        internal volatile int WheelLedMasterBrightnessRaw = -1;
        private int _esMasterBriApplied = -2;         // poll-timer-local change gate
        private int _esMasterBriCandidate = int.MinValue; // value awaiting settle
        private int _esMasterBriStableTicks;          // ticks the candidate has held
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
        // Startup GitHub Releases check (24h throttle + per-process dedupe) —
        // see UI/UpdateCheck/UpdateCheckCoordinator.cs.
        private UpdateCheckCoordinator _updateCheck = null!;
        // FSR1 decode probes + live byte-strip viz — see Diagnostics/Fsr1ProbeTool.cs.
        private Diagnostics.Fsr1ProbeTool _fsr1Probe = null!;
        internal Diagnostics.Fsr1ProbeTool Fsr1Probe => _fsr1Probe;

        private TelemetrySender? _telemetrySender;

        // Standalone FSR V1 group-0x42 display driver (dev 0x17), independent of the
        // tier-def _telemetrySender so an FSR1 screen + a CM2 dash run concurrently.
        internal Telemetry.Display.Fsr1DisplayDriver? _fsr1Driver;

        // Dedicated tier-def sender that drives a CM2 dash whenever a CM2 is present —
        // regardless of the wheel (display wheel, screenless wheel, or no wheel at all).
        // Targets dev 0x14 on the shared wheelbase connection (lane base 18) or dev 0x12
        // on the CM2's own USB connection (lane base 0). Null until a CM2 is detected.
        internal TelemetrySender? _cm2Sender;

        // Standalone CM1 base-bridged dash driver (group-0x35 → dev 0x14). Used instead
        // of the tier-def _cm2Sender when a bridged dash is a CM1 (no tier-def catalog).
        internal Telemetry.Display.Cm1DisplayDriver? _cm1Driver;
        // True if Init reused the persistent connection/sender from a
        // prior plugin instance. End() respects this flag and skips
        // disposing them so the next Init can pick up where we left off.
        private bool _usingPersistentWire;
        internal DashboardProfileStore DashProfileStore { get; } = new DashboardProfileStore();
        internal DashboardCache DashCache { get; private set; } = null!;

        /// <summary>
        /// Reload the folder half of the dashboard library from every source
        /// that can hold a user's dashboards. Single entry point so the Files
        /// tab, the Refresh button and post-detection load all see the same set.
        ///
        /// <para>MOZA Dashboard Studio authors into its own projectRoot while
        /// PitHouse syncs a per-wheel copy elsewhere, and neither tree is a
        /// superset of the other — a dashboard just created in Studio would
        /// otherwise be invisible here. The user's configured folder is scanned
        /// LAST so it wins on a duplicate name.</para>
        /// </summary>
        /// <param name="configuredFolder">
        /// Override for the configured folder; defaults to
        /// <see cref="ActiveTelemetryMzdashFolder"/> when null.
        /// </param>
        internal void ReloadDashboardLibrary(string? configuredFolder = null)
        {
            var folder = configuredFolder ?? ActiveTelemetryMzdashFolder;
            DashCache?.LoadFromFolders(new[]
            {
                UI.DashboardStudioLauncher.ResolveProjectRoot(),
                folder,
            });
        }

        /// <summary>Every folder <see cref="ReloadDashboardLibrary"/> scans, for
        /// UI that reports where the library came from.</summary>
        internal IReadOnlyList<string> DashboardLibraryFolders(string? configuredFolder = null)
        {
            var result = new List<string>(2);
            var studio = UI.DashboardStudioLauncher.ResolveProjectRoot();
            var folder = configuredFolder ?? ActiveTelemetryMzdashFolder;
            if (!string.IsNullOrEmpty(folder)) result.Add(folder!);
            if (!string.IsNullOrEmpty(studio)
                && !result.Any(p => string.Equals(p, studio, StringComparison.OrdinalIgnoreCase)))
                result.Add(studio!);
            return result;
        }

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
        private Integration.ControlMapperBridge? _controlMapperBridge;
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
            // base-live-torque is deliberately NOT here: SampleTorqueHistory reads
            // it unconditionally at 5 Hz on the same base-owning pipe, so a copy
            // on this sweep would just double-read the register every 5 s.
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

        internal bool IsBaseAmbientLedSupported => DetectionState.BaseAmbientLedSupported;
        internal bool IsHandbrakeDetected => DetectionState.HandbrakeDetected;
        internal bool IsPedalsDetected => DetectionState.PedalsDetected;
        // Independent per-model gates for the dedicated HGP / SGP tabs — both can be
        // true at once (a user with both shifters, each on its own USB port).
        internal bool IsHgpShifterDetected => DetectionState.HgpDetected;
        internal bool IsSgpShifterDetected => DetectionState.SgpDetected;
        internal bool IsHubDetected => DetectionState.HubDetected;
        internal bool IsAb9Detected => DetectionState.Ab9Detected;
        internal MozaAb9DeviceManager Ab9Manager => _ab9Manager;
        internal MozaMBoosterRegistry? MBoosterRegistry => _mboosterRegistry;
        internal MozaStandalonePeripheralRegistry? PeripheralRegistry => _peripheralRegistry;
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
        internal Sdk.MozaSdkCoapServer? SdkServer => _sdk?.Server;

        /// <summary>
        /// PitHouse-compatible plain-UDP control server (port 40288 by default).
        /// Started/stopped alongside <see cref="SdkServer"/> — both are part of
        /// the third-party SDK emulation surface.
        /// </summary>
        internal Sdk.PitHouseUdp.MozaControlUdpServer? ControlUdpServer => _sdk?.ControlUdpServer;

        /// <summary>
        /// Live CoAP-stub child-process manager when SDK emulation is
        /// enabled; null otherwise. Same UI consumer as <see cref="SdkServer"/>.
        /// </summary>
        internal Sdk.CoapStubManager? SdkStubManager => _sdk?.StubManager;

        internal MozaHidReader HidReader => _hidReader;

        /// <summary>Live truck-sim stalk controller (for the settings UI's
        /// "Re-sync wipers" action). Null until Init runs.</summary>
        internal StalksTruckSimController StalksController => _stalksController;

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
        internal bool IsFsr1DisplayWheel => _data?.IsFsr1DisplayWheel ?? false;

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
                _hardwareApplier.ClearLedsOnHardware();
                _telemetrySender?.Stop();
                _connection?.Disconnect();
                // Deliberate disable — clear any classified failure so the UI
                // doesn't keep showing a "port in use" banner after the user
                // has intentionally turned the connection off.
                _connection?.ResetFailureState();
                _data.IsBaseConnected = false;
                _data.IsHubConnected = false;
                _data.ClearWheelIdentity();
                DetectionState.ResetBase();
                _data.ClearBaseIdentity();
                _data.BaseSettingsRead = false;
                DetectionState.DashDetected = false;
                _baseModelChunk1 = null;
                _baseModelChunk2 = null;
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
                DetectionState.ResetWheelPollMisses();
                DetectionState.LastKnownWheelModel = "";
                DetectionState.LastKnownWheelDeviceId = 0;
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
            if (on) _fsr1Probe?.DisarmAll(); // exclusive with both probes
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
