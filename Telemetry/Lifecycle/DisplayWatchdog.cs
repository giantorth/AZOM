using System;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry.Lifecycle
{
    /// <summary>
    /// Unified, content-aware display watchdog. Replaces the five former
    /// per-session watchdog loops (sess=0x09 retry, sess=0x01 / sess=0x02
    /// engagement, configJson gap escalation, configJson stuck-state) with ONE
    /// per-tick verdict that fuses every session's liveness with POSITIVE
    /// content proof, and on a confirmed "display not engaged" verdict drives a
    /// full sender restart (escalating to Park when the restart budget is spent).
    ///
    /// Why this exists — the failure the old design missed:
    /// A diagnostics bundle (CS-Pro 2026-05-31) showed the wheel transport-alive
    /// but the display completely UNBOUND: sess=0x01 b2h carried only type=0x06
    /// seq-acks, sess=0x02 b2h only empty keep-alives, and WheelReportedSlot
    /// stayed -1 across 44 host kind=4 dashboard switches — clicking Test did
    /// nothing on the screen. The plugin reported Phase=Active because the old
    /// engagement gates latched on mere inbound FILLER bytes (acks + keepalives),
    /// and the only content-aware rejection signal the code consumed was the
    /// wheel-initiated CLOSE, which this failure mode never emits. CatalogCount
    /// (18, stale) and ConfigJsonHasLastState (true) were BOTH satisfied while
    /// dead — the only true discriminator was the slot round-trip failing
    /// (WheelReportedSlot never converging to LastEmittedKind4Slot).
    ///
    /// Engaged is therefore defined by POSITIVE proof, never by filler:
    ///   (A) catalog present  AND  configJson device-init/state present.
    ///   (B) the slot round-trip is POSITIVE confirmation only and no longer
    ///       gates the verdict — the wheel's reported slot is authoritative and
    ///       a mismatch never restarts (see EvaluateEngagement Context B).
    /// Filler acks/keepalives and inbound stalls are consumed only as liveness
    /// inputs that REINFORCE a content-absent verdict; they never clear one and
    /// never fire alone against a content-proven wheel. The one place liveness
    /// stands alone is Context C (binding-channel death), and there it means
    /// TOTAL silence on the lane — no pushed data AND no acks of our own sends.
    ///
    /// Threading: state is touched from the serial-read thread (Note*/Handle*),
    /// the tick-timer ThreadPool thread (TickDisplayWatchdog), and the Stop
    /// thread (Reset). int counters and TickCount stamps are atomic on the x86
    /// build; the 64-bit *UtcTicks fields are NOT, so every read/write of those
    /// goes through Interlocked.Read/Exchange to avoid torn reads. No lock is
    /// held across any _sender call-out (TryClose/Open/ApplySubscription/
    /// SendSessionPrime) because those block on acks delivered by the read
    /// thread — holding a lock across them would stall that thread.
    /// </summary>
    internal sealed class DisplayWatchdog
    {
        private readonly TelemetrySender _sender;

        public DisplayWatchdog(TelemetrySender sender)
        {
            _sender = sender;
        }

        // ── Cold-start / verdict tuning ───────────────────────────────────
        // Grace from Active before any engagement verdict is allowed. = the
        // former S01InitialGraceMs / S0x stall thresholds (all 20 s); covers
        // the CS-Pro / Universal-Hub slow bring-up where the wheel takes
        // 12-15 s after device-lock to start acking + announcing.
        private const int EngagementGraceMs = 20_000;
        // A not-engaged verdict must persist this long before it escalates to a
        // restart, so a momentary gap mid-catalog-push can't trigger one.
        private const int NotEngagedConfirmMs = 3_000;
        // Self-throttle between restart requests (mirror of
        // RecoveryDispatcher.DebounceMs); avoids recomputing/log-spamming a
        // verdict while our own restart is settling.
        private const int RestartCooldownMs = 30_000;
        // Binding-channel death (Context C): the resolved tier-def session has
        // carried NO inbound of ANY kind — pushed data chunks or fc:00 acks of
        // our own sends — for this long while the mirror session stayed
        // demonstrably alive.
        //
        // The wheel's own type=0x01 push cadence on this lane is NOT a
        // heartbeat and must never be the sole liveness input: bundle
        // VG9V7XB2 (W18/R12, 2026-08-22) shows it pushing a 4-byte all-zero
        // filler in bursts of 3 at ~3.91 s roughly every 15 s, then stopping
        // for 126 s at a stretch while the session is fully healthy — 811 host
        // chunks acked 356 times in the same window. Hence the ack stamp; see
        // NoteSession02Ack.
        private const int TierDefSessionDeadThresholdMs = 120_000;
        // The corroborating mirror session must have inbound at least this
        // fresh, so a dead cable / vanished port (both sessions quiet) can
        // never fire this rule — that is the connection layer's job.
        private const int MirrorSessionFreshMs = 30_000;
        // Post-switch variant of the binding-death threshold. Right after we
        // re-bind the display, total silence on the tier-def lane is the switch
        // having failed, not an idle gap — so 120 s of dead dashboard is the
        // wrong price. Bundle W0V1PF9V (W17/CS-Pro, 2026-08-23, "broke dash on
        // game change"): Dirt Rally → Automobilista 2 switched to slot 7 at
        // 22:52:35, the lane answered until 22:52:45.873, then went totally
        // silent — no pushed chunk and no ack — for the remaining 98 s of the
        // capture while the host pushed 129 chunks into it and the wheel stayed
        // stuck reporting slot 18. A healthy switch never does this: the lane
        // keeps acking throughout the reload. Same evidence bar as the steady-
        // state rule (no data AND no acks, mirror demonstrably alive, and we
        // must actually have sent on the lane), just a shorter fuse while a
        // switch we initiated is still settling.
        private const int PostSwitchWindowMs = 90_000;
        private const int PostSwitchTierDefDeadMs = 30_000;

        // ── liveness / rejection feeder state (Interlocked on 64-bit) ─────
        private long _session01EngagedUtcTicks;
        private long _session01LastInboundUtcTicks;
        private long _session01LastCloseUtcTicks;
        private long _session02FirstInboundUtcTicks;
        private long _session02LastInboundUtcTicks;
        // Last fc:00 ack the wheel sent on the FlagByte lane. Kept SEPARATE
        // from _session02LastInboundUtcTicks so the engagement latch stays
        // data-only ("positive proof, never filler"); only Context C's
        // liveness test fuses the two.
        private long _session02LastAckUtcTicks;
        private int _activeStateEnteredTickCount;

        // ── unified verdict state ─────────────────────────────────────────
        // Stamped whenever the host emits a kind=4 (any source: cold-start
        // resync probe, SwitchToProfile, PostSwitchCatalogConvergence nudge).
        // Anchors the slot round-trip window. -1 slot = none emitted yet.
        private long _lastKind4EmitUtcTicks;
        private int _lastKind4Slot = -1;
        private long _lastRestartRequestUtcTicks;
        private int _notEngagedFirstSeenTickCount;

        // ── sess=0x09 prime+open transmit retry (retained plumbing) ───────
        // Pure transmit nudge to get the wheel to device-init the configJson
        // session. NOT an engagement decision — budget exhaustion no longer
        // restarts here (the unified verdict's "no configJson state past
        // grace" path owns that). The one exception is the genuinely
        // screenless terminal case (no display sub-device after the full retry
        // window): that is a benign DEGRADED park, not a restart candidate.
        private int _s09RetryRounds;
        private int _s09RetryLastTickCount;
        private static readonly int[] S09BackoffMs =
            { 250, 500, 1000, 2000, 3000, 5000, 7000, 10000, 12000, 15000 };
        private const int S09RetryMaxRounds = 10;
        private bool _s09ScreenlessParked;

        // ── configJson chunk-gap retransmit (retained plumbing) ───────────
        // Narrowed to "nudge the wheel to retransmit missing chunks". The
        // independent RequestRestart escalations were removed — restart is the
        // unified verdict's job.
        private int _configJsonGapCount;
        private long _configJsonLastChunkUtcTicks;
        private long _configJsonLastPrimeRetryUtcTicks;
        private int _configJsonGapTickEscalations;
        // First gap of the current unhealed episode (0 = healthy). Most gaps
        // are losses the wheel's dup-ack ARQ heals within ~2 s, so escalation
        // keys on the episode staying unhealed, never on the event count.
        private long _configJsonGapEpisodeStartUtcTicks;
        private const int ConfigJsonGapStallEscalateMs = 10_000;
        private const int ConfigJsonGapTickEscalationCap = 3;
        private const int ConfigJsonGapPassiveWaitMs = 5_000;
        private const int ConfigJsonGapPrimeRetryAt = 1;

        // ── wheel-initiated CLOSE storm tracking (retained feeder+backstop) ─
        // The wheel CLOSE-ing a session we opened is its one reliable rejection
        // signal. Each close revokes the affected session's liveness flag and,
        // on a sustained storm (>= CloseStormRestartThreshold in
        // CloseStormRestartWindowMs), fast-escalates to a full restart — this
        // is an event-driven backstop that complements the tick verdict.
        private const int CloseStormSessionMax = 16;
        private readonly int[] _closeStormFirstTickMs = new int[CloseStormSessionMax];
        private readonly int[] _closeStormCount = new int[CloseStormSessionMax];
        private readonly int[] _closeStormLastWarnTickMs = new int[CloseStormSessionMax];
        private const int CloseStormWindowMs = 5_000;
        private const int CloseStormWarnThreshold = 3;
        private const int CloseStormWarnCooldownMs = 10_000;
        private const int CloseStormRestartThreshold = 3;
        private const int CloseStormRestartWindowMs = 30_000;
        private readonly int[] _closeStormRestartRequested = new int[CloseStormSessionMax];
        private readonly int[] _closeStormRestartFirstTickMs = new int[CloseStormSessionMax];
        private readonly int[] _closeStormRestartCount = new int[CloseStormSessionMax];

        // ── tier-def reject detector (single-close wedge, no storm) ───────
        // Cold-start wedge (CS-Pro bundle 2026-06-10): the wheel routes its
        // catalog + END marker to sess=0x02 and gives the tier-def session
        // (0x01) only a value-less END stub, so every tier-def goes out with
        // end=0; the wheel rejects the first one and CLOSEs the tier-def
        // session ONCE — no storm follows, Context A stays green (catalog +
        // configJson both present on 0x02), and the slot rule never restarts.
        // The reject is one wheel-initiated CLOSE of the tier-def session
        // within this window of an emission whose echoed END was 0 while the
        // wheel's END demonstrably lives elsewhere (LastWheelEndMarker != 0).
        // Recovery = the empirically-proven telemetry off/on cycle:
        // RequestRestart → Stop() (close 0x01..0x03) → SilenceGate (~11 s
        // host quiet) → fresh opens, after which the wheel re-advertises
        // WITH a valid END on the tier-def session (verified 2026-06-10
        // 12:13 log: first post-cycle tier-def echoed end=12 and bound).
        // One-shot per Start cycle; RecoveryDispatcher's debounce + restart
        // cap bound the retry loop if the wheel re-wedges.
        private const int TierDefRejectWindowMs = 10_000;
        private int _tierDefRejectRestartRequested;

        // ───── Notification API (called by sender / inbound dispatch) ─────

        /// <summary>Called when sess=MgmtPort receives an fc:00 ack or any 7c:00
        /// data chunk. Liveness only — proves the mgmt session is alive on the
        /// wheel side, NOT that the display bound (acks + the identity blob +
        /// END keepalives flow while the dashboard is dead).</summary>
        public void NoteSession01Engaged()
        {
            long now = DateTime.UtcNow.Ticks;
            if (Interlocked.Read(ref _session01EngagedUtcTicks) == 0)
                Interlocked.Exchange(ref _session01EngagedUtcTicks, now);
            Interlocked.Exchange(ref _session01LastInboundUtcTicks, now);
        }

        /// <summary>Called for every sess=FlagByte (0x02) inbound chunk.
        /// Liveness only — empty keep-alives satisfy this even when the display
        /// is unbound, which is exactly why the verdict gates on slot
        /// round-trip and not on this flag.</summary>
        public void NoteSession02FirstInbound()
        {
            long now = DateTime.UtcNow.Ticks;
            if (Interlocked.Read(ref _session02FirstInboundUtcTicks) == 0)
                Interlocked.Exchange(ref _session02FirstInboundUtcTicks, now);
            Interlocked.Exchange(ref _session02LastInboundUtcTicks, now);
        }

        /// <summary>Called for every wheel fc:00 ack on sess=FlagByte (0x02).
        /// Lane liveness ONLY — the mirror of the mgmt lane's ack feed in
        /// <see cref="NoteSession01Engaged"/>: an ack proves the wheel still
        /// holds the session open and is servicing our sends even when it has
        /// nothing of its own to push. Deliberately does NOT touch
        /// _session02FirstInboundUtcTicks — that latch is the engagement gate
        /// and stays data-only, so filler can never satisfy it. Consumed only
        /// by Context C's liveness test.</summary>
        public void NoteSession02Ack() =>
            Interlocked.Exchange(ref _session02LastAckUtcTicks, DateTime.UtcNow.Ticks);

        /// <summary>Stamp the emit time + slot for every host kind=4
        /// (SendDashboardSwitch). Anchors the slot round-trip window in
        /// <see cref="EvaluateEngagement"/>.</summary>
        public void NoteHostEmittedKind4(int slot)
        {
            _lastKind4Slot = slot;
            Interlocked.Exchange(ref _lastKind4EmitUtcTicks, DateTime.UtcNow.Ticks);
            // A fresh switch resets the not-engaged debounce: give the new
            // round-trip its full window before any verdict.
            _notEngagedFirstSeenTickCount = 0;
        }

        public void NoteConfigJsonChunkArrived()
        {
            Interlocked.Exchange(ref _configJsonLastChunkUtcTicks, DateTime.UtcNow.Ticks);
            _configJsonGapTickEscalations = 0;
        }

        public void ResetConfigJsonGapTracking()
        {
            _configJsonGapCount = 0;
            Interlocked.Exchange(ref _configJsonGapEpisodeStartUtcTicks, 0);
            Interlocked.Exchange(ref _configJsonLastPrimeRetryUtcTicks, 0);
            _configJsonGapTickEscalations = 0;
        }

        public void NoteActiveStateEntered() => _activeStateEnteredTickCount = Environment.TickCount;

        /// <summary>Wheel-initiated type=0x00 CLOSE. Revokes the affected
        /// session's liveness flag (the wheel rejecting a session we believe is
        /// engaged is a contradiction), stamps the mgmt close time so the
        /// verdict refuses to trust inbound-bytes liveness while a close is
        /// recent, and fast-escalates a sustained storm to a full restart.</summary>
        public void NoteWheelInitiatedClose(byte session)
        {
            if (session >= CloseStormSessionMax) return;
            int now = Environment.TickCount;

            // Revoke liveness on the affected session.
            if (session == _sender.MgmtPort)
                Interlocked.Exchange(ref _session01EngagedUtcTicks, 0);
            if (session == _sender.FlagByte && Interlocked.Read(ref _session02FirstInboundUtcTicks) != 0)
                Interlocked.Exchange(ref _session02FirstInboundUtcTicks, 0);
            // Stamp the mgmt close time unconditionally. No consumer today —
            // the post-close settle window it fed was removed with the rest of
            // the unread threshold constants; kept as a cheap observation point.
            if (session == _sender.MgmtPort)
                Interlocked.Exchange(ref _session01LastCloseUtcTicks, DateTime.UtcNow.Ticks);

            // Tier-def reject wedge — see the field block for the full story.
            var emitter = _sender.TierDefEmitter;
            long emitTicks = emitter.LastTierDefEmitUtcTicks;
            if (_tierDefRejectRestartRequested == 0
                && emitTicks != 0
                && session == emitter.LastTierDefSession
                && emitter.LastTierDefEndMarker == 0
                && _sender.CatalogParser.LastWheelEndMarker != 0
                && (DateTime.UtcNow.Ticks - emitTicks) / TimeSpan.TicksPerMillisecond
                    <= TierDefRejectWindowMs)
            {
                _tierDefRejectRestartRequested = 1;
                _sender.Recovery.RequestRestart(
                    $"sess=0x{session:X2} tier-def REJECT: wheel closed the tier-def session " +
                    $"after an emission with end=0 while its END marker " +
                    $"({_sender.CatalogParser.LastWheelEndMarker}) lives on another session — " +
                    "cycling the pipeline so the wheel re-advertises with a valid END " +
                    "(catalog-on-wrong-session cold-start wedge).");
            }

            // Restart-escalation accounting on its own 30 s window.
            int sinceRestartFirst = now - _closeStormRestartFirstTickMs[session];
            if (_closeStormRestartCount[session] == 0
                || sinceRestartFirst > CloseStormRestartWindowMs)
            {
                _closeStormRestartFirstTickMs[session] = now;
                _closeStormRestartCount[session] = 1;
            }
            else
            {
                _closeStormRestartCount[session]++;
            }
            if (_closeStormRestartCount[session] >= CloseStormRestartThreshold
                && sinceRestartFirst <= CloseStormRestartWindowMs
                && _closeStormRestartRequested[session] == 0)
            {
                _closeStormRestartRequested[session] = 1;
                _sender.Recovery.RequestRestart(
                    $"sess=0x{session:X2} CLOSE storm: " +
                    $"{_closeStormRestartCount[session]} wheel-initiated closes in " +
                    $"{sinceRestartFirst} ms — escalating to full sender restart.");
            }

            // WARN-window slide (visibility), one WARN per CloseStormWarnCooldownMs.
            int sinceFirst = now - _closeStormFirstTickMs[session];
            if (_closeStormCount[session] == 0 || sinceFirst > CloseStormWindowMs)
            {
                _closeStormFirstTickMs[session] = now;
                _closeStormCount[session] = 1;
                MozaLog.Debug(
                    $"[AZOM] sess=0x{session:X2} wheel-initiated CLOSE (first in window) — " +
                    "either a normal end-of-stream or the start of a rejection storm.");
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
                        $"[AZOM] sess=0x{session:X2} CLOSE storm: " +
                        $"{_closeStormCount[session]} wheel-initiated closes in " +
                        $"{sinceFirst} ms. Wheel is rejecting our session — display watchdog " +
                        "will escalate to a full restart if it persists.");
                }
            }
        }

        /// <summary>Reset all watchdog state at sender Start/Stop boundary.</summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _session01EngagedUtcTicks, 0);
            Interlocked.Exchange(ref _session01LastInboundUtcTicks, 0);
            Interlocked.Exchange(ref _session01LastCloseUtcTicks, 0);
            Interlocked.Exchange(ref _session02FirstInboundUtcTicks, 0);
            Interlocked.Exchange(ref _session02LastInboundUtcTicks, 0);
            Interlocked.Exchange(ref _session02LastAckUtcTicks, 0);
            _activeStateEnteredTickCount = 0;

            Interlocked.Exchange(ref _lastKind4EmitUtcTicks, 0);
            _lastKind4Slot = -1;
            Interlocked.Exchange(ref _lastRestartRequestUtcTicks, 0);
            _notEngagedFirstSeenTickCount = 0;

            _s09RetryRounds = 0;
            _s09RetryLastTickCount = 0;
            _s09ScreenlessParked = false;

            _configJsonGapCount = 0;
            Interlocked.Exchange(ref _configJsonGapEpisodeStartUtcTicks, 0);
            Interlocked.Exchange(ref _configJsonLastChunkUtcTicks, 0);
            Interlocked.Exchange(ref _configJsonLastPrimeRetryUtcTicks, 0);
            _configJsonGapTickEscalations = 0;

            for (int s = 0; s < CloseStormSessionMax; s++)
            {
                _closeStormFirstTickMs[s] = 0;
                _closeStormCount[s] = 0;
                _closeStormLastWarnTickMs[s] = 0;
                _closeStormRestartRequested[s] = 0;
                _closeStormRestartFirstTickMs[s] = 0;
                _closeStormRestartCount[s] = 0;
            }
            _tierDefRejectRestartRequested = 0;

            // Clear the wheel-ready latch so a reconnect re-arms detection from
            // a clean slate (consumed by ProbeAndOpenSessions).
            _sender.ResetWheelReadyObserved();
        }

        // ───── Single tick entry (replaces the five former Tick* loops) ───

        /// <summary>
        /// One per-tick pass: drive the retained transmit nudges, then compute
        /// the unified engagement verdict and (after a persistence debounce)
        /// escalate a confirmed not-engaged verdict to a full sender restart.
        /// </summary>
        public void TickDisplayWatchdog()
        {
            if (!_sender.StateIsActive) return;
            if (!_sender.ConnectionIsConnected) return;

            // Transmit plumbing first — these are nudges to ESTABLISH the
            // sessions, independent of the verdict's restart cooldown. Each
            // self-gates on its own conditions (incl. HotSwitchBurstPending).
            RetryS09IfNotEstablished();
            DriveConfigJsonGapRetransmit();

            // The verdict must never fire mid hot-switch burst (documented
            // hazard: a restart's teardown clobbers the burst's catalog-END
            // handshake), nor before NoteActiveStateEntered anchors the grace.
            if (_sender.HotSwitchBurstPending) return;
            if (_activeStateEnteredTickCount == 0) return;

            int nowTick = Environment.TickCount;
            long nowUtc = DateTime.UtcNow.Ticks;

            // Self-throttle: skip while our own restart is still settling.
            long lastRestart = Interlocked.Read(ref _lastRestartRequestUtcTicks);
            if (lastRestart != 0
                && (nowUtc - lastRestart) / TimeSpan.TicksPerMillisecond < RestartCooldownMs)
                return;

            bool inGrace = (nowTick - _activeStateEnteredTickCount) < EngagementGraceMs;

            (bool notEngaged, string reason) = EvaluateEngagement(nowUtc, inGrace);

            if (!notEngaged)
            {
                _notEngagedFirstSeenTickCount = 0;
                return;
            }

            // Persistence debounce — require the verdict to hold before acting.
            if (_notEngagedFirstSeenTickCount == 0)
            {
                _notEngagedFirstSeenTickCount = nowTick;
                return;
            }
            if ((nowTick - _notEngagedFirstSeenTickCount) < NotEngagedConfirmMs) return;

            _notEngagedFirstSeenTickCount = 0;
            Interlocked.Exchange(ref _lastRestartRequestUtcTicks, nowUtc);

            // Full restart. RecoveryDispatcher enforces the cap and parks on
            // exhaustion, copying this reason verbatim into ParkReason — so the
            // power-cycle hint (relevant when a display IS present but never
            // engaged) rides along to the user-facing park surface.
            string hint = _sender.DisplayDetected
                ? " — if this persists, a wheel power-cycle may be required"
                : "";
            _sender.Recovery.RequestRestart(
                $"DisplayWatchdog: transport alive but display NOT engaged — {reason}{hint}");
        }

        /// <summary>
        /// The fused verdict. Engaged requires POSITIVE proof across the
        /// required sessions; filler acks/keepalives never count. Returns
        /// (notEngaged, reason).
        /// </summary>
        private (bool, string) EvaluateEngagement(long nowUtc, bool inGrace)
        {
            // Inside the cold-start grace window we never judge — protects the
            // slow-bring-up (CS-Pro / Universal-Hub) wheels.
            if (inGrace) return (false, "");

            // Context A — engagement establishment.
            if (_sender.CatalogCount <= 0)
                return (true, $"no channel catalog past {EngagementGraceMs} ms grace");

            // Context C — binding-channel liveness (binding dead under a green
            // dashboard). Fires when the resolved tier-def session has carried
            // ZERO inbound past the threshold while the mirror session proves
            // the wheel and wire alive. "Inbound" means ANY wheel utterance on
            // the lane: a pushed type=0x01 chunk OR an fc:00 ack of one of our
            // sends. Two confirmed true positives:
            //   • 2026-07-24: wheel power-cycled under an Active sender — 0x01
            //     dead 27 min, 0x02 chatty, every content check green off
            //     caches, display dead until a manual toggle.
            //   • 2026-07-31/08-01: HOT-path game switch drops the value feed —
            //     the wheel ECHOES the kind=4 (slot change is not data binding!)
            //     then 0x01 goes silent ~10 s later and the dash sits dead.
            //     With this rule armed the restart recovered it at +2 min every
            //     time; with it warn-only (08-01) the drop was permanent.
            // History: briefly downgraded to warn-only on 2026-07-31 after
            // misreading the kind=4 echo as proof of a working display — the
            // "false positive" wasn't one. Both-quiet (cable pull) stays
            // excluded via the mirror freshness gate — connection layer's job.
            //
            // 2026-08-22, bundle VG9V7XB2 (W18/R12, AC): the confirmed
            // healthy-display fire the note above asked for. The rule fired six
            // times in 24 min on a working dashboard, blanking it ~13 s each
            // time, because the tier-def stamp was fed ONLY by wheel-pushed
            // type=0x01 chunks while the mgmt stamp was fed by acks too. In the
            // 123 s the rule called dead, sess=0x02 carried 811 host chunks,
            // 356 wheel acks (the last 0.8 s before the restart) and a steady
            // 1 Hz 7c:23 display-state push — only the wheel's own filler push
            // had stopped. The lane's liveness therefore fuses data + acks.
            // Teeth retained: a wheel that power-cycles or drops the session
            // stops acking the ~800 chunks/2 min we push at it, so both stamps
            // go stale and this still fires.
            {
                byte tierDefSes = _sender.ResolveTierDefSession();
                bool tierDefIsFlag = tierDefSes == _sender.FlagByte
                    && _sender.FlagByte != _sender.MgmtPort;
                // Both lanes are measured the same way: newest of (pushed data,
                // ack). _session01LastInboundUtcTicks is already fed by both.
                long s01 = Interlocked.Read(ref _session01LastInboundUtcTicks);
                long s02 = Math.Max(
                    Interlocked.Read(ref _session02LastInboundUtcTicks),
                    Interlocked.Read(ref _session02LastAckUtcTicks));
                long deadStamp = tierDefIsFlag ? s02 : s01;
                long aliveStamp = tierDefIsFlag ? s01 : s02;
                // Never-heard-from this Start cycle counts from Active entry.
                long deadAgeMs = deadStamp != 0
                    ? (nowUtc - deadStamp) / TimeSpan.TicksPerMillisecond
                    : Environment.TickCount - _activeStateEnteredTickCount;
                long aliveAgeMs = aliveStamp != 0
                    ? (nowUtc - aliveStamp) / TimeSpan.TicksPerMillisecond
                    : long.MaxValue;
                // A switch we emitted recently shortens the fuse — see
                // PostSwitchTierDefDeadMs.
                long lastKind4 = Interlocked.Read(ref _lastKind4EmitUtcTicks);
                bool postSwitch = lastKind4 != 0
                    && (nowUtc - lastKind4) / TimeSpan.TicksPerMillisecond <= PostSwitchWindowMs;
                long deadThresholdMs = postSwitch
                    ? PostSwitchTierDefDeadMs
                    : TierDefSessionDeadThresholdMs;
                if (deadAgeMs >= deadThresholdMs
                    && aliveAgeMs <= MirrorSessionFreshMs)
                {
                    // Silence is only evidence of a dead binding once we have
                    // actually driven the lane. Without this guard a lane the
                    // host never sent on restarts every ~120 s forever — the
                    // Active-entry fallback above re-anchors on each restart and
                    // the spacing stays under both RecoveryDispatcher caps, so it
                    // never escalates to Park either. Evaluated here, not in the
                    // condition, so the lock is taken only on the firing path;
                    // falling through leaves the checks below this block intact.
                    int hostSends = _sender.SessionOutboundCount(tierDefSes);
                    if (deadStamp != 0 || hostSends > 0)
                    {
                        byte mirrorSes = tierDefIsFlag ? _sender.MgmtPort : _sender.FlagByte;
                        string span = deadStamp != 0
                            ? $"for {deadAgeMs / 1000}s"
                            : $"at all this Start cycle ({deadAgeMs / 1000}s since Active)";
                        return (true,
                            $"binding channel dead{(postSwitch ? " right after a switch" : "")}: " +
                            $"no inbound (data or ack) on tier-def session 0x{tierDefSes:X2} " +
                            $"{span} across {hostSends} host sends, while sess=0x{mirrorSes:X2} " +
                            $"stayed live (inbound {aliveAgeMs / 1000}s ago) — binding lost under " +
                            "a green dashboard (wheel reboot, or the post-game-switch value-feed drop)");
                    }
                }
            }

            if (!_sender.ConfigJsonHasLastState)
            {
                // The catalog is present (checked above): the display has already
                // bound the channel/tier-def catalog and is rendering the current
                // dashboard. The configJson device-init/state push (sess=0x09) is
                // SUPPLEMENTARY — it carries the dashboard LIST for switching — and
                // lags badly for slow-to-establish dashboards (radar/track-map). The
                // watchdog nudges it every tick (RetryS09IfNotEstablished /
                // DriveConfigJsonGapRetransmit), so it arrives without a restart.
                //
                // Force-restarting a BOUND display over a late state repeatedly broke
                // the working radar (2026-06-25, W17/CS-Pro): boundComplete + slot
                // rendering, state not yet pushed (and WheelReportedSlot can still be
                // -1 mid-bind, so a slot check alone doesn't save it) — and the
                // restart was the ONLY thing provoking the state, so patience + the
                // nudges get there without the damage. A late state on a
                // catalog-bound display is not a restart-worthy failure: genuine
                // non-establishment is the no-catalog check above, and genuine
                // rejection is the wheel-CLOSE-storm backstop.
                //
                // EXCEPTION — the stale-session wedge (CS-Pro bundle 2026-07-21):
                // catalog healthy on the tier-def session, but sess=0x02/0x09
                // survived STALE from a prior host instance, so the wheel never
                // emits ANY engagement record — no 0x09 device-init (ever), no
                // configJson state, no slot report — while acking everything we
                // send. None of the other triggers can fire (no wheel CLOSEs,
                // END echoed fine), and the plugin sat Phase=Active/LastFailure=
                // None for 10.5 min until a manual telemetry off/on. Distinguish
                // it from the slow radar bind, which device-inits 0x09 within
                // ~1 s and pushes its list (~40 s) INSIDE the s09 retry budget:
                // here the wheel never device-inited 0x09 across the whole
                // ~55 s budget, never pushed a live list, and never reported a
                // slot. Restart = the empirically-proven off/on cycle (Stop →
                // 11 s silence → fresh opens engaged in <2 s in that bundle);
                // RecoveryDispatcher caps it and parks with the power-cycle hint.
                bool s09BudgetSpent = _s09RetryRounds >= S09RetryMaxRounds;
                bool wheelNeverDeviceInit09 = !_sender.WheelReadyObserved;
                bool noLiveList =
                    (_sender.ConfigJson?.LiveState?.ConfigJsonList?.Count ?? 0) == 0;
                bool slotNeverReported = _sender.WheelReportedSlot < 0;
                if (_sender.DisplayDetected
                    && s09BudgetSpent && wheelNeverDeviceInit09
                    && noLiveList && slotNeverReported)
                {
                    return (true,
                        "catalog present but the display never engaged: no configJson state, " +
                        $"sess=0x09 never device-initiated across {S09RetryMaxRounds} nudge rounds, " +
                        "no live dashboard list, no slot report — stale-session wedge " +
                        "(wheel session state survived from a prior host instance)");
                }
                return (false, "");
            }

            // Context B — slot round-trip is POSITIVE confirmation only, never a
            // restart trigger. The wheel's reported slot is AUTHORITATIVE: any
            // valid reported slot means a live, bound display — whether it echoed
            // our kind=4 or the user switched the dash on the wheel itself (a
            // wheel-initiated switch always wins; the wheel is wherever it says it
            // is). Demanding reported == last-kind4 target force-restarted WORKING
            // displays during normal use: (a) the user switching on the wheel
            // (reported is a different valid slot), and (b) a slow cold-start where
            // the wheel had not yet echoed a slot (reported=-1, observed on RBR
            // while the dash was binding fine). Neither is proof the display is
            // dead, so slot state no longer drives a NOT-engaged verdict. Genuine
            // non-establishment is still caught by Context A above; a frozen-after-
            // bind display would need a corroborated signal (e.g. value-frame
            // stall), not a slot mismatch.

            // Liveness/rejection fusion is reinforcement only: a content-proven
            // wheel (catalog + state + slot round-trip OK above) is engaged even
            // through legitimate quiet intervals. A recent mgmt CLOSE or an
            // inbound stall on its own does NOT override that — the close-storm
            // backstop (NoteWheelInitiatedClose) handles genuine rejection, and
            // a stall without a content failure is a healthy idle gap.
            return (false, "");
        }

        // ───── Retained transmit plumbing ────────────────────────────────

        /// <summary>Re-emit prime+open-request on sess=0x09 until device-init or
        /// budget exhaustion. Transmit nudge only — restart on "has display but
        /// never engaged" is the unified verdict's job (Context A sees no
        /// configJson state). The screenless terminal case parks (degraded).</summary>
        private void RetryS09IfNotEstablished()
        {
            if (_s09RetryRounds >= S09RetryMaxRounds)
            {
                MaybeParkScreenless();
                return;
            }

            // Stop retrying ONLY once the wheel has actually PUSHED its dashboard
            // list this session — NOT merely opened 0x09. On a slow-bring-up wheel
            // (radar/track-map dash on a CS-Pro base) the wheel device-inits 0x09
            // within ~1 s of our open request but only pushes the list once its
            // dashboard has finished loading (~35-40 s later). The old gate stopped
            // at DeviceInitiated (the open itself), so the request landed ~35 s too
            // early, the wheel opened 0x09 with nothing to enumerate, and the list
            // never arrived — dropdown "(none)", UI stuck on "wheel state not yet
            // ready" (wire trace: 6c80 request at t=5.7s, wheel 0x09 open type=0x81
            // at t=6.0s, dashboard load at t=41s, no list ever pushed). Re-emit
            // prime+open across the full backoff window (~56 s) until the LIVE
            // (this-session, not cross-session-cached) list lands. A screenless wheel
            // never pushes one and falls through to MaybeParkScreenless at
            // S09RetryMaxRounds below, unchanged.
            bool liveListArrived =
                (_sender.ConfigJson?.LiveState?.ConfigJsonList?.Count ?? 0) > 0;
            if (liveListArrived) return;

            int now = Environment.TickCount;
            if (_s09RetryRounds > 0)
            {
                int gateMs = S09BackoffMs[Math.Min(_s09RetryRounds - 1, S09BackoffMs.Length - 1)];
                if ((now - _s09RetryLastTickCount) < gateMs) return;
            }

            _s09RetryRounds++;
            _s09RetryLastTickCount = now;

            ushort recoverySeq = (ushort)(0x000B + _s09RetryRounds * 0x10);
            MozaLog.Warn(
                $"[AZOM] sess=0x09 not yet device-initiated; retry round " +
                $"{_s09RetryRounds}/{S09RetryMaxRounds} (open-seq=0x{recoverySeq:X4})");

            try
            {
                _sender.SendConfigSessionPrime(0x09, (ushort)(0x0001 + _s09RetryRounds));
                SendConfigJsonOpenRequest(0x09, recoverySeq);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] sess=0x09 retry emit failed: {ex.Message}");
            }

            if (_s09RetryRounds >= S09RetryMaxRounds)
                MaybeParkScreenless();
        }

        /// <summary>Park (degraded) ONLY the genuinely screenless terminal case:
        /// no display sub-device ever appeared across the full sess=0x09 retry
        /// window, so there is nothing for telemetry to drive. A wheel WITH a
        /// display that still hasn't engaged is left to the unified verdict
        /// (restart, then park-on-cap with the power-cycle hint).</summary>
        private void MaybeParkScreenless()
        {
            if (_s09ScreenlessParked) return;
            if (_sender.DisplayDetected) return;
            // The sender's own latch is set ONLY by the wheel's one-shot 0x87
            // identity reply, which it sends once in answer to a 0x07 query. If
            // that reply lands before the sender is listening, the latch stays
            // false for the whole session and nothing re-queries — so it is not
            // sufficient evidence of a screenless wheel on its own. The device
            // prober's identity read is the corroborating source, and
            // MozaPlugin.IsDisplayDetected already fuses both.
            //
            // Bundle TFYW9H33 (W17/CS-Pro, 2026-08-23, "no display"): the 0x87
            // reply arrived at 10:01:41.296, one millisecond after the sender
            // logged "deferring telemetry start until display probe completes",
            // so it was never handled. Diagnostics then read
            // "DisplayDetected: sender=False probe=True" while the identity
            // block plainly showed "W17 Display" — and this parked a wheel that
            // has a screen, blaming a hardware trait rather than the race.
            if (MozaPlugin.Instance?.IsDisplayDetected == true) return;
            _s09ScreenlessParked = true;
            _sender.Recovery.Park(
                $"Screenless wheel — no display sub-device after {S09RetryMaxRounds} sess=0x09 rounds; " +
                "telemetry has nothing to drive. A wheel hot-swap or telemetry toggle will re-check.",
                degraded: true);
        }

        /// <summary>
        /// Nudge the wheel to retransmit missing configJson chunks once the
        /// gap has stalled past the passive-wait window. Transmit plumbing only
        /// — no restart escalation (the unified verdict owns that).
        /// </summary>
        private void DriveConfigJsonGapRetransmit()
        {
            if (_sender.ConfigJsonHasLastState) return;
            // Don't inject prime+open mid hot-switch burst — stomps the
            // catalog-END handshake.
            if (_sender.HotSwitchBurstPending) return;
            long gapTs = _sender.ConfigJsonLastForwardGapUtcTicks;
            if (gapTs == 0) return;

            long now = DateTime.UtcNow.Ticks;
            long gapAgeMs = (now - gapTs) / TimeSpan.TicksPerMillisecond;
            if (gapAgeMs < ConfigJsonGapPassiveWaitMs) return;

            long primeAgeTicks = now - Interlocked.Read(ref _configJsonLastPrimeRetryUtcTicks);
            long passiveWaitTicks = ConfigJsonGapPassiveWaitMs * TimeSpan.TicksPerMillisecond;
            if (Interlocked.Read(ref _configJsonLastPrimeRetryUtcTicks) != 0 && primeAgeTicks < passiveWaitTicks)
                return;

            // Stop nudging once the cap is reached — the unified verdict's
            // "no configJson state past grace" path will restart if needed.
            if (_configJsonGapTickEscalations >= ConfigJsonGapTickEscalationCap)
                return;

            int recoveryOpenSeq = unchecked((ushort)(0x100 + _configJsonGapCount));
            int primeSeq = unchecked((ushort)(0x200 + _configJsonGapCount));
            byte session = 0x09;
            // Prefer 0x0a if the wheel's been talking on it instead.
            if (_sender.Session09InboundSeq == 0 && Interlocked.Read(ref _configJsonLastChunkUtcTicks) != 0)
                session = 0x0a;

            try
            {
                MozaLog.Warn(
                    $"[AZOM] sess=0x{session:X2} configJson gap stale " +
                    $"({gapAgeMs}ms >= {ConfigJsonGapPassiveWaitMs}ms passive-wait) — " +
                    $"prime + open-request (open seq=0x{recoveryOpenSeq:X4}, " +
                    $"nudge {_configJsonGapTickEscalations + 1}/{ConfigJsonGapTickEscalationCap})");
                _sender.SendConfigSessionPrime(session, (ushort)primeSeq);
                SendConfigJsonOpenRequest(session, (ushort)recoveryOpenSeq);
                Interlocked.Exchange(ref _configJsonLastPrimeRetryUtcTicks, now);
                _configJsonGapTickEscalations++;
                if (_configJsonGapCount == 0) _configJsonGapCount = 1;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] sess=0x{session:X2} configJson retransmit nudge failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Three-tier gap recovery from inbound dispatch on a forward gap:
        /// passive wait (wheel auto-retransmits) -> prime+open nudge. Restart
        /// is no longer a tier here — the unified verdict owns it.
        /// </summary>
        public void HandleConfigJsonGap(byte session, int seq)
        {
            _configJsonGapCount++;
            long nowTicks = DateTime.UtcNow.Ticks;
            // Interlocked like every other *UtcTicks here: Reset() zeroes this from
            // the Stop thread, and an x86 torn read would fake a huge episode.
            long episodeStart = Interlocked.CompareExchange(ref _configJsonGapEpisodeStartUtcTicks, nowTicks, 0);
            if (episodeStart == 0) episodeStart = nowTicks;
            bool haveCachedState = _sender.ConfigJsonHasLastState;
            string tag = $"sess=0x{session:X2}";
            string cachedTag = haveCachedState ? "cached-state" : "no-state";

            if (haveCachedState)
            {
                long episodeMs = (nowTicks - episodeStart) / TimeSpan.TicksPerMillisecond;
                MozaLog.Warn(
                    $"[AZOM] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                    $"buffer preserved, keeping cached state (episode {episodeMs}ms)");
                // The wheel retransmits the missing chunks on our dup-acks and
                // the episode heals within ~2 s (StateReady resets tracking).
                // Escalate only when it stays unhealed past the stall window.
                if (episodeMs >= ConfigJsonGapStallEscalateMs)
                    _sender.ScheduleLibrarySyncRestart(
                        $"configJson stream stalled (gap #{_configJsonGapCount}, {episodeMs}ms unhealed)");
                return;
            }

            if (_sender.HotSwitchBurstPending)
            {
                MozaLog.Debug(
                    $"[AZOM] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
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
                    $"[AZOM] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                    $"waiting up to {ConfigJsonGapPassiveWaitMs}ms for wheel auto-retransmit " +
                    $"(gap age={gapAgeTicks / TimeSpan.TicksPerMillisecond}ms)");
                return;
            }

            long primeAgeTicks = now - Interlocked.Read(ref _configJsonLastPrimeRetryUtcTicks);
            if (_configJsonGapCount <= ConfigJsonGapPrimeRetryAt
                && Interlocked.Read(ref _configJsonLastPrimeRetryUtcTicks) != 0
                && primeAgeTicks < passiveWaitTicks)
            {
                MozaLog.Debug(
                    $"[AZOM] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
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
                        $"[AZOM] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                        $"passive wait expired ({gapAgeTicks / TimeSpan.TicksPerMillisecond}ms) — " +
                        $"prime + open-request (open seq=0x{recoveryOpenSeq:X4})");
                    _sender.SendConfigSessionPrime(session, (ushort)primeSeq);
                    SendConfigJsonOpenRequest(session, (ushort)recoveryOpenSeq);
                    Interlocked.Exchange(ref _configJsonLastPrimeRetryUtcTicks, now);
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] {tag} configJson recovery emit failed: {ex.Message}");
                }
                return;
            }

            // Past the nudge tier: log and wait for the next chunk; if state
            // never materialises, the unified verdict (no configJson state past
            // grace) restarts.
            MozaLog.Warn(
                $"[AZOM] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                "nudge budget spent — waiting for chunk/state (display watchdog will restart if it never arrives)");
        }

        // ───── Diagnostics surface ────────────────────────────────────────

        /// <summary>Human-readable engagement view for the Diagnostics tab.</summary>
        public string DisplayEngagementText()
        {
            int catalog = _sender.CatalogCount;
            bool state = _sender.ConfigJsonHasLastState;
            int target = _lastKind4Slot;
            int reported = _sender.WheelReportedSlot;

            string roundTrip;
            if (target < 0)
                roundTrip = "n/a (no switch emitted)";
            else if (reported == target)
                roundTrip = "ok";
            else if (reported >= 0)
                roundTrip = $"ok (wheel authoritative on slot {reported})";
            else
                roundTrip = "pending (no echo yet)";

            // Slot state is authoritative / positive-only and no longer gates the
            // engagement verdict (see EvaluateEngagement Context B); engagement is
            // catalog + configJson establishment.
            bool engaged = catalog > 0 && state;
            long nowUtc = DateTime.UtcNow.Ticks;
            long s01 = Interlocked.Read(ref _session01LastInboundUtcTicks);
            long s02 = Interlocked.Read(ref _session02LastInboundUtcTicks);
            long s02ack = Interlocked.Read(ref _session02LastAckUtcTicks);
            string age(long stamp) => stamp == 0
                ? "never"
                : $"{(nowUtc - stamp) / TimeSpan.TicksPerSecond}s";
            // Which lane Context C currently judges flips with
            // ResolveTierDefSession(), so name it here — otherwise the s01/s02
            // ages below can't be read against the restart verdict.
            byte tierDefSes = _sender.ResolveTierDefSession();
            byte mirrorSes = tierDefSes == _sender.FlagByte && _sender.FlagByte != _sender.MgmtPort
                ? _sender.MgmtPort
                : _sender.FlagByte;
            return $"{(engaged ? "yes" : "no")} (catalog={catalog} state={state} slotRoundTrip={roundTrip} " +
                   $"s09devinit={(_sender.WheelReadyObserved ? "yes" : "no")} " +
                   $"s09rounds={_s09RetryRounds}/{S09RetryMaxRounds} " +
                   $"s01inbound={age(s01)} s02inbound={age(s02)} s02ack={age(s02ack)} " +
                   $"watchdogLane=tierDef:0x{tierDefSes:X2}/mirror:0x{mirrorSes:X2})";
        }

        // ───── Helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Host-initiated session-open for the configJson channel (port 9).
        /// Uses configJson-specific magic <c>7c 1e 6c 80</c> — upload-style
        /// <c>7c 23 46 80</c> does NOT trigger wheel device-init for 0x09.
        /// Addressed to the sender's current target device so the trigger reaches
        /// the actual display: the wheel screen (0x17), a base-bridged CM2 (0x14),
        /// or a standalone-USB CM2 (0x12). The prime + configJson reply already
        /// follow <see cref="TelemetrySender.TargetDeviceId"/>; hardcoding the
        /// open-request to the wheel left CM2 targets without their device-init
        /// trigger, so sess=0x09 never opened and the dashboard never engaged.
        /// </summary>
        public void SendConfigJsonOpenRequest(byte port, ushort seq)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x0A,
                MozaProtocol.TelemetrySendGroup, _sender.TargetDeviceId,
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
