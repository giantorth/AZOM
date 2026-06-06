using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using static MozaPlugin.UI.UiHelpers;

namespace MozaPlugin.Devices
{
    // Wheel-input controls (paddles · joystick) that used to live in the
    // plugin-pane WheelTab. The plugin pane keeps its controls hidden so its
    // existing handlers don't fire; this file owns the per-device behaviour.
    //
    // Per-knob rotary signal-mode rows belong on the Knobs sub-tab (since they
    // describe knobs) — also driven from here so all knob/encoder UI ships
    // through a single refresh path.
    //
    // No forwarding to SettingsControl.Instance: all reads come from `_data`,
    // all writes go to `_plugin.WriteIfWheelDetected` + `_plugin.SaveSettings`
    // and update `_plugin.UpdateActiveWheelOverlay`. Same path the plugin-pane
    // handlers use, no logic duplication risk because plugin-pane WheelTab is
    // hidden and its handlers no longer fire.
    public partial class MozaWheelSettingsControl
    {
        internal static MozaWheelSettingsControl? Instance { get; private set; }

        // 33Hz timer just for live paddle + button display — too fast for the
        // 500ms RefreshWheel tick, separate from it so HID input feels live
        // even when no telemetry is streaming.
        private DispatcherTimer? _inputsLiveTimer;
        private readonly DateTime[] _wiButtonLastPressed = new DateTime[MozaData.MaxButtons];

        private void EnsureInputsLiveTimer()
        {
            if (_inputsLiveTimer != null) return;
            _inputsLiveTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _inputsLiveTimer.Tick += (_, __) => RefreshInputsLive();
            _inputsLiveTimer.Start();
        }

        private void StopInputsLiveTimer()
        {
            if (_inputsLiveTimer == null) return;
            _inputsLiveTimer.Stop();
            _inputsLiveTimer = null;
        }

        // ── Live paddle bars + active buttons ──────────────────────────

        internal void PushInputsLiveData(MozaData data) => RefreshInputsLive();

        private void RefreshInputsLive()
        {
            if (_data == null || WiLeftPaddleBar == null) return;
            bool connected = _plugin?.HidReader != null && _data.IsHidConnected;
            if (!connected)
            {
                WiLeftPaddleBar.Value = 0;
                WiRightPaddleBar.Value = 0;
                WiCombinedPaddleBar.Value = 0;
                if (WiActiveButtonsText.Inlines.Count != 1 ||
                    !(WiActiveButtonsText.Inlines.FirstInline is Run r0 && r0.Text == "None"))
                {
                    WiActiveButtonsText.Inlines.Clear();
                    WiActiveButtonsText.Inlines.Add(new Run("None"));
                }
                return;
            }
            WiLeftPaddleBar.Value     = _data.LeftPaddlePosition;
            WiRightPaddleBar.Value    = _data.RightPaddlePosition;
            WiCombinedPaddleBar.Value = _data.CombinedPaddlePosition;
            RefreshActiveButtons();
        }

        private void RefreshActiveButtons()
        {
            if (_data == null || _data.ButtonCount == 0)
            {
                WiActiveButtonsText.Inlines.Clear();
                WiActiveButtonsText.Inlines.Add(new Run("None"));
                return;
            }
            var now = DateTime.UtcNow;
            var inlines = new List<Inline>();
            int count = _data.ButtonCount;
            for (int i = 0; i < count; i++)
            {
                bool pressed = _data.ButtonStates[i];
                if (pressed) _wiButtonLastPressed[i] = now;
                if ((now - _wiButtonLastPressed[i]).TotalSeconds < 1.0)
                {
                    if (inlines.Count > 0) inlines.Add(new Run(", "));
                    var run = new Run((i + 1).ToString());
                    if (pressed)
                    {
                        run.FontWeight = FontWeights.Bold;
                        run.Foreground = Brushes.White;
                    }
                    inlines.Add(run);
                }
            }
            WiActiveButtonsText.Inlines.Clear();
            if (inlines.Count > 0) foreach (var inline in inlines) WiActiveButtonsText.Inlines.Add(inline);
            else WiActiveButtonsText.Inlines.Add(new Run("None"));
        }

        // 0=Buttons → no live bars visible
        // 1=Combined → single combined bar + Clutch Split Point slider visible
        // 2=Split → left + right bars visible
        private void ApplyPaddleVisibility(int mode)
        {
            bool buttons = mode == 0;
            bool combined = mode == 1;
            WiSplitPaddlePanel.Visibility    = !buttons && !combined ? Visibility.Visible : Visibility.Collapsed;
            WiCombinedPaddlePanel.Visibility = combined ? Visibility.Visible : Visibility.Collapsed;
            WiClutchPointPanel.Visibility    = combined ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Paddle + joystick + knob signal-mode handlers ──────────────

        private void WiPaddlesModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int val = WiPaddlesModeCombo.SelectedIndex;
            _data.WheelPaddlesMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelPaddlesMode = val);
            ApplyPaddleVisibility(val);
            _plugin.WriteIfWheelDetected("wheel-paddles-mode", val + 1);
            _plugin.SaveSettings();
        }

