using System;
using System.Diagnostics;
using System.Threading;
using MozaPlugin.Devices.Haptics;
using MozaPlugin.Protocol;
using MozaPlugin.Settings;

namespace MozaPlugin.Devices.PedalHaptics
{
    /// <summary>
    /// Renders the S12 tab: nine channel cards, each sent to its own firmware
    /// slot (S12 vibration guide, "Effect channels") on every pedal ticked on the
    /// card. Trigger / frequency / intensity are slider-or-formula as on the LFE
    /// tab, evaluated live per tick.
    ///
    /// Two trigger modes:
    /// <list type="bullet">
    /// <item>Level — streamed while the trigger is non-zero.</item>
    /// <item>On-change — a burst of <see cref="PedalHapticsEffect.PulseMs"/> per
    /// accepted edge. On the timed slots that is one frame carrying the duration
    /// and the firmware ends it; Engine and Road Texture ignore duration, so
    /// those stream for the pulse length instead.</item>
    /// </list>
    ///
    /// Only runs while Options routes the S12 to the plugin tab; ShakeIt owns the
    /// scheduler otherwise and this posts nothing.
    /// </summary>
    internal sealed class PedalHapticsEffectWorker : IDisposable
    {
        private const int TickPeriodMs = 20;
        private const long FeedStaleMs = 250;

        private const int Pedals = MozaPedalHapticsProtocol.PedalCount;
        private const int Slots = MozaPedalHapticsProtocol.SlotsPerPedal;
        private const int RoadSlot = MozaPedalHapticsProtocol.RoadTextureSlot;
        private const int EngineSlot = (int)PedalHapticsEffectSlot.EngineVibration;

        // Test patterns, picked by the card's channel and trigger mode.
        private const double SweepTestMs = 2000;          // Engine RPM sweep / Road position sweep
        private const double HoldTestMs = 1000;           // Level timed slots
        private const double SecondBurstMs = 180;         // On-change: two bursts this far apart
        private const double RoadTestHz = 3.0;            // position oscillation during the Road test

        private static readonly PedalHapticsSettings DefaultSettings = new PedalHapticsSettings();

        private readonly Func<MozaPedalHapticsRegistry?> _registry;
        private readonly Func<PedalHapticsSettings?> _lookup;
        private readonly Func<string, double> _evalFormula;
        private readonly Func<bool> _routedToTab;
        private readonly Func<bool> _isShuttingDown;

        private Thread? _thread;
        private volatile bool _stop;

        private volatile bool _latestGameActive;
        private long _lastFrameTicks;
        private long _feedStaleTicks;

        private struct ChannelState
        {
            public HapticsEdgeDetector Edge;
            public long StreamUntil;     // On-change on Engine / Road: stream until
            public long ScopeUntil;      // firmware burst in flight, for the scope
            public double ScopeFreq, ScopeAmp;
            public int TestBursts;       // On-change test: bursts already fired
        }
        private readonly ChannelState[] _st = new ChannelState[Slots];
        private readonly long[] _testStart = new long[Slots];

        // Built per tick, then posted in one pass.
        private readonly double[,] _gain = new double[Pedals, Slots];
        private readonly double[,] _freq = new double[Pedals, Slots];

        // Per-pedal scope: strongest live slot. Viz only — torn reads are harmless.
        private readonly double[] _scopeFreq = new double[Pedals];
        private readonly double[] _scopeAmp = new double[Pedals];

        // True once this worker has posted since it last went idle.
        private bool _posting;

        public PedalHapticsEffectWorker(
            Func<MozaPedalHapticsRegistry?> registry,
            Func<PedalHapticsSettings?> lookup,
            Func<string, double> evalFormula,
            Func<bool> routedToTab,
            Func<bool> isShuttingDown)
        {
            _registry = registry;
            _lookup = lookup;
            _evalFormula = evalFormula ?? (_ => 0.0);
            _routedToTab = routedToTab;
            _isShuttingDown = isShuttingDown;
        }

