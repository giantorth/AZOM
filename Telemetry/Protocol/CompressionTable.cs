using System;
using System.Collections.Generic;

namespace MozaPlugin.Telemetry.Protocol
{
    // Single source of truth for every compression code observed in PitHouse traffic.
    // Old code spread bit-width / encoder / test-range maps across three files
    // (Telemetry/TierDefinitionBuilder.cs:180, TelemetryEncoder.cs:15, :154); this
    // collapses them into one row per code.
    //
    // Codes covered (per docs/protocol/findings/2026-05-04-tierdef-reference.md):
    //   captured in tier-def channel records: 0x00 0x02 0x04 0x07 0x0D 0x0E 0x0F 0x11 0x13 0x16
    //   captured bit widths:                  1, 5, 8, 10, 12, 14, 16, 32
    //
    // String aliases preserve the existing Telemetry.json / mzdash channel descriptors
    // so DashboardProfileStore can keep emitting them; CompressionTable maps them to
    // numeric codes for tier-def emission and to encoder lambdas for value-frame packing.
    public static class CompressionTable
    {
        public sealed class Entry
        {
            public string Name { get; }            // canonical name from Telemetry.json
            public uint Code { get; }              // wire byte in tier-def channel record
            public int BitWidth { get; }           // bits packed into value frame
            public Func<double, ulong> Encode { get; } // game value → packed uint
            // Wire-safe (min,max) for this compression. Used as the
            // last-resort fallback for the test-mode sweep when neither an
            // override nor a parseable Telemetry.json range is available
            // (see Telemetry/TestMode/TestSignalCatalog.cs).
            public (double min, double max) TestRange { get; }

            public Entry(string name, uint code, int bitWidth,
                Func<double, ulong> encode, (double, double) testRange)
            {
                Name = name;
                Code = code;
                BitWidth = bitWidth;
                Encode = encode;
                TestRange = testRange;
            }
        }

        private static readonly Dictionary<string, Entry> _byName =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<uint, Entry> _byCode = new Dictionary<uint, Entry>();
        private static readonly List<Entry> _all = new List<Entry>();

