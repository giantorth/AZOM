using System;
using System.Diagnostics;
using System.Threading;

namespace MozaPlugin.Devices.MBooster
{
    public enum BitePointCalState
    {
        Idle,
        // Started; needs the car stopped, in gear, with the clutch pressed in.
        Waiting,
        // Conditions met; the next release past ClutchInLevel starts the run.
        Armed,
        // Clutch coming up with the car still stopped.
        Releasing,
        Captured,
        Cancelled,
    }

    /// <summary>One standing-start observation.</summary>
    internal readonly struct BitePointSample
    {
        public readonly int Pct;
        /// <summary>How fast the clutch was coming up at the bite, pedal fraction per second.</summary>
        public readonly double ReleaseRatePerSec;

        public BitePointSample(int pct, double releaseRatePerSec)
        {
            Pct = pct;
            ReleaseRatePerSec = releaseRatePerSec;
        }
    }

    /// <summary>
    /// Standing-start bite point detection: the car stops in gear with the
    /// clutch pressed in, then the clutch comes up. Once the speed has risen
    /// <see cref="ConfirmRiseKmh"/> over standstill, the moment the rise began is
    /// fitted from the whole release (<c>PedalWhenItBit</c>), and the bite point
    /// is the clutch-pedal position then: by the first frame the car is seen
    /// moving, the pedal has already come up past the bite. Delay in the game's
    /// own speed output isn't seen. Data thread only.
    /// </summary>
    internal sealed class MBoosterBitePointDetector
    {
        public enum Phase { Waiting, Armed, Releasing }

        // Pedal position (0..1) counted as "clutch fully in".
        internal const double ClutchInLevel = 0.9;
        // Below this the car counts as stopped.
        internal const double StandstillKmh = 0.5;
        // Rise over the standstill speed that starts the fitted window...
        internal const double OnsetRiseKmh = 0.3;
        // ...and the rise that confirms the car is really pulling away and ends it.
        internal const double ConfirmRiseKmh = 2.0;
        // Window the release rate at the bite is measured over.
        private const double RateWindowSec = 0.2;

        // ~4 s at SimHub's data rate: the whole release, back to where it bit.
        private const int HistorySize = 256;
        // Step of the search for when the speed rise began.
        private const double FitStepSec = 0.004;
        private readonly long[] _histTicks = new long[HistorySize];
        private readonly double[] _histPedal = new double[HistorySize];
        private readonly double[] _histRise = new double[HistorySize];
        private int _histCount;
        private int _histHead;

        private double _baselineKmh;
        private double _onsetPedal = double.NaN;
        private long _onsetTicks;
        private double _onsetRate;

        public Phase Current { get; private set; }

        public void Reset()
        {
            Current = Phase.Waiting;
            _onsetPedal = double.NaN;
            _histCount = 0;
        }

        /// <param name="ready">Driving, in gear, with a clutch pedal reporting.</param>
        /// <returns>True on the frame a bite point is captured.</returns>
        public bool Update(bool ready, double speedKmh, double pedal01, out BitePointSample sample)
        {
            sample = default;
            if (!ready || double.IsNaN(speedKmh) || double.IsNaN(pedal01))
            {
                Reset();
                return false;
            }
            bool stopped = speedKmh < StandstillKmh;
            bool clutchIn = pedal01 >= ClutchInLevel;

            switch (Current)
            {
                case Phase.Waiting:
                    if (stopped && clutchIn) Current = Phase.Armed;
                    break;

                case Phase.Armed:
                    if (!stopped) Current = Phase.Waiting;
                    else if (!clutchIn)
                    {
                        _baselineKmh = speedKmh;
                        _onsetPedal = double.NaN;
                        _histCount = 0;
                        Current = Phase.Releasing;
                    }
                    break;

                case Phase.Releasing:
                    if (clutchIn)
                    {
                        // Pressed back in: re-arm if still stopped.
                        Current = stopped ? Phase.Armed : Phase.Waiting;
                        break;
                    }
                    long now = Stopwatch.GetTimestamp();
                    double rise = speedKmh - _baselineKmh;
                    Record(now, pedal01, rise);
                    if (rise <= OnsetRiseKmh)
                    {
                        // Back at standstill (false start): wait for a fresh onset.
                        _onsetPedal = double.NaN;
                        break;
                    }
                    if (double.IsNaN(_onsetPedal))
                    {
                        _onsetPedal = pedal01;
                        _onsetTicks = now;
                        _onsetRate = ReleaseRate(now, pedal01);
                    }
                    if (rise >= ConfirmRiseKmh)
                    {
                        double bite = PedalWhenItBit();
                        sample = new BitePointSample(
                            (int)Math.Round(Math.Max(0, Math.Min(1, bite)) * 100.0), _onsetRate);
                        Reset();
                        return true;
                    }
                    break;
            }
            return false;
        }

