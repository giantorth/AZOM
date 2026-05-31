using System;
using System.Collections.Generic;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.UI.UpdateCheck;

namespace MozaPlugin
{
    /// <summary>
    /// Persisted plugin settings. Saved/loaded via SimHub's ReadCommonSettings/SaveCommonSettings.
    /// Stores values that the wheel doesn't retain between sessions.
    /// </summary>
    public class MozaPluginSettings
    {
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

        // Wheelbase ambient LED settings (R21/R25/R27 family — 18 LEDs / 2 strips).
        // Defaults match observed R25 capture (rainbow mode, brightness 100,
        // startup/shutdown #66B8FF). See docs/protocol/leds/base-ambient-0x20-0x22.md.
        public int BaseAmbientBrightness { get; set; } = 100;       // 0..255 wire range; UI exposes 0..100 mapped
        public int BaseAmbientStandbyMode { get; set; } = 4;        // 4 = rainbow
        public int BaseAmbientIndicatorState { get; set; } = 1;     // on
        public int BaseAmbientSleepMode { get; set; } = 1;          // enabled
        public int BaseAmbientSleepTimeout { get; set; } = 15;
        public int BaseAmbientStartupColor { get; set; } = 0x66B8FF;
        public int BaseAmbientShutdownColor { get; set; } = 0x66B8FF;

        // Blink colors (write-only, can't be polled — persisted here)
        // Packed as R<<16 | G<<8 | B, null = defaults not yet customized
        public int[]? WheelRpmBlinkColors { get; set; }
        public int[]? DashRpmBlinkColors { get; set; }

        // Per-knob LED ring colours (W17/W18 only). Write-only on the wire —
        // persisted here so they survive restarts. Packed as R<<16 | G<<8 | B.
        public int[]? WheelKnobBackgroundColors { get; set; }
        public int[]? WheelKnobPrimaryColors { get; set; }

        // Group 3 per-LED ring colors (up to 56 LEDs). Readable from wheel but persisted
        // for profile switching. Packed as R<<16 | G<<8 | B.
        public int[]? WheelKnobRingColors { get; set; }
        private volatile int _wheelKnobRingBrightness = -1;
        public int WheelKnobRingBrightness { get => _wheelKnobRingBrightness; set => _wheelKnobRingBrightness = value; }

        // Connection enabled (persisted toggle)
        public bool ConnectionEnabled { get; set; } = true;

        // Last successful COM port — seeded into MozaSerialConnection on startup
        // to skip re-probing. Empty = no saved port.
        public string LastWheelbasePort { get; set; } = "";
        public string LastAb9Port { get; set; } = "";
        public string LastDashboardPort { get; set; } = "";
        public string LastHubPort { get; set; } = "";

        // Hard opt-out of the serial-probe fallback. Default behaviour
        // (false) is registry-first: if the registry-based MOZA USB
        // discovery returns matching ports, those are used and no probe
        // ever runs; if the registry returns zero MOZA devices total
        // (Wine/Proton without USB enumeration, driver not loaded),
        // the plugin falls back to writing a Moza protocol probe frame
        // to every COM port on the system as a last resort. Setting
        // this to true forbids the probe entirely — useful for the
        // rare user who wants to guarantee no Moza writes ever reach
        // a non-Moza serial peripheral. The previous ScanUnknownSerialPorts
        // (and earlier EnableAb9) on-disk keys are silently ignored.
        public bool DisableSerialProbeFallback { get; set; } = false;

        // Skip AB9 active-shifter detection entirely. Explicit user opt-out
        // that wins regardless of discovery mode. When false (default), AB9
        // probing is *also* auto-suppressed at runtime whenever MOZA's
        // registry-based USB enumeration returns empty — that's the signal
        // that the system has no working Windows registry path (Wine/Proton),
        // where the AB9 probe would otherwise sweep every COM symlink every
        // 5 seconds and has been observed to lock up SimHub. So in practice:
        //   - Windows w/ working registry, no AB9: probe runs in microseconds.
        //   - Windows w/ working registry, AB9 present: detected normally.
        //   - Wine/Linux (registry empty): probe auto-skipped.
        //   - User explicitly true: probe skipped on any system.
        // The AB9 manager instance is still constructed so existing null-safe
        // call sites and the dormant engine-vib worker stay valid; only the
        // connect/probe is suppressed.
        public bool DisableAb9Detection { get; set; } = false;

