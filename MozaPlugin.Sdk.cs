using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using MozaPlugin.Devices;
using MozaPlugin.Devices.StalksTruckSim;
using MozaPlugin.Hardware;
using MozaPlugin.Protocol;
using MozaPlugin.Resources;
using MozaPlugin.Settings;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.TileServer;
using MozaPlugin.UI.UpdateCheck;
using Timer = System.Timers.Timer;

namespace MozaPlugin
{
    public partial class MozaPlugin
    {

        /// <summary>
        /// Start or stop the CoAP SDK emulation surface (port 40266 server +
        /// the <c>MOZA Pit House.exe</c> impersonation stub) at runtime. Called
        /// both from <see cref="Init"/> (to honour the persisted setting) and
        /// from the live UI toggle, so startup and a mid-session flip share one
        /// path. Serialized by <see cref="_sdkLifecycleGate"/>; safe to call
        /// repeatedly (idempotent in each direction).
        ///
        /// <para>Disabling stops the stub via <c>TryStop</c> (bounded — never
        /// wedges the caller under Wine) which restores
        /// <c>HKCU\Software\MOZA\PitHouse\path</c> to the user's original value
        /// before the child is killed, and clears the persistent static so the
        /// redirect can't be re-applied after an explicit "off".</para>
        /// </summary>
        internal void SetSdkEmulationEnabled(bool enabled)
        {
            lock (_sdkLifecycleGate)
            {
                if (enabled)
                {
                    try
                    {
                        // Stub manager is persistent across plugin reloads (the
                        // child holds no per-instance state and its Wine teardown
                        // is the path that intermittently hangs). Reuse a live
                        // one; otherwise reap a dead husk and spawn fresh.
                        if (_sdkStubManager != null && _sdkStubManager.IsRunning)
                        {
                            // Already running for this instance — nothing to do.
                        }
                        else if (s_persistentSdkStubManager != null
                                 && s_persistentSdkStubManager.IsRunning)
                        {
                            _sdkStubManager = s_persistentSdkStubManager;
                            MozaLog.Info(
                                "[Sdk] Reusing persistent CoAP stub " +
                                $"(status={_sdkStubManager.Status})");
                        }
                        else
                        {
                            // Persistent reference exists but its child is gone
                            // (crashed / killed externally). Tear down the husk
                            // before allocating a fresh manager so its job/process
                            // handles don't leak. Bounded so a Wine-side
                            // JobObject.Dispose wedge can't block the caller.
                            if (s_persistentSdkStubManager != null)
                            {
                                try { s_persistentSdkStubManager.TryStop(1500); } catch { }
                                s_persistentSdkStubManager = null;
                            }
                            _sdkStubManager = new Sdk.CoapStubManager();
                            _sdkStubManager.Start();
                            s_persistentSdkStubManager = _sdkStubManager;
                        }

                        // SDK server holds refs to _data + _hardwareApplier (both
                        // per-instance), so it lives and dies with this instance —
                        // it cannot be persistent like the stub manager. Create
                        // only when not already up (idempotent re-enable).
                        if (_sdkServer == null)
                        {
                            _sdkServer = new Sdk.MozaSdkCoapServer(_data, _hardwareApplier);
                            _sdkServer.Start();
                            MozaLog.Info("[Sdk] CoAP SDK server enabled");
                        }
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Error($"[Sdk] Failed to start CoAP SDK server: {ex.Message}");
                        try { _sdkServer?.Stop(); } catch { /* swallow */ }
                        // Don't Stop() the stub manager from this catch — it may
                        // be the persistent one and a Wine-side Stop() hang is
                        // exactly the failure we're avoiding. Leave it running;
                        // the next transition re-evaluates via IsRunning.
                        _sdkServer = null;
                        _sdkStubManager = null;
                    }
                }
                else
                {
                    // Stop the CoAP server, then the stub. Stopping the stub
                    // restores the registry redirect (before the kill, so it
                    // survives a Wine-side hang). Clear the persistent static so
                    // nothing re-applies the redirect after an explicit "off".
                    try { _sdkServer?.Stop(); _sdkServer?.Dispose(); }
                    catch (Exception ex) { MozaLog.Warn($"[Sdk] server stop: {ex.Message}"); }
                    _sdkServer = null;

                    var stub = _sdkStubManager ?? s_persistentSdkStubManager;
                    if (stub != null)
                    {
                        try { stub.TryStop(1500); }
                        catch (Exception ex) { MozaLog.Warn($"[Sdk] stub stop: {ex.Message}"); }
                        MozaLog.Info("[Sdk] CoAP SDK emulation disabled — stub stopped, registry restored");
                    }
                    _sdkStubManager = null;
                    s_persistentSdkStubManager = null;
                }
            }
        }

        /// <summary>
        /// Start or stop the PitHouse-compatible plain-UDP control server
        /// (port 40288) at runtime. Parallel to
        /// <see cref="SetSdkEmulationEnabled"/>; shares the same lifecycle gate
        /// and is driven from both <see cref="Init"/> and the live UI toggle.
        /// </summary>
        internal void SetUdpControlEnabled(bool enabled)
        {
            lock (_sdkLifecycleGate)
            {
                if (enabled)
                {
                    if (_controlUdpServer != null) return; // already running
                    try
                    {
                        _controlUdpServer = new Sdk.PitHouseUdp.MozaControlUdpServer(
                            _data, _hardwareApplier);
                        _controlUdpServer.Start();
                        MozaLog.Info("[Sdk] UDP control server enabled");
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Error($"[Sdk] Failed to start UDP control server: {ex.Message}");
                        try { _controlUdpServer?.Stop(); } catch { /* swallow */ }
                        _controlUdpServer = null;
                    }
                }
                else
                {
                    try { _controlUdpServer?.Stop(); _controlUdpServer?.Dispose(); }
                    catch (Exception ex) { MozaLog.Warn($"[PitHouseUdp] server stop: {ex.Message}"); }
                    _controlUdpServer = null;
                }
            }
        }

        internal MozaHidReader HidReader => _hidReader;

        /// <summary>Live truck-sim stalk controller (for the settings UI's
        /// "Re-sync wipers" action). Null until Init runs.</summary>
        internal StalksTruckSimController StalksController => _stalksController;
    }
}
