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
        private const int GearPulseMs = 120;

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

        /// <summary>Latest frame if it is younger than <paramref name="maxAgeMs"/>.</summary>
        public MotionTelemetryFrame? Latest(int maxAgeMs)
        {
            var f = Volatile.Read(ref _latest);
            if (f == null || _clock.ElapsedMilliseconds - Interlocked.Read(ref _stampMs) > maxAgeMs) return null;
            return f;
        }

        public void Update(PluginManager pluginManager, GameData data)
        {
            var d = data.NewData;
            if (!data.GameRunning || data.GamePaused || d == null)
            {
                Volatile.Write(ref _latest, null);
                _lastGear = null;
                return;
            }

            long now = _clock.ElapsedMilliseconds;
            double surge = d.AccelerationSurge ?? 0.0;   // m/s², braking positive
            double sway = d.AccelerationSway ?? 0.0;
            double heave = d.AccelerationHeave ?? 0.0;
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
            if (!ReadIRacingShocks(pluginManager, f))
            {
                for (int k = 0; k < 4; k++)
                    f.Fields[8 + k] = (float)(ShockFit[k, 0] + ShockFit[k, 1] * surge + ShockFit[k, 2] * sway
                        + ShockFit[k, 3] * heave + ShockFit[k, 4] * speed * speed);
            }
            f.BrakePercent = Percent(d.Brake);
            f.ThrottlePercent = Percent(d.Throttle);

            string gear = d.Gear ?? string.Empty;
            if (_lastGear != null && gear != _lastGear) _shiftUntilMs = now + GearPulseMs;
            _lastGear = gear;
            f.Flags = (byte)(MozaMotionProtocol.FlagInGame | (now < _shiftUntilMs ? MozaMotionProtocol.FlagGearShift : 0));

            Volatile.Write(ref _latest, f);
            Interlocked.Exchange(ref _stampMs, now);
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
