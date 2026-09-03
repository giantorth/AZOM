namespace MozaPlugin.Telemetry.Sessions
{
    /// <summary>
    /// Inbound counterpart to <see cref="SessionPropertyPushBuilder.WrapFfRecord"/>:
    /// parses one FF property record out of a session byte stream.
    ///
    /// <code>
    /// [0xFF] [size:u32 LE] [inner_crc:u32 LE] [kind:u32 LE] [value: size-4 bytes]
    /// </code>
    ///
    /// <c>size</c> counts the 4-byte kind prefix, so the value is
    /// <c>size - 4</c> bytes and the whole record is <c>size + 9</c> on the wire.
    /// <c>inner_crc</c> is <c>crc32(kind ‖ value)</c>.
    ///
    /// The CRC check is load-bearing, not belt-and-braces: FF records are
    /// interleaved with the typed sub-msg / catalog TLV stream on the same
    /// session, so a scan for the 0xFF sentinel WILL hit 0xFF bytes inside
    /// other records. Only the CRC distinguishes a real record from a
    /// coincidental one.
    /// </summary>
    public static class FfRecordReader
    {
        /// <summary>Wire bytes preceding the value: sentinel + size + crc + kind.</summary>
        public const int HeaderBytes = 13;

        /// <summary>Reject absurd sizes before waiting for them. The largest FF
        /// record observed is the ~2.5 KB kind=11 catalog; kind=14 log payloads
        /// run 2.7–3.2 KB. Kept tight on purpose: a false sentinel whose size
        /// field happens to look plausible parks the scan until that many bytes
        /// accumulate, so the cap bounds how long a bogus header can stall
        /// record recovery.</summary>
        public const int MaxRecordSize = 64 * 1024;

        /// <summary>
        /// Try to parse an FF record starting exactly at <paramref name="offset"/>.
        /// Returns false when the bytes there are not a valid record OR when the
        /// record is valid but not yet fully buffered — the two cases are told
        /// apart by <paramref name="needMoreBytes"/> so the caller knows whether
        /// to wait for more chunks or resync past a false sentinel.
        /// </summary>
        public static bool TryParse(
            byte[] buf, int offset, int end,
            out uint kind, out int valueOffset, out int valueLength,
            out int consumed, out bool needMoreBytes)
        {
            kind = 0;
            valueOffset = 0;
            valueLength = 0;
            consumed = 0;
            needMoreBytes = false;

            if (buf == null || offset < 0 || end > buf.Length || offset >= end) return false;
            if (buf[offset] != 0xFF)
                return false;
            if (end - offset < HeaderBytes)
            {
                // Could still become a record once more chunks land.
                needMoreBytes = true;
                return false;
            }

            uint size = ReadU32LE(buf, offset + 1);
            // size must at least cover the kind, and stay sane.
            if (size < 4 || size > MaxRecordSize) return false;

            int total = HeaderBytes + (int)size - 4;   // 9 + size
            if (end - offset < total)
            {
                needMoreBytes = true;
                return false;
            }

            uint wireCrc = ReadU32LE(buf, offset + 5);
            // CRC covers kindAndValue = the `size` bytes starting at the kind.
            uint calcCrc = global::MozaPlugin.Telemetry.Frames.TierDefinitionBuilder
                .Crc32(buf, offset + 9, (int)size);
            if (calcCrc != wireCrc) return false;

            kind = ReadU32LE(buf, offset + 9);
            valueOffset = offset + HeaderBytes;
            valueLength = (int)size - 4;
            consumed = total;
            return true;
        }

        private static uint ReadU32LE(byte[] buf, int offset)
        {
            return (uint)(buf[offset]
                        | (buf[offset + 1] << 8)
                        | (buf[offset + 2] << 16)
                        | (buf[offset + 3] << 24));
        }
    }
}
