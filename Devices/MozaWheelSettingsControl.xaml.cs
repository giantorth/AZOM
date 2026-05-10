using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MozaPlugin.Telemetry;

namespace MozaPlugin.Devices
{
    public partial class MozaWheelSettingsControl : UserControl
    {
        private MozaPlugin? _plugin;
        private MozaDeviceManager? _device;
        private MozaData? _data;
        private MozaPluginSettings? _settings;
        private bool _suppressEvents;
        private bool _swatchesBuilt;

        private readonly DispatcherTimer _refreshTimer;

        // Debounce timer for the wheel-integrated display brightness slider.
        // Slider drags fire one ValueChanged per integer step; firing
        // SendDashDisplayBrightness on each fills the session-0x02 retransmit
        // queue with up to 100 chunks, every one of which retransmits for ~20s
        // until acked. On a wheel that doesn't ack (e.g. CSP-on-hub
        // engagement issue) the queue clogs the wire and stale values keep
        // firing — including any momentary pass through brightness=0 — so
        // the display stays blanked. We only commit (and persist) the
        // brightness once the slider has been still for the debounce
        // interval. _propertyPushLastSeqs in TelemetrySender additionally
        // supersedes any in-flight retransmit when a new value lands.
        private DispatcherTimer? _displayBrightnessDebounce;
        private static readonly TimeSpan DisplayBrightnessDebounce = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// The virtual LED driver for the device instance this control belongs to.
        /// When set, connection status is derived from the driver's model-aware IsConnected().
        /// When null (legacy), falls back to global plugin state.
        /// </summary>
        internal MozaLedDeviceManager? LinkedLedDriver { get; set; }

        // Color swatch references
        private readonly Border[] _wheelFlagColorSwatches = new Border[6];
        private readonly Border[] _wheelButtonColorSwatches = new Border[14];
        private readonly CheckBox[] _wheelButtonDefaultTelemetryChecks = new CheckBox[14];
        private readonly FrameworkElement[] _wheelButtonSlotContainers = new FrameworkElement[14];
        private const int WheelRpmSwatchMax = 25;
        private readonly Border[] _wheelRpmColorSwatches = new Border[WheelRpmSwatchMax];
        private readonly TextBlock[] _wheelRpmIndexLabels = new TextBlock[WheelRpmSwatchMax];
        private readonly Border[] _wheelKnobBgSwatches = new Border[MozaData.WheelKnobMax];
        private readonly Border[] _wheelKnobPrimarySwatches = new Border[MozaData.WheelKnobMax];
        private readonly FrameworkElement[] _wheelKnobRowContainers = new FrameworkElement[MozaData.WheelKnobMax];

        // Group 3 per-LED ring swatches
        private readonly Border[] _knobRingColorSwatches = new Border[MozaData.KnobRingLedMax];
        private readonly FrameworkElement[] _knobRingKnobContainers = new FrameworkElement[MozaData.WheelKnobMax];

        // ES wheel indicator: device 1=RPM, 2=Off, 3=On (1-based, -1 applied on read)
        // UI combo: 0="SimHub Mode", 1="Always On", 2="Off"
        private static readonly int[] EsIndicatorToDisplay = { 0, 2, 1 };
        private static readonly int[] EsIndicatorToStored = { 0, 2, 1 };

        public MozaWheelSettingsControl()
        {
            _suppressEvents = true;
            InitializeComponent();

            if (ResolvePlugin())
                BuildColorSwatches();

            _suppressEvents = false;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += OnRefreshTick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnRefreshTick(object? sender, EventArgs e) => RefreshWheel();

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();

            // Flush any pending brightness debounce so the user's latest
            // slider value reaches the wheel + persisted settings even if
            // they navigate away before the 1s debounce expires.
            if (_displayBrightnessDebounce != null && _displayBrightnessDebounce.IsEnabled)
                DisplayBrightnessDebounce_Tick(this, EventArgs.Empty);
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
            // Flag LEDs live on the Meter sub-device (RS21 DB); swatch writes route via dash-flag-color*.
            BuildSwatchRow(WheelFlagColorPanel, _wheelFlagColorSwatches, 6, "dash-flag-color", _data.WheelFlagColors);
            BuildButtonSwatchRow();
            BuildRpmSwatches();
            BuildKnobSwatchRows();
            BuildKnobRingSwatchRows();
            _swatchesBuilt = true;
        }

        private void BuildRpmSwatches()
        {
            if (_data == null) return;
            int count = Math.Min(WheelRpmSwatchMax, _data.WheelRpmColors.Length);
            var indexStyle = TryFindResource("LedIndex") as Style;
            for (int i = 0; i < count; i++)
            {
                var idxLabel = new TextBlock { Text = (i + 1).ToString(), Style = indexStyle };
                WheelRpmIndexPanel.Children.Add(idxLabel);
                _wheelRpmIndexLabels[i] = idxLabel;
            }
            BuildSwatchRow(WheelRpmColorPanel, _wheelRpmColorSwatches, count, "wheel-rpm-color", _data.WheelRpmColors);
        }

