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
        /// <paramref name="mzdashSourceDirectory"/> is the first place PNG asset
        /// dependencies are looked up (<c>&lt;dir&gt;/Resource/MD5/&lt;hex&gt;.png</c>).
        /// <c>null</c> or empty is fine — PitHouse's shared image pool is
        /// searched regardless, so a Dashboard Studio project (which has no
        /// <c>Resource/</c> subtree) or an embedded/wheel-cache mzdash can still
        /// bundle its images. Anything still unresolved is logged with every
        /// path tried, and those widgets render blank on the wheel.
        /// See <see cref="ImageSearchDirectories"/>.
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
            // No source-dir guard: images also resolve from PitHouse's SHARED
            // pool (see ImageSearchDirectories), which is where Dashboard Studio
            // keeps them, so a dashboard with no Resource/ subtree — or one that
            // came from the wheel cache with no directory at all — can still
            // bundle its PNGs.
            foreach (var (hex, ext, bytes) in ResolveImageReferences(
                         normalizedMzdash, mzdashSourceDirectory ?? ""))
            {
                string imageDest = $"/home/moza/resource/images/MD5/{hex}.{ext}";
                files.Add((imageDest, bytes));
                bundledPngs++;
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

        // Matches the content-addressed image ref forms used in mzdash JSON:
        // `MD5/<32hex>.<ext>`, with either forward slash (Linux paths in
        // widget src attributes) or backslash (Windows paths in some
        // PitHouse-generated JSON). The wheel's image store preserves the
        // original extension — a 2026-08-16 PitHouse ground-truth capture
        // bundled `/home/moza/resource/images/MD5/<hex>.jpg` verbatim — so
        // the scan accepts every raster format the display firmware renders.
        private static readonly Regex ImageRefRegex = new Regex(
            @"MD5[/\\]([0-9a-fA-F]{32})\.(png|jpg|jpeg|bmp|gif)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Walk the mzdash text for distinct <c>MD5/&lt;32hex&gt;.&lt;ext&gt;</c>
        /// image references and resolve each against
        /// <c>&lt;sourceDir&gt;/Resource/MD5/&lt;hex&gt;.&lt;ext&gt;</c>. Missing
        /// files are logged at warn level and skipped — the bundle still
        /// uploads (file_count = 1 + N_resolved) but widgets bound to a
        /// missing image render blank on the wheel.
        /// </summary>
        /// <summary>
        /// Directories to search for <c>&lt;32hex&gt;.&lt;ext&gt;</c>, most
        /// specific first.
        ///
        /// <para>Two layouts exist in the wild and only the first was
        /// originally handled:</para>
        /// <list type="bullet">
        /// <item><c>&lt;dir&gt;/Resource/MD5/</c> — a dashboard downloaded from
        /// the wheel, or a PitHouse export bundle, which carries its own
        /// copies. Lowercase variants are tried too: the FS is case-insensitive
        /// on Windows but not under Proton.</item>
        /// <item><c>&lt;imageRoot&gt;/MD5/</c> — the SHARED pool MOZA Dashboard
        /// Studio actually authors against (default
        /// <c>%LOCALAPPDATA%\MOZA Pit House\_dashes\images</c>). Studio does not
        /// copy an image into the project folder, so a dashboard created there
        /// has no <c>Resource/</c> subtree at all and its custom images were
        /// silently dropped from the upload.</item>
        /// </list>
        /// </summary>
        private static IEnumerable<string> ImageSearchDirectories(string sourceDirectory)
        {
            if (!string.IsNullOrEmpty(sourceDirectory))
            {
                yield return Path.Combine(sourceDirectory, "Resource", "MD5");
                yield return Path.Combine(sourceDirectory, "resource", "MD5");
                yield return Path.Combine(sourceDirectory, "Resource", "md5");
                yield return Path.Combine(sourceDirectory, "resource", "md5");
            }

            string? imageRoot = null;
            try { imageRoot = UI.DashboardStudioLauncher.ResolveImageRoot(); }
            catch { /* discovery is best-effort; the sourceDir paths still apply */ }
            if (!string.IsNullOrEmpty(imageRoot))
            {
                yield return Path.Combine(imageRoot!, "MD5");
                yield return Path.Combine(imageRoot!, "md5");
            }
        }

        private static IEnumerable<(string hex, string ext, byte[] bytes)> ResolveImageReferences(
            byte[] mzdashUtf8, string sourceDirectory)
        {
            string json;
            try
            {
                json = System.Text.Encoding.UTF8.GetString(mzdashUtf8);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Upload: failed to decode mzdash as UTF-8 for image scan: {ex.Message}");
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in ImageRefRegex.Matches(json))
            {
                string hex = m.Groups[1].Value.ToLowerInvariant();
                string ext = m.Groups[2].Value.ToLowerInvariant();
                if (!seen.Add(hex)) continue;

                string fileName = hex + "." + ext;
                var searched = new List<string>(6);
                string? candidate = null;
                foreach (var dir in ImageSearchDirectories(sourceDirectory))
                {
                    string p = Path.Combine(dir, fileName);
                    searched.Add(p);
                    if (File.Exists(p)) { candidate = p; break; }
                }
                if (candidate == null)
                {
                    MozaLog.Warn(
                        $"[AZOM] Upload: image asset MD5/{fileName} referenced by mzdash but not " +
                        $"found — widget bound to it will render blank. Looked in: "
                        + string.Join(", ", searched));
                    continue;
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

                yield return (hex, ext, bytes);
            }
        }

        public static uint PickToken()
            => (uint)(Environment.TickCount ^ (int)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) & 0x7FFFFFFF;
    }
}
