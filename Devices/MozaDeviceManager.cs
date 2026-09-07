using System;
using System.Threading;
using System.Threading.Tasks;
using MozaPlugin.Protocol;


namespace MozaPlugin.Devices
{
    /// <summary>
    /// Handles reading and writing settings to Moza devices.
    /// Includes wheel ID cycling to support different wheel models (23, 21, 19).
    /// </summary>
    public class MozaDeviceManager : IDisposable
    {
        private readonly MozaSerialConnection _connection;
        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();

        // Retransmit tracker for tracked reads. EVERY pipe passes its own tracker
        // at construction: a read must be re-emitted on the connection it was sent
        // on, so the hub's reads never go out on the base port and vice versa. The
        // primary passes MozaPlugin.PendingResponses (the same instance
        // OnMessageReceived calls NoteResponse on, and the retry timer ticks).
        //
        // Deliberately NOT resolved lazily through MozaPlugin.Instance. That static
        // is published at the END of Init while the reconnect + poll timers are
        // started ~150 lines earlier, so a connect that lands before Init returns
        // reaches this class with Instance still null — and Track() is a silent
        // no-op, for the one burst that issues every identity read. Reads sent then
        // are never retried and never sunset, so a single dropped reply can disable
        // a firmware-gated feature for the rest of the session with nothing in the
        // log to say so. An injected tracker cannot have that window.
        private readonly PendingResponseTracker? _pendingResponses;
        private PendingResponseTracker? Tracker => _pendingResponses;

        // When set, every command on this pipe targets this device id regardless
        // of the command's DeviceType. Used by a dedicated standalone-peripheral
        // pipe: a pedal set / handbrake plugged STRAIGHT into the PC is the root
        // ("main", 0x12) device on its OWN CDC connection, NOT the bus sub-device
        // id (pedals 0x19 / handbrake 0x1B) it would carry through a base/hub.
        // Mirrors the AB9, which is likewise a single internal device at 0x12 on
        // its own pipe. Null → normal per-DeviceType addressing.
        private readonly byte? _deviceIdOverride;

        /// <summary>
        /// The retransmit tracker this manager's reads are recorded against.
        /// The message handler calls <c>NoteResponse</c> on it when a response
        /// arrives on this manager's pipe, so acks clear retransmits on the
        /// correct connection. Null only if the global singleton is unavailable.
        /// </summary>
        public PendingResponseTracker? PendingResponses => Tracker;

        // Wheel device ID detection
        // ES wheels may be on ID 21 instead of 23; R5 ES wheels share base ID 19
        private volatile byte _wheelDeviceId = MozaProtocol.DeviceWheel; // starts at 23
        private volatile bool _wheelDetected;
        private volatile bool _wheelRespondedSinceLastPoll;

        public byte WheelDeviceId => _wheelDeviceId;
        public bool WheelRespondedSinceLastPoll => _wheelRespondedSinceLastPoll;

        /// <summary>
        /// Reset wheel detection state so ProbeWheelDetection() will probe again.
        /// Call when the serial connection is intentionally disconnected.
        /// </summary>
        public void ResetWheelDetection()
        {
            _wheelDetected = false;
            _wheelDeviceId = MozaProtocol.DeviceWheel;
            _wheelRespondedSinceLastPoll = false;
        }

        public void MarkWheelResponse(byte deviceId)
        {
            // Match against the locked wheel ID once we have one; before lock,
            // match any candidate ID so probe-time responses still count as
            // "wheel is alive". The earlier _wheelDetected gate caused a race
            // during SimHub plugin reload: the persistent DetectionState says
            // the wheel is detected (and PollStatus's miss check is therefore
            // armed), but this fresh MozaDeviceManager has _wheelDetected=false
            // until ProbeWheelDetection completes, so wheel responses were
            // ignored and the miss counter ran to threshold for no good reason.
            if (_wheelDetected)
            {
                if (deviceId == _wheelDeviceId)
                    _wheelRespondedSinceLastPoll = true;
            }
            else
            {
                for (int i = 0; i < WheelIdCandidates.Length; i++)
                {
                    if (deviceId == WheelIdCandidates[i])
                    {
                        _wheelRespondedSinceLastPoll = true;
                        break;
                    }
                }
            }
        }

