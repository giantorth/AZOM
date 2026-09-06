using System;
using MozaPlugin.Devices;
using MozaPlugin.Protocol;
using MozaPlugin.Devices.Extensions;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Dual-display pipeline coordination: drives a CM2 dash on a dedicated
    /// tier-def sender whenever a CM2 is present (independent of the wheel —
    /// display wheel, screenless wheel, or none), discriminates a
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

        // Teardown debounce: when EnsureCm2Pipeline computes want==false, hold off
        // tearing the CM2 pipeline down until want has stayed false this long. A
        // single-tick detection blip must not abort a mid-cold-start / Active CM2.
        // Stored as UtcNow.Ticks and read/written via Interlocked because
        // EnsureCm2Pipeline runs from BOTH the PollStatus timer thread and the
        // serial-read thread (DeviceProber/ApplyTelemetrySettings) — a non-atomic
        // 64-bit DateTime could tear on 32-bit and yield a garbage elapsed interval.
        // 0 = want is currently true (no pending teardown).
        private long _wantFalseSinceUtcTicks;
        // MUST exceed a couple of PollStatus intervals (~5 s each): EnsureCm2Pipeline
        // is reconciled periodically from PollStatus, so a dwell shorter than the poll
        // let a SINGLE transient want=false tick arm the dwell and the very NEXT poll
        // complete the teardown — Stopping a healthy CM2 on one wheel-presence blip
        // (the "CM2 dash dies on a CS-Pro rim glitch" regression). 12 s ⇒ a teardown
        // requires want=false sustained across ~3 polls (a real CM2/connection loss),
        // not a transient. Paired with the ResetWheelDetection dash-preserve fix.
        private const int TeardownDwellMs = 12000;

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
        /// Drive a CM2 dash on the dedicated _cm2Sender whenever a CM2 is present —
        /// regardless of the wheel (display wheel, screenless wheel, or none). The CM2
        /// catalog-synthesises its own dashboard, so no mzdash is needed here. On the
        /// shared wheelbase bus the CM2 sender uses lane base 18 + strict inbound, and a
        /// tier-def DISPLAY wheel co-residing on that bus is flipped to strict/shares so
        /// the two don't collide. On a CM2's own USB cable it uses base 0. Tears the CM2
        /// sender down (debounced) when no CM2 is present.
        /// </summary>
        internal void EnsureCm2Pipeline()
        {
            bool busCm2 = _detectionState.DashDetected && !_plugin.DashboardUsbConnected
                          && _plugin.Connection?.IsConnected == true;
            bool usbCm2 = _plugin.DashboardUsbConnected;
            // DECOUPLED: the dedicated _cm2Sender drives the CM2 whenever a CM2 is
            // present — regardless of the wheel. (Previously gated on the wheel having
            // its own screen, which left the MAIN sender driving the CM2 for screenless/no-wheel rigs
            // and never ran the CM1 discriminator there — a CM1 with no display-wheel
            // was never identified.) The MAIN sender is now always wheel-only;
            // ApplyTelemetrySettings guarantees it never targets a CM2 device, so the
            // two senders can never collide on 0x12/0x14.
            // Dash-scoped enable, NOT the wheel's: a hub-only / dash-only rig resolves
            // no wheel page GUID, which pinned ActiveTelemetryEnabled false forever and
            // left a standalone-USB CM2 permanently dark (bundle JJV3D910).
            bool want = _plugin.ActiveDashTelemetryEnabled && (busCm2 || usbCm2);

            if (!want)
            {
                // Debounce the ENTIRE teardown — the Stop, the CM1-driver stop, AND
                // the wheel-flag clears must all wait out the dwell. A one-tick blip
                // on any `want` input (DashDetected / DashboardUsbConnected /
                // Connection.IsConnected / ActiveTelemetryEnabled) must change nothing:
                // it would otherwise abort a multi-second CM2
                // cold-start (the CS-Pro 3-attempt/29s pathology) and flip the wheel's
                // SharesConnection false for a tick — which is exactly the flag Stop()
                // reads to choose ClearStreamSlots (safe) vs FlushPendingWrites (blanks
                // the co-resident pipeline + in-flight LEDs). Holding the whole branch
                // keeps the wheel's flags stable-true while the CM2 is live, closing
                // that window without a connection-level rewrite.
                long since = System.Threading.Interlocked.CompareExchange(
                    ref _wantFalseSinceUtcTicks, DateTime.UtcNow.Ticks, 0);
                if (since == 0) since = System.Threading.Interlocked.Read(ref _wantFalseSinceUtcTicks);
                if ((DateTime.UtcNow.Ticks - since) / TimeSpan.TicksPerMillisecond < TeardownDwellMs)
                    return; // within dwell — leave the CM2 sender + wheel flags untouched

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
            // want is true — clear any pending teardown dwell.
            System.Threading.Interlocked.Exchange(ref _wantFalseSinceUtcTicks, 0);

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
            cm2.Policy = Era.EraPolicy.For(Era.MozaWheelEra.Auto);
            cm2.PropertyResolver = _plugin.PropertyResolver.ResolveAsDouble;
            cm2.PropertyStringResolver = _plugin.PropertyResolver.ResolveAsString;
            cm2.UploadDashboard = false;
            cm2.SetDownloadEnabled(false);
            cm2.StandaloneDashboardMode = true;
            // Re-point the wire target (dev id + slot base) ONLY when Idle — never
            // mid-cold-start. A usbCm2<->busCm2 flap must not move dev 0x14<->0x12 /
            // slot-base 18<->0 under a Starting/Preamble sender (its session opens
            // would land on one dev and its tier-def on another). Stable topologies
            // set the same values (no-op); a real change re-applies on the next idle
            // (re)start, alongside the Idle-gated Rebind above.
            if (cm2.StateIsIdle)
            {
                cm2.TargetDeviceId = dev;
                cm2.StreamSlotBase = slotBase;
            }
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

            // Only kick a FRESH cold-start when the sender is genuinely Idle.
            // FramesSent stays 0 throughout the (multi-second) cold-start
            // (Starting → Preamble → Active-before-first-value-frame), so gating on
            // FramesSent==0 alone re-issued Start() on every EnsureCm2Pipeline call
            // mid-cold-start — each one superseding the in-progress start (StartInner
            // Stop). On a CM2 bridged behind a DISPLAY wheel (CS Pro + bus CM2) the two
            // senders share the wheelbase pipe, so the wheel sender's restarts pump
            // ApplyTelemetrySettings → EnsureCm2Pipeline frequently, and the CM2 sender
            // never finished cold-start → stuck Idle, CM2 dark (bundle 2026-06-17). The
            // StateIsIdle guard lets the cold-start run to completion; a sender that's
            // Starting/Preamble/Active is left alone.
            // Collision guard: never start _cm2Sender on a device the MAIN sender is
            // still Active on. A persistent main sender can carry a stale CM2 target
            // (0x14/0x12) across a plugin reload until ApplyTelemetrySettings' device-id
            // Stop moves it back to the wheel (0x17); in that window, starting here
            // would open dual sessions on the same dev/connection — the exact collision
            // the decoupling prevents. Defer: a later reconcile starts _cm2Sender once
            // the main sender has relinquished the device. (Normal case: main is on
            // 0x17 != dev, so this never blocks.)
            var mainSender = _plugin.TelemetrySender;
            bool mainHoldsThisDevice = mainSender != null && !mainSender.StateIsIdle
                && ReferenceEquals(mainSender.ConnectionRef, conn)
                && mainSender.TargetDeviceId == dev;

            // StartInProgress (not just StateIsIdle): a sender waiting out its pre-open
            // silence gate is still _state==Idle, so gating on StateIsIdle alone let this
            // ~5 s reconcile supersede the in-progress start every poll — re-stamping the
            // ~11 s gate so it never elapsed and the CM2 cold-start livelocked (CS-Pro +
            // bus CM2: _cm2Sender stuck Idle/frames=0, CM2 screen dark while its LEDs ran).
            // Leave a start that's already in flight alone so it can finish the gate once.
            if (cm2.FramesSent == 0 && cm2.StateIsIdle && !cm2.StartInProgress && !mainHoldsThisDevice)
            {
                // Fresh start: allow the saved-dashboard re-assert to fire once the
                // CM2 advertises its dashboard list (PollStatus → TickCm2DashboardReassert).
                _cm2ReassertAttempted = false;
                // Re-anchored once this sender reaches Active (the discriminator times
                // its CM1 decision from there, not from start — cold-start is long).
                _discriminateSinceUtc = DateTime.MinValue;
                // Fresh discrimination cycle — clear the param-read flag so a stale
                // CM1 answer can't fast-latch a newly-attached CM2.
                _dashParamReadAnswered = false;
                _lastCm1ProbeUtc = DateTime.MinValue;
                _cm1ProbeCount = 0;
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

        // CM1 discriminator anchor: when the dash became decidable — the _cm2Sender
        // reached Active (cold-start done), or, when no tier-def sender is running for
        // this dash at all, the first tick that saw the bus dash. The CM1 decision is
        // timed from here: a CM1 advertises no catalog AND emits no value frames, so
        // timing from FramesSent>0 (which never happens) wedged the discriminator.
        private DateTime _discriminateSinceUtc = DateTime.MinValue;
        // Set when the dash answers the group-0x0E param-read probe with a 0x8E
        // reply (MozaPlugin.OnMessageReceived) — the CM1-exclusive positive signal a
        // tier-def CM2 never produces. The SOLE basis for latching CM1.
        private volatile bool _dashParamReadAnswered;
        private DateTime _lastCm1ProbeUtc = DateTime.MinValue;
        // Probes issued in the current discrimination cycle — diagnostics only
        // (DiagnosticsTextBuilder's "Dash class:" line); never a decision input.
        private int _cm1ProbeCount;
        // Settle window after the positive 0x8E answer before latching CM1, long
        // enough that a slow tier-def CM2's catalog still arrives first and wins via
        // the CatalogCount check.
        private static readonly TimeSpan Cm1FastDecideAfter = TimeSpan.FromSeconds(5);

        /// <summary>Serial-read-thread hook: the dash answered the group-0x0E
        /// param-read probe with a 0x8E reply.</summary>
        internal void NoteDashParamReadAnswered() => _dashParamReadAnswered = true;

        // ===== Discriminator state, for the diagnostics bundle =====
        /// <summary>True once the bridged dash answered the CM1-exclusive 0x8E
        /// param-read probe.</summary>
        internal bool DashParamReadAnswered => _dashParamReadAnswered;
        /// <summary>Param-read probes issued in the current discrimination cycle.</summary>
        internal int Cm1ProbeCount => _cm1ProbeCount;
        /// <summary>How long the bridged dash has been decidable-but-unclassified, or
        /// null before the anchor is stamped (no dash, or sender still cold-starting).</summary>
        internal TimeSpan? DiscriminatingFor =>
            _discriminateSinceUtc == DateTime.MinValue
                ? (TimeSpan?)null : DateTime.UtcNow - _discriminateSinceUtc;

        /// <summary>Start (or stop) the CM1 group-0x35 driver for a confirmed CM1 dash.
        /// Mirrors <see cref="StartFsr1DriverIfNeeded"/>.</summary>
        internal void StartCm1DriverIfNeeded()
        {
            if (_plugin._cm1Driver == null) return;
            bool busDash = _detectionState.DashDetected && !_plugin.DashboardUsbConnected
                           && _plugin.Connection?.IsConnected == true;
            if (_plugin.ActiveDashTelemetryEnabled && _plugin.DashIsCm1 && busDash)
            {
                if (!_plugin._cm1Driver.IsRunning) _plugin._cm1Driver.Start();
            }
            else if (_plugin._cm1Driver.IsRunning)
            {
                _plugin._cm1Driver.Stop();
            }
        }

        /// <summary>
        /// PollStatus hook: decide whether a bus-bridged dash is a CM1 (group-0x35)
        /// rather than a tier-def CM2, using only POSITIVE evidence — by probing the
        /// CM1-exclusive group-0x0E param register ~1 Hz. Two mutually-exclusive
        /// outcomes: a tier-def catalog arrives → real CM2, drop the engagement-watchdog
        /// suppress flag; or the dash answers the probe with a 0x8E reply → latch
        /// DashIsCm1, tear down any _cm2Sender, hand off to the CM1 driver. Mere absence
        /// of a catalog NEVER latches CM1 (see body).
        ///
        /// The decision is anchored on the BUS DASH's own presence, not on a running
        /// pipeline. A live tier-def _cm2Sender only accelerates it (its CatalogCount is
        /// the "real CM2" fast path) — it is not a precondition. Gating identification on
        /// the sender made classification depend on the dash telemetry-enable, so a
        /// hub-only / wheel-less rig (which never starts that sender by default) never
        /// probed and left a CM1 permanently wearing the speculative CM2 device
        /// definition MarkDashDetected wrote (bundle MGXWJ3YH).
        /// </summary>
        internal void TickCm1Discriminator()
        {
            if (_plugin.DashIsCm1) { StartCm1DriverIfNeeded(); return; }

            // CM1 only applies to a bus-bridged dash; a USB dash (0x0025) is a real CM2.
            bool busCm2 = _detectionState.DashDetected && !_plugin.DashboardUsbConnected
                          && _plugin.Connection?.IsConnected == true;
            if (!busCm2)
            {
                // No bridged dash to classify. Drop the cycle so a detach/re-attach
                // starts clean and a stale 0x8E answer can't latch the next dash —
                // self-healing here rather than in every DashDetected=false path.
                _discriminateSinceUtc = DateTime.MinValue;
                _dashParamReadAnswered = false;
                _lastCm1ProbeUtc = DateTime.MinValue;
                _cm1ProbeCount = 0;
                return;
            }

            var cm2 = _plugin._cm2Sender;
            if (cm2 != null && cm2.Enabled)
            {
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
            }
            else if (cm2 != null && cm2.StartInProgress)
            {
                // A start waiting out its pre-open silence gate is still _state==Idle
                // (so !Enabled): let it reach Active and decide via CatalogCount rather
                // than anchoring the clock underneath an in-flight cold-start.
                return;
            }
            // else: no tier-def sender for this dash and none in flight (the dash lane is
            // disabled, or this rig has no wheel). Classify from the probe alone.

            if (_discriminateSinceUtc == DateTime.MinValue) _discriminateSinceUtc = DateTime.UtcNow;

            var elapsed = DateTime.UtcNow - _discriminateSinceUtc;

            // CM1 is latched ONLY on the POSITIVE signal: the dash answered the
            // group-0x0E param-read probe with a 0x8E reply (_dashParamReadAnswered).
            // That register interface is CM1-exclusive — PitHouse sweeps ~49 of these
            // registers on a CM1 at connect, and a tier-def CM2 implements none of
            // them (verified across the whole capture set: FSR1_CM1.pcapng answers,
            // every CM2 / wheel / base capture answers zero). So we re-probe ~1 Hz
            // until either the dash answers (→ CM1) or its tier-def catalog arrives
            // (→ CM2, handled by the CatalogCount check above).
            //
            // There is deliberately NO no-catalog timeout fallback. "No catalog yet"
            // is NOT proof of a CM1 — it equally describes a CM2 whose catalog is
            // merely slow or was starved — and that absence-based fallback is exactly
            // what mislabeled a real CM2 as a CM1 (then persisted it globally). A
            // genuine CM1 always announces itself via 0x8E, so absence-of-evidence
            // must never latch. A dash that is neither (no catalog, no 0x8E) simply
            // stays in discrimination — re-probed each tick — rather than being
            // guessed into the wrong device class.
            if (!_dashParamReadAnswered)
            {
                if ((DateTime.UtcNow - _lastCm1ProbeUtc).TotalMilliseconds >= 1000)
                {
                    _lastCm1ProbeUtc = DateTime.UtcNow;
                    _cm1ProbeCount++;
                    try { _plugin.DeviceManager.SendCm1ParamProbe(); } catch { }
                }
                return;
            }

            // Positive CM1 signal received. Latch after a short settle so a slow CM2
            // whose catalog lands inside the window still wins via the CatalogCount
            // check above.
            if (elapsed >= Cm1FastDecideAfter)
                LatchDashAsCm1("answered param-read (0x8E) — positive CM1 signal "
                    + $"(settled {Cm1FastDecideAfter.TotalSeconds:F0}s)");
        }

        /// <summary>Latch the bus-bridged dash as a CM1 for THIS session: set the
        /// in-memory flag, deploy the CM1 device definition (its own GUID/tab) and
        /// drop the speculative CM2 copy MarkDashDetected wrote before we could tell
        /// them apart (guarded against a real USB CM2), tear down the tier-def
        /// sender, and start the CM1 driver. The flag is session-only — re-derived
        /// each boot by the discriminator — so there is nothing to persist here.</summary>
        private void LatchDashAsCm1(string reason)
        {
            MozaLog.Info($"[AZOM] Bridged dash → CM1 (group-0x35): {reason}; handing off to CM1 driver");
            _plugin.DashIsCm1 = true;

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
