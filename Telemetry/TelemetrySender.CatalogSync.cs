using System;
using System.Linq;
using System.Threading;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.Sessions;
using MozaPlugin.Telemetry.TestMode;
using MozaPlugin.Telemetry.TileServer;
using Timer = System.Timers.Timer;

namespace MozaPlugin.Telemetry
{
    public partial class TelemetrySender
    {

        /// <summary>Tier-def blind retransmit rounds. Some firmwares need the
        /// tier-def re-sent a few times during cold-start before it sticks;
        /// fire each blind round at exponential backoff up to
        /// <see cref="TierDefBlindMaxRounds"/>, then stop (and free the buffer).
        ///
        /// Early-exit when the retransmit queue no longer contains any of the
        /// blind tier-def chunks — that means the wheel acked them all and
        /// re-sending would just waste bandwidth. Trace analysis 2026-05-09
        /// showed the prior catalog-activity-timestamp gate never tripped
        /// (catalog activity is timestamped before tier-def sends), so all
        /// 6 rounds always fired. Switching to ack-state lets us stop after
        /// the first round on healthy connects, eliminating the cold-start
        /// saturation event (~6 KB extra h2b per connect).</summary>
        
        /// <summary>True iff every chunk in <see cref="_tierDefBlindFrames"/>
        /// has been acked by the wheel (and therefore removed from the
        /// retransmitter queue). Frame layout per <see cref="TierDefinition-
        /// Builder.ChunkMessage"/> places session at byte 6 and seq at
        /// bytes 8-9 (LE). Returns false if any chunk is still pending.
        /// </summary>
        
        /// <summary>Re-emit the sess=0x09 prime + ConfigJson open request until
        /// the wheel device-inits 0x09 (b2h <c>7c 00 09 81 ...</c>) or the retry
        /// budget is exhausted. Cold-start fires the pair once from
        /// <see cref="PrimeAndOpenSession09"/>; if the wheel doesn't respond
        /// (Wine SerialPort R/W contention, dropped chunk, slow firmware) the
        /// configJson handshake never starts and the dashboard never renders.
        ///
        /// Guarded by <c>_sessions.GetOrCreate(0x09).DeviceInitiated</c> — once
        /// the wheel emits its device-init the retry stops naturally and never
        /// fires again for this Start cycle. Steady-state and post-switch
        /// sessions are untouched (we never close 0x09 host-side, so
        /// DeviceInitiated stays true across switches).</summary>
        
