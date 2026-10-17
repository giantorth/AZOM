using System;
using System.Text;

namespace MozaPlugin.Protocol
{
    /// <summary>Controller mode as written with <c>0x1E/03</c> and reported by <c>0x9F/03</c>.</summary>
    public enum MotionMode : byte
    {
        Idle = 0,
        Game = 1,
        ManualTest = 2,
        PositionCalibration = 3,
        Transient = 4,
        Armed = 5,
        TravelSelfCheck = 7,
        PortSelfTest = 8,
        Standby = 13,
    }

    /// <summary>
    /// Corner role stored per actuator port (<c>0x1E/02</c>). Rear-left / rear-right are
    /// consistent across captures; front-right / front-left are inferred
    /// (docs/protocol/motion/identity-and-ports.md).
    /// </summary>
    public enum MotionCorner : byte
    {
        None = 0,
        FrontRight = 1,
        FrontLeft = 3,
        RearLeft = 5,
        RearRight = 7,
    }

    /// <summary>Last <c>0x20/02</c> sub-command (stop / hold / run / identify-port).</summary>
    public enum MotionRunState : byte
    {
        Stop = 1,
        Hold = 2,
        Run = 3,
        IdentifyPort = 4,
    }

    /// <summary>
    /// One <c>0x22/00</c> telemetry frame: 12 big-endian floats + brake, throttle, a zero
    /// byte and the flag byte. Field meanings: docs/protocol/motion/game-stream-0x22.md.
    /// </summary>
    public sealed class MotionTelemetryFrame
    {
        public const int FieldCount = 12;

        /// <summary>Suspension deflection at rest (mm), f8..f11 = LF, LR, RR, RF (Motion Manager capture).</summary>
        public static readonly float[] ShockRestMm = { 34.7f, 38.4f, 38.7f, 33.1f };

        public readonly float[] Fields = new float[FieldCount];
        public byte BrakePercent;
        public byte ThrottlePercent;
        public byte Flags;

        /// <summary>Car on track, all inputs at rest, flag bit0 set.</summary>
        public static MotionTelemetryFrame Neutral(bool inGame)
        {
            var f = new MotionTelemetryFrame();
            for (int i = 0; i < 4; i++) f.Fields[8 + i] = ShockRestMm[i];
            f.Flags = inGame ? MozaMotionProtocol.FlagInGame : (byte)0;
            return f;
        }
    }

    /// <summary>
    /// Frame builders and parsers for the MOZA Motion Controller (HMA150 platform, USB PID
    /// <c>0x2000</c>). Standard MOZA framing; checksum and stuffing reuse
    /// <see cref="MozaProtocol"/>. Every request targets the controller, dev <c>0x12</c>;
    /// replies come from <c>0x21</c>, actuator port <i>p</i> is dev <c>0x13 + p</c>.
    ///
    /// Reference: docs/protocol/motion/.
    /// </summary>
    public static class MozaMotionProtocol
    {
        public const byte DeviceController = 0x12;
        public const byte ReplyController = 0x21;
        public const int PortCount = 8;
        public const int SupportedActuators = 4;

        public const byte GroupWrite = 0x1E;
        public const byte GroupRead = 0x1F;
        public const byte GroupRunState = 0x20;
        public const byte GroupStatus = 0x21;
        public const byte GroupStream = 0x22;
        public const byte GroupPresence = 0x06;

        public const byte ReplyRead = 0x9F;     // 0x1F | 0x80
        public const byte ReplyWrite = 0x9E;    // 0x1E | 0x80
        public const byte ReplyStatus = 0xA1;   // 0x21 | 0x80

        public const byte CmdPortRole = 0x02;
        public const byte CmdMode = 0x03;
        public const byte CmdGlobal = 0x04;
        public const byte CmdEffectSlot = 0x05;
        public const byte CmdRoadDetail = 0x06;
        public const byte CmdRigDimensions = 0x08;
        public const byte CmdDeviceType = 0x0A;
        public const byte CmdLighting = 0x0B;

        public const byte StatusController = 0x01;
        public const byte StatusPort = 0x02;
        public const byte StatusRunState = 0x03;

        public const byte StreamTelemetry = 0x00;

        public const byte GlobalMaxStroke = 0x00;
        public const byte GlobalHapticIntensity = 0x01;

        /// <summary>Effect slots MM writes on this platform.</summary>
        public const byte SlotPitch = 0x00, SlotRoll = 0x01, SlotHeave = 0x03, SlotAccelPitch = 0x07, SlotAccelRoll = 0x08;
        public static readonly byte[] EffectSlots = { SlotPitch, SlotRoll, SlotHeave, SlotAccelPitch, SlotAccelRoll };