        private void BuildSwatchRow(StackPanel panel, Border[] swatches, int count,
            string commandPrefix, byte[][] colorSource)
        {
            for (int i = 0; i < count; i++)
            {
                var border = new Border
                {
                    Width = 28, Height = 28,
                    BorderBrush = GetCachedBrush(85, 85, 85),
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

        private void BuildButtonSwatchRow()
        {
            if (_data == null) return;
            const int count = 14;
            for (int i = 0; i < count; i++)
            {
                var col = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(2, 0, 2, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };

                var border = new Border
                {
                    Width = 28, Height = 28,
                    BorderBrush = GetCachedBrush(85, 85, 85),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Black,
                    Tag = new ColorSwatchInfo
                    {
                        CommandPrefix = "wheel-button-color",
                        Index = i,
                        ColorSource = _data.WheelButtonColors,
                    },
                };
                border.MouseLeftButtonUp += ColorSwatch_Click;
                col.Children.Add(border);
                _wheelButtonColorSwatches[i] = border;

                var cb = new CheckBox
                {
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    IsChecked = _data.WheelButtonDefaultDuringTelemetry[i],
                    ToolTip = "Default during telemetry: replace 'off' with this button's color whenever SimHub is sending telemetry.",
                    Tag = i,
                };
                cb.Checked += ButtonDefaultTelemetryCheck_Changed;
                cb.Unchecked += ButtonDefaultTelemetryCheck_Changed;
                col.Children.Add(cb);
                _wheelButtonDefaultTelemetryChecks[i] = cb;

                WheelButtonColorPanel.Children.Add(col);
                _wheelButtonSlotContainers[i] = col;
            }
        }

        private void ButtonDefaultTelemetryCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            var cb = (CheckBox)sender;
            int i = (int)cb.Tag;
            if (i < 0 || i >= _data.WheelButtonDefaultDuringTelemetry.Length) return;
            _data.WheelButtonDefaultDuringTelemetry[i] = cb.IsChecked == true;
            _plugin.SaveSettings();
        }

        private class ColorSwatchInfo
        {
            public string CommandPrefix = "";
            public int Index;
            public byte[][] ColorSource = Array.Empty<byte[]>();
            // When non-empty, used verbatim as the wheel command name instead of
            // "{CommandPrefix}{Index+1}". Used for knob colors whose commands follow
            // the pattern "wheel-knob{N}-active-color" (per-knob Active LED).
            public string CommandNameOverride = "";
            // Optional callback fired after a successful picker commit — lets the
            // caller repack the colour into a packed int[] on MozaPluginSettings
            // (knob colours are write-only on the wire, so settings is the only
            // persisted copy).
            public Action? OnChanged;
        }

        private void BuildKnobSwatchRows()
        {
            if (_data == null) return;
            int count = MozaData.WheelKnobMax;
            for (int i = 0; i < count; i++)
            {
                int idx = i;
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 4),
                };
                row.Children.Add(new TextBlock
                {
                    Text = $"Knob {idx + 1}",
                    Width = 70,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                // "Active" swatch — single per-knob LED color (cmd 0x27 ROLE=0).
                // The picked color is shown at whichever ring LED is the knob's
                // current rotation position.
                var active = CreateKnobSwatch($"wheel-knob{idx + 1}-active-color", idx, _data.WheelKnobPrimaryColors, isBackground: false);
                // "Inactive" swatch — per-knob bulk default. The picked color is
                // fanned out to all 12 ring LEDs (cmd 0x1F per-LED) via
                // BulkSetKnobRingColor. The empty CommandNameOverride suppresses
                // the per-click cmd 0x27 write — bulk handling is the only wire
                // activity for this swatch.
                var bg = CreateKnobSwatch("", idx, _data.WheelKnobBackgroundColors, isBackground: true);
                row.Children.Add(WrapInCell(active));
                row.Children.Add(WrapInCell(bg));
                WheelKnobPanel.Children.Add(row);
                _wheelKnobBgSwatches[idx] = bg;
                _wheelKnobPrimarySwatches[idx] = active;
                _wheelKnobRowContainers[idx] = row;
            }
        }

        private static FrameworkElement WrapInCell(Border swatch)
        {
            var cell = new Grid { Width = 60, HorizontalAlignment = HorizontalAlignment.Center };
            swatch.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(swatch);
            return cell;
        }

        private Border CreateKnobSwatch(string commandName, int idx, byte[][] colorSource, bool isBackground)
        {
            var border = new Border
            {
                Width = 28, Height = 28,
                BorderBrush = GetCachedBrush(85, 85, 85),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand,
                Background = Brushes.Black,
                Tag = new ColorSwatchInfo
                {
                    CommandNameOverride = commandName,
                    Index = idx,
                    ColorSource = colorSource,
                    OnChanged = () => PersistKnobColor(idx, isBackground),
                },
            };
            border.MouseLeftButtonUp += ColorSwatch_Click;
            return border;
        }

        private void PersistKnobColor(int idx, bool isBackground)
        {
            if (_data == null || _settings == null) return;
            // Write-only on the wire — settings is the canonical store. Repack the
            // full 3-element array each time so null -> default black is preserved.
            if (isBackground)
            {
                _settings.WheelKnobBackgroundColors = MozaProfile.PackColors(_data.WheelKnobBackgroundColors);
                // "Inactive" swatch bulk-sets all ring LEDs for this knob to the same color.
                // Writes to hardware + updates MozaData but does NOT persist ring colors —
                // individual ring swatch edits take priority on next save.
                BulkSetKnobRingColor(idx);
            }
            else
            {
                _settings.WheelKnobPrimaryColors = MozaProfile.PackColors(_data.WheelKnobPrimaryColors);
            }
        }

        private void BulkSetKnobRingColor(int knobIdx)
        {
            if (_data == null || _device == null || _plugin == null) return;
            var model = _plugin.WheelModelInfo;
            if (model?.KnobRingLeds == null || knobIdx >= model.KnobRingLeds.Length) return;
            if (!_plugin.IsWheelLedGroupPresent(3)) return;

            var color = _data.WheelKnobBackgroundColors[knobIdx];
            byte r = color[0], g = color[1], b = color[2];
            int startIdx = model.KnobRingStartIndex(knobIdx);
            int count = model.KnobRingLeds[knobIdx];

            for (int i = 0; i < count; i++)
            {
                int ledIdx = startIdx + i;
                _device.WriteColor($"wheel-knob-bg-color{ledIdx + 1}", r, g, b);
                _data.KnobRingColors[ledIdx][0] = r;
                _data.KnobRingColors[ledIdx][1] = g;
                _data.KnobRingColors[ledIdx][2] = b;
            }
            // Read each ring LED back so the per-LED swatches in the UI reflect
            // the wheel's actual state rather than only the colour we asked for.
            // The reads come back asynchronously; MozaData.UpdateFromArray writes
            // _data.KnobRingColors[], and the next RefreshWheel tick repaints the
            // swatches from that array. ReadSettingsPaced spaces the reads so the
            // wheel's RX queue doesn't flood.
            var readCmds = new string[count];
            for (int i = 0; i < count; i++)
                readCmds[i] = $"wheel-knob-bg-color{startIdx + i + 1}";
            _device.ReadSettingsPaced(readCmds);
            // Intentionally NOT calling PersistKnobRingColors() — bulk-set is a
            // convenience shortcut, individual ring edits take persistence priority.
        }

        private void BuildKnobRingSwatchRows()
        {
            if (_data == null) return;
            int knobMax = MozaData.WheelKnobMax;
            // Build with maximum possible LED count per knob (12); visibility is
            // controlled per-knob in RefreshWheel based on actual WheelModelInfo.
            for (int k = 0; k < knobMax; k++)
            {
                int knobIdx = k;
                var knobPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                knobPanel.Children.Add(new TextBlock
                {
                    Text = $"Knob {knobIdx + 1} Ring",
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 4),
                });

                // Index labels row
                var indexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
                for (int i = 0; i < 12; i++)
                {
                    indexRow.Children.Add(new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        Width = 28,
                        TextAlignment = TextAlignment.Center,
                        Foreground = Brushes.Gray,
                        FontSize = 10,
                        Margin = new Thickness(2, 0, 2, 0),
                    });
                }
                knobPanel.Children.Add(indexRow);

                // Swatch row — always build 12, hide extras in RefreshWheel
                var swatchRow = new StackPanel { Orientation = Orientation.Horizontal };
                for (int i = 0; i < 12; i++)
                {
                    // Placeholder LED index — corrected per-model in RefreshWheel
                    int ledIndex = knobIdx * 12 + i;
                    if (ledIndex >= MozaData.KnobRingLedMax) break;
                    var border = new Border
                    {
                        Width = 28, Height = 28,
                        BorderBrush = GetCachedBrush(85, 85, 85),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Margin = new Thickness(2, 0, 2, 0),
                        Cursor = Cursors.Hand,
                        Background = Brushes.Black,
                        Tag = new ColorSwatchInfo
                        {
                            CommandPrefix = "wheel-knob-bg-color",
                            Index = ledIndex,
                            ColorSource = _data.KnobRingColors,
                            OnChanged = () => PersistKnobRingColors(),
                        },
                    };
                    border.MouseLeftButtonUp += ColorSwatch_Click;
                    swatchRow.Children.Add(border);
                    if (ledIndex < MozaData.KnobRingLedMax)
                        _knobRingColorSwatches[ledIndex] = border;
                }
                knobPanel.Children.Add(swatchRow);

                WheelKnobRingPanel.Children.Add(knobPanel);
                _knobRingKnobContainers[knobIdx] = knobPanel;
            }
        }

        private void PersistKnobRingColors()
        {
            if (_data == null || _settings == null) return;
            _settings.WheelKnobRingColors = MozaProfile.PackColors(_data.KnobRingColors);
        }

        private void KnobRingBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            KnobRingBrightnessValue.Text = $"{val}";
            _data!.KnobRingBrightness = val;
            _device!.WriteSetting("wheel-knob-brightness", val);
            _settings!.WheelKnobRingBrightness = val;
            _plugin.SaveSettings();
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
                // Resolve cmd name: explicit override > prefix+index > empty
                // (suppress wire write — used by swatches whose hardware update
                // happens entirely in OnChanged, e.g. the per-knob "Inactive
                // bulk" swatch which fans out via BulkSetKnobRingColor).
                string cmdName;
                if (!string.IsNullOrEmpty(info.CommandNameOverride))
                    cmdName = info.CommandNameOverride;
                else if (!string.IsNullOrEmpty(info.CommandPrefix))
                    cmdName = $"{info.CommandPrefix}{info.Index + 1}";
                else
                    cmdName = "";
                if (!string.IsNullOrEmpty(cmdName))
                    _device!.WriteColor(cmdName, r, g, b);
                info.ColorSource[info.Index][0] = r;
                info.ColorSource[info.Index][1] = g;
                info.ColorSource[info.Index][2] = b;
                border.Background = GetCachedBrush(r, g, b);

