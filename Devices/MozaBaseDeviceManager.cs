using System;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Owns a dedicated <see cref="MozaSerialConnection"/> for a MOZA wheelbase on
    /// its own COM port. The MIRROR of <see cref="MozaHubDeviceManager"/>: used in
    /// the broken-base case where the wheel is reachable only via a Universal Hub.
    /// There the primary connection migrates to the hub and runs the full
    /// wheel/session/telemetry pipeline, and THIS manager keeps base-only traffic
    /// (motor temps, base-state, FFB read/write, base-ambient LEDs, base identity)
    /// flowing on the base's separate pipe.
    ///
    /// In the normal case the base IS the primary connection, so this manager
    /// finds the base port already held by the primary (the _activePorts guard)
    /// and no-ops — it only ever claims a base port the primary has LEFT after a
    /// base→hub migration. <see cref="MozaPlugin.PrimaryBoundToHub"/> gates the
    /// reconnect-tick TryConnectBase so it never even attempts an open while the
    /// base is the primary.
    ///
    /// Registry/USB-enumeration only: the probe fallback is force-disabled so this
    /// connection NEVER writes scan bytes to unclassified COM ports. The wheelbase
    /// PID is registered, so the registry always classifies it.
    /// </summary>
    public class MozaBaseDeviceManager : IDisposable
    {
        private readonly MozaSerialConnection _connection;
        private readonly MozaDeviceManager _deviceManager;
        // Dedicated retransmit tracker so the base's tracked reads are re-emitted
        // on the BASE pipe, not the hub-bound primary. Mirrors the hub manager's
        // per-pipe isolation (see MozaHubDeviceManager).
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

        public MozaBaseDeviceManager()
        {
            // Wheelbase PID only, registry-only discovery. BaseAndHub is the
            // probe-shape target but probe is force-disabled here so it stays
            // dormant — kept for self-consistency if probing is ever enabled.
            _connection = new MozaSerialConnection(
                pid => MozaUsbIds.IsWheelbasePid(pid),
                MozaProbeTarget.BaseAndHub,
                disableProbeFallback: () => true);
            _connection.CaptureLabel = "base";
            _deviceManager = new MozaDeviceManager(_connection, _pendingResponses);
        }

        /// <summary>Open the base's COM port. Idempotent; reuses the last port.</summary>
        public bool TryConnect()
        {
            if (_connection.IsConnected) return true;
            bool ok = _connection.Connect();
            if (ok)
                MozaLog.Info($"[AZOM] Connected to base aux pipe ({_connection.DiscoveredPid})");
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
