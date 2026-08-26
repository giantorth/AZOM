using System;
using System.Diagnostics;
using System.Threading;
using MozaPlugin.Protocol;
using MozaPlugin.Settings;

namespace MozaPlugin.Devices.Haptics
{
    /// <summary>
    /// Renders the three host-rendered wheelbase LFE channels (Engine / ABS /
    /// Gearshift) as cmd 0x2D/0x77 streams. Each channel's four parameters —
    /// trigger, frequency, intensity, smoothness — are dual-mode: a static slider
    /// value, or an NCalc/property formula (evaluated live per tick via the
    /// injected evaluator) that overrides it. The trigger gates the channel:
    /// non-zero = active for the level channels (Engine/ABS), or a change fires a
    /// burst for the edge channel (Gearshift). Available only on base firmware
    /// >= 1.2.10.10 (MozaData.BaseSupportsLfe). See docs/protocol/devices/wheelbase-0x13.md.
    ///
    /// Parameters are not waveforms — the firmware oscillates at the streamed
    /// frequency; the host sets freq + an amplitude ENVELOPE (intensity shaped by
    /// smoothness: 100 = steady, 0 = full-swing pulse). Frequency is sent
    /// unclamped (the wire encoder saturates the freq field at 200 Hz).
    ///
    /// ShakeIt mode: while the "MOZA Wheelbase LFE" ShakeIt haptics device posts
    /// values (see ShakeIt/), the worker renders those on all three slots instead
    /// of the plugin channels. See the ShakeIt takeover block below.
    /// </summary>
    internal sealed class BaseLfeEffectWorker : IDisposable
    {
        private const int TickPeriodMs = 20;                    // 50 Hz
        private const double TickPeriodSec = TickPeriodMs / 1000.0;
        private const long FeedStaleMs = 250;                  // feed paused/stopped → silence game-driven effects
        private const long ShakeItStaleMs = 1000;              // ShakeIt device gone/stalled → fall back to plugin channels
        private const long GearshiftBurstMs = 120;             // per-shift / per-bump burst hold
        // Momentary test-button patterns. Which pattern a slot's Test plays is
        // chosen by the slot's Mode (Engine sweep / ABS pulse / Gearshift bump /
        // Custom tone), not the slot's fixed wire id — see RenderTest.
        private const long EngineTestMs = 2000;                // engine test = 2 s frequency sweep
        private const double SweepIdleFraction = 0.12;         // sweeps start ~12% up the band (idle ≈ 600 of ~5000 rpm), not at 0 rpm
        // A parked test has no live RPM, so the Engine/Custom sweep substitutes a
        // synthetic RPM (idle→redline) into the real formulas and evaluates them —
        // so the combined preset is coherent (every slot at the SAME rpm) and matches
        // in-game exactly. Non-RPM property refs still read live telemetry.
        private const double SynthRedlineRpm = EngineVibrationMath.DefaultRedlineRpm;
        private const double SynthMaxSpeedKmh = 250.0;       // speed at the top of the sweep (for speed-scaled effects)
        private const string RpmToken = "[DataCorePlugin.GameData.Rpms]";
        private const string MaxRpmToken = "[DataCorePlugin.GameData.MaxRpm]";
        private const string ThrottleToken = "[DataCorePlugin.GameData.Throttle]";
        private const string BrakeToken = "[DataCorePlugin.GameData.Brake]";
        private const string SpeedToken = "[DataCorePlugin.GameData.SpeedKmh]";
        private const long AbsTestMs = 1000;                   // abs test = 1 s pulse burst
        private const double GsTestBump1EndMs = 70;            // gearshift test = two rapid bumps
        private const double GsTestBump2StartMs = 180;
        private const double GsTestBump2EndMs = 250;

        // Active profile carries no LFE block → factory defaults (all disabled).
        private static readonly BaseLfeSettings DefaultLfe = new BaseLfeSettings();

        private readonly MozaDeviceManager _base;              // base == primary pipe
        private readonly DeviceDetectionState _detectionState;
        private readonly MozaData _data;
        private readonly Func<BaseLfeSettings?> _lookup;
        private readonly Func<bool> _isShuttingDown;
        private readonly Func<string, double> _evalFormula;    // NCalc/property → double (0 on error)

        private Thread? _thread;
        private volatile bool _stop;