        private void Record(long ticks, double pedal, double rise)
        {
            _histTicks[_histHead] = ticks;
            _histPedal[_histHead] = pedal;
            _histRise[_histHead] = rise;
            _histHead = (_histHead + 1) % HistorySize;
            if (_histCount < HistorySize) _histCount++;
        }

        // Pedal position when the speed rise began. Over the whole release, fits
        // "no rise until t0, then a·x + b·x²" (x = time since t0, a and b ≥ 0:
        // an even pull-away or one that builds as the clutch engages) and takes
        // the best t0 from a search up to the onset frame. Using the flat
        // standstill stretch as well as the rise keeps it steady on noisy or
        // stepped speed. The onset frame's pedal if there's too little to fit.
        private double PedalWhenItBit()
        {
            int n = _histCount;
            if (n < 4) return _onsetPedal;
            var t = new double[n];
            var r = new double[n];
            int oldest = (_histHead - n + HistorySize) % HistorySize;
            for (int k = 0; k < n; k++)
            {
                int idx = (oldest + k) % HistorySize;
                // Seconds from the onset frame.
                t[k] = (_histTicks[idx] - _onsetTicks) / (double)Stopwatch.Frequency;
                r[k] = _histRise[idx];
            }
            double bestErr = double.PositiveInfinity, bestT0 = double.NaN;
            for (double t0 = t[0]; t0 <= 0; t0 += FitStepSec)
            {
                double err = OnsetFitError(t, r, t0);
                if (err < bestErr)
                {
                    bestErr = err;
                    bestT0 = t0;
                }
            }
            if (double.IsNaN(bestT0)) return _onsetPedal;
            return PedalAt(_onsetTicks + (long)(bestT0 * Stopwatch.Frequency));
        }

        // Squared error of the best "zero until t0, then a·x + b·x²" fit, a, b ≥ 0.
        private static double OnsetFitError(double[] t, double[] r, double t0)
        {
            double flat = 0, sx2 = 0, sx3 = 0, sx4 = 0, srx = 0, srx2 = 0, srr = 0;
            int after = 0;
            for (int k = 0; k < t.Length; k++)
            {
                if (t[k] <= t0)
                {
                    flat += r[k] * r[k];
                    continue;
                }
                double x = t[k] - t0, x2 = x * x;
                sx2 += x2;
                sx3 += x2 * x;
                sx4 += x2 * x2;
                srx += r[k] * x;
                srx2 += r[k] * x2;
                srr += r[k] * r[k];
                after++;
            }
            if (after < 2) return double.PositiveInfinity;
            double det = sx2 * sx4 - sx3 * sx3;
            if (det > 1e-18)
            {
                double a = (srx * sx4 - srx2 * sx3) / det;
                double b = (sx2 * srx2 - sx3 * srx) / det;
                if (a >= 0 && b >= 0)
                    return flat + srr - 2 * (a * srx + b * srx2) + a * a * sx2 + 2 * a * b * sx3 + b * b * sx4;
            }
            // A coefficient wants to go negative: the better single-term fit.
            double aOnly = Math.Max(0, srx / sx2), bOnly = Math.Max(0, srx2 / sx4);
            double errA = srr - 2 * aOnly * srx + aOnly * aOnly * sx2;
            double errB = srr - 2 * bOnly * srx2 + bOnly * bOnly * sx4;
            return flat + Math.Min(errA, errB);
        }

