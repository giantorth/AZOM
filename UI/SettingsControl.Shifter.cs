using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MozaPlugin.Resources;

namespace MozaPlugin
{
    /// <summary>
    /// Settings tabs for the passive HGP (H-pattern) and SGP (sequential) shifters,
    /// each gated on positive detection of its own device (<see cref="MozaPlugin.IsHgpShifterDetected"/> /
    /// <see cref="MozaPlugin.IsSgpShifterDetected"/>). Both expose reverse-direction +
    /// paddle-sync; the SGP additionally has 2 configurable LEDs (fixed 8-colour palette,
    /// index 0-7) + brightness; the HGP has an H-pattern calibration routine. Config is
    /// serial-only — gear input stays HID-sourced. Only one shifter is ever connected (a
    /// single 0x1A slot / single ShifterOwner), so both tabs write the same shifter-* commands.
    /// </summary>
    public partial class SettingsControl
    {
        // SGP LED palette: wire index 0-7 -> swatch RGB. Names are localized (see
        // EnsureShifterCombos). Matches PitHouse / foxblat (data/style.css .c0-.c7).
        private static readonly (byte R, byte G, byte B)[] ShifterPaletteRgb =
        {
            (0xcf, 0x27, 0x27), // 0 red
            (0xdf, 0xa5, 0x00), // 1 orange
            (0xdf, 0xdf, 0x3a), // 2 yellow
            (0x3a, 0x90, 0x3a), // 3 green
            (0x00, 0xd0, 0xd0), // 4 cyan
            (0x3a, 0x3a, 0xff), // 5 blue
            (0x80, 0x20, 0x80), // 6 purple
            (0xdd, 0xdd, 0xdd), // 7 white
        };
        private bool _shifterCombosBuilt;

        private void RefreshHgpTab()
        {
            bool detected = _plugin.IsHgpShifterDetected;
            HgpTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;
            if (!detected) return;

            using (_suppressor.Begin())
            {
                HgpDirectionCheck.IsChecked = _data?.ShifterDirection == 1;
                // Paddle-sync wire range is {1,2}: 2 = enabled, 1 = disabled.
                HgpPaddleSyncCheck.IsChecked = _data?.ShifterPaddleSync == 2;
            }
        }

        private void RefreshSgpTab()
        {
            bool detected = _plugin.IsSgpShifterDetected;
            SgpTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;
            if (!detected) return;

            EnsureShifterCombos();

            using (_suppressor.Begin())
            {
                SgpDirectionCheck.IsChecked = _data?.ShifterDirection == 1;
                SgpPaddleSyncCheck.IsChecked = _data?.ShifterPaddleSync == 2;

                if (_data != null)
                {
                    if (_data.ShifterLed1Index >= 0 && _data.ShifterLed1Index < ShifterPaletteRgb.Length)
                        SgpLed1Combo.SelectedIndex = _data.ShifterLed1Index;
                    if (_data.ShifterLed2Index >= 0 && _data.ShifterLed2Index < ShifterPaletteRgb.Length)
                        SgpLed2Combo.SelectedIndex = _data.ShifterLed2Index;
                    if (_data.ShifterBrightness >= 0)
                    {
                        SgpBrightnessSlider.Value = _data.ShifterBrightness;
                        SgpBrightnessValue.Text = _data.ShifterBrightness.ToString();
                    }
                }
            }
        }

        private void EnsureShifterCombos()
        {
            if (_shifterCombosBuilt) return;
            _shifterCombosBuilt = true;
            var names = new[]
            {
                Strings.ShifterColor_Red, Strings.ShifterColor_Orange, Strings.ShifterColor_Yellow,
                Strings.ShifterColor_Green, Strings.ShifterColor_Cyan, Strings.ShifterColor_Blue,
                Strings.ShifterColor_Purple, Strings.ShifterColor_White,
            };
            PopulateShifterCombo(SgpLed1Combo, names);
            PopulateShifterCombo(SgpLed2Combo, names);
        }

