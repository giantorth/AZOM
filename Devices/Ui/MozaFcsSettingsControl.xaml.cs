using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MozaControls;
using MozaPlugin.Resources;
using MozaPlugin.Settings;
using MozaPlugin.UI;

namespace MozaPlugin.Devices.Ui
{
    /// <summary>
    /// FCS device page: Inputs (live axes/buttons, calibration, joystick modes) and
    /// LEDs (light mode, brightness, colours, standby effect). LED settings follow
    /// the FCS profile; axis modes and thresholds are device-stored.
    /// Protocol: docs/protocol/devices/fcs.md.
    /// </summary>
    public partial class MozaFcsSettingsControl : UserControl
    {
        private MozaPlugin? _plugin;
        private MozaData? _data;
        private readonly EventSuppressor _suppressor = new EventSuppressor();
        private bool _suppressEvents => _suppressor.Suppressed;

        private readonly DispatcherTimer _refreshTimer;
        private DispatcherTimer? _liveTimer;
        private ActiveButtonsRenderer? _activeButtons;
        private Border[]? _colorSwatches;

        // ── Calibration ──
        // control id (wire), checkCalib axes in Pit House's read order, label.
        private sealed class CalRow
        {
            public byte Control;
            public byte[] Axes = Array.Empty<byte>();
            public bool JoystickOnly;
            public FrameworkElement Root = null!;
            public Button Start = null!;
            public Button Cancel = null!;
            public TextBlock Status = null!;
            public TextBlock Instruction = null!;
        }
        private readonly List<CalRow> _calRows = new List<CalRow>();
        private CalRow? _calRunning;
        private CalRow? _calChecking;
        private DateTime _calCheckDeadline;
        private DispatcherTimer? _calReadDelay;

        // ── Axis modes (index 0 thumbstick XY, 1 joystick XY, 2 joystick Z) ──
        private sealed class ModeRow
        {
            public int Index;
            public bool JoystickOnly;
            public FrameworkElement Root = null!;
            public SegmentedControl Mode = null!;
            public FrameworkElement ThresholdRow = null!;
            public Slider Threshold = null!;
            public TextBox ThresholdValue = null!;
        }
        private readonly List<ModeRow> _modeRows = new List<ModeRow>();

        // Row labels in the half-width calibration / modes cards.
        private const double CardLabelWidth = 130;

        public MozaFcsSettingsControl()
        {
            using (_suppressor.Begin())
            {
                InitializeComponent();
                BuildCalibrationRows();
                BuildModeRows();
                _colorSwatches = BuildColorRow();
            }
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += OnRefreshTick;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
            UpdateLiveTimer();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            StopLiveTimer();
            // Don't leave the device mid-calibration when the page goes away.
            if (_calRunning != null) CancelCalibration(_calRunning);
        }

        private bool ResolvePlugin()
        {
            _plugin = MozaPlugin.Instance;
            if (_plugin == null) return false;
            _data = _plugin.Data;
            return true;
        }

        private void OnRefreshTick(object? sender, EventArgs e)
        {
            try { Refresh(); }
            catch (Exception ex) { MozaLog.DebugIfChanged("ui-tick-fcs", $"[AZOM] FCS page tick failed: {ex}"); }
        }

        // ===== Refresh =====

