using System;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Pedals (bus dev <c>0x19</c>) reached through another device's own USB pipe —
    /// a pedal set wired into an S12 pedal vibration unit, which relays the pedal
    /// bus onto its CDC port (Pit House supports this hookup). The host lane owns
    /// the connection; this lane only borrows it.
    ///
    /// Same shape as the Universal Hub's pedal handling
    /// (<see cref="ConnectionCoordinator.PollHubPeripherals"/> /
    /// <see cref="ConnectionCoordinator.OnHubMessageReceived"/>): its own device
    /// manager, retransmit tracker and secondary prober, so <c>PedalsOwner</c> and
    /// the pedal settings reads/writes land on this pipe, and the existing Pedals
    /// tab and <see cref="Hardware.HardwareApplier"/> work unchanged.
    /// </summary>
    internal sealed class RelayedPedalsLane : IDisposable
    {
        private readonly MozaSerialConnection _connection;
        private readonly MozaData _data;
        private readonly DeviceDetectionState _detectionState;
        private readonly Func<bool> _isShuttingDown;
        private readonly PendingResponseTracker _pending = new PendingResponseTracker();
        private readonly MozaDeviceManager _deviceManager;
        private readonly DeviceProber _prober;

        private volatile bool _disposed;
        private volatile bool _answered;

        /// <summary>True once dev 0x19 answered a presence probe on this pipe.</summary>
        public bool Answered => _answered;

        /// <summary>True when pedal writes route down this pipe.</summary>
        public bool OwnsPedals => ReferenceEquals(_detectionState.PedalsOwner, _deviceManager);

        public int PendingReads => _pending.PendingCount;

        public RelayedPedalsLane(
            MozaPlugin plugin,
            MozaSerialConnection connection,
            MozaData data,
            DeviceDetectionState detectionState,
            Func<bool>? isShuttingDown)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _detectionState = detectionState ?? throw new ArgumentNullException(nameof(detectionState));
            _isShuttingDown = isShuttingDown ?? (() => false);

            // No device-id override: the pedals are a bus sub-device here, so
            // pedals-* commands address 0x19 as they do through a base or hub.
            _deviceManager = new MozaDeviceManager(_connection, _pending);
            _prober = new DeviceProber(plugin, _connection, _deviceManager, _data, _detectionState,
                drivesTelemetry: false);

            _connection.MessageReceived += OnConnectionMessage;
            _connection.Disconnected += OnConnectionDisconnected;
        }

        /// <summary>
        /// Presence probe until some pipe owns the pedals (first responder wins,
        /// same gate as the base and hub pipes). Owner-null also re-probes so a flag
        /// carried across a persistent-wire reload gets re-pointed.
        /// </summary>
        public void Poll()
        {
            if (_disposed || _isShuttingDown() || !_connection.IsConnected) return;
            if (!_detectionState.PedalsDetected || _detectionState.PedalsOwner == null)
                _deviceManager.SendPresenceProbe(MozaProtocol.DevicePedals);
        }

        /// <summary>Retransmit this lane's tracked reads on its own pipe.</summary>
        public void TickRetransmits()
        {
            if (_disposed || !_connection.IsConnected) return;
            _pending.TickRetransmits(_connection.SendDeviceCommand);
        }

        private void OnConnectionMessage(byte[] data)
        {
            if (_disposed || _isShuttingDown()) return;
            if (data == null || data.Length < 2) return;

            if (data[0] == MozaProtocol.FirmwareDebugGroup) return;

            // Presence-probe ACK: data = {0x80, swap(dev)}.
            if (data.Length == 2 && data[0] == 0x80)
            {
                if (MozaProtocol.SwapNibbles(data[1]) != MozaProtocol.DevicePedals) return;
                _answered = true;
                _prober.MarkPedalsDetected();
                return;
            }

            var result = MozaResponseParser.Parse(data);
            if (!result.HasValue) return;
            var r = result.Value;
            if (r.Name == null) return;

            // The host device's own replies share this pipe; only pedal replies are ours.
            if (!r.Name.StartsWith("pedals-", StringComparison.Ordinal)) return;

            _pending.NoteResponse(r.Name);
            _data.UpdateFromCommand(r.Name, r.IntValue);
            if (r.ArrayValue != null)
                _data.UpdateFromArray(r.Name, r.ArrayValue);
            _prober.DetectDevices(r.Name, r.IntValue, r.DeviceId);
        }

        private void OnConnectionDisconnected()
        {
            _answered = false;
            _prober.ReleasePeripheralOwnership();
            try { _pending.Clear(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _connection.MessageReceived -= OnConnectionMessage; } catch { }
            try { _connection.Disconnected -= OnConnectionDisconnected; } catch { }
            // An explicit close doesn't raise Disconnected, so drop ownership here.
            _prober.ReleasePeripheralOwnership();
            try { _deviceManager.Dispose(); } catch { }
            try { _pending.Clear(); } catch { }
        }
    }
}
