using System;

namespace MozaPlugin.Sdk.Coap
{
    /// <summary>
    /// CoAP (RFC 7252) message-type and code constants used by the SDK
    /// emulation. Only the subset required by the PitHouse-mirror URI tree is
    /// defined; do NOT add codes here without a matching wire-capture frame.
    ///
    /// Code field is one byte split as <c>class:3 | detail:5</c> (e.g.
    /// 2.05 = 0b010_00101 = 0x45 = 69). The constants below collapse that to
    /// the raw byte value for direct comparison with <see cref="CoapMessage.Code"/>.
    /// </summary>
    public static class CoapCode
    {
        // Message types (2-bit field in header byte 0, bits 5..4).
        public const byte TypeCon = 0;
        public const byte TypeNon = 1;
        public const byte TypeAck = 2;
        public const byte TypeRst = 3;

        // Method codes (class 0).
        public const byte Empty = 0x00; // 0.00 — empty message (ping/RST)
        public const byte Get   = 0x01; // 0.01
        public const byte Post  = 0x02; // 0.02
        public const byte Put   = 0x03; // 0.03 — unused but defined for completeness
        public const byte Delete = 0x04; // 0.04 — unused but defined for completeness

        // Success responses (class 2).
        public const byte Valid    = 0x44; // 2.03
        public const byte Content  = 0x45; // 2.05

        // Client errors (class 4).
        public const byte BadRequest        = 0x80; // 4.00
        public const byte NotFound          = 0x84; // 4.04
        public const byte MethodNotAllowed  = 0x85; // 4.05

        // Server errors (class 5).
        public const byte InternalServerError = 0xA0; // 5.00

        /// <summary>Extract the class digit (0..7) from a code byte.</summary>
        public static int Class(byte code) => (code >> 5) & 0x07;

        /// <summary>Extract the detail digit (0..31) from a code byte.</summary>
        public static int Detail(byte code) => code & 0x1F;

        /// <summary>Format a code byte as the canonical "c.dd" string (e.g. "2.05").</summary>
        public static string Format(byte code) => $"{Class(code)}.{Detail(code):D2}";
    }
}