        /// <summary>
        /// When the most-recent tier-def emission had unbound channels,
        /// schedule a kind=4 dashboard-switch re-emit for the slot the
        /// wheel is currently on. Re-applying the same slot tells some
        /// firmwares to re-run their dashboard-load sequence which re-
        /// advertises the full channel catalog. Throttled via
        /// <see cref="_catalogResyncProbe"/> so a stuck case can't produce
        /// a switch storm.
        /// </summary>
        private void ScheduleCatalogResyncProbe()
        {
            long now = System.DateTime.UtcNow.Ticks;
            if (!_catalogResyncProbe.IsThrottleClear(now)) return;

            // Resolve the current slot from the wheel-reported configJsonList
            // by matching profile name. Without LastState we don't know which
            // slot the wheel thinks it's on; skip silently in that case.
            var state = _configJson.LastState;
            string? profileName = _profile?.Name;
            if (state == null || state.ConfigJsonList == null || state.ConfigJsonList.Count == 0
                || string.IsNullOrEmpty(profileName))
                return;
            int slot = -1;
            for (int i = 0; i < state.ConfigJsonList.Count; i++)
            {
                if (string.Equals(state.ConfigJsonList[i], profileName,
                    System.StringComparison.OrdinalIgnoreCase))
                { slot = i; break; }
            }
            if (slot < 0)
            {
                MozaLog.Debug(
                    $"[AZOM] Catalog re-sync probe skipped: profile '{profileName}' " +
                    "not found in wheel-reported configJsonList");
                return;
            }

            // Wheel-on-target shortcut: the wheel emits a type-04 record on
            // sess=0x02 b2h announcing its current slot at startup (BEFORE
            // any host kind=4 — observed t=11.5 s in 2026-05-14 wire trace).
            // If that matches what we'd be emitting to, the probe is pure
            // noise — the catalog incompleteness will resolve via
            // TickGrowSubscriptionIfCatalogStable's natural re-emit as the
            // wheel pushes more channel URLs. Importantly we DON'T call
            // _catalogResyncProbe.MarkFired here, so HasCatalogResyncProbeFired
            // stays false and ApplyTelemetryDashboardFromProfile's slot-match
            // path takes the "no wire action needed" branch instead of
            // pointlessly cycling the pipeline.
            if (_slotTracker.WheelReportedSlot == slot)
            {
                MozaLog.Debug(
                    $"[AZOM] Catalog re-sync probe skipped: wheel already on " +
                    $"slot {slot} ('{profileName}') per wheel-reported state");
                return;
            }

            // From here on we're committing to emit. Arm the timestamp now
            // so the min-interval throttle counts from a real emission, and
            // HasCatalogResyncProbeFired reflects a probe that actually
            // changed wheel state.
            _catalogResyncProbe.MarkFired(now);

            int slotCapture = slot;
            string nameCapture = profileName!;
            // Defer the kind=4 emission so it lands AFTER the just-sent tier-
            // def chunks finish hitting the wire. 800ms covers the largest
            // observed tier-def burst (Grids: 26 chunks * 4ms one-shot pace
            // ≈ 100ms with budget pacing absorbed).
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    System.Threading.Thread.Sleep(800);
                    if (_state == TelemetryState.Idle || !_connection.IsConnected) return;
                    SendDashboardSwitch((uint)slotCapture);
                    MozaLog.Debug(
                        $"[AZOM] Catalog re-sync probe: re-emitted kind=4 " +
                        $"slot={slotCapture} ('{nameCapture}')");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Catalog re-sync probe failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Re-emit tier-def when the wheel's channel catalog has grown since
        /// the last subscription emission. Mirrors PitHouse's growing-
        /// subscription pattern: as the wheel pushes additional URL records
        /// over the first few seconds (and again post-dashboard-switch), we
        /// re-subscribe so late-arriving channels acquire correct chIndex
        /// bindings instead of being stuck at chIndex=0.
        ///
        /// Without this, dashboard widgets bound to URLs that arrive after
        /// the initial preamble→Active tier-def render frozen at zero —
        /// observed 2026-05-09 with Grids tire channels (catalog slots 9-20
        /// arrived after preamble exit) and Mono test channels.
        ///
        /// Quiet-window gating: only re-emit when the catalog has been
        /// stable for <see cref="CatalogGrowthQuietMs"/>. Re-emitting mid-
        /// burst would race the wheel's continuing advertisements and
        /// fragment the tier-def across two emissions.
        /// </summary>
        private void TickGrowSubscriptionIfCatalogStable()
        {
            if (_state != TelemetryState.Active) return;
            if (!_connection.IsConnected) return;
            int cur = _catalogParser.Count;
            bool hotSwitchPending = _hotSwitch.IsBurstPending;
            bool catalogGrew = (cur - _catalogCountAtLastSubscription) >= CatalogGrowthMinDelta;
            if (!hotSwitchPending && !catalogGrew) return;

            int act = _catalogParser.LastActivityMs;
            int idle = act == 0
                ? int.MaxValue
                : Environment.TickCount - act;

            if (hotSwitchPending)
            {
                // Burst pacing: PitHouse fires 3-13 tier-def emissions
                // ~1s apart post-switch. Each emission rebuilds with the
                // wheel's most-recent END marker, so even if the first
                // emission echoes a stale END (wheel hadn't pushed the
                // new one yet), a later emission picks up the updated
                // value and the wheel binds then. Gating logic for first
                // emission (END handshake + fallback) and pacing for
                // subsequent emissions both live in the helper.
                if (!_hotSwitch.ShouldEmitThisTick(act, _catalogParser.LastWheelEndMarkerTickMs))
                    return;

                int now = Environment.TickCount;
                int sinceArm = now - _hotSwitch.ArmTickMs;
                bool isFirstEmission = _hotSwitch.LastEmissionTickMs == 0;
                int prev = _catalogCountAtLastSubscription;
                int emissionIdx = _hotSwitch.EmissionsSent + 1;
                MozaLog.Debug(
                    $"[AZOM] Re-applying tier-def (hot-switch burst " +
                    $"#{emissionIdx}, cap {Lifecycle.HotSwitchCoordinator.MinEmissions}-" +
                    $"{Lifecycle.HotSwitchCoordinator.MaxEmissions}): " +
                    $"catalog {prev}→{cur}, wheel END={_catalogParser.LastWheelEndMarker}, " +
                    $"sinceArm={sinceArm}ms, sinceLast={(isFirstEmission ? -1 : now - _hotSwitch.LastEmissionTickMs)}ms");
                try
                {
                    ApplySubscription(force: true);
                    _catalogCountAtLastSubscription = _catalogParser.Count;
                    // Pass bind info from the most-recent emission. Total>0
                    // means cspIdx ran (Type02); for older eras LastTierDefTotalCount
                    // stays at -1 → pass null and the coordinator keeps the
                    // legacy fixed-length cap.
                    bool? boundComplete = (_tierDefEmitter.LastTierDefTotalCount > 0)
                        ? (bool?)_tierDefEmitter.IsTierDefFullyBound
                        : null;
                    int newRemaining = _hotSwitch.MarkEmission(boundComplete);
                    if (newRemaining == 0)
                    {
                        MozaLog.Debug(
                            $"[AZOM] Hot-switch burst complete after " +
                            $"{_hotSwitch.EmissionsSent} emissions (boundComplete={boundComplete?.ToString() ?? "n/a"})");
                    }
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Tier-def re-apply (hot-switch) failed: {ex.Message}");
                }
                return;
            }

            // Pure catalog-growth path (no hot switch pending). Reuse the
            // existing flagBase: this is not a new subscription generation,
            // it's a binding refresh for late-arriving URLs within the same
            // dashboard. Advancing flagBase would invalidate the wheel's
            // existing channel bindings and silently drop subsequent value
            // frames (dashboard freezes on last good values).
            if (idle < CatalogGrowthQuietMs) return;
            int p = _catalogCountAtLastSubscription;
            MozaLog.Debug(
                $"[AZOM] Re-applying tier-def: catalog grew {p}→{cur} " +
                $"(idle {idle}ms ≥ {CatalogGrowthQuietMs}ms, reusing flagBase)");
            try
            {
                ApplySubscription(force: true, reuseFlagBase: true);
                _catalogCountAtLastSubscription = _catalogParser.Count;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Tier-def re-apply (catalog growth) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Post-switch catalog convergence tick. While armed (by a recent
        /// dashboard switch — see <see cref="ArmPostSwitchConvergence"/>),
        /// periodically sample the host's catalog signature and emit kind=4
        /// nudges to the wheel's target slot until N consecutive samples
        /// agree. Bypasses the wheel-on-target shortcut so the nudge fires
        /// even when slot already matches — some firmwares re-run their
        /// dashboard-load on every kind=4 and re-publish the catalog, which
        /// is exactly the "ask wheel to confirm we have the full set" signal
        /// we want.
        /// </summary>
        private void TickPostSwitchCatalogConvergence()
        {
            if (_state != TelemetryState.Active) return;
            if (!_connection.IsConnected) return;
            if (!_postSwitchConvergence.IsArmed) return;
            // While HOT burst is pending, defer — its emissions own the
            // wire and would race a nudge. The watcher's TickIfArmed slides
            // its sample timestamp forward in this case so the post-burst
            // gap is measured from now.
            bool busy = _hotSwitch.IsBurstPending;

            int sig = ComputeCatalogSignature();
            long now = DateTime.UtcNow.Ticks;
            int targetSlot = _postSwitchConvergence.TargetSlot;
            int matchCountBefore = _postSwitchConvergence.MatchCount;
            int nudgesSentBefore = _postSwitchConvergence.NudgesSent;

            var decision = _postSwitchConvergence.TickIfArmed(now, sig, busy);
            switch (decision)
            {
                case Lifecycle.PostSwitchCatalogConvergence.TickDecision.EmitNudge:
                    MozaLog.Debug(
                        $"[AZOM] Post-switch convergence nudge #{_postSwitchConvergence.NudgesSent}: " +
                        $"slot={targetSlot} sig=0x{sig:X8} matchStreak={_postSwitchConvergence.MatchCount}/" +
                        $"{Lifecycle.PostSwitchCatalogConvergence.StableSampleThreshold}");
                    try
                    {
                        // Bypass the wheel-on-target shortcut for the kind=4
                        // EMISSION (the nudge's job is to make the wheel
                        // re-advertise its catalog regardless of slot), but do
                        // NOT re-anchor the watchdog's slot round-trip window
                        // when the wheel already reports it's on this slot —
                        // re-arming a window that has already round-tripped
                        // just keeps a no-op timer alive.
                        SendDashboardSwitch((uint)targetSlot,
                            anchorSlotRoundTrip: _slotTracker.WheelReportedSlot != targetSlot);
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Warn($"[AZOM] Post-switch convergence nudge failed: {ex.Message}");
                    }
                    break;
                case Lifecycle.PostSwitchCatalogConvergence.TickDecision.Converged:
                    MozaLog.Debug(
                        $"[AZOM] Post-switch catalog convergence reached: slot={targetSlot} " +
                        $"after {nudgesSentBefore} nudge(s), final sig=0x{sig:X8} " +
                        $"(stable for {Lifecycle.PostSwitchCatalogConvergence.StableSampleThreshold} samples)");
                    break;
                case Lifecycle.PostSwitchCatalogConvergence.TickDecision.DeadlineExpired:
                    MozaLog.Warn(
                        $"[AZOM] Post-switch catalog convergence deadline expired " +
                        $"after {Lifecycle.PostSwitchCatalogConvergence.DeadlineMs}ms / " +
                        $"{nudgesSentBefore} nudge(s) — disarming; reactive watchdogs take over.");
                    break;
                case Lifecycle.PostSwitchCatalogConvergence.TickDecision.MaxNudgesReached:
                    MozaLog.Warn(
                        $"[AZOM] Post-switch catalog convergence nudge cap " +
                        $"({Lifecycle.PostSwitchCatalogConvergence.MaxNudges}) reached " +
                        $"with streak {matchCountBefore}/" +
                        $"{Lifecycle.PostSwitchCatalogConvergence.StableSampleThreshold} — " +
                        "disarming; catalog state may still be inconsistent.");
                    break;
                case Lifecycle.PostSwitchCatalogConvergence.TickDecision.NoAction:
                default:
                    break;
            }
        }

        /// <summary>
        /// Compute a stable hash of the host's current view of the wheel's
        /// channel catalog. Uses <see cref="ChannelCatalogParser.LiveCatalog"/>
        /// when available (only the current dashboard's URLs) and falls back
        /// to the full catalog otherwise. Order matters — a re-shuffled
        /// catalog (different idx → URL mapping) hashes differently from
        /// the original, which is exactly the change we want
        /// PostSwitchCatalogConvergence to notice.
        /// </summary>
        private int ComputeCatalogSignature()
        {
            var live = _catalogParser.LiveCatalog ?? _catalogParser.Catalog;
            if (live == null || live.Count == 0) return 0;
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < live.Count; i++)
                {
                    string s = live[i] ?? string.Empty;
                    hash = hash * 31 + s.GetHashCode();
                }
                return hash;
            }
        }

        /// <summary>
        /// Arm the post-switch catalog convergence watcher. Called from
        /// every committed dashboard switch site: host-initiated
        /// <see cref="SwitchToProfile"/>, the wheel-initiated path in
        /// <see cref="RaiseWheelInitiatedSwitch"/>, and the deferred replay
        /// in <see cref="Display.WheelSlotTracker.ReplayPendingSwitchIfReady"/>.
        /// Idempotent — a new arm cancels any in-flight cycle.
        /// </summary>
        internal void ArmPostSwitchConvergence(int slot)
        {
            _postSwitchConvergence.Arm(slot, DateTime.UtcNow.Ticks);
            MozaLog.Debug(
                $"[AZOM] Post-switch catalog convergence armed: slot={slot} " +
                $"(spacing {Lifecycle.PostSwitchCatalogConvergence.SampleIntervalMs}ms, " +
                $"threshold {Lifecycle.PostSwitchCatalogConvergence.StableSampleThreshold} samples, " +
                $"deadline {Lifecycle.PostSwitchCatalogConvergence.DeadlineMs}ms, " +
                $"max nudges {Lifecycle.PostSwitchCatalogConvergence.MaxNudges})");
        }
    }
}
