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
        /// The bool is <c>issueReads</c> — this lane always passes false and issues
        /// <see cref="SettingsReadCommands"/> itself, because the shared
        /// <c>Mark*Detected</c> helpers early-return once the flag is latched.</summary>
        public Action<DeviceProber, bool> MarkDetected { get; }
        /// <summary>Reads this peripheral's shared detection flag (drives the UI tab).</summary>
        public Func<DeviceDetectionState, bool> IsDetected { get; }
        /// <summary>Settings this lane reads once the binary channel is confirmed.
        /// Every descriptor carries one: the shared <c>Mark*Detected</c> helpers latch
        /// on first sight and early-return, so a later <c>issueReads:true</c> can never
        /// deliver them — this list is a dedicated pipe's ONLY read path.</summary>
        public string[]? SettingsReadCommands { get; }
        /// <summary>Which shifter model this descriptor is (Hgp / Sgp), or Unknown for
        /// non-shifter peripherals. Routes this lane's <c>shifter-*</c> replies into the
        /// right per-device mirror; the SGP is the one with LEDs.</summary>
        public ShifterModelKind ShifterModel { get; }

        public StandalonePeripheralDescriptor(
            MozaDeviceCategory category,
            Func<string?, bool> pidFilter,
            MozaProbeTarget probeTarget,
            byte deviceId,
            string commandPrefix,
            string captureLabelBase,
            Action<DeviceProber, bool> markDetected,
            Func<DeviceDetectionState, bool> isDetected,
            string[]? settingsReadCommands = null,
            ShifterModelKind shifterModel = ShifterModelKind.Unknown)
        {
            Category = category;
            PidFilter = pidFilter ?? throw new ArgumentNullException(nameof(pidFilter));
            ProbeTarget = probeTarget;
            DeviceId = deviceId;
            CommandPrefix = commandPrefix ?? throw new ArgumentNullException(nameof(commandPrefix));
            CaptureLabelBase = captureLabelBase ?? throw new ArgumentNullException(nameof(captureLabelBase));
            MarkDetected = markDetected ?? throw new ArgumentNullException(nameof(markDetected));
            IsDetected = isDetected ?? throw new ArgumentNullException(nameof(isDetected));
            SettingsReadCommands = settingsReadCommands;
            ShifterModel = shifterModel;
        }

        // Directly-USB-attached peripherals with a config surface. Each reads its own
        // settings once the binary channel confirms, so the tab shows the device's
        // stored calibration instead of MozaData's placeholder defaults; the SGP list
        // adds its LED commands. A base/hub-relayed device reads via Mark*Detected's
        // own issueReads path instead.
        public static readonly StandalonePeripheralDescriptor Pedals =
            new StandalonePeripheralDescriptor(
                MozaDeviceCategory.Pedals,
                pid => MozaUsbIds.IsPedalsPid(pid),
                MozaProbeTarget.PedalsOnly,
                MozaProtocol.DevicePedals,
                "pedals-",
                "pedals",
                (prober, issueReads) => prober.MarkPedalsDetected(issueReads),
                s => s.PedalsDetected,
                DeviceProber.PedalsSettingsReadCommands);

        public static readonly StandalonePeripheralDescriptor Handbrake =
            new StandalonePeripheralDescriptor(
                MozaDeviceCategory.Handbrake,
                pid => MozaUsbIds.IsHandbrakePid(pid),
                MozaProbeTarget.HandbrakeOnly,
                MozaProtocol.DeviceHandbrake,
                "handbrake-",
                "handbrake",
                (prober, issueReads) => prober.MarkHandbrakeDetected(issueReads),
                s => s.HandbrakeDetected,
                DeviceProber.HandbrakeSettingsReadCommands);

        private static readonly string[] ShifterCommonReads =
            { "shifter-direction", "shifter-paddle-sync", "shifter-hid-mode", "shifter-apply-mode" };
        private static readonly string[] ShifterSgpReads =
            { "shifter-direction", "shifter-paddle-sync", "shifter-hid-mode", "shifter-apply-mode",
              "shifter-brightness", "shifter-colors" };

        // HGP: H-pattern shifter, no LEDs.
        public static readonly StandalonePeripheralDescriptor Hgp =
            new StandalonePeripheralDescriptor(
                MozaDeviceCategory.Shifter,
                pid => MozaUsbIds.IsShifterHgpPid(pid),
                MozaProbeTarget.ShifterOnly,
                MozaProtocol.DeviceHPattern,
                "shifter-",
                "shifter",
                (prober, issueReads) => prober.MarkHgpDetected(issueReads),
                s => s.HgpDetected,
                ShifterCommonReads,
                shifterModel: ShifterModelKind.Hgp);

        // SGP: sequential shifter with 2 configurable LEDs.
        public static readonly StandalonePeripheralDescriptor Sgp =
            new StandalonePeripheralDescriptor(
                MozaDeviceCategory.Shifter,
                pid => MozaUsbIds.IsShifterSgpPid(pid),
                MozaProbeTarget.ShifterOnly,
                MozaProtocol.DeviceSequential,
                "shifter-",
                "shifter",
                (prober, issueReads) => prober.MarkSgpDetected(issueReads),
                s => s.SgpDetected,
                ShifterSgpReads,
                shifterModel: ShifterModelKind.Sgp);

        /// <summary>Descriptor for a discovered port's category + PID, or null if
        /// unsupported. HGP and SGP share category <c>Shifter</c> and are disambiguated
        /// by PID (only the SGP has LEDs).</summary>
        public static StandalonePeripheralDescriptor? ForCategory(MozaDeviceCategory category, ushort pid = 0)
        {
            if (category == MozaDeviceCategory.Pedals) return Pedals;
            if (category == MozaDeviceCategory.Handbrake) return Handbrake;
            if (category == MozaDeviceCategory.Shifter)
                return MozaUsbIds.IsShifterSgpPid(pid) ? Sgp
                     : MozaUsbIds.IsShifterHgpPid(pid) ? Hgp
                     : null;
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
        // tab shows on connect; the settings reads wait for this.
        private volatile bool _binaryConfirmed;

        public string Identity { get; }
        public string PortName { get; private set; }
        public MozaDeviceCategory Category => _desc.Category;
        public bool IsConnected => _connection.IsConnected;
        public MozaSerialConnection Connection => _connection;
        public PendingResponseTracker PendingResponses => _pending;
        /// <summary>Which shifter model this lane is, or Unknown for pedals/handbrake.</summary>
        public ShifterModelKind ShifterModel => _desc.ShifterModel;
        /// <summary>True once the device answered a presence probe on this pipe — the
        /// gate on the settings reads (see <see cref="OnConnectionMessage"/>).</summary>
        public bool BinaryConfirmed => _binaryConfirmed;
        /// <summary>The shared detection flag this lane feeds (drives the UI tab).</summary>
        public bool SharedFlagSet => _desc.IsDetected(_detectionState);
        /// <summary>True when this lane's device manager is the recorded owner of its
        /// peripheral, i.e. writes for it route down THIS pipe.</summary>
        public bool OwnsPeripheral =>
            ReferenceEquals(_detectionState.PedalsOwner, _deviceManager)
            || ReferenceEquals(_detectionState.HandbrakeOwner, _deviceManager)
            || ReferenceEquals(_detectionState.HgpOwner, _deviceManager)
            || ReferenceEquals(_detectionState.SgpOwner, _deviceManager);

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
                MozaLog.Info($"[AZOM] Connected to standalone {_desc.CaptureLabelBase} ({_connection.DiscoveredPid} on {_connection.LastPortName})");
                // A shifter's model is known from its PID (the descriptor is Hgp or Sgp),
                // so MarkDetected below flips the correct per-model flag/owner directly —
                // the right dedicated tab shows immediately, no probe/timeout on this lane.
                // Registry PID classification + an open dedicated port IS proof of
                // presence on this topology, so show the tab immediately — don't
                // gate it on a binary ACK this device may never send. issueReads:
                // false until the self-probe confirms the device speaks binary.
                _desc.MarkDetected(_prober, false);
                // Probe now (self/root 0x00): the ACK confirms binary and
                // unlocks the settings reads.
                Poll();
            }
            return ok;
        }

        /// <summary>
        /// Send the presence probe to the root device. On a dedicated pipe the
        /// peripheral IS the root ("main", 0x12) device — NOT the 0x19/0x1B
        /// sub-device address used when a base/hub relays the probe
        /// (docs/protocol/devices/usb-ids.md). Its own debug frames confirm this
        /// (src=main → swap(0x21)=0x12).
        /// The registry calls this every Refresh (5 s) for the life of the
        /// connection. Until the first ACK it is the binary-channel probe that
        /// unlocks the settings reads; after that it is the lane's keepalive —
        /// the device sends nothing unasked, and the connection's 30 s read-idle
        /// detector closes a port that stays silent, which would drop the tab.
        /// </summary>
        public void Poll()
        {
            if (_disposed || !_connection.IsConnected) return;
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
                bool firstConfirm = !_binaryConfirmed;
                _binaryConfirmed = true;
                // issueReads:false — TryConnect already latched the flag, so the
                // shared Mark* helper early-returns and could not issue reads even if
                // asked. This call is only the idempotent re-latch for a lane whose
                // ownership was cleared by a disconnect.
                _desc.MarkDetected(_prober, false);
                // Binary channel confirmed — read the descriptor's settings once so
                // the tab reflects the device's stored values rather than defaults.
                // Paced (and off this serial read thread): the pedal list is 25
                // commands, and large startup bursts get dropped — see ReadSettingsPaced.
                if (firstConfirm && _desc.SettingsReadCommands != null)
                    _deviceManager.ReadSettingsPaced(_desc.SettingsReadCommands);
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
            // Shifter replies route into this lane's per-model mirror (this descriptor
            // knows whether it's the HGP or SGP); everything else uses the shared model.
            if (!_data.TryUpdateShifter(_desc.ShifterModel, r.Name, r.IntValue, r.ArrayValue))
            {
                _data.UpdateFromCommand(r.Name, r.IntValue);
                if (r.ArrayValue != null)
                    _data.UpdateFromArray(r.Name, r.ArrayValue);
            }
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
            if (ReferenceEquals(_detectionState.HgpOwner, _deviceManager))
            {
                _detectionState.HgpDetected = false;
                _detectionState.HgpOwner = null;
            }
            if (ReferenceEquals(_detectionState.SgpOwner, _deviceManager))
            {
                _detectionState.SgpDetected = false;
                _detectionState.SgpOwner = null;
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
