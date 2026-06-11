using System;
using MozaPlugin.Devices;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Dual-display pipeline coordination: drives a CM2 dash on a second
    /// tier-def sender concurrently with a wheel screen, discriminates a
    /// bus-bridged CM1 (group-0x35, no tier-def catalog) from a real CM2 and
    /// hands off to the CM1 driver, and owns the FSR1/CM1 driver start/stop
    /// gates. The sender/driver instances (<c>_cm2Sender</c>/<c>_cm1Driver</c>/
    /// <c>_fsr1Driver</c>) stay on <see cref="MozaPlugin"/> (End() teardown +
    /// diagnostics) and are reached via internal fields.
    /// </summary>
    internal sealed class DualDisplayCoordinator
    {
        private readonly MozaPlugin _plugin;
        private readonly DeviceDetectionState _detectionState;

        internal DualDisplayCoordinator(MozaPlugin plugin, DeviceDetectionState detectionState)
        {
            _plugin = plugin;
            _detectionState = detectionState;
        }

        /// <summary>Start the FSR V1 group-0x42 display driver when an FSR1 wheel is
        /// connected; stop it if the wheel is no longer FSR1 (hot-swap). Telemetry-
        /// enable gating is handled inside the driver tick.</summary>
        internal void StartFsr1DriverIfNeeded()
        {
            if (_plugin._fsr1Driver == null) return;
            if (_plugin.IsFsr1DisplayWheel)
            {
                if (!_plugin._fsr1Driver.IsRunning && _plugin.Connection?.IsConnected == true)
                    _plugin._fsr1Driver.Start();
            }
            else if (_plugin._fsr1Driver.IsRunning)
            {
                _plugin._fsr1Driver.Stop();
            }
        }

        /// <summary>
        /// Drive a CM2 dash on a SECOND tier-def sender concurrently with a wheel that
        /// has its own screen (FSR1 driver or a tier-def display wheel). The CM2
        /// catalog-synthesises its own dashboard, so no mzdash is needed here. On the
        /// shared wheelbase bus the CM2 sender uses lane base 18 + strict inbound, and
        /// the wheel's tier-def sender (if any) is flipped to strict/shares so the two
        /// don't collide. On a CM2's own USB cable it uses base 0 on that connection.
        /// Tears the CM2 sender down when the dual-screen condition no longer holds.
        /// </summary>
        internal void EnsureCm2Pipeline()
        {
            bool wheelHasOwnScreen = _plugin.IsFsr1DisplayWheel || (_plugin.WheelModelInfo?.HasDisplay == true);
            bool busCm2 = _detectionState.DashDetected && !_plugin.DashboardUsbConnected
                          && _plugin.Connection?.IsConnected == true;
            bool usbCm2 = _plugin.DashboardUsbConnected;
            bool want = _plugin.ActiveTelemetryEnabled && wheelHasOwnScreen && (busCm2 || usbCm2);

            if (!want)
            {
                if (_plugin._cm2Sender != null) { try { _plugin._cm2Sender.Stop(); } catch { } }
                if (_plugin._cm1Driver != null && _plugin._cm1Driver.IsRunning) { try { _plugin._cm1Driver.Stop(); } catch { } }
                // Wheel sender no longer shares the bus with a CM2 sender.
                if (_plugin.TelemetrySender != null)
                {
                    _plugin.TelemetrySender.SharesConnection = false;
                    _plugin.TelemetrySender.StrictInboundFilter = false;
                }
                return;
            }

            // Known CM1 base-bridged dash (group-0x35, no tier-def catalog): drive it with
            // the dedicated Cm1DisplayDriver, never the tier-def sender. (CM1 only applies
            // to a bus-bridged dash; a USB dash on PID 0x0025 is always a real CM2.)
            if (busCm2 && _plugin.DashIsCm1)
            {
                if (_plugin._cm2Sender != null) { try { _plugin._cm2Sender.Stop(); } catch { } }
                if (_plugin.TelemetrySender != null)
                {
                    _plugin.TelemetrySender.SharesConnection = false;
                    _plugin.TelemetrySender.StrictInboundFilter = false;
                }
                StartCm1DriverIfNeeded();
                return;
            }

            var conn = usbCm2 ? _plugin.DashboardConnection : _plugin.Connection;
            if (conn == null) return;
            // Standalone-USB CM2 bridges as the main 0x12; a CM2 behind the
            // wheelbase is the meter at 0x14 (PitHouse cm2.pcapng drives the
            // bus CM2's session + telemetry on 0x14, which engages and answers;
            // 0x12 there is the base main and never engages the session layer).
            // A bus CM2 keeps lane-base 18 so it coexists with the wheel screen.
            byte dev = usbCm2 ? MozaProtocol.DeviceMain : MozaProtocol.DeviceDash; // 0x12 / 0x14
            int slotBase = busCm2 ? 18 : 0;
            bool shareBus = busCm2;

            if (_plugin._cm2Sender == null)
                _plugin._cm2Sender = new TelemetrySender(conn);
            else if (_plugin._cm2Sender.StateIsIdle)
                _plugin._cm2Sender.Rebind(conn); // no-op when already on this connection

            var cm2 = _plugin._cm2Sender;
            cm2.Policy = Era.EraPolicy.For(_plugin.ActiveTelemetryWheelEra);
            cm2.PropertyResolver = _plugin.PropertyResolver.ResolveAsDouble;
            cm2.PropertyStringResolver = _plugin.PropertyResolver.ResolveAsString;
            cm2.UploadDashboard = false;
            cm2.SetDownloadEnabled(false);
            cm2.StandaloneDashboardMode = true;
            cm2.TargetDeviceId = dev;
            cm2.StreamSlotBase = slotBase;
            cm2.SharesConnection = shareBus;
            cm2.StrictInboundFilter = shareBus;
            cm2.ProfileTelemetryEnabled = true;
            // CM2 channel mappings live under the dash device GUID + a fixed key,
            // independent of the wheel, so the CM2's catalog-synth applies its own.
            cm2.MappingPageGuid = MozaPlugin.Cm2PageGuid;
            cm2.MappingDashKeys = new[] { MozaPlugin.Cm2DashKey };
            // A bus-bridged dash of unknown type might be a CM1 (group-0x35) that never
            // advertises a tier-def catalog. Suppress the no-catalog engagement watchdog
            // so it doesn't loop restarts while TickCm1Discriminator decides. A USB dash
            // (0x0025) is always a real CM2 → never suppress.
            cm2.SuppressDisplayWatchdog = busCm2 && !_plugin.DashIsCm1;

            // A tier-def WHEEL sender sharing the same bus must also filter strictly.
            bool wheelTierDefOnBus = busCm2 && !_plugin.IsFsr1DisplayWheel
                                     && (_plugin.WheelModelInfo?.HasDisplay == true);
            if (_plugin.TelemetrySender != null)
            {
                _plugin.TelemetrySender.SharesConnection = wheelTierDefOnBus;
                _plugin.TelemetrySender.StrictInboundFilter = wheelTierDefOnBus;
            }

            if (cm2.FramesSent == 0)
            {
                // Fresh start: allow the saved-dashboard re-assert to fire once the
                // CM2 advertises its dashboard list (PollStatus → TickCm2DashboardReassert).
                _cm2ReassertAttempted = false;
                // Stamp the start so TickCm1Discriminator can time the catalog-wait.
                _cm2StartUtc = DateTime.UtcNow;
                // Re-stamped when the sender reaches Active (the discriminator times
                // its CM1 decision from there, not from start — cold-start is long).
                _cm2ActiveUtc = DateTime.MinValue;
                // Fresh discrimination cycle — clear the param-read flag so a stale
                // CM1 answer can't fast-latch a newly-attached CM2.
                _dashParamReadAnswered = false;
                _lastCm1ProbeUtc = DateTime.MinValue;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { cm2.Start(); }
                    catch (Exception ex) { MozaLog.Warn($"[AZOM] CM2 pipeline start failed: {ex.Message}"); }
                });
            }
        }

        // One-shot guard: re-assert the saved CM2 dashboard once per pipeline start.
        private bool _cm2ReassertAttempted;

        /// <summary>
        /// PollStatus hook: once the CM2 sender advertises its dashboard list, switch
        /// it to the user's saved selection (<see cref="MozaPlugin.ActiveCm2DashboardName"/>) so
        /// the choice survives a pipeline restart — the CM2 analogue of the wheel's
        /// TickPendingDashboardRetry. Fires at most once per CM2 (re)start.
        /// </summary>
        internal void TickCm2DashboardReassert()
        {
            if (_cm2ReassertAttempted) return;
            var cm2 = _plugin._cm2Sender;
            if (cm2 == null || !cm2.Enabled || cm2.FramesSent == 0) return;

            var list = cm2.WheelState?.ConfigJsonList;
            if (list == null || list.Count == 0) return; // not advertised yet — keep waiting

            string saved = _plugin.ActiveCm2DashboardName;
            if (string.IsNullOrEmpty(saved)) { _cm2ReassertAttempted = true; return; }

            int slot = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], saved, StringComparison.OrdinalIgnoreCase)) { slot = i; break; }
            }
            if (slot < 0) { _cm2ReassertAttempted = true; return; } // saved dash not on this CM2

            _cm2ReassertAttempted = true; // claim before issuing — switch restarts the pipeline
            if (cm2.WheelReportedSlot == slot) return; // already there

            MozaLog.Info($"[AZOM] Re-asserting saved CM2 dashboard '{saved}' (slot {slot}) after pipeline start");
            _plugin.OnCm2DashboardSwitched((uint)slot);
        }

        // CM1 discriminator: when did the tier-def _cm2Sender start streaming? Used to
        // time the catalog-wait before declaring a bridged dash a CM1.
        private DateTime _cm2StartUtc = DateTime.MinValue;
        // When the _cm2Sender first reached Active (cold-start done). The CM1 decision
        // is timed from here: a CM1 advertises no catalog AND emits no value frames,
        // so timing from FramesSent>0 (which never happens) wedged the discriminator.
        private DateTime _cm2ActiveUtc = DateTime.MinValue;
        // Past the watchdog's 20s engagement grace + 3s confirm; a real tier-def CM2
        // advertises its catalog well within this, a CM1 never does.
        private static readonly TimeSpan Cm1DecideAfter = TimeSpan.FromSeconds(25);
        // Set when the dash answers the group-0x0E param-read probe with a 0x8E
        // reply (MozaPlugin.OnMessageReceived) — a positive CM1 signal a tier-def
        // CM2 doesn't produce. Lets TickCm1Discriminator latch CM1 on the fast path.
        private volatile bool _dashParamReadAnswered;
        private DateTime _lastCm1ProbeUtc = DateTime.MinValue;
        // Fast-path latch: param-read answered + no catalog after this short
        // settle → CM1, without waiting out Cm1DecideAfter. Kept long enough that
        // a slow tier-def CM2's catalog still arrives first and wins.
        private static readonly TimeSpan Cm1FastDecideAfter = TimeSpan.FromSeconds(5);

        /// <summary>Serial-read-thread hook: the dash answered the group-0x0E
        /// param-read probe with a 0x8E reply.</summary>
        internal void NoteDashParamReadAnswered() => _dashParamReadAnswered = true;

        /// <summary>Start (or stop) the CM1 group-0x35 driver for a confirmed CM1 dash.
        /// Mirrors <see cref="StartFsr1DriverIfNeeded"/>.</summary>
        internal void StartCm1DriverIfNeeded()
        {
            if (_plugin._cm1Driver == null) return;
            bool busDash = _detectionState.DashDetected && !_plugin.DashboardUsbConnected
                           && _plugin.Connection?.IsConnected == true;
            if (_plugin.ActiveTelemetryEnabled && _plugin.DashIsCm1 && busDash)
            {
                if (!_plugin._cm1Driver.IsRunning) _plugin._cm1Driver.Start();
            }
            else if (_plugin._cm1Driver.IsRunning)
            {
                _plugin._cm1Driver.Stop();
            }
        }

        /// <summary>
        /// PollStatus hook: decide whether a bus-bridged dash is a CM1 (group-0x35, never
        /// advertises a tier-def catalog) rather than a tier-def CM2. Once the tier-def
        /// _cm2Sender has run past the engagement grace with no catalog, latch DashIsCm1,
        /// tear down the (watchdog-suppressed) _cm2Sender, and hand off to the CM1 driver.
        /// If a catalog DOES arrive, it's a real CM2 → drop the suppress flag.
        /// </summary>
        internal void TickCm1Discriminator()
        {
            if (_plugin.DashIsCm1) { StartCm1DriverIfNeeded(); return; }

            var cm2 = _plugin._cm2Sender;
            if (cm2 == null || !cm2.Enabled) return;

            // CM1 only applies to a bus-bridged dash; a USB dash (0x0025) is a real CM2.
            bool busCm2 = _detectionState.DashDetected && !_plugin.DashboardUsbConnected
                          && _plugin.Connection?.IsConnected == true;
            if (!busCm2) return;

            if (cm2.CatalogCount > 0)
            {
                // Real tier-def CM2 — stop suppressing its engagement watchdog.
                if (cm2.SuppressDisplayWatchdog) cm2.SuppressDisplayWatchdog = false;
                return;
            }

            // Wait for the sender to finish cold-start (reach Active) before deciding,
            // then time from there. A CM1 advertises no catalog and emits no value
            // frames, so the old `FramesSent == 0` gate never released and the
            // discriminator stayed wedged here forever — the CM1 never engaged.
            if (!cm2.IsActive) return;
            if (_cm2ActiveUtc == DateTime.MinValue) _cm2ActiveUtc = DateTime.UtcNow;

            var elapsed = DateTime.UtcNow - _cm2ActiveUtc;

            // Fast positive CM1 signal: the dash answers the group-0x0E param-read
            // probe with a 0x8E reply (_dashParamReadAnswered); a tier-def CM2
            // doesn't. Re-probe ~1 Hz until it answers or a catalog arrives. Once
            // answered, latch CM1 after a short settle (Cm1FastDecideAfter) so a
            // slow CM2 — whose catalog arrives well inside that window — still
            // wins via the CatalogCount check above. Falls back to the long
            // no-catalog timeout when the dash never answers the probe.
            if (!_dashParamReadAnswered)
            {
                if ((DateTime.UtcNow - _lastCm1ProbeUtc).TotalMilliseconds >= 1000)
                {
                    _lastCm1ProbeUtc = DateTime.UtcNow;
                    try { _plugin.DeviceManager.SendCm1ParamProbe(); } catch { }
                }
            }
            else if (elapsed >= Cm1FastDecideAfter)
            {
                LatchDashAsCm1($"answered param-read (0x8E) with no tier-def catalog in "
                    + $"{Cm1FastDecideAfter.TotalSeconds:F0}s");
                return;
            }

            if (elapsed < Cm1DecideAfter) return;
            LatchDashAsCm1($"no tier-def catalog within {Cm1DecideAfter.TotalSeconds:F0}s (timeout fallback)");
        }

        /// <summary>Latch the bus-bridged dash as a CM1: persist the flag, deploy
        /// the CM1 device definition (its own GUID/tab) and drop the speculative
        /// CM2 copy MarkDashDetected wrote before we could tell them apart (guarded
        /// against a real USB CM2), tear down the tier-def sender, and start the
        /// CM1 driver.</summary>
        private void LatchDashAsCm1(string reason)
        {
            MozaLog.Info($"[AZOM] Bridged dash → CM1 (group-0x35): {reason}; handing off to CM1 driver");
            _plugin.DashIsCm1 = true;
            _plugin.SaveSettings();

            try
            {
                string? pid = _plugin.Connection?.DiscoveredPid;
                if (DeviceDefinitionDeployer.DeployCm1Dashboard(pid))
                    _plugin.DeviceDefinitionDeployed = true;
                DeviceDefinitionDeployer.RemoveSpeculativeCm2Dashboard();
            }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] CM1 device-definition deploy skipped: {ex.Message}"); }

            try { _plugin._cm2Sender?.Stop(); } catch { }
            if (_plugin.TelemetrySender != null)
            {
                _plugin.TelemetrySender.SharesConnection = false;
                _plugin.TelemetrySender.StrictInboundFilter = false;
            }
            StartCm1DriverIfNeeded();
        }
    }
}
