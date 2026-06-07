using System.Collections.Generic;
using System.Linq;

namespace MozaPlugin.Telemetry
{
    /// <summary>Byte encoding of a group-0x42 field. Width fixes the field's
    /// full-scale capability (<see cref="Fsr1FieldDef.CapabilityMax"/>).</summary>
    internal enum Fsr1Encoding { U8, U16_BE, U16_LE, U24_BE }

    /// <summary>How a resolved SimHub value becomes the field's wire integer.</summary>
    internal enum Fsr1FieldKind
    {
        /// <summary>Normalise the source over [InMin,InMax] → [0, FullScale].</summary>
        Scaled,
        /// <summary>Use the source value directly (rounded, clamped to capability).</summary>
        Direct,
        /// <summary>Protocol anchor, not user-mappable: 0x4B when the engine runs, else 0.</summary>
        EngineFlag,
    }

    /// <summary>
    /// One mappable (or anchor) field within an FSR V1 dashboard record. Offsets
    /// are payload-relative (payload[0]=type, [1]=b1, [2]=b2, data from [3]).
    /// </summary>
    internal sealed class Fsr1FieldDef
    {
        public string FieldId = "";
        public string Label = "";
        public int[] Offsets = System.Array.Empty<int>();
        public Fsr1Encoding Encoding = Fsr1Encoding.U8;
        public Fsr1FieldKind Kind = Fsr1FieldKind.Scaled;
        public string DefaultProperty = "";
        public double DefaultInMin = 0;
        public double DefaultInMax = 1;
        /// <summary>Output cap; 0 = use the encoding's full capability.</summary>
        public long FullScale = 0;
        /// <summary>True when the field's semantics are proven; false = raw/experimental slot.</summary>
        public bool Decoded = true;

        /// <summary>Largest value the field's byte width can represent.</summary>
        public long CapabilityMax => Encoding switch
        {
            Fsr1Encoding.U8 => 0xFF,
            Fsr1Encoding.U16_BE => 0xFFFF,
            Fsr1Encoding.U16_LE => 0xFFFF,
            Fsr1Encoding.U24_BE => 0xFFFFFF,
            _ => 0xFF,
        };

        /// <summary>Effective output ceiling (FullScale override or capability).</summary>
        public long OutputMax => FullScale > 0 ? FullScale : CapabilityMax;

        /// <summary>Anchor fields are filled by the protocol, not user-mappable.</summary>
        public bool IsUserMappable => Kind != Fsr1FieldKind.EngineFlag;
    }

    /// <summary>One built-in dashboard = one group-0x42 record type.</summary>
    internal sealed class Fsr1Dashboard
    {
        public byte RecordType;
        public string Key = "";        // settings key, e.g. "type-02"
        public string Label = "";      // UI group header
        public byte PayloadLen;        // wire len byte (type+b1+b2+data)
        public byte LiveB1;            // b1 when populated
        public byte LiveB2;            // b2 structural base (always-set bits)
        public byte LiveB2EngineOffBit; // OR'd into b2 while the engine is stopped
        public bool IsLive;            // false = declared-only (never streams live)
        public Fsr1FieldDef[] Fields = System.Array.Empty<Fsr1FieldDef>();
    }

    /// <summary>
    /// The fixed catalog of FSR V1 built-in dashboards and their fields — the
    /// single source of truth for "what is on which dashboard". Derived verbatim
    /// from the captures in <c>usb-capture/fsr1/</c> via <c>tools/fsr1-0x42-extract</c>
    /// and <c>tools/fsr1-field-decode</c> (see docs/protocol/devices/wheel-0x17.md
    /// § Group 0x42).
    ///
    /// Field OFFSETS/WIDTHS are proven by per-offset variance analysis; field
    /// SEMANTICS (default mappings) are best-guess for all but the high-confidence
    /// anchors (RPM bar, gear at the last byte, the 0x4B engine flag). Everything is
    /// user-overridable, so precise default semantics are not load-bearing.
    /// Undecoded live bytes are exposed as raw slots (<see cref="Fsr1FieldDef.Decoded"/>
    /// false) so users can experiment / help finish the decode.
    /// </summary>
    internal static class Fsr1DashboardCatalog
    {
        // Common SimHub property paths used as default mappings.
        private const string PropRpmPct = "DataCorePlugin.GameData.CarSettings_CurrentDisplayedRPMPercent"; // 0..100
        private const string PropGear = "DataCorePlugin.GameData.Gear";          // string: R/N/1..n
        private const string PropSpeed = "DataCorePlugin.GameData.SpeedKmh";
        private const string PropTempFL = "DataCorePlugin.GameData.TyreTemperatureFrontLeft";
        private const string PropTempFR = "DataCorePlugin.GameData.TyreTemperatureFrontRight";
        private const string PropTempRL = "DataCorePlugin.GameData.TyreTemperatureRearLeft";
        private const string PropTempRR = "DataCorePlugin.GameData.TyreTemperatureRearRight";