        private void Refresh()
        {
            if (!ResolvePlugin() || _plugin == null || _data == null) return;
            var plugin = _plugin;
            var fcs = _data.Fcs;

            DeviceProfileSelector.Bind(DeviceProfileRow, DeviceProfileList,
                plugin.DeviceProfiles?.FcsStore(DeviceProfileCoordinator.FcsPageGuid, seed: true));

            bool detected = plugin.IsFcsDetected;
            using (_suppressor.Begin())
            {
                NotDetectedPanel.Visibility = detected ? Visibility.Collapsed : Visibility.Visible;
                FcsTabs.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;
                if (!detected) return;

                RightRodText.Text = RodTypeName(fcs.RightRodType);

                // An external right rod replaces the built-in joystick (Pit House hides
                // the joystick rows then).
                bool externalRod = fcs.RightRodType >= 2;
                foreach (var r in _calRows)
                    r.Root.Visibility = r.JoystickOnly && externalRod ? Visibility.Collapsed : Visibility.Visible;
                foreach (var m in _modeRows)
                {
                    m.Root.Visibility = m.JoystickOnly && externalRod ? Visibility.Collapsed : Visibility.Visible;
                    int mode = fcs.AxisMode[m.Index];
                    if (mode >= 0) UiHelpers.SetComboSafe(m.Mode, Math.Min(mode, 1));
                    m.ThresholdRow.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
                    int raw = fcs.AxisThreshold[m.Index];
                    if (raw >= 0)
                    {
                        m.Threshold.Value = Clamp(RawToPct(raw), 1, 100);
                        UiHelpers.SetValueText(m.ThresholdValue, $"{(int)m.Threshold.Value}");
                    }
                }
                TickCalibrationCheck();

                if (fcs.LightMode >= 0) UiHelpers.SetComboSafe(LightModeCombo, Clamp(fcs.LightMode, 0, 2));
                if (fcs.Brightness >= 0)
                {
                    BrightnessSlider.Value = Clamp(fcs.Brightness, 0, 100);
                    UiHelpers.SetValueText(BrightnessValue, $"{(int)BrightnessSlider.Value}");
                }
                // Pulse needs app firmware 1.2.7.0+.
                bool pulse = fcs.FwAtLeast(1, 2, 7, 0);
                PulseItem.Visibility = pulse ? Visibility.Visible : Visibility.Collapsed;
                if (fcs.StandbyMode >= 0)
                    UiHelpers.SetComboSafe(StandbyModeCombo, Clamp(fcs.StandbyMode, 0, pulse ? 6 : 5));
                RefreshStandbySpeedRow();

                if (_colorSwatches != null)
                    for (int i = 0; i < _colorSwatches.Length; i++)
                    {
                        int c = fcs.Colors[i];
                        if (c >= 0)
                            _colorSwatches[i].Background =
                                WheelUiHelpers.GetCachedBrush((byte)(c >> 16), (byte)(c >> 8), (byte)c);
                    }
            }
        }

        private void RefreshStandbySpeedRow()
        {
            if (_data == null) return;
            int mode = _data.Fcs.StandbyMode;
            bool animated = mode >= 2 && mode <= 6;
            StandbySpeedRow.Visibility = animated ? Visibility.Visible : Visibility.Collapsed;
            if (!animated) return;
            int ms = _data.Fcs.StandbyIntervals[mode];
            if (ms < 0) return;
            StandbySpeedSlider.Value = Clamp(ms, (int)StandbySpeedSlider.Minimum, (int)StandbySpeedSlider.Maximum);
            UiHelpers.SetValueText(StandbySpeedValue, $"{(int)StandbySpeedSlider.Value}");
        }

        private static string RodTypeName(int type)
        {
            switch (type)
            {
                case 0: return Strings.Option_RodNotConnected;
                case 1: return Strings.Option_RodBuiltIn;
                case 2: return "MHG";
                case 3: return "MA3X";
                case 4: return "MH16";
                default: return "—";
            }
        }

        // ===== Live inputs (33 ms, Inputs tab only) =====

