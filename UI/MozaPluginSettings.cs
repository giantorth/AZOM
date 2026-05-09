using System;
using System.Collections.Generic;
using MozaPlugin.Telemetry;

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

        // AB9 shifter detection toggle. When false, plugin never probes / opens
        // the AB9 port — defense for users with base-only setups where the
        // AB9 manager would otherwise try to grab a COM port that may collide
        // with the wheelbase under Wine. Off by default — most users don't
        // have an AB9; AB9 owners flip this on once. Existing users with AB9
        // hardware will need to enable it after upgrade (Newtonsoft fills
        // missing JSON keys from the C# initializer = false).
        public bool EnableAb9 { get; set; } = false;

        // Whether to automatically apply profile settings on launch
        public bool AutoApplyProfileOnLaunch { get; set; } = true;

        // When true, only send LED updates to wheel when data actually changed (ignores SimHub forceRefresh).
        // Fixes flickering on some non-ES wheels. When false, respects SimHub's refresh cycle.
        public bool LimitWheelUpdates { get; set; } = false;

        // Per-slot min/max index for the experimental diagnostic panels (slots 0..5).
        // -1 sentinel = "use full range" (slot's MaxLeds-1 for max, 0 for min).
        public int[] ExtLedDiagMin { get; set; } = new[] { -1, -1, -1, -1, -1, -1 };
        public int[] ExtLedDiagMax { get; set; } = new[] { -1, -1, -1, -1, -1, -1 };

        // When true, resend LED state to wheel every ~1 second even if unchanged.
        // Some ES wheels need this to stay in telemetry mode.
        public bool WheelKeepalive { get; set; } = true;

        // When true, always resend the LED bitmask alongside color updates even if the bitmask
        // value hasn't changed. Fixes wheels that don't pick up new colors without a bitmask write.
        public bool AlwaysResendBitmask { get; set; } = false;

        // One-shot flag: arm capture from the Diagnostics tab so it's already running when
        // the plugin re-initializes (catches early connect/handshake traffic that the user
        // can't normally arm in time). Cleared on Init the moment capture is started, so
        // it never persists past one launch.
        public bool StartCaptureOnNextLaunch { get; set; } = false;

        // Bridge-format JSONL wire trace at SimHub/Logs/moza-wire-*.jsonl.
        // Code-only toggle — not serialized so changing the default here
        // is the only way to flip it. Avoids stale persisted values.
        [Newtonsoft.Json.JsonIgnore]
        public bool EnableWireTraceFileSink { get; set; } = false;

        [Newtonsoft.Json.JsonIgnore]
        public bool EnableAutoTestOnConnect { get; set; } = false;

        /// <summary>
        /// Persisted slot the auto-test most recently switched TO. On next
        /// run the harness picks the OTHER of {Core, Grids} so each launch
        /// flips direction without manual config. Persisted so debugging
        /// across restarts alternates dashboards naturally.
        /// </summary>
        public int AutoTestLastSlot { get; set; } = -1;

        // ===== Profile system (SimHub native) =====
        public MozaProfileStore ProfileStore { get; set; } = new MozaProfileStore();

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

        // Byte limit override (0 = auto from profile)
        public int TelemetryByteLimitOverride { get; set; } = 0;

        // Upload the .mzdash dashboard to the wheel on every telemetry start.
        // PitHouse does this on every connection — the wheel may require it.
        public bool TelemetryUploadDashboard { get; set; } = false;

        // Download dashboards from the wheel when it reports them.
        public bool TelemetryDownloadDashboard { get; set; } = false;

        // Legacy tier-def variant setting (pre-MozaFirmwareEra). Migrated into
        // TelemetryWheelEra on first load (see MozaPlugin.ApplyTelemetrySettings).
        // Kept around so older saved settings continue to round-trip during migration.
        //   0 = URL-based subscription (CSP-style) → Era2024
        //   2 = Compact numeric (VGS-style)        → Era2025
        // -1 = sentinel meaning "migrated, ignore".
        public int TelemetryProtocolVersion { get; set; } = -1;

        // Legacy MozaFirmwareEra serialization slot. Older builds wrote the
        // firmware-era enum here as an int (Auto=0, TierDefV2_Upload8B=1,
        // TierDefV2_Upload6B=2, TierDefV0_Upload6B=4, TierDefV2_Type02=5).
        // We capture that int by mapping the JSON key "TelemetryFirmwareEra"
        // into this property; ApplyTelemetrySettings drains it into
        // TelemetryWheelEra and clears to -1 to mark as migrated.
        // Sentinel -1 = no legacy value present (fresh install or already migrated).
        [Newtonsoft.Json.JsonProperty("TelemetryFirmwareEra")]
        public int TelemetryFirmwareEraLegacy { get; set; } = -1;

        // Wheel firmware era. Drives every wire-protocol axis (tier-def
        // session, encoding, preamble policy, blind-retransmit, upload
        // header) via Telemetry/EraPolicy.cs. Default Auto probes the wheel
        // at session start and picks Era2024/Era2025/Era2026 from catalog
        // presence + wheel-model identity.
        public MozaWheelEra TelemetryWheelEra { get; set; }
            = MozaWheelEra.Auto;

        // Greenfield telemetry pipeline toggle (Phase 5 of the refactor).
        // false → existing Telemetry/TelemetrySender path.
        // true (default for Phase 7 live testing) → new Telemetry2/MozaTelemetryHost path.
        // Byte-diff verified against PitHouse captures for tier-def emission and structurally
        // fixes the dashboard-switch bug. Flip to false here when reverting to old pipeline.
        public bool UseNewTelemetryPipeline { get; set; } = false;

        // Telemetry send rate in Hz
        public int TelemetrySendRateHz { get; set; } = 20;

        // Whether to send the 0x40/28:02 telemetry mode frame periodically
        public bool TelemetrySendModeFrame { get; set; } = true;

        // Whether to send the 0x2D/F5:31 sequence counter to the base (~30 Hz)
        public bool TelemetrySendSequenceCounter { get; set; } = true;

        // Per-dashboard user channel mappings. Outer key = DashboardProfileStore.GetDashboardKey
        // (e.g. "builtin:Formula 1" or "file:custom.mzdash:a1b2c3d4"). Inner key =
        // channel URL (e.g. "v1/gameData/Rpm"). Value = SimHub property path
        // (e.g. "DataCorePlugin.GameData.Rpms"). Empty value clears the override.
        public Dictionary<string, Dictionary<string, string>> TelemetryChannelMappings { get; set; }
            = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // Per-wheel-model setting slots. Keyed by wheel model name (from data.WheelModelName).
        // The flat Wheel* properties above represent the CURRENTLY-ACTIVE wheel; on save we
        // mirror them into the slot for the active model, and on wheel-model-detected we load
        // the slot back into the flat properties. This keeps each physical wheel model's
        // brightness/mode/input settings isolated when multiple SimHub device extensions exist.
        public Dictionary<string, PerWheelSlot> PerWheelSlots { get; set; }
            = new Dictionary<string, PerWheelSlot>(StringComparer.OrdinalIgnoreCase);

        // Guards every read/write on PerWheelSlots. Serial-reader thread
        // detects a wheel and calls LoadSlotIntoActive while the UI thread
        // can be calling MirrorActiveToSlot from SaveSettings — Dictionary<>
        // is not safe for concurrent writers and corrupts buckets.
        // Newtonsoft serialization touches the dictionary outside these
        // helpers; SimHub serializes from the UI thread on shutdown so the
        // window with the serial reader is small but not zero.
        private readonly object _slotsLock = new object();

        /// <summary>Get the slot for a model, creating one on first access.</summary>
        public PerWheelSlot GetOrCreateSlot(string? modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return new PerWheelSlot();
            lock (_slotsLock)
            {
                if (!PerWheelSlots.TryGetValue(modelName!, out var slot))
                {
                    slot = new PerWheelSlot();
                    PerWheelSlots[modelName!] = slot;
                }
                return slot;
            }
        }

        /// <summary>Copy the flat Wheel* fields into the slot for <paramref name="modelName"/>.</summary>
        public void MirrorActiveToSlot(string? modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return;
            lock (_slotsLock)
            {
                if (!PerWheelSlots.TryGetValue(modelName!, out var slot))
                {
                    slot = new PerWheelSlot();
                    PerWheelSlots[modelName!] = slot;
                }
                slot.WheelTelemetryMode     = WheelTelemetryMode;
                slot.WheelIdleEffect        = WheelIdleEffect;
                slot.WheelButtonsIdleEffect = WheelButtonsIdleEffect;
                slot.WheelPaddlesMode       = WheelPaddlesMode;
                slot.WheelClutchPoint       = WheelClutchPoint;
                slot.WheelKnobMode          = WheelKnobMode;
                slot.WheelStickMode         = WheelStickMode;
                slot.WheelRpmIndicatorMode  = WheelRpmIndicatorMode;
                slot.WheelRpmDisplayMode    = WheelRpmDisplayMode;
                slot.WheelRpmBrightness     = WheelRpmBrightness;
                slot.WheelButtonsBrightness = WheelButtonsBrightness;
                slot.WheelFlagsBrightness   = WheelFlagsBrightness;
                slot.WheelESRpmBrightness   = WheelESRpmBrightness;
                slot.WheelRpmBlinkColors    = WheelRpmBlinkColors;
                slot.WheelKnobBackgroundColors = WheelKnobBackgroundColors;
                slot.WheelKnobPrimaryColors    = WheelKnobPrimaryColors;
                slot.WheelKnobRingColors       = WheelKnobRingColors;
                slot.WheelKnobRingBrightness   = WheelKnobRingBrightness;
            }
        }

        /// <summary>Copy the slot for <paramref name="modelName"/> into the flat Wheel* fields.</summary>
        public void LoadSlotIntoActive(string? modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return;
            lock (_slotsLock)
            {
                if (!PerWheelSlots.TryGetValue(modelName!, out var slot)) return;
                WheelTelemetryMode     = slot.WheelTelemetryMode;
                WheelIdleEffect        = slot.WheelIdleEffect;
                WheelButtonsIdleEffect = slot.WheelButtonsIdleEffect;
                WheelPaddlesMode       = slot.WheelPaddlesMode;
                WheelClutchPoint       = slot.WheelClutchPoint;
                WheelKnobMode          = slot.WheelKnobMode;
                WheelStickMode         = slot.WheelStickMode;
                WheelRpmIndicatorMode  = slot.WheelRpmIndicatorMode;
                WheelRpmDisplayMode    = slot.WheelRpmDisplayMode;
                WheelRpmBrightness     = slot.WheelRpmBrightness;
                WheelButtonsBrightness = slot.WheelButtonsBrightness;
                WheelFlagsBrightness   = slot.WheelFlagsBrightness;
                WheelESRpmBrightness   = slot.WheelESRpmBrightness;
                WheelRpmBlinkColors    = slot.WheelRpmBlinkColors;
                WheelKnobBackgroundColors = slot.WheelKnobBackgroundColors;
                WheelKnobPrimaryColors    = slot.WheelKnobPrimaryColors;
                WheelKnobRingColors       = slot.WheelKnobRingColors;
                WheelKnobRingBrightness   = slot.WheelKnobRingBrightness;
            }
        }
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
    }
}