        // Mode-poll / controller-status flag byte.
        public const byte FlagSelfCheckRunning = 0x80;
        public const byte FlagPortConfigPending = 0x20;
        public const byte FlagTopologyChanged = 0x04;

        // 0x22/00 flag byte.
        public const byte FlagInGame = 0x01;
        public const byte FlagGearShift = 0x02;

        // Firmware log / notifications on group 0x0E.
        public const byte LogLineSub = 0x05;
        public const byte NotificationSub = 0x03;
        public const byte NotificationAckSub = 0x04;

        public static byte PortDevice(int port) => (byte)(0x13 + port);

        /// <summary>Build an unstuffed frame <c>7E len group dev payload chk</c>.</summary>
        public static byte[] Build(byte group, byte device, params byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            var frame = new byte[payload.Length + 5];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)payload.Length;
            frame[2] = group;
            frame[3] = device;
            Array.Copy(payload, 0, frame, 4, payload.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        private static void PutF32(byte[] dst, int offset, float value)
        {
            uint bits = (uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            dst[offset] = (byte)(bits >> 24);
            dst[offset + 1] = (byte)(bits >> 16);
            dst[offset + 2] = (byte)(bits >> 8);
            dst[offset + 3] = (byte)bits;
        }

        public static float GetF32(byte[] src, int offset)
        {
            int bits = (src[offset] << 24) | (src[offset + 1] << 16) | (src[offset + 2] << 8) | src[offset + 3];
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        // ------------------------------------------------------------ mode / status

        /// <summary><c>7E 05 1E 12 03 [mode] 00 00 00</c>, e.g. self-check <c>7e051e120307000000ca</c>.</summary>
        public static byte[] BuildSetMode(MotionMode mode)
            => Build(GroupWrite, DeviceController, CmdMode, (byte)mode, 0, 0, 0);

        /// <summary><c>7e051f120300000000c4</c> → <c>9F 21 03 [target] [current] 00 [flags]</c>.</summary>
        public static byte[] BuildModePoll()
            => Build(GroupRead, DeviceController, CmdMode, 0, 0, 0, 0);

        /// <summary><c>7e01211201c0</c> → 13-byte controller status.</summary>
        public static byte[] BuildControllerStatus()
            => Build(GroupStatus, DeviceController, StatusController);

        /// <summary><c>7e0221120201c3</c> (port 1) → 13-byte port status.</summary>
        public static byte[] BuildPortStatus(int port)
            => Build(GroupStatus, DeviceController, StatusPort, (byte)port);

        /// <summary><c>7e01211203c2</c> → <c>A1 21 03 [sub]</c>.</summary>
        public static byte[] BuildRunStateRead()
            => Build(GroupStatus, DeviceController, StatusRunState);

        /// <summary><c>7e032012020100c3</c> (stop), <c>…0300c5</c> (run); acked with an empty <c>A0 21</c>.</summary>
        public static byte[] BuildRunState(MotionRunState sub, byte arg = 0)
            => Build(GroupRunState, DeviceController, 0x02, (byte)sub, arg);

        /// <summary>Identify-port: the actuator on <paramref name="port"/> signals.</summary>
        public static byte[] BuildIdentifyPort(int port)
            => BuildRunState(MotionRunState.IdentifyPort, (byte)port);

        /// <summary>Empty group-0x06 frame to a port id; populated ports answer with their UID.</summary>
        public static byte[] BuildPresence(byte device)
            => Build(GroupPresence, device);

        /// <summary>Unsolicited <c>0E 21 03 01 [code] 00 01</c> is acked with <c>0E 12 04 01 [code] 01</c> (<c>7e040e1204010401b9</c>).</summary>
        public static byte[] BuildNotificationAck(byte b1, byte code, byte b3)
            => Build(MozaProtocol.FirmwareDebugGroup, DeviceController, NotificationAckSub, b1, code, b3);

        // ------------------------------------------------------------ configuration

        /// <summary><c>7e031f12000000bf</c> → <c>9F 21 00 [supported] [ports]</c>.</summary>
        public static byte[] BuildCountsRead()
            => Build(GroupRead, DeviceController, 0x00, 0, 0);

        /// <summary><c>7e051f120201000000c4</c> → <c>9F 21 02 [port] 00 [role] 00</c>.</summary>
        public static byte[] BuildPortRoleRead(int port)
            => Build(GroupRead, DeviceController, CmdPortRole, (byte)port, 0, 0, 0);

        /// <summary><c>1E 12 02 [port] 01 [role] 00</c>, echoed on <c>9E 21</c>.</summary>
        public static byte[] BuildPortRoleWrite(int port, MotionCorner role)
            => Build(GroupWrite, DeviceController, CmdPortRole, (byte)port, 0x01, (byte)role, 0);

        /// <summary><c>1E 12 05 [slot] [f32 intensity] [f32 smoothness]</c>, both 0..1.</summary>
        public static byte[] BuildEffectSlot(byte slot, float intensity, float smoothness)
        {
            var p = new byte[10];
            p[0] = CmdEffectSlot;
            p[1] = slot;
            PutF32(p, 2, intensity);
            PutF32(p, 6, smoothness);
            return Build(GroupWrite, DeviceController, p);
        }

        /// <summary><c>1E 12 06 [f32 intensity] [f32 smoothness]</c>.</summary>
        public static byte[] BuildRoadDetail(float intensity, float smoothness)
        {
            var p = new byte[9];
            p[0] = CmdRoadDetail;
            PutF32(p, 1, intensity);
            PutF32(p, 5, smoothness);
            return Build(GroupWrite, DeviceController, p);
        }

        /// <summary>
        /// <c>1F 12 05 [slot] 00×8</c> → <c>9F 21 05 [slot] [f32][f32]</c>. Read replies are cut
        /// to the request's length, so the request carries both float slots.
        /// </summary>
        public static byte[] BuildEffectSlotRead(byte slot)
            => Build(GroupRead, DeviceController, CmdEffectSlot, slot, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary><c>7e091f12060000000000000000cb</c> → <c>9F 21 06 [f32][f32]</c>.</summary>
        public static byte[] BuildRoadDetailRead()
            => Build(GroupRead, DeviceController, CmdRoadDetail, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary><c>7e061f12040000000000c6</c> (idx 0) → <c>9F 21 04 [idx] [f32]</c>.</summary>
        public static byte[] BuildGlobalRead(byte index)
            => Build(GroupRead, DeviceController, CmdGlobal, index, 0, 0, 0, 0);

        /// <summary>
        /// <c>1E 12 04 [idx] [f32]</c>. Captured for idx 1 (haptic intensity); idx 0 (Max
        /// Available Stroke) is inferred from its read.
        /// </summary>
        public static byte[] BuildGlobalWrite(byte index, float value)
        {
            var p = new byte[6];
            p[0] = CmdGlobal;
            p[1] = index;
            PutF32(p, 2, value);
            return Build(GroupWrite, DeviceController, p);
        }

        /// <summary><c>7e091f12080000000000000000cd</c> → <c>9F 21 08 [f32 L1] [f32 L2]</c> (mm).</summary>
        public static byte[] BuildRigDimensionsRead()
            => Build(GroupRead, DeviceController, CmdRigDimensions, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary><c>1E 12 08 [f32 L1] [f32 L2]</c> — inferred from the read, never captured.</summary>
        public static byte[] BuildRigDimensionsWrite(float lengthMm, float widthMm)
        {
            var p = new byte[9];
            p[0] = CmdRigDimensions;
            PutF32(p, 1, lengthMm);
            PutF32(p, 5, widthMm);
            return Build(GroupWrite, DeviceController, p);
        }

        /// <summary><c>7e021e120a01c8</c>.</summary>
        public static byte[] BuildDeviceType()
            => Build(GroupWrite, DeviceController, CmdDeviceType, 0x01);

        /// <summary><c>1E 12 0B [zone] [param] [value…]</c>: zones 0–3 take one byte, zones 4–6 an RGB triple.</summary>
        public static byte[] BuildLightingWrite(byte zone, byte param, params byte[] value)
        {
            var p = new byte[3 + value.Length];
            p[0] = CmdLighting;
            p[1] = zone;
            p[2] = param;
            Array.Copy(value, 0, p, 3, value.Length);
            return Build(GroupWrite, DeviceController, p);
        }

        /// <summary>
        /// Lighting read as captured: zones 0–3 <c>1F 12 0B [zone] 00 00</c> (reply param 01),
        /// zones 4–6 <c>1F 12 0B [zone] [param] 00 00 00</c>.
        /// </summary>
        public static byte[] BuildLightingRead(byte zone, byte param)
            => zone < 4
                ? Build(GroupRead, DeviceController, CmdLighting, zone, 0, 0)
                : Build(GroupRead, DeviceController, CmdLighting, zone, param, 0, 0, 0);

        /// <summary>Controller identity probes MM sends at enrol, in capture order.</summary>
        public static byte[][] BuildIdentityProbes() => new[]
        {
            Build(0x06, DeviceController),
            Build(0x04, DeviceController, 0, 0, 0, 0),
            Build(0x05, DeviceController, 0, 0, 0, 0),
            Build(0x02, DeviceController, 0),
            Build(0x09, DeviceController),
            Build(0x07, DeviceController, 1),
            Build(0x08, DeviceController, 1),
            Build(0x0F, DeviceController, 1),
            Build(0x10, DeviceController, 0),
            Build(0x10, DeviceController, 1),
            Build(0x11, DeviceController, 0),
            Build(0x11, DeviceController, 1),
            Build(0x11, DeviceController, 4),
            Build(0x08, DeviceController, 2),
        };

        // ------------------------------------------------------------ game stream

        /// <summary>
        /// <c>7E 35 22 12 00 [12 × f32 BE] [brake] [throttle] 00 [flags]</c>, sent ~33 Hz in
        /// mode 1, never answered.
        /// </summary>
        public static byte[] BuildTelemetryFrame(MotionTelemetryFrame f)
        {
            var p = new byte[53];
            p[0] = StreamTelemetry;
            for (int i = 0; i < MotionTelemetryFrame.FieldCount; i++)
                PutF32(p, 1 + 4 * i, f.Fields[i]);
            p[49] = f.BrakePercent;
            p[50] = f.ThrottlePercent;
            p[51] = 0;
            p[52] = f.Flags;
            return Build(GroupStream, DeviceController, p);
        }

        // ------------------------------------------------------------ parsers
        // Inbound messages are [group, device, payload…] (checksum stripped).

        private static bool IsReply(byte[] d, byte group, byte cmd, int minPayload)
            => d != null && d.Length >= 2 + minPayload && d[0] == group && d[1] == ReplyController
               && (minPayload == 0 || d[2] == cmd);

        /// <summary><c>9F 21 03 [target] [current] 00 [flags]</c>.</summary>
        public static bool TryParseMode(byte[] d, out byte target, out byte current, out byte flags)
        {
            target = current = flags = 0;
            if (!IsReply(d, ReplyRead, CmdMode, 5)) return false;
            target = d[3];
            current = d[4];
            flags = d[6];
            return true;
        }

        /// <summary><c>A1 21 01 [b1] 00 [present] [enrolled] 00 [flags] …</c>.</summary>
        public static bool TryParseControllerStatus(byte[] d, out byte b1, out byte present, out byte enrolled, out byte flags)
        {
            b1 = present = enrolled = flags = 0;
            if (!IsReply(d, ReplyStatus, StatusController, 8)) return false;
            b1 = d[3];
            present = d[5];
            enrolled = d[6];
            flags = d[8];
            return true;
        }

        /// <summary><c>A1 21 02 [port] [present] 00 [slave] 00 00 00 00 [w0..w3]</c>.</summary>
        public static bool TryParsePortStatus(byte[] d, out int port, out bool present, out byte slave, out uint word)
        {
            port = 0; present = false; slave = 0; word = 0;
            if (!IsReply(d, ReplyStatus, StatusPort, 5)) return false;
            port = d[3];
            present = d[4] != 0;
            slave = d[6];
            if (d.Length >= 15)
                word = ((uint)d[11] << 24) | ((uint)d[12] << 16) | ((uint)d[13] << 8) | d[14];
            return true;
        }

        /// <summary><c>A1 21 03 [sub]</c>.</summary>
        public static bool TryParseRunState(byte[] d, out byte sub)
        {
            sub = 0;
            if (!IsReply(d, ReplyStatus, StatusRunState, 2)) return false;
            sub = d[3];
            return true;
        }

        /// <summary><c>9F 21 00 [supported] [ports]</c>.</summary>
        public static bool TryParseCounts(byte[] d, out byte supported, out byte ports)
        {
            supported = ports = 0;
            if (!IsReply(d, ReplyRead, 0x00, 3)) return false;
            supported = d[3];
            ports = d[4];
            return true;
        }

        /// <summary><c>9F 21 02 [port] 00 [role] 00</c>, or the <c>9E 21</c> echo of a write (<c>[port] 01 [role] 00</c>).</summary>
        public static bool TryParsePortRole(byte[] d, out int port, out byte role)
        {
            port = 0; role = 0;
            if (d == null || d.Length < 6 || d[1] != ReplyController || d[2] != CmdPortRole) return false;
            if (d[0] != ReplyRead && d[0] != ReplyWrite) return false;
            port = d[3];
            role = d[5];
            return true;
        }

        /// <summary><c>9F 21 04 [idx] [f32]</c> (also the <c>9E</c> echo of a write).</summary>
        public static bool TryParseGlobal(byte[] d, out byte index, out float value)
        {
            index = 0; value = 0;
            if (d == null || d.Length < 8 || d[1] != ReplyController || d[2] != CmdGlobal) return false;
            if (d[0] != ReplyRead && d[0] != ReplyWrite) return false;
            index = d[3];
            value = GetF32(d, 4);
            return true;
        }

        /// <summary><c>9F 21 08 [f32 L1] [f32 L2]</c>.</summary>
        public static bool TryParseRigDimensions(byte[] d, out float lengthMm, out float widthMm)
        {
            lengthMm = widthMm = 0;
            if (d == null || d.Length < 11 || d[1] != ReplyController || d[2] != CmdRigDimensions) return false;
            if (d[0] != ReplyRead && d[0] != ReplyWrite) return false;
            lengthMm = GetF32(d, 3);
            widthMm = GetF32(d, 7);
            return true;
        }

        /// <summary><c>9F 21 05 [slot] [f32][f32]</c> / <c>9E</c> echo.</summary>
        public static bool TryParseEffectSlot(byte[] d, out byte slot, out float intensity, out float smoothness)
        {
            slot = 0; intensity = smoothness = 0;
            if (d == null || d.Length < 12 || d[1] != ReplyController || d[2] != CmdEffectSlot) return false;
            if (d[0] != ReplyRead && d[0] != ReplyWrite) return false;
            slot = d[3];
            intensity = GetF32(d, 4);
            smoothness = GetF32(d, 8);
            return true;
        }

        /// <summary><c>9F 21 06 [f32][f32]</c> / <c>9E</c> echo.</summary>
        public static bool TryParseRoadDetail(byte[] d, out float intensity, out float smoothness)
        {
            intensity = smoothness = 0;
            if (d == null || d.Length < 11 || d[1] != ReplyController || d[2] != CmdRoadDetail) return false;
            if (d[0] != ReplyRead && d[0] != ReplyWrite) return false;
            intensity = GetF32(d, 3);
            smoothness = GetF32(d, 7);
            return true;
        }

        /// <summary><c>9F 21 0B [zone] [param] [value…]</c> / <c>9E</c> echo.</summary>
        public static bool TryParseLighting(byte[] d, out byte zone, out byte param, out byte[] value)
        {
            zone = param = 0; value = Array.Empty<byte>();
            if (d == null || d.Length < 6 || d[1] != ReplyController || d[2] != CmdLighting) return false;
            if (d[0] != ReplyRead && d[0] != ReplyWrite) return false;
            zone = d[3];
            param = d[4];
            value = new byte[d.Length - 5];
            Array.Copy(d, 5, value, 0, value.Length);
            return true;
        }

        /// <summary>Unsolicited <c>0E [src] 03 [b1] [code] 00 [b3]</c>.</summary>
        public static bool TryParseNotification(byte[] d, out byte b1, out byte code, out byte b3)
        {
            b1 = code = b3 = 0;
            if (d == null || d.Length < 7 || d[0] != MozaProtocol.FirmwareDebugGroup || d[2] != NotificationSub) return false;
            b1 = d[3];
            code = d[4];
            b3 = d[6];
            return true;
        }

        /// <summary>Firmware log fragment <c>0E [src] 05 [ascii…]</c>; lines end with <c>\n</c> and may span frames.</summary>
        public static bool TryParseLogFragment(byte[] d, out byte source, out string text)
        {
            source = 0; text = string.Empty;
            if (d == null || d.Length < 3 || d[0] != MozaProtocol.FirmwareDebugGroup || d[2] != LogLineSub) return false;
            source = d[1];
            text = Encoding.ASCII.GetString(d, 3, d.Length - 3);
            return true;
        }

        /// <summary>Identity reply string (groups 0x07/0x08/0x0F/0x10/0x11): ASCII after the sub byte, zero-trimmed.</summary>
        public static string IdentityString(byte[] d)
        {
            if (d == null || d.Length < 4) return string.Empty;
            return Encoding.ASCII.GetString(d, 3, d.Length - 3).TrimEnd('\0', ' ');
        }
    }
}