        // Pedal position at a moment in the history, interpolated; the oldest
        // sample if it's earlier than that.
        private double PedalAt(long ticks)
        {
            int newer = -1;
            for (int i = 1; i <= _histCount; i++)
            {
                int idx = (_histHead - i + HistorySize) % HistorySize;
                if (_histTicks[idx] <= ticks)
                {
                    if (newer < 0 || _histTicks[newer] == _histTicks[idx]) return _histPedal[idx];
                    double f = (ticks - _histTicks[idx]) / (double)(_histTicks[newer] - _histTicks[idx]);
                    return _histPedal[idx] + f * (_histPedal[newer] - _histPedal[idx]);
                }
                newer = idx;
            }
            return newer >= 0 ? _histPedal[newer] : _onsetPedal;
        }

        // Pedal travel (upward = positive) per second over the last RateWindowSec.
        private double ReleaseRate(long now, double pedalNow)
        {
            long window = (long)(RateWindowSec * Stopwatch.Frequency);
            for (int i = 1; i <= _histCount; i++)
            {
                int idx = (_histHead - i + HistorySize) % HistorySize;
                long age = now - _histTicks[idx];
                if (age >= window || i == _histCount)
                {
                    if (age <= 0) return 0;
                    return (_histPedal[idx] - pedalNow) * Stopwatch.Frequency / age;
                }
            }
            return 0;
        }
    }

    /// <summary>
    /// A user-started calibration run. Commands arrive from any thread;
    /// <see cref="Update"/> runs on the SimHub data thread and owns every state change.
    /// </summary>
    internal sealed class MBoosterBitePointCalibrator
    {
        private const int RequestNone = 0, RequestToggle = 1, RequestStop = 2;

        private readonly MBoosterBitePointDetector _detector = new MBoosterBitePointDetector();
        private int _request;
        private int _state;
        private string? _game;

        public BitePointCalState State => (BitePointCalState)Volatile.Read(ref _state);

        public bool IsActive
        {
            get
            {
                var s = State;
                return s == BitePointCalState.Waiting || s == BitePointCalState.Armed || s == BitePointCalState.Releasing;
            }
        }

        /// <summary>Start a run, or cancel the one in progress.</summary>
        public void Toggle() => Interlocked.Exchange(ref _request, RequestToggle);

        /// <summary>Drop any run and return to Idle.</summary>
        public void Stop() => Interlocked.Exchange(ref _request, RequestStop);

        /// <param name="game">SimHub game code.</param>
        /// <param name="ready">Driving, in gear, with a clutch pedal reporting.</param>
        /// <param name="capturedGame">Game the capture belongs to.</param>
        /// <returns>True on the frame a bite point is captured.</returns>
        public bool Update(string? game, bool ready, double speedKmh, double pedal01,
            out string capturedGame, out BitePointSample sample)
        {
            capturedGame = "";
            sample = default;

            switch (Interlocked.Exchange(ref _request, RequestNone))
            {
                case RequestToggle:
                    if (IsActive || string.IsNullOrEmpty(game)) SetState(BitePointCalState.Cancelled);
                    else
                    {
                        _game = game;
                        _detector.Reset();
                        SetState(BitePointCalState.Waiting);
                    }
                    break;
                case RequestStop:
                    SetState(BitePointCalState.Idle);
                    break;
            }

            if (!IsActive) return false;
            if (!string.Equals(game, _game, StringComparison.OrdinalIgnoreCase))
            {
                SetState(BitePointCalState.Cancelled);
                return false;
            }

            if (_detector.Update(ready, speedKmh, pedal01, out sample))
            {
                capturedGame = _game!;
                SetState(BitePointCalState.Captured);
                return true;
            }
            SetState(_detector.Current switch
            {
                MBoosterBitePointDetector.Phase.Armed => BitePointCalState.Armed,
                MBoosterBitePointDetector.Phase.Releasing => BitePointCalState.Releasing,
                _ => BitePointCalState.Waiting,
            });
            return false;
        }

        private void SetState(BitePointCalState s) => Volatile.Write(ref _state, (int)s);
    }
}