        private void WiClutchPointSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int val = (int)Math.Round(e.NewValue);
            WiClutchPointValue.Text = $"{val}%";
            _data.WheelClutchPoint = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelClutchPoint = val);
            _plugin.WriteIfWheelDetected("wheel-clutch-point", val);
            _plugin.SaveSettings();
        }

        private void WiKnobModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int val = WiKnobModeCombo.SelectedIndex;
            _data.WheelKnobMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelKnobMode = val);
            _plugin.WriteIfWheelDetected("wheel-knob-mode", val);
            _plugin.SaveSettings();
        }

        private void WiKnobSignalMode0Combo_Changed(object sender, SelectionChangedEventArgs e) => WriteWiKnobSignalMode(0, WiKnobSignalMode0Combo.SelectedIndex);
        private void WiKnobSignalMode1Combo_Changed(object sender, SelectionChangedEventArgs e) => WriteWiKnobSignalMode(1, WiKnobSignalMode1Combo.SelectedIndex);
        private void WiKnobSignalMode2Combo_Changed(object sender, SelectionChangedEventArgs e) => WriteWiKnobSignalMode(2, WiKnobSignalMode2Combo.SelectedIndex);
        private void WiKnobSignalMode3Combo_Changed(object sender, SelectionChangedEventArgs e) => WriteWiKnobSignalMode(3, WiKnobSignalMode3Combo.SelectedIndex);
        private void WiKnobSignalMode4Combo_Changed(object sender, SelectionChangedEventArgs e) => WriteWiKnobSignalMode(4, WiKnobSignalMode4Combo.SelectedIndex);

        private void WriteWiKnobSignalMode(int index, int value)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            if (value < 0) return; // ComboBox SelectionChanged can fire during refresh
            _data.WheelKnobSignalModes[index] = value;
            _plugin.UpdateActiveWheelOverlay(o =>
                o.WheelKnobSignalModes = (int[])_data.WheelKnobSignalModes.Clone());
            _plugin.WriteIfWheelDetected($"wheel-knob-signal-mode{index}", value);
            _plugin.SaveSettings();
        }

        private void WiStickModeCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int val = WiStickModeCheck.IsChecked == true ? 1 : 0;
            _data.WheelStickMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelStickMode = val);
            _plugin.WriteIfWheelDetected("wheel-stick-mode", val * 256);
            _plugin.SaveSettings();
        }

        private void WiStickModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int val = WiStickModeCombo.SelectedIndex;
            _data.WheelStickMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelStickMode = val);
            _plugin.WriteIfWheelDetected("wheel-stick-mode-new", val);
            _plugin.SaveSettings();
        }

        // ── Refresh: pulls _data state into Inputs + Knobs UI ──────────

        private void RefreshInputsAndKnobsSignalMode(bool newWheelDetected)
        {
            if (_data == null) return;
            using (_suppressor.Begin())
            {
                if (newWheelDetected)
                {
                    SetComboSafe(WiPaddlesModeCombo, _data.WheelPaddlesMode);
                    ApplyPaddleVisibility(_data.WheelPaddlesMode);
                    WiClutchPointSlider.Value = Math.Max(0, Math.Min(100, _data.WheelClutchPoint));
                    WiClutchPointValue.Text   = $"{_data.WheelClutchPoint}%";

                    bool perKnob = _data.WheelKnobSignalModeSupported;
                    // Legacy "All Rotaries" panel now lives inside KNOB COLOURS card;
                    // visible only when firmware does NOT support per-knob signal mode.
                    WiKnobModeLegacyPanel.Visibility = perKnob ? Visibility.Collapsed : Visibility.Visible;
                    if (perKnob)
                    {
                        // Per-knob mode: keep the hidden source-of-truth combos in sync;
                        // the visible chips above each KnobRingViz forward to them.
                        var combos = new[] { WiKnobSignalMode0Combo, WiKnobSignalMode1Combo, WiKnobSignalMode2Combo, WiKnobSignalMode3Combo, WiKnobSignalMode4Combo };
                        for (int i = 0; i < 5; i++)
                        {
                            int v = _data.WheelKnobSignalModes[i];
                            if (v >= 0) SetComboSafe(combos[i], v);
                        }
                    }
                    else
                    {
                        SetComboSafe(WiKnobModeCombo, _data.WheelKnobMode);
                    }
                    SyncKnobSignalChips();

                    if (_data.WheelDualStickSupported)
                    {
                        WiStickModeNewPanel.Visibility = Visibility.Visible;
                        WiStickModeOldPanel.Visibility = Visibility.Collapsed;
                        WiStickModeNotDetected.Visibility = Visibility.Collapsed;
                        SetComboSafe(WiStickModeCombo, _data.WheelStickMode);
                    }
                    else
                    {
                        WiStickModeOldPanel.Visibility = Visibility.Visible;
                        WiStickModeNewPanel.Visibility = Visibility.Collapsed;
                        WiStickModeNotDetected.Visibility = Visibility.Collapsed;
                        WiStickModeCheck.IsChecked = _data.WheelStickMode != 0;
                    }
                }
                else
                {
                    // No new-protocol wheel detected → show the not-detected hint
                    // in the Joystick card; collapse both control panels.
                    WiStickModeOldPanel.Visibility = Visibility.Collapsed;
                    WiStickModeNewPanel.Visibility = Visibility.Collapsed;
                    WiStickModeNotDetected.Visibility = Visibility.Visible;
                }
            }
        }
    }
}
