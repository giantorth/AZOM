using System;
using System.Diagnostics;
using GameReaderCommon;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Protocol;
using MozaPlugin.Telemetry.TestMode;

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
        // Fixed-point scale for radar relative coordinates (metres → int16
        // counts). The ri wire format packs two int16; this constant maps
        // SimHub's RelativeCoordinatesToPlayer (metres) onto that range.
        // TUNABLE: starting estimate from the PitHouse FSR2 radar capture
        // (nearby cars read ~±2000–8000 counts); confirm against known car
        // gaps on hardware and adjust.
        private const float RadarFixedPointScale = 100f;

        // Pre-allocated buffers reused every frame to avoid GC pressure
        private readonly byte[] _frameBuffer;
        private readonly TelemetryBitWriter? _bitWriter;

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
                // packed (X, Z) coordinate pair (two float32 in the 64-bit
                // location_t slot), not a scalar SimHub property. Detect by
                // the location_t compression + URL and resolve from the
                // snapshot's per-car positions instead. Wire format verified
                // from the PitHouse FSR2 capture (each slot = [f32 X | f32 Z]).
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
                    // the test signal onto X and hold Z at 0 so the slot still
                    // consumes its full 64 bits and the map shows motion.
                    _bitWriter!.WriteFloat((float)value);
                    _bitWriter!.WriteFloat(0f);
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

        // Pack one track-map slot as two little-endian float32 (X low, Z high)
        // = the 64-bit location_t the wheel expects. Absent cars (index past
        // the live opponent list) pack (0, 0); the wheel masks them out via
        // OpponentCount. Bypasses WriteChannel's NaN/Inf sanitiser so genuine
        // coordinate bit patterns are preserved exactly.
        private void WriteLocationPair(int locIndex, in GameDataSnapshot snap)
        {
            float x, y;
            if (locIndex < 0)
            {
                x = snap.PlayerLocation.X;
                y = snap.PlayerLocation.Y;
            }
            else if (snap.CarLocations != null && locIndex < snap.CarLocations.Length)
            {
                x = snap.CarLocations[locIndex].X;
                y = snap.CarLocations[locIndex].Y;
            }
            else
            {
                x = 0f;
                y = 0f;
            }
            // Never emit NaN/Inf: a non-finite coordinate (car in the pits /
            // not yet spawned) makes the wheel's Map.qml plot a dot at NaN and
            // can crash the display. PitHouse only ever sends finite values;
            // fall back to (0,0) = the empty-slot marker.
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y))
            {
                x = 0f;
                y = 0f;
            }
            _bitWriter!.WriteFloat(x);
            _bitWriter!.WriteFloat(y);
        }

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

        // Pack one radar slot as two int16 (relX low, relY high) into the
        // 32-bit ri uint32 — the player-relative format reverse-engineered
        // from the PitHouse FSR2 radar capture. (0,0) when SimHub has no
        // relative coordinate for the car (out of radar range), matching the
        // wire's sparse empty-slot behaviour.
        private void WriteRadarPair(int carIndex, in GameDataSnapshot snap)
        {
            float x = 0f, y = 0f;
            if (snap.CarRelative != null && carIndex >= 0 && carIndex < snap.CarRelative.Length)
            {
                x = snap.CarRelative[carIndex].X;
                y = snap.CarRelative[carIndex].Y;
            }
            uint lo = (uint)(ushort)ClampInt16(x * RadarFixedPointScale);
            uint hi = (uint)(ushort)ClampInt16(y * RadarFixedPointScale);
            _bitWriter!.WriteBits(lo | (hi << 16), 32);
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