        public void Start()
        {
            _stop = false;
            _feedStaleTicks = Stopwatch.Frequency * FeedStaleMs / 1000;
            _thread = new Thread(Loop) { Name = "MozaPedalHaptics", IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { _thread?.Join(1000); } catch { }
            _thread = null;
        }

        public void Dispose() => Stop();

        /// <summary>Feed liveness from the SimHub data thread (game running and not paused/in-menu).</summary>
        public void PostFrame(bool gameActive)
        {
            _latestGameActive = gameActive;
            Interlocked.Exchange(ref _lastFrameTicks, Stopwatch.GetTimestamp());
        }

        /// <summary>Play one channel's test pattern on its ticked pedals.</summary>
        public void PostTest(PedalHapticsEffectSlot slot)
        {
            int s = (int)slot;
            if (s < 0 || s >= Slots) return;
            Interlocked.Exchange(ref _testStart[s], Stopwatch.GetTimestamp());
        }

        /// <summary>Latest (freq, amplitude 0..1) per pedal — throttle, brake, clutch.</summary>
        public (double freq, double amp)[] ScopeSamples()
        {
            var r = new (double, double)[Pedals];
            for (int p = 0; p < Pedals; p++) r[p] = (_scopeFreq[p], _scopeAmp[p]);
            return r;
        }

        private void Loop()
            => PacedWorkerLoop.Run(TickPeriodMs, () => _stop, IsActive, Tick, "[AZOM/PedalHaptics] tick");

        private bool IsActive()
        {
            if (_isShuttingDown()) return false;
            var reg = _registry();
            if (reg == null) return false;
            return (_routedToTab() && reg.AnyReady) || _posting;
        }

        private void Tick()
        {
            var reg = _registry();
            if (reg == null || _isShuttingDown()) return;

            // Not ours (ShakeIt routed) or nothing to drive: stop posting. A mode
            // switch clears the scheduler; staleness covers a dropped unit.
            if (!_routedToTab() || !reg.AnyReady)
            {
                if (_posting) ResetIdle();
                return;
            }

            bool feedLive = _latestGameActive &&
                (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastFrameTicks)) <= _feedStaleTicks;
            var settings = _lookup() ?? DefaultSettings;
            long now = Stopwatch.GetTimestamp();

            Array.Clear(_gain, 0, _gain.Length);
            Array.Clear(_freq, 0, _freq.Length);
            for (int s = 0; s < Slots; s++)
                TickChannel(reg, s, settings.Get((PedalHapticsEffectSlot)s), feedLive, now);

            for (int p = 0; p < Pedals; p++)
            {
                double bestAmp = 0, bestFreq = 0;
                for (int s = 0; s < Slots; s++)
                {
                    reg.PostSlot(p, s, _gain[p, s], _freq[p, s]);
                    if (_gain[p, s] > bestAmp) { bestAmp = _gain[p, s]; bestFreq = _freq[p, s]; }
                }
                _scopeAmp[p] = bestAmp;
                _scopeFreq[p] = bestFreq;
            }
            ApplyBurstScope(settings, now);
            _posting = true;
        }

        private void ResetIdle()
        {
            _posting = false;
            for (int s = 0; s < Slots; s++)
            {
                _st[s].Edge.Reset();
                _st[s].StreamUntil = 0;
                _st[s].ScopeUntil = 0;
                _st[s].TestBursts = 0;
                Interlocked.Exchange(ref _testStart[s], 0);
            }
            for (int p = 0; p < Pedals; p++) { _scopeAmp[p] = 0; _scopeFreq[p] = 0; }
        }

        private void TickChannel(MozaPedalHapticsRegistry reg, int s, PedalHapticsEffect fx, bool feedLive, long now)
        {
            ref var st = ref _st[s];
            if (TickTest(reg, s, fx, ref st, now)) return;

            if (!fx.Enabled || !feedLive)
            {
                st.Edge.Reset();
                st.StreamUntil = 0;
                return;
            }

            if (fx.TriggerMode == BaseLfeTriggerMode.Level)
            {
                if (!Triggered(fx.TriggerFormula)) return;
                EvalRender(s, fx, out double freq, out double int01);
                Output(s, fx, int01, freq);
                return;
            }

            double raw = string.IsNullOrWhiteSpace(fx.TriggerFormula) ? 0.0 : _evalFormula(fx.TriggerFormula);
            if (st.Edge.Fire(raw, fx.VibrateOnNeutral, fx.DebounceMs, now))
            {
                long pulseTicks = Stopwatch.Frequency * Math.Max(1, fx.PulseMs) / 1000;
                if (MozaPedalHapticsProtocol.IsTimedSlot((byte)s))
                {
                    EvalRender(s, fx, out double freq, out double int01);
                    Burst(reg, s, fx, int01, freq);
                    st.ScopeUntil = now + pulseTicks;
                    st.ScopeFreq = freq; st.ScopeAmp = int01;
                }
                else
                {
                    st.StreamUntil = now + pulseTicks;
                }
            }
            if (st.StreamUntil > now)
            {
                EvalRender(s, fx, out double freq, out double int01);
                Output(s, fx, int01, freq);
            }
        }

