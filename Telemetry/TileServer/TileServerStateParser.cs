using System;
using System.Collections.Generic;
using System.Text;
using MozaPlugin.Telemetry.Sessions;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Telemetry.TileServer
{
    /// <summary>
    /// Decodes session 0x03 tile-server state blobs pushed by the wheel. Uses
    /// the 12-byte envelope format reversed from PitHouse captures
    /// (2026-04-21): <c>FF 01 00 [comp_size+4 u32 LE] FF 00 [uncomp_size u24 BE]</c>
    /// followed by a zlib stream. Payload is a JSON object with `map.ats` and
    /// `map.ets2` keys carrying nested escaped-JSON strings for ATS/ETS2 map
    /// metadata.
    ///
    /// See <c>usb-capture/session-0x03-tile-server-re.md</c> and doc
    /// <c>§ Session 0x03 tile-server envelope (variant, 12 bytes)</c>.
    /// </summary>
    public sealed class TileServerStateParser
    {
        // Hard cap on accumulated buffer. Real envelopes are <100 KB; anything
        // beyond this is malformed input that would otherwise grow unbounded
        // (an envelope whose size field claims more than arrives). Also the
        // bound on an envelope's claimed size.
        private const int MaxBufferBytes = 1 * 1024 * 1024;

        // Guards _buf: OnChunk runs on the serial read thread, Clear() on Stop().
        private readonly object _lock = new object();
        private readonly List<byte> _buf = new List<byte>();
        private volatile TileServerState? _lastState;

        public TileServerState? LastState => _lastState;

        private enum DecodeStatus { Incomplete, Decoded, Invalid }

        /// <summary>Append a session 0x03 chunk payload (CRC already stripped)
        /// and decode every complete envelope it completes; returns the last.</summary>
        public TileServerState? OnChunk(byte[] chunkPayload)
        {
            if (chunkPayload == null || chunkPayload.Length == 0) return null;
            lock (_lock)
            {
                _buf.AddRange(chunkPayload);
                TileServerState? latest = null;
                while (true)
                {
                    // Leading bytes that aren't an envelope start are stray
                    // (envelope from a prior, partially-lost message or
                    // genuinely corrupt input): skip to the next plausible
                    // FF 01 00 so the buffer doesn't accumulate up to
                    // MaxBufferBytes before clearing.
                    if (_buf.Count >= 3 && !LooksLikeEnvelopeStart(_buf, 0))
                    {
                        DropToNextEnvelopeStart();
                        continue;
                    }

                    var status = TryDecode(out TileServerState? decoded, out int consumed);
                    if (status == DecodeStatus.Decoded)
                    {
                        _buf.RemoveRange(0, consumed);
                        _lastState = decoded;
                        latest = decoded;
                        continue;
                    }
                    if (status == DecodeStatus.Invalid)
                    {
                        // Complete-but-undecodable (or impossible) envelope at
                        // offset 0: its bytes will never change, so retrying
                        // would block every later envelope. Resync past it.
                        DropToNextEnvelopeStart();
                        continue;
                    }
                    break;
                }

                if (_buf.Count > MaxBufferBytes)
                {
                    // Defensive: buffer still grew past the cap (e.g. a long
                    // run of plausible-sentinel starts that never resolve into
                    // a real envelope). Drop everything.
                    _buf.Clear();
                }
                return latest;
            }
        }

        /// <summary>Drop the buffer head up to the next FF 01 00 after offset 0.
        /// With none, keep the last 2 bytes in case an envelope start straddles
        /// the next chunk boundary.</summary>
        private void DropToNextEnvelopeStart()
        {
            int next = FindNextEnvelopeStart(_buf, 1);
            if (next > 0)
                _buf.RemoveRange(0, next);
            else
                _buf.RemoveRange(0, Math.Max(0, _buf.Count - 2));
        }

        private static bool LooksLikeEnvelopeStart(List<byte> buf, int offset)
        {
            return offset + 2 < buf.Count
                && buf[offset] == 0xFF
                && buf[offset + 1] == 0x01
                && buf[offset + 2] == 0x00;
        }

        private static int FindNextEnvelopeStart(List<byte> buf, int from)
        {
            for (int i = from; i + 2 < buf.Count; i++)
            {
                if (buf[i] == 0xFF && buf[i + 1] == 0x01 && buf[i + 2] == 0x00)
                    return i;
            }
            return -1;
        }

        public void Clear()
        {
            lock (_lock) _buf.Clear();
        }

        /// <summary>Decode the envelope at offset 0. <see cref="DecodeStatus.Invalid"/>
        /// when it can never decode: bad sentinel, impossible size, or a fully
        /// buffered stream that fails inflate / Adler-32 / size / JSON checks.</summary>
        private DecodeStatus TryDecode(out TileServerState? state, out int consumed)
        {
            state = null;
            consumed = 0;
            // Need at least envelope (12) + zlib header (2)
            if (_buf.Count < 14) return DecodeStatus.Incomplete;
            // Sentinels must match
            if (_buf[0] != 0xFF || _buf[1] != 0x01 || _buf[2] != 0x00 ||
                _buf[7] != 0xFF || _buf[8] != 0x00)
            {
                return DecodeStatus.Invalid;
            }
            // compressed_size + 4 (LE u32 at offset 3..6)
            uint compPlus4 = (uint)_buf[3] | ((uint)_buf[4] << 8) |
                             ((uint)_buf[5] << 16) | ((uint)_buf[6] << 24);
            if (compPlus4 < 4 || compPlus4 - 4 > MaxBufferBytes - 12) return DecodeStatus.Invalid;
            int compSize = (int)(compPlus4 - 4);
            // uncompressed_size (BE u24 at offset 9..11)
            int uncompSize = (_buf[9] << 16) | (_buf[10] << 8) | _buf[11];
            int total = 12 + compSize;
            if (_buf.Count < total) return DecodeStatus.Incomplete;
            try
            {
                byte[] zlib = _buf.GetRange(12, compSize).ToArray();
                // Bounded inflate; null on Adler-32 or size mismatch.
                byte[]? raw = SessionDataReassembler.InflateZlibExact(zlib, 0, compSize, uncompSize);
                if (raw == null) return DecodeStatus.Invalid;
                string json = Encoding.UTF8.GetString(raw);
                state = ParseJson(json);
                consumed = total;
                return DecodeStatus.Decoded;
            }
            catch
            {
                return DecodeStatus.Invalid;
            }
        }

        private static TileServerState ParseJson(string json)
        {
            var root = JObject.Parse(json);
            var state = new TileServerState
            {
                Root = root.Value<string>("root") ?? "",
                Version = root.Value<int?>("version") ?? 0,
            };
            if (root["map"] is JObject map)
            {
                state.Games = new Dictionary<string, TileServerGameInfo>();
                foreach (var prop in map.Properties())
                {
                    if (prop.Value.Type != JTokenType.String) continue;
                    string inner = prop.Value.Value<string>() ?? "";
                    if (string.IsNullOrEmpty(inner)) continue;
                    try
                    {
                        var g = JObject.Parse(inner);
                        var info = new TileServerGameInfo
                        {
                            Name = g.Value<string>("name") ?? prop.Name,
                            MapVersion = g.Value<int?>("map_version") ?? -1,
                            TileSize = g.Value<int?>("tile_size") ?? 0,
                            FileType = g.Value<string>("file_type") ?? "",
                            Root = g.Value<string>("root") ?? "",
                            Populated = (g["layers"] is JArray la && la.Count > 0) &&
                                        (g.Value<int?>("map_version") ?? -1) >= 0,
                            LayersCount = (g["layers"] is JArray la2) ? la2.Count : 0,
                        };
                        ((Dictionary<string, TileServerGameInfo>)state.Games)[prop.Name] = info;
                    }
                    catch
                    {
                        // Ignore malformed inner JSON — keep the other games
                    }
                }
            }
            return state;
        }
    }

    public sealed class TileServerState
    {
        public string Root { get; set; } = "";
        public int Version { get; set; }
        /// <summary>Per-game info keyed by game tag (e.g. "ats", "ets2").</summary>
        public IReadOnlyDictionary<string, TileServerGameInfo> Games { get; set; }
            = new Dictionary<string, TileServerGameInfo>();
        public bool AnyPopulated
        {
            get
            {
                foreach (var kv in Games)
                    if (kv.Value.Populated) return true;
                return false;
            }
        }
    }

    public sealed class TileServerGameInfo
    {
        public string Name { get; set; } = "";
        public int MapVersion { get; set; } = -1;
        public int TileSize { get; set; }
        public string FileType { get; set; } = "";
        public string Root { get; set; } = "";
        public bool Populated { get; set; }
        public int LayersCount { get; set; }
    }
}