        private static Fsr1FieldDef Wheel(string id, string label, int off, string prop) => new()
        {
            FieldId = id, Label = label, Offsets = new[] { off }, Encoding = Fsr1Encoding.U8,
            Kind = Fsr1FieldKind.Scaled, DefaultProperty = prop, DefaultInMin = 0, DefaultInMax = 150,
            Decoded = true,
        };

        private static Fsr1FieldDef Gear(int off) => new()
        {
            FieldId = "gear", Label = "Gear", Offsets = new[] { off }, Encoding = Fsr1Encoding.U8,
            Kind = Fsr1FieldKind.Direct, DefaultProperty = PropGear, FullScale = 9, Decoded = true,
        };

        private static Fsr1FieldDef EngineFlag(int off) => new()
        {
            FieldId = "engineFlag", Label = "Engine-running flag (auto)", Offsets = new[] { off },
            Encoding = Fsr1Encoding.U8, Kind = Fsr1FieldKind.EngineFlag, Decoded = true,
        };

        private static Fsr1FieldDef Raw(int off, Fsr1Encoding enc = Fsr1Encoding.U8) => new()
        {
            FieldId = "raw" + off, Label = $"Raw byte @{off}" + (enc == Fsr1Encoding.U8 ? "" : $" ({enc})"),
            Offsets = enc == Fsr1Encoding.U16_BE ? new[] { off, off + 1 } : new[] { off },
            Encoding = enc, Kind = Fsr1FieldKind.Direct, DefaultProperty = "", Decoded = false,
        };

        public static readonly Fsr1Dashboard[] Dashboards =
        {
            // ── Live dashboards (decoded layouts) ────────────────────────────
            new()
            {
                RecordType = 0x02, Key = "type-02", Label = "Dashboard 02 — RPM / Gear", IsLive = true,
                // PitHouse: b2=0x00 engine-on, 0x20 engine-off (verified across captures).
                PayloadLen = 18, LiveB1 = 0x03, LiveB2 = 0x00, LiveB2EngineOffBit = 0x20,
                Fields = new[]
                {
                    Wheel("wheelFL", "Wheel 1 (cand. tyre FL)", 6, PropTempFL),
                    Wheel("wheelFR", "Wheel 2 (cand. tyre FR)", 8, PropTempFR),
                    Wheel("wheelRL", "Wheel 3 (cand. tyre RL)", 10, PropTempRL),
                    Wheel("wheelRR", "Wheel 4 (cand. tyre RR)", 12, PropTempRR),
                    new Fsr1FieldDef
                    {
                        FieldId = "rpmBar", Label = "RPM bar", Offsets = new[] { 14 },
                        Encoding = Fsr1Encoding.U8, Kind = Fsr1FieldKind.Scaled,
                        DefaultProperty = PropRpmPct, DefaultInMin = 0, DefaultInMax = 100,
                        FullScale = 158, Decoded = true,
                    },
                    EngineFlag(15),
                    Raw(16),
                    Gear(17),
                },
            },
            new()
            {
                RecordType = 0x06, Key = "type-06", Label = "Dashboard 06 — multi-field", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x0c, LiveB2 = 0x00,
                Fields = new[]
                {
                    Raw(5), Raw(6, Fsr1Encoding.U16_BE), Raw(8),
                    Raw(18), Raw(19), Raw(20), Raw(21),
                    EngineFlag(23),
                    Gear(24),
                },
            },
            new()
            {
                RecordType = 0x0e, Key = "type-0e", Label = "Dashboard 0E — multi-field", IsLive = true,
                // PitHouse always sets bit 0x80 on this type (0x40 sub-state toggle
                // observed but not yet characterised — left out).
                PayloadLen = 24, LiveB1 = 0x0d, LiveB2 = 0x80,
                Fields = new[]
                {
                    Raw(11), Raw(12), Raw(13), Raw(14), Raw(15),
                    EngineFlag(17),
                    Gear(23),
                },
            },
            new()
            {
                RecordType = 0x09, Key = "type-09", Label = "Dashboard 09 — sparse", IsLive = true,
                // PitHouse always sets bit 0x80 on this type (only engine-off captured).
                PayloadLen = 24, LiveB1 = 0x01, LiveB2 = 0x80,
                Fields = new[] { Raw(6, Fsr1Encoding.U16_BE), Raw(18) },
            },
            new()
            {
                // No live 0d frame (b1!=0) was ever observed in any capture — only
                // all-zero declarations. Declared-only until a real live frame is
                // captured; streaming b1=0x00 just spams useless declarations.
                RecordType = 0x0d, Key = "type-0d", Label = "Dashboard 0D — multi-field", IsLive = false,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    Raw(5), Raw(6), Raw(7), Raw(8), Raw(9),
                    Raw(10), Raw(11), Raw(12), Raw(13), Raw(14), Raw(18),
                },
            },
            new()
            {
                RecordType = 0x04, Key = "type-04", Label = "Dashboard 04 — static", IsLive = true,
                PayloadLen = 23, LiveB1 = 0x06, LiveB2 = 0x00, Fields = System.Array.Empty<Fsr1FieldDef>(),
            },
            new()
            {
                RecordType = 0x0b, Key = "type-0b", Label = "Dashboard 0B — static", IsLive = true,
                PayloadLen = 15, LiveB1 = 0x00, LiveB2 = 0x00, Fields = System.Array.Empty<Fsr1FieldDef>(),
            },

