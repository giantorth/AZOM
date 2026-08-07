using System;
using System.Collections.Generic;
using System.Text;
using MozaPlugin.Telemetry.Sessions;

namespace MozaPlugin.Diagnostics
{
    /// <summary>
    /// Decodes the b2h FF <c>kind=14</c> device display-log payload.
    ///
    /// The FF record's value is:
    /// <code>
    /// [reserved: 4 bytes] [zlib stream]
    /// </code>
    /// inflating to a length-prefixed UTF-16BE line list — the same idiom the
    /// kind=8 catalog uses:
    /// <code>
    /// [count: u32 BE]
    /// count × ( [byteLen: u32 BE] [UTF-16BE text, byteLen bytes] )
    /// </code>
    ///
    /// Verified against <c>bridge-20260731-064830.jsonl</c>: value size 3151,
    /// inflating to 37 226 B with <c>count=100</c> and a first record of
    /// byteLen 176 = 88 chars — <c>"[Thu Jul 23 06:38:48 2026][ld-linux-aarch64.so.1][7]
    /// ./MOZADash(+0x96090) [0x7fa77c6090]"</c>. See
    /// docs/protocol/sessions/session-0x02-ff-init.md § Device log pull.
    /// </summary>
    public static class DeviceLogParser
    {
        /// <summary>Bytes of the FF value that precede the zlib stream.</summary>
        private const int ReservedPrefixBytes = 4;

        /// <summary>A device that answers with an implausible line count is
        /// corrupt or misparsed; refuse rather than allocate against it.</summary>
        private const int MaxLines = 4096;

        /// <summary>Longest single line we will accept (bytes, so half this in
        /// UTF-16 chars). Real lines run 150–400 B; backtraces can be longer.</summary>
        private const int MaxLineBytes = 8 * 1024;

        /// <summary>Outcome of a parse attempt.</summary>
        public readonly struct Result
        {
            /// <summary>True once the zlib stream inflated and a plausible line
            /// count was read — i.e. <see cref="DeclaredCount"/> is meaningful
            /// even if <see cref="Lines"/> is short of it.</summary>
            public readonly bool Decoded;

            /// <summary>Lines the host successfully decoded. May be shorter than
            /// <see cref="DeclaredCount"/> when the tail was truncated.</summary>
            public readonly string[] Lines;

            /// <summary>Line count the DEVICE said it sent. This is what the
            /// kind=15 receipt must carry — acking the count we managed to walk
            /// instead would leave the undecoded remainder in the device buffer
            /// forever, and it would be re-sent on every subsequent pull.</summary>
            public readonly int DeclaredCount;

            public Result(bool decoded, string[] lines, int declaredCount)
            {
                Decoded = decoded;
                Lines = lines;
                DeclaredCount = declaredCount;
            }

            public static readonly Result Failed =
                new Result(false, Array.Empty<string>(), 0);
        }

        /// <summary>
        /// Parse an inbound kind=14 value into log lines.
        ///
        /// Note the asymmetry between <see cref="Result.Lines"/> and
        /// <see cref="Result.DeclaredCount"/>: a truncated or partially
        /// desynced payload still yields the prefix that decoded (a lost chunk
        /// shouldn't discard the lines that did arrive), but the caller must
        /// still acknowledge the device's own count.
        /// </summary>
        public static Result Parse(byte[] value)
        {
            if (value == null || value.Length <= ReservedPrefixBytes) return Result.Failed;

            byte[]? inflated = SessionDataReassembler.DecompressZlib(value, ReservedPrefixBytes);
            if (inflated == null || inflated.Length == 0)
            {
                // The reserved prefix width is the one part of this layout we
                // have only one firmware's captures for; fall back to locating
                // the stream. DecompressZlib does not check the zlib magic, so
                // a wrong prefix width can also yield short garbage — hence the
                // length test above rather than a plain null check.
                inflated = SessionDataReassembler.TryDecompressByMagic(value);
            }
            if (inflated == null || inflated.Length < 4) return Result.Failed;

            uint count = ReadU32BE(inflated, 0);
            if (count > MaxLines) return Result.Failed;
            if (count == 0) return new Result(true, Array.Empty<string>(), 0);

            var lines = new List<string>((int)count);
            int pos = 4;
            for (uint i = 0; i < count; i++)
            {
                if (pos + 4 > inflated.Length) break;          // truncated tail
                uint byteLen = ReadU32BE(inflated, pos);
                pos += 4;
                if (byteLen > MaxLineBytes) break;             // desynced
                if (pos + byteLen > inflated.Length) break;    // truncated tail
                // UTF-16 is 2 bytes/unit; an odd length means we've lost sync.
                if ((byteLen & 1) != 0) break;

                string text = Encoding.BigEndianUnicode.GetString(inflated, pos, (int)byteLen);
                pos += (int)byteLen;
                lines.Add(text.TrimEnd('\r', '\n', '\0'));
            }

            return new Result(true, lines.ToArray(), (int)count);
        }

        private static uint ReadU32BE(byte[] buf, int offset)
        {
            return (uint)((buf[offset] << 24)
                        | (buf[offset + 1] << 16)
                        | (buf[offset + 2] << 8)
                        | buf[offset + 3]);
        }
    }
}
