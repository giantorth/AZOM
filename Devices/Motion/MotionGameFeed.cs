using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using GameReaderCommon;
using MozaPlugin.Protocol;
using SimHub.Plugins;

namespace MozaPlugin.Devices.Motion
{
    /// <summary>
    /// Game telemetry → <c>0x22/00</c> frame, the way Motion Manager fills it: raw physics,
    /// unscaled — the controller's own Motion Effects sliders set the strength. Field map and
    /// signs follow the handover bridge (v30), which was matched against Motion Manager's
    /// stream: docs/protocol/motion/game-stream-0x22.md § Plugin mapping.
    ///
    /// Fed from DataUpdate on SimHub's data thread; read by the output (latest wins).
    /// Empty while no game is running or the game is paused.
    /// </summary>
    public sealed class MotionGameFeed
    {
        private const double DegToRad = Math.PI / 180.0;
        // Pose heave → f6: SimHub's default heave range (17 m/s² → 80 mm) run backwards. Fixed on
        // purpose; the profile's Heave row is where strength is tuned. +f6 raises the platform.
        private const float HeaveAccelPerMm = 17f / 80f;
        // Bound on pose f6 (heave + crest push); travel levels off near 16 m/s².
        private const float PoseHeaveAccelLimit = 30f;
        // AI mode (PoseFrameAsPosition): the controller's heave tops out near 25 mm, so more is never asked.
        private const double AiPoseHeaveLimitMm = 25.0;
        // Crest strength 100 in AI mode: the full crest push (30) is a 20 mm drop on top of the profile's heave.
        private const double AiPoseCrestDropMm = 20.0;
        private const double CrestPushMax = 30.0;
        // Crest signal (docs/protocol/motion/game-stream-0x22.md § Plugin mapping): low-passed
        // vertical accel, one-sided past a dead band, so crests pass and short bumps don't.
        private const double HeaveSlowSeconds = 0.2;
        private const double CrestDeadbandAccel = 2.0;
        private const double CrestGainFull = 15.0;
        private const double CrestMaxAccel = 30.0;
        // Impacts: input clipped, and |surge| or |sway| above 80 (past any car's braking or
        // cornering) holds the signal at 0 for 2 s.
        private const double CrestInputLimit = 12.0;
        private const double CrestImpactAccel = 80.0;
        private const int CrestImpactHoldMs = 2000;
        // Fast attack, slow release so a wobbling crest is one push; release firms up as the car lands.
        private const double CrestAttackSeconds = 0.05;
        private const double CrestReleaseSoftSeconds = 0.8;
        private const double CrestReleaseFirmSeconds = 0.3;
        private const double CrestLandingAccel = 3.0;
        private const double CrestImpactReleaseSeconds = 0.15;
        private const int GearPulseMs = 120;
        private const double EngineOffRpm = 100.0;

        // f8..f11 (LF, LR, RR, RF) for games without per-corner suspension:
        // base + long·a + lat·b + vert·c + speed²·d, a least-squares fit to Motion Manager's
        // Spa stream (bridge v30 SHOCK_FIT, R² 0.78–0.90). Accelerations in m/s², speed in m/s.
        private static readonly double[,] ShockFit =
        {
            { 34.40,  0.427,  0.555, -1.089, 0.00048 },
            { 38.72, -0.203,  0.346, -0.174, 0.01362 },
            { 39.25, -0.225, -1.119, -0.268, 0.01285 },
            { 33.66,  0.450, -0.848, -1.158, 0.00276 },
        };

        // iRacing shock deflection (m), in f8..f11 order — what Motion Manager sends × 1000.
        private static readonly string[] IRacingShocks =
        {
            "DataCorePlugin.GameRawData.Telemetry.LFshockDefl",
            "DataCorePlugin.GameRawData.Telemetry.LRshockDefl",
            "DataCorePlugin.GameRawData.Telemetry.RRshockDefl",
            "DataCorePlugin.GameRawData.Telemetry.RFshockDefl",
        };

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private MotionTelemetryFrame? _latest;
        private long _stampMs;
        private string? _lastGear;
        private long _shiftUntilMs;
        private double _heaveSlow;
        private long _heaveSlowAtMs = -1;
        private long _crestHoldUntilMs;
        private double _crestPush;
        private volatile float _crestPushOut;

        /// <summary>Crest push at full strength (m/s² of f6, ≥ 0); 0 while no game runs.</summary>
        public float CrestPush => _crestPushOut;

        /// <summary>Latest frame if it is younger than <paramref name="maxAgeMs"/>.</summary>
        public MotionTelemetryFrame? Latest(int maxAgeMs)
        {
            var f = Volatile.Read(ref _latest);
            if (f == null || _clock.ElapsedMilliseconds - Interlocked.Read(ref _stampMs) > maxAgeMs) return null;
            return f;
        }

