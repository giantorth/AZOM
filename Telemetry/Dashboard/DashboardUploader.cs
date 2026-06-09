using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// Orchestrates the file-transfer upload of a `.mzdash` dashboard file
    /// plus its PNG widget dependencies to the wheel.
    ///
    /// For <see cref="FileTransferWireFormat.New2026_04_Type02"/> the upload is
    /// shaped as PitHouse 2026-05+ ships it:
    ///   1. Walk the mzdash JSON for <c>MD5/&lt;32hex&gt;.png</c> references,
    ///      resolving each to bytes from <c>&lt;sourceDir&gt;/Resource/MD5/&lt;hex&gt;.png</c>.
    ///   2. Normalise the mzdash JSON to CRLF line endings (PNG bytes pass through).
    ///   3. Build the uncompressed bundle preamble (file table) + a single zlib
    ///      stream over the concatenated file bytes
    ///      (<see cref="FileTransferBuilder.BuildCompressedPayloadType02"/>).
    ///   4. Compute MD5 over the assembled compressed payload (not the raw mzdash);
    ///      the staging path filename includes this hex.
    ///   5. Build the type=0x02 metadata sub-msg carrying that MD5 + total_size.
    ///   6. Chunk the payload at 4092-byte stride into type=0x03 sub-msgs with
    ///      per-chunk position envelopes
    ///      (<see cref="FileTransferBuilder.BuildType03ChunksType02"/>).
    ///
    /// Legacy wire formats keep the older single-blob path for older firmware.
    /// </summary>
    public static class DashboardUploader
    {
        /// <summary>Bundle carrying the upload sub-messages + correlation metadata.</summary>
        public sealed class UploadPayload
        {
            public byte[] SubMsg1PathRegistration { get; set; } = Array.Empty<byte>();
            /// <summary>
            /// Single concatenation of all sub-msg 2 chunks. Convenience accessor;
            /// callers driving the wire dance should iterate <see cref="SubMsg2Chunks"/>
            /// instead so each sub-msg is sent as its own session-data burst.
            /// </summary>
            public byte[] SubMsg2FileContent { get; set; } = Array.Empty<byte>();
            /// <summary>
            /// File-content sub-msgs. Length 1 for legacy/2025-11 firmware
            /// (single-blob type=0x03). Length N≥1 for Type02 (chunked at
            /// <see cref="FileTransferBuilder.Type03ChunkStride"/>).
            /// </summary>
            public List<byte[]> SubMsg2Chunks { get; set; } = new List<byte[]>();
            public uint Token { get; set; }
            public string DashboardName { get; set; } = "";
            /// <summary>Hex MD5 of the compressed payload (Type02) or raw mzdash (legacy).</summary>
            public string Md5Hex { get; set; } = "";
            public int UncompressedSize { get; set; }
            /// <summary>
            /// Byte count of the compressed payload delivered via subsequent
            /// type=0x03 sub-msgs. Mirrors the type=0x02 metadata body's
            /// <c>total_size:u32 BE</c> field. Zero for legacy wire formats.
            /// </summary>
            public uint TotalCompressedSize { get; set; }
            /// <summary>Number of PNG assets bundled alongside the mzdash (Type02 only).</summary>
            public int BundledPngCount { get; set; }
        }

        public static UploadPayload BuildUpload(byte[] mzdashContent, string dashboardName,
                                                uint token, long timestampMs)
            => BuildUpload(mzdashContent, dashboardName, token, timestampMs,
                FileTransferWireFormat.Legacy2025_11);

        public static UploadPayload BuildUpload(byte[] mzdashContent, string dashboardName,
                                                uint token, long timestampMs,
                                                FileTransferWireFormat format)
            => BuildUpload(mzdashContent, dashboardName, token, timestampMs, format, null);

        /// <summary>
        /// Build a file-transfer upload with the chosen wire format.
        /// <paramref name="mzdashSourceDirectory"/> is used to look up PNG asset
        /// dependencies referenced by the mzdash JSON at
        /// <c>&lt;dir&gt;/Resource/MD5/&lt;hex&gt;.png</c>. Pass <c>null</c> or
        /// empty when the mzdash came from an embedded resource — the upload
        /// will ship as a single-file bundle (file_count=1) and widgets that
        /// reference PNGs will render blank until the PNGs land on the wheel
        /// by some other path.
        /// </summary>
        public static UploadPayload BuildUpload(byte[] mzdashContent, string dashboardName,
                                                uint token, long timestampMs,
                                                FileTransferWireFormat format,
                                                string? mzdashSourceDirectory)
        {
            if (mzdashContent == null) throw new ArgumentNullException(nameof(mzdashContent));
            if (string.IsNullOrEmpty(dashboardName))
                throw new ArgumentException("dashboardName required", nameof(dashboardName));

            string localTemp = FileTransferBuilder.BuildLocalTempPath(timestampMs);
            string destMzdash = FileTransferBuilder.BuildDashboardDestPath(dashboardName);

            if (format == FileTransferWireFormat.New2026_04_Type02)
            {
                return BuildUploadType02(mzdashContent, dashboardName,
                    token, localTemp, destMzdash, mzdashSourceDirectory);
            }

            // Legacy 2025-11 wire path: single dest_path, single zlib stream,
            // MD5 of raw mzdash bytes, staging includes `/home/root` prefix.
            byte[] md5Legacy = FileTransferBuilder.ComputeMd5(mzdashContent);
            string md5HexLegacy = FileTransferBuilder.Md5Hex(md5Legacy);
            string remoteStagingLegacy = FileTransferBuilder.BuildRemoteStagingPath(md5HexLegacy);
            var legacyChunks = FileTransferBuilder.BuildFileContentChunked(
                localTemp, remoteStagingLegacy, md5Legacy, token, destMzdash, mzdashContent, format);

            int legacyTotalLen = 0;
            foreach (var c in legacyChunks) legacyTotalLen += c.Length;
            byte[] legacyConcat = new byte[legacyTotalLen];
            int legOff = 0;
            foreach (var c in legacyChunks)
            {
                Buffer.BlockCopy(c, 0, legacyConcat, legOff, c.Length);
                legOff += c.Length;
            }

            return new UploadPayload
            {
                SubMsg1PathRegistration = FileTransferBuilder.BuildPathRegistration(
                    localTemp, remoteStagingLegacy, md5Legacy, token, format),
                SubMsg2FileContent = legacyConcat,
                SubMsg2Chunks = legacyChunks,
                Token = token,
                DashboardName = dashboardName,
                Md5Hex = md5HexLegacy,
                UncompressedSize = mzdashContent.Length,
                TotalCompressedSize = 0,
                BundledPngCount = 0,
            };
        }

        /// <summary>
        /// Type02 build flow: CRLF-normalise mzdash, gather PNG refs,
        /// build compressed payload, derive MD5 + staging path from payload,
        /// emit metadata + chunked content sub-msgs.
        /// </summary>
        private static UploadPayload BuildUploadType02(byte[] mzdashContent, string dashboardName,
                                                       uint token, string localTemp,
                                                       string destMzdash,
                                                       string? mzdashSourceDirectory)
        {
            // 1. CRLF-normalise the mzdash JSON (PitHouse always does this;
            //    PNG bytes are binary and pass through).
            byte[] normalizedMzdash = FileTransferBuilder.NormalizeMzdashCrlf(mzdashContent);

            // 2. Walk the mzdash for MD5/<hex>.png widget refs and resolve
            //    each against the source dir. Missing files are logged once
            //    and skipped; the upload still lands but those widgets won't
            //    render until the PNGs reach the wheel.
            var files = new List<(string destPath, byte[] content)>();
            files.Add((destMzdash, normalizedMzdash));
            int bundledPngs = 0;
            if (!string.IsNullOrEmpty(mzdashSourceDirectory))
            {
                foreach (var (hex, bytes) in ResolvePngReferences(normalizedMzdash, mzdashSourceDirectory!))
                {
                    string pngDest = $"/home/moza/resource/images/MD5/{hex}.png";
                    files.Add((pngDest, bytes));
                    bundledPngs++;
                }
            }

            // 3. Build the compressed payload (preamble + zlib).
            byte[] payload = FileTransferBuilder.BuildCompressedPayloadType02(files);
            uint totalCompressedSize = (uint)payload.Length;

            // 4. MD5 of the assembled compressed payload — this is what the
            //    wheel uses as the staging-path filename and what shows up
            //    in the configJson state's hash field.
            byte[] md5 = FileTransferBuilder.ComputeMd5(payload);
            string md5Hex = FileTransferBuilder.Md5Hex(md5);
            string remoteStaging = FileTransferBuilder.BuildRemoteStagingPathType02(md5Hex);

            // 5. Metadata sub-msg carries md5 + total_size; type=0x02 form.
            byte[] metadata = FileTransferBuilder.BuildPathRegistration(
                localTemp, remoteStaging, md5, token,
                FileTransferWireFormat.New2026_04_Type02, totalCompressedSize);

            // 6. Chunk the payload at 4092-byte stride into type=0x03 sub-msgs.
            var chunks = FileTransferBuilder.BuildType03ChunksType02(
                localTemp, remoteStaging, md5, payload);

            int concatLen = 0;
            foreach (var c in chunks) concatLen += c.Length;
            byte[] concat = new byte[concatLen];
            int off = 0;
            foreach (var c in chunks)
            {
                Buffer.BlockCopy(c, 0, concat, off, c.Length);
                off += c.Length;
            }

            return new UploadPayload
            {
                SubMsg1PathRegistration = metadata,
                SubMsg2FileContent = concat,
                SubMsg2Chunks = chunks,
                Token = token,
                DashboardName = dashboardName,
                Md5Hex = md5Hex,
                UncompressedSize = mzdashContent.Length,
                TotalCompressedSize = totalCompressedSize,
                BundledPngCount = bundledPngs,
            };
        }

        // Matches the PNG ref forms used in mzdash JSON: `MD5/<32hex>.png`,
        // with either forward slash (Linux paths in widget src attributes) or
        // backslash (Windows paths in some PitHouse-generated JSON).
        private static readonly Regex PngRefRegex = new Regex(
            @"MD5[/\\]([0-9a-fA-F]{32})\.png",
            RegexOptions.Compiled);

        /// <summary>
        /// Walk the mzdash text for distinct <c>MD5/&lt;32hex&gt;.png</c>
        /// references and resolve each against
        /// <c>&lt;sourceDir&gt;/Resource/MD5/&lt;hex&gt;.png</c>. Missing
        /// files are logged at warn level and skipped — the bundle still
        /// uploads (file_count = 1 + N_resolved) but widgets bound to a
        /// missing image render blank on the wheel.
        /// </summary>
        private static IEnumerable<(string hex, byte[] bytes)> ResolvePngReferences(
            byte[] mzdashUtf8, string sourceDirectory)
        {
            string json;
            try
            {
                json = System.Text.Encoding.UTF8.GetString(mzdashUtf8);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Upload: failed to decode mzdash as UTF-8 for PNG scan: {ex.Message}");
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in PngRefRegex.Matches(json))
            {
                string hex = m.Groups[1].Value.ToLowerInvariant();
                if (!seen.Add(hex)) continue;

                string candidate = Path.Combine(sourceDirectory, "Resource", "MD5", hex + ".png");
                if (!File.Exists(candidate))
                {
                    // Also try lowercase directory variants (case-insensitive FS
                    // on Windows but not on Linux when SimHub runs in Proton —
                    // be lenient and look at both).
                    string altRes = Path.Combine(sourceDirectory, "resource", "MD5", hex + ".png");
                    string altMd5 = Path.Combine(sourceDirectory, "Resource", "md5", hex + ".png");
                    if (File.Exists(altRes)) candidate = altRes;
                    else if (File.Exists(altMd5)) candidate = altMd5;
                    else
                    {
                        MozaLog.Warn(
                            $"[AZOM] Upload: PNG asset MD5/{hex}.png referenced by mzdash but not " +
                            $"found at {candidate} — widget bound to it will render blank.");
                        continue;
                    }
                }

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(candidate);
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Upload: failed to read {candidate}: {ex.Message}");
                    continue;
                }

                yield return (hex, bytes);
            }
        }

        public static uint PickToken()
            => (uint)(Environment.TickCount ^ (int)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) & 0x7FFFFFFF;
    }
}