        // Feed liveness (from the SimHub data thread). gameActive = running and
        // not paused/in-menu; _lastFrameTicks detects a stopped feed (game exit).
        private volatile bool _latestGameActive;
        private long _lastFrameTicks;

        // Momentary test triggers — one start timestamp per slot (0 = idle). The
        // pattern + duration come from the slot's Mode at render time.
        private long _engineTestStart, _absTestStart, _gearTestStart;

        // Per-channel state (worker thread only). Every channel carries both the
        // continuous phase and the OnChange edge state, so any channel can run in
        // either trigger mode (used by the additive-engine partials).
        private struct ChannelState
        {
            public bool Active;
            public double Phase;
            public double LastTrigger;   // OnChange edge detection
            public bool Warm;
            public long BurstUntil;
            public long LastFireTicks;
            public double ScopeFreq;     // last carrier freq (Hz) sent — for the UI scope
            public double ScopeAmp;      // last amplitude (0..1) sent (0 = idle)
        }
        private ChannelState _engineSt, _absSt, _gearSt;

        // ── ShakeIt takeover ──────────────────────────────────────────────────
        // While a "MOZA Wheelbase LFE" ShakeIt haptics device exists in SimHub, its
        // provider posts the mixed per-oscillator (gain, freq) for all three summed
        // LFE oscillators every SimHub data tick and the worker renders THOSE
        // instead of the plugin channels (single wire owner; the plugin's LFE tab
        // is hidden for as long as the device posts). Immutable snapshot swap — the
        // tick reads the reference once; freshness expiry covers device removal
        // without Stop().
        private sealed class ShakeItSnapshot
        {
            public readonly double Gain0, Freq0, Gain1, Freq1, Gain2, Freq2;
            public readonly long Ticks;
            public ShakeItSnapshot(double g0, double f0, double g1, double f1, double g2, double f2)
            {
                Gain0 = g0; Freq0 = f0; Gain1 = g1; Freq1 = f1; Gain2 = g2; Freq2 = f2;
                Ticks = Stopwatch.GetTimestamp();
            }
        }
        private volatile ShakeItSnapshot? _shakeIt;
        private long _shakeItStaleTicks;

        // Latest sent (carrier freq, amplitude) per slot for the settings scope.
        // Plain reads (viz only — a torn double read is a single invisible frame).
        public double ScopeEngineFreq => _engineSt.ScopeFreq; public double ScopeEngineAmp => _engineSt.ScopeAmp;
        public double ScopeAbsFreq => _absSt.ScopeFreq;       public double ScopeAbsAmp => _absSt.ScopeAmp;
        public double ScopeGearFreq => _gearSt.ScopeFreq;     public double ScopeGearAmp => _gearSt.ScopeAmp;

        private long _burstTicks, _feedStaleTicks;

        public BaseLfeEffectWorker(
            MozaDeviceManager baseManager,
            DeviceDetectionState detectionState,
            MozaData data,
            Func<BaseLfeSettings?> lookup,
            Func<string, double> evalFormula,
            Func<bool> isShuttingDown)
        {
            _base = baseManager;
            _detectionState = detectionState;
            _data = data;
            _lookup = lookup;
            _evalFormula = evalFormula ?? (_ => 0.0);
            _isShuttingDown = isShuttingDown;
        }

