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
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.UI;
using static MozaPlugin.UI.UiHelpers;
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
        private readonly EventSuppressor _suppressor = new EventSuppressor();
        private bool _suppressEvents => _suppressor.Suppressed;

        // Per-pedal Y-curve UI bindings, cached after InitializeComponent so
        // ApplyPedalCurvePreset can take an arrays pair instead of 10 args.
        private Slider[]? _throttleCurveSliders, _brakeCurveSliders, _clutchCurveSliders;
        private TextBlock[]? _throttleCurveLabels, _brakeCurveLabels, _clutchCurveLabels;
        private readonly DateTime[] _buttonLastPressed = new DateTime[MozaData.MaxButtons];

        public SettingsControl(MozaPlugin plugin)
        {
            _plugin = plugin;
            _device = plugin.DeviceManager;
            _data = plugin.Data;

            using (_suppressor.Begin())
            {
                InitializeComponent();
                _throttleCurveSliders = new[] { ThrottleY1Slider, ThrottleY2Slider, ThrottleY3Slider, ThrottleY4Slider, ThrottleY5Slider };
                _brakeCurveSliders    = new[] { BrakeY1Slider,    BrakeY2Slider,    BrakeY3Slider,    BrakeY4Slider,    BrakeY5Slider };
                _clutchCurveSliders   = new[] { ClutchY1Slider,   ClutchY2Slider,   ClutchY3Slider,   ClutchY4Slider,   ClutchY5Slider };
                _throttleCurveLabels  = new[] { ThrottleY1Value,  ThrottleY2Value,  ThrottleY3Value,  ThrottleY4Value,  ThrottleY5Value };
                _brakeCurveLabels     = new[] { BrakeY1Value,     BrakeY2Value,     BrakeY3Value,     BrakeY4Value,     BrakeY5Value };
                _clutchCurveLabels    = new[] { ClutchY1Value,    ClutchY2Value,    ClutchY3Value,    ClutchY4Value,    ClutchY5Value };
                ConnectionToggle.IsChecked = plugin.ConnectionEnabled;
                AutoApplyProfileCheck.IsChecked = plugin.Settings.AutoApplyProfileOnLaunch;
                LimitWheelUpdatesCheck.IsChecked = plugin.Settings.LimitWheelUpdates;
                AlwaysResendBitmaskCheck.IsChecked = plugin.Settings.AlwaysResendBitmask;
                {
                    // Source from the active profile (single source of truth). Falls
                    // back to plugin-global flat fields for legacy data that hasn't
                    // been promoted into a profile yet on this launch.
                    var gsProfile = plugin.Settings.ProfileStore?.CurrentProfile;
                    bool von = gsProfile?.GearshiftVibrateOnNeutral == 1
                        || (gsProfile?.GearshiftVibrateOnNeutral == -1 && plugin.Settings.GearshiftVibrateOnNeutral);
                    GearshiftVibrateOnNeutralCheck.IsChecked = von;

                    int dbMs = gsProfile?.GearshiftDebounceMs ?? -1;
                    if (dbMs < 0) dbMs = plugin.Settings.GearshiftDebounceMs;
                    if (dbMs < 0) dbMs = 500;
                    if (dbMs > 1000) dbMs = 1000;
                    // Snap to 50 ms grid so the slider thumb sits on a tick when the
                    // persisted value came from an older build / a manual edit.
                    dbMs = ((dbMs + 25) / 50) * 50;
                    GearshiftDebounceSlider.Value = dbMs;
                    GearshiftDebounceValue.Text = $"{dbMs} ms";
                }
                DisableSerialProbeFallbackCheck.IsChecked = plugin.Settings.DisableSerialProbeFallback;
                DisableAb9DetectionCheck.IsChecked = plugin.Settings.DisableAb9Detection;
                StartCaptureOnNextLaunchCheck.IsChecked = plugin.Settings.StartCaptureOnNextLaunch;
                // Reflect any in-flight capture (e.g. armed from a previous session
                // and started in MozaPlugin.Init) so the user sees Stop instead of
                // a stale Start button when they open the Diagnostics tab.
                if (SerialTrafficCapture.Instance.Enabled)
                {
                    SerialCaptureToggleButton.Content = "Stop capture";
                    SerialCaptureStatusText.Text = "capturing… (armed from prior session — click Stop when ready)";
                }
            }

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

            using (_suppressor.Begin())
            {
                RefreshBaseTab();
                RefreshWheelTab();
                RefreshHandbrakeTab();
                RefreshPedalsTab();
                RefreshHubTab();
                RefreshAb9Tab();
                RefreshMBoosterTab();
                InitTelemetryTab();
                RefreshExtendedLedGroups();
                RefreshDashboardUploadTab();
                RefreshWheelFilesTab();
                RefreshDiagnosticsTab();
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
            _plugin.WriteIfBaseConnected("base-limit", raw);
            _plugin.WriteIfBaseConnected("base-max-angle", raw);
            _plugin.SaveSettings();
        }

        private void FfbStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            FfbStrengthValue.Text = $"{pct}%";
            _data.FfbStrength = raw;
            _plugin.WriteIfBaseConnected("base-ffb-strength", raw);
            _plugin.SaveSettings();
        }

        private void TorqueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            TorqueValue.Text = $"{val}%";
            _data.Torque = val;
            _plugin.WriteIfBaseConnected("base-torque", val);
            _plugin.SaveSettings();
        }

        private void PerformanceOutputCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = PerformanceOutputCombo.SelectedIndex;
            if (val < 0) return;
            _data.TempStrategy = val;
            _plugin.WriteIfBaseConnected("base-temp-strategy", val);
            _plugin.SaveSettings();
        }

        private void GearshiftVibrationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            GearshiftVibrationValue.Text = val.ToString();
            _data.GearshiftVibration = val;
            _plugin.WriteIfBaseConnected("base-gearshift-vibration", val);
            _plugin.SaveSettings();
        }

        private void GearshiftVibrateOnNeutralCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = GearshiftVibrateOnNeutralCheck.IsChecked == true;
            _plugin.UpdateActiveProfile(p => p.GearshiftVibrateOnNeutral = on ? 1 : 0);
            _plugin.SaveSettings();
        }

        private void GearshiftDebounceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            // Snap to 50 ms grid (IsSnapToTickEnabled + TickFrequency=50 already
            // enforces this on user input, but be defensive against external
            // sources that bypass the tick grid).
            val = ((val + 25) / 50) * 50;
            if (val < 0) val = 0;
            if (val > 1000) val = 1000;
            GearshiftDebounceValue.Text = $"{val} ms";
            _plugin.UpdateActiveProfile(p => p.GearshiftDebounceMs = val);
            _plugin.SaveSettings();
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            SpeedValue.Text = $"{pct}%";
            _data.Speed = raw;
            _plugin.WriteIfBaseConnected("base-speed", raw);
            _plugin.SaveSettings();
        }

        private void DamperSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            DamperValue.Text = $"{pct}%";
            _data.Damper = raw;
            _plugin.WriteIfBaseConnected("base-damper", raw);
            _plugin.SaveSettings();
        }

        private void FrictionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            FrictionValue.Text = $"{pct}%";
            _data.Friction = raw;
            _plugin.WriteIfBaseConnected("base-friction", raw);
            _plugin.SaveSettings();
        }

        private void InertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            int raw = val * 10;
            InertiaValue.Text = $"{val}";
            _data.Inertia = raw;
            _plugin.WriteIfBaseConnected("base-inertia", raw);
            _plugin.SaveSettings();
        }

        private void SpringSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            SpringValue.Text = $"{pct}%";
            _data.Spring = raw;
            _plugin.WriteIfBaseConnected("base-spring", raw);
            _plugin.SaveSettings();
        }

        private void GameDamperSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameDamperValue.Text = $"{pct}%";
            _data.GameDamper = raw;
            _plugin.WriteIfBaseConnected("main-set-damper-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameFrictionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameFrictionValue.Text = $"{pct}%";
            _data.GameFriction = raw;
            _plugin.WriteIfBaseConnected("main-set-friction-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameInertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameInertiaValue.Text = $"{pct}%";
            _data.GameInertia = raw;
            _plugin.WriteIfBaseConnected("main-set-inertia-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameSpringSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameSpringValue.Text = $"{pct}%";
            _data.GameSpring = raw;
            _plugin.WriteIfBaseConnected("main-set-spring-gain", raw);
            _plugin.SaveSettings();
        }

        private void SpeedDampingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            SpeedDampingValue.Text = $"{val}%";
            _data.SpeedDamping = val;
            _plugin.WriteIfBaseConnected("base-speed-damping", val);
            _plugin.SaveSettings();
        }

        private void SpeedDampingPointSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            SpeedDampingPointValue.Text = $"{val} kph";
            _data.SpeedDampingPoint = val;
            _plugin.WriteIfBaseConnected("base-speed-damping-point", val);
            _plugin.SaveSettings();
        }

        private void NaturalInertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            NaturalInertiaValue.Text = $"{val}";
            _data.NaturalInertia = val;
            _plugin.WriteIfBaseConnected("base-natural-inertia", val);
            _plugin.SaveSettings();
        }

        private void SoftLimitStiffnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int display = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(display * (400.0 / 9.0) - (400.0 / 9.0) + 100.0);
            SoftLimitStiffnessValue.Text = $"{display}";
            _data.SoftLimitStiffness = raw;
            _plugin.WriteIfBaseConnected("base-soft-limit-stiffness", raw);
            _plugin.SaveSettings();
        }

        // ===== Checkbox handlers =====

        private void FfbReverseCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = FfbReverseCheck.IsChecked == true ? 1 : 0;
            _data.FfbReverse = val;
            _plugin.WriteIfBaseConnected("base-ffb-reverse", val);
            _plugin.SaveSettings();
        }

        private void ProtectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = ProtectionCheck.IsChecked == true ? 1 : 0;
            _data.Protection = val;
            _plugin.WriteIfBaseConnected("base-protection", val);
            _plugin.SaveSettings();
        }

        private void SoftLimitRetainCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = SoftLimitRetainCheck.IsChecked == true ? 1 : 0;
            _data.SoftLimitRetain = val;
            _plugin.WriteIfBaseConnected("base-soft-limit-retain", val);
            _plugin.SaveSettings();
        }

        private void StandbyCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = StandbyCheck.IsChecked == true ? 1 : 0;
            _data.WorkMode = val;
            _plugin.WriteIfBaseConnected("main-set-work-mode", val);
            _plugin.SaveSettings();
        }

        private void LedStatusCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = LedStatusCheck.IsChecked == true ? 1 : 0;
            _data.LedStatus = val;
            _plugin.WriteIfBaseConnected("main-set-led-status", val);
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
            _plugin.UpdateActiveWheelOverlay(o => o.WheelPaddlesMode = val);
            UpdatePaddlePanelVisibility(val);
            _plugin.WriteIfWheelDetected("wheel-paddles-mode", val + 1); // display 0/1/2 → raw 1/2/3
            _plugin.SaveSettings();
        }

        private void WheelClutchPointSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            WheelClutchPointValue.Text = $"{val}%";
            _data.WheelClutchPoint = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelClutchPoint = val);
            _plugin.WriteIfWheelDetected("wheel-clutch-point", val);
            _plugin.SaveSettings();
        }

        private void KnobModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = KnobModeCombo.SelectedIndex;
            _data.WheelKnobMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelKnobMode = val);
            _plugin.WriteIfWheelDetected("wheel-knob-mode", val);
            _plugin.SaveSettings();
        }

        private void WriteKnobSignalMode(int index, int value)
        {
            if (_suppressEvents) return;
            _data.WheelKnobSignalModes[index] = value;
            _plugin.WriteIfWheelDetected($"wheel-knob-signal-mode{index}", value);
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
            _plugin.UpdateActiveWheelOverlay(o => o.WheelStickMode = val);
            _plugin.WriteIfWheelDetected("wheel-stick-mode", val * 256);
            _plugin.SaveSettings();
        }

        private void StickModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = StickModeCombo.SelectedIndex;
            _data.WheelStickMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelStickMode = val);
            _plugin.WriteIfWheelDetected("wheel-stick-mode-new", val);
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
            _plugin.WriteIfHandbrakeDetected("handbrake-mode", val);
            _plugin.SaveSettings();
        }

        private void HandbrakeThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            HandbrakeThresholdValue.Text = $"{val}%";
            _data.HandbrakeButtonThreshold = val;
            _plugin.WriteIfHandbrakeDetected("handbrake-button-threshold", val);
            _plugin.SaveSettings();
        }

        private void HandbrakeDirectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = HandbrakeDirectionCheck.IsChecked == true ? 1 : 0;
            _data.HandbrakeDirection = val;
            _plugin.WriteIfHandbrakeDetected("handbrake-direction", val);
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

        // SetSliderPercent, SetSliderRaw, SetComboSafe, Clamp moved to UI/UiHelpers.

        private double ConvertTemp(int raw)
        {
            double celsius = raw / 100.0;
            return _data.UseFahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;
        }

        // ===== Generic slider-handler helpers =====

        // Most int-valued sliders share the same body: drop the event if a refresh
        // is mid-flight, round the new value, paint the label, commit it to the
        // data model + device, then queue a settings save. The per-slider commit
        // lambda captures which data field and which device command to use.
        private void OnIntSliderChanged(double newValue, TextBlock label, string suffix,
            Action<int> commit)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(newValue);
            label.Text = $"{v}{suffix}";
            commit(v);
            _plugin.SaveSettings();
        }

        // Min/max pair sliders additionally clamp against the sibling bound and
        // bounce the slider back without re-firing this handler.
        private void OnMinMaxSliderChanged(double newValue, Slider self, int otherBound,
            bool isMin, TextBlock label, Action<int> commit)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(newValue);
            if (isMin ? v > otherBound : v < otherBound)
            {
                v = otherBound;
                using (_suppressor.Begin()) self.Value = v;
            }
            label.Text = $"{v}%";
            commit(v);
            _plugin.SaveSettings();
        }

        // ===== FFB Equalizer handlers =====

        private static readonly string[] EqCommands = {
            "base-equalizer1", "base-equalizer2", "base-equalizer3",
            "base-equalizer4", "base-equalizer5", "base-equalizer6"
        };

        private void Eq1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq1Value, "%", v => { _data.Equalizer1 = v; _plugin.WriteIfBaseConnected(EqCommands[0], v); });
        private void Eq2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq2Value, "%", v => { _data.Equalizer2 = v; _plugin.WriteIfBaseConnected(EqCommands[1], v); });
        private void Eq3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq3Value, "%", v => { _data.Equalizer3 = v; _plugin.WriteIfBaseConnected(EqCommands[2], v); });
        private void Eq4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq4Value, "%", v => { _data.Equalizer4 = v; _plugin.WriteIfBaseConnected(EqCommands[3], v); });
        private void Eq5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq5Value, "%", v => { _data.Equalizer5 = v; _plugin.WriteIfBaseConnected(EqCommands[4], v); });
        private void Eq6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq6Value, "%", v => { _data.Equalizer6 = v; _plugin.WriteIfBaseConnected(EqCommands[5], v); });

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
            using (_suppressor.Begin())
            {
                FfbCurveY1Slider.Value = p[0]; FfbCurveY1Value.Text = $"{p[0]}"; _data.FfbCurveY1 = p[0];
                FfbCurveY2Slider.Value = p[1]; FfbCurveY2Value.Text = $"{p[1]}"; _data.FfbCurveY2 = p[1];
                FfbCurveY3Slider.Value = p[2]; FfbCurveY3Value.Text = $"{p[2]}"; _data.FfbCurveY3 = p[2];
                FfbCurveY4Slider.Value = p[3]; FfbCurveY4Value.Text = $"{p[3]}"; _data.FfbCurveY4 = p[3];
                FfbCurveY5Slider.Value = p[4]; FfbCurveY5Value.Text = $"{p[4]}"; _data.FfbCurveY5 = p[4];
            }
            // Always write fixed X breakpoints first
            _plugin.WriteIfBaseConnected("base-ffb-curve-x1", 20); _plugin.WriteIfBaseConnected("base-ffb-curve-x2", 40);
            _plugin.WriteIfBaseConnected("base-ffb-curve-x3", 60); _plugin.WriteIfBaseConnected("base-ffb-curve-x4", 80);
            _plugin.WriteIfBaseConnected("base-ffb-curve-y1", p[0]); _plugin.WriteIfBaseConnected("base-ffb-curve-y2", p[1]);
            _plugin.WriteIfBaseConnected("base-ffb-curve-y3", p[2]); _plugin.WriteIfBaseConnected("base-ffb-curve-y4", p[3]);
            _plugin.WriteIfBaseConnected("base-ffb-curve-y5", p[4]);
            _plugin.SaveSettings();
        }

        private void FfbCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[0]);
        private void FfbCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[1]);
        private void FfbCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[2]);
        private void FfbCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[3]);

        private void FfbCurveY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY1Value, "", v => { _data.FfbCurveY1 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-y1", v); });
        private void FfbCurveY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY2Value, "", v => { _data.FfbCurveY2 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-y2", v); });
        private void FfbCurveY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY3Value, "", v => { _data.FfbCurveY3 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-y3", v); });
        private void FfbCurveY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY4Value, "", v => { _data.FfbCurveY4 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-y4", v); });
        private void FfbCurveY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY5Value, "", v => { _data.FfbCurveY5 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-y5", v); });

        // ===== Bluetooth + Base Calibration =====

        private void BluetoothCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = BluetoothCheck.IsChecked == true ? 0 : 85;
            _data.BleMode = val;
            _plugin.WriteIfBaseConnected("main-set-ble-mode", val);
            _plugin.SaveSettings();
        }

        private void BaseCalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.WriteIfBaseConnected("base-calibration", 1);
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
            using (_suppressor.Begin())
            {
                HbY1Slider.Value = p[0]; HbY1Value.Text = $"{p[0]}"; _data.HandbrakeCurve[0] = p[0];
                HbY2Slider.Value = p[1]; HbY2Value.Text = $"{p[1]}"; _data.HandbrakeCurve[1] = p[1];
                HbY3Slider.Value = p[2]; HbY3Value.Text = $"{p[2]}"; _data.HandbrakeCurve[2] = p[2];
                HbY4Slider.Value = p[3]; HbY4Value.Text = $"{p[3]}"; _data.HandbrakeCurve[3] = p[3];
                HbY5Slider.Value = p[4]; HbY5Value.Text = $"{p[4]}"; _data.HandbrakeCurve[4] = p[4];
            }
            for (int i = 0; i < 5; i++)
                _plugin.WriteFloatIfHandbrakeDetected($"handbrake-y{i + 1}", p[i]);
            _plugin.SaveSettings();
        }

        private void HbCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[0]);
        private void HbCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[1]);
        private void HbCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[2]);
        private void HbCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[3]);

        private void HandbrakeMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, HandbrakeMinSlider, _data.HandbrakeMax, isMin: true, HandbrakeMinValue, v => { _data.HandbrakeMin = v; _plugin.WriteIfHandbrakeDetected("handbrake-min", v); });
        private void HandbrakeMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, HandbrakeMaxSlider, _data.HandbrakeMin, isMin: false, HandbrakeMaxValue, v => { _data.HandbrakeMax = v; _plugin.WriteIfHandbrakeDetected("handbrake-max", v); });
        private void HbY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY1Value, "", v => { _data.HandbrakeCurve[0] = v; _plugin.WriteFloatIfHandbrakeDetected("handbrake-y1", v); });
        private void HbY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY2Value, "", v => { _data.HandbrakeCurve[1] = v; _plugin.WriteFloatIfHandbrakeDetected("handbrake-y2", v); });
        private void HbY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY3Value, "", v => { _data.HandbrakeCurve[2] = v; _plugin.WriteFloatIfHandbrakeDetected("handbrake-y3", v); });
        private void HbY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY4Value, "", v => { _data.HandbrakeCurve[3] = v; _plugin.WriteFloatIfHandbrakeDetected("handbrake-y4", v); });
        private void HbY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY5Value, "", v => { _data.HandbrakeCurve[4] = v; _plugin.WriteFloatIfHandbrakeDetected("handbrake-y5", v); });

        private void HbCalStartButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.WriteIfHandbrakeDetected("handbrake-cal-start", 1);
            HbCalStatus.Text = "Calibrating — pull fully then stop";
        }

        private void HbCalStopButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.WriteIfHandbrakeDetected("handbrake-cal-stop", 1);
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
            Slider[] sliders, TextBlock[] labels)
        {
            using (_suppressor.Begin())
            {
                for (int i = 0; i < 5; i++)
                {
                    sliders[i].Value = curve[i];
                    labels[i].Text = $"{curve[i]}";
                    dataArray[i] = curve[i];
                }
            }
            for (int i = 0; i < 5; i++)
                _plugin.WriteFloatIfPedalsDetected($"pedals-{pedal}-y{i + 1}", curve[i]);
            _plugin.SaveSettings();
        }

        // Throttle presets
        private void ThrottleCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("throttle", PedalCurvePresets[0], _data.PedalsThrottleCurve, _throttleCurveSliders!, _throttleCurveLabels!);
        private void ThrottleCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("throttle", PedalCurvePresets[1], _data.PedalsThrottleCurve, _throttleCurveSliders!, _throttleCurveLabels!);
        private void ThrottleCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("throttle", PedalCurvePresets[2], _data.PedalsThrottleCurve, _throttleCurveSliders!, _throttleCurveLabels!);
        private void ThrottleCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyPedalCurvePreset("throttle", PedalCurvePresets[3], _data.PedalsThrottleCurve, _throttleCurveSliders!, _throttleCurveLabels!);

        // Brake presets
        private void BrakeCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("brake", PedalCurvePresets[0], _data.PedalsBrakeCurve, _brakeCurveSliders!, _brakeCurveLabels!);
        private void BrakeCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("brake", PedalCurvePresets[1], _data.PedalsBrakeCurve, _brakeCurveSliders!, _brakeCurveLabels!);
        private void BrakeCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("brake", PedalCurvePresets[2], _data.PedalsBrakeCurve, _brakeCurveSliders!, _brakeCurveLabels!);
        private void BrakeCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyPedalCurvePreset("brake", PedalCurvePresets[3], _data.PedalsBrakeCurve, _brakeCurveSliders!, _brakeCurveLabels!);

        // Clutch presets
        private void ClutchCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("clutch", PedalCurvePresets[0], _data.PedalsClutchCurve, _clutchCurveSliders!, _clutchCurveLabels!);
        private void ClutchCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("clutch", PedalCurvePresets[1], _data.PedalsClutchCurve, _clutchCurveSliders!, _clutchCurveLabels!);
        private void ClutchCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("clutch", PedalCurvePresets[2], _data.PedalsClutchCurve, _clutchCurveSliders!, _clutchCurveLabels!);
        private void ClutchCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyPedalCurvePreset("clutch", PedalCurvePresets[3], _data.PedalsClutchCurve, _clutchCurveSliders!, _clutchCurveLabels!);

        // Throttle direction + range + curve sliders
        private void ThrottleDirCheck_Click(object sender, RoutedEventArgs e) { if (_suppressEvents) return; int v = ThrottleDirCheck.IsChecked == true ? 1 : 0; _data.PedalsThrottleDir = v; _plugin.WriteIfPedalsDetected("pedals-throttle-dir", v); _plugin.SaveSettings(); }
        private void ThrottleMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, ThrottleMinSlider, _data.PedalsThrottleMax, isMin: true, ThrottleMinValue, v => { _data.PedalsThrottleMin = v; _plugin.WriteIfPedalsDetected("pedals-throttle-min", v); });
        private void ThrottleMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, ThrottleMaxSlider, _data.PedalsThrottleMin, isMin: false, ThrottleMaxValue, v => { _data.PedalsThrottleMax = v; _plugin.WriteIfPedalsDetected("pedals-throttle-max", v); });
        private void ThrottleY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ThrottleY1Value, "", v => { _data.PedalsThrottleCurve[0] = v; _plugin.WriteFloatIfPedalsDetected("pedals-throttle-y1", v); });
        private void ThrottleY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ThrottleY2Value, "", v => { _data.PedalsThrottleCurve[1] = v; _plugin.WriteFloatIfPedalsDetected("pedals-throttle-y2", v); });
        private void ThrottleY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ThrottleY3Value, "", v => { _data.PedalsThrottleCurve[2] = v; _plugin.WriteFloatIfPedalsDetected("pedals-throttle-y3", v); });
        private void ThrottleY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ThrottleY4Value, "", v => { _data.PedalsThrottleCurve[3] = v; _plugin.WriteFloatIfPedalsDetected("pedals-throttle-y4", v); });
        private void ThrottleY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ThrottleY5Value, "", v => { _data.PedalsThrottleCurve[4] = v; _plugin.WriteFloatIfPedalsDetected("pedals-throttle-y5", v); });

        // Throttle calibration
        private void ThrottleCalStartButton_Click(object sender, RoutedEventArgs e) { _plugin.WriteIfPedalsDetected("pedals-throttle-cal-start", 1); }
        private void ThrottleCalStopButton_Click(object sender, RoutedEventArgs e) { _plugin.WriteIfPedalsDetected("pedals-throttle-cal-stop", 1); }

        // Brake direction + range + curve sliders
        private void BrakeDirCheck_Click(object sender, RoutedEventArgs e) { if (_suppressEvents) return; int v = BrakeDirCheck.IsChecked == true ? 1 : 0; _data.PedalsBrakeDir = v; _plugin.WriteIfPedalsDetected("pedals-brake-dir", v); _plugin.SaveSettings(); }
        private void BrakeAngleRatioSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeAngleRatioValue.Text = $"{v}%"; _data.PedalsBrakeAngleRatio = v; _plugin.WriteFloatIfPedalsDetected("pedals-brake-angle-ratio", v); _plugin.SaveSettings(); }
        private void BrakeMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, BrakeMinSlider, _data.PedalsBrakeMax, isMin: true, BrakeMinValue, v => { _data.PedalsBrakeMin = v; _plugin.WriteIfPedalsDetected("pedals-brake-min", v); });
        private void BrakeMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, BrakeMaxSlider, _data.PedalsBrakeMin, isMin: false, BrakeMaxValue, v => { _data.PedalsBrakeMax = v; _plugin.WriteIfPedalsDetected("pedals-brake-max", v); });
        private void BrakeY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, BrakeY1Value, "", v => { _data.PedalsBrakeCurve[0] = v; _plugin.WriteFloatIfPedalsDetected("pedals-brake-y1", v); });
        private void BrakeY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, BrakeY2Value, "", v => { _data.PedalsBrakeCurve[1] = v; _plugin.WriteFloatIfPedalsDetected("pedals-brake-y2", v); });
        private void BrakeY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, BrakeY3Value, "", v => { _data.PedalsBrakeCurve[2] = v; _plugin.WriteFloatIfPedalsDetected("pedals-brake-y3", v); });
        private void BrakeY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, BrakeY4Value, "", v => { _data.PedalsBrakeCurve[3] = v; _plugin.WriteFloatIfPedalsDetected("pedals-brake-y4", v); });
        private void BrakeY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, BrakeY5Value, "", v => { _data.PedalsBrakeCurve[4] = v; _plugin.WriteFloatIfPedalsDetected("pedals-brake-y5", v); });

        // Brake calibration
        private void BrakeCalStartButton_Click(object sender, RoutedEventArgs e) { _plugin.WriteIfPedalsDetected("pedals-brake-cal-start", 1); }
        private void BrakeCalStopButton_Click(object sender, RoutedEventArgs e) { _plugin.WriteIfPedalsDetected("pedals-brake-cal-stop", 1); }

        // Clutch direction + range + curve sliders
        private void ClutchDirCheck_Click(object sender, RoutedEventArgs e) { if (_suppressEvents) return; int v = ClutchDirCheck.IsChecked == true ? 1 : 0; _data.PedalsClutchDir = v; _plugin.WriteIfPedalsDetected("pedals-clutch-dir", v); _plugin.SaveSettings(); }
        private void ClutchMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, ClutchMinSlider, _data.PedalsClutchMax, isMin: true, ClutchMinValue, v => { _data.PedalsClutchMin = v; _plugin.WriteIfPedalsDetected("pedals-clutch-min", v); });
        private void ClutchMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, ClutchMaxSlider, _data.PedalsClutchMin, isMin: false, ClutchMaxValue, v => { _data.PedalsClutchMax = v; _plugin.WriteIfPedalsDetected("pedals-clutch-max", v); });
        private void ClutchY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ClutchY1Value, "", v => { _data.PedalsClutchCurve[0] = v; _plugin.WriteFloatIfPedalsDetected("pedals-clutch-y1", v); });
        private void ClutchY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ClutchY2Value, "", v => { _data.PedalsClutchCurve[1] = v; _plugin.WriteFloatIfPedalsDetected("pedals-clutch-y2", v); });
        private void ClutchY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ClutchY3Value, "", v => { _data.PedalsClutchCurve[2] = v; _plugin.WriteFloatIfPedalsDetected("pedals-clutch-y3", v); });
        private void ClutchY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ClutchY4Value, "", v => { _data.PedalsClutchCurve[3] = v; _plugin.WriteFloatIfPedalsDetected("pedals-clutch-y4", v); });
        private void ClutchY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ClutchY5Value, "", v => { _data.PedalsClutchCurve[4] = v; _plugin.WriteFloatIfPedalsDetected("pedals-clutch-y5", v); });

        // Clutch calibration
        private void ClutchCalStartButton_Click(object sender, RoutedEventArgs e) { _plugin.WriteIfPedalsDetected("pedals-clutch-cal-start", 1); }
        private void ClutchCalStopButton_Click(object sender, RoutedEventArgs e) { _plugin.WriteIfPedalsDetected("pedals-clutch-cal-stop", 1); }

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

        private void DisableSerialProbeFallbackCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.DisableSerialProbeFallback = DisableSerialProbeFallbackCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void DisableAb9DetectionCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.DisableAb9Detection = DisableAb9DetectionCheck.IsChecked == true;
            _plugin.SaveSettings();
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

            using (_suppressor.Begin())
            {
                AutoApplyProfileCheck.IsChecked = _plugin.Settings.AutoApplyProfileOnLaunch;
                LimitWheelUpdatesCheck.IsChecked = _plugin.Settings.LimitWheelUpdates;
                ConnectionToggle.IsChecked = _plugin.Settings.ConnectionEnabled;
                ProfileListControl.DataContext = null;
                ProfileListControl.DataContext = _plugin.ProfileStore;
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

            using (_suppressor.Begin())
            {
                var s = _plugin.Settings;
                UploadDashboardCheck.IsChecked = s.TelemetryUploadDashboard;
                DownloadDashboardCheck.IsChecked = s.TelemetryDownloadDashboard;
                FirmwareEraCombo.SelectedIndex = (int)s.TelemetryWheelEra;
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
            _plugin.ActiveTelemetryWheelEra = (idx >= 0 && idx <= 3)
                ? (MozaWheelEra)idx
                : MozaWheelEra.Auto;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

        // ── Diagnostics tab ─────────────────────────────────────────────
        private void RefreshDiagnosticsTab()
        {
            if (DiagWheelIdentityBox == null) return;
            DiagPluginBox.Text = DiagnosticsTextBuilder.BuildPluginInfo();
            if (DiagUsbDetectionBox != null)
                DiagUsbDetectionBox.Text = DiagnosticsTextBuilder.BuildUsbDetection(_plugin);
            DiagWheelIdentityBox.Text = DiagnosticsTextBuilder.BuildWheelIdentity(_data);
            DiagDisplayIdentityBox.Text = DiagnosticsTextBuilder.BuildDisplayIdentity(_data);
            DiagDashboardStateBox.Text = DiagnosticsTextBuilder.BuildDashboardState(_plugin);
            DiagTileServerBox.Text = DiagnosticsTextBuilder.BuildTileServer(_plugin);
            DiagSessionBox.Text = DiagnosticsTextBuilder.BuildSessionState(_plugin);
            if (DiagWheelCatalogBox != null)
                DiagWheelCatalogBox.Text = DiagnosticsTextBuilder.BuildWheelCatalog(_plugin);
            if (DiagSubscriptionBox != null)
                DiagSubscriptionBox.Text = DiagnosticsTextBuilder.BuildSubscription(_plugin);
            if (DiagSubscriptionResponseBox != null)
                DiagSubscriptionResponseBox.Text = DiagnosticsTextBuilder.BuildSubscriptionResponse(_plugin);
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
            sb.AppendLine("=== USB detection ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildUsbDetection(_plugin));
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
            sb.AppendLine(DiagnosticsTextBuilder.BuildWheelCatalog(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Last subscription sent ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildSubscription(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Wheel response on 0x02 (post-subscription window) ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildSubscriptionResponse(_plugin));
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
            var modelSlug = DiagnosticsBundleWriter.BuildWheelModelFilenameSlug(_data?.WheelModelName);
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
                var captureText = _serialCaptureRendered ?? "(no capture buffer — click Start, exercise the device, then Stop)\n";
                DiagnosticsBundleWriter.Write(dlg.FileName, BuildDiagnosticsDump(), captureText, _serialCaptureSnapshot);
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

        // Build a filesystem-safe slug from the active wheel's firmware model name
        // for use as a filename prefix on diagnostics bundles. Prefers the curated
        // friendly name (e.g. "CS Pro" for firmware "W17"); falls back to the raw
        // prefix for unknown wheels. Returns "" when no model is known yet so the
        // caller can omit the prefix entirely rather than emit a leading dash.
        
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
                _plugin.WriteColorIfWheelDetected($"{cfg.ColorCmdPrefix}{i + 1}", r, g, b);
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
                _plugin.WriteColorIfWheelDetected($"{cfg.ColorCmdPrefix}{i + 1}", 0, 0, 0);
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
            _plugin.WriteColorIfWheelDetected($"{cfg.ColorCmdPrefix}{idx + 1}", r, g, b);
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
            _plugin.WriteArrayIfWheelDetected(cfg.LiveBitmaskCmd, maskBytes);
        }

        private void ExtLedSendBrightness_Click(object sender, RoutedEventArgs e)
        {
            var (cfg, slider) = ((DiagLedCfg, Slider))((Button)sender).Tag;
            _plugin.WriteIfWheelDetected(cfg.BrightnessCmd, (int)slider.Value);
        }

        private void ExtLedSendMode_Click(object sender, RoutedEventArgs e)
        {
            var (cfg, modeCombo) = ((DiagLedCfg, ComboBox))((Button)sender).Tag;
            if (cfg.ModeCmd == null) return;
            if (modeCombo.SelectedIndex < 0) return;
            _plugin.WriteIfWheelDetected(cfg.ModeCmd, modeCombo.SelectedIndex);
        }

        // ── Dashboard Upload tab ─────────────────────────────────────────
        // Lets the user pick a .mzdash file (or a library entry) and push it
        // to the connected wheel via TelemetrySender.TriggerManualUpload.
        // Status panel reflects the WheelUploadCoordinator's latest ack:
        // in-flight flag, bytes_written / total_size, status byte.

        // Source bytes + name held in the UI while the user picks; pushed to
        // the uploader on UploadNow_Click. Decouples picking from uploading
        // so the user can review the parsed name/MD5 before sending.
        private byte[]? _uploadPickedContent;
        private string _uploadPickedName = "";
        private string _uploadPickedSourceLabel = "";
        // Directory the mzdash file lives in. Used to find sibling PNGs at
        // <dir>/Resource/MD5/<hex>.png for the multi-file upload bundle.
        // Empty for library/embedded picks.
        private string _uploadPickedSourceDirectory = "";
        private bool _uploadLibrarySeeded;

        private void UploadSourceRadio_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool libMode = UploadSourceLibraryRadio?.IsChecked == true;
            if (UploadFilePanel != null)
                UploadFilePanel.Visibility = libMode ? Visibility.Collapsed : Visibility.Visible;
            if (UploadLibraryPanel != null)
                UploadLibraryPanel.Visibility = libMode ? Visibility.Visible : Visibility.Collapsed;
            if (libMode) SeedUploadLibrary(force: false);
        }

        private void UploadPickFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Moza dashboard (*.mzdash)|*.mzdash|All files (*.*)|*.*",
                Title = "Pick a .mzdash file to upload",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(dlg.FileName);
                _uploadPickedContent = bytes;
                _uploadPickedName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName) ?? "";
                _uploadPickedSourceLabel = dlg.FileName;
                _uploadPickedSourceDirectory = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
                if (UploadPickedFileText != null)
                    UploadPickedFileText.Text = dlg.FileName;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to read .mzdash file:\n{ex.Message}",
                    "Moza", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private void UploadLibraryRefresh_Click(object sender, RoutedEventArgs e)
        {
            SeedUploadLibrary(force: true);
        }

        private void SeedUploadLibrary(bool force)
        {
            if (UploadLibraryCombo == null) return;
            if (_uploadLibrarySeeded && !force) return;
            using (_suppressor.Begin())
            {
                string? prev = UploadLibraryCombo.SelectedItem as string;
                UploadLibraryCombo.Items.Clear();
                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_plugin.DashCache != null)
                {
                    foreach (var name in _plugin.DashCache.CachedNames)
                        if (seen.Add(name)) UploadLibraryCombo.Items.Add(name);
                }
                foreach (var p in _plugin.DashProfileStore.BuiltinProfiles)
                    if (seen.Add(p.Name)) UploadLibraryCombo.Items.Add(p.Name);
                if (!string.IsNullOrEmpty(prev) && UploadLibraryCombo.Items.Contains(prev))
                    UploadLibraryCombo.SelectedItem = prev;
                else if (UploadLibraryCombo.Items.Count > 0 && UploadLibraryCombo.SelectedItem == null)
                    UploadLibraryCombo.SelectedIndex = 0;
            }
            _uploadLibrarySeeded = true;
        }

        private void UploadLibraryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (UploadLibraryCombo?.SelectedItem is not string name || string.IsNullOrEmpty(name))
                return;
            byte[]? bytes = DashboardLibraryResolver.ResolveBytes(_plugin.DashCache, _plugin.DashProfileStore, name);
            if (bytes == null)
            {
                _uploadPickedContent = null;
                _uploadPickedName = "";
                _uploadPickedSourceLabel = "";
                _uploadPickedSourceDirectory = "";
                if (UploadStatusText != null)
                    UploadStatusText.Text = $"Cannot resolve raw bytes for \"{name}\" — pick a local file instead.";
                return;
            }
            _uploadPickedContent = bytes;
            _uploadPickedName = name;
            _uploadPickedSourceLabel = $"library: {name}";
            // Library/folder entries: try to resolve the source dir from
            // DashCache so widget PNG assets can be looked up. Builtins from
            // embedded resources have no dir → single-file upload.
            _uploadPickedSourceDirectory = DashboardLibraryResolver.ResolveDirectory(_plugin.DashCache, name);
            if (UploadStatusText != null && UploadStatusText.Text.StartsWith("Cannot resolve"))
                UploadStatusText.Text = "idle";
        }

        private void UploadNow_Click(object sender, RoutedEventArgs e)
        {
            var ts = _plugin.TelemetrySender;
            if (ts == null)
            {
                if (UploadStatusText != null)
                    UploadStatusText.Text = "Telemetry sender unavailable (plugin not initialised).";
                return;
            }
            if (_uploadPickedContent == null || _uploadPickedContent.Length == 0)
            {
                if (UploadStatusText != null)
                    UploadStatusText.Text = "Pick a .mzdash file or library entry first.";
                return;
            }
            string name = !string.IsNullOrEmpty(_uploadPickedName) ? _uploadPickedName : "dashboard";
            string? sourceDir = string.IsNullOrEmpty(_uploadPickedSourceDirectory)
                ? null
                : _uploadPickedSourceDirectory;
            bool queued = ts.TriggerManualUpload(_uploadPickedContent, name, sourceDir);
            if (UploadStatusText != null)
            {
                UploadStatusText.Text = queued
                    ? $"Upload queued — pushing \"{name}\" to the wheel…"
                    : "Upload not started — wheel not connected or no management session yet.";
            }
        }

        private void RefreshDashboardUploadTab()
        {
            if (UploadInfoNameText == null) return; // tab template not yet realized
            var ts = _plugin.TelemetrySender;

            string activeName = ts?.MzdashName ?? "";
            string displayName = !string.IsNullOrEmpty(_uploadPickedName)
                ? _uploadPickedName
                : (!string.IsNullOrEmpty(activeName) ? activeName : "—");
            UploadInfoNameText.Text = displayName;

            int rawSize = _uploadPickedContent?.Length ?? ts?.MzdashContent?.Length ?? 0;
            UploadInfoRawSizeText.Text = rawSize > 0 ? $"{rawSize:N0} bytes" : "—";

            byte[]? bytes = _uploadPickedContent ?? ts?.MzdashContent;
            UploadInfoMd5Text.Text = bytes != null && bytes.Length > 0
                ? FileTransferBuilder.Md5Hex(FileTransferBuilder.ComputeMd5(bytes))
                : "—";

            bool inFlight = ts?.IsUploadInFlight ?? false;
            UploadInfoInFlightText.Text = inFlight ? "yes" : "no";
            UploadInfoInFlightText.Foreground = inFlight ? Brushes.Goldenrod : Brushes.Gray;

            uint bw = ts?.UploadLastBytesWritten ?? 0;
            uint total = ts?.UploadLastTotalSize ?? 0;
            UploadInfoProgressText.Text = total == 0
                ? "—"
                : $"{bw:N0} / {total:N0}" + (bw == total && total != 0 ? "  (complete)" : "");

            byte status = ts?.UploadLastStatusByte ?? 0;
            UploadInfoStatusByteText.Text = status == 0 ? "—" : $"0x{status:X2}";

            // Surface an automatic status hint when an upload finishes so the
            // user doesn't have to interpret bw == total themselves.
            if (UploadStatusText != null && !inFlight && total != 0)
            {
                if (bw == total)
                    UploadStatusText.Text = $"Upload complete (bytes_written={bw} == total_size={total}, status=0x{status:X2})";
                else if (UploadStatusText.Text.StartsWith("Upload queued"))
                    UploadStatusText.Text = $"Upload stopped (bytes_written={bw} / total_size={total}, status=0x{status:X2})";
            }

            // Enable the upload button only when the wheel is connected and a
            // management session has been negotiated — TriggerManualUpload
            // rejects otherwise.
            if (UploadNowButton != null)
                UploadNowButton.IsEnabled = ts != null
                    && _uploadPickedContent != null
                    && _uploadPickedContent.Length > 0
                    && _data.IsConnected;
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
            using (_suppressor.Begin())
            {
                SetAb9ModeCombo(ab9.Mode);
                SetAb9Slider(Ab9MechResistanceSlider,    Ab9MechResistanceValue,    ab9.MechanicalResistance);
                SetAb9Slider(Ab9SpringSlider,            Ab9SpringValue,            ab9.Spring);
                SetAb9Slider(Ab9DampingSlider,           Ab9DampingValue,           ab9.NaturalDamping);
                SetAb9Slider(Ab9FrictionSlider,          Ab9FrictionValue,          ab9.NaturalFriction);
                SetAb9Slider(Ab9MaxTorqueSlider,         Ab9MaxTorqueValue,         ab9.MaxTorqueLimit);
                SetAb9Slider(Ab9EngineVibIntensitySlider, Ab9EngineVibIntensityValue, ab9.EngineVibrationIntensity);
                Ab9EngineVibFreqSlider.Value = ab9.EngineVibrationFrequency;
                Ab9EngineVibFreqValue.Text = ab9.EngineVibrationFrequency + " Hz";
                SetAb9Slider(Ab9GearShiftVibSlider,       Ab9GearShiftVibValue,       ab9.GearShiftVibrationIntensity);
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

        // Engine Vibration intensity (host-rendered). The 91 Hz worker thread
        // reads the new value from the profile on its next tick — no device
        // command is sent.
        private void Ab9EngineVibIntensitySlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            byte v = (byte)Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            Ab9EngineVibIntensityValue.Text = v.ToString();
            GetOrCreateAb9Profile().EngineVibrationIntensity = v;
            _plugin.SaveSettings();
        }

        // Engine Vibration frequency slider — literal target Hz (0..300) of
        // the AB9 oscillator. Host-rendered, no device-side write; the worker
        // thread picks up the new value on its next tick.
        private void Ab9EngineVibFreqSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            ushort v = (ushort)Math.Max(0, Math.Min(300, (int)Math.Round(e.NewValue)));
            Ab9EngineVibFreqValue.Text = v + " Hz";
            GetOrCreateAb9Profile().EngineVibrationFrequency = v;
            _plugin.SaveSettings();
        }

        // Gear-shift vibration intensity. Fires one 0x0A 0x01 config write per
        // change so the AB9 firmware persists the new stored intensity for its
        // autonomous shift-rumble.
        private void Ab9GearShiftVibSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            byte v = (byte)Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            Ab9GearShiftVibValue.Text = v.ToString();
            GetOrCreateAb9Profile().GearShiftVibrationIntensity = v;
            _plugin.Ab9Manager?.SendGearShiftVibrationIntensity(v);
            _plugin.SaveSettings();
        }

        // =====================================================================
        // mBooster tab — multi-device. ComboBox selects the active device;
        // settings panel below populates from the selection's per-device
        // entry in MozaProfile.MBoosterSettings (lazily created).
        // =====================================================================

        private string? _mboosterSelectedIdentity;
        private bool _mboosterUiSeeded;

        private void RefreshMBoosterTab()
        {
            var registry = _plugin?.MBoosterRegistry;
            if (registry == null) { MBoosterTab.Visibility = Visibility.Collapsed; return; }
            var devices = registry.Devices;
            if (devices.Count == 0)
            {
                MBoosterTab.Visibility = Visibility.Collapsed;
                _mboosterUiSeeded = false;
                return;
            }
            MBoosterTab.Visibility = Visibility.Visible;

            // Rebuild the device combo if the list changed.
            using (_suppressor.Begin())
            {
                int prevSelected = -1;
                for (int i = 0; i < devices.Count; i++)
                {
                    if (string.Equals(devices[i].Identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase))
                    {
                        prevSelected = i;
                        break;
                    }
                }
                if (MBoosterDeviceCombo.Items.Count != devices.Count)
                {
                    MBoosterDeviceCombo.Items.Clear();
                    for (int i = 0; i < devices.Count; i++)
                    {
                        var c = devices[i];
                        var item = new ComboBoxItem
                        {
                            Content = $"{MBoosterDeviceController.ShortIdentity(c.Identity)} ({c.PortName})",
                            Tag = c.Identity,
                        };
                        MBoosterDeviceCombo.Items.Add(item);
                    }
                }
                if (prevSelected < 0) prevSelected = 0;
                if (MBoosterDeviceCombo.SelectedIndex != prevSelected)
                    MBoosterDeviceCombo.SelectedIndex = prevSelected;
                _mboosterSelectedIdentity = devices[prevSelected].Identity;
            }

            var selected = registry.FindByIdentity(_mboosterSelectedIdentity ?? "");
            if (selected == null)
            {
                MBoosterStatusDot.Fill = Brushes.Gray;
                MBoosterStatusLabel.Text = "No mBooster selected";
                MBoosterDevicePanel.Visibility = Visibility.Collapsed;
                return;
            }

            MBoosterStatusDot.Fill = selected.Detected
                ? Brushes.LimeGreen
                : (selected.IsConnected ? Brushes.Goldenrod : Brushes.Gray);
            MBoosterStatusLabel.Text = selected.Detected
                ? $"Connected ({MBoosterDeviceController.ShortIdentity(selected.Identity)})"
                : (selected.IsConnected ? "Probing…" : "Disconnected");
            MBoosterDevicePanel.Visibility = Visibility.Visible;

            // Live position bar — driven by the controller's latest HID position.
            int pct = (int)Math.Round(selected.LastHidPosition * 100);
            if (pct < 0) pct = 0; if (pct > 100) pct = 100;
            MBoosterPositionBar.Value = pct;
            MBoosterPositionLabel.Text = pct + " %";

            if (_mboosterUiSeeded) return;
            // Seed slider/checkbox values from the profile entry. _plugin is
            // never null past Init (the constructor stores it); guard anyway.
            if (_plugin == null) return;
            var s = _plugin.GetOrCreateMBoosterSettings(selected.Identity);
            using (_suppressor.Begin())
            {
                SetMBoosterRoleCombo(s.Role);
                MBoosterAbsEnable.IsChecked       = s.Abs?.Enabled       ?? false;
                MBoosterAbsIntensity.Value        = s.Abs?.IntensityPct  ?? 50;
                MBoosterAbsIntensityValue.Text    = (s.Abs?.IntensityPct ?? 50).ToString();
                MBoosterLockupEnable.IsChecked    = s.Lockup?.Enabled       ?? false;
                MBoosterLockupIntensity.Value     = s.Lockup?.IntensityPct  ?? 50;
                MBoosterLockupIntensityValue.Text = (s.Lockup?.IntensityPct ?? 50).ToString();
                MBoosterThresholdEnable.IsChecked    = s.Threshold?.Enabled       ?? false;
                MBoosterThresholdIntensity.Value     = s.Threshold?.IntensityPct  ?? 50;
                MBoosterThresholdIntensityValue.Text = (s.Threshold?.IntensityPct ?? 50).ToString();
                MBoosterEngineEnable.IsChecked    = s.Engine?.Enabled       ?? false;
                MBoosterEngineIntensity.Value     = s.Engine?.IntensityPct  ?? 50;
                MBoosterEngineIntensityValue.Text = (s.Engine?.IntensityPct ?? 50).ToString();
                MBoosterDirCheck.IsChecked = (s.Direction == 1);
                MBoosterMinSlider.Value = s.Min >= 0 ? s.Min : 0;
                MBoosterMinValue.Text = MBoosterMinSlider.Value.ToString("F0");
                MBoosterMaxSlider.Value = s.Max >= 0 ? s.Max : 0;
                MBoosterMaxValue.Text = MBoosterMaxSlider.Value.ToString("F0");
            }
            _mboosterUiSeeded = true;
        }

        private void SetMBoosterRoleCombo(MBoosterRole role)
        {
            for (int i = 0; i < MBoosterRoleCombo.Items.Count; i++)
            {
                if (MBoosterRoleCombo.Items[i] is ComboBoxItem ci
                    && ci.Tag is string tag
                    && int.TryParse(tag, out int v)
                    && v == (int)role)
                {
                    MBoosterRoleCombo.SelectedIndex = i;
                    return;
                }
            }
            MBoosterRoleCombo.SelectedIndex = 0;
        }

        private MBoosterDeviceSettings? CurrentMBoosterSettings()
        {
            if (_mboosterSelectedIdentity == null) return null;
            return _plugin.GetOrCreateMBoosterSettings(_mboosterSelectedIdentity);
        }

        private MBoosterDeviceController? CurrentMBoosterController()
        {
            return _plugin?.MBoosterRegistry?.FindByIdentity(_mboosterSelectedIdentity ?? "");
        }

        private void MBoosterDeviceCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (MBoosterDeviceCombo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string identity) return;
            if (string.Equals(identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)) return;
            _mboosterSelectedIdentity = identity;
            _mboosterUiSeeded = false;
            RefreshMBoosterTab();
        }

        private void MBoosterRoleCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            if (MBoosterRoleCombo.SelectedItem is not ComboBoxItem ci) return;
            if (ci.Tag is not string tag || !int.TryParse(tag, out int v)) return;
            s.Role = (MBoosterRole)v;
            _plugin.SaveSettings();
        }

        // ===== Effect handlers (4 × enable + slider + test button) ==========

        private void MBoosterAbsEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            (s.Abs ??= new MBoosterEffectSettings()).Enabled = MBoosterAbsEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterAbsIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterAbsIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            (s.Abs ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        private void MBoosterAbsTest_Click(object sender, RoutedEventArgs e)
        {
            CurrentMBoosterController()?.FireEffectTest(global::MozaPlugin.Protocol.MBoosterEffectId.Abs);
        }

        private void MBoosterLockupEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            (s.Lockup ??= new MBoosterEffectSettings()).Enabled = MBoosterLockupEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterLockupIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterLockupIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            (s.Lockup ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        private void MBoosterLockupTest_Click(object sender, RoutedEventArgs e)
        {
            CurrentMBoosterController()?.FireEffectTest(global::MozaPlugin.Protocol.MBoosterEffectId.Lockup);
        }

        private void MBoosterThresholdEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            (s.Threshold ??= new MBoosterEffectSettings()).Enabled = MBoosterThresholdEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterThresholdIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterThresholdIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            (s.Threshold ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        private void MBoosterThresholdTest_Click(object sender, RoutedEventArgs e)
        {
            CurrentMBoosterController()?.FireEffectTest(global::MozaPlugin.Protocol.MBoosterEffectId.Threshold);
        }

        private void MBoosterEngineEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            (s.Engine ??= new MBoosterEffectSettings()).Enabled = MBoosterEngineEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterEngineIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterEngineIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            (s.Engine ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        private void MBoosterEngineTest_Click(object sender, RoutedEventArgs e)
        {
            CurrentMBoosterController()?.FireEffectTest(global::MozaPlugin.Protocol.MBoosterEffectId.Engine);
        }

        // ===== Calibration (experimental) ===================================

        private void MBoosterDirCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            s.Direction = MBoosterDirCheck.IsChecked == true ? 1 : 0;
            _plugin.SaveSettings();
        }
        private void MBoosterMinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            MBoosterMinValue.Text = v.ToString();
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            s.Min = v;
            _plugin.SaveSettings();
        }
        private void MBoosterMaxSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            MBoosterMaxValue.Text = v.ToString();
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            s.Max = v;
            _plugin.SaveSettings();
        }
        private void MBoosterReadCalButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentMBoosterController()?.RequestCalibrationReads();
        }
        private void MBoosterApplyCalButton_Click(object sender, RoutedEventArgs e)
        {
            var c = CurrentMBoosterController();
            var s = CurrentMBoosterSettings();
            if (c == null || s == null) return;
            _plugin.ApplyMBoosterToHardware(c, s);
        }

    }
}
