using System;
using System.Diagnostics;
using System.Threading;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Fixed-rate worker loop for the plugin-lifetime effect threads. While the
    /// device is absent the thread polls <c>isActive</c> every
    /// <see cref="IdleSleepMs"/> instead of ticking at full rate; polling a
    /// predicate cannot miss an attach the way an edge-armed wake event can.
    /// </summary>
    internal static class PacedWorkerLoop
    {
        /// <summary>Poll interval while inactive. Bounds the attach latency and must stay
        /// well under the owners' stop-join timeouts.</summary>
        public const int IdleSleepMs = 250;

        /// <summary>
        /// Run <paramref name="tick"/> every <paramref name="periodMs"/> until
        /// <paramref name="stop"/> returns true. <paramref name="isActive"/> must be
        /// false only when a tick would be a no-op.
        /// </summary>
        public static void Run(int periodMs, Func<bool> stop, Func<bool> isActive, Action tick, string tickErrorPrefix)
        {
            long stopwatchFreq = Stopwatch.Frequency;
            long periodTicks = stopwatchFreq * periodMs / 1000;
            long next = Stopwatch.GetTimestamp() + periodTicks;
            while (!stop())
            {
                bool active;
                try { active = isActive(); }
                catch { active = true; }   // let the tick's own guards decide
                if (!active)
                {
                    Thread.Sleep(IdleSleepMs);
                    next = Stopwatch.GetTimestamp() + periodTicks;
                    continue;
                }

                try { tick(); }
                // Repeat-suppressed: a persistent throw would log at the tick rate.
                catch (Exception ex) { MozaLog.DebugIfChanged(tickErrorPrefix, $"{tickErrorPrefix}: {ex.GetType().Name}: {ex.Message}"); }

                long now = Stopwatch.GetTimestamp();
                long delta = next - now;
                if (delta <= 0)
                {
                    // Fell behind by >1 tick; reset deadline so we don't burst.
                    next = now + periodTicks;
                    continue;
                }
                int sleepMs = (int)Math.Min(50, Math.Max(1, delta * 1000 / stopwatchFreq));
                Thread.Sleep(sleepMs);
                next += periodTicks;
            }
        }
    }
}
