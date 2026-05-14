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
        /// Number of physical rotary encoders on this wheel that have configurable
        /// background + primary LED ring colors. Protocol group indices are 0..KnobCount-1.
        /// 0 when the wheel has no configurable knob colors.
        /// </summary>
        public int KnobCount { get; }

        /// <summary>
        /// Per-knob individual LED counts for the Group 3 (Rotary) ring LEDs.
        /// Array length == KnobCount. Null when the wheel has no per-LED ring control.
        /// CS Pro: [12,12,12,12] (48 total). KS Pro: [12,12,8,12,12] (56 total).
        /// </summary>
        public int[]? KnobRingLeds { get; }

        /// <summary>Total number of Group 3 ring LEDs across all knobs.</summary>
        public int KnobRingLedTotal { get; }

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

        /// <summary>Default for unknown models — 10 RPM, 14 buttons, no flags, contiguous, no knobs, display unknown, no brow.</summary>
        public static readonly WheelModelInfo Default = new(10, 14, false, null, 0, null, hasDisplay: null, browSegmentSize: 0);

        /// <summary>
        /// Known wheel models, ordered longest prefix first for correct disambiguation.
        /// Model names are 16-byte null-padded ASCII strings from the wheel firmware
        /// (group 0x07, command 0x01). Examples: "GS V2P", "CS V2.1", "VGS".
        /// FriendlyName is used for the SimHub device profile display name.
        /// </summary>
        internal static readonly (string Prefix, string FriendlyName, WheelModelInfo Info)[] KnownModels =
        {
            ("GS V2P",  "GS V2 Pro",  new WheelModelInfo(10, 10, false, null, 0, hasDisplay: false)),
            ("CS V2.1", "CS V2",      new WheelModelInfo(10, 6,  false, new[] { 0, 1, 3, 6, 8, 9 }, 0, hasDisplay: false)),
            // CS Pro / KS Pro expose rotary encoders with configurable background +
            // primary colors (protocol groups 0..KnobCount-1 via cmd 0x27).
            // Group 3 (Rotary) provides per-LED ring control: 12 LEDs/knob on CS Pro,
            // 12/12/8/12/12 on KS Pro (knob 3 has 8 LEDs).
            ("W17",     "CS Pro",     new WheelModelInfo(16, 8,  false, null, 4, new[] { 12, 12, 12, 12 }, hasDisplay: true,  browSegmentSize: 3)),
            // KS Pro 3/12/3 LED strip appears to live entirely in group 0 (Shift/RPM),
            // not split across RPM + Meter flag sub-device. Driving all 18 as one RPM strip.
            ("W18",     "KS Pro",     new WheelModelInfo(18, 14, false, null, 5, new[] { 12, 12, 8, 12, 12 }, hasDisplay: true,  browSegmentSize: 3)),
            ("KS",      "KS",         new WheelModelInfo(10, 10, false, null, 0, hasDisplay: false)),
            ("W13",     "FSR V2",     new WheelModelInfo(16, 10, false, null, 0, hasDisplay: true,  browSegmentSize: 3)),  // firmware reports "W13" for FSR V2
            ("VGS",     "Vision GS",  new WheelModelInfo(10, 8,  false, null, 0, hasDisplay: true)),
            ("TSW",     "TSW",        new WheelModelInfo(10, 14, false, null, 0, hasDisplay: false)),
            // RS V2 referenced in Telemetry/EraPolicy.cs:187 but not yet measured.
            // LED dimensions are best-guess from the Default profile; HasDisplay=false
            // per user. RPM/button counts should be confirmed against a real RS V2
            // and tightened in a follow-up.
            ("RS V2",   "RS V2",      new WheelModelInfo(10, 14, false, null, 0, hasDisplay: false)),
        };

        public WheelModelInfo(int rpmLedCount, int buttonLedCount, bool hasFlagLeds, int[]? buttonLedMap, int knobCount = 0, int[]? knobRingLeds = null, bool? hasDisplay = null, int browSegmentSize = 0)
        {
            RpmLedCount = rpmLedCount;
            ButtonLedCount = buttonLedCount;
            HasFlagLeds = hasFlagLeds;
            ButtonLedMap = buttonLedMap;
            KnobCount = knobCount;
            KnobRingLeds = knobRingLeds;
            int total = 0;
            if (knobRingLeds != null)
                foreach (int n in knobRingLeds) total += n;
            KnobRingLedTotal = total;
            HasDisplay = hasDisplay;
            BrowSegmentSize = browSegmentSize;
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
