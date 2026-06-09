using System;
using System.Collections.Generic;
using System.Threading;

namespace MozaPlugin.Telemetry.Lifecycle
{
    /// <summary>
    /// Single funnel for all watchdog-driven full-restart escalations. Replaces
    /// per-watchdog <c>ThreadPool.QueueUserWorkItem(_ =&gt; sender.RestartForSwitch())</c>
    /// calls. Three concerns rolled into one:
    ///
    /// <list type="number">
    /// <item><b>Debounce</b> — once a restart has been queued, ignore additional
    /// escalations for <see cref="DebounceMs"/>. Closes the race where
    /// sess=0x01 and sess=0x02 watchdogs (both watching the same wheel)
    /// exhausted simultaneously and queued two Stop+Start cycles back-to-back;
    /// the second's <c>Stop</c> flush would discard the first's in-flight
    /// kind=4 / tier-def frames.</item>
    /// <item><b>Rate limit</b> — at most <see cref="RestartCapPerWindow"/>
    /// restarts inside a sliding <see cref="WindowMs"/> window. The N+1th
    /// escalation parks the pipeline instead of looping forever. Prevents
    /// silent multi-minute escalation storms on wheels that re-emerge from
    /// Restart still broken.</item>
    /// <item><b>Park signalling</b> — sess=0x09 retry budget exhaustion now
    /// routes through the same call so the UI / binding coordinator only
    /// has to subscribe to one event.</item>
    /// </list>
    ///
    /// <para>Threading: <see cref="RequestRestart"/> is safe from any thread
    /// (watchdog tick on the timer ThreadPool, inbound dispatch on the serial
    /// read thread). The actual <c>RestartForSwitch</c> / <c>Stop</c> work is
    /// always queued onto a worker so the caller never blocks on serial I/O
    /// or the 11 s silence gate inside <c>StartInner</c>.</para>
    /// </summary>
    internal sealed class RecoveryDispatcher
    {
        // ── Tuning ────────────────────────────────────────────────────────
        /// <summary>Once a restart has been queued, subsequent escalations
        /// inside this window are dropped with a debug log. Set ~30 s so the
        /// full Stop+Start+11 s silence + first-tick-pass has time to land
        /// before another watchdog fires.</summary>
        internal const int DebounceMs = 30_000;

        /// <summary>Sliding window for restart-cap counting.</summary>
        internal const int WindowMs = 300_000;  // 5 min

        /// <summary>Max restarts inside <see cref="WindowMs"/> before park
        /// escalation. Three covers transient wheel hiccups (one bad pass +
        /// one retry + one safety margin) without letting a genuinely-broken
        /// state machine loop forever.</summary>
        internal const int RestartCapPerWindow = 3;

        /// <summary>After parking, wait this long then make ONE bounded
        /// re-attempt (Start) — many park causes (slow boot, transient USB,
        /// wheel mid-reboot) self-resolve within a couple of minutes. Strictly
        /// one-shot per park episode (<see cref="_parkRetryUsed"/>) so it can
        /// never reintroduce the loop Park exists to stop. Cleared by Reset.</summary>
        internal const int ParkAutoRetryMs = 180_000;  // 3 min

        /// <summary>Connection-independent flap window. Unlike <see cref="WindowMs"/>,
        /// this is NOT cleared by a reconnect-edge <see cref="Reset(bool)"/>, so a
        /// connection that keeps dropping → reconnecting → restarting cannot dodge
        /// the cap by clearing the per-cycle budget on every reconnect. Only a
        /// deliberate user re-enable / hardware hot-swap clears it.</summary>
        internal const int FlapWindowMs = 600_000;     // 10 min
        /// <summary>Restarts within <see cref="FlapWindowMs"/> across connect cycles
        /// before the pipeline parks as "flapping".</summary>
        internal const int FlapCapPerWindow = 6;

        // ── State (guarded by _lock) ──────────────────────────────────────
        private readonly object _lock = new object();
        private long _lastEscalationUtcTicks;
        private readonly Queue<long> _recentRestartTicks = new Queue<long>();
        private readonly Queue<long> _flapRestartTicks = new Queue<long>();
        private bool _parked;
        private string? _parkReason;
        // True when the park is a benign "degraded" state (e.g. a screenless wheel
        // that has no telemetry session) rather than a failure — drives a calmer,
        // non-alarming status string/banner instead of "stopped after failures".
        private bool _parkIsDegraded;
        // One-shot post-park re-attempt: latch + timer (guarded by _lock for the
        // latch; Timer.Change is itself thread-safe).
        private bool _parkRetryUsed;
        private readonly Timer _parkRetryTimer;

