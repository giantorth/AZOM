using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MozaPlugin.Devices;
using MozaPlugin.Telemetry;
using SerialTrafficCapture = MozaPlugin.Diagnostics.SerialTrafficCapture;

namespace MozaPlugin
{
    public partial class SettingsControl : UserControl
    {
        private readonly MozaPlugin _plugin;
        private readonly MozaDeviceManager _device;
        private readonly MozaData _data;
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _steeringAngleTimer;
        private bool _suppressEvents;
        private readonly DateTime[] _buttonLastPressed = new DateTime[MozaData.MaxButtons];

        public SettingsControl(MozaPlugin plugin)
        {
            _plugin = plugin;
            _device = plugin.DeviceManager;
            _data = plugin.Data;

            _suppressEvents = true;
            InitializeComponent();
            ConnectionToggle.IsChecked = plugin.ConnectionEnabled;
            AutoApplyProfileCheck.IsChecked = plugin.Settings.AutoApplyProfileOnLaunch;
            LimitWheelUpdatesCheck.IsChecked = plugin.Settings.LimitWheelUpdates;
            AlwaysResendBitmaskCheck.IsChecked = plugin.Settings.AlwaysResendBitmask;
            EnableAb9Check.IsChecked = plugin.Settings.EnableAb9;
            StartCaptureOnNextLaunchCheck.IsChecked = plugin.Settings.StartCaptureOnNextLaunch;
            // Reflect any in-flight capture (e.g. armed from a previous session
            // and started in MozaPlugin.Init) so the user sees Stop instead of
            // a stale Start button when they open the Diagnostics tab.
            if (SerialTrafficCapture.Instance.Enabled)
            {
                SerialCaptureToggleButton.Content = "Stop capture";
                SerialCaptureStatusText.Text = "capturing… (armed from prior session — click Stop when ready)";
            }
            _suppressEvents = false;

            InitProfilesTab();

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += RefreshDisplay;

            _steeringAngleTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _steeringAngleTimer.Tick += OnSteeringAngleTick;

            Loaded   += OnLoadedStartTimers;
            Unloaded += OnUnloadedStopTimers;

            RequestAllSettings();
        }

        private void OnSteeringAngleTick(object? sender, EventArgs e) => UpdateHidInputDisplays();

        private void OnLoadedStartTimers(object sender, RoutedEventArgs e)
        {
            // WPF can fire Loaded more than once if the control is reparented
            // (SimHub's tab containers do this during settings-panel layout).
            // Calling Start() twice would double the tick rate.
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
            if (!_steeringAngleTimer.IsEnabled) _steeringAngleTimer.Start();
        }

        private void OnUnloadedStopTimers(object sender, RoutedEventArgs e)
        {
            // Stop only — leave Tick handlers attached so a subsequent Loaded
            // re-Start picks up where it left off. Detaching here permanently
            // killed the timers if the control was reloaded.
            _refreshTimer.Stop();
            _steeringAngleTimer.Stop();
        }

        private MozaPluginSettings _settings => _plugin.Settings;

        // ===== Refresh =====

        private void RequestAllSettings()
        {
            _device.ReadSettings(
                "base-limit", "base-ffb-strength", "base-torque", "base-speed",
                "base-damper", "base-friction", "base-inertia", "base-spring",
                "main-get-damper-gain", "main-get-friction-gain",
                "main-get-inertia-gain", "main-get-spring-gain",
                "base-protection", "base-natural-inertia",
                "base-speed-damping", "base-speed-damping-point",
                "base-soft-limit-stiffness", "base-soft-limit-retain",
                "base-ffb-reverse", "main-get-work-mode", "main-get-led-status",
                "main-get-ble-mode",
                "base-mcu-temp", "base-mosfet-temp", "base-motor-temp"
            );
        }

        private void RefreshDisplay(object sender, EventArgs e)
        {
            RestartBanner.Visibility = _plugin.DeviceDefinitionDeployed
                ? Visibility.Visible : Visibility.Collapsed;

            _suppressEvents = true;
            try
            {
                RefreshBaseTab();
                RefreshWheelTab();
                RefreshHandbrakeTab();
                RefreshPedalsTab();
                RefreshHubTab();
                RefreshAb9Tab();
                InitTelemetryTab();
                RefreshExtendedLedGroups();
                RefreshWheelFilesTab();
                RefreshDiagnosticsTab();
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void UpdateHidInputDisplays()
        {
            var hidReader = _plugin.HidReader;
            bool connected = hidReader != null && _data.IsHidConnected;

            if (connected && _data.MaxAngle > 0)
            {
                double deg = hidReader!.GetCurrentAngleDegrees(_data.MaxAngle * 2);
                SteeringAngleLabel.Text = $"{deg:0;-0;0}°";
            }
            else
            {
                SteeringAngleLabel.Text = "--";
            }

            if (connected)
            {
                ThrottleBar.Value      = _data.ThrottlePosition;
                BrakeBar.Value         = _data.BrakePosition;
                ClutchBar.Value        = _data.ClutchPosition;
                HandbrakeBar.Value     = _data.HandbrakePosition;
                LeftPaddleBar.Value    = _data.LeftPaddlePosition;
                RightPaddleBar.Value   = _data.RightPaddlePosition;
                CombinedPaddleBar.Value = _data.CombinedPaddlePosition;
            }
            else
            {
                ThrottleBar.Value      = 0;
                BrakeBar.Value         = 0;
                ClutchBar.Value        = 0;
                HandbrakeBar.Value     = 0;
                LeftPaddleBar.Value    = 0;
                RightPaddleBar.Value   = 0;
                CombinedPaddleBar.Value = 0;
            }

            UpdateActiveButtons(connected);
            UpdateHandbrakeButtonStatus(connected);
        }

        private void UpdateActiveButtons(bool connected)
        {
            if (!connected || _data.ButtonCount == 0)
            {
                ActiveButtonsText.Inlines.Clear();
                ActiveButtonsText.Inlines.Add(new Run("None"));
                return;
            }

            var now = DateTime.UtcNow;
            var inlines = new System.Collections.Generic.List<Inline>();
            int count = _data.ButtonCount;
            for (int i = 0; i < count; i++)
            {
                bool pressed = _data.ButtonStates[i];
                if (pressed)
                    _buttonLastPressed[i] = now;

                if ((now - _buttonLastPressed[i]).TotalSeconds < 1.0)
                {
                    if (inlines.Count > 0)
                        inlines.Add(new Run(", "));

                    var run = new Run((i + 1).ToString());
                    if (pressed)
                    {
                        run.FontWeight = FontWeights.Bold;
                        run.Foreground = Brushes.White;
                    }
                    inlines.Add(run);
                }
            }

            ActiveButtonsText.Inlines.Clear();
            if (inlines.Count > 0)
                ActiveButtonsText.Inlines.AddRange(inlines);
            else
                ActiveButtonsText.Inlines.Add(new Run("None"));
        }

        private void UpdateHandbrakeButtonStatus(bool connected)
        {
            if (_data.HandbrakeMode != 1) return;

            bool pressed = connected && _data.HandbrakeButtonPressed;
            HandbrakeButtonStatus.Text = pressed ? "Pressed" : "Released";
            HandbrakeButtonStatus.FontWeight = pressed ? FontWeights.Bold : FontWeights.Normal;
            HandbrakeButtonStatus.Foreground = pressed ? Brushes.White : Brushes.Gray;
        }

        private void RefreshBaseTab()
        {
            ConnectionIndicator.Fill = _data.IsConnected ? Brushes.LimeGreen : Brushes.Gray;
            ConnectionLabel.Text = _data.IsConnected ? "Connected" : "Disconnected";

            string tempUnit = _data.UseFahrenheit ? "°F" : "°C";
            McuTempLabel.Text = _data.IsBaseConnected ? $"{ConvertTemp(_data.McuTemp):F0} {tempUnit}" : "--";
            MosfetTempLabel.Text = _data.IsBaseConnected ? $"{ConvertTemp(_data.MosfetTemp):F0} {tempUnit}" : "--";
            MotorTempLabel.Text = _data.IsBaseConnected ? $"{ConvertTemp(_data.MotorTemp):F0} {tempUnit}" : "--";

            // Reverse expression: *2 (raw → display degrees)
            double rot = _data.Limit * 2.0;
            RotationSlider.Value = Clamp(rot, 90, 2700);
            RotationValue.Text = $"{rot:F0}°";

            double ffb = _data.FfbStrength / 10.0;
            FfbStrengthSlider.Value = Clamp(ffb, 0, 100);
            FfbStrengthValue.Text = $"{ffb:F0}%";

            TorqueSlider.Value = Clamp(_data.Torque, 50, 100);
            TorqueValue.Text = $"{_data.Torque}%";

            // Performance output (cmd 0x1E base = TempStrategy): 0 = Reserved, 1 = Full
            int perf = _data.TempStrategy;
            if (perf >= 0 && perf < PerformanceOutputCombo.Items.Count)
                PerformanceOutputCombo.SelectedIndex = perf;
            // Gearshift vibration intensity (cmd 0x2E base): 0..5
            int gs = _data.GearshiftVibration;
            if (gs < 0) gs = 0;
            if (gs > 5) gs = 5;
            GearshiftVibrationSlider.Value = gs;
            GearshiftVibrationValue.Text = gs.ToString();

            double spd = _data.Speed / 10.0;
            SpeedSlider.Value = Clamp(spd, 0, 200);
            SpeedValue.Text = $"{spd:F0}%";

            SetSliderPercent(DamperSlider, DamperValue, _data.Damper / 10.0, 0, 100);
            SetSliderPercent(FrictionSlider, FrictionValue, _data.Friction / 10.0, 0, 100);
            InertiaSlider.Value = Clamp(_data.Inertia / 10.0, 100, 500);
            InertiaValue.Text = $"{_data.Inertia / 10.0:F0}";
            SetSliderPercent(SpringSlider, SpringValue, _data.Spring / 10.0, 0, 100);

            FfbReverseCheck.IsChecked = _data.FfbReverse != 0;

            SetSliderPercent(GameDamperSlider, GameDamperValue, _data.GameDamper / 2.55, 0, 100);
            SetSliderPercent(GameFrictionSlider, GameFrictionValue, _data.GameFriction / 2.55, 0, 100);
            SetSliderPercent(GameInertiaSlider, GameInertiaValue, _data.GameInertia / 2.55, 0, 100);
            SetSliderPercent(GameSpringSlider, GameSpringValue, _data.GameSpring / 2.55, 0, 100);

            SpeedDampingSlider.Value = Clamp(_data.SpeedDamping, 0, 100);
            SpeedDampingValue.Text = $"{_data.SpeedDamping}%";
            SpeedDampingPointSlider.Value = Clamp(_data.SpeedDampingPoint, 0, 400);
            SpeedDampingPointValue.Text = $"{_data.SpeedDampingPoint} kph";

            ProtectionCheck.IsChecked = _data.Protection != 0;
            NaturalInertiaSlider.Value = Clamp(_data.NaturalInertia, 100, 4000);
            NaturalInertiaValue.Text = $"{_data.NaturalInertia}";

            double stiff = (_data.SoftLimitStiffness / (400.0 / 9.0)) - 2.25 + 1.0;
            stiff = Math.Round(Clamp(stiff, 1, 10));
            SoftLimitStiffnessSlider.Value = stiff;
            SoftLimitStiffnessValue.Text = $"{stiff:F0}";
            SoftLimitRetainCheck.IsChecked = _data.SoftLimitRetain != 0;

            StandbyCheck.IsChecked = _data.WorkMode != 0;
            LedStatusCheck.IsChecked = _data.LedStatus != 0;
            BluetoothCheck.IsChecked = _data.BleMode == 0;

            // FFB Equalizer (0-400% where 100% is default/flat)
            SetSliderRaw(Eq1Slider, Eq1Value, _data.Equalizer1, 0, 400, "%");
            SetSliderRaw(Eq2Slider, Eq2Value, _data.Equalizer2, 0, 400, "%");
            SetSliderRaw(Eq3Slider, Eq3Value, _data.Equalizer3, 0, 400, "%");
            SetSliderRaw(Eq4Slider, Eq4Value, _data.Equalizer4, 0, 400, "%");
            SetSliderRaw(Eq5Slider, Eq5Value, _data.Equalizer5, 0, 400, "%");
            SetSliderRaw(Eq6Slider, Eq6Value, _data.Equalizer6, 0, 400, "%");

            // FFB Curve (X breakpoints are fixed at 20/40/60/80; only Y output values are user-adjustable)
            SetSliderRaw(FfbCurveY1Slider, FfbCurveY1Value, _data.FfbCurveY1, 0, 100, "");
            SetSliderRaw(FfbCurveY2Slider, FfbCurveY2Value, _data.FfbCurveY2, 0, 100, "");
            SetSliderRaw(FfbCurveY3Slider, FfbCurveY3Value, _data.FfbCurveY3, 0, 100, "");
            SetSliderRaw(FfbCurveY4Slider, FfbCurveY4Value, _data.FfbCurveY4, 0, 100, "");
            SetSliderRaw(FfbCurveY5Slider, FfbCurveY5Value, _data.FfbCurveY5, 0, 100, "");
        }

        // ===== Base tab slider handlers =====
        // Each handler writes to device AND updates _data so the refresh timer doesn't revert.

        private void RotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int deg = (int)Math.Round(e.NewValue);
            // Expression: /2 (display degrees → raw)
            int raw = deg / 2;
            RotationValue.Text = $"{deg}°";
            _data.Limit = raw;
            _data.MaxAngle = raw;
            _device.WriteSetting("base-limit", raw);
            _device.WriteSetting("base-max-angle", raw);
            _plugin.SaveSettings();
        }

        private void FfbStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            FfbStrengthValue.Text = $"{pct}%";
            _data.FfbStrength = raw;
            _device.WriteSetting("base-ffb-strength", raw);
            _plugin.SaveSettings();
        }

