using System;
using System.Collections.Generic;
using System.Text;

namespace MozaPlugin.Sdk.Cbor
{
    /// <summary>
    /// Minimal hand-rolled CBOR decoder paired with <see cref="CborWriter"/>.
    /// Supports only the subset PitHouse uses on the wire (major types 0, 3,
    /// 4, 5). Anything else throws <see cref="CborFormatException"/> rather
    /// than returning a placeholder — incorrect typing in an incoming SDK
    /// PUT is a protocol bug, not a value we want to silently coerce.
    /// </summary>
    public static class CborReader
    {
        private const byte MajorUnsigned = 0;
        private const byte MajorText = 3;
        private const byte MajorArray = 4;
        private const byte MajorMap = 5;

        /// <summary>
        /// Decode a CBOR-encoded array of text strings into a
        /// <see cref="List{String}"/>. Throws if the payload is not a single
        /// top-level array of text strings or if trailing bytes remain.
        /// </summary>
        public static List<string> ReadArrayOfText(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            int offset = 0;
            var result = ReadArrayOfText(data, ref offset);
            if (offset != data.Length)
                throw new CborFormatException($"Trailing bytes after top-level array: consumed {offset} of {data.Length}.");
            return result;
        }

        /// <summary>
        /// Decode a CBOR map of text-string keys to unsigned-int values
        /// into a <see cref="Dictionary{String,Int32}"/>. Values must fit in
        /// a signed int — larger arguments throw. Duplicate keys throw
        /// (RFC 8949 §3.1 allows duplicates but PitHouse never emits them
        /// and silently overwriting could mask a malformed payload).
        /// </summary>
        public static Dictionary<string, int> ReadMapStringToInt(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            int offset = 0;
            var result = ReadMapStringToInt(data, ref offset);
            if (offset != data.Length)
                throw new CborFormatException($"Trailing bytes after top-level map: consumed {offset} of {data.Length}.");
            return result;
        }

        /// <summary>
        /// Decode the next CBOR item starting at offset 0 of
        /// <paramref name="data"/>. Returns:
        /// <list type="bullet">
        ///   <item><description><see cref="int"/>  — unsigned int values that fit in Int32.</description></item>
        ///   <item><description><see cref="uint"/> — unsigned int values larger than Int32.MaxValue but ≤ UInt32.MaxValue.</description></item>
        ///   <item><description><see cref="ulong"/> — unsigned int values larger than UInt32.MaxValue.</description></item>
        ///   <item><description><see cref="string"/> — text string.</description></item>
        ///   <item><description><see cref="List{Object}"/> — array; each element re-decoded recursively.</description></item>
        ///   <item><description><see cref="Dictionary{String,Object}"/> — map; keys must be text strings.</description></item>
        /// </list>
        /// Throws on any major type outside this subset.
        /// </summary>
        public static object ReadItem(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            int offset = 0;
            var result = ReadItem(data, ref offset);
            if (offset != data.Length)
                throw new CborFormatException($"Trailing bytes after top-level item: consumed {offset} of {data.Length}.");
            return result;
        }

        // ---- Internal offset-tracking core ---------------------------------

        private static List<string> ReadArrayOfText(byte[] data, ref int offset)
        {
            ReadHeader(data, ref offset, out byte majorType, out ulong arg);
            if (majorType != MajorArray)
                throw new CborFormatException($"Expected array (major type 4), got major type {majorType}.");
            if (arg > int.MaxValue)
                throw new CborFormatException($"Array length {arg} exceeds Int32.MaxValue.");

            int count = (int)arg;
            var list = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(ReadText(data, ref offset, $"array element {i}"));
            }
            return list;
        }

        private static Dictionary<string, int> ReadMapStringToInt(byte[] data, ref int offset)
        {
            ReadHeader(data, ref offset, out byte majorType, out ulong arg);
            if (majorType != MajorMap)
                throw new CborFormatException($"Expected map (major type 5), got major type {majorType}.");
            if (arg > int.MaxValue)
                throw new CborFormatException($"Map pair-count {arg} exceeds Int32.MaxValue.");

            int count = (int)arg;
            var dict = new Dictionary<string, int>(count, StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string key = ReadText(data, ref offset, $"map key {i}");
                ReadHeader(data, ref offset, out byte vMajor, out ulong vArg);
                if (vMajor != MajorUnsigned)
                    throw new CborFormatException($"Map value for key '{key}' is major type {vMajor}, expected 0 (unsigned int).");
                if (vArg > int.MaxValue)
                    throw new CborFormatException($"Map value {vArg} for key '{key}' exceeds Int32.MaxValue.");
                if (dict.ContainsKey(key))
                    throw new CborFormatException($"Duplicate map key '{key}'.");
                dict[key] = (int)vArg;
            }
            return dict;
        }

