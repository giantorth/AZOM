using System;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Owns the lifecycle of one Moza mBooster Pedals unit on its own COM port:
    /// a dedicated <see cref="MozaSerialConnection"/>, identity tracking, motor
    /// frame builders, and the per-device effect worker. Multiple instances may
    /// run side-by-side under <see cref="MozaMBoosterRegistry"/> when a user
    /// has more than one mBooster attached (one each for throttle / brake /
    /// clutch is the common case).
    ///
    /// Identity is the USB device instance ID surfaced by
    /// <see cref="MozaPortDiscovery.PortInfo.InstanceId"/> — stable across
    /// reconnects so per-device profile settings (role + per-effect knobs)
    /// survive replug.
    ///
    /// Reference: <c>docs/MozamBooster — Protocol Note.md</c>.
    /// </summary>
    public sealed class MBoosterDeviceController : IDisposable
    {
        private readonly MozaSerialConnection _connection;
        private readonly MBoosterEffectWorker _worker;
        private readonly Func<MBoosterDeviceSettings?> _settingsLookup;
        private readonly Func<bool> _isShuttingDown;
        private volatile bool _detected;
        private volatile bool _disposed;

        // Identity is the USB device instance ID from MozaPortDiscovery —
        // canonical key for per-device settings in the profile dict. Survives
        // reconnects within the same USB port; a user moving the device to a
        // different USB hub may get a different instance id (Windows quirk),
        // which is the same way every other USB peripheral identity-tracks.
        public string Identity { get; }

        // Port name at construction time. May change on reconnect — read live
        // via Connection.LastPortName if needed.
        public string PortName { get; private set; }

        public bool Detected => _detected;
        public bool IsConnected => _connection.IsConnected;
        public MozaSerialConnection Connection => _connection;

        // Latest HID axis value (0..1). Updated by MozaHidReader via the
        // registry; published as a property so the UI panel can show the bar.
        public double LastHidPosition { get; internal set; }

        /// <summary>Latest per-identity settings (role, display name, calibration).
        /// Thin pass-through to the registry's settings lookup — returns null if no
        /// row is recorded yet for this identity.</summary>
        public MBoosterDeviceSettings? CurrentSettings => _settingsLookup();

        public event Action<byte[]>? MessageReceived
        {
            add    => _connection.MessageReceived += value;
            remove => _connection.MessageReceived -= value;
        }

        /// <summary>
        /// Fired on the rising edge of detection (first valid <c>mbooster-*</c>
        /// response on the connection). UI uses this to refresh the tab.
        /// </summary>
        public event Action? DetectedRisingEdge;

        public MBoosterDeviceController(
            string identity,
            string portName,
            Func<MBoosterDeviceSettings?> settingsLookup,
            Func<bool> isShuttingDown,
            Func<bool>? disableProbeFallback = null)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            PortName = portName ?? throw new ArgumentNullException(nameof(portName));
            _settingsLookup = settingsLookup ?? throw new ArgumentNullException(nameof(settingsLookup));
            _isShuttingDown = isShuttingDown ?? (() => false);

            // PID-filter accepts only the mBooster PID. We deliberately do NOT
            // include the "unknown PID" fallback set used by the wheelbase /
            // AB9 connections — mBooster discovery is registry-only by design
            // (see MozaProbeTarget.MBooster). The connection's LastPortName
            // pinning below ensures we target THIS specific COM port and never
            // wander to a sibling mBooster's port if the registry order shifts.
            _connection = new MozaSerialConnection(
                pid => MozaUsbIds.IsMBoosterPid(pid),
                MozaProbeTarget.MBooster,
                disableProbeFallback);
            _connection.CaptureLabel = "mbooster-" + ShortIdentity(identity);
            _connection.LastPortName = portName;
            // Detection / response handling lives on the controller — parse with
            // the dedicated "mbooster" bus hint so dev 0x12 responses don't
            // cross-match against base-* (wheelbase main) or ab9-* (AB9 main).
            _connection.MessageReceived += OnConnectionMessage;
            // Reset detection latch when the underlying port wedges. Disconnected
            // fires from HandleIoFailure on the read/write thread, so this stays
            // lightweight (single volatile bool write). Without it, _detected
            // remains true after a silent reconnect and MarkDetected short-circuits
            // → DetectedRisingEdge never re-fires → OnMBoosterDeviceDetected does
            // not re-run RequestCalibrationReads or ApplyMBoosterSettings for the
            // recovered device.
            _connection.Disconnected += OnConnectionDisconnected;

            _worker = new MBoosterEffectWorker(this, _settingsLookup, _isShuttingDown);
        }

        private void OnConnectionDisconnected()
        {
            _detected = false;
        }

        private void OnConnectionMessage(byte[] data)
        {
            if (_disposed || data == null || data.Length < 2) return;
            // Silence firmware debug noise (same shape as the wheelbase path).
            if (data[0] == MozaProtocol.FirmwareDebugGroup) return;

            var result = MozaResponseParser.Parse(data, busHint: "mbooster");
            if (!result.HasValue) return;
            var r = result.Value;
            if (r.Name == null || !r.Name.StartsWith("mbooster-", StringComparison.Ordinal))
                return;

            // First valid mbooster-* response latches detection (fires DetectedRisingEdge).
            MarkDetected();

            // Calibration read-backs land here — log at Debug so the user can see
            // in the support bundle what the device returned (or didn't). Actual
            // mapping into MBoosterDeviceSettings happens in the plugin-level
            // detection handler so the registry doesn't have to know about the
            // settings shape.
            MozaLog.Debug($"[Moza/mBooster] {ShortIdentity(Identity)} {r.Name} = {r.IntValue}");
        }

        /// <summary>Short identity slug for capture labels / log lines — last 8 chars of instance id.</summary>
        public static string ShortIdentity(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return "unknown";
            if (identity.Length <= 8) return identity;
            return identity.Substring(identity.Length - 8);
        }

        /// <summary>
        /// Attempt to open the COM port for this mBooster. Idempotent — returns
        /// true if already connected. Worker is started on the first successful
        /// connect. Subsequent calls just re-open if the connection died.
        /// </summary>
        public bool TryConnect()
        {
            if (_disposed) return false;
            if (_connection.IsConnected) return true;
            // Pin the cached port name so the connection targets THIS specific
            // mBooster's COM port, not whichever PID 0x0008 device the registry
            // happens to list first.
            _connection.LastPortName = PortName;
            bool ok = _connection.Connect();
            if (ok)
            {
                MozaLog.Info($"[Moza/mBooster] Connected ({ShortIdentity(Identity)} on {_connection.LastPortName})");
                _worker.Start();
            }
            return ok;
        }

        public void Disconnect()
        {
            _worker.Stop();
            _connection.Disconnect();
            _detected = false;
        }

        /// <summary>
        /// Mark detected (first recognisable <c>mbooster-*</c> response).
        /// Latched true; rising-edge event fires once per detection cycle.
        /// </summary>
        public void MarkDetected()
        {
            if (_detected) return;
            _detected = true;
            MozaLog.Debug($"[Moza/mBooster] Detected {ShortIdentity(Identity)}");
            try { DetectedRisingEdge?.Invoke(); }
            catch (Exception ex) { MozaLog.Debug($"[Moza/mBooster] DetectedRisingEdge handler: {ex.Message}"); }
        }

        // ===== Frame submission =====================================

        /// <summary>
        /// Send a motor-write frame via the latest-wins stream lane (worker
        /// path). Stream lane coalesces stale frames if writer lag piles up,
        /// which is the correct behaviour at 50 Hz cadence.
        /// </summary>
        public void SendMotorStream(byte[] frame)
        {
            if (frame == null || !_connection.IsConnected) return;
            _connection.SendStream(StreamKind.MBoosterEffect, frame);
        }

        /// <summary>
        /// Send a one-shot (typically a disable or test-fire frame) via the
        /// FIFO so it is never coalesced away.
        /// </summary>
        public void SendOneShot(byte[] frame)
        {
            if (frame == null || !_connection.IsConnected) return;
            _connection.Send(frame);
        }

        /// <summary>Publish latest telemetry to the worker.</summary>
        public void PostTelemetry(in MBoosterTelemetrySnapshot snap)
        {
            _worker.PostFrame(snap);
        }

        // ===== Settings reads / calibration writes (experimental per § 6) ====

        /// <summary>
        /// Build + send a write for a registered <c>mbooster-*</c> int command
        /// against device 0x12 on THIS device's connection. Returns true if the
        /// frame was enqueued. The protocol note marks this surface as "likely
        /// but unverified" on mBooster firmware — the UI surfaces a warning so
        /// the user knows the request may not be acknowledged.
        /// </summary>
        public bool SendIntWrite(string commandName, int value)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteInt(MozaProtocol.DeviceMain, value);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        /// <summary>Build + send a write for a registered <c>mbooster-*</c> float command.</summary>
        public bool SendFloatWrite(string commandName, float value)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteFloat(MozaProtocol.DeviceMain, value);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        /// <summary>
        /// Build + send a read for a registered <c>mbooster-*</c> command. Read
        /// responses (group 35 + 0x80 = 0xA3) land on <see cref="MessageReceived"/>
        /// and the caller must <see cref="MozaResponseParser.Parse"/> them with
        /// <c>busHint: "mbooster"</c> to disambiguate from wheelbase main and AB9.
        /// </summary>
        public bool SendRead(string commandName)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildReadMessage(MozaProtocol.DeviceMain);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        /// <summary>
        /// Issue a one-time burst of calibration reads (direction / min / max
        /// per pedal + 5-point curves). Mirrors the wheelbase pedal seed; runs
        /// once per detection edge so the UI can populate from device state.
        /// Experimental: may produce no responses on mBooster firmware.
        /// </summary>
        public void RequestCalibrationReads()
        {
            if (!_connection.IsConnected) return;
            foreach (var name in new[]
            {
                "mbooster-throttle-dir", "mbooster-throttle-min", "mbooster-throttle-max",
                "mbooster-brake-dir", "mbooster-brake-min", "mbooster-brake-max",
                "mbooster-clutch-dir", "mbooster-clutch-min", "mbooster-clutch-max",
                "mbooster-throttle-y1", "mbooster-throttle-y2", "mbooster-throttle-y3", "mbooster-throttle-y4", "mbooster-throttle-y5",
                "mbooster-brake-y1", "mbooster-brake-y2", "mbooster-brake-y3", "mbooster-brake-y4", "mbooster-brake-y5",
                "mbooster-clutch-y1", "mbooster-clutch-y2", "mbooster-clutch-y3", "mbooster-clutch-y4", "mbooster-clutch-y5",
            })
            {
                SendRead(name);
            }
        }

        /// <summary>Fire all four disable frames; called on disconnect / shutdown.</summary>
        public void SendAllDisableFrames()
        {
            if (!_connection.IsConnected) return;
            // One-shot FIFO so they all land in order (no coalescing).
            SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(MBoosterEffectId.Abs));
            SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(MBoosterEffectId.Lockup));
            SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(MBoosterEffectId.Threshold));
            SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(MBoosterEffectId.Engine));
        }

        /// <summary>
        /// Fire a single effect for ~1 s, used by the per-effect "Test" buttons
        /// in the UI. The worker handles per-effect scale caps and brake
        /// modulation (ABS / Lockup / Threshold track live brake; Engine uses
        /// its reference frequency at the configured intensity). The raw 0..1
        /// user setting is passed through unchanged.
        /// </summary>
        public void FireEffectTest(MBoosterEffectId effect)
        {
            var settings = _settingsLookup();
            if (settings == null || !_connection.IsConnected) return;

            var es = effect switch
            {
                MBoosterEffectId.Abs       => settings.Abs,
                MBoosterEffectId.Lockup    => settings.Lockup,
                MBoosterEffectId.Threshold => settings.Threshold,
                MBoosterEffectId.Engine    => settings.Engine,
                _ => settings.Abs,
            };

            double intensity = (es?.IntensityPct ?? 50) / 100.0;
            _worker.FireTestPulse(effect, intensity);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                // Best-effort emit disable frames before tearing down so the
                // motor doesn't latch the last waveform after the port closes
                // (protocol note § 3 "Disable").
                SendAllDisableFrames();
            }
            catch (Exception ex) { MozaLog.Debug($"[Moza/mBooster] Disable on dispose: {ex.Message}"); }
            try { _worker.Stop(); } catch { }
            try { _connection.MessageReceived -= OnConnectionMessage; } catch { }
            try { _connection.Disconnected -= OnConnectionDisconnected; } catch { }
            try { _connection.Dispose(); } catch { }
        }
    }
}
