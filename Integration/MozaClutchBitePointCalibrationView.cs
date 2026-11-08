using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Resources;
using MozaPlugin.UI;
using SimHub.Plugins.UI;

namespace MozaPlugin.Integration
{
    /// <summary>
    /// Clutch source, calibration, Calibrated/Fallback picker and Trigger Input
    /// Level for the ShakeIt Clutch Bite Point editor — the same plugin state the
    /// mBooster tab's card shows (MozaPlugin.MBoosterBitePoint.cs). Trigger Input
    /// Level shows the calibrated value while it is in use (dragging pins it),
    /// else the effect's fallback. Polls while on screen.
    /// </summary>
    internal sealed class MozaClutchBitePointCalibrationView : UserControl
    {
        private readonly Model _model = new Model();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _calibrate = new Button { Padding = new Thickness(10, 2, 10, 2) };
        private readonly Button _reset = new Button
        {
            Content = Strings.Button_ResetCalibration,
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(8, 0, 0, 0),
        };
        private readonly CheckBox _auto = new CheckBox { Content = Strings.Label_AutoCalibrate, Margin = new Thickness(0, 6, 0, 6) };
        private readonly ComboBox _source = new ComboBox { MinWidth = 140, Margin = new Thickness(8, 0, 0, 0) };
        private readonly Button _sourceFormula = new Button
        {
            Content = "𝑓(𝑥)",
            ToolTip = Strings.Tooltip_EditFormula,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(6, 0, 0, 0),
        };
        private readonly TextBlock _sourceValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        private readonly TextBlock _sourceFormulaText = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 4),
            Opacity = 0.7,
        };
        private readonly ComboBox _useCalibrated = new ComboBox { MinWidth = 140, Margin = new Thickness(8, 0, 0, 0) };
        private readonly StackPanel _useCalibratedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        private readonly TitledSlider _trigger;
        private readonly MozaClutchBitePointContainer _effect;
        private bool _refreshing;

        public MozaClutchBitePointCalibrationView(MozaClutchBitePointContainer effect)
        {
            _effect = effect;
            var header = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_calibrate, 1);
            Grid.SetColumn(_reset, 2);
            header.Children.Add(_status);
            header.Children.Add(_calibrate);
            header.Children.Add(_reset);

            _useCalibrated.Items.Add(Strings.Option_BitePointCalibrated);
            _useCalibrated.Items.Add(Strings.Option_BitePointFallback);
            _useCalibratedRow.Children.Add(new TextBlock { Text = Strings.SliderLabel_Trigger, VerticalAlignment = VerticalAlignment.Center });
            _useCalibratedRow.Children.Add(_useCalibrated);

            _trigger = new TitledSlider
            {
                Title = Strings.SliderLabel_TriggerLevel,
                Minimum = MBoosterUiConstants.BitePointTriggerMinPct,
                Maximum = MBoosterUiConstants.BitePointTriggerMaxPct,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                DataContext = _model,
            };
            _trigger.SetBinding(TitledSlider.ValueProperty, new Binding(nameof(Model.Pct)) { Mode = BindingMode.TwoWay });

            _source.Items.Add(Strings.Option_ClutchSourceMBooster);
            _source.Items.Add(Strings.Motion_Source_GameTelemetry);
            _source.Items.Add(Strings.SliderLabel_Formula);
            var sourceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            sourceRow.Children.Add(new TextBlock { Text = Strings.Label_ClutchSource, VerticalAlignment = VerticalAlignment.Center });
            sourceRow.Children.Add(_source);
            sourceRow.Children.Add(_sourceFormula);
            sourceRow.Children.Add(_sourceValue);

            var root = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            root.Children.Add(sourceRow);
            root.Children.Add(_sourceFormulaText);
            root.Children.Add(header);
            root.Children.Add(_auto);
            root.Children.Add(_useCalibratedRow);
            root.Children.Add(_trigger);
            Content = root;

            _calibrate.Click += (s, e) => MozaPlugin.Instance?.ToggleBitePointCalibration();
            _reset.Click += (s, e) =>
            {
                MozaPlugin.Instance?.ResetClutchBitePoint();
                Refresh();
            };
            _source.SelectionChanged += (s, e) =>
            {
                var plugin = MozaPlugin.Instance;
                if (plugin == null || _refreshing || _source.SelectedIndex < 0) return;
                plugin.BitePointSource = (MBoosterBitePointSource)_source.SelectedIndex;
                Refresh();
                if (plugin.BitePointSource == MBoosterBitePointSource.Formula && plugin.BitePointSourceFormula.Length == 0)
                    EditFormula();
            };
            _sourceFormula.Click += (s, e) => EditFormula();
            _auto.Checked += (s, e) => SetAuto(true);
            _auto.Unchecked += (s, e) => SetAuto(false);
            _useCalibrated.SelectionChanged += (s, e) =>
            {
                if (_refreshing || _useCalibrated.SelectedIndex < 0) return;
                _effect.UseCalibratedBitePoint = _useCalibrated.SelectedIndex == 0;
                Refresh();
            };
            _model.PctEdited += pct =>
            {
                var plugin = MozaPlugin.Instance;
                if (plugin == null || _refreshing) return;
                string? game = plugin.CurrentGameName;
                if (UsesCalibration(plugin, plugin.ClutchBitePointInfo(game))) plugin.PinClutchBitePoint(game!, pct);
                else _effect.TriggerLevelPct = pct;
            };

            _timer.Tick += (s, e) => Refresh();
            Loaded += (s, e) => { Refresh(); _timer.Start(); };
            Unloaded += (s, e) => _timer.Stop();
        }

        private async void EditFormula()
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null) return;
            string? formula = await FormulaEditing.EditAsync(this, plugin.ChannelFormulaEngine,
                plugin.BitePointSourceFormula, Strings.Label_ClutchSource);
            if (formula == null) return;
            plugin.BitePointSourceFormula = formula;
            Refresh();
        }

        // Trigger Input Level edits the calibration while the effect is using it.
        private bool UsesCalibration(MozaPlugin plugin, in MozaPlugin.BitePointInfo info) =>
            _effect.UseCalibratedBitePoint && info.IsCalibrated && !string.IsNullOrEmpty(plugin.CurrentGameName);

        private void SetAuto(bool on)
        {
            var plugin = MozaPlugin.Instance;
            if (plugin != null && !_refreshing) plugin.BitePointAutoCalibrate = on;
        }

        private void Refresh()
        {
            var plugin = MozaPlugin.Instance;
            if (plugin == null) return;
            var info = plugin.ClutchBitePointInfo(plugin.CurrentGameName);
            bool running = plugin.IsBitePointCalibrationRunning;
            _refreshing = true;
            try
            {
                var source = plugin.BitePointSource;
                _source.SelectedIndex = (int)source;
                bool formula = source == MBoosterBitePointSource.Formula;
                _sourceFormula.Visibility = formula ? Visibility.Visible : Visibility.Collapsed;
                string formulaText = plugin.BitePointSourceFormula;
                _sourceFormulaText.Text = formulaText;
                _sourceFormulaText.Visibility = formula && formulaText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                _sourceValue.Text = plugin.TryGetBitePointClutchPosition(out double clutch01)
                    ? Math.Round(clutch01 * 100) + "%" : "—";

                _status.Text = plugin.BitePointStatusText(info);
                _calibrate.Content = running ? Strings.Button_Cancel : Strings.Button_Calibrate;
                _reset.IsEnabled = info.IsCalibrated || running;
                _auto.IsChecked = plugin.BitePointAutoCalibrate;
                _useCalibratedRow.Visibility = info.IsCalibrated ? Visibility.Visible : Visibility.Collapsed;
                _useCalibrated.SelectedIndex = _effect.UseCalibratedBitePoint ? 0 : 1;
                if (!_trigger.IsMouseCaptureWithin)
                    _model.Pct = UsesCalibration(plugin, info) ? info.Pct : _effect.TriggerLevelPct;
            }
            finally
            {
                _refreshing = false;
            }
        }

        private sealed class Model : INotifyPropertyChanged
        {
            private int _pct;

            public event PropertyChangedEventHandler? PropertyChanged;
            public event Action<int>? PctEdited;

            public int Pct
            {
                get => _pct;
                set
                {
                    value = Math.Max(0, Math.Min(100, value));
                    if (_pct == value) return;
                    _pct = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pct)));
                    PctEdited?.Invoke(value);
                }
            }
        }
    }
}
