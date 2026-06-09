using System;
using System.Collections.Generic;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Multi-connection owner for MOZA peripherals plugged STRAIGHT into the PC
    /// (their own USB CDC port + PID) rather than through a wheelbase or
    /// Universal Hub. The user may have one of each attached simultaneously
    /// (a pedal set AND a handbrake, each on its own port), so a single shared
    /// connection won't do — each peripheral gets its own
    /// <see cref="StandalonePeripheralController"/>, one per discovered port.
    ///
    /// Modeled on <see cref="MozaMBoosterRegistry"/>: <see cref="Refresh"/> walks
    /// <see cref="MozaPortDiscovery"/> for supported peripheral PIDs, spawns a
    /// controller for each newly-attached device, drops any whose port has
    /// disappeared, and reconnects healthy-but-disconnected ones. Called from
    /// the plugin's 5 s reconnect timer.
    ///
    /// Config/calibration only — axes stay HID-sourced. Detection ownership
    /// (<c>PedalsOwner</c>/<c>HandbrakeOwner</c>) reuse means the existing
    /// Pedals/Handbrake UI tabs + HardwareApplier write paths work unchanged.
    /// </summary>
    internal sealed class MozaStandalonePeripheralRegistry : IDisposable
    {
        // identity → controller (one per physical peripheral)
        private readonly Dictionary<string, StandalonePeripheralController> _byIdentity =
            new Dictionary<string, StandalonePeripheralController>(StringComparer.OrdinalIgnoreCase);
        private readonly List<StandalonePeripheralController> _order =
            new List<StandalonePeripheralController>();
        private readonly object _lock = new object();

        private readonly MozaPlugin _plugin;
        private readonly MozaData _data;
        private readonly DeviceDetectionState _detectionState;
        private readonly Func<bool> _isShuttingDown;

        // Supported peripheral PIDs (pedals + handbrake). Shifters are excluded
        // until they gain a settings/UI surface — see StandalonePeripheralDescriptor.
        private static bool IsSupportedPid(ushort pid) =>
            MozaUsbIds.IsPedalsPid(pid) || MozaUsbIds.IsHandbrakePid(pid);

        public MozaStandalonePeripheralRegistry(
            MozaPlugin plugin,
            MozaData data,
            DeviceDetectionState detectionState,
            Func<bool> isShuttingDown)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _detectionState = detectionState ?? throw new ArgumentNullException(nameof(detectionState));
            _isShuttingDown = isShuttingDown ?? (() => false);
        }

        /// <summary>
        /// Walk the port-discovery cache for supported peripheral PIDs; spawn a
        /// controller for any newly-attached device, drop any whose port has
        /// disappeared, reconnect disconnected ones, and re-probe connected-but-
        /// undetected ones. Idempotent — no-ops on a healthy steady state.
        /// </summary>
        public void Refresh()
        {
            if (_isShuttingDown()) return;

            var ports = MozaPortDiscovery.Instance.EnumerateMatching(IsSupportedPid);

            // Map current identities → port info for the add/drop diff.
            var currentByIdentity = new Dictionary<string, MozaPortDiscovery.PortInfo>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < ports.Count; i++)
            {
                var p = ports[i];
                string id = !string.IsNullOrEmpty(p.InstanceId)
                    ? p.InstanceId
                    : "port:" + p.PortName;
                currentByIdentity[id] = p;
            }

            List<StandalonePeripheralController>? added = null;
            List<StandalonePeripheralController>? removed = null;

            lock (_lock)
            {
                // Add new ones.
                foreach (var kvp in currentByIdentity)
                {
                    if (_byIdentity.ContainsKey(kvp.Key)) continue;
                    var desc = StandalonePeripheralDescriptor.ForCategory(kvp.Value.Category);
                    if (desc == null) continue; // unsupported category — shouldn't happen given the PID filter
                    var c = new StandalonePeripheralController(
                        desc, kvp.Key, kvp.Value.PortName,
                        _plugin, _data, _detectionState, _isShuttingDown);
                    _byIdentity[kvp.Key] = c;
                    _order.Add(c);
                    (added ??= new List<StandalonePeripheralController>()).Add(c);
                }

                // Drop stale ones.
                List<string>? toRemove = null;
                foreach (var kvp in _byIdentity)
                    if (!currentByIdentity.ContainsKey(kvp.Key))
                        (toRemove ??= new List<string>()).Add(kvp.Key);
                if (toRemove != null)
                {
                    foreach (var key in toRemove)
                    {
                        var c = _byIdentity[key];
                        _byIdentity.Remove(key);
                        _order.Remove(c);
                        (removed ??= new List<StandalonePeripheralController>()).Add(c);
                    }
                }
            }

            // Connect newcomers + dispose removed (outside the lock — Connect /
            // Dispose can block briefly under Wine and must not stall _lock).
            if (added != null)
            {
                foreach (var c in added)
                {
                    MozaLog.Info($"[AZOM] Discovered standalone {c.Category} on {c.PortName}");
                    try { c.TryConnect(); }
                    catch (Exception ex) { MozaLog.Warn($"[AZOM] Standalone {c.Category} connect failed: {ex.Message}"); }
                }
            }
            if (removed != null)
            {
                foreach (var c in removed)
                {
                    MozaLog.Info($"[AZOM] Removed standalone {c.Category} (port gone from registry)");
                    try { c.Dispose(); } catch (Exception ex) { MozaLog.Debug($"[AZOM] Standalone dispose: {ex.Message}"); }
                }
            }

            // Reconnect disconnected + re-probe connected-but-undetected. Snapshot
            // under the lock, act outside it.
            List<StandalonePeripheralController> snapshot;
            lock (_lock)
                snapshot = new List<StandalonePeripheralController>(_order);
            foreach (var c in snapshot)
            {
                try
                {
                    if (!c.IsConnected) c.TryConnect();
                    else c.Poll();
                }
                catch (Exception ex) { MozaLog.Debug($"[AZOM] Standalone refresh: {ex.Message}"); }
            }
        }

        /// <summary>Retransmit each connected peripheral's tracked reads on ITS
        /// own pipe. Called from the plugin's retry timer alongside the base + hub
        /// pending-tick.</summary>
        public void TickRetransmits()
        {
            List<StandalonePeripheralController> snapshot;
            lock (_lock)
                snapshot = new List<StandalonePeripheralController>(_order);
            foreach (var c in snapshot)
            {
                if (!c.IsConnected) continue;
                try { c.PendingResponses.TickRetransmits(c.Connection.Send); }
                catch (Exception ex) { MozaLog.Warn($"[AZOM] Standalone {c.Category} retransmit tick failed: {ex.Message}"); }
            }
        }

        public void Dispose()
        {
            List<StandalonePeripheralController> all;
            lock (_lock)
            {
                all = new List<StandalonePeripheralController>(_order);
                _order.Clear();
                _byIdentity.Clear();
            }
            foreach (var c in all)
            {
                try { c.Dispose(); } catch (Exception ex) { MozaLog.Debug($"[AZOM] Standalone dispose: {ex.Message}"); }
            }
        }
    }
}
