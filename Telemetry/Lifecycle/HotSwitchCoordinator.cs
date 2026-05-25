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
        /// <summary>Burst-length floor. Always emit at least this many tier-
        /// def re-applications before considering an early exit. Three matches
        /// PitHouse's small-dashboard cadence. The minimum exists because a
        /// single emission isn't enough — if the first one races the wheel's
        /// END-marker push, the wheel may echo a stale generation and we need
        /// at least one follow-up to converge.</summary>
        public const int MinEmissions = 3;

        /// <summary>Burst-length cap. Stops the burst even when the wheel
        /// keeps reporting "not fully bound" — covers a wheel that's genuinely
        /// pathological so we don't spend forever cycling. Eight covers the
        /// PitHouse 3-13 range with margin without risking saturation.</summary>
        public const int MaxEmissions = 8;

        /// <summary>Legacy fixed burst length. Used when the caller passes
        /// <c>null</c> to <see cref="MarkEmission"/> — i.e., when bind
        /// completeness isn't measurable for this era (V0Url, V2Compact).
        /// Preserves the prior pre-adaptive behaviour for those wheels.</summary>
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
        // Pending count is the residual emission budget. Arm sets it to
        // MaxEmissions; the adaptive logic in MarkEmission clamps to MinEmissions
        // when the wheel reports bound, or EmissionCount in legacy mode.
        private int _pendingReemit;
        private int _emissionsSent;
        private int _armTickMs;
        private int _lastEmissionTickMs;

        /// <summary>Feature flag. When false, dashboard switches cycle the
        /// full Stop+Start pipeline; when true, switches stay in this hot
        /// path. Set from <c>MozaPluginSettings.EnableHotRenegotiation</c>.</summary>
        public bool Enabled { get; set; }

        public bool IsBurstPending => Volatile.Read(ref _pendingReemit) != 0;
        public int RemainingEmissions => Math.Max(0, Volatile.Read(ref _pendingReemit));
        public int EmissionsSent => Volatile.Read(ref _emissionsSent);
        public int ArmTickMs => _armTickMs;
        public int LastEmissionTickMs => _lastEmissionTickMs;

        /// <summary>Queue a fresh burst. Resets pacing so the first
        /// emission runs the catalog-END handshake gate rather than the
        /// pacing gate. The pending counter is set to <see cref="MaxEmissions"/>
        /// up front; <see cref="MarkEmission"/> clamps it down based on
        /// per-emission bind reports.</summary>
        public void ArmBurst()
        {
            _armTickMs = Environment.TickCount;
            _lastEmissionTickMs = 0;
            Interlocked.Exchange(ref _emissionsSent, 0);
            Interlocked.Exchange(ref _pendingReemit, MaxEmissions);
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
        /// complete).
        ///
        /// <para><paramref name="boundComplete"/> drives the adaptive cap:</para>
        /// <list type="bullet">
        /// <item><c>null</c> — bind info isn't available for this era (V0Url,
        /// V2Compact). Caps the burst at the legacy <see cref="EmissionCount"/>
        /// (4) to preserve pre-adaptive behaviour for older wheels.</item>
        /// <item><c>true</c> — wheel-catalog covers every channel we emitted.
        /// Stops early after at least <see cref="MinEmissions"/> (3) have gone
        /// out — saves wire bandwidth on the common case where the first
        /// emission already binds correctly.</item>
        /// <item><c>false</c> — channels still unbound. Keep emitting up to
        /// <see cref="MaxEmissions"/> (8) so a wheel that's slowly publishing
        /// its catalog has more chances to converge.</item>
        /// </list>
        /// </summary>
        public int MarkEmission(bool? boundComplete = null)
        {
            _lastEmissionTickMs = Environment.TickCount;
            int newSent = Interlocked.Increment(ref _emissionsSent);
            int newRemaining = Interlocked.Decrement(ref _pendingReemit);

            int cap = boundComplete switch
            {
                null  => EmissionCount,                              // legacy
                true  => Math.Max(MinEmissions, EmissionCount),      // bound: stop at min
                false => MaxEmissions,                               // not bound: extend
            };
            // Bound-with-min-reached: clamp the residual to zero.
            if (boundComplete == true && newSent >= MinEmissions)
            {
                Interlocked.Exchange(ref _pendingReemit, 0);
                return 0;
            }

            // Hit the legacy cap (no bind info) — stop.
            if (boundComplete == null && newSent >= cap)
            {
                Interlocked.Exchange(ref _pendingReemit, 0);
                return 0;
            }

            // Hit the hard max — stop regardless.
            if (newSent >= MaxEmissions || newRemaining <= 0)
            {
                Interlocked.Exchange(ref _pendingReemit, 0);
                return 0;
            }

            return newRemaining;
        }
    }
}
