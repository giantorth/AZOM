using System;

namespace MozaPlugin.Protocol
{
    public struct ParsedResponse
    {
        public string Name;
        public int IntValue;
        public byte[] ArrayValue;
        public byte DeviceId;
        public int PayloadLength;
    }

    /// <summary>
    /// Parses response messages from Moza devices, matching them to known commands.
    /// Matches against both ReadGroup and WriteGroup (for write confirmations).
    /// Filters out firmware debug noise (group 0x0E from main device).
    /// </summary>
    public class MozaResponseParser
    {
        /// <summary>
        /// Returns null for unrecognized messages, false for filtered noise.
        /// </summary>
        public static ParsedResponse? Parse(byte[] data)
        {
            if (data == null || data.Length < 3)
                return null;

            byte responseGroup = data[0];
            byte responseDeviceId = data[1];
            var payload = new byte[data.Length - 2];
            Array.Copy(data, 2, payload, 0, payload.Length);

            byte group = MozaProtocol.ToggleBit7(responseGroup);
            byte deviceId = MozaProtocol.SwapNibbles(responseDeviceId);

            // Filter firmware debug output. Firmware sends debug frames with raw wire
            // group 0x0E (bit7 clear, so this is NOT a normal toggled response).
            // These are unsolicited status/log messages, not protocol responses.
            if (responseGroup == 0x0E)
                return null;

            // Filter SerialStream control frames (group 0xC3 = response to 0x43 +
            // payload starts with `7C 00` or `FC 00`). These are session-management
            // chunks (data / session-open-ack / fc:00 ack) handled by
            // TelemetrySender.OnMessageDuringPreamble, not command responses.
            // Logging them as "Unmatched" was noise.
            if (responseGroup == 0xC3 && payload.Length >= 2 &&
                (payload[0] == 0x7C || payload[0] == 0xFC) && payload[1] == 0x00)
                return null;

            // Wrapped Display sub-device identity responses arrive as 0xC3/0x71
            // with an inner response group byte. Unwrap and re-dispatch as if
            // it were a top-level response, but tag with "display-" prefix so
            // MozaData.UpdateFromArray can route them to Display fields without
            // overwriting the base wheel identity. Covers response groups:
            //   0x89/0x84/0x85/0x86/0x82/0x87/0x88/0x8F/0x90/0x91
            if (responseGroup == 0xC3 && payload.Length >= 1 &&
                IsDisplayIdentityResponseGroup(payload[0]))
            {
                return ParseDisplayIdentity(payload, responseDeviceId);
            }

            // Channel-enable read-back: wheel responds to `0x40/0x17 1E PP CC 00 00`
            // (BuildChannelEnableFrame) with `0xC0/0x71 1E PP CC HH LL`, where HHLL is
            // the stored value (BE u16) for that channel. Observed 0x0bb8 (3000),
            // 0x03e8 (1000), 0x01f4 (500). Parser exposes via "wheel-channel-enable-readback"
            // so logs/diagnostics can show what the wheel committed for each (page,channel).
            if (responseGroup == 0xC0 && responseDeviceId == 0x71
                && payload.Length >= 5 && payload[0] == 0x1E)
            {
                int storedBE = (payload[3] << 8) | payload[4];
                int packed = (payload[1] << 24) | (payload[2] << 16) | (payload[3] << 8) | payload[4];
                return new ParsedResponse
                {
                    Name = $"wheel-channel-enable-readback[p{payload[1]:X2}c{payload[2]:X2}]",
                    IntValue = storedBE,
                    ArrayValue = new byte[] { payload[1], payload[2], payload[3], payload[4] },
                    DeviceId = MozaProtocol.SwapNibbles(responseDeviceId),
                    PayloadLength = packed,
                };
            }

            // Device hint overrides based on group range
            string? deviceHint = null;
            if (group >= 63 && group <= 66)
                deviceHint = "wheel";
            if (group == 228 || group == 100)
            {
                deviceHint = "hub";
                group = 100;
            }

            // Device-ID–based hint: responses from the base/main device
            // (0x12) must not match "wheel"-typed commands. Without this,
            // identity-probe groups (2/4/5/6/9/17) that lack a group-range
            // hint let base responses collide with wheel commands — e.g.
            // the base STM32 UID overwrites the wheel UID depending on
            // arrival order, breaking auto-detect folder lookup.
            if (deviceHint == null && deviceId == MozaProtocol.DeviceMain)
                deviceHint = "base";

            // Group-indexed scan: skips ~99% of the command database for any
            // given inbound message. CommandId may contain 0xFF wildcards so we
            // still walk the per-group bucket linearly, but each bucket is at
            // most ~30 entries vs the full ~200+.
            var bucket = MozaCommandDatabase.CommandsForGroup(group);
            for (int idx = 0; idx < bucket.Count; idx++)
            {
                var cmd = bucket[idx];

                if (deviceHint != null && cmd.DeviceType != deviceHint)
                    continue;

                if (payload.Length < cmd.CommandId.Length)
                    continue;

                bool idMatch = true;
                for (int i = 0; i < cmd.CommandId.Length; i++)
                {
                    if (cmd.CommandId[i] != 0xFF && payload[i] != cmd.CommandId[i])
                    {
                        idMatch = false;
                        break;
                    }
                }

                if (!idMatch)
                    continue;

                var valueData = new byte[payload.Length - cmd.CommandId.Length];
                Array.Copy(payload, cmd.CommandId.Length, valueData, 0, valueData.Length);

                var result = new ParsedResponse { Name = cmd.Name, DeviceId = deviceId, PayloadLength = valueData.Length };

                if (cmd.PayloadType == "array")
                {
                    result.ArrayValue = valueData;
                    result.IntValue = MozaCommand.ParseIntValue(valueData, Math.Min(valueData.Length, 4));
                }
                else if (cmd.PayloadType == "float")
                {
                    result.IntValue = (int)MozaCommand.ParseFloatValue(valueData);
                }
                else
                {
                    result.IntValue = MozaCommand.ParseIntValue(valueData,
                        Math.Min(valueData.Length, cmd.PayloadBytes));
                }

                return result;
            }

            return null;
        }

