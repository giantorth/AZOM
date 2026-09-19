using System;

namespace MozaPlugin.Protocol
{
    /// <summary>Which pedal a vibration command addresses.</summary>
    public enum PedalHapticsPedal : byte
    {
        Throttle = 0x01,
        Brake    = 0x02,
        Clutch   = 0x03,
    }

    /// <summary>
    /// Effect slot within a pedal. Each pedal owns nine of these and they run
    /// <b>concurrently</b> — enabling one does not disturb another, and stopping
    /// one does not stop the rest. The names are the game events each slot is
    /// meant to represent; the firmware synthesizes nothing from them, it plays
    /// the duration / frequency / strength it is handed, so a slot is really
    /// "one independent oscillator on that pedal's motor".
    ///
    /// <see cref="EngineVibration"/> and <see cref="RoadTexture"/> are the two
    /// that behave differently — see
    /// <see cref="MozaPedalHapticsProtocol.SlotSelfExpires"/> and
    /// <see cref="MozaPedalHapticsProtocol.BuildRoadTextureFrame"/>.
    /// </summary>
    public enum PedalHapticsEffectSlot : byte
    {
        TractionControl = 0x00,
        Abs             = 0x01,
        Lockup          = 0x02,
        BrakeThreshold  = 0x03,
        EngineVibration = 0x04,
        ClutchBitePoint = 0x05,
        GearShift       = 0x06,
        WheelSlip       = 0x07,
        RoadTexture     = 0x08,
    }

    /// <summary>
    /// How a frame is addressed. The same 11-byte vibration payload is carried
    /// either way; only the envelope differs.
    /// </summary>
    public enum PedalHapticsAddressing
    {
        /// <summary>Unit plugged straight into the PC on its own CDC port.</summary>
        UsbDirect,

        /// <summary>Unit reached through another device's pipe, via the extended-id envelope.</summary>
        Extended,
    }

    /// <summary>
    /// Frame builders + value encoders for the MOZA S12 pedal vibration module —
    /// three motor ports, nine concurrent effect slots per port. The framing is
    /// the standard Moza wire format, so checksum and 0x7E stuffing reuse
    /// <see cref="MozaProtocol"/>; this file owns the vibration command itself.
    ///
    /// <b>Two envelopes.</b> Plugged into the PC the unit answers at address
    /// <c>0x12</c> (reply <c>0x21</c>) with an 11-byte payload. Reached through
    /// another device's pipe it uses the extended form: address <c>0x1F</c>
    /// (reply <c>0xF1</c>) followed by a one-byte extended destination id
    /// <c>0x1E</c>, which counts toward both the length and the checksum — so the
    /// length goes 0x0B → 0x0C and every payload byte shifts one place right.
    /// Everything after that extended id is byte-identical between the two.
    ///
    /// Full reference: <c>docs/protocol/devices/pedal-haptics.md</c>.
    /// Regression check: <c>tools/pedal-haptics-frame-check</c>.
    /// </summary>
    public static class MozaPedalHapticsProtocol
    {
        // Envelope ----------------------------------------------------------

        /// <summary>Group 77 (0x4D) — vibration-module command. Replies come back on 0xCD.</summary>
        public const byte GroupVibration = 0x4D;
        public const byte GroupVibrationReply = 0xCD;

        /// <summary>Address byte for a unit on its own USB CDC port, and its reply form.</summary>
        public const byte AddressUsb = 0x12;
        public const byte AddressUsbReply = 0x21;

        /// <summary>Address byte for the extended (routed) envelope, and its reply form.</summary>
        public const byte AddressExtended = 0x1F;
        public const byte AddressExtendedReply = 0xF1;

        /// <summary>Extended destination id (30), present only in the extended envelope.</summary>
        public const byte ExtendedId = 0x1E;

        /// <summary>Payload length byte: 11, or 12 once the extended id is counted.</summary>
        public const byte PayloadLenUsb = 0x0B;
        public const byte PayloadLenExtended = 0x0C;

        /// <summary>Total frame size pre-stuffing, per envelope.</summary>
        public const int FrameLenUsb = 16;
        public const int FrameLenExtended = 17;

