using System;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Owns a dedicated <see cref="MozaSerialConnection"/> for a MOZA Universal
    /// Hub (PID 0x0020) on its own COM port. Used when a wheelbase is ALSO
    /// present: the base stays the telemetry-driving primary connection, and
    /// this manager enumerates the peripherals attached to the hub (pedals,
    /// handbrake, hub port-power status) on the hub's separate pipe.
    ///
    /// The hub-ONLY case (no base) is handled by the primary BaseAndHub
    /// connection, which falls back to the hub when no wheelbase port exists and
    /// runs the full wheel/session/telemetry pipeline there. In that case this
    /// manager finds the hub port already held by the primary (the _activePorts
    /// guard) and no-ops — so it never double-opens or steals the primary's port.
    ///
    /// Registry/USB-enumeration only: the probe fallback is force-disabled so
    /// this connection NEVER writes scan bytes to unclassified COM ports. The
    /// hub PID is registered, so the registry always classifies it.
    /// </summary>
    public class MozaHubDeviceManager : IDisposable
    {
        private readonly MozaSerialConnection _connection;
        private readonly MozaDeviceManager _deviceManager;
        // Dedicated retransmit tracker so the hub's tracked reads are re-emitted
        // on the HUB pipe, not the primary. The plugin drives its
        // TickRetransmits against _connection.Send (see MozaPlugin reconnect/
        // retry wiring). Without a separate tracker, hub reads tracked in the
        // global singleton would be retransmitted on the base port.
        private readonly PendingResponseTracker _pendingResponses = new PendingResponseTracker();

        public bool IsConnected => _connection.IsConnected;
        public MozaSerialConnection Connection => _connection;
        public MozaDeviceManager DeviceManager => _deviceManager;
        public PendingResponseTracker PendingResponses => _pendingResponses;

        public event Action<byte[]>? MessageReceived
        {
            add    => _connection.MessageReceived += value;
            remove => _connection.MessageReceived -= value;
        }

        public MozaHubDeviceManager()
        {
            // Hub PID (0x0020) only, registry-only discovery. The HubOnly probe
            // target is the correct fallback shape (single 0x64/0x12 hub probe,
            // never a base probe), but probe is force-disabled here so it stays
            // dormant — kept for self-consistency if probing is ever enabled.
            _connection = new MozaSerialConnection(
                pid => MozaUsbIds.IsHubPid(pid),
                MozaProbeTarget.HubOnly,
                disableProbeFallback: () => true);
            _connection.CaptureLabel = "hub";
            _deviceManager = new MozaDeviceManager(_connection, _pendingResponses);
        }

        /// <summary>Open the hub's COM port. Idempotent; reuses the last port.</summary>
        public bool TryConnect()
        {
            if (_connection.IsConnected) return true;
            bool ok = _connection.Connect();
            if (ok)
                MozaLog.Info($"[AZOM] Connected to Universal Hub ({_connection.DiscoveredPid})");
            return ok;
        }

        public void Disconnect() => _connection.Disconnect();

        public void Dispose()
        {
            _deviceManager.Dispose();
            _connection.Dispose();
        }
    }
}