        private readonly TelemetrySender _sender;

        public RecoveryDispatcher(TelemetrySender sender)
        {
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _parkRetryTimer = new Timer(_ => OnParkAutoRetry(), null, Timeout.Infinite, Timeout.Infinite);
        }

        // Arm the one-shot post-park re-attempt. Caller holds _lock. No-op if the
        // single retry for this park episode has already been spent.
        private void ArmParkAutoRetryLocked()
        {
            if (_parkRetryUsed) return;
            try { _parkRetryTimer.Change(ParkAutoRetryMs, Timeout.Infinite); } catch { }
        }

        // Fires once ~ParkAutoRetryMs after a park. Un-parks with a fresh restart
        // budget and re-attempts Start exactly once; if that attempt parks again,
        // _parkRetryUsed stays set so there is no further auto-retry until Reset.
        private void OnParkAutoRetry()
        {
            lock (_lock)
            {
                if (!_parked || _parkRetryUsed) return;
                _parkRetryUsed = true;
                _parked = false;
                _parkReason = null;
                _parkIsDegraded = false;
                _recentRestartTicks.Clear();
                _lastEscalationUtcTicks = 0;
            }
            MozaLog.Warn("[AZOM] Recovery park auto-retry — re-attempting telemetry start once after park.");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _sender.Start(); }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Park auto-retry Start() raised: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        /// <summary>True once a watchdog escalation has crossed the rate-limit
        /// cap and the dispatcher has parked the pipeline. Cleared on
        /// <see cref="Reset"/> (cold-start, user toggle, wheel hot-swap).</summary>
        public bool IsParked
        {
            get { lock (_lock) return _parked; }
        }

        /// <summary>Human-readable reason the pipeline parked (verbatim from the
        /// escalation that tripped it), or null when not parked. Surfaced by the
        /// status banner / diagnostics so the user sees WHY telemetry stopped.</summary>
        public string? ParkReason
        {
            get { lock (_lock) return _parkReason; }
        }

        /// <summary>True when the current park is a benign "degraded" state (e.g.
        /// a screenless wheel) rather than a failure park. UI uses this to choose a
        /// calm "no display" message over "stopped after repeated failures".</summary>
        public bool ParkIsDegraded
        {
            get { lock (_lock) return _parkIsDegraded; }
        }

        /// <summary>True while a queued restart is still inside its debounce
        /// window — for UI / <c>PipelinePhase</c> consumers that want to
        /// reflect "recovery in flight" without having to listen to events.
        /// Auto-clears once the debounce window elapses; if recovery actually
        /// succeeded the next tick of <c>Active</c> state confirms it.</summary>
        public bool IsRecoveryInFlight
        {
            get
            {
                lock (_lock)
                {
                    if (_lastEscalationUtcTicks == 0) return false;
                    long ageMs = (DateTime.UtcNow.Ticks - _lastEscalationUtcTicks)
                        / TimeSpan.TicksPerMillisecond;
                    return ageMs < DebounceMs;
                }
            }
        }