        /// <summary>Drop the latest frame and the crest filter state.</summary>
        public void Reset()
        {
            Volatile.Write(ref _latest, null);
            _lastGear = null;
            _heaveSlow = 0.0;
            _heaveSlowAtMs = -1;
            _crestHoldUntilMs = 0;
            _crestPush = 0.0;
            _crestPushOut = 0f;
        }

        public void Update(PluginManager pluginManager, GameData data)
        {
            var d = data.NewData;
            if (!data.GameRunning || data.GamePaused || d == null)
            {
                Reset();
                return;
            }

            long now = _clock.ElapsedMilliseconds;
            double surge = Finite(d.AccelerationSurge);   // m/s², braking positive
            // SimHub's sway is opposite to Motion Manager's lateral field (MM capture,
            // iRacing slow turns, bridge v31).
            double sway = -Finite(d.AccelerationSway);
            double heave = Finite(d.AccelerationHeave);
            double speed = d.SpeedKmh / 3.6;

            var f = new MotionTelemetryFrame();
            f.Fields[0] = (float)(d.OrientationPitch * DegToRad);
            f.Fields[1] = (float)(-d.OrientationRoll * DegToRad);
            f.Fields[2] = (float)WrapPi(-d.OrientationYaw * DegToRad);
            f.Fields[3] = (float)(d.OrientationYawVelocity * DegToRad);
            f.Fields[4] = (float)surge;
            f.Fields[5] = (float)sway;
            f.Fields[6] = (float)heave;
            f.Fields[7] = (float)speed;
            double sinceSlow = _heaveSlowAtMs < 0 ? 0.0 : Math.Min(0.25, (now - _heaveSlowAtMs) / 1000.0);
            _heaveSlowAtMs = now;
            if (Math.Abs(surge) > CrestImpactAccel || Math.Abs(sway) > CrestImpactAccel)
                _crestHoldUntilMs = now + CrestImpactHoldMs;
            if (now < _crestHoldUntilMs)
                _heaveSlow = 0.0;
            else
                _heaveSlow += (Math.Max(-CrestInputLimit, Math.Min(CrestInputLimit, heave)) - _heaveSlow)
                    * (1.0 - Math.Exp(-sinceSlow / HeaveSlowSeconds));
            double crestTarget = Math.Min(CrestMaxAccel, Math.Max(0.0, -_heaveSlow - CrestDeadbandAccel) * CrestGainFull);
            double crestRate;
            if (crestTarget > _crestPush)
                crestRate = 1.0 / CrestAttackSeconds;
            else if (now < _crestHoldUntilMs)
                crestRate = 1.0 / CrestImpactReleaseSeconds;
            else
            {
                double landing = Math.Max(0.0, Math.Min(1.0, _heaveSlow / CrestLandingAccel));
                crestRate = 1.0 / CrestReleaseSoftSeconds
                    + (1.0 / CrestReleaseFirmSeconds - 1.0 / CrestReleaseSoftSeconds) * landing;
            }
            _crestPush += (crestTarget - _crestPush) * (1.0 - Math.Exp(-sinceSlow * crestRate));
            _crestPushOut = (float)_crestPush;
            if (!ReadIRacingShocks(pluginManager, f))
            {
                for (int k = 0; k < 4; k++)
                    f.Fields[8 + k] = (float)(ShockFit[k, 0] + ShockFit[k, 1] * surge + ShockFit[k, 2] * sway
                        + ShockFit[k, 3] * heave + ShockFit[k, 4] * speed * speed);
            }
            f.BrakePercent = Percent(d.Brake);
            f.ThrottlePercent = Percent(d.Throttle);
            // Engine off: no tone. Motion Manager keeps its 25 Hz idle buzz here; testers prefer silence.
            f.EngineRpm = d.Rpms > EngineOffRpm ? (float)d.Rpms : -1f;
            f.EngineRpmFraction = d.Rpms > 0 ? (float)Haptics.EngineVibrationMath.RedlineFraction(d.Rpms, d.MaxRpm) : 0f;
            f.Throttle = (float)(d.Throttle / 100.0);

            string gear = d.Gear ?? string.Empty;
            if (_lastGear != null && gear != _lastGear) _shiftUntilMs = now + GearPulseMs;
            _lastGear = gear;
            f.Flags = (byte)(MozaMotionProtocol.FlagInGame | (now < _shiftUntilMs ? MozaMotionProtocol.FlagGearShift : 0));

            Volatile.Write(ref _latest, f);
            Interlocked.Exchange(ref _stampMs, now);
        }

