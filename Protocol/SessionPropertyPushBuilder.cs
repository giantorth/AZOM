using System;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Builds session-0x01 host→wheel property push records (the `ff`-tagged
    /// format PitHouse uses for wheel-integrated dashboard runtime settings
    /// like display brightness and standby timeout).
    ///
    /// Net-data layout (caller wraps with the standard chunk container +
    /// outer chunk CRC32 via <see cref="MozaPlugin.Telemetry.Frames.TierDefinitionBuilder.ChunkMessage"/>):
    ///
    /// <code>
    /// ff &lt;size:u32 LE&gt; &lt;inner_crc32_LE:4&gt; &lt;kind:u32 LE&gt; &lt;value:size-4 bytes LE&gt;
    /// </code>
    ///
    /// where <c>size = 4 + sizeof(value)</c> and <c>inner_crc32 =
    /// zlib.crc32(kind ‖ value)</c>. See
    /// <c>docs/protocol/findings/2026-04-29-session-01-property-push.md</c>
    /// for the wire-format reverse-engineering and verified samples.
    /// </summary>
    public static class SessionPropertyPushBuilder
    {
        /// <summary>
        /// Property `kind` for dashboard display brightness (u32 0–100).
        /// </summary>
        public const uint KindDashBrightness = 1;

        /// <summary>
        /// Property `kind` for display standby timeout (u64 milliseconds).
        /// </summary>
        public const uint KindDashStandbyMs = 10;

        /// <summary>
        /// Field1 constant for the dashboard-switch FF-record. Verified
        /// in capture <c>automobilista-switch-dashboard-many-ends-on-grids-1.2.6.17.pcapng</c>
        /// and <c>wireshark/csp/startup, change knob colors, ...pcapng</c>.
        /// </summary>
        public const uint DashSwitchField1 = 4;

        /// <summary>
        /// Build the net-data body for a u32-valued property (e.g. brightness).
        /// </summary>
        public static byte[] BuildU32Body(uint kind, uint value)
        {
            // size = kind(4) + value(4) = 8
            var kv = new byte[8];
            WriteU32LE(kv, 0, kind);
            WriteU32LE(kv, 4, value);
            return WrapFfRecord(kv);
        }

        /// <summary>
        /// Build the net-data body for a u64-valued property (e.g. standby ms).
        /// </summary>
        public static byte[] BuildU64Body(uint kind, ulong value)
        {
            // size = kind(4) + value(8) = 12
            var kv = new byte[12];
            WriteU32LE(kv, 0, kind);
            WriteU64LE(kv, 4, value);
            return WrapFfRecord(kv);
        }

        /// <summary>
        /// Build the net-data body for a dashboard-switch command.
        /// <paramref name="slotIndex"/> is the <b>0-based</b> index into the
        /// wheel's <c>configJsonList</c> (alphabetical name list from session
        /// 0x09 state push). Verified 2026-04-30: slot=1 → configJsonList[1].
        /// See <c>docs/protocol/findings/2026-04-30-dashboard-switch-3f27.md</c>.
        /// </summary>
        public static byte[] BuildDashboardSwitchBody(uint slotIndex)
        {
            var kv = new byte[12];
            WriteU32LE(kv, 0, DashSwitchField1);  // field1 = 4
            WriteU32LE(kv, 4, slotIndex);          // field2 = 0-based configJsonList index
            WriteU32LE(kv, 8, 0u);                 // field3 = 0
            return WrapFfRecord(kv);
        }

        /// <summary>
        /// Session-init handshake record #1 sent by PitHouse on session 0x02
        /// at session start. Wheel requires this (and the kind=7 follow-up
        /// below) before it will echo dashboard-switch FF records and bind
        /// post-switch tier-defs to display elements; without it, FF-record
        /// switches sent on a fresh session 0x02 are silently ignored.
        ///
        /// Cross-capture comparison
        /// (<c>tools/bridge-decode-ff-init &lt;cap&gt; --until-switch -k 2</c>
        /// across bridge-2026-04-28..05-03 captures): the 12-byte payload
        /// after the 4-byte kind prefix is
        ///   <c>[timestamp_u32_LE | 00 00 00 00 | 90 9d ff ff]</c>
        /// where <i>timestamp_u32_LE</i> matches the Unix epoch seconds at
        /// the capture start. It varies per capture; the other 8 bytes are
        /// static. We emit the current Unix time so the record looks
        /// freshly-minted to the wheel rather than re-using a stale 2026-
        /// 04-30 timestamp from one specific capture.
        /// </summary>
        public static byte[] BuildSessionInitField2Body()
        {
            var kv = new byte[16];
            WriteU32LE(kv, 0, 2u);
            uint nowUnix = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WriteU32LE(kv, 4, nowUnix);
            // bytes 8-11 stay zero
            kv[12] = 0x90; kv[13] = 0x9d; kv[14] = 0xff; kv[15] = 0xff;
            return WrapFfRecord(kv);
        }

        /// <summary>
        /// Session-init handshake record #2: sets the initial active dashboard
        /// slot. Sent by PitHouse immediately after the field1=2 record on
        /// session 0x02. Format `ff 0c 00 00 00 [crc] 07 00 00 00 [slot:LE32]
        /// 00 00 00 00 [crc]`.
        /// </summary>
        public static byte[] BuildSessionInitField7Body(uint slotIndex)
        {
            var kv = new byte[12];
            WriteU32LE(kv, 0, 7u);
            WriteU32LE(kv, 4, slotIndex);
            WriteU32LE(kv, 8, 0u);
            return WrapFfRecord(kv);
        }

        internal static byte[] WrapFfRecord(byte[] kindAndValue)
        {
            int size = kindAndValue.Length;                 // 4 + sizeof(value)
            uint innerCrc = global::MozaPlugin.Telemetry.Frames.TierDefinitionBuilder
                .Crc32(kindAndValue, 0, size);

            var body = new byte[1 + 4 + 4 + size];
            body[0] = 0xFF;
            WriteU32LE(body, 1, (uint)size);
            WriteU32LE(body, 5, innerCrc);
            Array.Copy(kindAndValue, 0, body, 9, size);
            return body;
        }

        private static void WriteU32LE(byte[] buf, int offset, uint value)
        {
            buf[offset]     = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteU64LE(byte[] buf, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
                buf[offset + i] = (byte)((value >> (8 * i)) & 0xFF);
        }
    }
}
