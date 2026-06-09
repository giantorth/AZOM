using System;
using System.Collections.Generic;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Mechanical layout the AB9 advertises to the host. Numeric value matches the
    /// single-byte payload of the <c>0x1F / 0xD3 00</c> mode-set command captured
    /// from PitHouse (see docs/protocol/devices/ab9-shifter.md).
    /// </summary>
    public enum Ab9Mode : byte
    {
        FivePlusR_L1 = 0x00,
        SixPlusR_L1  = 0x04,
        SixPlusR_L2  = 0x05,
        SevenPlusR_L1 = 0x06,
        SevenPlusR_L2 = 0x07,
        Sequential   = 0x09,
    }

    /// <summary>
    /// Configurable feel sliders exposed on the AB9. Each value maps to a 0..100
    /// integer payload sent verbatim to the device.
    /// </summary>
    public enum Ab9Slider
    {
        MechanicalResistance,
        Spring,
        NaturalDamping,
        NaturalFriction,
        MaxTorqueLimit,
    }

    /// <summary>
    /// Wraps a dedicated <see cref="MozaSerialConnection"/> for the AB9 active shifter
    /// (VID 0x346E, PID 0x1000). Owns the connection lifecycle, identity probe,
    /// mode/slider writes, and stored-state read-back. Independent of the wheelbase
    /// connection so both can run side-by-side.
    /// </summary>
    public class MozaAb9DeviceManager : IDisposable
    {
        // Slider command names registered in MozaCommandDatabase. Keep this in
        // sync with the AB9 entries added there — the lookup is the single source
        // of truth for cmdId bytes, so no opcode tables live here.
        private static readonly Dictionary<Ab9Slider, string> SliderCommands =
            new Dictionary<Ab9Slider, string>
            {
                { Ab9Slider.MechanicalResistance, "ab9-mech-resistance" },
                { Ab9Slider.Spring,               "ab9-spring" },
                { Ab9Slider.NaturalDamping,       "ab9-natural-damping" },
                { Ab9Slider.NaturalFriction,      "ab9-natural-friction" },
                { Ab9Slider.MaxTorqueLimit,       "ab9-max-torque-limit" },
            };

        public static IReadOnlyList<Ab9Slider> AllSliders { get; } = new[]
        {
            Ab9Slider.MechanicalResistance,
            Ab9Slider.Spring,
            Ab9Slider.NaturalDamping,
            Ab9Slider.NaturalFriction,
            Ab9Slider.MaxTorqueLimit,
        };

        private readonly MozaSerialConnection _connection;
        private volatile bool _detected;
        private volatile bool _ffbInitSent;

        public bool Detected => _detected;
        public bool IsConnected => _connection.IsConnected;
        public MozaSerialConnection Connection => _connection;

        public event Action<byte[]>? MessageReceived
        {
            add    => _connection.MessageReceived += value;
            remove => _connection.MessageReceived -= value;
        }

        public MozaAb9DeviceManager(Func<bool>? disableProbeFallback = null)
        {
            // PID filter accepts the AB9 PID and any unknown Moza PID
            // (future-hardware fallback) during registry-based discovery.
            // The wheelbase's filter accepts wheelbase PIDs, the Universal
            // HUB PID, and the same unknown set, so both connections race
            // for unknown ports — safe because each runs its own
            // protocol-specific probe and only one will get a matching
            // response. The AB9 identity probe (group 0x09 dev 0x12) only
            // accepts a 0x89 response and runs a base-disambiguation
            // pre-check first, so it cannot mis-claim a wheelbase tty
            // even when admitted to the candidate set. When the registry
            // returns zero MOZA devices (Wine/Proton, missing driver) the
            // legacy serial-probe path runs as a last resort and honours
            // the same filter. See Protocol/MozaUsbIds.cs and
            // docs/protocol/devices/usb-ids.md.
            _connection = new MozaSerialConnection(
                pid => MozaUsbIds.IsAb9Pid(pid) || !MozaUsbIds.IsKnownMozaPid(pid),
                MozaProbeTarget.Ab9,
                disableProbeFallback);
            _connection.CaptureLabel = "ab9";
            // Reset detection latches when the underlying port wedges. The
            // Disconnected event fires from HandleIoFailure on the read/write
            // thread, so this handler MUST stay lightweight (no Join, no I/O):
            // it only clears two volatile bools. Without this, _ffbInitSent
            // stays sticky after a silent reconnect and the FFB slot-table
            // handshake never re-fires → engine vibration / pulse streams hit
            // an uninitialised device.
            _connection.Disconnected += OnConnectionDisconnected;
        }

        private void OnConnectionDisconnected()
        {
            _detected = false;
            _ffbInitSent = false;
        }

        /// <summary>
        /// Attempt to open the AB9's COM port. Returns true if a connection
        /// was established (or was already up). Idempotent across reconnect
        /// attempts; the underlying serial connection re-uses the last known
        /// port name when possible to avoid re-probing.
        /// </summary>
        public bool TryConnect()
        {
            if (_connection.IsConnected) return true;
            bool ok = _connection.Connect();
            if (ok)
                MozaLog.Info("[AZOM/AB9] Connected to AB9 shifter");
            return ok;
        }

        public void Disconnect()
        {
            _connection.Disconnect();
            _detected = false;
            _ffbInitSent = false;
        }

        /// <summary>
        /// Mark the AB9 as detected (i.e. a recognisable response landed on this
        /// pipe). Latched true; reset only via <see cref="Disconnect"/> or
        /// <see cref="Dispose"/>. Logging happens on the rising edge so reconnects
        /// don't spam the log.
        /// </summary>
        public void MarkDetected()
        {
            if (_detected) return;
            _detected = true;
            MozaLog.Debug("[AZOM/AB9] AB9 active shifter detected");
        }

        /// <summary>
        /// Send the PitHouse-style identity probe sequence (0x09 / 0x02 / 0x06 /
        /// 0x08 / 0x11) targeted at the AB9's main device id 0x12. Mirrors the
        /// wheelbase identity handshake — captured frames show PitHouse running
        /// the same probes against the AB9 on connect.
        /// </summary>
        public void SendIdentityProbe()
        {
            if (!_connection.IsConnected) return;
            // Full cascade as PitHouse sends it (sim/logs/ab9-game-20260513.jsonl
            // t=0.002, t=0.034). The previous shorter cascade missed groups 04,
            // 05, 07, 0F, 10 — any of those responses could have been the first
            // structurally-matching ab9-* parse for detection.
            SendRawProbe(0x09, null);
            SendRawProbe(0x04, new byte[] { 0x00, 0x00, 0x00, 0x00 });
            SendRawProbe(0x06, null);
            SendRawProbe(0x02, new byte[] { 0x00 });
            SendRawProbe(0x05, new byte[] { 0x00, 0x00, 0x00, 0x00 });
            SendRawProbe(0x07, new byte[] { 0x01 });
            SendRawProbe(0x0F, new byte[] { 0x01 });
            SendRawProbe(0x11, new byte[] { 0x04 });
            SendRawProbe(0x08, new byte[] { 0x01 });
            SendRawProbe(0x10, new byte[] { 0x00 });
        }

        private void SendRawProbe(byte group, byte[]? payload)
        {
            int payloadLen = payload?.Length ?? 0;
            var frame = new byte[4 + payloadLen + 1];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)payloadLen;
            frame[2] = group;
            frame[3] = MozaProtocol.DeviceAb9;
            if (payload != null)
                System.Buffer.BlockCopy(payload, 0, frame, 4, payloadLen);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            _connection.Send(frame);
        }

        /// <summary>
        /// Push the mechanical layout (5+R / 6+R / 7+R / Sequential) to the AB9.
        /// Single 8-byte CDC frame on group 0x1F with cmdId D3 00 + mode byte.
        /// </summary>
        public bool SendMode(Ab9Mode mode)
        {
            return WriteSliderRaw("ab9-mode", (byte)mode);
        }

        /// <summary>
        /// Push a slider value (0..100, clamped) to the AB9. Returns false if
        /// the connection is dead or the slider is not in the command database.
        /// </summary>
        public bool SendSlider(Ab9Slider slider, int value0to100)
        {
            if (!SliderCommands.TryGetValue(slider, out var commandName))
                return false;
            byte clamped = (byte)Math.Max(0, Math.Min(100, value0to100));
            return WriteSliderRaw(commandName, clamped);
        }

        /// <summary>
        /// Issue a read for every stored slider so the panel can populate from
        /// device state. Each read goes out as a separate one-shot frame and
        /// shares the connection's 4 ms pacing with the identity probe.
        /// </summary>
        public void RequestAllStoredSettings()
        {
            if (!_connection.IsConnected) return;
            // Reads use group 0x1E with a single-byte cmd-id payload — distinct
            // from the 0x1F + 3-byte-payload write format. See ab9-shifter.md
            // "Read group is 0x1E" section. PitHouse polls these continuously at
            // ~66 Hz throughout a session; the plugin only needs the initial
            // burst to populate the UI.
            SendAb9Read(0xD3); // mode
            SendAb9Read(0xD6); // mech-resistance
            SendAb9Read(0xAF); // spring
            SendAb9Read(0xB0); // natural-damping
            SendAb9Read(0xB2); // natural-friction
            SendAb9Read(0xA9); // max-torque-limit
            SendAb9Read(0xD4); // status-d4
            SendAb9Read(0x5D); // status-5d
            SendAb9Read(0xD7); // shifter-x analog
            SendAb9Read(0xD8); // shifter-y analog
        }

        private void SendAb9Read(byte cmdId)
        {
            if (!_connection.IsConnected) return;
            // Wire: 7E 01 1E 12 <cmdId> <chk>
            var frame = new byte[6];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = 0x01;
            frame[2] = 0x1E;
            frame[3] = MozaProtocol.DeviceAb9;
            frame[4] = cmdId;
            frame[5] = MozaProtocol.CalculateWireChecksum(frame, 5);
            _connection.Send(frame);
        }

        private bool WriteSliderRaw(string commandName, byte value)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteMessage(MozaProtocol.DeviceAb9, new byte[] { value });
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        // ===== FFB init handshake (Group 0x20 / cmds 0x0E, 0x07, 0x13) =====
        //
        // PitHouse sends this once at session connect before any FFB streaming.
        // Exact byte-for-byte order from ab9-game-20260513.jsonl (t_rel 0.082..0.115):
        //   0e02            ffb-init type 2
        //   0e01            ffb-init type 1
        //   0703            alloc effect type 0x03 (sim returns slot idx 0x01)
        //   0709            alloc effect type 0x09 (sim returns slot idx 0x02)
        //   0709            alloc effect type 0x09 (sim returns slot idx 0x03)
        //   0701            alloc effect type 0x01 (sim returns slot idx 0x04)
        //   0704            alloc effect type 0x04 (sim returns slot idx 0x05)
        //   0701            alloc effect type 0x01 (sim returns slot idx 0x06)
        //   130000          commit (mask 0x0000)
        //
        // The 1-byte slot indices returned by 0x07 ACKs are device-side internal
        // handles and are NOT the same as the 16-bit slot IDs used in 0x0A 0x05
        // streaming frames (those are runtime Windows DirectInput effect handles).
        // The plugin uses a fixed slot table on the streaming side and ignores
        // the 0x07 response indices.

        private static readonly byte[] FfbAllocSequence = new byte[]
        {
            0x03, 0x09, 0x09, 0x01, 0x04, 0x01,
        };

        /// <summary>
        /// Emit the session-start FFB handshake exactly as PitHouse does. Idempotent
        /// per connection — re-sending it would re-allocate slots on the device
        /// side, so the plugin only fires it once per detect event (reset on
        /// disconnect).
        /// </summary>
        public void SendFfbInitSequence()
        {
            if (!_connection.IsConnected) return;
            if (_ffbInitSent) return;
            _ffbInitSent = true;

            SendFfbControl(new byte[] { 0x0E, 0x02 });
            SendFfbControl(new byte[] { 0x0E, 0x01 });
            foreach (var effectType in FfbAllocSequence)
                SendFfbControl(new byte[] { 0x07, effectType });
            SendFfbControl(new byte[] { 0x13, 0x00, 0x00 });
            MozaLog.Debug("[AZOM/AB9] Sent FFB init handshake (0e02/0e01/07×6/13)");
        }

        private void SendFfbControl(byte[] payload)
        {
            // Wire: 7E <len> 20 12 <payload...> <chk>
            var frame = new byte[5 + payload.Length];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)payload.Length;
            frame[2] = 0x20;
            frame[3] = MozaProtocol.DeviceAb9;
            System.Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            _connection.Send(frame);
        }

        // ===== Host-rendered engine vibration (Group 0x20 / cmd 0x0A 0x05) =====
        //
        // PitHouse streams this with a 24-bit BE period field. The freq slider
        // is the buzz frequency AT REDLINE, scaled by RPM fraction below it:
        //   audible = freqSlider × (rpm/maxRpm)
        //   period  = FreqTickHz × maxRpm / (engine_rpm × freqSlider),  FreqTickHz ≈ 6.366e7
        // (calibrated 2026-05-31 against ground-truth RPM telemetry — see
        // Ab9EngineVibrationWorker and docs/protocol/devices/ab9-shifter.md).
        //
        // The 16-bit field at payload offset 2-3 (historically mislabelled the
        // "slot ID" / DirectInput handle) is the ENGINE-VIBRATION INTENSITY,
        // encoded LINEARLY:
        //   field = round(intensity_percent × 65.5)   (100% → 0x1996 = 6550)
        // Verified directly: with the PitHouse intensity slider stepped
        // 100/60/40 %, the field held 0x1996 / 0x0F5A / 0x0A3C exactly while
        // RPM swept idle→redline, and slider-drag intermediates (96/56/52/48/44 %)
        // all landed on intensity×65.5. The earlier "DirectInput handle that
        // toggles 0x1996↔0x0000" reading was an artefact of only ever observing
        // 0 % and 100 %. Intensity 0 → field 0 (silent). See
        // docs/protocol/devices/ab9-shifter.md and tools/ab9-rpm-correlate.
        //
        // Layout of the 24-byte wire frame:
        //   7E 13 20 12 0A 05 [int_hi int_lo] [00 × 7]
        //                     [per_hi per_mid per_lo] 04 [00 × 4] [cksum]
        // Length byte 0x13 = 19 = cmd-id(2) + intensity(2) + 7-zero + period(3)
        //                       + tag(1) + 4-zero.

        // Intensity field value at 100 % (= 0x1996). Linearly scaled by the
        // user's engine-vibration intensity slider; 0 % → 0 (silent).
        public const ushort EngineVibIntensityFullScale = 0x1996; // 6550
        public const uint MinPeriodTicks = 0x64;
        public const uint MaxPeriodTicks = 0xFFFFFF;

        /// <summary>
        /// Push one frame of the engine-vibration stream. The 16-bit intensity
        /// field carries <paramref name="intensity0to100"/> scaled linearly
        /// (round(intensity/100 × 0x1996)); 0 means silent. The frame goes
        /// through the latest-wins stream lane so worker stalls never pile stale
        /// frames on the wire.
        /// </summary>
        public bool SendEngineVibrationStream(int intensity0to100, uint periodTicks)
        {
            if (!_connection.IsConnected) return false;
            if (intensity0to100 < 0) intensity0to100 = 0;
            if (intensity0to100 > 100) intensity0to100 = 100;
            ushort slot = (ushort)Math.Round(intensity0to100 / 100.0 * EngineVibIntensityFullScale);
            if (periodTicks < MinPeriodTicks) periodTicks = MinPeriodTicks;
            if (periodTicks > MaxPeriodTicks) periodTicks = MaxPeriodTicks;

            var frame = new byte[24];
            frame[0] = MozaProtocol.MessageStart; // 0x7E
            frame[1] = 0x13;                      // length = cmd(2) + payload(17)
            frame[2] = 0x20;                      // group: FFB
            frame[3] = MozaProtocol.DeviceAb9;    // dev id 0x12
            frame[4] = 0x0A;                      // cmd hi
            frame[5] = 0x05;                      // cmd lo: streaming refresh
            frame[6] = (byte)(slot >> 8);
            frame[7] = (byte)(slot & 0xFF);
            // frame[8..14] left zero (already zero-initialised)
            frame[15] = (byte)((periodTicks >> 16) & 0xFF);
            frame[16] = (byte)((periodTicks >> 8) & 0xFF);
            frame[17] = (byte)(periodTicks & 0xFF);
            frame[18] = 0x04;                     // type tag
            // frame[19..22] trailing zeros
            frame[23] = MozaProtocol.CalculateWireChecksum(frame, 23);
            _connection.SendStream(StreamKind.Ab9EngineVibration, frame);
            return true;
        }

        /// <summary>
        /// Emit a short burst of silence frames to stop the engine-vib effect
        /// immediately when SimHub is shutting down. Without this the AB9
        /// firmware keeps the last-streamed effect running until its ~10 s
        /// keepalive timeout. Sends ~9 silent-slot vib-stream frames (one
        /// 100 ms window at the running 91 Hz cadence) plus one engine-pulse
        /// OFF to flush any in-flight pulse half-cycle. Caller is responsible
        /// for stopping the engine-vib worker first so it can't race with us.
        /// </summary>
        public void SendEngineSilence()
        {
            if (!_connection.IsConnected) return;
            const int silentFrameCount = 9;
            for (int i = 0; i < silentFrameCount; i++)
            {
                SendEngineVibrationStream(intensity0to100: 0, periodTicks: 0x100000);
            }
            // Engine-pulse OFF half (amp16 = 0) flushes any active pulse.
            SendEnginePulsePair(_lastEnginePulsePhase, intensity0to100: 0);
        }

        private ushort _lastEnginePulsePhase;

        // ===== Engine-pulse pair (Group 0x20 / cmd 0x0B 0x02 + 0x0B 0x03) =====
        //
        // 22-byte frames emitted as paired ON/OFF half-cycles. The 16-bit phase
        // counter at offset 6-7 (duplicated at 8-9, the device cross-checks)
        // advances per emitted pair. Amplitude16 at offset 19-20 is intensity-
        // derived for ON, zero for OFF. Constants `ff fa` at offset 16-17 and
        // `04` tag at offset 18 verbatim from PitHouse.
        //
        // The doc's "engine vibration intensity" slider drives the ON amplitude;
        // PitHouse drove `0x2328` (9000 decimal) at ~68 % captured intensity.

        // Match PitHouse's observed full-intensity amplitude. Linear-scale 0..100
        // from the user's "engine vibration intensity" slider through this value.
        private const ushort EnginePulseAmpFullScale = 0x2328;

        /// <summary>
        /// Push an engine-pulse ON/OFF pair. The pair shares <paramref name="phase"/>
        /// — the device pairs the two frames by phase value, not by arrival order.
        /// <paramref name="intensity0to100"/> drives the ON amplitude; OFF is always zero.
        /// </summary>
        public bool SendEnginePulsePair(ushort phase, int intensity0to100)
        {
            if (!_connection.IsConnected) return false;
            if (intensity0to100 < 0) intensity0to100 = 0;
            if (intensity0to100 > 100) intensity0to100 = 100;
            ushort amp = (ushort)Math.Round(intensity0to100 / 100.0 * EnginePulseAmpFullScale);
            _lastEnginePulsePhase = phase;

            var on  = BuildEnginePulseFrame(0x02, phase, amp);
            var off = BuildEnginePulseFrame(0x03, phase, 0x0000);
            _connection.SendStream(StreamKind.Ab9EnginePulse, on);
            // OFF immediately follows on the wire because the lane is latest-wins:
            // queue the ON first so it goes out, then enqueue OFF to overwrite the
            // slot for the next drain pass. Result: ON, OFF land back-to-back in
            // the right order matching PitHouse's pair cadence.
            _connection.Send(off);
            return true;
        }

        private static byte[] BuildEnginePulseFrame(byte subLo, ushort phase, ushort amplitude)
        {
            // Wire: 7E 16 20 12 0B XX 00 00 00 [ph ph][ph ph] 00×6 FF FA 04 [amp_hi amp_lo] 00 00
            //       len 0x16 = 22 = payload (2 sub-cmd + 20 fixed)
            //
            // Layout verified byte-for-byte against PitHouse capture
            // (sim/logs/ab9-game-20260513.jsonl, 17,603 pulse-on frames):
            //
            //   payload[0..1]   = sub-cmd (0B 02/03)
            //   payload[2..4]   = 3 zero pad   ← was 4 before 2026-05-24 fix
            //   payload[5..6]   = phase counter (16-bit BE)
            //   payload[7..8]   = phase counter mirror (same value)
            //   payload[9..14]  = 6 zero pad
            //   payload[15..16] = FF FA (const)
            //   payload[17]     = 04 (tag)
            //   payload[18..19] = amp16 (0x2328 on, 0x0000 off)
            //   payload[20..21] = 2 zero trailing
            //
            // Pre-fix the layout was shifted right by 1 byte: phase landed at
            // payload[6..7], amp16 at payload[19..20]. The device firmware
            // reads amp16 from PitHouse's offset (18..19), so our amp_hi
            // landed where the tag byte should be and our tag byte landed in
            // the amp16 high slot — capping effective amp16 in the
            // 0x0400..0x0423 range regardless of slider value. That was the
            // root cause of the binary engine-vib-intensity report.
            var frame = new byte[27];
            frame[0]  = MozaProtocol.MessageStart;
            frame[1]  = 0x16;
            frame[2]  = 0x20;
            frame[3]  = MozaProtocol.DeviceAb9;
            frame[4]  = 0x0B;
            frame[5]  = subLo;
            // frame[6..8] zero (3 bytes — payload[2..4])
            frame[9]  = (byte)(phase >> 8);
            frame[10] = (byte)(phase & 0xFF);
            frame[11] = (byte)(phase >> 8);
            frame[12] = (byte)(phase & 0xFF);
            // frame[13..18] zero (6 bytes — payload[9..14])
            frame[19] = 0xFF;
            frame[20] = 0xFA;
            frame[21] = 0x04;
            frame[22] = (byte)(amplitude >> 8);
            frame[23] = (byte)(amplitude & 0xFF);
            // frame[24..25] zero (2 bytes trailing — payload[20..21])
            frame[26] = MozaProtocol.CalculateWireChecksum(frame, 26);
            return frame;
        }

        // ===== Low-rate signed pair (Group 0x20 / cmd 0x08 0x04 + 0x08 0x06) =====
        //
        // 11-byte frames carrying an engine-cycle phase signal as a signed-16 pair:
        //   0x08 0x04 has -magnitude, 0x08 0x06 has +magnitude.
        // Constants `00 64 04` at offset 4-6 (envelope amp 100, tag 0x04). Sparse:
        // PitHouse fired ~0.35 Hz across the session.

        /// <summary>
        /// Push the signed-magnitude low-rate pair. The plugin's caller drives this
        /// from an engine-cycle phase accumulator (advances per engine cycle).
        /// </summary>
        public bool SendLowRatePair(short magnitude)
        {
            if (!_connection.IsConnected) return false;
            var neg = BuildLowRateFrame(0x04, (short)(-magnitude));
            var pos = BuildLowRateFrame(0x06, magnitude);
            _connection.SendStream(StreamKind.Ab9LowRate, neg);
            _connection.Send(pos);
            return true;
        }

        private static byte[] BuildLowRateFrame(byte subLo, short magnitude)
        {
            // Wire: 7E 0B 20 12 08 XX [mag_hi mag_lo] 00 64 04 00 00 00 00 <chk>
            //       len 0x0B = 11 = payload (2 sub-cmd + 9)
            var frame = new byte[16];
            frame[0]  = MozaProtocol.MessageStart;
            frame[1]  = 0x0B;
            frame[2]  = 0x20;
            frame[3]  = MozaProtocol.DeviceAb9;
            frame[4]  = 0x08;
            frame[5]  = subLo;
            frame[6]  = (byte)((ushort)magnitude >> 8);
            frame[7]  = (byte)((ushort)magnitude & 0xFF);
            frame[8]  = 0x00;
            frame[9]  = 0x64;
            frame[10] = 0x04;
            // frame[11..14] zero
            frame[15] = MozaProtocol.CalculateWireChecksum(frame, 15);
            return frame;
        }

        // ===== Trigger sub-cmds (Group 0x20 / cmd 0x0D 0x01..0x06) =====
        //
        // 3-byte frames with a single payload byte (always 0x01 observed).
        //   0x0D 0x01 (Sparse / Shift) — fires alongside per-shift events.
        //              At idle (no shifts) appears at ~0.10 Hz; during active
        //              gear cycling jumps to ~1.2 Hz. See usb-capture/AB9/
        //              all_gears.pcapng + 1-N.pcapng.
        //   0x0D 0x02 + 0x0D 0x03 — paired flat-rate keepalive (~9 Hz).
        //   0x0D 0x04 (Engage)     — per-shift trigger when entering any non-
        //                              neutral gear. Observed only during gear
        //                              cycling; ACKed by device with standard
        //                              0xA0 generic FFB ACK.
        //   0x0D 0x05 (RpmTrack)   — RPM-tracking trigger (1.3..32 Hz with state).
        //   0x0D 0x06 (Disengage)  — per-shift trigger when entering neutral.
        //                              Same shape as Engage.

        public enum Ab9Trigger : byte
        {
            // Values are the sub-cmd lo byte on the wire.
            Sparse     = 0x01,
            KeepaliveA = 0x02,
            KeepaliveB = 0x03,
            Engage     = 0x04,
            RpmTrack   = 0x05,
            Disengage  = 0x06,
        }

        public bool SendTrigger(Ab9Trigger trigger)
        {
            if (!_connection.IsConnected) return false;
            // Wire: 7E 03 20 12 0D XX 01 <chk>
            var frame = new byte[8];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = 0x03;
            frame[2] = 0x20;
            frame[3] = MozaProtocol.DeviceAb9;
            frame[4] = 0x0D;
            frame[5] = (byte)trigger;
            frame[6] = 0x01;
            frame[7] = MozaProtocol.CalculateWireChecksum(frame, 7);

            // Route each trigger to its own lane so back-to-back same-kind pushes
            // don't coalesce, while still letting stale ones drop if the worker
            // falls behind. Per-shift events (Sparse/Engage/Disengage) bypass
            // the latest-wins stream lane entirely — they're event-driven and
            // must not drop or coalesce.
            switch (trigger)
            {
                case Ab9Trigger.KeepaliveA:
                case Ab9Trigger.KeepaliveB:
                    _connection.SendStream(StreamKind.Ab9TriggerA, frame);
                    break;
                case Ab9Trigger.RpmTrack:
                    _connection.SendStream(StreamKind.Ab9TriggerRpm, frame);
                    break;
                case Ab9Trigger.Sparse:
                case Ab9Trigger.Engage:
                case Ab9Trigger.Disengage:
                    _connection.Send(frame);
                    break;
                default:
                    _connection.SendStream(StreamKind.Ab9TriggerExtra, frame);
                    break;
            }
            return true;
        }

        /// <summary>
        /// Fire the per-shift trigger pair PitHouse emits on each gear change.
        /// Sends 0x0D 0x01 (Sparse) immediately followed by either 0x0D 0x04
        /// (engage) or 0x0D 0x06 (disengage / into neutral). Both frames go
        /// through the one-shot FIFO so they can't drop or reorder against
        /// the engine-vib stream lanes. The AB9 firmware fires its stored
        /// rumble pattern on the trigger; without it, gear engagements produce
        /// no haptic feedback (verified empirically by the user 2026-05-24 and
        /// in usb-capture/AB9/all_gears.pcapng / 1-N.pcapng).
        /// </summary>
        public bool SendGearShiftTrigger(bool engageNotDisengage)
        {
            if (!_connection.IsConnected) return false;
            SendTrigger(Ab9Trigger.Sparse);
            SendTrigger(engageNotDisengage ? Ab9Trigger.Engage : Ab9Trigger.Disengage);
            return true;
        }

        /// <summary>
        /// Emit the paired keepalive triggers (0x0D 0x02 + 0x0D 0x03). PitHouse
        /// fires them within sub-ms of each other; we queue both into the same
        /// lane so the second naturally follows the first onto the wire.
        /// </summary>
        public bool SendKeepalivePair()
        {
            if (!_connection.IsConnected) return false;
            SendTrigger(Ab9Trigger.KeepaliveA);
            // Second keepalive uses the one-shot FIFO so it can't be overwritten
            // by another KeepaliveA before it reaches the wire.
            var b = new byte[8];
            b[0] = MozaProtocol.MessageStart;
            b[1] = 0x03;
            b[2] = 0x20;
            b[3] = MozaProtocol.DeviceAb9;
            b[4] = 0x0D;
            b[5] = (byte)Ab9Trigger.KeepaliveB;
            b[6] = 0x01;
            b[7] = MozaProtocol.CalculateWireChecksum(b, 7);
            _connection.Send(b);
            return true;
        }

        // ===== Gear-shift vibration intensity config (Group 0x20 / cmd 0x0A 0x01) =====
        //
        // One-shot push on slider change. AB9 firmware persists the value and
        // fires the rumble pattern itself on every HID-detected gear shift —
        // no per-shift host trigger needed (see ab9-shifter.md).
        //
        // Layout of the 24-byte wire frame:
        //   7E 13 20 12 0A 01 [int_hi int_lo] [00 × 7]
        //                     [0E 00 64 04] [00 × 4] [cksum]
        // Intensity is BE 16-bit, linearly scaled from 0..100 to 0..0x332C
        // (verified: 30% = 0x0F5A, 100% = 0x332C).
        private const ushort MaxGearShiftIntensityRaw = 0x332C;

        /// <summary>
        /// Push the stored gear-shift-vibration intensity (0..100) to the AB9.
        /// Goes through the one-shot FIFO so it preserves order against the
        /// other slider writes that follow in <c>ApplySavedAb9Settings</c>.
        /// </summary>
        public bool SendGearShiftVibrationIntensity(int intensity0to100)
        {
            if (!_connection.IsConnected) return false;
            if (intensity0to100 < 0) intensity0to100 = 0;
            if (intensity0to100 > 100) intensity0to100 = 100;
            ushort raw = (ushort)Math.Round(intensity0to100 / 100.0 * MaxGearShiftIntensityRaw);

            var frame = new byte[24];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = 0x13;
            frame[2] = 0x20;
            frame[3] = MozaProtocol.DeviceAb9;
            frame[4] = 0x0A;
            frame[5] = 0x01;
            frame[6] = (byte)(raw >> 8);
            frame[7] = (byte)(raw & 0xFF);
            // frame[8..14] zero
            frame[15] = 0x0E;
            frame[16] = 0x00;
            frame[17] = 0x64;
            frame[18] = 0x04;
            // frame[19..22] zero
            frame[23] = MozaProtocol.CalculateWireChecksum(frame, 23);
            _connection.Send(frame);
            return true;
        }

        public void Dispose()
        {
            _connection.Disconnected -= OnConnectionDisconnected;
            _detected = false;
            _ffbInitSent = false;
            _connection.Dispose();
        }
    }
}
