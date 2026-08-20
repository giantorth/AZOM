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

namespace MozaPlugin.UI
{
    public partial class SettingsControl : UserControl
    {

        // ===== Custom Effects (Experimental) =====
        // Dynamic per-device list — unlike the eight built-in effects (static
        // named XAML controls wired one-by-one), the count here is
        // user-defined, so the ItemsControl is bound to an ObservableCollection
        // of row view-models (MBoosterCustomEffectRow) instead. Rebuilt wholesale
        // (not incrementally synced) whenever the tab reseeds — simpler than
        // diffing, and this list is edited far less often than a slider is dragged.

        private void PopulateMBoosterCustomEffectsList(IMBoosterEffects? fx)
        {
            MBoosterCustomEffectsList.ItemsSource = _mboosterCustomEffectRows;
            _mboosterCustomEffectRows.Clear();
            var list = fx?.CustomEffects;
            if (list == null || _plugin == null) return;
            // Snapshot once so every row shares one backing list for the
            // simple editor, same as ChannelMappingRowFactory.Build does for
            // the (unrelated) telemetry channel-mapping rows.
            var plugin = _plugin;
            var props = plugin.GetAllSimHubPropertyNames();
            var engine = plugin.ChannelFormulaEngine;
            for (int i = 0; i < list.Count; i++)
            {
                _mboosterCustomEffectRows.Add(new MBoosterCustomEffectRow(list[i], () => plugin.SaveSettings(), OnCustomEffectTestToggle)
                {
                    AllProperties = props,
                    Engine = engine,
                });
            }
        }

        private void OnCustomEffectTestToggle(string effectId, bool on)
        {
            // Resolved at call time (not captured at row-construction time) so
            // this always targets whichever device is currently selected —
            // matters for StopAllCustomEffectTests, called just BEFORE the
            // selected device changes.
            CurrentMBoosterController()?.SetCustomEffectTestActive(effectId, on, _mboosterEffectPedalIndex);
        }

        /// <summary>
        /// Turn off every custom effect's sustained Test toggle for the
        /// currently-selected device — mirrors the explicit stop calls for
        /// the eight built-in effects' Test toggles in
        /// <see cref="OnMBoosterDeviceRowSelected"/> and
        /// <see cref="OnUnloadedStopTimers"/>, so a forgotten toggle doesn't
        /// leave the pedal buzzing with no UI left to turn it off.
        /// </summary>
        private void StopAllCustomEffectTests()
        {
            foreach (var row in _mboosterCustomEffectRows)
                if (row.TestActive) row.TestActive = false;
        }

        private void MBoosterAddCustomEffectButton_Click(object sender, RoutedEventArgs e)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.CustomEffects ??= new List<MBoosterCustomEffect>();
            var effect = new MBoosterCustomEffect
            {
                Name = $"{Strings.DefaultName_CustomEffect} {s.CustomEffects.Count + 1}",
            };
            s.CustomEffects.Add(effect);
            _plugin.SaveSettings();
            _mboosterCustomEffectRows.Add(new MBoosterCustomEffectRow(effect, () => _plugin.SaveSettings(), OnCustomEffectTestToggle)
            {
                AllProperties = _plugin?.GetAllSimHubPropertyNames() ?? Array.Empty<string>(),
                Engine = _plugin?.ChannelFormulaEngine,
            });
        }

