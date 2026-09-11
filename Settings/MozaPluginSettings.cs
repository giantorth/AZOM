using System;
using System.Collections.Generic;
using MozaPlugin.Devices.StalksTruckSim;
using MozaPlugin.Telemetry;
using MozaPlugin.UI.UpdateCheck;

namespace MozaPlugin.Settings
{
    /// <summary>Which subsystem drives the wheelbase LFE oscillators.</summary>
    public enum WheelbaseLfeSource
    {
        /// <summary>The plugin's own LFE tab (engine / ABS / gearshift effects).</summary>
        PluginTab = 0,
        /// <summary>SimHub's ShakeIt motors editor, via the base device's Haptics section.</summary>
        ShakeIt = 1,
    }

    /// <summary>Which graph fills the right-hand slot of the Base tab's live
    /// telemetry pair. Torque additionally gates its own ~15 Hz wire poll, so
    /// this is not purely cosmetic.</summary>
    public enum BaseGraphMode
    {
        /// <summary>Dual-line serial traffic sparkline (in/out). No extra wire cost.</summary>
        Bandwidth = 0,
        /// <summary>Live wheelbase torque magnitude, polled from base status reg 0x07.</summary>
        Torque = 1,
    }

    /// <summary>Which source the Files tab's dashboard-upload picker reads from.</summary>
    public enum DashboardUploadSource
    {
        /// <summary>Browse for a .mzdash on disk.</summary>
        LocalFile = 0,
        /// <summary>Pick from the cached / folder dashboard library.</summary>
        Library = 1,
    }

    /// <summary>
    /// Persisted plugin settings. Saved/loaded via SimHub's ReadCommonSettings/SaveCommonSettings.
    /// Stores values that the wheel doesn't retain between sessions.
    /// </summary>
    public class MozaPluginSettings
    {
        /// <summary>
        /// Defaults for an install with no settings file yet — the ONE place the code
        /// can tell "brand new user" from "upgraded from a build that predates the
        /// field", since a missing field just deserializes to its initializer.
        ///
        /// Used by both the ReadCommonSettings create-if-not-found factory and
        /// ProfileCoordinator.ClearSettings, which resets to a fresh install and must
        /// not silently hand the user a worse configuration than a real fresh install.
        /// Anything set here MUST keep its conservative value in the field initializer
        /// so an existing settings file is never retroactively changed.
        /// </summary>
        public static MozaPluginSettings CreateForNewInstall() => new MozaPluginSettings
        {
            TelemetryEnabledDefaultForNewWheels = true,
            WheelbaseLfeSource = WheelbaseLfeSource.ShakeIt,
            KnobColorAllBlackRepaired = true,
        };

        // Wheel LED mode settings (-1 = not yet saved).
        // Backing-field-volatile auto-properties: torn reads are possible when
        // LoadSlotIntoActive (serial-reader thread) writes while the UI/telemetry
        // threads read. `volatile int` reads/writes are atomic and ordered on
        // .NET, which is sufficient — we hold _slotsLock for the dictionary
        // mutation but not for these flat fields.
        private volatile int _wheelTelemetryMode = -1;
        public int WheelTelemetryMode { get => _wheelTelemetryMode; set => _wheelTelemetryMode = value; }
        private volatile int _wheelIdleEffect = -1;
        public int WheelIdleEffect { get => _wheelIdleEffect; set => _wheelIdleEffect = value; }
        private volatile int _wheelButtonsIdleEffect = -1;
        public int WheelButtonsIdleEffect { get => _wheelButtonsIdleEffect; set => _wheelButtonsIdleEffect = value; }
        private volatile int _wheelKnobIdleEffect = -1;
        public int WheelKnobIdleEffect { get => _wheelKnobIdleEffect; set => _wheelKnobIdleEffect = value; }
        private volatile int _wheelKnobLedMode = -1;
        public int WheelKnobLedMode { get => _wheelKnobLedMode; set => _wheelKnobLedMode = value; }
        private volatile int _wheelButtonsLedMode = -1;
        public int WheelButtonsLedMode { get => _wheelButtonsLedMode; set => _wheelButtonsLedMode = value; }
        // Per-group idle-effect speed (cmd 0x1E [group] [effect_id] [BE u16 ms]).
        // -1 = never set; UI defaults to 1000ms when first showing the slider.
        private volatile int _wheelTelemetryIdleSpeedMs = -1;
        public int WheelTelemetryIdleSpeedMs { get => _wheelTelemetryIdleSpeedMs; set => _wheelTelemetryIdleSpeedMs = value; }
        private volatile int _wheelButtonsIdleSpeedMs = -1;
        public int WheelButtonsIdleSpeedMs { get => _wheelButtonsIdleSpeedMs; set => _wheelButtonsIdleSpeedMs = value; }
        private volatile int _wheelKnobIdleSpeedMs = -1;
        public int WheelKnobIdleSpeedMs { get => _wheelKnobIdleSpeedMs; set => _wheelKnobIdleSpeedMs = value; }
        // Wheel sleep-light settings (cmd 0x20/0x21/0x22/0x24).
        private volatile int _wheelSleepMode = -1;        // cmd 0x20 — 1-byte mode enum
        public int WheelSleepMode { get => _wheelSleepMode; set => _wheelSleepMode = value; }
        private volatile int _wheelSleepTimeoutMin = -1;  // cmd 0x21 — BE u16 minutes
        public int WheelSleepTimeoutMin { get => _wheelSleepTimeoutMin; set => _wheelSleepTimeoutMin = value; }
        private volatile int _wheelSleepSpeedMs = -1;     // cmd 0x22 [mode] [BE u16 ms]
        public int WheelSleepSpeedMs { get => _wheelSleepSpeedMs; set => _wheelSleepSpeedMs = value; }
        public int[]? WheelSleepColor { get; set; }       // packed R<<16|G<<8|B (single)

