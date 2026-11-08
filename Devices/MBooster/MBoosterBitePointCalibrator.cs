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
    /// clutch pressed in, then the clutch comes up. The bite point is the
    /// clutch-pedal position on the first frame vehicle speed rises above its
    /// standstill value, kept once the speed rise is confirmed. Data thread only.
    /// </summary>
    internal sealed class MBoosterBitePointDetector
    {
        public enum Phase { Waiting, Armed, Releasing }

        // Pedal position (0..1) counted as "clutch fully in".
        internal const double ClutchInLevel = 0.9;
        // Below this the car counts as stopped.
        internal const double StandstillKmh = 0.5;
        // Rise over the standstill speed that marks the bite...
        internal const double OnsetRiseKmh = 0.3;
        // ...and the rise that confirms the car is really pulling away.
        internal const double ConfirmRiseKmh = 2.0;
        // Window the release rate at the bite is measured over.
        private const double RateWindowSec = 0.2;

        private const int HistorySize = 64;
        private readonly long[] _histTicks = new long[HistorySize];
        private readonly double[] _histPedal = new double[HistorySize];
        private int _histCount;
        private int _histHead;

        private double _baselineKmh;
        private double _onsetPedal = double.NaN;
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
                    Record(now, pedal01);
                    double rise = speedKmh - _baselineKmh;
                    if (rise <= OnsetRiseKmh)
                    {
                        // Back at standstill (false start): wait for a fresh onset.
                        _onsetPedal = double.NaN;
                        break;
                    }
                    if (double.IsNaN(_onsetPedal))
                    {
                        _onsetPedal = pedal01;
                        _onsetRate = ReleaseRate(now, pedal01);
                    }
                    if (rise >= ConfirmRiseKmh)
                    {
                        sample = new BitePointSample(
                            (int)Math.Round(Math.Max(0, Math.Min(1, _onsetPedal)) * 100.0), _onsetRate);
                        Reset();
                        return true;
                    }
                    break;
            }
            return false;
        }

        private void Record(long ticks, double pedal)
        {
            _histTicks[_histHead] = ticks;
            _histPedal[_histHead] = pedal;
            _histHead = (_histHead + 1) % HistorySize;
            if (_histCount < HistorySize) _histCount++;
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
