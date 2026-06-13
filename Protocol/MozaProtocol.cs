namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Constants from the MOZA Racing serial protocol (docs/protocol/, device tables in docs/protocol/devices/).
    /// </summary>
    public static class MozaProtocol
    {
        public const byte MessageStart = 0x7E;
        public const byte MagicValue = 0x0D; // 13 decimal, used for checksum
        public const int BaudRate = 115200;
        public const int VendorId = 0x346E; // Gudsen/Moza

        // Device IDs
        // Note: Main and Hub share ID 18 (same physical controller).
        // HPattern and Sequential share ID 26 (same device type).
        // The response parser disambiguates via group range checks.
        public const byte DeviceMain = 18;
        public const byte DeviceBase = 19;
        public const byte DeviceDash = 20;
        public const byte DeviceWheel = 23;
        // ES (old-protocol) steering wheel: a module of the wheelbase MCU,
        // reachable at its own internal id 0x18 (24). Distinct from DeviceWheel
        // (0x17), which is silent on ES hardware. Shares the base MCU UID +
        // sw-version; identified by model-name (group 0x07 → "ES") and hw-version
        // (0x08 → "…SM-C"). See docs/protocol/identity/known-wheel-models.md.
        public const byte DeviceEsWheel = 24; // 0x18
        public const byte DevicePedals = 25;
        public const byte DeviceHPattern = 26;
        public const byte DeviceSequential = 26;
        public const byte DeviceHandbrake = 27;
        public const byte DeviceEStop = 28;
        public const byte DeviceHub = 18;
        // AB9 active shifter exposes a single internal device at id 0x12 on its own
        // VID_346E PID_1000 composite USB device — same numeric value as DeviceMain
        // but reached via the AB9's dedicated CDC pipe, not the wheelbase's.
        public const byte DeviceAb9 = 18;

        // Read request groups
        public const byte BaseReadSettings = 40;   // FFB, angle, etc.
        public const byte BaseReadTelemetry = 43;   // Temps, state
        public const byte PedalsReadSettings = 35;
        public const byte PedalsReadOutput = 37;    // Throttle/brake/clutch output
        public const byte WheelRead = 64;
        public const byte DashRead = 51;
        public const byte HubRead = 100;
        public const byte HandbrakeRead = 91;

        // Write request groups
        public const byte BaseWriteSettings = 41;
        public const byte BaseWriteCalibration = 42;
        public const byte BaseSendTelemetry = 65;
        public const byte PedalsWriteSettings = 36;
        public const byte WheelWrite = 63;
        public const byte DashWrite = 50;
        public const byte HandbrakeWrite = 92;

        // Dashboard telemetry (pithouse-re.md § 4)
        public const byte TelemetrySendGroup = 0x43;  // Group for telemetry data frames
        public const byte TelemetryModeGroup = 0x40;  // Group for telemetry mode config (28:02)

        // Response groups — request group with bit 7 toggled per MozaResponseParser.
        public const byte BaseRespGroup = 0xAB;             // BaseReadSettings (0x2B) bit7-toggled
        public const byte HubRespGroup  = 0xE4;             // HubRead (0x64) bit7-toggled
        public const byte Ab9RespGroup  = 0x89;             // AB9 probe group (0x09) bit7-toggled
        public const byte SerialStreamRespGroup    = 0xC3;  // TelemetrySendGroup (0x43) bit7-toggled
        public const byte WheelChannelCfgRespGroup = 0xC0;  // TelemetryModeGroup (0x40) bit7-toggled

        // Wheel device id 0x17 (DeviceWheel = 23) with nibbles swapped, as it
        // appears in response frames at data[1].
        public const byte WheelDeviceIdSwapped = 0x71;

        // Boot/firmware debug noise on data[0] — silenced everywhere.
        public const byte FirmwareDebugGroup = 0x0E;

        // SerialStream chunk header opcodes at payload[2] under SerialStreamRespGroup.
        public const byte SerialStreamOpcodeData = 0x7C;  // chunk data / dashboard-activate
        public const byte SerialStreamOpcodeCtrl = 0xFC;  // session open / ack

        // Channel-config burst opcodes at payload[2] under WheelChannelCfgRespGroup:
        //   1E 00/01 — channel CC enable read per page
        //   28 00/01/02 — WheelGetCfg_GetMultiFunction{Switch,Num,Left}
        public const byte WheelCfgOpcodeChannelEnable = 0x1E;
        public const byte WheelCfgOpcodeMultiFunction = 0x28;

        /// <summary>
        /// Wire-level checksum: each 0x7E in body positions 2.. counts twice
        /// (byte-stuffing doubles it on the wire). See docs § 54.
        /// </summary>
        public static byte CalculateWireChecksum(byte[] data)
            => CalculateWireChecksum(data, data.Length);

        /// <summary>Wire-level checksum over the first <paramref name="length"/> bytes.</summary>
        public static byte CalculateWireChecksum(byte[] data, int length)
        {
            int sum = MagicValue;
            for (int i = 0; i < length; i++)
                sum += data[i];
            for (int i = 2; i < length; i++)
                if (data[i] == MessageStart)
                    sum += MessageStart;
            return (byte)(sum & 0xFF);
        }

        /// <summary>
        /// Allocation-free overload for the read path. Computes the expected
        /// checksum of the conceptual frame
        /// <c>[MessageStart, length, body[0..bodyLength-1], 0]</c> — i.e. the
        /// same answer as
        /// <see cref="CalculateWireChecksum(byte[], int)"/> applied to a synthesised
        /// frame, without requiring the synthesised array to exist. ReadLoop
        /// uses this to avoid a per-Rx <c>byte[payloadLength+4]</c> allocation
        /// at telemetry rates (250–1000 frames/sec).
        /// </summary>
        public static byte CalculateWireChecksumFromParts(byte length, byte[] body, int bodyLength)
        {
            // Conceptual frame contributes: MessageStart + length + body[0..bodyLength-1]
            // plus a trailing zero placeholder (the checksum slot itself). The
            // trailing zero adds nothing to either accumulator, so we drop it.
            int sum = MagicValue + MessageStart + length;
            for (int i = 0; i < bodyLength; i++)
            {
                byte b = body[i];
                sum += b;
                // The escape-double pass in the array overload covers indices ≥ 2,
                // i.e. everything in `body`. Index 0 (start) and index 1 (length)
                // never participate in the doubling. length is constrained ≤ 64
                // so it can never collide with MessageStart anyway.
                if (b == MessageStart)
                    sum += MessageStart;
            }
            return (byte)(sum & 0xFF);
        }

        public static byte SwapNibbles(byte b)
        {
            return (byte)(((b & 0x0F) << 4) | ((b & 0xF0) >> 4));
        }

        /// <summary>Stuffed wire-size: header (0..1) unchanged; every 0x7E from idx 2 doubles.</summary>
        public static int StuffedFrameSize(byte[] frame)
        {
            int escapes = 0;
            for (int i = 2; i < frame.Length; i++)
                if (frame[i] == MessageStart) escapes++;
            return frame.Length + escapes;
        }

        /// <summary>Byte-stuff <paramref name="frame"/> into <paramref name="dest"/>; returns bytes written.</summary>
        public static int StuffFrame(byte[] frame, byte[] dest)
        {
            if (frame.Length < 2) return 0;
            dest[0] = frame[0];
            dest[1] = frame[1];
            int di = 2;
            for (int i = 2; i < frame.Length; i++)
            {
                dest[di++] = frame[i];
                if (frame[i] == MessageStart)
                    dest[di++] = MessageStart;
            }
            return di;
        }

        public static byte ToggleBit7(byte b)
        {
            return (byte)(b ^ 0x80);
        }

        /// <summary>
        /// Wheel-echoed write prefixes (group|0x80, dev nibble-swapped, payload mirrored).
        /// Used to swallow echo responses + treat as keepalive. Mirrors sim/wheel_sim.py.
        /// </summary>
        public static readonly byte[][] WheelEchoPrefixes = new[]
        {
            new byte[] { 0x3F, 0x17, 0x1f, 0x00 }, // per-LED color page 0
            new byte[] { 0x3F, 0x17, 0x1f, 0x01 }, // per-LED color page 1
            new byte[] { 0x3F, 0x17, 0x1e, 0x00 }, // channel CC enable page 0
            new byte[] { 0x3F, 0x17, 0x1e, 0x01 }, // channel CC enable page 1
            new byte[] { 0x3F, 0x17, 0x1b, 0x00 }, // brightness page 0
            new byte[] { 0x3F, 0x17, 0x1b, 0x01 }, // brightness page 1
            new byte[] { 0x3F, 0x17, 0x1c, 0x00 }, // page config
            new byte[] { 0x3F, 0x17, 0x1d, 0x00 },
            new byte[] { 0x3F, 0x17, 0x1d, 0x01 },
            new byte[] { 0x3F, 0x17, 0x27, 0x00 }, // knob 1 bg/primary
            new byte[] { 0x3F, 0x17, 0x27, 0x01 }, // knob 2 bg/primary
            new byte[] { 0x3F, 0x17, 0x27, 0x02 }, // knob 3 bg/primary
            new byte[] { 0x3F, 0x17, 0x27, 0x03 }, // knob 4 bg/primary (CS Pro / KS Pro)
            new byte[] { 0x3F, 0x17, 0x27, 0x04 }, // knob 5 bg/primary (KS Pro)
            new byte[] { 0x3F, 0x17, 0x2a, 0x00 },
            new byte[] { 0x3F, 0x17, 0x2a, 0x01 },
            new byte[] { 0x3F, 0x17, 0x2a, 0x02 },
            new byte[] { 0x3F, 0x17, 0x2a, 0x03 },
            new byte[] { 0x3F, 0x17, 0x0a, 0x00 },
            new byte[] { 0x3F, 0x17, 0x24, 0xff }, // display setting
            new byte[] { 0x3F, 0x17, 0x20, 0x01 },
            new byte[] { 0x3F, 0x17, 0x1a, 0x00 }, // RPM LED telemetry write
            new byte[] { 0x3F, 0x17, 0x19, 0x00 }, // RPM LED color write
            new byte[] { 0x3F, 0x17, 0x19, 0x01 }, // button LED color write
            new byte[] { 0x3F, 0x17, 0x1a, 0x03 }, // knob bitmask telemetry write
            new byte[] { 0x3F, 0x17, 0x19, 0x03 }, // knob LED color write
            new byte[] { 0x3E, 0x17, 0x0b },       // newer-wheel LED cmd (1-byte prefix)
        };

        /// <summary>
        /// Returns true when <paramref name="data"/> is a wheel echo of a known write
        /// command. <paramref name="data"/> layout: [responseGroup, responseDeviceId, payload...].
        /// responseGroup is bit7-toggled (0xBF ↔ 0x3F, 0xBE ↔ 0x3E); responseDeviceId is
        /// nibble-swapped (0x71 ↔ 0x17). Match is against the normalized group/device.
        /// </summary>
        public static bool IsWheelEcho(byte[] data)
        {
            if (data == null || data.Length < 4) return false;
            byte group = ToggleBit7(data[0]);
            byte device = SwapNibbles(data[1]);
            foreach (var prefix in WheelEchoPrefixes)
            {
                if (prefix[0] != group || prefix[1] != device) continue;
                int prefixLen = prefix.Length - 2;
                if (data.Length < 2 + prefixLen) continue;
                bool match = true;
                for (int i = 0; i < prefixLen; i++)
                {
                    if (data[2 + i] != prefix[2 + i]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }
    }
}
