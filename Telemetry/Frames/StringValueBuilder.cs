using System;
using System.IO;
using System.Text;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>
    /// Builds the host → wheel string-channel value push that travels as a
    /// <c>type=0x05</c> sub-msg on the tier-def/catalog session. String-typed
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
    /// <see cref="TierDefinitionBuilder.ChunkMessage"/> on the resolved
    /// tier-def session (<c>ResolveTierDefSession()</c>) to add chunk header
    /// + CRC32 + wire framing.
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
        // UTF-8. The protocol doc says "ASCII only, UTF-16 is not used" —
        // that was inferred from PitHouse captures of pure-ASCII track IDs
        // (imola / ks_laguna_seca) and rules out *UTF-16*, not Unicode in
        // general. MOZA is a Chinese manufacturer whose firmware needs CJK
        // glyphs for its own system UI, so the renderer is almost certainly
        // UTF-8 capable. ASCII fallback turned "Viñedos" into "Vi?edos" on
        // the wire; Latin-1 sent the raw 0xF1 byte and the wheel rendered a
        // missing-glyph placeholder (font has no slot at 0xF1 alone). UTF-8
        // encodes ñ as 0xC3 0xB1 — if the wheel's text widget decodes that
        // as one codepoint we get the actual ñ; if it decodes per-byte we
        // get two placeholders.
        //
        // strlen is byte count, not char count — the flag-byte low 7 bits
        // still cap us at 127 BYTES, which is ~42 CJK chars or ~63 European
        // accented chars. Real track names fit easily; we truncate by bytes
        // and accept that a truncation could split a multi-byte sequence
        // (the wheel will render the trailing orphan as a placeholder, no
        // worse than the missing-glyph case we already have).
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        public static byte[] Build(byte channelIdx, string value)
        {
            if (channelIdx == 0)
                throw new ArgumentOutOfRangeException(nameof(channelIdx),
                    "channel_idx is 1-based; idx=0 has no meaning in this protocol");
            value ??= "";

            byte[] ascii = Utf8NoBom.GetBytes(value);
            int strlen = ascii.Length;
            if (strlen > MaxStringLength)
            {
                strlen = MaxStringLength;
                // If the byte at the cut would have been a UTF-8 continuation
                // byte (0b10xxxxxx), the cut splits a multi-byte sequence and
                // the wheel sees an orphan tail. Walk back until the cut lands
                // on a sequence boundary (next byte is ASCII or a leading
                // multi-byte byte 0b11xxxxxx). Worst case loses 3 trailing
                // bytes — a single CJK codepoint or accented Latin character.
                while (strlen > 0 && (ascii[strlen] & 0xC0) == 0x80)
                    strlen--;
            }

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
