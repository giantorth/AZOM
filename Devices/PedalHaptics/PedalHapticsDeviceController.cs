using System;
using System.Text;
using MozaPlugin.Diagnostics;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.PedalHaptics
{
    /// <summary>
    /// One S12 pedal vibration unit. Two lanes, same as the mBooster:
    ///
    /// <list type="bullet">
    /// <item>USB — plugged straight into the PC on its own CDC port
    /// (PID <c>0x002F</c>), addressed <c>0x12</c>.</item>
    /// <item>ROUTED — reached through a wheelbase/hub pipe, so frames ride that
    /// connection using the extended envelope (<c>0x1F</c> + extended id
    /// <c>0x1E</c>) and the pipe's lifecycle stays with its owning manager.</item>
    /// </list>
    ///
    /// Simpler than <see cref="MBooster.MBoosterDeviceController"/> in one
    /// important way: the three motors are selected by a payload field, not by
    /// three different bus device ids, so there is no chain topology to resolve
    /// and no role→device-id map.
    ///
    /// Protocol reference: <c>docs/protocol/devices/pedal-haptics.md</c>.
    /// </summary>
    internal sealed class PedalHapticsDeviceController : IDisposable
    {
        /// <summary>
        /// Slot used for the presence query. Any slot would do — the query is
        /// asking whether the device speaks this protocol at all, not what that
        /// slot is doing.
        /// </summary>
        private const byte ProbeSlot = (byte)PedalHapticsEffectSlot.TractionControl;

        private readonly MozaSerialConnection _connection;
        private readonly bool _ownsConnection;
        private readonly Func<bool> _isShuttingDown;
        private readonly PedalHapticsEffectWorker _worker;

        private int _detected;      // 0/1, latched on the first vibration reply
        private bool _disposed;

        /// <summary>Stable key for this unit — the USB instance id, or a port label for a routed lane.</summary>
        public string Identity { get; }

        /// <summary>Port name for a USB lane; the owner's port label for a routed one.</summary>
        public string PortName { get; private set; }

        /// <summary>Which envelope this lane's frames use.</summary>
        public PedalHapticsAddressing Addressing { get; }

        /// <summary>True when the unit has answered a vibration query at least once.</summary>
        public bool Detected => System.Threading.Volatile.Read(ref _detected) != 0;

        /// <summary>True for a lane sharing a wheelbase/hub pipe rather than owning a CDC port.</summary>
        public bool IsRouted => !_ownsConnection;

        /// <summary>The PID this lane's USB port enumerated as. Null on a routed lane.</summary>
        public string? DiscoveredPid => _ownsConnection ? _connection.DiscoveredPid : null;

        /// <summary>Raised the first time the unit answers a vibration query.</summary>
        public event Action<PedalHapticsDeviceController>? DetectedRisingEdge;

        /// <summary>ROUTED lane: shares the wheelbase/hub connection that already owns the pipe.</summary>
        internal PedalHapticsDeviceController(
            string identity,
            MozaSerialConnection sharedConnection,
            string portLabel,
            Func<bool>? isShuttingDown)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _connection = sharedConnection ?? throw new ArgumentNullException(nameof(sharedConnection));
            PortName = portLabel ?? string.Empty;
            Addressing = PedalHapticsAddressing.Extended;
            _ownsConnection = false;
            _isShuttingDown = isShuttingDown ?? (() => false);

            _connection.MessageReceived += OnConnectionMessage;
            _worker = new PedalHapticsEffectWorker(this, _isShuttingDown);
        }

        /// <summary>USB lane: owns its own CDC connection, selected by PID.</summary>
        internal PedalHapticsDeviceController(
            string identity,
            string portName,
            Func<bool>? isShuttingDown)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            PortName = portName ?? throw new ArgumentNullException(nameof(portName));
            Addressing = PedalHapticsAddressing.UsbDirect;
            _ownsConnection = true;
            _isShuttingDown = isShuttingDown ?? (() => false);

            _connection = new MozaSerialConnection(
                pidFilter: MozaUsbIds.IsPedalHapticsPid,
                probeTarget: MozaProbeTarget.PedalHaptics,
                // Registry-driven like every other peripheral lane: the port is
                // already classified by PID, so this must never scan.
                disableProbeFallback: () => true)
            {
                CaptureLabel = "pedal-haptics",
                LastPortName = portName,
            };
            _connection.MessageReceived += OnConnectionMessage;
            _worker = new PedalHapticsEffectWorker(this, _isShuttingDown);
        }

        public bool IsConnected => _connection.IsConnected;

        /// <summary>True when this lane is bound to <paramref name="connection"/>. A routed lane
        /// borrows its owner's pipe, so if the owner ever swaps in a fresh connection object the
        /// lane is stranded on a dead one and has to be rebuilt.</summary>
        public bool SharesConnection(MozaSerialConnection connection)
            => ReferenceEquals(_connection, connection);

        /// <summary>Open the USB lane and start the motor loop. Routed lanes use <see cref="StartWorker"/>.</summary>
        public bool TryConnect()
        {
            if (!_ownsConnection) return _connection.IsConnected;
            if (_connection.IsConnected) return true;
            if (!_connection.Connect()) return false;

            PortName = _connection.LastPortName ?? PortName;
            StartWorker();
            SendPresenceProbe();
            return true;
        }

        /// <summary>Start the motor loop — the registry calls this for a routed lane.</summary>
        public void StartWorker() => _worker.Start();

        /// <summary>
        /// Ask the unit whether one slot is enabled. Any well-formed
        /// answer proves it speaks the vibration protocol, which is what
        /// detection is really asking; the reported state itself is ignored.
        /// </summary>
        public void SendPresenceProbe()
            => SendOneShot(MozaPedalHapticsProtocol.BuildQueryFrame(
                Addressing, (byte)PedalHapticsPedal.Brake, ProbeSlot));

        /// <summary>
        /// Send an effect frame. These go on the paced one-shot FIFO rather than
        /// a latest-wins stream lane: every frame addresses a different
        /// pedal+slot, so coalescing by lane would drop one slot's update in
        /// favour of another's. The worker's per-tick budget is what keeps the
        /// queue from growing, not the transport.
        /// </summary>
        public void SendFrame(byte[] frame)
        {
            // Gated on Detected, not just Connected: a routed lane is registered
            // speculatively on every base pipe, and a base with no S12 attached
            // must never be written effect frames.
            if (frame == null || !Detected || !_connection.IsConnected) return;
            _connection.Send(frame);
        }

        /// <summary>Send a frame that must go out regardless of detection state — the presence probe.</summary>
        public void SendOneShot(byte[] frame)
        {
            if (frame == null || !_connection.IsConnected) return;
            _connection.Send(frame);
        }

        /// <summary>Publish one channel's latest ShakeIt output to the motor loop.</summary>
        public void PostChannel(int channel, double gain01, double freqHz)
            => _worker.PostChannel(channel, gain01, freqHz);

        /// <summary>Drop every channel to silent (the ShakeIt provider's Stop path).</summary>
        public void ClearChannels() => _worker.ClearChannels();

        private void OnConnectionMessage(byte[] data)
        {
            if (data == null || data.Length < 3) return;

            // Inbound frames are already de-framed by the read path: data[0] is
            // the group, data[1] the address.
            if (!MozaPedalHapticsProtocol.IsVibrationResponse(data, data.Length, Addressing))
                return;

            if (System.Threading.Interlocked.Exchange(ref _detected, 1) == 0)
            {
                MozaLog.Info($"[AZOM] Pedal-haptics unit detected on {PortName}"
                    + (IsRouted ? " (routed)" : $" (USB, PID {DiscoveredPid ?? "unknown"})"));
                try { DetectedRisingEdge?.Invoke(this); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM] Pedal-haptics detect handler failed: {ex.Message}"); }
            }

            LogQueryReply(data);
        }

        /// <summary>
        /// Surface a query reply at debug level. Worth having: it reports the
        /// unit's own view of a slot — enabled, remaining time, frequency,
        /// strength — which is the only read-back the protocol offers, and the
        /// quickest way to see whether a frame was accepted.
        ///
        /// It reports software effect state, not measured vibration, so it
        /// cannot confirm a motor actually moved.
        /// </summary>
        private void LogQueryReply(byte[] data)
        {
            if (!MozaPedalHapticsProtocol.TryParseQueryResponse(
                    data, data.Length, Addressing,
                    out byte pedal, out byte slot, out bool enabled,
                    out ushort remainingMs, out ushort freq, out ushort strength))
                return;

            MozaLog.DebugIfChanged("pedal-haptics-query",
                $"[AZOM] Pedal-haptics {MozaPedalHapticsProtocol.PedalName(pedal)} slot {slot}: "
                + $"{(enabled ? "on" : "off")}, {remainingMs} ms left, {freq} Hz, "
                + $"strength {strength}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Stop() sends the disable frames, so it must run before the pipe goes.
            try { _worker.Dispose(); } catch { /* shutting down */ }

            _connection.MessageReceived -= OnConnectionMessage;
            if (_ownsConnection)
            {
                try { _connection.Disconnect(); } catch { /* shutting down */ }
                try { _connection.Dispose(); } catch { /* shutting down */ }
            }
        }
    }
}