        // Sanitise NaN / ±Infinity (SimHub surfaces these during init / pre-warmup).
        private static double Sanitize(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : v;
        private static double Clamp(double v, double lo, double hi) =>
            v < lo ? lo : v > hi ? hi : v;

        private static void Add(Entry e)
        {
            _byName[e.Name] = e;
            if (!_byCode.ContainsKey(e.Code))
                _byCode[e.Code] = e;
            _all.Add(e);
        }

        static CompressionTable()
        {
            // 1-bit boolean
            Add(new Entry("bool", 0x00, 1,
                v => Sanitize(v) != 0.0 ? 1ul : 0ul, (0, 1)));

            // 8-bit signed (used as gear etc.)
            Add(new Entry("int8_t", 0x02, 8,
                v => (ulong)(byte)(int)Sanitize(v), (-128, 127)));
            Add(new Entry("uint8_t", 0x01, 8,
                v => (ulong)(byte)(int)Sanitize(v), (0, 255)));

            // 16-bit
            Add(new Entry("uint16_t", 0x04, 16,
                v => (ulong)(ushort)(int)Sanitize(v), (0, 10000)));
            Add(new Entry("int16_t", 0x05, 16,
                v => (ulong)(ushort)(int)Sanitize(v), (-1000, 1000)));

            // 32-bit float (special: store the IEEE-754 bits)
            Add(new Entry("float", 0x07, 32,
                v => (ulong)BitConverter.ToUInt32(BitConverter.GetBytes((float)Sanitize(v)), 0),
                (0, 200)));

            // 5-bit gear-style (signed via 2's complement: -1 → 31 = reverse)
            Add(new Entry("int30", 0x0D, 5, EncodeInt30, (0, 30)));
            Add(new Entry("uint30", 0x0D, 5, EncodeInt30, (0, 30)));
            Add(new Entry("uint31", 0x0D, 5, EncodeInt30, (0, 30)));

            // 10-bit percent (×10, 0..1000)
            Add(new Entry("percent_1", 0x0E, 10,
                v => (ulong)Clamp(Sanitize(v) * 10.0, 0, 1000), (0, 100)));
            Add(new Entry("float_001", 0x17, 10,
                v => (ulong)Clamp(Sanitize(v) * 1000.0, 0, 1000), (0, 1)));

            // 16-bit speed (×10, 0..65535)
            Add(new Entry("float_6000_1", 0x0F, 16,
                v => (ulong)Clamp(Sanitize(v) * 10.0, 0, 65535), (0, 400)));
            Add(new Entry("float_600_2", 0x15, 16,
                v => (ulong)Clamp(Sanitize(v) * 100.0, 0, 65535), (0, 400)));

            // 12-bit tyre pressure (×10, 0..4095). Code confirmed by decoding
            // PitHouse value frames in bridge-20260503-112940.jsonl flag=0x04/0x0a/0x10:
            // raw=299 → 29.9 PSI / 2.99 bar (all-tyres garage default). PitHouse emits
            // comp=0x16 (not 0x10 as previously inferred — 0x10 never appears in any
            // V2/Type02 tier-def channel record).
            Add(new Entry("tyre_pressure_1", 0x16, 12,
                v => (ulong)Clamp(Sanitize(v) * 10.0, 0, 4095), (0, 40)));

            // 14-bit temps with +5000 offset
            Add(new Entry("tyre_temp_1", 0x11, 14, EncodeTemp14, (0, 150)));
            Add(new Entry("track_temp_1", 0x12, 14, EncodeTemp14, (0, 60)));
            Add(new Entry("oil_pressure_1", 0x13, 14, EncodeTemp14, (0, 10)));

            // 16-bit brake temp
            Add(new Entry("brake_temp_1", 0x16, 16,
                v => (ulong)Clamp(Sanitize(v) * 10.0 + 5000.0, 0, 65535), (0, 1000)));

            // 4-bit
            Add(new Entry("uint3", 0x14, 4,
                v => Math.Min((ulong)Math.Max(0, (int)Sanitize(v)), 15ul), (0, 15)));
            Add(new Entry("uint8", 0x14, 4,
                v => Math.Min((ulong)Math.Max(0, (int)Sanitize(v)), 15ul), (0, 15)));
            Add(new Entry("uint15", 0x03, 4,
                v => Math.Min((ulong)Math.Max(0, (int)Sanitize(v)), 15ul), (0, 15)));

            // 32-bit ints
            Add(new Entry("int32_t", 0x08, 32,
                v => (ulong)(uint)(int)Sanitize(v), (-10000, 10000)));
            Add(new Entry("uint32_t", 0x09, 32,
                v => (ulong)(uint)(int)Sanitize(v), (0, 10000)));
            Add(new Entry("uint24_t", 0x18, 24,
                v => (ulong)((uint)(int)Sanitize(v) & 0xFFFFFFu), (0, 10000)));

            // 64-bit (caller uses BitConverter on raw double; encode returns the bits)
            Add(new Entry("double", 0x0A, 64,
                v => BitConverter.ToUInt64(BitConverter.GetBytes(Sanitize(v)), 0),
                (0, 200)));
            Add(new Entry("location_t", 0x0B, 64,
                v => BitConverter.ToUInt64(BitConverter.GetBytes(Sanitize(v)), 0),
                (0, 1)));
            Add(new Entry("int64_t", 0x0C, 64,
                v => (ulong)(long)Sanitize(v), (-1000, 1000)));
            Add(new Entry("uint64_t", 0x19, 64,
                v => (ulong)(long)Sanitize(v), (0, 1000)));
        }

        private static ulong EncodeInt30(double v)
        {
            v = Sanitize(v);
            if (v < 0) return (ulong)((int)v & 0x1F); // -1 → 31 (reverse gear)
            return Math.Min((ulong)v, 30ul);
        }

        private static ulong EncodeTemp14(double v)
        {
            return (ulong)Clamp(Sanitize(v) * 10.0 + 5000.0, 0, 16383);
        }

        // Public lookups. Both throw if name/code is unknown — callers should
        // pre-validate via TryGet against the Telemetry.json catalog at load time.
        public static Entry GetByName(string name) => _byName[name];
        public static Entry GetByCode(uint code) => _byCode[code];
        public static bool TryGetByName(string name, out Entry entry) => _byName.TryGetValue(name, out entry!);
        public static bool TryGetByCode(uint code, out Entry entry) => _byCode.TryGetValue(code, out entry!);

        public static IReadOnlyList<Entry> All => _all;
    }
}
