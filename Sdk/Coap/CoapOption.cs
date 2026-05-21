using System;
using System.Collections.Generic;

namespace MozaPlugin.Sdk.Coap
{
    /// <summary>
    /// Well-known CoAP option numbers used by the SDK emulation. Names match
    /// the RFC 7252 / RFC 7641 registry; subset only.
    /// </summary>
    public static class CoapOptionNumber
    {
        public const int Observe        = 6;  // RFC 7641
        public const int UriPath        = 11; // repeatable
        public const int ContentFormat  = 12;
    }

    /// <summary>
    /// Well-known Content-Format identifiers used in the capture.
    /// </summary>
    public static class CoapContentFormat
    {
        public const int OctetStream = 42;  // application/octet-stream
        public const int Cbor        = 60;  // application/cbor
    }

    /// <summary>
    /// A single CoAP option as a (number, raw-value-bytes) pair. The decoder
    /// produces these in numerical/wire order; the encoder requires the same.
    /// </summary>
    public readonly struct CoapOption
    {
        public int Number { get; }
        public byte[] Value { get; }

        public CoapOption(int number, byte[]? value)
        {
            if (number < 0) throw new ArgumentOutOfRangeException(nameof(number));
            Number = number;
            Value = value ?? Array.Empty<byte>();
        }

        /// <summary>Decode this option's value as a big-endian unsigned int (RFC 7252 §3.2 "uint").</summary>
        public uint ValueAsUInt()
        {
            uint v = 0;
            for (int i = 0; i < Value.Length; i++)
            {
                v = (v << 8) | Value[i];
            }
            return v;
        }

        /// <summary>Decode this option's value as UTF-8 text (Uri-Path segments etc.).</summary>
        public string ValueAsString()
            => Value.Length == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(Value);

        /// <summary>
        /// Encode a non-negative integer as the minimal-length big-endian byte
        /// sequence per RFC 7252 §3.2 ("uint" option format). Zero encodes as
        /// the empty byte string — NOT a single 0x00 byte. This matters for
        /// Observe=Register(0), which must serialize as a zero-length option.
        /// </summary>
        public static byte[] EncodeUInt(uint value)
        {
            if (value == 0) return Array.Empty<byte>();
            // Find minimum byte count.
            int bytes;
            if (value <= 0xFF) bytes = 1;
            else if (value <= 0xFFFF) bytes = 2;
            else if (value <= 0xFFFFFF) bytes = 3;
            else bytes = 4;
            var result = new byte[bytes];
            for (int i = bytes - 1; i >= 0; i--)
            {
                result[i] = (byte)(value & 0xFF);
                value >>= 8;
            }
            return result;
        }
    }

    /// <summary>
    /// Wire-format encode/decode for the option list between the token and
    /// payload-marker bytes. Implements RFC 7252 §3.1 delta+length nibble
    /// encoding including the 13/14 extended forms; 15 is reserved (payload
    /// marker for delta-nibble) and rejected.
    /// </summary>
    internal static class CoapOptionCodec
    {
        /// <summary>
        /// Decode options starting at <paramref name="offset"/> in
        /// <paramref name="data"/>. Stops at the payload marker (0xFF) or end
        /// of buffer. Advances <paramref name="offset"/> past the marker if
        /// present. Returns the option list in wire order.
        /// </summary>
        /// <exception cref="FormatException">Malformed option byte.</exception>
        public static List<CoapOption> Decode(byte[] data, ref int offset, int end, out bool payloadMarkerSeen)
        {
            payloadMarkerSeen = false;
            var options = new List<CoapOption>();
            int currentOptionNumber = 0;

            while (offset < end)
            {
                byte first = data[offset];
                if (first == 0xFF)
                {
                    // Payload marker — caller picks up payload after this byte.
                    offset++;
                    payloadMarkerSeen = true;
                    return options;
                }
                offset++;

                int deltaNibble = (first >> 4) & 0x0F;
                int lengthNibble = first & 0x0F;

                if (deltaNibble == 15) throw new FormatException("Reserved option delta nibble 15.");
                if (lengthNibble == 15) throw new FormatException("Reserved option length nibble 15.");

                int delta = ReadExtended(deltaNibble, data, ref offset, end);
                int length = ReadExtended(lengthNibble, data, ref offset, end);

                if (length < 0) throw new FormatException("Negative option length.");
                if (offset + length > end) throw new FormatException("Option value exceeds buffer.");

                currentOptionNumber += delta;
                var value = new byte[length];
                if (length > 0)
                {
                    Buffer.BlockCopy(data, offset, value, 0, length);
                    offset += length;
                }
                options.Add(new CoapOption(currentOptionNumber, value));
            }

            return options;
        }