        public void Start()
        {
            _stop = false;
            _burstTicks = Stopwatch.Frequency * GearshiftBurstMs / 1000;
            _feedStaleTicks = Stopwatch.Frequency * FeedStaleMs / 1000;
            _shakeItStaleTicks = Stopwatch.Frequency * ShakeItStaleMs / 1000;
            _thread = new Thread(Loop) { Name = "MozaBaseLfe", IsBackground = true };
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

        /// <summary>Latest ShakeIt per-oscillator (gain 0..1, freq Hz) for the three summed LFE slots (SimHub data thread).</summary>
        public void PostShakeItChannels(double gain0, double freq0, double gain1, double freq1, double gain2, double freq2)
            => _shakeIt = new ShakeItSnapshot(gain0, freq0, gain1, freq1, gain2, freq2);

        /// <summary>Immediate ShakeIt release (device stopped/removed) — next tick falls back to the plugin channels.</summary>
        public void ClearShakeItChannels() => _shakeIt = null;

        // Each slot's Test plays the pattern for that slot's current Mode.
        public void PostEngineTest() => Interlocked.Exchange(ref _engineTestStart, Stopwatch.GetTimestamp());
        public void PostAbsTest() => Interlocked.Exchange(ref _absTestStart, Stopwatch.GetTimestamp());
        public void PostGearshiftTest() => Interlocked.Exchange(ref _gearTestStart, Stopwatch.GetTimestamp());

        private void Loop()
        {
            long stopwatchFreq = Stopwatch.Frequency;
            long periodTicks = stopwatchFreq * TickPeriodMs / 1000;
            long next = Stopwatch.GetTimestamp() + periodTicks;
            while (!_stop)
            {
                try { Tick(); }
                catch (Exception ex) { MozaLog.Debug($"[AZOM/LFE] tick: {ex.Message}"); }

                long now = Stopwatch.GetTimestamp();
                long delta = next - now;
                if (delta <= 0) { next = now + periodTicks; continue; }
                int sleepMs = (int)Math.Min(50, Math.Max(1, delta * 1000 / stopwatchFreq));
                Thread.Sleep(sleepMs);
                next += periodTicks;
            }
        }

        private void Tick()
        {
            if (_isShuttingDown()) return;
            if (_base == null || !_base.IsConnected || !_detectionState.BaseDetected) return;
            if (!_data.BaseSupportsLfe) { SilenceIfActive(); return; }

            // ShakeIt takeover: fresh provider values own all three slots (test
            // buttons included — the ShakeIt device page has its own test tools).
            var shakeIt = _shakeIt;
            if (shakeIt != null && Stopwatch.GetTimestamp() - shakeIt.Ticks <= _shakeItStaleTicks)
            {
                TickShakeIt(shakeIt);
                return;
            }

            // LFE routed to SimHub's ShakeIt haptics: the plugin's own engine/ABS/
            // gearshift modes must stay off the wire even between ShakeIt posts,
            // or a lull in the effect stream would let them bleed through.
            if (MozaPlugin.Instance?.WheelbaseLfeRoutedToShakeIt == true) { SilenceIfActive(); return; }

            // Feed paused/stopped → game-driven activation goes false (test
            // triggers still fire on their own deadlines).
            bool feedLive = _latestGameActive &&
                (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastFrameTicks)) <= _feedStaleTicks;

            var lfe = _lookup() ?? DefaultLfe;
            TickEngine(lfe, feedLive);
            TickAbs(lfe, feedLive);
            TickGearshift(lfe, feedLive);
        }

        // Game-driven activation, mode-aware. Level = trigger != 0 (continuous);
        // OnChange = burst on trigger change (neutral-suppress + debounce, edge
        // state kept per channel). Test windows are handled by the per-channel
        // tick methods. Any channel can run in either mode (additive partials).
        private bool ActiveByGame(BaseLfeChannel ch, bool feedLive, ref ChannelState st)
        {
            if (!ch.Enabled || !feedLive) { st.Warm = false; return false; }
            if (ch.TriggerMode == BaseLfeTriggerMode.Level)
                return Triggered(ch.TriggerFormula);

            long now = Stopwatch.GetTimestamp();
            double raw = EvalFormulaRaw(ch.TriggerFormula);
            if (!st.Warm) { st.LastTrigger = raw; st.Warm = true; }
            else if (Math.Abs(raw - st.LastTrigger) > 1e-6)
            {
                bool intoNeutral = Math.Abs(raw) < 0.5;   // gear ≈ 0
                st.LastTrigger = raw;
                int debounceMs = ch.DebounceMs; if (debounceMs < 0) debounceMs = 0;
                bool debounced = (now - st.LastFireTicks) * 1000.0 / Stopwatch.Frequency < debounceMs;
                if ((!intoNeutral || ch.VibrateOnNeutral) && !debounced)
                {
                    st.LastFireTicks = now;
                    st.BurstUntil = now + _burstTicks;
                }
            }
            return st.BurstUntil > now;
        }

