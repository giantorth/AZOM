using System;
using System.Collections.Generic;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Builds the group-<c>0x35</c> keyed value-stream frames for the CM1 base-bridged
    /// dash (device id <c>0x14</c>). Unlike the FSR1's positional group-0x42 records, the
    /// CM1 stream is flat: each record is a 16-bit field key followed by a big-endian
    /// float32 value:
    /// <code>7E &lt;6N&gt; 35 14  [&lt;keyHi&gt;&lt;keyLo&gt;&lt;float32 BE&gt;] * N  &lt;csum&gt;</code>
    /// (group <c>0x36</c> is a lower-rate secondary stream, same record shape.) Records are
    /// streamed round-robin, 10 per frame. Handshake/switch mirror the FSR1 family but
    /// address dev <c>0x14</c>. Frame structure + encoding are proven against
    /// <c>FSR1_CM1.pcapng</c> (see <see cref="Cm1DashboardCatalog"/>). Pure/static so it is
    /// unit-testable without a connection.
    /// </summary>
    internal static class Cm1DisplayEmitter
    {
        internal const byte GroupPrimary = 0x35;
        internal const byte GroupSecondary = 0x36;
        private const byte Dev = MozaProtocol.DeviceDash; // 0x14

        internal const int RecordsPerFrame = 10;

        /// <summary>Group-0x43 1-byte session ping (<c>7E 01 43 14 00</c>), ~1 Hz.</summary>
        internal static readonly byte[] SessionPing =
            BuildRaw(new byte[] { 0x7E, 0x01, 0x43, Dev, 0x00 });

        /// <summary>Group-0x00 presence probe (<c>7E 00 00 14</c>); dash acks 0x80.</summary>
        internal static readonly byte[] PresenceProbe =
            BuildRaw(new byte[] { 0x7E, 0x00, 0x00, Dev });

        internal const int MinDashboardIndex = Cm1DashboardCatalog.MinDashboardIndex;
        internal const int MaxDashboardIndex = Cm1DashboardCatalog.MaxDashboardIndex;

        /// <summary>One field's wire value: a field key + its resolved float.</summary>
        internal readonly struct Record
        {
            public readonly byte[] Key;   // 2 bytes, wire order
            public readonly float Value;
            public Record(byte[] key, float value) { Key = key; Value = value; }
        }

        /// <summary>
        /// Host-initiated dashboard-select — group <c>0x32</c>, cmd <c>0x81</c>, big-endian
        /// u32 page index (<c>7E 05 32 14 81 00 00 00 &lt;index&gt;</c>). The dash switches its
        /// displayed page and acks with <c>B2 41 81 00 00 00 &lt;index&gt;</c>, and reports the
        /// new page via the <c>Table 7, Param 6 Written: N</c> log (group 0x0e). Verified
        /// against <c>FSR1_CM1.pcapng</c>.
        /// </summary>
        internal static byte[] BuildSelect(int index)
        {
            if (index < MinDashboardIndex) index = MinDashboardIndex;
            if (index > MaxDashboardIndex) index = MaxDashboardIndex;
            return BuildRaw(new byte[]
            {
                0x7E, 0x05, 0x32, Dev, 0x81,
                (byte)((index >> 24) & 0xFF), (byte)((index >> 16) & 0xFF),
                (byte)((index >> 8) & 0xFF), (byte)(index & 0xFF),
            });
        }

        /// <summary>
        /// Build a value frame for <paramref name="records"/> (≤ <see cref="RecordsPerFrame"/>)
        /// on <paramref name="group"/> (0x35 primary / 0x36 secondary). Each record is 6
        /// bytes: 2-byte key + big-endian float32.
        /// </summary>
        internal static byte[] BuildValueFrame(byte group, IReadOnlyList<Record> records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            int n = records.Count;
            int payloadLen = n * 6;
            var frame = new byte[4 + payloadLen + 1];
            frame[0] = 0x7E;
            frame[1] = (byte)payloadLen;
            frame[2] = group;
            frame[3] = Dev;
            int o = 4;
            for (int i = 0; i < n; i++)
            {
                var rec = records[i];
                frame[o++] = rec.Key[0];
                frame[o++] = rec.Key[1];
                WriteFloatBE(frame, o, rec.Value);
                o += 4;
            }
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        /// <summary>Write a big-endian IEEE-754 float32 at <paramref name="off"/>.</summary>
        private static void WriteFloatBE(byte[] frame, int off, float value)
        {
            var b = BitConverter.GetBytes(value); // host order
            if (BitConverter.IsLittleEndian)
            {
                frame[off] = b[3]; frame[off + 1] = b[2]; frame[off + 2] = b[1]; frame[off + 3] = b[0];
            }
            else
            {
                frame[off] = b[0]; frame[off + 1] = b[1]; frame[off + 2] = b[2]; frame[off + 3] = b[3];
            }
        }

        /// <summary>Append the wire checksum to a literal 7E…payload prefix.</summary>
        private static byte[] BuildRaw(byte[] prefix)
        {
            var frame = new byte[prefix.Length + 1];
            Array.Copy(prefix, frame, prefix.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }
    }
}