        // Payload -----------------------------------------------------------
        // [00] cmd 0x01 (vibration)
        // [01] 0x01 set / 0x02 query
        // [02] pedal 1..3
        // [03] effect slot 0..8
        // [04] enable 1 / disable 0   (query: 0)
        // [05..06] duration ms, u16 BE
        // [07..08] frequency Hz, u16 BE   (slot 8: suspension position 0..100)
        // [09..10] strength, u16 BE, full scale 65535

        public const byte CmdVibration = 0x01;
        public const byte SubCmdSet = 0x01;
        public const byte SubCmdQuery = 0x02;

        /// <summary>Vibration payload size, excluding any extended id.</summary>
        public const int PayloadBytes = 11;

        // Limits ------------------------------------------------------------

        public const byte MinPedal = (byte)PedalHapticsPedal.Throttle;
        public const byte MaxPedal = (byte)PedalHapticsPedal.Clutch;
        public const byte MinSlot = (byte)PedalHapticsEffectSlot.TractionControl;
        public const byte MaxSlot = (byte)PedalHapticsEffectSlot.RoadTexture;

        /// <summary>Number of motor ports (pedals) a unit drives.</summary>
        public const int PedalCount = 3;

        /// <summary>Effect slots per pedal.</summary>
        public const int SlotsPerPedal = 9;

        /// <summary>
        /// Every addressable oscillator on a unit: three pedals × nine slots.
        /// This is what the plugin exposes as ShakeIt channels, so the index
        /// mapping below is part of the user-visible contract — reordering it
        /// silently moves every effect a user has already assigned.
        ///
        /// Pedal-major: channels 0–8 are the throttle's nine slots, 9–17 the
        /// brake's, 18–26 the clutch's.
        /// </summary>
        public const int ChannelCount = PedalCount * SlotsPerPedal;

        /// <summary>Pedal id for a ShakeIt channel index.</summary>
        public static byte PedalForChannel(int channel)
            => ClampPedal((byte)(ClampChannel(channel) / SlotsPerPedal + 1));

        /// <summary>Effect slot for a ShakeIt channel index.</summary>
        public static byte SlotForChannel(int channel)
            => (byte)(ClampChannel(channel) % SlotsPerPedal);

        private static int ClampChannel(int channel)
        {
            if (channel < 0) return 0;
            if (channel >= ChannelCount) return ChannelCount - 1;
            return channel;
        }

        /// <summary>Slot label, matching the names the module's own documentation uses.</summary>
        public static string SlotName(byte slot) => slot switch
        {
            (byte)PedalHapticsEffectSlot.TractionControl => "Traction Control",
            (byte)PedalHapticsEffectSlot.Abs             => "ABS",
            (byte)PedalHapticsEffectSlot.Lockup          => "Lockup",
            (byte)PedalHapticsEffectSlot.BrakeThreshold  => "Brake Threshold",
            (byte)PedalHapticsEffectSlot.EngineVibration => "Engine",
            (byte)PedalHapticsEffectSlot.ClutchBitePoint => "Bite Point",
            (byte)PedalHapticsEffectSlot.GearShift       => "Gear Shift",
            (byte)PedalHapticsEffectSlot.WheelSlip       => "Wheel Slip",
            (byte)PedalHapticsEffectSlot.RoadTexture     => "Road Texture",
            _ => "Slot " + slot,
        };

        /// <summary>Pedal label with a leading capital, for channel names.</summary>
        public static string PedalLabel(byte pedal) => pedal switch
        {
            (byte)PedalHapticsPedal.Throttle => "Throttle",
            (byte)PedalHapticsPedal.Brake    => "Brake",
            (byte)PedalHapticsPedal.Clutch   => "Clutch",
            _ => "Pedal " + pedal,
        };

        /// <summary>ShakeIt channel name, e.g. "Brake: ABS".</summary>
        public static string ChannelName(int channel)
            => PedalLabel(PedalForChannel(channel)) + ": " + SlotName(SlotForChannel(channel));

        /// <summary>Accepted frequency band. Values outside it are clamped, not rejected.</summary>
        public const int MinFrequencyHz = 10;
        public const int MaxFrequencyHz = 100;

