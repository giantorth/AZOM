using System;
using System.IO;
using System.Text;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// Builds the host → wheel string-channel value push that travels on
    /// session 0x01 as a <c>type=0x05</c> sub-msg. String-typed
    /// <c>Data/Telemetry.json</c> channels (TrackId, TrackName, CarModel,
    /// SessionTypeName, Flag_Name, … — 23 such channels) cannot be
    /// bit-packed into the value frame; they ride this separate transport.
    ///
    /// Wire layout (one record per push):
    /// <code>
    /// [type=0x05] [size_LE u32 = 2 + strlen] [channel_idx u8] [flag u8 = 0x80|strlen] [ASCII strlen bytes, no NUL]
    /// </code>
    ///
    /// Where <c>channel_idx</c> is the 1-based idx the wheel assigned the
    /// channel's URL in its most recent catalog announcement (b2h sess=0x01
    /// type=0x04 record). The high bit on the flag byte signals "string
    /// value"; the low 7 bits redundantly carry the length so the wheel can
    /// resync mid-stream. Max practical string is 127 bytes, matching the
    /// <c>range: "1~100 character"</c> declarations.
    ///
    /// See <c>docs/protocol/sessions/session-0x01-channel-protocol.md</c>
    /// and <c>docs/protocol/findings/2026-05-14-sess01-channel-protocol-and-string-values.md</c>
    /// for the wire-format decode against captured AC `TrackId` push
    /// (`imola` → `ks_laguna_seca`).
    ///
    /// Caller responsibilities: feed the returned net-data bytes through
    /// <see cref="TierDefinitionBuilder.ChunkMessage"/> with session=0x01 to
    /// add chunk header + CRC32 + wire framing.
    /// </summary>
    public static class StringValueBuilder
    {
        public const int MaxStringLength = 127;

        /// <summary>
        /// Build the type=0x05 sub-msg body for one string channel.
        /// Returns the net-data bytes ready to be chunked + wired by the
        /// caller. <paramref name="value"/> longer than 127 bytes will be
        /// truncated (warning logged by caller's discretion — the wheel's
        /// flag byte cannot encode lengths above 127).
        /// </summary>
        public static byte[] Build(byte channelIdx, string value)
        {
            if (channelIdx == 0)
                throw new ArgumentOutOfRangeException(nameof(channelIdx),
                    "channel_idx is 1-based; idx=0 has no meaning in this protocol");
            value ??= "";

            byte[] ascii = Encoding.ASCII.GetBytes(value);
            int strlen = ascii.Length;
            if (strlen > MaxStringLength) strlen = MaxStringLength;

            using var ms = new MemoryStream(5 + 2 + strlen);
            using var w = new BinaryWriter(ms);
            w.Write((byte)0x05);
            w.Write((uint)(2 + strlen));        // size_LE = idx(1) + flag(1) + strlen
            w.Write(channelIdx);
            w.Write((byte)(0x80 | strlen));
            w.Write(ascii, 0, strlen);
            return ms.ToArray();
        }
    }
}
