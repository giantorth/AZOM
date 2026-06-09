using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
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

        /// <summary>Snapshot of all known controllers in enumeration order.</summary>
        public IReadOnlyList<MBoosterDeviceController> Devices
        {
            get
            {
                lock (_lock)
                    return new ReadOnlyCollection<MBoosterDeviceController>(_order.ToList());
            }
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
            Action<MBoosterDeviceController>? onDeviceDetectedEdge = null)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _settingsLookup = settingsLookup ?? throw new ArgumentNullException(nameof(settingsLookup));
            _isShuttingDown = isShuttingDown ?? (() => false);
            _onDeviceDetectedEdge = onDeviceDetectedEdge;
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
                        isShuttingDown: _isShuttingDown);
                    // Wire rising-edge detection to the plugin-level handler
                    // (applies profile, reads calibration, etc.).
                    c.DetectedRisingEdge += () => OnControllerDetected(c);
                    _byIdentity[kvp.Key] = c;
                    _order.Add(c);
                    (added ??= new List<MBoosterDeviceController>()).Add(c);
                }

                // Drop stale ones.
                var toRemove = new List<string>();
                foreach (var kvp in _byIdentity)
                {
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
        }

        private void OnControllerDetected(MBoosterDeviceController c)
        {
            try { _onDeviceDetectedEdge?.Invoke(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] OnDetectedEdge: {ex.Message}"); }
            try { DeviceDetected?.Invoke(c); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] DeviceDetected handler: {ex.Message}"); }
        }

        /// <summary>
        /// Fan-out the latest game telemetry to every controller's worker.
        /// Called from <c>MozaPlugin.DataUpdate</c> once per SimHub tick.
        /// </summary>
        public void OnDataUpdate(in MBoosterTelemetrySnapshot snap)
        {
            lock (_lock)
            {
                for (int i = 0; i < _order.Count; i++)
                    _order[i].PostTelemetry(snap);
            }
        }

        /// <summary>
        /// Update one device's HID position (called from <see cref="Protocol.MozaHidReader"/>
        /// when an mBooster HID axis changes). After saving the position on
        /// the controller, the merge step writes per-role values into
        /// <c>MozaData.{Throttle,Brake,Clutch}Position</c>. First-wins on
        /// role collision (logged once per (role, identity) combo).
        /// </summary>
        public void OnHidAxisUpdate(string identity, double pos01)
        {
            if (string.IsNullOrEmpty(identity)) return;
            if (double.IsNaN(pos01)) pos01 = 0;
            if (pos01 < 0) pos01 = 0;
            if (pos01 > 1) pos01 = 1;

            MBoosterDeviceController? c;
            lock (_lock)
                _byIdentity.TryGetValue(identity, out c);
            if (c == null) return;
            c.LastHidPosition = pos01;

            // Merge step: re-compute the active positions across all devices
            // every time any one of them ticks. Cheap (N ≤ 3 typically).
            MergePositions();
        }

        /// <summary>
        /// Walk all devices in enumeration order and assign each device's
        /// position to the matching <c>MozaData</c> field if its role is set.
        /// First-wins on collision (later devices with the same role are
        /// ignored). Devices with Role=Disabled contribute nothing.
        /// </summary>
        private void MergePositions()
        {
            bool throttleSet = false, brakeSet = false, clutchSet = false;
            // First-wins iteration over _order while holding the lock so the
            // controller list can't mutate underneath us.
            lock (_lock)
            {
                for (int i = 0; i < _order.Count; i++)
                {
                    var c = _order[i];
                    var s = _settingsLookup(c.Identity);
                    if (s == null) continue;
                    // MozaData position fields are int (0..100, the same scale
                    // the existing HID reader writes). Round explicitly.
                    int v100 = (int)Math.Round(c.LastHidPosition * 100.0);
                    if (v100 < 0) v100 = 0; if (v100 > 100) v100 = 100;
                    switch (s.Role)
                    {
                        case MBoosterRole.Throttle:
                            if (!throttleSet)
                            {
                                _data.ThrottlePosition = v100;
                                throttleSet = true;
                            }
                            else { LogCollisionOnce("throttle", c.Identity); }
                            break;
                        case MBoosterRole.Brake:
                            if (!brakeSet)
                            {
                                _data.BrakePosition = v100;
                                brakeSet = true;
                            }
                            else { LogCollisionOnce("brake", c.Identity); }
                            break;
                        case MBoosterRole.Clutch:
                            if (!clutchSet)
                            {
                                _data.ClutchPosition = v100;
                                clutchSet = true;
                            }
                            else { LogCollisionOnce("clutch", c.Identity); }
                            break;
                        case MBoosterRole.Disabled:
                        default:
                            break;
                    }
                }
            }
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
            List<MBoosterDeviceController> all;
            lock (_lock)
            {
                all = _order.ToList();
                _order.Clear();
                _byIdentity.Clear();
            }
            foreach (var c in all)
            {
                try { c.Dispose(); } catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Dispose {c.Identity}: {ex.Message}"); }
            }
        }
    }
}