        // Whether to automatically apply profile settings on launch
        public bool AutoApplyProfileOnLaunch { get; set; } = true;

        // When true, only send LED updates to wheel when data actually changed (ignores SimHub forceRefresh).
        // Fixes flickering on some non-ES wheels. When false, respects SimHub's refresh cycle.
        public bool LimitWheelUpdates { get; set; } = false;

        // When true, resend LED state to wheel every ~1 second even if unchanged.
        // Some ES wheels need this to stay in telemetry mode.
        public bool WheelKeepalive { get; set; } = true;

        // When true, always resend the LED bitmask alongside color updates even if the bitmask
        // value hasn't changed. Fixes wheels that don't pick up new colors without a bitmask write.
        public bool AlwaysResendBitmask { get; set; } = false;

        // Gearshift event tuning (plugin-side; the firmware-stored intensity
        // is `base-gearshift-vibration`). VibrateOnNeutral default is false so
        // H-pattern shifters bump on engagement only — the prior gear → "N"
        // transition is dis-engagement and gets suppressed. Flip to true to
        // also fire on transitions into neutral. DebounceMs coalesces rapid
        // ratcheting (paddle bursts); 500 ms = ~2 shifts/sec ceiling.
        public bool GearshiftVibrateOnNeutral { get; set; } = false;
        public int GearshiftDebounceMs { get; set; } = 500;

        // Persistent: when true, MozaPlugin.Init starts the serial traffic capture
        // automatically (catches early connect/handshake traffic the user can't normally
        // arm in time). Stays on across launches until the user toggles it off.
        public bool AlwaysCaptureOnStartup { get; set; } = false;

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
        // Currently ON while string-channel (sess=0x01 type=0x05) wiring is
        // being verified; flip back to false once that work is signed off.
        [Newtonsoft.Json.JsonIgnore]
        public bool EnableWireTraceFileSink { get; set; } = false;

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

        // Release stream the checker follows. Stable = /releases/latest,
        // Dev = /releases/tags/dev-latest. Persisted as int so JSON shape
        // matches the existing enum convention.
        public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

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

        // Persisted schema-migration marker.
        //   2 = first cutover: legacy per-UID slots → profile.WheelOverridesByPageGuid.
        //   3 = full clean cutover (telemetry settings moved to overlay).
        //   4 = mzdash folder moved from per-overlay to per-wheel-page (shared
        //       across profiles). Folder is a library setting tied to the wheel,
        //       not the game.
        //   6 = (broken) initial v4/v5/v6 cutover. The empty-profiles short-circuit
        //       in MigrateSettingsToSchemaV2 returned without seeding the per-page
        //       mzdash-folder dict or the default profile's dash baselines, so
        //       pre-refactor users upgrading lost their folder + got a zero-default
        //       display brightness on first launch.
        //   7 = repair pass for the v6 short-circuit: re-runs the per-page folder
        //       seed from the flat field (which survives ClearLegacyAfterMigration),
        //       and reseeds every profile's dash/ambient/gearshift baselines from
        //       _settings flat fields when still sentinel. Idempotent.
        //   8 = wheel sleep-light settings (mode/timeout/speed/color) moved off
        //       the per-game-per-wheel overlay onto WheelSleepByPageGuid (one
        //       record per wheel, shared across profiles). Sleep behavior is a
        //       firmware preference, not a per-game decision. Migration drains
        //       from WheelOverride.WheelSleep* (now captured via LegacyJsonFields),
        //       MozaProfile.WheelSleep* baseline (now captured via JsonExtensionData
        //       on MozaProfile), and the _settings.WheelSleep* flat fields.
        //   9 = wheel idle-effect + idle-speed (telemetry/buttons/knob) moved
        //       off WheelOverride / MozaProfile baseline onto WheelIdleByPageGuid.
        //       Same reasoning as v8 sleep: the idle animation pick is a property
        //       of the wheel, not the game. Migration drains from the per-profile
        //       overlay, the profile baseline, and the _settings flat fields,
        //       then zeroes the legacy slots so subsequent saves don't resurrect
        //       per-game values.
        public int SettingsSchemaVersion { get; set; } = 0;

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