        private static void PopulateShifterCombo(ComboBox combo, string[] names)
        {
            combo.Items.Clear();
            for (int i = 0; i < ShifterPaletteRgb.Length; i++)
            {
                var (r, g, b) = ShifterPaletteRgb[i];
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Rectangle
                {
                    Width = 14,
                    Height = 14,
                    Margin = new Thickness(0, 0, 8, 0),
                    Fill = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    Stroke = Brushes.Gray,
                    StrokeThickness = 0.5,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                sp.Children.Add(new TextBlock { Text = names[i], VerticalAlignment = VerticalAlignment.Center });
                combo.Items.Add(new ComboBoxItem { Content = sp });
            }
        }

        // Handlers follow the handbrake/pedals convention: set _data, write to the
        // device, save. Persistence to the profile is via MozaProfile.CaptureFromCurrent
        // (shifter fields are device-read + only read on connect, so no drift). The two
        // tabs' direction/paddle-sync controls target the same shifter-* commands, so
        // each handler routes through a shared setter.
        private void HgpDirectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            SetShifterDirection(HgpDirectionCheck.IsChecked == true);
        }

        private void SgpDirectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            SetShifterDirection(SgpDirectionCheck.IsChecked == true);
        }

        private void SetShifterDirection(bool on)
        {
            int v = on ? 1 : 0;
            if (_data != null) _data.ShifterDirection = v;
            _plugin.WriteIfShifterDetected("shifter-direction", v);
            _plugin.SaveSettings();
        }

        private void HgpPaddleSyncCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            SetShifterPaddleSync(HgpPaddleSyncCheck.IsChecked == true);
        }

        private void SgpPaddleSyncCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            SetShifterPaddleSync(SgpPaddleSyncCheck.IsChecked == true);
        }

        private void SetShifterPaddleSync(bool on)
        {
            int v = on ? 2 : 1;   // wire range {1,2}
            if (_data != null) _data.ShifterPaddleSync = v;
            _plugin.WriteIfShifterDetected("shifter-paddle-sync", v);
            _plugin.SaveSettings();
        }

        private void SgpLed1Combo_Changed(object sender, SelectionChangedEventArgs e) => OnShifterColorChanged();
        private void SgpLed2Combo_Changed(object sender, SelectionChangedEventArgs e) => OnShifterColorChanged();

        private void OnShifterColorChanged()
        {
            if (_suppressEvents) return;
            // Both LEDs ride one 2-byte command [S1,S2], so a change to either
            // re-sends both. If the other combo hasn't been seeded yet (device read
            // still in flight), fall back to its last-known value rather than
            // clobbering that LED with index 0 (red).
            int s1 = ResolveShifterColor(SgpLed1Combo.SelectedIndex, _data?.ShifterLed1Index ?? -1);
            int s2 = ResolveShifterColor(SgpLed2Combo.SelectedIndex, _data?.ShifterLed2Index ?? -1);
            if (_data != null) { _data.ShifterLed1Index = s1; _data.ShifterLed2Index = s2; }
            _plugin.WriteArrayIfShifterDetected("shifter-colors", new byte[] { (byte)s1, (byte)s2 });
            _plugin.SaveSettings();
        }

        private static int ResolveShifterColor(int comboIndex, int dataIndex)
        {
            if (comboIndex >= 0) return comboIndex;            // user's current pick
            if (dataIndex >= 0) return dataIndex;              // last device-read value
            return 0;                                          // nothing known yet
        }

        private void SgpBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            SgpBrightnessValue.Text = v.ToString();
            if (_data != null) _data.ShifterBrightness = v;
            _plugin.WriteIfShifterDetected("shifter-brightness", v);
            _plugin.SaveSettings();
        }

        private void HgpCalStartButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.WriteIfShifterDetected("shifter-cal-start", 1);
            if (HgpCalStatus != null)
            {
                HgpCalStatus.Text = Strings.Subtitle_ShifterCalibrate;
                HgpCalStatus.Visibility = Visibility.Visible;
            }
        }
    }
}
