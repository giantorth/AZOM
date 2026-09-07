using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MozaPlugin.Devices;
using MozaPlugin.Resources;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.UI;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;
using SimHub.Plugins.OutputPlugins.EditorControls;
using SimHub.Plugins.OutputPlugins.GraphicalDash.Models;
using static MozaPlugin.UI.UiHelpers;
using SerialTrafficCapture = MozaPlugin.Diagnostics.SerialTrafficCapture;
using CaptureRedactor = MozaPlugin.Diagnostics.CaptureRedactor;
using MozaPlugin.Settings;

namespace MozaPlugin.UI
{
    public partial class SettingsControl : UserControl
    {

        // ===== Wheelbase LFE effects (base fw >= 1.2.10.10) =====
        // Profile-tier host-rendered effects — mutate BaseLfeSettings and Save.
        // Each parameter is dual-mode: a slider, or an NCalc/property formula
        // (ƒₓ) that overrides it. A set formula disables the slider and shows "ƒₓ".

        private void SeedBaseLfeControls(BaseLfeSettings? fx)
        {
            // Populate the preset dropdown once (rebuilding items per tick would
            // close an open dropdown every 500 ms); then keep its selection synced to
            // the active profile's saved preset each tick (handles profile switches).
            if (!_lfePresetsInitialized)
            {
                _lfePresetsInitialized = true;
                BaseLfeScope.Poll = () => _plugin.GetLfeScopeSamples();
                SeedBuiltInPresets();
                RefreshLfePresetList();
            }
            SelectLfePreset();

            fx ??= new BaseLfeSettings();
            var eng = fx.Engine ?? new BaseLfeChannel();
            var ab = fx.Abs ?? new BaseLfeChannel();
            var gs = fx.Gearshift ?? new BaseLfeChannel();

            BaseLfeEngineEnable.IsChecked = eng.Enabled;
            BaseLfeEngineMode.SelectedIndex = (int)eng.Mode;
            BaseLfeAbsMode.SelectedIndex = (int)ab.Mode;
            BaseLfeGearshiftMode.SelectedIndex = (int)gs.Mode;
            BaseLfeEngineTriggerMode.SelectedIndex = (int)eng.TriggerMode;
            BaseLfeAbsTriggerMode.SelectedIndex = (int)ab.TriggerMode;
            BaseLfeGearshiftTriggerMode.SelectedIndex = (int)gs.TriggerMode;
            SeedLfeTrigger(BaseLfeEngineTriggerText, eng.TriggerFormula);
            SetLfeFreqSliderRange(BaseLfeEngineFrequencySlider, eng.Mode);
            SeedLfeParam(BaseLfeEngineFrequencySlider, BaseLfeEngineFrequencyValue, BaseLfeEngineFrequencyFormulaText, eng.Frequency, eng.FrequencyFormula);
            SeedLfeParam(BaseLfeEngineIntensity, BaseLfeEngineIntensityValue, BaseLfeEngineIntensityFormulaText, eng.Intensity, eng.IntensityFormula);
            SeedLfeParam(BaseLfeEngineSmoothness, BaseLfeEngineSmoothnessValue, BaseLfeEngineSmoothnessFormulaText, eng.Smoothness, eng.SmoothnessFormula);

            BaseLfeAbsEnable.IsChecked = ab.Enabled;
            SeedLfeTrigger(BaseLfeAbsTriggerText, ab.TriggerFormula);
            SetLfeFreqSliderRange(BaseLfeAbsFrequencySlider, ab.Mode);
            SeedLfeParam(BaseLfeAbsFrequencySlider, BaseLfeAbsFrequencyValue, BaseLfeAbsFrequencyFormulaText, ab.Frequency, ab.FrequencyFormula);
            SeedLfeParam(BaseLfeAbsIntensity, BaseLfeAbsIntensityValue, BaseLfeAbsIntensityFormulaText, ab.Intensity, ab.IntensityFormula);
            SeedLfeParam(BaseLfeAbsSmoothness, BaseLfeAbsSmoothnessValue, BaseLfeAbsSmoothnessFormulaText, ab.Smoothness, ab.SmoothnessFormula);

            BaseLfeGearshiftEnable.IsChecked = gs.Enabled;
            SeedLfeTrigger(BaseLfeGearshiftTriggerText, gs.TriggerFormula);
            SetLfeFreqSliderRange(BaseLfeGearshiftFrequencySlider, gs.Mode);
            SeedLfeParam(BaseLfeGearshiftFrequencySlider, BaseLfeGearshiftFrequencyValue, BaseLfeGearshiftFrequencyFormulaText, gs.Frequency, gs.FrequencyFormula);
            SeedLfeParam(BaseLfeGearshiftIntensity, BaseLfeGearshiftIntensityValue, BaseLfeGearshiftIntensityFormulaText, gs.Intensity, gs.IntensityFormula);
            SeedLfeParam(BaseLfeGearshiftSmoothness, BaseLfeGearshiftSmoothnessValue, BaseLfeGearshiftSmoothnessFormulaText, gs.Smoothness, gs.SmoothnessFormula);

            // Frequency band — shown only while a frequency formula is active.
            SeedLfeFreqLimits(BaseLfeEngineFreqLimits, BaseLfeEngineFreqRange, BaseLfeEngineFreqRangeText, eng);
            SeedLfeFreqLimits(BaseLfeAbsFreqLimits, BaseLfeAbsFreqRange, BaseLfeAbsFreqRangeText, ab);
            SeedLfeFreqLimits(BaseLfeGearshiftFreqLimits, BaseLfeGearshiftFreqRange, BaseLfeGearshiftFreqRangeText, gs);

            // Edge refinements (vibrate-on-neutral + debounce) — every channel has
            // them, shown only while that channel is in On-change mode.
            SeedLfeEdge(BaseLfeEngineEdgeOptions, BaseLfeEngineVibrateOnNeutral, BaseLfeEngineDebounceSlider, BaseLfeEngineDebounceValue, eng);
            SeedLfeEdge(BaseLfeAbsEdgeOptions, BaseLfeAbsVibrateOnNeutral, BaseLfeAbsDebounceSlider, BaseLfeAbsDebounceValue, ab);
            SeedLfeEdge(BaseLfeGearshiftEdgeOptions, BaseLfeGearshiftVibrateOnNeutral, BaseLfeGearshiftDebounceSlider, BaseLfeGearshiftDebounceValue, gs);

            // Live formula readouts next to ƒ(x) (shown only when that param has a
            // formula). Re-evaluated each RefreshDisplay tick while the LFE tab is
            // showing — twelve NCalc evaluations are not worth paying for a hidden
            // tab. Frequency uses the channel's own rescale so it matches the value
            // the worker sends.
            if (!BaseLfeTab.IsSelected) return;
            UpdateLfeCalc(BaseLfeEngineTriggerCalc, eng.TriggerFormula, r => r);
            UpdateLfeCalc(BaseLfeEngineFrequencyCalc, eng.FrequencyFormula, eng.RescaleFreq);
            UpdateLfeCalc(BaseLfeEngineIntensityCalc, eng.IntensityFormula, r => Math.Max(0, Math.Min(100, r)));
            UpdateLfeCalc(BaseLfeEngineSmoothnessCalc, eng.SmoothnessFormula, r => Math.Max(0, Math.Min(100, r)));
            UpdateLfeCalc(BaseLfeAbsTriggerCalc, ab.TriggerFormula, r => r);
            UpdateLfeCalc(BaseLfeAbsFrequencyCalc, ab.FrequencyFormula, ab.RescaleFreq);
            UpdateLfeCalc(BaseLfeAbsIntensityCalc, ab.IntensityFormula, r => Math.Max(0, Math.Min(100, r)));
            UpdateLfeCalc(BaseLfeAbsSmoothnessCalc, ab.SmoothnessFormula, r => Math.Max(0, Math.Min(100, r)));
            UpdateLfeCalc(BaseLfeGearshiftTriggerCalc, gs.TriggerFormula, r => r);
            UpdateLfeCalc(BaseLfeGearshiftFrequencyCalc, gs.FrequencyFormula, gs.RescaleFreq);
            UpdateLfeCalc(BaseLfeGearshiftIntensityCalc, gs.IntensityFormula, r => Math.Max(0, Math.Min(100, r)));
            UpdateLfeCalc(BaseLfeGearshiftSmoothnessCalc, gs.SmoothnessFormula, r => Math.Max(0, Math.Min(100, r)));
        }

