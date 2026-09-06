using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MozaPlugin.Devices;
using MozaPlugin.Resources;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Settings tabs for the passive HGP (H-pattern) and SGP (sequential) shifters.
    /// They are independent devices — a user can run both at once (each on its own USB
    /// port), so each tab is gated on its own detection (<see cref="MozaPlugin.IsHgpShifterDetected"/> /
    /// <see cref="MozaPlugin.IsSgpShifterDetected"/>), reads/writes its own device mirror
    /// (<c>MozaData.ShifterHgp</c> / <c>ShifterSgp</c>), and routes writes to its own pipe.
    /// Both expose reverse-direction + paddle-sync; the SGP adds 2 configurable LEDs
    /// (fixed 8-colour palette, index 0-7) + brightness; the HGP has an H-pattern
    /// calibration routine. Config is serial-only — gear input stays HID-sourced.
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
                HgpDirectionCheck.IsChecked = _data?.ShifterHgp.Direction == 1;
                // Paddle-sync wire range is {1,2}: 2 = enabled, 1 = disabled.
                HgpPaddleSyncCheck.IsChecked = _data?.ShifterHgp.PaddleSync == 2;
                HgpTypeCombo.SelectedIndex = ShifterTypeIndex(_data?.ShifterHgp.ApplyMode ?? -1);
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
                SgpDirectionCheck.IsChecked = _data?.ShifterSgp.Direction == 1;
                SgpPaddleSyncCheck.IsChecked = _data?.ShifterSgp.PaddleSync == 2;

                if (_data != null)
                {
                    var s = _data.ShifterSgp;
                    if (s.Led1Index >= 0 && s.Led1Index < ShifterPaletteRgb.Length)
                        SgpLed1Combo.SelectedIndex = s.Led1Index;
                    if (s.Led2Index >= 0 && s.Led2Index < ShifterPaletteRgb.Length)
                        SgpLed2Combo.SelectedIndex = s.Led2Index;
                    if (s.Brightness >= 0)
                    {
                        SgpBrightnessSlider.Value = s.Brightness;
                        SgpBrightnessValue.Text = s.Brightness.ToString();
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
        // (shifter fields are device-read + only read on connect, so no drift). HGP and
        // SGP are separate devices, so each routes to its own mirror + its own pipe.
        private void HgpDirectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            SetShifterDirection(ShifterModelKind.Hgp, HgpDirectionCheck.IsChecked == true);
        }

        private void SgpDirectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            SetShifterDirection(ShifterModelKind.Sgp, SgpDirectionCheck.IsChecked == true);
        }

        private void SetShifterDirection(ShifterModelKind model, bool on)
        {
            int v = on ? 1 : 0;
            if (_data != null) ShifterStateFor(model).Direction = v;
            if (model == ShifterModelKind.Hgp) _plugin.HardwareApplier.WriteIfHgpDetected("shifter-direction", v);
            else _plugin.HardwareApplier.WriteIfSgpDetected("shifter-direction", v);
            _plugin.SaveSettings();
        }

        private void HgpPaddleSyncCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            SetShifterPaddleSync(ShifterModelKind.Hgp, HgpPaddleSyncCheck.IsChecked == true);
        }

        private void SgpPaddleSyncCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            SetShifterPaddleSync(ShifterModelKind.Sgp, SgpPaddleSyncCheck.IsChecked == true);
        }

        private void SetShifterPaddleSync(ShifterModelKind model, bool on)
        {
            int v = on ? 2 : 1;   // wire range {1,2}
            if (_data != null) ShifterStateFor(model).PaddleSync = v;
            if (model == ShifterModelKind.Hgp) _plugin.HardwareApplier.WriteIfHgpDetected("shifter-paddle-sync", v);
            else _plugin.HardwareApplier.WriteIfSgpDetected("shifter-paddle-sync", v);
            _plugin.SaveSettings();
        }

        private MozaData.ShifterState ShifterStateFor(ShifterModelKind model) =>
            model == ShifterModelKind.Hgp ? _data.ShifterHgp : _data.ShifterSgp;

        // shifter-type (apply-mode, wire {0,1}: 0=H-pattern, 1=sequential; confirmed
        // from a field bundle). HGP-only recovery path for units that v1.5.1 flipped
        // into sequential mode — device-owned, written on user action only, never
        // profile-applied; the SGP tab deliberately has no selector. A readback
        // follows each write so the combo settles on what the device actually stored.
        private static int ShifterTypeIndex(int applyMode) =>
            applyMode == 0 || applyMode == 1 ? applyMode : -1;

        private void HgpTypeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int v = HgpTypeCombo.SelectedIndex;
            if (v != 0 && v != 1) return;
            if (_data != null) _data.ShifterHgp.ApplyMode = v;
            MozaLog.Info($"[AZOM] User set Hgp shifter-type (apply-mode) = {v}");
            _plugin.HardwareApplier.WriteIfHgpDetected("shifter-apply-mode", v);
            _plugin.HardwareApplier.ReadIfHgpDetected("shifter-apply-mode");
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
            int s1 = ResolveShifterColor(SgpLed1Combo.SelectedIndex, _data?.ShifterSgp.Led1Index ?? -1);
            int s2 = ResolveShifterColor(SgpLed2Combo.SelectedIndex, _data?.ShifterSgp.Led2Index ?? -1);
            if (_data != null) { _data.ShifterSgp.Led1Index = s1; _data.ShifterSgp.Led2Index = s2; }
            _plugin.HardwareApplier.WriteArrayIfSgpDetected("shifter-colors", new byte[] { (byte)s1, (byte)s2 });
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
            if (_data != null) _data.ShifterSgp.Brightness = v;
            _plugin.HardwareApplier.WriteIfSgpDetected("shifter-brightness", v);
            _plugin.SaveSettings();
        }

        private void HgpCalStartButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.HardwareApplier.WriteIfHgpDetected("shifter-cal-start", 1);
            if (HgpCalStatus != null)
            {
                HgpCalStatus.Text = Strings.Subtitle_ShifterCalibrate;
                HgpCalStatus.Visibility = Visibility.Visible;
            }
        }
    }
}