        // Wheel input settings cached locally — newer KS-family firmware
        // silently drops read-back for these (cmd 9 / cmd 10), so we have to
        // remember them ourselves across restarts.
        private volatile int _wheelPaddlesMode = -1; // display 0/1/2 (Buttons/Combined/Split)
        public int WheelPaddlesMode { get => _wheelPaddlesMode; set => _wheelPaddlesMode = value; }
        private volatile int _wheelClutchPoint = -1; // 0..100
        public int WheelClutchPoint { get => _wheelClutchPoint; set => _wheelClutchPoint = value; }
        private volatile int _wheelKnobMode = -1;    // legacy 0=Buttons, 1=Knob
        public int WheelKnobMode { get => _wheelKnobMode; set => _wheelKnobMode = value; }
        private volatile int _wheelStickMode = -1;   // new FW: 0=off,1=left,2=right,3=both; old FW: 0=off,1=left
        public int WheelStickMode { get => _wheelStickMode; set => _wheelStickMode = value; }

        // ES/Old wheel mode settings (-1 = not yet saved)
        private volatile int _wheelRpmIndicatorMode = -1;
        public int WheelRpmIndicatorMode { get => _wheelRpmIndicatorMode; set => _wheelRpmIndicatorMode = value; }
        private volatile int _wheelRpmDisplayMode = -1;
        public int WheelRpmDisplayMode { get => _wheelRpmDisplayMode; set => _wheelRpmDisplayMode = value; }

        // Brightness settings (-1 = not yet saved; defaults: new wheel/dash=100, old wheel=15)
        private volatile int _wheelRpmBrightness = 100;
        public int WheelRpmBrightness { get => _wheelRpmBrightness; set => _wheelRpmBrightness = value; }
        private volatile int _wheelButtonsBrightness = 100;
        public int WheelButtonsBrightness { get => _wheelButtonsBrightness; set => _wheelButtonsBrightness = value; }
        private volatile int _wheelFlagsBrightness = 100;
        public int WheelFlagsBrightness { get => _wheelFlagsBrightness; set => _wheelFlagsBrightness = value; }
        private volatile int _wheelESRpmBrightness = 15;
        public int WheelESRpmBrightness { get => _wheelESRpmBrightness; set => _wheelESRpmBrightness = value; }
        public int DashRpmBrightness { get; set; } = 100;
        public int DashFlagsBrightness { get; set; } = 100;
        // Wheel-integrated dashboard display brightness (0..100) and standby
        // timeout (minutes). Sent via session-0x01 ff-record property push;
        // see docs/protocol/findings/2026-04-29-session-01-property-push.md.
        public int DashDisplayBrightness { get; set; } = 100;
        public int DashDisplayStandbyMin { get; set; } = 5;
        // VGS wheel display-rotation mode baseline (0=off, 1=smooth, 2=immediate).
        // Pushed via session-0x02 ff-record kind=5; VGS-only. Default off.
        public int DashDisplayRotation { get; set; } = 0;

        // CM2 dash (dual-screen): the dashboard the user selected for the CM2's
        // own pipeline, independent of the wheel's selection. Empty = catalog default.
        public string Cm2SelectedDashboard { get; set; } = "";

        // Wheelbase ambient LED settings (2 strips; strip length per base model,
        // see BaseModelInfo). Defaults match observed R25 capture (rainbow mode,
        // brightness 100, startup/shutdown #66B8FF).
        // See docs/protocol/leds/base-ambient-0x20-0x22.md.
        public int BaseAmbientBrightness { get; set; } = 100;       // percent, 0..100
        public int BaseAmbientStandbyMode { get; set; } = 4;        // 4 = rainbow
        public int BaseAmbientIndicatorState { get; set; } = 1;     // on
        public int BaseAmbientSleepMode { get; set; } = 1;          // sleep effect: 1 = breathe
        public int BaseAmbientSleepTimeout { get; set; } = 15;      // minutes
        public int BaseAmbientStartupColor { get; set; } = 0x66B8FF;
        public int BaseAmbientShutdownColor { get; set; } = 0x66B8FF;

        // Blink colors (write-only, can't be polled — persisted here)
        // Packed as R<<16 | G<<8 | B, null = defaults not yet customized
        public int[]? WheelRpmBlinkColors { get; set; }
        public int[]? DashRpmBlinkColors { get; set; }

        // Connection enabled (persisted toggle)
        public bool ConnectionEnabled { get; set; } = true;

        // Last-known physical pedal connectivity per mBooster lane, indexed
        // [throttle, brake, clutch] and keyed like the per-profile device
        // settings ("mbooster:<serial>" once interrogated, transport instance
        // id before/alongside). Seeds a freshly created controller's
        // ConnectedAxes so phantom-axis protection and correct routing are
        // armed from the first HID event, instead of waiting up to a minute
        // for the device's next PD-Linked broadcast — a window every plugin
        // restart used to reopen. The live diagnostic still overwrites the
        // seed when it arrives.
        public Dictionary<string, bool[]> MBoosterKnownPedals { get; set; }
            = new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);

        // Routed-lane identities ("routedpedals:<port>") whose pedal slot (dev 0x19)
        // last identified as an mBooster rather than CRP/SRP pedals. Read by
        // HardwareApplier.SuppressPedalsWrite to block the pedals-* command set —
        // byte-identical to mbooster-* — during Init, before the model probe can
        // answer. Without it every plugin reload lands one CRP calibration burst on
        // the mBooster's registers. Maintained (added AND removed) by
        // MozaPlugin.OnRoutedMBoosterModelResolved, so a hookup swap self-corrects.
        public List<string> RoutedMBoosterPedalSlots { get; set; } = new List<string>();

        // Last successful COM port per device lane — seeded into that lane's
        // MozaSerialConnection on startup to skip re-probing. Empty = no saved port.
        public string LastWheelbasePort { get; set; } = "";
        public string LastAb9Port { get; set; } = "";
        public string LastDashboardPort { get; set; } = "";
        public string LastHubPort { get; set; } = "";
        // Port the dedicated base-aux pipe last bound (broken base + wheel on hub).
        public string LastBaseAuxPort { get; set; } = "";

