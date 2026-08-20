using System;

namespace MozaPlugin.Telemetry.Sessions
{
    /// <summary>
    /// Session-0x01 host→wheel property push records (ff-tagged format):
    /// <c>ff &lt;size:u32 LE&gt; &lt;inner_crc32:u32 LE&gt; &lt;kind:u32 LE&gt; &lt;value LE&gt;</c>.
    /// See docs/protocol/findings/2026-04-29-session-01-property-push.md.
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
        /// Property `kind` for the VGS display-rotation mode (single-byte value:
        /// 0=off, 1=smooth, 2=immediate). The wheel senses its own rotation with
        /// an internal IMU and counter-rotates the dashboard to keep it upright;
        /// this push only selects <i>how</i> it counter-rotates. Reverse-engineered
        /// from VGS PitHouse captures — see
        /// docs/protocol/sessions/session-0x02-ff-init.md § Runtime property pushes.
        /// Unlike brightness (u32) / standby (u64) this carries a 1-byte value
        /// (wire size=5); build with <see cref="BuildU8Body"/>.
        /// </summary>
        public const uint KindDashDisplayRotation = 5;

        /// <summary>
        /// Property `kind` for the device display-log pull request (u32 = the
        /// maximum number of log lines wanted; PitHouse always asks for 100).
        /// The device answers with a record of the SAME kind carrying a zlib'd
        /// UTF-16BE line list — so kind=14 is bidirectional and the directions
        /// are told apart by value size (4 bytes = request, larger = payload).
        /// See docs/protocol/sessions/session-0x02-ff-init.md § Device log pull.
        /// </summary>
        public const uint KindDeviceLogRequest = 14;

        /// <summary>
        /// Property `kind` for the device display-log receipt (u32 = how many
        /// lines the host consumed). The device CLEARS that many lines from its
        /// own buffer, so this is what advances the read cursor.
        /// </summary>
        public const uint KindDeviceLogReceipt = 15;

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
        /// Build the net-data body for a single-byte-valued property (e.g. the
        /// VGS display-rotation mode). Wire size = kind(4) + value(1) = 5.
        /// </summary>
        public static byte[] BuildU8Body(uint kind, byte value)
        {
            // size = kind(4) + value(1) = 5
            var kv = new byte[5];
            WriteU32LE(kv, 0, kind);
            kv[4] = value;
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

        /// <summary>Dashboard-switch FF record. <paramref name="slotIndex"/> = 0-based configJsonList index.</summary>
        public static byte[] BuildDashboardSwitchBody(uint slotIndex)
        {
            var kv = new byte[12];
            WriteU32LE(kv, 0, DashSwitchField1);  // field1 = 4
            WriteU32LE(kv, 4, slotIndex);          // field2 = 0-based configJsonList index
            WriteU32LE(kv, 8, 0u);                 // field3 = 0
            return WrapFfRecord(kv);
        }

        /// <summary>
        /// Session-init record #1 (kind=2) on sess=0x02. Body:
        /// <c>[timestamp_u32_LE | 00 00 00 00 | 90 9d ff ff]</c>. Required so
        /// the wheel echoes dashboard-switch FF records.
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
        /// Session-init record #2 (kind=7, `init_enum`). Value is the CONSTANT
        /// <c>[3][0]</c> — not a dashboard slot index, which is what this used
        /// to emit.
        ///
        /// Verified 2026-08-07 against <c>bridge-20260731-064830.jsonl</c>: all
        /// 34 kind=7 records in the capture are byte-identical
        /// (<c>ff 0c 00 00 00 | 03 28 c2 81 | 07 00 00 00 | 03 00 00 00 00 00 00 00</c>)
        /// across several reconnects and a dashboard switch, so the field cannot
        /// be slot-dependent. Matches the decode already recorded in
        /// docs/protocol/findings/2026-05-04-init-sequence.md ("always val=3").
        /// Emitting 0 here left the wheel's FF-record layer dormant — it never
        /// answered with kind=9/10/16.
        /// </summary>
        public static byte[] BuildSessionInitField7Body()
        {
            var kv = new byte[12];
            WriteU32LE(kv, 0, 7u);
            WriteU32LE(kv, 4, InitEnumValue);
            WriteU32LE(kv, 8, 0u);
            return WrapFfRecord(kv);
        }

        /// <summary>Constant carried by the kind=7 init record.</summary>
        private const uint InitEnumValue = 3;

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
