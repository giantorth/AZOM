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
        public byte LiveB1;            // b1 anchor (per-dashboard config; option-A default)
        public byte LiveB2;            // b2 anchor (per-dashboard config; option-A default)
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

        // ===== Auto-generated from usb-capture/fsr1 (tools/.. gencatalog) =====
        public static readonly Fsr1Dashboard[] Dashboards =
        {
            new()
            {
                RecordType = 0x01, Key = "type-01", Label = "Dashboard 01 — dashboard", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g23", Label = "Gauge @23 (16-bit)", Offsets = new[] { 23, 24 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x02, Key = "type-02", Label = "Dashboard 02 — RPM / gear", IsLive = true,
                PayloadLen = 18, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "b17", Label = "Slot @17 (8-bit)", Offsets = new[] { 17 }, Encoding = Fsr1Encoding.U8, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x03, Key = "type-03", Label = "Dashboard 03 — dashboard", IsLive = true,
                PayloadLen = 19, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x04, Key = "type-04", Label = "Dashboard 04 — dashboard", IsLive = true,
                PayloadLen = 23, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x05, Key = "type-05", Label = "Dashboard 05 — dashboard", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g23", Label = "Gauge @23 (16-bit)", Offsets = new[] { 23, 24 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x06, Key = "type-06", Label = "Dashboard 06 — multi-gauge", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x08,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g23", Label = "Gauge @23 (16-bit)", Offsets = new[] { 23, 24 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x08, Key = "type-08", Label = "Dashboard 08 — dashboard", IsLive = true,
                PayloadLen = 23, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x09, Key = "type-09", Label = "Dashboard 09 — sparse", IsLive = true,
                PayloadLen = 24, LiveB1 = 0x00, LiveB2 = 0x08,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "b23", Label = "Slot @23 (8-bit)", Offsets = new[] { 23 }, Encoding = Fsr1Encoding.U8, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x0b, Key = "type-0b", Label = "Dashboard 0B — dashboard", IsLive = true,
                PayloadLen = 15, LiveB1 = 0x00, LiveB2 = 0x04,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x0c, Key = "type-0c", Label = "Dashboard 0C — dashboard", IsLive = true,
                PayloadLen = 18, LiveB1 = 0x00, LiveB2 = 0x02,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "b17", Label = "Slot @17 (8-bit)", Offsets = new[] { 17 }, Encoding = Fsr1Encoding.U8, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x0d, Key = "type-0d", Label = "Dashboard 0D — dashboard", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g23", Label = "Gauge @23 (16-bit)", Offsets = new[] { 23, 24 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x0e, Key = "type-0e", Label = "Dashboard 0E — multi-field", IsLive = true,
                PayloadLen = 24, LiveB1 = 0x0e, LiveB2 = 0x01,
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Gauge @7 (16-bit)", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Gauge @9 (16-bit)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gauge @11 (16-bit)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Gauge @15 (16-bit)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Gauge @17 (16-bit)", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gauge @19 (16-bit)", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "b23", Label = "Slot @23 (8-bit)", Offsets = new[] { 23 }, Encoding = Fsr1Encoding.U8, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x11, Key = "type-11", Label = "Dashboard 11 — GT Style A", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x06,
                // Slot semantics for the GT-style page (index 17 = records 11+12) are
                // community-contributed (driving known channels, reading which box moved).
                // See docs/protocol/devices/wheel-0x17.md § GT-style field semantics. Default
                // properties are the canonical simhub_property values from Data/Telemetry.json
                // (MOZA's own channel catalog). All Direct → InMin/InMax unused; user-overridable.
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Gauge @5 (16-bit)", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Estimated lap time", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.EstimatedLapTime" },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Predicted lap time", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.EstimatedLapTime" },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Gear", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.Gear" },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Gauge @13 (16-bit)", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Speed (km/h)", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.SpeedKmh" },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Fuel — remaining laps", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.FuelLaps" },
                    new Fsr1FieldDef { FieldId = "g19", Label = "Gear", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.Gear" },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Gauge @21 (16-bit)", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                    new Fsr1FieldDef { FieldId = "g23", Label = "Gauge @23 (16-bit)", Offsets = new[] { 23, 24 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
            new()
            {
                RecordType = 0x12, Key = "type-12", Label = "Dashboard 12 — GT Style B", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                // GT-style page second record (paired with 11). Community-contributed
                // slot semantics — see docs/protocol/devices/wheel-0x17.md § GT-style fields.
                Fields = new[]
                {
                    new Fsr1FieldDef { FieldId = "g5", Label = "Tyre pressure FL", Offsets = new[] { 5, 6 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.TyrePressureFrontLeft" },
                    new Fsr1FieldDef { FieldId = "g7", Label = "Tyre pressure RL", Offsets = new[] { 7, 8 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.TyrePressureRearLeft" },
                    new Fsr1FieldDef { FieldId = "g9", Label = "Fuel used (L)", Offsets = new[] { 9, 10 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.FuelUsed" },
                    new Fsr1FieldDef { FieldId = "g11", Label = "Fuel per lap (L)", Offsets = new[] { 11, 12 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.FuelConsumeLap" },
                    new Fsr1FieldDef { FieldId = "g13", Label = "Fuel level", Offsets = new[] { 13, 14 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.Fuel" },
                    new Fsr1FieldDef { FieldId = "g15", Label = "Current lap time", Offsets = new[] { 15, 16 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.CurrentLapTime" },
                    new Fsr1FieldDef { FieldId = "g17", Label = "Lap time", Offsets = new[] { 17, 18 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.CurrentLapTime" },
                    new Fsr1FieldDef { FieldId = "g19", Label = "TC level", Offsets = new[] { 19, 20 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true, DefaultProperty = "DataCorePlugin.GameData.TCLevel" },
                    new Fsr1FieldDef { FieldId = "g21", Label = "Light stage", Offsets = new[] { 21, 22 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = true },
                    new Fsr1FieldDef { FieldId = "g23", Label = "Gauge @23 (16-bit)", Offsets = new[] { 23, 24 }, Encoding = Fsr1Encoding.U16_BE, Kind = Fsr1FieldKind.Direct, Decoded = false },
                },
            },
        };

        // Index → record type(s), verified by correlating g32/81 selects + the wheel's
        // Param-6 0x0E log with the streamed 0x42 record type across the usb-capture/fsr1
        // dashboard-change captures (All dashboards / Moza FSR1 dashboard change / FS1
        // multiple changes / GT Style / manual). Index 0 is the power-on default (784
        // streamed type-01 frames before the first switch). Index 16 is never enumerated
        // by PitHouse (its sweep goes …15, 17, 18) → left unmapped (falls back to the
        // full live set). See docs/protocol/devices/wheel-0x17.md § Group 0x42.
        private static readonly System.Collections.Generic.Dictionary<int, byte[]> IndexToRecordTypes = new()
        {
            { 0, new byte[] { 0x01 } },
            { 1, new byte[] { 0x02 } },
            { 2, new byte[] { 0x06 } },
            { 3, new byte[] { 0x06 } },
            { 4, new byte[] { 0x03 } },
            { 5, new byte[] { 0x04 } },
            { 6, new byte[] { 0x04 } },
            { 7, new byte[] { 0x06 } },
            { 8, new byte[] { 0x05 } },
            { 9, new byte[] { 0x03 } },
            { 10, new byte[] { 0x08 } },
            { 11, new byte[] { 0x09 } },
            { 12, new byte[] { 0x0e } },
            { 13, new byte[] { 0x04 } },
            { 14, new byte[] { 0x04 } },
            { 15, new byte[] { 0x0c } },
            { 17, new byte[] { 0x11, 0x12 } },
            { 18, new byte[] { 0x0c } },
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
        /// Active page index (Param 6 / g32-81) -> the record type(s) the wheel renders
        /// on that page (table above). Firmware-fixed: the index->type mapping is
        /// consistent across all captures (only the channel feeding each field is
        /// per-dashboard config). Most pages map to one type; the GT-style page streams
        /// two (0x11 + 0x12). Unmapped indices return empty -> the driver falls back to
        /// the full live set. See docs/protocol/devices/wheel-0x17.md.
        /// </summary>
        public static Fsr1Dashboard[] ByIndex(int index)
        {
            if (!IndexToRecordTypes.TryGetValue(index, out var types))
                return System.Array.Empty<Fsr1Dashboard>();
            var list = new List<Fsr1Dashboard>(types.Length);
            foreach (var t in types) { var d = ByType(t); if (d != null) list.Add(d); }
            return list.ToArray();
        }
    }
}
