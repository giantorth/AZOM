using System;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Describes one supported directly-USB-attached peripheral type so the
    /// <see cref="MozaStandalonePeripheralRegistry"/> and
    /// <see cref="StandalonePeripheralController"/> are entirely data-driven.
    /// Adding a new peripheral (e.g. a standalone shifter, once it gains a
    /// settings + UI surface) is a single descriptor entry.
    /// </summary>
    internal sealed class StandalonePeripheralDescriptor
    {
        /// <summary>USB category this descriptor claims (Pedals / Handbrake).</summary>
        public MozaDeviceCategory Category { get; }
        /// <summary>PID filter for the dedicated connection — accepts only this category.</summary>
        public Func<string?, bool> PidFilter { get; }
        /// <summary>Probe target (dormant; probe fallback is force-disabled).</summary>
        public MozaProbeTarget ProbeTarget { get; }
        /// <summary>Device id used for the empty presence probe (e.g. <c>DevicePedals</c>).</summary>
        public byte DeviceId { get; }
        /// <summary>Response-name prefix this lane owns (<c>"pedals-"</c> / <c>"handbrake-"</c>).</summary>
        public string CommandPrefix { get; }
        /// <summary>Capture-label / log base (<c>"pedals"</c> / <c>"handbrake"</c>).</summary>
        public string CaptureLabelBase { get; }
        /// <summary>Flips the shared detection flag + owner to this lane's prober.
        /// The bool is <c>issueReads</c> — false shows the tab without firing the
        /// (possibly doomed) settings-read cascade; true once the device has
        /// answered our binary protocol.</summary>
        public Action<DeviceProber, bool> MarkDetected { get; }
        /// <summary>Reads this peripheral's shared detection flag (gates presence-probe polling).</summary>
        public Func<DeviceDetectionState, bool> IsDetected { get; }

        public StandalonePeripheralDescriptor(
            MozaDeviceCategory category,
            Func<string?, bool> pidFilter,
            MozaProbeTarget probeTarget,
            byte deviceId,
            string commandPrefix,
            string captureLabelBase,
            Action<DeviceProber, bool> markDetected,
            Func<DeviceDetectionState, bool> isDetected)
        {
            Category = category;
            PidFilter = pidFilter ?? throw new ArgumentNullException(nameof(pidFilter));
            ProbeTarget = probeTarget;
            DeviceId = deviceId;
            CommandPrefix = commandPrefix ?? throw new ArgumentNullException(nameof(commandPrefix));
            CaptureLabelBase = captureLabelBase ?? throw new ArgumentNullException(nameof(captureLabelBase));
            MarkDetected = markDetected ?? throw new ArgumentNullException(nameof(markDetected));
            IsDetected = isDetected ?? throw new ArgumentNullException(nameof(isDetected));
        }

        // The two peripherals that actually have a config/calibration surface
        // (settings commands + UI tab + Apply*ToHardware). Standalone shifters
        // (HGP 0x001E / SGP 0x0023) have NO settings commands and no UI, so
        // there is nothing for a lane to read or write — they are intentionally
        // not listed until that surface exists.
        public static readonly StandalonePeripheralDescriptor Pedals =
            new StandalonePeripheralDescriptor(
                MozaDeviceCategory.Pedals,
                pid => MozaUsbIds.IsPedalsPid(pid),
                MozaProbeTarget.PedalsOnly,
                MozaProtocol.DevicePedals,
                "pedals-",
                "pedals",
                (prober, issueReads) => prober.MarkPedalsDetected(issueReads),
                s => s.PedalsDetected);

        public static readonly StandalonePeripheralDescriptor Handbrake =
            new StandalonePeripheralDescriptor(
                MozaDeviceCategory.Handbrake,
                pid => MozaUsbIds.IsHandbrakePid(pid),
                MozaProbeTarget.HandbrakeOnly,
                MozaProtocol.DeviceHandbrake,
                "handbrake-",
                "handbrake",
                (prober, issueReads) => prober.MarkHandbrakeDetected(issueReads),
                s => s.HandbrakeDetected);

        /// <summary>Descriptor for a discovered port's category, or null if unsupported.</summary>
        public static StandalonePeripheralDescriptor? ForCategory(MozaDeviceCategory category)
        {
            if (category == MozaDeviceCategory.Pedals) return Pedals;
            if (category == MozaDeviceCategory.Handbrake) return Handbrake;
            return null;
        }
    }

    /// <summary>
    /// Owns a dedicated <see cref="MozaSerialConnection"/> for one MOZA
    /// peripheral plugged STRAIGHT into the PC (its own USB CDC port + PID),
    /// rather than reaching the plugin through a wheelbase or Universal Hub.
    /// It is the pedals/handbrake analogue of <see cref="MozaHubDeviceManager"/>:
    /// its own connection + <see cref="MozaDeviceManager"/> + dedicated
    /// <see cref="PendingResponseTracker"/> + a secondary
    /// <see cref="DeviceProber"/> (<c>drivesTelemetry:false</c>) so detection
    /// ownership and tracked reads land on THIS pipe.
    ///
    /// Config/calibration only — pedal/handbrake axis positions still come from
    /// SimHub's own HID input. Because <c>PedalsOwner</c>/<c>HandbrakeOwner</c>
    /// routing in <see cref="Hardware.HardwareApplier"/> and the existing
    /// Pedals/Handbrake UI tabs already read from <see cref="MozaData"/>, this
    /// lane only has to detect the device, stamp ownership to its own device
    /// manager, and pump settings reads/writes — the existing surfaces populate
    /// automatically.
    ///
    /// Registry-only discovery: the probe fallback is force-disabled so this
    /// connection NEVER writes scan bytes to unclassified COM ports. The
    /// peripheral PIDs are registered, so the registry always classifies them.
    /// </summary>
    internal sealed class StandalonePeripheralController : IDisposable
    {
        private readonly StandalonePeripheralDescriptor _desc;
        private readonly MozaPlugin _plugin;
        private readonly MozaData _data;
        private readonly DeviceDetectionState _detectionState;
        private readonly Func<bool> _isShuttingDown;

        private readonly MozaSerialConnection _connection;
        private readonly MozaDeviceManager _deviceManager;
        // Dedicated tracker so this peripheral's tracked reads retransmit on
        // ITS pipe, never the primary (mirrors MozaHubDeviceManager).
        private readonly PendingResponseTracker _pending = new PendingResponseTracker();
        private readonly DeviceProber _prober;

        private volatile bool _disposed;
        // True once the device has answered our binary protocol on THIS dedicated
        // pipe (a {0x80,*} presence ACK). Distinct from the shared tab flag: the
        // tab shows on connect, but the self/root (0x00) presence probe keeps
        // firing until this latches — only then are settings reads safe to issue.
        private volatile bool _binaryConfirmed;

        public string Identity { get; }
        public string PortName { get; private set; }
        public MozaDeviceCategory Category => _desc.Category;
        public bool IsConnected => _connection.IsConnected;
        public MozaSerialConnection Connection => _connection;
        public PendingResponseTracker PendingResponses => _pending;

        public StandalonePeripheralController(
            StandalonePeripheralDescriptor descriptor,
            string identity,
            string portName,
            MozaPlugin plugin,
            MozaData data,
            DeviceDetectionState detectionState,
            Func<bool> isShuttingDown)
        {
            _desc = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            PortName = portName ?? throw new ArgumentNullException(nameof(portName));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _detectionState = detectionState ?? throw new ArgumentNullException(nameof(detectionState));
            _isShuttingDown = isShuttingDown ?? (() => false);

            // PID filter accepts only this peripheral's category — no "unknown
            // PID" fallback (discovery is registry-only by design). LastPortName
            // pinning targets THIS specific COM port, never a sibling's.
            _connection = new MozaSerialConnection(
                _desc.PidFilter,
                _desc.ProbeTarget,
                disableProbeFallback: () => true);
            _connection.CaptureLabel = _desc.CaptureLabelBase + "-" + ShortId(identity);
            _connection.LastPortName = portName;
            // On its OWN CDC pipe the peripheral is the root ("main", 0x12) device
            // — its frames identify as 0x12 (debug src=main), NOT the bus sub-device
            // id (pedals 0x19 / handbrake 0x1B) used when relayed by a base/hub.
            // Override so every read/write on this pipe targets 0x12.
            _deviceManager = new MozaDeviceManager(_connection, _pending, MozaProtocol.DeviceMain);
            // drivesTelemetry:false — this prober only enumerates the peripheral
            // and must never touch the primary TelemetrySender.
            _prober = new DeviceProber(_plugin, _connection, _deviceManager, _data, _detectionState, drivesTelemetry: false);

            _connection.MessageReceived += OnConnectionMessage;
            _connection.Disconnected += OnConnectionDisconnected;
        }

        /// <summary>Open the peripheral's COM port. Idempotent; re-pins the port.</summary>
        public bool TryConnect()
        {
            if (_disposed) return false;
            if (_connection.IsConnected) return true;
            _connection.LastPortName = PortName;
            bool ok = _connection.Connect();
            if (ok)
            {
                MozaLog.Info($"[Moza] Connected to standalone {_desc.CaptureLabelBase} ({_connection.DiscoveredPid} on {_connection.LastPortName})");
                // Registry PID classification + an open dedicated port IS proof of
                // presence on this topology, so show the tab immediately — don't
                // gate it on a binary ACK this device may never send. issueReads:
                // false until the self-probe confirms the device speaks binary.
                _desc.MarkDetected(_prober, false);
                // Still probe (self/root 0x00) so a device that DOES answer can
                // upgrade us to settings reads; Poll gates on _binaryConfirmed.
                Poll();
            }
            return ok;
        }

        /// <summary>
        /// While the binary channel is unconfirmed, (re)send the presence probe
        /// to the root device. On a dedicated pipe the peripheral IS the root
        /// ("main", 0x12) device — NOT the 0x19/0x1B sub-device address used when
        /// a base/hub relays the probe (docs/protocol/devices/usb-ids.md). Its
        /// own debug frames confirm this (src=main → swap(0x21)=0x12).
        /// The registry calls this each Refresh so a device that wasn't ready at
        /// connect still latches later. Gated on _binaryConfirmed (not the shared
        /// tab flag) so probing continues after the tab is shown on connect.
        /// </summary>
        public void Poll()
        {
            if (_disposed || !_connection.IsConnected || _binaryConfirmed) return;
            _deviceManager.SendPresenceProbe(MozaProtocol.DeviceMain);
        }

        private void OnConnectionDisconnected()
        {
            // Re-route ownership if this pipe owned the peripheral, so it
            // re-enumerates on whichever pipe answers next (mirrors
            // OnHubDisconnected). Reset the flag too so the UI tab hides.
            _binaryConfirmed = false;
            ClearOwnershipIfHeld();
            try { _pending.Clear(); } catch { }
        }

        private void OnConnectionMessage(byte[] data)
        {
            if (_disposed || _isShuttingDown()) return;
            if (data == null || data.Length < 2) return;

            // Firmware debug noise.
            if (data[0] == MozaProtocol.FirmwareDebugGroup) return;

            // Presence-probe ACK: 7e 00 80 swap(dev) chk → data = {0x80, dev}.
            // This pipe is dedicated and the PID already told us the category, so
            // ANY 0x80 ACK means the peripheral answered (it replies as the root
            // device 0x12, i.e. {0x80, 0x21}, not the 0x19/0x1B sub-device id).
            // The device just proved it speaks binary, so confirm the channel and
            // issue the settings reads.
            if (data.Length == 2 && data[0] == 0x80)
            {
                _binaryConfirmed = true;
                _desc.MarkDetected(_prober, true);
                return;
            }

            // The device answers as 0x12 (main) on this pipe, which the parser
            // would otherwise resolve to main-*/base-ambient-* commands. Pass the
            // command family as busHint so responses bind to THIS lane's commands
            // (mirrors the AB9 connection, which passes "ab9" for the same reason).
            var result = MozaResponseParser.Parse(data, _desc.CaptureLabelBase);
            if (!result.HasValue) return;
            var r = result.Value;
            if (r.Name == null) return;

            // Scope strictly to this lane's command family — anything else is
            // not ours (and shouldn't appear on this dedicated pipe anyway).
            if (!r.Name.StartsWith(_desc.CommandPrefix, StringComparison.Ordinal))
                return;

            _pending.NoteResponse(r.Name);
            _data.UpdateFromCommand(r.Name, r.IntValue);
            if (r.ArrayValue != null)
                _data.UpdateFromArray(r.Name, r.ArrayValue);
            _prober.DetectDevices(r.Name, r.IntValue, r.DeviceId);
        }

        private void ClearOwnershipIfHeld()
        {
            if (ReferenceEquals(_detectionState.PedalsOwner, _deviceManager))
            {
                _detectionState.PedalsDetected = false;
                _detectionState.PedalsOwner = null;
            }
            if (ReferenceEquals(_detectionState.HandbrakeOwner, _deviceManager))
            {
                _detectionState.HandbrakeDetected = false;
                _detectionState.HandbrakeOwner = null;
            }
        }

        private static string ShortId(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return "unknown";
            return identity.Length <= 8 ? identity : identity.Substring(identity.Length - 8);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _connection.MessageReceived -= OnConnectionMessage; } catch { }
            try { _connection.Disconnected -= OnConnectionDisconnected; } catch { }
            // Drop ownership before the pipe goes away so a stale owner ref
            // can't outlive its connection (the Disconnected event may not fire
            // on an explicit Dispose path).
            ClearOwnershipIfHeld();
            try { _deviceManager.Dispose(); } catch { }
            try { _connection.Dispose(); } catch { }
            try { _pending.Clear(); } catch { }
        }
    }
}
