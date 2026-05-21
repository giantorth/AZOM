using System;
using System.Globalization;
using System.Text;

namespace MozaPlugin.Sdk
{
    /// <summary>
    /// Scalar payload codec helpers for the MOZA SDK over CoAP. The capture
    /// showed the wire uses an asymmetric encoding for single-int properties
    /// (FFB strength, max torque, etc.):
    /// <list type="bullet">
    ///   <item><description>GET responses carry the value as an ASCII text
    ///     representation under Content-Format <c>42</c>
    ///     (<c>application/octet-stream</c>). E.g. <c>"75"</c> → bytes
    ///     <c>0x37 0x35</c>.</description></item>
    ///   <item><description>POST requests carry the value as 4-byte
    ///     little-endian int32 under Content-Format <c>42</c>. E.g. <c>75</c>
    ///     → bytes <c>0x4B 0x00 0x00 0x00</c>.</description></item>
    /// </list>
    /// The asymmetry is intentional on the vendor side and is preserved here
    /// so a real iRacing client sees byte-identical exchanges to a PitHouse
    /// server. The helpers in this class are the single source of truth for
    /// scalar serialisation; resource handlers MUST NOT roll their own.
    /// </summary>
    public static class PayloadCodec
    {
        /// <summary>
        /// Content-Format value for <c>application/octet-stream</c>. Mirrors
        /// <see cref="Coap.CoapContentFormat.OctetStream"/>; re-exported here
        /// so resource handlers don't need a using for the Coap namespace.
        /// </summary>
        public const int CFOctetStream = 42;

        /// <summary>Content-Format value for <c>application/cbor</c>. Mirrors <see cref="Coap.CoapContentFormat.Cbor"/>.</summary>
        public const int CFCbor = 60;

        /// <summary>
        /// Encode <paramref name="value"/> as its base-10 ASCII string for
        /// use in GET scalar responses. Always invariant-culture so a
        /// non-en-US locale on the host does not change the byte output
        /// (a German "," decimal separator would break parsing — there is no
        /// fractional component here, but the principle stands for any
        /// future float properties).
        /// </summary>
        public static byte[] EncodeScalarAsAsciiText(int value)
        {
            string s = value.ToString(CultureInfo.InvariantCulture);
            return Encoding.ASCII.GetBytes(s);
        }

        /// <summary>
        /// Decode a 4-byte little-endian int32 from <paramref name="body"/>.
        /// Used by POST request handlers to consume scalar writes. Returns
        /// <c>false</c> when the body is not exactly 4 bytes — handlers should
        /// turn that into a 4.00 Bad Request response. Sign-extends naturally
        /// because the source field is int32 on the wire.
        /// </summary>
        public static bool TryDecodeScalarFromLittleEndian(byte[]? body, out int value)
        {
            if (body == null || body.Length != 4)
            {
                value = 0;
                return false;
            }
            // Little-endian on the wire — matches the MOZA serial protocol
            // convention. Do NOT confuse with CBOR's big-endian arguments.
            value = body[0]
                  | (body[1] << 8)
                  | (body[2] << 16)
                  | (body[3] << 24);
            return true;
        }

        /// <summary>
        /// Encode <paramref name="value"/> as a 4-byte little-endian int32.
        /// Not used by the GET scalar path (which is ASCII text) but kept for
        /// the rare echo-back / observe-notification cases where the server
        /// emits in the same shape the client sent.
        /// </summary>
        public static byte[] EncodeScalarAsLittleEndian(int value)
        {
            return new byte[]
            {
                (byte)(value         & 0xFF),
                (byte)((value >>  8) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 24) & 0xFF),
            };
        }
    }
}