        // Evaluate a param's formula and show the shaped result next to ƒ(x)
        // (hidden when there is no formula — the slider/value box shows it then).
        private void UpdateLfeCalc(TextBlock calc, string? formula, Func<double, double> shape)
        {
            bool has = !string.IsNullOrWhiteSpace(formula);
            calc.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has) return;
            double v = shape(_plugin.EvalHapticsFormula(formula));
            calc.Text = Math.Abs(v) >= 10
                ? Math.Round(v).ToString("0")
                : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void SeedLfeFreqLimits(FrameworkElement panel, MozaControls.MozaRangeSlider range, TextBlock readout, BaseLfeChannel ch)
        {
            panel.Visibility = string.IsNullOrWhiteSpace(ch.FrequencyFormula) ? Visibility.Collapsed : Visibility.Visible;
            double lo = Math.Max(0, Math.Min(200, ch.FrequencyMin));
            double hi = Math.Max(0, Math.Min(200, ch.FrequencyMax));
            range.LowValue = lo;
            range.HighValue = hi;
            readout.Text = LfeRangeText(lo, hi);
        }

        private static string LfeRangeText(double lo, double hi) => $"{(int)Math.Round(lo)} – {(int)Math.Round(hi)}";

        private static void SeedLfeEdge(FrameworkElement panel, System.Windows.Controls.Primitives.ToggleButton neutral, Slider debSlider, TextBox debBox, BaseLfeChannel ch)
        {
            panel.Visibility = ch.TriggerMode == BaseLfeTriggerMode.OnChange ? Visibility.Visible : Visibility.Collapsed;
            neutral.IsChecked = ch.VibrateOnNeutral;
            int db = ch.DebounceMs;
            if (db < 0) db = 50;
            if (db > 1000) db = 1000;
            db = ((db + 25) / 50) * 50;
            debSlider.Value = db;
            debBox.Text = $"{db} ms";
        }