        private static object ReadItem(byte[] data, ref int offset)
        {
            ReadHeader(data, ref offset, out byte majorType, out ulong arg);
            switch (majorType)
            {
                case MajorUnsigned:
                    // Pick the narrowest .NET type that fits. Callers that
                    // need a specific type can cast / convert.
                    if (arg <= int.MaxValue) return (int)arg;
                    if (arg <= uint.MaxValue) return (uint)arg;
                    return arg; // ulong

                case MajorText:
                    if (arg > int.MaxValue)
                        throw new CborFormatException($"Text length {arg} exceeds Int32.MaxValue.");
                    return ReadTextPayload(data, ref offset, (int)arg);

                case MajorArray:
                    if (arg > int.MaxValue)
                        throw new CborFormatException($"Array length {arg} exceeds Int32.MaxValue.");
                    int alen = (int)arg;
                    var arr = new List<object>(alen);
                    for (int i = 0; i < alen; i++)
                        arr.Add(ReadItem(data, ref offset));
                    return arr;

                case MajorMap:
                    if (arg > int.MaxValue)
                        throw new CborFormatException($"Map pair-count {arg} exceeds Int32.MaxValue.");
                    int mlen = (int)arg;
                    var map = new Dictionary<string, object>(mlen, StringComparer.Ordinal);
                    for (int i = 0; i < mlen; i++)
                    {
                        // Generic decoder still requires string keys —
                        // matches PitHouse practice and keeps the return
                        // type sane.
                        ReadHeader(data, ref offset, out byte kMajor, out ulong kArg);
                        if (kMajor != MajorText)
                            throw new CborFormatException($"Map key {i} has major type {kMajor}, expected 3 (text).");
                        if (kArg > int.MaxValue)
                            throw new CborFormatException($"Map key {i} length {kArg} exceeds Int32.MaxValue.");
                        string key = ReadTextPayload(data, ref offset, (int)kArg);
                        if (map.ContainsKey(key))
                            throw new CborFormatException($"Duplicate map key '{key}'.");
                        map[key] = ReadItem(data, ref offset);
                    }
                    return map;

                default:
                    throw new CborFormatException(
                        $"Major type {majorType} is not supported by this subset (only 0, 3, 4, 5).");
            }
        }

        private static string ReadText(byte[] data, ref int offset, string context)
        {
            ReadHeader(data, ref offset, out byte majorType, out ulong arg);
            if (majorType != MajorText)
                throw new CborFormatException($"Expected text string at {context}, got major type {majorType}.");
            if (arg > int.MaxValue)
                throw new CborFormatException($"Text length {arg} at {context} exceeds Int32.MaxValue.");
            return ReadTextPayload(data, ref offset, (int)arg);
        }

        private static string ReadTextPayload(byte[] data, ref int offset, int length)
        {
            if (length < 0)
                throw new CborFormatException($"Negative text length {length}.");
            EnsureAvailable(data, offset, length);
            // CBOR mandates UTF-8 for major type 3.
            string s = length == 0 ? string.Empty : Encoding.UTF8.GetString(data, offset, length);
            offset += length;
            return s;
        }

        /// <summary>
        /// Parse a CBOR initial byte plus any trailing argument bytes into
        /// (majorType, arg). Mirrors RFC 8949 §3 exactly — see the matching
        /// comment in <c>CborWriter.WriteTypeAndArg</c>. Indefinite-length
        /// items (low 5 bits = 31) explicitly throw because they are not in
        /// the supported subset.
        /// </summary>
        private static void ReadHeader(byte[] data, ref int offset, out byte majorType, out ulong arg)
        {
            EnsureAvailable(data, offset, 1);
            byte initial = data[offset++];
            majorType = (byte)(initial >> 5);
            byte info = (byte)(initial & 0x1F);

            if (info < 24)
            {
                arg = info;
                return;
            }
            switch (info)
            {
                case 24:
                    EnsureAvailable(data, offset, 1);
                    arg = data[offset];
                    offset += 1;
                    return;
                case 25:
                    EnsureAvailable(data, offset, 2);
                    arg = ((ulong)data[offset] << 8) | data[offset + 1];
                    offset += 2;
                    return;
                case 26:
                    EnsureAvailable(data, offset, 4);
                    arg = ((ulong)data[offset] << 24)
                        | ((ulong)data[offset + 1] << 16)
                        | ((ulong)data[offset + 2] << 8)
                        | data[offset + 3];
                    offset += 4;
                    return;
                case 27:
                    EnsureAvailable(data, offset, 8);
                    arg = ((ulong)data[offset] << 56)
                        | ((ulong)data[offset + 1] << 48)
                        | ((ulong)data[offset + 2] << 40)
                        | ((ulong)data[offset + 3] << 32)
                        | ((ulong)data[offset + 4] << 24)
                        | ((ulong)data[offset + 5] << 16)
                        | ((ulong)data[offset + 6] << 8)
                        | data[offset + 7];
                    offset += 8;
                    return;
                case 28:
                case 29:
                case 30:
                    throw new CborFormatException(
                        $"Reserved additional-info value {info} encountered at offset {offset - 1}.");
                case 31:
                    throw new CborFormatException(
                        $"Indefinite-length item (additional-info 31) at offset {offset - 1} is not supported by this subset.");
                default:
                    // Unreachable — info is masked to 5 bits.
                    throw new CborFormatException($"Unexpected additional-info value {info}.");
            }
        }

        private static void EnsureAvailable(byte[] data, int offset, int needed)
        {
            if (offset < 0 || needed < 0 || offset + needed > data.Length)
                throw new CborFormatException(
                    $"Unexpected end of CBOR data: need {needed} byte(s) at offset {offset}, have {data.Length}.");
        }
    }

    /// <summary>
    /// Thrown when the CBOR reader encounters bytes that violate the
    /// supported subset or are otherwise malformed. Caller code is expected
    /// to log via <see cref="MozaLog"/> and respond with a CoAP 4.00 Bad
    /// Request (or equivalent) — never to swallow this silently.
    /// </summary>
    public sealed class CborFormatException : Exception
    {
        public CborFormatException(string message) : base(message) { }
        public CborFormatException(string message, Exception inner) : base(message, inner) { }
    }
}