        // ── Engine slot (wire id 1, continuous stream) ────────────────────────
        private void TickEngine(BaseLfeSettings lfe, bool feedLive)
        {
            var ch = lfe.Engine ?? DefaultLfe.Engine;
            if (TickTest(ref _engineTestStart, ch, ref _engineSt, MozaBaseLfeProtocol.LfeEffect.Engine,
                         (f, a) => _base.SendBaseLfeEngineStream(playing: true, f, a))) return;
            if (!ActiveByGame(ch, feedLive, ref _engineSt)) { DisableIf(ref _engineSt, MozaBaseLfeProtocol.LfeEffect.Engine); return; }
            EvalRender(ch, out double freq, out double int01, out double smooth01);
            double amp = Envelope(int01, ref _engineSt.Phase, freq, smooth01);
            _base.SendBaseLfeEngineStream(playing: true, freq, amp);
            _engineSt.Active = true;
            _engineSt.ScopeFreq = freq; _engineSt.ScopeAmp = amp;
        }

        // ── ABS slot (wire id 2, continuous stream) ───────────────────────────
        private void TickAbs(BaseLfeSettings lfe, bool feedLive)
        {
            var ch = lfe.Abs ?? DefaultLfe.Abs;
            if (TickTest(ref _absTestStart, ch, ref _absSt, MozaBaseLfeProtocol.LfeEffect.Abs,
                         (f, a) => _base.SendBaseLfeAbsStream(playing: true, f, a))) return;
            if (!ActiveByGame(ch, feedLive, ref _absSt)) { DisableIf(ref _absSt, MozaBaseLfeProtocol.LfeEffect.Abs); return; }
            EvalRender(ch, out double freq, out double int01, out double smooth01);
            double amp = Envelope(int01, ref _absSt.Phase, freq, smooth01);
            _base.SendBaseLfeAbsStream(playing: true, freq, amp);
            _absSt.Active = true;
            _absSt.ScopeFreq = freq; _absSt.ScopeAmp = amp;
        }

        // ── Gearshift slot (wire id 0, one-shot burst) ────────────────────────
        private void TickGearshift(BaseLfeSettings lfe, bool feedLive)
        {
            var ch = lfe.Gearshift ?? DefaultLfe.Gearshift;
            if (TickTest(ref _gearTestStart, ch, ref _gearSt, MozaBaseLfeProtocol.LfeEffect.Gearshift,
                         (f, a) => _base.SendBaseLfeGearshiftBurst(f, a))) return;
            if (!ActiveByGame(ch, feedLive, ref _gearSt)) { DisableIf(ref _gearSt, MozaBaseLfeProtocol.LfeEffect.Gearshift); return; }
            EvalRender(ch, out double freq, out double int01, out double smooth01);
            double amp = Envelope(int01, ref _gearSt.Phase, freq, smooth01);
            _base.SendBaseLfeGearshiftBurst(freq, amp);
            _gearSt.Active = true;
            _gearSt.ScopeFreq = freq; _gearSt.ScopeAmp = amp;
        }

        // Test playback for one slot. Returns true while the test owns the slot
        // (rendered a frame or a silent gap); false once elapsed so the caller
        // falls through to the normal game-driven path. Pattern chosen by Mode.
        private bool TickTest(ref long testStart, BaseLfeChannel ch, ref ChannelState st,
                              MozaBaseLfeProtocol.LfeEffect id, Action<double, double> send)
        {
            long ts = Interlocked.Read(ref testStart);
            if (ts <= 0) return false;
            double ms = (Stopwatch.GetTimestamp() - ts) * 1000.0 / Stopwatch.Frequency;
            if (!RenderTest(ch.Mode, ms, ch, out bool playing, out double freq, out double int01, out double smooth01))
            {
                Interlocked.Exchange(ref testStart, 0);     // window elapsed → hand back to the game path
                return false;
            }
            if (playing)
            {
                double amp = Envelope(int01, ref st.Phase, freq, smooth01);
                send(freq, amp); st.Active = true;
                st.ScopeFreq = freq; st.ScopeAmp = amp;
            }
            else DisableIf(ref st, id);                      // silent gap (gearshift between bumps)
            return true;
        }

