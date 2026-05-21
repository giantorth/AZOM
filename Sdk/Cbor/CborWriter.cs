using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MozaPlugin.Sdk.Cbor
{
    /// <summary>
    /// Minimal hand-rolled CBOR encoder (RFC 8949) covering only the major
    /// types the MOZA SDK uses on the wire:
    /// <list type="bullet">
    ///   <item><description>0 — unsigned integer</description></item>
    ///   <item><description>3 — text string (UTF-8)</description></item>
    ///   <item><description>4 — array</description></item>
    ///   <item><description>5 — map</description></item>
    /// </list>
    /// Everything else (negative ints, byte strings, tags, floats, indefinite
    /// length, simple values) is out of scope. This class is intentionally
    /// not a general-purpose CBOR library — it exists so the plugin can
    /// emit/consume PitHouse-shaped payloads without taking a NuGet dep.
    /// </summary>
    public static class CborWriter
    {
        // Major-type constants. RFC 8949 §3 packs the major type in the top
        // 3 bits of the initial byte; the bottom 5 are the "argument" (which
        // either is the value itself if <24, or indicates how many bytes of
        // value follow).
        private const byte MajorUnsigned = 0;
        private const byte MajorText = 3;
        private const byte MajorArray = 4;
        private const byte MajorMap = 5;

        /// <summary>
        /// Encode a list of strings as a CBOR array (major type 4) whose
        /// elements are text strings (major type 3).
        /// </summary>
        public static byte[] WriteArray(IReadOnlyList<string> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            using (var ms = new MemoryStream())
            {
                WriteTypeAndArg(ms, MajorArray, (ulong)items.Count);
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null)
                        throw new ArgumentException($"Array element {i} is null; CBOR null/undefined is not supported by this subset.", nameof(items));
                    WriteText(ms, item);
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Encode a list of non-negative ints as a CBOR array (major type 4)
        /// whose elements are unsigned ints (major type 0). Used by the
        /// pedal / handbrake "non-linear curve" surfaces, which are exposed
        /// to the SDK as <c>vector&lt;int&gt;</c> of 5 axis-output values
        /// (0..100). Negative inputs throw — the subset does not encode
        /// major type 1 (negative integer), and the plugin's curve fields
        /// are unsigned by domain.
        /// </summary>
        public static byte[] WriteArray(IReadOnlyList<int> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            using (var ms = new MemoryStream())
            {
                WriteTypeAndArg(ms, MajorArray, (ulong)items.Count);
                for (int i = 0; i < items.Count; i++)
                {
                    int v = items[i];
                    if (v < 0)
                        throw new ArgumentException(
                            $"Array element {i} is negative ({v}); negative ints are out of subset scope.",
                            nameof(items));
                    WriteTypeAndArg(ms, MajorUnsigned, (ulong)(uint)v);
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Encode a map (major type 5) of text-string keys to unsigned-int
        /// values. Used for the LimitAngle / EqualizerAmp PUT shapes which
        /// carry two named scalars per call.
        /// </summary>
        public static byte[] WriteMap(IReadOnlyDictionary<string, int> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));

            using (var ms = new MemoryStream())
            {
                WriteTypeAndArg(ms, MajorMap, (ulong)entries.Count);
                foreach (var kvp in entries)
                {
                    if (kvp.Key == null)
                        throw new ArgumentException("Map contains null key; not supported by this subset.", nameof(entries));
                    if (kvp.Value < 0)
                        throw new ArgumentException($"Map value for key '{kvp.Key}' is negative ({kvp.Value}); negative ints are out of subset scope.", nameof(entries));
                    WriteText(ms, kvp.Key);
                    WriteTypeAndArg(ms, MajorUnsigned, (ulong)(uint)kvp.Value);
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Encode a map (major type 5) of text-string keys to text-string
        /// values. Useful for purely-string manifest payloads.
        /// </summary>
        public static byte[] WriteMap(IReadOnlyDictionary<string, string> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));

            using (var ms = new MemoryStream())
            {
                WriteTypeAndArg(ms, MajorMap, (ulong)entries.Count);
                foreach (var kvp in entries)
                {
                    if (kvp.Key == null)
                        throw new ArgumentException("Map contains null key; not supported by this subset.", nameof(entries));
                    if (kvp.Value == null)
                        throw new ArgumentException($"Map value for key '{kvp.Key}' is null; not supported by this subset.", nameof(entries));
                    WriteText(ms, kvp.Key);
                    WriteText(ms, kvp.Value);
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Encode a map of text keys to mixed-type values. Each value must
        /// be a boxed <see cref="int"/>, <see cref="uint"/>, or
        /// <see cref="string"/>. Other types throw — this writer does not
        /// fall back to ToString() because silent coercion would mask bugs
        /// in callers that hand us the wrong shape. Designed for the
        /// device-manifest payload (Stream 4's <c>DeviceCatalog</c>), which
        /// mixes ints (firmware versions, types) with text identifiers.
        /// Uses a list of pairs (not <see cref="IReadOnlyDictionary{TKey,TValue}"/>)
        /// because the on-wire payload preserves a specific key order which
        /// dictionaries do not guarantee.
        /// </summary>
        public static byte[] WriteMap(IReadOnlyList<KeyValuePair<string, object>> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));

            using (var ms = new MemoryStream())
            {
                WriteTypeAndArg(ms, MajorMap, (ulong)entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    var kvp = entries[i];
                    if (kvp.Key == null)
                        throw new ArgumentException($"Map entry {i} has null key; not supported by this subset.", nameof(entries));
                    if (kvp.Value == null)
                        throw new ArgumentException($"Map entry '{kvp.Key}' has null value; not supported by this subset.", nameof(entries));
                    WriteText(ms, kvp.Key);

                    switch (kvp.Value)
                    {
                        case string s:
                            WriteText(ms, s);
                            break;
                        case int i32:
                            if (i32 < 0)
                                throw new ArgumentException($"Map entry '{kvp.Key}' has negative int value ({i32}); negative ints are out of subset scope.", nameof(entries));
                            WriteTypeAndArg(ms, MajorUnsigned, (ulong)(uint)i32);
                            break;
                        case uint u32:
                            WriteTypeAndArg(ms, MajorUnsigned, u32);
                            break;
                        default:
                            throw new ArgumentException(
                                $"Map entry '{kvp.Key}' has unsupported value type '{kvp.Value.GetType().FullName}'. " +
                                "Only string, int, and uint are supported by this subset.",
                                nameof(entries));
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Write the initial-byte plus extended argument bytes for a given
        /// major type and unsigned argument value. RFC 8949 §3:
        /// <list type="bullet">
        ///   <item><description>arg &lt; 24: argument is encoded directly in low 5 bits</description></item>
        ///   <item><description>arg &lt; 2^8:  low 5 bits = 24, then 1 byte BE</description></item>
        ///   <item><description>arg &lt; 2^16: low 5 bits = 25, then 2 bytes BE</description></item>
        ///   <item><description>arg &lt; 2^32: low 5 bits = 26, then 4 bytes BE</description></item>
        ///   <item><description>else:         low 5 bits = 27, then 8 bytes BE</description></item>
        /// </list>
        /// CBOR uses big-endian for all multi-byte arguments; do not confuse
        /// with the little-endian MOZA serial protocol used elsewhere.
        /// </summary>
        private static void WriteTypeAndArg(MemoryStream ms, byte majorType, ulong arg)
        {
            byte high = (byte)(majorType << 5);
            if (arg < 24)
            {
                ms.WriteByte((byte)(high | (byte)arg));
            }
            else if (arg <= 0xFF)
            {
                ms.WriteByte((byte)(high | 24));
                ms.WriteByte((byte)arg);
            }
            else if (arg <= 0xFFFF)
            {
                ms.WriteByte((byte)(high | 25));
                ms.WriteByte((byte)(arg >> 8));
                ms.WriteByte((byte)(arg & 0xFF));
            }
            else if (arg <= 0xFFFFFFFFUL)
            {
                ms.WriteByte((byte)(high | 26));
                ms.WriteByte((byte)((arg >> 24) & 0xFF));
                ms.WriteByte((byte)((arg >> 16) & 0xFF));
                ms.WriteByte((byte)((arg >> 8) & 0xFF));
                ms.WriteByte((byte)(arg & 0xFF));
            }
            else
            {
                ms.WriteByte((byte)(high | 27));
                ms.WriteByte((byte)((arg >> 56) & 0xFF));
                ms.WriteByte((byte)((arg >> 48) & 0xFF));
                ms.WriteByte((byte)((arg >> 40) & 0xFF));
                ms.WriteByte((byte)((arg >> 32) & 0xFF));
                ms.WriteByte((byte)((arg >> 24) & 0xFF));
                ms.WriteByte((byte)((arg >> 16) & 0xFF));
                ms.WriteByte((byte)((arg >> 8) & 0xFF));
                ms.WriteByte((byte)(arg & 0xFF));
            }
        }

        private static void WriteText(MemoryStream ms, string s)
        {
            // CBOR text strings are explicitly UTF-8 (§3.1).
            var bytes = Encoding.UTF8.GetBytes(s);
            WriteTypeAndArg(ms, MajorText, (ulong)bytes.Length);
            if (bytes.Length > 0)
                ms.Write(bytes, 0, bytes.Length);
        }
    }
}
