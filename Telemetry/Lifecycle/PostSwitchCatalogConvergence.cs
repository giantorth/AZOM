using System;

namespace MozaPlugin.Telemetry.Lifecycle
{
    /// <summary>
    /// Post-switch catalog convergence watcher. Armed by every committed
    /// dashboard switch (host kind=4 from <see cref="TelemetrySender.SwitchToProfile"/>,
    /// or wheel-initiated via <see cref="Display.WheelSlotTracker"/>); polls the
    /// host's view of the wheel's channel catalog and nudges the wheel with a
    /// kind=4 re-emit until <see cref="StableSampleThreshold"/> consecutive
    /// identical catalog signatures arrive. Each nudge is a re-emit of the
    /// wheel's CURRENT slot — some firmwares re-run their dashboard-load on
    /// every kind=4 even when the slot already matches, which causes them to
    /// re-publish the full URL catalog. After enough identical samples we
    /// trust the host's catalog matches the wheel's, and disarm.
    ///
    /// Bypasses the wheel-on-target shortcut that <see cref="CatalogResyncProbe"/>
    /// uses — that shortcut is correct for the reactive "fix a single
    /// unbound-channel emission" use case, but defeats the goal here, which
    /// is to force a fresh catalog push even when the wheel is already on
    /// our target slot.
    ///
    /// Per-instance state — destroyed on plugin reload. A new
    /// <see cref="TelemetrySender.SwitchToProfile"/> while armed restarts the
    /// counter from the new switch's slot.
    /// </summary>
    internal sealed class PostSwitchCatalogConvergence
    {
        /// <summary>Minimum spacing between catalog samples (and the nudges
        /// that follow each non-final sample). 3 s leaves time for the
        /// wheel to receive our kind=4, run its dashboard-load, and re-push
        /// the catalog before we sample the next round (observed wheel
        /// post-switch settle ≤1.5 s on healthy connect).</summary>
        public const int SampleIntervalMs = 3_000;

        /// <summary>Number of consecutive identical catalog signatures
        /// required to declare convergence. Three is enough to trust the
        /// catalog isn't quietly missing a chunk that's still in flight.</summary>
        public const int StableSampleThreshold = 3;

        /// <summary>Hard deadline after arm. Past this we give up and
        /// disarm — covers wheels that just never converge for whatever
        /// firmware reason (the existing reactive watchdogs take over from
        /// here).</summary>
        public const int DeadlineMs = 30_000;

        /// <summary>Maximum nudges per arm cycle. Cap separately from the
        /// deadline so a fast-cycle wheel can't get spammed if something
        /// keeps changing the catalog.</summary>
        public const int MaxNudges = 8;

        // Arm() fires from the UI thread (SwitchToProfile) and the serial-read
        // thread (wheel-initiated switch); TickIfArmed from the timer thread.
        // All state access is serialized here so the 64-bit timestamp fields
        // aren't torn on x86. No method calls out to the sender under the lock,
        // so there is no re-entrancy/deadlock risk (the caller acts on the
        // returned TickDecision outside this class).
        private readonly object _lock = new object();
        private bool _armed;
        private int _targetSlot = -1;
        private long _armedUtcTicks;
        private long _lastSampleUtcTicks;
        private int _lastSignature;
        private bool _hasSample;
        private int _matchCount;
        private int _nudgesSent;

        public bool IsArmed { get { lock (_lock) return _armed; } }
        public int TargetSlot { get { lock (_lock) return _targetSlot; } }
        public int MatchCount { get { lock (_lock) return _matchCount; } }
        public int NudgesSent { get { lock (_lock) return _nudgesSent; } }

        /// <summary>Arm the watcher against a specific slot. Resets all
        /// counters — a switch arriving while a prior convergence is still
        /// in flight cancels the prior cycle and starts fresh.</summary>
        public void Arm(int slot, long nowUtcTicks)
        {
            lock (_lock)
            {
                _armed = true;
                _targetSlot = slot;
                _armedUtcTicks = nowUtcTicks;
                // Treat arm as the first "sample time" so the first nudge
                // doesn't fire until SampleIntervalMs has elapsed — gives the
                // HOT burst a chance to land first.
                _lastSampleUtcTicks = nowUtcTicks;
                _hasSample = false;
                _matchCount = 0;
                _nudgesSent = 0;
                _lastSignature = 0;
            }
        }

        /// <summary>Cancel the watcher. Called from <c>Stop()</c> /
        /// <c>ResetBindingTracking</c> so a torn-down pipeline doesn't try
        /// to nudge against a stale slot.</summary>
        public void Disarm()
        {
            lock (_lock)
            {
                _armed = false;
                _targetSlot = -1;
            }
        }

        /// <summary>Caller drives this each steady-state tick. Returns true
        /// when the caller should emit a kind=4 nudge to <see cref="TargetSlot"/>
        /// AND record the timestamp for the next sample. Returns false in
        /// every "not yet" / "we're done" / "wait for something else" case.
        /// </summary>
        /// <param name="nowUtcTicks">Current <c>DateTime.UtcNow.Ticks</c>.</param>
        /// <param name="currentSignature">Hash of the host's current catalog
        /// view — typically computed from the LiveCatalog URL list.</param>
        /// <param name="busy">True while the HOT burst is pending or some
        /// other higher-priority cycle owns sess=0x01/0x02. Defers sampling
        /// so we don't measure mid-burst.</param>
        public TickDecision TickIfArmed(long nowUtcTicks, int currentSignature, bool busy)
        {
            lock (_lock)
            {
                if (!_armed) return TickDecision.NoAction;

                if ((nowUtcTicks - _armedUtcTicks) >= (long)DeadlineMs * TimeSpan.TicksPerMillisecond)
                {
                    Disarm();
                    return TickDecision.DeadlineExpired;
                }

                // Don't sample / nudge while HOT burst owns the wire.
                // Slide the "last sample" forward so the post-burst gap is
                // measured from busy-clear, not from arm.
                if (busy)
                {
                    _lastSampleUtcTicks = nowUtcTicks;
                    return TickDecision.NoAction;
                }

                // Spacing gate.
                long intervalTicks = (long)SampleIntervalMs * TimeSpan.TicksPerMillisecond;
                if ((nowUtcTicks - _lastSampleUtcTicks) < intervalTicks)
                    return TickDecision.NoAction;

                _lastSampleUtcTicks = nowUtcTicks;

                // Take the sample. Update match streak.
                if (!_hasSample)
                {
                    _lastSignature = currentSignature;
                    _hasSample = true;
                    _matchCount = 1;
                }
                else if (currentSignature == _lastSignature)
                {
                    _matchCount++;
                    if (_matchCount >= StableSampleThreshold)
                    {
                        Disarm();
                        return TickDecision.Converged;
                    }
                }
                else
                {
                    // Catalog moved — restart the streak with the new value.
                    _lastSignature = currentSignature;
                    _matchCount = 1;
                }

                if (_nudgesSent >= MaxNudges)
                {
                    Disarm();
                    return TickDecision.MaxNudgesReached;
                }
                _nudgesSent++;
                return TickDecision.EmitNudge;
            }
        }

        public enum TickDecision
        {
            NoAction,
            EmitNudge,
            Converged,
            DeadlineExpired,
            MaxNudgesReached,
        }
    }
}
