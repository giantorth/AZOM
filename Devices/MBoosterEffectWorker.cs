using System;
using System.Diagnostics;
using System.Threading;
using MozaPlugin.Protocol;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// 50 Hz motor-frame producer + 500 ms keepalive for one mBooster device.
    /// Owns per-effect synthesis state (phase, elapsed, last amplitude) and
    /// the telemetry → freq/intensity mapping per protocol note § 4.
    ///
    /// One worker per <see cref="MBoosterDeviceController"/>. The shared
    /// <c>StreamKind.MBoosterEffect</c> lane on the device's connection
    /// coalesces frames if the writer lags — at 50 Hz with one frame per
    /// tick this is harmless (older tick gets dropped, newer tick lands).
    /// </summary>
    internal sealed class MBoosterEffectWorker : IDisposable
    {
        // Motor loop @ 50 Hz (20 ms period — protocol note § 4).
        private const int TickPeriodMs = 20;
        private const double TickPeriodSec = 0.020;
        // Keepalive @ ~2 Hz — 25 ticks × 20 ms = 500 ms (protocol note § 3 / § 4).
        private const int KeepaliveTickInterval = 25;
        // Engine continuous-effect safety clamp (doc § 4 suggested default = 0.01..0.1).
        private const double EngineIntensityCap = 0.10;

        private readonly MBoosterDeviceController _device;
        private readonly Func<MBoosterDeviceSettings?> _settingsLookup;
        private readonly Func<bool> _isShuttingDown;

        private Thread? _thread;
        private volatile bool _stop;

        // Latest telemetry snapshot — published from MozaPlugin.DataUpdate via the
        // registry. Read via Volatile.Read; struct itself is small (≤ ~64 B).
        // We can't make `MBoosterTelemetrySnapshot` volatile so use a holder.
        private MBoosterTelemetrySnapshot _latest = MBoosterTelemetrySnapshot.Empty;
        private readonly object _telemetryLock = new object();

        // Per-effect synthesis state.
        private EffectState _abs;
        private EffectState _lockup;
        private EffectState _threshold;
        private EffectState _engine;
        private bool _thresholdLatched; // hysteresis flag for the Threshold effect (doc § 4)

        // Test pulses (UI test buttons) — fire one effect for ~1 s outside the
        // game-driven mapping. Each effect tracks a `_testUntilTicks` deadline.
        private long _testAbsUntil;
        private long _testLockupUntil;
        private long _testThresholdUntil;
        private long _testEngineUntil;
        private double _testIntensityAbs, _testIntensityLockup, _testIntensityThreshold, _testIntensityEngine;
        private double _testHzAbs, _testHzLockup, _testHzThreshold, _testHzEngine;

        // Keepalive tick counter (mod KeepaliveTickInterval).
        private int _keepaliveCounter;

        private struct EffectState
        {
            public bool Active;
            public double PhaseRad;     // wraps at 2π
            public double ElapsedSec;
            public double IntensityRequest;  // 0..1
            public double FreqHz;            // user/telemetry-mapped frequency
        }

        public MBoosterEffectWorker(
            MBoosterDeviceController device,
            Func<MBoosterDeviceSettings?> settingsLookup,
            Func<bool> isShuttingDown)
        {
            _device = device;
            _settingsLookup = settingsLookup;
            _isShuttingDown = isShuttingDown;
        }

        public void Start()
        {
            _stop = false;
            if (_thread != null && _thread.IsAlive) return;
            _thread = new Thread(Loop)
            {
                Name = "MozaMBoosterEffect-" + MBoosterDeviceController.ShortIdentity(_device.Identity),
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

        public void PostFrame(in MBoosterTelemetrySnapshot snap)
        {
            lock (_telemetryLock) _latest = snap;
        }

        public void FireTestPulse(MBoosterEffectId effect, double intensity01, double freqHz)
        {
            // Default pulse length 1 s.
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            switch (effect)
            {
                case MBoosterEffectId.Abs:
                    _testIntensityAbs = intensity01; _testHzAbs = freqHz; _testAbsUntil = deadline; break;
                case MBoosterEffectId.Lockup:
                    _testIntensityLockup = intensity01; _testHzLockup = freqHz; _testLockupUntil = deadline; break;
                case MBoosterEffectId.Threshold:
                    _testIntensityThreshold = intensity01; _testHzThreshold = freqHz; _testThresholdUntil = deadline; break;
                case MBoosterEffectId.Engine:
                    _testIntensityEngine = intensity01; _testHzEngine = freqHz; _testEngineUntil = deadline; break;
            }
        }

        private void Loop()
        {
            long stopwatchFreq = Stopwatch.Frequency;
            long periodTicks = stopwatchFreq * TickPeriodMs / 1000;
            long next = Stopwatch.GetTimestamp() + periodTicks;
            while (!_stop)
            {
                try { Tick(); }
                catch (Exception ex) { MozaLog.Debug($"[Moza/mBooster] worker tick: {ex.Message}"); }

                long now = Stopwatch.GetTimestamp();
                long delta = next - now;
                if (delta <= 0)
                {
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
            if (!_device.IsConnected) return;

            var settings = _settingsLookup();

            MBoosterTelemetrySnapshot snap;
            lock (_telemetryLock) snap = _latest;

            // --- Compute per-effect requests from telemetry per doc § 4 -----

            UpdateEngineRequest(settings, snap, ref _engine);
            UpdateAbsRequest(settings, snap, ref _abs);
            UpdateLockupRequest(settings, snap, ref _lockup);
            UpdateThresholdRequest(settings, snap, ref _threshold);

            // --- Apply per-effect activation edges + emit motor frame ------

            ProcessEffect(MBoosterEffectId.Engine,    ref _engine);
            ProcessEffect(MBoosterEffectId.Abs,       ref _abs);
            ProcessEffect(MBoosterEffectId.Lockup,    ref _lockup);
            ProcessEffect(MBoosterEffectId.Threshold, ref _threshold);

            // --- 500 ms keepalive (separate from motor frames) -------------
            _keepaliveCounter++;
            if (_keepaliveCounter >= KeepaliveTickInterval)
            {
                _keepaliveCounter = 0;
                _device.SendOneShot(MozaMBoosterProtocol.BuildKeepalive());
            }
        }

        // ===== Telemetry → effect parameters (doc § 4) ====================

        private void UpdateEngineRequest(MBoosterDeviceSettings? settings, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            // Test pulse overrides telemetry-driven engine.
            if (Stopwatch.GetTimestamp() < _testEngineUntil)
            {
                st.IntensityRequest = ClampEngine(_testIntensityEngine);
                st.FreqHz = _testHzEngine;
                return;
            }

            if (settings?.Engine == null || !settings.Engine.Enabled)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }

            double rpm = snap.Rpm;
            double idle = Math.Max(snap.IdleRpm, 500);
            if (!snap.GameRunning || rpm <= 0.8 * idle)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }

            // freq = clamp(rpm / 20000 * 200, 10, 200)  per doc § 4
            double hz = rpm / 20000.0 * 200.0;
            if (hz < 10) hz = 10;
            if (hz > 200) hz = 200;
            st.FreqHz = hz;
            // Engine continuous-effect safety: doc default is engineScale = 0.01,
            // clamped to [0, 0.1]. We map user 0..100 → 0..0.1.
            double engineScale = (settings.Engine.IntensityPct / 100.0) * 0.10;
            st.IntensityRequest = ClampEngine(engineScale);
        }

        private void UpdateAbsRequest(MBoosterDeviceSettings? settings, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            if (Stopwatch.GetTimestamp() < _testAbsUntil)
            {
                st.IntensityRequest = Clamp01(_testIntensityAbs);
                st.FreqHz = _testHzAbs;
                return;
            }

            if (settings?.Abs == null || !settings.Abs.Enabled)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }

            // The plugin's snapshot exposes AbsActive as bool. Doc's pseudocode
            // expects a 0..1 float so the freq can scale with engagement depth;
            // we map the bool to {0, 1} which collapses the freq range to a
            // single value (30 Hz). Acceptable simplification — most games
            // don't expose ABS engagement depth.
            double abs01 = snap.AbsActive ? 1.0 : 0.0;
            if (abs01 <= 0.1)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }
            st.FreqHz = 18 + abs01 * 12;       // 18..30 Hz
            double absScale = settings.Abs.IntensityPct / 100.0;
            st.IntensityRequest = Clamp01(abs01 * absScale);
        }

        private void UpdateLockupRequest(MBoosterDeviceSettings? settings, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            if (Stopwatch.GetTimestamp() < _testLockupUntil)
            {
                st.IntensityRequest = Clamp01(_testIntensityLockup);
                st.FreqHz = _testHzLockup;
                return;
            }

            if (settings?.Lockup == null || !settings.Lockup.Enabled)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }

            double brake = Clamp01(snap.Brake);
            double vehicleSpeed = Math.Abs(snap.VehicleSpeedMs);
            double avgWheelSpeed = Math.Abs(snap.AvgWheelSpeedMs);

            bool isLocking = brake > 0.8
                          && vehicleSpeed > 5
                          && avgWheelSpeed < vehicleSpeed * 0.3;

            // Fallback path: many games don't expose per-wheel speeds. If
            // avgWheelSpeedMs is zero AND vehicle is moving heavily braked,
            // treat as a probable lockup so the effect still fires meaningfully.
            if (!isLocking && avgWheelSpeed <= 0 && brake > 0.9 && vehicleSpeed > 5)
                isLocking = true;

            if (!isLocking)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }
            st.FreqHz = 40 + brake * 30;       // 40..70 Hz
            double lockupScale = settings.Lockup.IntensityPct / 100.0;
            st.IntensityRequest = Clamp01(brake * lockupScale);
        }

        private void UpdateThresholdRequest(MBoosterDeviceSettings? settings, in MBoosterTelemetrySnapshot snap, ref EffectState st)
        {
            if (Stopwatch.GetTimestamp() < _testThresholdUntil)
            {
                st.IntensityRequest = Clamp01(_testIntensityThreshold);
                st.FreqHz = _testHzThreshold;
                _thresholdLatched = true;
                return;
            }

            if (settings?.Threshold == null || !settings.Threshold.Enabled)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                _thresholdLatched = false;
                return;
            }

            double brake = Clamp01(snap.Brake);
            // Rising-edge trigger at 0.6, release below 0.3 — doc § 4 hysteresis.
            if (!_thresholdLatched && brake > 0.6)
                _thresholdLatched = true;
            else if (_thresholdLatched && brake < 0.3)
                _thresholdLatched = false;

            if (!_thresholdLatched)
            {
                st.IntensityRequest = 0;
                st.FreqHz = 0;
                return;
            }
            st.FreqHz = 60 + brake * 30;       // 60..90 Hz
            double thresholdScale = settings.Threshold.IntensityPct / 100.0;
            st.IntensityRequest = Clamp01(brake * thresholdScale);
        }

        // ===== Edge handling + frame emission =============================

        private void ProcessEffect(MBoosterEffectId id, ref EffectState st)
        {
            bool wantActive = st.IntensityRequest > 0 && st.FreqHz > 0;

            if (!wantActive && st.Active)
            {
                // Deactivation edge: emit one disable frame and go silent.
                _device.SendOneShot(MozaMBoosterProtocol.BuildDisableFrame(id));
                st.Active = false;
                st.PhaseRad = 0;
                st.ElapsedSec = 0;
                return;
            }
            if (!wantActive)
            {
                return;   // already silent — doc § 4: don't send frames while inactive
            }

            // Activation or already-active path.
            if (!st.Active)
            {
                st.Active = true;
                st.PhaseRad = 0;
                st.ElapsedSec = 0;
            }

            st.ElapsedSec += TickPeriodSec;
            // phase += 2π * freq * dt; wrap at 2π for numerical stability.
            st.PhaseRad += 2.0 * Math.PI * st.FreqHz * TickPeriodSec;
            if (st.PhaseRad >= 2.0 * Math.PI)
                st.PhaseRad -= 2.0 * Math.PI * Math.Floor(st.PhaseRad / (2.0 * Math.PI));

            double amp01 = id switch
            {
                MBoosterEffectId.Abs       => MBoosterEffectSynthesizer.SynthesizeAbs(st.IntensityRequest, st.PhaseRad),
                MBoosterEffectId.Lockup    => MBoosterEffectSynthesizer.SynthesizeLockup(st.IntensityRequest, st.ElapsedSec),
                MBoosterEffectId.Threshold => MBoosterEffectSynthesizer.SynthesizeThreshold(st.IntensityRequest, st.ElapsedSec),
                MBoosterEffectId.Engine    => MBoosterEffectSynthesizer.SynthesizeEngine(st.IntensityRequest, st.PhaseRad),
                _                          => 0.0,
            };

            byte param1 = MozaMBoosterProtocol.ComputeParam1(
                MozaMBoosterProtocol.ParamKFor(id), st.FreqHz);
            ushort freqU16 = MozaMBoosterProtocol.EncodeFreq(st.FreqHz);
            ushort ampU16 = MozaMBoosterProtocol.EncodeAmp(amp01);

            var frame = MozaMBoosterProtocol.BuildMotorFrame(id, enable: true, param1, freqU16, ampU16);
            _device.SendMotorStream(frame);
        }

        // ===== Helpers ====================================================

        private static double Clamp01(double v)
        {
            if (double.IsNaN(v)) return 0;
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        private static double ClampEngine(double v)
        {
            if (double.IsNaN(v)) return 0;
            if (v < 0) return 0;
            if (v > EngineIntensityCap) return EngineIntensityCap;
            return v;
        }
    }
}