        // Durable device identity per lane (VID:PID:serial — see
        // MozaPortDiscovery.DurableId), saved alongside the port name and tried
        // first on reconnect. A port name only holds until the device is replugged
        // somewhere else; this holds across that, which is what makes reconnect
        // deterministic under Wine/Proton where tty numbering moves. Empty = none
        // saved yet.
        public string LastWheelbaseDeviceId { get; set; } = "";
        public string LastAb9DeviceId { get; set; } = "";
        public string LastDashboardDeviceId { get; set; } = "";
        public string LastHubDeviceId { get; set; } = "";
        public string LastBaseAuxDeviceId { get; set; } = "";

        // Whether to automatically apply profile settings on launch
        public bool AutoApplyProfileOnLaunch { get; set; } = true;

        // Which graph occupies the right-hand slot of the Base tab's live
        // telemetry pair. Defaults to Bandwidth: it costs nothing on the wire,
        // whereas Torque runs a ~15 Hz poll whenever the panel is open.
        public BaseGraphMode BaseTabGraph { get; set; } = BaseGraphMode.Bandwidth;

        // When true, every device tab in the plugin pane is shown regardless of
        // detection — including the ones normally gated on hardware being present
        // and the retired/in-development ones (wheel, dashboard upload, wheel
        // files) whose content lives elsewhere or isn't finished. Diagnostic; the
        // tabs still render against whatever state the plugin has, so a tab for
        // absent hardware shows empty/idle values rather than live ones.
        public bool ShowAllTabs { get; set; } = false;

        // When true, automatically put the wheelbase into Work Mode standby
        // (main-set-work-mode=1) after the idle timeout elapses with no game
        // running and no recent activity, and wake it (=0) the moment a game
        // starts or the user interacts. Opt-in; default off.
        public bool AutoStandbyWhenNoGame { get; set; } = false;

        // Minutes of inactivity (no game, no wheel/pedal input, no UI use)
        // before auto-standby engages. Only meaningful when AutoStandbyWhenNoGame
        // is true. Selected from a preset combo in the UI.
        public int AutoStandbyTimeoutMinutes { get; set; } = 10;

        // When true, resend LED state to wheel every ~1 second even if unchanged.
        // Some ES wheels need this to stay in telemetry mode.
        public bool WheelKeepalive { get; set; } = true;

        // How long (seconds) to keep re-sending the last LED frame after the LEDs go
        // idle before pausing, so the wheel/dash can enter its own idle/sleep mode.
        // 0 = pause immediately. Applies to both the wheel and dash LED keepalives.
        public int WheelKeepaliveTimeoutSec { get; set; } = 45;

        // CM2 meter firmware era, auto-detected from the meter's 0x0E heartbeat
        // vocabulary and persisted so the right LED path is used from boot (the
        // heartbeat only arrives ~1/min). False = legacy RPM-ramp firmware
        // (autonomous thresholds + 41 FD DE bitmask); true = 2026-06 indicator
        // firmware (wheel-style group-0x3F live LED commands).
        public bool Cm2NewLedFirmware { get; set; } = false;

        // Gearshift event tuning (plugin-side; the firmware-stored intensity
        // is `base-gearshift-vibration`). VibrateOnNeutral default is false so
        // H-pattern shifters bump on engagement only — the prior gear → "N"
        // transition is dis-engagement and gets suppressed. Flip to true to
        // also fire on transitions into neutral. DebounceMs coalesces rapid
        // ratcheting (paddle bursts); 500 ms = ~2 shifts/sec ceiling.
        public bool GearshiftVibrateOnNeutral { get; set; } = false;
        public int GearshiftDebounceMs { get; set; } = 500;

        // Diagnostic serial capture is always on (no user toggle): the
        // dual-segment ring (SerialTrafficCapture) keeps the first ~60s of
        // startup plus a rolling last-N-minutes window in RAM so a bug report
        // always has the connect/handshake. See MozaPlugin.Init.
        //
        // Deprecated capture toggles — kept only so pre-existing settings JSON
        // still deserializes; no longer read anywhere.
        public bool DiagnosticCaptureEnabled { get; set; } = true;
        public bool AlwaysCaptureOnStartup { get; set; } = false;

        // Client-side cooldown timestamp for the "Submit bug report" button —
        // guards against accidental double-submits. Server enforces the real
        // per-IP rate limits.
        public DateTime LastBugReportUtc { get; set; }

        // Register a Control Mapper IVariantProvider so SimHub can key per-wheel
        // button mappings off (VID, PID, friendly-wheel-name) instead of treating
        // every MOZA wheel as the same wheelbase controller. Default-on; hidden
        // escape hatch for the rare case where a future SimHub assembly change
        // breaks the reflection-based registration — flip to false in
        // MozaPluginSettings.json to disable. See docs/controlmapper.md.
        public bool EnableControlMapperVariants { get; set; } = true;

        // Bridge-format JSONL wire trace at SimHub/Logs/moza-wire-*.jsonl.
        // Code-only toggle — not serialized so changing the default here
        // is the only way to flip it. Avoids stale persisted values.
        [Newtonsoft.Json.JsonIgnore]
        public bool EnableWireTraceFileSink { get; set; } = false;

        // Per-frame wire / firmware-debug / display-driver diagnostic lines
        // (MozaLog.WireDebugEnabled). No UI — flip to true in
        // MozaPluginSettings.json when debugging the frame path.
        //
        // Default OFF: measured across the diagnostics bundles in usb-capture/,
        // the WIRE session-chunk line alone was 46–74 % of moza-log.txt at 2–4
        // lines/s, so it evicted the connect/handshake history from the 5 000-line
        // ring before a bug report was ever pulled. SerialTrafficCapture already
        // records the same bytes and ships in the same bundle, so nothing is lost.
        public bool VerboseWireDebugLog { get; set; } = false;

