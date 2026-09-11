using System;
using System.Drawing;
using System.Threading;

namespace MozaPlugin.Devices.Led
{
    /// <summary>
    /// Repurposes the wheel's RPM bar as a dashboard-upload progress meter, and
    /// owns the decision to stand the live LED pipeline down while it does.
    ///
    /// While an upload is progressing the live pipeline stands down — see the
    /// <see cref="IsStandDownActive"/> guard in <see cref="MozaLedDeviceManager"/>,
    /// <see cref="MozaDashLedDeviceManager"/> and
    /// <see cref="MozaBaseLedDeviceManager"/>. The transfer and a 60 Hz LED
    /// stream contend for the same half-duplex 115200 link (the contention the
    /// catalog-negotiation throttle already exists for) and the wheel processes
    /// upload rounds at a few hundred B/s, so the stream is replaced by ~1
    /// frame/s of progress fill for the duration.
    ///
    /// The bar spans only the CENTRE RPM band. The side elements — the 3-LED
    /// brow segment a display wheel carries at each end of its strip, or the
    /// 3+3 flag LEDs on a flag-LED wheel — carry no progress and stay dark, so
    /// 100 % scales over the 8-12 LEDs the band actually holds (CS Pro
    /// 16-3-3 = 10, KS Pro 18-3-3 = 12, ESSENZA SCV12 10-1-1 = 8).
    ///
    /// Progress is the host emit fraction (content sub-msgs on the wire / total
    /// chunks), the same fraction the Files tab reports — so the bar and the UI
    /// can't disagree. Explicitly not the wheel's <c>bytes_written</c>; see
    /// <see cref="Telemetry.Dashboard.WheelUploadCoordinator.UploadProgress"/>
    /// for why that is not a progress counter. It reads 0 through the metadata
    /// handshake, which the frontier LED covers: the LED at the fill edge
    /// toggles once a second, so the bar shows it is alive before it has
    /// anything to fill. Liveness is separate — see
    /// <see cref="StallReleaseSeconds"/>.
    ///
    /// Deliberately NOT tied to SimHub's <c>Display()</c> callback: an upload
    /// runs at connect time and from the Files tab with no game loaded, when
    /// SimHub feeds no LED frames at all. Driven from the telemetry tick.
    /// </summary>
    internal static class UploadProgressLedBar
    {
        /// <summary>Fill colour — amber, matching the Files tab's in-flight status text.</summary>
        private static readonly Color FillColor = Color.FromArgb(255, 140, 0);

        /// <summary>
        /// Wire feed cadence: one frame per second, which is also one frame per
        /// progress step (see <see cref="FeedsPerProgressStep"/>) — so every
        /// feed carries a changed frame and nothing is spent on keepalive.
        ///
        /// <para>This deliberately does NOT hold a sub-1 s keepalive. The RPM
        /// group's live frame PERSISTS with no re-feeding at all: bundle
        /// NS9G817J stopped feeding the strip at 08:26:14 and the fill was
        /// still lit when the capture ended 85 s later, with zero group-0
        /// (<c>3f 17 19/1a 00</c>) frames in between. That is the opposite of
        /// the knob rings, which revert to stored colours ~1 s after the last
        /// feed and are why <c>MozaLedDeviceManager</c>'s unified keepalive runs
        /// at 0.75 s. Halving this rate halves the bar's wire cost during a
        /// transfer that is already the tightest thing on the link.</para>
        ///
        /// <para>The corollary is <see cref="ReleaseBar"/>: because the frame
        /// persists, standing down has to blank the strip explicitly rather
        /// than just going quiet.</para>
        /// </summary>
        private const double FeedIntervalSeconds = 1.0;

        /// <summary>
        /// Feeds per progress step. 1 — the displayed progress and the frontier
        /// LED's blink phase advance on every feed, giving the 1 Hz update and
        /// the 1 s on / 1 s off blink asked for.
        /// </summary>
        private const int FeedsPerProgressStep = 1;

