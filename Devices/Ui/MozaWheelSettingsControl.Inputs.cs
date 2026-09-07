using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using static MozaPlugin.UI.UiHelpers;

namespace MozaPlugin.Devices.Ui
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

        // Fed by the plugin pane's own 30 Hz HID timer; while this page's live
        // timer is running that would be a second 30 Hz driver of the same text.
        internal void PushInputsLiveData(MozaData data)
        {
            if (_inputsLiveTimer != null) return;
            RefreshInputsLive();
        }

        // Signature of the last rendered button list ("3,7*," — star = pressed) so
        // the Inlines are only rebuilt when the visible content changes.
        private string _wiActiveButtonsSig = "";

        private void RefreshInputsLive()
        {
            if (_data == null || WiLeftPaddleBar == null) return;
            bool connected = _plugin?.HidReader != null && _data.IsHidConnected;
            if (!connected)
            {
                WiLeftPaddleBar.Value = 0;
                WiRightPaddleBar.Value = 0;
                WiCombinedPaddleBar.Value = 0;
                if (_wiActiveButtonsSig != "none")
                {
                    _wiActiveButtonsSig = "none";
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
                if (_wiActiveButtonsSig != "none")
                {
                    _wiActiveButtonsSig = "none";
                    WiActiveButtonsText.Inlines.Clear();
                    WiActiveButtonsText.Inlines.Add(new Run("None"));
                }
                return;
            }
            var now = DateTime.UtcNow;
            int count = _data.ButtonCount;
            var sig = new System.Text.StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                bool pressed = _data.ButtonStates[i];
                if (pressed) _wiButtonLastPressed[i] = now;
                if ((now - _wiButtonLastPressed[i]).TotalSeconds < 1.0)
                {
                    sig.Append(i + 1);
                    if (pressed) sig.Append('*');
                    sig.Append(',');
                }
            }
            string signature = sig.Length == 0 ? "none" : sig.ToString();
            if (signature == _wiActiveButtonsSig) return;   // 30 Hz caller; only rebuild on change
            _wiActiveButtonsSig = signature;

            WiActiveButtonsText.Inlines.Clear();
            if (signature == "none")
            {
                WiActiveButtonsText.Inlines.Add(new Run("None"));
                return;
            }
            bool first = true;
            for (int i = 0; i < count; i++)
            {
                if ((now - _wiButtonLastPressed[i]).TotalSeconds >= 1.0) continue;
                if (!first) WiActiveButtonsText.Inlines.Add(new Run(", "));
                first = false;
                var run = new Run((i + 1).ToString());
                if (_data.ButtonStates[i])
                {
                    run.FontWeight = FontWeights.Bold;
                    run.Foreground = Brushes.White;
                }
                WiActiveButtonsText.Inlines.Add(run);
            }
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
            _plugin.HardwareApplier.WriteIfWheelDetected("wheel-paddles-mode", val + 1);
            _plugin.SaveSettings();
        }

        private DispatcherTimer? _paddleCalTimer;
        private int _paddleCalRemaining;
        private const int PaddleCalSeconds = 5;

        // Clutch-paddle calibration (no profile state): clicking Start sends
        // 08 01, instructs the user to sweep + release all paddles with a live
        // countdown, then auto-sends 08 02 (save) after 5 s. See wheel-0x17.md.
        private void WiPaddleCalStartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.HardwareApplier.WriteIfWheelDetected("wheel-paddles-calibration", 1);  // start

            WiPaddleCalStartButton.IsEnabled = false;
            _paddleCalRemaining = PaddleCalSeconds;
            WiPaddleCalStatus.Text = string.Format(
                global::MozaPlugin.Resources.Strings.Hint_PaddleCalibrate, _paddleCalRemaining);
            WiPaddleCalStatus.Visibility = Visibility.Visible;

            _paddleCalTimer?.Stop();
            _paddleCalTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _paddleCalTimer.Tick += (s, ev) =>
            {
                _paddleCalRemaining--;
                if (_paddleCalRemaining > 0)
                {
                    WiPaddleCalStatus.Text = string.Format(
                        global::MozaPlugin.Resources.Strings.Hint_PaddleCalibrate, _paddleCalRemaining);
                    return;
                }
                _paddleCalTimer?.Stop();
                _paddleCalTimer = null;
                _plugin?.HardwareApplier.WriteIfWheelDetected("wheel-paddles-calibration", 2);  // save
                WiPaddleCalStatus.Text = global::MozaPlugin.Resources.Strings.Hint_PaddleCalibrateDone;
                WiPaddleCalStartButton.IsEnabled = true;
            };
            _paddleCalTimer.Start();
        }

        private void WiClutchPointSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int val = (int)Math.Round(e.NewValue);
            WiClutchPointValue.Text = $"{val}%";
            _data.WheelClutchPoint = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelClutchPoint = val);
            _plugin.HardwareApplier.WriteIfWheelDetected("wheel-clutch-point", val);
            _plugin.SaveSettings();
        }

        private void WiKnobModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int val = WiKnobModeCombo.SelectedIndex;
            _data.WheelKnobMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelKnobMode = val);
            _plugin.HardwareApplier.WriteIfWheelDetected("wheel-knob-mode", val);
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
            // index is the logical knob (LED/UI order); the wire command addresses
            // the firmware signal-mode index, which differs on the KS Pro.
            int fwIndex = _plugin.WheelModelInfo?.SignalModeFirmwareIndex(index) ?? index;
            _plugin.HardwareApplier.WriteIfWheelDetected($"wheel-knob-signal-mode{fwIndex}", value);
            _plugin.SaveSettings();
        }

        // Old-protocol stick mode (ES + old-firmware new wheels): 0=Buttons, 1=D-Pad.
        // Wire value is the 2-byte form 0x0100 for D-Pad, 0 for Buttons.
        private void WiStickModeOldCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int val = WiStickModeOldCombo.SelectedIndex;
            if (val < 0) return; // fires during refresh
            _data.WheelStickMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelStickMode = val);
            _plugin.HardwareApplier.WriteIfWheelDetected("wheel-stick-mode", val * 256);
            _plugin.SaveSettings();
        }

        private void WiStickModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int val = WiStickModeCombo.SelectedIndex;
            _data.WheelStickMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelStickMode = val);
            _plugin.HardwareApplier.WriteIfWheelDetected("wheel-stick-mode-new", val);
            _plugin.SaveSettings();
        }

        // ── Refresh: pulls _data state into Inputs + Knobs UI ──────────

        private void RefreshInputsAndKnobsSignalMode(bool newWheelDetected, bool oldWheelDetected)
        {
            if (_data == null) return;
            using (_suppressor.Begin())
            {
                // Analog-paddle indicators + paddle settings only apply to
                // new-protocol wheels. The ES (old protocol) has no analog
                // paddles — hide those cards and let Active Buttons span the row.
                if (WiLivePaddlesCard != null)
                {
                    bool esWheel = oldWheelDetected;
                    WiLivePaddlesCard.Visibility    = esWheel ? Visibility.Collapsed : Visibility.Visible;
                    WiPaddleSettingsCard.Visibility = esWheel ? Visibility.Collapsed : Visibility.Visible;
                    Grid.SetColumn(WiActiveButtonsCard, esWheel ? 0 : 1);
                    Grid.SetColumnSpan(WiActiveButtonsCard, esWheel ? 2 : 1);
                    WiActiveButtonsCard.Margin = esWheel
                        ? new Thickness(0) : new Thickness(7, 0, 0, 0);
                    // ES RPM indicator mode lives on this main page (the ES has no tabs).
                    EsIndicatorCard.Visibility = esWheel ? Visibility.Visible : Visibility.Collapsed;
                }

                // Input modes render overlay-first, _data as fallback — same rule the
                // LED swatches and the sleep bundle already follow. Newer firmware
                // silently drops the wheel-paddles-mode / wheel-clutch-point readback
                // (see WheelOverride's "Inputs" comment), and _data is not per-wheel:
                // ClearWheelIdentity leaves these mirrors alone, so after a rim swap
                // _data still holds the OTHER rim's mode. The overlay is keyed by the
                // wheel's page GUID, so it is the only per-wheel truth available.
                var ov = _plugin?.GetCurrentWheelOverlay(_plugin.Settings?.ProfileStore?.CurrentProfile);

                if (newWheelDetected)
                {
                    int paddlesMode = ov != null && ov.WheelPaddlesMode >= 0
                        ? ov.WheelPaddlesMode : _data.WheelPaddlesMode;
                    int clutchPoint = ov != null && ov.WheelClutchPoint >= 0
                        ? ov.WheelClutchPoint : _data.WheelClutchPoint;
                    SetComboSafe(WiPaddlesModeCombo, paddlesMode);
                    ApplyPaddleVisibility(paddlesMode);
                    WiClutchPointSlider.Value = Math.Max(0, Math.Min(100, clutchPoint));
                    WiClutchPointValue.Text   = $"{clutchPoint}%";

                    bool perKnob = _data.WheelKnobSignalModeSupported;
                    // Legacy "All Rotaries" panel lives in the knob settings card;
                    // visible only when the wheel HAS encoders but does NOT support
                    // per-knob signal mode. The encoder check is load-bearing now that
                    // the card is no longer hidden wholesale on knob-LED-less rims —
                    // without it, a wheel that answered neither read would still show
                    // a selector that writes nowhere.
                    WiKnobModeLegacyPanel.Visibility = (!perKnob && HasKnobEncoders())
                        ? Visibility.Visible : Visibility.Collapsed;
                    if (perKnob)
                    {
                        // Per-knob mode: keep the hidden source-of-truth combos in sync;
                        // the visible chips above each KnobRingViz forward to them.
                        var combos = new[] { WiKnobSignalMode0Combo, WiKnobSignalMode1Combo, WiKnobSignalMode2Combo, WiKnobSignalMode3Combo, WiKnobSignalMode4Combo };
                        var ovSig = ov?.WheelKnobSignalModes;
                        for (int i = 0; i < 5; i++)
                        {
                            int v = ovSig != null && i < ovSig.Length && ovSig[i] >= 0
                                ? ovSig[i] : _data.WheelKnobSignalModes[i];
                            if (v >= 0) SetComboSafe(combos[i], v);
                        }
                    }
                    else
                    {
                        SetComboSafe(WiKnobModeCombo, ov != null && ov.WheelKnobMode >= 0
                            ? ov.WheelKnobMode : _data.WheelKnobMode);
                    }
                    SyncKnobSignalChips();
                }

                // Left stick mode — shown for any detected wheel. The ES + old
                // firmware report the 2-byte form (Buttons/D-Pad segmented); newer
                // firmware reports the 1-byte assignment enum (None/Left/Right).
                if (newWheelDetected || oldWheelDetected)
                {
                    int stickMode = ov != null && ov.WheelStickMode >= 0
                        ? ov.WheelStickMode : _data.WheelStickMode;
                    if (_data.WheelDualStickSupported)
                    {
                        WiStickModeNewPanel.Visibility = Visibility.Visible;
                        WiStickModeOldPanel.Visibility = Visibility.Collapsed;
                        WiStickModeNotDetected.Visibility = Visibility.Collapsed;
                        SetComboSafe(WiStickModeCombo, stickMode);
                    }
                    else
                    {
                        WiStickModeOldPanel.Visibility = Visibility.Visible;
                        WiStickModeNewPanel.Visibility = Visibility.Collapsed;
                        WiStickModeNotDetected.Visibility = Visibility.Collapsed;
                        SetComboSafe(WiStickModeOldCombo, stickMode != 0 ? 1 : 0);
                    }
                }
                else
                {
                    // No wheel detected → show the not-detected hint in the
                    // Joystick card; collapse both control panels.
                    WiStickModeOldPanel.Visibility = Visibility.Collapsed;
                    WiStickModeNewPanel.Visibility = Visibility.Collapsed;
                    WiStickModeNotDetected.Visibility = Visibility.Visible;
                }
            }
        }
    }
}
