using System;

namespace MozaPlugin.Devices.Motion
{
    /// <summary>
    /// Limits for the pose streamed in the controller's AI mode (mode 11), which follows pitch, roll
    /// and heave as sent. Game mode leaves travel and rates to the controller's own cueing; here the
    /// plugin asks for positions, so it bounds them itself:
    /// <list type="bullet">
    /// <item>travel: pitch and roll are each eased off towards the travel the controller gives one axis
    /// in game mode, and the furthest any actuator is then meant to move, |heave| + pitch travel + roll
    /// travel (one corner always has all three the same way), is held to game mode's maximum by
    /// scaling the whole pose back;</item>
    /// <item>speed and acceleration: each axis, measured as the actuator travel it causes, moves at
    /// no more than a set mm/s and changes speed by no more than a set mm/s².</item>
    /// </list>
    /// Angles in radians, heave in mm. Not thread-safe: the lane's tick owns it.
    /// </summary>
    public sealed class AiPoseLimiter
    {
        /// <summary>
        /// Actuator travel from centre asked for at most at Max Available Stroke 1.0: the furthest game
        /// mode itself drives an actuator (pitch and roll both at 20°, bench 13 Nov 2026: 62.5 mm).
        /// Manual mode with pitch and roll at 1.0 reaches 63.2 mm, and 64.7 mm with heave too, so a
        /// clearance check done there covers this. The controller also soft-limits mode 11 on its own
        /// (about 50 mm on one axis, scaled by Max Available Stroke); this is the plugin's bound on top.
        /// </summary>
        public const double FullTravelMm = 62.0;

        /// <summary>
        /// Share of a pose angle tilted by without Max performance. Game mode at Pitch / Roll 100 % (bench: a 1° step
        /// moves an actuator 6.7 mm on a 1029 mm rig, where 1° is 8.98 mm). AI mode follows angles
        /// 1:1 and game mode gives about 0.75 of it; without Max performance the pose is scaled to 0.9,
        /// a little above game mode, as tuned from the seat (0.75 felt tamer than the game mode build).
        /// </summary>
        public const double DefaultTiltShare = 0.9;

        /// <summary>
        /// Caps without Max performance, as actuator travel. Chosen from recorded laps (13 Nov 2026): a
        /// lap at full profile gains asked for an 84 mm swing on one axis within 0.3 s and the rig
        /// hopped; these hold the largest such swing to about 56 mm, close to the largest driven without a
        /// hop (53 mm). 150 / 700 and 175 / 900 were driven first and felt tamer than game mode.
        /// </summary>
        public const double SafeMaxSpeedMmS = 210.0, SafeMaxAccelMmS2 = 1080.0;

        private readonly double[] _x = new double[3];   // pitch, roll, heave as actuator mm
        private readonly double[] _v = new double[3];   // mm/s

        /// <summary>Pitch (rad), roll (rad) and heave (mm) last returned.</summary>
        public double Pitch { get; private set; }
        public double Roll { get; private set; }
        public double HeaveMm { get; private set; }

        /// <summary>Forget the motion state and hold this pose (entering the mode: the platform is level).</summary>
        public void Reset(double pitch = 0.0, double roll = 0.0, double heaveMm = 0.0)
        {
            Pitch = pitch; Roll = roll; HeaveMm = heaveMm;
            Array.Clear(_x, 0, 3);
            Array.Clear(_v, 0, 3);
            _seeded = false;
        }

        private bool _seeded;

        /// <summary>
        /// Tilt travel one axis may cause at Max Available Stroke 1.0: where the controller's own
        /// soft limit puts pitch or roll alone in game mode (bench: 49.5 mm at 20° of pitch, 48.0 mm
        /// of roll). Each axis is eased off towards it on its own, as the controller does, so a
        /// pinned pitch leaves roll and heave their full size.
        /// </summary>
        public const double AxisTravelMm = 50.0;

        /// <summary>
        /// The controller's soft limit on a mode 11 angle at Max Available Stroke 1.0, as actuator
        /// travel: out = S·tanh(asked / S) with S about 70 (bench: 32.3 mm asked gave 30.1, 64.5 gave
        /// 49.5; at stroke 0.5, 32.3 gave 24.4). Asking S·atanh(wanted / S) undoes it.
        /// </summary>
        public const double ControllerTiltScaleMm = 70.0;

        private static double Forward(double asked, double s) => s > 0.0 ? s * Math.Tanh(asked / s) : asked;

