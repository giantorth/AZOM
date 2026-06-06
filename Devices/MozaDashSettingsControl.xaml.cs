using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MozaPlugin.UI;
using static MozaPlugin.UI.UiHelpers;

namespace MozaPlugin.Devices
{
    public partial class MozaDashSettingsControl : UserControl
    {
        private MozaPlugin? _plugin;
        private MozaDeviceManager? _device;
        private MozaData? _data;
        private MozaPluginSettings? _settings;
        private readonly EventSuppressor _suppressor = new EventSuppressor();
        private bool _suppressEvents => _suppressor.Suppressed;
        private bool _swatchesBuilt;

        private readonly DispatcherTimer _refreshTimer;

        /// <summary>
        /// The virtual LED driver for the device instance this control belongs to.
        /// When set, connection status is derived from the driver's IsConnected().
        /// </summary>
        internal MozaDashLedDeviceManager? LinkedLedDriver { get; set; }

        // Color swatch references
        private readonly Border[] _dashRpmColorSwatches = new Border[10];
        private readonly Border[] _dashRpmBlinkColorSwatches = new Border[10];
        private readonly Border[] _dashFlagColorSwatches = new Border[6];

        public MozaDashSettingsControl()
        {
            using (_suppressor.Begin())
            {
                InitializeComponent();
                // This control configures the CM2 dash pipeline (its own sender +
                // CM2-keyed mappings), independent of the wheel page.
                DashMgmtHostCm2.Content = new WheelUi.DashboardManagementControl { IsCm2Target = true };

                if (ResolvePlugin())
                    BuildColorSwatches();
            }

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += OnRefreshTick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnRefreshTick(object? sender, EventArgs e) => RefreshDash();

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
        }

        private bool ResolvePlugin()
        {
            _plugin = MozaPlugin.Instance;
            if (_plugin == null) return false;
            _device = _plugin.DeviceManager;
            _data = _plugin.Data;
            _settings = _plugin.Settings;
            return true;
        }

        // ===== Color swatches =====

        private void BuildColorSwatches()
        {
            if (_swatchesBuilt || _data == null) return;
            BuildSwatchRow(DashRpmColorPanel, _dashRpmColorSwatches, 10, "dash-rpm-color", _data.DashRpmColors);
            BuildBlinkSwatchRow(DashRpmBlinkColorPanel, _dashRpmBlinkColorSwatches, 10, "dash-rpm-blink-color");
            BuildSwatchRow(DashFlagColorPanel, _dashFlagColorSwatches, 6, "dash-flag-color", _data.DashFlagColors);
            _swatchesBuilt = true;
        }