        /// <summary>
        /// How long the wheel may fail to acknowledge a single chunk before the
        /// stand-down is abandoned: the bar stops feeding and the live pipeline
        /// takes its LEDs back, without waiting for the upload to admit defeat.
        ///
        /// <para>The upload only releases the pipeline when its attempt
        /// TERMINATES, and a wedged attempt takes far longer than that to
        /// notice — its completion deadline rolls forward on every ack sub-msg,
        /// not on actual byte progress. Bundle C4KX4GKK stopped advancing at
        /// <c>bw=167772</c> (12:00:47) and did not terminate until 12:07:04:
        /// <b>6 min 17 s</b> of frozen amber bar with the user's RPM, button and
        /// knob LEDs held off. This bounds that.</para>
        ///
        /// <para>Watches the wheel's ACK COUNT, not the displayed fraction.
        /// The fraction only ticks when a whole content sub-msg clears its
        /// backlog drain, and late in a transfer that legitimately exceeds 50 s
        /// — bundle 33E47E0M tripped this at 73 % while the wheel's ack seq was
        /// advancing through 3659 distinct values right up to the capture, i.e.
        /// a perfectly healthy transfer with its LEDs taken away. A single
        /// chunk ack is a much finer-grained liveness signal than a 4092-byte
        /// round, so 50 s is generous for it.</para>
        /// </summary>
        private const double StallReleaseSeconds = 50.0;

        /// <summary>
        /// The same bound, but for a bar sitting at 100 %. Once every content
        /// sub-msg is on the wire the fraction cannot advance any further, so
        /// the ordinary stall watch would blank a bar that is doing exactly
        /// what it should — waiting for the wheel's completion ack while it
        /// decompresses and writes the bundle. That wait is legitimately long
        /// (~15 s on bundle NS9G817J's 245 KB payload; the coordinator allows
        /// per-stride latency up to ~40 s), so it gets its own, longer ceiling
        /// rather than an exemption: a completion ack that never arrives must
        /// still hand the LEDs back.
        /// </summary>
        private const double CompletionHoldSeconds = 120.0;

        /// <summary>
        /// How long past the last <see cref="Tick"/> the stand-down stays
        /// asserted. The LED managers read <see cref="IsStandDownActive"/> on
        /// SimHub's own thread, so it must not be a latch that a dead telemetry
        /// tick can pin true — it expires on its own. Generous against a slow
        /// tick (4x the feed interval), quick to lapse if the tick stops.
        /// </summary>
        private const double StandDownGraceSeconds = 3.0;

        // Ticked from both tier-def senders' timer threads and released from
        // the upload worker thread; the fields under s_lock carry the render
        // state. At 2 Hz the contention cost is nil.
        private static readonly object s_lock = new object();
        private static bool s_engaged;
        private static DateTime s_lastFeedUtc = DateTime.MinValue;
        private static int s_feedsSinceProgress;
        private static double s_fraction;
        private static bool s_frontierLit;
        private static DateTime s_lastAdvanceUtc = DateTime.MinValue;
        private static long s_lastAckedChunks = -1L;
        // Strip length the bar last painted, so ReleaseBar can blank exactly
        // what it lit even if the wheel model has since changed or gone away.
        private static int s_engagedRpmN;

        // Read by the LED managers at up to 60 Hz on SimHub's thread, so these
        // two stay lock-free (same reason MozaLedDeviceManager keeps
        // s_lastLiveSendUtcTicks on Interlocked).
        private static long s_standDownUntilTicks;
        private static volatile bool s_stalledOut;

        /// <summary>
        /// True while the live LED pipeline should stand down for an upload.
        /// Three independent ways out, all self-healing: the upload ends (Tick
        /// stops asserting), the telemetry tick dies (this lapses after
        /// <see cref="StandDownGraceSeconds"/>), or the transfer stops advancing
        /// (<see cref="StallReleaseSeconds"/>).
        /// </summary>
        internal static bool IsStandDownActive
        {
            get
            {
                if (s_stalledOut) return false;
                long until = Interlocked.Read(ref s_standDownUntilTicks);
                if (until == 0) return false;
                return DateTime.UtcNow.Ticks <= until;
            }
        }

