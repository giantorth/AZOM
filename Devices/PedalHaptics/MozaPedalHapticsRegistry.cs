using System;
using System.Collections.Generic;
using System.Linq;
using MozaPlugin.Diagnostics;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices.PedalHaptics
{
    /// <summary>
    /// Owns every S12 pedal vibration unit — USB units found by PID, and routed
    /// lanes registered against a wheelbase/hub pipe.
    ///
    /// Modelled on <see cref="MBooster.MozaMBoosterRegistry"/>: enumerate, diff
    /// against what is already held, connect newcomers outside the lock, drop
    /// lanes whose port vanished. USB discovery is a plain PID filter
    /// (<see cref="MozaUsbIds.IsPedalHapticsPid"/>) — no scanning, no probing of
    /// ports belonging to anything else.
    ///
    /// The routed case cannot be discovered that way, because the unit has no
    /// port of its own there. Instead a lane is registered speculatively on each
    /// connected pipe and its own presence query decides: nothing reaches the
    /// wire until the unit answers, since the controller gates effect frames on
    /// <see cref="PedalHapticsDeviceController.Detected"/>.
    /// </summary>
    internal sealed class MozaPedalHapticsRegistry : IDisposable
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, PedalHapticsDeviceController> _devices =
            new Dictionary<string, PedalHapticsDeviceController>(StringComparer.OrdinalIgnoreCase);

        private readonly Func<bool> _isShuttingDown;

        // Copy-on-write snapshot so the ShakeIt post path never takes _lock.
        private PedalHapticsDeviceController[] _snapshot = Array.Empty<PedalHapticsDeviceController>();

        private bool _disposed;

        /// <summary>Raised the first time any unit answers its presence query.</summary>
        public event Action<PedalHapticsDeviceController>? DeviceDetected;

        internal MozaPedalHapticsRegistry(Func<bool>? isShuttingDown)
        {
            _isShuttingDown = isShuttingDown ?? (() => false);
        }

        /// <summary>True once at least one unit has answered — gates device-definition deployment.</summary>
        public bool AnyDetected
        {
            get
            {
                var snap = System.Threading.Volatile.Read(ref _snapshot);
                for (int i = 0; i < snap.Length; i++)
                    if (snap[i].Detected) return true;
                return false;
            }
        }

        /// <summary>Lock-free gate for the hot ShakeIt path.</summary>
        public bool HasControllers => System.Threading.Volatile.Read(ref _snapshot).Length > 0;

        public IReadOnlyList<PedalHapticsDeviceController> Devices
            => System.Threading.Volatile.Read(ref _snapshot);

        /// <summary>
        /// Register a lane against a wheelbase/hub pipe. The caller owns the
        /// connection; this lane only borrows it. Idempotent per identity.
        /// </summary>
        public void AddRoutedLane(string identity, MozaSerialConnection sharedConnection, string portLabel)
        {
            if (_disposed || sharedConnection == null) return;

            PedalHapticsDeviceController controller;
            PedalHapticsDeviceController? stale = null;
            lock (_lock)
            {
                if (_devices.TryGetValue(identity, out var existing))
                {
                    // Same pipe, nothing to do. A DIFFERENT connection object under
                    // the same identity means the owner reconnected and swapped its
                    // pipe out; the old lane is bound to a dead one, so replace it
                    // rather than silently keeping a lane that can never send again.
                    if (existing.SharesConnection(sharedConnection)) return;
                    stale = existing;
                    _devices.Remove(identity);
                }

                controller = new PedalHapticsDeviceController(
                    identity, sharedConnection, portLabel, _isShuttingDown);
                // Subscribe before the lane is reachable: the controller hooks the
                // shared pipe in its constructor, so a reply could in principle
                // land before a subscription made after the lock.
                controller.DetectedRisingEdge += OnControllerDetected;
                _devices[identity] = controller;
                RebuildSnapshotLocked();
            }

            if (stale != null)
            {
                stale.DetectedRisingEdge -= OnControllerDetected;
                try { stale.Dispose(); } catch { /* pipe already gone */ }
                MozaLog.Debug($"[AZOM] Pedal-haptics routed lane on {portLabel} rebound to a new pipe");
            }

            controller.StartWorker();
            controller.SendPresenceProbe();
            MozaLog.Debug($"[AZOM] Pedal-haptics routed lane registered on {portLabel}");
        }

        /// <summary>
        /// Poll tick: pick up newly-plugged USB units, retire ones whose port
        /// vanished, and re-probe any lane that has not answered yet. Called from
        /// the plugin's 5 s timer.
        /// </summary>
        public void Refresh()
        {
            if (_disposed) return;

            SyncUsbLanes();
            NudgeUndetectedLanes();
        }

        private void SyncUsbLanes()
        {
            IReadOnlyList<MozaPortDiscovery.PortInfo> ports;
            try { ports = MozaPortDiscovery.Instance.EnumerateMatching(MozaUsbIds.IsPedalHapticsPid); }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Pedal-haptics port enumeration failed: {ex.Message}");
                return;
            }

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newcomers = new List<PedalHapticsDeviceController>();
            List<PedalHapticsDeviceController>? gone = null;

            lock (_lock)
            {
                for (int i = 0; i < ports.Count; i++)
                {
                    var p = ports[i];
                    if (string.IsNullOrEmpty(p.PortName)) continue;

                    string identity = !string.IsNullOrEmpty(p.InstanceId)
                        ? p.InstanceId
                        : "port:" + p.PortName;
                    present.Add(identity);

                    if (_devices.ContainsKey(identity)) continue;
                    if (MozaSerialConnection.IsPortHeld(p.PortName)) continue;

                    var c = new PedalHapticsDeviceController(identity, p.PortName, _isShuttingDown);
                    c.DetectedRisingEdge += OnControllerDetected;
                    _devices[identity] = c;
                    newcomers.Add(c);
                }

                // Retire USB lanes whose device is no longer enumerated. Routed
                // lanes have no port of their own and are never dropped here —
                // their pipe's owner handles that.
                foreach (var key in _devices.Keys.ToList())
                {
                    var c = _devices[key];
                    if (c.IsRouted || present.Contains(key)) continue;
                    _devices.Remove(key);
                    (gone ??= new List<PedalHapticsDeviceController>()).Add(c);
                }

                if (newcomers.Count > 0 || gone != null) RebuildSnapshotLocked();
            }

            // Connecting opens a port; never do that under the lock.
            foreach (var c in newcomers)
            {
                if (c.TryConnect()) continue;
                MozaLog.Debug($"[AZOM] Pedal-haptics unit on {c.PortName} did not open — retrying next tick");
            }

            if (gone == null) return;
            foreach (var c in gone)
            {
                c.DetectedRisingEdge -= OnControllerDetected;
                MozaLog.Info($"[AZOM] Pedal-haptics unit on {c.PortName} disconnected");
                try { c.Dispose(); } catch { /* port already gone */ }
            }
        }

        /// <summary>
        /// Re-send the presence query on every lane that has never answered.
        /// Uncapped on purpose: routed lanes are registered speculatively on each
        /// pipe, so this is the only thing that notices a unit plugged in after
        /// SimHub started. One small frame every 5 s.
        ///
        /// A USB lane whose port exists but whose connection dropped is also
        /// reopened here, so a wedged port recovers without a replug.
        /// </summary>
        private void NudgeUndetectedLanes()
        {
            var snap = System.Threading.Volatile.Read(ref _snapshot);
            for (int i = 0; i < snap.Length; i++)
            {
                var c = snap[i];
                if (c.Detected) continue;

                if (!c.IsRouted && !c.IsConnected)
                {
                    try { c.TryConnect(); }
                    catch (Exception ex) { MozaLog.Debug($"[AZOM] Pedal-haptics reconnect: {ex.Message}"); }
                    continue;
                }

                if (!c.IsConnected) continue;
                try { c.SendPresenceProbe(); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM] Pedal-haptics probe: {ex.Message}"); }
            }
        }

        private void OnControllerDetected(PedalHapticsDeviceController controller)
        {
            try { DeviceDetected?.Invoke(controller); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] Pedal-haptics detect subscriber failed: {ex.Message}"); }
        }

        /// <summary>
        /// Fan one ShakeIt channel out to every unit. With more than one attached
        /// they mirror each other — the protocol gives no way to address units
        /// independently, and SimHub sees a single three-motor device.
        /// </summary>
        public void PostChannel(int channel, double gain01, double freqHz)
        {
            var snap = System.Threading.Volatile.Read(ref _snapshot);
            for (int i = 0; i < snap.Length; i++)
                snap[i].PostChannel(channel, gain01, freqHz);
        }

        public void ClearChannels()
        {
            var snap = System.Threading.Volatile.Read(ref _snapshot);
            for (int i = 0; i < snap.Length; i++)
                snap[i].ClearChannels();
        }

        private void RebuildSnapshotLocked()
        {
            var next = new PedalHapticsDeviceController[_devices.Count];
            _devices.Values.CopyTo(next, 0);
            System.Threading.Volatile.Write(ref _snapshot, next);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            PedalHapticsDeviceController[] all;
            lock (_lock)
            {
                all = _devices.Values.ToArray();
                _devices.Clear();
                System.Threading.Volatile.Write(ref _snapshot, Array.Empty<PedalHapticsDeviceController>());
            }

            foreach (var c in all)
            {
                c.DetectedRisingEdge -= OnControllerDetected;
                try { c.Dispose(); } catch { /* shutting down */ }
            }
        }
    }
}