        // The frequency slider's range follows the slot's mode (Custom = full wire
        // range); the formula-limits band uses the same per-mode ranges via ApplyMode.
        private static (double min, double max) LfeFreqRange(BaseLfeMode mode) => mode switch
        {
            BaseLfeMode.Engine => (30, 130),
            BaseLfeMode.Abs => (5, 30),
            BaseLfeMode.Gearshift => (20, 100),
            _ => (0, 200),   // Custom → full 0..200 Hz
        };
        private static void SetLfeFreqSliderRange(Slider slider, BaseLfeMode mode)
        {
            var (min, max) = LfeFreqRange(mode);
            slider.Minimum = min;   // Min first: new min <= old max in every mode transition
            slider.Maximum = max;
        }

        // A formula overrides the slider: hide the slider + value box and show the
        // formula string in their place (full text in the tooltip). No formula →
        // slider + editable value box, formula line hidden.
        private static void SeedLfeParam(Slider slider, TextBox box, TextBlock formulaText, double sliderVal, string? formula)
        {
            bool hasFormula = !string.IsNullOrWhiteSpace(formula);
            slider.Value = sliderVal;                     // kept as the revert-to value; WPF clamps to Min/Max
            slider.Visibility = hasFormula ? Visibility.Collapsed : Visibility.Visible;
            box.Visibility = hasFormula ? Visibility.Collapsed : Visibility.Visible;
            box.Text = ((int)slider.Value).ToString();
            formulaText.Visibility = hasFormula ? Visibility.Visible : Visibility.Collapsed;
            formulaText.Text = hasFormula ? formula : "";
            formulaText.ToolTip = hasFormula ? formula : null;
        }

        private static void SeedLfeTrigger(TextBlock text, string? formula)
        {
            bool has = !string.IsNullOrWhiteSpace(formula);
            text.Text = has ? formula : Strings.Label_AlwaysOn;
            text.ToolTip = has ? formula : null;
            // Match the frequency/intensity/smoothness formula lines: cyan mono for
            // a live formula, dim UI font for the "(always on)" placeholder.
            text.Foreground = (System.Windows.Media.Brush)text.FindResource(has ? "CyanBrush" : "TextDimBrush");
            text.FontFamily = (System.Windows.Media.FontFamily)text.FindResource(has ? "FontMono" : "FontUi");
        }