        private void FcsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, FcsTabs)) return;
            UpdateLiveTimer();
        }

        private void UpdateLiveTimer()
        {
            if (IsLoaded && ReferenceEquals(FcsTabs.SelectedItem, InputsTab))
            {
                if (_liveTimer != null) return;
                _liveTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
                _liveTimer.Tick += (_, __) => RefreshLive();
                _liveTimer.Start();
            }
            else StopLiveTimer();
        }

        private void StopLiveTimer()
        {
            _liveTimer?.Stop();
            _liveTimer = null;
        }

        private void RefreshLive()
        {
            if (_data == null) return;
            var fcs = _data.Fcs;
            _activeButtons ??= new ActiveButtonsRenderer(ActiveButtonsText, MozaData.FcsState.MaxButtons);
            if (!fcs.IsHidConnected)
            {
                ThrottleBar.Value = JoystickXBar.Value = JoystickYBar.Value = 0;
                TwistBar.Value = ThumbXBar.Value = ThumbYBar.Value = 0;
                _activeButtons.ShowNone();
                return;
            }
            // Report order X, Y, Rx, Ry, Rz, Z.
            JoystickXBar.Value = fcs.AxisPct[0];
            JoystickYBar.Value = fcs.AxisPct[1];
            ThumbXBar.Value = fcs.AxisPct[2];
            ThumbYBar.Value = fcs.AxisPct[3];
            ThrottleBar.Value = fcs.AxisPct[4];
            TwistBar.Value = fcs.AxisPct[5];
            _activeButtons.Refresh(fcs.ButtonStates, fcs.ButtonCount);
        }

        // ===== Calibration =====

        private void BuildCalibrationRows()
        {
            AddCalRow(3, new byte[] { 0 }, Strings.Label_FcsThrottleLever, Strings.Hint_FcsCalThrottle, joystickOnly: false);
            AddCalRow(2, new byte[] { 1, 2 }, Strings.Label_FcsJoystickXy, Strings.Hint_FcsCalJoystickXy, joystickOnly: true);
            AddCalRow(4, new byte[] { 3 }, Strings.Label_FcsJoystickTwist, Strings.Hint_FcsCalTwist, joystickOnly: true);
            // Thumbstick X (axis 5) is read before Y (axis 4), as Pit House does.
            AddCalRow(1, new byte[] { 5, 4 }, Strings.Label_FcsThumbstick, Strings.Hint_FcsCalThumbstick, joystickOnly: false);
        }

        private void AddCalRow(byte control, byte[] axes, string label, string instruction, bool joystickOnly)
        {
            var row = new CalRow { Control = control, Axes = axes, JoystickOnly = joystickOnly };
            var root = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel"), Width = CardLabelWidth });
            row.Start = new Button { Content = Strings.Button_Calibrate, MinWidth = 110, Tag = row };
            row.Start.Click += CalStart_Click;
            row.Cancel = new Button
            {
                Content = Strings.Button_Cancel, MinWidth = 90, Tag = row,
                Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed,
                Style = (Style)FindResource("MozaButtonGhost"),
            };
            row.Cancel.Click += CalCancel_Click;
            row.Status = new TextBlock
            {
                Style = (Style)FindResource("MutedHintText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            panel.Children.Add(row.Start);
            panel.Children.Add(row.Cancel);
            panel.Children.Add(row.Status);
            root.Children.Add(panel);
            // Shown only while this control is calibrating.
            row.Instruction = new TextBlock
            {
                Text = instruction,
                Style = (Style)FindResource("MutedHintText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            root.Children.Add(row.Instruction);
            row.Root = root;
            CalibrationRows.Children.Add(root);
            _calRows.Add(row);
        }

        private void CalStart_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || _data == null || !((sender as Button)?.Tag is CalRow row)) return;
            if (_calRunning == row) { FinishCalibration(row); return; }
            if (_calRunning != null || _calChecking != null) return;

            _plugin.HardwareApplier.FcsCalibrate(row.Control, 1);
            _calRunning = row;
            row.Start.Content = Strings.Button_RangeDone;
            row.Cancel.Visibility = Visibility.Visible;
            row.Status.Text = "";
            row.Instruction.Visibility = Visibility.Visible;
            SetOtherCalRowsEnabled(row, false);
        }

        private void CalCancel_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is CalRow row && _calRunning == row) CancelCalibration(row);
        }

        private void CancelCalibration(CalRow row)
        {
            _plugin?.HardwareApplier.FcsCalibrate(row.Control, 2);
            _calRunning = null;
            ResetCalRow(row);
            row.Status.Text = "";
        }

        // Stop, then read each axis's result ~190 ms later (Pit House's spacing).
        private void FinishCalibration(CalRow row)
        {
            if (_plugin == null || _data == null) return;
            _plugin.HardwareApplier.FcsCalibrate(row.Control, 0);
            _calRunning = null;
            foreach (var a in row.Axes) _data.Fcs.CalibCheck[a] = -1;
            ResetCalRow(row);
            row.Start.IsEnabled = false;
            SetOtherCalRowsEnabled(row, false);
            _calChecking = row;
            _calCheckDeadline = DateTime.UtcNow.AddSeconds(5);

            _calReadDelay?.Stop();
            _calReadDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(190) };
            _calReadDelay.Tick += (_, __) =>
            {
                _calReadDelay?.Stop();
                _calReadDelay = null;
                foreach (var a in row.Axes) _plugin?.HardwareApplier.ReadFcs($"fcs-calib-check{a}");
            };
            _calReadDelay.Start();
        }

        private void TickCalibrationCheck()
        {
            var row = _calChecking;
            if (row == null || _data == null) return;
            var checks = _data.Fcs.CalibCheck;
            bool all = true;
            foreach (var a in row.Axes) all &= checks[a] >= 0;
            if (!all && DateTime.UtcNow < _calCheckDeadline) return;

            var failed = new List<string>();
            foreach (var a in row.Axes)
                if (checks[a] != 1) failed.Add(CheckAxisName(a));
            row.Status.Text = failed.Count == 0
                ? Strings.Status_Done
                : string.Format(Strings.Status_CalibrationFailed, string.Join(", ", failed));
            _calChecking = null;
            row.Start.IsEnabled = true;
            SetOtherCalRowsEnabled(row, true);
        }

        private static string CheckAxisName(byte axis)
        {
            switch (axis)
            {
                case 0: return Strings.Label_FcsThrottleLever;
                case 1: return Strings.Label_FcsJoystickX;
                case 2: return Strings.Label_FcsJoystickY;
                case 3: return Strings.Label_FcsJoystickTwist;
                case 4: return Strings.Label_FcsThumbstickY;
                default: return Strings.Label_FcsThumbstickX;
            }
        }

        private void ResetCalRow(CalRow row)
        {
            row.Start.Content = Strings.Button_Calibrate;
            row.Cancel.Visibility = Visibility.Collapsed;
            row.Instruction.Visibility = Visibility.Collapsed;
            SetOtherCalRowsEnabled(row, true);
        }

        private void SetOtherCalRowsEnabled(CalRow except, bool enabled)
        {
            foreach (var r in _calRows)
                if (r != except) r.Start.IsEnabled = enabled;
        }

        // ===== Joystick modes (device-stored) =====

        private void BuildModeRows()
        {
            AddModeRow(1, Strings.Label_FcsJoystickXy, joystickOnly: true);
            AddModeRow(2, Strings.Label_FcsJoystickTwist, joystickOnly: true);
            AddModeRow(0, Strings.Label_FcsThumbstick, joystickOnly: false);
        }

        private void AddModeRow(int index, string label, bool joystickOnly)
        {
            var row = new ModeRow { Index = index, JoystickOnly = joystickOnly };
            var root = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel"), Width = CardLabelWidth });
            row.Mode = new SegmentedControl { Tag = row };
            row.Mode.Items.Add(new ListBoxItem { Content = Strings.Option_Axis });
            row.Mode.Items.Add(new ListBoxItem { Content = Strings.Option_Button });
            row.Mode.SelectionChanged += Mode_Changed;
            line.Children.Add(row.Mode);
            root.Children.Add(line);

            var thr = new Grid { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            thr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            thr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            thr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var thrLabel = new TextBlock { Text = Strings.SliderLabel_ButtonThreshold, Style = (Style)FindResource("FieldLabel"), Width = CardLabelWidth };
            row.Threshold = new Slider
            {
                Style = (Style)FindResource("SettingSlider"),
                Minimum = 1, Maximum = 100, TickFrequency = 1, Value = 80, Tag = row,
            };
            row.Threshold.ValueChanged += Threshold_Changed;
            row.ThresholdValue = new TextBox { Style = (Style)FindResource("SliderValueEditBox"), Text = "80" };
            SliderValueBox.SetSlider(row.ThresholdValue, row.Threshold);
            Grid.SetColumn(thrLabel, 0);
            Grid.SetColumn(row.Threshold, 1);
            Grid.SetColumn(row.ThresholdValue, 2);
            thr.Children.Add(thrLabel);
            thr.Children.Add(row.Threshold);
            thr.Children.Add(row.ThresholdValue);
            row.ThresholdRow = thr;
            root.Children.Add(thr);

            row.Root = root;
            ModeRows.Children.Add(root);
            _modeRows.Add(row);
        }

        private void Mode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            if (!((sender as SegmentedControl)?.Tag is ModeRow row)) return;
            int v = row.Mode.SelectedIndex;
            if (v < 0) return;
            _data.Fcs.AxisMode[row.Index] = v;
            _plugin.HardwareApplier.WriteIfFcsDetected($"fcs-axis-mode{row.Index}", v);
            row.ThresholdRow.Visibility = v == 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            if (!((sender as Slider)?.Tag is ModeRow row)) return;
            int pct = (int)Math.Round(e.NewValue);
            row.ThresholdValue.Text = $"{pct}";
            int raw = PctToRaw(pct);
            _data.Fcs.AxisThreshold[row.Index] = raw;
            _plugin.HardwareApplier.WriteIfFcsDetected($"fcs-axis-threshold{row.Index}", raw);
        }

        // Threshold is u16 over full travel; Pit House shows it as 1..100 % (80 % = 0xCCCC).
        private static int PctToRaw(int pct) => (int)Math.Round(pct * 65535 / 100.0);
        private static int RawToPct(int raw) => (int)Math.Round(raw * 100 / 65535.0);

        // ===== LEDs (FCS profile) =====

        private Border[] BuildColorRow()
        {
            ColorPanel.Children.Add(SwatchRow.Build(this, Strings.Label_LedColors, MozaData.FcsState.LedCount,
                c => c, ColorSwatch_Click, out var swatches));
            return swatches;
        }

        private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            if (!((sender as Border)?.Tag is int idx)) return;
            int cur = _data.Fcs.Colors[idx];
            if (cur < 0) cur = 0;
            var dialog = new ColorPickerDialog((byte)(cur >> 16), (byte)(cur >> 8), (byte)cur)
            {
                Owner = Window.GetWindow(this),
            };
            if (dialog.ShowDialog() != true) return;
            byte r = dialog.SelectedR, g = dialog.SelectedG, b = dialog.SelectedB;
            int packed = (r << 16) | (g << 8) | b;
            _data.Fcs.Colors[idx] = packed;
            ((Border)sender).Background = WheelUiHelpers.GetCachedBrush(r, g, b);
            _plugin.UpdateFcsProfile(p => p.Colors = MozaFcsDeviceProfile.SetSlot(p.Colors, MozaData.FcsState.LedCount, idx, packed));
            _plugin.HardwareApplier.WriteFcsColorIfDetected(idx, r, g, b);
            _plugin.SaveSettings();
        }

        private void LightModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int v = LightModeCombo.SelectedIndex;
            if (v < 0) return;
            _data.Fcs.LightMode = v;
            _plugin.UpdateFcsProfile(p => p.LightMode = v);
            _plugin.HardwareApplier.WriteIfFcsDetected("fcs-light-mode", v);
            _plugin.SaveSettings();
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int v = (int)Math.Round(e.NewValue);
            BrightnessValue.Text = $"{v}";
            _data.Fcs.Brightness = v;
            _plugin.UpdateFcsProfile(p => p.Brightness = v);
            _plugin.HardwareApplier.WriteIfFcsDetected("fcs-brightness", v);
            _plugin.SaveSettings();
        }

        private void StandbyModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int v = StandbyModeCombo.SelectedIndex;
            if (v < 0) return;
            _data.Fcs.StandbyMode = v;
            _plugin.UpdateFcsProfile(p => p.StandbyMode = v);
            _plugin.HardwareApplier.WriteIfFcsDetected("fcs-standby-mode", v);
            _plugin.SaveSettings();
            using (_suppressor.Begin()) RefreshStandbySpeedRow();
        }

        // The interval register is per effect; the slider edits the selected one.
        private void StandbySpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int mode = _data.Fcs.StandbyMode;
            if (mode < 2 || mode > 6) return;
            int ms = (int)Math.Round(e.NewValue);
            StandbySpeedValue.Text = $"{ms}";
            _data.Fcs.StandbyIntervals[mode] = ms;
            _plugin.UpdateFcsProfile(p => p.StandbyIntervals = MozaFcsDeviceProfile.SetSlot(p.StandbyIntervals, 7, mode, ms));
            _plugin.HardwareApplier.WriteIfFcsDetected($"fcs-standby-interval{mode}", ms);
            _plugin.SaveSettings();
        }

        private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    }
}
