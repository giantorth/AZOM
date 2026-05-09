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
            // the pattern "wheel-knob{N}-bg-color" / "wheel-knob{N}-primary-color".
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
                var primary = CreateKnobSwatch($"wheel-knob{idx + 1}-primary-color", idx, _data.WheelKnobPrimaryColors, isBackground: false);
                var bg = CreateKnobSwatch($"wheel-knob{idx + 1}-bg-color", idx, _data.WheelKnobBackgroundColors, isBackground: true);
                row.Children.Add(WrapInCell(primary));
                row.Children.Add(WrapInCell(bg));
                WheelKnobPanel.Children.Add(row);
                _wheelKnobBgSwatches[idx] = bg;
                _wheelKnobPrimarySwatches[idx] = primary;
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
                _device.WriteColor($"wheel-group3-color{ledIdx + 1}", r, g, b);
                _data.KnobRingColors[ledIdx][0] = r;
                _data.KnobRingColors[ledIdx][1] = g;
                _data.KnobRingColors[ledIdx][2] = b;
            }
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
                            CommandPrefix = "wheel-group3-color",
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
            _device!.WriteSetting("wheel-group3-brightness", val);
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
                string cmdName = !string.IsNullOrEmpty(info.CommandNameOverride)
                    ? info.CommandNameOverride
                    : $"{info.CommandPrefix}{info.Index + 1}";
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
                WheelTypeText.Text = "";
                WheelFwText.Text = "";
                WheelNotDetectedPanel.Visibility = Visibility.Visible;
                NewWheelPanel.Visibility = Visibility.Collapsed;
                EsWheelPanel.Visibility = Visibility.Collapsed;
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

                WheelTypeText.Text = string.IsNullOrEmpty(modelName) ? "Detecting wheel..." : modelName;
                var fwParts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrEmpty(swVersion)) fwParts.Add($"FW: {swVersion}");
                if (!string.IsNullOrEmpty(hwVersion)) fwParts.Add($"HW: {hwVersion}");
                WheelFwText.Text = string.Join("  |  ", fwParts);
            }
            else
            {
                WheelTypeText.Text = "";
                WheelFwText.Text = "";
            }

            _suppressEvents = true;
            try
            {
                bool anyWheel = newWheel || oldWheel;
                WheelNotDetectedPanel.Visibility = anyWheel ? Visibility.Collapsed : Visibility.Visible;
                NewWheelPanel.Visibility = newWheel ? Visibility.Visible : Visibility.Collapsed;
                EsWheelPanel.Visibility = oldWheel ? Visibility.Visible : Visibility.Collapsed;
                bool showTelemetry = newWheel && (_plugin?.IsDisplayDetected ?? false);
                TelemetrySection.Visibility = showTelemetry ? Visibility.Visible : Visibility.Collapsed;

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
            _plugin.SaveSettings();
        }

        private void WheelButtonIdleEffectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelButtonIdleEffectCombo.SelectedIndex;
            _data!.WheelButtonsIdleEffect = val;
            _settings!.WheelButtonsIdleEffect = val;
            _device!.WriteSetting("wheel-buttons-idle-effect", val);
            _plugin.SaveSettings();
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

        private void TelemetryMappingsExpander_Expanded(object sender, RoutedEventArgs e)
            => PopulateChannelMappingGrid();

        private void TelemetryApplyMappings_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || TelemetryChannelGrid.ItemsSource is not IEnumerable<ChannelMappingRow> rows)
                return;

            foreach (var row in rows)
                _plugin.SetChannelMapping(row.Url, row.SimHubProperty);

            _plugin.RestartTelemetry();
            PopulateChannelMappingGrid();
            TelemetryMappingStatus.Text = $"Applied at {DateTime.Now:HH:mm:ss}";
        }

        private void TelemetryResetMappings_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.ClearCurrentDashboardMappings();
            _plugin.RestartTelemetry();
            PopulateChannelMappingGrid();
            TelemetryMappingStatus.Text = $"Reset to defaults at {DateTime.Now:HH:mm:ss}";
        }

        private void PopulateChannelMappingGrid()
        {
            if (_plugin == null) { TelemetryChannelGrid.ItemsSource = null; return; }
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
                    catalogRows.Add(new ChannelMappingRow
                    {
                        Name = url,
                        Url = url,
                        PackageLevel = 0,
                        Compression = "uint32_t",
                        SimHubProperty = "",
                    });
                    idx++;
                }
                TelemetryChannelGrid.ItemsSource = catalogRows;
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

                    rows.Add(new ChannelMappingRow
                    {
                        Name = ch.Name,
                        Url = ch.Url,
                        PackageLevel = ch.PackageLevel,
                        Compression = ch.Compression,
                        SimHubProperty = ch.SimHubProperty ?? "",
                    });
                }
            }
            TelemetryChannelGrid.ItemsSource = rows;
        }

        private sealed class ChannelMappingRow : INotifyPropertyChanged
        {
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
                    if (_simHubProperty == value) return;
                    _simHubProperty = value ?? "";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SimHubProperty)));
                }
            }

            public IReadOnlyList<string> KnownProperties => KnownSimHubProperties.Paths;

            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
