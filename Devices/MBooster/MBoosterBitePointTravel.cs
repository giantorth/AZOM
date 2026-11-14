using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MozaPlugin.Devices.MBooster
{
    /// <summary>Saved clutch travel times (MozaPluginSettings.MBoosterBitePointTravelTimes).</summary>
    public sealed class MBoosterBitePointTravelTimes
    {
        /// <summary>From the released end to the bite zone, ms, oldest first.</summary>
        public List<int> PressMs { get; set; } = new List<int>();

        /// <summary>From the pressed end to the bite zone, ms, oldest first.</summary>
        public List<int> ReleaseMs { get; set; } = new List<int>();
    }

    /// <summary>
    /// Clutch travel times learned by every MBoosterBitePointTrigger, per
    /// direction, and the Prediction arm window each gives: the 95th percentile
    /// of the last <see cref="MaxSamples"/>. Thread-safe; the plugin loads and
    /// saves it.
    /// </summary>
    internal static class MBoosterBitePointTravel
    {
        public const int MaxSamples = 32;
        public const int MinSamples = 5;
        public const double ArmPercentile = 0.95;
        public const double DefaultArmSec = 1.0;
        // Slower than this is a deliberate hover, not a move.
        public const double MaxTravelSec = 3.0;

        private static readonly object Gate = new object();
        // Copy-on-write under Gate.
        private static int[] _pressMs = Array.Empty<int>();
        private static int[] _releaseMs = Array.Empty<int>();
        // Double bits: a plain double can tear on the x86 build.
        private static long _pressArmBits = BitConverter.DoubleToInt64Bits(DefaultArmSec);
        private static long _releaseArmBits = BitConverter.DoubleToInt64Bits(DefaultArmSec);

        /// <summary>Called after a sample is added, on the adding thread.</summary>
        public static Action? Changed;

        public static double ArmSec(bool release) => BitConverter.Int64BitsToDouble(
            release ? Interlocked.Read(ref _releaseArmBits) : Interlocked.Read(ref _pressArmBits));

        public static void Load(MBoosterBitePointTravelTimes? saved)
        {
            lock (Gate)
            {
                _pressMs = Clean(saved?.PressMs);
                _releaseMs = Clean(saved?.ReleaseMs);
                Interlocked.Exchange(ref _pressArmBits, BitConverter.DoubleToInt64Bits(Arm(_pressMs)));
                Interlocked.Exchange(ref _releaseArmBits, BitConverter.DoubleToInt64Bits(Arm(_releaseMs)));
            }
        }

        public static MBoosterBitePointTravelTimes Snapshot()
        {
            lock (Gate)
            {
                return new MBoosterBitePointTravelTimes
                {
                    PressMs = new List<int>(_pressMs),
                    ReleaseMs = new List<int>(_releaseMs),
                };
            }
        }

        public static void Add(bool release, double sec)
        {
            if (!(sec > 0) || sec > MaxTravelSec) return;
            int ms = (int)Math.Round(sec * 1000);
            lock (Gate)
            {
                var next = Append(release ? _releaseMs : _pressMs, ms);
                long bits = BitConverter.DoubleToInt64Bits(Arm(next));
                if (release)
                {
                    _releaseMs = next;
                    Interlocked.Exchange(ref _releaseArmBits, bits);
                }
                else
                {
                    _pressMs = next;
                    Interlocked.Exchange(ref _pressArmBits, bits);
                }
            }
            Changed?.Invoke();
        }

        private static int[] Append(int[] cur, int ms)
        {
            int keep = Math.Min(cur.Length, MaxSamples - 1);
            var next = new int[keep + 1];
            Array.Copy(cur, cur.Length - keep, next, 0, keep);
            next[keep] = ms;
            return next;
        }

        private static int[] Clean(List<int>? saved)
        {
            if (saved == null) return Array.Empty<int>();
            var valid = saved.Where(ms => ms > 0 && ms <= MaxTravelSec * 1000).ToArray();
            return valid.Length <= MaxSamples ? valid : valid.Skip(valid.Length - MaxSamples).ToArray();
        }

        private static double Arm(int[] samples)
        {
            if (samples.Length < MinSamples) return DefaultArmSec;
            var sorted = (int[])samples.Clone();
            Array.Sort(sorted);
            return sorted[(int)Math.Ceiling(ArmPercentile * sorted.Length) - 1] / 1000.0;
        }
    }
}
