using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MozaPlugin.UI;
using MozaPlugin.Resources;
using MozaPlugin.Settings;

namespace MozaPlugin.Devices.Ui
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

        // Per-LED swatch rows, rebuilt when the resolved strip length changes (it
        // is unknown until the base model name arrives) or when the active idle
        // mode switches to a different palette. Index matches the flat palette
        // index: strip * MaxLedsPerStrip + led.
        private readonly System.Collections.Generic.Dictionary<PaletteKind, Border[]> _perLedSwatches
            = new System.Collections.Generic.Dictionary<PaletteKind, Border[]>();
        private int _idleBuiltLeds = -1;
        private PaletteKind? _idleBuiltKind;
        private int _sleepBuiltLeds = -1;

        private enum PaletteKind { IdleConstant, IdleBreath, Sleep }

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

        private void OnRefreshTick(object? sender, EventArgs e)
        {
            try { Refresh(); }
            catch (Exception ex) { MozaLog.DebugIfChanged("ui-tick-base", $"[AZOM] Base page tick failed: {ex}"); }
        }

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
                BaseAmbientNotDetectedPanel.Visibility = Visibility.Visible;
                BasePanel.Visibility = Visibility.Collapsed;
                return;
            }

            // ResolvePlugin() returning true means both fields were just assigned;
            // flow analysis can't tie the bool to them, so snapshot into locals.
            var plugin = _plugin;
            var data = _data;
            if (plugin == null || data == null) return;

            // Detection-based (matches MozaBaseLedDeviceManager.IsConnected) so the
            // tab reflects detection independent of the lazily-injected LED driver.
            bool detected = plugin.IsBaseAmbientLedSupported;

            using (_suppressor.Begin())
            {
                BaseAmbientNotDetectedPanel.Visibility = detected ? Visibility.Collapsed : Visibility.Visible;
                BasePanel.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;

                if (detected)
                {
                    // 0 off, 1 SimHub mode, 2 on — the selector index IS the register value
                    // written to 0x1C.
                    SetComboSafe(IndicatorStateCombo, Clamp(data.BaseAmbientIndicatorState, 0, 2));
                    // Standby dropdown index == device mode: 0 off, 1 constant,
                    // 2 breathing, 3 color cycle, 4 rainbow, 5 sand flow.
                    SetComboSafe(StandbyModeCombo, Clamp(data.BaseAmbientStandbyMode, 0, 5));
                    SetComboSafe(SleepModeCombo, Clamp(data.BaseAmbientSleepMode, 0, 1));

                    BrightnessSlider.Value = Clamp(data.BaseAmbientBrightness, 0, 100);
                    BrightnessValue.Text = $"{(int)BrightnessSlider.Value}";
                    SleepTimeoutSlider.Value = Clamp(data.BaseAmbientSleepTimeout, 0, (int)SleepTimeoutSlider.Maximum);
                    SleepTimeoutValue.Text = $"{(int)SleepTimeoutSlider.Value}";


                    RefreshEffectRows();
                    RefreshPerLedSwatches();
                }
            }
        }

        // Show only what the selected effects actually have. Standby modes 0
        // (off) and 1 (constant) have no interval register, so the animation
        // speed row applies to 2..5 only; only modes 1 and 2 have a palette, so
        // the idle swatch row shows for those and carries that mode's colours.
        // The sleep speed row and palette apply only while the sleep effect is
        // breathing (mode 1).
        private void RefreshEffectRows()
        {
            if (_data == null) return;

            int standbyMode = _data.BaseAmbientStandbyMode;
            int ledsPerStrip = _data.ResolvedAmbientLedsPerStrip;

            bool standbyAnimated = standbyMode >= 2 && standbyMode <= 5;
            StandbySpeedRow.Visibility = standbyAnimated ? Visibility.Visible : Visibility.Collapsed;
            if (standbyAnimated)
            {
                int ms = _data.BaseAmbientStandbyIntervals[standbyMode];
                if (ms >= 0)
                {
                    StandbySpeedSlider.Value = Clamp(ms, (int)StandbySpeedSlider.Minimum, (int)StandbySpeedSlider.Maximum);
                    StandbySpeedValue.Text = $"{(int)StandbySpeedSlider.Value}";
                }
            }

            // The palette belongs to the active standby mode — one row, not one
            // per mode, so an edit always lands on what is being displayed.
            PaletteKind? idleKind =
                standbyMode == 1 ? PaletteKind.IdleConstant :
                standbyMode == 2 ? PaletteKind.IdleBreath :
                (PaletteKind?)null;
            BuildIdleLedRow(ledsPerStrip, idleKind);
            // The card itself always stays visible — it hosts the animation
            // selector, so collapsing it on a mode with no speed and no palette
            // (i.e. "off") would leave no way to pick a different mode.

            bool sleepBreathing = _data.BaseAmbientSleepMode == 1;
            SleepSpeedRow.Visibility = sleepBreathing ? Visibility.Visible : Visibility.Collapsed;
            if (sleepBreathing && _data.BaseAmbientSleepBreathInterval >= 0)
            {
                SleepSpeedSlider.Value = Clamp(_data.BaseAmbientSleepBreathInterval,
                    (int)SleepSpeedSlider.Minimum, (int)SleepSpeedSlider.Maximum);
                SleepSpeedValue.Text = $"{(int)SleepSpeedSlider.Value}";
            }

            BuildSleepLedRow(ledsPerStrip);
            SleepLedPanel.Visibility = sleepBreathing ? Visibility.Visible : Visibility.Collapsed;
        }

        // Writes the interval slot of the mode that is currently selected —
        // the register is per mode (`1E [mode]`), and the UI exposes one slider.
        private void StandbySpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int mode = _data.BaseAmbientStandbyMode;
            if (mode < 2 || mode > 5) return;

            int ms = (int)Math.Round(e.NewValue);
            StandbySpeedValue.Text = $"{ms}";
            _data.BaseAmbientStandbyIntervals[mode] = ms;
            _plugin.UpdateActiveProfile(p =>
            {
                p.BaseAmbientStandbyIntervals = EnsureIntArray(p.BaseAmbientStandbyIntervals, 6);
                p.BaseAmbientStandbyIntervals[mode] = ms;
            });
            _plugin.HardwareApplier.WriteIfBaseAmbientSupported(
                $"base-ambient-standby-interval-mode{mode}", ms);
            _plugin.SaveSettings();
        }

        private void SleepSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            int ms = (int)Math.Round(e.NewValue);
            SleepSpeedValue.Text = $"{ms}";
            _data.BaseAmbientSleepBreathInterval = ms;
            _plugin.UpdateActiveProfile(p => p.BaseAmbientSleepBreathInterval = ms);
            _plugin.HardwareApplier.WriteIfBaseAmbientSupported("base-ambient-sleep-breath-interval", ms);
            _plugin.SaveSettings();
        }

        // Idle palette row for the active standby mode, or nothing when the
        // active mode has no palette. Rebuilt only when the strip length or the
        // active palette changes, so the 500 ms refresh tick does not churn the
        // visual tree.
        private void BuildIdleLedRow(int ledsPerStrip, PaletteKind? kind)
        {
            if (_idleBuiltLeds == ledsPerStrip && _idleBuiltKind == kind) return;
            _idleBuiltLeds = ledsPerStrip;
            _idleBuiltKind = kind;

            IdleLedPanel.Children.Clear();
            _perLedSwatches.Remove(PaletteKind.IdleConstant);
            _perLedSwatches.Remove(PaletteKind.IdleBreath);
            if (!kind.HasValue) return;

            string label = kind == PaletteKind.IdleConstant
                ? Strings.Label_IdleConstant
                : Strings.Label_IdleBreathing;
            IdleLedPanel.Children.Add(BuildLedGrid(kind.Value, label, ledsPerStrip));
        }

        private void BuildSleepLedRow(int ledsPerStrip)
        {
            if (_sleepBuiltLeds == ledsPerStrip) return;
            _sleepBuiltLeds = ledsPerStrip;

            SleepLedPanel.Children.Clear();
            _perLedSwatches.Remove(PaletteKind.Sleep);
            SleepLedPanel.Children.Add(
                BuildLedGrid(PaletteKind.Sleep, Strings.Label_SleepBreathing, ledsPerStrip));
        }

        // One palette as a Grid: a header row numbering the LEDs the way the
        // vendor UI does (1..2N continuous across both strips) above a single
        // swatch row. A grid rather than stacked panels so each number sits over
        // its own swatch column by construction.
        private Grid BuildLedGrid(PaletteKind kind, string label, int ledsPerStrip)
        {
            int stride = BaseModelInfo.MaxLedsPerStrip;
            int total = ledsPerStrip * 2;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int c = 0; c < total; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int ui = 1; ui <= total; ui++)
            {
                var num = new TextBlock
                {
                    Text = ui.ToString(),
                    Width = 28,
                    TextAlignment = TextAlignment.Center,
                    Opacity = 0.6,
                    Margin = new Thickness(1, 0, 1, 2),
                };
                Grid.SetRow(num, 0);
                Grid.SetColumn(num, ui);
                grid.Children.Add(num);
            }

            var caption = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 12, 2),
                Style = TryFindResource("LabelText") as Style,
            };
            Grid.SetRow(caption, 1);
            Grid.SetColumn(caption, 0);
            grid.Children.Add(caption);

            var swatches = new Border[stride * 2];
            for (int strip = 0; strip < 2; strip++)
            {
                for (int led = 0; led < ledsPerStrip; led++)
                {
                    int index = strip * stride + led;
                    int column = 1 + strip * ledsPerStrip + led;
                    var swatch = new Border
                    {
                        Width = 28,
                        Height = 24,
                        Margin = new Thickness(1, 2, 1, 2),
                        BorderBrush = TryFindResource("BorderBrightBrush") as Brush,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Cursor = Cursors.Hand,
                        Background = Brushes.Black,
                        Tag = new PerLedTag(kind, strip, led, index),
                    };
                    swatch.MouseLeftButtonUp += PerLedSwatch_Click;
                    Grid.SetRow(swatch, 1);
                    Grid.SetColumn(swatch, column);
                    grid.Children.Add(swatch);
                    swatches[index] = swatch;
                }
            }

            _perLedSwatches[kind] = swatches;
            return grid;
        }

        private sealed class PerLedTag
        {
            public readonly PaletteKind Kind;
            public readonly int Strip;
            public readonly int Led;
            public readonly int Index;

            public PerLedTag(PaletteKind kind, int strip, int led, int index)
            {
                Kind = kind; Strip = strip; Led = led; Index = index;
            }
        }

        // Paint each swatch from the last value read back off the device.
        private void RefreshPerLedSwatches()
        {
            if (_data == null) return;
            foreach (var pair in _perLedSwatches)
            {
                var source = PaletteFor(pair.Key);
                var swatches = pair.Value;
                for (int i = 0; i < swatches.Length; i++)
                {
                    if (swatches[i] == null || i >= source.Length) continue;
                    UpdateSwatch(swatches[i], source[i]);
                }
            }
        }

        private byte[][] PaletteFor(PaletteKind kind)
        {
            switch (kind)
            {
                case PaletteKind.IdleConstant: return _data!.BaseAmbientIdleColorsConstant;
                case PaletteKind.IdleBreath:   return _data!.BaseAmbientIdleColorsBreath;
                default:                       return _data!.BaseAmbientSleepColors;
            }
        }

        // Wire command for one palette entry. Each command carries its own mode
        // byte, so a row can be edited whatever standby mode is active — only the
        // active mode's palette is visible on the hardware.
        private static string PerLedCommand(PerLedTag tag)
        {
            switch (tag.Kind)
            {
                case PaletteKind.IdleConstant:
                    return $"base-ambient-led-color-strip{tag.Strip}-mode1-led{tag.Led}";
                case PaletteKind.IdleBreath:
                    return $"base-ambient-led-color-strip{tag.Strip}-mode2-led{tag.Led}";
                default:
                    return $"base-ambient-sleep-led-color-strip{tag.Strip}-led{tag.Led}";
            }
        }

        private void PerLedSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            if (!((sender as Border)?.Tag is PerLedTag tag)) return;

            var palette = PaletteFor(tag.Kind);
            if (tag.Index < 0 || tag.Index >= palette.Length) return;

            ShowColorPicker(palette[tag.Index], PerLedCommand(tag),
                packed => PersistPerLed(tag, packed),
                (Border)sender);
        }

        // Persist one entry into the active profile's flat palette array,
        // allocating it on first use and defaulting untouched entries to -1
        // ("not set") so HardwareApplier leaves them alone.
        private void PersistPerLed(PerLedTag tag, int packed)
        {
            int length = BaseModelInfo.MaxLedsPerStrip * 2;
            _plugin!.UpdateActiveProfile(p =>
            {
                int[] target = EnsureIntArray(SelectProfilePalette(p, tag.Kind), length);
                target[tag.Index] = packed;
                AssignProfilePalette(p, tag.Kind, target);
            });
        }

        private static int[]? SelectProfilePalette(MozaProfile p, PaletteKind kind)
        {
            switch (kind)
            {
                case PaletteKind.IdleConstant: return p.BaseAmbientIdleColorsConstant;
                case PaletteKind.IdleBreath:   return p.BaseAmbientIdleColorsBreath;
                default:                       return p.BaseAmbientSleepColors;
            }
        }

        private static void AssignProfilePalette(MozaProfile p, PaletteKind kind, int[] value)
        {
            switch (kind)
            {
                case PaletteKind.IdleConstant: p.BaseAmbientIdleColorsConstant = value; break;
                case PaletteKind.IdleBreath:   p.BaseAmbientIdleColorsBreath = value; break;
                default:                       p.BaseAmbientSleepColors = value; break;
            }
        }

        private static int[] EnsureIntArray(int[]? existing, int length)
        {
            if (existing != null && existing.Length == length) return existing;
            var grown = new int[length];
            for (int i = 0; i < length; i++)
                grown[i] = existing != null && i < existing.Length ? existing[i] : -1;
            return grown;
        }

        private static void UpdateSwatch(Border swatch, byte[] rgb)
        {
            swatch.Background = new SolidColorBrush(Color.FromRgb(rgb[0], rgb[1], rgb[2]));
        }

        private static void SetComboSafe(Selector combo, int index)
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
            _plugin.HardwareApplier.WriteIfBaseAmbientSupported("base-ambient-indicator-state", val);
            _plugin.SaveSettings();
        }

        private void StandbyModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            // Index is the device mode value directly — see the load path.
            int deviceMode = StandbyModeCombo.SelectedIndex;
            if (deviceMode < 0) return;
            _data!.BaseAmbientStandbyMode = deviceMode;
            _plugin.UpdateActiveProfile(p => p.BaseAmbientStandbyMode = deviceMode);
            _plugin.HardwareApplier.WriteIfBaseAmbientSupported("base-ambient-standby-mode", deviceMode);
            _plugin.SaveSettings();
            // Show/hide the speed row for the newly selected mode without
            // waiting for the next refresh tick.
            using (_suppressor.Begin()) RefreshEffectRows();
        }

        private void SleepModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = SleepModeCombo.SelectedIndex;
            if (val < 0) return;
            _data!.BaseAmbientSleepMode = val;
            _plugin.UpdateActiveProfile(p => p.BaseAmbientSleepMode = val);
            _plugin.HardwareApplier.WriteIfBaseAmbientSupported("base-ambient-sleep-mode", val);
            _plugin.SaveSettings();
            using (_suppressor.Begin()) RefreshEffectRows();
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            BrightnessValue.Text = $"{val}";
            _data!.BaseAmbientBrightness = val;
            _plugin.UpdateActiveProfile(p => p.BaseAmbientBrightness = val);
            _plugin.HardwareApplier.WriteIfBaseAmbientSupported("base-ambient-brightness", val);
            _plugin.SaveSettings();
        }

        private void SleepTimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            SleepTimeoutValue.Text = $"{val}";
            _data!.BaseAmbientSleepTimeout = val;
            _plugin.UpdateActiveProfile(p => p.BaseAmbientSleepTimeout = val);
            _plugin.HardwareApplier.WriteIfBaseAmbientSupported("base-ambient-sleep-timeout", val);
            _plugin.SaveSettings();
        }

        private void ShowColorPicker(byte[] target, string command, Action<int> persistPacked, Border swatch)
        {
            if (_suppressEvents || _plugin == null) return;

            var dialog = new ColorPickerDialog(target[0], target[1], target[2]);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                byte r = dialog.SelectedR, g = dialog.SelectedG, b = dialog.SelectedB;
                _plugin.HardwareApplier.WriteColorIfBaseAmbientSupported(command, r, g, b);
                target[0] = r; target[1] = g; target[2] = b;
                swatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
                persistPacked((r << 16) | (g << 8) | b);
                _plugin.SaveSettings();
            }
        }
    }
}