        /// <summary>
        /// A frame that carries a SimHub Motion pose instead of raw physics: pitch/roll in f0/f1,
        /// which the controller applies as platform attitude; heave as vertical acceleration in f6
        /// (the input its washed-out Heave effect follows), minus <paramref name="crestPush"/>, held
        /// within ±<see cref="PoseHeaveAccelLimit"/>; f4/f5 zero so its acceleration effects stay
        /// idle; shocks at rest. Heading, yaw rate, speed, pedals, engine and flags come from
        /// <paramref name="game"/> when a game runs. Pose signs: SimHub's pitch is −GameData pitch
        /// and its roll +GameData roll (MotionPlugin), so both flip to match <see cref="Update"/>.
        /// </summary>
        public static MotionTelemetryFrame PoseFrame(double pitchDegrees, double rollDegrees, double heaveMm,
            MotionTelemetryFrame? game, double crestPush = 0.0)
        {
            var f = new MotionTelemetryFrame();
            f.Fields[0] = (float)(-pitchDegrees * DegToRad);
            f.Fields[1] = (float)(-rollDegrees * DegToRad);
            if (game != null)
            {
                f.Fields[2] = game.Fields[2];
                f.Fields[3] = game.Fields[3];
                f.Fields[7] = game.Fields[7];
                f.BrakePercent = game.BrakePercent;
                f.ThrottlePercent = game.ThrottlePercent;
                f.EngineRpm = game.EngineRpm;
                f.EngineRpmFraction = game.EngineRpmFraction;
                f.Throttle = game.Throttle;
                f.Flags = game.Flags;
            }
            else
                f.Flags = MozaMotionProtocol.FlagInGame;
            double heaveAccel = Finite(heaveMm) * HeaveAccelPerMm - Math.Max(0.0, Finite(crestPush));
            f.Fields[6] = (float)Math.Max(-PoseHeaveAccelLimit, Math.Min(PoseHeaveAccelLimit, heaveAccel));
            for (int k = 0; k < 4; k++)
                f.Fields[8 + k] = MotionTelemetryFrame.ShockRestMm[k];
            return f;
        }

        /// <summary>
        /// The pose for the controller's AI mode (mode 11), which follows the frame as positions:
        /// f0/f1 are the platform's angles 1:1 and f6 carries the wanted heave in mm, with
        /// <paramref name="crestPush"/> taken off as a drop in mm (a full push being
        /// <see cref="AiPoseCrestDropMm"/>). The lane applies its travel, speed and acceleration
        /// limits and the inverse of the controller's heave soft limit before sending
        /// (<see cref="AiPoseLimiter"/>, MozaMotionController.StreamTick). The shock fields are
        /// ignored in this mode.
        /// </summary>
        public static MotionTelemetryFrame PoseFrameAsPosition(double pitchDegrees, double rollDegrees, double heaveMm,
            MotionTelemetryFrame? game, double crestPush, double tiltScale)
        {
            // tiltScale: share of the pose angle to tilt by (1 = the true angle; AiPoseLimiter.DefaultTiltShare without Max performance).
            // ±30° only keeps the numbers sane; the lane's travel limit is the real bound.
            double scale = Math.Max(0.0, Math.Min(1.0, Finite(tiltScale)));
            double pitch = Math.Max(-30.0, Math.Min(30.0, Finite(pitchDegrees) * scale));
            double roll = Math.Max(-30.0, Math.Min(30.0, Finite(rollDegrees) * scale));
            var f = PoseFrame(pitch, roll, 0.0, game);
            double mm = Finite(heaveMm) - Math.Max(0.0, Finite(crestPush)) * (AiPoseCrestDropMm / CrestPushMax);
            f.Fields[6] = (float)Math.Max(-AiPoseHeaveLimitMm, Math.Min(AiPoseHeaveLimitMm, mm));
            return f;
        }

        private static bool ReadIRacingShocks(PluginManager pluginManager, MotionTelemetryFrame f)
        {
            if (!string.Equals(pluginManager.GameName, "IRacing", StringComparison.OrdinalIgnoreCase)) return false;
            var mm = new float[4];
            for (int k = 0; k < 4; k++)
            {
                if (!TryDouble(pluginManager.GetPropertyValue(IRacingShocks[k]), out double m)) return false;
                mm[k] = (float)(m * 1000.0);
            }
            Array.Copy(mm, 0, f.Fields, 8, 4);
            return true;
        }

        private static bool TryDouble(object? v, out double value)
        {
            value = 0;
            if (v == null) return false;
            try { value = Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return false; }
            return IsFinite(value);
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        // A non-finite sample would stick in the crest filter.
        private static double Finite(double? v) => v is double x && IsFinite(x) ? x : 0.0;

        private static byte Percent(double v) => (byte)Math.Max(0, Math.Min(100, Math.Round(v)));

        private static double WrapPi(double rad)
        {
            rad %= 2 * Math.PI;
            if (rad > Math.PI) rad -= 2 * Math.PI;
            else if (rad <= -Math.PI) rad += 2 * Math.PI;
            return rad;
        }
    }
}