        // Momentary test pattern for a slot's Mode. Returns false once the window
        // has elapsed. Uses the slot's static (fallback) values so a parked car
        // (formula → 0) still produces a felt test. `playing` gates the gearshift
        // inter-bump gap.
        private bool RenderTest(BaseLfeMode mode, double ms, BaseLfeChannel ch,
                                out bool playing, out double freq, out double int01, out double smooth01)
        {
            // A parked test has no telemetry to drive a frequency formula, so when a
            // formula is set we demonstrate the [min,max] limit band directly: the
            // Engine sweep runs min→max, the steady modes play the band midpoint. A
            // plain slider value has no band (lo==hi), so it plays that fixed value.
            bool hasFormula = !string.IsNullOrWhiteSpace(ch.FrequencyFormula);
            double lo = hasFormula ? Math.Min(ch.FrequencyMin, ch.FrequencyMax) : Clamp(ch.Frequency, 0, 500);
            double hi = hasFormula ? Math.Max(ch.FrequencyMin, ch.FrequencyMax) : Clamp(ch.Frequency, 0, 500);
            freq = (lo + hi) / 2.0;
            int01 = ClampPct(ch.Intensity) / 100.0;
            smooth01 = ClampPct(ch.Smoothness) / 100.0;
            playing = true;
            switch (mode)
            {
                case BaseLfeMode.Abs:                         // fixed-freq amplitude pulse (from smoothness)
                    return ms <= AbsTestMs;
                case BaseLfeMode.Gearshift:                   // two rapid bumps at the configured freq
                    if (ms >= GsTestBump2EndMs) return false;
                    playing = ms < GsTestBump1EndMs || (ms >= GsTestBump2StartMs && ms < GsTestBump2EndMs);
                    return true;
                default:
                    // Engine + Custom: continuous like in-game. Sweep a synthetic RPM
                    // idle→redline and run the REAL formulas against it, so every slot in
                    // a combined preset sees the SAME rpm at each instant (coherent, and
                    // exactly the frequency/intensity/smoothness the formulas produce in
                    // game — not just a linear range sweep). Starting at idle also keeps
                    // the sweep out of the near-0 Hz region where a waveform cycle is so
                    // long the base ignores mid-sweep updates.
                    if (ms > EngineTestMs) return false;
                    double pct = SweepIdleFraction + (1.0 - SweepIdleFraction) * Clamp01(ms / EngineTestMs);
                    double synthRpm = pct * SynthRedlineRpm;
                    double synthSpeed = pct * SynthMaxSpeedKmh;
                    freq = hasFormula ? ch.RescaleFreq(EvalTestFormula(ch.FrequencyFormula!, synthRpm, synthSpeed)) : ch.Frequency;
                    int01 = Clamp01(EvalTestParam(ch.IntensityFormula, ClampPct(ch.Intensity), synthRpm, synthSpeed) / 100.0);
                    smooth01 = Clamp01(EvalTestParam(ch.SmoothnessFormula, ClampPct(ch.Smoothness), synthRpm, synthSpeed) / 100.0);
                    return true;
            }
        }

        private void EvalRender(BaseLfeChannel ch, out double freq, out double intensity01, out double smoothness01)
        {
            // Plain slider value is used as-is. A formula's output is linearly
            // RE-SCALED from the full 0..200 Hz wire range into the channel's
            // permitted [min, max] band, so the whole dynamic range is preserved
            // (compressed to fit) instead of flat-topping at the limits. The
            // full 0..200 band is the identity map (presets unaffected).
            if (string.IsNullOrWhiteSpace(ch.FrequencyFormula))
                freq = ch.Frequency;
            else
                freq = ch.RescaleFreq(_evalFormula(ch.FrequencyFormula!));
            intensity01 = Clamp01(EvalParam(ch.IntensityFormula, ch.Intensity) / 100.0);
            smoothness01 = Clamp01(EvalParam(ch.SmoothnessFormula, ch.Smoothness) / 100.0);
        }

        private void DisableIf(ref ChannelState st, MozaBaseLfeProtocol.LfeEffect id)
        {
            if (st.Active) { _base.SendBaseLfeDisable(id); st.Active = false; st.Phase = 0; }
            st.ScopeAmp = 0; st.ScopeFreq = 0;   // scope: idle ribbon collapses to the floor
        }