        // Slider handlers only fire on user drag (a set formula disables the
        // slider). Each writes the channel's slider value.
        // Engine ---------------------------------------------------------------
        private void BaseLfeEngineEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = BaseLfeEngineEnable.IsChecked == true;
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Engine.Enabled = on; });
            _plugin.SaveSettings();
        }
        private void BaseLfeEngineFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(Math.Max(BaseLfeEngineFrequencySlider.Minimum, Math.Min(BaseLfeEngineFrequencySlider.Maximum, e.NewValue)));
            BaseLfeEngineFrequencyValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Engine.Frequency = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeEngineIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            BaseLfeEngineIntensityValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Engine.Intensity = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeEngineSmoothness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            BaseLfeEngineSmoothnessValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Engine.Smoothness = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeEngineTest_Click(object sender, RoutedEventArgs e)
            => _plugin.TriggerBaseLfeEngineTest();

        // ABS ------------------------------------------------------------------
        private void BaseLfeAbsEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = BaseLfeAbsEnable.IsChecked == true;
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Abs.Enabled = on; });
            _plugin.SaveSettings();
        }
        private void BaseLfeAbsFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(Math.Max(BaseLfeAbsFrequencySlider.Minimum, Math.Min(BaseLfeAbsFrequencySlider.Maximum, e.NewValue)));
            BaseLfeAbsFrequencyValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Abs.Frequency = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeAbsIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            BaseLfeAbsIntensityValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Abs.Intensity = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeAbsSmoothness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            BaseLfeAbsSmoothnessValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Abs.Smoothness = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeAbsTest_Click(object sender, RoutedEventArgs e)
            => _plugin.TriggerBaseLfeAbsTest();

        // Gearshift ------------------------------------------------------------
        private void BaseLfeGearshiftEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = BaseLfeGearshiftEnable.IsChecked == true;
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Gearshift.Enabled = on; });
            _plugin.SaveSettings();
        }
        private void BaseLfeGearshiftFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(Math.Max(BaseLfeGearshiftFrequencySlider.Minimum, Math.Min(BaseLfeGearshiftFrequencySlider.Maximum, e.NewValue)));
            BaseLfeGearshiftFrequencyValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Gearshift.Frequency = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeGearshiftIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            BaseLfeGearshiftIntensityValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Gearshift.Intensity = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeGearshiftSmoothness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            BaseLfeGearshiftSmoothnessValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Gearshift.Smoothness = v; });
            _plugin.SaveSettings();
        }
        // Edge refinements (Tag = channel). Vibrate-on-neutral + debounce, per channel.
        private void BaseLfeVibrateOnNeutral_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (sender is not System.Windows.Controls.Primitives.ToggleButton tog || tog.Tag is not string ch) return;
            bool on = tog.IsChecked == true;
            _plugin.UpdateActiveProfile(p => LfeChannelForTag(p.BaseLfe ??= new BaseLfeSettings(), ch).VibrateOnNeutral = on);
            _plugin.SaveSettings();
        }
        private void BaseLfeDebounce_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            if (sender is not Slider sl || sl.Tag is not string ch) return;
            int val = (int)Math.Round(e.NewValue);
            val = ((val + 25) / 50) * 50;
            if (val < 0) val = 0;
            if (val > 1000) val = 1000;
            if (FindName($"BaseLfe{LfeCap(ch)}DebounceValue") is TextBox box) box.Text = $"{val} ms";
            _plugin.UpdateActiveProfile(p => LfeChannelForTag(p.BaseLfe ??= new BaseLfeSettings(), ch).DebounceMs = val);
            _plugin.SaveSettings();
        }
        private void BaseLfeGearshiftTest_Click(object sender, RoutedEventArgs e)
            => _plugin.TriggerBaseLfeGearshiftTest();

        // Presets Test: fire every ENABLED slot's mode-based test at once. They
        // sum on the base, so this previews the combined feel of the whole setup.
        private void BaseLfePresetsTest_Click(object sender, RoutedEventArgs e)
        {
            var lfe = _plugin.Settings?.ProfileStore?.CurrentProfile?.BaseLfe;
            if (lfe == null) return;
            if (lfe.Engine?.Enabled == true) _plugin.TriggerBaseLfeEngineTest();
            if (lfe.Abs?.Enabled == true) _plugin.TriggerBaseLfeAbsTest();
            if (lfe.Gearshift?.Enabled == true) _plugin.TriggerBaseLfeGearshiftTest();
        }

        // Frequency clamp band (Tag = channel) — double-ended slider, 0..200 Hz.
        private void BaseLfeFreqRange_RangeChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            if (sender is not MozaControls.MozaRangeSlider rs || rs.Tag is not string ch) return;
            float lo = (float)rs.LowValue, hi = (float)rs.HighValue;
            if (FindName($"BaseLfe{LfeCap(ch)}FreqRangeText") is TextBlock t) t.Text = LfeRangeText(lo, hi);
            _plugin.UpdateActiveProfile(p =>
            {
                var c = LfeChannelForTag(p.BaseLfe ??= new BaseLfeSettings(), ch);
                c.FrequencyMin = lo; c.FrequencyMax = hi;
            });
            _plugin.SaveSettings();
        }

        // "engine" → "Engine" (element-name prefix for the channel's controls).
        private static string LfeCap(string ch) => char.ToUpperInvariant(ch[0]) + ch.Substring(1);

        // Slot role (Tag = channel). Applies a trigger/limits/character template
        // for the chosen effect type; Custom leaves the slot's values untouched.
        // The slot's fixed wire id / render path is unaffected. Re-seed to reflect
        // the applied template across all the slot's controls.
        private void BaseLfeMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (sender is not System.Windows.Controls.Primitives.Selector sel || sel.Tag is not string ch) return;
            int idx = sel.SelectedIndex;
            if (idx < 0) return;
            var mode = (BaseLfeMode)idx;
            _plugin.UpdateActiveProfile(p => BaseLfeSettings.ApplyMode(LfeChannelForTag(p.BaseLfe ??= new BaseLfeSettings(), ch), mode));
            _plugin.SaveSettings();
            RefreshDisplay(this, EventArgs.Empty);
        }

        // Trigger mode: Level (continuous) vs On-change (burst). Tag = channel name.
        // The edge refinements (neutral/debounce) only apply in On-change mode.
        private void BaseLfeTriggerMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (sender is not System.Windows.Controls.Primitives.Selector sel || sel.Tag is not string ch) return;
            var mode = sel.SelectedIndex == 1 ? BaseLfeTriggerMode.OnChange : BaseLfeTriggerMode.Level;
            _plugin.UpdateActiveProfile(p => LfeChannelForTag(p.BaseLfe ??= new BaseLfeSettings(), ch).TriggerMode = mode);
            _plugin.SaveSettings();
            if (FindName($"BaseLfe{LfeCap(ch)}EdgeOptions") is FrameworkElement panel)
                panel.Visibility = mode == BaseLfeTriggerMode.OnChange ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Preset library ────────────────────────────────────────────────────
        // Built-in presets are code-generated (the original preset buttons); user
        // presets persist in MozaPluginSettings.BaseLfePresets. A user preset shadows
        // a same-named built-in (so a built-in can be "edited"); deleting it restores
        // the built-in. The list is rebuilt only on init/load/save/delete — NOT per
        // RefreshDisplay tick — so the dropdown selection isn't clobbered.
        private List<BaseLfePreset> _lfePresets = new List<BaseLfePreset>();
        private bool _lfePresetsInitialized;

        // Bump when FactoryLfePresets changes so existing libraries get refreshed.
        private const int CurrentLfeSeedVersion = 2;

        // The factory presets, generated from code. Seeded into the editable list;
        // thereafter they're ordinary editable/deletable presets.
        private static List<BaseLfePreset> FactoryLfePresets() => new List<BaseLfePreset>
        {
            new BaseLfePreset { Name = Strings.Label_Defaults, Settings = new BaseLfeSettings() },
            new BaseLfePreset { Name = Strings.Preset_AdditiveEngine, Settings = BaseLfeSettings.AdditiveEngine() },
            new BaseLfePreset { Name = Strings.Preset_BigRig, Settings = BaseLfeSettings.BigRig() },
            new BaseLfePreset { Name = Strings.Preset_DetunedV8, Settings = BaseLfeSettings.DetunedV8() },
            new BaseLfePreset { Name = Strings.Preset_RoadRumble, Settings = BaseLfeSettings.RoadRumble() },
        };

        // Seed / refresh the factory presets into the persisted list up to the current
        // seed version. Adds missing factory presets and refreshes factory-NAMED ones
        // to the latest definition; user-named presets are never touched. A factory
        // preset the user renamed survives; one they edited in place is refreshed.
        private void SeedBuiltInPresets()
        {
            var settings = _plugin.Settings;
            if (settings == null || settings.BaseLfePresetsSeedVersion >= CurrentLfeSeedVersion) return;
            var list = settings.BaseLfePresets ??= new List<BaseLfePreset>();
            foreach (var f in FactoryLfePresets())
            {
                var existing = list.Find(u => string.Equals(u.Name, f.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null) existing.Settings = f.Settings;
                else list.Add(f);
            }
            settings.BaseLfePresetsSeedVersion = CurrentLfeSeedVersion;
            _plugin.SaveSettings();
        }

        // Rebuild the dropdown items from the persisted list (factory + user, all
        // uniform). Selection is then synced from the profile.
        private void RefreshLfePresetList()
        {
            var list = new List<BaseLfePreset>(_plugin.Settings?.BaseLfePresets ?? new List<BaseLfePreset>());
            _lfePresets = list;
            using (_suppressor.Begin())
            {
                BaseLfePresetCombo.ItemsSource = null;
                BaseLfePresetCombo.ItemsSource = list;
            }
            SelectLfePreset();
        }

        // Sync the dropdown (+ name box) to the active profile's saved preset name.
        // Suppressed, and only when the selection actually changes, so it never fires
        // an apply and never clobbers what the user is typing in the name box.
        private void SelectLfePreset()
        {
            string? name = _plugin.Settings?.ProfileStore?.CurrentProfile?.BaseLfePresetName;
            int idx = string.IsNullOrEmpty(name)
                ? -1 : _lfePresets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (BaseLfePresetCombo.SelectedIndex == idx) return;
            using (_suppressor.Begin())
            {
                BaseLfePresetCombo.SelectedIndex = idx;
                BaseLfePresetName.Text = idx >= 0 ? _lfePresets[idx].Name : "";
            }
        }

        // Picking a preset applies it to the current effects, records it on the
        // profile (so the dropdown remembers it across restarts), and fills the name
        // box. Programmatic reselects (seed / after save) are suppressed.
        private void BaseLfePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (BaseLfePresetCombo.SelectedItem is not BaseLfePreset p) return;
            BaseLfePresetName.Text = p.Name;
            _plugin.UpdateActiveProfile(prof => { prof.BaseLfe = p.Settings?.Clone() ?? new BaseLfeSettings(); prof.BaseLfePresetName = p.Name; });
            _plugin.SaveSettings();
            RefreshDisplay(this, EventArgs.Empty);
        }

        // Save the current effects under the name box's text. Overwrites a same-named
        // user preset, else adds one (which shadows a same-named built-in). Marks it
        // active on the profile so the dropdown reflects it.
        private void BaseLfePresetSave_Click(object sender, RoutedEventArgs e)
        {
            var settings = _plugin.Settings;
            if (settings == null) return;
            string name = BaseLfePresetName.Text?.Trim() ?? "";
            if (name.Length == 0) return;
            var snapshot = (settings.ProfileStore?.CurrentProfile?.BaseLfe ?? new BaseLfeSettings()).Clone();
            var users = settings.BaseLfePresets ??= new List<BaseLfePreset>();
            var existing = users.Find(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Settings = snapshot;
            else users.Add(new BaseLfePreset { Name = name, BuiltIn = false, Settings = snapshot });
            _plugin.UpdateActiveProfile(prof => prof.BaseLfePresetName = name);
            _plugin.SaveSettings();
            RefreshLfePresetList();
        }

        // Delete the selected preset (any preset — factory presets are ordinary now).
        private void BaseLfePresetDelete_Click(object sender, RoutedEventArgs e)
        {
            if (BaseLfePresetCombo.SelectedItem is not BaseLfePreset p) return;
            _plugin.Settings?.BaseLfePresets?.RemoveAll(u => string.Equals(u.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            _plugin.UpdateActiveProfile(prof => { if (string.Equals(prof.BaseLfePresetName, p.Name, StringComparison.OrdinalIgnoreCase)) prof.BaseLfePresetName = ""; });
            _plugin.SaveSettings();
            BaseLfePresetName.Text = "";
            RefreshLfePresetList();
        }

        // Export the selected preset to its own JSON file (share / back up one preset).
        private void BaseLfePresetExport_Click(object sender, RoutedEventArgs e)
        {
            if (BaseLfePresetCombo.SelectedItem is not BaseLfePreset p) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Moza LFE preset (*.json)|*.json|All files (*.*)|*.*",
                FileName = SanitizeFileName(p.Name) + ".json",
                DefaultExt = ".json",
            };
            try
            {
                if (dlg.ShowDialog() != true) return;
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(p, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(dlg.FileName, json);
            }
            catch (Exception ex) { MozaLog.Warn("[AZOM] LFE preset export failed: " + ex.Message); }
        }

        // Import a preset from a JSON file, merging by name (same name overwrites).
        // Accepts a single preset object or an array (older all-presets exports).
        private void BaseLfePresetImport_Click(object sender, RoutedEventArgs e)
        {
            var settings = _plugin.Settings;
            if (settings == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Moza LFE preset (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            try
            {
                if (dlg.ShowDialog() != true) return;
                var incoming = ParsePresetJson(System.IO.File.ReadAllText(dlg.FileName));
                var list = settings.BaseLfePresets ??= new List<BaseLfePreset>();
                foreach (var p in incoming)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;
                    var settingsClone = p.Settings?.Clone() ?? new BaseLfeSettings();
                    var existing = list.Find(u => string.Equals(u.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) existing.Settings = settingsClone;
                    else list.Add(new BaseLfePreset { Name = p.Name.Trim(), Settings = settingsClone });
                }
                _plugin.SaveSettings();
                RefreshLfePresetList();
            }
            catch (Exception ex) { MozaLog.Warn("[AZOM] LFE preset import failed: " + ex.Message); }
        }

        // A preset file is a single object; an array (old all-presets export) also works.
        private static List<BaseLfePreset> ParsePresetJson(string text)
        {
            if (text != null && text.TrimStart().StartsWith("[", StringComparison.Ordinal))
                return Newtonsoft.Json.JsonConvert.DeserializeObject<List<BaseLfePreset>>(text) ?? new List<BaseLfePreset>();
            var one = Newtonsoft.Json.JsonConvert.DeserializeObject<BaseLfePreset>(text ?? "");
            return one != null ? new List<BaseLfePreset> { one } : new List<BaseLfePreset>();
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "preset" : name;
        }

        // ƒₓ — open SimHub's formula editor for the tagged "channel:param" field.
        // Mirrors MBoosterAdvancedEditFormula_Click. Empty result clears back to
        // the slider.
        private async void BaseLfeFx_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string tag) return;
            var engine = _plugin.ChannelFormulaEngine;
            if (engine == null)
            {
                MozaLog.Warn("[AZOM] LFE formula editor unavailable (SimHub engine not loaded)");
                return;
            }
            var lfe = _plugin.Settings?.ProfileStore?.CurrentProfile?.BaseLfe ?? new BaseLfeSettings();
            var ev = LfeMakeExpression(GetLfeFormula(lfe, tag));
            var working = new ExpressionValue { UseJavascript = ev.UseJavascript, Expression = ev.Expression, PreExpression = ev.PreExpression };
            var data = new DashboardBindingData
            {
                Formula = working,
                Mode = string.IsNullOrWhiteSpace(working.Expression) ? BindingMode.None : BindingMode.Formula,
                TargetPropertyName = tag,
                TargetType = typeof(double),
            };
            try
            {
                var editor = new BindingEditor(engine) { DataContext = data };
                var result = await editor.ShowDialogWindowAsync(this);
                if ((int)result != 1) return;
                string formula = data.Mode == BindingMode.Formula ? LfeSerializeExpression(data.Formula) : "";
                _plugin.UpdateActiveProfile(p => SetLfeFormula(p.BaseLfe ??= new BaseLfeSettings(), tag, formula));
                _plugin.SaveSettings();
                RefreshDisplay(this, EventArgs.Empty);
            }
            catch (Exception ex) { MozaLog.Warn("[AZOM] LFE formula editor failed: " + ex.Message); }
        }

        private static BaseLfeChannel LfeChannelForTag(BaseLfeSettings lfe, string tag)
        {
            if (tag.StartsWith("engine", StringComparison.Ordinal)) return lfe.Engine ??= new BaseLfeChannel();
            if (tag.StartsWith("abs", StringComparison.Ordinal)) return lfe.Abs ??= new BaseLfeChannel();
            return lfe.Gearshift ??= new BaseLfeChannel();
        }
        private static string GetLfeFormula(BaseLfeSettings lfe, string tag)
        {
            var ch = LfeChannelForTag(lfe, tag);
            if (tag.EndsWith("trigger", StringComparison.Ordinal)) return ch.TriggerFormula;
            if (tag.EndsWith("frequency", StringComparison.Ordinal)) return ch.FrequencyFormula;
            if (tag.EndsWith("intensity", StringComparison.Ordinal)) return ch.IntensityFormula;
            return ch.SmoothnessFormula;
        }
        private static void SetLfeFormula(BaseLfeSettings lfe, string tag, string formula)
        {
            var ch = LfeChannelForTag(lfe, tag);
            if (tag.EndsWith("trigger", StringComparison.Ordinal)) ch.TriggerFormula = formula;
            else if (tag.EndsWith("frequency", StringComparison.Ordinal)) ch.FrequencyFormula = formula;
            else if (tag.EndsWith("intensity", StringComparison.Ordinal)) ch.IntensityFormula = formula;
            else ch.SmoothnessFormula = formula;
        }

        // Stored-string <-> ExpressionValue (mirror MBoosterCustomEffectRow).
        private static ExpressionValue LfeMakeExpression(string? stored)
        {
            var ev = new ExpressionValue();
            var s = (stored ?? "").Trim();
            if (s.Length == 0) { ev.UseJavascript = false; ev.Expression = ""; }
            else if (s.StartsWith("js:", StringComparison.OrdinalIgnoreCase)) { ev.UseJavascript = true; ev.Expression = s.Substring(3); }
            else { ev.UseJavascript = false; ev.Expression = global::MozaPlugin.Telemetry.NCalcExpressionEvaluator.LooksLikeExpression(s) ? s : "[" + s + "]"; }
            return ev;
        }
        private static string LfeSerializeExpression(ExpressionValue ev)
        {
            var expr = (ev?.Expression ?? "").Trim();
            if (expr.Length == 0) return "";
            if (ev!.UseJavascript) return "js:" + expr;
            if (expr.Length >= 2 && expr[0] == '[' && expr[expr.Length - 1] == ']')
            {
                var inner = expr.Substring(1, expr.Length - 2);
                if (inner.IndexOf('[') < 0 && inner.IndexOf(']') < 0
                    && !global::MozaPlugin.Telemetry.NCalcExpressionEvaluator.LooksLikeExpression(inner))
                    return inner;
            }
            return expr;
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            SpeedValue.Text = $"{pct}%";
            _data.Speed = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-speed", raw);
            _plugin.SaveSettings();
        }

        private void DamperSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            DamperValue.Text = $"{pct}%";
            _data.Damper = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-damper", raw);
            _plugin.SaveSettings();
        }

        private void FrictionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            FrictionValue.Text = $"{pct}%";
            _data.Friction = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-friction", raw);
            _plugin.SaveSettings();
        }

        private void InertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            int raw = val * 10;
            InertiaValue.Text = $"{val}";
            _data.Inertia = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-inertia", raw);
            _plugin.SaveSettings();
        }

        private void SpringSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            SpringValue.Text = $"{pct}%";
            _data.Spring = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-spring", raw);
            _plugin.SaveSettings();
        }

        private void GameDamperSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameDamperValue.Text = $"{pct}%";
            _data.GameDamper = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("main-set-damper-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameFrictionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameFrictionValue.Text = $"{pct}%";
            _data.GameFriction = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("main-set-friction-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameInertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameInertiaValue.Text = $"{pct}%";
            _data.GameInertia = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("main-set-inertia-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameSpringSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameSpringValue.Text = $"{pct}%";
            _data.GameSpring = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("main-set-spring-gain", raw);
            _plugin.SaveSettings();
        }

        private void SpeedDampingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            SpeedDampingValue.Text = $"{val}%";
            _data.SpeedDamping = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-speed-damping", val);
            _plugin.SaveSettings();
        }

        private void SpeedDampingPointSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            SpeedDampingPointValue.Text = $"{val} kph";
            _data.SpeedDampingPoint = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-speed-damping-point", val);
            _plugin.SaveSettings();
        }

        private void NaturalInertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            NaturalInertiaValue.Text = $"{val}";
            _data.NaturalInertia = val;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-natural-inertia", val);
            _plugin.SaveSettings();
        }

        private void SoftLimitStiffnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int display = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(display * (400.0 / 9.0) - (400.0 / 9.0) + 100.0);
            SoftLimitStiffnessValue.Text = $"{display}";
            _data.SoftLimitStiffness = raw;
            _plugin.HardwareApplier.WriteIfBaseConnected("base-soft-limit-stiffness", raw);
            _plugin.SaveSettings();
        }

    }
}
