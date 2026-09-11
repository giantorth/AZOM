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

        // Background-responsiveness reconcile. Idempotent + thread-safe, so it is driven
        // from DataUpdate (responsive raise), PollStatus (release backstop for when
        // DataUpdate goes quiet on game exit), and the UI FFB-Lag-Fix toggle handler.
        //
        //  - EcoQoS execution-speed opt-out: ON only while a game is active (and a device
        //    is connected), so mid-game control writes (e.g. RSF steering lock over UDP)
        //    reach AND engage the base while SimHub is the background app. PitHouse does
        //    the same — without it the base only adopts the change on alt-tab, when the
        //    foreground lifts the Windows background throttle. Scoped to gameplay so we
        //    never hold the process un-throttled while sitting idle on the desktop.
        //  - 1 ms timer (kept honoured in the background): during active gameplay — the
        //    legacy "FFB Lag Fix" (e.g. Forza/FH6 half-fps), now always-on when it matters.
        internal void ApplyResponsivenessState()
        {
            var mgr = _responsiveness;
            if (mgr == null) return;
            bool wanted = !IsShuttingDown && _data != null && _data.IsConnected && IsGameActive;
            mgr.SetExecutionThrottleOptOut(wanted);
            mgr.SetTimerResolution(wanted);
        }

        // ES/ESX firmware RPM brightness register (0x14 0x00) is a small field, not a
        // 0..100 percentage: sweeping SimHub's 0..100 master made the bar ramp+wrap
        // ~3.3 times (issue #113 follow-up), so its period is ~30 counts. Scale the
        // master into 0..EsBrightnessMax, kept just under the ~30 wrap point so a
        // full slider lands at near-full brightness without tipping into a wrap.
        private const int EsBrightnessMax = 29;
        // SimHub's LED master brightness can churn at startup (day/night brightness-mode
        // settling as the profile loads); writing each transient to the EEPROM register
        // flickered the bar. Commit a value only after it has held steady this many
        // 250 ms ticks (~0.75 s) — absorbs the startup churn and a slider drag alike,
        // while still landing on the final value since this timer always ticks.
        private const int EsBrightnessSettleTicks = 3;

        /// <summary>
        /// Write the ES/ESX master LED brightness, ticked from the steady 250 ms poll
        /// timer. Old-protocol wheels dim ONLY via the legacy firmware brightness
        /// register (<c>wheel-old-rpm-brightness</c>, <c>0x14 0x00</c>) — their live RPM
        /// path sends only an on/off bitmask, so per-frame colour scaling can't dim
        /// them. The LED thread publishes the live 0..100 slider value into
        /// <see cref="WheelLedMasterBrightnessRaw"/>; we scale it into the firmware's
        /// small brightness range and write it whenever the scaled value changes. This
        /// runs here (not on Display()/DataUpdate) because both go quiet at idle, which
        /// otherwise lagged the applied value a whole gesture behind the slider. The
        /// 250 ms tick naturally caps a drag to ~4 writes/sec — no separate debounce
        /// needed. Direct device-manager write — thread-safe, and deliberately NOT via
        /// HardwareApplier's data-thread-only cfg cache.
        /// </summary>
        private void TickEsMasterBrightness()
        {
            if (!IsOldWheelDetected || !_connection.IsConnected) return;
            int raw = WheelLedMasterBrightnessRaw;
            if (raw < 0) return;
            var model = WheelModelInfo;
            if (model == null || model.RpmLedCount <= 0) return;

            int wire = (int)Math.Round(raw * EsBrightnessMax / 100.0);
            if (wire < 0) wire = 0; else if (wire > EsBrightnessMax) wire = EsBrightnessMax;

            // Settle: require the scaled value to hold across several ticks before
            // committing, so a startup transient can't flick the register through a
            // burst of intermediate values.
            if (wire != _esMasterBriCandidate)
            {
                _esMasterBriCandidate = wire;
                _esMasterBriStableTicks = 0;
                return;
            }
            if (_esMasterBriStableTicks < EsBrightnessSettleTicks)
            {
                _esMasterBriStableTicks++;
                return;
            }
            if (wire == _esMasterBriApplied) return;

            _esMasterBriApplied = wire;
            // Keep MasterCompensated (LED thread) on the 0..100 master scale.
            WheelLedMasterBrightness = raw;
            _data.WheelESRpmBrightness = wire;
            _deviceManager.WriteSetting("wheel-old-rpm-brightness", wire);
        }

        private void ReconnectTick()
        {
            if (!_connection.IsConnected)
                _connectionCoordinator?.TryConnect();
            else
            {
                // Primary already latched. Two complementary self-heals:
                //  1. base→hub: the base has no wheel but one answered on
                //     the hub (broken base) → run the wheel pipeline over
                //     the hub. Runs FIRST and sets _wheellessBasePort so (2)
                //     can't immediately undo it.
                //  2. hub→base: the primary grabbed a hub before the
                //     wheelbase enumerated (wrong latch order) → hand it
                //     back to the base. Runs before TryConnectHub so the
                //     freed hub port is claimed by the hub manager this tick.
                _connectionCoordinator?.MigratePrimaryToHubIfNeeded();
                _connectionCoordinator?.MigratePrimaryToWheelbaseIfNeeded();
            }
            // Dedicated lanes run only against an authoritative device source —
            // the Windows registry, or Linux sysfs under Wine/Proton. Without one
            // each lane would fall back to a blind sweep of every wine COM
            // symlink, which locks up SimHub and opens other vendors' hardware.
            // (This used to test the registry alone, which is always empty on
            // Wine — it silently disabled every lane below on Linux.)
            bool deviceSourceLive =
                Protocol.MozaPortDiscovery.Instance.IsAuthoritative;
            if (deviceSourceLive && !_ab9Manager.IsConnected)
                _connectionCoordinator?.TryConnectAb9();

            // Standalone-USB CM2 on its own port (0x0025) — same gate.
            if (deviceSourceLive && !_dashboardManager.IsConnected)
                _connectionCoordinator?.TryConnectDashboard();

            // Universal Hub on its own port (0x0020) — enumeration-only, same
            // gate. The hub-only case is handled by the primary
            // (BaseAndHub) connection; this dedicated connection only takes
            // a hub the primary didn't claim (i.e. a base is the primary),
            // and no-ops when the hub port is already held by the primary.
            if (deviceSourceLive && !_hubManager.IsConnected)
                _connectionCoordinator?.TryConnectHub();

            // Dedicated base-aux pipe — ONLY after a DELIBERATE base→hub
            // migration (broken base), identified by the _wheellessBasePort
            // latch. Must NOT gate on PrimaryBoundToHub alone: the primary
            // can be TRANSIENTLY on the hub during wrong-latch-order cold
            // start (hub enumerated before the base). If base-aux grabbed
            // the base then, it would hold the port and permanently block
            // MigratePrimaryToWheelbaseIfNeeded from reclaiming it — leaving
            // the primary stuck on the hub (wheel still works via the hub,
            // but the port is mislabeled "Wheelbase"). The latch is set only
            // by a real migration, so a transient hub latch never trips it.
            if (deviceSourceLive && _connectionCoordinator?.WheellessBasePort != null && !_baseManager.IsConnected)
                _connectionCoordinator?.TryConnectBase();

            // Slice I: reconnect-timer mBooster Refresh re-enabled.
            try { _mboosterRegistry?.Refresh(); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Refresh: {ex.Message}"); }
            // Routed-mBooster identity probes that lost their reply get a
            // re-burst on the same cadence (capped — see the method).
            try { NudgeRoutedMBoosterProbes(); }
            catch (Exception ex) { MozaLog.Debug($"[AZOM/mBooster] Routed probe nudge: {ex.Message}"); }

            // Standalone pedals/handbrake on their own ports (enumeration-
            // only, same device-source gate as the other dedicated lanes).
            if (deviceSourceLive)
            {
                try { _peripheralRegistry?.Refresh(); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM] Standalone peripheral refresh: {ex.Message}"); }
            }
        }

        // Background feed for the Base-tab temperature graph's rolling history —
        // fires on _tempHistoryTimer regardless of whether the settings panel is
        // open. Cheap: reads three volatile ints + one bool into a ring buffer.
        private void SampleTemperatureHistory(object sender, ElapsedEventArgs e)
        {
            if (IsShuttingDown) return;
            var data = _data;
            if (data == null) return;
            try { _tempHistory.Record(data.McuTemp, data.MosfetTemp, data.MotorTemp, data.IsBaseConnected); }
            catch (Exception ex) { MozaLog.Warn($"[AZOM] Temp-history sample failed: {ex.Message}"); }
        }

        // Background feed for live torque: the Base-tab graph's history ring and
        // the whole AZOM.CurrentTorque* / MaxTorque family. Ungated — the
        // properties are read by dashboards with the settings panel shut, and one
        // uniform rate is also what keeps the ring's time axis honest.
        //
        // Costs one 8-byte read + 8-byte reply per tick: ~80 B/s at 5 Hz, ~0.7% of
        // the 11520 B/s ceiling. Runs off the WPF dispatcher; the UI only ever
        // renders a snapshot on its existing 500 ms tick.
        private void SampleTorqueHistory(object sender, ElapsedEventArgs e)
        {
            if (IsShuttingDown) return;
            var data = _data;
            if (data == null) return;

            try
            {
                if (!data.IsBaseConnected)
                {
                    _torqueHistory.Record(0.0);
                    return;
                }
                // Route to whichever pipe owns the base, the same way
                // HardwareApplier.BaseManager does: after a base→hub migration the
                // primary is hub-bound and a read sent there never reaches the base.
                var dm = DetectionState.BaseOwner ?? _deviceManager;
                // Untracked: a dropped reply costs one sample, where a tracked
                // read re-registered at this rate would stack retransmits on its
                // own backoff.
                dm?.ReadSettingUntracked("base-live-torque");
                _torqueHistory.Record(data.LiveTorqueNm);
            }
            catch (Exception ex) { MozaLog.Warn($"[AZOM] Torque-history sample failed: {ex.Message}"); }
        }

        // Attempts spent on the base-fw-version retry burst before giving up.
        // ~5 × the 5 s poll interval ≈ 25 s past base detect, at three small
        // frames per round — enough to ride out a busy cold-start, cheap enough
        // that a genuinely mute base costs 15 frames total and then goes quiet.
        private const int BaseFwVersionProbeRetryLimit = 5;

        /// <summary>
        /// Re-ask for the numeric base firmware while it is still unknown. The
        /// detect-time burst in <see cref="DeviceProber.SendBaseFwVersionProbes"/>
        /// rides the <c>BaseAmbientProbed</c> latch and fires exactly once, so a
        /// base that drops all three replies would leave
        /// <see cref="MozaData.BaseSupportsLfe"/> false — no LFE effects, no LFE
        /// haptics device, no 10-band EQ — for the whole session with nothing
        /// re-asking. Bundle 65HZBQJT is that failure on an R12.
        /// </summary>
        private void TickBaseFwVersionRetry()
        {
            if (!DetectionState.BaseDetected) return;
            // The poll timer starts before Init finishes wiring _data/_deviceProber.
            var data = _data;
            if (data == null || data.BaseFwVersion != 0) return;
            if (DetectionState.BaseFwVersionProbeRetries >= BaseFwVersionProbeRetryLimit) return;
            // Ask on whichever pipe detected the base (primary normally, base-aux
            // after a base→hub migration) — HardwareApplier routes base writes the
            // same way.
            var owner = DetectionState.BaseOwner ?? _deviceManager;
            if (owner == null || !owner.IsConnected) return;

            int attempt = Interlocked.Increment(ref DetectionState.BaseFwVersionProbeRetries);
            _deviceProber?.SendBaseFwVersionProbes(owner);
            MozaLog.Debug(
                $"[AZOM] Base firmware still unknown — re-probing " +
                $"({attempt}/{BaseFwVersionProbeRetryLimit})");
            if (attempt >= BaseFwVersionProbeRetryLimit)
                MozaLog.Info(
                    "[AZOM] Base firmware unanswered after " +
                    $"{BaseFwVersionProbeRetryLimit} retries — LFE effects and the " +
                    "10-band EQ stay disabled (needs >= 1.2.10.10)");
        }

        private int _pollTickInProgress;
        private int _retryTickInProgress;

        private void PollStatus(object sender, ElapsedEventArgs e)
        {
            if (IsShuttingDown) return;
            // AutoReset timer: a slow tick (Disconnect() joins its I/O threads,
            // EnsureCm2Pipeline starts/stops a sender) must not overlap the next
            // one — the miss counter and model-recheck tick are per-tick state.
            if (System.Threading.Interlocked.CompareExchange(ref _pollTickInProgress, 1, 0) != 0) return;
            // System.Timers.Timer swallows exceptions silently — without this
            // wrapper a deterministic throw halts the whole 5 s detection state
            // machine with no log evidence.
            try { PollStatusCore(); }
            catch (Exception ex) { MozaLog.Warn($"[AZOM] PollStatus tick failed: {ex}"); }
            finally { System.Threading.Interlocked.Exchange(ref _pollTickInProgress, 0); }
        }

        private void PollStatusCore()
        {
            // Responsiveness backstop: SimHub stops calling DataUpdate when a game
            // exits, so the timer raise + EcoQoS opt-out can only be released here once
            // IsGameActive goes false via feed-staleness (~3 s) — within one 5 s poll.
            ApplyResponsivenessState();

            // The dedicated hub pipe is polled independently of the primary
            // (base) connection — a Universal Hub can be present with the base
            // unplugged, or vice versa. No-op when the hub isn't connected.
            _connectionCoordinator?.PollHubPeripherals();
            // Likewise the dedicated base-aux pipe (post base→hub migration) is
            // polled independently for base temps/state. No-op unless connected.
            _connectionCoordinator?.PollBaseAux();

            TickBaseFwVersionRetry();

            // CM2 / dashboard-pipeline reconcile runs REGARDLESS of the wheelbase
            // connection. DECOUPLED: a standalone-USB CM2 is driven by the dedicated
            // _cm2Sender on its own pipe with no wheelbase, so gating these behind the
            // wheelbase guard below would never service it. EnsureCm2Pipeline is the
            // periodic reconcile (start when a CM2 appears, reconfigure, and complete
            // the debounced teardown when the CM2 is gone) — it MUST run on a timer for
            // the teardown dwell to elapse; the event-driven apply/detect callers alone
            // can't advance it. Each call is a no-op / idempotent when nothing changed
            // (the Start gate prevents restart churn; the CM1 discriminator early-returns
            // for a non-bus CM2). For a bus CM2 the wheelbase IS connected so the guard
            // below passes anyway — running them here only adds the USB-only case.
            _dualDisplay?.EnsureCm2Pipeline();
            _dashboardBindingCoordinator?.TickPendingDashboardRetry();
            _dualDisplay?.TickCm2DashboardReassert();
            _dualDisplay?.TickCm1Discriminator();
            // FSR1 self-heal: _fsr1Driver is per-instance (unlike the persistent
            // tier-def sender) and is only started via the event-driven
            // StartTelemetryIfReady, which a persistent-wire reload skips for an
            // already-known wheel (LastKnownWheelModel still set → DeviceProber
            // first-sight block gated out) — orphaning the group-0x42 push into a
            // dark screen with nothing to restart it. Reconcile it here like the
            // CM2/CM1 drivers above. Idempotent: no-op unless a stopped FSR1.
            _dualDisplay?.StartFsr1DriverIfNeeded();

            if (!_connection.IsConnected) return;

            // Auto-standby backstop: enters standby when idle (covers the case
            // where SimHub stops calling DataUpdate with no game running) and
            // re-applies the desired work mode after a base reconnect.
            _standby?.Apply();

            // Hot-swap detection: track whether the locked wheel is still responding
            // and periodically verify the model name hasn't changed.
            if (DetectionState.NewWheelDetected || DetectionState.OldWheelDetected)
            {
                if (_deviceManager.WheelRespondedSinceLastPoll)
                {
                    DetectionState.ResetWheelPollMisses();
                }
                else
                {
                    int misses = DetectionState.IncrementWheelPollMisses();
                    if (misses >= WheelMissThreshold)
                    {
                        ResetWheelDetection(
                            $"Wheel on ID {_deviceManager.WheelDeviceId} not responding " +
                            $"({misses} misses) — resetting for hot-swap");
                    }
                }
                _deviceManager.ResetWheelResponseFlag();

                // Active wheel maintenance. The 0x00 presence poll and the 1-byte
                // 0x43 keepalive (below) replicate PitHouse's idle footprint to
                // 0x17. The group-0x0E param poll is deliberately NOT sent: group
                // 0x0E is the param-manager channel (param_manage.c) itself, and
                // poking it on the wheel provokes the Table-8 "Failed to Read
                // Parameter" storm. On the matching R9 + bare-"CS" rig PitHouse
                // never sends 0e→0x17 (verified cs v2(1).pcapng — it polls 0x0E
                // only on the base, 0e12/0e13). Liveness is driven by the 0x00
                // presence ACK (OnPresenceProbeAck → MarkWheelAlive) plus the
                // wheel's continuous unsolicited 0x0e logs.
                _deviceManager.SendPresenceProbe(MozaProtocol.DeviceWheel);

                // 1-byte 0x43 keepalive — sent to new-protocol wheels regardless of
                // display capability. PitHouse sends this exact frame to the
                // screenless R5 (and to 0x14/0x15) and the wheel stays healthy; the
                // documented screenless hazard is the 11-frame display PROBE
                // (SendDisplayProbe), NOT this keepalive. FSR1 streams its own via
                // Fsr1DisplayDriver, so exclude it to avoid a double-send. Old ES
                // wheels (id 0x13) are excluded — PitHouse never keepalives them.
                if (DetectionState.NewWheelDetected && !IsFsr1DisplayWheel)
                    _deviceManager.SendWheelKeepalive();

                // Param-storm self-protection: while the wheel is actively logging
                // "Failed to Read/Write Parameter", every identity/config read we add
                // is another failure for a param manager that is already drowning —
                // the documented CS-wheel hazard (wheel-0x17.md § Table 8 storm), and
                // the FSR1 wedge signature (2026-08 bundles: identity reads unanswered,
                // failures advancing table-by-table while we re-polled ~1 Hz). Suspend
                // identity rechecks + hot-swap ID probes until the storm clears; the
                // 0x00 presence poll and 0x43 keepalive above stay on (PitHouse parity,
                // and OnPresenceProbeAck/unsolicited 0x0E logs keep liveness alive).
                bool paramStorm = _firmwareDebugLog.ParamStormActive;
                if (paramStorm && !_paramStormLogged)
                {
                    _paramStormLogged = true;
                    MozaLog.Warn("[AZOM] Wheel param-store storm detected — suspending identity/config "
                        + "rechecks while it persists (wheel may need a power-cycle to recover).");
                }
                else if (!paramStorm && _paramStormLogged)
                {
                    _paramStormLogged = false;
                    MozaLog.Info("[AZOM] Wheel param-store storm cleared — resuming identity rechecks.");
                }

                // wheel-model-name recheck: triggers initial identity resolution
                // and hot-swap model-change detection. Every tick while unresolved
                // (fast identity, as before); once resolved the presence ACK is the
                // heartbeat so we recheck only every WheelModelRecheckInterval ticks
                // (kept below WheelMissThreshold so the response still resets the
                // miss counter even if 0x00/0x0e fall silent).
                // FSR1: once identity has resolved, STOP re-polling it. This firmware
                // never answers the model-name read again after initial detection
                // (documented above at MarkWheelAlive) — every recheck is a guaranteed
                // unanswered identity read, i.e. one more param-manager read failure,
                // ~1/s forever (2026-08 wedge bundles: 60 unanswered 07/01 reads per
                // minute). Liveness for this wheel comes from the presence ACK and its
                // continuous unsolicited 0x0E logs; a genuine unplug still trips the
                // miss watchdog, and a rim swap re-resolves identity on re-detect.
                bool fsr1IdentitySettled = IsFsr1DisplayWheel && WheelModelInfo != null;
                if (!paramStorm && !fsr1IdentitySettled
                    && (WheelModelInfo == null
                        || ++_wheelModelRecheckTick >= WheelModelRecheckInterval))
                {
                    _wheelModelRecheckTick = 0;
                    _deviceManager.ReadSetting("wheel-model-name");
                    // ES wheels carry their real model at module id 0x18 (the
                    // locked-id read above returns the base/motor name on ES), so
                    // re-read it on the same cadence — a rim swap to a different
                    // model is then caught by model-name hot-swap. No-op on a non-ES
                    // old wheel (0x18 silent); modern wheels skip this branch.
                    if (DetectionState.OldWheelDetected)
                        _deviceManager.ReadSetting("es-wheel-model-name");
                }

                // Probe other wheel IDs for hot-swap detection.
                // Handles ES → new-protocol case where the base keeps responding
                // on the locked ID (19) so miss counter never fires. Storm-gated:
                // these are identity reads too (see the recheck gate above).
                if (!paramStorm)
                    _deviceManager.ProbeOtherWheelIds();
            }

            // Base temps/state are dev-0x13 reads the base main controller answers.
            // A hub-bound primary (post base→hub migration) can't reach the base
            // over the hub — the dedicated base-aux pipe polls them instead (see
            // PollBaseAux). A base-bound primary keeps polling them as before.
            if (!(_connectionCoordinator?.PrimaryBoundToHub ?? false))
                _deviceManager.ReadSettings(StatusPollCommands);

            // Device detection probes — only sent until each device is found.
            //
            // For dash / handbrake / pedals we now use PitHouse-style empty
            // presence probes (`0x00 dev=<id>` → `0x80 dev=<swap>`). The prior
            // approach re-issued the first settings read (`dash-rpm-indicator-mode`
            // etc.) every PollStatus tick; with no device attached the read
            // never got a response and PendingResponseTracker amplified each
            // probe by its 3-retry budget (200/400/800 ms backoff). Net result:
            // 9 frames/tick of pure noise per absent sub-device. Empty probes
            // are NOT tracked, so absent devices cost exactly one 5-byte frame
            // per tick. ACK handling lives in OnPresenceProbeAck (called from
            // OnMessageReceived) which flips DetectionState and kicks off the
            // existing per-device settings read batch.
            //
            // Hub stays on the cmd-specific read path because hub shares
            // device id 0x12 with the wheelbase main controller — an empty
            // probe to 0x12 always ACKs from the base and can't distinguish.
            if (!DetectionState.NewWheelDetected && !DetectionState.OldWheelDetected)
                _deviceManager.ProbeWheelDetection();
            if (!DetectionState.DashDetected)
                _deviceManager.SendPresenceProbe(MozaProtocol.DeviceDash);
            // Also re-probe when the flag rode a persistent-wire reload but End()
            // cleared the owner: the ACK is the only thing that re-points it (and,
            // for pedals, re-arms the routed-mBooster probe — see MarkPedalsDetected).
            // Closes as soon as an owner is recorded. HGP/SGP need no equivalent —
            // their gate below is already owner-keyed.
            if (!DetectionState.HandbrakeDetected || DetectionState.HandbrakeOwner == null)
                _deviceManager.SendPresenceProbe(MozaProtocol.DeviceHandbrake);
            if (!DetectionState.PedalsDetected || DetectionState.PedalsOwner == null)
                _deviceManager.SendPresenceProbe(MozaProtocol.DevicePedals);
            // HGP/SGP attached to the base's peripheral port (dev 0x1A). A shifter on
            // its own USB port is found by MozaStandalonePeripheralRegistry instead;
            // one behind the Universal Hub answers on the dedicated hub pipe. Gate on
            // THIS pipe's slot being unresolved — a standalone HGP/SGP elsewhere must
            // not suppress the base-slot probe (both can be attached at once).
            if (DetectionState.ShifterModelForOwner(_deviceManager) == Devices.ShifterModelKind.Unknown)
                _deviceManager.SendPresenceProbe(MozaProtocol.DeviceHPattern);
            // No hub-port-power poll on the wheelbase connection — a Universal Hub
            // is found by the dedicated hub connection on its own port, never by
            // sending hub commands to a device we already know is a wheelbase.

            // Re-probe display sub-device until fully identified — initial probe
            // can race power-up and return only partial identity. Skip for wheels
            // we already know have no display: the probe sends 11 frames on the
            // dashboard session group (0x43 dev=0x17), and screenless wheels
            // (CS V2.1 / KS / GS V2P / TSW / RS V2 / "CS") may interpret those as
            // dashboard-pipeline traffic and stop servicing settings reads.
            // For resolved-but-unknown wheels (Default model, HasDisplay==null)
            // the re-probe still runs so the UI can light the dashboard section.
            //
            // WheelModelInfo MUST be resolved (non-null) before probing: a bare
            // `WheelModelInfo?.HasDisplay != false` reads `null != false` == true
            // when WheelModelInfo is null, so the probe fired during the
            // unresolved window — which ResetWheelDetection re-opens every time it
            // nulls WheelModelInfo. On a screenless CS V2 that intermittently
            // misses the model-name poll, the model never re-resolves, the probe
            // re-fires each PollStatus tick, and its 0x43 burst drives the wheel
            // into the Table-8 read-fail storm that makes it miss the next poll —
            // a self-sustaining detect→storm→teardown loop. Gate on a resolved
            // model so the unresolved window can't poke a wheel we can't yet
            // confirm has a display.
            if (DetectionState.NewWheelDetected
                && !IsDisplayDetected
                && WheelModelInfo != null
                && WheelModelInfo.HasDisplay != false)
                _deviceManager.SendDisplayProbe();

            // Display-boot wedge watchdog. The W17 (CS Pro) takes ~20 s for its
            // display sub-device to come up after a hot-attach; KS Pro and other
            // displayed wheels are similar. StartTelemetryIfReady's
            // display-detected gate (DashboardBindingCoordinator.cs) defers
            // pipeline start until the display probe answers — correct under
            // normal conditions, but if the display sub-device is genuinely
            // stuck (firmware wedge, mid-USB-enumeration glitch) the gate would
            // sit forever and the user has no signal that anything's wrong.
            // After DisplayWedgeTimeoutMs of waiting we treat the wheel as
            // wedged and force a serial disconnect; the 5 s reconnect timer
            // reopens the port, which gives the wheel's USB stack a chance to
            // re-enumerate and the display a fresh boot. One-shot per attach:
            // DisplayWedgeRecoveryFired stays set until the next successful
            // display detection (cleared in DeviceProber's display-model-name
            // case) or a manual Connection-enable toggle, so a permanently
            // wedged display can't loop the connection.
            // Gated to NewWheelDetected only: old-protocol (ES) wheels never
            // resolve WheelModelInfo (the wheel-model-name resolve is gated on
            // NewWheelDetected because dev 0x13's model name is the base's, not
            // the rim's), so WheelModelInfo stays null and `?.HasDisplay != false`
            // reads null!=false == true — which would otherwise force a one-shot
            // disconnect on a screenless ES wheel that has no display sub-device
            // to wait for. Old wheels have no display; exclude them outright.
            const long DisplayWedgeTimeoutMs = 60_000;
            long wheelDetectedTicks = WheelDetectedUtcTicks;
            if (!DisplayWedgeRecoveryFired
                && DetectionState.NewWheelDetected
                && WheelModelInfo?.HasDisplay != false
                && !IsDisplayDetected
                && wheelDetectedTicks != 0)
            {
                long elapsedMs = (DateTime.UtcNow.Ticks - wheelDetectedTicks)
                    / TimeSpan.TicksPerMillisecond;
                if (elapsedMs >= DisplayWedgeTimeoutMs)
                {
                    DisplayWedgeRecoveryFired = true;
                    var hasDisplayStr = WheelModelInfo?.HasDisplay?.ToString() ?? "unknown";
                    MozaLog.Warn(
                        $"[AZOM] Display sub-device wedge: wheel detected " +
                        $"{elapsedMs}ms ago (HasDisplay={hasDisplayStr}) but " +
                        "display has not responded. Forcing serial disconnect — " +
                        "reconnect timer (5 s) will reopen the port and give the " +
                        "wheel's USB stack a chance to re-enumerate. " +
                        "If this recurs, the display firmware is likely stuck; " +
                        "physically detaching the wheel and reattaching is the next step.");
                    try { _connection?.Disconnect(); } catch { }
                }
            }

            // Group 3 (knob ring) brightness read once after group detected +
            // model resolved. The per-LED ring COLORS (wheel-knob-bg-color{N})
            // are not polled here — they're seeded once at detect in
            // DeviceProber.BuildNewWheelLedReadCommands and re-read on Knobs-tab
            // activation, same policy as the RPM and Button colors. Brightness is
            // a single non-color status read, kept here as part of capability
            // discovery.
            if (!DetectionState.Group3ColorsRead && DetectionState.NewWheelDetected && IsWheelLedGroupPresent(3))
            {
                var model = WheelModelInfo;
                if (model?.KnobRingLeds != null && model.KnobRingLedTotal > 0)
                {
                    DetectionState.Group3ColorsRead = true;
                    _deviceManager.ReadSetting("wheel-knob-brightness");
                    MozaLog.Debug("[AZOM] Read knob ring brightness");
                }
            }

            // Poll hub port status while hub is connected (read-only, no settings to save).
            // When the primary is itself bound to a hub (hub-only setup) and the hub
            // hasn't been detected yet, keep issuing the hub-port1-power presence read
            // — the connect-time read is tracked/retried, but this also recovers if the
            // hub re-enumerates without a full reconnect. Once detected, poll the full
            // port-power set so the Hub-tab indicators stay current. Mirrors
            // PollHubPeripherals' trigger/full-set split for the dedicated hub pipe.
            if (DetectionState.HubDetected)
                _deviceManager.ReadSettings(DeviceProber.HubReadCommands);
            else if (_connectionCoordinator?.PrimaryBoundToHub == true)
                _deviceManager.ReadSetting("hub-port1-power");
        }

        internal int _unmatched;   // Interlocked (two dispatch threads increment it)
    }
}
