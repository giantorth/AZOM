using System;
using MozaPlugin.Hardware;

namespace MozaPlugin.Sdk
{
    /// <summary>
    /// Owns the third-party SDK emulation surface: the CoAP server (port 40266),
    /// the <c>MOZA Pit House.exe</c> impersonation stub, and the PitHouse-compatible
    /// plain-UDP control server (port 40288). Extracted from MozaPlugin.
    ///
    /// <para>All transitions are serialized by <see cref="_gate"/>. The three
    /// teardown entry points are deliberately distinct because the callers have
    /// different persistent-stub policies — see each method.</para>
    /// </summary>
    internal sealed class SdkLifecycleCoordinator
    {
        private readonly MozaData _data;
        private readonly HardwareApplier _hardware;

        // Guards the server/stub fields and the persistent static during
        // transitions (Init, UI toggle, End can all race). Static so the
        // process-exit path shares it with the instance transitions.
        private static readonly object _gate = new object();

        private MozaSdkCoapServer? _server;
        private PitHouseUdp.MozaControlUdpServer? _udpServer;
        private CoapStubManager? _stub;

        // CoAP stub child-process manager. Persistent for the same reason the
        // wire is: stopping and restarting the stub on every plugin reload is
        // wasted work (the stub is a long-lived "PitHouse impersonator" child
        // process with no per-plugin-instance state) AND under Wine/Proton the
        // teardown path (Process.Kill + JobObject.Dispose) intermittently
        // hangs — observed 2026-05-25: End() on the SECOND game switch wedged
        // in CoapStubManager.Stop() between RestoreRegistryRedirect (logged)
        // and the "CoAP stub stopped" line (never logged). Leaving the stub
        // alive across reloads avoids the unsafe teardown path entirely.
        //
        // Disposed on full process exit (StopPersistentStub) AND on cold-start
        // re-entry when SdkEmulationEnabled was toggled off.
        private static CoapStubManager? s_persistentStub;

        internal SdkLifecycleCoordinator(MozaData data, HardwareApplier hardware)
        {
            _data = data;
            _hardware = hardware;
        }

        internal MozaSdkCoapServer? Server => _server;
        internal PitHouseUdp.MozaControlUdpServer? ControlUdpServer => _udpServer;
        internal CoapStubManager? StubManager => _stub;

        /// <summary>
        /// Start or stop the CoAP SDK emulation surface (port 40266 server +
        /// the <c>MOZA Pit House.exe</c> impersonation stub) at runtime. Called
        /// both from Init (to honour the persisted setting) and from the live UI
        /// toggle, so startup and a mid-session flip share one path. Serialized
        /// by the lifecycle gate; safe to call repeatedly (idempotent in each
        /// direction).
        ///
        /// <para>Disabling stops the stub via <c>TryStop</c> (bounded — never
        /// wedges the caller under Wine) which restores
        /// <c>HKCU\Software\MOZA\PitHouse\path</c> to the user's original value
        /// before the child is killed, and clears the persistent static so the
        /// redirect can't be re-applied after an explicit "off".</para>
        /// </summary>
        internal void SetEmulationEnabled(bool enabled)
        {
            lock (_gate)
            {
                if (enabled)
                {
                    try
                    {
                        // Stub manager is persistent across plugin reloads (the
                        // child holds no per-instance state and its Wine teardown
                        // is the path that intermittently hangs). Reuse a live
                        // one; otherwise reap a dead husk and spawn fresh.
                        if (_stub != null && _stub.IsRunning)
                        {
                            // Already running for this instance — nothing to do.
                        }
                        else if (s_persistentStub != null && s_persistentStub.IsRunning)
                        {
                            _stub = s_persistentStub;
                            MozaLog.Info(
                                "[Sdk] Reusing persistent CoAP stub " +
                                $"(status={_stub.Status})");
                        }
                        else
                        {
                            // Persistent reference exists but its child is gone
                            // (crashed / killed externally). Tear down the husk
                            // before allocating a fresh manager so its job/process
                            // handles don't leak. Bounded so a Wine-side
                            // JobObject.Dispose wedge can't block the caller.
                            if (s_persistentStub != null)
                            {
                                try { s_persistentStub.TryStop(1500); } catch { }
                                s_persistentStub = null;
                            }
                            _stub = new CoapStubManager();
                            _stub.Start();
                            s_persistentStub = _stub;
                        }

                        // SDK server holds refs to _data + _hardwareApplier (both
                        // per-instance), so it lives and dies with this instance —
                        // it cannot be persistent like the stub manager. Create
                        // only when not already up (idempotent re-enable).
                        if (_server == null)
                        {
                            _server = new MozaSdkCoapServer(_data, _hardware);
                            _server.Start();
                            MozaLog.Info("[Sdk] CoAP SDK server enabled");
                        }
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Error($"[Sdk] Failed to start CoAP SDK server: {ex.Message}");
                        try { _server?.Stop(); } catch { /* swallow */ }
                        // Don't Stop() the stub manager from this catch — it may
                        // be the persistent one and a Wine-side Stop() hang is
                        // exactly the failure we're avoiding. Leave it running;
                        // the next transition re-evaluates via IsRunning.
                        _server = null;
                        _stub = null;
                    }
                }
                else
                {
                    // Stop the CoAP server, then the stub. Stopping the stub
                    // restores the registry redirect (before the kill, so it
                    // survives a Wine-side hang). Clear the persistent static so
                    // nothing re-applies the redirect after an explicit "off".
                    try { _server?.Stop(); _server?.Dispose(); }
                    catch (Exception ex) { MozaLog.Warn($"[Sdk] server stop: {ex.Message}"); }
                    _server = null;

                    var stub = _stub ?? s_persistentStub;
                    if (stub != null)
                    {
                        try { stub.TryStop(1500); }
                        catch (Exception ex) { MozaLog.Warn($"[Sdk] stub stop: {ex.Message}"); }
                        MozaLog.Info("[Sdk] CoAP SDK emulation disabled — stub stopped, registry restored");
                    }
                    // Only drop the static if that is the stub we just stopped — a
                    // different live persistent child must not be orphaned.
                    if (ReferenceEquals(stub, s_persistentStub)) s_persistentStub = null;
                    _stub = null;
                }
            }
        }