            // ── Declared-only record types (never stream live; in the sweep) ──
            new() { RecordType = 0x01, Key = "type-01", Label = "Record 01", PayloadLen = 25 },
            new() { RecordType = 0x03, Key = "type-03", Label = "Record 03", PayloadLen = 19 },
            new() { RecordType = 0x05, Key = "type-05", Label = "Record 05", PayloadLen = 25 },
            new() { RecordType = 0x08, Key = "type-08", Label = "Record 08", PayloadLen = 23 },
            new() { RecordType = 0x0c, Key = "type-0c", Label = "Record 0C", PayloadLen = 18 },
            new() { RecordType = 0x11, Key = "type-11", Label = "Record 11", PayloadLen = 25 },
            new() { RecordType = 0x12, Key = "type-12", Label = "Record 12", PayloadLen = 25 },
        };

        /// <summary>Live dashboards (stream at runtime). Type 02 first (primary).</summary>
        public static readonly Fsr1Dashboard[] LiveDashboards =
            Dashboards.Where(d => d.IsLive).OrderBy(d => d.RecordType == 0x02 ? 0 : 1)
                      .ThenBy(d => d.RecordType).ToArray();

        public static Fsr1Dashboard? ByKey(string key) =>
            Dashboards.FirstOrDefault(d => d.Key == key);

        public static Fsr1Dashboard? ByType(byte type) =>
            Dashboards.FirstOrDefault(d => d.RecordType == type);

        /// <summary>
        /// Active page index (`Param 6` / `g32-81`) → the live record type the wheel
        /// renders on that page. **Partial**, decoded from gameplay captures (live
        /// `0x42` frames only — declarations were noise): only the indices actually
        /// exercised in captures are known. Unmapped indices return null; the sender
        /// then falls back to streaming the whole live set so data still shows. The
        /// map may also be per-dashboard-configuration, not a firmware constant — to
        /// be confirmed with more captures. See docs/protocol/devices/wheel-0x17.md
        /// § Group 0x42.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<int, byte> IndexToRecordType = new()
        {
            { 3, 0x06 }, { 5, 0x04 }, { 7, 0x06 }, { 11, 0x09 }, { 12, 0x0e },
        };

        /// <summary>The live dashboard for a page index, or null if that index's
        /// record type isn't decoded yet.</summary>
        public static Fsr1Dashboard? ByIndex(int index) =>
            IndexToRecordType.TryGetValue(index, out var t) ? ByType(t) : null;
    }
}
