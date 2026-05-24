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
        // Period constant. See docs/protocol/devices/ab9-shifter.md.
        // Calibrated against real-hardware audible measurement: PitHouse driving
        // a Cayman GT4 (~7700 RPM redline) at slider=100 Hz produces ~103 Hz
        // audible buzz; the prior K=3.95e11 (derived from a capture session with
        // uncertain RPM assumptions) produced ~145 Hz at the same operating
        // point. Ratio 145/103 = 1.41 → K = 3.95e11 × 1.41 ≈ 5.56e11.
        private const double K = 5.56e11;
        private const int TickPeriodMs = 11;
        // Sub-stream tick budgets at idle (RPM ~800). Scaled by rpm/IdleRpm at runtime.
        private const int KeepalivePairBaseTicks = 12;
        private const int EnginePulsePairBaseTicks = 62;
        private const int RpmTrackBaseTicks = 80;
        private const int LowRatePairBaseTicks = 260;
        private const double IdleRpm = 800.0;
        // Engine-vib frequency slider cap (matches the UI slider's Maximum).
        // Older saved profiles may still carry larger values; clamp at use time.
        private const double MaxFreqHz = 200.0;
        // Pulse-pair emission rate (0x0B 0x02/03 — see ab9-shifter.md).
        // PitHouse fires at ~1.7 Hz at idle and ~34 Hz at redline; intensity
        // slider attenuates this multiplicatively.
        private const double PulseRateIdleHz = 1.7;
        private const double PulseRateRedlineHz = 34.0;
        // Safety roll-off: how fast pulse-pair rate is allowed to drop. 500 ms
        // from redline (34 Hz) to silent is the design point — short enough to
        // feel responsive on a real gear-shift cut, long enough that game
        // pause / engine-off / slider-to-zero produce an audible fade rather
        // than a click.
        private const double PulseRateRollOffHzPerSec = (PulseRateRedlineHz / 0.5);
        // Below this rate, treat the stream as effectively silent — flip slot
        // to 0x0000 and stop emitting pulse pairs. Avoids a long tail of
        // sub-1-Hz pulses at the bottom of the fade.
        private const double PulseRateSilentThresholdHz = 0.5;

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
        // Slew-rate limited pulse-pair rate. Cuts the audible-click that
        // would otherwise happen on abrupt drops (slider → 0, game pause,
        // engine off mid-drive). Rise is instant for responsiveness; fall
        // decays at PulseRateRollOffHzPerSec.
        private double _smoothedRateHz;
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

            var ab9 = _ab9Lookup();
            if (ab9 == null) return;

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

            // Engine-RPM relative to redline (0..1). Drives the engine-pulse
            // pair RATE per the PitHouse envelope: ~1.7 Hz at idle, ~34 Hz
            // at redline. Games that don't report MaxRpm fall back to an
            // 8000 RPM redline (HardwareApplier convention) so the buzz
            // still has dynamic range.
            double rpmRedlineFraction;
            if (maxRpm > 100.0)
                rpmRedlineFraction = Math.Min(1.0, Math.Max(0.0, rpm / maxRpm));
            else
                rpmRedlineFraction = Math.Min(1.0, Math.Max(0.0, rpm / 8000.0));

            // Target pulse-pair emission rate (Hz). The intensity slider
            // multiplicatively attenuates a purely-RPM-driven baseline rate,
            // so slider=0 → silent, slider=50 → half PitHouse rate at every
            // RPM, slider=100 → full PitHouse rate. amp16 stays at the
            // constant 0x2328 PitHouse uses (verified across 17,603 capture
            // frames) — see docs/protocol/devices/ab9-shifter.md.
            double rpmDrivenHz = PulseRateIdleHz
                                 + (PulseRateRedlineHz - PulseRateIdleHz) * rpmRedlineFraction;
            double targetRateHz = rawActive ? (intensity / 100.0) * rpmDrivenHz : 0.0;

            // Slew-rate limit on the down direction only. Rise stays instant
            // so a user revving the engine or pushing the slider up feels
            // responsive. Drops (game pause, RPM → 0, slider → 0, engine off
            // mid-drive) decay over ~500 ms to avoid an audible click as the
            // device firmware sees pulse-pair frames suddenly stop.
            double rollOffPerTick = PulseRateRollOffHzPerSec * TickPeriodMs / 1000.0;
            if (targetRateHz < _smoothedRateHz)
                _smoothedRateHz = Math.Max(targetRateHz, _smoothedRateHz - rollOffPerTick);
            else
                _smoothedRateHz = targetRateHz;

            // Effective stream state: raw-active OR still fading down.
            bool fading = !rawActive && _smoothedRateHz > PulseRateSilentThresholdHz;
            bool effectiveStreaming = rawActive || fading;

            // Slot ID is binary: active slot (0x1996) while streaming or
            // fading, silent slot (0x0000) once the fade completes. PitHouse's
            // intensity slider toggles between these two states at the wire
            // level — the perceived intensity envelope comes from
            // engine-pulse-pair density, not slot-side amplitude.
            bool slotActive = effectiveStreaming;

            // 0x0A 0x05 engine-vibration refresh — every tick.
            uint period;
            if (rawActive)
            {
                double p = K / (rpm * freqHz);
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
            _ab9.SendEngineVibrationStream(slotActive, period);

            if (effectiveStreaming != _active)
            {
                _active = effectiveStreaming;
                MozaLog.Debug($"[Moza/AB9] engine-vib {(effectiveStreaming ? "active" : "silent")} "
                              + $"(rpm={rpm:F0}/{maxRpm:F0} freq={freqHz:F1}Hz period={period} "
                              + $"int={intensity} rpmRel={rpmRedlineFraction:F2} "
                              + $"rate={_smoothedRateHz:F1}/{targetRateHz:F1}Hz)");
            }

            // Sub-streams gated on `effectiveStreaming`. During a fade, all
            // four sub-streams (vib-stream, keepalive, RPM-track, low-rate)
            // continue running so the device doesn't see the FFB session go
            // dark — only the engine-pulse pair throttles down with the
            // slewed rate.
            if (!effectiveStreaming)
            {
                _tickCount++;
                return;
            }

            int tick = ++_tickCount;
            double rpmFactor = Math.Max(1.0, rpm / IdleRpm);

            // 0x0D 0x02/03 keepalive pair — ~9 Hz regardless of RPM.
            if (tick % KeepalivePairBaseTicks == 0)
                _ab9.SendKeepalivePair();

            // 0x0B 0x02/03 engine-pulse pair — emission RATE modulates audible
            // intensity. Uses the slewed `_smoothedRateHz` so abrupt
            // transitions fade rather than click. amp16 is held at constant
            // 0x2328 (PitHouse-faithful) by passing `100` to
            // SendEnginePulsePair — see ab9-shifter.md for why.
            if (_smoothedRateHz > PulseRateSilentThresholdHz)
            {
                int pulseInterval = Math.Max(2, (int)Math.Round(1000.0 / TickPeriodMs / _smoothedRateHz));
                if (tick % pulseInterval == 0)
                {
                    ushort step = (ushort)Math.Min(0xFFFF, (int)(32 + 78 * Math.Min(1.0, rpmFactor / 10.0)));
                    unchecked { _pulsePhase += step; }
                    _ab9.SendEnginePulsePair(_pulsePhase, intensity0to100: 100);
                }
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
