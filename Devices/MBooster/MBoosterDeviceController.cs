using System;
using System.Collections.Generic;
using System.Text;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.MBooster
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
        // One effect worker per possible HID axis slot (0/1/2). Each resolves
        // its own target device id LIVE (MotorDeviceForCurrentAxis) rather
        // than owning a fixed one — 0x12 (host) unless this controller
        // genuinely has more than one axis connected (a real chain), since a
        // standalone unit's sole pedal commonly reports on a non-zero axis
        // regardless of chain status. _workers[0] is the primary (owns the
        // shared keepalive + Brake Fade).
        private readonly MBoosterEffectWorker[] _workers;
        private readonly Func<MBoosterDeviceSettings?> _settingsLookup;
        private readonly Func<bool> _isShuttingDown;
        private volatile bool _detected;
        private volatile bool _disposed;

        // Identity is the USB device instance ID from MozaPortDiscovery —
        // canonical key for per-device settings in the profile dict. Survives
        // reconnects within the same USB port; a user moving the device to a
        // different USB hub may get a different instance id (Windows quirk),
        // which is the same way every other USB peripheral identity-tracks.
        // A ROUTED lane (mBooster on the wheelbase/hub pedal port — see the
        // routed constructor) uses a synthetic "routedpedals:<port>" identity
        // instead; both re-key to "mbooster:<serial>" once interrogated, so
        // the same physical unit keeps its settings across hookups.
        public string Identity { get; }

        // Sub-device id this lane's mBooster answers as. A unit on its own
        // USB CDC pipe is the pipe's main device (0x12); routed through a
        // wheelbase/hub it is that bus's pedals sub-device (0x19) — the same
        // id Pit House addresses in that hookup, and the same relayed-id
        // pattern the shifter uses (0x12 on USB, 0x1A relayed).
        public byte HostDeviceId { get; } = MozaProtocol.DeviceMain;
        // HostDeviceId nibble-swapped — the source byte inbound frames carry.
        private readonly byte _swappedHostId = 0x21;
        // False for a routed lane: the connection belongs to the base/hub
        // manager — never open/close/pin/dispose it from here, and filter
        // inbound traffic to our sub-device id (the shared pipe carries
        // every peripheral's frames).
        private readonly bool _ownsConnection = true;
        public bool IsRouted => !_ownsConnection;

        // Motor/config device ids this lane may address: the whole chain id set
        // on a dedicated USB pipe; on a ROUTED lane it starts as the host
        // sub-device id alone and grows only as chained units PROVE themselves
        // (see DiscoverRoutedChainDevice). 0x1d/0x1e are other peripherals' bus
        // ids on a base/hub pipe (0x1c is the E-stop), so a routed lane must
        // never spray keepalives, disables or writes at an id that hasn't
        // identified itself as an mBooster.
        //
        // Volatile whole-array swap rather than in-place mutation: the 50 Hz
        // effect workers and SendAllDisableFrames iterate this without a lock.
        private volatile byte[] _motorIds = MozaMBoosterProtocol.MotorDeviceIds;
        public byte[] MotorIds => _motorIds;

        // Port name at construction time. May change on reconnect — read live
        // via Connection.LastPortName if needed.
        public string PortName { get; private set; }

        // Windows Container ID (from MozaPortDiscovery) — identical across the
        // CDC + HID interfaces of this one physical mBooster. Used by the
        // registry to pair the HID axis stream to this CDC lane. Empty when the
        // registry key had none (some driver stacks / Wine).
        public string ContainerId { get; }

        // Device-reported identity, learned over the Moza wire (group 0x10 serial
        // read + group 7 model-name + group 9 presence). Capture-verified that the
        // mBooster answers these exactly like the wheelbase — see
        // docs/protocol/devices/mbooster.md. Null/0 until the reads reply (or if
        // the firmware never answers, in which case identity stays the transport
        // instance id).
        public string? Serial { get; private set; }
        public string? ModelName { get; private set; }
        public int SubDeviceCount { get; private set; } = -1;

        // Load-cell force (kg) the DEVICE itself reports as its Max Threshold —
        // the raw 100% point of its HID axis. Read back by RequestCalibrationReads
        // (mbooster-brake-threshold); -1 until it answers. Live only, never
        // persisted and never copied into MBoosterDeviceSettings.MaxThresholdKg:
        // that field's -1 means "user set no override", and seeding it would make
        // the plugin start writing the value back on every connect. Surfaced in
        // Diagnostics (see DiagnosticsTextBuilder) as a read-back sanity check.
        // Volatile: written on the serial read thread, read by the HID thread
        // and the UI.
        private volatile float _deviceReportedMaxThresholdKg = -1;
        public float DeviceReportedMaxThresholdKg
        {
            get => _deviceReportedMaxThresholdKg;
            private set => _deviceReportedMaxThresholdKg = value;
        }

        // Which pedal slots the device reports physically connected, indexed by
        // HID axis (0 = throttle/Rx, 1 = brake/Ry, 2 = clutch/Rz — the same
        // throttle/brake/clutch order the axes default to). Parsed from the
        // device's "PD Linked:[T x B y C z]" group-0x0E diagnostic. null until
        // that line arrives (the device streams it only under some conditions);
        // when null the UI falls back to showing every detected axis. Volatile
        // reference swap so the UI thread sees a consistent array.
        private volatile bool[]? _connectedAxes;
        public bool[]? ConnectedAxes => _connectedAxes;

        // Per-axis pedal type from the device's "type: active/passive pedal"
        // diagnostic: 0 = unknown / not connected, 1 = active (a motorized
        // mBooster — can play vibration effects), 2 = passive (no motor, e.g. a
        // CRP2 — effects don't apply). Indexed like ConnectedAxes. null until the
        // device streams the diagnostic. Used by the UI to hide effect controls
        // for passive pedals, and — critically — to tell a real multi-unit chain
        // from ONE mBooster hosting passive pedals: see ActiveAxisCount.
        private volatile byte[]? _axisTypes;
        public byte[]? AxisTypes => _axisTypes;

        // Which of the 3 diagnostic slots (T/B/C) have had a "<Pedal> pedal
        // is …" line parsed this session. Distinct from _axisTypes having a
        // non-zero entry: a pedal reported "not connected !" is type 0, so the
        // types array alone can't tell "reported absent" from "not reported
        // yet". The device emits all three lines ~10ms apart inside one
        // heartbeat block, and the routing verdict must not be announced (or
        // acted on) from a half-read block — see LogRoutingDecision.
        private readonly bool[] _axisTypeSeen = new bool[3];

        /// <summary>
        /// Whether the device has reported a type for every pedal slot the
        /// diagnostic covers, i.e. a WHOLE block has been read. The three lines
        /// arrive ~10ms apart, so a half-read block would momentarily under-count
        /// the active pedals — on a genuine chain that flips routing to the host
        /// for the couple of 50 Hz effect ticks in between. Everything that acts
        /// on <see cref="ActiveAxisCount"/> waits for this. Capped at 3 slots:
        /// the long-form 4-axis devices still only describe T/B/C.
        /// </summary>
        public bool AxisTypesComplete
        {
            get
            {
                if (_axisTypes == null) return false;
                int slots = Math.Min(_axisTypeSeen.Length, Math.Max(1, AxisCount));
                for (int i = 0; i < slots; i++)
                    if (!_axisTypeSeen[i]) return false;
                return true;
            }
        }

        /// <summary>
        /// How many of this lane's pedals are ACTIVE (motorized mBooster units,
        /// type 1) — the only signal that identifies a genuine multi-unit chain.
        /// -1 = the device hasn't streamed a complete type diagnostic yet (see
        /// <see cref="AxisTypesComplete"/>).
        ///
        /// This is NOT the connected-pedal count: one mBooster commonly hosts
        /// passive pedals (a CRP2 throttle/clutch) on the same lane, which
        /// "PD Linked" reports as connected exactly like a chained unit, and
        /// which the presence read can't separate either (a standalone unit
        /// reports the same [00 02] as a 2-pedal chain). Support bundle
        /// KY3HK4QP: one active brake + two passive pedals, all living at 0x12,
        /// was read as a 3-unit chain — every brake write went to 0x1d and drew
        /// zero responses while 0x12's writes were all echoed. See
        /// docs/protocol/devices/mbooster.md "Chain topology".
        /// </summary>
        public int ActiveAxisCount
        {
            get
            {
                var types = _axisTypes;
                if (types == null || !AxisTypesComplete) return -1;
                int n = 0;
                foreach (var t in types) if (t == 1) n++;
                return n;
            }
        }

        /// <summary>
        /// Whether axis <paramref name="axisIndex"/> has a motor, i.e. can play
        /// vibration effects. True when the type diagnostic hasn't arrived yet
        /// (best-effort, same convention as <see cref="AxisTypes"/>'s UI use) so
        /// nothing regresses before it lands.
        /// </summary>
        public bool IsAxisMotorized(int axisIndex)
        {
            var types = _axisTypes;
            if (types == null || !AxisTypesComplete) return true;
            return axisIndex >= 0 && axisIndex < types.Length && types[axisIndex] == 1;
        }

        /// <summary>Axis index of this lane's SOLE active (motorized) pedal, or
        /// -1 when the type diagnostic is missing or more than one axis is
        /// active. The counterpart to <see cref="SoleConnectedAxis"/> for the
        /// motor/config routing decision.</summary>
        public int SoleActiveAxis()
        {
            var types = _axisTypes;
            if (types == null || !AxisTypesComplete) return -1;
            int sole = -1, count = 0;
            for (int i = 0; i < types.Length; i++)
                if (types[i] == 1) { sole = i; count++; }
            return count == 1 ? sole : -1;
        }

        // Serial arrives in two halves (part A = selector 0, part B = selector 1);
        // full serial = A + B (32 ASCII chars). Held until both land.
        private string _serialPartA = "";
        private string _serialPartB = "";

        public bool Detected => _detected;
        public bool IsConnected => _connection.IsConnected;
        public MozaSerialConnection Connection => _connection;

        // Latest HID axis value (0..1), AFTER Pedal Feel shaping (deadzone,
        // max force, input curve). Updated by MozaHidReader via the
        // registry; published as a property so the UI panel can show the bar.
        public double LastHidPosition { get; internal set; }

        // Same signal, but BEFORE the input curve (i.e. after deadzone/max
        // force only) — 0..100. Lets the UI place a live position marker on
        // the Pedal Feel input curve showing exactly what it receives, since
        // LastHidPosition is already past that point. See
        // MozaMBoosterRegistry.OnHidAxisUpdate.
        public double LastRawPercentPreCurve { get; internal set; }

        // GenericDesktop axis usages 0x30..0x37 — a chain host exposes at most
        // this many pedal axes on one HID report.
        public const int MaxAxes = 8;

        // Per-axis normalized position (0..1) for a multi-pedal chain — axis 0
        // is the master unit's pedal, axis 1 the 2nd chained device, etc.
        // Written per axis by MozaMBoosterRegistry.OnHidAxisUpdate; read whole
        // in MergePositions. LastHidPosition above mirrors axis 0 for the UI
        // position bar. Unlocked: on the x86 build a concurrent read of a double
        // can tear (not last-value-wins), so every consumer clamps to 0..1 and a
        // torn sample costs one tick of a wrong amplitude, never a crash.
        public readonly double[] LastAxisPositions = new double[MaxAxes];

        // Per-axis pre-input-curve percent (0..100) — the same signal as
        // LastRawPercentPreCurve (after deadzone/max-force, before the input
        // curve) but for EVERY pedal, so the settings tab's live curve markers
        // track whichever pedal is selected, not just the master. NOTE: since
        // MozaMBoosterRegistry.OnHidAxisUpdate added the host-side Max
        // Threshold rescale, this is "% of Threshold's span" (the Sim Input
        // Mapping curve's own input domain) — see LastAxisRawPercentPreThreshold
        // below for the true raw reading (% of Max Force's span) instead.
        public readonly double[] LastAxisRawPercentPreCurve = new double[MaxAxes];

        // Per-axis TRUE raw HID percent (0..100), captured BEFORE the host-side
        // Max Threshold rescale (see MozaMBoosterRegistry.OnHidAxisUpdate) —
        // i.e. genuinely "% of Max Force's own hardware ceiling", the physical
        // force the user is actually applying to the pedal. This is the
        // Pedal Feel curve's real input domain (Deadzone-Max Force span), and
        // what the "Input Force" live label/marker should show — unlike
        // LastAxisRawPercentPreCurve, which is now post-Threshold-rescale and
        // represents the Sim Input Mapping curve's own (different) domain.
        public readonly double[] LastAxisRawPercentPreThreshold = new double[MaxAxes];

        // Highest axis index + 1 the HID has reported for this lane: 1 for a
        // lone pedal, up to 3 for a full chain. 0 until the first axis update.
        public int AxisCount { get; internal set; }

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

        /// <summary>
        /// Fired once when the device's full 32-char Moza serial has been
        /// interrogated (both halves in). Args: transport identity, serial.
        /// The plugin re-keys per-device settings from the transport identity to
        /// the serial so they follow the physical unit across USB ports.
        /// </summary>
        public event Action<string, string>? SerialResolved;

        /// <summary>
        /// Fired when the device's own connectivity diagnostic ("PD Linked" /
        /// "Pedals connected state") has been parsed — i.e. LIVE data, never a
        /// seed. Arg: the role-indexed [T,B,C] connected flags. The plugin
        /// persists these per device so the next controller (plugin restart,
        /// next session) can be seeded instead of waiting for the broadcast.
        /// </summary>
        public event Action<bool[]>? ConnectivityResolved;

        /// <summary>
        /// Fired when the active/passive pedal-type diagnostic settles (or
        /// changes) the motor/config device-id routing for this lane — see
        /// <see cref="MotorDeviceForCurrentAxis"/>. Arg: true on the FIRST
        /// resolution of the session. The plugin re-applies the lane's hardware
        /// settings, because the detection-edge apply ran before this arrived and
        /// may have addressed the wrong device id.
        /// </summary>
        public event Action<bool>? RoutingResolved;

        /// <summary>
        /// Fired when the model-name read answers (once per distinct value).
        /// The routed-lane probe uses this to discriminate an mBooster on a
        /// base/hub pedal port from plain pedals before registering the
        /// lane — both answer the same identity groups at dev 0x19.
        /// </summary>
        public event Action<string>? ModelNameResolved;

        /// <summary>
        /// Every printable group-0x0E firmware-log line this lane emits, raw and
        /// un-deduped. The device narrates its own calibration routines here
        /// ("Pedal Calib Start" / "Backward" / "Forward" /
        /// "pressure Calculating" / "End", "B-PD :Min … Max… Speed… B-L …",
        /// "Motor Locate Start", "max_err … compen_theta_e …",
        /// "Rotor Not Located"), which is the only real progress signal either
        /// routine has — see <see cref="MBoosterCalibrationRunner"/>.
        /// Fires on the connection's dispatch thread.
        /// </summary>
        public event Action<string>? FirmwareLogLine;

        /// <summary>
        /// Seed <see cref="ConnectedAxes"/> from a persisted last-known value.
        /// No-op when live connectivity has already been parsed (or on a null/
        /// empty seed) — the device's own diagnostic always wins. Arms the
        /// phantom-axis merge guard, worker gating, and role-map narrowing from
        /// the first HID event instead of after the once-a-minute broadcast.
        /// </summary>
        public void SeedConnectedAxes(bool[]? connected)
        {
            if (connected == null || connected.Length == 0) return;
            if (_connectedAxes != null) return;
            _connectedAxes = (bool[])connected.Clone();
            MozaLog.Info(
                $"[AZOM/mBooster] {ShortIdentity(Identity)} seeded connectivity from cache: " +
                $"T={connected.Length > 0 && connected[0]} B={connected.Length > 1 && connected[1]} C={connected.Length > 2 && connected[2]} " +
                "(live diagnostic will confirm/override)");
            RecomputeChainRoleMap();
        }

        public MBoosterDeviceController(
            string identity,
            string portName,
            Func<MBoosterDeviceSettings?> settingsLookup,
            Func<bool> isShuttingDown,
            Func<bool>? disableProbeFallback = null,
            Func<string, double>? customEffectFormulaEvaluator = null,
            string containerId = "")
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            PortName = portName ?? throw new ArgumentNullException(nameof(portName));
            ContainerId = containerId ?? string.Empty;
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

            // One worker per possible HID axis slot (0/1/2). Which physical
            // device each one's frames actually address is resolved live per
            // tick (MBoosterEffectWorker.TargetDevice → MotorDeviceForCurrentAxis),
            // not fixed here — ConnectedAxes isn't known yet at construction
            // time (it arrives asynchronously from a "PD Linked" diagnostic).
            var motorIds = MozaMBoosterProtocol.MotorDeviceIds; // {0x12, 0x1d, 0x1e}
            _workers = new MBoosterEffectWorker[motorIds.Length];
            for (int i = 0; i < motorIds.Length; i++)
                _workers[i] = new MBoosterEffectWorker(
                    this, _settingsLookup, _isShuttingDown, customEffectFormulaEvaluator,
                    pedalAxisIndex: i, isPrimary: i == 0);
        }

        /// <summary>
        /// ROUTED-lane constructor: an mBooster attached to a wheelbase/hub
        /// pedal port (RJ45) instead of USB. There is no dedicated CDC pipe —
        /// the base tunnels it as its pedals sub-device (0x19), same as any
        /// other relayed peripheral (Pit House drives it this way too), so
        /// this lane shares the owner's <paramref name="sharedConnection"/>:
        /// frames go out addressed to <paramref name="hostDeviceId"/>, inbound
        /// traffic is filtered to that id's swapped source byte, and the
        /// pipe's lifecycle (open/close/dispose/port pinning/capture label)
        /// stays entirely with its owning manager. Positions come from the
        /// base HID / merged MozaData (mirrored in by the registry), never
        /// from an mBooster HID pairing.
        /// </summary>
        public MBoosterDeviceController(
            string identity,
            MozaSerialConnection sharedConnection,
            byte hostDeviceId,
            string portLabel,
            Func<MBoosterDeviceSettings?> settingsLookup,
            Func<bool> isShuttingDown,
            Func<string, double>? customEffectFormulaEvaluator = null)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _connection = sharedConnection ?? throw new ArgumentNullException(nameof(sharedConnection));
            PortName = portLabel ?? "";
            ContainerId = string.Empty;
            _settingsLookup = settingsLookup ?? throw new ArgumentNullException(nameof(settingsLookup));
            _isShuttingDown = isShuttingDown ?? (() => false);
            _ownsConnection = false;
            HostDeviceId = hostDeviceId;
            _swappedHostId = (byte)(((hostDeviceId & 0x0f) << 4) | (hostDeviceId >> 4));
            _motorIds = new[] { hostDeviceId };
            // The base HID reports the full 3-axis pedal surface regardless of
            // hookup; connectivity (ConnectedAxes) narrows it as usual.
            AxisCount = 3;

            _connection.MessageReceived += OnConnectionMessage;
            _connection.Disconnected += OnConnectionDisconnected;

            _workers = new MBoosterEffectWorker[MozaMBoosterProtocol.MotorDeviceIds.Length];
            for (int i = 0; i < _workers.Length; i++)
                _workers[i] = new MBoosterEffectWorker(
                    this, _settingsLookup, _isShuttingDown, customEffectFormulaEvaluator,
                    pedalAxisIndex: i, isPrimary: i == 0);
        }

        /// <summary>Start the per-axis effect workers — the registry calls this
        /// when a ROUTED lane is registered (a USB lane starts them from its
        /// own <see cref="TryConnect"/>).</summary>
        public void StartWorkers()
        {
            foreach (var w in _workers) w.Start();
        }

        /// <summary>The effect worker driving pedal <paramref name="pedalIndex"/>
        /// (0 = master/host), or null if out of range.</summary>
        private MBoosterEffectWorker? WorkerFor(int pedalIndex) =>
            pedalIndex >= 0 && pedalIndex < _workers.Length ? _workers[pedalIndex] : null;

        /// <summary>The motor/config device id for a pedal by HID axis index
        /// (0x12 host, 0x1d/0x1e chain ports) — used to address a chained
        /// mBooster unit's own load-cell config. Master (0x12) if out of range.
        /// Only meaningful for a GENUINE physical chain (multiple mBooster
        /// units on one connection) — see <see cref="MotorDeviceForCurrentAxis"/>,
        /// which is almost always what callers actually want instead.</summary>
        public static byte MotorDeviceForAxis(int axisIndex)
        {
            var ids = MozaMBoosterProtocol.MotorDeviceIds;
            return (axisIndex >= 0 && axisIndex < ids.Length) ? ids[axisIndex] : MozaProtocol.DeviceMain;
        }

        /// <summary>
        /// The motor/config device id for THIS controller's pedal at HID axis
        /// <paramref name="axisIndex"/> — 0x12 (host) unless this controller
        /// genuinely has more than one axis physically connected (a real
        /// chain of multiple mBooster units on one connection), in which case
        /// chain position maps to 0x1d/0x1e via <see cref="MotorDeviceForAxis"/>.
        /// A STANDALONE unit's sole pedal always lives at 0x12 regardless of
        /// which logical HID axis (Rx/Ry/Rz) it happens to report on — the
        /// axis-index-to-device-id mapping only applies when that axis index
        /// corresponds to a real separate physical unit, not to wherever a
        /// lone pedal's data happens to land in the report descriptor.
        /// Chain-ness comes from <see cref="ActiveAxisCount"/> — the count of
        /// MOTORIZED pedals from the "type: active/passive pedal" diagnostic.
        /// A lane with one active pedal is a single unit no matter how many
        /// passive pedals hang off it, and a single unit keeps everything at
        /// 0x12 regardless of which logical HID axis (Rx/Ry/Rz) its pedal
        /// happens to report on.
        ///
        /// Neither of the two older signals can make this call:
        /// <see cref="ConnectedAxes"/> ("PD Linked") counts passive pedals as
        /// connected exactly like chained units, and a confirmed STANDALONE
        /// unit reports presence [00 02] (<see cref="SubDeviceCount"/> 2), the
        /// same bytes as a 2-pedal chain. Both remain the fallback for the
        /// window before the type diagnostic lands (it streams seconds after
        /// connect, and sometimes first as an unparseable short form
        /// "PD Linked: 1") so a real chain isn't collapsed onto the master
        /// 0x12 for that window — brake effects would fire from the throttle
        /// motor. Once types are known they win outright.
        ///
        /// For a genuine multi-active chain on a USB pipe the ids are
        /// role-based (0x12/0x1d/0x1e per axis).
        ///
        /// A ROUTED lane always falls back to the host sub-device here, even
        /// when it turns out to carry a real chain (bug reports GVT5H8B8 /
        /// 34JAASN5): the only thing that can move one of its pedals onto a
        /// chained id is the calibration-derived role map, which by
        /// construction only ever contains ids that ANSWERED all six min/max
        /// reads. So a chained id that was discovered but stays silent is
        /// never written to — see DiscoverRoutedChainDevice.
        /// </summary>
        public byte MotorDeviceForCurrentAxis(int axisIndex)
        {
            // Routed lane: the safe fallback is the tunneled pedal sub-device.
            // Chained ids are reached only via the role map (see the remarks).
            if (!_ownsConnection) return HostDeviceId;
            int activeCount = ActiveAxisCount;
            if (activeCount >= 0)
                return activeCount > 1 ? MotorDeviceForAxis(axisIndex) : MozaProtocol.DeviceMain;
            // Types not reported yet — fall back to the older, coarser signals.
            var connected = _connectedAxes;
            int connectedCount = 0;
            if (connected != null)
                foreach (var b in connected) if (b) connectedCount++;
            bool isChain = connected != null ? connectedCount > 1 : SubDeviceCount > 1;
            return isChain ? MotorDeviceForAxis(axisIndex) : MozaProtocol.DeviceMain;
        }

        /// <summary>
        /// Whether HID axis <paramref name="axisIndex"/> is a genuinely wired
        /// pedal rather than an unused GenericDesktop usage a chain-capable
        /// hub's report descriptor always exposes (Rx/Ry/Rz) regardless of how
        /// many pedals are actually plugged in. Trusts the parsed "PD Linked"
        /// diagnostic (<see cref="ConnectedAxes"/>) once it arrives; before
        /// that, treats every axis as real if <see cref="SubDeviceCount"/>
        /// already confirmed a multi-motor chain at connect, else assumes only
        /// axis 0 is wired. Same convention <see cref="MBoosterEffectWorker"/>
        /// uses to gate its own per-pedal tick — callers that resolve a HID
        /// axis's role (see <see cref="MozaMBoosterRegistry.ResolveAxisRole"/>)
        /// need this too: raw <see cref="AxisCount"/> alone can't tell a real
        /// chain from a single connected pedal on a chain-capable hub, so
        /// using it directly silently overrides that pedal's own configured
        /// Role with the axis-order default (Throttle/Brake/Clutch by index).
        /// </summary>
        public bool IsAxisConnected(int axisIndex)
        {
            var connected = _connectedAxes;
            if (connected != null)
                return axisIndex < connected.Length && connected[axisIndex];
            if (SubDeviceCount > 1)
                return axisIndex < Math.Max(1, AxisCount);
            return axisIndex == 0;
        }

        /// <summary>
        /// Device id for a pedal CONFIG write (calibration, Pedal Feel, Sim
        /// Input) — the resolved role map when it exists, else the host
        /// <see cref="MozaProtocol.DeviceMain"/>. Never the count-based chain
        /// guess that <see cref="MotorDeviceForCurrentAxis"/> falls back to.
        ///
        /// <para>Config writes are flash-committed on the unit that receives
        /// them, so a guessed id does durable damage: on a single unit hosting a
        /// passive pedal the whole write batch lands on <c>0x1d</c>, which does
        /// not exist and never answers, and the pedal keeps whatever it had
        /// (bug report A6N521CS — 28 group-0x24 writes to 0x1d, nothing to the
        /// real device until the type verdict 6.2 s later); on a genuine chain
        /// the wrong unit commits another pedal's curve and
        /// <c>RoutingResolved</c>'s re-apply only writes the corrected id, so
        /// the foreign values stay (docs/protocol/devices/mbooster.md, bundle
        /// KY3HK4QP). <c>0x12</c> is the one id always present on the pipe, so
        /// it is the only safe guess.</para>
        ///
        /// <para>The count-based guess is wrong on its own terms anyway: pedal
        /// COUNT is explicitly not the chain discriminator — one mBooster
        /// commonly hosts passive pedals that the connectivity diagnostic
        /// reports exactly like chained units, and only the per-pedal active/
        /// passive TYPE separates them (see mbooster.md "Active vs passive is
        /// the chain discriminator"). Until <see cref="ActiveAxisCount"/> is
        /// known there is nothing to route with.</para>
        ///
        /// <para>Effects deliberately keep using
        /// <see cref="MotorDeviceForRole"/>: a motor frame is transient, and
        /// collapsing every axis onto <c>0x12</c> during the unresolved window
        /// would make several axes' workers race for the host's motor (the
        /// hazard <c>MBoosterEffectWorker.IsPedalAxisConnected</c> guards, whose
        /// own <see cref="IsAxisMotorized"/> gate is fail-open in that same
        /// window).</para>
        /// </summary>
        public byte ConfigDeviceForRole(int roleIndex, int axisFallback)
        {
            var map = _roleToDevice;
            if (map != null && roleIndex >= 0 && !RoleIsAmbiguous(roleIndex)
                && map.TryGetValue(roleIndex, out var dev))
                return dev;
            // ROUTED lane: neither fallback below applies. The reasoning above
            // ("0x12 is the one id always present on the pipe") holds for a
            // dedicated USB pipe only — on a shared base/hub pipe 0x12 is the
            // WHEELBASE MAIN and the mBooster is the tunneled pedal sub-device,
            // so both fallbacks would flash pedal registers into the wrong
            // device entirely. Same guard MotorDeviceForCurrentAxis carries.
            // A routed chain's second unit is reached only through the map
            // above, once it has answered — see DiscoverRoutedChainDevice.
            if (!_ownsConnection) return HostDeviceId;
            // Types known and a genuine multi-motor chain — the axis mapping is
            // real, not a guess.
            if (ActiveAxisCount > 1) return MotorDeviceForAxis(axisFallback);
            return MozaProtocol.DeviceMain;
        }

        /// <summary>
        /// The motor device id for a pedal ROLE (0=Throttle,1=Brake,2=Clutch),
        /// using the calibration-derived chain map (see
        /// <see cref="RecomputeChainRoleMap"/>) so effects reach the physical
        /// pedal that role belongs to even when the chain plug order doesn't
        /// match the HID axis order. Falls back to the axis-index mapping
        /// (<see cref="MotorDeviceForCurrentAxis"/>) when the role isn't
        /// resolved yet — i.e. never routes worse than before the map exists.
        /// </summary>
        public byte MotorDeviceForRole(int roleIndex, int axisFallback)
        {
            var map = _roleToDevice;
            if (map != null && roleIndex >= 0 && !RoleIsAmbiguous(roleIndex)
                && map.TryGetValue(roleIndex, out var dev))
                return dev;
            return MotorDeviceForCurrentAxis(axisFallback);
        }

        /// <summary>
        /// Role index (0=Throttle, 1=Brake, 2=Clutch, -1=unknown) a HID axis
        /// currently holds. Resolved against the CONNECTED axis count, never
        /// the raw HID axis count — a chain-capable hub reports 3-4 axes
        /// regardless of how many pedals are wired, and passing the raw count
        /// drops <see cref="MozaMBoosterRegistry.ResolveAxisRole"/> into its
        /// axis-order fallback.
        /// </summary>
        public int RoleIndexForAxis(int axisIndex)
        {
            int axisCount = ConnectedAxisIndices().Count;
            if (axisCount <= 0) axisCount = 1;
            var role = MozaMBoosterRegistry.ResolveAxisRole(CurrentSettings, axisIndex, axisCount);
            return role == MBoosterRole.Throttle ? 0
                 : role == MBoosterRole.Brake ? 1
                 : role == MBoosterRole.Clutch ? 2 : -1;
        }

        /// <summary>
        /// Device id an axis's own PHYSICAL (per-unit) calibration writes go to
        /// — travel, endstop, damping, threshold, sensor ratio, and the two
        /// calibration ROUTINES. Routed by role through the chain map, not by
        /// raw HID axis; see <see cref="ConfigDeviceForRole"/>. Single shared
        /// implementation so the UI sliders, the connect-time apply and
        /// <see cref="MBoosterCalibrationRunner"/> can never disagree about
        /// which physical pedal they are addressing.
        ///
        /// <para>Resolves through <see cref="ConfigDeviceForRole"/>, so an
        /// unresolved chain falls back to the host rather than to a guessed
        /// 0x1d/0x1e — these writes are flash-committed by whichever unit
        /// receives them. The matching calibration READS already had this
        /// behaviour via <see cref="MotorDeviceForRole(int)"/>.</para>
        /// </summary>
        public byte CalibDeviceForAxis(int axisIndex)
            => ConfigDeviceForRole(RoleIndexForAxis(axisIndex), axisIndex);

        /// <summary>
        /// Whether this axis may push the brake-named SINGLETON registers —
        /// Travel (0x84/0x85), End Stop (0xB2), Natural Friction (0xAE),
        /// Virtual Damping (0xAD), Segmented Damping (0xB7), Deadzone/Max
        /// Force/feel curve (0xAB), Max Threshold (0xB3), Sensor Ratio (0x1A).
        /// None of them carries a per-pedal selector, so they configure
        /// whichever pedal owns that hardware on the device id they are sent
        /// to.
        ///
        /// Normally every motorized axis resolves to its own device id and the
        /// answer is yes for all of them. But when two motorized axes land on
        /// the SAME id — a routed chain whose role map hasn't resolved, so both
        /// fall back to the host — they would take turns overwriting one
        /// register set. Bug 34JAASN5 is exactly that: throttle 3.8/35.9mm and
        /// brake 17.6/49.7mm hitting 0x19's travel registers 74ms apart on
        /// every apply, last writer winning.
        ///
        /// In that case exactly one axis owns them: the pedal whose role the
        /// registers are named for (Brake), else the lowest axis index, so the
        /// choice is stable across applies rather than order-dependent.
        /// </summary>
        public bool OwnsSingletonRegisters(int axisIndex)
        {
            if (!IsAxisMotorized(axisIndex)) return false;
            byte mine = CalibDeviceForAxis(axisIndex);

            int bestAxis = axisIndex;
            int bestRank = SingletonOwnerRank(axisIndex);
            for (int a = 0; a < MaxAxes; a++)
            {
                if (a == axisIndex || !IsAxisMotorized(a)) continue;
                if (CalibDeviceForAxis(a) != mine) continue;
                int rank = SingletonOwnerRank(a);
                if (rank < bestRank || (rank == bestRank && a < bestAxis))
                {
                    bestRank = rank;
                    bestAxis = a;
                }
            }
            return bestAxis == axisIndex;
        }

        // Brake wins (the registers are its own hardware), then throttle, then
        // clutch, then an unresolved role.
        private int SingletonOwnerRank(int axisIndex)
        {
            switch (RoleIndexForAxis(axisIndex))
            {
                case 1: return 0;   // Brake
                case 0: return 1;   // Throttle
                case 2: return 2;   // Clutch
                default: return 3;
            }
        }

        /// <summary>Command-name prefix ("throttle"/"brake"/"clutch") for an
        /// axis's role, or null when unresolved.</summary>
        public string? RolePrefixForAxis(int axisIndex)
        {
            switch (RoleIndexForAxis(axisIndex))
            {
                case 0: return "throttle";
                case 1: return "brake";
                case 2: return "clutch";
                default: return null;
            }
        }

        /// <summary>
        /// Whether more than one connected axis on this lane resolves to
        /// <paramref name="roleIndex"/> — i.e. two physical pedals claim one
        /// role, which is never valid (see
        /// <see cref="MozaMBoosterRegistry.ResolveAxisRole"/>).
        ///
        /// The role→device map is keyed by role alone, so an ambiguous role
        /// would hand every axis claiming it the SAME motor device: two
        /// pedals' effect streams on one motor, the other pedal silent — a
        /// user reported exactly this as effects playing on the wrong pedal.
        /// Fall back to the axis-index mapping for those, which is distinct
        /// per axis by construction; it may not match a chain's plug order
        /// (that's what the map is for), but it never collides.
        /// </summary>
        private bool RoleIsAmbiguous(int roleIndex)
        {
            if (roleIndex < 0) return false;
            var s = CurrentSettings;
            int axisCount = ConnectedAxisCount;
            int raw = AxisCount > 0 ? AxisCount : 1;
            if (raw > MaxAxes) raw = MaxAxes;
            int count = 0;
            for (int a = 0; a < raw; a++)
            {
                if (!IsAxisConnected(a)) continue;
                if (RoleIndexOf(MozaMBoosterRegistry.ResolveAxisRole(s, a, axisCount)) != roleIndex) continue;
                if (++count > 1) { LogRoleAmbiguityOnce(roleIndex); return true; }
            }
            return false;
        }

        // Warn once per (lane, role): this is a settings state the user can
        // see and fix (two pedal rows showing the same role), and it silently
        // costs them one pedal's position and sends both pedals' effects to
        // one motor, so a support bundle needs to say so out loud.
        private readonly HashSet<int> _roleAmbiguityLogged = new HashSet<int>();
        private void LogRoleAmbiguityOnce(int roleIndex)
        {
            lock (_roleAmbiguityLogged)
                if (!_roleAmbiguityLogged.Add(roleIndex)) return;
            MozaLog.Warn(
                $"[AZOM/mBooster] {ShortIdentity(Identity)}: two connected pedals both resolve to " +
                $"'{RoleName(roleIndex)}'. Effects for that role route by axis index instead of the " +
                $"chain map, and one pedal's position is dropped — assign each pedal a distinct role.");
        }

        /// <summary>Role → the 0/1/2 index the chain map and the motor-frame
        /// addressing use (-1 = Disabled/none).</summary>
        internal static int RoleIndexOf(MBoosterRole role) =>
            role == MBoosterRole.Throttle ? 0
            : role == MBoosterRole.Brake ? 1
            : role == MBoosterRole.Clutch ? 2 : -1;

        /// <summary>
        /// How many of this lane's axes are genuinely wired pedals, per
        /// <see cref="IsAxisConnected"/> — the count every role resolution on
        /// this lane must pass to
        /// <see cref="MozaMBoosterRegistry.ResolveAxisRole"/>, since raw
        /// <see cref="AxisCount"/> is 3 on any chain-capable hub no matter how
        /// many pedals are plugged in, and that difference changes the answer
        /// (a sole pedal's own configured Role vs the axis-order default).
        /// Never below 1 — a lane always has at least the one pedal.
        /// </summary>
        public int ConnectedAxisCount
        {
            get
            {
                int raw = AxisCount > 0 ? AxisCount : 1;
                if (raw > MaxAxes) raw = MaxAxes;
                int n = 0;
                for (int a = 0; a < raw; a++)
                    if (IsAxisConnected(a)) n++;
                return n > 0 ? n : 1;
            }
        }

        /// <summary>Role→motor device id with no axis to fall back on (used by
        /// the calibration reads): the mapped device, else this lane's host id
        /// (0x12 on a USB pipe, 0x19 routed).</summary>
        public byte MotorDeviceForRole(int roleIndex)
        {
            var map = _roleToDevice;
            if (map != null && roleIndex >= 0 && !RoleIsAmbiguous(roleIndex)
                && map.TryGetValue(roleIndex, out var dev))
                return dev;
            return HostDeviceId;
        }

        private static int CalibIndex(string name)
        {
            switch (name)
            {
                case "mbooster-throttle-min": return 0;
                case "mbooster-throttle-max": return 1;
                case "mbooster-brake-min":    return 2;
                case "mbooster-brake-max":    return 3;
                case "mbooster-clutch-min":   return 4;
                case "mbooster-clutch-max":   return 5;
                default: return -1;
            }
        }

        private static string RoleName(int roleIndex) =>
            roleIndex == 0 ? "Throttle" : roleIndex == 1 ? "Brake" : roleIndex == 2 ? "Clutch" : "?";

        /// <summary>Record a per-role min/max calibration read for a device id
        /// (host 0x12 from RequestCalibrationReads, chained 0x1d/0x1e from
        /// ProbeChainDevices) and re-derive the role→motor map.</summary>
        private void StoreCalib(byte device, string name, int value)
        {
            int idx = CalibIndex(name);
            if (idx < 0) return;
            lock (_calibLock)
            {
                if (!_deviceCalib.TryGetValue(device, out var arr))
                {
                    arr = new int[6];
                    for (int i = 0; i < 6; i++) arr[i] = -1;
                    _deviceCalib[device] = arr;
                }
                arr[idx] = value;
            }
            RecomputeChainRoleMap();
        }

        // Latest value of each register in StatusReadNames plus the motor
        // calibration status, keyed "<dev hex>:<command name>" — per device,
        // because a chain answers these from whichever unit was addressed and
        // a calibration routine has to read back the state of the pedal IT is
        // driving, not the host's. Only mbooster-calibration-state and
        // mbooster-motor-cal-locate have decoded meanings; the rest ride along
        // so a support bundle shows them (they read as per-unit constants in
        // every capture so far — see docs/protocol/devices/mbooster.md).
        private readonly System.Collections.Generic.Dictionary<string, int> _statusRegs =
            new System.Collections.Generic.Dictionary<string, int>();
        private readonly object _statusLock = new object();

        /// <summary>
        /// Pedal calibration-mode state for a device id, from register 0xB4:
        /// 2 = normal operation, 0 = mid travel calibration, -1 = not read yet.
        /// Proven by both 2026-09-08 captures — it drops to 0 within ~0.3s of a
        /// travel-calibration start and returns to 2 only after the soft
        /// reboot, tracking the firmware's own
        /// `pedal_active_mode changed: 4` / `Table 6 Param 50`. That is what
        /// makes the reboot mandatory rather than cosmetic.
        /// </summary>
        public int CalibrationStateFor(byte device) => StatusValue(device, "mbooster-calibration-state");

        /// <summary>
        /// Motor rotor-locate status for a device id, from the group-0x2A echo:
        /// 1 = running, 3 = complete, -1 = nothing seen yet. No failure value
        /// has ever been captured.
        /// </summary>
        public int MotorCalStateFor(byte device) => StatusValue(device, "mbooster-motor-cal-locate");

        private int StatusValue(byte device, string name)
        {
            lock (_statusLock)
                return _statusRegs.TryGetValue($"{device:x2}:{name}", out var v) ? v : -1;
        }

        private void StoreStatusRegister(byte device, string name, int value)
        {
            if (name != "mbooster-motor-cal-locate" && Array.IndexOf(StatusReadNames, name) < 0) return;
            lock (_statusLock) _statusRegs[$"{device:x2}:{name}"] = value;
        }

        /// <summary>Snapshot of the status registers for the diagnostics dump,
        /// keyed "&lt;dev hex&gt;:&lt;command name&gt;".</summary>
        public System.Collections.Generic.Dictionary<string, int> StatusRegisters()
        {
            lock (_statusLock)
                return new System.Collections.Generic.Dictionary<string, int>(_statusRegs);
        }

        /// <summary>
        /// Automatic role→motor mapping for a chain, from the per-device
        /// calibration reads. Confirmed on hardware: every mBooster in the
        /// chain stores only ITS OWN pedal's calibration, under the register
        /// for that pedal's role, and reads back the unconfigured full-range
        /// default (min 0 / max 100) for the roles it doesn't have. So each
        /// device's role is simply the single register that is NOT the default
        /// — e.g. host 0x12 reads brake 16/99 (the rest 0/100) → Brake; chained
        /// 0x1d reads throttle 3/99 (the rest 0/100) → Throttle. Roles
        /// <see cref="ConnectedAxes"/> reports as having no pedal are excluded
        /// from the fingerprint: the host retains stale calibration for
        /// detached pedals (a confirmed standalone brake also read back a
        /// non-default throttle register), which would otherwise make it count
        /// as ambiguous. A device with zero or more than one configured
        /// register is left unmapped (routes by axis index), so this never
        /// routes worse than before.
        ///
        /// The fingerprint only exists to disambiguate a genuine multi-unit
        /// chain. With ONE active pedal there is nothing to disambiguate — the
        /// single motor IS the host 0x12 — so that case maps directly and never
        /// has to satisfy the "exactly one non-default register" test, which a
        /// host aggregating several pedals' calibration cannot pass anyway
        /// (bundle KY3HK4QP: throttle 0/95 AND brake 3/99 both non-default, so
        /// the map stayed null and routing fell through to the phantom 0x1d).
        /// </summary>
        private void RecomputeChainRoleMap()
        {
            // Single active pedal: its role owns the one motor, which is this
            // lane's host — 0x12 on a USB pipe, HostDeviceId (0x19) routed.
            // Passive pedals have no motor at all, and their output calibration
            // lives on the host too (it answers all three roles' min/max), so
            // nothing else needs a mapping.
            int soleActive = SoleActiveAxis();
            if (soleActive >= 0)
            {
                var soleRole = MozaMBoosterRegistry.ResolveAxisRole(
                    CurrentSettings, soleActive, ConnectedAxisCount);
                int soleRoleIdx = RoleIndexOf(soleRole);
                if (soleRoleIdx >= 0)
                {
                    PublishRoleMap(new Dictionary<int, byte> { [soleRoleIdx] = HostDeviceId });
                    return;
                }
            }

            List<KeyValuePair<byte, int[]>> devices;
            lock (_calibLock)
            {
                devices = new List<KeyValuePair<byte, int[]>>(_deviceCalib.Count);
                foreach (var kv in _deviceCalib)
                    devices.Add(new KeyValuePair<byte, int[]>(kv.Key, (int[])kv.Value.Clone()));
            }
            var connected = _connectedAxes; // role-indexed [T,B,C]; null until PD Linked

            var roleToDev = new Dictionary<int, byte>();
            var conflict = new HashSet<int>();
            foreach (var kv in devices)
            {
                int[] c = kv.Value;
                bool full = true;
                for (int i = 0; i < 6; i++) if (c[i] < 0) full = false;
                if (!full) continue;

                // The one register that isn't the 0..100 unconfigured default
                // names this device's own pedal. Registers for roles with no
                // physically-connected pedal are stale — skip them.
                int role = -1, count = 0;
                for (int r = 0; r < 3; r++)
                {
                    if (connected != null && (r >= connected.Length || !connected[r])) continue;
                    if (!(c[r * 2] == 0 && c[r * 2 + 1] == 100)) { role = r; count++; }
                }
                if (count != 1) continue; // uncalibrated / ambiguous — leave to fallback

                if (roleToDev.TryGetValue(role, out var existing) && existing != kv.Key)
                    conflict.Add(role); // two devices claim one role — trust neither
                else
                    roleToDev[role] = kv.Key;
            }
            foreach (var r in conflict) roleToDev.Remove(r);

            PublishRoleMap(roleToDev);
        }

        /// <summary>Swap in a resolved role→motor map and log it once per
        /// distinct signature. An empty map is ignored so a transient read gap
        /// never drops a mapping that already resolved.</summary>
        private void PublishRoleMap(Dictionary<int, byte> roleToDev)
        {
            if (roleToDev.Count == 0) return;
            _roleToDevice = roleToDev;

            var sb = new StringBuilder();
            for (int role = 0; role < 3; role++)
                if (roleToDev.TryGetValue(role, out var d))
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(RoleName(role)).Append("=0x").Append(d.ToString("x2"));
                }
            string sig = sb.ToString();
            if (sig != _lastRoleMapLogged)
            {
                _lastRoleMapLogged = sig;
                MozaLog.Info($"[AZOM/mBooster] {ShortIdentity(Identity)} mapped pedal roles → motors: {sig}");
            }
        }

        private void OnConnectionDisconnected()
        {
            _detected = false;
            _chainProbed = false;
            _roleToDevice = null;
            _lastRoleMapLogged = "";
            _lastRoutingLogged = "";
            _deviceReportedMaxThresholdKg = -1;
            // Drop parked calibration writes — the port is gone. Every value is
            // already in the profile, so the next connect's ApplyMBoosterToHardware
            // carries it (same rationale as HardwareApplier's own flush teardown).
            try { StopCalibFlushTimer(flush: false); } catch { }
            lock (_calibLock) _deviceCalib.Clear();
            // Re-probe proven chained ids on the next connect — their answers
            // are what the role map needs, and _deviceCalib was just cleared.
            // The discovered set itself is kept: it identifies this rig's
            // topology, which a port bounce doesn't change.
            lock (_routedChainProbed) _routedChainProbed.Clear();
            // Status read-backs describe a live device; a stale 0xB4 or motor
            // status surviving a reconnect would let a calibration runner
            // conclude "already normal" / "already complete" before the
            // reconnected pedal has answered anything.
            lock (_statusLock) _statusRegs.Clear();
        }

        private void OnConnectionMessage(byte[] data)
        {
            if (_disposed || data == null || data.Length < 2) return;
            // Routed lane rides a SHARED base/hub pipe carrying every
            // peripheral's traffic — only frames sourced from OUR pedal
            // sub-device id (nibble-swapped in the source byte) belong to
            // this lane. Applies to the 0x0E diagnostics too, which carry
            // the same swapped source byte (0x91 for dev 0x19).
            if (!_ownsConnection && data[1] != _swappedHostId
                && !IsDiscoveredRoutedChainSource(data))
                return;
            // Firmware debug/diagnostic group (0x0E) is normally silenced as noise,
            // but the mBooster streams useful chain-layout lines here ("PD Linked:
            // [T x B y C z]", "<pedal> is connected, type: active/passive pedal").
            // Surface those once each so a support bundle shows the physical chain;
            // drop the rest.
            if (data[0] == MozaProtocol.FirmwareDebugGroup)
            {
                LogPedalDiagnosticIfRelevant(data);
                return;
            }

            // EXPERIMENTAL chain-mapping diagnostic: a response from a chained
            // motor id carries the nibble-swapped device byte at data[1]
            // (0x1d→0xd1, 0x1e→0xe1); the host 0x12→0x21 falls through to the
            // normal identity handling below. Log each distinct chain-device
            // read response (raw + best-effort decode) and feeds the
            // calibration store that drives the role→motor map. Skip the
            // 2-byte keepalive acks (group 0x80) and the ~50Hz motor-write
            // echoes (group 0xa4, cmd 0xb1) — only read-backs carry mapping
            // info. Never let a chain response run the host identity switch.
            if (data.Length >= 3 && data[0] != 0x80 && data[0] != 0xa4 && (data[1] == 0xd1 || data[1] == 0xe1))
            {
                int unswapped = ((data[1] & 0x0f) << 4) | ((data[1] & 0xf0) >> 4);
                var probe = MozaResponseParser.Parse(data, busHint: "mbooster");
                if (probe.HasValue && probe.Value.Name != null)
                {
                    StoreCalib((byte)unswapped, probe.Value.Name, probe.Value.IntValue);
                    // A calibration routine driving a CHAINED pedal reads its
                    // 0xB4 / motor-locate status back through this branch, not
                    // the host arm below.
                    StoreStatusRegister((byte)unswapped, probe.Value.Name, probe.Value.IntValue);
                }
                string hex = ToHex(data);
                bool isNew;
                // Wire-derived key: cap so a response carrying a changing value can't
                // grow the set for the controller's lifetime.
                lock (_chainProbeLogged) isNew = _chainProbeLogged.Count < LoggedSetCap && _chainProbeLogged.Add(hex);
                if (isNew)
                {
                    string decoded = probe.HasValue
                        ? $"{probe.Value.Name} int={probe.Value.IntValue} bytes=[{ToHex(probe.Value.ArrayValue)}]"
                        : "(unparsed)";
                    MozaLog.Info($"[AZOM/mBooster] {ShortIdentity(Identity)} chain-probe dev=0x{unswapped:x2} resp=[{hex}] {decoded}");
                }
                return;
            }

            var result = MozaResponseParser.Parse(data, busHint: "mbooster");
            if (!result.HasValue) return;
            var r = result.Value;
            if (r.Name == null || !r.Name.StartsWith("mbooster-", StringComparison.Ordinal))
                return;

            // First valid mbooster-* response latches detection (fires DetectedRisingEdge).
            MarkDetected();

            // Identity read-backs — the mBooster answers the wheelbase's own serial/
            // model/presence probe surface (capture-verified). Reassemble the serial
            // + capture model/presence here so the device is identified by its own
            // stable serial rather than the port-topology instance id.
            switch (r.Name)
            {
                case "mbooster-serial-a":
                    _serialPartA = MozaData.ParseNullTerminatedString(r.ArrayValue ?? Array.Empty<byte>());
                    TryCompleteSerial();
                    break;
                case "mbooster-serial-b":
                    _serialPartB = MozaData.ParseNullTerminatedString(r.ArrayValue ?? Array.Empty<byte>());
                    TryCompleteSerial();
                    break;
                case "mbooster-model-name":
                    string model = MozaData.ParseNullTerminatedString(r.ArrayValue ?? Array.Empty<byte>());
                    bool modelChanged = !string.Equals(model, ModelName, StringComparison.Ordinal);
                    ModelName = model;
                    MozaLog.Debug($"[AZOM/mBooster] {ShortIdentity(Identity)} model='{ModelName}'");
                    if (modelChanged && !string.IsNullOrEmpty(model))
                    {
                        try { ModelNameResolved?.Invoke(model); }
                        catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] ModelNameResolved handler: {ex.Message}"); }
                    }
                    break;
                case "mbooster-presence":
                    // Sub-device COUNT byte offset isn't pinned yet (wheelbase reads
                    // data[0]; a real 2-pedal chain capture shows "00 02", so the
                    // count may be the last byte). Store the best-effort int and log
                    // the raw bytes so the offset can be confirmed from a bundle.
                    SubDeviceCount = r.IntValue;
                    MozaLog.Debug($"[AZOM/mBooster] {ShortIdentity(Identity)} presence raw=[{ToHex(r.ArrayValue)}] intVal={r.IntValue}");
                    if (SubDeviceCount > 1)
                    {
                        // Ambiguous by itself — a STANDALONE unit reports the same
                        // [00 02] as a 2-pedal chain, so this only says "maybe a
                        // chain, route per axis for now". The active/passive type
                        // diagnostic settles it seconds later and LogRoutingDecision
                        // reports the final answer.
                        MozaLog.Info($"[AZOM/mBooster] {ShortIdentity(Identity)} presence reports subDevs={SubDeviceCount} (ambiguous: standalone and 2-pedal chain look alike) — provisionally routing effects per axis: ax0=0x{MotorDeviceForAxis(0):x2} ax1=0x{MotorDeviceForAxis(1):x2} ax2=0x{MotorDeviceForAxis(2):x2}");
                        ProbeChainDevices();
                    }
                    break;
                case "mbooster-device-type":
                    MozaLog.Debug($"[AZOM/mBooster] {ShortIdentity(Identity)} device-type=[{ToHex(r.ArrayValue)}]");
                    break;
                default:
                    // Calibration read-backs — log at Debug so the bundle shows what
                    // the device returned. Mapping into settings happens plugin-side.
                    // The host's (0x12 USB / 0x19 routed) per-role min/max feeds the
                    // chain role→motor map.
                    if (r.Name == "mbooster-brake-threshold")
                        DeviceReportedMaxThresholdKg =
                            (float)MozaMBoosterProtocol.DecodeThresholdKg(r.IntValue);
                    StoreStatusRegister(HostDeviceId, r.Name, r.IntValue);
                    StoreCalib(HostDeviceId, r.Name, r.IntValue);
                    MozaLog.Debug($"[AZOM/mBooster] {ShortIdentity(Identity)} {r.Name} = {r.IntValue}");
                    break;
            }
        }

        /// <summary>Concatenate the two serial halves once both have arrived.</summary>
        private void TryCompleteSerial()
        {
            if (string.IsNullOrEmpty(_serialPartA) || string.IsNullOrEmpty(_serialPartB)) return;
            string full = _serialPartA + _serialPartB;
            if (string.Equals(full, Serial, StringComparison.Ordinal)) return;
            Serial = full;
            MozaLog.Info($"[AZOM/mBooster] {ShortIdentity(Identity)} serial={MozaLog.RedactId(full)} (len={full.Length})");
            try { SerialResolved?.Invoke(Identity, full); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] SerialResolved handler: {ex.Message}"); }
        }

        private static string ToHex(byte[]? b) =>
            b == null ? "" : BitConverter.ToString(b).Replace("-", " ").ToLowerInvariant();

        // Each distinct diagnostic line logged once — the device re-streams these
        // continuously, so an unguarded log would flood the support bundle.
        private readonly HashSet<string> _diagLinesLogged = new HashSet<string>(StringComparer.Ordinal);
        // Both once-only sets are keyed by device-emitted text; cap them.
        private const int LoggedSetCap = 256;

        // EXPERIMENTAL chain-mapping probe (see ProbeChainDevices): fired once
        // per connection when a chain is detected; each distinct chain-device
        // response is logged once.
        private volatile bool _chainProbed;
        private readonly HashSet<string> _chainProbeLogged = new HashSet<string>(StringComparer.Ordinal);

        // device-id → its reported [Tmin,Tmax,Bmin,Bmax,Cmin,Cmax] calibration
        // (-1 = not read yet). The host 0x12 aggregates every pedal's per-role
        // calibration; a chained single-pedal device reports only its OWN
        // pedal's calibration. That difference is what lets RecomputeChainRoleMap
        // work out which physical pedal (role) each chained motor id is.
        private readonly object _calibLock = new object();
        private readonly Dictionary<byte, int[]> _deviceCalib = new Dictionary<byte, int[]>();
        // Resolved role index (0=Throttle,1=Brake,2=Clutch) → motor device id,
        // from the calibration match. null/absent = unresolved → routing falls
        // back to the axis-index mapping (never worse than before).
        private volatile Dictionary<int, byte>? _roleToDevice;
        private string _lastRoleMapLogged = "";

        private void LogPedalDiagnosticIfRelevant(byte[] data)
        {
            var sb = new StringBuilder(data.Length);
            foreach (var ch in data)
                if (ch >= 0x20 && ch < 0x7f) sb.Append((char)ch);
            string ascii = sb.ToString().Trim();
            if (ascii.Length == 0) return;
            // Publish every printable line BEFORE the connectivity filter and
            // the dedupe set below: a calibration routine's own progress lines
            // ("Pedal Calib Start/Backward/Forward/End", "compen_theta_e …",
            // "Rotor Not Located") are none of the things this method keeps,
            // and they repeat verbatim on a second run, which the dedupe set
            // would swallow.
            try { FirmwareLogLine?.Invoke(ascii); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] FirmwareLogLine handler: {ex.Message}"); }
            // A chained unit on a ROUTED lane announces itself here and nowhere
            // else — this is the only way to learn it exists without writing
            // blind to an id that may belong to another peripheral.
            DiscoverRoutedChainDevice(data, ascii);
            if (ascii.IndexOf("PD Linked", StringComparison.OrdinalIgnoreCase) < 0 &&
                ascii.IndexOf("connected state", StringComparison.OrdinalIgnoreCase) < 0 &&
                ascii.IndexOf("pedal is connected", StringComparison.OrdinalIgnoreCase) < 0 &&
                ascii.IndexOf("not connected", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            lock (_diagLinesLogged) { if (_diagLinesLogged.Count >= LoggedSetCap || !_diagLinesLogged.Add(ascii)) return; }
            MozaLog.Debug($"[AZOM/mBooster] {ShortIdentity(Identity)} diag: {ascii}");

            // "PD Linked:[T 0 B 1 C 1]" — or, on newer firmware (device-type
            // 01-02-07-05, support bundle 2026-07-30), the long form "Pedals
            // connected state: [throttle 0 brake 1 clutch 0]". 1 = that pedal
            // slot is physically connected. Slots map to axis index 0/1/2
            // (throttle/brake/clutch), the same order the HID axes (Rx/Ry/Rz)
            // sort into.
            bool shortForm = ascii.IndexOf("PD Linked", StringComparison.OrdinalIgnoreCase) >= 0;
            bool longForm = !shortForm && ascii.IndexOf("connected state", StringComparison.OrdinalIgnoreCase) >= 0;
            if (shortForm || longForm)
            {
                int t = shortForm ? FlagAfter(ascii, 'T') : WordFlagAfter(ascii, "throttle");
                int b = shortForm ? FlagAfter(ascii, 'B') : WordFlagAfter(ascii, "brake");
                int c = shortForm ? FlagAfter(ascii, 'C') : WordFlagAfter(ascii, "clutch");
                if (t >= 0 && b >= 0 && c >= 0)
                {
                    var live = new[] { t == 1, b == 1, c == 1 };
                    _connectedAxes = live;
                    MozaLog.Info($"[AZOM/mBooster] {ShortIdentity(Identity)} connected pedals: T={t == 1} B={b == 1} C={c == 1}");
                    // Connectivity narrows which roles the calibration
                    // fingerprint may consider — re-derive with it known.
                    RecomputeChainRoleMap();
                    try { ConnectivityResolved?.Invoke(live); }
                    catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] ConnectivityResolved handler: {ex.Message}"); }
                }
            }

            // "<Pedal> pedal is [not ]connected[, type: active/passive pedal]" —
            // per-slot type. 1 = active (has a motor), 2 = passive (no motor).
            if (ascii.IndexOf("pedal is", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int slot = ascii.IndexOf("Throttle", StringComparison.OrdinalIgnoreCase) >= 0 ? 0
                         : ascii.IndexOf("Brake", StringComparison.OrdinalIgnoreCase) >= 0 ? 1
                         : ascii.IndexOf("Clutch", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : -1;
                if (slot >= 0)
                {
                    byte type = ascii.IndexOf("not connected", StringComparison.OrdinalIgnoreCase) >= 0 ? (byte)0
                              : ascii.IndexOf("passive", StringComparison.OrdinalIgnoreCase) >= 0 ? (byte)2
                              : ascii.IndexOf("active", StringComparison.OrdinalIgnoreCase) >= 0 ? (byte)1 : (byte)0;
                    var arr = _axisTypes != null ? (byte[])_axisTypes.Clone() : new byte[3];
                    if (slot < arr.Length) arr[slot] = type;
                    _axisTypes = arr;
                    if (slot < _axisTypeSeen.Length) _axisTypeSeen[slot] = true;
                    // Active/passive is what actually decides chain-ness (see
                    // ActiveAxisCount / MotorDeviceForCurrentAxis), so re-derive
                    // the role→motor map and report the settled routing.
                    RecomputeChainRoleMap();
                    LogRoutingDecision();
                }
            }
        }

        /// <summary>
        /// Report the settled motor/config routing once the active/passive types
        /// are known, superseding the provisional per-axis line the ambiguous
        /// presence read logs, and re-apply hardware against it. Announced once
        /// per distinct outcome — the device re-streams the type diagnostic about
        /// once a minute.
        ///
        /// Waits for the WHOLE diagnostic block (via
        /// <see cref="AxisTypesComplete"/>, which <see cref="ActiveAxisCount"/>
        /// already enforces): the three type lines arrive ~10ms apart, so acting
        /// on the first one would announce — and re-apply against — "0 active
        /// pedals" before the active pedal's own line lands.
        /// </summary>
        private void LogRoutingDecision()
        {
            int activeCount = ActiveAxisCount;
            if (activeCount < 0) return;
            var sb = new StringBuilder();
            for (int a = 0; a < MaxAxes; a++)
            {
                var types = _axisTypes;
                if (types == null || a >= types.Length || types[a] == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append("ax").Append(a)
                  .Append(types[a] == 1 ? "(active)" : "(passive)")
                  .Append("=0x").Append(MotorDeviceForCurrentAxis(a).ToString("x2"));
            }
            string sig = $"{activeCount}|{sb}";
            if (sig == _lastRoutingLogged) return;
            bool firstResolution = _lastRoutingLogged.Length == 0;
            _lastRoutingLogged = sig;
            MozaLog.Info(
                $"[AZOM/mBooster] {ShortIdentity(Identity)} {activeCount} active pedal(s) → " +
                (activeCount > 1 ? "genuine chain, routing per axis: " : "single unit, everything at the host: ")
                + sb);
            // The connect-time apply already ran — the type diagnostic only
            // streams about once a minute, so it fires long AFTER detection
            // (bundle KY3HK4QP: ~37 s later) and every calibration write in
            // that window went to whatever device id the coarser fallback
            // guessed. Re-apply now that the routing is authoritative.
            try { RoutingResolved?.Invoke(firstResolution); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] RoutingResolved handler: {ex.Message}"); }
        }

        private string _lastRoutingLogged = "";

        /// <summary>Digit (0/1) immediately following the first <paramref name="slot"/>
        /// letter after a '[' in a "PD Linked:[T 0 B 1 C 1]" line; -1 if absent.</summary>
        private static int FlagAfter(string s, char slot)
        {
            int start = s.IndexOf('[');
            if (start < 0) start = 0;
            for (int i = start; i < s.Length; i++)
            {
                if (s[i] != slot) continue;
                for (int j = i + 1; j < s.Length && j <= i + 3; j++)
                    if (s[j] == '0' || s[j] == '1') return s[j] - '0';
                return -1;
            }
            return -1;
        }

        /// <summary>Digit (0/1) shortly after the first case-insensitive
        /// <paramref name="word"/> in a long-form "Pedals connected state:
        /// [throttle 0 brake 1 clutch 0]" line; -1 if absent.</summary>
        private static int WordFlagAfter(string s, string word)
        {
            int i = s.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return -1;
            int after = i + word.Length;
            for (int j = after; j < s.Length && j <= after + 3; j++)
                if (s[j] == '0' || s[j] == '1') return s[j] - '0';
            return -1;
        }

        /// <summary>
        /// Axis index of this lane's SOLE connected pedal, or -1 when
        /// connectivity is unknown or more than one pedal is wired. A
        /// standalone unit's sole pedal commonly reports on a non-zero HID
        /// axis, but its config historically lives in the lane's flat
        /// settings fields — the only row the UI shows before connectivity is
        /// known. The worker / UI / HID-shaping paths use this to fall back
        /// to the flat fields for that pedal when it has no per-pedal entry,
        /// so learning the real axis doesn't orphan the existing config.
        /// </summary>
        public int SoleConnectedAxis()
        {
            var connected = _connectedAxes;
            if (connected == null) return -1;
            int sole = -1, count = 0;
            for (int i = 0; i < connected.Length; i++)
                if (connected[i]) { sole = i; count++; }
            return count == 1 ? sole : -1;
        }

        /// <summary>
        /// HID axis indices of the pedals this lane ACTUALLY hosts. The HID
        /// interface commonly reports 3 axes (Rx/Ry/Rz) regardless of how many
        /// pedals are physically connected — <see cref="ConnectedAxes"/> (from
        /// the "PD Linked" firmware diagnostic) is the only way to tell which
        /// are real. Until that diagnostic arrives (null), only axis 0 counts:
        /// the common case is a standalone single pedal, and a genuine chain's
        /// extra axes appear as soon as the diagnostic confirms them instead of
        /// showing phantom pedals. Shared by the mBooster tab's row list and the
        /// PitHouse import wizard's target list so both show the same pedals.
        /// </summary>
        public List<int> ConnectedAxisIndices()
        {
            int axisCount = AxisCount > 0 ? AxisCount : 1;
            var connected = _connectedAxes;
            var axes = new List<int>();
            for (int axis = 0; axis < axisCount && axis < MaxAxes; axis++)
            {
                bool known = connected != null && axis < connected.Length ? connected[axis] : axis == 0;
                if (known) axes.Add(axis);
            }
            return axes;
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
            // Routed lane: the shared pipe's lifecycle belongs to its owning
            // base/hub manager — never open or pin it from here. Workers are
            // started at lane registration (StartWorkers).
            if (!_ownsConnection) return _connection.IsConnected;
            if (_connection.IsConnected) return true;
            // Pin the cached port name so the connection targets THIS specific
            // mBooster's COM port, not whichever PID 0x0008 device the registry
            // happens to list first.
            _connection.LastPortName = PortName;
            bool ok = _connection.Connect();
            if (ok)
            {
                MozaLog.Info($"[AZOM/mBooster] Connected ({ShortIdentity(Identity)} on {_connection.LastPortName})");
                foreach (var w in _workers) w.Start();
                // Nothing else proactively elicits a response from this device:
                // motor frames and the keepalive are write-only, and with all
                // effects disabled (the default for a fresh device) the worker
                // sends nothing else at all. Without this, MarkDetected() never
                // fires and the UI sits at "Probing…" until the user manually
                // clicks "Read from device" in the Calibration section — fire
                // the same read burst here so detection latches on its own.
                RequestCalibrationReads();
            }
            return ok;
        }

        public void Disconnect()
        {
            foreach (var w in _workers) w.Stop();
            if (_ownsConnection) _connection.Disconnect();
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
            MozaLog.Debug($"[AZOM/mBooster] Detected {ShortIdentity(Identity)}");
            try { DetectedRisingEdge?.Invoke(); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] DetectedRisingEdge handler: {ex.Message}"); }
        }

        // ===== Frame submission =====================================

        /// <summary>
        /// Send a motor-write frame via the latest-wins stream lane (worker
        /// path). Stream lane coalesces stale frames if writer lag piles up,
        /// which is the correct behaviour at 50 Hz cadence. Each pedal axis
        /// gets its OWN lane so a chained lane's axes can't coalesce each
        /// other away — and so every axis gets identical delivery. Routing an
        /// axis to the one-shot FIFO instead is what made a chained brake's
        /// Road Texture far stronger than the throttle's (see the
        /// MBoosterEffectAxis1/2 comment in StreamKind).
        /// </summary>
        public void SendMotorStream(byte[] frame, int axisIndex)
        {
            if (frame == null || !_connection.IsConnected) return;
            _connection.SendStream(StreamSlotForAxis(axisIndex), frame);
        }

        /// <summary>Per-axis motor stream lane. Axes beyond the mapped ones
        /// share axis 0's lane — the device only ever drives three motor ids
        /// (MozaMBoosterProtocol.MotorDeviceIds), so that branch is unreachable
        /// today and only guards a future axis-count bump from an out-of-range
        /// slot (which SendStream would drop with a loud warning).</summary>
        private static StreamKind StreamSlotForAxis(int axisIndex) => axisIndex switch
        {
            1 => StreamKind.MBoosterEffectAxis1,
            2 => StreamKind.MBoosterEffectAxis2,
            _ => StreamKind.MBoosterEffect,
        };

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
            foreach (var w in _workers) w.PostFrame(snap);
        }

        // ===== Settings reads / calibration writes (experimental per § 6) ====

        /// <summary>
        /// Build + send a write for a registered <c>mbooster-*</c> int command
        /// against this lane's host sub-device (0x12 USB / 0x19 routed) when
        /// <paramref name="device"/> is omitted. Returns true if the frame was
        /// enqueued. The protocol note marks this surface as "likely but
        /// unverified" on mBooster firmware — the UI surfaces a warning so
        /// the user knows the request may not be acknowledged.
        /// </summary>
        public bool SendIntWrite(string commandName, int value, byte? device = null)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteInt(device ?? HostDeviceId, value);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        /// <summary>Build + send a write for a registered <c>mbooster-*</c> float command.
        /// <paramref name="device"/> selects WHICH mBooster unit on a chain (0x12
        /// host / 0x1d / 0x1e) — used to target a chained unit's own load cell.
        /// Omitted = this lane's host sub-device.</summary>
        public bool SendFloatWrite(string commandName, float value, byte? device = null)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteFloat(device ?? HostDeviceId, value);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        /// <summary>
        /// Write Deadzone, Max Force, and the Pedal Feel curve's 6 nodes on
        /// BOTH axes between them (cmdId 0xAB selectors 0x01-0x0E) as one
        /// atomic burst — CONFIRMED real hardware calibration. The Y half
        /// (selectors 0x07-0x0E: Deadzone, 6 nodes, Max Force) is reverse-
        /// engineered from max-force-24-75-128-166-200.pcapng and
        /// deadzone-0-5-11-14.pcapng (bug bundle 5VR5AQ8Y): every Deadzone or
        /// Max Force change in both captures resent the whole 8-value family
        /// together, not just the field that moved — same "no partial
        /// update" shape as Segmented Damping. The X half (selectors
        /// 0x01-0x06, one per node) is reverse-engineered from
        /// pedal-feel-node{2,5}-{x,y}-adjust.pcapng: every isolated single-
        /// node drag (on EITHER axis) wrote that node's X selector and its Y
        /// selector together, X first — sent here in the same order for
        /// consistency, though a full resync's exact intra-burst ordering is
        /// unconfirmed to matter. All fields use the identical kg encoding
        /// as Max Threshold (<see cref="MozaMBoosterProtocol.EncodeThresholdKg"/>).
        /// <paramref name="inputCurveY"/>/<paramref name="inputCurveX"/> are
        /// the Pedal Feel curve's own 6 user-adjustable nodes per axis
        /// (0-100%, null/wrong-length = use the default Linear shape) — Y is
        /// a percentage of the Deadzone-Max Force span, X of the fixed
        /// 0-200kg full scale (the two axes are NOT interchangeable — see
        /// <see cref="MozaMBoosterRegistry.ComputeFeelCurveY"/> and
        /// <see cref="MozaMBoosterRegistry.ComputeFeelCurveX"/>).
        /// </summary>
        public void PushFeelCurveResync(double deadzoneKg, double maxForceKg, float[]? inputCurveY, float[]? inputCurveX, byte device)
        {
            SendIntWrite("mbooster-brake-deadzone", MozaMBoosterProtocol.EncodeThresholdKg(deadzoneKg), device);
            var midX = MozaMBoosterRegistry.ComputeFeelCurveX(inputCurveX);
            var midY = MozaMBoosterRegistry.ComputeFeelCurveY(deadzoneKg, maxForceKg, inputCurveY);
            for (int i = 0; i < midY.Length; i++)
            {
                SendIntWrite($"mbooster-brake-feelcurve-x-{i + 1}", MozaMBoosterProtocol.EncodeThresholdKg(midX[i]), device);
                SendIntWrite($"mbooster-brake-feelcurve-{i + 1}", MozaMBoosterProtocol.EncodeThresholdKg(midY[i]), device);
            }
            SendIntWrite("mbooster-brake-maxforce", MozaMBoosterProtocol.EncodeThresholdKg(maxForceKg), device);
        }

        // ── Coalescing gate for UI-driven calibration writes ──
        // A slider raises ValueChanged per tick, and every one of these
        // commands is a flash-backed calibration register — writing it on
        // every tick of a drag would hammer flash unnecessarily. (An
        // earlier design also dragged a 6-frame curve7 resync behind every
        // write here, motivated by bundle KY3HK4QP's "~2s Max Threshold
        // drag emitted 77 threshold + 462 curve7 frames" cost — that resync
        // was later removed as unconfirmed/unneeded, but the coalescing
        // below is still worth it purely for the primary writes.) UI writes
        // are parked in a latest-wins slot per (device, command) and
        // flushed once the user stops moving, collapsing a whole drag into
        // one write set. Same pending+coalesce+throttle shape
        // HardwareApplier.QueueWheelCfgWrite uses for the wheel's own
        // flash-backed writes, minus its change cache.
        //
        // The connect-time apply (MozaPlugin.ApplyMBoosterToHardware) deliberately
        // does NOT go through this — it fires once and must not be deferred.
        private const double CalibFlushDelayMs = 400.0;
        private readonly Dictionary<string, Action> _pendingCalibWrites =
            new Dictionary<string, Action>(StringComparer.Ordinal);
        // Leaf lock: guards only the dictionary + the lazy timer. Never held
        // across a device write — the flush copies out, releases, then writes.
        private readonly object _pendingCalibLock = new object();
        private System.Timers.Timer? _calibFlushTimer;

        /// <summary>
        /// Park a UI-driven calibration write until the user stops changing it.
        /// Latest write per <paramref name="key"/> wins and the quiet window
        /// restarts on every call, so one slider drag results in one write.
        /// <paramref name="key"/> must identify the (device, command) pair so
        /// edits to different fields — or to the same field on different pedals —
        /// never displace each other.
        /// </summary>
        public void QueueCalibWrite(string key, Action write)
        {
            if (write == null || _disposed) return;
            lock (_pendingCalibLock)
            {
                _pendingCalibWrites[key] = write;
                if (_calibFlushTimer == null)
                {
                    _calibFlushTimer = new System.Timers.Timer(CalibFlushDelayMs) { AutoReset = false };
                    _calibFlushTimer.Elapsed += (_, __) => FlushPendingCalibWrites();
                }
                _calibFlushTimer.Stop();
                _calibFlushTimer.Start();
            }
        }

        private void FlushPendingCalibWrites()
        {
            Action[] due;
            lock (_pendingCalibLock)
            {
                if (_pendingCalibWrites.Count == 0) return;
                due = new Action[_pendingCalibWrites.Count];
                _pendingCalibWrites.Values.CopyTo(due, 0);
                _pendingCalibWrites.Clear();
            }
            if (_disposed || _isShuttingDown()) return;
            foreach (var w in due)
            {
                try { w(); }
                catch (Exception ex) { MozaLog.Warn($"[AZOM/mBooster] calib flush: {ex.Message}"); }
            }
        }

        /// <summary>Flush anything still parked immediately — used on teardown so a
        /// drag that ended within the quiet window isn't silently dropped.</summary>
        private void StopCalibFlushTimer(bool flush)
        {
            System.Timers.Timer? t;
            lock (_pendingCalibLock)
            {
                t = _calibFlushTimer;
                _calibFlushTimer = null;
            }
            try { t?.Stop(); t?.Dispose(); } catch { }
            if (flush) FlushPendingCalibWrites();
            else lock (_pendingCalibLock) _pendingCalibWrites.Clear();
        }

        /// <summary>
        /// Build + send a read for a registered <c>mbooster-*</c> command,
        /// addressed to this lane's host sub-device (0x12 USB / 0x19 routed).
        /// Read responses (group 35 + 0x80 = 0xA3) land on <see cref="MessageReceived"/>
        /// and the caller must <see cref="MozaResponseParser.Parse"/> them with
        /// <c>busHint: "mbooster"</c> to disambiguate from wheelbase main and AB9.
        /// </summary>
        public bool SendRead(string commandName) => SendRead(commandName, HostDeviceId);

        /// <summary>Build + send an <c>mbooster-*</c> read addressed to a specific
        /// device id (host 0x12 or a chained motor 0x1d/0x1e).</summary>
        public bool SendRead(string commandName, byte device)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildReadMessage(device);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        /// <summary>
        /// EXPERIMENTAL chain-mapping diagnostic — read identity + per-pedal
        /// calibration from the chained motor device ids (0x1d/0x1e), once per
        /// connection, so a support bundle reveals whether a chained pedal
        /// self-reports its role/calibration. That is the missing link for
        /// mapping HID axis (role) to motor device id (chain plug position):
        /// "PD Linked" reports which roles exist but not which device id each
        /// is at, and the host 0x12 aggregates all three roles' calibration so
        /// it can't disambiguate on its own. Responses land in
        /// <see cref="OnConnectionMessage"/> and are logged as "chain-probe …".
        /// Benign — reads only, no writes.
        /// </summary>
        public void ProbeChainDevices()
        {
            // Routed lane: 0x1d/0x1e are OTHER peripherals' bus ids on a shared
            // base/hub pipe, so they are only probed once one has identified
            // itself as an mBooster on its own (DiscoverRoutedChainDevice).
            if (!_ownsConnection)
            {
                ProbeDiscoveredRoutedChain();
                return;
            }
            if (!_connection.IsConnected || _chainProbed) return;
            _chainProbed = true;
            foreach (var dev in new byte[] { 0x1d, 0x1e })
                ProbeChainDevice(dev);
        }

        private void ProbeChainDevice(byte dev)
        {
            foreach (var name in new[]
            {
                "mbooster-model-name", "mbooster-serial-a", "mbooster-serial-b",
                "mbooster-presence", "mbooster-device-type",
                "mbooster-throttle-min", "mbooster-throttle-max",
                "mbooster-brake-min", "mbooster-brake-max",
                "mbooster-clutch-min", "mbooster-clutch-max",
                "mbooster-brake-threshold", "mbooster-brake-angle-ratio",
            })
                SendRead(name, dev);
        }

        // ===== Routed multi-unit chains =====
        //
        // A routed lane was assumed to be a single unit at the tunneled pedal
        // sub-device id (0x19), because on a shared base/hub pipe 0x1d/0x1e
        // belong to other peripherals. Bug reports GVT5H8B8 / 34JAASN5 disprove
        // the single-unit half: that W17 carries an mBooster at 0x19 hosting one
        // active pedal AND a second active unit chained behind it at 0x1d, which
        // streams its own group-0x0E diagnostics (own MCU/MOS/MOT temps,
        // "PD Linked: 1", own Theta and load cell) in the scalar chained-unit
        // dialect. With everything forced onto 0x19, both pedals shared one set
        // of the brake-named SINGLETON registers, so their Travel values
        // overwrote each other ~74 ms apart on every apply (34JAASN5) and the
        // motor routine always drove whichever motor 0x19 fronts (GVT5H8B8).
        //
        // Discovery is passive and evidence-gated: nothing is ever emitted to a
        // chained id until that id has, unprompted, logged the active-pedal
        // diagnostic wording. A non-mBooster peripheral sitting at 0x1d never
        // does, so it is never written to.
        private readonly HashSet<byte> _routedChainIds = new HashSet<byte>();
        private readonly HashSet<byte> _routedChainProbed = new HashSet<byte>();

        /// <summary>
        /// Whether a device id has ever answered a parsed <c>mbooster-*</c>
        /// read on this lane. A discovered-but-silent chained id must never be
        /// routed to — writing into the void is the KY3HK4QP/A6N521CS failure
        /// mode, where the real pedal keeps its old values while every write
        /// disappears. The role map enforces this implicitly (it only contains
        /// devices that answered all six min/max reads); this exposes it for
        /// the diagnostics dump.
        /// </summary>
        public bool DeviceHasAnswered(byte device)
        {
            lock (_calibLock) return _deviceCalib.ContainsKey(device);
        }

        /// <summary>Chained mBooster ids this ROUTED lane has proven and may
        /// address. Empty on a USB lane (which uses the full id set).</summary>
        public byte[] RoutedChainIds
        {
            get { lock (_routedChainIds) return new List<byte>(_routedChainIds).ToArray(); }
        }

        /// <summary>
        /// Should an inbound frame from a non-host source be handled by this
        /// ROUTED lane? True for a chained id already proven to be an mBooster,
        /// and for the group-0x0E diagnostic frames that do the proving — those
        /// are read-only observations, so letting them through emits nothing.
        /// </summary>
        private bool IsDiscoveredRoutedChainSource(byte[] data)
        {
            byte src = data[1];
            if (src != 0xd1 && src != 0xe1) return false;
            byte dev = (byte)(((src & 0x0f) << 4) | (src >> 4));
            lock (_routedChainIds)
                if (_routedChainIds.Contains(dev)) return true;
            return data[0] == MozaProtocol.FirmwareDebugGroup;
        }

        /// <summary>
        /// Latch a chained id as a real mBooster once its own diagnostics say
        /// so. Called from the 0x0E handler with the decoded ASCII.
        /// </summary>
        private void DiscoverRoutedChainDevice(byte[] data, string ascii)
        {
            if (_ownsConnection) return;
            byte src = data[1];
            if (src != 0xd1 && src != 0xe1) return;
            // The scalar chained-unit dialect. "Active pedal heartbeat log" is
            // the unambiguous one; the others corroborate it on a unit whose
            // heartbeat header was dropped mid-stream.
            if (ascii.IndexOf("Active pedal heartbeat", StringComparison.OrdinalIgnoreCase) < 0
                && ascii.IndexOf("P-Sens raw", StringComparison.OrdinalIgnoreCase) < 0
                && ascii.IndexOf("PD Linked", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            byte dev = (byte)(((src & 0x0f) << 4) | (src >> 4));
            lock (_routedChainIds)
                if (!_routedChainIds.Add(dev)) return;

            _motorIds = BuildRoutedMotorIds();
            MozaLog.Info($"[AZOM/mBooster] {ShortIdentity(Identity)} routed chain: dev 0x{dev:x2} identified itself as an active mBooster pedal — addressing it directly ({ascii})");
            ProbeDiscoveredRoutedChain();
        }

        private byte[] BuildRoutedMotorIds()
        {
            var ids = new List<byte> { HostDeviceId };
            lock (_routedChainIds)
                foreach (var d in _routedChainIds)
                    if (d != HostDeviceId) ids.Add(d);
            ids.Sort();
            return ids.ToArray();
        }

        /// <summary>Identity + calibration reads at each proven chained id, once
        /// each. Reads only — the role map needs their answers to tell the two
        /// pedals apart, and a chained id that answers nothing is never routed
        /// to (see <see cref="DeviceHasAnswered"/>).</summary>
        private void ProbeDiscoveredRoutedChain()
        {
            if (!_connection.IsConnected) return;
            foreach (var dev in RoutedChainIds)
            {
                lock (_routedChainProbed)
                    if (!_routedChainProbed.Add(dev)) continue;
                ProbeChainDevice(dev);
            }
        }

        /// <summary>
        /// Issue a one-time burst of calibration reads (direction / min / max
        /// per pedal + 5-point curves). Mirrors the wheelbase pedal seed.
        /// Called from <see cref="TryConnect"/> (it's also the only thing
        /// that elicits a response a fresh connection can latch detection
        /// on), from the rising-edge handler, and from the UI's "Read from
        /// device" button. Experimental: may produce no responses on
        /// mBooster firmware.
        /// </summary>
        /// <summary>Identity/presence reads alone — the routed-lane probe uses
        /// this to identify what's on a base/hub pedal port (model-name is the
        /// mBooster-vs-SGP discriminator) without the calibration burst.</summary>
        public void SendIdentityReads()
        {
            if (!_connection.IsConnected) return;
            // Order matches real Pit House's own connect handshake frame for
            // frame (see tools/cmd-frame-mbooster-cal.txt, checked against the
            // 2026-09-08 captures). It runs again on every reconnect, which is
            // what a calibration's soft reboot produces.
            foreach (var name in IdentityReadOrder)
                SendRead(name);
        }

        /// <summary>Pit House's connect-handshake read order, verbatim.</summary>
        private static readonly string[] IdentityReadOrder =
        {
            "mbooster-presence",
            "mbooster-device-type",
            "mbooster-mcu-uid",
            "mbooster-device-presence",
            "mbooster-capabilities",
            "mbooster-model-name",
            "mbooster-sw-version",
            "mbooster-identity-11",
            "mbooster-hw-version",
            "mbooster-serial-a",
            "mbooster-hw-sub",
            "mbooster-serial-b",
        };

        public void RequestCalibrationReads()
        {
            if (!_connection.IsConnected) return;
            // Identity/presence come from the host sub-device — they identify
            // the unit, double as the detection-eliciting response, and presence
            // triggers the chain probe that builds the role→motor map.
            SendIdentityReads();
            // Per-role calibration read-backs go to each role's own mapped
            // motor device (host 0x12 until the map resolves — the first burst
            // is what discovers it), so a chained pedal's read reflects THAT
            // pedal's real values rather than the host's defaults. Mirrors the
            // writes (see MozaPlugin.ApplyMBoosterToHardware).
            ReadRoleCalibration("throttle", 0);
            ReadRoleCalibration("brake", 1);
            ReadRoleCalibration("clutch", 2);
        }

        private void ReadRoleCalibration(string prefix, int roleIndex)
        {
            byte dev = MotorDeviceForRole(roleIndex);
            SendRead($"mbooster-{prefix}-dir", dev);
            SendRead($"mbooster-{prefix}-min", dev);
            SendRead($"mbooster-{prefix}-max", dev);
            for (int i = 1; i <= 5; i++) SendRead($"mbooster-{prefix}-y{i}", dev);
            // Load-cell-only settings live under the brake-named command set.
            // Everything below is a brake-named SINGLETON register (no
            // per-pedal selector), so it is read once, for the brake role, on
            // that role's own device — reading it per role would just re-read
            // the same hardware three times. This is Pit House's own per-cycle
            // read set; the plugin had been reading only angle-ratio and
            // threshold out of it, so Travel / End Stop / Friction / Damping /
            // Deadzone and the whole status block were never seeded from the
            // device. See docs/protocol/devices/mbooster.md.
            if (roleIndex == 1)
            {
                SendRead("mbooster-brake-angle-ratio", dev);
                SendRead("mbooster-brake-threshold", dev);
                SendRead("mbooster-brake-travel-start", dev);
                SendRead("mbooster-brake-travel-end", dev);
                SendRead("mbooster-brake-deadzone", dev);
                SendRead("mbooster-brake-damping-press", dev);
                SendRead("mbooster-brake-damping-release", dev);
                SendRead("mbooster-brake-friction-0", dev);
                SendRead("mbooster-brake-friction-1", dev);
                SendRead("mbooster-brake-endstop-front", dev);
                SendRead("mbooster-brake-endstop-end", dev);
                // Calibration-mode state + the constants Pit House also polls.
                // 0xB4 is the one with proven meaning (2 = normal, 0 = mid
                // travel calibration); the rest ride along so a support bundle
                // carries them.
                foreach (var name in StatusReadNames)
                    SendRead(name, dev);
            }
        }

        /// <summary>Status registers Pit House reads that have no decoded
        /// meaning yet, plus the calibration-mode state. Surfaced in the
        /// diagnostics dump — see <see cref="StatusRegisters"/>.</summary>
        internal static readonly string[] StatusReadNames =
        {
            "mbooster-calibration-state",
            "mbooster-status-0d",
            "mbooster-status-21",
            "mbooster-status-22",
            "mbooster-status-23",
            "mbooster-status-24",
        };

        /// <summary>Fire all five disable frames; called on disconnect / shutdown.
        /// Traction Control and Custom Effects share Engine's wire ID (no
        /// verified ID of their own), so the Engine disable frame below
        /// already covers them too.</summary>
        public void SendAllDisableFrames()
        {
            if (!_connection.IsConnected) return;
            // One-shot FIFO so they all land in order (no coalescing). Disable
            // every effect on EVERY motor device id this lane addresses (USB:
            // host 0x12 + chain ports 0x1d/0x1e so a chained active pedal's
            // motor can't latch its last waveform after the port closes;
            // routed: the tunneled pedal sub-device only).
            foreach (var dev in MotorIds)
            {
                SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(MBoosterEffectId.Abs, dev));
                SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(MBoosterEffectId.Lockup, dev));
                SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(MBoosterEffectId.Threshold, dev));
                SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(MBoosterEffectId.Engine, dev));
                SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(MBoosterEffectId.RoadTexture, dev));
            }
        }

        /// <summary>
        /// Continuously runs the Engine effect at its currently configured
        /// Frequency/Intensity while <paramref name="on"/> is true — the
        /// Engine card's Test toggle. Both sliders are tracked live by
        /// the worker, not snapshotted at toggle-on time. Turning off is
        /// always allowed (even if disconnected) so a stuck toggle can
        /// always be cleared; turning on requires a live connection.
        /// </summary>
        public void SetEngineTestActive(bool on, int pedalIndex = 0)
        {
            if (on && !_connection.IsConnected) return;
            WorkerFor(pedalIndex)?.SetEngineTestSustained(on);
        }

        /// <summary>
        /// Continuously runs the ABS effect — substituting live brake
        /// position for absActive, same as the old 1s test pulse did — at
        /// its currently configured Frequency/Intensity/Smoothness while
        /// <paramref name="on"/> is true. See <see cref="SetEngineTestActive"/>
        /// for the analogous Engine toggle; same live-tracking and
        /// always-allow-off semantics apply here.
        /// </summary>
        public void SetAbsTestActive(bool on, int pedalIndex = 0)
        {
            if (on && !_connection.IsConnected) return;
            WorkerFor(pedalIndex)?.SetAbsTestSustained(on);
        }

        /// <summary>
        /// Continuously runs Traction Control — substituting live throttle
        /// position for tcActive, same substitution ABS makes with brake
        /// position — at its currently configured Frequency/Intensity/
        /// Smoothness while <paramref name="on"/> is true. See
        /// <see cref="SetAbsTestActive"/> for the analogous ABS toggle; same
        /// live-tracking and always-allow-off semantics apply here.
        /// </summary>
        public void SetTcTestActive(bool on, int pedalIndex = 0)
        {
            if (on && !_connection.IsConnected) return;
            WorkerFor(pedalIndex)?.SetTcTestSustained(on);
        }

        /// <summary>
        /// Continuously runs Wheel Spin — substituting live throttle
        /// position for the wheelspin heuristic, same substitution Traction
        /// Control makes — at its currently configured Frequency/Intensity
        /// while <paramref name="on"/> is true. See
        /// <see cref="SetTcTestActive"/> for the analogous Traction Control
        /// toggle; same live-tracking and always-allow-off semantics apply
        /// here.
        /// </summary>
        public void SetWheelSpinTestActive(bool on, int pedalIndex = 0)
        {
            if (on && !_connection.IsConnected) return;
            WorkerFor(pedalIndex)?.SetWheelSpinTestSustained(on);
        }

        /// <summary>
        /// Continuously runs Gear Shift at its currently configured
        /// Frequency/Intensity while <paramref name="on"/> is true, bypassing
        /// the real one-shot pulse/debounce/neutral-suppression machinery
        /// entirely — there's no live "gear just changed" signal to press
        /// against outside a real shift. See <see cref="SetTcTestActive"/>
        /// for the analogous Traction Control toggle; same live-tracking and
        /// always-allow-off semantics apply here.
        /// </summary>
        public void SetGearShiftTestActive(bool on, int pedalIndex = 0)
        {
            if (on && !_connection.IsConnected) return;
            WorkerFor(pedalIndex)?.SetGearShiftTestSustained(on);
        }

        /// <summary>
        /// Continuously runs Road Texture at its currently configured
        /// Intensity/Smoothness while <paramref name="on"/> is true,
        /// bypassing Enabled and the game-running/speed gate entirely —
        /// there's no live "how rough is the road" signal to preview
        /// against outside a real drive. See <see cref="SetEngineTestActive"/>
        /// for the analogous Engine toggle; same live-tracking and
        /// always-allow-off semantics apply here.
        /// </summary>
        public void SetRoadTextureTestActive(bool on, int pedalIndex = 0)
        {
            if (on && !_connection.IsConnected) return;
            WorkerFor(pedalIndex)?.SetRoadTextureTestSustained(on);
        }

        /// <summary>
        /// Continuously alternates G-Force's commanded travel offset
        /// forward/backward at the currently configured Max Travel/Response
        /// Speed while <paramref name="on"/> is true, bypassing Enabled and
        /// the game-running gate — mirrors Pit House's own "Test" demo. See
        /// <see cref="SetEngineTestActive"/> for the analogous Engine
        /// toggle; same live-tracking and always-allow-off semantics apply
        /// here.
        /// </summary>
        public void SetGForceTestActive(bool on, int pedalIndex = 0)
        {
            if (on && !_connection.IsConnected) return;
            WorkerFor(pedalIndex)?.SetGForceTestSustained(on);
        }

        /// <summary>
        /// Continuously runs Lockup — substituting live brake position for
        /// the wheel-slip detection heuristic (which needs vehicle speed),
        /// same as the old 1s test pulse did — at its currently configured
        /// Frequency/Intensity while <paramref name="on"/> is true. See
        /// <see cref="SetEngineTestActive"/> for the analogous Engine
        /// toggle; same live-tracking and always-allow-off semantics apply
        /// here.
        /// </summary>
        public void SetLockupTestActive(bool on, int pedalIndex = 0)
        {
            if (on && !_connection.IsConnected) return;
            WorkerFor(pedalIndex)?.SetLockupTestSustained(on);
        }

        /// <summary>
        /// Continuously runs Threshold — skipping the rising-edge hysteresis
        /// entirely, substituting live brake position for it (same
        /// substitution the old 1s test pulse used) — at its currently
        /// configured Frequency/Intensity/Decay while <paramref name="on"/>
        /// is true. See <see cref="SetEngineTestActive"/> for the analogous
        /// Engine toggle; same live-tracking and always-allow-off semantics
        /// apply here.
        /// </summary>
        public void SetThresholdTestActive(bool on, int pedalIndex = 0)
        {
            if (on && !_connection.IsConnected) return;
            WorkerFor(pedalIndex)?.SetThresholdTestSustained(on);
        }

        /// <summary>Turn Bite Point's (Clutch-only) sustained test toggle on/off for
        /// pedal <paramref name="pedalIndex"/> — same live-tracking and always-allow-off
        /// semantics as SetThresholdTestActive.</summary>
        public void SetBitePointTestActive(bool on, int pedalIndex = 0)
        {
            if (on && !_connection.IsConnected) return;
            WorkerFor(pedalIndex)?.SetBitePointTestSustained(on);
        }

        /// <summary>
        /// Forces Travel End and Max Threshold to their Brake Fade caps
        /// (BrakeFadeMaxTravelEndMm / BrakeFadeMaxThresholdKg) while
        /// <paramref name="on"/> is true, bypassing Enabled and the
        /// brake-temperature gate entirely — there's no live "how hot are
        /// the brakes" signal to preview against outside a real drive with
        /// genuinely hot brakes. Unlike the vibration effects' test toggles,
        /// this writes REAL hardware calibration — see
        /// MBoosterEffectWorker.UpdateBrakeFade. Each of the two
        /// calibrations independently requires its own configured base
        /// value (that pedal's own TravelEndMm / MaxThresholdKg &gt;= 0) or
        /// that one stays a no-op. Always-allow-off semantics apply here
        /// (see <see cref="SetEngineTestActive"/>) so a stuck toggle can
        /// still restore the base values even if disconnected.
        /// </summary>
        public void SetBrakeFadeTestActive(bool on)
        {
            if (on && !_connection.IsConnected) return;
            // Broadcast to every worker — Brake Fade only actually acts on
            // whichever axis's role resolves to Brake (see
            // MBoosterEffectWorker.Tick), which isn't necessarily axis 0/the
            // primary worker (a standalone unit's sole pedal can report on
            // any HID axis). The other workers' flag just sits unused since
            // their own Tick() gate never lets Brake Fade run.
            foreach (var w in _workers) w.SetBrakeFadeTestSustained(on);
        }

        /// <summary>
        /// Park every effect worker on this lane while a hardware calibration
        /// routine owns the pedal. Broadcast, like Brake Fade above: the
        /// calibration addresses one role's device, but a chain's other
        /// workers share the pipe and their keepalives/effects would still
        /// interleave with it. The keepalive itself keeps flowing — see
        /// <see cref="MBoosterEffectWorker.SetSuspended"/>.
        /// </summary>
        public void SetEffectsSuspended(bool on)
        {
            foreach (var w in _workers)
            {
                try { w.SetSuspended(on); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] SetSuspended: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Continuously runs one custom effect (by id) at its currently
        /// configured Frequency/Intensity while <paramref name="on"/> is
        /// true, bypassing Enabled/Formula/Threshold entirely — same
        /// always-allow-off, live-tracking semantics as
        /// <see cref="SetEngineTestActive"/>.
        /// </summary>
        public void SetCustomEffectTestActive(string effectId, bool on, int pedalIndex = 0)
        {
            if (on && !_connection.IsConnected) return;
            WorkerFor(pedalIndex)?.SetCustomEffectTestSustained(effectId, on);
        }

        public void Dispose()
        {
            if (_disposed) return;
            // Flush any parked calibration write BEFORE _disposed latches (the
            // flush and QueueCalibWrite both bail on it) so a drag that ended
            // inside the 400 ms quiet window still reaches the device — the
            // connection is still open at this point.
            try { StopCalibFlushTimer(flush: true); } catch { }
            _disposed = true;
            try
            {
                // Best-effort emit disable frames before tearing down so the
                // motor doesn't latch the last waveform after the port closes
                // (protocol note § 3 "Disable"). Only once the lane has
                // identified as an actual mBooster — a routed probe that
                // turned out to be plain pedals must not write motor
                // frames at the pedals sub-device.
                if (ModelName != null && ModelName.IndexOf("mBooster", StringComparison.OrdinalIgnoreCase) >= 0)
                    SendAllDisableFrames();
            }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Disable on dispose: {ex.Message}"); }
            // Signal all three first so their joins overlap (≤1 s total, not ≤3 s
            // serially on the reconnect timer / End()).
            foreach (var w in _workers) { try { w.RequestStop(); } catch { } }
            foreach (var w in _workers) { try { w.Stop(); } catch { } }
            try { _connection.MessageReceived -= OnConnectionMessage; } catch { }
            try { _connection.Disconnected -= OnConnectionDisconnected; } catch { }
            // A routed lane shares its owner's pipe — never dispose it.
            if (_ownsConnection) { try { _connection.Dispose(); } catch { } }
        }
    }
}
