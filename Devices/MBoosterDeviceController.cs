using System;
using System.Collections.Generic;
using System.Text;
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
        public string Identity { get; }

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
        // for passive pedals.
        private volatile byte[]? _axisTypes;
        public byte[]? AxisTypes => _axisTypes;

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
        // position bar. Element writes race benignly (last-value-wins, same as
        // LastHidPosition) so no lock is needed.
        public readonly double[] LastAxisPositions = new double[MaxAxes];

        // Per-axis pre-input-curve percent (0..100) — the same signal as
        // LastRawPercentPreCurve (after deadzone/max-force, before the input
        // curve) but for EVERY pedal, so the settings tab's live curve markers
        // track whichever pedal is selected, not just the master.
        public readonly double[] LastAxisRawPercentPreCurve = new double[MaxAxes];

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
        /// Defaults to "not a chain" (0x12) when <see cref="ConnectedAxes"/>
        /// hasn't arrived yet (null) — the common case is standalone, and
        /// this corrects itself once the "PD Linked" diagnostic confirms
        /// whether it's actually a chain.
        /// </summary>
        public byte MotorDeviceForCurrentAxis(int axisIndex)
        {
            var connected = _connectedAxes;
            int connectedCount = 0;
            if (connected != null)
                foreach (var b in connected) if (b) connectedCount++;
            bool isChain = connected != null && connectedCount > 1;
            return isChain ? MotorDeviceForAxis(axisIndex) : MozaProtocol.DeviceMain;
        }

        private void OnConnectionDisconnected()
        {
            _detected = false;
        }

        private void OnConnectionMessage(byte[] data)
        {
            if (_disposed || data == null || data.Length < 2) return;
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
                    ModelName = MozaData.ParseNullTerminatedString(r.ArrayValue ?? Array.Empty<byte>());
                    MozaLog.Debug($"[AZOM/mBooster] {ShortIdentity(Identity)} model='{ModelName}'");
                    break;
                case "mbooster-presence":
                    // Sub-device COUNT byte offset isn't pinned yet (wheelbase reads
                    // data[0]; a real 2-pedal chain capture shows "00 02", so the
                    // count may be the last byte). Store the best-effort int and log
                    // the raw bytes so the offset can be confirmed from a bundle.
                    SubDeviceCount = r.IntValue;
                    MozaLog.Debug($"[AZOM/mBooster] {ShortIdentity(Identity)} presence raw=[{ToHex(r.ArrayValue)}] intVal={r.IntValue}");
                    break;
                case "mbooster-device-type":
                    MozaLog.Debug($"[AZOM/mBooster] {ShortIdentity(Identity)} device-type=[{ToHex(r.ArrayValue)}]");
                    break;
                default:
                    // Calibration read-backs — log at Debug so the bundle shows what
                    // the device returned. Mapping into settings happens plugin-side.
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

        private void LogPedalDiagnosticIfRelevant(byte[] data)
        {
            var sb = new StringBuilder(data.Length);
            foreach (var ch in data)
                if (ch >= 0x20 && ch < 0x7f) sb.Append((char)ch);
            string ascii = sb.ToString().Trim();
            if (ascii.Length == 0) return;
            if (ascii.IndexOf("PD Linked", StringComparison.OrdinalIgnoreCase) < 0 &&
                ascii.IndexOf("pedal is connected", StringComparison.OrdinalIgnoreCase) < 0 &&
                ascii.IndexOf("not connected", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            lock (_diagLinesLogged) { if (!_diagLinesLogged.Add(ascii)) return; }
            MozaLog.Debug($"[AZOM/mBooster] {ShortIdentity(Identity)} diag: {ascii}");

            // "PD Linked:[T 0 B 1 C 1]" — 1 = that pedal slot is physically
            // connected. Slots map to axis index 0/1/2 (throttle/brake/clutch),
            // the same order the HID axes (Rx/Ry/Rz) sort into.
            if (ascii.IndexOf("PD Linked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int t = FlagAfter(ascii, 'T'), b = FlagAfter(ascii, 'B'), c = FlagAfter(ascii, 'C');
                if (t >= 0 && b >= 0 && c >= 0)
                {
                    _connectedAxes = new[] { t == 1, b == 1, c == 1 };
                    MozaLog.Info($"[AZOM/mBooster] {ShortIdentity(Identity)} connected pedals: T={t == 1} B={b == 1} C={c == 1}");
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
                }
            }
        }

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
            MozaLog.Debug($"[AZOM/mBooster] Detected {ShortIdentity(Identity)}");
            try { DetectedRisingEdge?.Invoke(); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] DetectedRisingEdge handler: {ex.Message}"); }
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
            foreach (var w in _workers) w.PostFrame(snap);
        }

        // ===== Settings reads / calibration writes (experimental per § 6) ====

        /// <summary>
        /// Build + send a write for a registered <c>mbooster-*</c> int command
        /// against device 0x12 on THIS device's connection. Returns true if the
        /// frame was enqueued. The protocol note marks this surface as "likely
        /// but unverified" on mBooster firmware — the UI surfaces a warning so
        /// the user knows the request may not be acknowledged.
        /// </summary>
        public bool SendIntWrite(string commandName, int value, byte device = MozaProtocol.DeviceMain)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteInt(device, value);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        /// <summary>Build + send a write for a registered <c>mbooster-*</c> float command.
        /// <paramref name="device"/> selects WHICH mBooster unit on a chain (0x12
        /// host / 0x1d / 0x1e) — used to target a chained unit's own load cell.</summary>
        public bool SendFloatWrite(string commandName, float value, byte device = MozaProtocol.DeviceMain)
        {
            if (!_connection.IsConnected) return false;
            var cmd = MozaCommandDatabase.Get(commandName);
            if (cmd == null) return false;
            var msg = cmd.BuildWriteFloat(device, value);
            if (msg == null) return false;
            _connection.Send(msg);
            return true;
        }

        /// <summary>
        /// EXPERIMENTAL / unverified — resend the output curve at 7
        /// breakpoints (<c>mbooster-brake-curve7-*</c>, cmdId 0xAB) after a
        /// real hardware calibration write. pedal_travel.pcapng showed Pit
        /// House doing exactly this alongside a Travel Start write, and
        /// omitting it is what made Travel Start/End silently no-op on
        /// hardware despite the raw register write reading back fine — see
        /// MozaCommandDatabase.cs's mbooster-brake-curve7-* comment. Callers
        /// use this after any of Travel/Endstop/Ratio/Threshold's own writes
        /// on the theory that the same firmware requirement applies to all of
        /// them, not just Travel — unconfirmed for the others.
        /// </summary>
        public void PushCurve7Resync(float[]? curveX, float[]? curveY, byte device)
        {
            var curve7 = MozaMBoosterRegistry.ResampleCurveAtSevenths(curveX, curveY);
            for (int i = 0; i < curve7.Length; i++)
                SendIntWrite($"mbooster-brake-curve7-{i + 1}", MozaMBoosterProtocol.EncodeCurve7Point(curve7[i]), device);
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
        /// per pedal + 5-point curves). Mirrors the wheelbase pedal seed.
        /// Called from <see cref="TryConnect"/> (it's also the only thing
        /// that elicits a response a fresh connection can latch detection
        /// on), from the rising-edge handler, and from the UI's "Read from
        /// device" button. Experimental: may produce no responses on
        /// mBooster firmware.
        /// </summary>
        public void RequestCalibrationReads()
        {
            if (!_connection.IsConnected) return;
            foreach (var name in new[]
            {
                // Identity first — the serial/model/presence reads are what let us
                // key this lane by its own stable serial (and they double as a
                // detection-eliciting response for a fresh, all-effects-off device).
                "mbooster-model-name", "mbooster-serial-a", "mbooster-serial-b",
                "mbooster-presence", "mbooster-device-type",
                "mbooster-throttle-dir", "mbooster-throttle-min", "mbooster-throttle-max",
                "mbooster-brake-dir", "mbooster-brake-min", "mbooster-brake-max",
                "mbooster-clutch-dir", "mbooster-clutch-min", "mbooster-clutch-max",
                "mbooster-throttle-y1", "mbooster-throttle-y2", "mbooster-throttle-y3", "mbooster-throttle-y4", "mbooster-throttle-y5",
                "mbooster-brake-y1", "mbooster-brake-y2", "mbooster-brake-y3", "mbooster-brake-y4", "mbooster-brake-y5",
                "mbooster-clutch-y1", "mbooster-clutch-y2", "mbooster-clutch-y3", "mbooster-clutch-y4", "mbooster-clutch-y5",
                "mbooster-brake-angle-ratio", "mbooster-brake-threshold",
            })
            {
                SendRead(name);
            }
        }

        /// <summary>Fire all five disable frames; called on disconnect / shutdown.
        /// Traction Control and Custom Effects share Engine's wire ID (no
        /// verified ID of their own), so the Engine disable frame below
        /// already covers them too.</summary>
        public void SendAllDisableFrames()
        {
            if (!_connection.IsConnected) return;
            // One-shot FIFO so they all land in order (no coalescing). Disable
            // every effect on EVERY motor device id in the chain (host 0x12 +
            // chain ports 0x1d/0x1e) so a chained active pedal's motor can't
            // latch its last waveform after the port closes.
            foreach (var dev in MozaMBoosterProtocol.MotorDeviceIds)
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
            _disposed = true;
            try
            {
                // Best-effort emit disable frames before tearing down so the
                // motor doesn't latch the last waveform after the port closes
                // (protocol note § 3 "Disable").
                SendAllDisableFrames();
            }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Disable on dispose: {ex.Message}"); }
            foreach (var w in _workers) { try { w.Stop(); } catch { } }
            try { _connection.MessageReceived -= OnConnectionMessage; } catch { }
            try { _connection.Disconnected -= OnConnectionDisconnected; } catch { }
            try { _connection.Dispose(); } catch { }
        }
    }
}