        // Test playback for one channel. True while the test owns the slot.
        private bool TickTest(MozaPedalHapticsRegistry reg, int s, PedalHapticsEffect fx, ref ChannelState st, long now)
        {
            long ts = Interlocked.Read(ref _testStart[s]);
            if (ts <= 0) return false;
            double ms = (now - ts) * 1000.0 / Stopwatch.Frequency;
            if (ms < 0) ms = 0;

            double staticInt = ClampPct(fx.Intensity) / 100.0;
            bool timed = MozaPedalHapticsProtocol.IsTimedSlot((byte)s);

            if (s == EngineSlot)
            {
                if (ms > SweepTestMs) return EndTest(s, ref st);
                double pct = HapticsTestFormula.SweepIdleFraction
                           + (1.0 - HapticsTestFormula.SweepIdleFraction) * Clamp01(ms / SweepTestMs);
                double rpm = pct * HapticsTestFormula.SynthRedlineRpm;
                double speed = pct * HapticsTestFormula.SynthMaxSpeedKmh;
                double freq = string.IsNullOrWhiteSpace(fx.FrequencyFormula)
                    ? ClampHz(fx.Frequency)
                    : fx.RescaleFreq(HapticsTestFormula.Eval(fx.FrequencyFormula!, rpm, speed, _evalFormula));
                double int01 = Clamp01(HapticsTestFormula.Param(fx.IntensityFormula, ClampPct(fx.Intensity), rpm, speed, _evalFormula) / 100.0);
                Output(s, fx, int01, freq);
                return true;
            }

            if (s == RoadSlot)
            {
                if (ms > SweepTestMs) return EndTest(s, ref st);
                double pos = 50 + 50 * Math.Sin(2 * Math.PI * RoadTestHz * ms / 1000.0);
                Output(s, fx, staticInt, pos);
                return true;
            }

            double hz = TestFrequency(fx);
            if (timed && fx.TriggerMode == BaseLfeTriggerMode.OnChange)
            {
                int pulse = Math.Max(1, fx.PulseMs);
                if (ms >= SecondBurstMs + pulse) return EndTest(s, ref st);
                int due = ms >= SecondBurstMs ? 2 : 1;
                while (st.TestBursts < due)
                {
                    Burst(reg, s, fx, staticInt, hz);
                    st.TestBursts++;
                    st.ScopeUntil = now + Stopwatch.Frequency * pulse / 1000;
                    st.ScopeFreq = hz; st.ScopeAmp = staticInt;
                }
                return true;
            }

            if (ms > HoldTestMs) return EndTest(s, ref st);
            Output(s, fx, staticInt, hz);
            return true;
        }

        private bool EndTest(int s, ref ChannelState st)
        {
            Interlocked.Exchange(ref _testStart[s], 0);
            st.TestBursts = 0;
            return false;
        }

        // A parked test has no telemetry for a frequency formula, so it plays the
        // middle of the limit band; a plain slider plays its own value.
        private static double TestFrequency(PedalHapticsEffect fx)
        {
            if (string.IsNullOrWhiteSpace(fx.FrequencyFormula)) return ClampHz(fx.Frequency);
            return ClampHz((Math.Min(fx.FrequencyMin, fx.FrequencyMax) + Math.Max(fx.FrequencyMin, fx.FrequencyMax)) / 2.0);
        }

        private void EvalRender(int s, PedalHapticsEffect fx, out double freq, out double int01)
        {
            bool hasFormula = !string.IsNullOrWhiteSpace(fx.FrequencyFormula);
            if (s == RoadSlot)
                freq = Clamp(hasFormula ? _evalFormula(fx.FrequencyFormula!) : fx.Frequency,
                             MozaPedalHapticsProtocol.MinRoadPosition, MozaPedalHapticsProtocol.MaxRoadPosition);
            else
                freq = hasFormula ? fx.RescaleFreq(_evalFormula(fx.FrequencyFormula!)) : ClampHz(fx.Frequency);
            int01 = Clamp01((string.IsNullOrWhiteSpace(fx.IntensityFormula)
                ? fx.Intensity : _evalFormula(fx.IntensityFormula!)) / 100.0);
        }

        private void Output(int s, PedalHapticsEffect fx, double int01, double freq)
        {
            for (int p = 0; p < Pedals; p++)
            {
                if (!fx.OnPedal(p)) continue;
                _gain[p, s] = int01;
                _freq[p, s] = freq;
            }
        }

        private static void Burst(MozaPedalHapticsRegistry reg, int s, PedalHapticsEffect fx, double int01, double freq)
        {
            for (int p = 0; p < Pedals; p++)
                if (fx.OnPedal(p)) reg.PostBurst(p, s, int01, freq, Math.Max(1, fx.PulseMs));
        }

        // Firmware-timed bursts never pass through _gain, so fold them into the
        // per-pedal scope while they are in flight.
        private void ApplyBurstScope(PedalHapticsSettings settings, long now)
        {
            for (int s = 0; s < Slots; s++)
            {
                ref var st = ref _st[s];
                if (st.ScopeUntil <= now) continue;
                var fx = settings.Get((PedalHapticsEffectSlot)s);
                for (int p = 0; p < Pedals; p++)
                {
                    if (!fx.OnPedal(p) || st.ScopeAmp <= _scopeAmp[p]) continue;
                    _scopeAmp[p] = st.ScopeAmp;
                    _scopeFreq[p] = st.ScopeFreq;
                }
            }
        }

        /// <summary>Trigger truth: empty formula = always active; else eval != 0.</summary>
        private bool Triggered(string? formula)
        {
            if (string.IsNullOrWhiteSpace(formula)) return true;
            return Math.Abs(_evalFormula(formula!)) > 1e-9;
        }

        private static double ClampHz(double hz)
            => Clamp(hz, MozaPedalHapticsProtocol.MinFrequencyHz, MozaPedalHapticsProtocol.MaxFrequencyHz);
        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        private static int ClampPct(int v) => v < 0 ? 0 : (v > 100 ? 100 : v);
    }
}