        private static double Inverse(double wanted, double s)
        {
            if (s <= 0.0) return wanted;
            double u = Math.Max(-0.95, Math.Min(0.95, wanted / s));
            return s * 0.5 * Math.Log((1.0 + u) / (1.0 - u));
        }

        /// <summary>
        /// Move towards the target pose within the travel, speed and acceleration limits and return
        /// the pose to send. <paramref name="strokeShare"/> is Max Available Stroke (0..1), which
        /// scales every travel figure as it does in the controller; <paramref name="dtSeconds"/> is
        /// the time since the last call. The limits act on the travel the platform is meant to end
        /// up with: each of pitch and roll eased off towards <see cref="AxisTravelMm"/>, their sum
        /// with heave held to <see cref="FullTravelMm"/>, then speed and acceleration; the angles
        /// returned are those that give that travel through the controller's own soft limit.
        /// </summary>
        public void Step(double pitch, double roll, double heaveMm, double dtSeconds,
            double halfLengthMm, double halfWidthMm, double strokeShare, double maxSpeedMmS, double maxAccelMmS2)
        {
            if (double.IsNaN(pitch) || double.IsInfinity(pitch)) pitch = 0.0;
            if (double.IsNaN(roll) || double.IsInfinity(roll)) roll = 0.0;
            if (double.IsNaN(heaveMm) || double.IsInfinity(heaveMm)) heaveMm = 0.0;
            double stroke = Math.Max(0.0, Math.Min(1.0, strokeShare));
            double axis = AxisTravelMm * stroke, travel = FullTravelMm * stroke, scale = ControllerTiltScaleMm * stroke;

            // Each axis as the actuator travel it is meant to cause, so one mm/s figure covers all three.
            double tp = Forward(halfLengthMm * Math.Sin(pitch), axis);
            double tr = Forward(halfWidthMm * Math.Sin(roll), axis);
            double th = heaveMm;
            double worst = Math.Abs(tp) + Math.Abs(tr) + Math.Abs(th);
            if (worst > travel && worst > 0.0)
            {
                double k = travel / worst;
                tp *= k; tr *= k; th *= k;
            }
            if (!_seeded)
            {
                _x[0] = Forward(halfLengthMm * Math.Sin(Pitch), scale);
                _x[1] = Forward(halfWidthMm * Math.Sin(Roll), scale);
                _x[2] = HeaveMm;
                _seeded = true;
            }
            double dt = Math.Max(0.001, Math.Min(0.2, dtSeconds));
            Advance(0, tp, dt, maxSpeedMmS, maxAccelMmS2);
            Advance(1, tr, dt, maxSpeedMmS, maxAccelMmS2);
            Advance(2, th, dt, maxSpeedMmS, maxAccelMmS2);

            // The axes move at their own pace, so on the way between two poses that are each inside
            // the travel their sum can pass it (one axis still leaving while another arrives): hold
            // the sum to the travel here too, pulling all three back in proportion.
            double sum = Math.Abs(_x[0]) + Math.Abs(_x[1]) + Math.Abs(_x[2]);
            if (sum > travel && sum > 0.0)
            {
                double k = travel / sum;
                for (int i = 0; i < 3; i++) { _x[i] *= k; _v[i] *= k; }
            }

            IntendedPitchMm = _x[0]; IntendedRollMm = _x[1];
            Pitch = Math.Asin(Math.Max(-1.0, Math.Min(1.0, Inverse(_x[0], scale) / halfLengthMm)));
            Roll = Math.Asin(Math.Max(-1.0, Math.Min(1.0, Inverse(_x[1], scale) / halfWidthMm)));
            HeaveMm = _x[2];
        }

        /// <summary>Tilt travel (mm at an actuator) the last <see cref="Step"/> meant pitch and roll to cause.</summary>
        public double IntendedPitchMm { get; private set; }
        public double IntendedRollMm { get; private set; }

        private void Advance(int i, double target, double dt, double vMax, double aMax)
        {
            double err = target - _x[i];
            // The speed that reaches the target this tick, no faster than the cap, and no faster than
            // can still be braked to rest at the target within the acceleration cap.
            double want = err / dt;
            double brake = Math.Sqrt(2.0 * aMax * Math.Abs(err));
            double cap = Math.Min(vMax, brake);
            want = Math.Max(-cap, Math.Min(cap, want));
            double dv = aMax * dt;
            double v = Math.Max(_v[i] - dv, Math.Min(_v[i] + dv, want));
            double next = _x[i] + v * dt;
            // Never step past the target.
            if ((target - _x[i]) * (target - next) < 0.0) { next = target; v = 0.0; }
            _x[i] = next;
            _v[i] = v;
        }
    }
}
