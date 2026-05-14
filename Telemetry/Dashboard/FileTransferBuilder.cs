using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MozaPlugin.Telemetry.Frames;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// Wire-format variant of the file-transfer sub-msg header.
    /// </summary>
    /// <remarks>
    /// Two formats observed across firmware revisions. <see cref="Legacy2025_11"/>
    /// is the original 8-byte `[role:1][max_chunk:1][type:1][reserved:5]` form
    /// captured in `usb-capture/09-04-26/dash-upload.pcapng`. <see cref="New2026_04"/>
    /// is a 6-byte `[type:1][size_LE:2][pad:3]` header where size_LE is the
    /// sub-msg body length — observed in 2026-04 PitHouse uploads (sessions
    /// 0x07 / 0x09). Bytes on the wire ARE NOT byte-identical between the two:
    /// the type byte position differs (byte 0 in new vs byte 2 in legacy), and
    /// the size_LE field's two bytes overlap legacy's `max_chunk` + `type`
    /// positions, so a legacy-encoded sub-msg 2 (content) would be misread as
    /// new-format size 0x0340 = 832 bytes. New firmware needs the new layout.
    /// </remarks>
    public enum FileTransferWireFormat
    {
        /// <summary>2025-11 firmware: 8-byte role/max_chunk/type/5×reserved header.</summary>
        Legacy2025_11 = 0,
        /// <summary>2026-04 firmware: 6-byte type/size_LE/3×reserved header.
        /// First sub-msg uses type=0x01 (path-registration) with paired LOCAL+REMOTE TLVs.</summary>
        New2026_04 = 1,
        /// <summary>Post-2026-04 CSP firmware: 6-byte header, but first sub-msg
        /// uses type=0x02 (METADATA) with 2-byte body pad, only LOCAL TLV pair
        /// (no REMOTE), trailing 1-byte XOR status. Content sub-msg uses 0x70 REMOTE
        /// marker and BE size fields. Verified from PitHouse capture
        /// `wireshark/csp/upload-asdf-dash.pcapng` against W17 wheel firmware
        /// `RS21-W17-MC SW` (2026-04-28).</summary>
        New2026_04_Type02 = 2,
    }

    /// <summary>
    /// Builds file-transfer sub-messages used to upload a `.mzdash` dashboard
    /// file to the wheel. Replaces the older session 0x01 FF-prefixed 3-field
    /// uploader that matched a pre-2025 firmware snapshot but which 2025-11+
    /// firmware no longer accepts.
    ///
    /// Two sub-messages are sent in sequence:
    ///
    ///   Sub-msg 1 — path registration (no file content):
    ///     header(8 legacy / 6 new)
    ///     0x8C local_path_utf16le (null-terminated)
    ///     0x8C local_path_utf16le (repeat)
    ///     0x84 remote_staging_path_utf16le
    ///     0x84 remote_staging_path_utf16le (repeat)
    ///     MD5_len(1=0x10) + MD5(16)
    ///     reserved(4=0x00000000)
    ///     token(4 LE)
    ///     sentinel(4=0xFFFFFFFF)
    ///
    ///   Sub-msg 2 — file content push:
    ///     header(8 legacy with type=0x03 / 6 new with type=0x03)
    ///     0x8C local_path_utf16le + repeat
    ///     0x84 remote_staging_path_utf16le + repeat
    ///     MD5_len(1=0x10) + MD5(16)
    ///     reserved(4)
    ///     token(4 LE) + token(4 LE)
    ///     file_count(4 LE = 1)
    ///     dest_path_byte_len(4 LE)
    ///     dest_path_utf16BE (NOT null-terminated)
    ///     compressed_header(12) + zlib_stream
    ///
    /// The 12-byte compressed header before the zlib stream:
    ///   CRC32(uncompressed, LE=4B) + 0x08 0x00 0x00 0x00 (4B) + uncompressed_size_BE(4B)
    ///
    /// Legacy wire format confirmed by decoding usb-capture/09-04-26/dash-upload.pcapng
    /// session 0x04 host→device reassembly. New format documented in
    /// <c>docs/protocol/dashboard-upload/6-byte-submsg-header.md</c> and
    /// <c>docs/protocol/dashboard-upload/per-chunk-trailer.md</c>.
    /// </summary>
    public static class FileTransferBuilder
    {
        /// <summary>Header byte identifying the sender role. Host = 0x02. Legacy format only.</summary>
        public const byte HeaderRoleHost = 0x02;
        /// <summary>Header byte identifying the device role (for parsing only). Legacy format only.</summary>
        public const byte HeaderRoleDevice = 0x01;
        /// <summary>Max chunk payload size the sender advertises. Host uses 0x40 (64). Legacy format only.</summary>
        public const byte HeaderMaxChunkHost = 0x40;
        /// <summary>TLV marker for a local (host-side) path entry. UTF-16LE.</summary>
        public const byte TlvLocalPath = 0x8C;
        /// <summary>TLV marker for a remote (device-side) path entry. UTF-16LE. Legacy format only — new firmware uses 0x70.</summary>
        public const byte TlvRemotePath = 0x84;

        /// <summary>Sentinel indicating no file content in sub-msg 1.</summary>
        private const uint NoContentSentinel = 0xFFFFFFFFu;

        /// <summary>
        /// Build sub-msg 1 — the path-registration preamble. Tells the wheel
        /// the host has a file ready to transfer and declares its MD5 so the
        /// wheel can prepare a staging location. Legacy 2025-11 wire format.
        /// </summary>
        public static byte[] BuildPathRegistration(string localTempPath,
                                                   string remoteStagingPath,
                                                   byte[] md5,
                                                   uint token)
            => BuildPathRegistration(localTempPath, remoteStagingPath, md5, token,
                FileTransferWireFormat.Legacy2025_11);

        /// <summary>
        /// Build sub-msg 1 with the chosen wire format.
        /// </summary>
        public static byte[] BuildPathRegistration(string localTempPath,
                                                   string remoteStagingPath,
                                                   byte[] md5,
                                                   uint token,
                                                   FileTransferWireFormat format)
        {
            if (md5 == null || md5.Length != 16)
                throw new ArgumentException("md5 must be 16 bytes", nameof(md5));

            if (format == FileTransferWireFormat.New2026_04_Type02)
            {
                byte[] metaBody = BuildMetadataBodyType02(localTempPath, md5, token);
                return Compose(0x02, metaBody, format);
            }

            byte[] body = BuildPathRegistrationBody(localTempPath, remoteStagingPath, md5, token);
            return Compose(0x01, body, format);
        }

        /// <summary>
        /// Build sub-msg 2 — the file content push. Contains the destination
        /// path (UTF-16BE) and a zlib-compressed copy of <paramref name="mzdashContent"/>
        /// prefixed by a 12-byte compressed header. Legacy 2025-11 wire format.
        /// </summary>
        public static byte[] BuildFileContent(string localTempPath,
                                              string remoteStagingPath,
                                              byte[] md5,
                                              uint token,
                                              string destPath,
                                              byte[] mzdashContent)
            => BuildFileContent(localTempPath, remoteStagingPath, md5, token, destPath,
                mzdashContent, FileTransferWireFormat.Legacy2025_11);

        /// <summary>
        /// Build sub-msg 2 with the chosen wire format.
        /// </summary>
        public static byte[] BuildFileContent(string localTempPath,
                                              string remoteStagingPath,
                                              byte[] md5,
                                              uint token,
                                              string destPath,
                                              byte[] mzdashContent,
                                              FileTransferWireFormat format)
        {
            if (md5 == null || md5.Length != 16)
                throw new ArgumentException("md5 must be 16 bytes", nameof(md5));

            if (format == FileTransferWireFormat.New2026_04_Type02)
            {
                byte[] type02Body = BuildFileContentBodyType02(localTempPath,
                    remoteStagingPath, md5, destPath, mzdashContent);
                return Compose(0x03, type02Body, format);
            }

            byte[] body = BuildFileContentBody(localTempPath, remoteStagingPath, md5, token,
                destPath, mzdashContent);
            return Compose(0x03, body, format);
        }

        /// <summary>
        /// Build sub-msg 2 as one or more sub-msgs (file content push), splitting if
        /// the body exceeds the 16-bit size cap of <see cref="FileTransferWireFormat.New2026_04"/>.
        /// </summary>
        /// <remarks>
        /// MVP scope: returns a single-element list for both wire formats when the
        /// total body fits in <c>0xFFFF</c> bytes, which covers typical mzdash files
        /// (≤ ~50 KB compressed). Larger uploads need true multi-sub-msg splitting
        /// per <c>docs/protocol/dashboard-upload/per-chunk-trailer.md</c>
        /// (continuation chunks) — the per-chunk counter semantics at body[281..283] and the
        /// 7B constant at body[284..290] still need a clean PitHouse capture to
        /// reverse-engineer. Until then, oversized uploads will throw at
        /// <see cref="BuildSubMsgHeader"/>.
        /// </remarks>
        public static System.Collections.Generic.List<byte[]> BuildFileContentChunked(
            string localTempPath, string remoteStagingPath, byte[] md5, uint token,
            string destPath, byte[] mzdashContent, FileTransferWireFormat format)
        {
            // For now both formats produce a single sub-msg. Hook left as a List
            // so the call sites (DashboardUploader, TelemetrySender) don't need
            // to change shape when true chunking lands.
            byte[] singleSubMsg = BuildFileContent(localTempPath, remoteStagingPath, md5, token,
                destPath, mzdashContent, format);
            return new System.Collections.Generic.List<byte[]> { singleSubMsg };
        }

        // ── body builders (header-agnostic) ──────────────────────────────────

        private static byte[] BuildPathRegistrationBody(string localTempPath,
                                                        string remoteStagingPath,
                                                        byte[] md5,
                                                        uint token)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WritePathTlv(w, TlvLocalPath, localTempPath);
            WritePathTlv(w, TlvLocalPath, localTempPath);
            WritePathTlv(w, TlvRemotePath, remoteStagingPath);
            WritePathTlv(w, TlvRemotePath, remoteStagingPath);
            w.Write((byte)0x10);              // MD5 length
            w.Write(md5);
            w.Write((uint)0);                 // reserved
            w.Write(token);
            w.Write(NoContentSentinel);       // signals "no content in this sub-msg"
            return ms.ToArray();
        }

        private static byte[] BuildFileContentBody(string localTempPath,
                                                   string remoteStagingPath,
                                                   byte[] md5,
                                                   uint token,
                                                   string destPath,
                                                   byte[] mzdashContent)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WritePathTlv(w, TlvLocalPath, localTempPath);
            WritePathTlv(w, TlvLocalPath, localTempPath);
            WritePathTlv(w, TlvRemotePath, remoteStagingPath);
            WritePathTlv(w, TlvRemotePath, remoteStagingPath);
            w.Write((byte)0x10);              // MD5 length
            w.Write(md5);
            w.Write((uint)0);                 // reserved
            w.Write(token);                   // token #1
            w.Write(token);                   // token #2 (same value observed)
            w.Write((uint)1);                 // file_count = 1
            byte[] destBytes = Encoding.BigEndianUnicode.GetBytes(destPath);
            w.Write((uint)destBytes.Length);
            w.Write(destBytes);
            w.Write(BuildCompressedHeader(mzdashContent));
            w.Write(CompressZlib(mzdashContent));
            return ms.ToArray();
        }

        // ── New2026_04_Type02 body builders ─────────────────────────────────
        // Layout reverse-engineered from PitHouse capture
        // `wireshark/csp/upload-asdf-dash.pcapng` against W17 firmware
        // `RS21-W17-MC SW`. Differences vs <see cref="FileTransferWireFormat.New2026_04"/>:
        //   * METADATA sub-msg uses type=0x02 (was 0x01) with no REMOTE TLVs.
        //   * Body starts with 2-byte `00 00` pad before first TLV.
        //   * REMOTE TLV marker = 0x70 (was 0x84).
        //   * Remote staging path = `/_moza_filetransfer_md5_<hex>` (no `/home/root` prefix).
        //   * Size fields after MD5 are big-endian.
        //   * Body trailer: `[reserved 4B][token 4B LE][ff ff ff ff sentinel][1B XOR status]`.

        private static byte[] BuildMetadataBodyType02(string localTempPath, byte[] md5, uint token)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)0); w.Write((byte)0);            // 2B body pad
            WritePathTlv(w, TlvLocalPath, localTempPath);
            WritePathTlv(w, TlvLocalPath, localTempPath);
            w.Write((byte)0x10);
            w.Write(md5);
            w.Write((uint)0);                              // reserved
            w.Write(token);                                // 4B token (LE)
            w.Write(NoContentSentinel);                    // ff ff ff ff
            byte[] body = ms.ToArray();
            byte xor = XorBody(body, 0, body.Length);
            byte[] result = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, result, 0, body.Length);
            result[body.Length] = xor;
            return result;
        }

        private static byte[] BuildFileContentBodyType02(string localTempPath,
                                                          string remoteStagingPath,
                                                          byte[] md5,
                                                          string destPath,
                                                          byte[] mzdashContent)
        {
            byte[] zlib = CompressZlib(mzdashContent);
            byte[] cmpHdr = BuildCompressedHeaderType02(mzdashContent, zlib.Length);

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)0); w.Write((byte)0);            // 2B body pad
            WritePathTlv(w, TlvLocalPath, localTempPath);
            WritePathTlv(w, TlvRemotePathNew, remoteStagingPath);
            w.Write((byte)0x10);
            w.Write(md5);
            w.Write((uint)0);                              // reserved 4B
            // file_count = 1 (single-file dashboard upload). Big-endian per
            // observed capture; PitHouse multi-file uploads (dash + image)
            // would set this to 2 with paired dest_path entries.
            WriteUInt32BE(w, 1);
            byte[] destBytes = Encoding.BigEndianUnicode.GetBytes(destPath);
            WriteUInt32BE(w, (uint)destBytes.Length);
            w.Write(destBytes);
            w.Write(cmpHdr);
            w.Write(zlib);
            byte[] body = ms.ToArray();
            byte xor = XorBody(body, 0, body.Length);
            byte[] result = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, result, 0, body.Length);
            result[body.Length] = xor;
            return result;
        }

        /// <summary>
        /// 12-byte compressed header for type-02 wire format:
        /// <c>[uncompressed_size BE 4B][compressed_size BE 4B][CRC32 LE 4B]</c>.
        /// Differs from legacy/<see cref="FileTransferWireFormat.New2026_04"/>
        /// header which packs `[CRC LE][08 00 00 00][uLen BE]`.
        /// </summary>
        public static byte[] BuildCompressedHeaderType02(byte[] uncompressed, int compressedLen)
        {
            uint crc = TierDefinitionBuilder.Crc32(uncompressed, 0, uncompressed.Length);
            var hdr = new byte[12];
            uint uLen = (uint)uncompressed.Length;
            hdr[0] = (byte)((uLen >> 24) & 0xFF);
            hdr[1] = (byte)((uLen >> 16) & 0xFF);
            hdr[2] = (byte)((uLen >> 8) & 0xFF);
            hdr[3] = (byte)(uLen & 0xFF);
            uint cLen = (uint)compressedLen;
            hdr[4] = (byte)((cLen >> 24) & 0xFF);
            hdr[5] = (byte)((cLen >> 16) & 0xFF);
            hdr[6] = (byte)((cLen >> 8) & 0xFF);
            hdr[7] = (byte)(cLen & 0xFF);
            hdr[8] = (byte)(crc & 0xFF);
            hdr[9] = (byte)((crc >> 8) & 0xFF);
            hdr[10] = (byte)((crc >> 16) & 0xFF);
            hdr[11] = (byte)((crc >> 24) & 0xFF);
            return hdr;
        }

        private static void WriteUInt32BE(BinaryWriter w, uint v)
        {
            w.Write((byte)((v >> 24) & 0xFF));
            w.Write((byte)((v >> 16) & 0xFF));
            w.Write((byte)((v >> 8) & 0xFF));
            w.Write((byte)(v & 0xFF));
        }

        /// <summary>TLV marker for a remote (device-side) path entry in
        /// <see cref="FileTransferWireFormat.New2026_04_Type02"/>. UTF-16LE.</summary>
        public const byte TlvRemotePathNew = 0x70;

        private static byte[] Compose(byte transferType, byte[] body, FileTransferWireFormat format)
        {
            byte[] header = BuildSubMsgHeader(transferType, body.Length, format);
            byte[] result = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, result, 0, header.Length);
            Buffer.BlockCopy(body, 0, result, header.Length, body.Length);
            return result;
        }

        /// <summary>
        /// 8-bit XOR over body bytes — message integrity status appended as the
        /// final byte of <see cref="FileTransferWireFormat.New2026_04_Type02"/>
        /// sub-msg bodies. The XOR is computed over every body byte preceding the
        /// status (the status byte is itself the last entry, so excluded). See
        /// <c>docs/protocol/dashboard-upload/upload-handshake-2026-04.md</c> §
        /// "1-byte XOR status after `ff*4` sentinel".
        /// </summary>
        public static byte XorBody(byte[] body, int offset, int length)
        {
            byte x = 0;
            int end = offset + length;
            for (int i = offset; i < end; i++)
                x ^= body[i];
            return x;
        }

        /// <summary>
        /// Build the file-transfer sub-msg header for the chosen wire format.
        /// </summary>
        /// <remarks>
        /// Legacy: <c>[role=0x02][max_chunk=0x40][type][reserved×5]</c> = 8 bytes,
        /// no explicit body-size field (size is implicit from the session-data
        /// chunking layer + sentinels).
        /// New: <c>[type][size_LE_2B][pad×3]</c> = 6 bytes, where size_LE is the
        /// body byte count. Body size capped at 65535 (firmware splits larger
        /// uploads across multiple sub-msgs each &lt;= 4384 bytes; see
        /// docs/protocol/dashboard-upload/per-chunk-trailer.md).
        /// </remarks>
        public static byte[] BuildSubMsgHeader(byte transferType, int bodyLength,
            FileTransferWireFormat format)
        {
            if (bodyLength < 0) throw new ArgumentOutOfRangeException(nameof(bodyLength));
            if (format == FileTransferWireFormat.Legacy2025_11)
                return new byte[] { HeaderRoleHost, HeaderMaxChunkHost, transferType, 0, 0, 0, 0, 0 };
            if (format == FileTransferWireFormat.New2026_04
                || format == FileTransferWireFormat.New2026_04_Type02)
            {
                if (bodyLength > 0xFFFF)
                    throw new ArgumentOutOfRangeException(nameof(bodyLength),
                        "New2026_04 sub-msg body must fit in 16 bits; split into multiple sub-msgs.");
                return new byte[]
                {
                    transferType,
                    (byte)(bodyLength & 0xFF),
                    (byte)((bodyLength >> 8) & 0xFF),
                    0, 0, 0,
                };
            }
            throw new ArgumentException($"Unknown wire format: {format}", nameof(format));
        }

        /// <summary>
        /// 12-byte pre-zlib header: CRC32(uncompressed, LE) + `08 00 00 00` + uncompressed_size (BE).
        /// </summary>
        public static byte[] BuildCompressedHeader(byte[] uncompressed)
        {
            uint crc = TierDefinitionBuilder.Crc32(uncompressed, 0, uncompressed.Length);
            var hdr = new byte[12];
            hdr[0] = (byte)(crc & 0xFF);
            hdr[1] = (byte)((crc >> 8) & 0xFF);
            hdr[2] = (byte)((crc >> 16) & 0xFF);
            hdr[3] = (byte)((crc >> 24) & 0xFF);
            hdr[4] = 0x08; hdr[5] = 0x00; hdr[6] = 0x00; hdr[7] = 0x00;
            uint uLen = (uint)uncompressed.Length;
            hdr[8] = (byte)((uLen >> 24) & 0xFF);
            hdr[9] = (byte)((uLen >> 16) & 0xFF);
            hdr[10] = (byte)((uLen >> 8) & 0xFF);
            hdr[11] = (byte)(uLen & 0xFF);
            return hdr;
        }

        /// <summary>zlib-compress (deflate with 78 DA header + Adler-32 trailer).</summary>
        public static byte[] CompressZlib(byte[] data)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0xDA);
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(data, 0, data.Length);
            uint adler = Adler32(data);
            output.WriteByte((byte)((adler >> 24) & 0xFF));
            output.WriteByte((byte)((adler >> 16) & 0xFF));
            output.WriteByte((byte)((adler >> 8) & 0xFF));
            output.WriteByte((byte)(adler & 0xFF));
            return output.ToArray();
        }

        /// <summary>MD5 of the raw mzdash bytes. Used for path naming + integrity.</summary>
        public static byte[] ComputeMd5(byte[] content)
        {
            using var md5 = MD5.Create();
            return md5.ComputeHash(content);
        }

        public static string Md5Hex(byte[] md5)
        {
            var sb = new StringBuilder(md5.Length * 2);
            foreach (var b in md5) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Build a local temp-file path the same way PitHouse names them.
        /// Used only as a label in the TLV preamble — the file never actually
        /// needs to exist on disk for the transfer to succeed.
        /// </summary>
        public static string BuildLocalTempPath(long timestampMs)
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "Temp");
            return $"{baseDir.Replace('\\', '/')}/_moza_filetransfer_tmp_{timestampMs}";
        }

        /// <summary>Device staging path the wheel uses for in-flight files.</summary>
        public static string BuildRemoteStagingPath(string md5Hex)
            => $"/home/root/_moza_filetransfer_md5_{md5Hex}";

        /// <summary>Staging path used by post-2026-04 CSP firmware: no `/home/root` prefix.</summary>
        public static string BuildRemoteStagingPathType02(string md5Hex)
            => $"/_moza_filetransfer_md5_{md5Hex}";

        /// <summary>Final destination path under the wheel's dashboard resource tree.</summary>
        public static string BuildDashboardDestPath(string dashboardName)
            => $"/home/moza/resource/dashes/{dashboardName}/{dashboardName}.mzdash";

        private static void WritePathTlv(BinaryWriter w, byte marker, string path)
        {
            w.Write(marker);
            w.Write((byte)0);
            byte[] bytes = Encoding.Unicode.GetBytes(path);
            w.Write(bytes);
            w.Write((byte)0);  // UTF-16LE null terminator
            w.Write((byte)0);
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }
    }
}