        private void BuildSwatchRow(StackPanel panel, Border[] swatches, int count,
            string commandPrefix, byte[][] colorSource)
        {
            for (int i = 0; i < count; i++)
            {
                var border = new Border
                {
                    Width = 28, Height = 28,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(2, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Black,
                    Tag = new ColorSwatchInfo { CommandPrefix = commandPrefix, Index = i, ColorSource = colorSource }
                };
                border.MouseLeftButtonUp += ColorSwatch_Click;
                panel.Children.Add(border);
                swatches[i] = border;
            }
        }

        private void BuildBlinkSwatchRow(StackPanel panel, Border[] swatches, int count,
            string commandPrefix)
        {
            // Blink colors are write-only (can't be polled) — read from the active profile.
            var saved = _plugin?.Settings?.ProfileStore?.CurrentProfile?.DashRpmBlinkColors;
            for (int i = 0; i < count; i++)
            {
                byte r = 0, g = 0, b = 0;
                if (saved != null && i < saved.Length)
                {
                    var rgb = MozaProfile.UnpackColor(saved[i]);
                    r = rgb[0]; g = rgb[1]; b = rgb[2];
                }

                var border = new Border
                {
                    Width = 28, Height = 28,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(2, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    Tag = new BlinkSwatchInfo { CommandPrefix = commandPrefix, Index = i }
                };
                border.MouseLeftButtonUp += BlinkSwatch_Click;
                panel.Children.Add(border);
                swatches[i] = border;
            }
        }

        private class ColorSwatchInfo
        {
            public string CommandPrefix = "";
            public int Index;
            public byte[][] ColorSource = Array.Empty<byte[]>();
        }

        private class BlinkSwatchInfo
        {
            public string CommandPrefix = "";
            public int Index;
        }

        private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var border = (Border)sender;
            var info = (ColorSwatchInfo)border.Tag;
            var current = info.ColorSource[info.Index];

            var dialog = new ColorPickerDialog(current[0], current[1], current[2]);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                byte r = dialog.SelectedR, g = dialog.SelectedG, b = dialog.SelectedB;
                string cmdName = $"{info.CommandPrefix}{info.Index + 1}";
                _plugin.WriteColorIfDashDetected(cmdName, r, g, b);
                info.ColorSource[info.Index][0] = r;
                info.ColorSource[info.Index][1] = g;
                info.ColorSource[info.Index][2] = b;
                border.Background = new SolidColorBrush(Color.FromRgb(r, g, b));

                // Mirror into profile color array
                _plugin.UpdateActiveProfile(p =>
                {
                    int packed = (r << 16) | (g << 8) | b;
                    int[]? arr = info.CommandPrefix switch
                    {
                        "dash-rpm-color"  => p.DashRpmColors,
                        "dash-flag-color" => p.DashFlagColors,
                        _ => null,
                    };
                    if (arr == null || info.Index >= arr.Length)
                    {
                        int size = info.CommandPrefix == "dash-flag-color" ? 6 : 10;
                        arr = new int[size];
                        // Initialize from data so we don't blank other indices
                        for (int k = 0; k < size; k++)
                        {
                            var src = info.ColorSource[k];
                            arr[k] = (src[0] << 16) | (src[1] << 8) | src[2];
                        }
                    }
                    arr[info.Index] = packed;
                    switch (info.CommandPrefix)
                    {
                        case "dash-rpm-color":  p.DashRpmColors  = arr; break;
                        case "dash-flag-color": p.DashFlagColors = arr; break;
                    }
                });

                _plugin.SaveSettings();
            }
        }

        private void BlinkSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var border = (Border)sender;
            var info = (BlinkSwatchInfo)border.Tag;

            // Read current from the active profile (single source of truth — blink
            // colors are write-only on the wire, so the profile is the only place
            // they're persisted).
            byte r = 0, g = 0, b = 0;
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            var currentArr = profile?.DashRpmBlinkColors;
            if (currentArr != null && info.Index < currentArr.Length)
            {
                var rgb = MozaProfile.UnpackColor(currentArr[info.Index]);
                r = rgb[0]; g = rgb[1]; b = rgb[2];
            }

            var dialog = new ColorPickerDialog(r, g, b);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                r = dialog.SelectedR; g = dialog.SelectedG; b = dialog.SelectedB;
                string cmdName = $"{info.CommandPrefix}{info.Index + 1}";
                _plugin.WriteColorIfDashDetected(cmdName, r, g, b);

                int packed = (r << 16) | (g << 8) | b;
                _plugin.UpdateActiveProfile(p =>
                {
                    var arr = p.DashRpmBlinkColors ?? new int[10];
                    if (info.Index < arr.Length) arr[info.Index] = packed;
                    p.DashRpmBlinkColors = arr;
                });