        // One-shot marker for the migration that clears the old serialized
        // VerboseWireDebugLog=true. See MozaPlugin.Init.
        public bool VerboseWireDebugLogDefaultMigrated { get; set; }

        // One-shot marker for the migration that resamples every saved
        // mBooster CurveY/CurveX/InputCurveY array from its old 5-node
        // shape to the current 6-node one, preserving each curve's visual
        // shape instead of silently discarding it to a default. See
        // MozaPlugin.Init and MozaPlugin.MBooster's
        // MigrateMBoosterCurveArraysTo6.
        public bool MBoosterCurveArraysMigratedTo6 { get; set; }

        // One-shot marker for the follow-up migration that fixes the Sim
        // Input Mapping curve's default X breakpoints — they were 100/7 * k
        // (last node ~85.7%, inherited from the disproven/removed curve7
        // mechanism), capping Linear/preset/migrated curves at ~86% output
        // instead of reaching 100%. Any profile already run through
        // MBoosterCurveArraysMigratedTo6, or that clicked a preset button,
        // baked in the too-low shape. See MozaPlugin.Init and
        // FixMBoosterCurveArraysSeventhsBug.
        public bool MBoosterCurveArraysFixedSeventhsBug { get; set; }

        // Where wheelbase LFE effects come from. The plugin's own LFE tab and a
        // SimHub ShakeIt haptics device would sum on the wire, so exactly one owns
        // it. ShakeIt mode is what puts HapticsFeature in the base's device.json,
        // so switching redeploys the definition and asks for a SimHub restart.
        //
        // The initializer stays PluginTab deliberately: an existing settings file
        // has no such key, and flipping it here would silently hide a user's
        // configured LFE tab. New installs get ShakeIt via CreateForNewInstall, and
        // upgraders who were using the pre-1.6 haptics device get it via the
        // LegacyLfeDeviceMigrated one-shot below.
        public WheelbaseLfeSource WheelbaseLfeSource { get; set; } = WheelbaseLfeSource.PluginTab;

        // One-shot marker for the migration off the pre-1.6 "Wheelbase LFE haptics"
        // device (DeviceTypeID F208F60B-…), which was code-registered and vanished
        // when v1.6 replaced it with the per-model wheelbase device's Haptics
        // section. See Devices/Extensions/LegacyBaseDeviceMigration.cs and
        // MozaPlugin.Init. Latched whether or not an orphan was found, so a user
        // who later picks the plugin LFE tab is never flipped back.
        public bool LegacyLfeDeviceMigrated { get; set; }

        // One-shot marker for the repair that nulls saved knob palettes that are
        // entirely black — laundered from an unseeded MozaData mirror by the old
        // device-JSON capture / whole-array persist paths, not chosen by anyone.
        // See ProfileCoordinator.RepairAllBlackKnobColorArrays.
        public bool KnobColorAllBlackRepaired { get; set; }

        // An orphaned pre-1.6 haptics device was found and its settings have not
        // been carried into a per-model wheelbase device yet. Drives the one-time
        // banner; cleared by the import or by the user dismissing it.
        public bool LegacyLfeMigrationPending { get; set; }

        // InstanceId of that orphaned device (its folder under
        // PluginsData/Common/Devices). Empty = nothing to import. The folder itself
        // is never deleted — consumption is recorded here, not on disk.
        public string LegacyLfeMigrationInstanceId { get; set; } = "";

        // ~1/min pull of the wheel display's own log via session FF kind=14,
        // acked with kind=15 (which clears those lines on the device). No UI —
        // flip to false in MozaPluginSettings.json to stop the pull entirely.
        public bool EnableDeviceLogPull { get; set; } = true;

        // Radar (patch/ri*, OpponentCount, PlayerIndex) + track-map
        // (patch/Location*) channels. Code-only toggle — not serialized, no UI.
        // Shipping enabled: the radar/track-map feature is staying on (no
        // tier-def enable-handshake change — that broke binding).
        [Newtonsoft.Json.JsonIgnore]
        public bool EnableRadarTrackMapChannels { get; set; } = true;

        [Newtonsoft.Json.JsonIgnore]
        public bool EnableAutoTestOnConnect { get; set; } = false;

        // Hot dashboard re-negotiation (on by default): SwitchToProfile emits FF
        // kind=4 and re-emits tier-def on the still-open sess=0x01/0x02 instead
        // of a Stop+11s-sleep+Start cycle. Sessions 0x01/0x02/0x03 stay open
        // across switches and tier-def is re-emitted without preamble (matches
        // PitHouse, verified in sim/logs/bridge-20260517-* captures). JSON-ignored
        // so the default here is the only switch — a stale persisted value can't
        // override it.
        [Newtonsoft.Json.JsonIgnore]
        public bool EnableHotRenegotiation { get; set; } = true;

        /// <summary>Wine/Proton only: when sysfs has identified the MOZA tty AND
        /// Wine's dosdevices mapping resolves its COM name, open through
        /// <c>SerialPortMozaPort</c> (64 KB receive buffer, reads not gated on
        /// ClearCommError) instead of the raw device path. sysfs still supplies the
        /// identity, so no blind COM probing happens either way.
        ///
        /// <para>Escape hatch, default on. .NET SerialPort.Open under Wine has a
        /// history of native SIGSEGV on freshly-powered CDC-ACM ports — the stty
        /// warm-up runs first to prevent that, but a segfault is not catchable, so
        /// set this false to force the raw device path if opens start crashing.</para></summary>
        public bool PreferComPortOnWine { get; set; } = true;

        /// <summary>
        /// Persisted slot the auto-test most recently switched TO. On next
        /// run the harness picks the OTHER of {Core, Grids} so each launch
        /// flips direction without manual config. Persisted so debugging
        /// across restarts alternates dashboards naturally.
        /// </summary>
        public int AutoTestLastSlot { get; set; } = -1;

