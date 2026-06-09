using System;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Owns a dedicated <see cref="MozaSerialConnection"/> for a standalone-USB
    /// MOZA dashboard (CM2, PID 0x0025). Independent of the wheelbase connection
    /// so a CM2 on its own cable works even when a base is connected — the
    /// shared wheelbase connection would otherwise claim the base port and leave
    /// the CM2's port unopened. The telemetry sender is repointed at this
    /// connection while a standalone CM2 is present (see
    /// <see cref="MozaPlugin.DashboardUsbConnected"/>).
    /// </summary>
    public class MozaDashboardDeviceManager : IDisposable
    {
        private readonly MozaSerialConnection _connection;
        private readonly MozaDeviceManager _deviceManager;

        public bool IsConnected => _connection.IsConnected;
        public MozaSerialConnection Connection => _connection;

        public event Action<byte[]>? MessageReceived
        {
            add    => _connection.MessageReceived += value;
            remove => _connection.MessageReceived -= value;
        }

        public MozaDashboardDeviceManager()
        {
            // Dashboard PID (0x0025) only, and registry/USB-enumeration only —
            // the probe fallback is force-disabled so this connection NEVER
            // writes scan bytes to unclassified COM ports. CM2's PID is
            // registered, so the registry always classifies it; serial probing
            // would only risk disturbing other devices.
            _connection = new MozaSerialConnection(
                pid => MozaUsbIds.IsDashboardPid(pid),
                MozaProbeTarget.BaseAndHub,
                disableProbeFallback: () => true);
            _connection.CaptureLabel = "dashboard";
            _deviceManager = new MozaDeviceManager(_connection);
        }

        /// <summary>Open the dashboard's COM port. Idempotent; reuses the last port.</summary>
        public bool TryConnect()
        {
            if (_connection.IsConnected) return true;
            bool ok = _connection.Connect();
            if (ok)
                MozaLog.Info($"[AZOM] Connected to standalone dashboard ({_connection.DiscoveredPid})");
            return ok;
        }

        /// <summary>Send setting reads on the dashboard connection (e.g. DashSettingsReadCommands).</summary>
        public void ReadSettings(params string[] commandNames) => _deviceManager.ReadSettings(commandNames);

        public void Disconnect() => _connection.Disconnect();

        public void Dispose()
        {
            _deviceManager.Dispose();
            _connection.Dispose();
        }
    }
}