        private static int ReadExtended(int nibble, byte[] data, ref int offset, int end)
        {
            if (nibble < 13) return nibble;
            if (nibble == 13)
            {
                if (offset + 1 > end) throw new FormatException("Truncated 8-bit extended option field.");
                int v = data[offset] + 13;
                offset++;
                return v;
            }
            if (nibble == 14)
            {
                if (offset + 2 > end) throw new FormatException("Truncated 16-bit extended option field.");
                int v = ((data[offset] << 8) | data[offset + 1]) + 269;
                offset += 2;
                return v;
            }
            // nibble == 15 is filtered by caller.
            throw new FormatException("Reserved option nibble 15.");
        }

        /// <summary>
        /// Encode the options into a wire-format byte sequence. The caller is
        /// responsible for ordering options by ascending option number; this
        /// method validates that invariant and throws on violation.
        /// </summary>
        public static byte[] Encode(IList<CoapOption> options)
        {
            // First pass: compute total size.
            int previousNumber = 0;
            int total = 0;
            for (int i = 0; i < options.Count; i++)
            {
                var opt = options[i];
                if (opt.Number < previousNumber)
                    throw new InvalidOperationException("Options must be sorted ascending by number before encoding.");
                int delta = opt.Number - previousNumber;
                int length = opt.Value.Length;
                total += 1; // header byte
                total += ExtendedFieldSize(delta);
                total += ExtendedFieldSize(length);
                total += length;
                previousNumber = opt.Number;
            }

            var buf = new byte[total];
            int o = 0;
            previousNumber = 0;
            for (int i = 0; i < options.Count; i++)
            {
                var opt = options[i];
                int delta = opt.Number - previousNumber;
                int length = opt.Value.Length;

                int deltaNibble = NibbleFor(delta);
                int lengthNibble = NibbleFor(length);

                buf[o++] = (byte)((deltaNibble << 4) | (lengthNibble & 0x0F));
                WriteExtended(deltaNibble, delta, buf, ref o);
                WriteExtended(lengthNibble, length, buf, ref o);

                if (length > 0)
                {
                    Buffer.BlockCopy(opt.Value, 0, buf, o, length);
                    o += length;
                }

                previousNumber = opt.Number;
            }
            return buf;
        }

        private static int NibbleFor(int value)
        {
            if (value < 0) throw new InvalidOperationException("Negative delta/length.");
            if (value < 13) return value;
            if (value < 269) return 13;
            if (value < 65805) return 14;
            throw new InvalidOperationException("Option delta/length out of supported range.");
        }

        private static int ExtendedFieldSize(int value)
        {
            if (value < 13) return 0;
            if (value < 269) return 1;
            return 2;
        }

        private static void WriteExtended(int nibble, int value, byte[] buf, ref int o)
        {
            if (nibble < 13) return;
            if (nibble == 13)
            {
                buf[o++] = (byte)(value - 13);
                return;
            }
            if (nibble == 14)
            {
                int adj = value - 269;
                buf[o++] = (byte)((adj >> 8) & 0xFF);
                buf[o++] = (byte)(adj & 0xFF);
                return;
            }
            throw new InvalidOperationException("Reserved extension nibble.");
        }
    }
}