        private static bool IsDisplayIdentityResponseGroup(byte g)
        {
            return g == 0x82 || g == 0x84 || g == 0x85 || g == 0x86 ||
                   g == 0x87 || g == 0x88 || g == 0x89 || g == 0x8F ||
                   g == 0x90 || g == 0x91;
        }

        private static ParsedResponse? ParseDisplayIdentity(byte[] payload, byte deviceId)
        {
            // payload[0] = response group (0x8X). Payload shape varies:
            //   0x89 00 01           — presence (2 bytes)
            //   0x82 02              — product type (1 byte)
            //   0x84 01 02 08 06     — device type (4 bytes, byte 2 = 0x08 for display)
            //   0x85 01 02 00 00     — capabilities
            //   0x86 <12B>           — MCU UID
            //   0x87 01 "<ASCII>"    — model name ("Display")
            //   0x88 01 "<ASCII>"    — HW version
            //   0x8F 01 "<ASCII>"    — FW version
            //   0x90 00 "<ASCII>"    — serial
            //   0x91 04 01           — identity-11
            byte g = payload[0];
            string name;
            byte[] value;
            switch (g)
            {
                case 0x89:
                    name = "display-presence";
                    value = payload.Length > 1 ? Slice(payload, 1) : new byte[0];
                    break;
                case 0x82:
                    name = "display-device-presence";
                    value = payload.Length > 1 ? Slice(payload, 1) : new byte[0];
                    break;
                case 0x84:
                    name = "display-device-type";
                    value = payload.Length > 1 ? Slice(payload, 1) : new byte[0];
                    break;
                case 0x85:
                    name = "display-capabilities";
                    value = payload.Length > 1 ? Slice(payload, 1) : new byte[0];
                    break;
                case 0x86:
                    name = "display-mcu-uid";
                    value = payload.Length > 1 ? Slice(payload, 1) : new byte[0];
                    break;
                case 0x87:
                    // Skip leading length/index byte (0x01). Payload after is ASCII model name.
                    name = "display-model-name";
                    value = payload.Length > 2 ? Slice(payload, 2) : new byte[0];
                    break;
                case 0x88:
                    name = "display-hw-version";
                    value = payload.Length > 2 ? Slice(payload, 2) : new byte[0];
                    break;
                case 0x8F:
                    name = "display-sw-version";
                    value = payload.Length > 2 ? Slice(payload, 2) : new byte[0];
                    break;
                case 0x90:
                    name = "display-serial";
                    value = payload.Length > 2 ? Slice(payload, 2) : new byte[0];
                    break;
                case 0x91:
                    name = "display-identity-11";
                    value = payload.Length > 1 ? Slice(payload, 1) : new byte[0];
                    break;
                default:
                    return null;
            }
            var r = new ParsedResponse
            {
                Name = name,
                DeviceId = deviceId,
                PayloadLength = value.Length,
                ArrayValue = value,
                IntValue = MozaCommand.ParseIntValue(value, Math.Min(value.Length, 4)),
            };
            return r;
        }

        private static byte[] Slice(byte[] src, int start)
        {
            int len = src.Length - start;
            if (len <= 0) return new byte[0];
            var dst = new byte[len];
            Array.Copy(src, start, dst, 0, len);
            return dst;
        }
    }
}
