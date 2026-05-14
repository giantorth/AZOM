using System.IO;
using System.IO.Compression;
using System.Text;

namespace MozaPlugin.Telemetry.TileServer
{
    /// <summary>
    /// Builds an empty-state tile-server JSON blob matching the shape PitHouse
    /// uploads on session 0x03 during wheel connect. The wheel stores this in
    /// its native dashboard UI's tile_server area; with an empty blob, the wheel
    /// shows "no map tiles installed" for ATS/ETS2.
    ///
    /// Envelope format reversed 2026-04-21 from live Pithouse traffic (see
    /// <c>usb-capture/session-0x03-tile-server-re.md</c>):
    ///
    ///   FF 01 00 [comp_size+4 u32 LE] FF 00 [uncomp_size u24 BE]
    ///
    /// Followed by the zlib stream. Combined payload is then chunked via the
    /// standard SerialStream 7c:00 chunker.
    /// </summary>
    public static class TileServerStateBuilder
    {
        /// <summary>
        /// Produce the empty tile-server JSON PitHouse sends when no maps are
        /// installed. Observed schema from decoded session 0x03 blob, size 775
        /// bytes uncompressed.
        /// </summary>
        public static byte[] BuildEmptyStateJson(string hostTileRoot = @"C:/Users/Public/MozaPlugin/tile_server")
        {
            // Inner per-game JSON (serialised once then embedded as an escaped string).
            string innerAts  = BuildInnerGameJson(hostTileRoot + "/ats");
            string innerEts2 = BuildInnerGameJson(hostTileRoot + "/ets2");

            // Outer wrapper: {"map":{"ats":"<inner>","ets2":"<inner>"},"root":"...","version":0}
            // Strings are JSON-escaped — the inner JSON must be backslash-escaped
            // as it sits inside a string value.
            var sb = new StringBuilder(1024);
            sb.Append("{\"map\":{\"ats\":\"");
            AppendEscaped(sb, innerAts);
            sb.Append("\",\"ets2\":\"");
            AppendEscaped(sb, innerEts2);
            sb.Append("\"},\"root\":\"");
            AppendEscaped(sb, hostTileRoot);
            sb.Append("\",\"version\":0}");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>
        /// Build the 12-byte session 0x03 envelope that precedes the zlib stream.
        /// Format: FF 01 00 [comp_size+4 u32 LE] FF 00 [uncomp_size u24 BE].
        /// </summary>
        public static byte[] BuildEnvelope(int compressedSize, int uncompressedSize)
        {
            if (uncompressedSize < 0 || uncompressedSize > 0xFFFFFF)
                throw new System.ArgumentOutOfRangeException(nameof(uncompressedSize),
                    "u24 BE field — must fit in 24 bits");
            uint sz = (uint)(compressedSize + 4);
            return new byte[]
            {
                0xFF, 0x01, 0x00,
                (byte)(sz & 0xFF), (byte)((sz >> 8) & 0xFF),
                (byte)((sz >> 16) & 0xFF), (byte)((sz >> 24) & 0xFF),
                0xFF, 0x00,
                (byte)((uncompressedSize >> 16) & 0xFF),
                (byte)((uncompressedSize >> 8) & 0xFF),
                (byte)(uncompressedSize & 0xFF),
            };
        }

        /// <summary>
        /// Produce the full session 0x03 payload: envelope + zlib stream. Caller
        /// then passes the result to the SerialStream chunker with session=0x03.
        /// </summary>
        public static byte[] BuildFullBlob(byte[] uncompressedJson)
        {
            byte[] zlib = Compress(uncompressedJson);
            byte[] env = BuildEnvelope(zlib.Length, uncompressedJson.Length);
            var result = new byte[env.Length + zlib.Length];
            System.Buffer.BlockCopy(env, 0, result, 0, env.Length);
            System.Buffer.BlockCopy(zlib, 0, result, env.Length, zlib.Length);
            return result;
        }

        /// <summary>zlib-wrap (Deflate + 2-byte zlib header + Adler-32) the JSON bytes.</summary>
        public static byte[] Compress(byte[] raw)
        {
            using var ms = new MemoryStream();
            // zlib header: 0x78 0x9C (default compression)
            ms.WriteByte(0x78);
            ms.WriteByte(0x9C);
            using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(raw, 0, raw.Length);
            uint adler = Adler32(raw);
            ms.WriteByte((byte)((adler >> 24) & 0xFF));
            ms.WriteByte((byte)((adler >> 16) & 0xFF));
            ms.WriteByte((byte)((adler >> 8) & 0xFF));
            ms.WriteByte((byte)(adler & 0xFF));
            return ms.ToArray();
        }

        private static string BuildInnerGameJson(string rootPath)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"bg\":\"\",");
            sb.Append("\"ext_files\":[],");
            sb.Append("\"file_type\":\"\",");
            sb.Append("\"layers\":[],");
            sb.Append("\"levels\":{},");
            sb.Append("\"map_version\":-1,");
            sb.Append("\"name\":\"\",");
            sb.Append("\"pm_support\":true,");
            sb.Append("\"pmtiles_exists\":false,");
            sb.Append("\"root\":\"");
            AppendEscaped(sb, rootPath);
            sb.Append("\",");
            sb.Append("\"support_games\":[],");
            sb.Append("\"tile_size\":0,");
            sb.Append("\"version\":0,");
            sb.Append("\"x_max\":0,\"x_min\":0,\"y_max\":0,\"y_min\":0}");
            return sb.ToString();
        }

        private static void AppendEscaped(StringBuilder sb, string value)
        {
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.Append($"\\u{(int)c:X4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
        }

        private static uint Adler32(byte[] data)
        {
            const uint Mod = 65521;
            uint a = 1, b = 0;
            foreach (byte x in data)
            {
                a = (a + x) % Mod;
                b = (b + a) % Mod;
            }
            return (b << 16) | a;
        }
    }
}
