using System;
using System.Threading;

namespace MozaPlugin.Telemetry.Lifecycle
{
    /// <summary>
    /// Hot-renegotiation burst state machine. When the user switches
    /// dashboards and <see cref="Enabled"/> is true, the wheel's
    /// sessions 0x01/0x02/0x03 stay open and the sender emits N paced
    /// tier-def re-applications instead of doing a full Stop+Start (which
    /// would pay the ~11 s sess=0x09 settle wait). Mirrors PitHouse's
    /// post-switch burst pattern: 3-13 tier-def emissions ~1 s apart.
    ///
    /// Per-instance state — re-armed on every switch via <see cref="ArmBurst"/>.
    /// </summary>
    internal sealed class HotSwitchCoordinator
    {
        /// <summary>Burst length. PitHouse captures show 3 emissions for
        /// small dashboards and up to 13 for multi-package; 4 covers the
        /// small case with margin. Each emission rebuilds tier-def with
        /// the wheel's most-recent END marker, so even if the first
        /// emission echoes a stale END the wheel hadn't pushed yet, a
        /// later emission picks up the updated value and the wheel binds
        /// then.</summary>
        public const int EmissionCount = 4;

        /// <summary>Min spacing between subsequent emissions in the burst.
        /// First emission has its own gating logic (catalog END handshake
        /// + fallback timeout) and ignores this.</summary>
        public const int EmissionSpacingMs = 1000;

        /// <summary>If the wheel hasn't pushed any catalog activity within
        /// this many ms of arming the burst, fire the first emission
        /// anyway. Covers wheel-side switches where the wheel skipped its
        /// post-switch catalog push (e.g., switching to a slot it was
        /// already on).</summary>
        public const int FirstEmissionFallbackMs = 1500;

        // ── State ─────────────────────────────────────────────────────────
        private int _pendingReemit;
        private int _armTickMs;
        private int _lastEmissionTickMs;

        /// <summary>Feature flag. When false, dashboard switches cycle the
        /// full Stop+Start pipeline; when true, switches stay in this hot
        /// path. Set from <c>MozaPluginSettings.EnableHotRenegotiation</c>.</summary>
        public bool Enabled { get; set; }

        public bool IsBurstPending => Volatile.Read(ref _pendingReemit) != 0;
        public int RemainingEmissions => Math.Max(0, Volatile.Read(ref _pendingReemit));
        public int ArmTickMs => _armTickMs;
        public int LastEmissionTickMs => _lastEmissionTickMs;

        /// <summary>Queue a fresh burst. Resets pacing so the first
        /// emission runs the catalog-END handshake gate rather than the
        /// pacing gate.</summary>
        public void ArmBurst()
        {
            _armTickMs = Environment.TickCount;
            _lastEmissionTickMs = 0;
            Interlocked.Exchange(ref _pendingReemit, EmissionCount);
        }

        /// <summary>Decide whether the tick handler should emit a tier-def
        /// re-application this tick. Encapsulates the catalog-END handshake
        /// gate (first emission) and the EmissionSpacingMs pacing gate
        /// (subsequent emissions).
        ///
        /// Parameters come from <see cref="Frames.ChannelCatalogParser"/>:
        /// <paramref name="catalogLastActivityTickMs"/> from
        /// <c>LastActivityMs</c> (0 = never), and
        /// <paramref name="catalogWheelEndMarkerTickMs"/> from
        /// <c>LastWheelEndMarkerTickMs</c> (0 = never).
        ///
        /// Returns true ONLY when the caller should call
        /// <see cref="MarkEmission"/> after a successful emit. False
        /// covers all "wait more" cases.</summary>
        public bool ShouldEmitThisTick(int catalogLastActivityTickMs,
                                       int catalogWheelEndMarkerTickMs)
        {
            if (Volatile.Read(ref _pendingReemit) == 0) return false;

            int now = Environment.TickCount;
            bool isFirstEmission = _lastEmissionTickMs == 0;

            if (isFirstEmission)
            {
                // First-emission gate: wait for the wheel's END marker
                // handshake before firing. The END u32 the host echoes on
                // every tier-def emission must match what the wheel just
                // pushed; firing too early means the first emission
                // echoes a stale END the wheel rejects.
                bool newEndSinceArm = catalogWheelEndMarkerTickMs != 0
                    && (catalogWheelEndMarkerTickMs - _armTickMs) > 0;
                if (newEndSinceArm) return true;

                // Wheel pushing catalog but hasn't sent END yet — no
                // timeout while valid traffic is in flight.
                bool newActivitySinceArm = catalogLastActivityTickMs != 0
                    && (catalogLastActivityTickMs - _armTickMs) > 0;
                if (newActivitySinceArm) return false;

                // No activity at all yet; wait the fallback window.
                int sinceArm = now - _armTickMs;
                if (sinceArm < FirstEmissionFallbackMs) return false;

                // Fallback window elapsed with no wheel activity — fire
                // anyway so we don't deadlock on a wheel that skipped its
                // post-switch push.
                return true;
            }

            // Subsequent emissions: pace ~1 s apart.
            return (now - _lastEmissionTickMs) >= EmissionSpacingMs;
        }

        /// <summary>Record that the caller successfully emitted a tier-def
        /// re-application. Stamps the pacing timestamp and decrements the
        /// remaining counter. Returns the new remaining count (0 = burst
        /// complete).</summary>
        public int MarkEmission()
        {
            _lastEmissionTickMs = Environment.TickCount;
            int newRemaining = Interlocked.Decrement(ref _pendingReemit);
            if (newRemaining <= 0)
            {
                // Burst done. Clear so the next switch can re-arm cleanly.
                Interlocked.Exchange(ref _pendingReemit, 0);
                return 0;
            }
            return newRemaining;
        }
    }
}
