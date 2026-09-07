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
        /// Parse a response (null = unrecognized). <paramref name="busHint"/> overrides
        /// the auto-derived device hint to resolve dev 0x12 collisions:
        ///   "ab9"      — AB9 / AB6 active shifter on its own PID 0x1000 / 0x1002 USB pipe
        ///   "mbooster" — Moza mBooster Pedals on its own PID 0x0008 USB pipe
        /// Wheelbase main, AB9 main and mBooster main all use device id 0x12;
        /// the bus hint is the only way to tell them apart at the parser layer.
        /// </summary>
        public static ParsedResponse? Parse(byte[] data, string? busHint = null)
        {
            if (data == null || data.Length < 3)
                return null;

            byte responseGroup = data[0];
            byte responseDeviceId = data[1];

            // Rejection filters run BEFORE the payload copy — these are the
            // highest-rate inbound classes on unfiltered pipes.
            // Firmware debug output: raw wire group 0x0E (bit7 clear, so this is
            // NOT a normal toggled response) — unsolicited status/log messages.
            if (responseGroup == 0x0E)
                return null;

            // SerialStream control frames (0xC3 + 7C/FC + 00) — session-mgmt
            // chunks handled by TelemetrySender, not command responses.
            if (responseGroup == 0xC3 && data.Length >= 4 &&
                (data[2] == 0x7C || data[2] == 0xFC) && data[3] == 0x00)
                return null;

            // The payload is data[2..]. Indexed in place (this runs per inbound frame
            // on the dispatch thread) — only the rare display-identity path and the
            // "array" result materialise a copy.
            const int PayloadStart = 2;
            int payloadLength = data.Length - PayloadStart;

            byte group = MozaProtocol.ToggleBit7(responseGroup);
            byte deviceId = MozaProtocol.SwapNibbles(responseDeviceId);

            // Wrapped Display sub-device identity (0xC3/0x71 + inner response group).
            // Unwrap + tag with "display-" prefix so it doesn't overwrite wheel identity.
            if (responseGroup == 0xC3 && payloadLength >= 1 &&
                IsDisplayIdentityResponseGroup(data[PayloadStart]))
            {
                var payload = new byte[payloadLength];
                Array.Copy(data, PayloadStart, payload, 0, payloadLength);
                return ParseDisplayIdentity(payload, responseDeviceId);
            }

            // Channel-enable read-back: wheel responds to `0x40/0x17 1E PP CC 00 00`
            // (BuildChannelEnableFrame) with `0xC0/0x71 1E PP CC HH LL`, where HHLL is
            // the stored value (BE u16) for that channel. Observed 0x0bb8 (3000),
            // 0x03e8 (1000), 0x01f4 (500). Parser exposes via "wheel-channel-enable-readback"
            // so logs/diagnostics can show what the wheel committed for each (page,channel).
            if (responseGroup == 0xC0 && responseDeviceId == 0x71
                && payloadLength >= 5 && data[PayloadStart] == 0x1E)
            {
                byte p1 = data[PayloadStart + 1], p2 = data[PayloadStart + 2];
                byte p3 = data[PayloadStart + 3], p4 = data[PayloadStart + 4];
                int storedBE = (p3 << 8) | p4;
                int packed = (p1 << 24) | (p2 << 16) | (p3 << 8) | p4;
                return new ParsedResponse
                {
                    Name = $"wheel-channel-enable-readback[p{p1:X2}c{p2:X2}]",
                    IntValue = storedBE,
                    ArrayValue = new byte[] { p1, p2, p3, p4 },
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

            // dev 0x12 → "main" so main-* and base-ambient-* match. Blocks the
            // wheel-collision on identity probes (groups 2/4/5/6/9/17). ab9 stays
            // isolated via busHint, hub via the group hint above.
            if (deviceHint == null && deviceId == MozaProtocol.DeviceMain)
                deviceHint = "main";

            // dev 0x13 → "base" so base-* identity probes (groups 6/7/8/15/17)
            // resolve against the base-* command bucket rather than the
            // wheel-* bucket that owns the same response groups. Required for
            // the DeviceCatalog Motor/Wheel-Base manifest entries to populate.
            if (deviceHint == null && deviceId == MozaProtocol.DeviceBase)
                deviceHint = "base";

            // dev 0x18 → "es-wheel" so the ES steering wheel's identity probes
            // (groups 4/6/7/8/15) resolve against the es-wheel-* bucket. The ES
            // wheel is a module of the wheelbase MCU with its own internal id;
            // 0x13 returns the motor name, so without this hint a 0x18 model-name
            // reply would collide with base-*/wheel-* in the shared groups.
            if (deviceHint == null && deviceId == MozaProtocol.DeviceEsWheel)
                deviceHint = "es-wheel";

            // dev 0x15 / 0x17 (DeviceWheel) → "wheel" so the wheel's identity
            // probes (groups 2/4/5/6/7/8/9/15/17) resolve deterministically
            // against the wheel-* bucket instead of by command-registration order.
            // docs/how-to-query-device-type.md treats 0x15/0x17/0x18 as the three
            // wheel-identity device ids; 0x18 maps to es-wheel just above.
            if (deviceHint == null
                && (deviceId == MozaProtocol.DeviceWheel || deviceId == MozaProtocol.DeviceWheel15))
                deviceHint = "wheel";

            // dev 0x1A → "shifter" so a base/hub-relayed shifter's identity probes
            // (esp. the group-0x04 device-type reply, which shares its response group
            // with wheel-*/es-wheel-*) resolve against the shifter-* bucket instead of
            // matching wheel-device-type first. 0x1A is the shifter's exclusive bus id,
            // so this never steals another device's reply. On its own USB pipe the
            // shifter answers as 0x12 and the lane passes an explicit "shifter" busHint.
            if (deviceHint == null && deviceId == MozaProtocol.DeviceHPattern)
                deviceHint = "shifter";

            // dev 0x19 / 0x1B → "pedals" / "handbrake", same reasoning as 0x1A above.
            // Relayed pedals/handbrake answer the shared identity groups
            // (0x04 device-type, 0x07 model-name, 0x09 presence, 0x10 serial) and
            // without a hint those replies fell through to the FIRST entry in the
            // group bucket — the wheel-* command. A relayed pedal set answering
            // `87 91 01 "SRP"` was therefore parsed as wheel-model-name, which wrote
            // the pedals' name into MozaData.WheelModelName and made DeviceProber
            // declare a wheel hot-swap (model 'KS' → 'SRP'). That reset wheel
            // detection AND cleared PendingResponseTracker mid-detection, dropping
            // the base identity reads still in flight — including base-fw-version,
            // which gates the wheelbase LFE effects and is never re-issued because
            // BaseAmbientProbed has already latched. Bundle 65HZBQJT (R12 + KS + SRP).
            if (deviceHint == null && deviceId == MozaProtocol.DevicePedals)
                deviceHint = "pedals";
            if (deviceHint == null && deviceId == MozaProtocol.DeviceHandbrake)
                deviceHint = "handbrake";

            // Explicit bus override (AB9 connection passes "ab9" to dodge dev 0x12 collision).
            if (busHint != null)
                deviceHint = busHint;

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

                if (payloadLength < cmd.CommandId.Length)
                    continue;

                bool idMatch = true;
                for (int i = 0; i < cmd.CommandId.Length; i++)
                {
                    if (cmd.CommandId[i] != 0xFF && data[PayloadStart + i] != cmd.CommandId[i])
                    {
                        idMatch = false;
                        break;
                    }
                }

                if (!idMatch)
                    continue;

                int valueStart = PayloadStart + cmd.CommandId.Length;
                int valueLength = payloadLength - cmd.CommandId.Length;

                var result = new ParsedResponse { Name = cmd.Name, DeviceId = deviceId, PayloadLength = valueLength };

                if (cmd.PayloadType == "array")
                {
                    var valueData = new byte[valueLength];
                    Array.Copy(data, valueStart, valueData, 0, valueLength);
                    result.ArrayValue = valueData;
                    result.IntValue = MozaCommand.ParseIntValue(valueData, Math.Min(valueLength, 4));
                }
                else if (cmd.PayloadType == "float")
                {
                    result.IntValue = (int)MozaCommand.ParseFloatValue(data, valueStart);
                }
                else
                {
                    result.IntValue = MozaCommand.ParseIntValue(data, valueStart,
                        Math.Min(valueLength, cmd.PayloadBytes));
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
