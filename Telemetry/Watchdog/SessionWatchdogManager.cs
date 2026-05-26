using System;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry.Watchdog
{
    /// <summary>
    /// Five wheel-session watchdog loops (sess=0x09 retry, sess=0x01 engagement,
    /// sess=0x02 engagement, configJson gap escalation, configJson stuck-state).
    /// Budget exhaustion parks or escalates to <see cref="TelemetrySender.RestartForSwitch"/>.
    /// </summary>
    internal sealed class SessionWatchdogManager
    {
        private readonly TelemetrySender _sender;

        // ── sess=0x09 retry ───────────────────────────────────────────────
        private int _s09RetryRounds;
        private int _s09RetryLastTickCount;
        private static readonly int[] S09BackoffMs =
            { 250, 500, 1000, 2000, 3000, 5000, 7000, 10000, 12000, 15000 };
        private const int S09RetryMaxRounds = 10;

        // ── sess=0x02 engagement watchdog ─────────────────────────────────
        private long _session02FirstInboundUtcTicks;
        private int _activeStateEnteredTickCount;
        private int _s02ReArmRounds;
        private int _s02ReArmLastTickCount;
        private static readonly int[] S02ReArmBackoffMs =
            { 3_000, 5_000, 7_000, 10_000, 15_000 };
        private const int S02ReArmMaxRounds = 5;
        private const int S02InitialGraceMs = 3_000;

        // ── sess=0x01 (mgmt) engagement watchdog ──────────────────────────
        // Symmetric to the sess=0x02 watchdog. ProbeAndOpenSessions emits
        // exactly one SessionOpen 0x01 with a 500 ms ack budget; on the
        // CS-Pro / Universal Hub the wheel takes 12-15 s after device-lock
        // to start acking session-control frames (CS-Pro-0.9.3-dev capture
        // 2026-05-20: first `c3 71 fc 00 02` at 11:33:02, ~14 s after the
        // host's `SessionOpen 0x01` at 11:32:48; zero `c3 71 fc 00 01` in
        // the entire 2 MB trace). The single-shot open always misses on
        // this hardware. Without retry the plugin then emits tier-defs on
        // a session the wheel never accepted — wheel never publishes its
        // catalog, dashboard renders nothing, user reports "didn't connect".
        // Engagement signal = wheel acked sess=0x01 (`fc:00 01`) OR pushed
        // any 7c:00 01 data chunk; either is sufficient.
        //
        // Grace is set wide (20 s): healthy wheels on this firmware push
        // first sess=0x01 chunk ~4 s after Active (verified W17 capture
        // 2026-05-20 17:55: Active at 17:55:05.015, first sess=0x01
        // inbound at 17:55:09.039). The CS-Pro pathology is total silence
        // for the full 14 s window after the first host frame — at 10 s
        // grace the re-arm landed inside that silence and its
        // close+open+ApplySubscription frequently stomped the wheel's
        // in-flight engagement just as it woke (user report 2026-05-20:
        // "doesn't connect with display after start with update, often
        // takes another simhub restart"). 20 s pushes the first re-arm
        // safely past the wheel's wake-up window so we only re-emit when
        // the wheel has genuinely failed to engage on its own. Slower
        // (>20 s) hardware falls into the re-arm path as before.
        private long _session01EngagedUtcTicks;
        private int _s01ReArmRounds;
        private int _s01ReArmLastTickCount;
        private static readonly int[] S01ReArmBackoffMs =
            { 5_000, 7_000, 10_000, 15_000, 20_000 };
        private const int S01ReArmMaxRounds = 5;
        private const int S01InitialGraceMs = 20_000;

        // ── wheel-initiated CLOSE storm tracking ──────────────────────────
        // The wheel may emit `c3 71 7c 00 <sess> 00 <seq>` (type=0x00, close)
        // on a session the HOST opened. Observed in 2026-05-26
        // moza-wire-...-043633: wheel emitted CLOSE on sess=0x01 every ~1 s
        // for 15+ seconds while the host kept pushing tier-def chunks to
        // sess=0x01. The CLOSE is the wheel saying "I am not in the state
        // you think I am" — usually after firmware confusion from rapid
        // tier-def re-emissions. Tracked here so we can (a) surface it
        // loudly via a single WARN per storm rather than silently absorbing
        // it, and (b) escalate to a clean recovery path when the storm
        // crosses a threshold.
        //
        // Per-session sliding-window state. Indexed by session id 0x01..0x09;
        // unused slots stay at default(0).
        private const int CloseStormSessionMax = 16; // indexes 0x00..0x0F covered
        private readonly int[] _closeStormFirstTickMs = new int[CloseStormSessionMax];
        private readonly int[] _closeStormCount = new int[CloseStormSessionMax];
        private readonly int[] _closeStormLastWarnTickMs = new int[CloseStormSessionMax];
        // Window in ms used to count consecutive closes as "a storm".
        // Wheel sends keepalive END markers on sess=0x02 once per second in
        // healthy operation, which is NOT a session close — those go through
        // a different path. CLOSEs (type=0x00) at this rate represent
        // session rejection, which the wheel only does when it has been
        // bombarded; 5 s catches every real instance we've observed without
        // tagging single occasional closes.
        private const int CloseStormWindowMs = 5_000;
        // Single close inside the window = quiet log + start window.
        // 3 closes = WARN log explaining the symptom; one WARN per storm.
        // (One-per-storm dedup uses _closeStormLastWarnTickMs.)
        private const int CloseStormWarnThreshold = 3;
        // Min gap between WARN re-emissions for the same session, so a long-
        // running storm doesn't spam the log on every additional close.
        private const int CloseStormWarnCooldownMs = 10_000;
        // Escalation to full sender restart: if the storm crosses this count
        // inside CloseStormRestartWindowMs, the engagement watchdog clearly
        // can't recover us (it would have re-armed and engaged sess=0x01
        // before we accumulated this many closes). At the wheel's observed
        // ~1 CLOSE/s cadence this means 10 closes in 30 s ≈ full minute of
        // sustained rejection. Higher threshold means we don't restart on a
        // brief firmware hiccup; window is wide enough to count steady-state
        // 1 Hz storms without timing out.
        private const int CloseStormRestartThreshold = 10;
        private const int CloseStormRestartWindowMs = 30_000;
        // One-shot guard per session so we don't request restart repeatedly
        // while the requested restart is in flight (RequestRestart is async).
        // Reset by Reset() at restart boundaries so a subsequent storm can
        // trigger another restart if needed.
        private readonly int[] _closeStormRestartRequested = new int[CloseStormSessionMax];

        // ── configJson gap / stuck-state ──────────────────────────────────
        private int _configJsonGapCount;
        private long _configJsonLastChunkUtcTicks;
        private long _configJsonLastEscalationUtcTicks;
        private long _configJsonLastPrimeRetryUtcTicks;
        // Hard cap on the tick-path prime+open-request retries. HandleConfigJsonGap
        // already escalates to RequestRestart at ConfigJsonGapRestartAt because
        // it counts inbound-gap events. The TICK path (TickConfigJsonGapEscalation)
        // fires when no chunks at all are flowing — exactly the case where
        // HandleConfigJsonGap never gets a chance to increment its counter.
        // Without this cap, the tick path would emit prime+open every cooldown
        // window indefinitely. Reset on real inbound progress (chunk arrival)
        // via NoteConfigJsonChunkArrived, and on Reset().
        private int _configJsonGapTickEscalations;
        private const int ConfigJsonGapTickEscalationCap = 3;
        // Passive wait window before active prime+open-request: wheel's
        // outstanding-ack timer (~1.3 s) gets headroom before host escalates.
        private const int ConfigJsonGapPassiveWaitMs = 5_000;
        private const int ConfigJsonGapPrimeRetryAt = 1;
        private const int ConfigJsonGapRestartAt = 4;
        // Min gap between escalations so a chunk-drop storm can't cycle restart
        // faster than the wheel's 11 s sess=0x09 settle.
        private const long ConfigJsonEscalationCooldownTicks =
            15_000 * TimeSpan.TicksPerMillisecond;
        // No-progress watchdog: LastState still null this long after first chunk = restart.
        private const long ConfigJsonNoStateRestartTimeoutTicks =
            30_000 * TimeSpan.TicksPerMillisecond;

        public SessionWatchdogManager(TelemetrySender sender)
        {
            _sender = sender;
        }

        // ───── Notification API (called by sender/inbound dispatch) ───────

        /// <summary>Called when sess=FlagByte (0x02) receives its first inbound chunk.</summary>
        public void NoteSession02FirstInbound()
        {
            if (_session02FirstInboundUtcTicks == 0)
                _session02FirstInboundUtcTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>Called when sess=MgmtPort receives an fc:00 ack or any 7c:00
        /// data chunk. Either is sufficient proof the mgmt session is alive on
        /// the wheel side. Idempotent — first hit arms, subsequent are no-ops.</summary>
        public void NoteSession01Engaged()
        {
            if (_session01EngagedUtcTicks == 0)
                _session01EngagedUtcTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>Called from the inbound dispatcher when the wheel sends a
        /// type=0x00 session-close chunk. The wheel CLOSE-ing a session we
        /// believe is engaged is an unambiguous rejection signal — engagement
        /// is revoked here so <see cref="TickSession01EngagementWatchdog"/>
        /// (or sess=0x02) can re-arm with backoff. Without this revoke the
        /// engagement flag latched-true at the first fc:00 ack and the
        /// watchdog never fired again, so a wheel that engaged once and
        /// later rejected stayed stuck forever (2026-05-26
        /// moza-wire-...-045658).
        ///
        /// On top of the per-CLOSE recovery hook, sustained storms
        /// (≥ CloseStormRestartThreshold closes in CloseStormRestartWindowMs)
        /// escalate to a full sender restart via Recovery.RequestRestart
        /// — the engagement watchdog's close+open+resubscribe sequence
        /// sends MORE tier-def to the wheel, which is exactly what's
        /// confused its firmware in the first place; tearing the whole
        /// session down and rebuilding from scratch breaks that loop.
        ///
        /// Each CLOSE also drives the close-storm WARN log (visibility),
        /// gated to one WARN per CloseStormWarnCooldownMs to keep the log
        /// readable.</summary>
        public void NoteWheelInitiatedClose(byte session)
        {
            if (session >= CloseStormSessionMax) return;
            int now = Environment.TickCount;

            // Recovery, layer 1 — revoke engagement on the affected session
            // so its engagement watchdog re-fires. Fires on EVERY inbound
            // CLOSE, not just storms: a single wheel-initiated CLOSE on a
            // session we believe is engaged is already a contradiction the
            // watchdog should resolve.
            if (session == _sender.MgmtPort && _session01EngagedUtcTicks != 0)
            {
                _session01EngagedUtcTicks = 0;
                MozaLog.Debug(
                    $"[Moza] sess=0x{session:X2} engagement revoked due to " +
                    "wheel-initiated CLOSE — engagement watchdog will re-arm.");
            }
            // sess=0x02 has its own engagement signal (first-inbound) which
            // is also useful to revoke on close.
            if (session == _sender.FlagByte && _session02FirstInboundUtcTicks != 0)
            {
                _session02FirstInboundUtcTicks = 0;
                MozaLog.Debug(
                    $"[Moza] sess=0x{session:X2} (telem) first-inbound flag " +
                    "revoked due to wheel-initiated CLOSE.");
            }

            // Slide the window: if it's been longer than CloseStormWindowMs
            // since the first close in this session's tracked storm, restart
            // counting. Otherwise increment.
            int sinceFirst = now - _closeStormFirstTickMs[session];
            if (_closeStormCount[session] == 0 || sinceFirst > CloseStormWindowMs)
            {
                _closeStormFirstTickMs[session] = now;
                _closeStormCount[session] = 1;
                MozaLog.Debug(
                    $"[Moza] sess=0x{session:X2} wheel-initiated CLOSE (first in window) — " +
                    "either a normal end-of-stream or the start of a rejection storm; " +
                    "logging at DEBUG until threshold reached.");
                return;
            }
            _closeStormCount[session]++;
            if (_closeStormCount[session] >= CloseStormWarnThreshold)
            {
                int sinceWarn = now - _closeStormLastWarnTickMs[session];
                if (_closeStormLastWarnTickMs[session] == 0 || sinceWarn > CloseStormWarnCooldownMs)
                {
                    _closeStormLastWarnTickMs[session] = now;
                    MozaLog.Warn(
                        $"[Moza] sess=0x{session:X2} CLOSE storm: " +
                        $"{_closeStormCount[session]} wheel-initiated closes in " +
                        $"{sinceFirst} ms. Wheel is rejecting our session — typically " +
                        "caused by rapid tier-def re-emissions confusing firmware state. " +
                        "Engagement watchdog will re-arm; if storm persists, sender will " +
                        "escalate to full restart.");
                }
            }
            // Recovery, layer 2 — sustained storm escalation. If we've taken
            // CloseStormRestartThreshold closes inside CloseStormRestartWindowMs,
            // the engagement watchdog's incremental re-arm clearly isn't
            // breaking us out (it would have engaged sess=0x01 long before
            // we got this deep). Full restart tears down every session,
            // re-runs session-close cold start, re-probes display, and
            // rebuilds the subscription from scratch — the wheel firmware
            // gets a clean slate.
            if (_closeStormCount[session] >= CloseStormRestartThreshold
                && sinceFirst <= CloseStormRestartWindowMs
                && _closeStormRestartRequested[session] == 0)
            {
                _closeStormRestartRequested[session] = 1;
                _sender.Recovery.RequestRestart(
                    $"sess=0x{session:X2} CLOSE storm: " +
                    $"{_closeStormCount[session]} wheel-initiated closes in " +
                    $"{sinceFirst} ms — escalating to full sender restart.");
            }
        }

        public void NoteConfigJsonChunkArrived()
        {
            _configJsonLastChunkUtcTicks = DateTime.UtcNow.Ticks;
            // Real progress: forgive the tick-path escalation streak so the
            // cap counts only consecutive un-acked nudges.
            _configJsonGapTickEscalations = 0;
        }

        public void ResetConfigJsonGapTracking()
        {
            _configJsonGapCount = 0;
            _configJsonLastPrimeRetryUtcTicks = 0;
            _configJsonGapTickEscalations = 0;
        }

        public void NoteActiveStateEntered() => _activeStateEnteredTickCount = Environment.TickCount;

        /// <summary>Reset all watchdog state at sender Start/Stop boundary.</summary>
        public void Reset()
        {
            _s09RetryRounds = 0;
            _s09RetryLastTickCount = 0;
            _session02FirstInboundUtcTicks = 0;
            _activeStateEnteredTickCount = 0;
            _s02ReArmRounds = 0;
            _s02ReArmLastTickCount = 0;
            _session01EngagedUtcTicks = 0;
            _s01ReArmRounds = 0;
            _s01ReArmLastTickCount = 0;
            _configJsonGapCount = 0;
            _configJsonLastChunkUtcTicks = 0;
            _configJsonLastPrimeRetryUtcTicks = 0;
            _configJsonGapTickEscalations = 0;
            for (int s = 0; s < CloseStormSessionMax; s++)
            {
                _closeStormFirstTickMs[s] = 0;
                _closeStormCount[s] = 0;
                _closeStormLastWarnTickMs[s] = 0;
                _closeStormRestartRequested[s] = 0;
            }
            // _configJsonLastEscalationUtcTicks NOT reset — cooldown spans restarts.
            // Clear the wheel-ready latch so a subsequent reconnect re-arms
            // detection from a clean slate (consumed by ProbeAndOpenSessions).
            _sender.ResetWheelReadyObserved();
        }

        // ───── Tick loops (called by sender's tick driver) ────────────────

        /// <summary>Re-emit prime+open-request on sess=0x09 until device-init or budget exhaustion (parks pipeline).</summary>
        public void TickRetryS09IfNotEstablished()
        {
            if (_sender.StateIsIdle) return;
            if (_s09RetryRounds >= S09RetryMaxRounds) return;
            if (!_sender.ConnectionIsConnected) return;

            var s09 = _sender.SessionsGetOrCreate(0x09);
            if (s09.DeviceInitiated) return;

            int now = Environment.TickCount;
            if (_s09RetryRounds > 0)
            {
                int gateMs = S09BackoffMs[Math.Min(_s09RetryRounds - 1, S09BackoffMs.Length - 1)];
                if ((now - _s09RetryLastTickCount) < gateMs) return;
            }

            _s09RetryRounds++;
            _s09RetryLastTickCount = now;

            // Fresh seq so the wheel doesn't dedupe against the prior open.
            ushort recoverySeq = (ushort)(0x000B + _s09RetryRounds * 0x10);
            MozaLog.Warn(
                $"[Moza] sess=0x09 not yet device-initiated; retry round " +
                $"{_s09RetryRounds}/{S09RetryMaxRounds} (open-seq=0x{recoverySeq:X4})");

            try
            {
                _sender.SendSessionPrime(0x09, (ushort)(0x0001 + _s09RetryRounds));
                SendConfigJsonOpenRequest(0x09, recoverySeq);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] sess=0x09 retry emit failed: {ex.Message}");
            }

            if (_s09RetryRounds >= S09RetryMaxRounds)
            {
                // Route through RecoveryDispatcher.Park — its Stop()+raise-event
                // sequence is identical to the prior inline path AND it sets the
                // shared parked flag so subsequent watchdog escalations from other
                // sessions don't queue restart work onto a torn-down pipeline.
                _sender.Recovery.Park(
                    $"sess=0x09 retry budget exhausted after {S09RetryMaxRounds} rounds " +
                    "— wheel never engaged the configJson handshake. " +
                    "Cause is usually a wheel with no display sub-device or a wheel that refused the " +
                    "dashboard session — common for displayless wheels that slipped past the static " +
                    "HasDisplay gate. A wheel hot-swap or telemetry toggle will re-attempt.");
            }
        }

        /// <summary>
        /// Soft-watchdog for configJson gaps that have stalled past the
        /// passive-wait window. Sends prime+open-request to nudge the wheel
        /// without yet escalating to RestartForSwitch.
        /// </summary>
        public void TickConfigJsonGapEscalation()
        {
            if (_sender.StateIsIdle) return;
            if (!_sender.ConnectionIsConnected) return;
            if (_sender.ConfigJsonHasLastState) return;
            // Hot-switch burst pacing owns sess=0x01/0x02 traffic for ~4s
            // post-switch; injecting a prime+open-request mid-burst stomps the
            // wheel's catalog-END handshake. See the matching guard in
            // TickSession01EngagementWatchdog for the original incident.
            if (_sender.HotSwitchBurstPending) return;
            long gapTs = _sender.ConfigJsonLastForwardGapUtcTicks;
            if (gapTs == 0) return;

            long now = DateTime.UtcNow.Ticks;
            long gapAgeMs = (now - gapTs) / TimeSpan.TicksPerMillisecond;
            if (gapAgeMs < ConfigJsonGapPassiveWaitMs) return;

            // Don't escalate if we already nudged within the passive window
            // (HandleConfigJsonGap and this share _configJsonLastPrimeRetryUtcTicks).
            long primeAgeTicks = now - _configJsonLastPrimeRetryUtcTicks;
            long passiveWaitTicks = ConfigJsonGapPassiveWaitMs * TimeSpan.TicksPerMillisecond;
            if (_configJsonLastPrimeRetryUtcTicks != 0 && primeAgeTicks < passiveWaitTicks)
                return;

            int recoveryOpenSeq = unchecked((ushort)(0x100 + _configJsonGapCount));
            int primeSeq = unchecked((ushort)(0x200 + _configJsonGapCount));
            byte session = 0x09;
            // Prefer 0x0a if the wheel's been talking on it instead.
            if (_sender.Session09InboundSeq == 0 && _configJsonLastChunkUtcTicks != 0)
                session = 0x0a;
            // Tick-path escalation cap: if HandleConfigJsonGap is never
            // triggered (zero chunks arriving at all), the gap-count counter
            // never advances to ConfigJsonGapRestartAt and the prime+open
            // nudges would loop indefinitely. After the cap is reached,
            // escalate to RecoveryDispatcher instead of emitting another
            // nudge. RecoveryDispatcher's debounce + rate-cap takes over from
            // here (parks if restarts don't help).
            if (_configJsonGapTickEscalations >= ConfigJsonGapTickEscalationCap)
            {
                if (now - _configJsonLastEscalationUtcTicks < ConfigJsonEscalationCooldownTicks)
                    return;
                _configJsonLastEscalationUtcTicks = now;
                _configJsonGapTickEscalations = 0;
                _sender.Recovery.RequestRestart(
                    $"sess=0x{session:X2} configJson tick watchdog: " +
                    $"{ConfigJsonGapTickEscalationCap} prime+open nudges produced no chunks " +
                    $"(gap age={gapAgeMs}ms)");
                return;
            }

            try
            {
                MozaLog.Warn(
                    $"[Moza] sess=0x{session:X2} configJson gap stale " +
                    $"({gapAgeMs}ms ≥ {ConfigJsonGapPassiveWaitMs}ms passive-wait window) — " +
                    $"wheel didn't auto-retransmit. " +
                    $"prime + open-request (open seq=0x{recoveryOpenSeq:X4}, prime seq=0x{primeSeq:X4}, " +
                    $"nudge {_configJsonGapTickEscalations + 1}/{ConfigJsonGapTickEscalationCap})");
                _sender.SendSessionPrime(session, (ushort)primeSeq);
                SendConfigJsonOpenRequest(session, (ushort)recoveryOpenSeq);
                _configJsonLastPrimeRetryUtcTicks = now;
                _configJsonGapTickEscalations++;
                if (_configJsonGapCount == 0) _configJsonGapCount = 1;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Moza] sess=0x{session:X2} configJson tick-watchdog recovery failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Stuck-state restart only when chunks arrived but no LastState materialised
        /// AND catalog+subscription aren't healthy. ConfigJson is library-UI only.
        /// </summary>
        public void TickConfigJsonStuckWatchdog()
        {
            if (_sender.StateIsIdle) return;
            if (!_sender.ConnectionIsConnected) return;
            if (_sender.ConfigJsonHasLastState) return;
            if (_configJsonLastChunkUtcTicks == 0) return;
            // Hot-switch burst pacing owns the recovery surface during the
            // ~4 s burst window; deferring the stuck-state restart until the
            // burst settles keeps the burst from being stomped mid-emission.
            if (_sender.HotSwitchBurstPending) return;

            // Working dashboard despite missing library list — catalog + subscription is enough.
            bool haveCatalog = _sender.CatalogCount > 0;
            bool haveSubscription = _sender.HasActiveSubscription;
            if (haveCatalog && haveSubscription)
                return;

            long now = DateTime.UtcNow.Ticks;
            if (now - _configJsonLastChunkUtcTicks < ConfigJsonNoStateRestartTimeoutTicks)
                return;
            if (now - _configJsonLastEscalationUtcTicks < ConfigJsonEscalationCooldownTicks)
                return;

            _configJsonLastEscalationUtcTicks = now;
            _configJsonGapCount = 0;
            _configJsonLastChunkUtcTicks = now;
            _sender.Recovery.RequestRestart(
                "configJson stuck-state watchdog: " +
                $"chunks arrived but no valid state for {ConfigJsonNoStateRestartTimeoutTicks / TimeSpan.TicksPerMillisecond / 1000}s, " +
                "and no catalog/subscription either");
        }

        /// <summary>Sess=0x02 engagement: re-arm (close+open+init+resubscribe) if no inbound; escalate on exhaustion.</summary>
        public void TickSession02EngagementWatchdog()
        {
            if (!_sender.StateIsActive) return;
            if (!_sender.ConnectionIsConnected) return;
            if (_session02FirstInboundUtcTicks != 0) return;
            if (_s02ReArmRounds >= S02ReArmMaxRounds) return;
            // Defensive: skip silently if a tick fires before NoteActiveStateEntered.
            if (_activeStateEnteredTickCount == 0) return;
            // Hot-switch burst is mid-flight: HotSwitchCoordinator's emission
            // pacing depends on uninterrupted sess=0x01/0x02 traffic, and a
            // re-arm's close+open+ApplySubscription clobbers the burst. Same
            // failure mode as the documented W17 incident on the sess=0x01
            // watchdog (2026-05-20 17:55:08); applies symmetrically here.
            if (_sender.HotSwitchBurstPending) return;

            int now = Environment.TickCount;
            if (_s02ReArmRounds == 0)
            {
                // Initial grace — ~3 s covers the observed PitHouse cadence.
                if ((now - _activeStateEnteredTickCount) < S02InitialGraceMs) return;
            }
            else
            {
                int gateMs = S02ReArmBackoffMs[Math.Min(_s02ReArmRounds - 1, S02ReArmBackoffMs.Length - 1)];
                if ((now - _s02ReArmLastTickCount) < gateMs) return;
            }

            _s02ReArmRounds++;
            _s02ReArmLastTickCount = now;

            MozaLog.Warn(
                $"[Moza] sess=0x02 no inbound observed; re-arm round " +
                $"{_s02ReArmRounds}/{S02ReArmMaxRounds} — close+open+init+resubscribe");

            try
            {
                const int ReArmAckTimeoutMs = 500;
                _sender.TryCloseSession(0x02, ReArmAckTimeoutMs);
                _sender.TryOpenSession(0x02, ReArmAckTimeoutMs);
                _sender.SendSessionInitHandshake();
                _sender.ApplySubscription(force: true);
            }
            catch (Exception ex)
            {
                MozaLog.Warn(
                    $"[Moza] sess=0x02 re-arm round {_s02ReArmRounds} failed: {ex.Message}");
            }

            if (_s02ReArmRounds >= S02ReArmMaxRounds)
            {
                _sender.Recovery.RequestRestart(
                    $"sess=0x02 re-arm budget exhausted after {S02ReArmMaxRounds} rounds");
            }
        }

        /// <summary>Sess=0x01 (mgmt) engagement: re-arm (close+open+resubscribe)
        /// if no fc:00 ack or 7c:00 data has been seen; escalate on exhaustion.
        /// Symmetric to <see cref="TickSession02EngagementWatchdog"/> — see the
        /// field-block comment above the sess=0x01 watchdog state for the
        /// hardware-specific failure this catches.</summary>
        public void TickSession01EngagementWatchdog()
        {
            if (!_sender.StateIsActive) return;
            if (!_sender.ConnectionIsConnected) return;
            if (_session01EngagedUtcTicks != 0) return;
            if (_s01ReArmRounds >= S01ReArmMaxRounds) return;
            if (_activeStateEnteredTickCount == 0) return;
            // Don't fight a hot-switch burst in flight. The burst's tier-def
            // re-applications and session state are coordinated by the
            // HotSwitchCoordinator; our close+open+ApplySubscription would
            // clobber that work and reset both to a half-formed state.
            // Verified in W17 capture 2026-05-20 17:55: re-arm fired at
            // 17:55:08.528 while burst 1/4 was in flight (armed 17:55:06,
            // first emission 17:55:07.564) and stomped the burst — switches
            // and test mode then misbehaved for the rest of the session.
            if (_sender.HotSwitchBurstPending) return;

            byte mgmt = _sender.MgmtPort;
            if (mgmt == 0) return;

            int now = Environment.TickCount;
            if (_s01ReArmRounds == 0)
            {
                if ((now - _activeStateEnteredTickCount) < S01InitialGraceMs) return;
            }
            else
            {
                int gateMs = S01ReArmBackoffMs[Math.Min(_s01ReArmRounds - 1, S01ReArmBackoffMs.Length - 1)];
                if ((now - _s01ReArmLastTickCount) < gateMs) return;
            }

            _s01ReArmRounds++;
            _s01ReArmLastTickCount = now;

            MozaLog.Warn(
                $"[Moza] sess=0x{mgmt:X2} (mgmt) no inbound observed; re-arm round " +
                $"{_s01ReArmRounds}/{S01ReArmMaxRounds} — close+open+resubscribe");

            try
            {
                const int ReArmAckTimeoutMs = 500;
                _sender.TryCloseSession(mgmt, ReArmAckTimeoutMs);
                _sender.TryOpenSession(mgmt, ReArmAckTimeoutMs);
                // ApplySubscription re-emits tier-def on MgmtPort, which is the
                // payload sess=0x01 exists to carry. If the wheel acks the fresh
                // open, tier-def reaches it on this round and the catalog push
                // follows naturally; no separate init handshake (unlike sess=0x02).
                _sender.ApplySubscription(force: true);
            }
            catch (Exception ex)
            {
                MozaLog.Warn(
                    $"[Moza] sess=0x{mgmt:X2} re-arm round {_s01ReArmRounds} failed: {ex.Message}");
            }

            if (_s01ReArmRounds >= S01ReArmMaxRounds)
            {
                _sender.Recovery.RequestRestart(
                    $"sess=0x{mgmt:X2} (mgmt) re-arm budget exhausted after {S01ReArmMaxRounds} rounds");
            }
        }

        /// <summary>
        /// Three-tier gap recovery: passive wait (5 s) → prime+open → restart.
        /// Called from inbound dispatch on session-data gap.
        /// </summary>
        public void HandleConfigJsonGap(byte session, int seq)
        {
            _configJsonGapCount++;
            bool haveCachedState = _sender.ConfigJsonHasLastState;
            string tag = $"sess=0x{session:X2}";
            string cachedTag = haveCachedState ? "cached-state" : "no-state";

            if (haveCachedState)
            {
                MozaLog.Warn(
                    $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                    $"buffer preserved, keeping cached state — no recovery action");
                return;
            }

            // Burst-active gating: a hot-switch tier-def burst is paced by
            // HotSwitchCoordinator and depends on uninterrupted session
            // traffic; injecting prime+open-request frames mid-burst stomps
            // the catalog-END handshake. Defer the gap response until burst
            // settles — the wheel's own retransmit timer will keep covering
            // missing chunks in the meantime.
            if (_sender.HotSwitchBurstPending)
            {
                MozaLog.Debug(
                    $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                    "deferring recovery — hot-switch burst in flight");
                return;
            }

            // T1 passive: wheel will retransmit from HighWaterSeq+1.
            long now = DateTime.UtcNow.Ticks;
            long gapAgeTicks = now - _sender.ConfigJsonLastForwardGapUtcTicks;
            long passiveWaitTicks = ConfigJsonGapPassiveWaitMs * TimeSpan.TicksPerMillisecond;
            if (_sender.ConfigJsonLastForwardGapUtcTicks != 0 && gapAgeTicks < passiveWaitTicks)
            {
                MozaLog.Debug(
                    $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                    $"waiting up to {ConfigJsonGapPassiveWaitMs}ms for wheel auto-retransmit " +
                    $"(gap age={gapAgeTicks / TimeSpan.TicksPerMillisecond}ms)");
                return;
            }

            // Don't re-fire prime+open-request within the passive window of the prior retry.
            long primeAgeTicks = now - _configJsonLastPrimeRetryUtcTicks;
            if (_configJsonGapCount <= ConfigJsonGapPrimeRetryAt
                && _configJsonLastPrimeRetryUtcTicks != 0
                && primeAgeTicks < passiveWaitTicks)
            {
                MozaLog.Debug(
                    $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                    $"prior prime+open-request {primeAgeTicks / TimeSpan.TicksPerMillisecond}ms ago — deferring next");
                return;
            }

            if (_configJsonGapCount <= ConfigJsonGapPrimeRetryAt)
            {
                try
                {
                    int recoveryOpenSeq = unchecked((ushort)(seq + 0x100 * _configJsonGapCount));
                    int primeSeq = unchecked((ushort)(seq + 0x200 + _configJsonGapCount));
                    MozaLog.Warn(
                        $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                        $"passive wait expired ({gapAgeTicks / TimeSpan.TicksPerMillisecond}ms) — " +
                        $"prime + open-request (open seq=0x{recoveryOpenSeq:X4}, prime seq=0x{primeSeq:X4})");
                    _sender.SendSessionPrime(session, (ushort)primeSeq);
                    SendConfigJsonOpenRequest(session, (ushort)recoveryOpenSeq);
                    _configJsonLastPrimeRetryUtcTicks = now;
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[Moza] {tag} configJson recovery emit failed: {ex.Message}");
                }
                return;
            }

            if (_configJsonGapCount >= ConfigJsonGapRestartAt)
            {
                if (now - _configJsonLastEscalationUtcTicks < ConfigJsonEscalationCooldownTicks)
                {
                    MozaLog.Warn(
                        $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                        "in escalation cooldown — deferring full restart");
                    return;
                }
                _configJsonLastEscalationUtcTicks = now;
                _configJsonGapCount = 0;
                _configJsonLastPrimeRetryUtcTicks = 0;
                _sender.Recovery.RequestRestart(
                    $"{tag} configJson recovery escalation: " +
                    "no cached state and prime+open-request didn't recover the burst");
                return;
            }

            // Mid-tier (count between prime-retry and restart): just log; next chunk may unblock.
            MozaLog.Warn(
                $"[Moza] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                "waiting for next chunk before escalating");
        }

        /// <summary>
        /// Host-initiated session-open for the configJson channel (port 9).
        /// Uses configJson-specific magic <c>7c 1e 6c 80</c> — upload-style
        /// <c>7c 23 46 80</c> does NOT trigger wheel device-init for 0x09.
        /// </summary>
        public void SendConfigJsonOpenRequest(byte port, ushort seq)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x0A,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0x7C, 0x1E, 0x6C, 0x80,
                (byte)(seq & 0xFF), (byte)(seq >> 8),
                port, 0x00,
                0xFE, 0x01,
                0x00
            };
            frame[14] = MozaProtocol.CalculateWireChecksum(frame);
            _sender.SendRawFrame(frame);
        }
    }
}