        /// <summary>Unconditional "the wheel is present" mark, independent of the
        /// locked wheel id. For evidence that identifies the wheel by itself —
        /// e.g. an unsolicited firmware-debug log on the wheel's log channel
        /// (dev 0x71) — where routing through <see cref="MarkWheelResponse"/>'s
        /// id match would wrongly drop it for a wheel locked on 21/19 instead of
        /// 23. Keeps the hot-swap poll-miss watchdog from re-detecting a wheel
        /// that is demonstrably alive but has stopped answering a specific poll.</summary>
        public void MarkWheelAlive()
        {
            _wheelRespondedSinceLastPoll = true;
        }

        public void ResetWheelResponseFlag()
        {
            _wheelRespondedSinceLastPoll = false;
        }

        public MozaDeviceManager(MozaSerialConnection connection,
                                 PendingResponseTracker pendingResponses,
                                 byte? deviceIdOverride = null)
        {
            _connection = connection;
            _pendingResponses = pendingResponses;
            _deviceIdOverride = deviceIdOverride;
        }

        /// <summary>True while the underlying serial pipe is open.</summary>
        public bool IsConnected => _connection.IsConnected;

        /// <summary>The underlying serial pipe — shared with ROUTED sub-device
        /// lanes (e.g. an mBooster on this pipe's pedal port, dev 0x19), which
        /// send/subscribe on it but never manage its lifecycle.</summary>
        public MozaSerialConnection Connection => _connection;

        // Valid wheel device IDs to try: 0x17 (DeviceWheel), 0x15 (DeviceWheel15)
        // and 0x13 (DeviceBase, the old-protocol / ES bus). 0x15 and 0x17 are two
        // of the three wheel-identity ids in docs/how-to-query-device-type.md.
        private static readonly byte[] WheelIdCandidates =
            { MozaProtocol.DeviceWheel, MozaProtocol.DeviceWheel15, MozaProtocol.DeviceBase };

        /// <summary>
        /// Send the PitHouse-style wheel identity probe sequence that the existing
        /// ReadSetting calls don't cover. PitHouse fires 12 identity frames on connect;
        /// "wheel-model-name"/"wheel-sw-version"/"wheel-hw-version"/"wheel-serial-a"/
        /// "wheel-serial-b" account for 5 of them. This method sends the remaining 7
        /// so the wheel sees the full PitHouse init handshake.
        /// Groups: 0x09 presence, 0x02 device-presence, 0x04 device-type,
        ///         0x05 capabilities, 0x06 hardware-id, 0x08 HW sub-version, 0x11 identity-11.
        /// </summary>
        public void SendPithouseIdentityProbe(byte deviceId)
        {
            if (!_connection.IsConnected) return;
            SendRawProbe(0x09, deviceId, null);                                   // presence/ready
            SendRawProbe(0x02, deviceId, null);                                   // device presence
            SendRawProbe(0x04, deviceId, new byte[] { 0x00, 0x00, 0x00, 0x00 }); // device type
            SendRawProbe(0x05, deviceId, new byte[] { 0x00, 0x00, 0x00, 0x00 }); // capability flags
            SendRawProbe(0x06, deviceId, null);                                   // hardware ID
            SendRawProbe(0x08, deviceId, new byte[] { 0x02 });                   // HW sub-version
            SendRawProbe(0x11, deviceId, new byte[] { 0x04 });                   // identity-11
        }

        /// <summary>Model-name (grp 0x07 cmd 01) + hw-version (grp 0x08 cmd 01) reads at an
        /// explicit device id — the two identity groups <see cref="SendPithouseIdentityProbe"/>
        /// doesn't cover. UNTRACKED on purpose: this is an exploratory probe at a relayed
        /// sub-device that may not implement the groups at all, and a tracked read retries
        /// for the life of the connection (<c>ReadRetryMaxAttempts</c> is int.MaxValue), so
        /// a silent device would carry a permanent unanswered-pending entry.</summary>
        public void SendNameIdentityProbe(byte deviceId)
        {
            if (!_connection.IsConnected) return;
            SendRawProbe(0x07, deviceId, new byte[] { 0x01 });   // model name
            SendRawProbe(0x08, deviceId, new byte[] { 0x01 });   // hardware version
        }

