using System.Collections.Generic;
using System.Linq;
using MozaPlugin.Telemetry.TestMode;

namespace MozaPlugin.Telemetry.Dashboard
{
    public class ChannelDefinition
    {
        /// <summary>Short display name, e.g. "Brake".</summary>
        public string Name { get; set; } = "";

        /// <summary>Full Moza telemetry URL, e.g. "v1/gameData/Brake".</summary>
        public string Url { get; set; } = "";

        /// <summary>Compression type string, e.g. "float_001".</summary>
        public string Compression { get; set; } = "";

        /// <summary>Bit width for this channel.</summary>
        public int BitWidth { get; set; }

        /// <summary>
        /// How to read the value from SimHub GameData.
        /// One of the SimHubProperty enum values defined in DashboardProfileStore.
        /// Used as the fallback when <see cref="SimHubProperty"/> is empty.
        /// </summary>
        public SimHubField SimHubField { get; set; } = SimHubField.Zero;

        /// <summary>
        /// Full SimHub property path (e.g. "DataCorePlugin.GameData.Rpms") resolved
        /// per-frame via <c>PluginManager.GetPropertyValue</c>. Empty = use SimHubField fallback.
        /// Populated from defaults at load time; user overrides persisted via settings.
        /// </summary>
        public string SimHubProperty { get; set; } = "";

        /// <summary>
        /// Multiplier applied to the resolved SimHub property value before encoding.
        /// Used to reconcile SimHub's unit convention with the channel's compression
        /// expectation (e.g. Throttle/Brake are 0–100 in SimHub but <c>float_001</c>
        /// expects 0–1, so the default scale is 0.01).
        /// </summary>
        public double SimHubPropertyScale { get; set; } = 1.0;

        /// <summary>Telemetry tier (ms update interval, e.g. 30, 500, 2000).</summary>
        public int PackageLevel { get; set; } = 30;

        /// <summary>
        /// Per-channel synthetic value generator used while
        /// <see cref="TelemetrySender.TestMode"/> is active. Resolved at
        /// dashboard-load time by <see cref="TestSignalCatalog.Resolve"/>
        /// from overrides + Telemetry.json range + compression-table fallback.
        /// Default sweeps 0..1 (safe for any compression).
        /// </summary>
        public TestSignal TestSignal { get; set; } = TestSignal.Sweep(0, 1);
    }

    public class DashboardProfile
    {
        public string Name { get; set; } = "";
        public List<ChannelDefinition> Channels { get; set; } = new List<ChannelDefinition>();
        public int TotalBits { get; set; }
        public int TotalBytes { get; set; }

        /// <summary>Telemetry tier this profile covers (ms interval).</summary>
        public int PackageLevel { get; set; } = 30;

        /// <summary>
        /// Wire flag byte for this tier in the V2 tier-def. Zero = use the
        /// tier's index in <c>MultiStreamProfile.Tiers</c> (legacy behavior).
        /// Non-zero = use this explicit flag (set by per-widget mzdash parse,
        /// where each widget's id maps to its tier-def flag — verified
        /// against PitHouse capture flag bytes 0x1f/0x20/0x21/0x22 matching
        /// widget node IDs in the active mzdash).
        /// </summary>
        public byte FlagByte { get; set; } = 0;

        public override string ToString() =>
            $"{Name} ({Channels.Count} channels, {TotalBytes} bytes)";
    }

    /// <summary>
    /// Concurrent telemetry streams for one dashboard, split by package_level tier.
    /// Tiers are sorted by package_level ascending; flag bytes are FlagByte + tier index.
    /// </summary>
    public class MultiStreamProfile
    {
        public string Name { get; set; } = "";

        /// <summary>
        /// Per-tier profiles, sorted by PackageLevel ascending.
        /// Flag byte offset = index in this list (0, 1, 2, ...).
        /// </summary>
        public List<DashboardProfile> Tiers { get; set; } = new List<DashboardProfile>();

        /// <summary>
        /// String-typed channels referenced by the dashboard (Telemetry.json
        /// <c>compression: "string"</c>). NOT part of <see cref="Tiers"/>: strings
        /// are pushed out-of-band as <c>type=0x05</c> sub-msgs on sess=0x01 (see
        /// <c>docs/protocol/sessions/session-0x01-channel-protocol.md</c>) and
        /// do not appear in the bit-packed value frame. Keyed by URL.
        /// </summary>
        public List<ChannelDefinition> StringChannels { get; set; } = new List<ChannelDefinition>();

        /// <summary>
        /// Number of pages (children) in the dashboard. Used for 7c:27 display config frames.
        /// Defaults to 1 for profiles that don't come from an mzdash file.
        /// </summary>
        public int PageCount { get; set; } = 1;

        public override string ToString()
        {
            var parts = Tiers.Select(t => $"L{t.PackageLevel}:{t.Channels.Count}ch/{t.TotalBytes}B");
            string strs = StringChannels.Count > 0 ? $", strings:{StringChannels.Count}" : "";
            return $"{Name} ({string.Join(", ", parts)}{strs})";
        }
    }

    /// <summary>
    /// Identifies which SimHub game data field supplies a channel's value.
    /// </summary>
    public enum SimHubField
    {
        Zero = 0,           // Unknown / unsupported — always send 0
        SpeedKmh,
        Rpms,
        Gear,               // SimHub: -1=R, 0=N, 1+=forward gears
        Throttle,           // 0–100
        Brake,              // 0–100
        BestLapTimeSeconds,
        CurrentLapTimeSeconds,
        LastLapTimeSeconds,
        DeltaToSessionBest, // seconds (GAP)
        FuelPercent,        // 0–100
        DrsEnabled,         // bool
        ErsPercent,         // 0–100
        TyreWearFrontLeft,  // 0–100
        TyreWearFrontRight,
        TyreWearRearLeft,
        TyreWearRearRight,
        CurrentLap,         // lap counter (1+)
    }
}
