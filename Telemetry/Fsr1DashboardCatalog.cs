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
        /// <summary>24-bit sign-magnitude: bit23 = sign (set when the source is negative), the low
        /// bits carry |value|. Used by the gap/delta field — a signed ms delta to best (negative =
        /// ahead/faster). Plain Direct would clamp negatives to 0.</summary>
        SignedMagnitude,
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
        // ── Sub-byte / bit-packed geometry (LSB-first). Set by the ground-truth catalog for the
        //    firmware's 10-bit tyre/pressure packs, GearDrsErs, and compact flag bundles. ──
        /// <summary>In-byte LSB-first bit of the field's LSB (0 = mask 0x01). Pairs with BitWidth.</summary>
        public int StartBit = 0;
        /// <summary>Sub-byte field bit width (1..24); 0 = byte-aligned (Offsets/Encoding govern).</summary>
        public int BitWidth = 0;
        /// <summary>Default output gain applied when no user override: wire = value·DefaultScale +
        /// DefaultBias. Ground truth: tyre temps use Bias +300 (10-bit, −300..723 °C headroom),
        /// lap times use Scale 1000 (seconds → ms). A user Scale/Bias override wins over these.</summary>
        public double DefaultScale = 1.0;
        public double DefaultBias = 0.0;

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

    /// <summary>
    /// A field resolved into its effective wire geometry by <see cref="Fsr1DashboardCatalog.ResolvePartition"/>.
    /// Bit geometry is the true model; byte-aligned fields are the case where the run starts on a
    /// byte boundary and is a whole number of bytes wide (<see cref="IsByteAligned"/> → the fast
    /// <c>WriteField</c> path, which also handles U16_LE). Sub-byte / bit-packed fields carry an
    /// arbitrary MSB-first bit run that may share a byte with a neighbour and leave spare bits.
    /// </summary>
    internal readonly struct Fsr1Slot
    {
        public readonly Fsr1FieldDef Field;
        public readonly int[] Offsets;      // contiguous touched payload bytes [ByteStart..ByteEnd]
        public readonly Fsr1Encoding Enc;   // real enc for byte-aligned; U24_BE placeholder for packed
        public readonly int BitOffset;      // absolute MSB-first bit over the payload (byte*8 + inByteBit)
        public readonly int BitWidth;       // total bits (1..24)
        public readonly bool MsbFirst;

        public Fsr1Slot(Fsr1FieldDef field, int[] offsets, Fsr1Encoding enc,
                        int bitOffset, int bitWidth, bool msbFirst)
        {
            Field = field; Offsets = offsets; Enc = enc;
            BitOffset = bitOffset; BitWidth = bitWidth; MsbFirst = msbFirst;
        }

        /// <summary>Byte-boundary + whole number of bytes → emit via the byte path (big/little
        /// endian per <see cref="Enc"/>). Anything sub-byte goes through the LSB-first bit packer.</summary>
        public bool IsByteAligned => (BitOffset & 7) == 0 && (BitWidth & 7) == 0;
        public int ByteStart => BitOffset >> 3;
        public int ByteEnd => (BitOffset + BitWidth - 1) >> 3;
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
        // Background tyre/status cache record — not a selectable page. PitHouse interleaves it
        // sparsely (~0.5 Hz) alongside the active page so the firmware can show tyre temps,
        // pressure, track/air temp, car count and lap count on pages whose primary record lacks
        // them. The driver streams it via a low-rate one-shot Send, not a full-rate slot.
        public bool IsBackground;
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
        // ── Ground-truth field builders ─────────────────────────────────────────
        // Records are laid out with an auto-advancing cursor (data starts at byte 5), mirroring
        // PitHouse's FormulaSteeringTelemetryDataPackN classes which concatenate each strategy's
        // whole-byte output in order. U8/U16/U24 advance whole bytes (big-endian on the wire);
        // Bits() advances a sub-byte LSB-first field; Pack10x4() lays a 4×10-bit LSB group
        // (TyreTemperature / TyrePressure strategies = 5 bytes). See docs § Group 0x42.
        private sealed class Fields
        {
            private readonly System.Collections.Generic.List<Fsr1FieldDef> _list =
                new System.Collections.Generic.List<Fsr1FieldDef>();
            private int _bit = 5 * 8;  // cursor: absolute payload bit, data begins at byte 5

            private Fields Add(Fsr1FieldDef f, int bits) { _list.Add(f); _bit += bits; return this; }

            public Fields U8(string id, string label, string prop = "", double bias = 0.0, double scale = 1.0) =>
                Add(MakeByte(id, label, _bit >> 3, Fsr1Encoding.U8, prop, scale: scale, bias: bias), 8);
            public Fields U16(string id, string label, string prop = "", long fullScale = 0, double scale = 1.0) =>
                Add(MakeByte(id, label, _bit >> 3, Fsr1Encoding.U16_BE, prop, fullScale, scale), 16);
            public Fields U24(string id, string label, string prop = "", double scale = 1.0, Fsr1FieldKind kind = Fsr1FieldKind.Direct) =>
                Add(MakeByte(id, label, _bit >> 3, Fsr1Encoding.U24_BE, prop, scale: scale, kind: kind), 24);
            public Fields Bits(string id, string label, int width, string prop = "", long fullScale = 0, double scale = 1.0, double bias = 0.0) =>
                Add(MakeBits(id, label, _bit, width, prop, fullScale, scale, bias), width);
            /// <summary>Four 10-bit values LSB-packed into 5 bytes (tyre temp / pressure group).
            /// bias = +300 for tyre temps (firmware decodes value−300 for sub-zero headroom).</summary>
            public Fields Pack10x4(string idPrefix, string labelPrefix, string[] suffix, string[] props, double bias = 0.0)
            {
                for (int i = 0; i < 4; i++)
                    Bits(idPrefix + suffix[i], labelPrefix + " " + suffix[i], 10, i < props.Length ? props[i] : "", bias: bias);
                return this;
            }
            /// <summary>GearDrsErs strategy: gear[0:4] · ERS deploy mode[4:6] (2-bit) · DRS[6] (1-bit),
            /// bit 7 spare. Verified from capture: the 2-bit field is ERS mode (0–3), the 1-bit is DRS.</summary>
            public Fields GearDrsErs(string idp)
            {
                int b = _bit;
                // Gear wire value = SimHub gear + 1 (firmware: 0=R, 1=N, 2=1st…); verified from capture.
                _list.Add(MakeBits(idp + "Gear", "Gear", b, 4, "DataCorePlugin.GameData.Gear", bias: 1.0));
                _list.Add(MakeBits(idp + "Ers", "ERS mode", b + 4, 2, ErsDeployMode));
                _list.Add(MakeBits(idp + "Drs", "DRS", b + 6, 1, "DataCorePlugin.GameData.DRSEnabled"));
                _bit += 8;
                return this;
            }
            /// <summary>Compact&lt;4,4&gt;: two 4-bit LSB values in one byte.</summary>
            public Fields Nibbles(string id0, string l0, string p0, string id1, string l1, string p1, double bias0 = 0.0, double bias1 = 0.0)
            {
                int b = _bit;
                _list.Add(MakeBits(id0, l0, b, 4, p0, bias: bias0));
                _list.Add(MakeBits(id1, l1, b + 4, 4, p1, bias: bias1));
                _bit += 8;
                return this;
            }
            /// <summary>Compact&lt;1,…&gt;: up to seven 1-bit flags LSB in one byte.</summary>
            public Fields Flags(params (string id, string label, string prop)[] flags)
            {
                int b = _bit;
                for (int i = 0; i < flags.Length; i++)
                    _list.Add(MakeBits(flags[i].id, flags[i].label, b + i, 1, flags[i].prop));
                _bit += 8;
                return this;
            }
            public Fsr1FieldDef[] Done() => _list.ToArray();
        }

        private static Fsr1FieldDef MakeByte(string id, string label, int byteStart, Fsr1Encoding enc, string prop, long fullScale = 0, double scale = 1.0, double bias = 0.0, Fsr1FieldKind kind = Fsr1FieldKind.Direct)
        {
            int w = enc == Fsr1Encoding.U8 ? 1 : enc == Fsr1Encoding.U24_BE ? 3 : 2;
            var offs = new int[w];
            for (int i = 0; i < w; i++) offs[i] = byteStart + i;
            return new Fsr1FieldDef { FieldId = id, Label = label, Offsets = offs, Encoding = enc,
                Kind = kind, DefaultProperty = prop, Decoded = true, FullScale = fullScale,
                DefaultScale = scale, DefaultBias = bias };
        }

        private static Fsr1FieldDef MakeBits(string id, string label, int bitOffset, int bitWidth, string prop, long fullScale = 0, double scale = 1.0, double bias = 0.0)
        {
            int b0 = bitOffset >> 3, b1 = (bitOffset + bitWidth - 1) >> 3;
            var offs = new int[b1 - b0 + 1];
            for (int i = 0; i < offs.Length; i++) offs[i] = b0 + i;
            return new Fsr1FieldDef { FieldId = id, Label = label, Offsets = offs, Encoding = Fsr1Encoding.U24_BE,
                StartBit = bitOffset & 7, BitWidth = bitWidth, Kind = Fsr1FieldKind.Direct,
                DefaultProperty = prop, Decoded = true, FullScale = fullScale, DefaultScale = scale, DefaultBias = bias };
        }

        // SimHub generic game-data property prefix. Field DefaultProperty = G + "<Name>".
        private const string G = "DataCorePlugin.GameData.";
        // PitHouse tyre/pressure wheel order within a 4×10-bit group: FL, FR, RL, RR
        // (verified by decoding a captured session — tyre0=FL, tyre1=FR, tyre2=RL, tyre3=RR).
        private static readonly string[] Corners = { "FL", "FR", "RL", "RR" };
        // The FSR1 firmware's tyre pages want the game's INNER (carcass) temp for the inner group and
        // the SURFACE temp for the outer group — SimHub's generic TyreTemperature* is only the surface,
        // so we bind the F1 raw arrays PitHouse itself uses. F1 wheel order is RL,RR,FL,FR (1-based
        // suffix 01,02,03,04); our group order is FL,FR,RL,RR → suffix 03,04,01,02.
        private const string F1Raw = "DataCorePlugin.GameRawData.PacketCarTelemetryData.m_carTelemetryData01.";
        private const string F1RawStatus = "DataCorePlugin.GameRawData.PacketCarStatusData.m_carStatusData01.";
        // ERS deploy mode 0–3 (3 = overtake) drives the firmware's OVERTAKE highlight; the game value
        // maps straight into the 2-bit field. Live delta to session best (signed seconds) for gap fields.
        private const string ErsDeployMode = F1RawStatus + "m_ersDeployMode";
        private const string LiveDelta = "PersistantTrackerPlugin.SessionBestLiveDeltaSeconds";
        // Fuel remaining as laps of range (signed: negative = short). Wire = laps × 100 (verified).
        private const string FuelRemainLaps = F1RawStatus + "m_fuelRemainingLaps";
        // ERS this-lap energy (Joules). Deploy bar shows budget REMAINING = 100 − deployed/40000 (of the
        // 4 MJ deploy cap; verified clean). Harvest bar = harvested/20000 (of the 2 MJ MGU-K cap; the
        // capture's harvest data was sparse so the scale is approximate — tune on-wheel if needed).
        private const string ErsDeployedThisLap = F1RawStatus + "m_ersDeployedThisLap";
        private const string ErsHarvestedThisLap = F1RawStatus + "m_ersHarvestedThisLapMGUK";
        // Fuel mix / class 0–3 (lean/standard/rich/max).
        private const string FuelMix = F1RawStatus + "m_fuelMix";
        // Session time remaining (seconds → ms via MsScale) for the GT session clock.
        private const string SessionTimeLeft = "DataCorePlugin.GameRawData.PacketSessionData.m_sessionTimeLeft";
        private static readonly string[] InnerTempProps =
        {
            F1Raw + "m_tyresInnerTemperature03", F1Raw + "m_tyresInnerTemperature04",
            F1Raw + "m_tyresInnerTemperature01", F1Raw + "m_tyresInnerTemperature02",
        };
        private static readonly string[] SurfaceTempProps =
        {
            F1Raw + "m_tyresSurfaceTemperature03", F1Raw + "m_tyresSurfaceTemperature04",
            F1Raw + "m_tyresSurfaceTemperature01", F1Raw + "m_tyresSurfaceTemperature02",
        };
        private static readonly string[] TyrePressProps =
        {
            G + "TyrePressureFrontLeft", G + "TyrePressureFrontRight",
            G + "TyrePressureRearLeft", G + "TyrePressureRearRight",
        };
        // Tyre-temp 10-bit fields carry a +300 wire bias (firmware decodes value−300 °C).
        private const double TyreTempBias = 300.0;
        // Lap-time 24-bit fields carry the game value in milliseconds (SimHub seconds × 1000).
        private const double MsScale = 1000.0;

        // Ground-truth catalog, derived by decompiling PitHouse's FormulaSteeringTelemetryDataPackN
        // classes (Pack N = record type 0x0N). Each record is that Pack's exact ordered field list;
        // strategies fix the encoding: tyre temp / tyre pressure = 4×10-bit LSB packs (5 bytes),
        // brake temp / speed / rpm / int16 = 16-bit big-endian, lap times = 24-bit BE (ms),
        // int8 / gear / temp = 8-bit, GearDrsErs / compact bundles = sub-byte LSB. See docs § Group 0x42.
        public static readonly Fsr1Dashboard[] Dashboards =
        {
            new()
            {
                RecordType = 0x01, Key = "type-01", Label = "Dashboard 01 — tyre / timing", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new Fields()
                    .Pack10x4("tti", "Tyre inner", Corners, InnerTempProps, TyreTempBias)
                    .U24("clt", "Current lap time", G + "CurrentLapTime", MsScale)
                    .U16("frl", "Fuel remain laps", FuelRemainLaps, scale: 100.0)
                    .U16("fsl", "Fuel surplus laps", "")
                    .U16("spd", "Speed", G + "SpeedKmh")
                    .U8("pos", "Position", G + "Position")
                    .U8("lap", "Lap", G + "CurrentLap")
                    .U8("ersR", "ERS remaining", G + "ERSPercent")
                    .U8("ersD", "ERS deploy left", ErsDeployedThisLap, bias: 100.0, scale: -1.0 / 40000.0)
                    .U8("ersH", "ERS harvested", ErsHarvestedThisLap, scale: 1.0 / 20000.0)
                    .GearDrsErs("gde")
                    .Done(),
            },
            new()
            {
                RecordType = 0x02, Key = "type-02", Label = "Dashboard 02 — brake temps", IsLive = true,
                PayloadLen = 18, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new Fields()
                    .U16("btFL", "Brake temp FL", G + "BrakeTemperatureFrontLeft")
                    .U16("btFR", "Brake temp FR", G + "BrakeTemperatureFrontRight")
                    .U16("btRL", "Brake temp RL", G + "BrakeTemperatureRearLeft")
                    .U16("btRR", "Brake temp RR", G + "BrakeTemperatureRearRight")
                    .U16("spd", "Speed", G + "SpeedKmh")
                    .U8("fuel", "Fuel remaining", G + "Fuel")
                    .U8("ersR", "ERS remaining", G + "ERSPercent")
                    .GearDrsErs("gde")
                    .Done(),
            },
            new()
            {
                RecordType = 0x03, Key = "type-03", Label = "Dashboard 03 — wear", IsLive = true,
                PayloadLen = 19, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new Fields()
                    .U16("spd", "Speed", G + "SpeedKmh")
                    .U8("twFL", "Tyre wear FL", G + "TyreWearFrontLeft")
                    .U8("twFR", "Tyre wear FR", G + "TyreWearFrontRight")
                    .U8("twRL", "Tyre wear RL", G + "TyreWearRearLeft")
                    .U8("twRR", "Tyre wear RR", G + "TyreWearRearRight")
                    .U8("wwFL", "Wing wear FL", "")
                    .U8("wwFR", "Wing wear FR", "")
                    .U8("wwR", "Wing wear R", "")
                    .U8("engWear", "Engine wear", "")
                    .U8("gbxWear", "Gearbox wear", "")
                    .U8("ersR", "ERS remaining", G + "ERSPercent")
                    .U8("fuel", "Fuel remaining", G + "Fuel")
                    .GearDrsErs("gde")
                    .Done(),
            },
            new()
            {
                RecordType = 0x04, Key = "type-04", Label = "Dashboard 04 — timing / RPM", IsLive = true,
                PayloadLen = 23, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new Fields()
                    .U24("clt", "Current lap time", G + "CurrentLapTime", MsScale)
                    .U24("llt", "Last lap time", G + "LastLapTime", MsScale)
                    .U24("blt", "Best lap time", G + "BestLapTime", MsScale)
                    .U16("spd", "Speed", G + "SpeedKmh")
                    .U16("rpm", "RPM", G + "Rpms")
                    .U16("maxRpm", "Max RPM", G + "MaxRpm")
                    .U8("ersR", "ERS remaining", G + "ERSPercent")
                    .U8("fuel", "Fuel remaining", G + "Fuel")
                    .GearDrsErs("gde")
                    .Done(),
            },
            new()
            {
                RecordType = 0x05, Key = "type-05", Label = "Dashboard 05 — timing / wear", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new Fields()
                    .U24("clt", "Current lap time", G + "CurrentLapTime", MsScale)
                    .U24("llt", "Last lap time", G + "LastLapTime", MsScale)
                    .U24("blt", "Best lap time", G + "BestLapTime", MsScale)
                    .U16("spd", "Speed", G + "SpeedKmh")
                    .U8("twFL", "Tyre wear FL", G + "TyreWearFrontLeft")
                    .U8("twFR", "Tyre wear FR", G + "TyreWearFrontRight")
                    .U8("twRL", "Tyre wear RL", G + "TyreWearRearLeft")
                    .U8("twRR", "Tyre wear RR", G + "TyreWearRearRight")
                    .U8("pos", "Position", G + "Position")
                    .U8("cars", "Car count", G + "OpponentsCount")
                    .U8("lap", "Lap", G + "CurrentLap")
                    .U8("laps", "Lap count", G + "TotalLaps")
                    .U8("gear", "Gear", G + "Gear", bias: 1.0)
                    .Done(),
            },
            new()
            {
                RecordType = 0x06, Key = "type-06", Label = "Dashboard 06 — timing / gap", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x08,
                Fields = new Fields()
                    .U24("clt", "Current lap time", G + "CurrentLapTime", MsScale)
                    .U24("llt", "Last lap time", G + "LastLapTime", MsScale)
                    .U24("blt", "Best lap time", G + "BestLapTime", MsScale)
                    .U24("gap", "Gap", LiveDelta, MsScale, Fsr1FieldKind.SignedMagnitude)
                    .U16("spd", "Speed", G + "SpeedKmh")
                    .U16("rpm", "RPM", G + "Rpms")
                    .U8("pos", "Position", G + "Position")
                    .U8("fuel", "Fuel remaining", G + "Fuel")
                    .U8("ersR", "ERS remaining", G + "ERSPercent")
                    .GearDrsErs("gde")
                    .Done(),
            },
            new()
            {
                RecordType = 0x08, Key = "type-08", Label = "Dashboard 08 — tyres / brakes", IsLive = true,
                PayloadLen = 23, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new Fields()
                    .Pack10x4("tti", "Tyre inner", Corners, InnerTempProps, TyreTempBias)
                    .Pack10x4("tto", "Tyre outer", Corners, SurfaceTempProps, TyreTempBias)
                    .U16("btFL", "Brake temp FL", G + "BrakeTemperatureFrontLeft")
                    .U16("btFR", "Brake temp FR", G + "BrakeTemperatureFrontRight")
                    .U16("btRL", "Brake temp RL", G + "BrakeTemperatureRearLeft")
                    .U16("btRR", "Brake temp RR", G + "BrakeTemperatureRearRight")
                    .Done(),
            },
            new()
            {
                RecordType = 0x09, Key = "type-09", Label = "Dashboard 09 — timing", IsLive = true,
                PayloadLen = 24, LiveB1 = 0x00, LiveB2 = 0x08,
                Fields = new Fields()
                    .U24("clt", "Current lap time", G + "CurrentLapTime", MsScale)
                    .U24("llt", "Last lap time", G + "LastLapTime", MsScale)
                    .U24("blt", "Best lap time", G + "BestLapTime", MsScale)
                    .U24("gap", "Gap", LiveDelta, MsScale, Fsr1FieldKind.SignedMagnitude)
                    .U16("spd", "Speed", G + "SpeedKmh")
                    .U8("pos", "Position", G + "Position")
                    .U8("ersR", "ERS remaining", G + "ERSPercent")
                    .U8("tc", "TC level", G + "TCLevel")
                    .U8("abs", "ABS level", G + "ABSLevel")
                    .U8("gear", "Gear", G + "Gear", bias: 1.0)
                    .Done(),
            },
            new()
            {
                RecordType = 0x0b, Key = "type-0b", Label = "Dashboard 0B — timing / bias", IsLive = true,
                PayloadLen = 15, LiveB1 = 0x00, LiveB2 = 0x04,
                Fields = new Fields()
                    .U24("llt", "Last lap time", G + "LastLapTime", MsScale)
                    .U24("blt", "Best lap time", G + "BestLapTime", MsScale)
                    .U16("fuelTemp", "Fuel temp", "")
                    .U16("bias", "Brake bias", G + "BrakeBias")
                    .Done(),
            },
            new()
            {
                RecordType = 0x0c, Key = "type-0c", Label = "Dashboard 0C — timing / RPM", IsLive = true,
                PayloadLen = 18, LiveB1 = 0x00, LiveB2 = 0x02,
                Fields = new Fields()
                    .U24("clt", "Current lap time", G + "CurrentLapTime", MsScale)
                    .U24("gap", "Gap", LiveDelta, MsScale, Fsr1FieldKind.SignedMagnitude)
                    .U16("spd", "Speed", G + "SpeedKmh")
                    .U16("rpm", "RPM", G + "Rpms")
                    .U16("maxRpm", "Max RPM", G + "MaxRpm")
                    .U8("gear", "Gear", G + "Gear", bias: 1.0)
                    .Done(),
            },
            new()
            {
                RecordType = 0x0d, Key = "type-0d", Label = "Tyre / status cache", IsLive = true, IsBackground = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new Fields()
                    .Pack10x4("tti", "Tyre inner", Corners, InnerTempProps, TyreTempBias)
                    .Pack10x4("tto", "Tyre outer", Corners, SurfaceTempProps, TyreTempBias)
                    .U8("cars", "Car count", G + "OpponentsCount")
                    .U8("lap", "Lap", G + "CurrentLap")
                    .U8("laps", "Lap count", G + "TotalLaps")
                    .Pack10x4("tp", "Tyre pressure", Corners, TyrePressProps)
                    .U8("trackT", "Track temp", G + "RoadTemperature")
                    .U8("airT", "Air temp", G + "AirTemperature")
                    .Done(),
            },
            new()
            {
                RecordType = 0x0e, Key = "type-0e", Label = "Dashboard 0E — race info", IsLive = true,
                PayloadLen = 24, LiveB1 = 0x0e, LiveB2 = 0x01,
                Fields = new Fields()
                    .U24("gap", "Gap", LiveDelta, MsScale, Fsr1FieldKind.SignedMagnitude)
                    .U16("frl", "Fuel remain laps", FuelRemainLaps, scale: 100.0)
                    .U16("spd", "Speed", G + "SpeedKmh")
                    .U16("rpm", "RPM", G + "Rpms")
                    .U8("lap", "Lap", G + "CurrentLap")
                    .U8("pos", "Position", G + "Position")
                    .U8("fuel", "Fuel remaining", G + "Fuel")
                    .U8("tc", "TC level", G + "TCLevel")
                    .U8("abs", "ABS level", G + "ABSLevel")
                    .U8("boost", "Boost", "")
                    .U8("ecu", "ECU map", G + "EngineMap")
                    .U8("tc2", "TC2", "")
                    .U8("fuelClass", "Fuel mix", FuelMix)
                    .U8("gear", "Gear", G + "Gear", bias: 1.0)
                    .Done(),
            },
            new()
            {
                RecordType = 0x11, Key = "type-11", Label = "Dashboard 11 — GT (A)", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x06,
                Fields = new Fields()
                    .U24("stl", "Session time left", SessionTimeLeft, MsScale)
                    .U24("elt", "Estimated lap time", "", MsScale)
                    .U24("gap", "Gap", LiveDelta, MsScale, Fsr1FieldKind.SignedMagnitude)
                    .U16("rpm", "RPM", G + "Rpms")
                    .U16("spd", "Speed", G + "SpeedKmh")
                    .U16("frl", "Fuel remain laps", FuelRemainLaps, scale: 100.0)
                    .Nibbles("gear", "Gear", G + "Gear", "abs", "ABS level", G + "ABSLevel", bias0: 1.0)
                    .U8("pos", "Position", G + "Position")
                    .U8("clutch", "Clutch", G + "Clutch")
                    .U8("brake", "Brake", G + "Brake")
                    .U8("throttle", "Throttle", G + "Throttle")
                    .Done(),
            },
            new()
            {
                RecordType = 0x12, Key = "type-12", Label = "Dashboard 12 — GT (B)", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new Fields()
                    .Pack10x4("tp", "Tyre pressure", Corners, TyrePressProps)
                    .U16("fuelUsed", "Fuel used", "")
                    .U16("fuelAvg", "Fuel avg / lap", "")
                    .U16("fuelRem", "Fuel remaining", G + "Fuel")
                    .U24("llt", "Last lap time", G + "LastLapTime", MsScale)
                    .U8("lap", "Lap", G + "CurrentLap")
                    .Nibbles("tc", "TC level", G + "TCLevel", "ecu", "ECU map", G + "EngineMap")
                    .U8("tcCut", "TC cut", "")
                    .Flags(("lowBeam", "Low beam", ""), ("highBeam", "High beam", ""), ("rain", "Rain light", ""),
                           ("wipers", "Wipers", ""), ("ign", "Ignition", ""), ("engine", "Engine on", ""), ("tyreType", "Tyre type", ""))
                    .U8("sector", "Sector", G + "CurrentSectorIndex")
                    .U8("redline", "Redline reached", "")
                    .Done(),
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
            // 0x0d = background tyre/status cache (IsBackground) — appended to every page whose
            // display shows tyre temps / pressure / track-air / car-count / lap-count that its
            // primary record doesn't carry. Streamed sparsely (see the driver), not full-rate.
            { 0, new byte[] { 0x01, 0x0d } },   // shows inner+outer tyre temps; primary has inner only
            { 1, new byte[] { 0x02, 0x0d } },   // brake dash also shows tyre temps
            { 2, new byte[] { 0x06, 0x0d } },
            { 3, new byte[] { 0x06, 0x0d } },
            { 4, new byte[] { 0x03 } },
            { 5, new byte[] { 0x04 } },
            { 6, new byte[] { 0x04 } },
            { 7, new byte[] { 0x06, 0x0d } },
            { 8, new byte[] { 0x05 } },         // already carries car-count + lap-count
            { 9, new byte[] { 0x03 } },
            { 10, new byte[] { 0x08 } },        // tyre dash carries its own inner+outer temps
            { 11, new byte[] { 0x09, 0x0d } },  // GT timing: tyres/pressure/track/air/lap-count
            { 12, new byte[] { 0x0e, 0x0d } },  // race info: car-count + lap-count total
            { 13, new byte[] { 0x04 } },
            { 14, new byte[] { 0x04 } },
            { 15, new byte[] { 0x0c } },
            { 16, new byte[] { 0x0c } },        // timing/gap page: PitHouse streams type-0c (ACC dual-capture)
            { 17, new byte[] { 0x11, 0x12, 0x0d } },  // GT style: tyres + totals
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

        // ── Per-profile override resolution ─────────────────────────────────
        // Turn (catalog default, user override, payload length) into the effective
        // wire layout. The driver, emitter, and UI all go through this so they agree
        // on where a field sits and how it is packed. A null override field means
        // "use the catalog default" (dict-missing ≠ explicit-off).

        /// <summary>Resolve the effective byte span + encoding for one field, applying
        /// any user override on top of the catalog default. Start/end are clamped to the
        /// record's data range <c>[5, payloadLen-1]</c>, width is clamped to 1..3 (the
        /// FSR1's byte-aligned encodings), and endianness only matters for width 2.</summary>
        internal static (int[] offsets, Fsr1Encoding encoding) ResolveLayout(
            Fsr1FieldDef def, Fsr1FieldMapping? m, int payloadLen)
        {
            int defStart = def.Offsets.Length > 0 ? def.Offsets[0] : 5;
            int defEnd = def.Offsets.Length > 0 ? def.Offsets[def.Offsets.Length - 1] : defStart;
            int start = m?.StartOffset ?? defStart;
            int end = m?.EndOffset ?? defEnd;

            int maxOff = payloadLen - 1;
            if (start < 5) start = 5;
            if (start > maxOff) start = maxOff;
            if (end < start) end = start;
            if (end > maxOff) end = maxOff;
            if (end - start > 2) end = start + 2;   // width ≤ 3

            int width = end - start + 1;
            Fsr1Encoding enc = width switch
            {
                1 => Fsr1Encoding.U8,
                2 => (m?.LittleEndian ?? (def.Encoding == Fsr1Encoding.U16_LE))
                        ? Fsr1Encoding.U16_LE : Fsr1Encoding.U16_BE,
                _ => Fsr1Encoding.U24_BE,
            };

            var offsets = new int[width];
            for (int i = 0; i < width; i++) offsets[i] = start + i;
            return (offsets, enc);
        }

        /// <summary>Output ceiling for a resolved encoding (mirrors
        /// <see cref="Fsr1FieldDef.OutputMax"/> but for the overridden width): the
        /// field's <paramref name="fullScale"/> cap if set, else the encoding capability.</summary>
        internal static long OutputMaxFor(Fsr1Encoding enc, long fullScale)
        {
            if (fullScale > 0) return fullScale;
            return enc switch
            {
                Fsr1Encoding.U8 => 0xFF,
                Fsr1Encoding.U16_BE => 0xFFFF,
                Fsr1Encoding.U16_LE => 0xFFFF,
                Fsr1Encoding.U24_BE => 0xFFFFFF,
                _ => 0xFF,
            };
        }

        /// <summary>Output ceiling for a packed field of <paramref name="bitWidth"/> bits:
        /// the field's <paramref name="fullScale"/> cap if set, else the full bit-width range
        /// <c>(1 &lt;&lt; bitWidth) - 1</c>. Mirrors <see cref="OutputMaxFor"/> for sub-byte fields.</summary>
        internal static long BitOutputMax(int bitWidth, long fullScale)
        {
            if (fullScale > 0) return fullScale;
            if (bitWidth <= 0) return 0;
            if (bitWidth >= 63) return long.MaxValue;
            return (1L << bitWidth) - 1;
        }

        /// <summary>
        /// The record's fields resolved into a GUARANTEED gapless, non-overlapping partition
        /// of the data range <c>[5, PayloadLen-1]</c> — the single layout source of truth for
        /// the driver, emitter, viz, UI, and probe. Composes catalog + synthetic fields, takes
        /// each field's desired span (<see cref="ResolveLayout"/>, catalog default merged with
        /// the per-profile override), sorts by start, then tiles left-to-right preserving each
        /// field's width where room allows; the last field absorbs any slack to the record end.
        ///
        /// This both enforces the invariant for new edits and AUTO-REPAIRS already-broken
        /// stored configs (gaps/overlaps from earlier builds) at use time — every byte ends up
        /// owned by exactly one field, so the wheel never renders a dead (gap) byte. Field
        /// order and identity are preserved; only spans are reapportioned to close gaps/overlaps.
        /// </summary>
        internal static System.Collections.Generic.IReadOnlyList<Fsr1Slot>
            ResolvePartition(MozaPlugin? plugin, Fsr1Dashboard dash)
        {
            const int dataMin = 5;
            int dataMax = dash.PayloadLen - 1;
            int dataBytes = dataMax - dataMin + 1;
            var empty = System.Array.Empty<Fsr1Slot>();
            if (dataBytes <= 0) return empty;

            var composed = Fsr1FieldComposer.FieldsFor(plugin, dash);

            // A record enters BIT mode as soon as any composed field carries a sub-byte override.
            // Every current catalog dash + byte-only profile stays in BYTE mode, so the tiler
            // below is the unchanged algorithm and those records resolve byte-identically.
            bool anyBit = false;
            foreach (var f in composed)
            {
                if (f.BitWidth > 0) { anyBit = true; break; }   // catalog default is bit-packed
                var mm = plugin?.GetFsr1FieldMapping(dash.Key, f.FieldId);
                if (mm != null && (mm.StartBit != null || mm.BitWidth != null)) { anyBit = true; break; }
            }
            if (anyBit) return ResolveBitPartition(plugin, dash, composed, dataMin, dataMax);

            // ── BYTE MODE (unchanged gapless byte tiler, wrapped as byte-aligned slots) ──
            // Desired width + endianness per composed field (catalog + synthetic splits),
            // ordered by where the field wants to sit.
            var items = new System.Collections.Generic.List<(Fsr1FieldDef f, int start, int width, bool le)>();
            foreach (var f in composed)
            {
                var m = plugin?.GetFsr1FieldMapping(dash.Key, f.FieldId);
                var (offs, enc) = ResolveLayout(f, m, dash.PayloadLen);
                items.Add((f, offs[0], offs.Length, enc == Fsr1Encoding.U16_LE));
            }
            items.Sort((a, b) => a.start.CompareTo(b.start));

            // A partition can't have more parts than bytes; drop any overflow (e.g. stale
            // synthetic splits piled onto a since-rebuilt catalog) so the tiling stays valid.
            int n = System.Math.Min(items.Count, dataBytes);
            if (n < items.Count)
                MozaLog.Warn($"[AZOM] FSR1 partition {dash.Key}: {items.Count} fields for {dataBytes} bytes — dropping {items.Count - n}.");
            if (n == 0) return empty;

            // Distribute the data bytes across the fields: each gets its desired width clamped
            // to [minW, maxW], where minW forces a field to grow when the remaining fields
            // can't otherwise reach the end (≤3 each), and maxW makes it shrink to leave ≥1
            // byte for each remaining field. Result tiles [5, dataMax] exactly — no gap/overlap.
            var result = new Fsr1Slot[n];
            int cursor = dataMin;
            for (int i = 0; i < n; i++)
            {
                int after = n - 1 - i;
                int bytesLeft = dataMax - cursor + 1;
                int maxW = System.Math.Min(3, bytesLeft - after);
                int minW = System.Math.Max(1, bytesLeft - 3 * after);
                int w = items[i].width;
                if (w < minW) w = minW;
                if (w > maxW) w = maxW;
                if (w < 1) w = 1;  // defensive — only if too few fields to cover the range
                var offsets = new int[w];
                for (int k = 0; k < w; k++) offsets[k] = cursor + k;
                Fsr1Encoding enc = w switch
                {
                    1 => Fsr1Encoding.U8,
                    2 => items[i].le ? Fsr1Encoding.U16_LE : Fsr1Encoding.U16_BE,
                    _ => Fsr1Encoding.U24_BE,
                };
                result[i] = new Fsr1Slot(items[i].f, offsets, enc, cursor * 8, w * 8, msbFirst: true);
                cursor += w;
            }
            return result;
        }

        /// <summary>
        /// Resolve a record that has at least one sub-byte / bit-packed field. Unlike byte mode
        /// this does NOT reapportion spans — explicit bit ranges (and their intentional spare
        /// bits) are honoured. Each field's bit run is resolved (packed → explicit; byte-aligned
        /// neighbours → their byte span ×8), sorted by bit, and pushed right on bit overlap so no
        /// two fields ever own the same bit. Byte-level gaps are warned but benign (an uncovered
        /// byte stays 0, which <c>NewFrame</c> already zeroes).
        /// </summary>
        private static System.Collections.Generic.IReadOnlyList<Fsr1Slot> ResolveBitPartition(
            MozaPlugin? plugin, Fsr1Dashboard dash,
            System.Collections.Generic.IReadOnlyList<Fsr1FieldDef> composed, int dataMin, int dataMax)
        {
            int bitLo = dataMin * 8;
            int bitHi = (dataMax + 1) * 8;   // exclusive

            var items = new System.Collections.Generic.List<(Fsr1FieldDef f, int bo, int bw, Fsr1Encoding enc, bool msb, bool aligned)>();
            foreach (var f in composed)
            {
                var m = plugin?.GetFsr1FieldMapping(dash.Key, f.FieldId);
                int defStart = f.Offsets.Length > 0 ? f.Offsets[0] : 5;
                int defEnd = f.Offsets.Length > 0 ? f.Offsets[f.Offsets.Length - 1] : defStart;
                bool mapBit = m != null && (m.StartBit != null || m.BitWidth != null);
                if (mapBit || f.BitWidth > 0)
                {
                    // Bit geometry from the mapping override if present, else the catalog default.
                    int byteStart = m?.StartOffset ?? defStart;
                    int startBit = m?.StartBit ?? f.StartBit;
                    int bo = byteStart * 8 + startBit;
                    int bw = m?.BitWidth ?? (f.BitWidth > 0 ? f.BitWidth : (defEnd - defStart + 1) * 8);
                    items.Add((f, bo, bw, Fsr1Encoding.U24_BE, false, aligned: false));
                }
                else
                {
                    var (offs, enc) = ResolveLayout(f, m, dash.PayloadLen);
                    items.Add((f, offs[0] * 8, offs.Length * 8, enc, true, aligned: true));
                }
            }
            items.Sort((a, b) => a.bo.CompareTo(b.bo));

            var result = new System.Collections.Generic.List<Fsr1Slot>(items.Count);
            int prevEnd = bitLo;
            foreach (var it in items)
            {
                int bo = it.bo, bw = it.bw < 1 ? 1 : it.bw;
                if (bo < bitLo) bo = bitLo;
                if (bo < prevEnd)   // bit overlap — push right so no two fields own a bit
                {
                    MozaLog.Warn($"[AZOM] FSR1 bit partition {dash.Key}: field {it.f.FieldId} overlaps at bit {bo}, pushed to {prevEnd}.");
                    bo = prevEnd;
                }
                if (bo >= bitHi)
                {
                    MozaLog.Warn($"[AZOM] FSR1 bit partition {dash.Key}: field {it.f.FieldId} past record end — dropped.");
                    continue;
                }
                if (bo + bw > bitHi) bw = bitHi - bo;   // clamp width into the record
                prevEnd = bo + bw;

                int byteStart = bo >> 3, byteEnd = (bo + bw - 1) >> 3;
                var offsets = new int[byteEnd - byteStart + 1];
                for (int k = 0; k < offsets.Length; k++) offsets[k] = byteStart + k;
                // Keep the real byte encoding only for runs that stayed byte-aligned (U16_LE
                // handling); anything sub-byte (incl. a byte field pushed off a boundary) → packed.
                bool stillAligned = it.aligned && (bo & 7) == 0 && (bw & 7) == 0;
                Fsr1Encoding enc = stillAligned ? it.enc : Fsr1Encoding.U24_BE;
                result.Add(new Fsr1Slot(it.f, offsets, enc, bo, bw, it.msb));
            }

            // Coverage check (warn-only): a data byte owned by no field renders 0 on the wheel.
            for (int b = dataMin; b <= dataMax; b++)
            {
                bool covered = false;
                foreach (var s in result) if (s.ByteStart <= b && b <= s.ByteEnd) { covered = true; break; }
                if (!covered)
                    MozaLog.Warn($"[AZOM] FSR1 bit partition {dash.Key}: data byte {b} uncovered (renders 0).");
            }
            return result;
        }

        /// <summary>Debug self-check: every live record's DEFAULT partition must tile
        /// <c>[5, PayloadLen-1]</c> with no BIT overlap and every data byte covered. Bit-aware
        /// (handles the 10-bit tyre/pressure packs + compact bundles). Logs each violation; returns
        /// false if any. Run once at startup so a catalog edit that breaks a layout is caught.</summary>
        internal static bool ValidateDefaultPartitions()
        {
            bool ok = true;
            foreach (var dash in LiveDashboards)
            {
                var slots = ResolvePartition(null, dash);   // plugin=null → catalog defaults only
                // 1. No two fields own the same bit (sort by bit, ensure non-overlap).
                var ord = new System.Collections.Generic.List<Fsr1Slot>(slots);
                ord.Sort((a, b) => a.BitOffset.CompareTo(b.BitOffset));
                int prevEnd = 5 * 8;
                foreach (var s in ord)
                {
                    if (s.BitOffset < prevEnd)
                    {
                        MozaLog.Warn($"[AZOM] FSR1 catalog {dash.Key}: field {s.Field.FieldId} bit {s.BitOffset} overlaps prior end {prevEnd}.");
                        ok = false;
                    }
                    prevEnd = System.Math.Max(prevEnd, s.BitOffset + s.BitWidth);
                }
                // 2. Every data byte [5, PayloadLen-1] is covered by some field.
                for (int b = 5; b <= dash.PayloadLen - 1; b++)
                {
                    bool covered = false;
                    foreach (var s in slots) if (s.ByteStart <= b && b <= s.ByteEnd) { covered = true; break; }
                    if (!covered)
                    {
                        MozaLog.Warn($"[AZOM] FSR1 catalog {dash.Key}: data byte {b} uncovered.");
                        ok = false;
                    }
                }
                // 3. Fields must not spill past the record.
                if (prevEnd > dash.PayloadLen * 8)
                {
                    MozaLog.Warn($"[AZOM] FSR1 catalog {dash.Key}: fields end at bit {prevEnd}, past record end {dash.PayloadLen * 8}.");
                    ok = false;
                }
            }
            return ok;
        }
    }
}
