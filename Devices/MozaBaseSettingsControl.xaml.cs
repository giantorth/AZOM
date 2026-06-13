using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MozaPlugin.UI;
using MozaPlugin.Resources;

namespace MozaPlugin.Devices
{
    public partial class MozaBaseSettingsControl : UserControl
    {
        private MozaPlugin? _plugin;
        private MozaDeviceManager? _device;
        private MozaData? _data;
        private MozaPluginSettings? _settings;
        private readonly EventSuppressor _suppressor = new EventSuppressor();
        private bool _suppressEvents => _suppressor.Suppressed;

        private readonly DispatcherTimer _refreshTimer;

        /// <summary>
        /// The virtual LED driver for the device instance this control belongs to.
        /// When set, connection status is derived from the driver's IsConnected().
        /// </summary>
        internal MozaBaseLedDeviceManager? LinkedLedDriver { get; set; }

        public MozaBaseSettingsControl()
        {
            using (_suppressor.Begin())
            {
                InitializeComponent();
                ResolvePlugin();
            }

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += OnRefreshTick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnRefreshTick(object? sender, EventArgs e) => Refresh();

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

        // ===== Refresh =====

        private void Refresh()
        {
            if (!ResolvePlugin())
            {
                StatusDot.Fill = Brushes.Gray;
                StatusText.Text = Strings.Status_PluginNotLoaded;
                BaseAmbientNotDetectedPanel.Visibility = Visibility.Visible;
                BasePanel.Visibility = Visibility.Collapsed;
                return;
            }

            bool detected = LinkedLedDriver?.IsConnected() ?? false;
            StatusDot.Fill = detected ? Brushes.LimeGreen : Brushes.Red;
            StatusText.Text = detected ? "Connected" : "Disconnected";
            BaseModelText.Text = string.IsNullOrEmpty(_data!.BaseModelName)
                ? "(unknown)"
                : _data.BaseModelName;

            using (_suppressor.Begin())
            {
                BaseAmbientNotDetectedPanel.Visibility = detected ? Visibility.Collapsed : Visibility.Visible;
                BasePanel.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;

                if (detected)
                {
                    SetComboSafe(IndicatorStateCombo, Clamp(_data.BaseAmbientIndicatorState, 0, 1));
                    // Standby mode dropdown shows 5 entries (const/breath/cycle/rainbow/flow)
                    // mapped from device modes 0/2/3/4/5. The undocumented mode 1 is hidden
                    // — if the device returns it, fall back to "Constant" in the UI without
                    // forcing a write.
                    SetComboSafe(StandbyModeCombo, MapStandbyDeviceToUi(_data.BaseAmbientStandbyMode));
                    SetComboSafe(SleepModeCombo, Clamp(_data.BaseAmbientSleepMode, 0, 1));

                    BrightnessSlider.Value = Clamp(_data.BaseAmbientBrightness, 0, 255);
                    BrightnessValue.Text = $"{(int)BrightnessSlider.Value}";
                    SleepTimeoutSlider.Value = Clamp(_data.BaseAmbientSleepTimeout, 0, (int)SleepTimeoutSlider.Maximum);
                    SleepTimeoutValue.Text = $"{(int)SleepTimeoutSlider.Value}";

                    UpdateSwatch(StartupColorSwatch, _data.BaseAmbientStartupColor);
                    UpdateSwatch(ShutdownColorSwatch, _data.BaseAmbientShutdownColor);
                }
            }
        }

        // Standby mode mapping: device range is 0..5 (mode 1 unconfirmed),
        // UI dropdown is 5 entries representing 0/2/3/4/5.
        private static int MapStandbyDeviceToUi(int deviceMode)
        {
            switch (deviceMode)
            {
                case 0: return 0; // Constant
                case 2: return 1; // Breath
                case 3: return 2; // Cycle
                case 4: return 3; // Rainbow
                case 5: return 4; // Flow
                default: return 0;
            }
        }

        private static int MapStandbyUiToDevice(int uiIndex)
        {
            switch (uiIndex)
            {
                case 0: return 0;
                case 1: return 2;
                case 2: return 3;
                case 3: return 4;
                case 4: return 5;
                default: return 0;
            }
        }

        private static void UpdateSwatch(Border swatch, byte[] rgb)
        {
            swatch.Background = new SolidColorBrush(Color.FromRgb(rgb[0], rgb[1], rgb[2]));
        }

        private static void SetComboSafe(ComboBox combo, int index)
        {
            if (index >= 0 && index < combo.Items.Count)
                combo.SelectedIndex = index;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ===== Handlers =====

        private void IndicatorStateCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = IndicatorStateCombo.SelectedIndex;
            if (val < 0) return;
            _data!.BaseAmbientIndicatorState = val;
            _plugin.UpdateActiveProfile(p => p.BaseAmbientIndicatorState = val);
            _plugin.WriteIfBaseAmbientSupported("base-ambient-indicator-state", val);
            _plugin.SaveSettings();
        }

        private void StandbyModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int uiIndex = StandbyModeCombo.SelectedIndex;
            if (uiIndex < 0) return;
            int deviceMode = MapStandbyUiToDevice(uiIndex);
            _data!.BaseAmbientStandbyMode = deviceMode;
            _plugin.UpdateActiveProfile(p => p.BaseAmbientStandbyMode = deviceMode);
            _plugin.WriteIfBaseAmbientSupported("base-ambient-standby-mode", deviceMode);
            _plugin.SaveSettings();
        }

        private void SleepModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = SleepModeCombo.SelectedIndex;
            if (val < 0) return;
            _data!.BaseAmbientSleepMode = val;
            _plugin.UpdateActiveProfile(p => p.BaseAmbientSleepMode = val);
            _plugin.WriteIfBaseAmbientSupported("base-ambient-sleep-mode", val);
            _plugin.SaveSettings();
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            BrightnessValue.Text = $"{val}";
            _data!.BaseAmbientBrightness = val;
            _plugin.UpdateActiveProfile(p => p.BaseAmbientBrightness = val);
            _plugin.WriteIfBaseAmbientSupported("base-ambient-brightness", val);
            _plugin.SaveSettings();
        }

        private void SleepTimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            SleepTimeoutValue.Text = $"{val}";
            _data!.BaseAmbientSleepTimeout = val;
            _plugin.UpdateActiveProfile(p => p.BaseAmbientSleepTimeout = val);
            _plugin.WriteIfBaseAmbientSupported("base-ambient-sleep-timeout", val);
            _plugin.SaveSettings();
        }

        private void StartupColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            ShowColorPicker(_data!.BaseAmbientStartupColor, "base-ambient-startup-color",
                packed => _plugin!.UpdateActiveProfile(p => p.BaseAmbientStartupColor = packed),
                StartupColorSwatch);
        }

        private void ShutdownColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            ShowColorPicker(_data!.BaseAmbientShutdownColor, "base-ambient-shutdown-color",
                packed => _plugin!.UpdateActiveProfile(p => p.BaseAmbientShutdownColor = packed),
                ShutdownColorSwatch);
        }

        private void ShowColorPicker(byte[] target, string command, Action<int> persistPacked, Border swatch)
        {
            if (_suppressEvents || _plugin == null) return;

            var dialog = new ColorPickerDialog(target[0], target[1], target[2]);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                byte r = dialog.SelectedR, g = dialog.SelectedG, b = dialog.SelectedB;
                _plugin.WriteColorIfBaseAmbientSupported(command, r, g, b);
                target[0] = r; target[1] = g; target[2] = b;
                swatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
                persistPacked((r << 16) | (g << 8) | b);
                _plugin.SaveSettings();
            }
        }
    }
}
