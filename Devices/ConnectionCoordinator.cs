using System;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Multi-connection management for <see cref="MozaPlugin"/>: primary
    /// wheelbase connect + per-device dedicated lanes (AB9 / standalone CM2 /
    /// Universal Hub / base-aux), the base↔hub primary migration state machine
    /// (broken base, wheel on hub), and the hub/base pipe polling + inbound
    /// message scoping. Injected with the per-Init connection/manager/prober
    /// instances (DeviceProber-style); settings are read live via
    /// <c>_plugin.Settings</c>.
    /// </summary>
    internal sealed class ConnectionCoordinator
    {
        private readonly MozaPlugin _plugin;
        private readonly MozaData _data;
        private readonly DeviceDetectionState _detectionState;
        private readonly MozaSerialConnection _connection;
        private readonly MozaDeviceManager _deviceManager;
        private readonly MozaAb9DeviceManager _ab9Manager;
        private readonly MozaDashboardDeviceManager _dashboardManager;
        private readonly MozaHubDeviceManager _hubManager;
        private readonly MozaBaseDeviceManager _baseManager;
        private readonly DeviceProber _hubDeviceProber;
        private readonly DeviceProber _baseDeviceProber;

        internal ConnectionCoordinator(
            MozaPlugin plugin,
            MozaData data,
            DeviceDetectionState detectionState,
            MozaSerialConnection connection,
            MozaDeviceManager deviceManager,
            MozaAb9DeviceManager ab9Manager,
            MozaDashboardDeviceManager dashboardManager,
            MozaHubDeviceManager hubManager,
            MozaBaseDeviceManager baseManager,
            DeviceProber hubDeviceProber,
            DeviceProber baseDeviceProber)
        {
            _plugin = plugin;
            _data = data;
            _detectionState = detectionState;
            _connection = connection;
            _deviceManager = deviceManager;
            _ab9Manager = ab9Manager;
            _dashboardManager = dashboardManager;
            _hubManager = hubManager;
            _baseManager = baseManager;
            _hubDeviceProber = hubDeviceProber;
            _baseDeviceProber = baseDeviceProber;
        }

        // Guard so only one TryConnect runs at a time (reconnect timer vs.
        // migration re-entry). CAS-latched; always released in finally.
        private int _connectingFlag;

        // ===== base→hub primary migration state (broken base, wheel on hub) =====
        // Stamped (ticks) the first reconnect tick the primary is base-bound with
        // no wheel detected; the migration waits a grace window past this. 0 = unset.
        private long _baseBoundNoWheelUtcTicks;
        // Stamped (ticks) when a wheel-MCU probe is answered on the HUB pipe —
        // positive evidence the wheel is reachable via the hub. 0 = none seen.
        private long _hubWheelSeenUtcTicks;
        // The wheelbase port the primary migrated AWAY from because it had no
        // wheel. MigratePrimaryToWheelbaseIfNeeded must skip it so it can't rip the
        // primary straight back to the dead base. Null = no migration in effect.
        private volatile string? _wheellessBasePort;
        // Grace window: base must be wheel-less for this long (and a wheel must
        // have answered on the hub) before migrating the primary to the hub. Long
        // enough that a slow-booting wheel on a HEALTHY base detects first.
        private const int BaseWheelGraceMs = 15000;

        /// <summary>The wheel-less-base latch — non-null only after a deliberate
        /// base→hub migration. Gates the reconnect timer's TryConnectBase.</summary>
        internal string? WheellessBasePort => _wheellessBasePort;

        /// <summary>
        /// True when the singular primary connection is itself bound to a Universal
        /// Hub — the hub-ONLY case (no wheelbase present). Here the primary, not the
        /// dedicated hub connection, must perform hub detection: the dedicated hub
        /// connection never opens because the primary already holds the only hub
        /// port (the <c>_activePorts</c> guard blocks a second open). Without this,
        /// nothing reads <c>hub-port1-power</c>, so <c>IsHubConnected</c> never flips,
        /// <c>Data.IsConnected</c> stays false, and the SimHub LED pipeline's
        /// connected-gate (<see cref="Devices.MozaLedDeviceManager.Display"/>)
        /// suppresses every frame — the wheel sits on its static EEPROM colours.
        ///
        /// Registry-based setups expose the hub PID on <c>DiscoveredPid</c>;
        /// Wine/probe setups have a null PID but set <c>HubProbeSucceeded</c> when
        /// the bound device answered the hub probe (the bases-first ordering means a
        /// hub-probe match implies no wheelbase responded). A wheelbase-bound primary
        /// (base-only or base+hub) trips neither branch, so it never sends hub reads
        /// down the base pipe.
        /// </summary>
        internal bool PrimaryBoundToHub =>
            _connection != null
            && _connection.IsConnected
            && (MozaUsbIds.IsHubPid(_connection.DiscoveredPid)
                || (_connection.DiscoveredPid == null && _connection.HubProbeSucceeded));

        internal void TryConnect()
        {
            if (Interlocked.CompareExchange(ref _connectingFlag, 1, 0) != 0)
                return;

            try
            {
                // If we had a wheel detected before reconnecting, reset it.
                // The serial port may have dropped during a wheel swap.
                if (_detectionState.NewWheelDetected || _detectionState.OldWheelDetected)
                    _plugin.ResetWheelDetection("Serial reconnecting — resetting wheel detection");

                if (_connection.Connect())
                {
                    _plugin._unmatched = 0;
                    // Drop any firmware-debug chatter captured from a prior
                    // (possibly different) wheel — the diagnostics tab should
                    // only show what THIS connection has produced.
                    _plugin.FirmwareDebugLogForDiagnostics.Clear();
                    MozaLog.Info("[AZOM] Connected to MOZA device");
                    MarkStandaloneDashboardDetectedFromUsb("serial connect");
                    // Base temps/state are dev-0x13 reads the base main controller
                    // answers — pointless (and retransmit-forever noise) on a
                    // hub-bound primary (hub-only, or post base→hub migration where
                    // the dedicated base-aux pipe owns these). Base-bound primary
                    // still polls them to drive base detection.
                    if (!PrimaryBoundToHub)
                        _deviceManager.ReadSettings(MozaPlugin.StatusPollCommands);
                    _deviceManager.ProbeWheelDetection();
                    _deviceManager.ReadSetting("dash-rpm-indicator-mode");
                    _deviceManager.ReadSetting("handbrake-direction");
                    _deviceManager.ReadSetting("pedals-throttle-dir");
                    // Hub detection normally belongs to the dedicated hub connection
                    // (its own port). The exception is the hub-ONLY case: the primary
                    // is itself bound to the hub, the dedicated hub connection can't
                    // open (port already held), so the primary must read
                    // hub-port1-power here — its 0xE4 reply flips HubDetected /
                    // IsHubConnected (DeviceProber.hub-port1-power case), which is what
                    // turns Data.IsConnected true and lets the SimHub LED pipeline
                    // forward frames. A wheelbase-bound primary skips this.
                    if (PrimaryBoundToHub)
                        _deviceManager.ReadSetting("hub-port1-power");

                    // Persist successful port for next launch
                    var port = _connection.LastPortName;
                    if (!string.IsNullOrEmpty(port) && _plugin.Settings.LastWheelbasePort != port)
                    {
                        _plugin.Settings.LastWheelbasePort = port!;
                        _plugin.ScheduleSave();
                    }
                }
                else if (!string.IsNullOrEmpty(_plugin.Settings.LastWheelbasePort)
                         && string.IsNullOrEmpty(_connection.LastPortName))
                {
                    // Connect() cleared the cached port (stale / wrong
                    // device after USB port change). Wipe the persisted
                    // setting so we don't repeat the stale-port check on
                    // every reconnect tick.
                    MozaLog.Info(
                        $"[AZOM] Cleared stale saved port {_plugin.Settings.LastWheelbasePort}");
                    _plugin.Settings.LastWheelbasePort = "";
                    _plugin.ScheduleSave();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _connectingFlag, 0);
            }
        }

        /// <summary>
        /// Flip dashboard detection on USB-PID alone when the open port is a
        /// Moza dashboard PID (CM2 = 0x0025). Idempotent. On rising edge
        /// deploys the CM2 device.json, reads CM2 settings, applies the
        /// current dash profile, and asks the binding coordinator to apply
        /// telemetry settings + start the sender. Each phase is wrapped in
        /// try/catch so a single failed phase doesn't abort the rest of the
        /// detection sequence. Called from <see cref="MozaPlugin.Init"/> (covers
        /// persistent-connection reuse and reload-without-restart) and from
        /// <see cref="TryConnect"/> (covers normal first-connect).
        /// </summary>
        internal bool MarkStandaloneDashboardDetectedFromUsb(string reason)
        {
            if (!_plugin.DashboardUsbConnected)
                return false;

            bool rising = !_detectionState.DashDetected;
            _detectionState.DashDetected = true;
            _data.IsDashboardConnected = true;

            string? dashPid = _dashboardManager.Connection.DiscoveredPid;
            if (DeviceDefinitionDeployer.DeployDashboard(dashPid))
                _plugin.DeviceDefinitionDeployed = true;

            if (rising)
            {
                MozaLog.Info(
                    $"[AZOM] Standalone dashboard detected from USB PID " +
                    $"{dashPid} ({MozaUsbIds.Describe(dashPid)}; {reason})");
                // Skip the legacy SHDP group-0x33 dash reads — a CM2 is driven by
                // the 0x43 telemetry path, so those reads are pointless bleedthrough.
            }

            try { _plugin.ApplyDashToHardware(_plugin.Settings?.ProfileStore?.CurrentProfile); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] Standalone dashboard profile apply skipped: {ex.Message}"); }

            try
            {
                _plugin.ApplyTelemetrySettings();
                _plugin.StartTelemetryIfReady();
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Standalone dashboard telemetry start skipped: {ex.Message}");
            }

            return true;
        }

        /// <summary>Open the AB9 shifter's dedicated CDC port (PID 0x1000) and probe identity.</summary>
        internal void TryConnectAb9()
        {
            if (_ab9Manager == null) return;
            if (_detectionState.Ab9Detected)
            {
                // Connection dropped after a successful detection — clear so the
                // next read response can re-trigger profile push.
                _detectionState.Ab9Detected = false;
            }
            if (_ab9Manager.TryConnect())
            {
                _ab9Manager.SendIdentityProbe();
                _ab9Manager.RequestAllStoredSettings();

                // Persist successful port for next launch
                var port = _ab9Manager.Connection.LastPortName;
                if (!string.IsNullOrEmpty(port) && _plugin.Settings.LastAb9Port != port)
                {
                    _plugin.Settings.LastAb9Port = port!;
                    _plugin.ScheduleSave();
                }
            }
            else if (!string.IsNullOrEmpty(_plugin.Settings.LastAb9Port)
                     && string.IsNullOrEmpty(_ab9Manager.Connection.LastPortName))
            {
                MozaLog.Info(
                    $"[AZOM/AB9] Cleared stale saved port {_plugin.Settings.LastAb9Port}");
                _plugin.Settings.LastAb9Port = "";
                _plugin.ScheduleSave();
            }
        }

        /// <summary>Open the standalone CM2's dedicated port (PID 0x0025) and, on the
        /// rising edge, run the standalone-dashboard detection (deploy + reads +
        /// retarget the sender to this connection).</summary>
        internal void TryConnectDashboard()
        {
            if (_dashboardManager == null) return;
            if (_dashboardManager.TryConnect())
            {
                MarkStandaloneDashboardDetectedFromUsb("dashboard USB connect");
                var port = _dashboardManager.Connection.LastPortName;
                if (!string.IsNullOrEmpty(port) && _plugin.Settings.LastDashboardPort != port)
                {
                    _plugin.Settings.LastDashboardPort = port!;
                    _plugin.ScheduleSave();
                }
            }
            else if (!string.IsNullOrEmpty(_plugin.Settings.LastDashboardPort)
                     && string.IsNullOrEmpty(_dashboardManager.Connection.LastPortName))
            {
                MozaLog.Info($"[AZOM] Cleared stale saved dashboard port {_plugin.Settings.LastDashboardPort}");
                _plugin.Settings.LastDashboardPort = "";
                _plugin.ScheduleSave();
            }
        }

        /// <summary>
        /// Open the Universal Hub's COM port (dedicated connection used when a
        /// wheelbase is also present). On success, persist the port and kick off
        /// peripheral enumeration immediately rather than waiting for the next
        /// poll tick. Clears a stale saved port on definitive open-failure.
        /// </summary>
        internal void TryConnectHub()
        {
            if (_hubManager == null) return;
            if (_hubManager.TryConnect())
            {
                var port = _hubManager.Connection.LastPortName;
                if (!string.IsNullOrEmpty(port) && _plugin.Settings.LastHubPort != port)
                {
                    _plugin.Settings.LastHubPort = port!;
                    _plugin.ScheduleSave();
                }
                PollHubPeripherals();
            }
            else if (!string.IsNullOrEmpty(_plugin.Settings.LastHubPort)
                     && string.IsNullOrEmpty(_hubManager.Connection.LastPortName))
            {
                MozaLog.Info($"[AZOM] Cleared stale saved hub port {_plugin.Settings.LastHubPort}");
                _plugin.Settings.LastHubPort = "";
                _plugin.ScheduleSave();
            }
        }

        /// <summary>
        /// Open the wheelbase's COM port on the dedicated base-aux pipe (used only
        /// after a base→hub primary migration). On success persist the port and
        /// kick the initial base poll so base-mcu-temp detection fires immediately
        /// rather than waiting for the next PollStatus tick. Mirror of
        /// <see cref="TryConnectHub"/>.
        /// </summary>
        internal void TryConnectBase()
        {
            if (_baseManager == null) return;
            if (_baseManager.TryConnect())
            {
                var port = _baseManager.Connection.LastPortName;
                if (!string.IsNullOrEmpty(port) && _plugin.Settings.LastBaseAuxPort != port)
                {
                    _plugin.Settings.LastBaseAuxPort = port!;
                    _plugin.ScheduleSave();
                }
                PollBaseAux();
            }
            else if (!string.IsNullOrEmpty(_plugin.Settings.LastBaseAuxPort)
                     && string.IsNullOrEmpty(_baseManager.Connection.LastPortName))
            {
                MozaLog.Info($"[AZOM] Cleared stale saved base-aux port {_plugin.Settings.LastBaseAuxPort}");
                _plugin.Settings.LastBaseAuxPort = "";
                _plugin.ScheduleSave();
            }
        }

        /// <summary>Clear all base→hub migration state — the grace-window stamp,
        /// the positive hub-wheel-evidence stamp, and the wheel-less-base latch.
        /// Called on events that invalidate the "this base has no wheel" judgement:
        /// USB re-enumeration / base or hub unplug / connection toggle / a wheel
        /// detected on the base.</summary>
        internal void ResetHubWheelMigrationState()
        {
            Interlocked.Exchange(ref _baseBoundNoWheelUtcTicks, 0);
            Interlocked.Exchange(ref _hubWheelSeenUtcTicks, 0);
            _wheellessBasePort = null;
        }

        /// <summary>
        /// Self-heal the broken-base case: a wheelbase enumerates and answers base
        /// probes, but no wheel is reachable on it — the wheel is on the Universal
        /// Hub instead. The mirror of <see cref="MigratePrimaryToWheelbaseIfNeeded"/>.
        ///
        /// When the primary is bound to a base, NO wheel has been detected after a
        /// grace window, AND a wheel actually answered a probe on the hub pipe
        /// (positive evidence — <see cref="_hubWheelSeenUtcTicks"/>), release the
        /// hub from the dedicated hub manager and migrate the primary onto it so it
        /// runs the full wheel/session/telemetry pipeline there (the proven
        /// hub-only path). The freed base port is reclaimed by the dedicated
        /// base-aux pipe (<see cref="TryConnectBase"/>) for base telemetry. The
        /// <see cref="_wheellessBasePort"/> latch stops MigratePrimaryToWheelbase
        /// from immediately pulling the primary back.
        ///
        /// Registry-category gated, same as the wheelbase migration. Healthy
        /// base+hub never reaches the action: the base detects its wheel first, so
        /// the no-wheel precondition fails.
        /// </summary>
        internal void MigratePrimaryToHubIfNeeded()
        {
            if (MozaPlugin.IsShuttingDown) return;
            if (_connection == null || !_connection.IsConnected) return;

            // Primary must be on a wheelbase (the broken base). If it's already on
            // the hub there's nothing to migrate.
            if (MozaUsbIds.Categorize(_connection.DiscoveredPid) != MozaDeviceCategory.Wheelbase)
                return;

            // A wheel on the base → this is a healthy base; never migrate. Also
            // clear any stale grace stamp so a later wheel-drop doesn't look like
            // an instantly-elapsed window.
            if (_detectionState.NewWheelDetected || _detectionState.OldWheelDetected)
            {
                ResetHubWheelMigrationState();
                return;
            }

            // Registry must categorize ports (real-HW Windows). Empty = Wine/Proton.
            var ports = MozaPortDiscovery.Instance.Enumerate();
            if (ports.Count == 0) return;

            // Arm/measure the grace window: the base must sit wheel-less for
            // BaseWheelGraceMs before we consider it broken. Stamp on first sight.
            long now = DateTime.UtcNow.Ticks;
            Interlocked.CompareExchange(ref _baseBoundNoWheelUtcTicks, now, 0);
            long stamp = Interlocked.Read(ref _baseBoundNoWheelUtcTicks);
            if ((now - stamp) / TimeSpan.TicksPerMillisecond < BaseWheelGraceMs) return;

            // Positive evidence: a wheel must have answered on the hub pipe.
            if (Interlocked.Read(ref _hubWheelSeenUtcTicks) == 0) return;

            // Find a free, registry-classified hub port. It is normally HELD by the
            // dedicated hub manager — free it first (mirrors how the wheelbase
            // migration frees the hub at the symmetric site).
            string? hubPort = null;
            foreach (var p in ports)
            {
                if (p.Category != MozaDeviceCategory.Hub) continue;
                hubPort = p.PortName;
                break;
            }
            if (hubPort == null) return;

            string basePort = _connection.LastPortName ?? "";

            MozaLog.Info(
                $"[AZOM] Base on {basePort} (PID={_connection.DiscoveredPid}) has no wheel but " +
                $"a wheel answered on the hub at {hubPort} — migrating primary to the hub " +
                "(base telemetry continues on the dedicated base-aux pipe)");

            // Release the dedicated hub manager so the primary can claim the hub
            // port. Manual Disconnect does NOT raise Disconnected, so clear its
            // tracked reads (including the wheel-evidence probes) explicitly.
            try { _hubManager?.Disconnect(); } catch { }
            try { _hubManager?.PendingResponses?.Clear(); } catch { }

            // Latch the wheel-less base BEFORE rebinding so MigratePrimaryToWheelbase
            // (which runs later this tick) skips it, and so the reconnect tick's
            // TryConnectBase gate (_wheellessBasePort != null) arms. Seed the
            // base-aux pipe at the exact port we're leaving so it reclaims it
            // deterministically.
            _wheellessBasePort = string.IsNullOrEmpty(basePort) ? null : basePort;
            if (!string.IsNullOrEmpty(basePort))
                _baseManager.Connection.LastPortName = basePort;

            // Point the primary directly at the hub port and rebind. The primary's
            // PID filter accepts the hub PID (same as the hub-only case), so the
            // cached-port Connect path opens it; FindMozaPort isn't consulted.
            // TryConnect persists the new (hub) port as LastWheelbasePort — fine,
            // it self-heals via MigratePrimaryToWheelbase if the base is fixed.
            _connection.Disconnect();
            _connection.LastPortName = hubPort;

            // The base is no longer the primary's device; its detection + ownership
            // must re-land on the base-aux pipe. Clear base flags so the base-aux
            // prober re-detects (mirrors OnBaseDisconnected's intent without losing
            // the physical base — it's still plugged in).
            _detectionState.BaseDetected = false;
            _detectionState.BaseAmbientLedSupported = false;
            _detectionState.BaseAmbientProbed = false;
            _detectionState.BaseOwner = null;
            _data.BaseSettingsRead = false;
            try { _plugin.PendingResponses.Clear(); } catch { }

            // Rebind the primary to the hub; TryConnect probes the wheel there and
            // (PrimaryBoundToHub) reads hub-port1-power so IsHubConnected flips. The
            // freed base port is claimed by TryConnectBase later in this same tick.
            TryConnect();
        }

        /// <summary>
        /// Self-heal a mis-latched primary connection. The primary (BaseAndHub)
        /// picks its port ONCE via FindMozaPort's wheel-location rule; if a
        /// Universal Hub enumerated before the wheelbase ("wrong latch order"),
        /// the primary bound the hub and — being IsConnected — never re-evaluates
        /// (the reconnect tick only calls TryConnect while disconnected). The
        /// wheel/session/telemetry pipeline must run on the BASE, so a primary
        /// stuck on the hub leaves telemetry dead and the base port unclaimed.
        ///
        /// When a wheelbase is the intended primary, detect a primary bound to a
        /// NON-wheelbase port while a free wheelbase port now exists, release the
        /// hub (so the dedicated hub manager can claim it on the next
        /// TryConnectHub), and rebind the primary to the base. Order-independent:
        /// also covers hot-plugging a base into a hub-only setup.
        ///
        /// Registry-category gated — Wine/empty-registry setups can't tell hub
        /// from base and rely on the probe path's bases-first ordering, so they
        /// never trip this.
        /// </summary>
        internal void MigratePrimaryToWheelbaseIfNeeded()
        {
            if (MozaPlugin.IsShuttingDown) return;
            if (_connection == null || !_connection.IsConnected) return;

            // Only meaningful when the registry can categorize ports (real-HW
            // Windows). Empty registry = Wine/Proton → leave the probe path alone.
            var ports = MozaPortDiscovery.Instance.Enumerate();
            if (ports.Count == 0) return;

            // Already on a wheelbase → correctly bound, nothing to do.
            if (MozaUsbIds.Categorize(_connection.DiscoveredPid) == MozaDeviceCategory.Wheelbase)
                return;

            // Find a wheelbase port that is present and not held by any sibling
            // connection (the hub-only case has none → primary correctly stays
            // on the hub and we return).
            string? wheelbasePort = null;
            foreach (var p in ports)
            {
                if (p.Category != MozaDeviceCategory.Wheelbase) continue;
                // Don't pull the primary back to a base we deliberately migrated
                // AWAY from because it had no wheel (the wheel is on the hub). That
                // base is owned by the dedicated base-aux pipe; ripping the primary
                // onto it would kill the hub-driven wheel pipeline and the two
                // migrations would fight every tick.
                if (string.Equals(p.PortName, _wheellessBasePort,
                                  StringComparison.OrdinalIgnoreCase))
                    continue;
                // Defensive: if the primary somehow already holds this wheelbase
                // port the category check above would have returned; treat a
                // self-match as "nothing to migrate".
                if (string.Equals(p.PortName, _connection.LastPortName,
                                  StringComparison.OrdinalIgnoreCase))
                    return;
                if (MozaSerialConnection.IsPortHeld(p.PortName)) continue;
                wheelbasePort = p.PortName;
                break;
            }
            if (wheelbasePort == null) return;

            MozaLog.Info(
                $"[AZOM] Primary bound to non-wheelbase port {_connection.LastPortName} " +
                $"(PID={_connection.DiscoveredPid}) but a wheelbase is available on {wheelbasePort} — " +
                "migrating primary to the wheelbase");

            // Release the hub: Disconnect() frees the port from the in-process
            // active-port set so TryConnectHub can claim it. (Manual Disconnect
            // does NOT raise the Disconnected event, so no OnSerialDisconnected
            // side effects fire here.)
            _connection.Disconnect();

            // Clear the cached/persisted port so Connect()'s cached path — which
            // validates by PID only (the hub PID passes the primary's filter) —
            // can't immediately re-grab the hub. FindMozaPort's wheel-location
            // rule then selects the wheelbase.
            _connection.LastPortName = null;
            if (!string.IsNullOrEmpty(_plugin.Settings.LastWheelbasePort))
            {
                _plugin.Settings.LastWheelbasePort = "";
                _plugin.ScheduleSave();
            }

            // Peripherals detected while the primary was wrongly on the hub got
            // their owner pinned to the primary device-manager; reset detection +
            // ownership (mirrors OnHubDisconnected) so they re-enumerate on the
            // correct pipe — pedals/handbrake via the hub manager, base settings
            // via the rebound primary.
            _detectionState.PedalsDetected = false;
            _detectionState.PedalsOwner = null;
            _detectionState.HandbrakeDetected = false;
            _detectionState.HandbrakeOwner = null;
            _detectionState.HubDetected = false;

            // Rebind the primary; TryConnect re-probes the wheel and persists the
            // new (base) port. The freed hub port is claimed by TryConnectHub
            // later in this same reconnect tick.
            TryConnect();
        }

        /// <summary>
        /// Probe the hub pipe for its attached peripherals + port-power status.
        /// Pedals/handbrake presence probes fire on BOTH the base and hub pipes
        /// until detected (shared flags); first responder wins and records the
        /// owning pipe (DeviceProber.Mark*Detected). No-op unless the hub is up.
        /// </summary>
        internal void PollHubPeripherals()
        {
            if (_hubManager == null || !_hubManager.IsConnected) return;
            var dm = _hubManager.DeviceManager;
            if (!_detectionState.PedalsDetected)
                dm.SendPresenceProbe(MozaProtocol.DevicePedals);
            if (!_detectionState.HandbrakeDetected)
                dm.SendPresenceProbe(MozaProtocol.DeviceHandbrake);
            // Positive-evidence probe for the broken-base case: while NO wheel has
            // been detected on the primary (base), also probe the wheel over the
            // hub pipe. A reply (recognized in OnHubMessageReceived) stamps
            // _hubWheelSeenUtcTicks, which arms the base→hub primary migration.
            // Healthy base+hub never reaches this: the base detects its wheel first
            // (NewWheelDetected flips), gating the probe off. Only fires while the
            // base IS the primary — once migrated the hub manager is disconnected.
            if (!_detectionState.NewWheelDetected && !_detectionState.OldWheelDetected
                && !PrimaryBoundToHub)
                dm.ProbeWheelDetection();
            // hub-port1-power is the hub-presence trigger (first 0xE4 reply sets
            // HubDetected). Once detected, read the full set so every Hub-tab
            // port-power indicator populates.
            if (!_detectionState.HubDetected)
                dm.ReadSetting("hub-port1-power");
            else
                dm.ReadSettings(DeviceProber.HubReadCommands);
        }

        /// <summary>Universal Hub unplugged — drop hub state and re-route any
        /// peripherals that were owned by the hub pipe so they re-detect on
        /// whichever pipe answers next.</summary>
        internal void OnHubDisconnected()
        {
            if (MozaPlugin.IsShuttingDown) return;
            _detectionState.HubDetected = false;
            _data.IsHubConnected = false;
            var hubDm = _hubManager?.DeviceManager;
            if (hubDm != null && ReferenceEquals(_detectionState.PedalsOwner, hubDm))
            {
                _detectionState.PedalsDetected = false;
                _detectionState.PedalsOwner = null;
            }
            if (hubDm != null && ReferenceEquals(_detectionState.HandbrakeOwner, hubDm))
            {
                _detectionState.HandbrakeDetected = false;
                _detectionState.HandbrakeOwner = null;
            }
            // The hub's tracked reads will never be answered now — drop them so
            // they don't retransmit against a reconnected (possibly different) hub.
            try { _hubManager?.PendingResponses?.Clear(); } catch { }
            // The hub carried our wheel-on-hub evidence; with it gone the base→hub
            // migration is moot. Clear so a re-plugged hub re-gathers evidence and
            // MigratePrimaryToWheelbase can reclaim the base for FFB meanwhile.
            ResetHubWheelMigrationState();
        }

        /// <summary>
        /// Inbound from the dedicated hub connection. Only peripheral (pedals /
        /// handbrake) and hub port-power frames are routed here — wheel / base /
        /// dash / session frames are dropped so the wheel/telemetry pipeline stays
        /// exclusively on the primary (base) connection. Detection is dispatched
        /// to the hub prober so ownership lands on the hub pipe.
        /// </summary>
        internal void OnHubMessageReceived(byte[] data)
        {
            if (MozaPlugin.IsShuttingDown) return;
            if (data == null || data.Length < 2) return;

            // Firmware debug noise.
            if (data[0] == MozaProtocol.FirmwareDebugGroup) return;

            // Empty presence-probe ACK: 7e 00 80 swap(dev) chk → data = {0x80, dev}.
            // Only pedals / handbrake are probed on the hub pipe.
            if (data.Length == 2 && data[0] == 0x80)
            {
                byte deviceId = MozaProtocol.SwapNibbles(data[1]);
                if (deviceId == MozaProtocol.DevicePedals)
                    _hubDeviceProber.MarkPedalsDetected();
                else if (deviceId == MozaProtocol.DeviceHandbrake)
                    _hubDeviceProber.MarkHandbrakeDetected();
                return;
            }

            var result = MozaResponseParser.Parse(data);
            if (!result.HasValue) return;
            var r = result.Value;
            if (r.Name == null) return;

            // Positive evidence that the wheel is reachable via the hub (broken
            // base): a wheel-MCU probe answered on the hub pipe. Stamp the
            // timestamp that arms MigratePrimaryToHubIfNeeded, then RETURN without
            // processing — the full wheel pipeline only runs after migration, when
            // the primary itself is bound to the hub.
            if (r.Name == "wheel-telemetry-mode" || r.Name == "wheel-rpm-value1")
            {
                Interlocked.CompareExchange(ref _hubWheelSeenUtcTicks, DateTime.UtcNow.Ticks, 0);
                // Ack the tracked probe so it stops retransmitting on the hub pipe.
                _hubManager.PendingResponses?.NoteResponse(r.Name);
                return;
            }

            // Scope to peripherals + hub status. Anything else (wheel/base/dash)
            // belongs to the primary pipe and is ignored here.
            if (!(r.Name.StartsWith("pedals-", StringComparison.Ordinal)
                  || r.Name.StartsWith("handbrake-", StringComparison.Ordinal)
                  || r.Name.StartsWith("hub-", StringComparison.Ordinal)))
                return;

            _hubManager.PendingResponses?.NoteResponse(r.Name);
            _data.UpdateFromCommand(r.Name, r.IntValue);
            if (r.ArrayValue != null)
                _data.UpdateFromArray(r.Name, r.ArrayValue);
            _hubDeviceProber.DetectDevices(r.Name, r.IntValue, r.DeviceId);
        }

        /// <summary>
        /// Poll base-only telemetry on the dedicated base-aux pipe (post base→hub
        /// migration). StatusPollCommands' base-mcu-temp doubles as the base
        /// detection trigger (DeviceProber base-mcu-temp case → BaseOwner = base-aux
        /// DM). No-op unless the base-aux pipe is up. Mirror of PollHubPeripherals.
        /// </summary>
        internal void PollBaseAux()
        {
            if (_baseManager == null || !_baseManager.IsConnected) return;
            _baseManager.DeviceManager.ReadSettings(MozaPlugin.StatusPollCommands);
        }

        /// <summary>Inbound from the dedicated base-aux connection. The INVERSE
        /// scoping of <see cref="OnHubMessageReceived"/>: only base/motor frames
        /// (base-* / main-*) are processed; wheel/dash/session/hub/peripheral
        /// frames belong to the hub-bound primary pipe and are dropped. Detection
        /// is dispatched to the base prober so BaseOwner lands on this pipe.</summary>
        internal void OnBaseMessageReceived(byte[] data)
        {
            if (MozaPlugin.IsShuttingDown) return;
            if (data == null || data.Length < 2) return;

            // Firmware debug noise.
            if (data[0] == MozaProtocol.FirmwareDebugGroup) return;

            var result = MozaResponseParser.Parse(data);
            if (!result.HasValue) return;
            var r = result.Value;
            if (r.Name == null) return;

            // Scope to base/motor. main-* are base FFB-gain / work-mode commands on
            // dev 0x12 (the wheelbase main controller); the CM2 standalone uses
            // cm2-* so there is no collision. Everything else is the primary's.
            if (!(r.Name.StartsWith("base-", StringComparison.Ordinal)
                  || r.Name.StartsWith("main-", StringComparison.Ordinal)))
                return;

            _baseManager.PendingResponses?.NoteResponse(r.Name);
            _data.UpdateFromCommand(r.Name, r.IntValue);
            if (r.ArrayValue != null)
                _data.UpdateFromArray(r.Name, r.ArrayValue);
            _baseDeviceProber.DetectDevices(r.Name, r.IntValue, r.DeviceId);
        }

        /// <summary>Base-aux pipe unplugged (broken base physically removed after a
        /// migration) — drop base state owned by this pipe. Mirror of
        /// OnHubDisconnected.</summary>
        internal void OnBaseDisconnected()
        {
            if (MozaPlugin.IsShuttingDown) return;
            _detectionState.BaseDetected = false;
            _detectionState.BaseAmbientLedSupported = false;
            _detectionState.BaseAmbientProbed = false;
            _data.IsBaseConnected = false;
            _data.BaseSettingsRead = false;
            var baseDm = _baseManager?.DeviceManager;
            if (baseDm != null && ReferenceEquals(_detectionState.BaseOwner, baseDm))
                _detectionState.BaseOwner = null;
            try { _baseManager?.PendingResponses?.Clear(); } catch { }
        }
    }
}
