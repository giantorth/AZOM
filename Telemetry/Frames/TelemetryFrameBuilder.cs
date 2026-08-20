using System;
using System.Diagnostics;
using GameReaderCommon;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.TestMode;
using MozaPlugin.Telemetry.Frames;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// Assembles a complete Moza telemetry serial frame from game data.
    ///
    /// Frame format (docs/protocol/telemetry/live-stream.md):
    ///   7E [N] 43 17 7D 23 32 00 23 32 [flag] 20 [data...] [checksum]
    ///
    /// Header is 12 bytes fixed, followed by variable data, followed by 1 checksum byte.
    /// </summary>
    public class TelemetryFrameBuilder
    {
        private const int HeaderLen = 12; // start(1) + N(1) + group(1) + dev(1) + cmdId(2) + prefix(4) + flag(1) + const(1)
        private const int ChecksumLen = 1;

        private readonly DashboardProfile _profile;
        private readonly Func<GameDataSnapshot, double>[] _resolvers;

        // Per-channel encode path resolved once at construction (a channel's
        // compression code never changes), so the per-frame loop skips the three
        // CompressionTable string-dictionary lookups TelemetryEncoder would do.
        private enum EncKind : byte { Float, Double, Bits, LocationPair, RadarPair }
        private readonly EncKind[] _encKind;
        private readonly Func<double, ulong>?[] _encFn;
        // For LocationPair / RadarPair channels: the source car index.
        // LocationPair: -1 = local car (patch/Location); >=0 = opponent index.
        // RadarPair: >=0 = opponent index (patch/ri<N>).
        // NotLocation for every other channel.
        private readonly int[] _locIndex;
        private const int NotLocation = int.MinValue;

        /// <summary>True when any channel in this builder's profile is a
        /// LocationPair/RadarPair — i.e. it actually reads the snapshot's per-car
        /// position arrays. Lets the caller skip GameDataSnapshot's reflection +
        /// per-opponent allocation entirely when no channel consumes them
        /// (the shipped default, with radar/track-map channels disabled).</summary>
        public bool NeedsCarPositions { get; private set; }
        // Track-map location_t packing. The 64-bit slot is, little-endian,
        //   [u16 Y(elev) | u24 Z | u24 X]
        // where each field = clamp(center + round(scale * worldMetres)).
        // Reverse-engineered from PitHouse's AC captures and verified to <0.3 m
        // against AC CarCoordinates on Imola (NOT two float32 — see
        // docs/protocol/telemetry/track-map.md). The wheel's map widget does NOT
        // auto-fit: it only renders coordinates inside its per-track field window
        // (a different scale/centre draws nothing), so the scale + centres are
        // TRACK-SPECIFIC. They are now resolved per track by TrackMapTransform
        // (from the track's map.ini), cached here and re-resolved on track change.
        private TrackMapTransform _mapTransform = TrackMapTransform.Fallback();
        private string? _mapTransformTrack;

        // Pre-allocated buffers reused every frame to avoid GC pressure
        private readonly byte[] _frameBuffer;
        private readonly TelemetryBitWriter? _bitWriter;

        // Frame-to-frame radar heading state. The radar ri field packs an
        // opponent's POSITION (relZ) in its low 20 bits and its ORIENTATION (the
        // car's heading relative to the player) in its high 12 bits — VERIFIED
        // against PitHouse AC captures (high12 = 0x167 + round(carHeadingDeg -
        // playerHeadingDeg), R^2 0.99). No game exposes opponent heading, so we
        // derive each car's heading from its world-position delta (atan2 of
        // motion); the player's too, so the relative angle is convention-free.
        // Indexed by CarLocations[].
        private (float X, float Z) _radarPrevPlayer;
        private (float X, float Z) _radarPlayerVel;   // EMA-smoothed unit motion dir
        private float _radarPlayerHeading;
        private bool _radarHavePlayerHeading;
        private (float X, float Z)[]? _radarPrevCars;
        private (float X, float Z)[]? _radarCarVel;    // EMA-smoothed unit motion dir
        private float[]? _radarCarHeadings;
        private bool[]? _radarHaveCarHeading;
        // Minimum per-car motion (metres) before re-deriving its heading from a
        // position delta; below this the last good heading persists so a
        // momentarily-stationary car keeps its orientation (and a parked grid car
        // stays at its default "aligned" 0x167).
        private const float RadarMotionEps = 0.30f;
        // EMA weight for motion-direction smoothing (opponents expose no heading
        // in AC, so a raw single-frame delta is too noisy; smoothing the unit
        // direction keeps the derived heading stable).
        private const float RadarVelSmooth = 0.08f;
        // A per-tick position jump beyond this (metres) is a teleport/reset, not
        // motion — never derive a heading from it (see UpdateRadarHeadings).
        private const float RadarTeleportEps = 40f;

        public TelemetryFrameBuilder(DashboardProfile profile)
            : this(profile, propertyResolver: null, type02NConvention: false,
                deviceId: MozaProtocol.DeviceWheel) { }

        public TelemetryFrameBuilder(DashboardProfile profile, Func<string, double>? propertyResolver)
            : this(profile, propertyResolver, type02NConvention: false,
                deviceId: MozaProtocol.DeviceWheel) { }

        /// <summary>
        /// Build a frame builder. If <paramref name="propertyResolver"/> is supplied,
        /// channels with a non-empty <see cref="ChannelDefinition.SimHubProperty"/>
        /// read their value via that resolver. Channels with an empty property fall
        /// back to <see cref="GameDataSnapshot.GetField"/>.
        ///
        /// <paramref name="type02NConvention"/> selects the N-field formula:
        /// false (default) = legacy 8+dataLen (VGS/F1), true = 10+dataLen
        /// including grp+dev (CSP/Type02 wheel firmware). Verified against
        /// PitHouse capture wireshark/csp/start-game-change-dash.pcapng — CSP
        /// frame `7e 0b 43 17 7d 23 32 00 23 32 1d 20 1f 00` has N=11 with
        /// data=1 byte, matching 10+1.
        ///
        /// <paramref name="deviceId"/> stamps the device-id byte at frame[3].
        /// Defaults to <see cref="MozaProtocol.DeviceWheel"/> (0x17) for wheels;
        /// pass <see cref="MozaProtocol.DeviceMain"/> (0x12) for the CM2 standalone
        /// dashboard so the wheel/dash firmware decodes the value frames correctly.
        /// </summary>
        public TelemetryFrameBuilder(DashboardProfile profile,
            Func<string, double>? propertyResolver,
            bool type02NConvention,
            byte deviceId = MozaProtocol.DeviceWheel)
        {
            _profile = profile;

            int dataLen = profile.TotalBytes;
            _frameBuffer = new byte[HeaderLen + dataLen + ChecksumLen];

            // Bind one resolver per channel. Each resolver captures the
            // ChannelDefinition reference (not just the path snapshot) so that
            // user-driven mapping edits — which mutate ch.SimHubProperty in
            // place via DashboardProfileStore.ApplyUserMappings — take effect
            // on the very next frame without rebuilding the frame builder.
            // The wire format (channel indices, compression, bit widths) is
            // unchanged by a mapping edit, so we don't need a tier-def restart;
            // we're just rewiring the host-side value source. Per-frame cost
            // is two property reads + one branch on top of the existing
            // resolver dispatch — sub-µs even on long tier lists.
            _resolvers = new Func<GameDataSnapshot, double>[profile.Channels.Count];
            _encKind = new EncKind[profile.Channels.Count];
            _encFn = new Func<double, ulong>?[profile.Channels.Count];
            _locIndex = new int[profile.Channels.Count];
            for (int i = 0; i < profile.Channels.Count; i++)
            {
                var ch = profile.Channels[i];
                if (propertyResolver != null)
                {
                    var resolver = propertyResolver;
                    var channel = ch;
                    _resolvers[i] = s =>
                    {
                        var p = channel.SimHubProperty;
                        if (!string.IsNullOrEmpty(p))
                        {
                            double scale = channel.SimHubPropertyScale;
                            if (scale == 0.0) scale = 1.0;
                            double v = resolver(p);
                            return scale == 1.0 ? v : v * scale;
                        }
                        return s.GetField(channel.SimHubField);
                    };
                }
                else
                {
                    var channel = ch;
                    _resolvers[i] = s => s.GetField(channel.SimHubField);
                }

                // Resolve the encode path once. Mirrors TelemetryEncoder's
                // IsFloat → IsDouble → Encode precedence exactly.
                if (ch.Compression == "float")
                    _encKind[i] = EncKind.Float;
                else if (CompressionTable.TryGetByName(ch.Compression, out var de) && de.BitWidth == 64)
                    _encKind[i] = EncKind.Double;
                else
                {
                    _encKind[i] = EncKind.Bits;
                    _encFn[i] = CompressionTable.TryGetByName(ch.Compression, out var be) ? be.Encode : null;
                }

                // Track-map override: patch/Location[_N] channels carry a
                // packed world position (fixed-point [u16 Y | u24 Z | u24 X]
                // in the 64-bit location_t slot), not a scalar SimHub property.
                // Detect by the location_t compression + URL and resolve from
                // the snapshot's per-car positions instead. Wire format
                // verified to <0.3 m against AC ground truth (Imola + Spa).
                int locIdx = ParseLocationIndex(ch.Url);
                int riIdx = ParseRadarIndex(ch.Url);
                if (locIdx != NotLocation && ch.Compression == "location_t")
                {
                    _encKind[i] = EncKind.LocationPair;
                    _locIndex[i] = locIdx;
                    NeedsCarPositions = true;
                }
                else if (riIdx != NotLocation && ch.Compression == "uint32_t")
                {
                    // Radar: patch/ri<N> carries car N's player-relative (X, Y)
                    // packed as two int16 in the 32-bit slot. Verified from the
                    // PitHouse FSR2 radar capture.
                    _encKind[i] = EncKind.RadarPair;
                    _locIndex[i] = riIdx;
                    NeedsCarPositions = true;
                }
                else
                {
                    _locIndex[i] = NotLocation;
                }
            }

            // Write the static header bytes once
            _frameBuffer[0] = MozaProtocol.MessageStart;       // 7E
            // N field formula varies by firmware era:
            //   legacy (VGS / KS Pro / F1 capture): N = 2(cmd) + 6(prefix) + data = 8+data
            //   CSP / Type02 (wireshark/csp/start-game-change-dash.pcapng):
            //     N = 2(grp+dev) + 8(cmd+prefix+flag+const) + data = 10+data
            // Verified CSP frame `7e 0b 43 17 7d 23 32 00 23 32 1d 20 1f 00`
            // has N=11 with data=1B (matches 10+1). Plugin sending N=8+data on
            // CSP firmware leaves wheel unable to render.
            _frameBuffer[1] = type02NConvention
                ? (byte)(10 + dataLen)
                : (byte)(2 + 6 + dataLen);
            _frameBuffer[2] = MozaProtocol.TelemetrySendGroup;  // 43
            _frameBuffer[3] = deviceId;                         // 0x17 wheel / 0x12 CM2 main
            _frameBuffer[4] = 0x7D;                             // cmdId[0]
            _frameBuffer[5] = 0x23;                             // cmdId[1]
            _frameBuffer[6] = 0x32;                             // header prefix
            _frameBuffer[7] = 0x00;
            _frameBuffer[8] = 0x23;
            _frameBuffer[9] = 0x32;
            // [10] = flagByte — patched per call
            _frameBuffer[11] = 0x20;                            // hardcoded constant

            if (dataLen > 0)
                _bitWriter = new TelemetryBitWriter(_frameBuffer, HeaderLen, dataLen);
        }

        public DashboardProfile Profile => _profile;

        // Pack one channel's value using the pre-resolved encode path. _bitWriter
        // is non-null here (callers guard). Matches TelemetryEncoder semantics:
        // float → WriteFloat, 64-bit → WriteDouble, else encode-and-pack with the
        // cached entry delegate (or the (uint)(int) fallback when uncatalogued).
        private void WriteChannel(int i, double value, int bitWidth)
        {
            switch (_encKind[i])
            {
                case EncKind.Float:
                    _bitWriter!.WriteFloat((float)value);
                    break;
                case EncKind.Double:
                    _bitWriter!.WriteDouble(value);
                    break;
                case EncKind.LocationPair:
                    // Reached only via the test-frame path (live frames
                    // resolve the real pair in BuildFrameFromSnapshot). Drive
                    // the test signal onto X (held above 0 so it isn't read as
                    // the empty-slot marker) and hold Y/Z at 0 so the slot
                    // consumes its full 64 bits and the map shows motion.
                    WritePackedLocation((float)value, 0f, 1f);
                    break;
                case EncKind.RadarPair:
                    // Test-frame path only. Drive the test signal onto the X
                    // int16, hold Y at 0; consumes the full 32 bits.
                    _bitWriter!.WriteBits((uint)(ushort)ClampInt16(value), 32);
                    break;
                default:
                    var fn = _encFn[i];
                    uint enc;
                    if (fn != null)
                        enc = (uint)fn(value);
                    else
                    {
                        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0.0;
                        enc = (uint)(int)value;
                    }
                    _bitWriter!.WriteBits(enc, bitWidth);
                    break;
            }
        }

        // Resolve and pack one track-map slot's ABSOLUTE world position into the
        // 64-bit location_t. The wheel's Map widget plots each car's path around
        // the track from these absolute positions; the player-relative "cars
        // nearby" view is the separate radar patch/ri* channels. Opponent N →
        // snap.CarLocations[N]; the base patch/Location (the player itself) →
        // snap.PlayerLocation.
        private void WriteLocationPair(int locIndex, in GameDataSnapshot snap)
        {
            float x, y, z;
            if (locIndex < 0)
            {
                x = snap.PlayerLocation.X; y = snap.PlayerLocation.Y; z = snap.PlayerLocation.Z;
            }
            else if (snap.CarLocations != null && locIndex < snap.CarLocations.Length)
            {
                x = snap.CarLocations[locIndex].X; y = snap.CarLocations[locIndex].Y; z = snap.CarLocations[locIndex].Z;
            }
            else
            {
                x = 0f; y = 0f; z = 0f;
            }
            WritePackedLocation(x, y, z);
        }

        // Pack one world position into PitHouse's 64-bit location_t, little-endian
        //   [u16 Y(elev) | u24 Z | u24 X],  field = clamp(center + round(scale·m)).
        // An absent / origin car (X==0 && Z==0) — or any non-finite coordinate
        // (car in the pits / not yet spawned) — writes all-zero, PitHouse's
        // empty-slot marker, which the wheel masks out via OpponentCount and
        // never plots. Always consumes exactly 16+24+24 = 64 bits.
        private void WritePackedLocation(float x, float y, float z)
        {
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(z) || float.IsInfinity(z)
                || (x == 0f && z == 0f))
            {
                _bitWriter!.WriteBits(0u, 16); // Y
                _bitWriter!.WriteBits(0u, 24); // Z
                _bitWriter!.WriteBits(0u, 24); // X
                return;
            }
            if (float.IsNaN(y) || float.IsInfinity(y)) y = 0f;
            var t = _mapTransform;
            uint fy = (uint)ClampInt(t.CenterY + Round(y * t.ScaleY), 0, 0xFFFF);
            uint fz = (uint)ClampInt(t.CenterZ + Round(z * t.ScaleZ), 0, 0xFFFFFF);
            uint fx = (uint)ClampInt(t.CenterX + Round(x * t.ScaleX), 0, 0xFFFFFF);
            _bitWriter!.WriteBits(fy, 16);
            _bitWriter!.WriteBits(fz, 24);
            _bitWriter!.WriteBits(fx, 24);
        }

        private static int Round(float v) => (int)Math.Round((double)v);
        private static int ClampInt(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // Resolve a channel URL to its track-map car index:
        //   patch/Location        → -1  (local car)
        //   patch/Location_<N>    →  N  (opponent index)
        //   anything else         → NotLocation
        // Guards against false matches like patch/LocationFoo.
        private static int ParseLocationIndex(string? url)
        {
            if (string.IsNullOrEmpty(url)) return NotLocation;
            const string marker = "patch/Location";
            int p = url!.IndexOf(marker, StringComparison.Ordinal);
            if (p < 0) return NotLocation;
            int rest = p + marker.Length;
            if (rest >= url.Length) return -1;          // patch/Location → local car
            if (url[rest] != '_') return NotLocation;   // e.g. patch/LocationFoo
            return int.TryParse(url.Substring(rest + 1), out int n) && n >= 0
                ? n
                : NotLocation;
        }

        // Write one radar slot. The radar tier is BIT-PACKED: a 131-bit preamble
        // (CurrentLapTime f32, Gear, Heading, Rpm, player Location) then ri0..riN
        // at bit 131+32k, one uint32 each (the wheel decodes ri BY URL but expects
        // them co-packed behind that preamble — see DashboardProfileStore, ri* must
        // keep its pkg-30 fast tier, NOT be isolated). Each ri<N> packs TWO fields
        // for the car with carId N (== CarLocations[N]); ri0 is the magic header:
        //   LOW 20 bits = POSITION: signed world relZ gap (oppZ - playerZ, metres)
        //                 low20 = (0x80000 + round(21630 * relZ)) mod 2^20.
        //                 Wraps at 2^20 (~+/-24.2 m), so we only emit cars inside
        //                 that window — a car beyond it would wrap and teleport
        //                 across the view (this matches PitHouse's sparse near-set,
        //                 which never sends the whole field).
        //   HIGH 12 bits = ORIENTATION: the car's heading relative to the player,
        //                 in degrees, high12 = (0x167 + round(carHeadDeg -
        //                 playerHeadDeg)) mod 2^12. 0x167 is "aligned with the
        //                 player" (NOT a magic constant — it's the field centre;
        //                 it merely looks constant when cars run parallel to you).
        // VERIFIED against PitHouse AC captures: position RMS 0.27 m, orientation
        // R^2 0.99 (radar4 carId1/5/7). Headings are derived from world motion
        // (UpdateRadarHeadings); a stationary car keeps its last heading, and a
        // never-moved (parked grid) car defaults to aligned (0x167). The player's
        // own slot is skipped. There is NO lateral here — left/right is the
        // separate ATSR / SpotterCar* spotter signal.
        private const uint RadarMagic = 0x1687FDFFu;   // ri0 header, constant
        // ri (low 20 bits) packs TWO signed 10-bit position fields, NOT one relZ:
        //   bits 10-19 = FORWARD  (world relZ gap)
        //   bits  0-9  = LATERAL  (world relX gap)
        // each centred at 0x200 (512), scale 512/24 units/m, +/-24 m window.
        // The wheel rotates the (relX,relZ) vector by the preamble Heading so the
        // player's forward points "up". Verified against PitHouse (regression on
        // matched cars): LATERAL = 21.33*relX + 511, FORWARD = 21.30*relZ + 512.
        // The old code wrote relZ across all 20 bits, so the lateral field decoded
        // to (scale*relZ) mod 1024 = garbage — that is the "cars sliding sideways /
        // scatter" defect.
        private const int RadarPosFieldCenter = 0x200;       // 512: 10-bit axis centre (rel = 0)
        private const double RadarPosFieldScale = 512.0 / 24.0; // 21.333 units/m per axis (+/-24 m)
        private const int RadarHeadCenter = 0x167;     // high 12 bits at relHeading = 0 (aligned)
        // Only emit a car whose |relZ| is inside the low-20 wrap window; beyond it
        // the position wraps and the dot teleports. 2^19 / 21630 = 24.24 m; gate
        // just inside so we never sit on the wrap seam.
        private const float RadarRelZRange = 24.0f;
        // ...AND within ~30 m in 2-D. relZ carries no lateral, so a car far across
        // the track on a parallel section can share the player's world-Z and would
        // otherwise plot as a phantom (wrong spot, ~180° heading). PitHouse's shown
        // cars are 100% within 30 m 2-D (0% beyond) — this gate matches that.
        private const float RadarRange2DSq = 30f * 30f;
        private const double RadToDeg = 180.0 / Math.PI;

        private void WriteRadarPair(int slot, in GameDataSnapshot snap)
        {
            uint packed;
            if (slot == 0)
            {
                packed = RadarMagic;                   // ri0 = magic header, always
            }
            else
            {
                packed = 0u;                           // unused slot beyond the in-range set = 0
                // ri1,ri2,... = the in-range opponents PACKED in carId order (see
                // GameDataSnapshot.RadarSlotCarIds) — matching PitHouse, which emits
                // only the near set so the nearest cars fill the fast tier. (Earlier
                // ri_k = carId k put near high-carId cars in the slow overflow tier
                // and emitted far phantom cars the wheel scattered.) The selection
                // (~24 m 2-D) is done in the snapshot, so |relZ| < 24 m here — no wrap.
                var slots = snap.RadarSlotCarIds;
                var locs = snap.CarLocations;
                int idx = (slots != null && slot < slots.Length) ? slots[slot] : -1;
                if (idx >= 0 && locs != null && idx < locs.Length)
                {
                    var c = locs[idx];
                    float relz = c.Z - snap.PlayerLocation.Z;   // world forward gap
                    float relx = c.X - snap.PlayerLocation.X;   // world lateral gap
                    int fwdField = RadarPosFieldCenter + (int)Math.Round(RadarPosFieldScale * relz);
                    int latField = RadarPosFieldCenter + (int)Math.Round(RadarPosFieldScale * relx);
                    if (fwdField < 0) fwdField = 0; else if (fwdField > 0x3FF) fwdField = 0x3FF;
                    if (latField < 0) latField = 0; else if (latField > 0x3FF) latField = 0x3FF;
                    int posField = (fwdField << 10) | latField;  // bits10-19 fwd, bits0-9 lat
                    // Orientation: relative heading (car - player) in degrees, centred
                    // on 0x167. Headings are derived from world motion (heading state is
                    // carId-indexed, so look it up by idx, not by slot); default aligned
                    // when a car or the player has no derived heading yet.
                    int headField = RadarHeadCenter;
                    if (_radarHavePlayerHeading
                        && _radarHaveCarHeading != null && idx < _radarHaveCarHeading.Length
                        && _radarHaveCarHeading[idx])
                    {
                        double relDeg = NormalizeRad(
                            _radarCarHeadings![idx] - _radarPlayerHeading) * RadToDeg;
                        headField = (RadarHeadCenter + (int)Math.Round(relDeg)) & 0xFFF;
                    }
                    packed = ((uint)headField << 20) | (uint)posField;
                }
            }
            _bitWriter!.WriteBits(packed, 32);
        }

        // Derive player + opponent headings from world-position deltas, once per
        // frame, feeding the radar's high-12-bit relative-orientation field. Each
        // car updates its heading only after it has moved RadarMotionEps since its
        // last update, so repeated within-tick reads (delta=0) are ignored and
        // stationary cars keep their last good heading.
        private void UpdateRadarHeadings(in GameDataSnapshot snap)
        {
            const float eps2 = RadarMotionEps * RadarMotionEps;
            // A frame-to-frame jump larger than any real car could travel in one
            // tick is a teleport/reset (a replay loop restart, a session/teleport-
            // to-pit, or a car popping in at a new position). Deriving a "heading"
            // from that jump produces a bogus orientation that then sticks while
            // the car sits still (e.g. the whole grid pointing one wrong way at a
            // standing start). Treat it as a discontinuity: drop the heading and
            // re-baseline, so a just-teleported/parked car renders aligned until
            // it actually moves.
            const float teleport2 = RadarTeleportEps * RadarTeleportEps;

            float pdx = snap.PlayerLocation.X - _radarPrevPlayer.X;
            float pdz = snap.PlayerLocation.Z - _radarPrevPlayer.Z;
            float pd2 = pdx * pdx + pdz * pdz;
            if (pd2 >= teleport2)
            {
                _radarHavePlayerHeading = false;           // discontinuity: re-baseline
                _radarPrevPlayer = (snap.PlayerLocation.X, snap.PlayerLocation.Z);
            }
            else if (pd2 >= eps2)
            {
                float inv = 1f / (float)Math.Sqrt(pd2);
                SmoothDir(ref _radarPlayerVel, pdx * inv, pdz * inv, _radarHavePlayerHeading);
                _radarPlayerHeading = (float)Math.Atan2(_radarPlayerVel.Z, _radarPlayerVel.X);
                _radarHavePlayerHeading = true;
                _radarPrevPlayer = (snap.PlayerLocation.X, snap.PlayerLocation.Z);
            }

            var locs = snap.CarLocations;
            if (locs == null) return;
            if (_radarPrevCars == null || _radarPrevCars.Length != locs.Length)
            {
                _radarPrevCars = new (float, float)[locs.Length];
                _radarCarVel = new (float, float)[locs.Length];
                _radarCarHeadings = new float[locs.Length];
                _radarHaveCarHeading = new bool[locs.Length];
                for (int i = 0; i < locs.Length; i++)
                    _radarPrevCars[i] = (locs[i].X, locs[i].Z);
            }
            for (int i = 0; i < locs.Length; i++)
            {
                float dx = locs[i].X - _radarPrevCars![i].X;
                float dz = locs[i].Z - _radarPrevCars[i].Z;
                float d2 = dx * dx + dz * dz;
                if (d2 >= teleport2)
                {
                    _radarHaveCarHeading![i] = false;       // discontinuity: re-baseline
                    _radarPrevCars[i] = (locs[i].X, locs[i].Z);
                }
                else if (d2 >= eps2)
                {
                    float inv = 1f / (float)Math.Sqrt(d2);
                    var v = _radarCarVel![i];
                    SmoothDir(ref v, dx * inv, dz * inv, _radarHaveCarHeading![i]);
                    _radarCarVel[i] = v;
                    _radarCarHeadings![i] = (float)Math.Atan2(v.Z, v.X);
                    _radarHaveCarHeading![i] = true;
                    _radarPrevCars[i] = (locs[i].X, locs[i].Z);
                }
            }
        }

        // EMA of a unit direction vector, re-normalised. Smooths heading so
        // single-frame position quantisation doesn't jitter it.
        private static void SmoothDir(ref (float X, float Z) v, float ux, float uz, bool have)
        {
            if (!have) { v = (ux, uz); return; }
            float nx = RadarVelSmooth * ux + (1f - RadarVelSmooth) * v.X;
            float nz = RadarVelSmooth * uz + (1f - RadarVelSmooth) * v.Z;
            float m = (float)Math.Sqrt(nx * nx + nz * nz);
            if (m > 1e-6f) v = (nx / m, nz / m);
        }

        // Wrap an angle (radians) to [-π, π].
        private static double NormalizeRad(double a)
        {
            const double TwoPi = 2.0 * Math.PI;
            a %= TwoPi;
            if (a > Math.PI) a -= TwoPi;
            else if (a < -Math.PI) a += TwoPi;
            return a;
        }

        // Resolve a channel URL to its radar car index: patch/ri<N> → N,
        // anything else → NotLocation.
        private static int ParseRadarIndex(string? url)
        {
            if (string.IsNullOrEmpty(url)) return NotLocation;
            const string marker = "patch/ri";
            int p = url!.IndexOf(marker, StringComparison.Ordinal);
            if (p < 0) return NotLocation;
            int rest = p + marker.Length;
            if (rest >= url.Length) return NotLocation;
            return int.TryParse(url.Substring(rest), out int n) && n >= 0
                ? n
                : NotLocation;
        }

        private static short ClampInt16(double v)
        {
            if (double.IsNaN(v)) return 0;
            if (v > short.MaxValue) return short.MaxValue;
            if (v < short.MinValue) return short.MinValue;
            return (short)v;
        }

        /// <summary>Build frame from live game data.</summary>
        public byte[] BuildFrame(StatusDataBase? gameData, byte flagByte) =>
            BuildFrameFromSnapshot(GameDataSnapshot.FromStatusData(gameData, NeedsCarPositions), flagByte);

        /// <summary>Build frame from a pre-populated snapshot (test patterns, etc.).</summary>
        public byte[] BuildFrameFromSnapshot(GameDataSnapshot snapshot, byte flagByte)
        {
            _frameBuffer[10] = flagByte;

            if (_bitWriter != null)
            {
                _bitWriter.Reset();

                // Refresh per-car headings from motion before writing radar slots
                // (feeds the ri high-12-bit relative-orientation field).
                UpdateRadarHeadings(in snapshot);

                // Pick the per-track world→field transform for the location_t
                // channels (map-pixel scale keyed to map.ini SCALE_FACTOR);
                // resolved once per track, cached until the track changes.
                if (snapshot.TrackFolderName != _mapTransformTrack)
                {
                    _mapTransformTrack = snapshot.TrackFolderName;
                    _mapTransform = TrackMapTransform.Resolve(snapshot.TrackFolderName);
                }

                for (int i = 0; i < _profile.Channels.Count; i++)
                {
                    if (_encKind[i] == EncKind.LocationPair)
                    {
                        WriteLocationPair(_locIndex[i], in snapshot);
                        continue;
                    }
                    if (_encKind[i] == EncKind.RadarPair)
                    {
                        WriteRadarPair(_locIndex[i], in snapshot);
                        continue;
                    }
                    double value = _resolvers[i](snapshot);
                    if (double.IsNaN(value) || double.IsInfinity(value)) value = 0.0;
                    WriteChannel(i, value, _profile.Channels[i].BitWidth);
                }
            }

            _frameBuffer[_frameBuffer.Length - 1] = MozaProtocol.CalculateWireChecksum(
                _frameBuffer, _frameBuffer.Length - 1);

            // Return a copy: the write queue holds a reference until the write thread drains it,
            // and we reuse _frameBuffer on the next tick. One Array.Copy is still far cheaper
            // than the old List<byte> + two ToArray() allocations.
            var copy = new byte[_frameBuffer.Length];
            Array.Copy(_frameBuffer, 0, copy, 0, copy.Length);
            return copy;
        }

        /// <summary>
        /// Build a test-pattern frame that synthesises sensible per-channel
        /// values via <see cref="TestSignalGenerator"/>. Each channel's
        /// <see cref="ChannelDefinition.TestSignal"/> was resolved at
        /// dashboard-load time from overrides + Telemetry.json range +
        /// compression-table fallback (see
        /// <see cref="MozaPlugin.Telemetry.TestMode.TestSignalCatalog"/>).
        ///
        /// Wall-clock-driven so a 1 Hz Gear stepper changes once per real
        /// second regardless of which tier (30 / 500 / 2000 ms) carries it.
        /// </summary>
        public byte[] BuildTestFrame(byte flagByte)
        {
            _frameBuffer[10] = flagByte;

            long nowMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

            if (_bitWriter != null)
            {
                _bitWriter.Reset();

                for (int i = 0; i < _profile.Channels.Count; i++)
                {
                    var ch = _profile.Channels[i];
                    double value = TestSignalGenerator.Compute(ch.TestSignal, nowMs);
                    WriteChannel(i, value, ch.BitWidth);
                }
            }

            _frameBuffer[_frameBuffer.Length - 1] = MozaProtocol.CalculateWireChecksum(
                _frameBuffer, _frameBuffer.Length - 1);

            var copy = new byte[_frameBuffer.Length];
            Array.Copy(_frameBuffer, 0, copy, 0, copy.Length);
            return copy;
        }

        /// <summary>
        /// Build a single V0 per-channel value frame for the URL-subscription
        /// telemetry path used by post-2026-04 CSP firmware. Layout reverse-
        /// engineered from PitHouse capture
        /// `wireshark/csp/start-game-change-dash.pcapng` (host outbound on
        /// session 0x02):
        /// <code>
        ///   [ff]                            sentinel
        ///   [size:u32 LE = 4 + value.Length]
        ///   [crc32_LE over (index_LE_4B || value_LE)]
        ///   [index:u32 LE]
        ///   [value: 4 or 8 bytes LE]
        /// </code>
        /// One frame per channel update; caller wraps each in a session-data
        /// chunk via <see cref="TierDefinitionBuilder.ChunkMessage"/>.
        /// </summary>
        public static byte[] BuildV0ValueFrame(uint channelIndex, byte[] valueLE)
        {
            if (valueLE == null) throw new ArgumentNullException(nameof(valueLE));
            if (valueLE.Length != 4 && valueLE.Length != 8)
                throw new ArgumentException("V0 value must be 4 or 8 bytes", nameof(valueLE));
            uint size = (uint)(4 + valueLE.Length);

            // CRC32 input is index_LE concatenated with value_LE.
            var crcInput = new byte[4 + valueLE.Length];
            crcInput[0] = (byte)(channelIndex & 0xFF);
            crcInput[1] = (byte)((channelIndex >> 8) & 0xFF);
            crcInput[2] = (byte)((channelIndex >> 16) & 0xFF);
            crcInput[3] = (byte)((channelIndex >> 24) & 0xFF);
            Buffer.BlockCopy(valueLE, 0, crcInput, 4, valueLE.Length);
            uint crc = TierDefinitionBuilder.Crc32(crcInput, 0, crcInput.Length);

            var frame = new byte[1 + 4 + 4 + 4 + valueLE.Length];
            int o = 0;
            frame[o++] = 0xFF;
            frame[o++] = (byte)(size & 0xFF);
            frame[o++] = (byte)((size >> 8) & 0xFF);
            frame[o++] = (byte)((size >> 16) & 0xFF);
            frame[o++] = (byte)((size >> 24) & 0xFF);
            frame[o++] = (byte)(crc & 0xFF);
            frame[o++] = (byte)((crc >> 8) & 0xFF);
            frame[o++] = (byte)((crc >> 16) & 0xFF);
            frame[o++] = (byte)((crc >> 24) & 0xFF);
            frame[o++] = (byte)(channelIndex & 0xFF);
            frame[o++] = (byte)((channelIndex >> 8) & 0xFF);
            frame[o++] = (byte)((channelIndex >> 16) & 0xFF);
            frame[o++] = (byte)((channelIndex >> 24) & 0xFF);
            Buffer.BlockCopy(valueLE, 0, frame, o, valueLE.Length);
            return frame;
        }

        /// <summary>
        /// Encode a decoded game value to the LE byte array expected by V0
        /// per-channel value frames. 4-byte for most channels, 8-byte for
        /// 64-bit / double channels (compression codes <c>int64_t</c>,
        /// <c>uint64_t</c>, <c>double</c>).
        /// </summary>
        public static byte[] EncodeV0Value(string compression, double value)
        {
            if (compression == "double")
            {
                long bits = BitConverter.DoubleToInt64Bits(value);
                return BitConverter.IsLittleEndian
                    ? BitConverter.GetBytes(bits)
                    : ReverseBytes(BitConverter.GetBytes(bits));
            }
            if (compression == "int64_t")
            {
                long v = (long)value;
                return BitConverter.IsLittleEndian
                    ? BitConverter.GetBytes(v)
                    : ReverseBytes(BitConverter.GetBytes(v));
            }
            if (compression == "uint64_t")
            {
                ulong v = (ulong)value;
                return BitConverter.IsLittleEndian
                    ? BitConverter.GetBytes(v)
                    : ReverseBytes(BitConverter.GetBytes(v));
            }
            if (compression == "float")
            {
                float v = (float)value;
                return BitConverter.IsLittleEndian
                    ? BitConverter.GetBytes(v)
                    : ReverseBytes(BitConverter.GetBytes(v));
            }
            // Default: encode as u32 LE using the same compression-aware mapping
            // the V2 path uses, then truncate to 4 bytes. Wheel firmware handles
            // type interpretation via its internal channel metadata.
            uint encoded = TelemetryEncoder.Encode(compression, value);
            return new byte[]
            {
                (byte)(encoded & 0xFF),
                (byte)((encoded >> 8) & 0xFF),
                (byte)((encoded >> 16) & 0xFF),
                (byte)((encoded >> 24) & 0xFF),
            };
        }

        private static byte[] ReverseBytes(byte[] b)
        {
            Array.Reverse(b);
            return b;
        }

        /// <summary>
        /// Build a stub frame for a tier with no active channels.
        /// Frame contains the full fixed header but no data bytes.
        /// </summary>
        public static byte[] BuildStubFrame(byte flagByte, byte deviceId = MozaProtocol.DeviceWheel)
        {
            var frame = new byte[HeaderLen + ChecksumLen];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)(2 + 6);  // N legacy: cmd+prefix, no data
            frame[2] = MozaProtocol.TelemetrySendGroup;
            frame[3] = deviceId;
            frame[4] = 0x7D;
            frame[5] = 0x23;
            frame[6] = 0x32;
            frame[7] = 0x00;
            frame[8] = 0x23;
            frame[9] = 0x32;
            frame[10] = flagByte;
            frame[11] = 0x20;
            frame[12] = MozaProtocol.CalculateWireChecksum(frame, 12);
            return frame;
        }
    }
}