        // ── ShakeIt-driven rendering ──────────────────────────────────────────
        // Gains arrive final from SimHub's tone mixer (global gain, effect gain,
        // channel mapping applied) — no envelope shaping here. All three summed
        // oscillators stream: 0 → id1 (engine slot), 1 → id2 (ABS slot), 2 → id0
        // (Osc0 slot). A slot with ~zero gain is disabled (one-shot edge), so an
        // unmapped oscillator stays silent and can't latch a residual tone.
        private void TickShakeIt(ShakeItSnapshot s)
        {
            RenderShakeItSlot(ref _engineSt, MozaBaseLfeProtocol.LfeEffect.Engine, s.Gain0, s.Freq0,
                (f, a) => _base.SendBaseLfeEngineStream(playing: true, f, a));
            RenderShakeItSlot(ref _absSt, MozaBaseLfeProtocol.LfeEffect.Abs, s.Gain1, s.Freq1,
                (f, a) => _base.SendBaseLfeAbsStream(playing: true, f, a));
            RenderShakeItSlot(ref _gearSt, MozaBaseLfeProtocol.LfeEffect.Gearshift, s.Gain2, s.Freq2,
                (f, a) => _base.SendBaseLfeOsc0Stream(playing: true, f, a));
        }

        private void RenderShakeItSlot(ref ChannelState st, MozaBaseLfeProtocol.LfeEffect id,
                                       double gain, double freqHz, Action<double, double> send)
        {
            double amp = Clamp01(gain);
            if (amp < 1e-4 || freqHz <= 0) { DisableIf(ref st, id); return; }
            send(freqHz, amp);
            st.Active = true;
            st.ScopeFreq = freqHz; st.ScopeAmp = amp;
        }

        private void SilenceIfActive()
        {
            DisableIf(ref _engineSt, MozaBaseLfeProtocol.LfeEffect.Engine);
            DisableIf(ref _absSt, MozaBaseLfeProtocol.LfeEffect.Abs);
            DisableIf(ref _gearSt, MozaBaseLfeProtocol.LfeEffect.Gearshift);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Trigger truth: empty formula = always active; else eval != 0.</summary>
        private bool Triggered(string? formula)
        {
            if (string.IsNullOrWhiteSpace(formula)) return true;
            return Math.Abs(_evalFormula(formula!)) > 1e-9;
        }

        private double EvalFormulaRaw(string? formula)
            => string.IsNullOrWhiteSpace(formula) ? 0.0 : _evalFormula(formula!);

        // Evaluate a formula against a synthetic "accelerating flat-out" telemetry
        // snapshot (rpm + speed sweeping, full throttle, no brake) so a parked test
        // drives the real formulas — including load- and speed-scaled intensity.
        // A pure-arithmetic result (RPM/throttle/speed-only formula) is parsed
        // directly; anything still referencing other telemetry evaluates live.
        private double EvalTestFormula(string formula, double synthRpm, double synthSpeed)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string s = formula
                .Replace(RpmToken, synthRpm.ToString("R", inv))
                .Replace(MaxRpmToken, SynthRedlineRpm.ToString("R", inv))
                .Replace(ThrottleToken, "100")      // flat out
                .Replace(BrakeToken, "0")
                .Replace(SpeedToken, synthSpeed.ToString("R", inv));
            if (double.TryParse(s, System.Globalization.NumberStyles.Any, inv, out double lit))
                return lit;
            return _evalFormula(s);
        }
        private double EvalTestParam(string? formula, double staticVal, double synthRpm, double synthSpeed)
            => string.IsNullOrWhiteSpace(formula) ? staticVal : EvalTestFormula(formula!, synthRpm, synthSpeed);

        /// <summary>Slider value, or the formula's evaluated value when non-empty.</summary>
        private double EvalParam(string? formula, double sliderValue)
            => string.IsNullOrWhiteSpace(formula) ? sliderValue : _evalFormula(formula!);

        /// <summary>
        /// Amplitude envelope: <c>intensity01 × ((1-depth) + depth·(0.5+0.5·sin φ))</c>,
        /// depth = 1 - smoothness01. smoothness 100 → steady; 0 → full-swing pulse.
        /// Phase advances at the carrier frequency.
        /// </summary>
        private static double Envelope(double intensity01, ref double phase, double freqHz, double smoothness01)
        {
            double depth = 1.0 - smoothness01;
            if (depth > 1e-6 && freqHz > 0)
                phase = EngineVibrationMath.AdvancePhase(phase, freqHz, TickPeriodSec);
            double env = (1.0 - depth) + depth * (0.5 + 0.5 * Math.Sin(phase));
            return Clamp01(intensity01 * env);
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        private static int ClampPct(int v) => v < 0 ? 0 : (v > 100 ? 100 : v);
    }
}
