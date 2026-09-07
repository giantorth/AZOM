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

        public void End(PluginManager pluginManager)
        {
            IsShuttingDown = true;
            MozaLog.Info("[AZOM] Shutting down plugin");

            // 1. Stop timers first so no new callbacks fire against disposed state.
            _profileCoordinator?.StopSaveDebounceTimer();
            _pollTimer?.Stop();
            _tempHistoryTimer?.Stop();
            _torqueHistoryTimer?.Stop();
            _retryTimer?.Stop();
            _reconnectTimer?.Stop();

            // Stop the AB9 engine-vib worker before the AB9 manager / connection
            // are disposed; the tick gates on connection state but joining here
            // keeps shutdown deterministic.
            try { _ab9Worker?.Stop(); _ab9Worker = null; } catch { }
            try { _baseLfeWorker?.Stop(); _baseLfeWorker = null; } catch { }
            // Drop the coalescing timer for flash-backed wheel writes. Anything still
            // parked is already in the profile, so the next connect's apply carries it.
            try { _hardwareApplier?.Shutdown(); } catch { }
            // Release the timer-resolution request + power-throttling opt-out on
            // shutdown/reload so neither leaks past the plugin's lifetime.
            try { _responsiveness?.Dispose(); _responsiveness = null; } catch { }

            // Remove the Control Mapper variant provider so a plugin reload
            // (game switch) doesn't leave a dead provider in VariantHelper's
            // list. The bridge is null when the toggle was off or construction
            // failed in Init.
            try { _controlMapperBridge?.Unregister(); _controlMapperBridge = null; } catch { }

            // Burst silent-slot frames + an engine-pulse OFF to stop the AB9
            // effect immediately on shutdown. Without this the firmware keeps
            // the last-streamed buzz running until its ~10 s keepalive timeout,
            // which means users hear vibrations for ten seconds after closing
            // SimHub mid-session. Worker is stopped above so this can't race.
            try { _ab9Manager?.SendEngineSilence(); } catch { }
            // Flush LFE effects so the base doesn't latch a waveform on teardown.
            try
            {
                _deviceManager?.SendBaseLfeDisable(Protocol.MozaBaseLfeProtocol.LfeEffect.Engine);
                _deviceManager?.SendBaseLfeDisable(Protocol.MozaBaseLfeProtocol.LfeEffect.Abs);
                _deviceManager?.SendBaseLfeDisable(Protocol.MozaBaseLfeProtocol.LfeEffect.Gearshift);
            }
            catch { }

            // Dispose every mBooster controller — fires disable frames + closes
            // each connection. Must happen before MozaData is torn down so the
            // position-merge path (which writes to _data) doesn't race.
            try { _mboosterRegistry?.Dispose(); _mboosterRegistry = null; } catch { }
            try { DisposeRoutedMBoosterProbes(); } catch { }
            // Standalone pedals/handbrake connections — close before MozaData
            // teardown so the response path (which writes to _data) can't race.
            try { _peripheralRegistry?.Dispose(); _peripheralRegistry = null; } catch { }

            // Tear down SDK emulation up-front. The CoAP receive thread holds
            // references into MozaData and HardwareApplier; stop it before the
            // rest of the wire stack disposes those out from under it. The
            // PitHouse UDP control server holds the same references and uses
            // the same shutdown pattern.
            _sdk?.StopServers();

            // Decide up-front whether the persistent wire will survive this
            // teardown — same condition the dispose step below uses. We need
            // it now so detection state can be captured (vs. wiped) in lock-
            // step with the wire AND so the CoAP stub manager teardown below
            // can match the wire's persistence decision.
            bool keepWireAlive = _usingPersistentWire
                                 || (_connection != null && _connection == s_persistentConnection
                                     && _telemetrySender != null
                                     && _telemetrySender == s_persistentTelemetrySender);

            // CoAP stub manager: persist across game-switch reloads alongside
            // the wire. Stopping the stub on every End()+Init() cycle (a) is
            // wasted work (the stub holds no per-plugin-instance state) and
            // (b) intermittently HANGS under Wine/Proton — Stop() wedged on
            // the 2026-05-25 second-game-switch path between the registry
            // restore log and the Process.Kill / JobObject.Dispose call.
            //
            // FSR1 driver + CM2 sender are per-instance (recreated each Init), never
            // persistent — always stop them on End so a keepWireAlive game-switch
            // doesn't leave two ticking the same connection after re-Init.
            try { _fsr1Driver?.Dispose(); } catch { }
            _fsr1Driver = null;
            try { _cm2Sender?.Dispose(); } catch { }
            _cm2Sender = null;
            try { _cm1Driver?.Dispose(); } catch { }
            _cm1Driver = null;

            // Persistent-stub policy on End: keepWireAlive drops only the
            // instance ref (next Init reuses the live child); a true cold-start
            // reset stops the stub and clears the static. See
            // Sdk/SdkLifecycleCoordinator.ReleaseStubOnEnd.
            _sdk?.ReleaseStubOnEnd(keepWireAlive);

            if (keepWireAlive)
            {
                // Wire stays; the device(s) on the other end are still those
                // we already probed. Hand the detection bag to the next plugin
                // instance so sub-device tabs (handbrake/pedals/hub/dash) stay
                // visible across the reload — presence probes don't reliably
                // re-ACK on the reused wire and would otherwise leave tabs
                // permanently hidden until SimHub restarts. The pipe owners are
                // this instance's managers (disposed below) — drop them so the
                // next instance's probers re-point rather than write into a
                // disposed manager.
                DetectionState.ClearOwners();
                s_persistentDetectionState = DetectionState;
            }
            else
            {
                // Clear detection flags so a future cold-start Init() doesn't
                // see stale state from a wire that's about to be torn down.
                ResetDetectionFlags();
            }

            // 2. Persist settings and clear LEDs while connection is still alive.
            try { this.SaveCommonSettings("MozaPluginSettings", _settings); } catch { }
            try { _hardwareApplier?.ClearLedsOnHardware(); } catch { }

            // 3. Detach event subscriptions so any in-flight callback from a still-running
            //    background thread (HID/serial reader) cannot reach the plugin during teardown.
            //    PowerModeChanged is static — detach first so a resume mid-teardown can't
            //    schedule a ForceReconnect against tearing-down state.
            UnhookPowerMode();
            UnhookArduinoScanVeto();
            try
            {
                if (_connection != null)
                {
                    _connection.MessageReceived -= OnMessageReceived;
                    _connection.Disconnected -= OnSerialDisconnected;
                }
            }
            catch { }
            try
            {
                if (_telemetrySender != null)
                {
                    _telemetrySender.DashboardPipelineParked -= _dashboardBindingCoordinator.OnDashboardPipelineParked;
                    _telemetrySender.WheelInitiatedSwitch -= _dashboardBindingCoordinator.OnWheelInitiatedSwitch;
                }
            }
            catch { }
            try { _profileCoordinator?.DetachProfileStore(); } catch { }

            // 4. Persistent wire: skip Stop+Dispose if we own the static refs
            //    so the next Init picks up open sessions without the settle wait.
            //    (keepWireAlive was computed earlier so detection-state capture
            //    could happen before ResetDetectionFlags() wiped the bag.)
            if (!keepWireAlive)
            {
                _telemetrySender?.Stop();
            }

            // Release the wire-trace file handle (new file per Init by design).
            // The in-memory ring stays enabled across the plugin reload (capture
            // is always on) so buffered frames survive game switches.
            try { global::MozaPlugin.Diagnostics.SerialTrafficCapture.Instance.StopFileSink(); } catch { }

            // 5. Cancel paced setting-reads (avoids tasks running past teardown).
            try { _deviceManager?.Dispose(); } catch { }

            // 6. Dispose I/O sources; skip sender+connection if keeping wire alive.
            _hidReader?.Dispose();
            try { _stalksController?.Dispose(); } catch { }
            if (!keepWireAlive)
            {
                _telemetrySender?.Dispose();
                _fsr1Driver?.Dispose();
                _cm1Driver?.Dispose();
                _connection?.Dispose();
                // Clear static refs so the next Init takes the cold-start path.
                if (_connection == s_persistentConnection)
                    s_persistentConnection = null;
                if (_telemetrySender == s_persistentTelemetrySender)
                    s_persistentTelemetrySender = null;
                // Wire is gone — discard the captured detection bag too so the
                // next Init re-probes against whatever's actually attached.
                s_persistentDetectionState = null;
            }
            else
            {
                MozaLog.Info(
                    "[AZOM] End: keeping persistent wire (connection + telemetry sender) alive " +
                    "across plugin reload — wheel sessions remain open, no settle wait on next Init");
            }
            try
            {
                if (_ab9Manager != null)
                    _ab9Manager.MessageReceived -= OnAb9MessageReceived;
            }
            catch { }
            _ab9Manager?.Dispose();

            try
            {
                if (_dashboardManager != null)
                {
                    _dashboardManager.MessageReceived -= OnDashboardMessageReceived;
                    _dashboardManager.Connection.Disconnected -= OnDashboardDisconnected;
                }
            }
            catch { }
            _dashboardManager?.Dispose();

            try
            {
                if (_hubManager != null)
                {
                    _hubManager.MessageReceived -= OnHubMessageReceived;
                    _hubManager.Connection.Disconnected -= OnHubDisconnected;
                }
                if (_baseManager != null)
                {
                    _baseManager.MessageReceived -= OnBaseMessageReceived;
                    _baseManager.Connection.Disconnected -= OnBaseDisconnected;
                }
            }
            catch { }
            _hubManager?.Dispose();
            _baseManager?.Dispose();

            // 7. Dispose timers after I/O is gone.
            _profileCoordinator?.DisposeSaveDebounceTimer();
            _pollTimer?.Dispose();
            _tempHistoryTimer?.Dispose();
            _torqueHistoryTimer?.Dispose();
            _retryTimer?.Dispose();
            _reconnectTimer?.Dispose();

            // 8. Null Instance last so any straggler callback can still no-op via IsShuttingDown.
            Instance = null;
        }

        // One-shot registration of the AppDomain.ProcessExit handler. Safe
        // to call from every Init() — only the first crosses the gate.
        private static void EnsureProcessExitHandlerRegistered()
        {
            if (Interlocked.Exchange(ref s_processExitHandlerRegistered, 1) != 0) return;
            try { AppDomain.CurrentDomain.ProcessExit += OnAppDomainProcessExit; }
            catch (Exception ex)
            {
                try { MozaLog.Warn($"[AZOM] ProcessExit handler registration failed: {ex.Message}"); } catch { }
            }
        }

        // Fires when the SimHub process is terminating. End() ran earlier
        // for each plugin instance but the keepWireAlive branch leaves the
        // persistent telemetry sender / serial connection alive (so plugin
        // reloads on game switch don't pay the sess=0x09 settle wait). On
        // full exit we still need a clean SessionClose 0x01/0x02/0x03
        // burst so the wheel doesn't carry stale host-side session state
        // into the next SimHub launch — see s_processExitHandlerRegistered
        // doc for the failure mode.
        //
        // ProcessExit has a ~2 s budget before the runtime is killed. Stop()
        // takes ~110 ms (timer dispose + FlushPendingWrites + 3 close frames
        // + 100 ms drain sleep), well inside budget. Connection Dispose then
        // closes the serial port cleanly so the close frames actually leave
        // the OS write buffer before the FTDI handle goes away.
        private static void OnAppDomainProcessExit(object? sender, EventArgs e)
        {
            try
            {
                var ts = s_persistentTelemetrySender;
                if (ts != null && !ts.IsDisposedFlag)
                {
                    try { ts.Stop(); }
                    catch (Exception ex)
                    {
                        try { MozaLog.Warn($"[AZOM] ProcessExit Stop(): {ex.GetType().Name}: {ex.Message}"); } catch { }
                    }
                }
            }
            catch { }
            try
            {
                var conn = s_persistentConnection;
                conn?.Dispose();
            }
            catch { }
            // Stop the persistent CoAP stub on full process exit so its child
            // process (and the registry redirect) don't outlive SimHub.
            // TryStop bounds the call at 1.5 s — well inside the ~2 s
            // ProcessExit budget — so a Wine-side wedge in Process.Kill or
            // JobObject.Dispose can't keep us from returning. If TryStop
            // times out the JobObject's KILL_ON_JOB_CLOSE backstops the
            // child cleanup on process exit, and the next launch's
            // orphan sweep handles the case where even that didn't fire.
            try { Sdk.SdkLifecycleCoordinator.StopPersistentStub(); }
            catch { }
        }
    }
}