        // Per-wheel-page firmware-era pick. Keyed by SimHub page GUID, stored as int
        // (cast from MozaWheelEra). Firmware era is a property of the wheel/firmware,
        // not the game — making it per-(game × wheel) would just force the user to
        // re-pick the same era for every profile.
        public Dictionary<Guid, int> WheelTelemetryEraByPageGuid { get; set; }
            = new Dictionary<Guid, int>();

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

        // Name of the active dashboard profile (empty = use first available)
        public string TelemetryProfileName { get; set; } = "";

        // User-loaded .mzdash file path (empty = use builtin profile)
        public string TelemetryMzdashPath { get; set; } = "";

        // User-configured folder of .mzdash files (empty = none).
        // Scanned at init and on picker change. Wheel cache takes priority;
        // folder acts as fallback library when cache misses.
        public string TelemetryMzdashFolder { get; set; } = "";

        // LEGACY (pre-2026-05-14 refactor): per-UID mzdash folder. Migration
        // moves entries into the wheel-page overlay's TelemetryMzdashFolder.
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
            DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
        public Dictionary<string, string> WheelMzdashFolderByUid { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // LEGACY (pre-2026-05-14 refactor): per-UID telemetry slot. Migration
        // moves entries into the wheel-page overlay's TelemetryEnabled /
        // TelemetryProfileName / TelemetryMzdashPath.
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
            DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
        public Dictionary<string, TelemetryWheelSlot> TelemetryByWheelUid { get; set; }
            = new Dictionary<string, TelemetryWheelSlot>(StringComparer.OrdinalIgnoreCase);

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

        // LEGACY (pre-2026-05-09): single-level dashboard → url → property dict, NOT
        // scoped per wheel and using a brittle dashboard key (file SHA1 changes on
        // every PitHouse re-save; cleared on dropdown switch). Kept solely for
        // one-shot migration into TelemetryChannelMappingsByWheel — see
        // MigrateLegacyChannelMappingsIfNeeded(). New code never writes here.
        public Dictionary<string, Dictionary<string, string>>? TelemetryChannelMappings { get; set; }

        // Per-wheel, per-dashboard channel mappings.
        //   Outer key:  Wheel MCU UID hex (24 lowercase chars from MozaData.WheelMcuUid),
        //               or "" when UID is unknown (pre-detect / legacy migration).
        //   Middle key: Dashboard identity from MozaPlugin.GetActiveDashboardKeyCandidates().
        //               Preferred: "wheel:&lt;configJsonId&gt;" (stable, survives re-uploads).
        //               Fallback:  "file:&lt;filename&gt;:&lt;sha1-first-8&gt;" (custom mzdash file).
        //               Fallback:  "builtin:&lt;profileName&gt;" (embedded profile).
        //   Inner key:  Channel URL (e.g. "v1/gameData/Rpm").
        //   Value:      SimHub property path (e.g. "DataCorePlugin.GameData.Rpms").
        //               Empty/missing = use Telemetry.json default.
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
            DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
        public Dictionary<string, Dictionary<string, Dictionary<string, string>>> TelemetryChannelMappingsByWheel { get; set; }
            = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One-shot migration: copy entries from the legacy single-level
        /// <see cref="TelemetryChannelMappings"/> into <see cref="TelemetryChannelMappingsByWheel"/>
        /// under the empty-wheel slot ("") so users upgrading from 2026-05-08 or earlier
        /// don't lose their per-dashboard mappings. The legacy field is cleared after
        /// migration so it serializes as null on the next save and never re-runs.
        /// Returns true when a migration actually happened (caller should SaveSettings()).
        /// </summary>
        public bool MigrateLegacyChannelMappingsIfNeeded()
        {
            var legacy = TelemetryChannelMappings;
            if (legacy == null || legacy.Count == 0) return false;

            if (TelemetryChannelMappingsByWheel == null)
                TelemetryChannelMappingsByWheel =
                    new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

            if (!TelemetryChannelMappingsByWheel.TryGetValue("", out var emptyWheel))
            {
                emptyWheel = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                TelemetryChannelMappingsByWheel[""] = emptyWheel;
            }

            foreach (var kv in legacy)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                // Don't clobber an existing entry under the empty-wheel slot — first
                // wins. (Subsequent loads of the legacy field shouldn't happen because
                // we null it out below, but be defensive.)
                if (emptyWheel.ContainsKey(kv.Key)) continue;
                emptyWheel[kv.Key] = new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase);
            }

