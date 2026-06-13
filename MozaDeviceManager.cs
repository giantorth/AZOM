using System;
using System.Threading;
using System.Threading.Tasks;
using MozaPlugin.Protocol;


namespace MozaPlugin
{
    /// <summary>
    /// Handles reading and writing settings to Moza devices.
    /// Includes wheel ID cycling to support different wheel models (23, 21, 19).
    /// </summary>
    public class MozaDeviceManager : IDisposable
    {
        private readonly MozaSerialConnection _connection;
        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();

        // Retransmit tracker for tracked reads. The primary connection uses the
        // global singleton (MozaPlugin.PendingResponses), whose TickRetransmits
        // is driven by the plugin's retry timer against the PRIMARY pipe. A
        // sibling connection (e.g. the dedicated Universal Hub pipe) MUST pass
        // its own tracker so its read retransmits go out on its own Send — not
        // the primary's — otherwise hub reads would be re-emitted on the base
        // port. Null → fall back to the global singleton (primary behavior).
        private readonly PendingResponseTracker? _pendingResponses;
        private PendingResponseTracker? Tracker => _pendingResponses ?? MozaPlugin.Instance?.PendingResponses;

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
                                 PendingResponseTracker? pendingResponses = null,
                                 byte? deviceIdOverride = null)
        {
            _connection = connection;
            _pendingResponses = pendingResponses;
            _deviceIdOverride = deviceIdOverride;
        }

        // Valid wheel device IDs to try (23, 21, 19)
        private static readonly byte[] WheelIdCandidates = { 23, 21, 19 };

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
        /// <see cref="Telemetry.Fsr1DisplayDriver"/>; callers exclude FSR1 to
        /// avoid a double-send.
        /// </summary>
        public void SendWheelKeepalive()
        {
            if (!_connection.IsConnected) return;
            SendRawProbe(MozaProtocol.TelemetrySendGroup, _wheelDeviceId, new byte[] { 0x00 });
        }

        /// <summary>
        /// Group-0x0e param-manager poll to the locked wheel
        /// (<c>7e 03 0e &lt;wheelDeviceId&gt; 00 00 01 chk</c>) — the same read
        /// shape <see cref="SendCm1ParamProbe"/> aims at the dash. PitHouse polls
        /// this on the wheel ~1.7 Hz (210× in the screenless-R5 capture); the
        /// plugin previously never touched the wheel's param subsystem. Part of
        /// the PitHouse idle footprint that keeps the wheel's Table-8 param state
        /// validated, so identity resolves instead of storming read/write failures.
        /// </summary>
        public void SendWheelParamPoll()
        {
            if (!_connection.IsConnected) return;
            SendRawProbe(MozaProtocol.FirmwareDebugGroup, _wheelDeviceId,
                new byte[] { 0x00, 0x00, 0x01 });
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
            var token = _shutdownCts.Token;
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
                    return MozaPlugin.Instance?.PreferredStandaloneDashboardTargetDeviceId
                        ?? MozaProtocol.DeviceMain;
                case "handbrake": return MozaProtocol.DeviceHandbrake;
                case "ab9":      return MozaProtocol.DeviceAb9;
                default:         return MozaProtocol.DeviceBase;
            }
        }
    }
}
