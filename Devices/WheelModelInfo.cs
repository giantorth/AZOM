using System;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Describes the physical LED layout for a specific wheel model.
    /// Used to set correct SimHub ButtonsCount, remap non-contiguous button indices,
    /// and show/hide flag LED UI based on the detected wheel.
    /// </summary>
    internal class WheelModelInfo
    {
        /// <summary>Number of physical RPM LEDs on this wheel (center section of the LED strip).</summary>
        public int RpmLedCount { get; }

        /// <summary>Number of physical button LEDs on this wheel.</summary>
        public int ButtonLedCount { get; }

        /// <summary>
        /// Whether this wheel has 6 physical flag LEDs arranged as 3 on the left and 3
        /// on the right of the RPM strip (total physical telemetry LEDs = RpmLedCount + 6).
        /// </summary>
        public bool HasFlagLeds { get; }

        /// <summary>
        /// Maps SimHub button index (0..ButtonLedCount-1) to protocol LED index (0..13).
        /// Null when the mapping is contiguous (protocol index == SimHub index).
        /// </summary>
        public int[]? ButtonLedMap { get; }

        /// <summary>
        /// Window mask for the button-LED bitmask (the 8-byte active+window form,
        /// <see cref="MozaLedDeviceManager.BuildWindowedBitmaskBytes"/>). PitHouse
        /// drives wheels with a non-contiguous <see cref="ButtonLedMap"/> using
        /// window = the full set of mapped protocol indices; CS V2.1 firmware leaves
        /// its buttons dark unless this window is present (verified in idk.pcapng →
        /// 0x034B). Contiguous-button wheels (CS Pro, VGS …) are driven with window
        /// = 0 by PitHouse (verified in the CS Pro captures), so they return 0 and
        /// keep their existing behaviour.
        /// </summary>
        public int ButtonWindowMask
        {
            get
            {
                if (ButtonLedMap == null) return 0;
                int mask = 0;
                foreach (int idx in ButtonLedMap) mask |= (1 << idx);
                return mask;
            }
        }

        /// <summary>
        /// Number of physical rotary encoders on this wheel that have configurable
        /// background + primary LED ring colors. Protocol group indices are 0..KnobCount-1.
        /// 0 when the wheel has no configurable knob colors.
        /// </summary>
        public int KnobCount { get; }

        /// <summary>
        /// Number of physical rotary encoders whose input mode (BUTTON vs KNOB,
        /// <c>wheel-knob-signal-mode{i}</c> / sub-id <c>[42, i]</c>) is configurable.
        /// Distinct from <see cref="KnobCount"/>, which counts only the encoders that
        /// also have addressable LED rings — most rims have encoders and no knob LEDs.
        /// <para><c>-1</c> = not catalogued: fall back to sweep discovery (the reads go
        /// out for indices 0..4 and the answers drive the UI). That fallback OVER-REPORTS
        /// — firmware answers every index 0..4 regardless of how many encoders exist
        /// (owner-confirmed on the KS, which answered all five with three knobs) — so a
        /// model is only accurate once its real count is recorded here. Defaults to
        /// <see cref="KnobCount"/> when that is non-zero, since an encoder with an LED
        /// ring is by definition an encoder.</para>
        /// <para><c>0</c> = confirmed to have no configurable encoders; suppresses the
        /// reads and the UI section entirely.</para>
        /// </summary>
        public int KnobEncoderCount { get; }

        /// <summary>
        /// Per-knob individual LED counts for the Group 3 (Rotary) ring LEDs.
        /// Array length == KnobCount. Null when the wheel has no per-LED ring control.
        /// CS Pro: [12,12,12,12] (48 total). KS Pro: [12,12,8,12,12] (56 total).
        /// </summary>
        public int[]? KnobRingLeds { get; }

        /// <summary>Total number of Group 3 ring LEDs across all knobs.</summary>
        public int KnobRingLedTotal { get; }

        /// <summary>
        /// Maps the firmware per-knob signal-mode index (the <c>i</c> in
        /// <c>wheel-knob-signal-mode{i}</c> / sub-id <c>[42, i]</c>) to the
        /// logical knob index used everywhere else (LED rings, colours, UI
        /// position): <c>KnobSignalModeOrder[firmwareIndex] = logicalKnob</c>.
        /// Null = identity (firmware index == logical knob).
        /// Some wheels address knob signal modes in a different order than their
        /// LED groups (which are in physical order): CS Pro firmware 0..3 map to
        /// physical knobs 1,4,3,2; KS Pro firmware 0..4 map to physical knobs
        /// 1,5,4,2,3.
        /// </summary>
        public int[]? KnobSignalModeOrder { get; }

        /// <summary>
        /// Whether this wheel model has a built-in display sub-device that can render
        /// dashboards. <c>true</c> = drive dashboard telemetry without waiting for the
        /// runtime probe; <c>false</c> = never drive dashboard telemetry (skip the
        /// sender entirely); <c>null</c> = unknown model, fall back to the runtime
        /// display probe (<see cref="Telemetry.TelemetrySender.DisplayDetected"/>).
        /// </summary>
        public bool? HasDisplay { get; }

        /// <summary>
        /// Size of the "brow" segment carved out of the LogicalTelemetryLeds strip.
        /// 0 = no brow (emit empty Segments array). When &gt;0, SimHub renders the
        /// first/last N LEDs as a distinct segment from the main RPM bar. Convention:
        /// any wheel with <see cref="RpmLedCount"/> &gt;= 16 ships a 3-LED brow.
        /// </summary>
        public int BrowSegmentSize { get; }

        /// <summary>
        /// Whether this wheel has the sleep-light / idle-light feature (mode 0x20,
        /// timeout 0x21, speed 0x22, color 0x24 on group 0x3F dev 0x17). Modern
        /// wheels support it; the legacy bare-"CS" prefix (RPM-only, no buttons /
        /// knobs / flags) does NOT — pushing those writes confuses its firmware
        /// into a Table 8 read-fail loop that makes the wheel periodically stop
        /// answering presence polls. Default <c>true</c>; flip to <c>false</c>
        /// on models known to lack the feature.
        /// </summary>
        public bool HasSleepLight { get; }

        /// <summary>
        /// Maximum live LED-update wire rate in frames/sec; <c>0</c> = unlimited
        /// (default — SimHub's 60 Hz tick drives the stream). The legacy bare-"CS"
        /// rim is wireless and wedges its param manager when the RPM stream is
        /// pushed at the full radio cadence (~3 ms gaps), so it is capped. The cap
        /// coalesces — the latest colour state still goes out, just no faster than
        /// the limit.
        /// </summary>
        public int MaxLedFps { get; }

        /// <summary>
        /// Whether this wheel has the display-rotation feature: an internal IMU
        /// that lets the wheel counter-rotate its dashboard so it stays upright as
        /// the rim turns. When <c>true</c>, the plugin exposes the rotation-mode
        /// control (off / smooth / immediate) and pushes the mode via the
        /// session-0x02 FF property record (<c>kind=5</c>,
        /// <see cref="Sessions.SessionPropertyPushBuilder.KindDashDisplayRotation"/>).
        /// Only the VGS (Vision GS) is known to have the sensor; other display
        /// wheels ignore the push. Default <c>false</c>; flip to <c>true</c> on any
        /// model confirmed to carry the rotation IMU.
        /// </summary>
        public bool SupportsDisplayRotation { get; }

        /// <summary>
        /// Drive RPM LEDs the PitHouse "old rim" way: a fixed colour palette
        /// (<c>0x19</c>, sent only on palette change) + a streamed lit-state
        /// bitmask (<c>wheel-send-rpm-telemetry</c> 0x1a and the old-protocol
        /// <c>wheel-old-send-telemetry</c> 0x41 <c>fd de</c>). The legacy bare-"CS"
        /// rim wedges its param manager (Table 8 read-fail storm) when the
        /// per-frame full-colour stream (the default new-wheel path) hammers its
        /// LED colour buffer — verified against <c>cs v2(1).pcapng</c> (wheel_wnfw),
        /// where PitHouse sends colour frames ~0.5/s and the old bitmask ~68/s.
        /// Default <c>false</c> (modern wheels keep the per-frame colour path).
        /// </summary>
        public bool UsesLegacyRpmTelemetry { get; }

        /// <summary>
        /// Returns the Group 3 start index for the given knob (0-based).
        /// E.g. CS Pro knob 2 → 12 (skip knob 0's 12 LEDs).
        /// </summary>
        public int KnobRingStartIndex(int knob)
        {
            if (KnobRingLeds == null || knob <= 0) return 0;
            int offset = 0;
            for (int i = 0; i < knob && i < KnobRingLeds.Length; i++)
                offset += KnobRingLeds[i];
            return offset;
        }

        /// <summary>Default for unknown models — 10 RPM, 14 buttons, no flags, contiguous, no knobs, display unknown, no brow.
        /// hasSleepLight:false — never push sleep-light params (wheel-idle-mode/timeout/speed/color) to a wheel we
        /// haven't positively identified. The legacy bare-"CS" rim doesn't implement them, and reading/writing them
        /// drives its firmware into a Table 8 read-fail storm that kills identity readback. A wheel that does support
        /// the feature gets it once it's added to <see cref="KnownModels"/> with hasSleepLight:true.</summary>
        public static readonly WheelModelInfo Default = new(10, 14, false, null, 0, null, hasDisplay: null, browSegmentSize: 0, hasSleepLight: false);

        /// <summary>
        /// Known wheel models, ordered longest prefix first for correct disambiguation.
        /// Model names are 16-byte null-padded ASCII strings from the wheel firmware,
        /// read via the model-name query (group 0x07, command 0x01) documented in
        /// docs/how-to-query-device-type.md — the wheel replies with a 22-byte frame
        /// whose bytes 6..21 are the ASCII identifier. Examples: "GS V2P", "CS V2.1",
        /// "VGS". FriendlyName is used for the SimHub device profile display name.
        /// Matching is StartsWith(prefix, OrdinalIgnoreCase), longest-first, so a
        /// firmware string carrying a trailing suffix still resolves to its
        /// registered prefix.
        /// </summary>
        internal static readonly (string Prefix, string FriendlyName, WheelModelInfo Info)[] KnownModels =
        {
            ("GS V2P",  "GS V2 Pro",  new WheelModelInfo(10, 10, false, null, 0, hasDisplay: false, knobEncoderCount: 5)),
            // Some GS V2 Pro firmware variants report the bare prefix "GS"
            // instead of "GS V2P" (observed on RS21-D02-MC GW). Same physical
            // layout — 10 RPM + 10 button LEDs, no display. Must come after
            // "GS V2P" so the longer prefix matches first when present.
            ("GS",      "GS V2 Pro",  new WheelModelInfo(10, 10, false, null, 0, hasDisplay: false, knobEncoderCount: 5)),
            ("CS V2.1", "CS V2",      new WheelModelInfo(10, 6,  false, new[] { 0, 1, 3, 6, 8, 9 }, 0, hasDisplay: false, knobEncoderCount: 2)),
            // CS Pro / KS Pro expose rotary encoders with configurable background +
            // primary colors (protocol groups 0..KnobCount-1 via cmd 0x27).
            // Group 3 (Rotary) provides per-LED ring control: 12 LEDs/knob on CS Pro,
            // 12/12/8/12/12 on KS Pro (knob 3 has 8 LEDs).
            ("W17",     "CS Pro",     new WheelModelInfo(16, 8,  false, null, 4, new[] { 12, 12, 12, 12 }, hasDisplay: true,  browSegmentSize: 3, knobSignalModeOrder: new[] { 0, 3, 2, 1 })),
            // KS Pro 3/12/3 LED strip appears to live entirely in group 0 (Shift/RPM),
            // not split across RPM + Meter flag sub-device. Driving all 18 as one RPM strip.
            ("W18",     "KS Pro",     new WheelModelInfo(18, 10, false, null, 5, new[] { 12, 12, 8, 12, 12 }, hasDisplay: true,  browSegmentSize: 3, knobSignalModeOrder: new[] { 0, 4, 3, 1, 2 })),
            // KS: 3 rotary encoders, none with LED rings (knobCount stays 0). The
            // encoder count is explicit because the firmware answers ALL FIVE
            // wheel-knob-signal-mode indices regardless of how many knobs exist, so
            // sweep discovery reports 5 — owner-confirmed 2026-08-25.
            ("KS",      "KS",         new WheelModelInfo(10, 10, false, null, 0, hasDisplay: false, knobEncoderCount: 3)),
            // Lamborghini Revuelto (firmware "W11"): screenless new-protocol wheel,
            // 0 RPM LEDs + 16 dimming-only backlit buttons (no per-button RGB — the
            // wheel ignores the colour bytes, like ES).
            ("W11",     "Lamborghini Revuelto", new WheelModelInfo(0, 16, false, null, 0, hasDisplay: false)),
            // MOZA × Porsche Mission R (firmware "W05"): display wheel, 4 backlit
            // buttons, no RPM LEDs (per user). Buttons are dimming-only backlit
            // (like the Revuelto/ES, no per-button RGB).
            ("W05",     "× Porsche Mission R", new WheelModelInfo(0, 4, false, null, 0, hasDisplay: true, knobEncoderCount: 2)),
            // ESSENZA SCV12 (firmware "W06"): display wheel, 10 RPM LEDs in a
            // 1/8/1 brow arrangement (1-LED brow each side, 8 in the middle —
            // the same scheme as the 3/10/3 wheels, smaller), no button LEDs.
            // Per user.
            ("W06",     "ESSENZA SCV12", new WheelModelInfo(10, 0, false, null, 0, hasDisplay: true, browSegmentSize: 1, knobEncoderCount: 4)),
            ("W13",     "FSR V2",     new WheelModelInfo(16, 10, false, null, 0, hasDisplay: true,  browSegmentSize: 3, knobEncoderCount: 5)),  // firmware reports "W13" for FSR V2
            // FSR V1 display wheel (box name "FSR1"): firmware reports model-name
            // "FSR", hw "RS21-D03-HW FW-C", sw "RS21-D03-MC FW". A DISTINCT, older
            // product from FSR V2 ("W13"). It does NOT speak the standard tier-def
            // telemetry protocol — it renders its screen from an undocumented
            // group-0x42 fixed-schema value push (see Telemetry/Fsr1DisplayEmitter
            // and docs/protocol/devices/wheel-0x17.md § Group 0x42). HasDisplay=false
            // deliberately keeps the standard dashboard pipeline, the 0x43-wrapped
            // display probe, and the display-wedge reconnect watchdog all OFF (the
            // wheel never answers that probe); the 0x42 sender is started instead via
            // the explicit MozaPlugin.IsFsr1DisplayWheel bypass. The 10 RPM + 10
            // button LEDs use the standard group-0x3F SimHub LED path unchanged.
            // knobEncoderCount 5 is owner-confirmed, but this rim reads NOTHING back
            // (BuildNewWheelLedReadCommands returns empty for FSR1 — its param store
            // wedges), so wheel-knob-signal-mode never answers and the UI cannot know
            // whether the firmware supports per-knob mode. It therefore offers the
            // legacy wheel-wide All-Rotaries selector rather than five chips. Writes
            // still go out on user edit, which is PitHouse's own model for this rim.
            ("FSR",     "FSR V1",     new WheelModelInfo(10, 10, false, null, 0, hasDisplay: false, knobEncoderCount: 5)),
            // VGS (Vision GS): has the display-rotation IMU — supportsDisplayRotation:true
            // exposes the off/smooth/immediate rotation-mode control (session-0x02 FF
            // kind=5 push). Verified from VGS PitHouse captures.
            ("VGS",     "Vision GS",  new WheelModelInfo(10, 8,  false, null, 0, hasDisplay: true, supportsDisplayRotation: true, knobEncoderCount: 3)),
            ("TSW",     "TSW",        new WheelModelInfo(10, 14, false, null, 0, hasDisplay: false, knobEncoderCount: 0)),
            // RS V2: 10 RPM + 10 button LEDs (owner-confirmed, 2026-07-30),
            // screenless. The real RS V2 self-reports "RS Leather # W00"
            // (hw "RS21-W00-HW SM-C", dev-type 01 02 09 07) and resolves via
            // the "RS Leather" rim entry below; this prefix stays for firmware
            // that reports the literal product name.
            ("RS V2",   "RS V2",      new WheelModelInfo(10, 10, false, null, 0, hasDisplay: false, knobEncoderCount: 2)),
            // RS round / D-shape family (MOZA-sourced names): 10 RGB RPM +
            // 10 button LEDs, screenless, sleep-light off (safe default for
            // unmeasured rims). The real RS V2 reports "RS Leather # W00"
            // ("# W00" = hw module code, tolerated by the StartsWith match) and
            // resolves via the "RS Leather" prefix — new-protocol, 10/10
            // owner-confirmed 2026-07-30; the other rims are assumed to share
            // the layout. No thumbnail art embedded yet.
            ("RS D-Shape Alcantara", "RS Alcantara D-Shape", new WheelModelInfo(10, 10, false, null, 0, hasDisplay: false, hasSleepLight: false)),
            ("RS D-Shape Leather",   "RS Leather D-Shape",   new WheelModelInfo(10, 10, false, null, 0, hasDisplay: false, hasSleepLight: false)),
            ("RS Alcantara",         "RS Alcantara Round",   new WheelModelInfo(10, 10, false, null, 0, hasDisplay: false, hasSleepLight: false)),
            // knobEncoderCount 2 is owner-confirmed for the RS V2, which resolves HERE
            // (it self-reports "RS Leather # W00"), not via the "RS V2" prefix above.
            // The other three RS rims are left uncatalogued (-1): the row comment says
            // their LED layout is assumed rather than measured, so their knob count is
            // unmeasured too — they fall back to sweep discovery and over-report at 5
            // until someone counts them.
            ("RS Leather",           "RS Leather Round",     new WheelModelInfo(10, 10, false, null, 0, hasDisplay: false, hasSleepLight: false, knobEncoderCount: 2)),
            // Original CS (predecessor to CS V2 / CS V2.1) — firmware reports the
            // bare prefix "CS" with no version suffix. 10 RGB RPM LEDs, no button
            // / flag / knob LEDs, no display. Must come after "CS V2.1" so the
            // longer prefix is matched first for newer firmware reports.
            // hasSleepLight=false: pushing wheel-idle-mode/timeout/speed/color at
            // this wheel triggers a Table 8 read-fail storm in its firmware that
            // makes it intermittently unresponsive.
            ("CS",      "CS",         new WheelModelInfo(10, 0,  false, null, 0, hasDisplay: false, hasSleepLight: false, usesLegacyRpmTelemetry: true, knobEncoderCount: 0)),
            // ESX wheel — the entry rim's ESX variant; same old-protocol topology
            // (wheelbase module at internal id 0x18) and the same capabilities as
            // the ES. Per docs/how-to-query-device-type.md the firmware reports
            // "RSX" — "ESX" is NOT a real identifier — so the PREFIX is "RSX" while
            // the FriendlyName stays "ESX" (device "MOZA ESX", art keyed to
            // Thumbnails/RSX.png = the ESX image). "RSX" does not start with "ES",
            // so it can never collide with the ES entry below. The doc's other ESX
            // strings ("ES UART", "ESX UART") only appear over a direct-UART link
            // the plugin doesn't use; over the wheelbase they'd resolve to the "ES"
            // entry (identical 10-RPM / screenless layout). GUID is UUID-v5
            // auto-generated from "RSX"; 10 RGB RPM LEDs, no button LEDs.
            // knobEncoderCount 0 follows the ES (owner-confirmed none) per this row's
            // "same capabilities as the ES" note. Inert either way: both are
            // old-protocol and never reach the knob read/UI paths.
            ("RSX",     "ESX",        new WheelModelInfo(10, 0,  false, null, 0, hasDisplay: false, hasSleepLight: false, knobEncoderCount: 0)),
            // ES — MOZA's entry wheel, integrated into the wheelbase as a module at
            // internal id 0x18 (firmware model-name "ES", hw "RS21-D05-HW SM-C").
            // Old-protocol RPM only: 10 RGB RPM LEDs driven via the wheel-old-rpm-*
            // path; no button / flag / knob LEDs, no display. hasDisplay:false keeps
            // the dashboard pipeline + 0x43 display probe OFF (screenless);
            // hasSleepLight:false avoids the Table-8 read-fail storm legacy rims hit
            // on sleep-light writes. Button-LED count is conservative (0) — refine
            // from a live capability read if ES exposes addressable button LEDs.
            ("ES",      "ES",         new WheelModelInfo(10, 0,  false, null, 0, hasDisplay: false, hasSleepLight: false, knobEncoderCount: 0)),
        };

        public WheelModelInfo(int rpmLedCount, int buttonLedCount, bool hasFlagLeds, int[]? buttonLedMap, int knobCount = 0, int[]? knobRingLeds = null, bool? hasDisplay = null, int browSegmentSize = 0, bool hasSleepLight = true, int maxLedFps = 0, bool usesLegacyRpmTelemetry = false, int[]? knobSignalModeOrder = null, bool supportsDisplayRotation = false, int knobEncoderCount = -1)
        {
            SupportsDisplayRotation = supportsDisplayRotation;
            RpmLedCount = rpmLedCount;
            ButtonLedCount = buttonLedCount;
            HasFlagLeds = hasFlagLeds;
            ButtonLedMap = buttonLedMap;
            KnobCount = knobCount;
            // An LED-ringed knob is an encoder, so knobCount is the floor when no
            // explicit encoder count is recorded. -1 stays -1 for rims with neither.
            KnobEncoderCount = knobEncoderCount >= 0 ? knobEncoderCount
                             : (knobCount > 0 ? knobCount : -1);
            KnobRingLeds = knobRingLeds;
            int total = 0;
            if (knobRingLeds != null)
                foreach (int n in knobRingLeds) total += n;
            KnobRingLedTotal = total;
            HasDisplay = hasDisplay;
            BrowSegmentSize = browSegmentSize;
            HasSleepLight = hasSleepLight;
            MaxLedFps = maxLedFps;
            UsesLegacyRpmTelemetry = usesLegacyRpmTelemetry;
            KnobSignalModeOrder = knobSignalModeOrder;
        }

        /// <summary>
        /// Firmware signal-mode index (<c>wheel-knob-signal-mode{i}</c>) that
        /// controls the given logical knob — inverse of <see cref="KnobSignalModeOrder"/>.
        /// Identity when no reorder is defined. Used on the write path so a UI
        /// edit on logical knob N reaches the physical knob whose LED ring is N.
        /// </summary>
        public int SignalModeFirmwareIndex(int logicalKnob)
        {
            if (KnobSignalModeOrder == null) return logicalKnob;
            int i = Array.IndexOf(KnobSignalModeOrder, logicalKnob);
            return i >= 0 ? i : logicalKnob;
        }

        /// <summary>
        /// Logical knob (LED/colour/UI order) controlled by a firmware
        /// signal-mode index. Identity when no reorder is defined. Used on the
        /// read path so a <c>wheel-knob-signal-mode{i}</c> response lands in the
        /// slot matching the knob the user sees. See <see cref="KnobSignalModeOrder"/>.
        /// </summary>
        public int SignalModeLogicalKnob(int firmwareIndex)
        {
            if (KnobSignalModeOrder == null
                || firmwareIndex < 0 || firmwareIndex >= KnobSignalModeOrder.Length)
                return firmwareIndex;
            return KnobSignalModeOrder[firmwareIndex];
        }

        /// <summary>
        /// Resolve a wheel model info from its firmware model name string.
        /// Matches the first known prefix (longest-first ordering).
        /// Returns <see cref="Default"/> for unrecognized models.
        /// </summary>
        public static WheelModelInfo FromModelName(string modelName)
        {
            if (string.IsNullOrEmpty(modelName))
                return Default;

            foreach (var (prefix, _, info) in KnownModels)
            {
                if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return info;
            }

            return Default;
        }

        /// <summary>
        /// Extract the matching known prefix from a firmware model name, or return
        /// the full model name if it doesn't match any known prefix (used as-is for
        /// device naming and GUID generation for unknown wheels).
        /// </summary>
        public static string ExtractPrefix(string modelName)
        {
            if (string.IsNullOrEmpty(modelName))
                return modelName;

            foreach (var (prefix, _, _) in KnownModels)
            {
                if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return prefix;
            }

            return modelName;
        }

        /// <summary>
        /// Get the friendly display name for a model prefix. Returns the prefix itself
        /// for unknown models.
        /// </summary>
        public static string GetFriendlyName(string modelPrefix)
        {
            foreach (var (prefix, friendlyName, _) in KnownModels)
            {
                if (prefix.Equals(modelPrefix, StringComparison.OrdinalIgnoreCase))
                    return friendlyName;
            }

            return modelPrefix;
        }

        /// <summary>
        /// Returns true if the given 0-based protocol index has a physical button LED
        /// on this wheel model. Used by the settings UI to show/hide individual swatches.
        /// </summary>
        public bool IsButtonActive(int protocolIndex)
        {
            if (ButtonLedMap != null)
            {
                foreach (int mapped in ButtonLedMap)
                {
                    if (mapped == protocolIndex)
                        return true;
                }
                return false;
            }

            return protocolIndex >= 0 && protocolIndex < ButtonLedCount;
        }
    }
}
