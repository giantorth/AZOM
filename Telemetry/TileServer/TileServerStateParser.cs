using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
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
        // (sentinel mismatch in TryDecode returns null without trimming the buffer).
        private const int MaxBufferBytes = 1 * 1024 * 1024;

        private readonly List<byte> _buf = new List<byte>();
        private TileServerState? _lastState;

        public TileServerState? LastState => _lastState;

        /// <summary>Append a session 0x03 chunk payload (CRC already stripped)
        /// and attempt to decode any complete envelope it completes.</summary>
        public TileServerState? OnChunk(byte[] chunkPayload)
        {
            if (chunkPayload == null || chunkPayload.Length == 0) return null;
            _buf.AddRange(chunkPayload);
            TileServerState? decoded = TryDecode(out int consumed);
            if (decoded != null && consumed > 0)
            {
                _buf.RemoveRange(0, consumed);
                _lastState = decoded;
                return decoded;
            }

            // No decode this pass. If the buffer is large enough to evaluate
            // the sentinel and it doesn't match at offset 0, the leading
            // bytes are stray (envelope from a prior, partially-lost message
            // or genuinely corrupt input). Scan for the next plausible
            // envelope start (FF 01 00) and trim everything before it so the
            // buffer doesn't accumulate up to MaxBufferBytes before clearing.
            // Mirrors SessionDataReassembler.TryDecompressByMagic's recovery
            // pattern. Without this, captures of malformed input grew the
            // buffer to the 1 MiB cap over time before the wholesale clear.
            if (_buf.Count >= 3 && !LooksLikeEnvelopeStart(_buf, 0))
            {
                int next = FindNextEnvelopeStart(_buf, 1);
                if (next > 0)
                    _buf.RemoveRange(0, next);
                else
                    // No sentinel anywhere in the buffer — keep the last 2
                    // bytes in case the envelope start straddles the next
                    // chunk boundary, drop the rest.
                    _buf.RemoveRange(0, Math.Max(0, _buf.Count - 2));
            }
            else if (_buf.Count > MaxBufferBytes)
            {
                // Defensive: buffer still grew past the cap (e.g. a long
                // run of plausible-sentinel starts that never resolve into
                // a real envelope). Drop everything.
                _buf.Clear();
            }

            return null;
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

        public void Clear() => _buf.Clear();

        private TileServerState? TryDecode(out int consumed)
        {
            consumed = 0;
            // Need at least envelope (12) + zlib header (2)
            if (_buf.Count < 14) return null;
            // Sentinels must match
            if (_buf[0] != 0xFF || _buf[1] != 0x01 || _buf[2] != 0x00 ||
                _buf[7] != 0xFF || _buf[8] != 0x00)
            {
                return null;
            }
            // compressed_size + 4 (LE u32 at offset 3..6)
            uint compPlus4 = (uint)_buf[3] | ((uint)_buf[4] << 8) |
                             ((uint)_buf[5] << 16) | ((uint)_buf[6] << 24);
            if (compPlus4 < 4) return null;
            int compSize = (int)(compPlus4 - 4);
            // uncompressed_size (BE u24 at offset 9..11)
            int uncompSize = (_buf[9] << 16) | (_buf[10] << 8) | _buf[11];
            int total = 12 + compSize;
            if (_buf.Count < total) return null;
            try
            {
                byte[] zlib = _buf.GetRange(12, compSize).ToArray();
                byte[] raw = DecompressZlib(zlib);
                if (raw.Length != uncompSize)
                {
                    // Size mismatch — likely wrong envelope interpretation.
                    // Skip this attempt but don't corrupt buffer.
                    return null;
                }
                string json = Encoding.UTF8.GetString(raw);
                var state = ParseJson(json);
                consumed = total;
                return state;
            }
            catch
            {
                return null;
            }
        }

        private static byte[] DecompressZlib(byte[] zlib)
        {
            if (zlib.Length < 6) return Array.Empty<byte>();
            // Skip 2-byte zlib header, strip 4-byte Adler-32 trailer
            using var input = new MemoryStream(zlib, 2, zlib.Length - 6);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
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
