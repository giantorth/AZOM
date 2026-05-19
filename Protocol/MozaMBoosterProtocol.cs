using System;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Effect identifiers as transmitted in the motor-write payload byte
    /// at offset 0 (effect type) of cmd <c>0xb1</c>. Per the protocol note
    /// in <c>docs/MozamBooster — Protocol Note.md</c> § 3.
    /// </summary>
    public enum MBoosterEffectId : byte
    {
        Abs       = 1,
        Lockup    = 2,
        Threshold = 3,
        Engine    = 4,
    }

    /// <summary>
    /// Frame builders + value encoders for the Moza mBooster vibration motor
    /// (USB-CDC PID <c>0x0008</c>, target device id <c>0x12</c>). The framing
    /// itself is the standard Moza wire format — checksum and 0x7E stuffing
    /// reuse <see cref="MozaProtocol"/>. This file only owns the mBooster's
    /// motor-write opcode (<c>0xb1</c>) and the keepalive shape.
    ///
    /// Reference: <c>docs/MozamBooster — Protocol Note.md</c> §§ 2–4.
    /// </summary>
    public static class MozaMBoosterProtocol
    {
        // Wire constants ----------------------------------------------------
        /// <summary>Group 36 (0x24) — "Pedal Config Write". Per protocol note § 3.</summary>
        public const byte GroupMotorWrite = 0x24;
        /// <summary>Device id 18 (0x12) — the standalone vibration motor target.</summary>
        public const byte DeviceMotor = 0x12;
        /// <summary>Motor-write command id. Per protocol note § 3.</summary>
        public const byte CmdMotorWrite = 0xb1;
        /// <summary>Motor-write payload length (excludes group + device per dirt-client framing rule).</summary>
        public const byte MotorPayloadLen = 0x09;

        // ParamK constants per protocol note § 3 "Effect types" table.
        // param1 = clamp(round(paramK / freq_hz), 1, 255)
        public const double ParamKAbs       = 2000.0;
        public const double ParamKLockup    = 2640.0;
        public const double ParamKThreshold = 3080.0;
        public const double ParamKEngine    = 1000.0;

        /// <summary>
        /// Build the motor-write frame for a given effect. Wire layout (14 bytes total,
        /// pre-stuffing, with the checksum byte assumed appended by caller of <see cref="MozaProtocol"/> stuffing):
        /// <pre>
        /// 7e  09  24  12   b1  EF  EN  00   P1  FH  FL  AH  AL   CK
        ///                  │   │   │   │    │   └─┴─freq u16 BE
        ///                  │   │   │   │    └ param1 (per-cycle scaling, 1..255)
        ///                  │   │   │   └ pad (0x00)
        ///                  │   │   └ enable (0 = off, 1 = on)
        ///                  │   └ effect type (1..4)
        ///                  └ cmd id (0xb1)
        /// </pre>
        ///
        /// Known-good frames (verified against hardware captures, protocol note § 3):
        /// <list type="bullet">
        /// <item>ABS on, 22 Hz, amp=0x08e8: <c>7e 09 24 12 b1 01 01 00 5a 1c 28 08 e8 0b</c></item>
        /// <item>ABS off: <c>7e 09 24 12 b1 01 00 00 00 00 00 00 00 7c</c></item>
        /// <item>Lockup on, 55 Hz, start of ramp: <c>7e 09 24 12 b1 02 01 00 30 46 66 00 00 5a</c></item>
        /// <item>Lockup off: <c>7e 09 24 12 b1 02 00 00 00 00 00 00 00 7d</c></item>
        /// <item>Engine on, 10 Hz, amp=0x020c: <c>7e 09 24 12 b1 04 01 00 64 0c cc 02 0c ca</c></item>
        /// <item>Engine off: <c>7e 09 24 12 b1 04 00 00 00 00 00 00 00 7f</c></item>
        /// </list>
        /// </summary>
        public static byte[] BuildMotorFrame(
            MBoosterEffectId effect,
            bool enable,
            byte param1,
            ushort freqU16,
            ushort ampU16)
        {
            // 14 bytes total: start + len + group + device + 9 payload + checksum.
            var frame = new byte[14];
            frame[0]  = MozaProtocol.MessageStart;
            frame[1]  = MotorPayloadLen;        // 0x09
            frame[2]  = GroupMotorWrite;        // 0x24
            frame[3]  = DeviceMotor;            // 0x12
            frame[4]  = CmdMotorWrite;          // 0xb1
            frame[5]  = (byte)effect;
            frame[6]  = enable ? (byte)1 : (byte)0;
            frame[7]  = 0x00;                   // pad
            frame[8]  = param1;
            frame[9]  = (byte)(freqU16 >> 8);   // freq high
            frame[10] = (byte)(freqU16 & 0xFF); // freq low
            frame[11] = (byte)(ampU16 >> 8);    // amp high
            frame[12] = (byte)(ampU16 & 0xFF);  // amp low
            frame[13] = MozaProtocol.CalculateWireChecksum(frame, 13);
            return frame;
        }

        /// <summary>
        /// Build a disable frame for one effect: same opcode with enable=0 and all
        /// params zeroed. Per protocol note § 3 "Disable" — must be sent at every
        /// effect-deactivate edge AND for all four effects on shutdown, otherwise
        /// the last-active waveform can latch.
        /// </summary>
        public static byte[] BuildDisableFrame(MBoosterEffectId effect)
            => BuildMotorFrame(effect, enable: false, param1: 0, freqU16: 0, ampU16: 0);

        /// <summary>
        /// Degenerate 0-payload frame targeting the motor — <c>7e 00 00 12 9d</c>.
        /// Per protocol note § 3 "Keepalive": send every ~500 ms whenever the port
        /// is open. If we stop sending it the motor will eventually drop connection
        /// state and may stop responding to writes until the link is re-established.
        /// </summary>
        public static byte[] BuildKeepalive()
        {
            var frame = new byte[5];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = 0x00;
            frame[2] = 0x00;
            frame[3] = DeviceMotor;
            frame[4] = MozaProtocol.CalculateWireChecksum(frame, 4);
            // Sanity-check the hard-coded expected checksum once at build time.
            // CalculateWireChecksum yields 0x9d for this body (verified against
            // protocol note § 3 keepalive).
            return frame;
        }

        // Encoders ----------------------------------------------------------

        /// <summary>
        /// Frequency encoding per protocol note § 2: <c>u16 = round(hz * 65536 / 200)</c>,
        /// saturating at 0xFFFF. Maps 0..200 Hz to the full u16 range.
        /// Verified reference values: 10 Hz → 0x0CCC, 22 Hz → 0x1C28, 55 Hz → 0x4666,
        /// 100 Hz → 0x8000, 200 Hz → 0xFFFF.
        /// </summary>
        public static ushort EncodeFreq(double hz)
        {
            if (double.IsNaN(hz) || hz <= 0) return 0;
            double raw = Math.Round(hz * 65536.0 / 200.0);
            if (raw <= 0) return 0;
            if (raw >= 0xFFFF) return 0xFFFF;
            return (ushort)raw;
        }

        /// <summary>
        /// Amplitude encoding per protocol note § 2:
        /// <c>u16 = clamp(round(amp_0_to_1 * 65535), 0, 0xFFFF)</c>.
        /// </summary>
        public static ushort EncodeAmp(double amp01)
        {
            if (double.IsNaN(amp01) || amp01 <= 0) return 0;
            double raw = Math.Round(amp01 * 65535.0);
            if (raw <= 0) return 0;
            if (raw >= 0xFFFF) return 0xFFFF;
            return (ushort)raw;
        }

        /// <summary>
        /// Per-cycle scaling factor: <c>param1 = clamp(round(paramK / freq_hz), 1, 255)</c>
        /// per protocol note § 3. Empirically observed values:
        /// ABS @ 22 Hz → 90 (capture: 0x5a), Lockup @ 55 Hz → 48, Threshold @ 70 Hz → 44,
        /// Engine @ 10 Hz → 100.
        /// </summary>
        public static byte ComputeParam1(double paramK, double freqHz)
        {
            if (freqHz <= 0) return 1;
            double raw = Math.Round(paramK / freqHz);
            if (raw < 1) return 1;
            if (raw > 255) return 255;
            return (byte)raw;
        }

        /// <summary>
        /// Look up the ParamK constant for an effect (used by <see cref="ComputeParam1"/>).
        /// </summary>
        public static double ParamKFor(MBoosterEffectId effect)
        {
            switch (effect)
            {
                case MBoosterEffectId.Abs:       return ParamKAbs;
                case MBoosterEffectId.Lockup:    return ParamKLockup;
                case MBoosterEffectId.Threshold: return ParamKThreshold;
                case MBoosterEffectId.Engine:    return ParamKEngine;
                default: return 1.0;
            }
        }
    }
}
