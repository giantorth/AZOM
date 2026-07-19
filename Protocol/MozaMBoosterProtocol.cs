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
        // Reverse-engineered from two real Pit House USB captures (see
        // docs/protocol/devices/mbooster.md "Effects card UI") — previously
        // unverified (only 1-4 were confirmed from earlier captures), now
        // confirmed real: the firmware accepted and held sustained frames
        // with this effect type. Uses a materially different payload shape
        // from the other four — see BuildRoadTextureFrame.
        RoadTexture = 9,
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
            ushort ampU16,
            byte device = DeviceMotor)
        {
            // 14 bytes total: start + len + group + device + 9 payload + checksum.
            // <paramref name="device"/> selects WHICH pedal's motor on a chained
            // mBooster — capture-verified the chain exposes motors at device ids
            // 0x12 (host) / 0x1d / 0x1e (PitHouse keepalives all three).
            var frame = new byte[14];
            frame[0]  = MozaProtocol.MessageStart;
            frame[1]  = MotorPayloadLen;        // 0x09
            frame[2]  = GroupMotorWrite;        // 0x24
            frame[3]  = device;                 // 0x12 host / 0x1d / 0x1e chained
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
        /// the last-active waveform can latch. Also correct for Road Texture's
        /// different payload shape (see <see cref="BuildRoadTextureFrame"/>) —
        /// zeroing every field produces the exact same bytes either way, and
        /// this matches the real disable frame observed in capture.
        /// </summary>
        public static byte[] BuildDisableFrame(MBoosterEffectId effect, byte device = DeviceMotor)
            => BuildMotorFrame(effect, enable: false, param1: 0, freqU16: 0, ampU16: 0, device);

        /// <summary>
        /// Build the motor-write frame for Road Texture (effect type 9) — a
        /// different payload shape from the other four effects, reverse-
        /// engineered from two real Pit House USB captures, each isolating a
        /// stepped 0/25/50/75/100% drag of one control (see
        /// docs/protocol/devices/mbooster.md "Effects card UI"). Unlike
        /// ABS/Lockup/Threshold/Engine — where the pad byte is always 0x00
        /// and param1 is a per-cycle scaling factor derived from ParamK/freq
        /// — Road Texture repurposes those two bytes as the high/low bytes
        /// of a 16-bit Smoothness value, and the "freq" slot carries a live
        /// noise sample rather than a fixed Hz:
        /// <pre>
        /// 7e  09  24  12   b1  09  EN   SH   SL   NH  NL   IH   IL   CK
        ///                  │   │   │    └─┴─smoothness u16 BE  └─┴─intensity u16 BE
        ///                  │   │   └ enable (0 = off, 1 = on)
        ///                  │   └ effect type (9 = Road Texture)
        ///                  └ cmd id (0xb1)
        /// </pre>
        /// <paramref name="noiseRaw"/> (NH,NL) is host-generated and sent raw/
        /// unscaled every tick — confirmed from capture that neither the
        /// noise signal's amplitude range nor its oscillation rate changed
        /// across 4 different Intensity/Smoothness values, meaning the
        /// firmware applies both parameters to the noise internally rather
        /// than Pit House pre-scaling it. See
        /// <see cref="MBoosterEffectSynthesizer.SynthesizeRoadTextureNoise"/>.
        /// </summary>
        public static byte[] BuildRoadTextureFrame(bool enable, ushort intensityRaw, ushort smoothnessRaw, ushort noiseRaw, byte device = DeviceMotor)
        {
            var frame = new byte[14];
            frame[0]  = MozaProtocol.MessageStart;
            frame[1]  = MotorPayloadLen;
            frame[2]  = GroupMotorWrite;
            frame[3]  = device;
            frame[4]  = CmdMotorWrite;
            frame[5]  = (byte)MBoosterEffectId.RoadTexture;
            frame[6]  = enable ? (byte)1 : (byte)0;
            frame[7]  = (byte)(smoothnessRaw >> 8);
            frame[8]  = (byte)(smoothnessRaw & 0xFF);
            frame[9]  = (byte)(noiseRaw >> 8);
            frame[10] = (byte)(noiseRaw & 0xFF);
            frame[11] = (byte)(intensityRaw >> 8);
            frame[12] = (byte)(intensityRaw & 0xFF);
            frame[13] = MozaProtocol.CalculateWireChecksum(frame, 13);
            return frame;
        }

        /// <summary>
        /// Degenerate 0-payload frame targeting the motor — <c>7e 00 00 12 9d</c>.
        /// Per protocol note § 3 "Keepalive": send every ~500 ms whenever the port
        /// is open. If we stop sending it the motor will eventually drop connection
        /// state and may stop responding to writes until the link is re-established.
        /// </summary>
        public static byte[] BuildKeepalive(byte device = DeviceMotor)
        {
            var frame = new byte[5];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = 0x00;
            frame[2] = 0x00;
            frame[3] = device;
            frame[4] = MozaProtocol.CalculateWireChecksum(frame, 4);
            // CalculateWireChecksum yields 0x9d for device 0x12, 0xa8 for 0x1d,
            // 0xa9 for 0x1e — the three motor device ids a chained mBooster
            // exposes (all keepalived by PitHouse; capture-verified).
            return frame;
        }

        /// <summary>
        /// The motor/keepalive device ids a chained mBooster exposes — host at
        /// 0x12 plus two chain ports at 0x1d/0x1e. Capture-verified: PitHouse
        /// keepalives all three every ~500 ms, and effect (cmd 0xb1) frames are
        /// addressed to the specific pedal's device id. See
        /// docs/protocol/devices/mbooster.md.
        /// </summary>
        public static readonly byte[] MotorDeviceIds = { 0x12, 0x1d, 0x1e };

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
        /// Pit House "Max Threshold (kg)" encoding — reverse-engineered from a
        /// real capture (wire command <c>mbooster-brake-threshold</c>, cmdId
        /// 0xB3; see docs/protocol/devices/mbooster.md "Sim Input Mapping").
        /// Same 0..200 → u16-range pattern as <see cref="EncodeFreq"/>:
        /// <c>raw = round(kg * 65536 / 200)</c>. Verified against two capture
        /// data points: 4 kg → 1311 exactly, and an unlabeled capture whose
        /// raw value decoded to ~126 kg, matching an independently-reported
        /// real Pit House setting of ~125 kg.
        /// </summary>
        public static int EncodeThresholdKg(double kg)
        {
            if (double.IsNaN(kg) || kg <= 0) return 0;
            double raw = Math.Round(kg * 65536.0 / 200.0);
            if (raw <= 0) return 0;
            if (raw >= int.MaxValue) return int.MaxValue;
            return (int)raw;
        }

        /// <summary>Inverse of <see cref="EncodeThresholdKg"/>.</summary>
        public static double DecodeThresholdKg(int raw)
        {
            if (raw <= 0) return 0;
            return raw * 200.0 / 65536.0;
        }

        /// <summary>
        /// Pit House "Start/End of Travel (mm)" encoding — reverse-engineered
        /// from two real Pit House USB captures (wire commands
        /// <c>mbooster-brake-travel-start</c>/<c>-end</c>, cmdIds 0x84/0x85;
        /// see docs/protocol/devices/mbooster.md "Pedal Feel"). Same
        /// "value * 65536 / fullscale" pattern as <see cref="EncodeThresholdKg"/>,
        /// with fullscale = 53.5mm (= <c>TravelMinMm</c> 3.8 + <c>TravelMaxMm</c>
        /// 49.7, the slider's own bounds): <c>raw = round(mm * 65536 / 53.5)</c>.
        /// Verified against 4 capture data points (10/20/30mm on Start,
        /// 30/40mm on End) — all matched exactly or within 1 raw unit
        /// (~0.001mm), and the shared 30mm target produced the identical raw
        /// value via both cmdIds.
        /// </summary>
        public static int EncodeTravelMm(double mm)
        {
            if (double.IsNaN(mm) || mm <= 0) return 0;
            double raw = Math.Round(mm * 65536.0 / 53.5);
            if (raw <= 0) return 0;
            if (raw >= 0xFFFF) return 0xFFFF;
            return (int)raw;
        }

        /// <summary>Inverse of <see cref="EncodeTravelMm"/>.</summary>
        public static double DecodeTravelMm(int raw)
        {
            if (raw <= 0) return 0;
            return raw * 53.5 / 65536.0;
        }

        /// <summary>
        /// EXPERIMENTAL / unverified — encoding for the <c>mbooster-brake-
        /// curve7-*</c> commands (cmdId 0xAB) spotted alongside a Travel
        /// Start write in pedal_travel.pcapng (see
        /// MozaCommandDatabase.cs and MozaMBoosterRegistry.ResampleCurveAtSevenths).
        /// Values decoded near <c>selector/7 * 65535</c>, i.e. a plain
        /// fraction-of-full-scale over the 0-65535 range — same
        /// "value * 65535 / fullscale" family as <see cref="EncodeEndstopStiffness"/>,
        /// with fullscale = 100 (a curve-node percentage, 0-100 like
        /// <c>MBoosterDeviceSettings.CurveY</c>): <c>raw = round(pct * 65535 / 100)</c>.
        /// Not cross-checked against a second capture — treat with more
        /// suspicion than this file's other Encode/Decode pairs.
        /// </summary>
        public static int EncodeCurve7Point(double pct)
        {
            if (double.IsNaN(pct)) pct = 0;
            pct = Math.Max(0, Math.Min(100, pct));
            double raw = Math.Round(pct * 65535.0 / 100.0);
            if (raw <= 0) return 0;
            if (raw >= 0xFFFF) return 0xFFFF;
            return (int)raw;
        }

        /// <summary>
        /// Pit House "End Stop Stiffness" (Front Limit / End Limit) encoding
        /// — reverse-engineered from two real Pit House USB captures (wire
        /// command <c>mbooster-brake-endstop-front</c>/<c>-end</c>, cmdId
        /// 0xB2 with a selector byte; see docs/protocol/devices/mbooster.md
        /// "Pedal Feel"). Fixed 1-10 scale over the 0-65535 range:
        /// <c>raw = round(value * 65535 / 10)</c>. Verified against 18
        /// capture data points (9 per slider, values 2-10) — all matched
        /// exactly, using round-half-away-from-zero (two points landed on an
        /// exact .5 tie and the device rounded up, not to even, so this uses
        /// <see cref="MidpointRounding.AwayFromZero"/> unlike the other
        /// Encode* helpers here, none of which had evidence of a tie case).
        /// </summary>
        public static int EncodeEndstopStiffness(double value)
        {
            if (double.IsNaN(value) || value <= 0) return 0;
            double raw = Math.Round(value * 65535.0 / 10.0, MidpointRounding.AwayFromZero);
            if (raw <= 0) return 0;
            if (raw >= 0xFFFF) return 0xFFFF;
            return (int)raw;
        }

        /// <summary>Inverse of <see cref="EncodeEndstopStiffness"/>.</summary>
        public static double DecodeEndstopStiffness(int raw)
        {
            if (raw <= 0) return 0;
            return raw * 10.0 / 65535.0;
        }

        /// <summary>
        /// Pit House Road Texture Intensity/Smoothness encoding — reverse-
        /// engineered from two real Pit House USB captures, one per
        /// parameter (each isolating stepped drags to 25/50/75/100%). Both
        /// parameters use the identical formula, a "count-1" full-scale
        /// pattern rather than the "* 65535" / "* 65536 / fullscale" pattern
        /// every other reverse-engineered mbooster value uses:
        /// <c>raw = round(pct / 100 * 65536) - 1</c>, clamped to 0 at
        /// <c>pct &lt;= 0</c>. Verified exactly against all 8 capture data
        /// points (4 per parameter): 25% -&gt; 0x3fff, 50% -&gt; 0x7fff,
        /// 75% -&gt; 0xbfff, 100% -&gt; 0xffff.
        /// </summary>
        public static ushort EncodeRoadTextureLevel(double pct)
        {
            if (double.IsNaN(pct) || pct <= 0) return 0;
            double raw = Math.Round(pct / 100.0 * 65536.0) - 1;
            if (raw <= 0) return 0;
            if (raw >= 0xFFFF) return 0xFFFF;
            return (ushort)raw;
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
