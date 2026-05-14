using System;
using System.IO;
using System.IO.Compression;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// Orchestrates the session 0x04 file-transfer upload of a `.mzdash`
    /// dashboard file. Replaces the previous session 0x01 FF-prefixed upload
    /// that matched pre-2025-11 firmware — the current wheel firmware only
    /// acts on mzdash writes delivered via session 0x04.
    ///
    /// Produces two sub-messages (path registration + file content) built by
    /// <see cref="FileTransferBuilder"/>. The caller is responsible for the
    /// wire-level dance (wait for device's session 0x04 open, send sub-msg 1,
    /// wait for echo, send sub-msg 2, wait for ack, send end marker).
    /// </summary>
    public static class DashboardUploader
    {
        /// <summary>Bundle carrying the two sub-messages + the chosen correlation token.</summary>
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
            /// One or more file-content sub-msgs. Length 1 for legacy/2025-11 firmware;
            /// length N≥1 for new firmware once true chunking lands. Iterate this and
            /// chunk each entry through the session-data layer with its own seq range.
            /// </summary>
            public System.Collections.Generic.List<byte[]> SubMsg2Chunks { get; set; }
                = new System.Collections.Generic.List<byte[]>();
            public uint Token { get; set; }
            public string DashboardName { get; set; } = "";
            public string Md5Hex { get; set; } = "";
            public int UncompressedSize { get; set; }
        }

        /// <summary>
        /// Build a file-transfer upload for <paramref name="mzdashContent"/>
        /// using the legacy 2025-11 wire format.
        /// <paramref name="dashboardName"/> is used to construct the
        /// destination path (`/home/moza/resource/dashes/{name}/{name}.mzdash`)
        /// and should match the dashboard's `dirName` so PitHouse groups the
        /// upload under the correct UI entry.
        /// </summary>
        public static UploadPayload BuildUpload(byte[] mzdashContent, string dashboardName,
                                                uint token, long timestampMs)
            => BuildUpload(mzdashContent, dashboardName, token, timestampMs,
                FileTransferWireFormat.Legacy2025_11);

        /// <summary>
        /// Build a file-transfer upload with the chosen wire format. New
        /// firmware (2026-04+) requires <see cref="FileTransferWireFormat.New2026_04"/>.
        /// </summary>
        public static UploadPayload BuildUpload(byte[] mzdashContent, string dashboardName,
                                                uint token, long timestampMs,
                                                FileTransferWireFormat format)
        {
            if (mzdashContent == null) throw new ArgumentNullException(nameof(mzdashContent));
            if (string.IsNullOrEmpty(dashboardName))
                throw new ArgumentException("dashboardName required", nameof(dashboardName));
            byte[] md5 = FileTransferBuilder.ComputeMd5(mzdashContent);
            string md5Hex = FileTransferBuilder.Md5Hex(md5);
            string localTemp = FileTransferBuilder.BuildLocalTempPath(timestampMs);
            // New2026_04_Type02 firmware drops the `/home/root` prefix from
            // staging paths — confirmed in PitHouse capture against W17.
            string remoteStaging = format == FileTransferWireFormat.New2026_04_Type02
                ? FileTransferBuilder.BuildRemoteStagingPathType02(md5Hex)
                : FileTransferBuilder.BuildRemoteStagingPath(md5Hex);
            string destPath = FileTransferBuilder.BuildDashboardDestPath(dashboardName);
            var subMsg2Chunks = FileTransferBuilder.BuildFileContentChunked(
                localTemp, remoteStaging, md5, token, destPath, mzdashContent, format);
            // Concatenate for legacy callers that expect a single byte[].
            int totalLen = 0;
            foreach (var c in subMsg2Chunks) totalLen += c.Length;
            byte[] subMsg2Concat = new byte[totalLen];
            int off = 0;
            foreach (var c in subMsg2Chunks)
            {
                Buffer.BlockCopy(c, 0, subMsg2Concat, off, c.Length);
                off += c.Length;
            }
            return new UploadPayload
            {
                SubMsg1PathRegistration = FileTransferBuilder.BuildPathRegistration(
                    localTemp, remoteStaging, md5, token, format),
                SubMsg2FileContent = subMsg2Concat,
                SubMsg2Chunks = subMsg2Chunks,
                Token = token,
                DashboardName = dashboardName,
                Md5Hex = md5Hex,
                UncompressedSize = mzdashContent.Length,
            };
        }

        /// <summary>
        /// Pick a correlation token that doesn't clash with session/seq usage.
        /// PitHouse uses `0x054B` in the dash-upload capture; we use the low
        /// bits of the current tick so multiple uploads in a single session
        /// don't collide while still staying recognisable in logs.
        /// </summary>
        public static uint PickToken()
            => (uint)(Environment.TickCount ^ (int)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) & 0x7FFFFFFF;
    }
}
