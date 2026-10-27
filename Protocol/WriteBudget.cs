using System;
using System.Diagnostics;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Sliding-window bandwidth gate for the serial write loop (115200 baud =
    /// ~11.5 kB/s ceiling). Extends one-shot pacing under saturation; never drops.
    /// </summary>
    public sealed class WriteBudget
    {
        // Sustained target = ~95% of the 11520 B/s wire ceiling; OS write
        // buffer (16 kB) blocks beyond, providing natural backpressure.
        public const int TargetBytesPerWindow = 11000;
        // Bursts up to BurstAllowedBytes / window are tolerated without pacing
        // adjustment — settings ApplyProfile (~30 frames × 4 ms = 120 ms burst)
        // and tier-def chunk emission sit in this band.
        private const int BurstAllowedBytes = 14000;
        // Below this threshold the gate stays at 0 ms — no impact on steady state.
        private const int SoftThresholdBytes = 9500;

        // 1 s window as 50 × 20 ms time buckets: bounded memory with no cap on
        // frames per window (unpaced stream/priority lanes push well past 256/s).
        private const int BucketCount = 50;

        private readonly object _lock = new object();
        private readonly long _bucketTicks;
        private readonly long[] _bucketId = new long[BucketCount];
        private readonly int[] _bucketBytes = new int[BucketCount];

        // Peak bytes/sec observed since last ResetPeak; diagnostics only.
        private int _peakBytesInWindow;

        public WriteBudget()
        {
            _bucketTicks = Math.Max(1, Stopwatch.Frequency / BucketCount);
            for (int i = 0; i < BucketCount; i++) _bucketId[i] = long.MinValue;
        }

        /// <summary>Record bytes written to the wire (post-stuffing).</summary>
        public void Record(int wireBytes)
        {
            if (wireBytes <= 0) return;
            long id = Stopwatch.GetTimestamp() / _bucketTicks;
            lock (_lock)
            {
                int slot = (int)(id % BucketCount);
                if (_bucketId[slot] != id)
                {
                    _bucketId[slot] = id;
                    _bucketBytes[slot] = 0;
                }
                _bucketBytes[slot] += wireBytes;
                int total = SumWindow(id);
                if (total > _peakBytesInWindow)
                    _peakBytesInWindow = total;
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
                total = SumWindow(CurrentBucketId()) + wireBytes;
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


        /// <summary>Snapshot of current bandwidth; peak is monotonic until ResetPeak.</summary>
        public Snapshot GetSnapshot()
        {
            lock (_lock)
            {
                int bytes = SumWindow(CurrentBucketId());
                return new Snapshot(bytes, PercentOf(bytes), _peakBytesInWindow);
            }
        }

        /// <summary>Manually reset the peak counter. UI can offer a "reset
        /// peak" button so the user can re-baseline after addressing a
        /// burst event without reloading the plugin.</summary>
        public void ResetPeak()
        {
            lock (_lock) _peakBytesInWindow = SumWindow(CurrentBucketId());
        }

        /// <summary>Non-mutating peek used by the WriteLoop's periodic warn
        /// check, so it can't clobber the peak that the diagnostics UI is
        /// about to read.</summary>
        public Snapshot PeekSnapshot() => GetSnapshot();

        private static int PercentOf(int bytes)
            => TargetBytesPerWindow == 0 ? 0 : (int)((long)bytes * 100 / TargetBytesPerWindow);

        private long CurrentBucketId() => Stopwatch.GetTimestamp() / _bucketTicks;

        /// <summary>Bytes in the buckets of the last second ending at <paramref name="id"/>.</summary>
        private int SumWindow(long id)
        {
            long oldest = id - BucketCount + 1;
            int total = 0;
            for (int i = 0; i < BucketCount; i++)
                if (_bucketId[i] >= oldest && _bucketId[i] <= id)
                    total += _bucketBytes[i];
            return total;
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
