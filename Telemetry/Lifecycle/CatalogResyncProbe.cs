using System;

namespace MozaPlugin.Telemetry.Lifecycle
{
    /// <summary>
    /// Throttle state for the catalog re-sync probe: a host-side kind=4 emit
    /// to the wheel's currently-active slot, used to nudge a wheel that
    /// emitted an incomplete channel catalog at tier-def time. Re-applying
    /// the same slot tells some firmwares to re-run their dashboard-load
    /// sequence which re-advertises the full channel catalog.
    ///
    /// The decision logic (slot lookup, wheel-on-target shortcut, deferred
    /// emission via ThreadPool) lives in <see cref="TelemetrySender"/>; this
    /// class only owns the throttle timestamp + the "has fired in this
    /// instance" flag that <c>ApplyTelemetryDashboardFromProfile</c> reads to
    /// decide whether a previously-bound slot needs a refresh.
    ///
    /// Per-instance state — destroyed on plugin reload (NOT static like
    /// <see cref="SilenceGate"/>'s timestamps). A re-sync probe firing in
    /// instance A is irrelevant to instance B's first dashboard binding.
    /// </summary>
    internal sealed class CatalogResyncProbe
    {
        // ~8 s between probes so a stuck case can't produce a switch storm.
        private static readonly long MinIntervalTicks =
            8000L * TimeSpan.TicksPerMillisecond;

        private long _lastFiredUtcTicks;

        /// <summary>True once a probe has actually emitted in this instance.
        /// Used by <c>ApplyTelemetryDashboardFromProfile</c> to force a
        /// refresh even when the wheel reports it's already on the target
        /// slot (the probe alone doesn't always cause the wheel to re-push
        /// its catalog, so we may need a full Stop+Start).</summary>
        public bool HasFired => _lastFiredUtcTicks != 0;

        /// <summary>Check whether the throttle interval has elapsed since
        /// the last fire. Does NOT update the timestamp — call
        /// <see cref="MarkFired"/> only when the caller commits to emitting.
        /// </summary>
        public bool IsThrottleClear(long nowUtcTicks)
            => (nowUtcTicks - _lastFiredUtcTicks) >= MinIntervalTicks;

        /// <summary>Stamp the throttle. The caller already passed
        /// <see cref="IsThrottleClear"/> and is about to (or just did) emit
        /// the probe; the timestamp arms the next throttle window AND lights
        /// up <see cref="HasFired"/>.</summary>
        public void MarkFired(long nowUtcTicks) => _lastFiredUtcTicks = nowUtcTicks;
    }
}
