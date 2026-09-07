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

        // Fires from MozaSerialConnection.HandleIoFailure on the read or
        // write thread once the port has been force-closed. Pause telemetry
        // and reset wheel detection right now rather than waiting for the
        // next reconnect-timer tick — otherwise the sender keeps firing and
        // accumulating ack waiters / catalog state for ~5 s.
        // System power-state notification (sleep/resume). Fires on the dedicated
        // SystemEvents notification thread — keep it non-blocking. Only Resume is
        // handled: after sleep the wheel power-cycles and drops its display/
        // telemetry sessions while the host tty can stay half-open, so we force a
        // clean reconnect on the display-bearing pipes (primary wheel + standalone
        // CM2). ForceReconnect raises Disconnected → OnSerialDisconnected /
        // OnDashboardDisconnected, which reset detection + Stop the sender; the
        // reconnect timer then reopens a fresh port and the session pipeline
        // rebuilds. Config-only lanes (hub/base-aux/AB9/peripherals) self-heal via
        // the connection's ~30 s half-open detector — a stale config lane is benign.
        private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (IsShuttingDown) return;
            if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
            MozaLog.Info("[AZOM] System resume — forcing reconnect to rebuild display sessions");
            // ForceReconnect can take a beat (raises the full detection/telemetry
            // reset chain); get off the SystemEvents thread so we don't stall other
            // power-event subscribers.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (IsShuttingDown) return;
                try { _connection?.ForceReconnect("System resume"); }
                catch (Exception ex) { MozaLog.Warn($"[AZOM] Resume reconnect (primary): {ex.Message}"); }
                try { _dashboardManager?.Connection?.ForceReconnect("System resume"); }
                catch (Exception ex) { MozaLog.Warn($"[AZOM] Resume reconnect (dashboard): {ex.Message}"); }
            });
        }

        // Detach the static PowerModeChanged subscription exactly once. Safe to
        // call from both End() and CleanupPartialInit (the Interlocked.Exchange
        // gate makes the second call a no-op).
        private void UnhookPowerMode()
        {
            if (Interlocked.Exchange(ref _powerModeHooked, 0) == 1)
            {
                try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; }
                catch (Exception ex) { MozaLog.Debug($"[AZOM] PowerModeChanged unhook: {ex.Message}"); }
            }
        }

        // SimHub's Arduino scanner (ComportScanner) asks subscribers before
        // opening each candidate COM port. Mark MOZA ports busy so it skips
        // them. Runs on the scanner's Parallel.ForEach workers — keep it cheap
        // and throw-proof.
        private void OnArduinoPortCanBeScanned(object? sender, SerialDash.SerialDashController.ScanArgs e)
        {
            try
            {
                string? port = e?.ComPort;
                if (string.IsNullOrEmpty(port)) return;
                string? claim = DescribeMozaPortClaim(port!);
                if (claim == null) return;
                e!.PortIsBusy = true;
                e.BusyReason = claim;
                MozaLog.Debug($"[AZOM] Vetoed SimHub Arduino scan of {port} ({claim})");
            }
            catch { /* never break SimHub's scanner */ }
        }

        // A port is ours when a plugin connection holds it, a device source
        // classifies it as a MOZA composite, it is the COM name Wine assigned to a
        // MOZA tty, or it matches one of the per-lane persisted last-good ports.
        //
        // The COM-label check is what keeps the veto working under Wine: there the
        // plugin opens the tty by unix path and never learns a COM name from the
        // connection, so without it SimHub's own scanner would happily open the
        // port we are holding (wine ptys have no O_EXCL).
        private string? DescribeMozaPortClaim(string port)
        {
            if (MozaSerialConnection.IsPortHeld(port))
                return "MOZA (in use)";
            if (MozaPortDiscovery.Instance.TryGetByPort(port, out var info))
                return $"MOZA {MozaUsbIds.Describe(info.Pid)}";
            if (Protocol.WineComNameResolver.IsMozaComName(port))
                return "MOZA (wine device)";
            var s = _settings;
            if (s != null)
            {
                bool Match(string saved) =>
                    !string.IsNullOrEmpty(saved)
                    && string.Equals(saved, port, StringComparison.OrdinalIgnoreCase);
                if (Match(s.LastWheelbasePort) || Match(s.LastAb9Port)
                    || Match(s.LastDashboardPort) || Match(s.LastHubPort)
                    || Match(s.LastBaseAuxPort))
                    return "MOZA (last known port)";
            }
            return null;
        }

        // Mirror of UnhookPowerMode for the Arduino-scan veto subscription.
        private void UnhookArduinoScanVeto()
        {
            if (Interlocked.Exchange(ref _arduinoScanHooked, 0) == 1)
            {
                try { SerialDash.ComportScanner.Instance.CanBeScanned -= OnArduinoPortCanBeScanned; }
                catch (Exception ex) { MozaLog.Debug($"[AZOM] Arduino-scan veto unhook: {ex.Message}"); }
            }
        }

        private void OnSerialDisconnected()
        {
            if (IsShuttingDown) return;
            // Pause first so the sender's next tick sees the timer stopped
            // before ResetWheelDetection issues its full Stop().
            try { _telemetrySender?.Pause(); } catch { }
            // Drop pending response watches — the wheel/port we sent to is
            // gone; their pending responses will never arrive on this
            // connection. They'd otherwise keep retrying after reconnect
            // against a fresh wheel that may not even speak the same protocol.
            try { PendingResponses.Clear(); } catch { }
            // The primary pipe dropped. If we were in the migrated (hub-primary)
            // state, tear down the dedicated base-aux pipe and clear the migration
            // latch so the next reconnect re-evaluates from scratch (base reverts
            // to primary, the wheel is re-probed on it). Harmless no-op when the
            // base is the primary (base-aux isn't connected).
            try { _baseManager?.Disconnect(); } catch { }
            _connectionCoordinator?.ResetHubWheelMigrationState();
            if (DetectionState.NewWheelDetected || DetectionState.OldWheelDetected || DetectionState.DashDetected)
                ResetWheelDetection("Serial disconnect — resetting wheel detection");
            // UNGATED, unlike the wheel reset above: an earlier rim reset may have
            // already cleared the wheel/dash flags, and the base state would then
            // survive a real disconnect with nothing left to re-probe it.
            ResetBaseDetection("Serial disconnect — resetting base detection");
        }

        /// <summary>
        /// Drop wheelbase detection + identity so the prober re-runs the base detect
        /// cascade on the next connection. Call ONLY when the base itself is gone
        /// (connection loss, deliberate disable) — never for a rim hot-swap, which
        /// leaves the base attached; see <see cref="ResetWheelDetection"/>.
        /// </summary>
        internal void ResetBaseDetection(string reason)
        {
            if (_data == null) return;
            MozaLog.Debug($"[AZOM] {reason}");
            DetectionState.ResetBase();
            _data.ClearBaseIdentity();
            _data.BaseSettingsRead = false;
            // Half-received main-model-name chunks must not recombine with the next
            // base's reply.
            _baseModelChunk1 = null;
            _baseModelChunk2 = null;
            // Ownership only drops if the pipe that went away was the owner; a base
            // on the dedicated base-aux pipe keeps its own.
            if (ReferenceEquals(DetectionState.BaseOwner, _deviceManager))
                DetectionState.BaseOwner = null;
        }

        /// <summary>
        /// Clear ALL device-detection flags. Called by Init() and End() so a plugin
        /// reload doesn't carry over stale detected state. See <see cref="ResetWheelDetection"/>
        /// for the hot-swap-scoped reset that preserves base/hub/handbrake/pedals, and
        /// <see cref="ResetBaseDetection"/> for the base-scoped one.
        /// </summary>
        private void ResetDetectionFlags()
        {
            DetectionState.ResetAll();
            if (_data != null) _data.ClearBaseIdentity();
            _baseModelChunk1 = null;
            _baseModelChunk2 = null;
        }

        /// <summary>
        /// Rim-scoped reset: preserves base/hub/handbrake/pedals detection AND base
        /// identity, because most callers (presence miss, rim detach, model/ID
        /// hot-swap) fire with the base still attached. A caller that knows the base
        /// went away calls <see cref="ResetBaseDetection"/> as well.
        /// </summary>
        internal void ResetWheelDetection(string reason)
        {
            MozaLog.Debug($"[AZOM] {reason}");
            _telemetrySender?.Stop();
            // Preserve dash detection across a WHEEL-rim reset: the dash (CM2/CM1)
            // is reached through the CONNECTION, not the wheel rim, so a hot-swap or
            // presence-miss of the rim must NOT blank it. Two cases:
            //   • standalone-USB dash — lives on its own pipe (IsStandaloneDashboardUsbConnection);
            //   • base-bridged (bus) dash — lives on the still-live wheelbase connection.
            // ResetWheel() clears DashDetected unconditionally; re-assert it for both.
            // CRITICAL: a bus CM2 behind the base is independent of which rim is
            // attached. Letting a rim miss clear DashDetected flips the dual-display
            // `want` false, and the periodic EnsureCm2Pipeline reconcile then tears
            // down a perfectly healthy CM2 dashboard (LEDs survive on a separate path,
            // so the symptom is "CM2 dash dead, LEDs fine"). Only a real connection
            // loss (Connection.IsConnected false) should drop a bus dash.
            bool preserveDash = IsStandaloneDashboardUsbConnection
                || (DetectionState.DashDetected && _connection?.IsConnected == true);
            DetectionState.ResetWheel();
            if (preserveDash)
                DetectionState.DashDetected = true;
            WheelModelInfo = null;
            _data.ClearWheelIdentity();
            // ClearWheelIdentity above blanks _data.Display* fields, but
            // TelemetrySender keeps its own _displayDetected / _displayModelName
            // latch (see SetDisplayDetected) — clear that too so the next
            // wheel's StartTelemetryIfReady display gate doesn't read stale
            // detection and bypass the ~20 s display-boot wait.
            _telemetrySender?.ResetDisplayDetection();
            // Clear the wedge-watchdog timestamp so elapsed-since-detect is
            // measured against the NEXT wheel's rising edge, not a stale one
            // from the wheel we just disconnected. DisplayWedgeRecoveryFired
            // intentionally NOT reset here — only cleared on a successful
            // display detection (or manual Connection-enable toggle), which
            // is what prevents the auto-recovery from looping when the
            // display is permanently wedged.
            Interlocked.Exchange(ref _wheelDetectedUtcTicks, 0);
            _deviceManager.ResetWheelDetection();
            if (_telemetrySender != null)
                _telemetrySender.DetectedDeviceMask = 0;
            Interlocked.Exchange(ref _telemetryStartRequested, 0);
            // Hot-swap may bind a different default dashboard; force kind=4 re-emit.
            _dashboardBindingCoordinator?.ClearLastAppliedDashboardKey();
            _telemetrySender?.ResetBindingTracking();
            // Drop sunsets — the newly-attached wheel may support commands the
            // previous one didn't, and any cross-device entries should re-try.
            try { PendingResponses.Clear(); } catch { }
        }
    }
}