        /// <summary>
        /// Probe the wheel's Display sub-device via the group 0x43 wrapper (same
        /// frames PitHouse sends, mirrored from <see cref="Telemetry.TelemetrySender.SendDisplayProbe"/>).
        /// Responses arrive as 0xC3 / 0x71 frames and are decoded by
        /// <see cref="Protocol.MozaResponseParser.ParseDisplayIdentity"/> →
        /// <see cref="MozaData"/> (display-* command names). Runs at wheel
        /// detect so <see cref="MozaPlugin.IsDisplayDetected"/> flips independent
        /// of telemetry start — required because the UI gates the dashboard-telemetry
        /// section on detection, and the user can't pick a profile until that
        /// section is visible.
        /// </summary>
        public void SendDisplayProbe() => SendDisplayProbe(MozaProtocol.DeviceWheel);

        /// <summary>Identity cascade aimed at an explicit device id (0x12 for a
        /// CM2 wired through the wheelbase; 0x17 for a wheel-hosted display).</summary>
        public void SendDisplayProbe(byte dev)
        {
            if (!_connection.IsConnected) return;
            byte g = MozaProtocol.TelemetrySendGroup; // 0x43
            // Heartbeat
            SendRawProbe(g, dev, new byte[] { 0x00 });
            // Identity cascade
            SendRawProbe(g, dev, new byte[] { 0x09 });
            SendRawProbe(g, dev, new byte[] { 0x04, 0x00, 0x00, 0x00, 0x00 });
            SendRawProbe(g, dev, new byte[] { 0x06 });
            SendRawProbe(g, dev, new byte[] { 0x02, 0x00 });
            SendRawProbe(g, dev, new byte[] { 0x05, 0x00, 0x00, 0x00, 0x00 });
            // Version queries
            SendRawProbe(g, dev, new byte[] { 0x07, 0x01 });
            SendRawProbe(g, dev, new byte[] { 0x0F, 0x01 });
            SendRawProbe(g, dev, new byte[] { 0x11, 0x04 });
            SendRawProbe(g, dev, new byte[] { 0x08, 0x01 });
            SendRawProbe(g, dev, new byte[] { 0x10, 0x00 });
        }

        /// <summary>
        /// Zero-length form of the numeric base-firmware query
        /// (<c>7E 00 04 12 CK</c>). The tracked <c>base-fw-version</c> read sends
        /// the 4-zero-byte form PitHouse uses; some bases answer group 0x04 only in
        /// the short form (it is the shape a relayed pedal set / shifter answers).
        /// Untracked — a reply lands on the tracked read's name and clears it.
        /// </summary>
        public void SendBaseFwVersionShortProbe()
        {
            if (!_connection.IsConnected) return;
            SendRawProbe(0x04, GetDeviceId("main"), null);
        }

        private void SendRawProbe(byte group, byte deviceId, byte[]? payload)
        {
            int payloadLen = payload?.Length ?? 0;
            var frame = new byte[4 + payloadLen + 1];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)payloadLen;
            frame[2] = group;
            frame[3] = deviceId;
            if (payload != null)
                System.Buffer.BlockCopy(payload, 0, frame, 4, payloadLen);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            _connection.Send(frame);
        }

        /// <summary>
        /// PitHouse-style empty presence probe: <c>7e 00 00 deviceId chk</c>. The
        /// device responds with <c>7e 00 80 (deviceId&lt;&lt;swap) chk</c> if
        /// alive. Cheap (single 5-byte frame) and NOT tracked by
        /// <see cref="MozaPlugin.PendingResponses"/> — absent devices don't
        /// burn the 3-attempt retry budget every PollStatus tick.
        ///
        /// Used for sub-device presence detection (dash / handbrake / pedals)
        /// where the prior approach (re-issuing the first settings read every
        /// 5 s) generated 3 retry frames per absent device per tick — the bulk
        /// of the steady-state wire noise in single-base setups. The wheel +
        /// base are detected via their cmd-specific responses and don't go
        /// through this path.
        /// </summary>
        public void SendPresenceProbe(byte deviceId)
        {
            if (!_connection.IsConnected) return;
            SendRawProbe(0x00, deviceId, null);
        }

