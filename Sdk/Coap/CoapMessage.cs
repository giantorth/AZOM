using System;
using System.Collections.Generic;
using System.Text;

namespace MozaPlugin.Sdk.Coap
{
    /// <summary>
    /// A parsed or to-be-built CoAP message (RFC 7252 §3). Header layout:
    /// <code>
    ///   0                   1                   2                   3
    ///   0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |Ver| T |  TKL  |      Code     |          Message ID           |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |   Token (0..8 bytes)                                          |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |   Options (variable)                                          |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    ///  |1 1 1 1 1 1 1 1|    Payload (variable)                         |
    ///  +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// </code>
    /// Only Ver=1 is supported; anything else fails decode.
    /// </summary>
    public sealed class CoapMessage
    {
        public const int Version = 1;

        public byte Type { get; set; } // CoapCode.TypeCon/Non/Ack/Rst
        public byte Code { get; set; } // CoapCode.* — class.detail encoded
        public ushort MessageId { get; set; }
        public byte[] Token { get; set; } = Array.Empty<byte>();
        public List<CoapOption> Options { get; set; } = new List<CoapOption>();
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Convenience: extract repeated Uri-Path (#11) options in wire order.
        /// </summary>
        public IEnumerable<string> UriPathSegments
        {
            get
            {
                for (int i = 0; i < Options.Count; i++)
                {
                    var opt = Options[i];
                    if (opt.Number == CoapOptionNumber.UriPath)
                        yield return opt.ValueAsString();
                }
            }
        }

        /// <summary>
        /// Convenience: join Uri-Path segments with '/' (no leading slash).
        /// </summary>
        public string UriPath
        {
            get
            {
                var sb = new StringBuilder();
                bool first = true;
                foreach (var seg in UriPathSegments)
                {
                    if (!first) sb.Append('/');
                    sb.Append(seg);
                    first = false;
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Returns the first option matching <paramref name="number"/>, or null
        /// if absent. Use <see cref="GetOptions"/> for repeated options.
        /// </summary>
        public CoapOption? GetOption(int number)
        {
            for (int i = 0; i < Options.Count; i++)
            {
                if (Options[i].Number == number) return Options[i];
            }
            return null;
        }

        public IEnumerable<CoapOption> GetOptions(int number)
        {
            for (int i = 0; i < Options.Count; i++)
            {
                if (Options[i].Number == number) yield return Options[i];
            }
        }

        /// <summary>
        /// True if an Observe option is present. <paramref name="value"/>
        /// receives the option's uint value (0 = Register, 1 = Deregister per
        /// RFC 7641). Empty option body decodes as 0.
        /// </summary>
        public bool TryGetObserve(out uint value)
        {
            var opt = GetOption(CoapOptionNumber.Observe);
            if (opt == null) { value = 0; return false; }
            value = opt.Value.ValueAsUInt();
            return true;
        }

        /// <summary>Decode a CoAP datagram. Throws <see cref="FormatException"/> on malformed input.</summary>
        public static CoapMessage Decode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < 4) throw new FormatException("CoAP datagram shorter than 4-byte header.");

            byte b0 = data[0];
            int ver = (b0 >> 6) & 0x03;
            if (ver != Version) throw new FormatException($"Unsupported CoAP version {ver}.");
            byte type = (byte)((b0 >> 4) & 0x03);
            int tkl = b0 & 0x0F;
            if (tkl > 8) throw new FormatException($"Invalid TKL {tkl} (must be 0..8).");

            byte code = data[1];
            ushort mid = (ushort)((data[2] << 8) | data[3]);

            int offset = 4;
            if (offset + tkl > data.Length) throw new FormatException("Truncated token.");
            byte[] token;
            if (tkl == 0) token = Array.Empty<byte>();
            else
            {
                token = new byte[tkl];
                Buffer.BlockCopy(data, offset, token, 0, tkl);
                offset += tkl;
            }

            var options = CoapOptionCodec.Decode(data, ref offset, data.Length, out bool payloadMarkerSeen);

            byte[] payload;
            if (payloadMarkerSeen)
            {
                int payloadLen = data.Length - offset;
                if (payloadLen <= 0) throw new FormatException("Payload marker present but no payload bytes.");
                payload = new byte[payloadLen];
                Buffer.BlockCopy(data, offset, payload, 0, payloadLen);
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            return new CoapMessage
            {
                Type = type,
                Code = code,
                MessageId = mid,
                Token = token,
                Options = options,
                Payload = payload,
            };
        }

        /// <summary>
        /// Build the on-wire datagram. Options are sorted by number before
        /// encoding so callers don't need to maintain wire order manually.
        /// </summary>
        public byte[] Encode()
        {
            if (Token == null) Token = Array.Empty<byte>();
            if (Token.Length > 8) throw new InvalidOperationException("Token length must be 0..8 bytes.");
            if (Payload == null) Payload = Array.Empty<byte>();

            // Sort options ascending; stable order for repeated option numbers.
            // List<T>.Sort is NOT a stable sort across .NET runtimes (Framework's
            // Array.Sort uses insertion sort for small N which happens to be
            // stable but Mono / wine-mono / arbitrary future runtimes do not
            // guarantee it). Repeated Uri-Path options MUST preserve their
            // original order — re-ordering them changes the path the server
            // resolves. We do an insertion-sort by hand to make stability
            // explicit and runtime-independent.
            var sorted = new List<CoapOption>(Options);
            for (int i = 1; i < sorted.Count; i++)
            {
                var current = sorted[i];
                int j = i - 1;
                while (j >= 0 && sorted[j].Number > current.Number)
                {
                    sorted[j + 1] = sorted[j];
                    j--;
                }
                sorted[j + 1] = current;
            }

            byte[] optionsBytes = CoapOptionCodec.Encode(sorted);

            int size = 4 + Token.Length + optionsBytes.Length;
            if (Payload.Length > 0) size += 1 + Payload.Length;

            var buf = new byte[size];
            buf[0] = (byte)((Version << 6) | ((Type & 0x03) << 4) | (Token.Length & 0x0F));
            buf[1] = Code;
            buf[2] = (byte)((MessageId >> 8) & 0xFF);
            buf[3] = (byte)(MessageId & 0xFF);

            int o = 4;
            if (Token.Length > 0)
            {
                Buffer.BlockCopy(Token, 0, buf, o, Token.Length);
                o += Token.Length;
            }
            if (optionsBytes.Length > 0)
            {
                Buffer.BlockCopy(optionsBytes, 0, buf, o, optionsBytes.Length);
                o += optionsBytes.Length;
            }
            if (Payload.Length > 0)
            {
                buf[o++] = 0xFF;
                Buffer.BlockCopy(Payload, 0, buf, o, Payload.Length);
            }
            return buf;
        }
    }
}
