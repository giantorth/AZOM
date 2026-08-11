using System;
using System.Collections.Generic;
using System.Text;

namespace MozaPlugin.Diagnostics
{
    /// <summary>
    /// Renders a serial capture to text with hardware identifiers masked out.
    /// Uploaded/exported bundles leave the user's machine, so any byte-sequence
    /// that identifies the specific hardware (MCU UIDs, serial-number ASCII —
    /// see <see cref="MozaData.GetIdentityByteSequences"/>) is masked wherever it
    /// appears in a frame. Everything else passes through as raw hex so the
    /// wire-decode tooling under <c>tools/</c> still works. Redaction runs only
    /// at bundle-build time, never on the serial hot path.
    /// </summary>
    public static class CaptureRedactor
    {
        // Trailing bytes of each masked run left visible — enough to correlate a
        // capture against a physical sticker without leaking the full identifier
        // (mirrors MozaLog.RedactTailChars, in bytes here rather than hex chars).
        private const int TailBytesVisible = 2;

        /// <summary>
        /// Format <paramref name="entries"/> like
        /// <see cref="SerialTrafficCapture.Format"/>, but with any occurrence of
        /// a known identity sequence rendered as <c>..</c>. Masked runs keep
        /// their last <see cref="TailBytesVisible"/> bytes visible.
        /// </summary>
        public static string FormatRedacted(
            IReadOnlyList<SerialTrafficCapture.Entry> entries, MozaData? data)
        {
            var sequences = data?.GetIdentityByteSequences();
            var sb = new StringBuilder(entries.Count * 64);
            sb.Append("# timestamp (local)        dir source     bytes (hardware identifiers masked as ..)\n");
            foreach (var e in entries)
            {
                SerialTrafficCapture.AppendEntryPrefix(sb, e);
                AppendMaskedHex(sb, e.Bytes, sequences);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Mask hardware identifiers in plain text (device log lines, which are
        /// human-readable rather than hex). Only the ASCII-printable identity
        /// sequences apply — MCU UIDs are binary and can't appear verbatim in a
        /// log line. Matched runs keep their last <see cref="TailBytesVisible"/>
        /// characters, mirroring the hex path.
        /// </summary>
        public static string RedactText(string text, MozaData? data)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sequences = data?.GetIdentityByteSequences();
            if (sequences == null || sequences.Count == 0) return text;

            foreach (var seq in sequences)
            {
                if (seq == null || seq.Length <= TailBytesVisible) continue;
                bool printable = true;
                foreach (var b in seq)
                {
                    if (b < 0x20 || b > 0x7E) { printable = false; break; }
                }
                if (!printable) continue;

                string needle = Encoding.ASCII.GetString(seq);
                if (text.IndexOf(needle, StringComparison.Ordinal) < 0) continue;
                string replacement = new string('.', needle.Length - TailBytesVisible)
                    + needle.Substring(needle.Length - TailBytesVisible);
                text = text.Replace(needle, replacement);
            }
            return text;
        }

        private static void AppendMaskedHex(
            StringBuilder sb, byte[] data, IReadOnlyList<byte[]>? sequences)
        {
            bool[]? masked = BuildMask(data, sequences);
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                if (masked != null && masked[i])
                {
                    sb.Append('.');
                    sb.Append('.');
                }
                else
                {
                    sb.Append(SerialTrafficCapture.HexChar(data[i] >> 4));
                    sb.Append(SerialTrafficCapture.HexChar(data[i] & 0xF));
                }
            }
        }

        /// <summary>
        /// Mark positions covered by an identity sequence, keeping the last
        /// <see cref="TailBytesVisible"/> bytes of each match visible. Returns
        /// null when nothing matches (common case — avoids the per-byte check).
        /// </summary>
        private static bool[]? BuildMask(byte[] data, IReadOnlyList<byte[]>? sequences)
        {
            if (sequences == null || sequences.Count == 0 || data.Length == 0) return null;
            bool[]? mask = null;
            foreach (var seq in sequences)
            {
                if (seq == null || seq.Length == 0 || seq.Length > data.Length) continue;
                int limit = data.Length - seq.Length;
                for (int i = 0; i <= limit; i++)
                {
                    if (!MatchesAt(data, i, seq)) continue;
                    mask = mask ?? new bool[data.Length];
                    int maskEnd = i + seq.Length - TailBytesVisible;
                    for (int j = i; j < maskEnd; j++) mask[j] = true;
                }
            }
            return mask;
        }

        private static bool MatchesAt(byte[] data, int offset, byte[] seq)
        {
            for (int k = 0; k < seq.Length; k++)
                if (data[offset + k] != seq[k]) return false;
            return true;
        }
    }
}