        /// <summary>
        /// Group-0x43 1-byte keepalive to the locked wheel
        /// (<c>7e 01 43 &lt;wheelDeviceId&gt; 00 chk</c>), the ~1 Hz keepalive
        /// PitHouse streams to hold a wheel's session subsystem up at idle.
        /// PitHouse sends this exact frame to a SCREENLESS wheel (verified on the
        /// R5 capture: 136× to 0x17, wheel stays healthy), so it is NOT gated on
        /// display capability — the documented screenless hazard is the 11-frame
        /// display PROBE (<see cref="SendDisplayProbe()"/>), not this single-byte
        /// keepalive. The FSR1 path streams its own via
        /// <see cref="Telemetry.Display.Fsr1DisplayDriver"/>; callers exclude FSR1 to
        /// avoid a double-send.
        /// </summary>
        public void SendWheelKeepalive()
        {
            if (!_connection.IsConnected) return;
            SendRawProbe(MozaProtocol.TelemetrySendGroup, _wheelDeviceId, new byte[] { 0x00 });
        }

        /// <summary>
        /// CM1-vs-CM2 discriminator probe: a group-0x0E param-manager register
        /// read to the dash (dev 0x14), <c>7E 03 0E 14 00 00 01 chk</c>. A CM1
        /// answers with a group-0x8E reply (<c>7E 07 8E 41 …</c>); a tier-def CM2
        /// does not. Cheap (8-byte frame); used by
        /// <see cref="MozaPlugin.TickCm1Discriminator"/> as a fast positive CM1
        /// signal so we don't wait out the full no-catalog timeout. See
        /// docs/protocol/devices/dash-0x14.md § "Param-manager register reads".
        /// </summary>
        public void SendCm1ParamProbe()
        {
            if (!_connection.IsConnected) return;
            SendRawProbe(MozaProtocol.FirmwareDebugGroup, MozaProtocol.DeviceDash,
                new byte[] { 0x00, 0x00, 0x01 });
        }

        /// <summary>
        /// Send detection probes for all candidate wheel IDs simultaneously.
        /// Much faster than cycling through IDs one at a time (~2s vs ~12s worst case).
        /// </summary>
        public void ProbeWheelDetection()
        {
            if (_wheelDetected) return;

            foreach (var id in WheelIdCandidates)
            {
                ReadSettingForDevice("wheel-telemetry-mode", id);
                ReadSettingForDevice("wheel-rpm-value1", id);
            }

            // docs/how-to-query-device-type.md's canonical probe: the model-name
            // group (0x07). Catches a new-protocol wheel that answers the identity
            // group but not the telemetry-mode / rpm-value settings groups. Only
            // the new-protocol wheel ids (0x17, 0x15); the base (0x13) answers this
            // as base-model-name and ES (0x18) via es-wheel-model-name.
            ReadSettingForDevice("wheel-model-name", MozaProtocol.DeviceWheel);
            ReadSettingForDevice("wheel-model-name", MozaProtocol.DeviceWheel15);
        }

        /// <summary>
        /// Probe wheel IDs OTHER than the currently locked one.
        /// Used for hot-swap detection: if a new wheel attaches on a different ID
        /// (e.g., new-protocol wheel after ES wheel was on ID 19), it'll respond.
        /// </summary>
        public void ProbeOtherWheelIds()
        {
            if (!_wheelDetected) return;

            foreach (var id in WheelIdCandidates)
            {
                if (id == _wheelDeviceId) continue;
                ReadSettingForDevice("wheel-telemetry-mode", id);
                ReadSettingForDevice("wheel-rpm-value1", id);
            }
        }