        // ===== Update notifier =====
        // In-plugin update check that hits the GitHub Releases API on plugin
        // load (and at most once per 24h thereafter), compares to the running
        // AssemblyInformationalVersion, and surfaces a banner in the About
        // tab when a newer release is available. Opt-out via UpdateCheckEnabled.
        // Network call is silent on failure for the automatic path; only the
        // manual "Check now" button surfaces errors inline. See
        // UI/UpdateCheck/UpdateCheckService.cs for the wire details.
        public bool UpdateCheckEnabled { get; set; } = true;

        // Legacy Stable/Dev enum, superseded by UpdateChannelId. Kept so an
        // older build reading a newer settings blob still gets a valid value;
        // always written as Stable (the dev channel no longer exists).
        public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

        // Release stream the checker follows: "stable", or "pr/<N>" to track
        // the newest per-commit build of an open pull request. Empty means
        // not migrated yet — MozaPlugin.Init resolves it to "stable".
        public string UpdateChannelId { get; set; } = "";

        // Display label of the selected PR channel (e.g. "PR #42: Fix …") so
        // the channel picker can render the selection before the release list
        // has been fetched this session. Empty for the stable channel.
        public string UpdateChannelLabel { get; set; } = "";

        // Version the user clicked "Skip this version" on — the banner stays
        // hidden as long as the latest published version still equals this
        // string. When a newer version appears the banner re-shows.
        public string LastSkippedVersion { get; set; } = "";

        // UTC timestamp of the last successful (or failed) check; the
        // automatic check skips while less than 24h has passed. DateTime.MinValue
        // means "never checked" → check immediately on next Init.
        public DateTime LastUpdateCheckUtc { get; set; } = DateTime.MinValue;

        // Cached version string from the last successful check. Lets the About
        // tab paint the banner immediately on open without waiting for a fresh
        // network round-trip. Empty = no successful check yet (or 404 on dev-latest).
        public string LastSeenLatestVersion { get; set; } = "";

        // html_url from the last successful check — wired to the "Open release
        // notes" banner button. Empty when LastSeenLatestVersion is empty.
        public string LastSeenReleaseUrl { get; set; } = "";

        // browser_download_url of the first MozaPlugin*.zip asset on the
        // latest release. Used by the in-app installer to fetch the new DLL
        // without re-hitting the GitHub API. Empty if the latest release has
        // no matching asset (manual hand-cut tags, or 404 on dev-latest) —
        // in which case the banner falls back to the release-notes link only.
        public string LastSeenAssetUrl { get; set; } = "";

        // The GitHub release `body` (markdown changelog) from the last
        // successful check, rendered as plain text in the About > Updates
        // "What's new" panel so users see what they're updating to without
        // leaving SimHub. Empty when LastSeenLatestVersion is empty.
        public string LastSeenReleaseNotes { get; set; } = "";

        // ===== Third-party SDK emulation =====
        // Master toggle for the in-plugin CoAP/UDP server that mimics MOZA's
        // PitHouse "partner SDK" surface (iRacing in particular). When false
        // (default) the plugin makes no attempt to bind a port — third-party
        // apps that depend on PitHouse continue to talk to PitHouse, not us.
        // Plugin-global (not per-game / per-wheel). Takes effect on next
        // plugin restart — Stream 7 wires the actual server lifecycle.
        public bool SdkEmulationEnabled { get; set; } = false;

        // One-time UI nudge: when the CoAP SDK server is disabled, the plugin
        // pane shows a banner suggesting the user enable it (prevents MOZA Pit
        // House from being launched by SDK apps). Set true once the user
        // dismisses the banner or clicks Configure SDK — persisted so it never
        // reappears.
        public bool SdkPromptDismissed { get; set; } = false;

        // Always bind to loopback (127.0.0.1) only. Hidden from the UI in v1
        // because exposing the partner-API to LAN traffic has no legitimate
        // use case and only adds attack surface — but plumbed through so a
        // future power-user switch can flip it without a settings migration.
        public bool SdkBindLoopbackOnly { get; set; } = true;

        // NOTE: ports for both UDP surfaces are NOT settings — they are
        // protocol-mandated and not actually configurable in practice.
        //   * CoAP SDK port 40266 is hardcoded as `mov dx, 0x9D4A` in
        //     MOZA_SDK.dll (both the official 1.0.1.8 build and iRacing's
        //     customized variant); the SDK does not discover the port.
        //   * UDP control port 40288 is the value third-party wheel-config
        //     tools assume by default; clients also accept an override
        //     from a settings.ini, but letting a SimHub user pick a port
        //     just guarantees the SDK / clients can't reach them.
        // The constants live with the server classes
        // (MozaSdkCoapServer + MozaControlUdpServer). If MOZA ever changes
        // the literals in a firmware/SDK update, change them there.

        // Independent enable for the plain-UDP-CBOR control surface
        // (MozaControlUdpServer on port 40288). Separate from
        // SdkEmulationEnabled so a user can run the CoAP server without the
        // UDP server or vice-versa. Default true so existing users with
        // SdkEmulationEnabled=true keep the previous combined behaviour
        // without a migration; users who want CoAP-only can flip this off
        // explicitly. When false, no UDP listener binds and clients on
        // 40288 silently fail to connect.
        public bool UdpControlEnabled { get; set; } = true;

        // Custom path for the PitHouse Presets folder used by the
        // "Import Profile" feature. Empty = auto-discover from
        // %USERPROFILE%\Documents\MOZA Pit House\Presets. Surface for Wine /
        // multi-drive setups where PitHouse's preset folder lives outside
        // the SimHub user's Documents (e.g. mounted Windows partition).
        public string PitHousePresetsPathOverride { get; set; } = "";

        // ===== Profile system (SimHub native) =====
        public MozaProfileStore ProfileStore { get; set; } = new MozaProfileStore();

