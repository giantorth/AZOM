using System.Globalization;
using MozaPlugin.Devices.MBooster;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Shows the mBooster Start/End of Travel slider in Pit House's
    /// pedal-face arc mm and steps it 0.1 arc mm at a time; its values stay
    /// the screw mm the wire carries. See <see cref="MBoosterTravelArc"/>.
    /// </summary>
    public sealed class MBoosterTravelArcScale : MozaControls.IRangeSliderScale
    {
        public static readonly MBoosterTravelArcScale Instance = new MBoosterTravelArcScale();

        private MBoosterTravelArcScale() { }

        public double ToDisplay(double screwMm) => MBoosterTravelArc.ArcMm(screwMm);

        public string Format(double screwMm) =>
            MBoosterTravelArc.ShownArcMm(screwMm).ToString("F1", CultureInfo.InvariantCulture);

        public double Edit(double arcMm, double current, double lo, double hi) =>
            MBoosterTravelArc.ScrewMmForEdit(arcMm, current, lo, hi);
    }
}