        /// <summary>
        /// Lock the wheel device ID to the one that actually responded.
        /// Called when a wheel detection probe gets a valid response.
        /// </summary>
        public void LockWheelId(byte deviceId)
        {
            if (_wheelDetected) return;
            // Publish the new id BEFORE flipping the detected flag so any thread
            // observing _wheelDetected==true also sees the matching _wheelDeviceId.
            // (Both fields are volatile; the assignment order is preserved by
            // .NET's memory model so MarkWheelResponse won't see detected=true
            // paired with a stale id.)
            _wheelDeviceId = deviceId;
            _wheelDetected = true;
            MozaLog.Info($"[AZOM] Wheel locked on device ID {_wheelDeviceId}");
        }

        // Default backoff for tracked reads. Exponential growth caps at the
        // array's last value, which PendingResponseTracker reuses indefinitely:
        // {200, 400, 800, 1600, 3200, 6400, 10000} — fast catch of transient
        // drops in the first ~1.4 s, then graceful widening up to one retry
        // per 10 s. Entries are NOT dropped on attempt count; the tracker
        // re-emits forever until the wheel acks or the connection drops.
        // ReadRetryMaxAttempts is retained for API compatibility but ignored
        // by the tracker.
        private static readonly int[] ReadRetryBackoffMs =
            { 200, 400, 800, 1600, 3200, 6400, 10000 };
        private const int ReadRetryMaxAttempts = int.MaxValue;