        // Explicit plugin-pane language override picked from the Options tab.
        // null/empty/"auto" = auto-detect (LanguageResolver walks SimHub culture
        // → OS culture → en). A BCP-47 tag like "es" / "fr" / "ru" pins the
        // plugin to that language regardless of what SimHub or the OS reports.
        public string? PreferredLanguage { get; set; }

        // Marks the wheel device extension as already drained into the per-page
        // bundle + overlay. MozaWheelExtensionSettings.ApplyTo gates on this:
        // once true, subsequent SetSettings calls (which fire every restart and
        // every profile switch) skip the merge entirely, so a stale device JSON
        // cannot clobber the user's saved values. Lives on MozaPluginSettings
        // (which the plugin reliably flushes via the debounce timer + End())
        // rather than on the DTO (which SimHub doesn't reliably re-serialize
        // before shutdown).
        public bool WheelExtensionDrained { get; set; } = false;

        // Per-wheel-page mzdash folder library. Keyed by SimHub page DescriptorUniqueId
        // GUID. Shared across all profiles — every game using the same wheel sees
        // the same folder. Set per-wheel-page, not per-game, so the user maintains
        // one folder per physical wheel.
        public Dictionary<Guid, string> WheelMzdashFolderByPageGuid { get; set; }
            = new Dictionary<Guid, string>();

        // Per-wheel-page "is telemetry on for this wheel". Keyed by SimHub page GUID,
        // shared across profiles. Whether telemetry runs for a wheel is a wheel-level
        // decision; the per-game decision (which dashboard, which mzdash) stays on
        // the profile's WheelOverride.
        public Dictionary<Guid, bool> WheelTelemetryEnabledByPageGuid { get; set; }
            = new Dictionary<Guid, bool>();

        // Wheelbase-LFE presets (named snapshots of the 3 slots). Global library,
        // applied to the active profile's BaseLfe on load. The factory presets are
        // seeded in here once (BaseLfePresetsSeeded) and are thereafter ordinary
        // editable/deletable entries; import/export round-trips this list.
        public List<BaseLfePreset> BaseLfePresets { get; set; } = new List<BaseLfePreset>();
        // Factory-preset seed version applied to this library (0 = never). Bumped
        // when the factory presets change so existing users get the updated set
        // (factory-named presets are refreshed; user-named presets are untouched).
        public int BaseLfePresetsSeedVersion { get; set; } = 0;

        // Default telemetry-enable state for a wheel page that has no explicit entry
        // in WheelTelemetryEnabledByPageGuid yet (dict-missing = "no opinion"). Fresh
        // installs set this true via the ReadCommonSettings create-if-not-found factory
        // so new users get dashboard telemetry on out of the box; existing users'
        // on-disk JSON lacks the field, so it deserializes to false.
        //
        // Only consulted for SCREENLESS and unknown-model wheels now — a wheel that
        // has a display defaults to on regardless of this flag. Being install-scoped,
        // it left every pre-existing install's newly-attached display wheel dark
        // (see ProfileCoordinator.ActiveTelemetryEnabled).
        public bool TelemetryEnabledDefaultForNewWheels { get; set; } = false;

        // Per-wheel-page sleep-light settings (firmware preference, not per-game).
        // Schema v8 moved these off WheelOverride / MozaProfile baseline. Each
        // entry holds mode / timeout (minutes) / speed (ms) / packed RGB color.
        // Absence = wheel keeps its currently-stored value.
        public Dictionary<Guid, WheelSleepSettings> WheelSleepByPageGuid { get; set; }
            = new Dictionary<Guid, WheelSleepSettings>();

        // Per-wheel-page idle-effect/speed settings (telemetry-area RPM LEDs,
        // buttons, knob). Schema v9 moved these off WheelOverride / MozaProfile
        // baseline because the idle animation is a property of the wheel, not
        // the game — same as the sleep-light bundle above. Each entry holds the
        // three effect IDs (cmd 0x1D [group]) and the three per-group speeds
        // (cmd 0x1E [group] [BE u16 ms]).
        public Dictionary<Guid, WheelIdleSettings> WheelIdleByPageGuid { get; set; }
            = new Dictionary<Guid, WheelIdleSettings>();

        // ===== Dashboard Telemetry =====
        public bool TelemetryEnabled { get; set; } = false;

