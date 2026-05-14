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
            if (_wheelDetected && deviceId == _wheelDeviceId)
                _wheelRespondedSinceLastPoll = true;
        }

        public void ResetWheelResponseFlag()
        {
            _wheelRespondedSinceLastPoll = false;
        }

        public MozaDeviceManager(MozaSerialConnection connection)
        {
            _connection = connection;
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
        public void SendDisplayProbe()
        {
            if (!_connection.IsConnected) return;
            byte dev = MozaProtocol.DeviceWheel;
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
            MozaLog.Info($"[Moza] Wheel locked on device ID {_wheelDeviceId}");
        }

        // Default backoff for tracked reads: catch transient drops in 200ms,
        // widen on each retry. 3 attempts × 200/400/800 ≈ 1.4 s budget total.
        private static readonly int[] ReadRetryBackoffMs = { 200, 400, 800 };
        private const int ReadRetryMaxAttempts = 3;

        public bool ReadSettingForDevice(string commandName, byte deviceId)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildReadMessage(deviceId);
            if (msg == null) return false;
            _connection.Send(msg);
            MozaPlugin.Instance?.PendingResponses.Track(
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
            MozaPlugin.Instance?.PendingResponses.Track(
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
            switch (deviceType)
            {
                case "base":   return MozaProtocol.DeviceBase;
                case "pedals": return MozaProtocol.DevicePedals;
                case "wheel":  return _wheelDeviceId;
                case "dash":   return MozaProtocol.DeviceDash;
                case "hub":       return MozaProtocol.DeviceHub;
                case "main":      return MozaProtocol.DeviceMain;
                case "handbrake": return MozaProtocol.DeviceHandbrake;
                case "ab9":       return MozaProtocol.DeviceAb9;
                default:          return MozaProtocol.DeviceBase;
            }
        }
    }
}
