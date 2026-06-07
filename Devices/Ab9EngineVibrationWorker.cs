using System;
using System.Diagnostics;
using System.Threading;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Background worker that drives the AB9 host-rendered engine-vibration
    /// stream at ~91 Hz. See docs/protocol/devices/ab9-shifter.md.
    ///
    /// DataUpdate calls <see cref="PostFrame"/> to publish the latest RPM /
    /// game-running state; the worker thread reads them each tick alongside
    /// intensity/freq from the active profile (via the lookup callback).
    /// </summary>
    internal sealed class Ab9EngineVibrationWorker : IDisposable
    {
        // Oscillator period. The frequency slider is the buzz frequency AT
        // REDLINE; below redline the audible frequency scales with the RPM
        // fraction:  audible = freqSlider × (rpm / maxRpm).  Therefore
        //   period = FreqTickHz / audible = FreqTickHz × maxRpm / (rpm × freqSlider).
        //
        // FreqTickHz (the device oscillator tick clock) calibrated 2026-05-31
        // against ground-truth RPM telemetry captured alongside the AB9 stream
        // in usb-capture/AB9/ab9-pithouse-engine-vibration-intensity-2.pcapng
        // (freq slider held at 100 Hz; wheel dashboard streamed real Rpm/MaxRpm):
        //   FreqTickHz = median(period × rpm/maxRpm × freq) = 636,553 × 100
        //              ≈ 6.366e7   (CV 0.03, independent of the car's redline).
        // This maxRpm-scaling reconciles the capture (effective K = 1.197e12 at
        // an 18,800-rpm redline) with the earlier Cayman GT4 phone-mic (~100 Hz
        // at its 7,700-rpm redline, slider 100) — a fixed K cannot satisfy both,
        // FreqTickHz × maxRpm does (K = FreqTickHz × maxRpm). See
        // docs/protocol/devices/ab9-shifter.md and tools/ab9-rpm-correlate.
        private const double FreqTickHz = 6.18e7; //New value from kilarn123, old: 6.366e7;
        // Redline fallback when the game doesn't report MaxRpm (matches the
        // HardwareApplier 8000-rpm convention) so the slider still maps to a
        // sensible redline frequency.
        private const double DefaultRedlineRpm = 8000.0;
        private const int TickPeriodMs = 11;
        // Sub-stream tick budgets. Scaled by rpm/IdleRpm at runtime where noted.
        private const int KeepalivePairBaseTicks = 12;
        private const int RpmTrackBaseTicks = 80;
        private const int LowRatePairBaseTicks = 260;
        private const double IdleRpm = 800.0;
        // Engine-vib frequency slider cap (matches the UI slider's Maximum).
        // Older saved profiles may still carry larger values; clamp at use time.
        private const double MaxFreqHz = 200.0;
        // Engine-pulse-pair emission rate (0x0B). Ground-truth capture shows
        // PitHouse holds this CONSTANT at ~48 Hz regardless of RPM or intensity
        // (median inter-pair 20.8 ms; flat across rpm-fraction 0.2..1.0 and
        // across intensity 100/60/40 %). The prior "1.7→34 Hz, intensity-
        // attenuated" model was wrong — intensity rides the 0x0A 0x05 amplitude
        // field, not the pulse rate. amp16 stays constant 0x2328.
        private const double PulsePairRateHz = 48.0;
        // Amplitude fade on stop: the 0x0A 0x05 intensity field is the device-
        // side amplitude, so ramp IT (not the pulse rate) down to 0 on game
        // pause / engine-off / slider-to-zero to avoid an abrupt cut. ~500 ms
        // from 100 % to silent. Rise is instant for responsiveness.
        private const double IntensityFadePerSec = 200.0;
        // Below this effective intensity, treat the stream as silent (field 0,
        // pulses stopped).
        private const double IntensitySilentThreshold = 0.5;

        // Used when the active profile carries no Ab9 block — keeps engine
        // vibration following the profile (defaults to silent) rather than
        // freezing on the previous profile's settings.
        private static readonly Ab9Settings DefaultAb9 = new Ab9Settings();

        private readonly MozaAb9DeviceManager _ab9;
        private readonly DeviceDetectionState _detectionState;
        private readonly Func<Ab9Settings?> _ab9Lookup;
        private readonly Func<bool> _isShuttingDown;

        private Thread? _thread;
        private volatile bool _stop;
        private volatile bool _active;
        private long _latestRpmBits;
        private long _latestMaxRpmBits;
        private volatile bool _latestGameRunning;
        private int _tickCount;
        private ushort _pulsePhase;
        private short _lowRatePhase;
        // Slew-limited effective intensity (0..100). Rise is instant; fall
        // decays at IntensityFadePerSec so stopping fades the amplitude rather
        // than cutting it. Drives the 0x0A 0x05 intensity field.
        private double _smoothedIntensity;
        // Fractional accumulator for the constant-rate engine-pulse pair.
        private double _pulseAccumulator;
        // Last computed period during an active stream — held during a fade
        // so the oscillator doesn't lurch when RPM drops to 0 mid-fade.
        private uint _lastActivePeriod = 0x100000;

        public Ab9EngineVibrationWorker(
            MozaAb9DeviceManager ab9Manager,
            DeviceDetectionState detectionState,
            Func<Ab9Settings?> ab9Lookup,
            Func<bool> isShuttingDown)
        {
            _ab9 = ab9Manager;
            _detectionState = detectionState;
            _ab9Lookup = ab9Lookup;
            _isShuttingDown = isShuttingDown;
        }

        public void Start()
        {
            _stop = false;
            _active = false;
            _thread = new Thread(Loop)
            {
                Name = "MozaAb9EngineVib",
                IsBackground = true,
            };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { _thread?.Join(1000); } catch { }
            _thread = null;
        }

        public void Dispose() => Stop();

        /// <summary>Publish latest game state from the SimHub data-update thread.</summary>
        public void PostFrame(double rpm, double maxRpm, bool gameRunning)
        {
            Interlocked.Exchange(ref _latestRpmBits, BitConverter.DoubleToInt64Bits(rpm));
            Interlocked.Exchange(ref _latestMaxRpmBits, BitConverter.DoubleToInt64Bits(maxRpm));
            _latestGameRunning = gameRunning;
        }

        private void Loop()
        {
            long stopwatchFreq = Stopwatch.Frequency;
            long periodTicks = stopwatchFreq * TickPeriodMs / 1000;
            long next = Stopwatch.GetTimestamp() + periodTicks;
            while (!_stop)
            {
                try { Tick(); }
                catch (Exception ex) { MozaLog.Debug($"[Moza/AB9] engine-vib tick: {ex.Message}"); }

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

        private void Tick()
        {
            if (_isShuttingDown()) return;
            if (_ab9 == null || !_ab9.IsConnected || !_detectionState.Ab9Detected) return;

            // A null lookup means the active profile has no Ab9 block; use
            // factory defaults (intensity 0 → silent) so engine vibration
            // follows per-game profile switches instead of freezing on the
            // last profile's values.
            var ab9 = _ab9Lookup() ?? DefaultAb9;

            int intensity = ab9.EngineVibrationIntensity;
            if (intensity < 0) intensity = 0;
            if (intensity > 100) intensity = 100;
            double freqHz = ab9.EngineVibrationFrequency;
            // UI caps freq at 200 Hz; clamp here so older saved profiles with
            // larger values still respect the cap.
            if (freqHz > MaxFreqHz) freqHz = MaxFreqHz;
            double rpm = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _latestRpmBits));
            double maxRpm = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _latestMaxRpmBits));
            bool gameOn = _latestGameRunning;

            bool rawActive = gameOn && intensity > 0 && rpm > 100.0 && freqHz > 0.0;

            // Effective intensity (0..100), slew-limited on the down direction.
            // The 0x0A 0x05 intensity field is the device-side amplitude, so on
            // a stop (game pause, RPM → 0, slider → 0, engine off mid-drive) we
            // ramp the amplitude down to 0 rather than cutting it. Rise is
            // instant so revving / pushing the slider up feels responsive.
            double targetIntensity = rawActive ? intensity : 0.0;
            double fadePerTick = IntensityFadePerSec * TickPeriodMs / 1000.0;
            if (targetIntensity < _smoothedIntensity)
                _smoothedIntensity = Math.Max(targetIntensity, _smoothedIntensity - fadePerTick);
            else
                _smoothedIntensity = targetIntensity;

            // Effective stream state: raw-active OR still fading down.
            bool fading = !rawActive && _smoothedIntensity > IntensitySilentThreshold;
            bool effectiveStreaming = rawActive || fading;
            int slotIntensity = (int)Math.Round(_smoothedIntensity);

            // 0x0A 0x05 engine-vibration refresh — every tick. The intensity
            // field carries the (faded) slider value linearly; period sets the
            // pitch.
            uint period;
            if (rawActive)
            {
                // audible = freqSlider × (rpm/maxRpm); slider is the redline
                // frequency. period = FreqTickHz / audible. Clamp the fraction
                // to (0,1] so over-rev can't exceed the redline pitch and a
                // missing MaxRpm falls back to an 8000-rpm redline.
                double redline = maxRpm > 100.0 ? maxRpm : DefaultRedlineRpm;
                double fraction = rpm / redline;
                if (fraction > 1.0) fraction = 1.0;
                double p = FreqTickHz / (freqHz * fraction);
                if (p < MozaAb9DeviceManager.MinPeriodTicks) p = MozaAb9DeviceManager.MinPeriodTicks;
                if (p > MozaAb9DeviceManager.MaxPeriodTicks) p = MozaAb9DeviceManager.MaxPeriodTicks;
                period = (uint)p;
                _lastActivePeriod = period;
            }
            else if (fading)
            {
                // Hold the last computed period during the fade so the
                // oscillator frequency doesn't slide bizarrely as RPM goes
                // to 0 (which would push period → MaxPeriodTicks and produce
                // a downward pitch slur over the fade).
                period = _lastActivePeriod;
            }
            else
            {
                // Stable midpoint when fully silent so the frame payload
                // stays well-formed.
                period = 0x100000;
            }
            _ab9.SendEngineVibrationStream(slotIntensity, period);

            if (effectiveStreaming != _active)
            {
                _active = effectiveStreaming;
                MozaLog.Debug($"[Moza/AB9] engine-vib {(effectiveStreaming ? "active" : "silent")} "
                              + $"(rpm={rpm:F0}/{maxRpm:F0} freq={freqHz:F1}Hz period={period} "
                              + $"int={intensity} eff={_smoothedIntensity:F0})");
            }

            // Sub-streams gated on `effectiveStreaming`. During a fade they keep
            // running so the device doesn't see the FFB session go dark.
            if (!effectiveStreaming)
            {
                _tickCount++;
                _pulseAccumulator = 0.0;
                return;
            }

            int tick = ++_tickCount;
            double rpmFactor = Math.Max(1.0, rpm / IdleRpm);

            // 0x0D 0x02/03 keepalive pair — ~9 Hz regardless of RPM.
            if (tick % KeepalivePairBaseTicks == 0)
                _ab9.SendKeepalivePair();

            // 0x0B 0x02/03 engine-pulse pair — CONSTANT ~48 Hz, independent of
            // RPM and intensity (ground-truth capture 2026-05-31). amp16 is held
            // at constant 0x2328 by passing 100 to SendEnginePulsePair —
            // intensity rides the 0x0A 0x05 amplitude field, not the pulse rate.
            // Fractional accumulator keeps the average rate at PulsePairRateHz
            // despite the 11 ms tick grid.
            _pulseAccumulator += PulsePairRateHz * TickPeriodMs / 1000.0;
            while (_pulseAccumulator >= 1.0)
            {
                _pulseAccumulator -= 1.0;
                ushort step = (ushort)Math.Min(0xFFFF, (int)(32 + 78 * Math.Min(1.0, rpmFactor / 10.0)));
                unchecked { _pulsePhase += step; }
                _ab9.SendEnginePulsePair(_pulsePhase, intensity0to100: 100);
            }

            // 0x0D 0x05 RPM-tracking trigger.
            int rpmTrackInterval = Math.Max(2, (int)(RpmTrackBaseTicks / rpmFactor));
            if (tick % rpmTrackInterval == 0)
                _ab9.SendTrigger(MozaAb9DeviceManager.Ab9Trigger.RpmTrack);

            // 0x08 0x04/06 low-rate signed pair.
            if (tick % LowRatePairBaseTicks == 0)
            {
                unchecked { _lowRatePhase += 100; }
                if (_lowRatePhase > 32000) _lowRatePhase = -32000;
                _ab9.SendLowRatePair(_lowRatePhase);
            }

            // 0x0D 0x01 (Sparse), 0x0D 0x04 (Engage), 0x0D 0x06 (Disengage) are
            // event-driven, fired from MozaPlugin.CheckAb9GearshiftEvent on
            // each SimHub gear-string transition — NOT emitted from this
            // worker. See usb-capture/AB9/all_gears.pcapng / 1-N.pcapng and
            // docs/protocol/devices/ab9-shifter.md.
        }
    }
}
