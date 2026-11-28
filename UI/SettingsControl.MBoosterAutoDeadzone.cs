using System;
using System.Windows;
using MozaPlugin.Devices.MBooster;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Automatic deadzone in the mBooster Sim Input Mapping card: while on, the
    /// output curve's start point (Min) sits where the output leaves 0 as the
    /// pedal leaves the Pedal Feel Deadzone, and can't be dragged. Recomputed
    /// from the sliders on every user edit of Deadzone, Sensor Output Ratio or
    /// Max Threshold, and stored and written like a drag of that point.
    /// </summary>
    public partial class SettingsControl
    {
        /// <summary>On, and this pedal owns the load cell the formula reads
        /// (its toggle is hidden otherwise).</summary>
        private bool MBoosterAutoDeadzoneActive(IMBoosterPedalConfig? fx) =>
            fx?.AutoDeadzone == true
            && (CurrentMBoosterController()?.OwnsSingletonRegisters(_mboosterEffectPedalIndex) ?? false);

        /// <summary>Seed the toggle and the start point's lock. Assumes the event
        /// suppressor is active.</summary>
        private void SeedMBoosterAutoDeadzone(IMBoosterPedalConfig? fx)
        {
            MBoosterAutoDeadzoneEnable.IsChecked = fx?.AutoDeadzone ?? false;
            MBoosterCurveEditor.LockFirstNodeX = MBoosterAutoDeadzoneActive(fx);
        }

        private void MBoosterAutoDeadzoneEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.AutoDeadzone = MBoosterAutoDeadzoneEnable.IsChecked == true;
            _plugin.SaveSettings();
            MBoosterCurveEditor.LockFirstNodeX = MBoosterAutoDeadzoneActive(s);
            ApplyMBoosterAutoDeadzone();
        }

        /// <summary>
        /// Move the start point to <see cref="MozaMBoosterRegistry.AutoDeadzoneStartPct"/>
        /// of the sliders, through its own data-store slider so it's stored and
        /// written exactly as a drag would be. User edits only: a suppressed seed
        /// would write it into whichever pedal is being seeded.
        /// </summary>
        private void ApplyMBoosterAutoDeadzone()
        {
            if (_suppressEvents || !MBoosterAutoDeadzoneActive(PeekMBoosterEffectTarget())) return;
            int start = MozaMBoosterRegistry.AutoDeadzoneStartPct(
                MBoosterRatioSlider.Value, MBoosterDeadzoneSlider.Value, MBoosterMaxThresholdSlider.Value);
            int maxStart = (int)Math.Round(MBoosterRangeEndSlider.Value) - MBoosterUiConstants.OutputCurveMinRangePct;
            start = Math.Max(0, Math.Min(start, maxStart));
            if ((int)Math.Round(MBoosterRangeStartSlider.Value) != start)
                MBoosterRangeStartSlider.Value = start;
        }
    }
}
