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
        // Pose heave as f6: SimHub's default heave range (17 m/s² of vertical acceleration to
        // 80 mm) run backwards, so at that range and 100 % gain the controller gets about the
        // game's own vertical acceleration. f6 is what the controller's Heave effect follows in
        // game mode, and +f6 raises the platform: bench run of 10 Nov (HeaveGain, Heave 1.0 / 0),
        // +8 m/s² held read +22.8 mm on all four actuators after 0.5 s and −8 read −22.0. The
        // effect washes out (+0.5 mm 2 s into the same step) and dips the other way on release.
        // The scale is fixed on purpose: the profile's Heave row (gain, ranges, gamma) is where the
        // strength is tuned, and a row set up for less travel gives proportionally less f6.
        private const float HeaveAccelPerMm = 17f / 80f;
        // The most the pose source sends as f6 either way, profile heave and crest push together:
        // the level driven on the rig, and well past the ~16 m/s² where the controller's travel
        // levels off (bench table in docs/protocol/motion/game-stream-0x22.md).
        private const float PoseHeaveAccelLimit = 30f;
        // Crest signal. A crest is a long, gentle lightness (Bathurst, 911 Cup, 217 km/h: 1.2 s at
        // −4 m/s² mean, −4.7 low-passed peak), a bump a short, large one (~7 m/s² for 30–50 ms),
        // so only duration tells them apart: the low-pass keeps the first and the dead band drops
        // what it leaves of the second. One-sided: a first version also answered compressions,
        // and the landing after that crest pushed back as hard as the crest itself (a 47 m/s²
        // swing within a second at strength 80), which felt odd and was followed by the
        // controller leaving game mode with error 261.
        private const double HeaveSlowSeconds = 0.2;
        private const double CrestDeadbandAccel = 2.0;
        // At full strength: f6 per m/s² of lightness beyond the dead band, and the ceiling; the
        // strength slider scales the result. 30 is the push judged right from the seat on that
        // crest, with every controller status line at error 0.
        private const double CrestGainFull = 15.0;
        private const double CrestMaxAccel = 30.0;
        // An impact is not a crest. Lightness on track peaked at −12 m/s² raw (the mountain,
        // 200 km/h), so that is the most the signal is allowed to see. The fastest downforce cars
        // brake and corner at 5–6 g (about 60 m/s²) and the wall hit that prompted this read
        // 90–350 along the car, so 80 along or across the car means a hit in any car. A lighter
        // hit is not caught; the input limit and the ceiling still bound what it can ask for.
        private const double CrestInputLimit = 12.0;
        private const double CrestImpactAccel = 80.0;
        private const int CrestImpactHoldMs = 2000;
        // The push follows the lightness up at once and lets go slowly. Over a long crest the
        // lightness wobbles around the dead band two or three times a second (Bathurst, 250 km/h,
        // 3.5 s: six separate pushes of 0 ↔ 30, felt as being bounced); with a slow release the
        // same stretch is one push, and a lap's 29 pushes become 10.
        private const double CrestAttackSeconds = 0.05;
        // How it lets go follows the car: gently while nothing presses back, firmly as the car
        // lands (slow acceleration turning positive, full firmness from +3 m/s²). One fixed
        // release fitted the crest whose landing is firm and came out too severe on those that
        // land softly or late.
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

        /// <summary>
        /// The crest signal at full strength (m/s² of f6, ≥ 0): the car's slow lightness beyond a
        /// dead band, quick to rise and slow to let go. 0 while no game runs. The pose source scales
        /// it by its strength setting and takes it off f6 (<see cref="PoseFrame"/>).
        /// </summary>
        public float CrestPush => _crestPushOut;

        /// <summary>Latest frame if it is younger than <paramref name="maxAgeMs"/>.</summary>
        public MotionTelemetryFrame? Latest(int maxAgeMs)
        {
            var f = Volatile.Read(ref _latest);
            if (f == null || _clock.ElapsedMilliseconds - Interlocked.Read(ref _stampMs) > maxAgeMs) return null;
            return f;
        }

        /// <summary>
        /// Drop the latest frame and the crest filter. Called whenever the feed stops being
        /// updated, so a push left over from before is not sent when updates resume.
        /// </summary>
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
            // An impact's spikes (−20…−28 m/s² vertical with 90…350 along the car, Bathurst wall,
            // 10 Nov) got through the low-pass and held the crest push at its ceiling again and
            // again. Clip what goes in, and sit out the seconds after a hit.
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
        /// which the controller applies as platform attitude; heave as vertical acceleration in f6,
        /// which the controller's Heave effect follows (quick, and washed out, so it gives bumps and
        /// crests their kick and holds nothing); f4/f5 zero so its acceleration effects stay idle,
        /// and the shocks at rest, so heave reaches the base through f6 alone. Heading, yaw rate,
        /// speed, pedals, engine and flags come from <paramref name="game"/> when a game runs.
        /// Pose signs: SimHub's pitch is −GameData pitch and its roll +GameData roll (MotionPlugin),
        /// so both flip to match <see cref="Update"/>. Heave: +mm is up, and +f6 raises the platform.
        /// <paramref name="crestPush"/> (m/s², ≥ 0; <see cref="CrestPush"/> scaled by the strength
        /// setting) comes off f6: the car going light over a crest lowers the platform, whatever
        /// the profile's heave makes of something that long and gentle. Compressions are left to
        /// the profile. The sum is held within ±<see cref="PoseHeaveAccelLimit"/>.
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
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        // A non-finite sample would stick in the crest filter and reach the wire.
        private static double Finite(double? v)
            => v is double x && !double.IsNaN(x) && !double.IsInfinity(x) ? x : 0.0;

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
