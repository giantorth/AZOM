using System.Collections.Generic;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// One field in the CM1 base-bridged dash's group-0x35 value stream. The CM1 does
    /// NOT use positional records like the FSR1 — it is a flat keyed stream: each field
    /// is addressed by a 16-bit <see cref="Key"/> and carries a big-endian float32 value
    /// (proven from <c>FSR1_CM1.pcapng</c>'s driving window via <c>tools/cm1-0x35-decode</c>).
    /// There is no per-dashboard layer: the same flat set streams regardless of which
    /// built-in dashboard is selected (the switch changes what the dash *displays*).
    /// </summary>
    internal sealed class Cm1FieldDef
    {
        /// <summary>The 16-bit field key in wire order (e.g. {0xF5,0x4D}). High byte is
        /// part of the key, NOT a type tag — every value is a big-endian float32.</summary>
        public byte[] Key = System.Array.Empty<byte>();

        /// <summary>Stable id for settings/UI keying: the key as 4 hex chars ("f54d").</summary>
        public string FieldId = "";

        public string Label = "";

        /// <summary>Default SimHub property (empty = unmapped; user assigns via the
        /// channel mapper). Field semantics are best-effort; nothing is invented.</summary>
        public string DefaultProperty = "";

        /// <summary>Output = resolved SimHub value × Scale (then float32 BE). Default 1.</summary>
        public double Scale = 1.0;

        /// <summary>When set and the field is unmapped, stream this constant verbatim
        /// (replicates PitHouse's fixed fields, e.g. 32.0). null = stream 0 when unmapped.</summary>
        public double? Constant = null;

        /// <summary>True when the field's meaning is reasonably confident; false = raw slot.</summary>
        public bool Decoded = false;

        public bool IsUserMappable => true;
    }

    /// <summary>
    /// The CM1 dash field catalog — the field universe streamed on group-0x35 (dev 0x14),
    /// derived from <c>tools/cm1-0x35-decode FSR1_CM1.pcapng</c>. Field KEYS + the big-endian
    /// float32 encoding are proven from the capture; field SEMANTICS (default mappings) are
    /// best-effort by value range and are user-overridable via the channel mapper, so they
    /// are not load-bearing. Undecoded fields ship with empty defaults so users can map them.
    /// See docs/protocol/devices/ (CM1 group-0x35) once written.
    /// </summary>
    internal static class Cm1DashboardCatalog
    {
        private static Cm1FieldDef F(byte hi, byte lo, string label, bool decoded = false,
                                     string prop = "", double scale = 1.0, double? constant = null) => new()
        {
            Key = new[] { hi, lo },
            FieldId = $"{hi:x2}{lo:x2}",
            Label = label,
            DefaultProperty = prop,
            Scale = scale,
            Constant = constant,
            Decoded = decoded,
        };

        /// <summary>
        /// Flat field set. Keys + encoding proven; labels reflect the value range observed
        /// while driving (best-effort grouping into per-wheel quads / pedals / etc). All
        /// default mappings are intentionally blank — the user assigns SimHub channels.
        /// Constant-valued fields PitHouse streamed (e.g. 32.0) are replicated when unmapped.
        /// </summary>
        public static readonly Cm1FieldDef[] Fields =
        {
            // Normalised 0..1 fields (pedals / inputs).
            F(0xf5, 0x5d, "Input A (0..1 — pedal/normalised)"),
            F(0xf5, 0x5e, "Input B (0..1 — pedal/normalised)"),
            F(0xf5, 0x5f, "Input C (0..1 — pedal/normalised)"),
            F(0xf5, 0x30, "Norm field f530 (0..1)"),
            F(0xf5, 0x31, "Norm field f531 (0..1)"),
            F(0xf5, 0x33, "Norm field f533 (0..1)"),
            F(0xf5, 0x34, "Field f534 (small int/enum)"),
            F(0xf5, 0x32, "Field f532 (0..~30 — speed/throttle?)"),

            // Per-wheel quad ~25 (tyre pressure?, psi).
            F(0xf5, 0x4d, "Wheel quad A1 (~25 — tyre pressure?)"),
            F(0xf5, 0x4e, "Wheel quad A2 (~25 — tyre pressure?)"),
            F(0xf5, 0x4f, "Wheel quad A3 (~25 — tyre pressure?)"),
            F(0xf5, 0x50, "Wheel quad A4 (~25 — tyre pressure?)"),

            // Per-wheel quad ~50 (tyre temp?, degC).
            F(0xf5, 0x41, "Wheel quad B1 (~50 — tyre temp?)"),
            F(0xf5, 0x45, "Wheel quad B2 (~50 — tyre temp?)"),
            F(0xf5, 0x49, "Wheel quad B3 (~50 — tyre temp?)"),
            F(0xf5, 0x3d, "Wheel quad B4 (~50 — tyre temp?)"),

            // Per-wheel quad ~120-140 (temp, degC).
            F(0xf5, 0xa1, "Wheel quad C1 (~120 — temp?)"),
            F(0xf5, 0xa5, "Wheel quad C2 (~120 — temp?)"),
            F(0xf5, 0xa9, "Wheel quad C3 (~120 — temp?)"),
            F(0xf5, 0xad, "Wheel quad C4 (~120 — temp?)"),

            // Per-wheel quad 26..278 (wheel speed?, km/h).
            F(0xda, 0x3d, "Wheel quad D1 (26..278 — speed/rot?)"),
            F(0xda, 0x3e, "Wheel quad D2 (26..278 — speed/rot?)"),
            F(0xda, 0x3f, "Wheel quad D3 (26..193 — speed/rot?)"),
            F(0xda, 0x40, "Wheel quad D4 (26..193 — speed/rot?)"),

            // Per-wheel quad 79..535 (brake temp?, degC).
            F(0xda, 0xa1, "Wheel quad E1 (79..535 — brake temp?)"),
            F(0xda, 0xa2, "Wheel quad E2 (79..535 — brake temp?)"),
            F(0xda, 0xa3, "Wheel quad E3 (79..380 — brake temp?)"),
            F(0xda, 0xa4, "Wheel quad E4 (79..380 — brake temp?)"),

            F(0xd9, 0xe4, "Field d9e4 (176..396)"),
            F(0xda, 0x5d, "Field da5d (const ~5)", constant: 5.0),
            F(0xda, 0x5e, "Field da5e (const ~5)", constant: 5.0),

            // Eight fields PitHouse holds at 32.0 — replicate the constant when unmapped.
            F(0xf5, 0xa2, "Const f5a2 (32.0)", constant: 32.0),
            F(0xf5, 0xa4, "Const f5a4 (32.0)", constant: 32.0),
            F(0xf5, 0xa6, "Const f5a6 (32.0)", constant: 32.0),
            F(0xf5, 0xa8, "Const f5a8 (32.0)", constant: 32.0),
            F(0xf5, 0xaa, "Const f5aa (32.0)", constant: 32.0),
            F(0xf5, 0xac, "Const f5ac (32.0)", constant: 32.0),
            F(0xf5, 0xae, "Const f5ae (32.0)", constant: 32.0),
            F(0xf5, 0xb0, "Const f5b0 (32.0)", constant: 32.0),
        };

        /// <summary>Dashboard pages selectable via the 0x32/0x81 switch command. The
        /// capture exercised indices 1..13 (1-based). Refine the count on hardware.</summary>
        public const int MinDashboardIndex = 1;
        public const int MaxDashboardIndex = 13;

        private static readonly Dictionary<string, Cm1FieldDef> _byId = BuildIndex();

        private static Dictionary<string, Cm1FieldDef> BuildIndex()
        {
            var d = new Dictionary<string, Cm1FieldDef>();
            foreach (var f in Fields) d[f.FieldId] = f;
            return d;
        }

        public static Cm1FieldDef ByFieldId(string fieldId) =>
            fieldId != null && _byId.TryGetValue(fieldId, out var f) ? f : null;
    }
}