        /// <summary>
        /// LEGACY drain source only — the master channel mapper's defaults now live on
        /// their own profile store, <see cref="ChannelDefaultsStore"/>. Nothing reads
        /// this at runtime; <c>ProfileCoordinator.MigrateMasterDefaultsToProfiles</c>
        /// copies it into that store's first profile once, guarded by
        /// <see cref="MasterDefaultsMigratedToProfiles"/>, then clears it.
        /// </summary>
        public Dictionary<string, string> TelemetryDefaultMappings { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>One-shot sentinel for the master-defaults drain above. Set the
        /// first time the profile system initialises on this build, so a user who
        /// afterwards clears the migrated profile's mappings doesn't get them
        /// re-seeded.</summary>
        public bool MasterDefaultsMigratedToProfiles { get; set; } = false;

        /// <summary>
        /// The master channel mapper's own profile list — independent of
        /// <see cref="ProfileStore"/> above, with its own selector in that dialog and
        /// its own per-game switching. Serialized in this same blob, so both stores
        /// persist through one SaveCommonSettings.
        /// </summary>
        public MozaChannelDefaultsStore ChannelDefaultsStore { get; set; }
            = new MozaChannelDefaultsStore();

        // Name of the active dashboard profile (empty = use first available)
        public string TelemetryProfileName { get; set; } = "";

        // User-loaded .mzdash file path (empty = use builtin profile)
        public string TelemetryMzdashPath { get; set; } = "";

        // User-configured folder of .mzdash files (empty = none).
        // Scanned at init and on picker change. Wheel cache takes priority;
        // folder acts as fallback library when cache misses.
        public string TelemetryMzdashFolder { get; set; } = "";

        // Files tab -> DASHBOARD UPLOAD -> Source radio. Plugin-global on
        // purpose: it is a preference about how the user likes to PICK a
        // dashboard, not a property of a wheel — so the wheel page and the CM2
        // dash page (which share one DashboardFilesControl instance type) share
        // it. LocalFile preserves the previously hardcoded XAML IsChecked.
        // Serializes as an int — there is no StringEnumConverter anywhere in
        // this project — so LocalFile must stay pinned at 0.
        public DashboardUploadSource DashboardUploadSourceMode { get; set; }
            = DashboardUploadSource.LocalFile;

        // Last dashboard name picked in the Files-tab library combo. A stale
        // name simply misses the combo's Items.Contains test and falls through
        // to index 0 — no failure mode.
        public string LastUploadLibraryName { get; set; } = "";

        // Directory the .mzdash OpenFileDialog opens in. Convenience only; the
        // file itself is deliberately NOT restored, so the Upload button stays
        // correctly disabled until the user actually picks one.
        public string LastUploadFileDirectory { get; set; } = "";

        // Byte limit override (0 = auto from profile)
        public int TelemetryByteLimitOverride { get; set; } = 0;

        // Upload the .mzdash dashboard to the wheel on every telemetry start.
        // PitHouse does this on every connection — the wheel may require it.
        public bool TelemetryUploadDashboard { get; set; } = false;

        // Download dashboards from the wheel when it reports them.
        public bool TelemetryDownloadDashboard { get; set; } = false;

        // Telemetry send rate in Hz
        public int TelemetrySendRateHz { get; set; } = 20;

        // Whether to send the 0x40/28:02 telemetry mode frame periodically
        public bool TelemetrySendModeFrame { get; set; } = true;

        // Whether to send the 0x2D/F5:31 sequence counter to the base (~30 Hz)
        public bool TelemetrySendSequenceCounter { get; set; } = true;

        /// <summary>
        /// FSR V1 active built-in dashboard/page index (0..18), keyed by wheel-page
        /// GUID. The plugin selects it by sending the group-0x32 cmd-0x81 index write
        /// (<see cref="MozaPlugin.Telemetry.Display.Fsr1DisplayEmitter.BuildSelect"/>); the
        /// wheel also changes it via its HID button combo and reports it back. Absent
        /// = default 0. Per-wheel (not per-game) — the wheel shows one dashboard.
        /// </summary>
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
            DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
        public Dictionary<Guid, int> Fsr1ActiveDashboardByWheelGuid { get; set; }
            = new Dictionary<Guid, int>();

        // NOTE: the CM1-vs-CM2 verdict is intentionally NOT persisted. It's a
        // statement about what's physically on the bus right now and is re-derived
        // each boot by the discriminator (see Fsr1Cm1MappingCoordinator.DashIsCm1).
        // A prior persisted form (DashIsCm1ByGuid, keyed by the constant CM1 GUID)
        // made a single mis-latch permanent and global; it has been removed. Any
        // stale value left in an existing settings file is ignored and dropped on
        // the next save.

        /// <summary>CM1 selected dashboard page index (1-based), per dash GUID. Set via the
        /// 0x32/0x81 select command and reported back by the dash's Param-6 log. Absent = 1.</summary>
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
            DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
        public Dictionary<Guid, int> Cm1ActiveDashboardByGuid { get; set; }
            = new Dictionary<Guid, int>();

        // --- MOZA Multi-Function Stalks: truck-sim keyboard emulation ---

        /// <summary>Stalks operating mode. Default <see cref="StalkMode.ButtonBox"/>
        /// = the plugin does nothing (raw HID buttons). <see cref="StalkMode.TruckSim"/>
        /// enables ETS2/ATS keyboard emulation from the button map below.</summary>
        public StalkMode StalksMode { get; set; } = StalkMode.ButtonBox;

        /// <summary>Per-button map + wiper/light stage config used when
        /// <see cref="StalksMode"/> is <see cref="StalkMode.TruckSim"/>.</summary>
        public StalkTruckSimSettings StalksTruckSim { get; set; } = new StalkTruckSimSettings();

    }

    /// <summary>
    /// Per-wheel-page sleep-light bundle stored on
    /// <see cref="MozaPluginSettings.WheelSleepByPageGuid"/>. Wraps the
    /// four wheel sleep-light fields into one dict-value so the JSON shape
    /// stays compact and adding a future field doesn't require a new dict.
    /// All fields use -1 (or null for the color array) as the "not set"
    /// sentinel, matching <see cref="WheelOverride"/> convention.
    /// </summary>
    public sealed class WheelSleepSettings
    {
        public int Mode { get; set; } = -1;          // cmd 0x20 mode enum
        public int TimeoutMin { get; set; } = -1;    // cmd 0x21 BE u16 minutes
        public int SpeedMs { get; set; } = -1;       // cmd 0x22 BE u16 ms
        public int[]? Color { get; set; }            // packed R<<16|G<<8|B (single)

        public WheelSleepSettings Clone()
        {
            return new WheelSleepSettings
            {
                Mode = Mode,
                TimeoutMin = TimeoutMin,
                SpeedMs = SpeedMs,
                Color = Color != null ? (int[])Color.Clone() : null,
            };
        }
    }

    /// <summary>
    /// Per-wheel-page idle-effect + idle-speed bundle stored on
    /// <see cref="MozaPluginSettings.WheelIdleByPageGuid"/>. Wraps the
    /// three telemetry/buttons/knob effect IDs and the three matching
    /// per-group speed values (ms) into one dict-value, mirroring the
    /// per-page sleep bundle (<see cref="WheelSleepSettings"/>).
    /// All fields use -1 as the "not set" sentinel.
    /// </summary>
    public sealed class WheelIdleSettings
    {
        public int TelemetryEffect { get; set; } = -1;   // cmd 0x1D [0]
        public int ButtonsEffect { get; set; } = -1;     // cmd 0x1D [1]
        public int KnobEffect { get; set; } = -1;        // cmd 0x1D [3]
        public int TelemetrySpeedMs { get; set; } = -1;  // cmd 0x1E [0] [BE u16]
        public int ButtonsSpeedMs { get; set; } = -1;    // cmd 0x1E [1] [BE u16]
        public int KnobSpeedMs { get; set; } = -1;       // cmd 0x1E [3] [BE u16]