        /// <summary>Accepted duration band, milliseconds.</summary>
        public const int MinDurationMs = 1;
        public const int MaxDurationMs = 60000;

        /// <summary>Strength full scale — 65535 is maximum, 0 is silent.</summary>
        public const ushort MaxStrength = 0xFFFF;

        /// <summary>Suspension-position range carried in the frequency field for <see cref="PedalHapticsEffectSlot.RoadTexture"/>.</summary>
        public const int MinRoadPosition = 0;
        public const int MaxRoadPosition = 100;

        /// <summary>
        /// Slots that stop themselves once their duration elapses. The other two
        /// — <see cref="PedalHapticsEffectSlot.EngineVibration"/> and
        /// <see cref="PedalHapticsEffectSlot.RoadTexture"/> — do not treat
        /// duration as a timeout and instead run until roughly a second passes
        /// with no update, or until explicitly disabled. Either way the host has
        /// to keep streaming to sustain an effect; the difference is only what
        /// happens once it stops.
        /// </summary>
        public static bool SlotSelfExpires(byte slot) =>
            slot != (byte)PedalHapticsEffectSlot.EngineVibration
            && slot != (byte)PedalHapticsEffectSlot.RoadTexture;

        /// <summary>
        /// Duration written into a streamed frame. An effect is held on by
        /// re-sending inside this window; the value is generous because with many
        /// channels live a single channel is only refreshed every few ticks (see
        /// <see cref="MaxFramesPerTick"/>), and it must not lapse between two of
        /// its own refreshes. It still bounds how long a motor runs if the host
        /// dies — a deliberate deactivation sends an explicit disable instead, so
        /// this never delays a normal stop.
        /// </summary>
        public const ushort StreamDurationMs = 500;

        /// <summary>Motor-loop tick.</summary>
        public const int StreamPeriodMs = 20;
        /// <summary>
        /// Frames emitted per tick, across all channels — a backstop, not a
        /// normal operating constraint. Any realistic setup has a handful of
        /// channels live at once, and up to eight are serviced every tick with
        /// no throttling at all. The budget only engages beyond that, where a
        /// flat refresh of all 27 would be 1350 frames/s (~23 kB/s, more than a
        /// 115200 link carries and far more than a routed unit's share of a pipe
        /// already streaming telemetry); there the round-robin degrades refresh
        /// to ~80 ms rather than overrunning the link. Activation edges bypass
        /// the budget, so an effect's attack is never delayed either way.
        /// </summary>
        public const int MaxFramesPerTick = 8;

        // Frame builders ----------------------------------------------------

        /// <summary>
        /// Build a set/stop frame. Wire layout, USB envelope first:
        /// <pre>
        /// 7e  0b  4d  12         01 01  PD  SL  EN   DH DL   FH FL   SH SL   CK
        /// 7e  0c  4d  1f   1e    01 01  PD  SL  EN   DH DL   FH FL   SH SL   CK
        ///                  └ext┘  │  │   │   │   │   └dur┘   └freq┘  └─str┘
        ///                         │  │   │   │   └ enable 1 / disable 0
        ///                         │  │   │   └ effect slot 0..8
        ///                         │  │   └ pedal 1 throttle, 2 brake, 3 clutch
        ///                         │  └ 01 set (02 = query)
        ///                         └ vibration command
        /// </pre>
        /// </summary>
        /// <param name="addressing">Envelope to use — <see cref="PedalHapticsAddressing.Extended"/> for a routed unit.</param>
        /// <param name="pedal">1 throttle, 2 brake, 3 clutch.</param>
        /// <param name="slot">Effect slot 0..8.</param>
        /// <param name="enable">False emits the stop shape: every parameter zeroed.</param>
        /// <param name="durationMs">1..60000. Clamped.</param>
        /// <param name="freqHz">10..100 Hz. Clamped.</param>
        /// <param name="strength01">0..1, scaled to the full 0..65535 range.</param>
        public static byte[] BuildSetFrame(
            PedalHapticsAddressing addressing,
            byte pedal,
            byte slot,
            bool enable,
            int durationMs,
            double freqHz,
            double strength01)
        {
            return BuildPayloadFrame(
                addressing, SubCmdSet, pedal, slot,
                enable ? (byte)1 : (byte)0,
                enable ? ClampDuration(durationMs) : (ushort)0,
                enable ? EncodeFrequency(freqHz) : (ushort)0,
                enable ? EncodeStrength(strength01) : (ushort)0);
        }

