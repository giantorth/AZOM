using System;
using System.Windows;
using System.Windows.Controls;
using MozaPlugin.Devices.MBooster;
using static MozaPlugin.UI.UiHelpers;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Automatic bite point in the mBooster Pedal Feel card (see
    /// MozaPlugin.MBoosterAutoBiteFeel.cs). While it owns the selected clutch's
    /// curve, the graph shows the curve the pedal gets and can't be edited, the
    /// presets are off, and Sensor Output Ratio stays at angle only. The stored
    /// curve is kept for when it's turned off.
    /// </summary>
    public partial class SettingsControl
    {
        // Whether the graph last showed the automatic curve, so the stored one is
        // put back once when it stops applying.
        private bool _mboosterAutoBiteShown;

        /// <summary>The automatic bite point owns this pedal's curve: on, and an
        /// active clutch (the gate MozaPlugin.TryAutoBiteFeelCurve applies).</summary>
        private bool MBoosterAutoBiteActive(IMBoosterPedalConfig? fx) =>
            fx?.AutoBiteFeel?.Enabled == true
            && MBoosterSelectedPedalRole() == MBoosterRole.Clutch
            && (CurrentMBoosterController()?.IsAxisMotorized(_mboosterEffectPedalIndex) ?? false);

        /// <summary>Seed the Automatic bite point controls and the curve. Assumes
        /// the event suppressor is active.</summary>
        private void SeedMBoosterAutoBite(IMBoosterPedalConfig? fx)
        {
            var p = fx?.AutoBiteFeel;
            MBoosterAutoBiteEnable.IsChecked = p?.Enabled ?? false;
            SeedAutoBiteSlider(MBoosterAutoBiteWidthSlider, MBoosterAutoBiteWidthValue,
                p?.WidthPct ?? MBoosterUiConstants.AutoBiteWidthDefaultPct);
            SeedAutoBiteSlider(MBoosterAutoBiteBeforeSlider, MBoosterAutoBiteBeforeValue,
                p?.BeforePct ?? MBoosterUiConstants.AutoBiteBeforeDefaultPct);
            SeedAutoBiteSlider(MBoosterAutoBiteAfterSlider, MBoosterAutoBiteAfterValue,
                p?.AfterPct ?? MBoosterUiConstants.AutoBiteAfterDefaultPct);
            ApplyMBoosterAutoBiteState(fx, showCurve: true);
        }

        private static void SeedAutoBiteSlider(Slider slider, TextBox box, int v)
        {
            slider.Value = v;
            SetValueText(box, ((int)Math.Round(slider.Value)).ToString());
        }

        /// <summary>Lock what the automatic curve owns, show the bite point, and
        /// show the curve the pedal gets — every call while it applies, else
        /// when <paramref name="showCurve"/> or it just stopped applying. Assumes
        /// the event suppressor is active.</summary>
        private void ApplyMBoosterAutoBiteState(IMBoosterPedalConfig? fx, bool showCurve)
        {
            bool active = MBoosterAutoBiteActive(fx);
            MBoosterInputCurveEditor.IsHitTestVisible = !active;
            MBoosterInputCurvePresetsPanel.IsEnabled = !active;
            MBoosterRatioSlider.IsEnabled = !active;
            MBoosterRatioValue.IsEnabled = !active;
            MBoosterAutoBiteSettingsPanel.IsEnabled = fx?.AutoBiteFeel?.Enabled == true;
            if (active)
            {
                MBoosterRatioSlider.Value = 0;
                SetValueText(MBoosterRatioValue, "0%");
            }
            int bite = _plugin.ClutchBitePedalPct;
            MBoosterAutoBiteValue.Text = bite >= 0 ? bite + "%" : "—";
            if (showCurve || active || _mboosterAutoBiteShown) ShowMBoosterFeelCurve(fx);
            _mboosterAutoBiteShown = active;
        }

        /// <summary>Put the curve the pedal gets into the editor's data-store
        /// sliders: the automatic one when it applies, else the stored one (the
        /// Linear default where unset). Assumes the event suppressor is active.</summary>
        private void ShowMBoosterFeelCurve(IMBoosterPedalConfig? fx)
        {
            var c = CurrentMBoosterController();
            if (fx == null || c == null
                || !_plugin.TryAutoBiteFeelCurve(fx, c, _mboosterEffectPedalIndex, out var y, out var x, out float endY))
            {
                int n = MBoosterUiConstants.PedalFeelNodeCount;
                y = fx?.InputCurveY is float[] sy && sy.Length == n ? sy : MBoosterInputCurveDefault;
                x = fx?.InputCurveX is float[] sx && sx.Length == n ? sx : MBoosterInputCurveDefault;
                endY = fx?.InputCurveEndY ?? -1;
            }
            var ys = new[] { MBoosterInputY1Slider, MBoosterInputY2Slider, MBoosterInputY3Slider,
                             MBoosterInputY4Slider, MBoosterInputY5Slider, MBoosterInputY6Slider };
            var xs = new[] { MBoosterInputX1Slider, MBoosterInputX2Slider, MBoosterInputX3Slider,
                             MBoosterInputX4Slider, MBoosterInputX5Slider, MBoosterInputX6Slider };
            for (int i = 0; i < ys.Length; i++)
            {
                ys[i].Value = y[i];
                xs[i].Value = x[i];
            }
            MBoosterInputEndYSlider.Value = endY >= 0 ? endY : 100;
        }

        private void MBoosterAutoBiteEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            var p = s.AutoBiteFeel ??= new MBoosterAutoBiteFeel();
            p.Enabled = MBoosterAutoBiteEnable.IsChecked == true;
            if (MBoosterAutoBiteActive(s))
            {
                // Angle only: see MozaMBoosterRegistry.EffectiveSensorRatioPct.
                s.SensorOutputRatioPct = 0;
                QueueMBoosterPedalFeelPush("angle-ratio",
                    (c, dev) => c.SendFloatWrite("mbooster-brake-angle-ratio", 0f, dev));
            }
            // On: the automatic curve once the bite point is known. Off: the
            // stored curve goes back. Never defaults over the unit's own.
            PushMBoosterFeelCurve(s, MBoosterFeelPushGuard.IfSetOrSent);
            _plugin.SaveSettings();
            using (_suppressor.Begin()) ApplyMBoosterAutoBiteState(s, showCurve: true);
        }

        private void MBoosterAutoBiteWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterAutoBiteWidthValue, "", v => SetMBoosterAutoBite(p => p.WidthPct = v));

        private void MBoosterAutoBiteBeforeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterAutoBiteBeforeValue, "", v => SetMBoosterAutoBite(p => p.BeforePct = v));

        private void MBoosterAutoBiteAfterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterAutoBiteAfterValue, "", v => SetMBoosterAutoBite(p => p.AfterPct = v));

        private void SetMBoosterAutoBite(Action<MBoosterAutoBiteFeel> set)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            set(s.AutoBiteFeel ??= new MBoosterAutoBiteFeel());
            if (!MBoosterAutoBiteActive(s)) return;
            PushMBoosterFeelCurve(s);
            using (_suppressor.Begin()) ShowMBoosterFeelCurve(s);
        }
    }
}