        private void TorqueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            TorqueValue.Text = $"{val}%";
            _data.Torque = val;
            _device.WriteSetting("base-torque", val);
            _plugin.SaveSettings();
        }

        private void PerformanceOutputCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = PerformanceOutputCombo.SelectedIndex;
            if (val < 0) return;
            _data.TempStrategy = val;
            _device.WriteSetting("base-temp-strategy", val);
            _plugin.SaveSettings();
        }

        private void GearshiftVibrationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            GearshiftVibrationValue.Text = val.ToString();
            _data.GearshiftVibration = val;
            _device.WriteSetting("base-gearshift-vibration", val);
            _plugin.SaveSettings();
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            SpeedValue.Text = $"{pct}%";
            _data.Speed = raw;
            _device.WriteSetting("base-speed", raw);
            _plugin.SaveSettings();
        }

        private void DamperSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            DamperValue.Text = $"{pct}%";
            _data.Damper = raw;
            _device.WriteSetting("base-damper", raw);
            _plugin.SaveSettings();
        }

        private void FrictionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            FrictionValue.Text = $"{pct}%";
            _data.Friction = raw;
            _device.WriteSetting("base-friction", raw);
            _plugin.SaveSettings();
        }

        private void InertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            int raw = val * 10;
            InertiaValue.Text = $"{val}";
            _data.Inertia = raw;
            _device.WriteSetting("base-inertia", raw);
            _plugin.SaveSettings();
        }

        private void SpringSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            SpringValue.Text = $"{pct}%";
            _data.Spring = raw;
            _device.WriteSetting("base-spring", raw);
            _plugin.SaveSettings();
        }

        private void GameDamperSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameDamperValue.Text = $"{pct}%";
            _data.GameDamper = raw;
            _device.WriteSetting("main-set-damper-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameFrictionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameFrictionValue.Text = $"{pct}%";
            _data.GameFriction = raw;
            _device.WriteSetting("main-set-friction-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameInertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameInertiaValue.Text = $"{pct}%";
            _data.GameInertia = raw;
            _device.WriteSetting("main-set-inertia-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameSpringSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameSpringValue.Text = $"{pct}%";
            _data.GameSpring = raw;
            _device.WriteSetting("main-set-spring-gain", raw);
            _plugin.SaveSettings();
        }

        private void SpeedDampingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            SpeedDampingValue.Text = $"{val}%";
            _data.SpeedDamping = val;
            _device.WriteSetting("base-speed-damping", val);
            _plugin.SaveSettings();
        }

        private void SpeedDampingPointSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            SpeedDampingPointValue.Text = $"{val} kph";
            _data.SpeedDampingPoint = val;
            _device.WriteSetting("base-speed-damping-point", val);
            _plugin.SaveSettings();
        }

        private void NaturalInertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            NaturalInertiaValue.Text = $"{val}";
            _data.NaturalInertia = val;
            _device.WriteSetting("base-natural-inertia", val);
            _plugin.SaveSettings();
        }

        private void SoftLimitStiffnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int display = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(display * (400.0 / 9.0) - (400.0 / 9.0) + 100.0);
            SoftLimitStiffnessValue.Text = $"{display}";
            _data.SoftLimitStiffness = raw;
            _device.WriteSetting("base-soft-limit-stiffness", raw);
            _plugin.SaveSettings();
        }

        // ===== Checkbox handlers =====

        private void FfbReverseCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = FfbReverseCheck.IsChecked == true ? 1 : 0;
            _data.FfbReverse = val;
            _device.WriteSetting("base-ffb-reverse", val);
            _plugin.SaveSettings();
        }

        private void ProtectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = ProtectionCheck.IsChecked == true ? 1 : 0;
            _data.Protection = val;
            _device.WriteSetting("base-protection", val);
            _plugin.SaveSettings();
        }

        private void SoftLimitRetainCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = SoftLimitRetainCheck.IsChecked == true ? 1 : 0;
            _data.SoftLimitRetain = val;
            _device.WriteSetting("base-soft-limit-retain", val);
            _plugin.SaveSettings();
        }

        private void StandbyCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = StandbyCheck.IsChecked == true ? 1 : 0;
            _data.WorkMode = val;
            _device.WriteSetting("main-set-work-mode", val);
            _plugin.SaveSettings();
        }

        private void LedStatusCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = LedStatusCheck.IsChecked == true ? 1 : 0;
            _data.LedStatus = val;
            _device.WriteSetting("main-set-led-status", val);
            _plugin.SaveSettings();
        }

        // ===== RPM range slider handlers =====

        // ===== Handbrake tab =====

        // ===== Wheel Tab =====

        private void RefreshWheelTab()
        {
            if (!_plugin.IsNewWheelDetected) return;

            SetComboSafe(WheelPaddlesModeCombo, _data.WheelPaddlesMode);
            UpdatePaddlePanelVisibility(_data.WheelPaddlesMode);
            WheelClutchPointSlider.Value = Clamp(_data.WheelClutchPoint, 0, 100);
            WheelClutchPointValue.Text = $"{_data.WheelClutchPoint}%";

            bool perKnob = _data.WheelKnobSignalModeSupported;
            KnobModeLegacyPanel.Visibility = perKnob ? Visibility.Collapsed : Visibility.Visible;
            KnobSignalModePanel.Visibility = perKnob ? Visibility.Visible : Visibility.Collapsed;
            if (perKnob)
            {
                var rows = new[] { KnobSignalMode0Row, KnobSignalMode1Row, KnobSignalMode2Row, KnobSignalMode3Row, KnobSignalMode4Row };
                var combos = new[] { KnobSignalMode0Combo, KnobSignalMode1Combo, KnobSignalMode2Combo, KnobSignalMode3Combo, KnobSignalMode4Combo };
                for (int i = 0; i < 5; i++)
                {
                    int v = _data.WheelKnobSignalModes[i];
                    rows[i].Visibility = v >= 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (v >= 0) SetComboSafe(combos[i], v);
                }
            }
            else
            {
                SetComboSafe(KnobModeCombo, _data.WheelKnobMode);
            }
            if (_data.WheelDualStickSupported)
            {
                StickModeNewPanel.Visibility = Visibility.Visible;
                StickModeOldPanel.Visibility = Visibility.Collapsed;
                SetComboSafe(StickModeCombo, _data.WheelStickMode);
            }
            else
            {
                StickModeOldPanel.Visibility = Visibility.Visible;
                StickModeNewPanel.Visibility = Visibility.Collapsed;
                StickModeCheck.IsChecked = _data.WheelStickMode != 0;
            }
        }

        private void UpdatePaddlePanelVisibility(int mode)
        {
            // 0=Buttons, 1=Combined, 2=Split
            bool buttons = mode == 0;
            bool combined = mode == 1;
            CombinedPaddlePanel.Visibility = combined ? Visibility.Visible : Visibility.Collapsed;
            SplitPaddlePanel.Visibility = !buttons && !combined ? Visibility.Visible : Visibility.Collapsed;
            WheelClutchPointPanel.Visibility = combined ? Visibility.Visible : Visibility.Collapsed;
        }

        private void WheelPaddlesModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = WheelPaddlesModeCombo.SelectedIndex;
            _data.WheelPaddlesMode = val;
            _settings.WheelPaddlesMode = val;
            UpdatePaddlePanelVisibility(val);
            _device.WriteSetting("wheel-paddles-mode", val + 1); // display 0/1/2 → raw 1/2/3
            _plugin.SaveSettings();
        }

        private void WheelClutchPointSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            WheelClutchPointValue.Text = $"{val}%";
            _data.WheelClutchPoint = val;
            _settings.WheelClutchPoint = val;
            _device.WriteSetting("wheel-clutch-point", val);
            _plugin.SaveSettings();
        }

        private void KnobModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = KnobModeCombo.SelectedIndex;
            _data.WheelKnobMode = val;
            _settings.WheelKnobMode = val;
            _device.WriteSetting("wheel-knob-mode", val);
            _plugin.SaveSettings();
        }

        private void WriteKnobSignalMode(int index, int value)
        {
            if (_suppressEvents) return;
            _data.WheelKnobSignalModes[index] = value;
            _device.WriteSetting($"wheel-knob-signal-mode{index}", value);
            _plugin.SaveSettings();
        }

        private void KnobSignalMode0Combo_Changed(object sender, SelectionChangedEventArgs e)
            => WriteKnobSignalMode(0, KnobSignalMode0Combo.SelectedIndex);
        private void KnobSignalMode1Combo_Changed(object sender, SelectionChangedEventArgs e)
            => WriteKnobSignalMode(1, KnobSignalMode1Combo.SelectedIndex);
        private void KnobSignalMode2Combo_Changed(object sender, SelectionChangedEventArgs e)
            => WriteKnobSignalMode(2, KnobSignalMode2Combo.SelectedIndex);
        private void KnobSignalMode3Combo_Changed(object sender, SelectionChangedEventArgs e)
            => WriteKnobSignalMode(3, KnobSignalMode3Combo.SelectedIndex);
        private void KnobSignalMode4Combo_Changed(object sender, SelectionChangedEventArgs e)
            => WriteKnobSignalMode(4, KnobSignalMode4Combo.SelectedIndex);

        private void StickModeCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = StickModeCheck.IsChecked == true ? 1 : 0;
            _data.WheelStickMode = val;
            _settings.WheelStickMode = val;
            _device.WriteSetting("wheel-stick-mode", val * 256);
            _plugin.SaveSettings();
        }

        private void StickModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = StickModeCombo.SelectedIndex;
            _data.WheelStickMode = val;
            _settings.WheelStickMode = val;
            _device.WriteSetting("wheel-stick-mode-new", val);
            _plugin.SaveSettings();
        }

        // ===== Handbrake Range + Curve + Calibration =====

        private void RefreshHandbrakeTab()
        {
            bool detected = _plugin.IsHandbrakeDetected;
            HandbrakeTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;

            if (!detected) return;

            SetComboSafe(HandbrakeModeCombo, _data.HandbrakeMode);

            bool buttonMode = _data.HandbrakeMode == 1;
            HandbrakeThresholdPanel.Visibility = buttonMode ? Visibility.Visible : Visibility.Collapsed;
            HandbrakeAxisPanel.Visibility = buttonMode ? Visibility.Collapsed : Visibility.Visible;
            HandbrakeButtonStatus.Visibility = buttonMode ? Visibility.Visible : Visibility.Collapsed;

            HandbrakeThresholdSlider.Value = Clamp(_data.HandbrakeButtonThreshold, 0, 100);
            HandbrakeThresholdValue.Text = $"{_data.HandbrakeButtonThreshold}%";

            HandbrakeDirectionCheck.IsChecked = _data.HandbrakeDirection != 0;

            HandbrakeMinSlider.Value = Clamp(_data.HandbrakeMin, 0, 100);
            HandbrakeMinValue.Text = $"{_data.HandbrakeMin}%";
            HandbrakeMaxSlider.Value = Clamp(_data.HandbrakeMax, 0, 100);
            HandbrakeMaxValue.Text = $"{_data.HandbrakeMax}%";

            SetSliderRaw(HbY1Slider, HbY1Value, _data.HandbrakeCurve[0], 0, 100, "");
            SetSliderRaw(HbY2Slider, HbY2Value, _data.HandbrakeCurve[1], 0, 100, "");
            SetSliderRaw(HbY3Slider, HbY3Value, _data.HandbrakeCurve[2], 0, 100, "");
            SetSliderRaw(HbY4Slider, HbY4Value, _data.HandbrakeCurve[3], 0, 100, "");
            SetSliderRaw(HbY5Slider, HbY5Value, _data.HandbrakeCurve[4], 0, 100, "");
        }

        private void HandbrakeModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = HandbrakeModeCombo.SelectedIndex;
            _data.HandbrakeMode = val;
            _device.WriteSetting("handbrake-mode", val);
            _plugin.SaveSettings();
        }

        private void HandbrakeThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            HandbrakeThresholdValue.Text = $"{val}%";
            _data.HandbrakeButtonThreshold = val;
            _device.WriteSetting("handbrake-button-threshold", val);
            _plugin.SaveSettings();
        }

        private void HandbrakeDirectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = HandbrakeDirectionCheck.IsChecked == true ? 1 : 0;
            _data.HandbrakeDirection = val;
            _device.WriteSetting("handbrake-direction", val);
            _plugin.SaveSettings();
        }

        // ===== Connection toggle =====

        private void ConnectionToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.SetConnectionEnabled(ConnectionToggle.IsChecked == true);
        }

        // ===== Refresh button =====

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RequestAllSettings();
        }

        // ===== Helpers =====

        private void SetSliderPercent(Slider slider, TextBlock label, double value, double min, double max)
        {
            slider.Value = Clamp(value, min, max);
            label.Text = $"{value:F0}%";
        }

        private static void SetComboSafe(ComboBox combo, int index)
        {
            if (index >= 0 && index < combo.Items.Count)
                combo.SelectedIndex = index;
        }

        private double ConvertTemp(int raw)
        {
            double celsius = raw / 100.0;
            return _data.UseFahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ===== Helpers (new) =====

        private void SetSliderRaw(Slider slider, TextBlock label, int value, int min, int max, string suffix)
        {
            slider.Value = Clamp(value, min, max);
            label.Text = $"{value}{suffix}";
        }

        // ===== FFB Equalizer handlers =====

        private static readonly string[] EqCommands = {
            "base-equalizer1", "base-equalizer2", "base-equalizer3",
            "base-equalizer4", "base-equalizer5", "base-equalizer6"
        };

        private void WriteEq(int index, int value)
        {
            _device.WriteSetting(EqCommands[index], value);
            _plugin.SaveSettings();
        }

        private void Eq1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); Eq1Value.Text = $"{v}%"; _data.Equalizer1 = v; WriteEq(0, v); }
        private void Eq2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); Eq2Value.Text = $"{v}%"; _data.Equalizer2 = v; WriteEq(1, v); }
        private void Eq3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); Eq3Value.Text = $"{v}%"; _data.Equalizer3 = v; WriteEq(2, v); }
        private void Eq4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); Eq4Value.Text = $"{v}%"; _data.Equalizer4 = v; WriteEq(3, v); }
        private void Eq5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); Eq5Value.Text = $"{v}%"; _data.Equalizer5 = v; WriteEq(4, v); }
        private void Eq6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); Eq6Value.Text = $"{v}%"; _data.Equalizer6 = v; WriteEq(5, v); }

        // ===== FFB Curve handlers =====

        // Presets: [Y1, Y2, Y3, Y4, Y5] — X breakpoints are fixed at [20, 40, 60, 80]
        private static readonly int[][] FfbCurvePresets =
        {
            new[] { 20, 40, 60, 80, 100 }, // Linear
            new[] {  8, 24, 76, 92, 100 }, // S-Curve
            new[] {  6, 14, 28, 54, 100 }, // Exponential
            new[] { 46, 72, 86, 94, 100 }, // Parabolic
        };

        private void ApplyFfbCurvePreset(int[] p)
        {
            _suppressEvents = true;
            FfbCurveY1Slider.Value = p[0]; FfbCurveY1Value.Text = $"{p[0]}"; _data.FfbCurveY1 = p[0];
            FfbCurveY2Slider.Value = p[1]; FfbCurveY2Value.Text = $"{p[1]}"; _data.FfbCurveY2 = p[1];
            FfbCurveY3Slider.Value = p[2]; FfbCurveY3Value.Text = $"{p[2]}"; _data.FfbCurveY3 = p[2];
            FfbCurveY4Slider.Value = p[3]; FfbCurveY4Value.Text = $"{p[3]}"; _data.FfbCurveY4 = p[3];
            FfbCurveY5Slider.Value = p[4]; FfbCurveY5Value.Text = $"{p[4]}"; _data.FfbCurveY5 = p[4];
            _suppressEvents = false;
            // Always write fixed X breakpoints first
            _device.WriteSetting("base-ffb-curve-x1", 20); _device.WriteSetting("base-ffb-curve-x2", 40);
            _device.WriteSetting("base-ffb-curve-x3", 60); _device.WriteSetting("base-ffb-curve-x4", 80);
            _device.WriteSetting("base-ffb-curve-y1", p[0]); _device.WriteSetting("base-ffb-curve-y2", p[1]);
            _device.WriteSetting("base-ffb-curve-y3", p[2]); _device.WriteSetting("base-ffb-curve-y4", p[3]);
            _device.WriteSetting("base-ffb-curve-y5", p[4]);
            _plugin.SaveSettings();
        }

        private void FfbCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[0]);
        private void FfbCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[1]);
        private void FfbCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[2]);
        private void FfbCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[3]);

        private void FfbCurveY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); FfbCurveY1Value.Text = $"{v}"; _data.FfbCurveY1 = v; _device.WriteSetting("base-ffb-curve-y1", v); _plugin.SaveSettings(); }
        private void FfbCurveY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); FfbCurveY2Value.Text = $"{v}"; _data.FfbCurveY2 = v; _device.WriteSetting("base-ffb-curve-y2", v); _plugin.SaveSettings(); }
        private void FfbCurveY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); FfbCurveY3Value.Text = $"{v}"; _data.FfbCurveY3 = v; _device.WriteSetting("base-ffb-curve-y3", v); _plugin.SaveSettings(); }
        private void FfbCurveY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); FfbCurveY4Value.Text = $"{v}"; _data.FfbCurveY4 = v; _device.WriteSetting("base-ffb-curve-y4", v); _plugin.SaveSettings(); }
        private void FfbCurveY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); FfbCurveY5Value.Text = $"{v}"; _data.FfbCurveY5 = v; _device.WriteSetting("base-ffb-curve-y5", v); _plugin.SaveSettings(); }

        // ===== Bluetooth + Base Calibration =====

        private void BluetoothCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = BluetoothCheck.IsChecked == true ? 0 : 85;
            _data.BleMode = val;
            _device.WriteSetting("main-set-ble-mode", val);
            _plugin.SaveSettings();
        }

        private void BaseCalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            _device.WriteSetting("base-calibration", 1);
            BaseCalibrateStatus.Text = "Calibration sent";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, _) => { BaseCalibrateStatus.Text = ""; ((DispatcherTimer)s!).Stop(); };
            timer.Start();
        }

        // ===== Handbrake Range + Curve + Calibration =====

        private static readonly int[][] HbCurvePresets =
        {
            new[] { 20, 40,  60,  80, 100 }, // Linear
            new[] {  8, 24,  76,  92, 100 }, // S Curve
            new[] {  6, 14,  28,  54, 100 }, // Exponential
            new[] { 46, 72,  86,  94, 100 }, // Parabolic
        };

        private void ApplyHbCurvePreset(int[] p)
        {
            _suppressEvents = true;
            HbY1Slider.Value = p[0]; HbY1Value.Text = $"{p[0]}"; _data.HandbrakeCurve[0] = p[0];
            HbY2Slider.Value = p[1]; HbY2Value.Text = $"{p[1]}"; _data.HandbrakeCurve[1] = p[1];
            HbY3Slider.Value = p[2]; HbY3Value.Text = $"{p[2]}"; _data.HandbrakeCurve[2] = p[2];
            HbY4Slider.Value = p[3]; HbY4Value.Text = $"{p[3]}"; _data.HandbrakeCurve[3] = p[3];
            HbY5Slider.Value = p[4]; HbY5Value.Text = $"{p[4]}"; _data.HandbrakeCurve[4] = p[4];
            _suppressEvents = false;
            for (int i = 0; i < 5; i++)
                _device.WriteFloat($"handbrake-y{i + 1}", p[i]);
            _plugin.SaveSettings();
        }

        private void HbCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[0]);
        private void HbCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[1]);
        private void HbCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[2]);
        private void HbCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[3]);

        private void HandbrakeMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v > _data.HandbrakeMax) { v = _data.HandbrakeMax; _suppressEvents = true; HandbrakeMinSlider.Value = v; _suppressEvents = false; } HandbrakeMinValue.Text = $"{v}%"; _data.HandbrakeMin = v; _device.WriteSetting("handbrake-min", v); _plugin.SaveSettings(); }
        private void HandbrakeMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v < _data.HandbrakeMin) { v = _data.HandbrakeMin; _suppressEvents = true; HandbrakeMaxSlider.Value = v; _suppressEvents = false; } HandbrakeMaxValue.Text = $"{v}%"; _data.HandbrakeMax = v; _device.WriteSetting("handbrake-max", v); _plugin.SaveSettings(); }
        private void HbY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); HbY1Value.Text = $"{v}"; _data.HandbrakeCurve[0] = v; _device.WriteFloat("handbrake-y1", v); _plugin.SaveSettings(); }
        private void HbY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); HbY2Value.Text = $"{v}"; _data.HandbrakeCurve[1] = v; _device.WriteFloat("handbrake-y2", v); _plugin.SaveSettings(); }
        private void HbY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); HbY3Value.Text = $"{v}"; _data.HandbrakeCurve[2] = v; _device.WriteFloat("handbrake-y3", v); _plugin.SaveSettings(); }
        private void HbY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); HbY4Value.Text = $"{v}"; _data.HandbrakeCurve[3] = v; _device.WriteFloat("handbrake-y4", v); _plugin.SaveSettings(); }
        private void HbY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); HbY5Value.Text = $"{v}"; _data.HandbrakeCurve[4] = v; _device.WriteFloat("handbrake-y5", v); _plugin.SaveSettings(); }

        private void HbCalStartButton_Click(object sender, RoutedEventArgs e)
        {
            _device.WriteSetting("handbrake-cal-start", 1);
            HbCalStatus.Text = "Calibrating — pull fully then stop";
        }

        private void HbCalStopButton_Click(object sender, RoutedEventArgs e)
        {
            _device.WriteSetting("handbrake-cal-stop", 1);
            HbCalStatus.Text = "Done";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, _) => { HbCalStatus.Text = ""; ((DispatcherTimer)s!).Stop(); };
            timer.Start();
        }

        // ===== Pedals Tab =====

        // Presets: [Y1, Y2, Y3, Y4, Y5]
        private static readonly int[][] PedalCurvePresets =
        {
            new[] { 20, 40,  60,  80, 100 }, // Linear
            new[] {  8, 24,  76,  92, 100 }, // S Curve
            new[] {  6, 14,  28,  54, 100 }, // Exponential
            new[] { 46, 72,  86,  94, 100 }, // Parabolic
        };

        private void RefreshPedalsTab()
        {
            bool detected = _plugin.IsPedalsDetected;
            PedalsTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;
            if (!detected) return;

            ThrottleDirCheck.IsChecked = _data.PedalsThrottleDir != 0;
            ThrottleMinSlider.Value = Clamp(_data.PedalsThrottleMin, 0, 100);
            ThrottleMinValue.Text = $"{_data.PedalsThrottleMin}%";
            ThrottleMaxSlider.Value = Clamp(_data.PedalsThrottleMax, 0, 100);
            ThrottleMaxValue.Text = $"{_data.PedalsThrottleMax}%";
            SetSliderRaw(ThrottleY1Slider, ThrottleY1Value, _data.PedalsThrottleCurve[0], 0, 100, "");
            SetSliderRaw(ThrottleY2Slider, ThrottleY2Value, _data.PedalsThrottleCurve[1], 0, 100, "");
            SetSliderRaw(ThrottleY3Slider, ThrottleY3Value, _data.PedalsThrottleCurve[2], 0, 100, "");
            SetSliderRaw(ThrottleY4Slider, ThrottleY4Value, _data.PedalsThrottleCurve[3], 0, 100, "");
            SetSliderRaw(ThrottleY5Slider, ThrottleY5Value, _data.PedalsThrottleCurve[4], 0, 100, "");

            BrakeDirCheck.IsChecked = _data.PedalsBrakeDir != 0;
            BrakeMinSlider.Value = Clamp(_data.PedalsBrakeMin, 0, 100);
            BrakeMinValue.Text = $"{_data.PedalsBrakeMin}%";
            BrakeMaxSlider.Value = Clamp(_data.PedalsBrakeMax, 0, 100);
            BrakeMaxValue.Text = $"{_data.PedalsBrakeMax}%";
            BrakeAngleRatioSlider.Value = Clamp(_data.PedalsBrakeAngleRatio, 0, 100);
            BrakeAngleRatioValue.Text = $"{_data.PedalsBrakeAngleRatio}%";
            SetSliderRaw(BrakeY1Slider, BrakeY1Value, _data.PedalsBrakeCurve[0], 0, 100, "");
            SetSliderRaw(BrakeY2Slider, BrakeY2Value, _data.PedalsBrakeCurve[1], 0, 100, "");
            SetSliderRaw(BrakeY3Slider, BrakeY3Value, _data.PedalsBrakeCurve[2], 0, 100, "");
            SetSliderRaw(BrakeY4Slider, BrakeY4Value, _data.PedalsBrakeCurve[3], 0, 100, "");
            SetSliderRaw(BrakeY5Slider, BrakeY5Value, _data.PedalsBrakeCurve[4], 0, 100, "");

            ClutchDirCheck.IsChecked = _data.PedalsClutchDir != 0;
            ClutchMinSlider.Value = Clamp(_data.PedalsClutchMin, 0, 100);
            ClutchMinValue.Text = $"{_data.PedalsClutchMin}%";
            ClutchMaxSlider.Value = Clamp(_data.PedalsClutchMax, 0, 100);
            ClutchMaxValue.Text = $"{_data.PedalsClutchMax}%";
            SetSliderRaw(ClutchY1Slider, ClutchY1Value, _data.PedalsClutchCurve[0], 0, 100, "");
            SetSliderRaw(ClutchY2Slider, ClutchY2Value, _data.PedalsClutchCurve[1], 0, 100, "");
            SetSliderRaw(ClutchY3Slider, ClutchY3Value, _data.PedalsClutchCurve[2], 0, 100, "");
            SetSliderRaw(ClutchY4Slider, ClutchY4Value, _data.PedalsClutchCurve[3], 0, 100, "");
            SetSliderRaw(ClutchY5Slider, ClutchY5Value, _data.PedalsClutchCurve[4], 0, 100, "");
        }

        // ===== Hub Tab =====

        private void RefreshHubTab()
        {
            bool detected = _plugin.IsHubDetected;
            HubTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;
            if (!detected) return;

            // Pedals port: high byte >= 1 means connected (foxblat convention)
            UpdateHubPortIndicator(HubPedals1Dot, HubPedals1Label, _data.HubPedals1Power, isPedals: true);

            // Accessory ports: value <= 1 means connected
            UpdateHubPortIndicator(HubPort1Dot, HubPort1Label, _data.HubPort1Power, isPedals: false);
            UpdateHubPortIndicator(HubPort2Dot, HubPort2Label, _data.HubPort2Power, isPedals: false);
            UpdateHubPortIndicator(HubPort3Dot, HubPort3Label, _data.HubPort3Power, isPedals: false);
        }

        private static void UpdateHubPortIndicator(Ellipse dot, TextBlock label, int value, bool isPedals)
        {
            if (value < 0)
            {
                dot.Fill = Brushes.Gray;
                label.Text = "--";
                return;
            }

            bool connected = isPedals ? (value >> 8) >= 1 : value <= 1;
            dot.Fill = connected ? Brushes.LimeGreen : Brushes.Gray;
            label.Text = connected ? "Connected" : "Disconnected";
        }

        private void ApplyPedalCurvePreset(string pedal, int[] curve, int[] dataArray,
            Slider y1, Slider y2, Slider y3, Slider y4, Slider y5,
            TextBlock l1, TextBlock l2, TextBlock l3, TextBlock l4, TextBlock l5)
        {
            _suppressEvents = true;
            y1.Value = curve[0]; l1.Text = $"{curve[0]}"; dataArray[0] = curve[0];
            y2.Value = curve[1]; l2.Text = $"{curve[1]}"; dataArray[1] = curve[1];
            y3.Value = curve[2]; l3.Text = $"{curve[2]}"; dataArray[2] = curve[2];
            y4.Value = curve[3]; l4.Text = $"{curve[3]}"; dataArray[3] = curve[3];
            y5.Value = curve[4]; l5.Text = $"{curve[4]}"; dataArray[4] = curve[4];
            _suppressEvents = false;
            for (int i = 0; i < 5; i++)
                _device.WriteFloat($"pedals-{pedal}-y{i + 1}", curve[i]);
            _plugin.SaveSettings();
        }

        // Throttle presets
        private void ThrottleCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("throttle", PedalCurvePresets[0], _data.PedalsThrottleCurve, ThrottleY1Slider, ThrottleY2Slider, ThrottleY3Slider, ThrottleY4Slider, ThrottleY5Slider, ThrottleY1Value, ThrottleY2Value, ThrottleY3Value, ThrottleY4Value, ThrottleY5Value);
        private void ThrottleCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("throttle", PedalCurvePresets[1], _data.PedalsThrottleCurve, ThrottleY1Slider, ThrottleY2Slider, ThrottleY3Slider, ThrottleY4Slider, ThrottleY5Slider, ThrottleY1Value, ThrottleY2Value, ThrottleY3Value, ThrottleY4Value, ThrottleY5Value);
        private void ThrottleCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("throttle", PedalCurvePresets[2], _data.PedalsThrottleCurve, ThrottleY1Slider, ThrottleY2Slider, ThrottleY3Slider, ThrottleY4Slider, ThrottleY5Slider, ThrottleY1Value, ThrottleY2Value, ThrottleY3Value, ThrottleY4Value, ThrottleY5Value);
        private void ThrottleCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("throttle", PedalCurvePresets[3], _data.PedalsThrottleCurve, ThrottleY1Slider, ThrottleY2Slider, ThrottleY3Slider, ThrottleY4Slider, ThrottleY5Slider, ThrottleY1Value, ThrottleY2Value, ThrottleY3Value, ThrottleY4Value, ThrottleY5Value);

        // Brake presets
        private void BrakeCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("brake", PedalCurvePresets[0], _data.PedalsBrakeCurve, BrakeY1Slider, BrakeY2Slider, BrakeY3Slider, BrakeY4Slider, BrakeY5Slider, BrakeY1Value, BrakeY2Value, BrakeY3Value, BrakeY4Value, BrakeY5Value);
        private void BrakeCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("brake", PedalCurvePresets[1], _data.PedalsBrakeCurve, BrakeY1Slider, BrakeY2Slider, BrakeY3Slider, BrakeY4Slider, BrakeY5Slider, BrakeY1Value, BrakeY2Value, BrakeY3Value, BrakeY4Value, BrakeY5Value);
        private void BrakeCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("brake", PedalCurvePresets[2], _data.PedalsBrakeCurve, BrakeY1Slider, BrakeY2Slider, BrakeY3Slider, BrakeY4Slider, BrakeY5Slider, BrakeY1Value, BrakeY2Value, BrakeY3Value, BrakeY4Value, BrakeY5Value);
        private void BrakeCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("brake", PedalCurvePresets[3], _data.PedalsBrakeCurve, BrakeY1Slider, BrakeY2Slider, BrakeY3Slider, BrakeY4Slider, BrakeY5Slider, BrakeY1Value, BrakeY2Value, BrakeY3Value, BrakeY4Value, BrakeY5Value);

        // Clutch presets
        private void ClutchCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("clutch", PedalCurvePresets[0], _data.PedalsClutchCurve, ClutchY1Slider, ClutchY2Slider, ClutchY3Slider, ClutchY4Slider, ClutchY5Slider, ClutchY1Value, ClutchY2Value, ClutchY3Value, ClutchY4Value, ClutchY5Value);
        private void ClutchCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("clutch", PedalCurvePresets[1], _data.PedalsClutchCurve, ClutchY1Slider, ClutchY2Slider, ClutchY3Slider, ClutchY4Slider, ClutchY5Slider, ClutchY1Value, ClutchY2Value, ClutchY3Value, ClutchY4Value, ClutchY5Value);
        private void ClutchCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("clutch", PedalCurvePresets[2], _data.PedalsClutchCurve, ClutchY1Slider, ClutchY2Slider, ClutchY3Slider, ClutchY4Slider, ClutchY5Slider, ClutchY1Value, ClutchY2Value, ClutchY3Value, ClutchY4Value, ClutchY5Value);
        private void ClutchCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("clutch", PedalCurvePresets[3], _data.PedalsClutchCurve, ClutchY1Slider, ClutchY2Slider, ClutchY3Slider, ClutchY4Slider, ClutchY5Slider, ClutchY1Value, ClutchY2Value, ClutchY3Value, ClutchY4Value, ClutchY5Value);

        // Throttle direction + range + curve sliders
        private void ThrottleDirCheck_Click(object sender, RoutedEventArgs e) { if (_suppressEvents) return; int v = ThrottleDirCheck.IsChecked == true ? 1 : 0; _data.PedalsThrottleDir = v; _device.WriteSetting("pedals-throttle-dir", v); _plugin.SaveSettings(); }
        private void ThrottleMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v > _data.PedalsThrottleMax) { v = _data.PedalsThrottleMax; _suppressEvents = true; ThrottleMinSlider.Value = v; _suppressEvents = false; } ThrottleMinValue.Text = $"{v}%"; _data.PedalsThrottleMin = v; _device.WriteSetting("pedals-throttle-min", v); _plugin.SaveSettings(); }
        private void ThrottleMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v < _data.PedalsThrottleMin) { v = _data.PedalsThrottleMin; _suppressEvents = true; ThrottleMaxSlider.Value = v; _suppressEvents = false; } ThrottleMaxValue.Text = $"{v}%"; _data.PedalsThrottleMax = v; _device.WriteSetting("pedals-throttle-max", v); _plugin.SaveSettings(); }
        private void ThrottleY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ThrottleY1Value.Text = $"{v}"; _data.PedalsThrottleCurve[0] = v; _device.WriteFloat("pedals-throttle-y1", v); _plugin.SaveSettings(); }
        private void ThrottleY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ThrottleY2Value.Text = $"{v}"; _data.PedalsThrottleCurve[1] = v; _device.WriteFloat("pedals-throttle-y2", v); _plugin.SaveSettings(); }
        private void ThrottleY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ThrottleY3Value.Text = $"{v}"; _data.PedalsThrottleCurve[2] = v; _device.WriteFloat("pedals-throttle-y3", v); _plugin.SaveSettings(); }
        private void ThrottleY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ThrottleY4Value.Text = $"{v}"; _data.PedalsThrottleCurve[3] = v; _device.WriteFloat("pedals-throttle-y4", v); _plugin.SaveSettings(); }
        private void ThrottleY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ThrottleY5Value.Text = $"{v}"; _data.PedalsThrottleCurve[4] = v; _device.WriteFloat("pedals-throttle-y5", v); _plugin.SaveSettings(); }

        // Throttle calibration
        private void ThrottleCalStartButton_Click(object sender, RoutedEventArgs e) { _device.WriteSetting("pedals-throttle-cal-start", 1); }
        private void ThrottleCalStopButton_Click(object sender, RoutedEventArgs e) { _device.WriteSetting("pedals-throttle-cal-stop", 1); }

        // Brake direction + range + curve sliders
        private void BrakeDirCheck_Click(object sender, RoutedEventArgs e) { if (_suppressEvents) return; int v = BrakeDirCheck.IsChecked == true ? 1 : 0; _data.PedalsBrakeDir = v; _device.WriteSetting("pedals-brake-dir", v); _plugin.SaveSettings(); }
        private void BrakeAngleRatioSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeAngleRatioValue.Text = $"{v}%"; _data.PedalsBrakeAngleRatio = v; _device.WriteFloat("pedals-brake-angle-ratio", v); _plugin.SaveSettings(); }
        private void BrakeMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v > _data.PedalsBrakeMax) { v = _data.PedalsBrakeMax; _suppressEvents = true; BrakeMinSlider.Value = v; _suppressEvents = false; } BrakeMinValue.Text = $"{v}%"; _data.PedalsBrakeMin = v; _device.WriteSetting("pedals-brake-min", v); _plugin.SaveSettings(); }
        private void BrakeMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v < _data.PedalsBrakeMin) { v = _data.PedalsBrakeMin; _suppressEvents = true; BrakeMaxSlider.Value = v; _suppressEvents = false; } BrakeMaxValue.Text = $"{v}%"; _data.PedalsBrakeMax = v; _device.WriteSetting("pedals-brake-max", v); _plugin.SaveSettings(); }
        private void BrakeY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeY1Value.Text = $"{v}"; _data.PedalsBrakeCurve[0] = v; _device.WriteFloat("pedals-brake-y1", v); _plugin.SaveSettings(); }
        private void BrakeY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeY2Value.Text = $"{v}"; _data.PedalsBrakeCurve[1] = v; _device.WriteFloat("pedals-brake-y2", v); _plugin.SaveSettings(); }
        private void BrakeY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeY3Value.Text = $"{v}"; _data.PedalsBrakeCurve[2] = v; _device.WriteFloat("pedals-brake-y3", v); _plugin.SaveSettings(); }
        private void BrakeY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeY4Value.Text = $"{v}"; _data.PedalsBrakeCurve[3] = v; _device.WriteFloat("pedals-brake-y4", v); _plugin.SaveSettings(); }
        private void BrakeY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeY5Value.Text = $"{v}"; _data.PedalsBrakeCurve[4] = v; _device.WriteFloat("pedals-brake-y5", v); _plugin.SaveSettings(); }

        // Brake calibration
        private void BrakeCalStartButton_Click(object sender, RoutedEventArgs e) { _device.WriteSetting("pedals-brake-cal-start", 1); }
        private void BrakeCalStopButton_Click(object sender, RoutedEventArgs e) { _device.WriteSetting("pedals-brake-cal-stop", 1); }

        // Clutch direction + range + curve sliders
        private void ClutchDirCheck_Click(object sender, RoutedEventArgs e) { if (_suppressEvents) return; int v = ClutchDirCheck.IsChecked == true ? 1 : 0; _data.PedalsClutchDir = v; _device.WriteSetting("pedals-clutch-dir", v); _plugin.SaveSettings(); }
        private void ClutchMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v > _data.PedalsClutchMax) { v = _data.PedalsClutchMax; _suppressEvents = true; ClutchMinSlider.Value = v; _suppressEvents = false; } ClutchMinValue.Text = $"{v}%"; _data.PedalsClutchMin = v; _device.WriteSetting("pedals-clutch-min", v); _plugin.SaveSettings(); }
        private void ClutchMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v < _data.PedalsClutchMin) { v = _data.PedalsClutchMin; _suppressEvents = true; ClutchMaxSlider.Value = v; _suppressEvents = false; } ClutchMaxValue.Text = $"{v}%"; _data.PedalsClutchMax = v; _device.WriteSetting("pedals-clutch-max", v); _plugin.SaveSettings(); }
        private void ClutchY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ClutchY1Value.Text = $"{v}"; _data.PedalsClutchCurve[0] = v; _device.WriteFloat("pedals-clutch-y1", v); _plugin.SaveSettings(); }
        private void ClutchY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ClutchY2Value.Text = $"{v}"; _data.PedalsClutchCurve[1] = v; _device.WriteFloat("pedals-clutch-y2", v); _plugin.SaveSettings(); }
        private void ClutchY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ClutchY3Value.Text = $"{v}"; _data.PedalsClutchCurve[2] = v; _device.WriteFloat("pedals-clutch-y3", v); _plugin.SaveSettings(); }
        private void ClutchY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ClutchY4Value.Text = $"{v}"; _data.PedalsClutchCurve[3] = v; _device.WriteFloat("pedals-clutch-y4", v); _plugin.SaveSettings(); }
        private void ClutchY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ClutchY5Value.Text = $"{v}"; _data.PedalsClutchCurve[4] = v; _device.WriteFloat("pedals-clutch-y5", v); _plugin.SaveSettings(); }

        // Clutch calibration
        private void ClutchCalStartButton_Click(object sender, RoutedEventArgs e) { _device.WriteSetting("pedals-clutch-cal-start", 1); }
        private void ClutchCalStopButton_Click(object sender, RoutedEventArgs e) { _device.WriteSetting("pedals-clutch-cal-stop", 1); }

        // ===== Options tab =====

        private void AutoApplyProfileCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.AutoApplyProfileOnLaunch = AutoApplyProfileCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void LimitWheelUpdatesCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.LimitWheelUpdates = LimitWheelUpdatesCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void AlwaysResendBitmaskCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.AlwaysResendBitmask = AlwaysResendBitmaskCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void EnableAb9Check_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            // SetAb9Enabled writes the setting, persists, and disconnects on
            // false — no separate SaveSettings call needed.
            _plugin.SetAb9Enabled(EnableAb9Check.IsChecked == true);
        }

        private void ClearAllSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will permanently delete all plugin settings and profiles.\n\nAre you sure?",
                "Clear All Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            _plugin.ClearSettings();

            _suppressEvents = true;
            try
            {
                AutoApplyProfileCheck.IsChecked = _plugin.Settings.AutoApplyProfileOnLaunch;
                LimitWheelUpdatesCheck.IsChecked = _plugin.Settings.LimitWheelUpdates;
                ConnectionToggle.IsChecked = _plugin.Settings.ConnectionEnabled;
                ProfileListControl.DataContext = null;
                ProfileListControl.DataContext = _plugin.ProfileStore;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        // ===== Profile system (SimHub native) =====

        private MozaProfileStore ProfileStore => _plugin.ProfileStore;

        private void InitProfilesTab()
        {
            ProfileListControl.DataContext = ProfileStore;
        }

        // ===== Telemetry (Options tab) =====

        private bool _telemetryUIInitialized;

        private void InitTelemetryTab()
        {
            if (_telemetryUIInitialized) return;
            _telemetryUIInitialized = true;

            _suppressEvents = true;
            try
            {
                var s = _plugin.Settings;
                UploadDashboardCheck.IsChecked = s.TelemetryUploadDashboard;
                DownloadDashboardCheck.IsChecked = s.TelemetryDownloadDashboard;
                FirmwareEraCombo.SelectedIndex = (int)s.TelemetryWheelEra;
                // Hidden for now: v2 telemetry pipeline UI not shown
                // UseNewTelemetryPipelineCheck.IsChecked = s.UseNewTelemetryPipeline;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void UploadDashboard_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.TelemetryUploadDashboard = UploadDashboardCheck.IsChecked == true;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

        private void DownloadDashboard_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.TelemetryDownloadDashboard = DownloadDashboardCheck.IsChecked == true;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

        private void FirmwareEra_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            // MozaWheelEra values are contiguous 0..3 matching ComboBox indices —
            // direct cast. -1 (no selection) clamps to Auto so the plugin stays
            // in a valid state even if the combo is somehow deselected.
            int idx = FirmwareEraCombo.SelectedIndex;
            _plugin.Settings.TelemetryWheelEra = (idx >= 0 && idx <= 3)
                ? (MozaWheelEra)idx
                : MozaWheelEra.Auto;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

        // Hidden for now: v2 telemetry pipeline UI not shown
        // private void UseNewTelemetryPipeline_Changed(object sender, RoutedEventArgs e)
        // {
        //     if (_suppressEvents) return;
        //     _plugin.Settings.UseNewTelemetryPipeline = UseNewTelemetryPipelineCheck.IsChecked == true;
        //     _plugin.SaveSettings();
        //     // The pipeline implementation is selected at MozaPlugin.Init() time. Toggling
        //     // mid-session does not swap implementations; user must restart SimHub. We still
        //     // call RestartTelemetry() so the active pipeline goes through Stop/Start.
        //     _plugin.RestartTelemetry();
        // }

        // ── Diagnostics tab ─────────────────────────────────────────────
        private void RefreshDiagnosticsTab()
        {
            if (DiagWheelIdentityBox == null) return;
            DiagPluginBox.Text = BuildPluginInfoText();
            DiagWheelIdentityBox.Text = BuildWheelIdentityText();
            DiagDisplayIdentityBox.Text = BuildDisplayIdentityText();
            DiagDashboardStateBox.Text = BuildDashboardStateText();
            DiagTileServerBox.Text = BuildTileServerText();
            DiagSessionBox.Text = BuildSessionStateText();
            if (DiagWheelCatalogBox != null)
                DiagWheelCatalogBox.Text = BuildWheelCatalogText();
            if (DiagSubscriptionBox != null)
                DiagSubscriptionBox.Text = BuildSubscriptionText();
            if (DiagSubscriptionResponseBox != null)
                DiagSubscriptionResponseBox.Text = BuildSubscriptionResponseText();
        }

        private string BuildPluginInfoText()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"Version:        {GetPluginVersion()}");
            return sb.ToString();
        }

        private string BuildWheelIdentityText()
        {
            var d = _data;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Model:          {Blank(d.WheelModelName)}");
            sb.AppendLine($"FW (sw):        {Blank(d.WheelSwVersion)}");
            sb.AppendLine($"HW version:     {Blank(d.WheelHwVersion)}");
            sb.AppendLine($"HW sub:         {Blank(d.WheelHwSubVersion)}");
            sb.AppendLine($"Serial:         {Redact(d.WheelSerialNumber)}");
            sb.AppendLine($"Sub-devices:    {d.WheelSubDeviceCount}");
            sb.AppendLine($"Device presence:0x{d.WheelDevicePresence:X2}");
            sb.AppendLine($"Device type:    {Hex(d.WheelDeviceType)}");
            sb.AppendLine($"Capabilities:   {Hex(d.WheelCapabilities)}");
            sb.AppendLine($"MCU UID:        {RedactBytes(d.WheelMcuUid)}");
            sb.Append    ($"Identity-11:    {Hex(d.WheelIdentity11)}");
            return sb.ToString();
        }

        private string BuildDisplayIdentityText()
        {
            var d = _data;
            if (string.IsNullOrEmpty(d.DisplayModelName) && d.DisplayMcuUid.Length == 0)
                return "(display sub-device not probed or not present)";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Model:          {Blank(d.DisplayModelName)}");
            sb.AppendLine($"FW (sw):        {Blank(d.DisplaySwVersion)}");
            sb.AppendLine($"HW version:     {Blank(d.DisplayHwVersion)}");
            sb.AppendLine($"Serial:         {Redact(d.DisplaySerialNumber)}");
            sb.AppendLine($"Sub-devices:    {d.DisplaySubDeviceCount}");
            sb.AppendLine($"Device presence:0x{d.DisplayDevicePresence:X2}");
            sb.AppendLine($"Device type:    {Hex(d.DisplayDeviceType)}");
            sb.AppendLine($"Capabilities:   {Hex(d.DisplayCapabilities)}");
            sb.AppendLine($"MCU UID:        {RedactBytes(d.DisplayMcuUid)}");
            sb.Append    ($"Identity-11:    {Hex(d.DisplayIdentity11)}");
            return sb.ToString();
        }

        private string BuildDashboardStateText()
        {
            var ts = _plugin.TelemetrySender;
            var state = _plugin.WheelStateForDiagnostics;
            if (state == null) return "(no configJson state received yet)";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"TitleId:        {state.TitleId}");
            sb.AppendLine($"displayVersion: {state.DisplayVersion}");
            sb.AppendLine($"resetVersion:   {state.ResetVersion}");
            sb.AppendLine($"sortTag:        {state.SortTag}");
            sb.AppendLine($"rootDirPath:    {Blank(state.RootDirPath)}");
            sb.AppendLine($"rootPath:       {Blank(state.RootPath)}");
            sb.AppendLine($"configJsonList ({state.ConfigJsonList.Count}): {JoinList(state.ConfigJsonList)}");
            sb.AppendLine($"imageRefMap:    {state.ImageRefMap.Count} entries");
            sb.AppendLine($"fontRefMap:     {state.FontRefMap.Count} entries");
            sb.AppendLine($"imagePath:      {state.ImagePath.Count} entries");
            sb.AppendLine($"captured at:    {state.CapturedAt:HH:mm:ss}");
            sb.AppendLine(Build28xRawLine());
            sb.AppendLine();
            sb.AppendLine($"-- Enabled dashboards ({state.EnabledDashboards.Count}) --");
            foreach (var d in state.EnabledDashboards)
            {
                sb.AppendLine($"  • {d.Title} / dirName={d.DirName} / id={TruncateId(d.Id)}");
                if (!string.IsNullOrEmpty(d.LastModified))
                    sb.AppendLine($"      lastModified: {d.LastModified}");
                if (d.IdealDeviceInfos.Count > 0)
                {
                    foreach (var info in d.IdealDeviceInfos)
                        sb.AppendLine($"      device: id={info.DeviceId} hw={info.HardwareVersion} product={info.ProductType}");
                }
            }
            sb.Append($"-- Disabled dashboards ({state.DisabledDashboards.Count}) --");
            foreach (var d in state.DisabledDashboards)
                sb.Append($"\n  • {d.Title} / {d.DirName}");
            return sb.ToString();
        }

        /// <summary>
        /// Render the wheel's most recent 28:00 / 28:01 reply bytes raw,
        /// with age in milliseconds. Semantics not decoded — captured for
        /// offline correlation against game state. See plan
        /// /home/rorth/.claude/plans/drifting-moseying-cook.md Phase 0.
        /// </summary>
        private string Build28xRawLine()
        {
            var d = _plugin.Data;
            if (d == null) return "wheel 28:xx raw: (no data)";
            string b00 = d.Last28x00ByteValid
                ? $"0x{d.Last28x00Byte5:X2}" : "(none)";
            string b01 = d.Last28x01BytesValid
                ? $"0x{d.Last28x01Byte4:X2} 0x{d.Last28x01Byte5:X2}"
                : "(none)";
            string age;
            if (d.Last28xReplyTickMs == 0)
                age = "never";
            else
            {
                int dt = unchecked(Environment.TickCount - d.Last28xReplyTickMs);
                age = dt < 0 ? "?" : $"{dt} ms";
            }
            return $"wheel 28:xx raw: 28:00=[{b00}]  28:01=[{b01}]  age={age}";
        }

        private string BuildTileServerText()
        {
            var tile = _plugin.TileServerStateForDiagnostics;
            if (tile == null)
                return "(no inbound tile-server blob received — plugin PUSHES empty state on 0x03; wheel doesn't push back in current captures)";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"root:          {Blank(tile.Root)}");
            sb.AppendLine($"version:       {tile.Version}");
            sb.AppendLine($"any populated: {tile.AnyPopulated}");
            foreach (var kv in tile.Games)
            {
                var g = kv.Value;
                sb.Append($"\n[{kv.Key}] populated={g.Populated} map_version={g.MapVersion} " +
                          $"tile_size={g.TileSize} layers={g.LayersCount} name={Blank(g.Name)}");
            }
            return sb.ToString();
        }

        private string BuildSessionStateText()
        {
            var ts = _plugin.TelemetrySender;
            if (ts == null && !_plugin.TelemetryEnabledForDiagnostics)
                return "(telemetry not running)";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Pipeline:           {(ts != null ? "OLD (TelemetrySender)" : "NEW (Telemetry2 host)")}");
            sb.AppendLine($"Enabled:            {_plugin.TelemetryEnabledForDiagnostics}");
            sb.AppendLine($"FramesSent:         {_plugin.FramesSentForDiagnostics}");
            var budget = _plugin.SerialBudgetForDiagnostics;
            var errs = _plugin.SerialWireErrorsForDiagnostics;
            int budgetTargetBytes = global::MozaPlugin.Protocol.WriteBudget.TargetBytesPerWindow;
            sb.AppendLine(
                $"Bandwidth:          out={budget.BytesLastSec,5} B/s ({budget.PercentBudget,3}% of {budgetTargetBytes}B target, peak={budget.PeakBurstBytes})");
            sb.AppendLine(
                $"WireErrors:         drops={errs.FramesDropped} cksumFail={errs.ChecksumFailures} resync={errs.FrameStartScanResyncs}");
            sb.AppendLine($"DisplayDetected:    {(ts?.DisplayDetected ?? _plugin.IsDisplayDetected)}");
            sb.AppendLine($"DisplayModelName:   {Blank(ts?.DisplayModelName ?? _plugin.DisplayModelName)}");
            sb.AppendLine($"WheelEra:           {_plugin.Settings.TelemetryWheelEra}");
            if (ts != null)
            {
                var p = ts.Policy;
                sb.AppendLine($"PolicyEra:          {p.Era}{(p.IsAuto ? " (auto)" : "")}");
                sb.AppendLine($"TierDefSession:     {p.TierDefSession}");
                sb.AppendLine($"Encoding:           {p.Encoding}");
                sb.AppendLine($"PreambleEverySend:  {p.SendV2PreambleEverySend}");
                sb.AppendLine($"BlindRetransmit:    {p.BlindRetransmitTierDef}");
                sb.AppendLine($"UploadWireFormat:   {p.UploadWireFormat}");
                sb.AppendLine($"FlagByte:           0x{ts.FlagByte:X2}");
                sb.AppendLine($"UploadDashboard:    {ts.UploadDashboard}");
                sb.Append    ($"Profile:            {ts.Profile?.Name ?? "(none)"}");
            }

            // Per-session chunk counters
            var counts = _plugin.SessionCountsForDiagnostics;
            if (counts != null && counts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("Session traffic (in/out chunks):");
                var keys = new System.Collections.Generic.List<byte>(counts.Keys);
                keys.Sort();
                foreach (var k in keys)
                {
                    var v = counts[k];
                    sb.AppendLine($"  0x{k:X2}:  in={v.In,-5} out={v.Out}");
                }
            }
            return sb.ToString();
        }

        private string BuildWheelCatalogText()
        {
            var sb = new System.Text.StringBuilder();

            // Parser internals — top-of-section so the cause of an empty catalog
            // is visible at a glance. Buffer>0 + count=0 = chunks fed but no URL
            // records found (wrong session? wrong record format?). CrcRejects>0
            // + count=0 = chunks reaching us but failing CRC. ActivityMs<0 = no
            // chunks ever delivered to parser.
            var pd = _plugin.CatalogParserDiagnostics;
            string activity = pd.LastActivityMsAgo < 0
                ? "never"
                : $"{pd.LastActivityMsAgo} ms ago";
            sb.AppendLine(
                $"Parser: buf={pd.BufferBytes}B (last parsed {pd.LastParsedBufferBytes}B) " +
                $"crcRejects={pd.CrcRejects} lastActivity={activity}");

            var catalog = _plugin.WheelChannelCatalogForDiagnostics;
            if (catalog != null && catalog.Count > 0)
            {
                sb.AppendLine($"{catalog.Count} channels advertised by wheel:");
                for (int i = 0; i < catalog.Count; i++)
                {
                    string url = catalog[i] ?? "";
                    sb.AppendLine($"  [{i + 1,2}]  {url}");
                }
                return sb.ToString().TrimEnd();
            }

            // Fallback: derive the catalog from the active subscription's
            // channel list. The subscription was built with the wheel's
            // catalog at emit time, so its URLs reflect the mapping we sent —
            // robust against the catalog parser losing its in-memory state
            // mid-session for any reason.
            //
            // The subscription's diag.Channels uses a SEQUENTIAL idx (1..N
            // across all tiers/buckets) rather than the wheel-catalog idx,
            // so a single URL appears multiple times when the host duplicates
            // channels across page-broadcast buckets (Grids = 4 buckets ×
            // 20 channels = 80 entries). Dedup by URL preserving first-seen
            // order so the listed catalog matches the wheel-side count.
            var sub = _plugin.SubscriptionForDiagnostics;
            if (sub != null && sub.Channels != null && sub.Channels.Count > 0)
            {
                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ordered = new System.Collections.Generic.List<string>();
                foreach (var ch in sub.Channels)
                {
                    if (string.IsNullOrEmpty(ch.Url)) continue;
                    if (seen.Add(ch.Url)) ordered.Add(ch.Url);
                }
                if (ordered.Count > 0)
                {
                    sb.AppendLine($"{ordered.Count} channels (from last subscription — catalog parser empty):");
                    for (int i = 0; i < ordered.Count; i++)
                        sb.AppendLine($"  [{i + 1,2}]  {ordered[i]}");
                    return sb.ToString().TrimEnd();
                }
            }

            return "(no channel catalog received from wheel yet)";
        }

        private string BuildSubscriptionText()
        {
            var sub = _plugin.SubscriptionForDiagnostics;
            if (sub == null) return "(no subscription sent yet)";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Sent on session {sub.SessionByte} format={sub.Format}  at {sub.CapturedAt:HH:mm:ss}");
            if (sub.PreambleBytes.Length > 0)
                sb.AppendLine($"Preamble ({sub.PreambleBytes.Length}B): {BitConverter.ToString(sub.PreambleBytes).Replace('-', ' ')}");
            sb.AppendLine($"Body ({sub.BodyBytes.Length}B): {BitConverter.ToString(sub.BodyBytes).Replace('-', ' ')}");
            if (sub.Channels.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Channels ({sub.Channels.Count}):");
                foreach (var ch in sub.Channels)
                    sb.AppendLine($"  idx={ch.Idx,2}  comp=0x{ch.Comp:X2}  width={ch.Width,3}  {ch.Url}");
            }
            return sb.ToString().TrimEnd();
        }

        private string BuildSubscriptionResponseText()
        {
            var chunks = _plugin.SubscriptionResponseForDiagnostics;
            if (chunks == null || chunks.Count == 0)
                return "(no inbound chunks captured on session 0x02 in 5s window after subscription)";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{chunks.Count} chunks captured on session 0x02 after most-recent subscription:");
            int total = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                total += c.Length;
                int show = System.Math.Min(c.Length, 80);
                string hex = BitConverter.ToString(c, 0, show).Replace('-', ' ');
                string ellip = c.Length > show ? " …" : "";
                sb.AppendLine($"  [{i,2}] {c.Length,3}B: {hex}{ellip}");
            }
            sb.AppendLine();
            sb.AppendLine($"Concat ({total}B): {BuildConcatHex(chunks, 200)}");
            return sb.ToString().TrimEnd();
        }

        private static string BuildConcatHex(System.Collections.Generic.IReadOnlyList<byte[]> chunks, int max)
        {
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (var c in chunks)
            {
                foreach (var b in c)
                {
                    if (n++ >= max) { sb.Append(" …"); return sb.ToString(); }
                    sb.Append(b.ToString("X2"));
                    sb.Append(' ');
                }
            }
            return sb.ToString().TrimEnd();
        }

        private void DiagCopyAll_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try { System.Windows.Clipboard.SetText(BuildDiagnosticsDump()); }
            catch { /* clipboard may be contested under Wine */ }
        }

        private string BuildDiagnosticsDump()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Plugin ===");
            sb.AppendLine(DiagPluginBox.Text);
            sb.AppendLine();
            sb.AppendLine("=== Wheel identity ===");
            sb.AppendLine(DiagWheelIdentityBox.Text);
            sb.AppendLine();
            sb.AppendLine("=== Display sub-device identity ===");
            sb.AppendLine(DiagDisplayIdentityBox.Text);
            sb.AppendLine();
            sb.AppendLine("=== Dashboard state ===");
            sb.AppendLine(DiagDashboardStateBox.Text);
            sb.AppendLine();
            sb.AppendLine("=== Tile-server state ===");
            sb.AppendLine(DiagTileServerBox.Text);
            sb.AppendLine();
            sb.AppendLine("=== Session state ===");
            sb.AppendLine(DiagSessionBox.Text);
            sb.AppendLine();
            sb.AppendLine("=== Wheel channel catalog ===");
            sb.AppendLine(BuildWheelCatalogText());
            sb.AppendLine();
            sb.AppendLine("=== Last subscription sent ===");
            sb.AppendLine(BuildSubscriptionText());
            sb.AppendLine();
            sb.AppendLine("=== Wheel response on 0x02 (post-subscription window) ===");
            sb.AppendLine(BuildSubscriptionResponseText());
            return sb.ToString();
        }

        // ── Serial traffic capture ───────────────────────────────────────
        // Last buffer rendered to text on Stop. Held so Export and Copy
        // operate on the same snapshot regardless of how long the user
        // takes to click them; cleared on next Start.
        private string? _serialCaptureRendered;
        private System.Collections.Generic.IReadOnlyList<SerialTrafficCapture.Entry>? _serialCaptureSnapshot;

        private void SerialCaptureToggle_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var cap = SerialTrafficCapture.Instance;
            if (!cap.Enabled)
            {
                cap.Start();
                _serialCaptureRendered = null;
                _serialCaptureSnapshot = null;
                SerialCaptureToggleButton.Content = "Stop capture";
                SerialCaptureOutputBox.Visibility = System.Windows.Visibility.Collapsed;
                SerialCaptureOutputBox.Text = string.Empty;
                SerialCaptureExportButton.IsEnabled = false;
                SerialCaptureCopyButton.IsEnabled = false;
                SerialCaptureStatusText.Text = "capturing… (open another tab and use the device, then come back to stop)";
                return;
            }

            var snap = cap.Stop();
            _serialCaptureSnapshot = snap;
            _serialCaptureRendered = SerialTrafficCapture.Format(snap);
            SerialCaptureToggleButton.Content = "Start capture";
            SerialCaptureStatusText.Text = $"stopped — {snap.Count} frames captured";
            SerialCaptureOutputBox.Text = _serialCaptureRendered;
            SerialCaptureOutputBox.Visibility = System.Windows.Visibility.Visible;
            SerialCaptureExportButton.IsEnabled = true;
            SerialCaptureCopyButton.IsEnabled = snap.Count > 0;
        }

        private void SerialCaptureCopy_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_serialCaptureRendered)) return;
            try { System.Windows.Clipboard.SetText(_serialCaptureRendered); }
            catch { /* clipboard contested; ignore */ }
        }

        private void StartCaptureOnNextLaunch_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.StartCaptureOnNextLaunch = StartCaptureOnNextLaunchCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void SerialCaptureExport_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            // Refuse export while capture is still running — user request: only
            // surface data after Stop. The button is disabled in that state too,
            // but guard here in case of a race.
            if (SerialTrafficCapture.Instance.Enabled) return;

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var modelSlug = BuildWheelModelFilenameSlug(_data?.WheelModelName);
            var prefix = string.IsNullOrEmpty(modelSlug) ? "" : modelSlug + "-";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"{prefix}moza-diagnostics-bundle-{stamp}.zip",
                Filter = "ZIP archive (*.zip)|*.zip",
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(System.Windows.Window.GetWindow(this)) != true) return;

            try
            {
                BuildAndWriteBundle(dlg.FileName);
                SerialCaptureStatusText.Text = $"exported to {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] Diagnostics export failed: {ex}");
                System.Windows.MessageBox.Show(
                    System.Windows.Window.GetWindow(this),
                    $"Export failed: {ex.Message}",
                    "MOZA Control",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private void BuildAndWriteBundle(string zipPath)
        {
            var captureText = _serialCaptureRendered ?? "(no capture buffer — click Start, exercise the device, then Stop)\n";
            var diagText = BuildDiagnosticsDump();

            // [Moza] log lines come straight from MozaLog's in-process ring
            // buffer — every plugin call site goes through that wrapper, so
            // the snapshot is guaranteed current without depending on
            // SimHub's rolling-file flush cadence or path layout.
            var logText = MozaLog.Snapshot();
            var logEntryCount = MozaLog.Count;

            // Header gives the receiver a quick idea of what the bundle contains
            // and when it was produced — saves a hunt through the files when a
            // user e-mails just the zip with no description.
            var manifest = new System.Text.StringBuilder();
            manifest.AppendLine("MOZA Control diagnostics bundle");
            manifest.AppendLine($"Created (local):     {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            manifest.AppendLine($"Plugin version:      {GetPluginVersion()}");
            manifest.AppendLine($"OS:                  {Environment.OSVersion}");
            manifest.AppendLine($"CLR:                 {Environment.Version}");
            manifest.AppendLine();
            manifest.AppendLine("Files:");
            manifest.AppendLine("  serial-capture.txt   – TX/RX frames captured between Start/Stop (timestamps in local time)");
            manifest.AppendLine("  diagnostics.txt      – snapshot of the Diagnostics tab text");
            manifest.AppendLine($"  moza-log.txt         – [Moza] log lines from MozaLog ring buffer ({logEntryCount} entries)");
            manifest.AppendLine();
            manifest.AppendLine("Capture summary:");
            if (_serialCaptureSnapshot != null)
            {
                manifest.AppendLine($"  Started (UTC):     {SerialTrafficCapture.Instance.StartedAtUtc:yyyy-MM-dd HH:mm:ss}");
                manifest.AppendLine($"  Frames:            {_serialCaptureSnapshot.Count}");
            }
            else
            {
                manifest.AppendLine("  (no capture buffer was active when this bundle was produced)");
            }

            // Write to a sibling .tmp first then atomic-rename on success so a
            // disk-full / permission failure mid-write doesn't leave a partial
            // zip at the user-visible path. Bug-report uploads have ended up
            // truncated this way before.
            string tmpPath = zipPath + ".tmp";
            try
            {
                using (var fs = new System.IO.FileStream(tmpPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                {
                    WriteZipEntry(zip, "manifest.txt", manifest.ToString());
                    WriteZipEntry(zip, "serial-capture.txt", captureText);
                    WriteZipEntry(zip, "diagnostics.txt", diagText);
                    WriteZipEntry(zip, "moza-log.txt", logText);
                }
                if (System.IO.File.Exists(zipPath)) System.IO.File.Delete(zipPath);
                System.IO.File.Move(tmpPath, zipPath);
            }
            catch
            {
                try { if (System.IO.File.Exists(tmpPath)) System.IO.File.Delete(tmpPath); } catch { }
                throw;
            }
        }

        private static void WriteZipEntry(System.IO.Compression.ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
            using (var s = entry.Open())
            using (var w = new System.IO.StreamWriter(s, new System.Text.UTF8Encoding(false)))
                w.Write(content);
        }

        // Build a filesystem-safe slug from the active wheel's firmware model name
        // for use as a filename prefix on diagnostics bundles. Prefers the curated
        // friendly name (e.g. "CS Pro" for firmware "W17"); falls back to the raw
        // prefix for unknown wheels. Returns "" when no model is known yet so the
        // caller can omit the prefix entirely rather than emit a leading dash.
        private static string BuildWheelModelFilenameSlug(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return "";
            var friendly = WheelModelInfo.GetFriendlyName(WheelModelInfo.ExtractPrefix(modelName!));
            if (string.IsNullOrWhiteSpace(friendly)) return "";

            var sb = new System.Text.StringBuilder(friendly.Length);
            foreach (var ch in friendly)
            {
                if (ch == ' ') sb.Append('-');
                else if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.') sb.Append(ch);
                // anything else (path separators, punctuation, control chars) is dropped
            }
            return sb.ToString().Trim('-');
        }

        private static string GetPluginVersion()
        {
            // AssemblyInformationalVersion carries the full semver string set by
            // CI via /p:Version=<x.y.z-dev.sha>, including the pre-release tag.
            // AssemblyVersion is a System.Version (numeric only) and silently
            // drops any -suffix, so we prefer informational here. SDK may append
            // "+<git-sha>" via SourceRevisionId — strip it for display.
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var info = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute
                    .GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                var s = info?.InformationalVersion;
                if (!string.IsNullOrEmpty(s))
                {
                    int plus = s!.IndexOf('+');
                    return plus >= 0 ? s.Substring(0, plus) : s;
                }
                return asm.GetName().Version?.ToString() ?? "unknown";
            }
            catch { return "unknown"; }
        }


        // ===== Experimental LED diagnostics (groups 2/3/4 + Meter flags) =====

        private class DiagLedCfg
        {
            public int Slot;
            public string Title = "";
            public int MaxLeds;
            public string ColorCmdPrefix = "";
            public string BrightnessCmd = "";
            public string? ModeCmd;
            public string? LiveColorCmd;
            public string? LiveBitmaskCmd;
        }

        private static readonly DiagLedCfg[] DiagLedCfgs =
        {
            new DiagLedCfg { Slot = 0, Title = "Group 0 — Shift/RPM", MaxLeds = 25, ColorCmdPrefix = "wheel-rpm-color",    BrightnessCmd = "wheel-rpm-brightness",     ModeCmd = null,
                             LiveColorCmd = "wheel-telemetry-rpm-colors", LiveBitmaskCmd = "wheel-send-rpm-telemetry" },
            new DiagLedCfg { Slot = 1, Title = "Group 1 — Button",   MaxLeds = 16, ColorCmdPrefix = "wheel-button-color", BrightnessCmd = "wheel-buttons-brightness", ModeCmd = null,
                             LiveColorCmd = "wheel-telemetry-button-colors", LiveBitmaskCmd = "wheel-send-buttons-telemetry" },
            new DiagLedCfg { Slot = 2, Title = "Group 2 — Single",   MaxLeds = 28, ColorCmdPrefix = "wheel-single-color",  BrightnessCmd = "wheel-single-brightness",  ModeCmd = "wheel-single-mode" },
            new DiagLedCfg { Slot = 3, Title = "Group 3 — Knob ring", MaxLeds = 56, ColorCmdPrefix = "wheel-knob-bg-color", BrightnessCmd = "wheel-knob-brightness",    ModeCmd = "wheel-knob-led-mode" },
            new DiagLedCfg { Slot = 4, Title = "Group 4 — Ambient",  MaxLeds = 12, ColorCmdPrefix = "wheel-ambient-color", BrightnessCmd = "wheel-ambient-brightness", ModeCmd = "wheel-ambient-mode" },
            new DiagLedCfg { Slot = 5, Title = "Flags (Meter device)", MaxLeds = 6, ColorCmdPrefix = "dash-flag-color",   BrightnessCmd = "dash-flags-brightness",    ModeCmd = null },
        };

        private readonly bool[] _extLedPanelBuilt = new bool[6];
        private readonly byte[] _extLedFillR = new byte[6];
        private readonly byte[] _extLedFillG = new byte[6];
        private readonly byte[] _extLedFillB = new byte[6];
        private readonly Border[] _extLedSwatches = new Border[6];
        private readonly int[] _extLedRangeMin = new int[6];
        private readonly int[] _extLedRangeMax = new int[6];
        private readonly TextBox?[] _extLedMinBoxes = new TextBox?[6];
        private readonly TextBox?[] _extLedMaxBoxes = new TextBox?[6];

        private void RefreshExtendedLedGroups()
        {
            bool any = false;
            bool built = false;
            foreach (var cfg in DiagLedCfgs)
            {
                bool present = IsDiagSlotPresent(cfg.Slot);
                if (present && !_extLedPanelBuilt[cfg.Slot])
                {
                    BuildDiagLedPanel(cfg);
                    _extLedPanelBuilt[cfg.Slot] = true;
                    built = true;
                }
                var panel = GetDiagLedPanel(cfg.Slot);
                if (panel != null)
                    panel.Visibility = present ? Visibility.Visible : Visibility.Collapsed;
                any |= present;
            }
            ExtLedSection.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (built)
                RefreshExtendedLedSummary();
        }

        private void RefreshExtendedLedSummary()
        {
            var sb = new System.Text.StringBuilder();
            var info = _plugin.WheelModelInfo;
            string modelName = _data?.WheelModelName ?? "";
            string friendly = string.IsNullOrEmpty(modelName) ? "Unknown wheel"
                : $"{WheelModelInfo.GetFriendlyName(WheelModelInfo.ExtractPrefix(modelName))} ({modelName})";
            sb.AppendLine($"{friendly} — wheel LED support");
            if (info != null)
                sb.AppendLine($"  WheelModelInfo: rpm={info.RpmLedCount}, buttons={info.ButtonLedCount}, flags={info.HasFlagLeds}");
            sb.AppendLine();

            foreach (var cfg in DiagLedCfgs)
            {
                if (!_extLedPanelBuilt[cfg.Slot]) continue;
                int min = _extLedRangeMin[cfg.Slot];
                int max = _extLedRangeMax[cfg.Slot];
                int count = max - min + 1;
                sb.AppendLine($"  {cfg.Title,-28} : {count,3} LEDs (indices {min}-{max} of 0-{cfg.MaxLeds - 1})");
            }
            ExtLedSummaryBox.Text = sb.ToString();
        }

        private void ExtLedRangeMin_LostFocus(object sender, RoutedEventArgs e) =>
            ExtLedRangeChanged((TextBox)sender, isMax: false);

        private void ExtLedRangeMax_LostFocus(object sender, RoutedEventArgs e) =>
            ExtLedRangeChanged((TextBox)sender, isMax: true);

        private void ExtLedRangeChanged(TextBox box, bool isMax)
        {
            var cfg = (DiagLedCfg)box.Tag;
            if (!int.TryParse(box.Text, out int v))
                v = isMax ? cfg.MaxLeds - 1 : 0;
            v = Math.Max(0, Math.Min(v, cfg.MaxLeds - 1));

            if (isMax)
            {
                if (v < _extLedRangeMin[cfg.Slot]) v = _extLedRangeMin[cfg.Slot];
                _extLedRangeMax[cfg.Slot] = v;
            }
            else
            {
                if (v > _extLedRangeMax[cfg.Slot]) v = _extLedRangeMax[cfg.Slot];
                _extLedRangeMin[cfg.Slot] = v;
            }
            box.Text = v.ToString();

            _settings.ExtLedDiagMin[cfg.Slot] = _extLedRangeMin[cfg.Slot];
            _settings.ExtLedDiagMax[cfg.Slot] = _extLedRangeMax[cfg.Slot];
            _plugin.SaveSettings();
            RefreshExtendedLedSummary();
        }

        private bool IsDiagSlotPresent(int slot)
        {
            if (slot == 0 || slot == 1) return _plugin.IsNewWheelDetected;
            if (slot >= 2 && slot <= 4) return _plugin.IsWheelLedGroupPresent(slot);
            if (slot == 5) return _plugin.IsDashDetected;
            return false;
        }

        private StackPanel? GetDiagLedPanel(int slot) => slot switch
        {
            0 => ExtLedGroup0Panel,
            1 => ExtLedGroup1Panel,
            2 => ExtLedGroup2Panel,
            3 => ExtLedGroup3Panel,
            4 => ExtLedGroup4Panel,
            5 => ExtLedFlagsPanel,
            _ => null,
        };

        private void BuildDiagLedPanel(DiagLedCfg cfg)
        {
            var panel = GetDiagLedPanel(cfg.Slot);
            if (panel == null) return;

            panel.Children.Clear();

            panel.Children.Add(new TextBlock
            {
                Text = $"{cfg.Title} (up to {cfg.MaxLeds} LEDs)",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 4),
            });

            int savedMin = _settings.ExtLedDiagMin.Length > cfg.Slot ? _settings.ExtLedDiagMin[cfg.Slot] : -1;
            int savedMax = _settings.ExtLedDiagMax.Length > cfg.Slot ? _settings.ExtLedDiagMax[cfg.Slot] : -1;
            _extLedRangeMin[cfg.Slot] = savedMin < 0 ? 0 : Math.Max(0, Math.Min(savedMin, cfg.MaxLeds - 1));
            _extLedRangeMax[cfg.Slot] = savedMax < 0 ? cfg.MaxLeds - 1 : Math.Max(0, Math.Min(savedMax, cfg.MaxLeds - 1));
            if (_extLedRangeMin[cfg.Slot] > _extLedRangeMax[cfg.Slot])
                _extLedRangeMin[cfg.Slot] = _extLedRangeMax[cfg.Slot];

            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row1.Children.Add(new TextBlock { Text = "Fill color:", Width = 80, VerticalAlignment = VerticalAlignment.Center });

            _extLedFillR[cfg.Slot] = 255;
            _extLedFillG[cfg.Slot] = 0;
            _extLedFillB[cfg.Slot] = 0;
            var swatch = new Border
            {
                Width = 28, Height = 28,
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(255, 0, 0)),
                Tag = cfg.Slot,
            };
            swatch.MouseLeftButtonUp += ExtLedSwatch_Click;
            _extLedSwatches[cfg.Slot] = swatch;
            row1.Children.Add(swatch);

            row1.Children.Add(new TextBlock { Text = "Range:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            var minBox = new TextBox { Width = 40, Text = _extLedRangeMin[cfg.Slot].ToString(), Margin = new Thickness(0, 0, 2, 0), Tag = cfg, VerticalAlignment = VerticalAlignment.Center };
            minBox.LostFocus += ExtLedRangeMin_LostFocus;
            _extLedMinBoxes[cfg.Slot] = minBox;
            row1.Children.Add(minBox);
            row1.Children.Add(new TextBlock { Text = "–", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
            var maxBox = new TextBox { Width = 40, Text = _extLedRangeMax[cfg.Slot].ToString(), Margin = new Thickness(0, 0, 8, 0), Tag = cfg, VerticalAlignment = VerticalAlignment.Center };
            maxBox.LostFocus += ExtLedRangeMax_LostFocus;
            _extLedMaxBoxes[cfg.Slot] = maxBox;
            row1.Children.Add(maxBox);

            var fillBtn = new Button { Content = "Fill all", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), Tag = cfg };
            fillBtn.Click += ExtLedFillAll_Click;
            row1.Children.Add(fillBtn);

            var clearBtn = new Button { Content = "All off", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), Tag = cfg };
            clearBtn.Click += ExtLedClearAll_Click;
            row1.Children.Add(clearBtn);
            panel.Children.Add(row1);

            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row2.Children.Add(new TextBlock { Text = "LED index:", Width = 80, VerticalAlignment = VerticalAlignment.Center });
            var idxBox = new TextBox { Width = 50, Text = "0", Margin = new Thickness(0, 0, 8, 0) };
            row2.Children.Add(idxBox);
            var sendOneBtn = new Button { Content = "Send one", Padding = new Thickness(8, 2, 8, 2), Tag = (cfg, idxBox) };
            sendOneBtn.Click += ExtLedSendOne_Click;
            row2.Children.Add(sendOneBtn);
            panel.Children.Add(row2);

            var row3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row3.Children.Add(new TextBlock { Text = "Brightness:", Width = 80, VerticalAlignment = VerticalAlignment.Center });
            var brightSlider = new Slider { Minimum = 0, Maximum = 100, Value = 50, Width = 200, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
            row3.Children.Add(brightSlider);
            var brightLabel = new TextBlock { Width = 40, TextAlignment = TextAlignment.Right, Margin = new Thickness(6, 0, 8, 0), Text = "50", VerticalAlignment = VerticalAlignment.Center };
            brightSlider.ValueChanged += (s, e) => brightLabel.Text = ((int)brightSlider.Value).ToString();
            row3.Children.Add(brightLabel);
            var sendBrightBtn = new Button { Content = "Send", Padding = new Thickness(8, 2, 8, 2), Tag = (cfg, brightSlider) };
            sendBrightBtn.Click += ExtLedSendBrightness_Click;
            row3.Children.Add(sendBrightBtn);
            panel.Children.Add(row3);

            if (cfg.ModeCmd != null)
            {
                var row4 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                row4.Children.Add(new TextBlock { Text = "Mode:", Width = 80, VerticalAlignment = VerticalAlignment.Center });
                var modeCombo = new ComboBox { Width = 60, Margin = new Thickness(0, 0, 8, 0) };
                modeCombo.Items.Add("0");
                modeCombo.Items.Add("1");
                modeCombo.Items.Add("2");
                modeCombo.SelectedIndex = 0;
                row4.Children.Add(modeCombo);
                var sendModeBtn = new Button { Content = "Send", Padding = new Thickness(8, 2, 8, 2), Tag = (cfg, modeCombo) };
                sendModeBtn.Click += ExtLedSendMode_Click;
                row4.Children.Add(sendModeBtn);
                panel.Children.Add(row4);
            }
        }

        private void ExtLedSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            var border = (Border)sender;
            int slot = (int)border.Tag;
            var dialog = new ColorPickerDialog(_extLedFillR[slot], _extLedFillG[slot], _extLedFillB[slot]);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                _extLedFillR[slot] = dialog.SelectedR;
                _extLedFillG[slot] = dialog.SelectedG;
                _extLedFillB[slot] = dialog.SelectedB;
                border.Background = new SolidColorBrush(Color.FromRgb(dialog.SelectedR, dialog.SelectedG, dialog.SelectedB));
            }
        }

        private void ExtLedFillAll_Click(object sender, RoutedEventArgs e)
        {
            var cfg = (DiagLedCfg)((Button)sender).Tag;
            byte r = _extLedFillR[cfg.Slot], g = _extLedFillG[cfg.Slot], b = _extLedFillB[cfg.Slot];
            int min = _extLedRangeMin[cfg.Slot], max = _extLedRangeMax[cfg.Slot];
            if (cfg.LiveColorCmd != null)
            {
                int mask = RangeMask(min, max);
                SendLiveFrame(cfg, fillColor: (r, g, b), activeMask: mask, rangeMin: min, rangeMax: max, onlyIdx: -1);
                return;
            }
            for (int i = min; i <= max; i++)
                _device.WriteColor($"{cfg.ColorCmdPrefix}{i + 1}", r, g, b);
        }

        private void ExtLedClearAll_Click(object sender, RoutedEventArgs e)
        {
            var cfg = (DiagLedCfg)((Button)sender).Tag;
            int min = _extLedRangeMin[cfg.Slot], max = _extLedRangeMax[cfg.Slot];
            if (cfg.LiveColorCmd != null)
            {
                SendLiveFrame(cfg, fillColor: (0, 0, 0), activeMask: 0, rangeMin: min, rangeMax: max, onlyIdx: -1);
                return;
            }
            for (int i = min; i <= max; i++)
                _device.WriteColor($"{cfg.ColorCmdPrefix}{i + 1}", 0, 0, 0);
        }

        private void ExtLedSendOne_Click(object sender, RoutedEventArgs e)
        {
            var (cfg, idxBox) = ((DiagLedCfg, TextBox))((Button)sender).Tag;
            if (!int.TryParse(idxBox.Text, out int idx)) return;
            if (idx < 0 || idx >= cfg.MaxLeds) return;
            byte r = _extLedFillR[cfg.Slot], g = _extLedFillG[cfg.Slot], b = _extLedFillB[cfg.Slot];
            if (cfg.LiveColorCmd != null)
            {
                SendLiveFrame(cfg, fillColor: (r, g, b), activeMask: 1 << idx, rangeMin: idx, rangeMax: idx, onlyIdx: idx);
                return;
            }
            _device.WriteColor($"{cfg.ColorCmdPrefix}{idx + 1}", r, g, b);
        }

        private static int RangeMask(int min, int max)
        {
            int mask = 0;
            for (int i = min; i <= max; i++)
                mask |= 1 << i;
            return mask;
        }

        private void SendLiveFrame(DiagLedCfg cfg, (byte r, byte g, byte b) fillColor,
            int activeMask, int rangeMin, int rangeMax, int onlyIdx)
        {
            if (cfg.LiveColorCmd == null || cfg.LiveBitmaskCmd == null) return;

            int n = cfg.MaxLeds;
            var colors = new System.Drawing.Color[n];
            var fill = System.Drawing.Color.FromArgb(fillColor.r, fillColor.g, fillColor.b);
            for (int i = 0; i < n; i++)
            {
                bool paint = onlyIdx < 0
                    ? (i >= rangeMin && i <= rangeMax)
                    : i == onlyIdx;
                colors[i] = paint ? fill : System.Drawing.Color.Black;
            }

            MozaLedDeviceManager.SendColorChunks(_plugin, colors, n, cfg.LiveColorCmd);

            byte[] maskBytes = (cfg.LiveBitmaskCmd == "wheel-send-rpm-telemetry" && n > 16)
                ? new byte[] {
                    (byte)(activeMask & 0xFF),
                    (byte)((activeMask >> 8) & 0xFF),
                    (byte)((activeMask >> 16) & 0xFF),
                    (byte)((activeMask >> 24) & 0xFF)
                }
                : new byte[] { (byte)(activeMask & 0xFF), (byte)((activeMask >> 8) & 0xFF) };
            _device.WriteArray(cfg.LiveBitmaskCmd, maskBytes);
        }

        private void ExtLedSendBrightness_Click(object sender, RoutedEventArgs e)
        {
            var (cfg, slider) = ((DiagLedCfg, Slider))((Button)sender).Tag;
            _device.WriteSetting(cfg.BrightnessCmd, (int)slider.Value);
        }

        private void ExtLedSendMode_Click(object sender, RoutedEventArgs e)
        {
            var (cfg, modeCombo) = ((DiagLedCfg, ComboBox))((Button)sender).Tag;
            if (cfg.ModeCmd == null) return;
            if (modeCombo.SelectedIndex < 0) return;
            _device.WriteSetting(cfg.ModeCmd, modeCombo.SelectedIndex);
        }

        // ── Wheel Files tab ─────────────────────────────────────────────
        // Shows the wheel-side dashboard inventory derived from the most-recent
        // session 0x09 configJson state push. Per-row Delete issues a
        // `completelyRemove` RPC over session 0x0a.

        public sealed class WheelFileRow
        {
            public string State { get; set; } = "";       // "enabled" / "disabled"
            public string Title { get; set; } = "";
            public string DirName { get; set; } = "";
            public string Hash { get; set; } = "";
            public string HashShort => string.IsNullOrEmpty(Hash) ? "" :
                (Hash.Length > 12 ? Hash.Substring(0, 12) + "…" : Hash);
            public string LastModified { get; set; } = "";
            public string Id { get; set; } = "";
        }

        private void RefreshWheelFilesTab()
        {
            if (WheelFilesGrid == null) return;
            var ts = _plugin.TelemetrySender;
            var state = _plugin.WheelStateForDiagnostics;
            var rows = new System.Collections.Generic.List<WheelFileRow>();
            if (state != null)
            {
                foreach (var d in state.EnabledDashboards)
                    rows.Add(new WheelFileRow
                    {
                        State = "enabled",
                        Title = d.Title,
                        DirName = d.DirName,
                        Hash = d.Hash,
                        LastModified = d.LastModified,
                        Id = d.Id,
                    });
                foreach (var d in state.DisabledDashboards)
                    rows.Add(new WheelFileRow
                    {
                        State = "disabled",
                        Title = d.Title,
                        DirName = d.DirName,
                        Hash = d.Hash,
                        LastModified = d.LastModified,
                        Id = d.Id,
                    });
            }
            // Preserve grid selection across refresh by DirName key.
            string? prevDir = (WheelFilesGrid.SelectedItem as WheelFileRow)?.DirName;
            WheelFilesGrid.ItemsSource = rows;
            if (!string.IsNullOrEmpty(prevDir))
            {
                foreach (var r in rows)
                    if (r.DirName == prevDir) { WheelFilesGrid.SelectedItem = r; break; }
            }
            if (WheelFilesStatusBox != null)
            {
                if (state == null)
                    WheelFilesStatusBox.Text = "(no configJson state received yet)";
                else
                    WheelFilesStatusBox.Text =
                        $"{rows.Count} dashboards (captured {state.CapturedAt:HH:mm:ss})";
            }
        }

        private void WheelFilesRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshWheelFilesTab();
        }

        private void WheelFilesDelete_Click(object sender, RoutedEventArgs e)
        {
            // Temporarily neutered: completelyRemove RPC wedges wheel firmware until
            // the wheelbase is power-cycled. Button is also IsEnabled="False" in XAML;
            // this guard is defensive in case the XAML flag is flipped without
            // re-validating the RPC behaviour. Remove both when the firmware path is fixed.
            return;
#pragma warning disable CS0162 // Unreachable code — preserved scaffolding
            if (((Button)sender).Tag is not WheelFileRow row) return;
            if (string.IsNullOrEmpty(row.Id))
            {
                System.Windows.MessageBox.Show(
                    $"Cannot delete \"{row.Title}\": wheel did not assign an id.",
                    "Moza", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            var confirm = System.Windows.MessageBox.Show(
                $"Delete \"{row.Title}\" from the wheel?\n\nDirName: {row.DirName}\nId: {row.Id}",
                "Moza — confirm delete",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.OK) return;
            var ts = _plugin.TelemetrySender;
            if (ts == null)
            {
                System.Windows.MessageBox.Show("Telemetry sender unavailable.",
                    "Moza", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                return;
            }
            byte[]? reply = ts.SendRpcCall("completelyRemove", row.Id);
            if (reply == null)
                System.Windows.MessageBox.Show(
                    $"completelyRemove(\"{row.Id}\") timed out. The dashboard may still be present on the wheel.",
                    "Moza", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            // Wheel pushes a refreshed configJson state after completelyRemove —
            // the next 500ms timer tick will refresh the grid via
            // RefreshWheelFilesTab → ts.WheelState.
#pragma warning restore CS0162
        }

        // ===== AB9 Active Shifter Tab =====

        // Tracks whether the slider/combo values have been seeded from the profile
        // — without this, the first refresh tick would race the user's drag and
        // immediately overwrite an in-flight slider value with the saved one.
        private bool _ab9UiSeeded;

        private void RefreshAb9Tab()
        {
            if (_plugin?.Ab9Manager == null) { Ab9Tab.Visibility = Visibility.Collapsed; return; }

            bool connected = _plugin.Ab9Manager.IsConnected;
            bool detected  = _plugin.IsAb9Detected;

            Ab9Tab.Visibility = (connected || detected)
                ? Visibility.Visible : Visibility.Collapsed;
            if (!connected && !detected) { _ab9UiSeeded = false; return; }

            Ab9StatusDot.Fill = detected
                ? Brushes.LimeGreen
                : (connected ? Brushes.Goldenrod : Brushes.Gray);
            Ab9StatusLabel.Text = detected
                ? "AB9 connected"
                : "Probing AB9…";

            if (_ab9UiSeeded) return;

            // Seed the controls from the profile (or defaults). Suppress events
            // so this seed pass doesn't fire WriteSlider for every control.
            var ab9 = _plugin.Settings?.ProfileStore?.CurrentProfile?.Ab9 ?? new Ab9Settings();
            _suppressEvents = true;
            try
            {
                SetAb9ModeCombo(ab9.Mode);
                SetAb9Slider(Ab9MechResistanceSlider, Ab9MechResistanceValue, ab9.MechanicalResistance);
                SetAb9Slider(Ab9SpringSlider,         Ab9SpringValue,         ab9.Spring);
                SetAb9Slider(Ab9DampingSlider,        Ab9DampingValue,        ab9.NaturalDamping);
                SetAb9Slider(Ab9FrictionSlider,       Ab9FrictionValue,       ab9.NaturalFriction);
                SetAb9Slider(Ab9MaxTorqueSlider,      Ab9MaxTorqueValue,      ab9.MaxTorqueLimit);
            }
            finally
            {
                _suppressEvents = false;
            }
            _ab9UiSeeded = true;
        }

        private void SetAb9Slider(Slider slider, TextBlock value, byte v)
        {
            slider.Value = v;
            value.Text = v.ToString();
        }

        private void SetAb9ModeCombo(Ab9Mode mode)
        {
            for (int i = 0; i < Ab9ModeCombo.Items.Count; i++)
            {
                var item = Ab9ModeCombo.Items[i] as ComboBoxItem;
                if (item?.Tag is string tag && byte.TryParse(tag, out byte val) && val == (byte)mode)
                {
                    Ab9ModeCombo.SelectedIndex = i;
                    return;
                }
            }
            Ab9ModeCombo.SelectedIndex = -1;
        }

        private Ab9Settings GetOrCreateAb9Profile()
        {
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return new Ab9Settings();
            if (profile.Ab9 == null) profile.Ab9 = new Ab9Settings();
            return profile.Ab9;
        }

        private void Ab9ModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (Ab9ModeCombo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string tag || !byte.TryParse(tag, out byte val)) return;

            var mode = (Ab9Mode)val;
            GetOrCreateAb9Profile().Mode = mode;
            _plugin.Ab9Manager?.SendMode(mode);
            _plugin.SaveSettings();
        }

        private void Ab9MechResistanceSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9MechResistanceSlider, Ab9MechResistanceValue, Ab9Slider.MechanicalResistance, e.NewValue);

        private void Ab9SpringSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9SpringSlider, Ab9SpringValue, Ab9Slider.Spring, e.NewValue);

        private void Ab9DampingSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9DampingSlider, Ab9DampingValue, Ab9Slider.NaturalDamping, e.NewValue);

        private void Ab9FrictionSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9FrictionSlider, Ab9FrictionValue, Ab9Slider.NaturalFriction, e.NewValue);

        private void Ab9MaxTorqueSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9MaxTorqueSlider, Ab9MaxTorqueValue, Ab9Slider.MaxTorqueLimit, e.NewValue);

        private void HandleAb9SliderChanged(Slider slider, TextBlock label, Ab9Slider which, double newValue)
        {
            if (_suppressEvents) return;
            byte v = (byte)Math.Max(0, Math.Min(100, (int)Math.Round(newValue)));
            label.Text = v.ToString();

            var ab9 = GetOrCreateAb9Profile();
            switch (which)
            {
                case Ab9Slider.MechanicalResistance: ab9.MechanicalResistance = v; break;
                case Ab9Slider.Spring:               ab9.Spring = v;               break;
                case Ab9Slider.NaturalDamping:       ab9.NaturalDamping = v;       break;
                case Ab9Slider.NaturalFriction:      ab9.NaturalFriction = v;      break;
                case Ab9Slider.MaxTorqueLimit:       ab9.MaxTorqueLimit = v;       break;
            }
            _plugin.Ab9Manager?.SendSlider(which, v);
            _plugin.SaveSettings();
        }

        private static string Blank(string s) => string.IsNullOrEmpty(s) ? "—" : s;
        private static string Redact(string s) => MozaLog.RedactId(s);
        private static string RedactBytes(byte[] b) => MozaLog.RedactBytesHex(b);
        private static string Hex(byte[] b) => b == null || b.Length == 0 ? "—" : BitConverter.ToString(b);
        private static string HexRaw(byte[] b) => b == null || b.Length == 0 ? "—" : BitConverter.ToString(b).Replace("-", "");
        private static string JoinList(System.Collections.Generic.IReadOnlyList<string> l)
            => l == null || l.Count == 0 ? "(empty)" : string.Join(", ", l);
        private static string TruncateId(string id)
            => string.IsNullOrEmpty(id) ? "—" : (id.Length > 40 ? id.Substring(0, 40) + "…" : id);
    }
}
