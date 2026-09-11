using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.MBooster
{
    /// <summary>
    /// Multi-device owner for Moza mBooster Pedals (PID <c>0x0008</c>).
    /// Walks <see cref="MozaPortDiscovery"/> for every connected mBooster,
    /// spawns one <see cref="MBoosterDeviceController"/> per device, fans
    /// out game-data updates, and merges per-device HID positions into the
    /// shared <c>MozaData.{Throttle,Brake,Clutch}Position</c> fields based
    /// on each device's user-assigned role.
    ///
    /// Stable per-device identity is the USB device instance ID — survives
    /// reconnects so the profile's per-device settings (role, per-effect
    /// knobs) stick across replug.
    /// </summary>
    public sealed class MozaMBoosterRegistry : IDisposable
    {
        // identity → controller (one per physical mBooster)
        private readonly Dictionary<string, MBoosterDeviceController> _byIdentity =
            new Dictionary<string, MBoosterDeviceController>(StringComparer.OrdinalIgnoreCase);
        // Enumeration order (matches first-seen ordering) so role-merge
        // can deterministically resolve collisions (first-wins).
        private readonly List<MBoosterDeviceController> _order =
            new List<MBoosterDeviceController>();
        // Copy-on-write mirror of _order for the 60 Hz DataUpdate fan-out and the
        // per-HID-report merge: iterating it needs no lock, so those paths no
        // longer hold _lock across the settings lookup and the workers' locks.
        // Rebuilt wherever _order changes (under _lock).
        private volatile MBoosterDeviceController[] _orderSnapshot = System.Array.Empty<MBoosterDeviceController>();
        private readonly object _lock = new object();

        // Lock-free fast-path counter for the DataUpdate hot path. Updated
        // only on Refresh() add/drop, which happens on the reconnect timer
        // (5s cadence). Reading is a volatile int load — no lock, no alloc.
        private int _controllerCount;
        /// <summary>True iff at least one controller is registered. Lock-free; safe to call from DataUpdate.</summary>
        public bool HasControllers => Volatile.Read(ref _controllerCount) > 0;

        private readonly MozaData _data;
        private readonly Func<string, MBoosterDeviceSettings?> _settingsLookup;
        private readonly Func<bool> _isShuttingDown;
        private readonly Action<MBoosterDeviceController>? _onDeviceDetectedEdge;
        private readonly Func<string, double> _customEffectFormulaEvaluator;
        private readonly Action<string, string>? _onSerialResolved;
        private readonly Func<string, bool[]?>? _connectivitySeedLookup;
        private readonly Action<string, bool[]>? _onConnectivityResolved;

        // Highest merged position (0..100) each role has reached this session —
        // diagnostics-only, so a support bundle can prove whether pedal input
        // ever flowed through the merge to MozaData (vs. "the graph never
        // moved" with no way to tell if anyone pressed).
        private int _maxThrottleSeen, _maxBrakeSeen, _maxClutchSeen;
        public (int throttle, int brake, int clutch) MaxMergedPositionsSeen =>
            (_maxThrottleSeen, _maxBrakeSeen, _maxClutchSeen);

        // Collision logging — emit at most one warning per (role, identity-tail)
        // combo per session to avoid spam.
        private readonly HashSet<string> _collisionsLogged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True iff at least one mBooster is currently detected (UI gate).</summary>
        public bool AnyDetected
        {
            get
            {
                lock (_lock)
                    return _order.Any(c => c.Detected);
            }
        }

        /// <summary>
        /// True iff an mBooster occupies the relayed pedal sub-device (0x19) on
        /// a base/hub pipe. A routed lane only reaches the registry once the
        /// model-name read confirmed an mBooster, so this is proof — not a
        /// guess — that the pedal slot is NOT plain pedals. The pedals-*
        /// command set writes the same group/cmd bytes as mbooster-*, so any
        /// pedals surface aimed at 0x19 would land on the mBooster.
        /// </summary>
        public bool AnyRoutedPedalLane
        {
            get
            {
                lock (_lock)
                    return _order.Any(c => c.IsRouted && c.HostDeviceId == MozaProtocol.DevicePedals);
            }
        }

        /// <summary>Snapshot of all known controllers in enumeration order.</summary>
        public IReadOnlyList<MBoosterDeviceController> Devices
        {
            get
            {
                lock (_lock)
                    return new ReadOnlyCollection<MBoosterDeviceController>(_order.ToList());
            }
        }

        // Owns the two hardware calibration routines. Deliberately NOT on the
        // controller: both routines soft-reboot the pedal, and Refresh() above
        // disposes and removes a controller whose port disappears, so
        // controller-scoped run state would die mid-flow. Identity survives the
        // re-enumeration; the runner keys on it. See MBoosterCalibrationRunner.
        private MBoosterCalibrationRunner? _calibrationRunner;
        private readonly object _calibrationRunnerLock = new object();

        /// <summary>The calibration routine runner for this session, created on
        /// first use. One per registry: the routines reboot the unit, so two
        /// cannot be allowed to interleave even across different lanes.</summary>
        internal MBoosterCalibrationRunner CalibrationRunner
        {
            get
            {
                lock (_calibrationRunnerLock)
                    return _calibrationRunner ??= new MBoosterCalibrationRunner(this);
            }
        }

        /// <summary>Runner state without forcing it into existence — the UI asks
        /// this on every tab refresh.</summary>
        internal MBoosterCalibrationRunner? CalibrationRunnerOrNull
        {
            get { lock (_calibrationRunnerLock) return _calibrationRunner; }
        }

        /// <summary>Fired (foreground thread NOT guaranteed) on detection rising edge.</summary>
        public event Action<MBoosterDeviceController>? DeviceDetected;
        /// <summary>Fired when a new device is added to the registry.</summary>
        public event Action<MBoosterDeviceController>? DeviceAdded;
        /// <summary>Fired when a device is removed (port disappeared from registry).</summary>
        public event Action<MBoosterDeviceController>? DeviceRemoved;

        public MozaMBoosterRegistry(
            MozaData data,
            Func<string, MBoosterDeviceSettings?> settingsLookup,
            Func<bool> isShuttingDown,
            Action<MBoosterDeviceController>? onDeviceDetectedEdge = null,
            Func<string, double>? customEffectFormulaEvaluator = null,
            Action<string, string>? onSerialResolved = null,
            Func<string, bool[]?>? connectivitySeedLookup = null,
            Action<string, bool[]>? onConnectivityResolved = null)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _settingsLookup = settingsLookup ?? throw new ArgumentNullException(nameof(settingsLookup));
            _isShuttingDown = isShuttingDown ?? (() => false);
            _onDeviceDetectedEdge = onDeviceDetectedEdge;
            _customEffectFormulaEvaluator = customEffectFormulaEvaluator ?? (_ => 0.0);
            _onSerialResolved = onSerialResolved;
            _connectivitySeedLookup = connectivitySeedLookup;
            _onConnectivityResolved = onConnectivityResolved;
        }

        /// <summary>
        /// Walk the port-discovery cache for mBooster PIDs; spawn a controller
        /// for any newly-attached device, drop any whose port has disappeared.
        /// Called from the plugin's 5 s reconnect timer alongside the AB9
        /// reconnect path. Idempotent — no-ops on a healthy steady state.
        /// </summary>
        public void Refresh()
        {
            if (_isShuttingDown()) return;

            var ports = MozaPortDiscovery.Instance.EnumerateMatching(MozaUsbIds.IsMBoosterPid);

            // Map current identities → port info for quick add/drop diff.
            var currentByIdentity = new Dictionary<string, MozaPortDiscovery.PortInfo>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ports.Count; i++)
            {
                var p = ports[i];
                // Identity falls back to "port:<COMx>" if the registry gave us
                // no instance ID — keeps the controller distinct from siblings
                // while making the limitation visible in the UI.
                string id = !string.IsNullOrEmpty(p.InstanceId)
                    ? p.InstanceId
                    : "port:" + p.PortName;
                currentByIdentity[id] = p;
            }

            List<MBoosterDeviceController>? added = null;
            List<MBoosterDeviceController>? removed = null;

            lock (_lock)
            {
                // Add new ones.
                foreach (var kvp in currentByIdentity)
                {
                    if (_byIdentity.ContainsKey(kvp.Key)) continue;
                    var c = new MBoosterDeviceController(
                        identity: kvp.Key,
                        portName: kvp.Value.PortName,
                        settingsLookup: () => _settingsLookup(kvp.Key),
                        isShuttingDown: _isShuttingDown,
                        customEffectFormulaEvaluator: _customEffectFormulaEvaluator,
                        containerId: kvp.Value.ContainerId);
                    // Wire rising-edge detection to the plugin-level handler
                    // (applies profile, reads calibration, etc.).
                    c.DetectedRisingEdge += () => OnControllerDetected(c);
                    c.FirmwareLogLine += line => OnFirmwareLogLine(c, line);
                    // Forward the serial-interrogation result so the plugin can
                    // re-key per-device settings from the transport identity to
                    // the stable serial (settings follow the physical unit).
                    c.SerialResolved += (id, ser) =>
                    {
                        try { _onSerialResolved?.Invoke(id, ser); }
                        catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] OnSerialResolved: {ex.Message}"); }
                    };
                    // Forward live connectivity so the plugin can persist it
                    // (the seed for the NEXT controller) and heal stale roles.
                    c.ConnectivityResolved += conn =>
                    {
                        try { _onConnectivityResolved?.Invoke(c.Identity, conn); }
                        catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] OnConnectivityResolved: {ex.Message}"); }
                    };
                    c.RoutingResolved += _ => OnControllerRoutingResolved(c);
                    // Arm phantom-axis protection immediately from the persisted
                    // last-known connectivity (live diagnostic overrides later).
                    try { c.SeedConnectedAxes(_connectivitySeedLookup?.Invoke(kvp.Key)); }
                    catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Connectivity seed: {ex.Message}"); }
                    _byIdentity[kvp.Key] = c;
                    _order.Add(c);
                    (added ??= new List<MBoosterDeviceController>()).Add(c);
                }

                // Drop stale ones. Routed lanes have no USB port to diff
                // against — their lifecycle follows the owning base/hub pipe.
                var toRemove = new List<string>();
                foreach (var kvp in _byIdentity)
                {
                    if (kvp.Value.IsRouted) continue;
                    if (!currentByIdentity.ContainsKey(kvp.Key))
                        toRemove.Add(kvp.Key);
                }
                foreach (var key in toRemove)
                {
                    var c = _byIdentity[key];
                    _byIdentity.Remove(key);
                    _order.Remove(c);
                    (removed ??= new List<MBoosterDeviceController>()).Add(c);
                }

                // Publish the new count for the lock-free hot-path gate.
                _orderSnapshot = _order.ToArray();
                Volatile.Write(ref _controllerCount, _order.Count);
            }

            // Connect newcomers + dispose removed (outside the lock — Connect
            // may block briefly and we don't want to hold the registry lock).
            if (added != null)
            {
                foreach (var c in added)
                {
                    MozaLog.Info($"[AZOM/mBooster] Discovered {MBoosterDeviceController.ShortIdentity(c.Identity)} on {c.PortName}");
                    try
                    {
                        c.TryConnect();
                        try { DeviceAdded?.Invoke(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] DeviceAdded handler: {ex.Message}"); }
                    }
                    catch (Exception ex) { MozaLog.Warn($"[AZOM/mBooster] Connect failed for {c.Identity}: {ex.Message}"); }
                }
            }
            if (removed != null)
            {
                foreach (var c in removed)
                {
                    MozaLog.Info($"[AZOM/mBooster] Removed {MBoosterDeviceController.ShortIdentity(c.Identity)} (port gone from registry)");
                    try { c.Dispose(); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Dispose: {ex.Message}"); }
                    // A running calibration expects exactly this: its own soft
                    // reboot takes the port away. Tell the runner before the
                    // public event so it can't miss the drop.
                    try { CalibrationRunnerOrNull?.OnDeviceRemoved(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] runner OnDeviceRemoved: {ex.Message}"); }
                    try { DeviceRemoved?.Invoke(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] DeviceRemoved handler: {ex.Message}"); }
                }
            }

            // Reconnect any healthy-port-but-disconnected controllers (port
            // wedged, restored on next attempt). Snapshot under the lock, then
            // connect outside it: SerialPort.Open can block ~600ms under Wine
            // and must not stall other _lock holders (the telemetry/HID fan-out
            // takes _lock every tick).
            List<MBoosterDeviceController>? toReconnect = null;
            lock (_lock)
            {
                foreach (var c in _order)
                    if (!c.IsConnected)
                        (toReconnect ??= new List<MBoosterDeviceController>()).Add(c);
            }
            if (toReconnect != null)
            {
                foreach (var c in toReconnect)
                {
                    try { c.TryConnect(); }
                    catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Reconnect: {ex.Message}"); }
                }
            }

            // Routed lanes: re-elicit detection after the owning pipe comes
            // back (a USB lane does this from its own TryConnect; a routed
            // lane never opens the shared pipe itself). Runs on the same 5 s
            // cadence and stops as soon as an mbooster-* response re-latches.
            List<MBoosterDeviceController>? toNudge = null;
            lock (_lock)
            {
                foreach (var c in _order)
                    if (c.IsRouted && c.IsConnected && !c.Detected)
                        (toNudge ??= new List<MBoosterDeviceController>()).Add(c);
            }
            if (toNudge != null)
            {
                foreach (var c in toNudge)
                {
                    try { c.RequestCalibrationReads(); }
                    catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Routed re-detect: {ex.Message}"); }
                }
            }
        }

        /// <summary>
        /// Register a ROUTED lane — an mBooster tunneled behind a wheelbase/hub
        /// pedal port (dev 0x19) rather than on its own USB pipe. The caller
        /// (MozaPlugin) creates the controller against the owning pipe's shared
        /// connection and only registers it once the model-name read has
        /// confirmed an actual mBooster. Wires the same events as USB discovery;
        /// identity/detection already latched during the pre-registration probe
        /// are replayed so settings apply and serial re-keying happen exactly
        /// once, same as a USB lane's rising edge.
        /// </summary>
        public void AddRoutedLane(MBoosterDeviceController c)
        {
            if (c == null || !c.IsRouted) return;
            lock (_lock)
            {
                if (_byIdentity.ContainsKey(c.Identity)) return;
                c.DetectedRisingEdge += () => OnControllerDetected(c);
                c.FirmwareLogLine += line => OnFirmwareLogLine(c, line);
                c.SerialResolved += (id, ser) =>
                {
                    try { _onSerialResolved?.Invoke(id, ser); }
                    catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] OnSerialResolved: {ex.Message}"); }
                };
                c.ConnectivityResolved += conn =>
                {
                    try { _onConnectivityResolved?.Invoke(c.Identity, conn); }
                    catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] OnConnectivityResolved: {ex.Message}"); }
                };
                c.RoutingResolved += _ => OnControllerRoutingResolved(c);
                try { c.SeedConnectedAxes(_connectivitySeedLookup?.Invoke(c.Identity)); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Connectivity seed: {ex.Message}"); }
                _byIdentity[c.Identity] = c;
                _order.Add(c);
                _orderSnapshot = _order.ToArray();
                Volatile.Write(ref _controllerCount, _order.Count);
            }
            MozaLog.Info($"[AZOM/mBooster] Routed lane registered: {MBoosterDeviceController.ShortIdentity(c.Identity)} ({c.PortName}, dev 0x{c.HostDeviceId:x2})");
            c.StartWorkers();
            // Replay identity already gathered by the probe: the rising edge
            // and serial both fired before these handlers were wired.
            if (c.Detected) OnControllerDetected(c);
            if (!string.IsNullOrEmpty(c.Serial))
            {
                try { _onSerialResolved?.Invoke(c.Identity, c.Serial!); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] OnSerialResolved replay: {ex.Message}"); }
            }
            try { DeviceAdded?.Invoke(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] DeviceAdded handler: {ex.Message}"); }
        }

        private void OnControllerDetected(MBoosterDeviceController c)
        {
            try { _onDeviceDetectedEdge?.Invoke(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] OnDetectedEdge: {ex.Message}"); }
            // A calibration run crossing its own soft reboot resumes from here.
            // After _onDeviceDetectedEdge, so the reconnect's config re-push is
            // already queued when the runner re-arms the effect suspension.
            try { CalibrationRunnerOrNull?.OnDeviceDetected(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] runner OnDeviceDetected: {ex.Message}"); }
            try { DeviceDetected?.Invoke(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] DeviceDetected handler: {ex.Message}"); }
        }

        /// <summary>Route a controller's group-0x0E firmware-log line to a
        /// running calibration, which reports the firmware's own progress
        /// wording instead of just a countdown.</summary>
        internal void OnFirmwareLogLine(MBoosterDeviceController c, string line)
        {
            try { CalibrationRunnerOrNull?.OnFirmwareLogLine(c, line); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] runner OnFirmwareLogLine: {ex.Message}"); }
        }

        /// <summary>
        /// The lane's motor/config device-id routing just became authoritative
        /// (or changed) — re-run the detection-edge path so every calibration
        /// value is re-pushed to the CORRECT device id. The detection edge fires
        /// long before the once-a-minute pedal-type diagnostic that decides
        /// routing, so the original apply may have gone to a device id the
        /// coarser fallback guessed (bundle KY3HK4QP: a phantom 0x1d that
        /// answered nothing). Same handler, so the writes stay sentinel-guarded —
        /// a lane with no overrides still produces zero traffic.
        /// </summary>
        private void OnControllerRoutingResolved(MBoosterDeviceController c)
        {
            if (_isShuttingDown() || !c.Detected) return;
            try { _onDeviceDetectedEdge?.Invoke(c); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] OnRoutingResolved: {ex.Message}"); }
        }

        /// <summary>
        /// Fan-out the latest game telemetry to every controller's worker.
        /// Called from <c>MozaPlugin.DataUpdate</c> once per SimHub tick.
        /// </summary>
        public void OnDataUpdate(in MBoosterTelemetrySnapshot snap)
        {
            // Snapshot iteration — no registry lock across the settings lookup
            // (its own lock) and PostTelemetry (each worker's lock).
            var order = _orderSnapshot;
            {
                for (int i = 0; i < order.Length; i++)
                {
                    var c = order[i];
                    if (c.IsRouted)
                    {
                        // A routed lane has no mBooster HID of its own — its
                        // positions arrive via the base HID → MozaData. Mirror
                        // them back in by role so the UI bars/curve markers and
                        // the workers' live-position feeds (test toggles, Brake
                        // Fade) track the real pedal.
                        var s = _settingsLookup(c.Identity);
                        int axisCount = c.AxisCount > 0 ? c.AxisCount : 3;
                        if (axisCount > MBoosterDeviceController.MaxAxes) axisCount = MBoosterDeviceController.MaxAxes;
                        // Role resolution uses the CONNECTED count (see
                        // MBoosterDeviceController.ConnectedAxisCount), not the
                        // loop bound above — otherwise this mirrors positions
                        // in under the axis-order default role instead of the
                        // pedal's own, disagreeing with the merge below and
                        // with what the workers play.
                        int roleAxisCount = c.ConnectedAxisCount;
                        for (int a = 0; a < axisCount; a++)
                        {
                            double v;
                            switch (ResolveAxisRole(s, a, roleAxisCount))
                            {
                                case MBoosterRole.Throttle: v = _data.ThrottlePosition / 100.0; break;
                                case MBoosterRole.Brake:    v = _data.BrakePosition / 100.0; break;
                                case MBoosterRole.Clutch:   v = _data.ClutchPosition / 100.0; break;
                                default: v = 0; break;
                            }
                            c.LastAxisPositions[a] = v;
                            if (a < c.LastAxisRawPercentPreCurve.Length) c.LastAxisRawPercentPreCurve[a] = v * 100.0;
                            if (a == 0) { c.LastHidPosition = v; c.LastRawPercentPreCurve = v * 100.0; }
                        }
                    }
                    c.PostTelemetry(snap);
                }
            }
        }

        /// <summary>
        /// Update one device's HID position (called from <see cref="Protocol.MozaHidReader"/>
        /// when an mBooster HID axis changes). After saving the position on
        /// the controller, the merge step writes per-role values into
        /// <c>MozaData.{Throttle,Brake,Clutch}Position</c>. First-wins on
        /// role collision (logged once per (role, identity) combo).
        /// </summary>
        public void OnHidAxisUpdate(string identity, string containerId, int axisIndex, double pos01)
        {
            if (string.IsNullOrEmpty(identity)) return;
            if (axisIndex < 0) axisIndex = 0;
            if (axisIndex >= MBoosterDeviceController.MaxAxes) return;
            if (double.IsNaN(pos01)) pos01 = 0;
            if (pos01 < 0) pos01 = 0;
            if (pos01 > 1) pos01 = 1;

            MBoosterDeviceController? c;
            lock (_lock)
            {
                if (!_byIdentity.TryGetValue(identity, out c))
                {
                    // Container-ID rung: the HID + CDC interfaces of one physical
                    // device share a Container ID, so this pairs them even when
                    // Windows gives the two interfaces unrelated instance IDs (the
                    // common multi-lane case — see docs). No-ops when the ID is
                    // empty/absent (older drivers / Wine), falling through to the
                    // prefix + single-device rungs exactly as before.
                    c = FindByContainerIdLocked(containerId);
                    if (c == null) c = FindByInstancePrefixLocked(identity);
                    if (c == null && _byIdentity.Count == 1)
                    {
                        // Real-hardware logs show the HID and CDC interfaces of
                        // the same physical mBooster can get entirely unrelated
                        // Windows instance IDs (not just a differing trailing
                        // segment — see docs/protocol/devices/mbooster.md), so
                        // prefix matching can't always pair them. With exactly
                        // one mBooster registered there's no ambiguity: it must
                        // be this one.
                        using (var e = _byIdentity.GetEnumerator())
                        {
                            e.MoveNext();
                            c = e.Current.Value;
                        }
                        LogSingleDeviceFallbackOnceLocked(identity, c.Identity);
                    }
                    if (c == null) LogUnmatchedHidIdentityOnceLocked(identity);
                }
            }
            if (c == null) return;

            // Pedal Feel — Deadzone, Max Force, and the Pedal Feel curve
            // (InputCurveY) are all now REAL hardware calibration
            // (mbooster-brake-deadzone/-maxforce/-feelcurve-1..6, cmdId
            // 0xAB selectors 0x07-0x0E — see
            // MBoosterDeviceController.PushFeelCurveResync and
            // docs/protocol/devices/mbooster.md "Pedal Feel"): the device
            // reshapes the raw HID axis itself before this read ever sees
            // it, so there is nothing left to reshape here for those.
            // Sim Input Mapping (CurveY/CurveX) is the opposite: it has NO
            // wire command at all (see docs "Sim Input Mapping") — it
            // remaps THIS already-hardware-shaped value into what AZOM
            // reports to the sim, applied here so every downstream
            // consumer (position bar, MergePositions -> game telemetry,
            // the effect worker's brake-position fallback) sees the same
            // remapped value. Per-axis: the master (axis 0) uses the
            // lane's flat fields, each chained pedal uses its own per-pedal
            // entry.
            var laneSettings = _settingsLookup(c.Identity);
            IMBoosterPedalConfig? cfg = laneSettings;
            if (axisIndex > 0)
            {
                var pedals = laneSettings?.Pedals;
                if (pedals != null && pedals.TryGetValue(axisIndex, out var pp)) cfg = pp;
                // Sole-connected-pedal fallback: a standalone unit's pedal on
                // a non-zero axis may keep its config in the flat fields —
                // see MBoosterDeviceController.SoleConnectedAxis.
                else cfg = c.SoleConnectedAxis() == axisIndex ? laneSettings : null;
            }

            double posPct = pos01 * 100.0;
            if (cfg != null)
            {
                // Capture the TRUE raw reading — % of Max Force's own
                // hardware ceiling, i.e. the physical force the user is
                // actually applying — BEFORE the Threshold rescale below
                // changes posPct's meaning. Powers the "Input Force" live
                // label/marker; see LastAxisRawPercentPreThreshold's doc.
                if (axisIndex < c.LastAxisRawPercentPreThreshold.Length) c.LastAxisRawPercentPreThreshold[axisIndex] = posPct;

                // Max Threshold — HOST-SIDE rescale. Raw HID 100% is the
                // Pedal Feel curve's own hardware ceiling (Max Force's kg
                // value — see MBoosterDeviceController.PushFeelCurveResync),
                // NOT Max Threshold: the mbooster-brake-threshold wire write
                // (cmdId 0xB3) does not reliably change that on-device, per
                // hardware testing (bug bundle — "Max Threshold does
                // nothing" investigation). Max Threshold is meant to be a
                // purely host-side remap of the ALREADY-Max-Force-scaled raw
                // position into "100% at Threshold's kg" for the sim, same
                // category as Sim Input Mapping's CurveY/CurveX below (no
                // wire command actually does the real work). Unset (-1) or
                // non-positive Threshold is a no-op (ratio 1, same as
                // Threshold == Max Force) so an uncustomized profile keeps
                // its previous raw-passthrough behavior unchanged.
                double maxForceKg = cfg.MaxForceKg >= 0 ? cfg.MaxForceKg : 200.0;
                double thresholdKg = cfg.MaxThresholdKg > 0 ? cfg.MaxThresholdKg : maxForceKg;
                if (Math.Abs(thresholdKg - maxForceKg) > 0.0001)
                    posPct = Math.Min(100.0, posPct * (maxForceKg / thresholdKg));

                // Store the pre-remap percent for EVERY axis so the UI's
                // live curve markers follow whichever pedal is selected (axis 0
                // also mirrored to LastRawPercentPreCurve for legacy callers).
                if (axisIndex < c.LastAxisRawPercentPreCurve.Length) c.LastAxisRawPercentPreCurve[axisIndex] = posPct;
                if (axisIndex == 0) c.LastRawPercentPreCurve = posPct;
                if (cfg.CurveY != null && cfg.CurveY.Length == MBoosterUiConstants.SimInputMappingNodeCount)
                    posPct = EvaluateCurveArbitraryX(cfg.CurveX ?? DefaultCurveX, cfg.CurveY, posPct);
            }
            else
            {
                if (axisIndex < c.LastAxisRawPercentPreCurve.Length) c.LastAxisRawPercentPreCurve[axisIndex] = posPct;
                if (axisIndex == 0) c.LastRawPercentPreCurve = posPct;
            }

            double shaped01 = posPct / 100.0;
            if (axisIndex == 0) c.LastHidPosition = shaped01;

            c.LastAxisPositions[axisIndex] = shaped01;
            if (axisIndex + 1 > c.AxisCount) c.AxisCount = axisIndex + 1;

            // Merge step: re-compute the active positions across all devices
            // every time any one of them ticks. Cheap (N ≤ 3 typically).
            MergePositions();
        }

        private static double CubicBezier(double p0, double c1, double c2, double p1, double t)
        {
            double mt = 1 - t;
            return mt * mt * mt * p0 + 3 * mt * mt * t * c1 + 3 * mt * t * t * c2 + t * t * t * p1;
        }

        /// <summary>
        /// Catmull-Rom evaluation generalized to arbitrary (draggable) node
        /// X positions instead of a fixed spacing — used for the Sim Input
        /// Mapping output curve's horizontal node drag
        /// (<c>MBoosterDeviceSettings.CurveX</c>/<c>CurveY</c>). Purely
        /// host-side (see docs/protocol/devices/mbooster.md "Sim Input
        /// Mapping") — this remaps the pedal's already-hardware-shaped raw
        /// HID position into what AZOM reports as game telemetry; there is
        /// no wire command for it. Beyond the last node's X, returns that
        /// node's Y (flat plateau) — this is what makes "100% output
        /// before 100% input" work: drag the last node left and everything
        /// past it just stays at that Y. Node count is derived from
        /// <paramref name="xs"/>'s own length (not hardcoded to the current
        /// <see cref="MBoosterUiConstants.SimInputMappingNodeCount"/>) so
        /// this same evaluator can also resample an OLDER saved curve (e.g.
        /// a legacy 5-node one) at a NEW breakpoint set during migration —
        /// see MozaPlugin's curve-array migration.
        /// </summary>
        internal static double EvaluateCurveArbitraryX(float[] xs, float[] ys, double x)
        {
            if (xs == null || ys == null || xs.Length < 2 || xs.Length != ys.Length) return x;
            int n = xs.Length;

            var px = new double[n + 2];
            var py = new double[n + 2];
            px[0] = 0; py[0] = 0;
            for (int k = 0; k < n; k++) { px[k + 1] = xs[k]; py[k + 1] = ys[k]; }
            px[n + 1] = xs[n - 1]; py[n + 1] = ys[n - 1];

            if (x <= 0) return 0;
            if (x >= px[n + 1]) return py[n + 1];

            int i = 0;
            for (int k = 0; k <= n; k++)
            {
                if (x >= px[k] && x <= px[k + 1]) { i = k; break; }
            }
            int p0i = i == 0 ? 0 : i - 1;
            int p2i = i + 1;
            int p3i = (i + 2 >= px.Length) ? i + 1 : i + 2;

            double p0x = px[p0i], p0y = py[p0i];
            double p1x = px[i], p1y = py[i];
            double p2x = px[p2i], p2y = py[p2i];
            double p3x = px[p3i], p3y = py[p3i];
            if (p2x <= p1x) return p1y; // degenerate (equal X) — shouldn't happen given drag clamping

            double c1x = p1x + (p2x - p0x) / 6.0, c1y = p1y + (p2y - p0y) / 6.0;
            double c2x = p2x - (p3x - p1x) / 6.0, c2y = p2y - (p3y - p1y) / 6.0;

            double lo = 0, hi = 1;
            for (int iter = 0; iter < 24; iter++)
            {
                double t = (lo + hi) / 2.0;
                double bx = CubicBezier(p1x, c1x, c2x, p2x, t);
                if (bx < x) lo = t; else hi = t;
            }
            return CubicBezier(p1y, c1y, c2y, p2y, (lo + hi) / 2.0);
        }

        // Default (un-dragged) node X breakpoints for the Sim Input Mapping
        // output curve, 100/6 * k for k=1..6 — evenly spaced, last node at
        // exactly 100% so an untouched curve maps full input to full output.
        // (Previously 100/7 * k, inherited from the disproven/removed
        // curve7 mechanism's selectors purely for cosmetic continuity — see
        // docs/protocol/devices/mbooster.md "Sim Input Mapping" — which left
        // the last node short at ~85.7%, so "100% output before 100% input"
        // via EvaluateCurveArbitraryX's plateau only needs a user's explicit
        // drag now, not an already-shortened default.)
        private static readonly float[] DefaultCurveX =
            { 100f / 6f, 200f / 6f, 300f / 6f, 400f / 6f, 500f / 6f, 600f / 6f };

        // Default/un-dragged shape of the Pedal Feel curve's 6 Y nodes
        // (mbooster-brake-feelcurve-1..6, cmdId 0xAB selectors 0x08-0x0D),
        // as a fraction (0-1) of the way from Deadzone to Max Force —
        // REVISED to evenly-spaced sevenths (k/7 for k=1..6). Sevenths are
        // what "evenly spaced" means here: these 6 are the interior points
        // of an 8-point graph whose two ends are fixed and undraggable —
        // (0,0) = Deadzone (0x07) and (100,100) = Max Force (0x0E). A "Deadzone
        // slider does nothing" report prompted 4 fresh sweeps
        // (clutch-0-8kg-deadzone-sweep.pcapng, clutch-4-20kg-maxforce-sweep
        // .pcapng, throttle-0-6kg-deadzone-sweep.pcapng, throttle-4-20kg-
        // maxforce-sweep.pcapng): (value - deadzone) / (maxForce - deadzone)
        // landed within ~0.005 of k/7 at every sweep point on both roles,
        // while the previous asymmetric constants below were off by up to
        // 1.7kg — enough to visibly distort the curve on every Deadzone/Max
        // Force edit. Those constants were originally measured off a single
        // Brake unit's un-dragged curve (max-force-24-75-128-166-200.pcapng
        // / deadzone-0-5-11-14.pcapng) and assumed to be the factory Linear
        // default; that unit almost certainly already carried a non-default
        // curve from earlier testing, not evidence of a genuinely
        // non-uniform default shape. Previous (WRONG) measured values, kept
        // for history: { 0.08049, 0.19495, 0.44245, 0.72433, 0.90040,
        // 0.97910 }. See docs/protocol/devices/mbooster.md "Pedal Feel" and
        // bug bundles 5VR5AQ8Y / mBooster-deadzone-no-effect.
        internal static readonly double[] FeelCurveFractions =
            { 1.0 / 7, 2.0 / 7, 3.0 / 7, 4.0 / 7, 5.0 / 7, 6.0 / 7 };

        /// <summary>
        /// The 6 Y points of the Pedal Feel curve, in kg, ready to write to
        /// <c>mbooster-brake-feelcurve-1..6</c> — see
        /// <see cref="MBoosterDeviceController.PushFeelCurveResync"/>. Each
        /// node in <paramref name="inputCurve"/> is a percentage (0-100) of
        /// the Deadzone-Max Force span; falls back to
        /// <see cref="FeelCurveFractions"/> (the Linear default) for any
        /// node the user hasn't customized (null or wrong-length array).
        /// </summary>
        internal static double[] ComputeFeelCurveY(double deadzoneKg, double maxForceKg, float[]? inputCurve = null)
        {
            double range = maxForceKg - deadzoneKg;
            int n = FeelCurveFractions.Length;
            bool haveCurve = inputCurve != null && inputCurve.Length == n;
            var result = new double[n];
            for (int i = 0; i < n; i++)
            {
                double frac01 = haveCurve ? inputCurve![i] / 100.0 : FeelCurveFractions[i];
                result[i] = deadzoneKg + frac01 * range;
            }
            return result;
        }

        /// <summary>
        /// The 6 X points of the Pedal Feel curve, in kg, ready to write to
        /// <c>mbooster-brake-feelcurve-x-1..6</c>. UNLIKE the Y nodes above,
        /// X is NOT relative to the pedal's own Deadzone-Max Force span —
        /// the same 4 sweeps documented on <see cref="FeelCurveFractions"/>
        /// showed the X selectors (0xAB 0x01-0x06) sent bit-for-bit
        /// identical values across an ENTIRE Deadzone sweep and an entire
        /// Max Force sweep, on both Throttle and Clutch, landing within
        /// ~0.15kg of k/7 * 200 — the fixed 0-200kg full-scale every other
        /// Pedal Feel field shares (see
        /// <see cref="MozaMBoosterProtocol.EncodeThresholdKg"/>) —
        /// regardless of that pedal's actual configured Deadzone/Max Force.
        /// Each node in <paramref name="inputCurve"/> is a percentage
        /// (0-100) of that same fixed 0-200kg scale (NOT of Deadzone-Max
        /// Force, unlike Y's <paramref name="inputCurve"/> above).
        /// </summary>
        internal static double[] ComputeFeelCurveX(float[]? inputCurve = null)
        {
            int n = FeelCurveFractions.Length;
            bool haveCurve = inputCurve != null && inputCurve.Length == n;
            var result = new double[n];
            for (int i = 0; i < n; i++)
            {
                double frac01 = haveCurve ? inputCurve![i] / 100.0 : FeelCurveFractions[i];
                result[i] = frac01 * 200.0;
            }
            return result;
        }

        /// <summary>
        /// Fallback pairing for <see cref="OnHidAxisUpdate"/> when the HID
        /// identity doesn't exactly match a known CDC identity. Per
        /// docs/protocol/devices/mbooster.md "HID identity reconciliation",
        /// the two instance IDs come from different USB interfaces of the
        /// same composite device and share every segment except the trailing
        /// interface-index one (e.g. CDC <c>a&amp;399b951f&amp;0&amp;0000</c> vs
        /// HID <c>a&amp;399b951f&amp;0&amp;0002</c>) — strip that last
        /// "&amp;NNNN" segment from both sides and match on what remains.
        /// Must be called with <see cref="_lock"/> already held.
        /// </summary>
        private MBoosterDeviceController? FindByInstancePrefixLocked(string hidIdentity)
        {
            string prefix = InstancePrefix(hidIdentity);
            if (prefix.Length == 0) return null;
            foreach (var kvp in _byIdentity)
            {
                if (string.Equals(InstancePrefix(kvp.Key), prefix, StringComparison.OrdinalIgnoreCase))
                {
                    LogPrefixPairingOnce(hidIdentity, kvp.Key);
                    return kvp.Value;
                }
            }
            return null;
        }

        /// <summary>
        /// Pair a HID axis stream to its CDC lane by Windows Container ID — the
        /// robust path when the two interfaces of one physical device get
        /// unrelated instance IDs (so exact + prefix both fail), which the
        /// real-hardware finding in docs/protocol/devices/mbooster.md shows is
        /// the norm. The Container ID is identical across all interfaces of one
        /// composite device. Must be called with <see cref="_lock"/> held.
        /// Returns null when the HID side reported no Container ID (older
        /// drivers / Wine) so the caller falls through to the other rungs.
        /// </summary>
        private MBoosterDeviceController? FindByContainerIdLocked(string containerId)
        {
            if (string.IsNullOrEmpty(containerId)) return null;
            foreach (var kvp in _byIdentity)
            {
                var c = kvp.Value;
                if (!string.IsNullOrEmpty(c.ContainerId) &&
                    string.Equals(c.ContainerId, containerId, StringComparison.OrdinalIgnoreCase))
                {
                    LogContainerPairingOnce(containerId, c.Identity);
                    return c;
                }
            }
            return null;
        }

        private readonly HashSet<string> _containerPairingsLogged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void LogContainerPairingOnce(string containerId, string cdcIdentity)
        {
            bool isNew;
            lock (_containerPairingsLogged)
                isNew = _containerPairingsLogged.Add(containerId);
            if (isNew)
            {
                MozaLog.Info(
                    $"[AZOM/mBooster] Paired HID axis to CDC device " +
                    $"'{MBoosterDeviceController.ShortIdentity(cdcIdentity)}' via Container ID " +
                    $"'{containerId}' (exact identity match failed — the expected multi-lane path).");
            }
        }

        private static string InstancePrefix(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            int lastAmp = id.LastIndexOf('&');
            return lastAmp > 0 ? id.Substring(0, lastAmp) : "";
        }

        private readonly HashSet<string> _prefixPairingsLogged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void LogPrefixPairingOnce(string hidIdentity, string cdcIdentity)
        {
            bool isNew;
            lock (_prefixPairingsLogged)
                isNew = _prefixPairingsLogged.Add(hidIdentity);
            if (isNew)
            {
                MozaLog.Info(
                    $"[AZOM/mBooster] Paired HID axis identity '{hidIdentity}' to CDC device " +
                    $"'{MBoosterDeviceController.ShortIdentity(cdcIdentity)}' via instance-prefix fallback " +
                    "(exact identity match failed — see docs/protocol/devices/mbooster.md).");
            }
        }

        private readonly HashSet<string> _singleDeviceFallbacksLogged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void LogSingleDeviceFallbackOnceLocked(string hidIdentity, string cdcIdentity)
        {
            bool isNew;
            lock (_singleDeviceFallbacksLogged)
                isNew = _singleDeviceFallbacksLogged.Add(hidIdentity);
            if (isNew)
            {
                MozaLog.Info(
                    $"[AZOM/mBooster] Paired HID axis identity '{hidIdentity}' to CDC device " +
                    $"'{MBoosterDeviceController.ShortIdentity(cdcIdentity)}' via single-device fallback " +
                    "(exact and prefix identity matches both failed, but only one mBooster is registered " +
                    "so there's no ambiguity — see docs/protocol/devices/mbooster.md).");
            }
        }

        /// <summary>
        /// Walk all devices in enumeration order and assign each device's
        /// position to the matching <c>MozaData</c> field if its role is set.
        /// First-wins on collision (later devices with the same role are
        /// ignored). Devices with Role=Disabled contribute nothing. Axes the
        /// "PD Linked" diagnostic reports as having no pedal are skipped —
        /// the HID exposes 3 axes regardless of how many pedals are wired,
        /// and a phantom axis's role claim would otherwise first-wins a real
        /// pedal's role with a frozen 0 position.
        /// </summary>
        private readonly HashSet<string> _unmatchedHidLogged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// One-time-per-identity Warn (so it's visible in SimHub's regular
        /// log, not just the support bundle) when a HID axis identity
        /// matches no known CDC device by either exact or prefix match.
        /// Logs full, untruncated identities — needed to actually diagnose
        /// the HID/CDC reconciliation gap rather than guess at it again.
        /// Must be called with <see cref="_lock"/> already held.
        /// </summary>
        private void LogUnmatchedHidIdentityOnceLocked(string hidIdentity)
        {
            bool isNew;
            lock (_unmatchedHidLogged)
                isNew = _unmatchedHidLogged.Add(hidIdentity);
            if (!isNew) return;
            string known = _byIdentity.Count == 0 ? "(none)" : string.Join(", ", _byIdentity.Keys);
            MozaLog.Warn(
                $"[AZOM/mBooster] HID axis identity '{hidIdentity}' matched no known CDC device " +
                $"(known CDC identities: [{known}]) — position will not update for this device. " +
                "See docs/protocol/devices/mbooster.md \"HID identity reconciliation\".");
        }

        private void MergePositions()
        {
            bool throttleSet = false, brakeSet = false, clutchSet = false;
            // First-wins iteration over the copy-on-write snapshot (runs per HID
            // report; the list itself only changes on the 5 s reconnect tick). Each
            // lane hosts up to 3 pedal axes; every axis routes independently by role.
            var order = _orderSnapshot;
            {
                for (int i = 0; i < order.Length; i++)
                {
                    var c = order[i];
                    // Routed lanes never merge: their positions already ARRIVE
                    // as MozaData.{Throttle,Brake,Clutch}Position via the base
                    // HID path — merging the mirrored copies back would just
                    // echo (or, before the mirror ticks, zero) the real values.
                    if (c.IsRouted) continue;
                    var s = _settingsLookup(c.Identity);
                    int rawAxisCount = c.AxisCount > 0 ? c.AxisCount : 1;
                    if (rawAxisCount > MBoosterDeviceController.MaxAxes) rawAxisCount = MBoosterDeviceController.MaxAxes;

                    // Resolve roles against how many axes are ACTUALLY wired,
                    // not the raw HID axis count — a chain-capable hub's report
                    // descriptor exposes all 3 GenericDesktop axes even when
                    // only one pedal is physically plugged in, so raw AxisCount
                    // can't tell a real chain from a single connected pedal.
                    // Getting this wrong silently overrides that pedal's own
                    // Role with the axis-order default (see IsAxisConnected).
                    int connectedAxisCount = 0;
                    for (int a = 0; a < rawAxisCount; a++)
                        if (c.IsAxisConnected(a)) connectedAxisCount++;

                    for (int a = 0; a < rawAxisCount; a++)
                    {
                        if (!c.IsAxisConnected(a)) continue;
                        var role = ResolveAxisRole(s, a, connectedAxisCount);
                        if (role == MBoosterRole.Disabled) continue;
                        // MozaData position fields are int (0..100, the same scale
                        // the existing HID reader writes). Round explicitly.
                        int v100 = (int)Math.Round(c.LastAxisPositions[a] * 100.0);
                        if (v100 < 0) v100 = 0; if (v100 > 100) v100 = 100;
                        switch (role)
                        {
                            case MBoosterRole.Throttle:
                                if (!throttleSet) { _data.ThrottlePosition = v100; throttleSet = true; if (v100 > _maxThrottleSeen) _maxThrottleSeen = v100; }
                                else { LogCollisionOnce("throttle", c.Identity); }
                                break;
                            case MBoosterRole.Brake:
                                if (!brakeSet) { _data.BrakePosition = v100; brakeSet = true; if (v100 > _maxBrakeSeen) _maxBrakeSeen = v100; }
                                else { LogCollisionOnce("brake", c.Identity); }
                                break;
                            case MBoosterRole.Clutch:
                                if (!clutchSet) { _data.ClutchPosition = v100; clutchSet = true; if (v100 > _maxClutchSeen) _maxClutchSeen = v100; }
                                else { LogCollisionOnce("clutch", c.Identity); }
                                break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Role for one axis of a lane. An explicit per-axis override
        /// (<see cref="MBoosterDeviceSettings.AxisRoles"/>, set by the UI when
        /// the user remaps) always wins. Otherwise: a single-axis device uses
        /// the legacy <see cref="MBoosterDeviceSettings.Role"/> (exact backward
        /// compat); axis 0 of a grown-but-not-yet-remapped chain ALSO honors an
        /// already-explicit Role rather than the position default, so a pedal
        /// the user set to Brake while solo doesn't silently become "Throttle"
        /// the moment a second pedal gets chained onto the same lane. Any other
        /// axis (or axis 0 with Role still at its Disabled default) falls back
        /// to axis order: [Throttle, Brake, Clutch]. That order is the standard
        /// Moza pedal usage convention — real hardware (support bundle
        /// 2026-07-07) exposes the chain's pedals as GenericDesktop axes
        /// Rx(0x33)/Ry(0x34)/Rz(0x35), which ascending-sorted give index 0/1/2,
        /// and Moza maps Rx→throttle, Ry→brake, Rz→clutch (see
        /// MozaHidClass.Pedals). The user remaps via the UI if a given unit's
        /// wiring differs.
        /// </summary>
        internal static MBoosterRole ResolveAxisRole(MBoosterDeviceSettings? s, int axisIndex, int axisCount)
        {
            var roles = s?.AxisRoles;
            if (roles != null && axisIndex >= 0 && axisIndex < roles.Length)
                return roles[axisIndex];
            if (axisCount <= 1)
                return s?.Role ?? MBoosterRole.Disabled;
            // Axis 0 IS the legacy Role field's slot for a single-axis device
            // (see the axisCount<=1 branch above) — if the chain then grows to
            // a multi-pedal lane before AxisRoles is ever explicitly seeded,
            // honor whatever the user already set there instead of silently
            // reverting axis 0 to the position-based Throttle default below.
            // Losing an explicitly-set Brake there used to hide the brake-only
            // Sensor Output Ratio/Max Threshold sliders (and mis-route
            // calibration writes) for a pedal the user never touched.
            var legacy = s?.Role ?? MBoosterRole.Disabled;
            if (axisIndex == 0 && legacy != MBoosterRole.Disabled)
                return legacy;
            return PositionalRole(axisIndex, legacy);
        }

        // Axis order → role, the standard Moza pedal convention: Rx(0x33) =
        // Throttle, Ry(0x34) = Brake, Rz(0x35) = Clutch.
        private static readonly MBoosterRole[] PositionalRoles =
            { MBoosterRole.Throttle, MBoosterRole.Brake, MBoosterRole.Clutch };

        /// <summary>
        /// The positional default for an axis, EXCLUDING whatever role axis 0
        /// already took from the legacy <see cref="MBoosterDeviceSettings.Role"/>
        /// field (<see cref="MBoosterRole.Disabled"/> = nothing claimed, plain
        /// axis order).
        ///
        /// Without the exclusion, a lane that grew from one pedal to several
        /// while Role said "Brake" handed BOTH axis 0 (honoring Role, above)
        /// and axis 1 (positional default) the Brake role. Two pedals claiming
        /// one role is never a valid state — the UI enforces that on every
        /// explicit assignment (ClearDuplicateMBoosterRoleAssignments) — and it
        /// broke both halves of the pedal pipeline: the second pedal's position
        /// was dropped as a collision (see LogCollisionOnce), and because
        /// MBoosterEffectWorker.TargetDevice addresses frames by ROLE, both
        /// pedals' effect streams were sent to the SAME motor — one pedal
        /// playing the other's effects while the other stayed silent.
        /// </summary>
        private static MBoosterRole PositionalRole(int axisIndex, MBoosterRole claimedByAxis0)
        {
            if (axisIndex < 0 || axisIndex >= PositionalRoles.Length) return MBoosterRole.Disabled;
            var positional = PositionalRoles[axisIndex];
            if (claimedByAxis0 == MBoosterRole.Disabled || positional != claimedByAxis0)
                return positional;
            // This axis's positional role is the one axis 0 just took from the
            // legacy Role field, so the two SWAP: this axis inherits axis 0's
            // own positional role (Throttle) instead. A swap keeps the lane a
            // permutation — every role on exactly one pedal — while leaving
            // every non-colliding axis on its documented positional default.
            return PositionalRoles[0];
        }

        /// <summary>
        /// The full per-pedal config object for one axis of a lane, creating a
        /// missing chained-pedal entry on demand: the master's flat fields for
        /// axis 0, else <see cref="MBoosterDeviceSettings.Pedals"/>[axis].
        /// A lane whose SOLE connected pedal is this (non-zero) axis with no
        /// per-pedal entry gets the flat fields instead (and never creates the
        /// entry) — that's where the config landed while the UI still showed the
        /// axis-0 row, and creating an empty entry here would orphan it. See
        /// <see cref="MBoosterDeviceController.SoleConnectedAxis"/>.
        /// </summary>
        internal static IMBoosterPedalConfig? GetOrCreatePedalConfig(
            MBoosterDeviceSettings? s, int axisIndex, int soleConnectedAxis)
        {
            if (s == null) return null;
            if (axisIndex <= 0) return s;
            if (!s.Pedals.TryGetValue(axisIndex, out var p))
            {
                if (soleConnectedAxis == axisIndex) return s;
                // Copy-on-write: publish a NEW dictionary via atomic reference
                // swap rather than mutating in place, so the 50 Hz effect worker
                // threads reading s.Pedals never see a dictionary mid-resize.
                p = new MBoosterPedalSettings();
                s.Pedals = new Dictionary<int, MBoosterPedalSettings>(s.Pedals) { [axisIndex] = p };
            }
            return p;
        }

        /// <summary>
        /// Same resolution as <see cref="GetOrCreatePedalConfig"/> but WITHOUT
        /// creating a missing chained-pedal entry — for read-only callers
        /// (control seeding, import previews) so merely looking at a pedal never
        /// persists an empty entry. Null when that pedal has no config yet.
        /// </summary>
        internal static IMBoosterPedalConfig? PeekPedalConfig(
            MBoosterDeviceSettings? s, int axisIndex, int soleConnectedAxis)
        {
            if (s == null) return null;
            if (axisIndex <= 0) return s;
            if (s.Pedals.TryGetValue(axisIndex, out var p)) return p;
            return soleConnectedAxis == axisIndex ? s : null;
        }

        private void LogCollisionOnce(string role, string identity)
        {
            string key = role + ":" + identity;
            bool isNew;
            lock (_collisionsLogged)
                isNew = _collisionsLogged.Add(key);
            if (isNew)
            {
                MozaLog.Warn(
                    $"[AZOM/mBooster] Role collision: {MBoosterDeviceController.ShortIdentity(identity)} " +
                    $"is configured as '{role}' but another mBooster already claimed that role — its position will be ignored.");
            }
        }

        /// <summary>Lookup a controller by identity (e.g. for UI selection).</summary>
        public MBoosterDeviceController? FindByIdentity(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return null;
            lock (_lock)
            {
                _byIdentity.TryGetValue(identity, out var c);
                return c;
            }
        }

        public void Dispose()
        {
            MBoosterCalibrationRunner? runner;
            lock (_calibrationRunnerLock) { runner = _calibrationRunner; _calibrationRunner = null; }
            try { runner?.Dispose(); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] runner Dispose: {ex.Message}"); }

            List<MBoosterDeviceController> all;
            lock (_lock)
            {
                all = _order.ToList();
                _order.Clear();
                _orderSnapshot = System.Array.Empty<MBoosterDeviceController>();
                _byIdentity.Clear();
            }
            foreach (var c in all)
            {
                try { c.Dispose(); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Dispose {c.Identity}: {ex.Message}"); }
            }
        }
    }
}
