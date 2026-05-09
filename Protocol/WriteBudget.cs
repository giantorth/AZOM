using System;
using System.Diagnostics;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Token-bucket bandwidth gate for the serial write loop. The 115200-baud link
    /// has a hard ceiling of ~11 520 wire bytes/sec; this class tracks bytes
    /// written over a sliding 1-second window and recommends additional pacing
    /// for one-shot writes plus a drop-this-iteration signal for the latest-wins
    /// stream lane when the budget is approached.
    ///
    /// Latest-wins safety: skipping a stream-lane drain under saturation is safe
    /// because fresh frames overwrite the pending slot before the next iteration —
    /// telemetry data is never queued, only the most recent value survives.
    ///
    /// One-shot ordering: FIFO order is preserved; saturation extends the existing
    /// 4 ms pacing gate rather than dropping frames. Settings/session/probe traffic
    /// is order-sensitive and must never be silently dropped.
    /// </summary>
    public sealed class WriteBudget
    {
        // Sustained budget = ~70% of theoretical 11 520 wire B/s. The remaining
        // headroom absorbs inbound flow-control replies and short bursts before
        // the OS write buffer (16 KB) fills.
        private const int TargetBytesPerWindow = 8000;
        // Bursts up to BurstAllowedBytes / window are tolerated without pacing
        // adjustment — settings ApplyProfile (~30 frames × 4 ms = 120 ms burst)
        // sits in this band.
        private const int BurstAllowedBytes = 12000;
        // Below this threshold the gate stays at 0 ms — no impact on steady state.
        private const int SoftThresholdBytes = 6400; // 80% of target

        private const long TicksPerMillisecond = TimeSpan.TicksPerMillisecond;
        private const long WindowTicks = 1000 * TicksPerMillisecond;

        private readonly object _lock = new object();
        private readonly long _stopwatchTicksPerWindow;
        // Ring of the last N (timestamp, bytes) entries — keeps memory bounded
        // even under burst storms. 256 is generous; at 250 frames/sec one-shot
        // ceiling that's a full second of granularity.
        private const int RingSize = 256;
        private readonly long[] _ringTs = new long[RingSize];
        private readonly int[] _ringBytes = new int[RingSize];
        private int _ringHead;     // next write position
        private int _ringTail;     // oldest entry still in window
        private int _bytesInWindow;

        // Peak bytes/sec observed since last GetSnapshot snapshot reset; rolling
        // visible-only metric for the diagnostics surface.
        private int _peakBytesInWindow;

        public WriteBudget()
        {
            _stopwatchTicksPerWindow = Stopwatch.Frequency;
        }

        /// <summary>Record bytes written to the wire (post-stuffing).</summary>
        public void Record(int wireBytes)
        {
            if (wireBytes <= 0) return;
            long now = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                Trim(now);
                _ringTs[_ringHead] = now;
                _ringBytes[_ringHead] = wireBytes;
                _ringHead = (_ringHead + 1) % RingSize;
                if (_ringHead == _ringTail)
                {
                    // Ring overflow — evict oldest before its window expires.
                    // Loses precision on the trailing edge but never blocks; in
                    // practice 256 slots covers any plausible burst.
                    _bytesInWindow -= _ringBytes[_ringTail];
                    _ringTail = (_ringTail + 1) % RingSize;
                }
                _bytesInWindow += wireBytes;
                if (_bytesInWindow > _peakBytesInWindow)
                    _peakBytesInWindow = _bytesInWindow;
            }
        }

        /// <summary>
        /// Additional milliseconds the one-shot lane should sleep beyond the
        /// existing 4 ms gate. Returns 0 below 80% of target. Ramps linearly
        /// from 2 ms at 90% to 20 ms at burst-ceiling.
        /// </summary>
        public int RecommendOneShotDelayMs(int wireBytes)
        {
            int total;
            lock (_lock)
            {
                Trim(Stopwatch.GetTimestamp());
                total = _bytesInWindow + wireBytes;
            }
            if (total <= SoftThresholdBytes) return 0;
            if (total <= TargetBytesPerWindow)
            {
                // 80% → 100%: scale 0..6 ms
                int span = TargetBytesPerWindow - SoftThresholdBytes;
                int over = total - SoftThresholdBytes;
                return Math.Max(0, (over * 6) / span);
            }
            if (total <= BurstAllowedBytes)
            {
                // 100% → burst ceiling: scale 6..20 ms
                int span = BurstAllowedBytes - TargetBytesPerWindow;
                int over = total - TargetBytesPerWindow;
                return 6 + Math.Max(0, (over * 14) / span);
            }
            // Above burst ceiling: 20 ms cap so we don't wedge the link entirely.
            return 20;
        }

        /// <summary>
        /// True while the stream lane is allowed to drain this WriteLoop
        /// iteration. Returns false above the target so latest-wins coalescing
        /// has time to compress the pending state into one frame per slot.
        /// </summary>
        public bool MayDrainStreams()
        {
            lock (_lock)
            {
                Trim(Stopwatch.GetTimestamp());
                return _bytesInWindow < TargetBytesPerWindow;
            }
        }

        /// <summary>Read-and-reset snapshot — peak value resets to the
        /// instantaneous count, so each caller sees the peak SINCE THEIR LAST
        /// CALL. The diagnostics tab uses this so the peak in the UI reflects
        /// activity since the user last looked.</summary>
        public Snapshot GetSnapshot()
        {
            lock (_lock)
            {
                Trim(Stopwatch.GetTimestamp());
                int pct = PercentOf(_bytesInWindow);
                int peak = _peakBytesInWindow;
                _peakBytesInWindow = _bytesInWindow;
                return new Snapshot(_bytesInWindow, pct, peak);
            }
        }

        /// <summary>Non-mutating peek used by the WriteLoop's periodic warn
        /// check, so it can't clobber the peak that the diagnostics UI is
        /// about to read.</summary>
        public Snapshot PeekSnapshot()
        {
            lock (_lock)
            {
                Trim(Stopwatch.GetTimestamp());
                return new Snapshot(_bytesInWindow, PercentOf(_bytesInWindow), _peakBytesInWindow);
            }
        }

        private static int PercentOf(int bytes)
            => TargetBytesPerWindow == 0 ? 0 : (int)((long)bytes * 100 / TargetBytesPerWindow);

        /// <summary>Drop ring entries older than the 1-second window.</summary>
        private void Trim(long now)
        {
            long cutoff = now - _stopwatchTicksPerWindow;
            while (_ringTail != _ringHead && _ringTs[_ringTail] < cutoff)
            {
                _bytesInWindow -= _ringBytes[_ringTail];
                _ringTail = (_ringTail + 1) % RingSize;
            }
            if (_bytesInWindow < 0) _bytesInWindow = 0;
        }

        public readonly struct Snapshot
        {
            public readonly int BytesLastSec;
            public readonly int PercentBudget;
            public readonly int PeakBurstBytes;

            public Snapshot(int bytesLastSec, int percentBudget, int peakBurstBytes)
            {
                BytesLastSec = bytesLastSec;
                PercentBudget = percentBudget;
                PeakBurstBytes = peakBurstBytes;
            }
        }
    }
}