        /// <summary>
        /// Build a Road Texture frame. That slot reuses the frequency field for
        /// an integer suspension position (0..100) and takes no duration — the
        /// firmware generates the texture from a stream of changing positions, so
        /// a constant position settles to near-silence.
        /// </summary>
        public static byte[] BuildRoadTextureFrame(
            PedalHapticsAddressing addressing,
            byte pedal,
            bool enable,
            int suspensionPosition,
            double strength01)
        {
            byte slot = (byte)PedalHapticsEffectSlot.RoadTexture;
            if (!enable) return BuildDisableFrame(addressing, pedal, slot);

            return BuildPayloadFrame(
                addressing, SubCmdSet, pedal, slot, enable: 1,
                duration: 0,
                frequency: ClampRoadPosition(suspensionPosition),
                strength: EncodeStrength(strength01));
        }

        /// <summary>
        /// Stop one slot: same command, enable 0, every parameter zeroed. A stop
        /// still carries the full payload. Each slot the plugin enabled has to be
        /// stopped individually — stopping one leaves the other eight running,
        /// and a strength of zero is not the same as disabling.
        /// </summary>
        public static byte[] BuildDisableFrame(PedalHapticsAddressing addressing, byte pedal, byte slot)
            => BuildPayloadFrame(addressing, SubCmdSet, pedal, slot, enable: 0,
                                 duration: 0, frequency: 0, strength: 0);

        /// <summary>
        /// Ask whether one slot is enabled. The reply echoes the command and
        /// carries [enabled][remaining ms][frequency][strength]. Used as the
        /// presence probe: unlike a bare bus heartbeat, an answer proves the
        /// device speaks the vibration protocol rather than merely existing.
        ///
        /// The request must still be padded to a full payload; a truncated
        /// <c>01 02 PD SL</c> is not accepted.
        /// </summary>
        public static byte[] BuildQueryFrame(PedalHapticsAddressing addressing, byte pedal, byte slot)
            => BuildPayloadFrame(addressing, SubCmdQuery, pedal, slot, enable: 0,
                                 duration: 0, frequency: 0, strength: 0);

        private static byte[] BuildPayloadFrame(
            PedalHapticsAddressing addressing,
            byte subCmd, byte pedal, byte slot, byte enable,
            ushort duration, ushort frequency, ushort strength)
        {
            bool ext = addressing == PedalHapticsAddressing.Extended;
            int len = ext ? FrameLenExtended : FrameLenUsb;
            var frame = new byte[len];

            frame[0] = MozaProtocol.MessageStart;
            frame[1] = ext ? PayloadLenExtended : PayloadLenUsb;
            frame[2] = GroupVibration;
            frame[3] = ext ? AddressExtended : AddressUsb;

            int p = 4;
            if (ext) frame[p++] = ExtendedId;

            frame[p + 0] = CmdVibration;
            frame[p + 1] = subCmd;
            frame[p + 2] = ClampPedal(pedal);
            frame[p + 3] = ClampSlot(slot);
            frame[p + 4] = enable;
            WriteU16(frame, p + 5, duration);
            WriteU16(frame, p + 7, frequency);
            WriteU16(frame, p + 9, strength);

            frame[len - 1] = MozaProtocol.CalculateWireChecksum(frame, len - 1);
            return frame;
        }

        // Response matching -------------------------------------------------

        /// <summary>
        /// True for a vibration reply in the given envelope, matched on the
        /// de-framed body (group, address, then payload) the read path hands up.
        /// </summary>
        public static bool IsVibrationResponse(byte[] body, int length, PedalHapticsAddressing addressing)
        {
            if (body == null || length < 3) return false;
            if (body[0] != GroupVibrationReply) return false;

            if (addressing == PedalHapticsAddressing.Extended)
                return length >= 4 && body[1] == AddressExtendedReply && body[2] == ExtendedId;

            return body[1] == AddressUsbReply;
        }

