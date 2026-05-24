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

            bool streamActive = gameOn && intensity > 0 && rpm > 100.0 && freqHz > 0.0;

            // Scale the slider intensity by current/max RPM so the buzz fades
            // in toward the slider value as the engine climbs to redline.
            // Games that don't report MaxRpm (or report 0) bypass scaling so
            // the slider behaves like a flat amplitude — better than silently
            // collapsing to zero when the source data is missing. <100 RPM
            // floor on maxRpm rejects obviously-bogus zero / idle-noise values.
            double rpmFraction = 1.0;
            if (maxRpm > 100.0)
            {
                rpmFraction = rpm / maxRpm;
                if (rpmFraction < 0.0) rpmFraction = 0.0;
                if (rpmFraction > 1.0) rpmFraction = 1.0;
            }
            int scaledIntensity = (int)Math.Round(intensity * rpmFraction);
            if (scaledIntensity < 0) scaledIntensity = 0;
            if (scaledIntensity > 100) scaledIntensity = 100;

            // Slot ID is binary: active slot (0x1996) while streaming, silent
            // slot (0x0000) otherwise. Earlier builds Bresenham-modulated this
            // at the 91 Hz tick rate to scale intensity, but that produced an
            // audible sub-bass rumble at low intensity (the on/off pattern beat
            // at sub-frame rates). PitHouse modulates intensity by adding or
            // removing parallel slot streams, never by toggling one slot in
            // time — see docs/protocol/devices/ab9-shifter.md "Slider effects
            // on the stream". Intensity scaling now happens entirely through
            // the engine-pulse-pair amp16 (already linear, already PitHouse-
            // faithful).
            bool slotActive = streamActive;

            // 0x0A 0x05 engine-vibration refresh — every tick.
            uint period;
            if (streamActive)
            {
                double p = K / (rpm * freqHz);
                if (p < MozaAb9DeviceManager.MinPeriodTicks) p = MozaAb9DeviceManager.MinPeriodTicks;
                if (p > MozaAb9DeviceManager.MaxPeriodTicks) p = MozaAb9DeviceManager.MaxPeriodTicks;
                period = (uint)p;
            }
            else
            {
                // Stable midpoint when silent so the frame payload stays well-formed.
                period = 0x100000;
            }
            _ab9.SendEngineVibrationStream(slotActive, period);

            if (streamActive != _active)
            {
                _active = streamActive;
                MozaLog.Debug($"[Moza/AB9] engine-vib {(streamActive ? "active" : "silent")} "
                              + $"(rpm={rpm:F0}/{maxRpm:F0} freq={freqHz:F1}Hz period={period} "
                              + $"int={intensity}→{scaledIntensity})");
            }

            // Sub-streams gated on `streamActive` — silent keepalive only otherwise.
            if (!streamActive)
            {
                _tickCount++;
                return;
            }

            int tick = ++_tickCount;
            double rpmFactor = Math.Max(1.0, rpm / IdleRpm);

            // 0x0D 0x02/03 keepalive pair — ~9 Hz regardless of RPM.
            if (tick % KeepalivePairBaseTicks == 0)
                _ab9.SendKeepalivePair();

            // 0x0B 0x02/03 engine-pulse pair — RPM-scaled rate. The pair's
            // amp16 (offset 19-20 of the wire frame) scales linearly with
            // `scaledIntensity` (slider × rpm/maxRpm), so the buzz fades up
            // toward the slider setting as the engine climbs to redline
            // instead of sitting at full amplitude from idle.
            int pulseInterval = Math.Max(2, (int)(EnginePulsePairBaseTicks / rpmFactor));
            if (tick % pulseInterval == 0)
            {
                ushort step = (ushort)Math.Min(0xFFFF, (int)(32 + 78 * Math.Min(1.0, rpmFactor / 10.0)));
                unchecked { _pulsePhase += step; }
                _ab9.SendEnginePulsePair(_pulsePhase, scaledIntensity);
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