        public bool ReadSettingForDevice(string commandName, byte deviceId)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildReadMessage(deviceId);
            if (msg == null) return false;
            _connection.Send(msg);
            Tracker?.Track(
                cmd.Name, msg, ReadRetryBackoffMs, ReadRetryMaxAttempts);
            return true;
        }

        public bool ReadSetting(string commandName)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildReadMessage(GetDeviceId(cmd.DeviceType));
            if (msg == null) return false;
            _connection.Send(msg);
            Tracker?.Track(
                cmd.Name, msg, ReadRetryBackoffMs, ReadRetryMaxAttempts);
            return true;
        }

        /// <summary>
        /// Send a settings read WITHOUT registering it with the retry tracker.
        /// For high-rate live polls (the live-torque sampler, 5 Hz): tracked
        /// reads are keyed by command name and retransmitted on a 200 ms-and-up
        /// backoff, so re-tracking one name faster than its own backoff piles
        /// retransmits on top of the poll. A dropped reply here should cost one
        /// graph sample, not start a retry storm — the next tick re-asks anyway.
        /// </summary>
        public bool ReadSettingUntracked(string commandName)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildReadMessage(GetDeviceId(cmd.DeviceType));
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        public bool WriteSetting(string commandName, int value)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteInt(GetDeviceId(cmd.DeviceType), value);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        public bool WriteFloat(string commandName, float value)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteFloat(GetDeviceId(cmd.DeviceType), value);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        public bool WriteArray(string commandName, byte[] payload)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteMessage(GetDeviceId(cmd.DeviceType), payload);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        public bool WriteColor(string commandName, byte r, byte g, byte b)
        {
            return WriteArray(commandName, new byte[] { r, g, b });
        }

        // ============================================================
        // Per-device-id override helpers. Used to retarget existing
        // commands at a different device (e.g. driving CM2's live RPM
        // LEDs via the wheel's `wheel-send-rpm-telemetry` /
        // `wheel-telemetry-rpm-colors` commands sent to dev=0x12 instead
        // of the wheel's default dev=0x17). Caller picks the deviceId
        // explicitly; <see cref="GetDeviceId"/> is bypassed.
        // ============================================================

        public bool WriteSettingForDevice(string commandName, byte deviceId, int value)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteInt(deviceId, value);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        public bool WriteArrayForDevice(string commandName, byte deviceId, byte[] payload)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteMessage(deviceId, payload);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        public bool WriteColorForDevice(string commandName, byte deviceId, byte r, byte g, byte b)
        {
            return WriteArrayForDevice(commandName, deviceId, new byte[] { r, g, b });
        }

        // ============================================================
        // Stream-lane variants. Identical frame construction to the Send
        // counterparts above, but enqueued via SendStream (latest-wins,
        // coalescing, unthrottled) instead of the paced/throttled one-shot
        // FIFO. Used for high-rate, idempotent LED writes so a co-resident
        // value stream on a shared bus can never starve them. The caller
        // supplies the absolute StreamKind slot (LED region). Only use for
        // end-state-idempotent writes — an intermediate frame may be
        // coalesced away; never for ordered/setup writes.
        // ============================================================

        public bool WriteSettingStream(string commandName, int value, StreamKind slot)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteInt(GetDeviceId(cmd.DeviceType), value);
            if (msg == null) return false;
            _connection.SendStream(slot, msg);
            return true;
        }

        public bool WriteArrayStream(string commandName, byte[] payload, StreamKind slot)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteMessage(GetDeviceId(cmd.DeviceType), payload);
            if (msg == null) return false;
            _connection.SendStream(slot, msg);
            return true;
        }

        public bool WriteSettingForDeviceStream(string commandName, byte deviceId, int value, StreamKind slot)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteInt(deviceId, value);
            if (msg == null) return false;
            _connection.SendStream(slot, msg);
            return true;
        }

        public bool WriteArrayForDeviceStream(string commandName, byte deviceId, byte[] payload, StreamKind slot)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteMessage(deviceId, payload);
            if (msg == null) return false;
            _connection.SendStream(slot, msg);
            return true;
        }

        // ============================================================
        // Wheelbase LFE (low-frequency effects) host-rendered streams —
        // cmd 0x2D/0x77 on the base (dev 0x13). Engine/ABS ride their own
        // latest-wins stream lanes; the discrete gearshift burst and the
        // disable edge go out the paced one-shot FIFO. See MozaBaseLfeProtocol.
        // Only valid on the base primary connection (base == primary pipe).
        // ============================================================

        // freqHz is sent unclamped (EncodeFreq saturates the wire field at 200 Hz;
        // EncodePeriod carries higher rates). amp01 is the already-enveloped
        // amplitude (0..1) the worker computed from intensity × smoothness.
        public bool SendBaseLfeEngineStream(bool playing, double freqHz, double amp01)
        {
            if (!_connection.IsConnected) return false;
            var f = MozaBaseLfeProtocol.BuildFrame(
                MozaBaseLfeProtocol.LfeEffect.Engine, playing,
                MozaBaseLfeProtocol.EncodePeriod(MozaBaseLfeProtocol.ParamKEngine, freqHz),
                MozaMBoosterProtocol.EncodeFreq(freqHz),
                MozaMBoosterProtocol.EncodeAmp(amp01));
            _connection.SendStream(StreamKind.BaseLfeEngine, f);
            return true;
        }

        public bool SendBaseLfeAbsStream(bool playing, double freqHz, double amp01)
        {
            if (!_connection.IsConnected) return false;
            var f = MozaBaseLfeProtocol.BuildFrame(
                MozaBaseLfeProtocol.LfeEffect.Abs, playing,
                MozaBaseLfeProtocol.EncodePeriod(MozaBaseLfeProtocol.ParamKAbs, freqHz),
                MozaMBoosterProtocol.EncodeFreq(freqHz),
                MozaMBoosterProtocol.EncodeAmp(amp01));
            _connection.SendStream(StreamKind.BaseLfeAbs, f);
            return true;
        }

        // The id-0 oscillator as a CONTINUOUS tone (ShakeIt only). The three LFE
        // slots are identical oscillators — plugin LFE mode happens to drive id 0 as
        // a one-shot gearshift burst, but nothing stops it streaming like ids 1/2.
        // Period is a timing hint on a host-modulated tone (per wheelbase-0x13.md);
        // reuse the engine ParamK so all three continuous tones encode it the same.
        public bool SendBaseLfeOsc0Stream(bool playing, double freqHz, double amp01)
        {
            if (!_connection.IsConnected) return false;
            var f = MozaBaseLfeProtocol.BuildFrame(
                MozaBaseLfeProtocol.LfeEffect.Gearshift, playing,   // wire effect id 0
                MozaBaseLfeProtocol.EncodePeriod(MozaBaseLfeProtocol.ParamKEngine, freqHz),
                MozaMBoosterProtocol.EncodeFreq(freqHz),
                MozaMBoosterProtocol.EncodeAmp(amp01));
            _connection.SendStream(StreamKind.BaseLfeOsc0, f);
            return true;
        }

        public bool SendBaseLfeGearshiftBurst(double freqHz, double amp01)
        {
            if (!_connection.IsConnected) return false;
            // Gearshift burst: fixed placeholder period, freq+amplitude from the
            // channel. One-shot FIFO (not coalesced).
            var f = MozaBaseLfeProtocol.BuildFrame(
                MozaBaseLfeProtocol.LfeEffect.Gearshift, playing: true,
                MozaBaseLfeProtocol.GearshiftPeriod,
                MozaMBoosterProtocol.EncodeFreq(freqHz),
                MozaMBoosterProtocol.EncodeAmp(amp01));
            _connection.Send(f);
            return true;
        }

        public bool SendBaseLfeDisable(MozaBaseLfeProtocol.LfeEffect id)
        {
            if (!_connection.IsConnected) return false;
            _connection.Send(MozaBaseLfeProtocol.BuildDisable(id));
            return true;
        }

        public void ReadSettings(params string[] commandNames)
        {
            foreach (var name in commandNames)
                ReadSetting(name);
        }

        /// <summary>
        /// Read a batch of settings with an extra ~10ms gap between enqueues.
        /// The write thread's 4ms global pacing is tuned for 48Hz telemetry throughput;
        /// larger startup bursts (30+ reads) still get dropped by the wheel. This runs
        /// the batch on a background task so the caller (usually the read thread) is
        /// not blocked.
        /// </summary>
        public void ReadSettingsPaced(string[] commandNames, int gapMs = 10)
        {
            System.Threading.CancellationToken token;
            // Dispose() can race this call (a stale owner, a late detection reply);
            // the Token getter is the one line here that throws on a disposed CTS.
            try { token = _shutdownCts.Token; }
            catch (ObjectDisposedException) { return; }
            Task.Run(() =>
            {
                try
                {
                    foreach (var name in commandNames)
                    {
                        if (token.IsCancellationRequested) return;
                        ReadSetting(name);
                        // Cancellable sleep — Dispose() cancels the token so a
                        // mid-batch teardown unblocks immediately instead of
                        // running the remaining (commandNames * gapMs) ms.
                        if (token.WaitHandle.WaitOne(gapMs)) return;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // CTS disposed while we were running — accept and exit.
                }
            }, token);
        }

        public void Dispose()
        {
            try { _shutdownCts.Cancel(); } catch { }
            try { _shutdownCts.Dispose(); } catch { }
        }

        private byte GetDeviceId(string deviceType)
        {
            // Dedicated standalone pipe: all commands address the root device.
            if (_deviceIdOverride.HasValue) return _deviceIdOverride.Value;
            switch (deviceType)
            {
                case "base":     return MozaProtocol.DeviceBase;
                case "pedals":   return MozaProtocol.DevicePedals;
                case "wheel":    return _wheelDeviceId;
                case "es-wheel": return MozaProtocol.DeviceEsWheel;
                case "dash":     return MozaProtocol.DeviceDash;
                case "hub":      return MozaProtocol.DeviceHub;
                case "main":     return MozaProtocol.DeviceMain;
                // CM2 meter-config commands (stored colours, thresholds, modes)
                // follow the dashboard target: dev=0x14 for a base-bridged CM2,
                // dev=0x12 for a standalone-USB CM2 — same as the telemetry/LED
                // stream. (A dedicated standalone pipe is handled by the override
                // above.)
                case "cm2-main":
                    return MozaPlugin.Instance?.Cm2TargetDeviceId
                        ?? MozaProtocol.DeviceMain;
                case "handbrake": return MozaProtocol.DeviceHandbrake;
                // HGP/SGP share bus dev 0x1A. On a dedicated standalone pipe the
                // override above wins; this covers a future hub/base-relayed shifter.
                case "shifter":  return MozaProtocol.DeviceHPattern;
                case "ab9":      return MozaProtocol.DeviceAb9;
                default:         return MozaProtocol.DeviceBase;
            }
        }
    }
}
