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
        /// <param name="token">Correlation token (legacy wire formats only; ignored for Type02).</param>
        /// <param name="totalCompressedSize">For <see cref="FileTransferWireFormat.New2026_04_Type02"/>:
        /// byte count of the compressed payload that subsequent type=0x03 sub-msgs
        /// will deliver (= preamble + zlib stream once the multi-file bundle lands;
        /// = zlib stream length for the current single-file build). Written into
        /// the type=0x02 metadata body's <c>total_size:u32 BE</c> field at
        /// body[L+21:L+25] per the upload-handshake protocol. Ignored for legacy
        /// formats.</param>
        public static byte[] BuildPathRegistration(string localTempPath,
                                                   string remoteStagingPath,
                                                   byte[] md5,
                                                   uint token,
                                                   FileTransferWireFormat format,
                                                   uint totalCompressedSize = 0)
        {
            if (md5 == null || md5.Length != 16)
                throw new ArgumentException("md5 must be 16 bytes", nameof(md5));

            if (format == FileTransferWireFormat.New2026_04_Type02)
            {
                byte[] metaBody = BuildMetadataBodyType02(localTempPath, md5, totalCompressedSize);
                return Compose(0x02, metaBody, format);
            }

            byte[] body = BuildPathRegistrationBody(localTempPath, remoteStagingPath, md5, token);
            return Compose(0x01, body, format);
        }

        /// <summary>
        /// Build sub-msg 2 — the file content push. Legacy 2025-11 wire format
        /// (8-byte header, single dest_path, 12-byte CRC compressed header).
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
        /// Build sub-msg 2 with the chosen wire format. Type02 callers should
        /// use <see cref="BuildType03ChunksType02"/> instead; this dispatcher
        /// rejects Type02 because the new firmware requires a multi-chunk
        /// payload that can't be expressed as a single body.
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
                throw new InvalidOperationException(
                    "Type02 file content is multi-chunked; use BuildType03ChunksType02 instead.");
            }

            byte[] body = BuildFileContentBody(localTempPath, remoteStagingPath, md5, token,
                destPath, mzdashContent);
            return Compose(0x03, body, format);
        }

        /// <summary>
        /// Build the type=0x03 sub-msg list for the file-content push, splitting
        /// across multiple sub-msgs when required by the wire format. Type02
        /// always chunks at <see cref="Type03ChunkStride"/> (4092 byte) deflate
        /// strides per <c>docs/protocol/dashboard-upload/per-chunk-trailer.md</c>.
        /// Legacy formats return a single sub-msg as before.
        /// </summary>
        public static System.Collections.Generic.List<byte[]> BuildFileContentChunked(
            string localTempPath, string remoteStagingPath, byte[] md5, uint token,
            string destPath, byte[] mzdashContent, FileTransferWireFormat format)
        {
            if (format == FileTransferWireFormat.New2026_04_Type02)
            {
                throw new InvalidOperationException(
                    "Type02 chunking requires the pre-computed compressed payload (preamble + zlib); " +
                    "callers must use BuildType03ChunksType02 with the payload returned by " +
                    "BuildCompressedPayloadType02.");
            }

            byte[] singleSubMsg = BuildFileContent(localTempPath, remoteStagingPath, md5, token,
                destPath, mzdashContent, format);
            return new System.Collections.Generic.List<byte[]> { singleSubMsg };
        }

        /// <summary>
        /// Chunk stride for type=0x03 deflate slices on
        /// <see cref="FileTransferWireFormat.New2026_04_Type02"/> uploads:
        /// <c>0x0FFC</c> (= 4092 bytes). Every type=0x03 chunk except the
        /// last carries exactly this many bytes of the deflate stream at
        /// body[291:291+stride]; the last chunk carries the residual.
        /// Verified byte-exact 2026-05-15 against PitHouse upload #2
        /// (4 chunks × 4092 + 2993 residual = 15269-byte payload).
        /// </summary>
        public const int Type03ChunkStride = 0x0FFC;

        /// <summary>
        /// Build the uncompressed bundle preamble + zlib stream that gets
        /// delivered via subsequent type=0x03 sub-msgs. Returns the contiguous
        /// payload bytes; chunking happens in <see cref="BuildType03ChunksType02"/>.
        ///
        /// Layout (verified byte-exact 2026-05-15 against PitHouse upload #2,
        /// see <c>docs/protocol/dashboard-upload/sess05-bundle-contents.md</c>):
        /// <code>
        ///   [file_count: u32 BE]
        ///   for each file i in 0..N-1:
        ///       [dest_path[i]_byte_len: u32 BE]
        ///       [dest_path[i]: UTF-16BE, no NUL terminator]
        ///       [file[i]_uncompressed_size: u32 BE]
        ///   [total_compressed_size: u32 BE]   -- informational (PitHouse emits zlib.Length + 4)
        ///   [total_uncompressed_size: u32 LE] -- note: little-endian
        ///   [zlib stream: 78 9c + raw deflate(file[0] || file[1] || ...) + adler32]
        /// </code>
        ///
        /// The mzdash file (or any text file by convention) should be passed
        /// already CRLF-normalised — see <see cref="NormalizeMzdashCrlf"/>.
        /// </summary>
        public static byte[] BuildCompressedPayloadType02(
            System.Collections.Generic.IList<(string destPath, byte[] content)> files)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            if (files.Count == 0)
                throw new ArgumentException("At least one file required.", nameof(files));

            // Concatenate file bytes in dest_path order — the wheel slices the
            // decompressed output using the per-file `uncompressed_size` fields
            // from the preamble, so order must match preamble order.
            long totalUncomp = 0;
            foreach (var (_, c) in files)
            {
                if (c == null) throw new ArgumentException("File content cannot be null.");
                totalUncomp += c.Length;
            }
            byte[] concat = new byte[totalUncomp];
            int co = 0;
            foreach (var (_, c) in files)
            {
                Buffer.BlockCopy(c, 0, concat, co, c.Length);
                co += c.Length;
            }
            byte[] zlib = CompressZlib(concat);

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            // Preamble — file table
            WriteUInt32BE(w, (uint)files.Count);
            foreach (var (destPath, content) in files)
            {
                byte[] destBytes = Encoding.BigEndianUnicode.GetBytes(destPath);
                WriteUInt32BE(w, (uint)destBytes.Length);
                w.Write(destBytes);
                WriteUInt32BE(w, (uint)content.Length);
            }
            // Informational total fields. PitHouse emits zlib.Length + 4 for
            // total_compressed_size in the preamble — the wheel uses the
            // chunk-envelope's value (= preamble + zlib total) for reassembly,
            // not this one, but matching PitHouse keeps the bytes consistent
            // for fingerprint comparisons.
            WriteUInt32BE(w, (uint)(zlib.Length + 4));
            w.Write((uint)totalUncomp);    // total_uncompressed_size LE (note: not BE)

            // Zlib stream follows the preamble immediately.
            w.Write(zlib);

            return ms.ToArray();
        }

        /// <summary>
        /// Chunk a compressed payload (preamble + zlib stream from
        /// <see cref="BuildCompressedPayloadType02"/>) into a list of type=0x03
        /// sub-msgs. Each sub-msg body is structured as:
        /// <code>
        ///   [00 00]                                   2-byte pad
        ///   [LOCAL TLV 0x8C + UTF-16LE host path + 00 00 NUL]
        ///   [REMOTE TLV 0x70 + UTF-16LE staging path + 00 00 NUL]
        ///   [0x10 flag]
        ///   [md5: 16 bytes]
        ///   [chunk_offset: u32 BE]                    \
        ///   [total_compressed_size: u32 BE]           |- 12-byte position envelope
        ///   [this_chunk_deflate_size: u32 BE]         /
        ///   [deflate slice]                           Type03ChunkStride bytes, residual on last
        ///   [xor status]                              XOR(body[:-1]) — validated by the wheel
        /// </code>
        /// The shared TLV/MD5 prefix is byte-identical across all chunks of one
        /// upload; only the position envelope and deflate slice differ per chunk.
        /// </summary>
        public static System.Collections.Generic.List<byte[]> BuildType03ChunksType02(
            string localTempPath, string remoteStagingPath, byte[] md5, byte[] compressedPayload)
        {
            if (md5 == null || md5.Length != 16)
                throw new ArgumentException("md5 must be 16 bytes", nameof(md5));
            if (compressedPayload == null || compressedPayload.Length == 0)
                throw new ArgumentException("compressedPayload must be non-empty",
                    nameof(compressedPayload));

            // Build the shared envelope once — bytes 0..(L-1) of every type=0x03
            // body up to (but not including) the per-chunk position envelope.
            byte[] sharedEnvelope = BuildType03SharedEnvelope(localTempPath, remoteStagingPath, md5);

            uint totalCompressedSize = (uint)compressedPayload.Length;
            int chunkCount = (compressedPayload.Length + Type03ChunkStride - 1) / Type03ChunkStride;
            var result = new System.Collections.Generic.List<byte[]>(chunkCount);

            for (int i = 0; i < chunkCount; i++)
            {
                uint chunkOffset = (uint)(i * Type03ChunkStride);
                int sliceStart = i * Type03ChunkStride;
                int sliceLen = Math.Min(Type03ChunkStride, compressedPayload.Length - sliceStart);

                // body = shared envelope + 12B position envelope + slice + 1B pad
                int bodyLen = sharedEnvelope.Length + 12 + sliceLen + 1;
                byte[] body = new byte[bodyLen];

                // shared envelope (LOCAL+REMOTE TLVs + flag + MD5)
                Buffer.BlockCopy(sharedEnvelope, 0, body, 0, sharedEnvelope.Length);

                // position envelope (3 × u32 BE)
                int poff = sharedEnvelope.Length;
                WriteUInt32BeAt(body, poff,     chunkOffset);
                WriteUInt32BeAt(body, poff + 4, totalCompressedSize);
                WriteUInt32BeAt(body, poff + 8, (uint)sliceLen);

                // deflate slice
                Buffer.BlockCopy(compressedPayload, sliceStart, body, poff + 12, sliceLen);

                // Final byte = XOR(body[:-1]) — the same 8-bit status checksum
                // the metadata carries. Earlier docs called this a "wire pad";
                // the wheel's FileTransferClient VALIDATES it and rejects the
                // sub-msg with "Invalid data: Checksum mismatch-- 0 <xor> <len>"
                // when it's zero (display log, 2026-08-16). Verified 27/27
                // against PitHouse ground-truth content chunks.
                body[bodyLen - 1] = XorBody(body, 0, bodyLen - 1);

                result.Add(Compose(0x03, body, FileTransferWireFormat.New2026_04_Type02));
            }

            return result;
        }

        /// <summary>
        /// Shared TLV envelope for every type=0x03 chunk of a Type02 upload —
        /// 2-byte pad, LOCAL path TLV, REMOTE path TLV, 0x10 flag, 16-byte MD5.
        /// Byte-identical across all chunks of one upload.
        /// </summary>
        private static byte[] BuildType03SharedEnvelope(string localTempPath,
                                                       string remoteStagingPath,
                                                       byte[] md5)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)0); w.Write((byte)0);                  // 2B body pad
            WritePathTlv(w, TlvLocalPath, localTempPath);        // 0x8C LOCAL
            WritePathTlv(w, TlvRemotePathNew, remoteStagingPath); // 0x70 REMOTE
            w.Write((byte)0x10);                                  // flag
            w.Write(md5);                                         // 16 bytes
            return ms.ToArray();
        }

        private static void WriteUInt32BeAt(byte[] buf, int offset, uint v)
        {
            buf[offset]     = (byte)((v >> 24) & 0xFF);
            buf[offset + 1] = (byte)((v >> 16) & 0xFF);
            buf[offset + 2] = (byte)((v >> 8)  & 0xFF);
            buf[offset + 3] = (byte)(v         & 0xFF);
        }

        /// <summary>
        /// CRLF-normalise text content (typically mzdash JSON) before bundling.
        /// Mirrors PitHouse: any existing <c>\r\n</c> is collapsed to <c>\n</c>
        /// first to avoid double-conversion, then every <c>\n</c> is expanded to
        /// <c>\r\n</c>. Binary blobs (PNGs etc.) must NOT be passed through this.
        /// Verified 2026-05-15 against PitHouse upload #2 bundle file[0]
        /// uncompressed_size = on-disk byte count after this conversion
        /// (332,404-byte LF mzdash → 340,342-byte CRLF normalised).
        /// </summary>
        public static byte[] NormalizeMzdashCrlf(byte[] content)
        {
            if (content == null) return Array.Empty<byte>();
            // First pass: drop any \r before \n so we don't double-convert.
            // Build into a list since we don't know exact final size.
            using var ms = new MemoryStream(content.Length + (content.Length >> 4));
            for (int i = 0; i < content.Length; i++)
            {
                byte b = content[i];
                if (b == 0x0D && i + 1 < content.Length && content[i + 1] == 0x0A)
                    continue; // skip \r in \r\n; the \n will be re-expanded below
                if (b == 0x0A)
                {
                    ms.WriteByte(0x0D);
                    ms.WriteByte(0x0A);
                }
                else
                {
                    ms.WriteByte(b);
                }
            }
            return ms.ToArray();
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
        // Layout verified byte-exact 2026-05-15 against PitHouse bridge
        // capture `sim/logs/bridge-20260514-170002.jsonl` upload #2.
        // Differences vs <see cref="FileTransferWireFormat.New2026_04"/>:
        //   * Metadata sub-msg type=0x02 carries [bytes_written:u32 BE = 0]
        //     [total_size:u32 BE] = byte count of preamble + zlib payload.
        //   * 2026-05+ metadata uses LOCAL TLV twice (no REMOTE) — see
        //     <see cref="BuildMetadataBodyType02"/>.
        //   * Content sub-msgs type=0x03 are chunked at 4092-byte deflate
        //     stride with a 12-byte per-chunk position envelope; see
        //     <see cref="BuildType03ChunksType02"/>.
        //   * Remote staging path = `/_moza_filetransfer_md5_<hex>` (no `/home/root` prefix).
        //   * Body trailer (metadata only): `[bytes_written:u32 BE = 0][total_size:u32 BE]
        //     [ff ff ff ff sentinel][1B XOR status]`. Content sub-msgs have
        //     an XOR status trailer as the final body byte, same formula as
        //     the metadata (validated by the wheel's FileTransferClient).

        /// <summary>
        /// Build the body of the type=0x02 metadata sub-msg
        /// (<see cref="FileTransferWireFormat.New2026_04_Type02"/>).
        ///
        /// Wire layout (relative to body offset):
        /// <code>
        ///   [00 00 pad]                       2 bytes
        ///   [LOCAL TLV #1]                    0x8C 0x00 + UTF-16LE local path + 00 00
        ///   [LOCAL TLV #2]                    identical duplicate of TLV #1 (2026-05+ PitHouse)
        ///   [flag 0x10]                       1 byte
        ///   [md5]                             16 bytes
        ///   [bytes_written:u32 BE = 0]        4 bytes — zero on host-emit
        ///   [total_size:u32 BE]               4 bytes — totalCompressedSize
        ///   [ff ff ff ff sentinel]            4 bytes
        ///   [xor status]                      1 byte (appended by caller)
        /// </code>
        /// Verified byte-exact 2026-05-15 against PitHouse upload #2's type=0x02
        /// body[307:319]. See <c>docs/protocol/dashboard-upload/upload-handshake-2026-04.md</c>
        /// §"1-byte XOR status after `ff*4` sentinel".
        /// </summary>
        private static byte[] BuildMetadataBodyType02(string localTempPath, byte[] md5, uint totalCompressedSize)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)0); w.Write((byte)0);            // 2B body pad
            WritePathTlv(w, TlvLocalPath, localTempPath);
            WritePathTlv(w, TlvLocalPath, localTempPath);
            w.Write((byte)0x10);
            w.Write(md5);
            WriteUInt32BE(w, 0);                           // bytes_written:u32 BE (zero on host-emit)
            WriteUInt32BE(w, totalCompressedSize);         // total_size:u32 BE
            w.Write(NoContentSentinel);                    // ff ff ff ff sentinel
            byte[] body = ms.ToArray();
            byte xor = XorBody(body, 0, body.Length);
            byte[] result = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, result, 0, body.Length);
            result[body.Length] = xor;
            return result;
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

        /// <summary>
        /// zlib-compress (deflate + 2-byte zlib header `78 9C` + Adler-32 trailer).
        /// The FLG byte 0x9C marks FLEVEL=10 (default compression), matching
        /// PitHouse's wire output. The deflate bit-stream itself comes from
        /// .NET <see cref="DeflateStream"/> with <see cref="CompressionLevel.Optimal"/>
        /// — actual block boundaries may differ from PitHouse's native zlib
        /// (different implementations), but the wheel doesn't enforce FLEVEL
        /// matches and accepts any valid deflate stream.
        /// FCHECK note: <c>(0x78 * 256 + 0x9C) % 31 == 0</c> is required by
        /// RFC 1950 — both 0x9C and 0xDA satisfy this; 0x9C is PitHouse's choice.
        /// </summary>
        public static byte[] CompressZlib(byte[] data)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
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
        /// Build a local temp-file path the same shape PitHouse names them.
        /// Used only as a label in the TLV preamble — the file never exists.
        ///
        /// The path length is load-bearing: the wheel firmware reads the
        /// file-transfer sub-msg fields at FIXED byte offsets calibrated to a
        /// 144-byte LOCAL TLV = 70-char path (34-char dir prefix +
        /// "_moza_filetransfer_tmp_" + 13-digit unix-ms). Every successful
        /// PitHouse capture has exactly this length; a longer real path (75
        /// chars under Proton's "steamuser") shifts the md5/position-envelope
        /// fields and the wheel answers the metadata with the degenerate
        /// 72-byte no-md5 ready-ack, then ignores all content (observed
        /// 2026-08-16, moza-wire-20260816-101613). So: constant synthetic
        /// prefix, never the real LocalApplicationData.
        /// </summary>
        public static string BuildLocalTempPath(long timestampMs)
            => $"C:/Users/simhb/AppData/Local/Temp/_moza_filetransfer_tmp_{timestampMs}";

        /// <summary>Device staging path the wheel uses for in-flight files.</summary>
        public static string BuildRemoteStagingPath(string md5Hex)
            => $"/home/root/_moza_filetransfer_md5_{md5Hex}";

        /// <summary>
        /// Staging path for <see cref="FileTransferWireFormat.New2026_04_Type02"/>:
        /// bare <c>/_moza_filetransfer_md5_&lt;hex&gt;</c>. Ground truth from two
        /// successful PitHouse uploads on 2026-07/08 W17 firmware
        /// (bridge-20260731 start.json, bridge-upload-groundtruth-20260816
        /// dashboard bundle): the type=0x03 content 0x70 REMOTE TLV carries the
        /// bare root-relative form — no <c>/tmp</c> and no <c>/home/root</c>
        /// prefix. (A 2026-05-era note claimed the bare path failed; that
        /// firmware also acked cross-session — both behaviors are gone.)
        /// </summary>
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
