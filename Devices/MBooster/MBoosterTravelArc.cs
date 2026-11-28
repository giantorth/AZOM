using System;

namespace MozaPlugin.Devices.MBooster
{
    /// <summary>
    /// Pit House 1.4.2.7's pedal-face arc mm for the travel registers, which
    /// carry ball-screw mm (0x84/0x85). Generation 1 (mBooster,
    /// RS21-P01-HW PM-CU-V12) constants only. See
    /// docs/protocol/devices/mbooster.md "Travel display: pedal-face arc mm".
    /// </summary>
    public static class MBoosterTravelArc
    {
        public const double StrokeMm = 53.5;

        private const double R = 163.293297;
        private const double Theta0 = 1.053970521;
        private const double D = 162.210043;
        private const double K = 49241.8953;
        private const double M = 46405.038043;

        // Pit House pads with 1e-6. Float storage and the wire's ±0.0005 arc mm
        // put 0.1-step values just under their tick, so they'd show 0.1 low.
        private const double TickEpsilon = 1e-3;

        /// <summary>Arc mm at screw position <paramref name="screwMm"/>; NaN outside 0–53.5.</summary>
        public static double ArcMm(double screwMm)
        {
            if (!(screwMm >= 0 && screwMm <= StrokeMm)) return double.NaN;
            double c = D - screwMm;
            return R * (Theta0 - Math.Acos((K - c * c) / M));
        }

        /// <summary>Inverse of <see cref="ArcMm"/>.</summary>
        public static double ScrewMm(double arcMm) =>
            D - Math.Sqrt(K - M * Math.Cos(Theta0 - arcMm / R));

        private static double Tick(double arcMm) => Math.Floor((arcMm + TickEpsilon) * 10);

        /// <summary>Arc mm of a screw position as Pit House shows it, floored to 0.1.</summary>
        public static double ShownArcMm(double screwMm) => Tick(ArcMm(screwMm)) / 10.0;

        /// <summary>Usable travel between two screw positions as Pit House shows it.</summary>
        public static double ShownSpanMm(double startScrewMm, double endScrewMm) =>
            Tick(ArcMm(endScrewMm) - ArcMm(startScrewMm)) / 10.0;

        /// <summary>
        /// Pit House's <c>screwMmForEdit</c>: the screw mm an edit to
        /// <paramref name="arcMm"/> commits, with <paramref name="current"/>
        /// kept while it shows the same 0.1 tick and either bound returned
        /// exactly once its shown tick is reached.
        /// </summary>
        public static double ScrewMmForEdit(double arcMm, double current, double lo, double hi)
        {
            double t = Tick(arcMm);
            if (current >= lo && current <= hi && Tick(ArcMm(current)) == t) return current;
            if (t <= Tick(ArcMm(lo))) return lo;
            if (t >= Tick(ArcMm(hi))) return hi;
            return Math.Max(lo, Math.Min(hi, ScrewMm(t / 10.0)));
        }
    }
}
