using System;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry.Watchdog
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
    ///   (A) catalog present  AND  configJson device-init/state present, AND
    ///   (B) the slot round-trips — after the host emits a kind=4 to slot N,
    ///       WheelReportedSlot reaches N within SlotRoundTripWindowMs.
    /// Filler acks/keepalives and inbound stalls are consumed only as liveness
    /// inputs that REINFORCE a content-absent verdict; they never clear one and
    /// never fire alone against a content-proven wheel.
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
        // After the host emits a kind=4 to slot N, the wheel must report slot N
        // within this window or the round-trip is judged failed. 4× the
        // PostSwitchCatalogConvergence sample interval (3 s) and well under its
        // 30 s deadline; a healthy switch settles in <=1.5 s, so this never
        // clips a slow-but-healthy bind.
        private const int SlotRoundTripWindowMs = 12_000;
        // A not-engaged verdict must persist this long before it escalates to a
        // restart, so a momentary gap mid-catalog-push can't trigger one.
        private const int NotEngagedConfirmMs = 3_000;
        // Self-throttle between restart requests (mirror of
        // RecoveryDispatcher.DebounceMs); avoids recomputing/log-spamming a
        // verdict while our own restart is settling.
        private const int RestartCooldownMs = 30_000;
        // Liveness staleness thresholds (reinforcement inputs). PH bridge
        // captures show p999 inter-frame gap of 14 s (sess=0x02) / ~5 s
        // (sess=0x01); 20 s leaves headroom past legitimate quiet.
        private const int S02StallThresholdMs = 20_000;
        private const int S01StallThresholdMs = 20_000;
        // Don't trust inbound-bytes liveness on mgmt while a wheel-initiated
        // CLOSE is recent — the close is the wheel rejecting the session.
        private const int S01PostCloseSettleMs = 15_000;

        // ── liveness / rejection feeder state (Interlocked on 64-bit) ─────
        private long _session01EngagedUtcTicks;
        private long _session01LastInboundUtcTicks;
        private long _session01LastCloseUtcTicks;
        private long _session02FirstInboundUtcTicks;
        private long _session02LastInboundUtcTicks;
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
            // Stamp the mgmt close time unconditionally — the verdict reads this
            // to refuse trusting inbound-bytes liveness for S01PostCloseSettleMs.
            if (session == _sender.MgmtPort)
                Interlocked.Exchange(ref _session01LastCloseUtcTicks, DateTime.UtcNow.Ticks);

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
            _activeStateEnteredTickCount = 0;

            Interlocked.Exchange(ref _lastKind4EmitUtcTicks, 0);
            _lastKind4Slot = -1;
            Interlocked.Exchange(ref _lastRestartRequestUtcTicks, 0);
            _notEngagedFirstSeenTickCount = 0;

            _s09RetryRounds = 0;
            _s09RetryLastTickCount = 0;
            _s09ScreenlessParked = false;

            _configJsonGapCount = 0;
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
            if (!_sender.ConfigJsonHasLastState)
                return (true, "no configJson device-init/state past grace");

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

            ushort recoverySeq = (ushort)(0x000B + _s09RetryRounds * 0x10);
            MozaLog.Warn(
                $"[AZOM] sess=0x09 not yet device-initiated; retry round " +
                $"{_s09RetryRounds}/{S09RetryMaxRounds} (open-seq=0x{recoverySeq:X4})");

            try
            {
                _sender.SendSessionPrime(0x09, (ushort)(0x0001 + _s09RetryRounds));
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
                    $"prime + open-request (open seq=0x{recoveryOpenSeq:X4}, prime seq=0x{primeSeq:X4}, " +
                    $"nudge {_configJsonGapTickEscalations + 1}/{ConfigJsonGapTickEscalationCap})");
                _sender.SendSessionPrime(session, (ushort)primeSeq);
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
            bool haveCachedState = _sender.ConfigJsonHasLastState;
            string tag = $"sess=0x{session:X2}";
            string cachedTag = haveCachedState ? "cached-state" : "no-state";

            if (haveCachedState)
            {
                MozaLog.Warn(
                    $"[AZOM] {tag} configJson gap #{_configJsonGapCount} ({cachedTag}): " +
                    "buffer preserved, keeping cached state — no recovery action");
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
                        $"prime + open-request (open seq=0x{recoveryOpenSeq:X4}, prime seq=0x{primeSeq:X4})");
                    _sender.SendSessionPrime(session, (ushort)primeSeq);
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
            return $"{(engaged ? "yes" : "no")} (catalog={catalog} state={state} slotRoundTrip={roundTrip})";
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