                info.OnChanged?.Invoke();
                _plugin.SaveSettings();
            }
        }

        // ===== Refresh =====

        private void RefreshWheel()
        {
            if (!ResolvePlugin())
            {
                StatusDot.Fill = Brushes.Gray;
                StatusText.Text = "Plugin not loaded";
                WheelNotDetectedPanel.Visibility = Visibility.Visible;
                DashboardTab.Visibility = Visibility.Collapsed;
                RpmTab.Visibility = Visibility.Collapsed;
                ButtonsTab.Visibility = Visibility.Collapsed;
                KnobsTab.Visibility = Visibility.Collapsed;
                return;
            }

            if (!_swatchesBuilt)
                BuildColorSwatches();

            InitTelemetryUI();
            RefreshTelemetryStatus();

            // Use the linked LED driver's model-aware connection check when available
            bool wheelConnected = LinkedLedDriver?.IsConnected() ?? false;
            StatusDot.Fill = wheelConnected ? Brushes.LimeGreen : Brushes.Red;
            StatusText.Text = wheelConnected ? "Connected" : "Disconnected";

            bool isOldProtoDevice = LinkedLedDriver?.ExpectedModelPrefix == MozaDeviceConstants.OldProtocolMarker;
            bool oldWheel = wheelConnected && isOldProtoDevice && _plugin!.IsOldWheelDetected;
            bool newWheel = wheelConnected && !isOldProtoDevice && _plugin!.IsNewWheelDetected;

            if (wheelConnected)
            {
                string modelName = _data!.WheelModelName;
                string swVersion = _data.WheelSwVersion;
                string hwVersion = _data.WheelHwVersion;
            }

            _suppressEvents = true;
            try
            {
                bool anyWheel = newWheel || oldWheel;
                WheelNotDetectedPanel.Visibility = anyWheel ? Visibility.Collapsed : Visibility.Visible;

                var modelInfoForTabs = newWheel ? _plugin?.WheelModelInfo : null;
                bool showTelemetry = newWheel && (_plugin?.IsDisplayDetected ?? false);
                bool showButtonsTab = newWheel && (modelInfoForTabs?.ButtonLedCount ?? 0) > 0;
                bool showKnobsTab = newWheel && (modelInfoForTabs?.KnobCount ?? 0) > 0;

                DashboardTab.Visibility = showTelemetry ? Visibility.Visible : Visibility.Collapsed;
                RpmTab.Visibility = anyWheel ? Visibility.Visible : Visibility.Collapsed;
                ButtonsTab.Visibility = showButtonsTab ? Visibility.Visible : Visibility.Collapsed;
                KnobsTab.Visibility = showKnobsTab ? Visibility.Visible : Visibility.Collapsed;
                SleepTab.Visibility = newWheel ? Visibility.Visible : Visibility.Collapsed;

                RpmNewContent.Visibility = newWheel ? Visibility.Visible : Visibility.Collapsed;
                RpmEsContent.Visibility = oldWheel ? Visibility.Visible : Visibility.Collapsed;

                EnsureVisibleTabSelected();

                if (showTelemetry && _data != null)
                {
                    int b = _data.DashDisplayBrightness;
                    if (b < 0) b = 0; else if (b > 100) b = 100;
                    WheelDisplayBrightnessSlider.Value = b;
                    WheelDisplayBrightnessValue.Text = $"{b}";
                    SelectWheelDisplayStandbyByMinutes(_data.DashDisplayStandbyMin);
                }

                if (newWheel)
                {
                    SetComboSafe(WheelTelemetryModeCombo, _data!.WheelTelemetryMode);
                    SetComboSafe(WheelIdleEffectCombo, _data.WheelTelemetryIdleEffect);
                    SetComboSafe(WheelButtonIdleEffectCombo, _data.WheelButtonsIdleEffect);
                    SetComboSafe(WheelKnobIdleEffectCombo, _data.WheelKnobIdleEffect);
                    SetComboSafe(WheelKnobLedModeCombo, _data.WheelKnobLedMode);
                    SetComboSafe(WheelButtonLedModeCombo, _data.WheelButtonsLedMode);

                    // Idle-effect speed sliders. Read from settings (-1 = unset
                    // → default 1000ms). Hide rows when the parent combo is at
                    // Off / Constant.
                    int rpmSpeed = _settings!.WheelTelemetryIdleSpeedMs;
                    if (rpmSpeed < 0) rpmSpeed = 1000;
                    WheelIdleSpeedSlider.Value = System.Math.Max(WheelIdleSpeedSlider.Minimum, System.Math.Min(WheelIdleSpeedSlider.Maximum, rpmSpeed));
                    WheelIdleSpeedValue.Text = $"{rpmSpeed} ms";
                    int btnSpeed = _settings.WheelButtonsIdleSpeedMs;
                    if (btnSpeed < 0) btnSpeed = 1000;
                    WheelButtonIdleSpeedSlider.Value = System.Math.Max(WheelButtonIdleSpeedSlider.Minimum, System.Math.Min(WheelButtonIdleSpeedSlider.Maximum, btnSpeed));
                    WheelButtonIdleSpeedValue.Text = $"{btnSpeed} ms";
                    int knbSpeed = _settings.WheelKnobIdleSpeedMs;
                    if (knbSpeed < 0) knbSpeed = 1000;
                    WheelKnobIdleSpeedSlider.Value = System.Math.Max(WheelKnobIdleSpeedSlider.Minimum, System.Math.Min(WheelKnobIdleSpeedSlider.Maximum, knbSpeed));
                    WheelKnobIdleSpeedValue.Text = $"{knbSpeed} ms";
                    UpdateIdleSpeedRowVisibility();

                    // Sleep-light tab.
                    SetComboSafe(WheelSleepModeCombo, _data.WheelIdleMode);
                    SelectSleepTimeoutByMinutes(_data.WheelIdleTimeout);
                    int sleepSpeed = _settings.WheelSleepSpeedMs;
                    if (sleepSpeed < 0) sleepSpeed = _data.WheelIdleSpeed > 0 ? _data.WheelIdleSpeed : 1000;
                    WheelSleepSpeedSlider.Value = System.Math.Max(WheelSleepSpeedSlider.Minimum, System.Math.Min(WheelSleepSpeedSlider.Maximum, sleepSpeed));
                    WheelSleepSpeedValue.Text = $"{sleepSpeed} ms";
                    UpdateSleepSpeedRowVisibility();
                    WheelSleepColorSwatch.Background = GetCachedBrush(_data.WheelIdleColor[0], _data.WheelIdleColor[1], _data.WheelIdleColor[2]);

                    // Show/hide flag and button LED sections based on wheel model
                    var modelInfo = _plugin!.WheelModelInfo;

                    WheelFlagSection.Visibility = (modelInfo?.HasFlagLeds ?? false)
                        ? Visibility.Visible : Visibility.Collapsed;

                    for (int i = 0; i < 14; i++)
                    {
                        var vis = (modelInfo?.IsButtonActive(i) ?? true) ? Visibility.Visible : Visibility.Collapsed;
                        if (_wheelButtonSlotContainers[i] != null)
                            _wheelButtonSlotContainers[i].Visibility = vis;
                        else if (_wheelButtonColorSwatches[i] != null)
                            _wheelButtonColorSwatches[i].Visibility = vis;
                    }

                    int rpmCount = modelInfo?.RpmLedCount ?? 10;
                    for (int i = 0; i < WheelRpmSwatchMax; i++)
                    {
                        var vis = i < rpmCount ? Visibility.Visible : Visibility.Collapsed;
                        if (_wheelRpmColorSwatches[i] != null) _wheelRpmColorSwatches[i].Visibility = vis;
                        if (_wheelRpmIndexLabels[i] != null) _wheelRpmIndexLabels[i].Visibility = vis;
                    }

                    UpdateSwatches(_wheelFlagColorSwatches, _data.WheelFlagColors, 6);
                    UpdateSwatches(_wheelButtonColorSwatches, _data.WheelButtonColors, 14);
                    for (int i = 0; i < 14; i++)
                    {
                        var cb = _wheelButtonDefaultTelemetryChecks[i];
                        if (cb == null) continue;
                        bool want = _data.WheelButtonDefaultDuringTelemetry[i];
                        if ((cb.IsChecked == true) != want) cb.IsChecked = want;
                    }
                    UpdateSwatches(_wheelRpmColorSwatches, _data.WheelRpmColors, rpmCount);

                    int knobCount = modelInfo?.KnobCount ?? 0;
                    WheelKnobSection.Visibility = knobCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                    for (int i = 0; i < MozaData.WheelKnobMax; i++)
                    {
                        var vis = i < knobCount ? Visibility.Visible : Visibility.Collapsed;
                        if (_wheelKnobRowContainers[i] != null)
                            _wheelKnobRowContainers[i].Visibility = vis;
                    }
                    UpdateSwatches(_wheelKnobBgSwatches, _data.WheelKnobBackgroundColors, knobCount);
                    UpdateSwatches(_wheelKnobPrimarySwatches, _data.WheelKnobPrimaryColors, knobCount);

                    // Knob ring LEDs (Group 3)
                    var ringLeds = modelInfo?.KnobRingLeds;
                    bool showRings = knobCount > 0 && ringLeds != null && _plugin!.IsWheelLedGroupPresent(3);
                    WheelKnobRingSection.Visibility = showRings ? Visibility.Visible : Visibility.Collapsed;
                    if (showRings)
                    {
                        for (int k = 0; k < MozaData.WheelKnobMax; k++)
                        {
                            bool knobVisible = k < knobCount;
                            if (_knobRingKnobContainers[k] != null)
                                _knobRingKnobContainers[k].Visibility = knobVisible ? Visibility.Visible : Visibility.Collapsed;

                            if (knobVisible)
                            {
                                // Update swatch indices + visibility based on actual per-knob LED count
                                int startIdx = modelInfo!.KnobRingStartIndex(k);
                                int ledsThisKnob = ringLeds![k];
                                for (int i = 0; i < 12; i++)
                                {
                                    int swatchSlot = k * 12 + i;
                                    if (swatchSlot >= MozaData.KnobRingLedMax) break;
                                    var swatch = _knobRingColorSwatches[swatchSlot];
                                    if (swatch == null) continue;
                                    if (i < ledsThisKnob)
                                    {
                                        swatch.Visibility = Visibility.Visible;
                                        var info = (ColorSwatchInfo)swatch.Tag;
                                        info.Index = startIdx + i;
                                    }
                                    else
                                    {
                                        swatch.Visibility = Visibility.Collapsed;
                                    }
                                }
                            }
                        }
                        // Update swatch colors from data
                        for (int k = 0; k < knobCount; k++)
                        {
                            int startIdx = modelInfo!.KnobRingStartIndex(k);
                            int ledsThisKnob = ringLeds![k];
                            for (int i = 0; i < ledsThisKnob; i++)
                            {
                                int swatchSlot = k * 12 + i;
                                if (swatchSlot >= MozaData.KnobRingLedMax) break;
                                var swatch = _knobRingColorSwatches[swatchSlot];
                                if (swatch == null) continue;
                                var c = _data.KnobRingColors[startIdx + i];
                                swatch.Background = GetCachedBrush(c[0], c[1], c[2]);
                            }
                        }

                        if (_data.KnobRingBrightness >= 0 && !KnobRingBrightnessSlider.IsMouseCaptureWithin)
                        {
                            KnobRingBrightnessSlider.Value = _data.KnobRingBrightness;
                            KnobRingBrightnessValue.Text = $"{_data.KnobRingBrightness}";
                        }
                    }
                }

                if (oldWheel)
                {
                    int storedIndicator = _data!.WheelRpmIndicatorMode;
                    if (storedIndicator >= 0 && storedIndicator < EsIndicatorToDisplay.Length)
                        SetComboSafe(EsRpmIndicatorCombo, EsIndicatorToDisplay[storedIndicator]);
                    SetComboSafe(EsRpmDisplayCombo, _data.WheelRpmDisplayMode);
                }
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        // Pick the first visible tab whenever the current selection has been
        // collapsed (e.g. user was on Dashboard, then unplugs the wheel and
        // reconnects an ES wheel which only exposes RPM).
        private void EnsureVisibleTabSelected()
        {
            var selected = WheelTabs.SelectedItem as TabItem;
            if (selected != null && selected.Visibility == Visibility.Visible)
                return;

            foreach (var item in WheelTabs.Items)
            {
                if (item is TabItem ti && ti.Visibility == Visibility.Visible)
                {
                    WheelTabs.SelectedItem = ti;
                    return;
                }
            }

            // No visible tabs (no wheel detected) — clear selection so a stale
            // header doesn't stay highlighted.
            WheelTabs.SelectedIndex = -1;
        }

        // Cache SolidColorBrush instances keyed by packed RGB. The 500ms refresh
        // timer touches ~30 swatches per tick — without the cache that's 60
        // SolidColorBrush allocations/sec doing nothing useful since most colors
        // don't change between ticks.
        private static readonly System.Collections.Generic.Dictionary<int, SolidColorBrush> s_brushCache
            = new System.Collections.Generic.Dictionary<int, SolidColorBrush>();

        private static SolidColorBrush GetCachedBrush(byte r, byte g, byte b)
        {
            int key = (r << 16) | (g << 8) | b;
            if (s_brushCache.TryGetValue(key, out var brush)) return brush;
            brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            s_brushCache[key] = brush;
            return brush;
        }

        private static void UpdateSwatches(Border[] swatches, byte[][] colors, int count)
        {
            for (int i = 0; i < count && i < swatches.Length; i++)
            {
                if (swatches[i] == null) continue;
                var c = colors[i];
                swatches[i].Background = GetCachedBrush(c[0], c[1], c[2]);
            }
        }

        private static void SetComboSafe(ComboBox combo, int index)
        {
            if (index >= 0 && index < combo.Items.Count)
                combo.SelectedIndex = index;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ===== New wheel handlers =====

        private void WheelTelemetryModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelTelemetryModeCombo.SelectedIndex;
            _data!.WheelTelemetryMode = val;
            _settings!.WheelTelemetryMode = val;
            _device!.WriteSetting("wheel-telemetry-mode", val);
            _plugin.SaveSettings();
        }

        private void WheelIdleEffectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelIdleEffectCombo.SelectedIndex;
            _data!.WheelTelemetryIdleEffect = val;
            _settings!.WheelIdleEffect = val;
            _device!.WriteSetting("wheel-telemetry-idle-effect", val);
            UpdateIdleSpeedRowVisibility();
            _plugin.SaveSettings();
        }

        private void WheelButtonIdleEffectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelButtonIdleEffectCombo.SelectedIndex;
            _data!.WheelButtonsIdleEffect = val;
            _settings!.WheelButtonsIdleEffect = val;
            _device!.WriteSetting("wheel-buttons-idle-effect", val);
            UpdateIdleSpeedRowVisibility();
            _plugin.SaveSettings();
        }

        private void WheelKnobIdleEffectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelKnobIdleEffectCombo.SelectedIndex;
            _data!.WheelKnobIdleEffect = val;
            _settings!.WheelKnobIdleEffect = val;
            _device!.WriteSetting("wheel-knob-idle-effect", val);
            UpdateIdleSpeedRowVisibility();
            _plugin.SaveSettings();
        }

        // Per-effect idle speed (cmd 0x1E [group] [effect_id] [BE u16 ms]).
        // The slider value is paired with the currently-selected idle effect at
        // write time; the wire payload is `[effect_id, ms_msb, ms_lsb]`.
        private static byte[] BuildIdleSpeedPayload(int effectId, int ms)
        {
            ms = System.Math.Max(0, System.Math.Min(0xFFFF, ms));
            return new byte[] {
                (byte)(effectId & 0xFF),
                (byte)((ms >> 8) & 0xFF),
                (byte)(ms & 0xFF),
            };
        }

        private void WheelIdleSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int ms = (int)System.Math.Round(e.NewValue);
            WheelIdleSpeedValue.Text = $"{ms} ms";
            _data!.WheelTelemetryIdleSpeedMs = ms;
            _settings!.WheelTelemetryIdleSpeedMs = ms;
            int effect = _data.WheelTelemetryIdleEffect;
            if (effect >= 2)
                _device!.WriteArray("wheel-telemetry-idle-interval", BuildIdleSpeedPayload(effect, ms));
            _plugin.SaveSettings();
        }

        private void WheelButtonIdleSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int ms = (int)System.Math.Round(e.NewValue);
            WheelButtonIdleSpeedValue.Text = $"{ms} ms";
            _data!.WheelButtonsIdleSpeedMs = ms;
            _settings!.WheelButtonsIdleSpeedMs = ms;
            int effect = _data.WheelButtonsIdleEffect;
            if (effect >= 2)
                _device!.WriteArray("wheel-buttons-idle-interval", BuildIdleSpeedPayload(effect, ms));
            _plugin.SaveSettings();
        }

        private void WheelKnobIdleSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int ms = (int)System.Math.Round(e.NewValue);
            WheelKnobIdleSpeedValue.Text = $"{ms} ms";
            _data!.WheelKnobIdleSpeedMs = ms;
            _settings!.WheelKnobIdleSpeedMs = ms;
            int effect = _data.WheelKnobIdleEffect;
            if (effect >= 2)
                _device!.WriteArray("wheel-knob-idle-interval", BuildIdleSpeedPayload(effect, ms));
            _plugin.SaveSettings();
        }

        private void WheelKnobLedModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelKnobLedModeCombo.SelectedIndex;
            if (val < 0) return;
            _data!.WheelKnobLedMode = val;
            _settings!.WheelKnobLedMode = val;
            _device!.WriteSetting("wheel-knob-led-mode", val);
            _plugin.SaveSettings();
        }

        private void WheelButtonLedModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelButtonLedModeCombo.SelectedIndex;
            if (val < 0) return;
            _data!.WheelButtonsLedMode = val;
            _settings!.WheelButtonsLedMode = val;
            _device!.WriteSetting("wheel-buttons-led-mode", val);
            _plugin.SaveSettings();
        }

        // Sleep-light tab handlers.
        private void WheelSleepModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelSleepModeCombo.SelectedIndex;
            if (val < 0) return;
            _data!.WheelIdleMode = val;
            _settings!.WheelSleepMode = val;
            _device!.WriteSetting("wheel-idle-mode", val);
            UpdateSleepSpeedRowVisibility();
            _plugin.SaveSettings();
        }

        private void WheelSleepTimeoutCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var item = WheelSleepTimeoutCombo.SelectedItem as ComboBoxItem;
            if (item?.Tag == null) return;
            if (!int.TryParse(item.Tag.ToString(), out int minutes)) return;
            _data!.WheelIdleTimeout = minutes;
            _settings!.WheelSleepTimeoutMin = minutes;
            _device!.WriteSetting("wheel-idle-timeout", minutes);
            _plugin.SaveSettings();
        }

        private void WheelSleepSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int ms = (int)System.Math.Round(e.NewValue);
            WheelSleepSpeedValue.Text = $"{ms} ms";
            _data!.WheelIdleSpeed = ms;
            _settings!.WheelSleepSpeedMs = ms;
            int mode = _data.WheelIdleMode;
            if (mode >= 2)
                _device!.WriteArray("wheel-idle-speed", BuildIdleSpeedPayload(mode, ms));
            _plugin.SaveSettings();
        }

        private void WheelSleepColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            byte cR = _data.WheelIdleColor[0];
            byte cG = _data.WheelIdleColor[1];
            byte cB = _data.WheelIdleColor[2];
            var dialog = new ColorPickerDialog(cR, cG, cB);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() != true) return;
            byte r = dialog.SelectedR, g = dialog.SelectedG, b = dialog.SelectedB;
            _data.WheelIdleColor[0] = r;
            _data.WheelIdleColor[1] = g;
            _data.WheelIdleColor[2] = b;
            WheelSleepColorSwatch.Background = GetCachedBrush(r, g, b);
            _settings!.WheelSleepColor = new[] { (r << 16) | (g << 8) | b };
            _device!.WriteColor("wheel-idle-color", r, g, b);
            _plugin.SaveSettings();
        }

        // Visibility helpers — speed sliders only show when an animated effect is
        // selected (idx >= 2 means not Off/Constant).
        private void UpdateIdleSpeedRowVisibility()
        {
            if (_data == null) return;
            WheelIdleSpeedRow.Visibility = _data.WheelTelemetryIdleEffect >= 2
                ? Visibility.Visible : Visibility.Collapsed;
            WheelButtonIdleSpeedRow.Visibility = _data.WheelButtonsIdleEffect >= 2
                ? Visibility.Visible : Visibility.Collapsed;
            WheelKnobIdleSpeedRow.Visibility = _data.WheelKnobIdleEffect >= 2
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateSleepSpeedRowVisibility()
        {
            if (_data == null) return;
            WheelSleepSpeedRow.Visibility = _data.WheelIdleMode >= 2
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===== ES Wheel handlers =====

        private void EsRpmIndicatorCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int display = EsRpmIndicatorCombo.SelectedIndex;
            if (display < 0 || display >= EsIndicatorToStored.Length) return;
            int stored = EsIndicatorToStored[display];
            // ES wheel uses +1 expression: stored 0 -> raw 1, stored 1 -> raw 2, etc.
            int raw = stored + 1;
            _data!.WheelRpmIndicatorMode = stored;
            _settings!.WheelRpmIndicatorMode = stored;
            _device!.WriteSetting("wheel-rpm-indicator-mode", raw);
            _plugin.SaveSettings();
        }

        private void EsRpmDisplayCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = EsRpmDisplayCombo.SelectedIndex;
            _data!.WheelRpmDisplayMode = val;
            _settings!.WheelRpmDisplayMode = val;
            _device!.WriteSetting("wheel-set-rpm-display-mode", val);
            _plugin.SaveSettings();
        }

        // ===== Dashboard Telemetry =====

        private bool _telemetryUIInitialized;

        private void InitTelemetryUI()
        {
            if (_telemetryUIInitialized || _plugin == null) return;
            _telemetryUIInitialized = true;

            _suppressEvents = true;
            try
            {
                var s = _plugin.Settings;
                TelemetryEnabledCheck.IsChecked = s.TelemetryEnabled;

                PopulateDashboardCombo();
                UpdateTelemetryProfileInfo();
                UpdateFolderInfo();
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        /// <summary>
        /// Populate the dashboard dropdown from the wheel's reported dashboard
        /// list (session 0x09 configJson state push). Falls back to builtin
        /// profile names if the wheel hasn't pushed state yet.
        /// </summary>
        private void PopulateDashboardCombo()
        {
            if (_plugin == null) return;

            _suppressEvents = true;
            try
            {
                TelemetryProfileCombo.Items.Clear();

                var state = _plugin.WheelStateForDiagnostics;
                if (state != null && state.ConfigJsonList.Count > 0)
                {
                    // Wheel-reported dashboards in configJsonList order
                    // (alphabetical). Dropdown index = configJsonList slot
                    // used by SendDashboardSwitch.
                    foreach (var name in state.ConfigJsonList)
                        TelemetryProfileCombo.Items.Add(name);
                }
                else
                {
                    // Fallback: cached dashboard names (wheel state not available yet).
                    TelemetryProfileCombo.Items.Add("(none)");
                    if (_plugin.DashCache != null)
                    {
                        foreach (var name in _plugin.DashCache.CachedNames)
                            TelemetryProfileCombo.Items.Add(name);
                    }
                    if (!string.IsNullOrEmpty(_plugin.Settings.TelemetryMzdashPath))
                        TelemetryProfileCombo.Items.Add(
                            "[Custom: " + System.IO.Path.GetFileName(
                                _plugin.Settings.TelemetryMzdashPath) + "]");
                }

                // Restore selection by saved name.
                string selectedName = _plugin.Settings.TelemetryProfileName;
                if (!string.IsNullOrEmpty(selectedName))
                {
                    for (int i = 0; i < TelemetryProfileCombo.Items.Count; i++)
                    {
                        if (TelemetryProfileCombo.Items[i]?.ToString() == selectedName)
                        {
                            TelemetryProfileCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
                if (TelemetryProfileCombo.SelectedIndex < 0 && TelemetryProfileCombo.Items.Count > 0)
                    TelemetryProfileCombo.SelectedIndex = 0;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        /// <summary>
        /// Track whether the dropdown was last populated from wheel-reported
        /// dashboards vs fallback builtins, so we can re-populate when wheel
        /// state first arrives mid-session.
        /// </summary>
        private bool _dashComboFromWheelState;

        private void RefreshTelemetryStatus()
        {
            if (_plugin == null) return;

            // Re-populate dropdown once when wheel state first becomes available.
            var state = _plugin.WheelStateForDiagnostics;
            if (!_dashComboFromWheelState && state != null && state.ConfigJsonList.Count > 0)
            {
                _dashComboFromWheelState = true;
                PopulateDashboardCombo();
            }

            bool enabled = _plugin.Settings.TelemetryEnabled;
            // Both pipelines implement IMozaTelemetry.TestMode now.
            var active = _plugin.ActiveTelemetry;
            bool testMode = active?.TestMode ?? false;
            int framesSent = _plugin.FramesSentForDiagnostics;

            if (!enabled)
                TelemetryStatusLabel.Text = "Disabled";
            else if (testMode)
                TelemetryStatusLabel.Text = $"Test pattern — {framesSent} frames sent";
            else
                TelemetryStatusLabel.Text = $"Sending — {framesSent} frames sent";

            // Disable switch-side controls while a Stop+Start cycle's silence
            // gate is in effect — kind=4 frames sent during this window race
            // against the in-flight restart and have been observed to push
            // the wheel into a corrupted catalog state.
            bool inCooldown = active?.IsInSilenceCooldown ?? false;
            TelemetryTestStopBtn.IsEnabled = testMode && !inCooldown;
            TelemetryTestStartBtn.IsEnabled = active != null && !testMode && !inCooldown;
            TelemetryProfileCombo.IsEnabled = active != null && !inCooldown;

            // Refresh profile info — auto-renegotiate may have swapped
            // the profile on a background thread after a dashboard switch.
            UpdateTelemetryProfileInfo();
        }

        private void UpdateTelemetryProfileInfo()
        {
            if (_plugin == null) return;
            var profile = _plugin.ActiveTelemetry?.Profile;
            if (profile == null || profile.Tiers.Count == 0)
            {
                TelemetryProfileInfo.Text = "—";
                return;
            }
            var parts = new System.Collections.Generic.List<string>();
            foreach (var tier in profile.Tiers)
                parts.Add($"L{tier.PackageLevel}: {tier.Channels.Count}ch/{tier.TotalBytes}B");
            TelemetryProfileInfo.Text = string.Join("  ", parts);
        }

        private void TelemetryEnabledCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetTelemetryEnabled(TelemetryEnabledCheck.IsChecked == true);
            UpdateTelemetryProfileInfo();
        }

        private void WheelDisplayBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            WheelDisplayBrightnessValue.Text = $"{val}";
            _data!.DashDisplayBrightness = val;
            _settings!.DashDisplayBrightness = val;
            // Defer the wire write + persist until the slider has been still
            // for DisplayBrightnessDebounce. A drag fires ValueChanged per
            // integer step (~30 events for a full sweep); pushing each value
            // floods the session-0x02 retransmit queue with intermediate
            // chunks, including any momentary 0 the user passes through —
            // and that 0 keeps retransmitting alongside the final value
            // until the queue ages out, leaving the display blanked. The
            // timer resets on every ValueChanged so only the final
            // resting value is committed; 500 ms is long enough to skip a
            // fast drag, short enough to feel instant when the user
            // releases.
            if (_displayBrightnessDebounce == null)
            {
                _displayBrightnessDebounce = new DispatcherTimer { Interval = DisplayBrightnessDebounce };
                _displayBrightnessDebounce.Tick += DisplayBrightnessDebounce_Tick;
            }
            _displayBrightnessDebounce.Stop();
            _displayBrightnessDebounce.Start();
        }

        private void DisplayBrightnessDebounce_Tick(object? sender, EventArgs e)
        {
            _displayBrightnessDebounce?.Stop();
            if (_plugin == null || _data == null) return;
            int val = _data.DashDisplayBrightness;
            if (val < 0) val = 0; else if (val > 100) val = 100;
            // allowZero: true — this is the deliberate user-intent path.
            // Every other call site of SendDashDisplayBrightness (settings
            // load, profile apply, dash-extension apply) leaves the default
            // false so a stray 0 there is suppressed, but a slider dragged
            // to 0 and held is honoured (display turns off as the user
            // requested).
            _plugin.TelemetrySender?.SendDashDisplayBrightness(val, allowZero: true);
            _plugin.SaveSettings();
        }

        private void WheelDisplayStandbyCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var item = WheelDisplayStandbyCombo.SelectedItem as ComboBoxItem;
            if (item?.Tag is not string tagStr) return;
            if (!int.TryParse(tagStr, out int minutes)) return;
            _data!.DashDisplayStandbyMin = minutes;
            _settings!.DashDisplayStandbyMin = minutes;
            _plugin.TelemetrySender?.SendDashDisplayStandbyMinutes(minutes);
            _plugin.SaveSettings();
        }

        private void SelectWheelDisplayStandbyByMinutes(int minutes)
        {
            for (int i = 0; i < WheelDisplayStandbyCombo.Items.Count; i++)
            {
                if (WheelDisplayStandbyCombo.Items[i] is ComboBoxItem cbi
                    && cbi.Tag is string tag
                    && int.TryParse(tag, out int m)
                    && m == minutes)
                {
                    WheelDisplayStandbyCombo.SelectedIndex = i;
                    return;
                }
            }
            WheelDisplayStandbyCombo.SelectedIndex = -1;
        }

        private void SelectSleepTimeoutByMinutes(int minutes)
        {
            for (int i = 0; i < WheelSleepTimeoutCombo.Items.Count; i++)
            {
                if (WheelSleepTimeoutCombo.Items[i] is ComboBoxItem cbi
                    && cbi.Tag is string tag
                    && int.TryParse(tag, out int m)
                    && m == minutes)
                {
                    WheelSleepTimeoutCombo.SelectedIndex = i;
                    return;
                }
            }
            WheelSleepTimeoutCombo.SelectedIndex = -1;
        }

        private void TelemetryProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var selected = TelemetryProfileCombo.SelectedItem?.ToString();
            if (selected == null) return;

            int idx = TelemetryProfileCombo.SelectedIndex;
            var active = _plugin.ActiveTelemetry;
            var state = _plugin.WheelStateForDiagnostics;

            // Wheel-reported mode: dropdown is configJsonList-ordered.
            // Index IS the slot directly.
            if (active != null && state != null && state.ConfigJsonList.Count > 0
                && idx >= 0 && idx < state.ConfigJsonList.Count)
            {
                // Save the new profile name but DON'T apply it yet.
                // Profile change (ApplyTelemetrySettings) is deferred to
                // after the new tier-def is sent so value frames stay
                // coherent with the old tier-def during the catalog wait.
                // PitHouse trace: tier-def is sent AFTER FF-SWITCH, not before.
                _plugin.Settings.TelemetryProfileName = selected;
                _plugin.Settings.TelemetryMzdashPath = "";
                _plugin.SaveSettings();

                active.SendDashboardSwitch((uint)idx);
                _plugin.OnDashboardSwitched();

                UpdateTelemetryProfileInfo();
                if (TelemetryMappingsExpander.IsExpanded) PopulateChannelMappingGrid();
                return;
            }

            // Fallback: builtin-profile mode (no wheel state).
            if (selected == "(none)")
            {
                _plugin.Settings.TelemetryProfileName = "";
                _plugin.Settings.TelemetryMzdashPath = "";
                _plugin.SaveSettings();
                _plugin.OnActiveDashboardChanged();
                UpdateTelemetryProfileInfo();
                if (TelemetryMappingsExpander.IsExpanded) PopulateChannelMappingGrid();
                return;
            }
            if (!selected.StartsWith("[Custom:"))
            {
                _plugin.Settings.TelemetryProfileName = selected;
                _plugin.Settings.TelemetryMzdashPath = "";
                _plugin.SaveSettings();
                _plugin.OnActiveDashboardChanged();
                UpdateTelemetryProfileInfo();
                if (TelemetryMappingsExpander.IsExpanded) PopulateChannelMappingGrid();
            }
        }

        private void TelemetryClearMzdash_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.Settings.TelemetryProfileName = "";
            _plugin.Settings.TelemetryMzdashPath = "";
            _plugin.SaveSettings();
            _plugin.OnActiveDashboardChanged();

            _suppressEvents = true;
            // Drop any stale [Custom: ...] entry so the dropdown doesn't keep
            // showing a previously-loaded mzdash filename.
            for (int i = TelemetryProfileCombo.Items.Count - 1; i >= 0; i--)
                if (TelemetryProfileCombo.Items[i]?.ToString()?.StartsWith("[Custom:") == true)
                    TelemetryProfileCombo.Items.RemoveAt(i);
            // Select "(none)".
            for (int i = 0; i < TelemetryProfileCombo.Items.Count; i++)
            {
                if (TelemetryProfileCombo.Items[i]?.ToString() == "(none)")
                {
                    TelemetryProfileCombo.SelectedIndex = i;
                    break;
                }
            }
            _suppressEvents = false;

            UpdateTelemetryProfileInfo();
            if (TelemetryMappingsExpander.IsExpanded) PopulateChannelMappingGrid();
        }

        private void TelemetryLoadMzdash_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open .mzdash dashboard file",
                Filter = "MOZA Dashboard|*.mzdash|All Files|*.*",
                DefaultExt = ".mzdash"
            };
            if (dlg.ShowDialog() != true) return;

            _plugin.Settings.TelemetryMzdashPath = dlg.FileName;
            _plugin.Settings.TelemetryProfileName = "";
            _plugin.SaveSettings();
            // Hot-reload tier def on the existing session — mirrors PitHouse's
            // mid-game dash-change burst on session 0x01.
            _plugin.OnActiveDashboardChanged();

            _suppressEvents = true;
            string label = "[Custom: " + System.IO.Path.GetFileName(dlg.FileName) + "]";
            for (int i = TelemetryProfileCombo.Items.Count - 1; i >= 0; i--)
                if (TelemetryProfileCombo.Items[i]?.ToString()?.StartsWith("[Custom:") == true)
                    TelemetryProfileCombo.Items.RemoveAt(i);
            TelemetryProfileCombo.Items.Add(label);
            TelemetryProfileCombo.SelectedIndex = TelemetryProfileCombo.Items.Count - 1;
            _suppressEvents = false;

            UpdateTelemetryProfileInfo();
            if (TelemetryMappingsExpander.IsExpanded) PopulateChannelMappingGrid();
        }

        private void TelemetrySetFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Select folder containing .mzdash dashboard files";
                dlg.ShowNewFolderButton = false;
                if (!string.IsNullOrEmpty(_plugin.Settings.TelemetryMzdashFolder)
                    && System.IO.Directory.Exists(_plugin.Settings.TelemetryMzdashFolder))
                    dlg.SelectedPath = _plugin.Settings.TelemetryMzdashFolder;

                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                _plugin.Settings.TelemetryMzdashFolder = dlg.SelectedPath;
                _plugin.SaveSettings();
                _plugin.DashCache?.LoadFromFolder(dlg.SelectedPath);
                PopulateDashboardCombo();
                _plugin.ApplyTelemetrySettings();
                UpdateFolderInfo();
            }
        }

        private static string UidToHex(byte[] uid)
            => uid == null ? "" : BitConverter.ToString(uid).Replace("-", "").ToLowerInvariant();

        private void TelemetryAutoDetect_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;

            string dashesRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MOZA Pit House", "_dashes");

            if (!System.IO.Directory.Exists(dashesRoot))
            {
                MessageBox.Show(
                    $"MOZA Pit House dashboard folder not found at:\n{dashesRoot}\n\n" +
                    "Install MOZA Pit House and load a dashboard, or use Set Folder… to point at a custom location.",
                    "Auto-detect dashboard folder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            byte[] uid = _plugin.Data?.WheelMcuUid ?? Array.Empty<byte>();
            string uidHex = uid.Length == 12 ? UidToHex(uid) : "";

            string? picked = null;
            string? failReason = null;

            if (!string.IsNullOrEmpty(uidHex))
            {
                string candidate = System.IO.Path.Combine(dashesRoot, uidHex);
                if (System.IO.Directory.Exists(candidate))
                    picked = candidate;
                else
                    failReason = $"No dashboard folder for the connected wheel (UID {uidHex}).\n" +
                                 $"Looked for:\n{candidate}\n\n" +
                                 "Open a dashboard in MOZA Pit House for this wheel first.";
            }
            else
            {
                var guidDirs = System.IO.Directory.GetDirectories(dashesRoot)
                    .Where(p => System.Text.RegularExpressions.Regex.IsMatch(
                        new System.IO.DirectoryInfo(p).Name, "^[0-9a-fA-F]{24}$"))
                    .ToList();
                if (guidDirs.Count == 1)
                    picked = guidDirs[0];
                else if (guidDirs.Count == 0)
                    failReason = "No wheel-specific dashboard folders found under _dashes. " +
                                 "Open MOZA Pit House and load a dashboard, then try again.";
                else
                    failReason = $"Multiple dashboard folders found ({guidDirs.Count}) and no wheel is connected. " +
                                 "Connect your wheel and try again, or use Set Folder… to choose manually.";
            }

            if (picked == null)
            {
                MessageBox.Show(failReason, "Auto-detect dashboard folder",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _plugin.Settings.TelemetryMzdashFolder = picked;
            if (!string.IsNullOrEmpty(uidHex))
                _plugin.Settings.WheelMzdashFolderByUid[uidHex] = picked;
            _plugin.SaveSettings();
            _plugin.DashCache?.LoadFromFolder(picked);
            PopulateDashboardCombo();
            _plugin.ApplyTelemetrySettings();
            UpdateFolderInfo();
        }

        private void UpdateFolderInfo()
        {
            if (_plugin == null) return;
            var folder = _plugin.Settings.TelemetryMzdashFolder;
            TelemetryFolderInfo.Text = string.IsNullOrEmpty(folder) ? "" : $"Folder: {folder}";
        }

        private void TelemetryTestStart_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var active = _plugin.ActiveTelemetry;
            if (active == null) return;
            active.TestMode = true;
            if (!_plugin.Settings.TelemetryEnabled)
            {
                _plugin.ApplyTelemetrySettings();
                System.Threading.ThreadPool.QueueUserWorkItem(_ => active.Start());
            }
            TelemetryTestStartBtn.IsEnabled = false;
            TelemetryTestStopBtn.IsEnabled = true;
        }

        private void TelemetryTestStop_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var active = _plugin.ActiveTelemetry;
            if (active == null) return;
            active.TestMode = false;
            if (!_plugin.Settings.TelemetryEnabled)
                active.Stop();
            TelemetryTestStartBtn.IsEnabled = true;
            TelemetryTestStopBtn.IsEnabled = false;
        }

        // ===== Channel mappings =====

        // 2 Hz refresh of the "Current value" column while the expander is open.
        // Stopped on collapse so we don't pay the GetPropertyValue cost when the
        // user can't see the values.
        private DispatcherTimer? _mappingValueTimer;
        private static readonly TimeSpan MappingValueInterval = TimeSpan.FromMilliseconds(500);

        private void TelemetryMappingsExpander_Expanded(object sender, RoutedEventArgs e)
        {
            PopulateChannelMappingGrid();
            StartMappingValueTimer();
        }

        private void TelemetryMappingsExpander_Collapsed(object sender, RoutedEventArgs e)
        {
            StopMappingValueTimer();
        }

        private void StartMappingValueTimer()
        {
            if (_mappingValueTimer != null) return;
            _mappingValueTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = MappingValueInterval,
            };
            _mappingValueTimer.Tick += MappingValueTimer_Tick;
            _mappingValueTimer.Start();
            // Push values immediately so the user doesn't see "—" for the first 500ms.
            MappingValueTimer_Tick(this, EventArgs.Empty);
        }

        private void StopMappingValueTimer()
        {
            if (_mappingValueTimer == null) return;
            _mappingValueTimer.Stop();
            _mappingValueTimer.Tick -= MappingValueTimer_Tick;
            _mappingValueTimer = null;
        }

        private void MappingValueTimer_Tick(object? sender, EventArgs e)
        {
            if (_plugin == null) return;
            if (TelemetryChannelGrid.ItemsSource is not IEnumerable<ChannelMappingRow> rows) return;
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.SimHubProperty))
                {
                    row.CurrentValueText = "";
                    continue;
                }
                var raw = _plugin.GetPropertyValueForDisplay(row.SimHubProperty);
                row.CurrentValueText = FormatPropertyValue(raw);
            }
        }

        private static string FormatPropertyValue(object? value)
        {
            if (value == null) return "(null)";
            switch (value)
            {
                case bool b: return b ? "true" : "false";
                case double d: return (!double.IsNaN(d) && !double.IsInfinity(d)) ? d.ToString("0.###") : d.ToString();
                case float f: return (!float.IsNaN(f) && !float.IsInfinity(f)) ? f.ToString("0.###") : f.ToString();
                case int i: return i.ToString();
                case long l: return l.ToString();
                case uint ui: return ui.ToString();
                case ushort us: return us.ToString();
                case short sh: return sh.ToString();
                case byte by: return by.ToString();
                case TimeSpan ts: return ts.ToString(@"mm\:ss\.fff");
                case string s: return s.Length > 32 ? s.Substring(0, 32) + "…" : s;
                default:
                    var str = value.ToString() ?? "";
                    return str.Length > 32 ? str.Substring(0, 32) + "…" : str;
            }
        }

        // WPF's editable ComboBox SelectAlls its inner TextBox the moment the
        // dropdown opens. With auto-open driven by our filter that means every
        // 3rd keystroke selects the user's whole search query — and the next
        // keystroke replaces it. Reset the selection to caret-at-end on open so
        // the user keeps typing into their existing text. Deferred via Background
        // dispatcher so the override runs AFTER ComboBox's internal SelectAll.
        private void TelemetryPropCombo_DropDownOpened(object sender, EventArgs e)
        {
            if (sender is not ComboBox cb) return;
            cb.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (cb.Template?.FindName("PART_EditableTextBox", cb) is TextBox tb)
                {
                    int len = tb.Text?.Length ?? 0;
                    tb.SelectionStart = len;
                    tb.SelectionLength = 0;
                }
            }), DispatcherPriority.Background);
        }

        private void TelemetryResetMappings_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            // Restore each channel to its Telemetry.json default before clearing
            // the override dict — otherwise the live profile keeps the user's
            // last typed value until the next telemetry restart.
            if (TelemetryChannelGrid.ItemsSource is IEnumerable<ChannelMappingRow> rows)
            {
                foreach (var row in rows)
                    _plugin.UpdateActiveChannelMapping(row.Url, "");
            }
            _plugin.ClearCurrentDashboardMappings();
            PopulateChannelMappingGrid();
            TelemetryMappingStatus.Text = $"Reset to defaults at {DateTime.Now:HH:mm:ss}";
        }

        // Subscribed once per row by PopulateChannelMappingGrid. Auto-saves the
        // mapping (debounced 500ms inside SaveSettings) AND live-rewires the
        // active profile's channel — no telemetry restart needed because the
        // wire format (tier-def, channel indices, compression) is unchanged.
        private void OnMappingRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_plugin == null) return;
            if (e.PropertyName != nameof(ChannelMappingRow.SimHubProperty)) return;
            if (sender is not ChannelMappingRow row) return;
            if (string.IsNullOrEmpty(row.Url)) return;
            _plugin.SetChannelMapping(row.Url, row.SimHubProperty);
            TelemetryMappingStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
        }

        private void PopulateChannelMappingGrid()
        {
            if (_plugin == null) { TelemetryChannelGrid.ItemsSource = null; return; }

            // Unsubscribe from prior rows so stale rows can be GC'd and we don't
            // double-fire OnMappingRowPropertyChanged when the same plugin
            // instance re-populates the grid (dashboard switch, Reset).
            if (TelemetryChannelGrid.ItemsSource is IEnumerable<ChannelMappingRow> oldRows)
            {
                foreach (var r in oldRows) r.PropertyChanged -= OnMappingRowPropertyChanged;
            }

            // Snapshot the SimHub property name list once per populate so all rows
            // share the same backing list (avoids N copies of a 500-entry list).
            var props = _plugin.GetAllSimHubPropertyNames();

            var profile = _plugin.ActiveTelemetry?.Profile;
            if (profile == null || profile.Tiers.Count == 0)
            {
                // No mzdash loaded: fall back to whatever channel URLs the
                // wheel has advertised in its catalog. Lets the user see /
                // edit mappings even without uploading a profile.
                var catalog = _plugin.WheelChannelCatalogForDiagnostics;
                if (catalog == null || catalog.Count == 0)
                {
                    TelemetryChannelGrid.ItemsSource = null;
                    TelemetryMappingStatus.Text =
                        "(no dashboard loaded and wheel has not advertised a channel catalog)";
                    return;
                }
                var catalogRows = new List<ChannelMappingRow>();
                int idx = 1;
                foreach (var url in catalog.OrderBy(u => u, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(url)) { idx++; continue; }
                    // AllProperties MUST be set before SimHubProperty so the
                    // setter's filter step sees the full list. Object initializers
                    // assign in source order, so list AllProperties first.
                    catalogRows.Add(new ChannelMappingRow
                    {
                        AllProperties = props,
                        Name = url,
                        Url = url,
                        PackageLevel = 0,
                        Compression = "uint32_t",
                        SimHubProperty = "",
                    });
                    idx++;
                }
                TelemetryChannelGrid.ItemsSource = catalogRows;
                // Now that initial-state filter passes have run with dropdown
                // auto-open suppressed, allow further user-typed input to open
                // the dropdown. Also subscribe for auto-save on every edit.
                foreach (var r in catalogRows)
                {
                    r.AllowDropdownOpen = true;
                    r.PropertyChanged += OnMappingRowPropertyChanged;
                }
                TelemetryMappingStatus.Text =
                    $"(no dashboard loaded — showing {catalogRows.Count} wheel-advertised channels)";
                return;
            }

            // Per-widget tier-def emits one tier per dashboard widget, so a
            // dashboard with 12 widgets binding 6 unique URLs surfaces 12
            // tier×channel pairs. The mapping grid is keyed by URL → SimHub
            // property, so collapse duplicates by URL (first occurrence wins).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<ChannelMappingRow>();
            foreach (var tier in profile.Tiers.OrderBy(t => t.PackageLevel))
            {
                foreach (var ch in tier.Channels.OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase))
                {
                    if (DashboardProfileStore.IsInternalChannel(ch.SimHubProperty)) continue;
                    if (!seen.Add(ch.Url)) continue;

                    // AllProperties MUST be set before SimHubProperty so the
                    // setter's filter step sees the full list. Object initializers
                    // assign in source order, so list AllProperties first.
                    rows.Add(new ChannelMappingRow
                    {
                        AllProperties = props,
                        Name = ch.Name,
                        Url = ch.Url,
                        PackageLevel = ch.PackageLevel,
                        Compression = ch.Compression,
                        SimHubProperty = ch.SimHubProperty ?? "",
                    });
                }
            }
            TelemetryChannelGrid.ItemsSource = rows;
            // Now that initial-state filter passes have run with dropdown auto-open
            // suppressed, allow further user-typed input to open the dropdown.
            // Also subscribe for auto-save on every edit.
            foreach (var r in rows)
            {
                r.AllowDropdownOpen = true;
                r.PropertyChanged += OnMappingRowPropertyChanged;
            }
        }

        private sealed class ChannelMappingRow : INotifyPropertyChanged
        {
            // Min characters before the filter activates. Below this we keep the
            // dropdown empty (and the help text reminds the user to type more) —
            // SimHub's full property list can be 1500+ entries, so filtering on
            // 1-2 chars renders too many items for the ComboBox to virtualize
            // smoothly.
            private const int MinSearchChars = 3;
            // Cap filtered results — protects the dropdown from a substring like
            // "data" matching half the universe. The user narrows further by
            // adding chars; if 200 isn't enough they can paste the full path.
            private const int MaxFilteredResults = 200;

            public string Name { get; set; } = "";
            public string Url { get; set; } = "";
            public int PackageLevel { get; set; }
            public string Compression { get; set; } = "";

            private string _simHubProperty = "";
            public string SimHubProperty
            {
                get => _simHubProperty;
                set
                {
                    var v = (value ?? "").Trim();
                    if (_simHubProperty == v) return;
                    _simHubProperty = v;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SimHubProperty)));
                    // Clear the live value so the next refresh repopulates from
                    // the new path, and the user doesn't see a stale value
                    // matched against an unrelated property name.
                    CurrentValueText = "";
                    UpdateFilteredProperties();
                }
            }

            /// <summary>
            /// Master snapshot of every SimHub property name (set once by the populator
            /// from <see cref="MozaPlugin.GetAllSimHubPropertyNames"/>). The ComboBox
            /// dropdown does NOT bind to this directly — it would render thousands
            /// of rows on every open. Instead we filter into <see cref="FilteredProperties"/>
            /// on each keystroke.
            /// </summary>
            public IReadOnlyList<string> AllProperties { get; set; } = KnownSimHubProperties.Paths;

            private IReadOnlyList<string> _filteredProperties = Array.Empty<string>();
            /// <summary>
            /// Live, filtered subset of <see cref="AllProperties"/>. Bound to the
            /// ComboBox.ItemsSource. Replaced wholesale (immutable-list swap) on each
            /// keystroke so the ComboBox doesn't see a Clear+Add cycle while the user
            /// is mid-click on a dropdown item — that race lost the SelectedItem
            /// reference and made selections silently fail. Populated by
            /// <see cref="UpdateFilteredProperties"/>: empty until the user types
            /// <see cref="MinSearchChars"/>+ characters, then case-insensitive
            /// substring match against AllProperties (capped at
            /// <see cref="MaxFilteredResults"/>).
            /// </summary>
            public IReadOnlyList<string> FilteredProperties
            {
                get => _filteredProperties;
                private set
                {
                    _filteredProperties = value ?? Array.Empty<string>();
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredProperties)));
                }
            }

            private bool _isDropDownOpen;
            /// <summary>
            /// TwoWay-bound to ComboBox.IsDropDownOpen. Auto-opened by the filter
            /// step on user input when there are matches; closed when the typed
            /// text is an exact property name (the user picked / typed in full).
            /// </summary>
            public bool IsDropDownOpen
            {
                get => _isDropDownOpen;
                set
                {
                    if (_isDropDownOpen == value) return;
                    _isDropDownOpen = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropDownOpen)));
                }
            }

            /// <summary>
            /// Gate that suppresses auto-open during the initial populate (when
            /// SimHubProperty is set to an existing override and we don't want
            /// the dropdown to pop open on every row). Set true by the populator
            /// after the row is wired into the grid.
            /// </summary>
            public bool AllowDropdownOpen { get; set; }

            private string _currentValueText = "";
            public string CurrentValueText
            {
                get => _currentValueText;
                set
                {
                    if (_currentValueText == value) return;
                    _currentValueText = value ?? "";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentValueText)));
                }
            }

            private void UpdateFilteredProperties()
            {
                string query = _simHubProperty;
                if (string.IsNullOrEmpty(query) || query.Length < MinSearchChars)
                {
                    FilteredProperties = Array.Empty<string>();
                    if (AllowDropdownOpen) IsDropDownOpen = false;
                    return;
                }

                // Build a fresh list off-bind, then swap. Atomic from the binding's
                // perspective — the previous list reference is still alive while
                // any in-flight click on a dropdown item finishes committing.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var list = new List<string>(MaxFilteredResults);
                bool exactMatchSeen = false;
                var src = AllProperties;
                if (src != null)
                {
                    for (int i = 0; i < src.Count; i++)
                    {
                        var p = src[i];
                        if (string.IsNullOrEmpty(p)) continue;
                        if (p.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!seen.Add(p)) continue; // dedupe defence-in-depth (source is already unique)
                        list.Add(p);
                        if (string.Equals(p, query, StringComparison.OrdinalIgnoreCase))
                            exactMatchSeen = true;
                        if (list.Count >= MaxFilteredResults) break;
                    }
                }

                FilteredProperties = list;

                // If the typed text is exactly a property name (user picked from
                // the dropdown or typed the full path), close — no point keeping
                // the dropdown open. Otherwise auto-open while filter has hits so
                // the user can see the narrowing list as they type.
                if (!AllowDropdownOpen) return;
                IsDropDownOpen = !exactMatchSeen && list.Count > 0;
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
