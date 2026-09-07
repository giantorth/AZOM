using System.Collections.Generic;
using System.Linq;

namespace MozaPlugin.Telemetry.Display
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
        // Records are laid out with an auto-advancing cursor (data starts at byte 5), mirroring how
        // PitHouse concatenates each field's whole-byte output in order on the wire.
        // U8/U16/U24 advance whole bytes (big-endian on the wire);
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
            public Fields Pack10x4(string idPrefix, string labelPrefix, string[] suffix, string[] props, double bias = 0.0, double scale = 1.0)
            {
                for (int i = 0; i < 4; i++)
                    Bits(idPrefix + suffix[i], labelPrefix + " " + suffix[i], 10, i < props.Length ? props[i] : "", scale: scale, bias: bias);
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
            /// <summary>GT light/flag bundle: 2-bit light stage at [0:2] (0=off, 1=low beam,
            /// 2=high beam — one field, not two beam bits; tester box-verified), then 1-bit
            /// flags from bit 2.</summary>
            public Fields LightStageFlags(params (string id, string label, string prop)[] flags)
            {
                int b = _bit;
                _list.Add(MakeBits("lightStage", "Light stage", b, 2, "", fullScale: 2));
                for (int i = 0; i < flags.Length; i++)
                    _list.Add(MakeBits(flags[i].id, flags[i].label, b + 2 + i, 1, flags[i].prop));
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
        // Player-extracted car status root. m_carStatusData01 is array SLOT 0, not the player;
        // the ERS members under it also fail to resolve on some SimHub builds (F1 2020 bundle
        // 2026-07-30), so the ERS channels bind here instead.
        private const string F1RawPlayerStatus = "DataCorePlugin.GameRawData.PlayerCarStatusData.";
        // ERS deploy mode 0–3 (3 = overtake) drives the firmware's OVERTAKE highlight; the game value
        // maps straight into the 2-bit field. Live delta to session best (signed seconds) for gap fields.
        private const string ErsDeployMode = F1RawPlayerStatus + "m_ersDeployMode";
        private const string LiveDelta = "PersistantTrackerPlugin.SessionBestLiveDeltaSeconds";
        // SimHub predicted/estimated final lap time (projected from session-best pace). Resolves as a
        // TimeSpan → TotalSeconds (PropertyCoercion), ×MsScale to ms for the 24-bit field. Variants:
        // _AllTimeBest / _SessionBestSimhub if the user tracks a different reference.
        private const string EstLapTime = "PersistantTrackerPlugin.EstimatedLapTime_SessionBest";
        // Fuel remaining as laps of range (signed: negative = short). Wire = laps × 100 (verified).
        private const string FuelRemainLaps = F1RawStatus + "m_fuelRemainingLaps";
        // ERS this-lap energy (Joules). Deploy bar shows budget REMAINING = 100 − deployed/40000 (of the
        // 4 MJ deploy cap; verified clean). Harvest bar = harvested/20000 (of the 2 MJ MGU-K cap; the
        // capture's harvest data was sparse so the scale is approximate — tune on-wheel if needed).
        private const string ErsDeployedThisLap = F1RawPlayerStatus + "m_ersDeployedThisLap";
        private const string ErsHarvestedThisLap = F1RawPlayerStatus + "m_ersHarvestedThisLapMGUK";
        // Fuel mix / class 0–3 (lean/standard/rich/max).
        private const string FuelMix = F1RawStatus + "m_fuelMix";
        // SimHub's computed fuel consumption (litres/lap); sent ×100 like fuelRem.
        private const string FuelPerLap = "DataCorePlugin.Computed.Fuel_LitersPerLap";
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
        // type-0f packs its tyre-TEMP 4×10-bit group in the order RR,RL,FR,FL (capture decode);
        // its PRESSURE pack is normal FL,FR,RL,RR (tester box-verified — the RR,RL,FR,FL guess
        // rendered diagonally crossed).
        private static readonly string[] GtTyreCorners = { "RR", "RL", "FR", "FL" };
        private static readonly string[] OuterTempProps =   // outer tyre surface temp, order RR,RL,FR,FL
        {
            G + "TyreTemperatureRearRight", G + "TyreTemperatureRearLeft",
            G + "TyreTemperatureFrontRight", G + "TyreTemperatureFrontLeft",
        };
        // Tyre-temp 10-bit fields carry a +300 wire bias (firmware decodes value−300 °C).
        private const double TyreTempBias = 300.0;
        // Lap-time 24-bit fields carry the game value in milliseconds (SimHub seconds × 1000).
        private const double MsScale = 1000.0;

        // Per-record field layouts, matched byte-for-byte against PitHouse's group-0x42 wire streams.
        // Encodings: tyre temp / tyre pressure = 4×10-bit LSB packs (5 bytes), brake temp / speed /
        // rpm / int16 = 16-bit big-endian, lap times = 24-bit BE (ms), int8 / gear / temp = 8-bit,
        // GearDrsErs / compact bundles = sub-byte LSB. See docs § Group 0x42.
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
                    // Wear gauges show REMAINING %; SimHub TyreWear* is % worn → wire = 100 − x.
                    .U8("twFL", "Tyre wear FL", G + "TyreWearFrontLeft", bias: 100.0, scale: -1.0)
                    .U8("twFR", "Tyre wear FR", G + "TyreWearFrontRight", bias: 100.0, scale: -1.0)
                    .U8("twRL", "Tyre wear RL", G + "TyreWearRearLeft", bias: 100.0, scale: -1.0)
                    .U8("twRR", "Tyre wear RR", G + "TyreWearRearRight", bias: 100.0, scale: -1.0)
                    // Damage boxes, in on-wheel gauge order: FL wing, FR wing, ICE, gearbox,
                    // REAR wing (tester-confirmed on dashboards 5/10 — the last three read one
                    // gauge earlier than the old labels claimed). Unlike the tyre boxes these
                    // carry DAMAGE, not remaining %, so no 100− inversion.
                    // Per-part damage from the F1 raw player-car status — the generic
                    // `CarDamage1..5` are one undifferentiated pool per game, so they can't tell
                    // a wing from a gearbox. Bias +1: the gauge leaves 0 unlit, so an undamaged
                    // part reads 1 and renders green (tester-verified on dashboards 5/10).
                    // FieldIds are historical, kept so existing profile overrides stay attached
                    // to the same gauge.
                    .U8("wwFL", "Front wing damage FL", F1RawPlayerStatus + "m_frontLeftWingDamage", bias: 1.0)
                    .U8("wwFR", "Front wing damage FR", F1RawPlayerStatus + "m_frontRightWingDamage", bias: 1.0)
                    .U8("wwR", "ICE damage", F1RawPlayerStatus + "m_engineDamage", bias: 1.0)
                    .U8("engWear", "Gearbox damage", F1RawPlayerStatus + "m_gearBoxDamage", bias: 1.0)
                    .U8("gbxWear", "Rear wing damage", F1RawPlayerStatus + "m_rearWingDamage", bias: 1.0)
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
                    // Wear gauges show REMAINING %; SimHub TyreWear* is % worn → wire = 100 − x.
                    .U8("twFL", "Tyre wear FL", G + "TyreWearFrontLeft", bias: 100.0, scale: -1.0)
                    .U8("twFR", "Tyre wear FR", G + "TyreWearFrontRight", bias: 100.0, scale: -1.0)
                    .U8("twRL", "Tyre wear RL", G + "TyreWearRearLeft", bias: 100.0, scale: -1.0)
                    .U8("twRR", "Tyre wear RR", G + "TyreWearRearRight", bias: 100.0, scale: -1.0)
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
                // Background timing/bias cache: no page selects it as a primary — PitHouse
                // interleaves it sparsely (~1%) alongside type-0e on the race-info page
                // ("Dash 12 and 13 assetto corsa" capture: 55×0b per 5204×0e, b1/b2=00/04).
                // Bias is ×10 on the wire (capture: 0x021B = 53.9%), like type-10's.
                RecordType = 0x0b, Key = "type-0b", Label = "Dashboard 0B — timing / bias", IsLive = true, IsBackground = true,
                PayloadLen = 15, LiveB1 = 0x00, LiveB2 = 0x04,
                Fields = new Fields()
                    .U24("llt", "Last lap time", G + "LastLapTime", MsScale)
                    .U24("blt", "Best lap time", G + "BestLapTime", MsScale)
                    .U16("fuelTemp", "Fuel temp", "")
                    .U16("bias", "Brake bias", G + "BrakeBias", scale: 10.0)
                    .Done(),
            },
            new()
            {
                RecordType = 0x0c, Key = "type-0c", Label = "Dashboard 0C — timing / RPM", IsLive = true,
                PayloadLen = 18, LiveB1 = 0x00, LiveB2 = 0x00,
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
                    // Outer pack FIRST (data[5-9]), inner second — the wheel renders them in
                    // this order (tester-verified on the brake dash; type-08 is the reverse).
                    .Pack10x4("tto", "Tyre outer", Corners, SurfaceTempProps, TyreTempBias)
                    .Pack10x4("tti", "Tyre inner", Corners, InnerTempProps, TyreTempBias)
                    .U8("cars", "Car count", G + "OpponentsCount")
                    .U8("lap", "Lap", G + "CurrentLap")
                    .U8("laps", "Lap count", G + "TotalLaps")
                    .Pack10x4("tp", "Tyre pressure", Corners, TyrePressProps, scale: 10.0)
                    .U8("trackT", "Track temp", G + "RoadTemperature")
                    .U8("airT", "Air temp", G + "AirTemperature")
                    .Done(),
            },
            new()
            {
                RecordType = 0x0e, Key = "type-0e", Label = "Dashboard 0E — race info", IsLive = true,
                PayloadLen = 24, LiveB1 = 0x08, LiveB2 = 0x00,
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
                    .U8("boost", "Boost", G + "TurboPressure")
                    .U8("ecu", "ECU map", G + "EngineMap")
                    .U8("tc2", "TC2", "")
                    .U8("fuelClass", "Fuel mix", FuelMix)
                    .U8("gear", "Gear", G + "Gear", bias: 1.0)
                    .Done(),
            },
            new()
            {
                // GT dashboard background record: tyre / brake status. Streamed alongside the primary
                // type-0x11 on the GT page (verified in the "Dashboard 17 AC" capture). Layout: 4×10-bit
                // outer tyre temp (RR,RL,FR,FL per capture decode), 4×U16 brake temp (FL,FR,RL,RR),
                // 4×10-bit tyre pressure (FL,FR,RL,RR — tester box-verified; the capture-decode
                // RR,RL,FR,FL guess rendered diagonally crossed), U8 lap = 19 bytes.
                RecordType = 0x0f, Key = "type-0f", Label = "Dashboard 0F — tyre / brake status", IsLive = true, IsBackground = true,
                PayloadLen = 24, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new Fields()
                    .Pack10x4("tto", "Tyre outer", GtTyreCorners, OuterTempProps, TyreTempBias)
                    .U16("btFL", "Brake temp FL", G + "BrakeTemperatureFrontLeft")
                    .U16("btFR", "Brake temp FR", G + "BrakeTemperatureFrontRight")
                    .U16("btRL", "Brake temp RL", G + "BrakeTemperatureRearLeft")
                    .U16("btRR", "Brake temp RR", G + "BrakeTemperatureRearRight")
                    .Pack10x4("tp", "Tyre pressure", Corners, TyrePressProps, scale: 10.0)
                    .U8("lap", "Lap", G + "CurrentLap")
                    .Done(),
            },
            new()
            {
                // GT dashboard background record: fuel / lights / status. Layout: 2×U24 best/last lap ms,
                // 3×U16 brake-bias/fuel-remaining/fuel-avg-per-lap, 4×U8 car-count/TC/TC-cut/ECU-map,
                // 7×1-bit light bundle, 2×U8 wiper-class/redline = 19 bytes.
                RecordType = 0x10, Key = "type-10", Label = "Dashboard 10 — fuel / lights / status", IsLive = true, IsBackground = true,
                PayloadLen = 24, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new Fields()
                    .U24("blt", "Best lap time", G + "BestLapTime", MsScale)
                    .U24("llt", "Last lap time", G + "LastLapTime", MsScale)
                    .U16("bias", "Brake bias", G + "BrakeBias", scale: 10.0)
                    .U16("fuelRem", "Fuel remaining", G + "Fuel", scale: 100.0)
                    .U16("fuelAvg", "Fuel avg / lap", FuelPerLap, scale: 100.0)
                    .U8("cars", "Car count", G + "OpponentsCount")
                    .U8("tc", "TC level", G + "TCLevel")
                    .U8("tcCut", "TC-R", "")   // on-wheel gauge is labelled TC-R
                    .U8("ecu", "ECU map", G + "EngineMap")
                    .LightStageFlags(("rain", "Rain light", ""), ("wipers", "Wipers", ""),
                           ("ign", "Ignition", G + "EngineIgnitionOn"), ("engine", "Engine on", G + "EngineStarted"), ("tyreType", "Tyre type", ""))
                    .U8("wiperCls", "Wiper class", "")
                    .U8("redline", "Redline reached", G + "CarSettings_RPMRedLineReached")
                    .Done(),
            },
            new()
            {
                RecordType = 0x11, Key = "type-11", Label = "Dashboard 11 — GT (A)", IsLive = true,
                PayloadLen = 25, LiveB1 = 0x00, LiveB2 = 0x00,
                Fields = new Fields()
                    .U24("stl", "Session time left", SessionTimeLeft, MsScale)
                    .U24("elt", "Estimated lap time", EstLapTime, MsScale)
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
                    .Pack10x4("tp", "Tyre pressure", Corners, TyrePressProps, scale: 10.0)
                    .U16("fuelUsed", "Fuel used", "")
                    .U16("fuelAvg", "Fuel avg / lap", FuelPerLap, scale: 100.0)
                    .U16("fuelRem", "Fuel remaining", G + "Fuel", scale: 100.0)
                    .U24("llt", "Last lap time", G + "LastLapTime", MsScale)
                    .U8("lap", "Lap", G + "CurrentLap")
                    .Nibbles("tc", "TC level", G + "TCLevel", "ecu", "ECU map", G + "EngineMap")
                    .U8("tcCut", "TC-R", "")   // on-wheel gauge is labelled TC-R
                    .LightStageFlags(("rain", "Rain light", ""), ("wipers", "Wipers", ""),
                           ("ign", "Ignition", G + "EngineIgnitionOn"), ("engine", "Engine on", G + "EngineStarted"), ("tyreType", "Tyre type", ""))
                    .U8("sector", "Sector", G + "CurrentSectorIndex")
                    .U8("redline", "Redline reached", G + "CarSettings_RPMRedLineReached")
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
            { 12, new byte[] { 0x0e, 0x0b } },  // race info + timing/bias cache (fuel temp, brake
                                                // bias, best/last lap). PitHouse streams 0e+0b here,
                                                // NOT 0d ("Dash 12 and 13 assetto corsa" capture).
            { 13, new byte[] { 0x04 } },
            { 14, new byte[] { 0x04 } },
            { 15, new byte[] { 0x0c } },
            { 16, new byte[] { 0x11, 0x0f, 0x10 } },  // user "dashboard 17" (Param-6 16): primary type-0x11 (gap) + background type-0f (tyre/brake) + type-10 (fuel/lights) — all 3 verified in the "Dashboard 17 AC" capture
            { 17, new byte[] { 0x11, 0x12, 0x0d } },  // Param-6 17: GT default (unverified — no capture yet)
            { 18, new byte[] { 0x0c } },
        };

        // Per-index sub-header (b1/b2) override — CAPTURE-PROVEN entries only. The 2026-08
        // cleanup emptied this dict (earlier values like 0x27fe/0x0b88 were session leftovers),
        // but the "Dash 12 and 13 assetto corsa" PitHouse capture shows sustained page-specific
        // headers on the GT timing pages (5204×0e all b1=0d/b2=80 on page 12; 09 at 01/80 on
        // page 11) — the wheel gates records on b1/b2, and streaming the per-type default
        // header on these pages is the prime suspect in the 2026-08-05 display-wedge capture.
        // Non-background records only; anything absent falls back to the type's LiveB1/LiveB2.
        private static readonly Dictionary<int, (byte b1, byte b2)> IndexDescriptorOverride = new()
        {
            { 11, (0x01, 0x80) },   // type-09 on user dash 12 ("Dash 12 and 13 AC" capture)
            { 12, (0x0d, 0x80) },   // type-0e on user dash 13 (same capture, 5204 frames)
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
        public static Fsr1Dashboard[] ByIndex(int index) =>
            s_byIndex.TryGetValue(index, out var arr) ? arr : System.Array.Empty<Fsr1Dashboard>();

        // Firmware-fixed, so resolved once. ByIndex runs on every 20 ms driver tick
        // and used to allocate a list, an array and LINQ enumerators per call.
        // Declared after the two tables above — static initializers run in order.
        private static readonly Dictionary<int, Fsr1Dashboard[]> s_byIndex = BuildByIndex();

        private static Dictionary<int, Fsr1Dashboard[]> BuildByIndex()
        {
            var map = new Dictionary<int, Fsr1Dashboard[]>(IndexToRecordTypes.Count);
            foreach (var kv in IndexToRecordTypes)
            {
                bool hasDesc = IndexDescriptorOverride.TryGetValue(kv.Key, out var desc);
                var list = new List<Fsr1Dashboard>(kv.Value.Length);
                foreach (var t in kv.Value)
                {
                    var d = ByType(t);
                    if (d == null) continue;
                    if (hasDesc && !d.IsBackground)
                        d = new Fsr1Dashboard
                        {
                            RecordType = d.RecordType, Key = d.Key, Label = d.Label,
                            PayloadLen = d.PayloadLen, LiveB1 = desc.b1, LiveB2 = desc.b2,
                            IsLive = d.IsLive, IsBackground = d.IsBackground, Fields = d.Fields,
                        };
                    list.Add(d);
                }
                map[kv.Key] = list.ToArray();
            }
            return map;
        }

        // ── Layout resolution ───────────────────────────────────────────────
        // Field GEOMETRY (byte span / bit packing / endianness) is catalog-fixed —
        // only the channel and Scale/Bias gain are user-assignable. The driver,
        // emitter, viz, and UI all read the same per-record slot partition.

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
        /// The record's fields as wire slots over the data range <c>[5, PayloadLen-1]</c>,
        /// sorted by bit position — the single layout source of truth for the driver,
        /// emitter, viz, and UI. Geometry comes straight from the catalog (validated once
        /// at startup by <see cref="ValidateDefaultPartitions"/>); partitions are cached
        /// per record type.
        /// </summary>
        internal static System.Collections.Generic.IReadOnlyList<Fsr1Slot> ResolvePartition(Fsr1Dashboard dash)
        {
            if (dash == null) return System.Array.Empty<Fsr1Slot>();
            lock (_partitions)
            {
                if (_partitions.TryGetValue(dash.RecordType, out var cached)) return cached;
                var slots = BuildPartition(dash);
                _partitions[dash.RecordType] = slots;
                return slots;
            }
        }

        private static readonly Dictionary<byte, System.Collections.Generic.IReadOnlyList<Fsr1Slot>> _partitions
            = new Dictionary<byte, System.Collections.Generic.IReadOnlyList<Fsr1Slot>>();

        private static System.Collections.Generic.IReadOnlyList<Fsr1Slot> BuildPartition(Fsr1Dashboard dash)
        {
            var fields = dash.Fields;
            var result = new Fsr1Slot[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                int byteStart = f.Offsets.Length > 0 ? f.Offsets[0] : 5;
                if (f.BitWidth > 0)   // sub-byte / bit-packed (LSB-first wire order)
                    result[i] = new Fsr1Slot(f, f.Offsets, Fsr1Encoding.U24_BE,
                        byteStart * 8 + f.StartBit, f.BitWidth, msbFirst: false);
                else                  // byte-aligned
                    result[i] = new Fsr1Slot(f, f.Offsets, f.Encoding,
                        byteStart * 8, f.Offsets.Length * 8, msbFirst: true);
            }
            System.Array.Sort(result, (a, b) => a.BitOffset.CompareTo(b.BitOffset));
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
                var slots = ResolvePartition(dash);
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