        /// <summary>
        /// Pull a query reply apart. Returns false for anything that is not a
        /// well-formed query response in this envelope.
        /// </summary>
        public static bool TryParseQueryResponse(
            byte[] body, int length, PedalHapticsAddressing addressing,
            out byte pedal, out byte slot, out bool enabled,
            out ushort remainingMs, out ushort frequencyHz, out ushort strength)
        {
            pedal = 0;
            slot = 0;
            enabled = false;
            remainingMs = 0;
            frequencyHz = 0;
            strength = 0;

            if (!IsVibrationResponse(body, length, addressing)) return false;

            // body = [group][address]([extended id]) then the payload.
            int p = addressing == PedalHapticsAddressing.Extended ? 3 : 2;
            if (length < p + PayloadBytes) return false;
            if (body[p] != CmdVibration || body[p + 1] != SubCmdQuery) return false;

            pedal = body[p + 2];
            slot = body[p + 3];
            enabled = body[p + 4] != 0;
            remainingMs = (ushort)((body[p + 5] << 8) | body[p + 6]);
            frequencyHz = (ushort)((body[p + 7] << 8) | body[p + 8]);
            strength = (ushort)((body[p + 9] << 8) | body[p + 10]);
            return true;
        }

        // Encoders ----------------------------------------------------------

        /// <summary>ShakeIt channel index (0-based) to pedal id: 0 throttle, 1 brake, 2 clutch.</summary>
        public static byte PedalForIndex(int index) => ClampPedal((byte)(index + 1));

        /// <summary>Human name for a pedal id, for log lines.</summary>
        public static string PedalName(byte pedal) => pedal switch
        {
            (byte)PedalHapticsPedal.Throttle => "throttle",
            (byte)PedalHapticsPedal.Brake    => "brake",
            (byte)PedalHapticsPedal.Clutch   => "clutch",
            _ => "pedal " + pedal,
        };

        private static byte ClampPedal(byte pedal)
        {
            if (pedal < MinPedal) return MinPedal;
            if (pedal > MaxPedal) return MaxPedal;
            return pedal;
        }

        private static byte ClampSlot(byte slot) => slot > MaxSlot ? MaxSlot : slot;

        /// <summary>Duration clamped into the accepted band. Zero stays zero — a stop frame carries no duration.</summary>
        public static ushort ClampDuration(int ms)
        {
            if (ms <= 0) return 0;
            if (ms < MinDurationMs) return MinDurationMs;
            if (ms > MaxDurationMs) return (ushort)MaxDurationMs;
            return (ushort)ms;
        }

        /// <summary>Frequency in plain Hz, clamped to the accepted band. Zero stays zero.</summary>
        public static ushort EncodeFrequency(double hz)
        {
            if (double.IsNaN(hz) || hz <= 0) return 0;
            double raw = Math.Round(hz);
            if (raw < MinFrequencyHz) return MinFrequencyHz;
            if (raw > MaxFrequencyHz) return MaxFrequencyHz;
            return (ushort)raw;
        }

        /// <summary>Suspension position for Road Texture, clamped to 0..100.</summary>
        public static ushort ClampRoadPosition(int position)
        {
            if (position <= MinRoadPosition) return MinRoadPosition;
            if (position >= MaxRoadPosition) return MaxRoadPosition;
            return (ushort)position;
        }

        /// <summary>
        /// Strength over the full 16-bit range: <c>round(level * 65535)</c>.
        /// Perceived vibration is not necessarily linear in this value.
        /// </summary>
        public static ushort EncodeStrength(double level01)
        {
            if (double.IsNaN(level01) || level01 <= 0) return 0;
            if (level01 >= 1.0) return MaxStrength;
            double raw = Math.Round(level01 * MaxStrength);
            if (raw <= 0) return 0;
            if (raw >= MaxStrength) return MaxStrength;
            return (ushort)raw;
        }

        private static void WriteU16(byte[] frame, int offset, ushort value)
        {
            frame[offset] = (byte)(value >> 8);
            frame[offset + 1] = (byte)(value & 0xFF);
        }
    }
}