        public WheelIdleSettings Clone()
        {
            return new WheelIdleSettings
            {
                TelemetryEffect = TelemetryEffect,
                ButtonsEffect = ButtonsEffect,
                KnobEffect = KnobEffect,
                TelemetrySpeedMs = TelemetrySpeedMs,
                ButtonsSpeedMs = ButtonsSpeedMs,
                KnobSpeedMs = KnobSpeedMs,
            };
        }
    }

    /// <summary>
    /// Truck-sim keyboard-mapping config for the MOZA Multi-Function Stalks. Stored
    /// on <see cref="MozaPluginSettings.StalksTruckSim"/>. The per-button map is
    /// assigned interactively in the Stalks settings tab — there is no default map.
    /// Cycle keys / stage counts start at ETS2 defaults and are user-adjustable.
    /// </summary>
    public sealed class StalkTruckSimSettings
    {
        /// <summary>0-based stalk button index → action. Absent = unmapped.</summary>
        public Dictionary<int, StalkAction> ButtonActions { get; set; }
            = new Dictionary<int, StalkAction>();

        // Cycle keys for the stage-cycle controls (game keybinds; ETS2 defaults).
        public string WiperForwardKey { get; set; } = "P";     // ETS2 wipers forward
        public string WiperBackKey { get; set; } = "Minus";    // ETS2 "wipers back"
        public string LightCycleKey { get; set; } = "L";       // ETS2 light-mode cycle

        // Turn-signal keys (toggle in-game). The neutral position re-taps whichever
        // side is active to cancel it.
        public string IndicatorLeftKey { get; set; } = "[";
        public string IndicatorRightKey { get; set; } = "]";

        /// <summary>How long the blinker stays lit after it is switched on, in seconds.
        /// A neutral-position cancel that lands sooner is deferred until the time is up,
        /// so a quick flick of the lever still signals. 0 = cancel immediately.</summary>
        public int IndicatorMinBlinkSeconds { get; set; } = 3;

        // Stage models.
        public int WiperStageCount { get; set; } = 4;
        public bool WiperForwardWraps { get; set; } = false;   // ETS2 wiper key does not wrap
        public int LightStageCount { get; set; } = 3;          // lights: forward-only wrap (L)

        // Key output timing (ms).
        public int KeyHoldMs { get; set; } = 30;
        public int KeyGapMs { get; set; } = 40;

        public StalkTruckSimSettings Clone()
        {
            var c = new StalkTruckSimSettings
            {
                WiperForwardKey = WiperForwardKey,
                WiperBackKey = WiperBackKey,
                LightCycleKey = LightCycleKey,
                IndicatorLeftKey = IndicatorLeftKey,
                IndicatorRightKey = IndicatorRightKey,
                IndicatorMinBlinkSeconds = IndicatorMinBlinkSeconds,
                WiperStageCount = WiperStageCount,
                WiperForwardWraps = WiperForwardWraps,
                LightStageCount = LightStageCount,
                KeyHoldMs = KeyHoldMs,
                KeyGapMs = KeyGapMs,
                ButtonActions = new Dictionary<int, StalkAction>(),
            };
            if (ButtonActions != null)
                foreach (var kv in ButtonActions)
                    c.ButtonActions[kv.Key] = kv.Value?.Clone() ?? new StalkAction();
            return c;
        }

        /// <summary>Populate the ETS2/ATS default button map (0-based button indices,
        /// i.e. HID button number − 1): light knob 1–3, high beam 4/5, turn signals
        /// 8/9/10, wipers single-swipe 20 and stages 21–24.</summary>
        public void ApplyEts2Defaults()
        {
            WiperForwardKey = "P";
            WiperBackKey = "Minus";
            LightCycleKey = "L";
            IndicatorLeftKey = "[";
            IndicatorRightKey = "]";
            IndicatorMinBlinkSeconds = 3;
            WiperStageCount = 4;
            WiperForwardWraps = false;
            LightStageCount = 3;

            ButtonActions = new Dictionary<int, StalkAction>
            {
                { 0, new StalkAction { Kind = StalkActionKind.LightStage, Stage = 0 } }, // btn1 lights off
                { 1, new StalkAction { Kind = StalkActionKind.LightStage, Stage = 1 } }, // btn2 accessory
                { 2, new StalkAction { Kind = StalkActionKind.LightStage, Stage = 2 } }, // btn3 headlights
                { 3, new StalkAction { Kind = StalkActionKind.Momentary, Key = "K" } },  // btn4 high beam toggle
                { 4, new StalkAction { Kind = StalkActionKind.ReleaseHeld } },           // btn5 neutral: releases the flash
                { 5, new StalkAction { Kind = StalkActionKind.LatchKey, Key = "J" } },   // btn6 flash: latch J until neutral (btn5)
                { 7, new StalkAction { Kind = StalkActionKind.IndicatorRight } },        // btn8 right
                { 8, new StalkAction { Kind = StalkActionKind.IndicatorCancel } },       // btn9 neutral/cancel
                { 9, new StalkAction { Kind = StalkActionKind.IndicatorLeft } },         // btn10 left
                { 19, new StalkAction { Kind = StalkActionKind.WiperSingleSwipe } },     // btn20 single swipe
                { 20, new StalkAction { Kind = StalkActionKind.WiperStage, Stage = 0 } },// btn21 off
                { 21, new StalkAction { Kind = StalkActionKind.WiperStage, Stage = 1 } },// btn22 intermittent
                { 22, new StalkAction { Kind = StalkActionKind.WiperStage, Stage = 2 } },// btn23 low
                { 23, new StalkAction { Kind = StalkActionKind.WiperStage, Stage = 3 } },// btn24 hi
            };
        }
    }
}
