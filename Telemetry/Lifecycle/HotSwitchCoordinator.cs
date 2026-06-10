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
        /// completeness isn't measurable (V0 URL, or the V2 compact fallback
        /// with no wheel catalog). Preserves the prior pre-adaptive
        /// behaviour for those wheels.</summary>
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

        /// <summary>After the wheel's END marker has advanced past the
        /// pre-switch generation, wait this many ms of catalog quiet
        /// (no fresh chunks) before firing the first emission. Lets the
        /// wheel finish pushing the rest of its new-dashboard URL set
        /// after the END handshake — without this, emission #1 can race
        /// the URL push by ~750 ms and emit a tier-def shaped for a
        /// partial catalog (verified 2026-05-25: post-switch the wheel
        /// pushes its END marker before all URLs have landed, so a
        /// catalog-quiet wait is the only reliable "fully published"
        /// signal).</summary>
        public const int FirstEmissionQuietMs = 200;

        // ── State ─────────────────────────────────────────────────────────
        // Pending count is the residual emission budget. Arm sets it to
        // MaxEmissions; the adaptive logic in MarkEmission clamps to MinEmissions
        // when the wheel reports bound, or EmissionCount in legacy mode.
        private int _pendingReemit;
        private int _emissionsSent;
        private int _armTickMs;
        private int _lastEmissionTickMs;
        // Monotonic counter incremented on every ArmBurst call. Read by
        // ChannelCatalogParser as the switch-boundary signal: any commit
        // that observes a different arm count than the prior commit is the
        // FIRST commit since a new switch (REPLACE _liveCatalog). Subsequent
        // commits at the same arm count are continuation of the same
        // publication burst (UNION). This replaces a time-based gate that
        // failed for rapid back-to-back switches — users can click switch
        // dashboards in succession faster than any meaningful quiet window.
        private int _armCount;
        public int ArmCount => Volatile.Read(ref _armCount);

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
        /// <param name="countsAsSwitch">True for a real dashboard switch: bumps
        /// ArmCount, which the catalog parser's live-set tracker treats as a
        /// switch boundary and REPLACES the live-set. False re-arms the
        /// emission burst WITHOUT bumping ArmCount, so the live-set keeps
        /// ACCUMULATING (unioning) channels across catalog generations until an
        /// actual switch — the wheel ships one dashboard's channels across
        /// several END-marker generations, so treating each generation as a
        /// switch drops the earlier ones (e.g. OpponentCount / PlayerIndex when
        /// the radar dots land in a later generation). Used by the
        /// generation-advance re-arm in MaybeSwapProfileForCatalog.</param>
        public void ArmBurst(bool countsAsSwitch = true)
        {
            _armTickMs = Environment.TickCount;
            _lastEmissionTickMs = 0;
            Interlocked.Exchange(ref _emissionsSent, 0);
            Interlocked.Exchange(ref _pendingReemit, MaxEmissions);
            if (countsAsSwitch)
                Interlocked.Increment(ref _armCount);
        }

        /// <summary>
        /// Bump the arm count without scheduling emissions. Called by
        /// <see cref="TelemetrySender.Stop"/> so that any subsequent
        /// catalog commit (e.g. after wheel disconnect/reconnect) is
        /// treated as a fresh-session REPLACE boundary by the catalog
        /// parser — without this, the wheel's first post-reconnect
        /// catalog batch would UNION with the stale prior-session
        /// _liveCatalog. Wheel display takes ~20 s to boot after USB
        /// re-attach, so we can't rely on time alone to detect that the
        /// session is fresh; the explicit boundary signal is required.
        /// </summary>
        public void NotifySessionBoundary()
        {
            Interlocked.Increment(ref _armCount);
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
                // Hard cap fallback: regardless of state, we never wait
                // longer than this. Covers wheels that skip the post-switch
                // catalog push entirely (e.g., switching to a slot the
                // wheel was already on) or stay quiet because their
                // catalog is already consistent with what the host had.
                int sinceArm = now - _armTickMs;
                if (sinceArm >= FirstEmissionFallbackMs) return true;

                // END-advance gate: the wheel's END marker u32 must have
                // advanced PAST the pre-switch generation. The END u32 the
                // host echoes on every tier-def emission must match what
                // the wheel just pushed; emitting before END advances
                // means we'd echo the OLD generation and the wheel
                // rejects the binding. (Buffer-scan-based detection lives
                // in ChannelCatalogParser; this gate just consumes the
                // tick stamp.)
                bool newEndSinceArm = catalogWheelEndMarkerTickMs != 0
                    && (catalogWheelEndMarkerTickMs - _armTickMs) > 0;
                if (!newEndSinceArm) return false;

                // END advanced — but the wheel often pushes more URL
                // records AFTER the END marker as part of finishing the
                // generation (verified 2026-05-25 W17 capture: END
                // bumped at +185 ms post-arm, URL stream continued to
                // +957 ms). Wait for catalog activity to be quiet for
                // FirstEmissionQuietMs before emitting so the tier-def
                // is shaped for the FINAL channel set, not a partial
                // mid-stream snapshot. catalogLastActivityTickMs == 0
                // means the parser hasn't seen any activity at all —
                // treat as quiet so the END-without-activity edge
                // (unusual but possible) still fires.
                if (catalogLastActivityTickMs == 0) return true;
                int sinceActivity = now - catalogLastActivityTickMs;
                return sinceActivity >= FirstEmissionQuietMs;
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
        /// <item><c>null</c> — bind info isn't available (V0 URL, or the V2
        /// compact fallback when no wheel catalog is advertised). Caps the burst
        /// at the legacy <see cref="EmissionCount"/> (4) to preserve pre-adaptive
        /// behaviour for older wheels.</item>
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