                border.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
                _plugin.SaveSettings();
            }
        }

        // ===== Refresh =====

        private void RefreshDash()
        {
            // The explicit `_plugin == null` here is redundant with ResolvePlugin
            // (which only returns true when _plugin is set) but lets the compiler's
            // flow analysis treat _plugin as non-null for the rest of the method —
            // net48 lacks [MemberNotNullWhen] to express that on ResolvePlugin.
            if (!ResolvePlugin() || _plugin == null)
            {
                StatusDot.Fill = Brushes.Gray;
                StatusText.Text = "Plugin not loaded";
                DashNotDetectedPanel.Visibility = Visibility.Visible;
                DashPanel.Visibility = Visibility.Collapsed;
                return;
            }

            if (!_swatchesBuilt)
                BuildColorSwatches();

            bool dashConnected = LinkedLedDriver?.IsConnected() ?? false;
            StatusDot.Fill = dashConnected ? Brushes.LimeGreen : Brushes.Red;
            StatusText.Text = dashConnected ? "Connected" : "Disconnected";

            // Dashboard tab (dash selection + channel mapping + files) shows for
            // a CM2 — behind the base or standalone-USB.
            DashMgmtTab.Visibility =
                (_plugin.IsCm2BehindBaseCandidate || _plugin.ShouldUseStandaloneDashboardTarget())
                    ? Visibility.Visible : Visibility.Collapsed;

            bool dashDetected = dashConnected;

            using (_suppressor.Begin())
            {
                DashNotDetectedPanel.Visibility = dashDetected ? Visibility.Collapsed : Visibility.Visible;
                DashPanel.Visibility = dashDetected ? Visibility.Visible : Visibility.Collapsed;

                if (dashDetected)
                {
                    SetComboSafe(DashRpmIndicatorCombo, (int)IndicatorMode.FromDashStored(_data!.DashRpmIndicatorMode));
                    SetComboSafe(DashRpmDisplayCombo, _data.DashRpmDisplayMode);
                    SetComboSafe(DashFlagsIndicatorCombo, (int)IndicatorMode.FromDashStored(_data.DashFlagsIndicatorMode));

                    DashRpmBrightnessSlider.Value = Clamp(_data.DashRpmBrightness, 0, 100);
                    DashRpmBrightnessValue.Text = $"{_data.DashRpmBrightness}";
                    DashFlagsBrightnessSlider.Value = Clamp(_data.DashFlagsBrightness, 0, 100);
                    DashFlagsBrightnessValue.Text = $"{_data.DashFlagsBrightness}";

                    UpdateSwatches(_dashRpmColorSwatches, _data.DashRpmColors, 10);
                    UpdateSwatches(_dashFlagColorSwatches, _data.DashFlagColors, 6);
                }
            }
        }

        private static void UpdateSwatches(Border[] swatches, byte[][] colors, int count)
        {
            for (int i = 0; i < count && i < swatches.Length; i++)
            {
                if (swatches[i] == null) continue;
                var c = colors[i];
                swatches[i].Background = new SolidColorBrush(Color.FromRgb(c[0], c[1], c[2]));
            }
        }

        // SetComboSafe, Clamp moved to UI/UiHelpers.

        // ===== Handlers =====

        private void DashRpmIndicatorCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int display = DashRpmIndicatorCombo.SelectedIndex;
            if (display < 0 || display > 2) return;
            int stored = IndicatorMode.ToDashStored((IndicatorDisplayMode)display);
            _data!.DashRpmIndicatorMode = stored;
            _plugin.UpdateActiveProfile(p => p.DashRpmIndicatorMode = stored);
            _plugin.WriteIfDashDetected("dash-rpm-indicator-mode", stored);
            _plugin.SaveSettings();
        }

        private void DashRpmDisplayCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = DashRpmDisplayCombo.SelectedIndex;
            _data!.DashRpmDisplayMode = val;
            _plugin.UpdateActiveProfile(p => p.DashRpmDisplayMode = val);
            _plugin.WriteIfDashDetected("dash-rpm-display-mode", val);
            _plugin.SaveSettings();
        }

        private void DashFlagsIndicatorCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int display = DashFlagsIndicatorCombo.SelectedIndex;
            if (display < 0 || display > 2) return;
            int stored = IndicatorMode.ToDashStored((IndicatorDisplayMode)display);
            _data!.DashFlagsIndicatorMode = stored;
            _plugin.UpdateActiveProfile(p => p.DashFlagsIndicatorMode = stored);
            _plugin.WriteIfDashDetected("dash-flags-indicator-mode", stored);
            _plugin.SaveSettings();
        }

        private void DashRpmBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            DashRpmBrightnessValue.Text = $"{val}";
            _data!.DashRpmBrightness = val;
            _plugin.UpdateActiveProfile(p => p.DashRpmBrightness = val);
            _plugin.WriteIfDashDetected("dash-rpm-brightness", val);
            _plugin.SaveSettings();
        }

        private void DashFlagsBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            DashFlagsBrightnessValue.Text = $"{val}";
            _data!.DashFlagsBrightness = val;
            _plugin.UpdateActiveProfile(p => p.DashFlagsBrightness = val);
            _plugin.WriteIfDashDetected("dash-flags-brightness", val);
            _plugin.SaveSettings();
        }

    }
}