        /// <summary>
        /// Start or stop the PitHouse-compatible plain-UDP control server
        /// (port 40288) at runtime. Parallel to <see cref="SetEmulationEnabled"/>;
        /// shares the same lifecycle gate and is driven from both Init and the
        /// live UI toggle.
        /// </summary>
        internal void SetUdpControlEnabled(bool enabled)
        {
            lock (_gate)
            {
                if (enabled)
                {
                    if (_udpServer != null) return; // already running
                    try
                    {
                        _udpServer = new PitHouseUdp.MozaControlUdpServer(_data, _hardware);
                        _udpServer.Start();
                        MozaLog.Info("[Sdk] UDP control server enabled");
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Error($"[Sdk] Failed to start UDP control server: {ex.Message}");
                        try { _udpServer?.Stop(); } catch { /* swallow */ }
                        _udpServer = null;
                    }
                }
                else
                {
                    try { _udpServer?.Stop(); _udpServer?.Dispose(); }
                    catch (Exception ex) { MozaLog.Warn($"[PitHouseUdp] server stop: {ex.Message}"); }
                    _udpServer = null;
                }
            }
        }

        /// <summary>Stop + dispose both servers, leaving the stub alone. Both
        /// teardown callers (CleanupPartialInit and End) do this first and
        /// identically: the CoAP receive thread holds references into MozaData
        /// and HardwareApplier, so it must stop before the wire stack disposes
        /// those out from under it.</summary>
        internal void StopServers()
        {
            lock (_gate)
            {
                try { _server?.Stop(); _server?.Dispose(); _server = null; }
                catch (Exception ex) { MozaLog.Warn($"[Sdk] server stop: {ex.Message}"); }
                try { _udpServer?.Stop(); _udpServer?.Dispose(); _udpServer = null; }
                catch (Exception ex) { MozaLog.Warn($"[PitHouseUdp] server stop: {ex.Message}"); }
            }
        }

        /// <summary>CleanupPartialInit's stub policy: if this Init reused the
        /// persistent stub, do NOT stop it — the next Init expects to inherit
        /// it. Only drop the local ref. Disposal gated on !ReferenceEquals
        /// matches the connection/sender pattern. Bounded TryStop so a Wine-side
        /// wedge can't block cleanup.</summary>
        internal void ReleaseStubAfterFailedInit()
        {
            lock (_gate)
            {
                bool ownStub = _stub != null && !ReferenceEquals(_stub, s_persistentStub);
                if (ownStub)
                {
                    try { _stub?.TryStop(1500); }
                    catch (Exception ex) { MozaLog.Warn($"[Sdk] stub stop: {ex.Message}"); }
                }
                _stub = null;
            }
        }

        /// <summary>End()'s stub policy. When <paramref name="keepWireAlive"/> we
        /// just drop the instance ref; the persistent static keeps the child
        /// alive for the next plugin instance to reuse via the IsRunning check
        /// in Init. When the wire is being torn down (true cold-start reset, not
        /// a game switch), stop the stub too and clear the static.</summary>
        internal void ReleaseStubOnEnd(bool keepWireAlive)
        {
            lock (_gate)
            {
                if (keepWireAlive)
                {
                    _stub = null;
                    return;
                }
                // Bounded so the End() flow (often runs on the SimHub UI thread)
                // can't get pinned by a Wine-side wedge in Process.Kill /
                // JobObject.Dispose.
                try { _stub?.TryStop(1500); }
                catch (Exception ex) { MozaLog.Warn($"[Sdk] stub stop: {ex.Message}"); }
                if (_stub != null && ReferenceEquals(_stub, s_persistentStub))
                    s_persistentStub = null;
                _stub = null;
            }
        }

        /// <summary>Process-exit path: stop whatever stub outlived the plugin
        /// instances. Bounded; failures are swallowed by the caller.</summary>
        internal static void StopPersistentStub()
        {
            lock (_gate) s_persistentStub?.TryStop(1500);
        }

        /// <summary>True when a persistent stub child is still alive — Init's
        /// cold-start re-entry check.</summary>
        internal static bool PersistentStubIsRunning =>
            s_persistentStub != null && s_persistentStub.IsRunning;
    }
}