        private void MBoosterDeleteCustomEffect_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not MBoosterCustomEffectRow row) return;
            if (row.TestActive) CurrentMBoosterController()?.SetCustomEffectTestActive(row.Id, false, _mboosterEffectPedalIndex);
            var s = CurrentMBoosterEffectTarget();
            s?.CustomEffects?.RemoveAll(c => string.Equals(c.Id, row.Id, StringComparison.Ordinal));
            _plugin.SaveSettings();
            _mboosterCustomEffectRows.Remove(row);
        }

        // ── Formula editing — same dual-mode (pencil + ƒₓ) handlers as
        // DashboardManagementControl's channel-mapping list, scoped to the
        // custom-effects row collection instead. See
        // MBoosterCustomEffectRow's "Formula editing" region.

        private void MBoosterEditFormula_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not MBoosterCustomEffectRow row) return;
            // Only one inline editor expanded at a time, same as the channel mapper.
            foreach (var r in _mboosterCustomEffectRows)
                if (!ReferenceEquals(r, row) && r.IsEditing) r.CancelEdit();
            row.BeginEdit();
        }

        private void MBoosterCommitFormula_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not MBoosterCustomEffectRow row) return;
            row.CommitEdit();
        }

        private void MBoosterCancelFormula_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not MBoosterCustomEffectRow row) return;
            row.CancelEdit();
        }

        /// <summary>
        /// Advanced edit: open SimHub's own formula editor (BindingEditor)
        /// against the shared engine and a working copy of the row's
        /// formula. On OK, write the result back through the row (which
        /// serializes it into Formula and persists). Mirrors
        /// DashboardManagementControl.AdvancedEditMapping_Click.
        /// </summary>
        private async void MBoosterAdvancedEditFormula_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not MBoosterCustomEffectRow row) return;
            var engine = row.Engine;
            if (engine == null)
            {
                MozaLog.Warn("[AZOM] mBooster custom-effect formula editor unavailable (SimHub engine not loaded)");
                return;
            }

            // Work on a throwaway ExpressionValue so the dialog never mutates
            // the row's live Expression mid-edit; copy back only on OK.
            var src = row.Expression;
            var working = new ExpressionValue
            {
                UseJavascript = src.UseJavascript,
                Expression = src.Expression,
                PreExpression = src.PreExpression,
            };

            var data = new DashboardBindingData
            {
                Formula = working,
                Mode = string.IsNullOrWhiteSpace(working.Expression) ? BindingMode.None : BindingMode.Formula,
                TargetPropertyName = row.Name,
                TargetType = typeof(double),
            };

            try
            {
                var editor = new BindingEditor(engine) { DataContext = data };
                var result = await editor.ShowDialogWindowAsync(this);
                if ((int)result != 1) return; // not OK
                if (data.Mode == BindingMode.Formula)
                    row.ApplyEditedFormula(data.Formula?.Expression, data.Formula?.UseJavascript ?? false);
                else
                    row.ApplyEditedFormula("", false); // cleared
            }
            catch (Exception ex)
            {
                MozaLog.Warn("[AZOM] mBooster custom-effect formula editor failed: " + ex.Message);
            }
        }

        // PopulateMBoosterAxisRoles / OnMBoosterAxisRoleChanged / PopulateMBoosterEffectPedalCombo /
        // MBoosterEffectPedalCombo_SelectionChanged used to live here — all three retired in favor
        // of the single unified per-pedal MBoosterDeviceRow list built in RefreshMBoosterTab.

        /// <summary>
        /// Hide the vibration-effect cards when the selected pedal is passive
        /// (type 2 = no motor, e.g. a CRP2 — effects can't play there). When the
        /// device hasn't reported pedal types (0x0E diagnostic not received) the
        /// cards stay visible (best-effort). See MBoosterDeviceController.AxisTypes.
        ///
        /// The same gate hides the Pedal Feel controls that are real hardware
        /// writes on brake-named SINGLETON cmdIds — Travel (0x84/0x85), End Stop
        /// (0xB2), Natural Friction (0xAE) and Segmented Damping (0xB7). None of
        /// them carries a per-pedal selector, so editing them from a passive
        /// pedal's page didn't configure that pedal — it silently overwrote the
        /// ACTIVE pedal's registers (bundle KY3HK4QP: the passive throttle page's
        /// 3.8/35.9mm travel is what the brake unit committed as Params 48/49).
        /// Inferred from the wire shape rather than from a Pit House capture of a
        /// passive-pedal edit — see docs/protocol/devices/mbooster.md.
        /// Host-side-only controls (Deadzone, Max Force, the input curve) and the
        /// per-role output curve stay visible for every pedal.
        /// </summary>
        private void UpdateMBoosterEffectPassiveState()
        {
            var types = CurrentMBoosterController()?.AxisTypes;
            bool passive = types != null
                && _mboosterEffectPedalIndex >= 0 && _mboosterEffectPedalIndex < types.Length
                && types[_mboosterEffectPedalIndex] == 2;
            MBoosterEffectsCardsPanel.Visibility = passive ? Visibility.Collapsed : Visibility.Visible;
            MBoosterEffectsPassiveNote.Visibility = passive ? Visibility.Visible : Visibility.Collapsed;
            var hwVisibility = passive ? Visibility.Collapsed : Visibility.Visible;
            MBoosterTravelEndstopPanel.Visibility = hwVisibility;
            MBoosterNaturalFrictionPanel.Visibility = hwVisibility;
            MBoosterSegDampCard.Visibility = hwVisibility;
        }

        /// <summary>
        /// Show the load-cell-only Sim Input controls (Sensor Output Ratio + Max
        /// Threshold) only when the selected pedal is a BRAKE — a throttle/clutch
        /// has no pressure sensor. Pedal travel, endstop, the output curve and
        /// Pedal Feel all stay visible for every pedal mode.
        /// </summary>
        private void UpdateMBoosterConfigVisibilityForRole()
        {
            bool isBrake = MBoosterSelectedPedalRolePrefix() == "brake";
            MBoosterBrakeOnlyPanel.Visibility = isBrake ? Visibility.Visible : Visibility.Collapsed;
        }

        // Force every device-gated mBooster panel visible for the no-hardware
        // demo case (Show-all-tabs with nothing connected). Deliberately shows
        // the role-specific panels too (effects cards, brake-only Sim Input) so
        // the whole surface is demonstrable — the normal device-driven path
        // (RefreshMBoosterTab + the role/passive updaters) re-gates them the
        // moment a real device appears, so this never fights real functionality.
        private void ShowMBoosterDemoPanels()
        {
            MBoosterDevicePanel.Visibility = Visibility.Visible;
            MBoosterEffectsCardsPanel.Visibility = Visibility.Visible;
            MBoosterEffectsPassiveNote.Visibility = Visibility.Collapsed;
            MBoosterBrakeOnlyPanel.Visibility = Visibility.Visible;
            MBoosterTravelEndstopPanel.Visibility = Visibility.Visible;
            MBoosterNaturalFrictionPanel.Visibility = Visibility.Visible;
            MBoosterSegDampCard.Visibility = Visibility.Visible;

            // Seed every control to its default once. The curve editors take no
            // node data of their own — they two-way bind to the hidden data-store
            // sliders (BindEditorToSliders), so without a seed those sliders sit at
            // 0 and the editors draw a collapsed/garbage curve. A null target makes
            // both seeders fall back to MBoosterDefaultCurve (linear 20/40/60/80/100)
            // and sane per-control defaults; the seed writes go through _suppressor
            // so the slider ValueChanged handlers (which would no-op on the null
            // target anyway) stay quiet while the bindings still update the editors.
            if (_mboosterDemoSeeded) return;
            using (_suppressor.Begin())
            {
                SeedMBoosterConfigControls(null);
                SeedMBoosterEffectControls(null);
            }
            _mboosterDemoSeeded = true;
        }

        // ===== Effect handlers =====
        // All five effects (ABS, Engine, Road Texture, Lockup, Threshold)
        // have now been rebuilt with Enable + sustained Test toggles — see
        // docs/protocol/devices/mbooster.md "Effects card UI (mid-rebuild)"
        // for the history.

        private void MBoosterAbsEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Abs ??= new MBoosterEffectSettings()).Enabled = MBoosterAbsEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterAbsIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterAbsIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Abs ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // Fixed vibration frequency (5-30Hz) — replaces the old ABS-
        // activation-depth mapping (which SimHub's bool AbsActive collapsed
        // to a constant 30Hz anyway). See MBoosterEffectSettings.FrequencyHz
        // and MBoosterEffectWorker.UpdateAbsRequest.
        private void MBoosterAbsFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.AbsFreqMinHz, Math.Min((int)MBoosterUiConstants.AbsFreqMaxHz, (int)Math.Round(e.NewValue)));
            MBoosterAbsFrequencyValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Abs ??= new MBoosterEffectSettings()).FrequencyHz = v;
            _plugin.SaveSettings();
        }
        // Pulse modulation depth (0-100%) — 100 (default) is the exact
        // original verified waveform; 0 widens it to a sharper, choppier
        // full-swing pulse. See MBoosterEffectSettings.SmoothnessPct and
        // MBoosterEffectSynthesizer.SynthesizeAbs.
        private void MBoosterAbsSmoothness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterAbsSmoothnessValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Abs ??= new MBoosterEffectSettings()).SmoothnessPct = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — substitutes live brake position for
        // absActive (there's no live ABS-activation signal to press against
        // outside a real ABS event), vibrating continuously at the live
        // Frequency/Intensity/Smoothness slider values for as long as it's
        // on. See MBoosterDeviceController.SetAbsTestActive and
        // MBoosterEffectWorker's _absTestSustained.
        private void MBoosterAbsTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetAbsTestActive(MBoosterAbsTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterTcEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.TractionControl ??= new MBoosterEffectSettings()).Enabled = MBoosterTcEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterTcIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterTcIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.TractionControl ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // Fixed vibration frequency (10-100Hz). See MBoosterEffectSettings
        // .FrequencyHz and MBoosterEffectWorker.UpdateTractionControlRequest.
        private void MBoosterTcFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.TractionControlFreqMinHz, Math.Min((int)MBoosterUiConstants.TractionControlFreqMaxHz, (int)Math.Round(e.NewValue)));
            MBoosterTcFrequencyValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.TractionControl ??= new MBoosterEffectSettings()).FrequencyHz = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — substitutes live throttle position for
        // tcActive (there's no live TC-activation signal to press against
        // outside a real TC event), vibrating continuously at the live
        // Frequency/Intensity slider values for as long as it's on. See
        // MBoosterDeviceController.SetTcTestActive and
        // MBoosterEffectWorker's _tcTestSustained.
        private void MBoosterTcTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetTcTestActive(MBoosterTcTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterWheelSpinEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.WheelSpin ??= new MBoosterEffectSettings()).Enabled = MBoosterWheelSpinEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterWheelSpinIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterWheelSpinIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.WheelSpin ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // Fixed vibration frequency (10-100Hz) — same range as Traction
        // Control. See MBoosterEffectSettings.FrequencyHz and
        // MBoosterEffectWorker.UpdateWheelSpinRequest.
        private void MBoosterWheelSpinFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.WheelSpinFreqMinHz, Math.Min((int)MBoosterUiConstants.WheelSpinFreqMaxHz, (int)Math.Round(e.NewValue)));
            MBoosterWheelSpinFrequencyValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.WheelSpin ??= new MBoosterEffectSettings()).FrequencyHz = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — substitutes live throttle position for
        // the wheelspin heuristic (there's no live wheelspin signal to
        // press against outside a real spin event), vibrating continuously
        // at the live Frequency/Intensity slider values for as long as it's
        // on. See MBoosterDeviceController.SetWheelSpinTestActive and
        // MBoosterEffectWorker's _wheelSpinTestSustained.
        private void MBoosterWheelSpinTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetWheelSpinTestActive(MBoosterWheelSpinTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterGearShiftEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GearShift ??= new MBoosterEffectSettings()).Enabled = MBoosterGearShiftEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterGearShiftIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterGearShiftIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GearShift ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // Fixed vibration frequency (10-100Hz) — same range as Traction
        // Control/Wheel Spin. See MBoosterEffectSettings.FrequencyHz and
        // MBoosterEffectWorker.UpdateGearShiftRequest.
        private void MBoosterGearShiftFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.GearShiftFreqMinHz, Math.Min((int)MBoosterUiConstants.GearShiftFreqMaxHz, (int)Math.Round(e.NewValue)));
            MBoosterGearShiftFrequencyValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GearShift ??= new MBoosterEffectSettings()).FrequencyHz = v;
            _plugin.SaveSettings();
        }
        // Whether a shift landing in Neutral still fires the pulse — off by
        // default (an H-pattern shift produces two transitions, e.g.
        // "1"->"N"->"2", and the engagement bump into the new gear is
        // normally what's wanted). Same knob/rationale as the wheelbase's
        // own GearshiftVibrateOnNeutralCheck.
        private void MBoosterGearShiftVibrateOnNeutralCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GearShift ??= new MBoosterEffectSettings()).VibrateOnNeutral = MBoosterGearShiftVibrateOnNeutralCheck.IsChecked == true;
            _plugin.SaveSettings();
        }
        // Minimum time (ms) between fired pulses — absorbs an H-pattern's
        // double transition (gear->N->gear) so one physical shift doesn't
        // fire twice. Same range/step/default as the wheelbase's own
        // GearshiftDebounceSlider.
        private void MBoosterGearShiftDebounceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            // Snap to 50 ms grid (IsSnapToTickEnabled + TickFrequency=50
            // already enforces this on user input, but be defensive against
            // external sources that bypass the tick grid).
            v = ((v + 25) / 50) * 50;
            v = Math.Max((int)MBoosterUiConstants.GearShiftDebounceMinMs, Math.Min((int)MBoosterUiConstants.GearShiftDebounceMaxMs, v));
            MBoosterGearShiftDebounceValue.Text = $"{v} ms";
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GearShift ??= new MBoosterEffectSettings()).DebounceMs = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — bypasses the real pulse/debounce/neutral
        // machinery entirely, vibrating continuously at the live Frequency/
        // Intensity slider values for as long as it's on (there's no live
        // "gear just changed" signal to press against outside a real
        // shift). See MBoosterDeviceController.SetGearShiftTestActive and
        // MBoosterEffectWorker's _gearShiftTestSustained.
        private void MBoosterGearShiftTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetGearShiftTestActive(MBoosterGearShiftTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterLockupEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Lockup ??= new MBoosterEffectSettings()).Enabled = MBoosterLockupEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        // Fixed vibration frequency (10-100Hz) — replaces the old brake-
        // position mapping. See MBoosterEffectSettings.FrequencyHz and
        // MBoosterEffectWorker.UpdateLockupRequest.
        private void MBoosterLockupFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.LockupFreqMinHz, Math.Min((int)MBoosterUiConstants.LockupFreqMaxHz, (int)Math.Round(e.NewValue)));
            MBoosterLockupFrequencyValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Lockup ??= new MBoosterEffectSettings()).FrequencyHz = v;
            _plugin.SaveSettings();
        }
        private void MBoosterLockupIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterLockupIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Lockup ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — bypasses the wheel-slip detection
        // heuristic entirely, substituting live brake position for it
        // (there's no live "is the wheel actually locking" signal to
        // preview against outside a real drive), vibrating continuously at
        // the live Frequency/Intensity slider values for as long as it's
        // on. See MBoosterDeviceController.SetLockupTestActive and
        // MBoosterEffectWorker's _lockupTestSustained.
        private void MBoosterLockupTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetLockupTestActive(MBoosterLockupTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterThresholdEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Threshold ??= new MBoosterEffectSettings()).Enabled = MBoosterThresholdEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        // Brake position (%) at which the rising-edge trigger fires. The
        // release threshold stays a fixed 30 points below this. See
        // MBoosterEffectSettings.TriggerLevelPct and
        // MBoosterEffectWorker.UpdateThresholdRequest.
        private void MBoosterThresholdTriggerLevel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.ThresholdTriggerMinPct, Math.Min((int)MBoosterUiConstants.ThresholdTriggerMaxPct, (int)Math.Round(e.NewValue)));
            MBoosterThresholdTriggerLevelValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Threshold ??= new MBoosterEffectSettings()).TriggerLevelPct = v;
            _plugin.SaveSettings();
        }
        // Fixed vibration frequency (5-100Hz) — replaces the old brake-
        // position mapping. See MBoosterEffectSettings.FrequencyHz and
        // MBoosterEffectWorker.UpdateThresholdRequest.
        private void MBoosterThresholdFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.ThresholdFreqMinHz, Math.Min((int)MBoosterUiConstants.ThresholdFreqMaxHz, (int)Math.Round(e.NewValue)));
            MBoosterThresholdFrequencyValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Threshold ??= new MBoosterEffectSettings()).FrequencyHz = v;
            _plugin.SaveSettings();
        }
        private void MBoosterThresholdIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterThresholdIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Threshold ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // How much the pulse fades after its initial burst (0 = barely
        // decays, 100 = drops to silence immediately). See
        // MBoosterEffectSettings.DecayPct and
        // MBoosterEffectSynthesizer.SynthesizeThreshold.
        private void MBoosterThresholdDecay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterThresholdDecayValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Threshold ??= new MBoosterEffectSettings()).DecayPct = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — skips the rising-edge hysteresis entirely,
        // substituting live brake position for it, vibrating continuously
        // at the live Frequency/Intensity/Decay slider values for as long
        // as it's on. See MBoosterDeviceController.SetThresholdTestActive
        // and MBoosterEffectWorker's _thresholdTestSustained.
        private void MBoosterThresholdTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetThresholdTestActive(MBoosterThresholdTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterEngineEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Engine ??= new MBoosterEffectSettings()).Enabled = MBoosterEngineEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterEngineIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterEngineIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Engine ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — unlike the other effects' fire-and-forget
        // 1s Test button, this vibrates continuously at the live Frequency/
        // Intensity slider values (both tracked in real time, not a
        // snapshot) for as long as it's on. See
        // MBoosterDeviceController.SetEngineTestActive and
        // MBoosterEffectWorker's _engineTestSustained.
        private void MBoosterEngineTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetEngineTestActive(MBoosterEngineTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterRoadTextureEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.RoadTexture ??= new MBoosterEffectSettings()).Enabled = MBoosterRoadTextureEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        // Both Intensity and Smoothness are sent to the device as raw
        // percentages — the firmware applies them to the streamed noise
        // signal internally (confirmed from capture: neither affects the
        // noise's shape as transmitted). See
        // MozaMBoosterProtocol.EncodeRoadTextureLevel and
        // MBoosterEffectWorker.ProcessRoadTextureEffect.
        private void MBoosterRoadTextureIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterRoadTextureIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.RoadTexture ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        private void MBoosterRoadTextureSmoothness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterRoadTextureSmoothnessValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.RoadTexture ??= new MBoosterEffectSettings()).SmoothnessPct = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — bypasses Enabled and the game-running/
        // speed gate entirely (there's no live "how rough is the road"
        // signal to preview against outside a real drive), running
        // continuously at the live Intensity/Smoothness slider values. See
        // MBoosterDeviceController.SetRoadTextureTestActive and
        // MBoosterEffectWorker's _roadTextureTestSustained.
        private void MBoosterRoadTextureTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetRoadTextureTestActive(MBoosterRoadTextureTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        // Brake Fade — NOT a vibration effect. Dynamically rewrites the real
        // Travel End AND Max Threshold hardware calibrations in lockstep
        // while brake temp is above BrakeFadeOnsetC (more travel AND more
        // force needed to reach 100%), restoring the user's own configured
        // values as it cools. See MBoosterEffectWorker.UpdateBrakeFade.
        private void MBoosterBrakeFadeEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            (s.BrakeFade ??= new MBoosterEffectSettings()).Enabled = MBoosterBrakeFadeEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        // Brake temperature (°C) above which Travel End and Max Threshold
        // start ramping — see MBoosterEffectSettings.BrakeFadeOnsetC.
        private void MBoosterBrakeFadeOnsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.BrakeFadeOnsetMinC, Math.Min((int)MBoosterUiConstants.BrakeFadeOnsetMaxC, (int)Math.Round(e.NewValue)));
            MBoosterBrakeFadeOnsetValue.Text = v.ToString();
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            (s.BrakeFade ??= new MBoosterEffectSettings()).BrakeFadeOnsetC = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — bypasses Enabled and the brake-temperature
        // gate entirely (there's no live "how hot are the brakes" signal to
        // preview against outside a real drive), forcing Travel End and Max
        // Threshold to their Brake Fade caps for as long as it's on. Each
        // independently requires its own configured base value — otherwise
        // that one is a no-op (the other can still preview on its own). See
        // MBoosterDeviceController.SetBrakeFadeTestActive and
        // MBoosterEffectWorker's _brakeFadeTestActive.
        private void MBoosterBrakeFadeTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetBrakeFadeTestActive(MBoosterBrakeFadeTestToggle.IsChecked == true);
        }

        private void MBoosterGForceEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GForce ??= new MBoosterEffectSettings()).Enabled = MBoosterGForceEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        // 0-15mm, half-mm steps (matches Pit House's own "Max Pedal
        // Travelment" slider) — see MBoosterEffectSettings.MaxTravelMm.
        private void MBoosterGForceMaxTravel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            double v = Math.Round(e.NewValue * 2) / 2.0;
            v = Math.Max(MBoosterUiConstants.GForceMaxTravelMinMm, Math.Min(MBoosterUiConstants.GForceMaxTravelMaxMm, v));
            MBoosterGForceMaxTravelValue.Text = v.ToString("0.#");
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GForce ??= new MBoosterEffectSettings()).MaxTravelMm = (float)v;
            _plugin.SaveSettings();
        }
        // 0-100% — sent to the firmware unshaped every frame (it does the
        // actual ramping, not the plugin) — see
        // MBoosterEffectSettings.ResponseSpeedPct.
        private void MBoosterGForceResponseSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterGForceResponseSpeedValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GForce ??= new MBoosterEffectSettings()).ResponseSpeedPct = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — alternates the commanded travel offset
        // forward/backward, mirroring Pit House's own "Test" demo (bypasses
        // Enabled and the game-running gate). See
        // MBoosterDeviceController.SetGForceTestActive.
        private void MBoosterGForceTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetGForceTestActive(MBoosterGForceTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

    }
}