        /// <summary>
        /// Telemetry-tick hook. No-ops unless a dashboard upload is in flight,
        /// and stands the bar down on the trailing edge. Safe to call from both
        /// senders' ticks — the pacing gates below dedupe.
        /// </summary>
        internal static void Tick(MozaPlugin? plugin)
        {
            if (plugin == null || !plugin.IsDashboardUploadInFlight)
            {
                Release();
                return;
            }

            // Assert the stand-down for every tick the upload is live, BEFORE
            // any geometry check below can bail out. A rig whose RPM bar can't
            // show the meter (no wheel, or a button-only rim) still needs the
            // pipeline quiet — the bandwidth is the point, the bar is the
            // consolation. Cleared by the stall check further down.
            Interlocked.Exchange(ref s_standDownUntilTicks,
                DateTime.UtcNow.AddSeconds(StandDownGraceSeconds).Ticks);

            lock (s_lock)
            {
                // Re-read under the lock: the upload can finish (and Release
                // can run) between the check above and the writes below, and a
                // stale fill landing after the live pipeline resumed would sit
                // on the rim until its next frame.
                if (!plugin.IsDashboardUploadInFlight) return;

                // Stalled out earlier in this attempt — stay off the wire until
                // it terminates and Release re-arms us. Must come BEFORE the
                // sampling block: ReleaseBar cleared s_engaged, so the feed gate
                // below no longer holds anything back and we would re-enter the
                // stall branch (and re-log it) twice a second forever.
                if (s_stalledOut) return;

                var now = DateTime.UtcNow;
                if (s_engaged && (now - s_lastFeedUtc).TotalSeconds < FeedIntervalSeconds) return;

                // Re-sample progress (and step the blink) on the first feed and
                // every FeedsPerProgressStep-th one after it; the feeds in
                // between re-send an identical frame purely to hold ownership.
                bool sampled = !s_engaged || ++s_feedsSinceProgress >= FeedsPerProgressStep;
                if (sampled)
                {
                    s_feedsSinceProgress = 0;
                    double p = plugin.DashboardUploadProgress;
                    s_fraction = p < 0.0 ? 0.0 : p > 1.0 ? 1.0 : p;
                    s_frontierLit = !s_frontierLit;

                    // Stall watch on the wheel's ack count — see
                    // StallReleaseSeconds for why not the displayed fraction.
                    long acked = plugin.DashboardUploadAckedChunks;
                    if (acked != s_lastAckedChunks || s_lastAdvanceUtc == DateTime.MinValue)
                    {
                        s_lastAckedChunks = acked;
                        s_lastAdvanceUtc = now;
                    }
                    else
                    {
                        // Everything is on the wire and acked; the wheel is now
                        // decompressing and writing, and sends no further chunk
                        // acks while it does. Judge that against the longer
                        // CompletionHoldSeconds.
                        double limit = s_fraction >= 1.0
                            ? CompletionHoldSeconds : StallReleaseSeconds;
                        if ((now - s_lastAdvanceUtc).TotalSeconds >= limit)
                        {
                            MozaLog.Warn(
                                $"[AZOM] Upload made no progress for {limit:F0}s " +
                                $"(stuck at {s_fraction * 100.0:F0}%) — handing the LEDs back to " +
                                "telemetry; the upload's own timeout still owns the transfer");
                            s_stalledOut = true;
                            Interlocked.Exchange(ref s_standDownUntilTicks, 0L);
                            ReleaseBar();
                            return;
                        }
                    }
                }

                // New-protocol wheel only: an old-protocol rim has no per-LED
                // colour path (bitmask only) and no display to receive a
                // dashboard in the first place.
                if (!plugin.Data.IsConnected || !plugin.IsNewWheelDetected) return;

                var model = plugin.WheelModelInfo;
                int rpmN = model?.RpmLedCount ?? 0;
                if (rpmN <= 0) return;   // button-only rim (Revuelto, Mission R)

                // Centre band = the strip minus its side elements. On a
                // flag-LED wheel the flags ride their own dash-flag-colorN
                // commands, so the strip this command addresses is already
                // just the band.
                int side = model!.HasFlagLeds ? 0 : Math.Max(0, model.BrowSegmentSize);
                int barCount = rpmN - 2 * side;
                if (barCount <= 0) { side = 0; barCount = rpmN; }

                int lit = (int)(s_fraction * barCount);
                if (lit > barCount) lit = barCount;
                // The frontier is the single LED still filling; at 100 % there
                // is none and the whole band sits solid.
                int frontier = lit < barCount ? lit : -1;

                var colors = new Color[rpmN];   // side elements stay black
                int active = 0;
                for (int i = 0; i < barCount; i++)
                {
                    if (i >= lit && !(i == frontier && s_frontierLit)) continue;
                    colors[side + i] = FillColor;
                    active |= 1 << (side + i);
                }

                if (!s_engaged)
                {
                    s_engaged = true;
                    MozaLog.Debug(
                        $"[AZOM] Upload progress bar engaged: {barCount} LEDs " +
                        $"(strip={rpmN}, side={side})");
                }
                s_engagedRpmN = rpmN;
                s_lastFeedUtc = now;

                // Same wire pair the live RPM path uses: colour chunks first so
                // no LED lights a frame before its colour lands, then the
                // 8-byte active+window bitmask. Window stays the full strip —
                // the form proven on every captured wheel for this group — so
                // the unlit side elements read as deliberately off rather than
                // keeping whatever the paused pipeline last left there.
                MozaLedDeviceManager.SendColorChunks(
                    plugin, colors, rpmN, "wheel-telemetry-rpm-colors");
                plugin.DeviceManager.WriteArray("wheel-send-rpm-telemetry",
                    MozaLedDeviceManager.BuildWindowedBitmaskBytes(active, (1 << rpmN) - 1));
            }
        }

