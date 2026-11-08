using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MozaPlugin.Devices.MBooster
{
    /// <summary>Where the Bite Point trigger and its calibration read the clutch position.</summary>
    public enum MBoosterBitePointSource
    {
        // The clutch-role mBooster pedal's own position.
        MBoosterPedal,
        // SimHub's game telemetry clutch (GameData.Clutch, 0..100).
        Game,
        // A user SimHub formula/property, 0..100.
        Formula,
        // Any HID game-controller axis, read on its own thread (HidAxisReader).
        HidAxis,
    }

    /// <summary>
    /// One game's clutch bite point: the median of the most recent captured samples.
    /// Treat a stored instance as immutable: change a <see cref="Clone"/> and
    /// store that, so a concurrent settings save never sees a mutation.
    /// </summary>
    public sealed class MBoosterBitePointCalibration
    {
        /// <summary>Samples kept for the median; older ones drop off.</summary>
        public const int MaxSamples = 9;

        /// <summary>Captured bite points (0..100), oldest first.</summary>
        public List<int> Samples { get; set; } = new List<int>();

        /// <summary>Bite point in use (0..100), -1 when there is none.</summary>
        [JsonIgnore]
        public int Pct => Median();

        public void AddSample(int pct)
        {
            Samples.Add(Math.Max(0, Math.Min(100, pct)));
            while (Samples.Count > MaxSamples) Samples.RemoveAt(0);
        }

        public MBoosterBitePointCalibration Clone() =>
            new MBoosterBitePointCalibration { Samples = new List<int>(Samples) };

        private int Median()
        {
            int n = Samples.Count;
            if (n == 0) return -1;
            var sorted = new List<int>(Samples);
            sorted.Sort();
            return n % 2 == 1
                ? sorted[n / 2]
                : (int)Math.Round((sorted[n / 2 - 1] + sorted[n / 2]) / 2.0, MidpointRounding.AwayFromZero);
        }
    }
}