        /// <summary>
        /// Request a full <see cref="TelemetrySender.RestartForSwitch"/>.
        /// Returns true if the restart was actually queued; false if debounced,
        /// rate-capped (pipeline parked instead), or the dispatcher itself is
        /// already in the parked state. <paramref name="reason"/> is logged
        /// verbatim so post-mortem analysis can identify which watchdog drove
        /// the escalation.
        /// </summary>
        public bool RequestRestart(string reason)
        {
            long now = DateTime.UtcNow.Ticks;
            bool queueRestart = false;
            bool queuePark = false;

            lock (_lock)
            {
                if (_parked)
                {
                    MozaLog.Debug(
                        $"[AZOM] Recovery restart dropped — pipeline already parked: {reason}");
                    return false;
                }

                long elapsedMs = (now - _lastEscalationUtcTicks) / TimeSpan.TicksPerMillisecond;
                if (_lastEscalationUtcTicks != 0 && elapsedMs < DebounceMs)
                {
                    MozaLog.Debug(
                        $"[AZOM] Recovery restart debounced " +
                        $"({elapsedMs} ms < {DebounceMs} ms since last escalation): {reason}");
                    return false;
                }

                long windowStartTicks = now - WindowMs * TimeSpan.TicksPerMillisecond;
                while (_recentRestartTicks.Count > 0 && _recentRestartTicks.Peek() < windowStartTicks)
                    _recentRestartTicks.Dequeue();
                long flapStartTicks = now - FlapWindowMs * TimeSpan.TicksPerMillisecond;
                while (_flapRestartTicks.Count > 0 && _flapRestartTicks.Peek() < flapStartTicks)
                    _flapRestartTicks.Dequeue();

                bool cycleCap = _recentRestartTicks.Count >= RestartCapPerWindow;
                bool flapCap = _flapRestartTicks.Count >= FlapCapPerWindow;
                if (cycleCap || flapCap)
                {
                    string capReason = (flapCap && !cycleCap)
                        ? $"flapping: {FlapCapPerWindow} restarts in {FlapWindowMs / 1000}s across connect cycles"
                        : $"{RestartCapPerWindow} restarts in {WindowMs / 1000}s";
                    MozaLog.Warn(
                        $"[AZOM] Recovery restart cap hit ({capReason}) — " +
                        $"parking pipeline rather than looping: {reason}");
                    _parked = true;
                    _parkReason = reason;
                    _parkIsDegraded = false; // a restart-cap park is a failure, not degraded
                    _lastEscalationUtcTicks = now;
                    ArmParkAutoRetryLocked();
                    queuePark = true;
                }
                else
                {
                    MozaLog.Warn($"[AZOM] Recovery restart initiated: {reason}");
                    _recentRestartTicks.Enqueue(now);
                    _flapRestartTicks.Enqueue(now);
                    _lastEscalationUtcTicks = now;
                    queueRestart = true;
                }
            }

            if (queuePark)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _sender.Stop(); }
                    catch (Exception ex)
                    {
                        MozaLog.Warn(
                            $"[AZOM] Recovery park Stop() raised: {ex.GetType().Name}: {ex.Message}");
                    }
                    try { _sender.RaiseDashboardPipelineParked(); } catch { }
                });
            }
            else if (queueRestart)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _sender.RestartForSwitch(); }
                    catch (Exception ex)
                    {
                        MozaLog.Warn(
                            $"[AZOM] Recovery RestartForSwitch raised: {ex.GetType().Name}: {ex.Message}");
                    }
                });
            }

            return queueRestart;
        }

        /// <summary>
        /// Explicit park escalation (e.g., sess=0x09 retry budget exhaustion).
        /// Doesn't consume restart budget — the caller has already decided no
        /// restart is going to help. Idempotent: subsequent calls are no-ops.
        /// </summary>
        public void Park(string reason, bool degraded = false)
        {
            bool fire;
            lock (_lock)
            {
                if (_parked)
                {
                    MozaLog.Debug($"[AZOM] Recovery park dropped — already parked: {reason}");
                    return;
                }
                _parked = true;
                _parkReason = reason;
                _parkIsDegraded = degraded;
                _lastEscalationUtcTicks = DateTime.UtcNow.Ticks;
                ArmParkAutoRetryLocked();
                fire = true;
            }
            if (!fire) return;
            MozaLog.Warn($"[AZOM] Recovery park initiated: {reason}");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _sender.Stop(); }
                catch (Exception ex)
                {
                    MozaLog.Warn(
                        $"[AZOM] Recovery park Stop() raised: {ex.GetType().Name}: {ex.Message}");
                }
                try { _sender.RaiseDashboardPipelineParked(); } catch { }
            });
        }

        /// <summary>
        /// Clear all rate-limit + parked state. Called on cold start (user
        /// toggled telemetry on/off, wheel hot-swap, etc.) so a fresh budget
        /// is available. Does NOT clear during normal Stop() — a Stop driven
        /// by a recovery restart must preserve the rate-limit window or the
        /// cap is unenforced.
        /// </summary>
        public void Reset(bool clearFlapHistory = true)
        {
            lock (_lock)
            {
                _lastEscalationUtcTicks = 0;
                _recentRestartTicks.Clear();
                _parked = false;
                _parkReason = null;
                _parkIsDegraded = false;
                _parkRetryUsed = false;
                try { _parkRetryTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                // Reconnect edge passes clearFlapHistory:false — a flapping link keeps
                // dropping+reconnecting, and clearing the flap window on every reconnect
                // would let it dodge the cross-cycle cap forever. Only a deliberate user
                // re-enable / hardware hot-swap (default true) wipes the flap history.
                if (clearFlapHistory)
                    _flapRestartTicks.Clear();
            }
        }
    }
}