        /// <summary>
        /// Stand the bar down and end the pipeline stand-down. Idempotent.
        /// Called on every upload outcome (<c>UploadCompleted</c> fires from
        /// <c>RunBackgroundUpload</c>'s finally on all paths) and from
        /// <see cref="Tick"/> once the in-flight flag clears, so it also clears
        /// the stall latch — the next attempt starts with a clean slate.
        /// </summary>
        internal static void Release()
        {
            Interlocked.Exchange(ref s_standDownUntilTicks, 0L);
            s_stalledOut = false;
            lock (s_lock)
            {
                s_lastAdvanceUtc = DateTime.MinValue;
                s_lastAckedChunks = -1L;
                ReleaseBar();
            }
        }

        /// <summary>
        /// Blank the bar, drop its render state, and re-arm the live pipeline's
        /// RPM cache. Caller holds <see cref="s_lock"/>. Split from
        /// <see cref="Release"/> so the stall path can stop drawing without
        /// clearing the stall latch that keeps it from re-engaging.
        /// </summary>
        private static void ReleaseBar()
        {
            s_lastFeedUtc = DateTime.MinValue;
            s_feedsSinceProgress = 0;
            s_fraction = 0.0;
            s_frontierLit = false;
            if (!s_engaged) return;
            s_engaged = false;
            int rpmN = s_engagedRpmN;
            s_engagedRpmN = 0;

            // Blank the strip EXPLICITLY. Going quiet is not enough: the RPM
            // group's live frame persists indefinitely with no re-feeding
            // (bundle NS9G817J — upload succeeded, bar released at 08:26:14,
            // fill still lit 85 s later with zero group-0 frames sent in
            // between), so the fill would just sit on the rim until something
            // else happened to repaint it. With no game running nothing does.
            //
            // One all-black colour frame plus active=0 over the full window is
            // exactly what the live pipeline emits on its own lit -> off
            // transition, so this is a proven shape rather than a new one. It
            // is a ONE-SHOT: repeatedly re-sending all-black would pin the
            // wheel in live-render mode and block its firmware sleep light,
            // which is why the live path sends it once and then goes quiet too.
            var plugin = MozaPlugin.Instance;
            if (plugin != null && rpmN > 0 && plugin.Data.IsConnected)
            {
                MozaLedDeviceManager.SendColorChunks(
                    plugin, new Color[rpmN], rpmN, "wheel-telemetry-rpm-colors");
                plugin.DeviceManager.WriteArray("wheel-send-rpm-telemetry",
                    MozaLedDeviceManager.BuildWindowedBitmaskBytes(0, (1 << rpmN) - 1));
            }

            // The live pipeline's RPM cache still describes the frame the
            // progress fill overwrote, so its next frame must re-send rather
            // than dedup against it — including against the blank just sent.
            // InvalidateLiveCacheAny takes the driver registry's own lock; that
            // nests under s_lock only here, and the registry never calls back
            // into this class.
            MozaLedDeviceManager.InvalidateLiveCacheAny(LedKind.Rpm);
            MozaLog.Debug("[AZOM] Upload progress bar released (strip blanked)");
        }
    }
}
