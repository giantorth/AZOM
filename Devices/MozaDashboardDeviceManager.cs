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
        // Own retransmit tracker — a CM2 read must be re-emitted on the dashboard
        // port, never the primary's. It also has to tick independently: a
        // standalone CM2 with no wheelbase leaves the primary disconnected, and
        // the retry timer's per-pipe IsConnected gate would otherwise never reach
        // this lane's pending reads.
        private readonly PendingResponseTracker _pendingResponses = new PendingResponseTracker();

        public bool IsConnected => _connection.IsConnected;
        public MozaSerialConnection Connection => _connection;
        public PendingResponseTracker PendingResponses => _pendingResponses;

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
            _deviceManager = new MozaDeviceManager(_connection, _pendingResponses);
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

        /// <summary>Send setting reads on the dashboard connection.</summary>
        public void ReadSettings(params string[] commandNames) => _deviceManager.ReadSettings(commandNames);

        /// <summary>
        /// Write an int setting to an explicit device id on the dashboard
        /// connection. Used to push the live RPM/flag LED bitmask
        /// (dash-send-telemetry) to a standalone-USB CM2, which bridges as the
        /// root device (0x12) on this dedicated pipe.
        /// </summary>
        public bool WriteSettingForDevice(string commandName, byte deviceId, int value)
            => _deviceManager.WriteSettingForDevice(commandName, deviceId, value);

        /// <summary>
        /// Write a byte-array command to an explicit device id on the dashboard
        /// connection. Used to push the live flag-LED colours (dash-flag-colors,
        /// group 0x32 cmd 08 00) to a standalone-USB CM2 at root device 0x12.
        /// </summary>
        public bool WriteArrayForDevice(string commandName, byte deviceId, byte[] payload)
            => _deviceManager.WriteArrayForDevice(commandName, deviceId, payload);

        /// <summary>Stream-lane (latest-wins, coalescing) variants of the LED writes
        /// above — keep high-rate idempotent CM2 LED frames off the throttled
        /// one-shot FIFO. See MozaDeviceManager.Write*Stream.</summary>
        public bool WriteSettingForDeviceStream(string commandName, byte deviceId, int value, Protocol.StreamKind slot)
            => _deviceManager.WriteSettingForDeviceStream(commandName, deviceId, value, slot);

        public bool WriteArrayForDeviceStream(string commandName, byte deviceId, byte[] payload, Protocol.StreamKind slot)
            => _deviceManager.WriteArrayForDeviceStream(commandName, deviceId, payload, slot);

        public void Disconnect() => _connection.Disconnect();

        public void Dispose()
        {
            _deviceManager.Dispose();
            _connection.Dispose();
        }
    }
}