            TelemetryChannelMappings = null;
            return true;
        }

        // LEGACY (pre-2026-05-14 refactor): per-wheel-model slot dict.
        // Migration translates entries into profile.WheelOverridesByPageGuid
        // and clears this dict; new code never reads or writes it. Kept on
        // the type only so the one-shot migration can deserialize legacy JSON.
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
            DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
        public Dictionary<string, PerWheelSlot> PerWheelSlots { get; set; }
            = new Dictionary<string, PerWheelSlot>(StringComparer.OrdinalIgnoreCase);
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
    /// Per-physical-wheel dashboard-telemetry slot. Keyed by MCU UID (24-char hex)
    /// inside <see cref="MozaPluginSettings.TelemetryByWheelUid"/>. Holds the
    /// dashboard profile selection for one specific physical wheel; lets the user
    /// keep a VGS configured for "DNR endurance" while leaving a CS V2.1 with no
    /// dashboard, even when both extensions are registered in SimHub at the same
    /// time. UID-keyed rather than model-keyed so two wheels of the same model
    /// can carry different profiles, and so the slot is never written before the
    /// wheel has identified itself on the serial bus.
    /// </summary>
    public class TelemetryWheelSlot
    {
        public bool TelemetryEnabled { get; set; }
        public string TelemetryProfileName { get; set; } = "";
        public string TelemetryMzdashPath { get; set; } = "";
    }

    /// <summary>
    /// Per-wheel-model snapshot of the subset of <see cref="MozaPluginSettings"/>
    /// fields that are scoped to a specific wheel model. Keyed by wheel model name
    /// inside <see cref="MozaPluginSettings.PerWheelSlots"/>.
    /// </summary>
    public class PerWheelSlot
    {
        public int WheelTelemetryMode { get; set; } = -1;
        public int WheelIdleEffect { get; set; } = -1;
        public int WheelButtonsIdleEffect { get; set; } = -1;
        public int WheelKnobIdleEffect { get; set; } = -1;
        public int WheelPaddlesMode { get; set; } = -1;
        public int WheelClutchPoint { get; set; } = -1;
        public int WheelKnobMode { get; set; } = -1;
        public int WheelStickMode { get; set; } = -1;
        public int WheelRpmIndicatorMode { get; set; } = -1;
        public int WheelRpmDisplayMode { get; set; } = -1;
        public int WheelRpmBrightness { get; set; } = 100;
        public int WheelButtonsBrightness { get; set; } = 100;
        public int WheelFlagsBrightness { get; set; } = 100;
        public int WheelESRpmBrightness { get; set; } = 15;
        public int[]? WheelRpmBlinkColors { get; set; }
        public int[]? WheelKnobBackgroundColors { get; set; }
        public int[]? WheelKnobPrimaryColors { get; set; }
        public int[]? WheelKnobRingColors { get; set; }
        public int WheelKnobRingBrightness { get; set; } = -1;
        public int WheelKnobLedMode { get; set; } = -1;
        public int WheelButtonsLedMode { get; set; } = -1;
        public int WheelTelemetryIdleSpeedMs { get; set; } = -1;
        public int WheelButtonsIdleSpeedMs { get; set; } = -1;
        public int WheelKnobIdleSpeedMs { get; set; } = -1;
        public int WheelSleepMode { get; set; } = -1;
        public int WheelSleepTimeoutMin { get; set; } = -1;
        public int WheelSleepSpeedMs { get; set; } = -1;
        public int[]? WheelSleepColor { get; set; }
    }
}
