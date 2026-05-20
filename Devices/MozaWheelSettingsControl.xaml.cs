using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MozaControls;
using MozaPlugin.Devices.WheelUi;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.UI;
using static MozaPlugin.UI.UiHelpers;
using static MozaPlugin.Devices.WheelUi.WheelUiHelpers;

namespace MozaPlugin.Devices
{
    public partial class MozaWheelSettingsControl : UserControl
    {
        private MozaPlugin? _plugin;
        private MozaDeviceManager? _device;
        private MozaData? _data;
        private MozaPluginSettings? _settings;
        private readonly EventSuppressor _suppressor = new EventSuppressor();
        private bool _suppressEvents => _suppressor.Suppressed;
        private bool _swatchesBuilt;

        // Plugin instance we've attached DashboardSelectionChanged to. Tracked
        // separately from _plugin so that re-resolving (after a plugin reload
        // while the control is reused, or when ResolvePlugin first sees a
        // non-null Instance) re-subscribes to the new instance and detaches
        // the old subscription.
        private MozaPlugin? _dashEventSubscribedPlugin;

        private readonly DispatcherTimer _refreshTimer;

        // Debounce: commit display brightness only after the slider settles,
        // so rapid drags don't flood the sess=0x02 retransmit queue.
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

        public MozaWheelSettingsControl()
        {
            using (_suppressor.Begin())
            {
                InitializeComponent();

                if (ResolvePlugin())
                    BuildColorSwatches();
            }

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += OnRefreshTick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnRefreshTick(object? sender, EventArgs e) => RefreshWheel();

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // ResolvePlugin (called from OnRefreshTick every 500ms) is where
            // DashboardSelectionChanged gets subscribed; do an initial attempt
            // here too so the first tick of the new control isn't responsible
            // for catching a same-instant profile-apply event.
            ResolvePlugin();
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
            Instance = this;
            EnsureInputsLiveTimer();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            StopInputsLiveTimer();
            _refreshTimer.Stop();

            if (_dashEventSubscribedPlugin != null)
            {
                try { _dashEventSubscribedPlugin.DashboardSelectionChanged -= OnPluginDashboardSelectionChanged; }
                catch { }
                _dashEventSubscribedPlugin = null;
            }

            // Flush any pending brightness debounce so the user's latest
            // slider value reaches the wheel + persisted settings even if
            // they navigate away before the 1s debounce expires.
            if (_displayBrightnessDebounce != null && _displayBrightnessDebounce.IsEnabled)
                DisplayBrightnessDebounce_Tick(this, EventArgs.Empty);
        }

        private void OnPluginDashboardSelectionChanged(object? sender, EventArgs e)
        {
            // Profile load runs on SimHub's profile-event thread, not the UI thread.
            // Marshal to the dispatcher before touching ComboBox.Items / SelectedIndex.
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnPluginDashboardSelectionChanged(sender, e)));
                return;
            }

            if (_plugin == null)
            {
                MozaLog.Debug("[Moza] UI: DashboardSelectionChanged handler — _plugin null, skipping");
                return;
            }

            // Always re-populate: event can arrive long after the first RefreshWheel tick.
            // Wheel-initiated dashboard switches also fire this event (via
            // DashboardBindingCoordinator.RaiseDashboardSelectionChangedInternal),
            // so refreshing the channel-mapping grid here covers both UI- and
            // wheel-initiated switches.
            MozaLog.Debug(
                $"[Moza] UI: DashboardSelectionChanged handler — selected='{_plugin.ActiveTelemetryProfileName}'");
            PopulateDashboardCombo();
            PopulateChannelMappingGrid();
        }

        private bool ResolvePlugin()
        {
            _plugin = MozaPlugin.Instance;
            if (_plugin == null) return false;
            _device = _plugin.DeviceManager;
            _data = _plugin.Data;
            _settings = _plugin.Settings;

            // Re-subscribe to dashboard events when plugin instance changes (Refresh tick self-heals on reload).
            if (!ReferenceEquals(_dashEventSubscribedPlugin, _plugin))
            {
                if (_dashEventSubscribedPlugin != null)
                {
                    try { _dashEventSubscribedPlugin.DashboardSelectionChanged -= OnPluginDashboardSelectionChanged; }
                    catch { }
                }
                _plugin.DashboardSelectionChanged += OnPluginDashboardSelectionChanged;
                _dashEventSubscribedPlugin = _plugin;
                MozaLog.Debug(
                    $"[Moza] UI: subscribed to DashboardSelectionChanged (plugin hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_plugin)})");
            }

            return true;
        }

        // ===== Color swatches =====

        private void BuildColorSwatches()
        {
            if (_swatchesBuilt || _data == null) return;
            // Flag LEDs live on the Meter sub-device (RS21 DB); swatch writes route via dash-flag-color*.
            BuildSwatchRow(WheelFlagColorPanel, _wheelFlagColorSwatches, 6, "dash-flag-color", _data.WheelFlagColors, SwatchSection.Flag);
            BuildButtonSwatchRow();
            BuildRpmSwatches();
            InitInlineColorEditors();
            BuildKnobSwatchRows();        // legacy hidden swatches — kept for refresh-path compat
            BuildKnobRingSwatchRows();    // legacy hidden swatches — kept for refresh-path compat
            // KnobRingViz panels for the redesigned Knobs tab. Wrapped: any
            // failure here must not take the whole device control down (the
            // legacy hidden swatch path above still updates _data).
            try { BuildKnobRingVizPanels(); }
            catch (Exception ex) { MozaLog.Warn($"[Moza] BuildKnobRingVizPanels failed: {ex.Message}"); }
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
            BuildSwatchRow(WheelRpmColorPanel, _wheelRpmColorSwatches, count, "wheel-rpm-color", _data.WheelRpmColors, SwatchSection.Rpm);
        }

        // Overlay-wins helper: unpack the persisted overlay's packed int[] array
        // into a fresh byte[][] when present; fall back to _data's wheel-mirrored
        // bytes otherwise. Slots past the overlay's length read from _data so a
        // short overlay (e.g. legacy 10-entry array) doesn't blank out the
        // higher slots. Used to render LED swatches without letting the wheel's
        // transient response state mask the user's saved choice.
        private static byte[][] MergeOverlayIntoData(int[]? packedOverlay, byte[][] data)
        {
            var result = new byte[data.Length][];
            for (int i = 0; i < data.Length; i++)
            {
                if (packedOverlay != null && i < packedOverlay.Length)
                {
                    var rgb = MozaProfile.UnpackColor(packedOverlay[i]);
                    result[i] = new[] { rgb[0], rgb[1], rgb[2] };
                }
                else
                {
                    result[i] = data[i];
                }
            }
            return result;
        }

        private void BuildSwatchRow(StackPanel panel, Border[] swatches, int count,
            string commandPrefix, byte[][] colorSource, SwatchSection section = SwatchSection.None)
        {
            // Inline-editor swatches (RPM/Flag/Button) render as circles per the
            // redesign; legacy (no section) stay rounded squares.
            double radius = section == SwatchSection.None ? 3 : 14;
            for (int i = 0; i < count; i++)
            {
                var border = new Border
                {
                    Width = 28, Height = 28,
                    BorderBrush = GetCachedBrush(85, 85, 85),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(radius),
                    Margin = new Thickness(2, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Black,
                    Tag = new ColorSwatchInfo
                    {
                        CommandPrefix = commandPrefix,
                        Index = i,
                        ColorSource = colorSource,
                        Section = section,
                    },
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
                    CornerRadius = new CornerRadius(14),  // circle
                    Cursor = Cursors.Hand,
                    Background = Brushes.Black,
                    Tag = new ColorSwatchInfo
                    {
                        CommandPrefix = "wheel-button-color",
                        Index = i,
                        ColorSource = _data.WheelButtonColors,
                        Section = SwatchSection.Button,
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

        // Routes a clicked swatch to the matching inline editor (PaletteStrip +
        // label panel) instead of opening a modal ColorPickerDialog.
        private enum SwatchSection { None, Rpm, Flag, Button }

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
            // Identifies which inline editor panel owns this swatch.
            public SwatchSection Section = SwatchSection.None;
        }

        // Track the currently-selected swatch per inline editor so we can
        // (a) write palette picks into it and (b) restore the prior swatch's
        // default border when a different one is clicked.
        private Border? _rpmSelectedSwatch;
        private Border? _flagSelectedSwatch;
        private Border? _buttonSelectedSwatch;
        private bool _inlineEditorsBuilt;

        private void InitInlineColorEditors()
        {
            if (_inlineEditorsBuilt) return;
            _inlineEditorsBuilt = true;

            // CUSTOM chip on every PaletteStrip pops the legacy hue dialog as a
            // fallback. Set once globally (PaletteStrip.CustomPickerFactory is
            // static) so all palettes share it.
            if (PaletteStrip.CustomPickerFactory == null)
            {
                PaletteStrip.CustomPickerFactory = (current) =>
                {
                    var dlg = new ColorPickerDialog(current.R, current.G, current.B);
                    dlg.Owner = Window.GetWindow(this);
                    if (dlg.ShowDialog() != true) return null;
                    return Color.FromRgb(dlg.SelectedR, dlg.SelectedG, dlg.SelectedB);
                };
            }

            WiRpmPalette.ColorChanged    += (_, c) => OnInlinePaletteCommit(SwatchSection.Rpm, c);
            WiFlagPalette.ColorChanged   += (_, c) => OnInlinePaletteCommit(SwatchSection.Flag, c);
            WiButtonPalette.ColorChanged += (_, c) => OnInlinePaletteCommit(SwatchSection.Button, c);
        }

        private Border? GetSelectedSwatch(SwatchSection s) => s switch
        {
            SwatchSection.Rpm    => _rpmSelectedSwatch,
            SwatchSection.Flag   => _flagSelectedSwatch,
            SwatchSection.Button => _buttonSelectedSwatch,
            _ => null,
        };

        private void SetSelectedSwatch(SwatchSection s, Border? b)
        {
            switch (s)
            {
                case SwatchSection.Rpm:    _rpmSelectedSwatch = b; break;
                case SwatchSection.Flag:   _flagSelectedSwatch = b; break;
                case SwatchSection.Button: _buttonSelectedSwatch = b; break;
            }
        }

        private (Border? editor, TextBlock? label, PaletteStrip? palette) GetEditorWidgets(SwatchSection s) => s switch
        {
            SwatchSection.Rpm    => (WiRpmEditorPanel, WiRpmEditorLabel, WiRpmPalette),
            SwatchSection.Flag   => (WiFlagEditorPanel, WiFlagEditorLabel, WiFlagPalette),
            SwatchSection.Button => (WiButtonEditorPanel, WiButtonEditorLabel, WiButtonPalette),
            _ => (null, null, null),
        };

        private static string FormatSwatchLabel(SwatchSection s, int idx) => s switch
        {
            SwatchSection.Rpm    => $"LED {idx + 1:D2}",
            SwatchSection.Flag   => $"FLAG {idx + 1}",
            SwatchSection.Button => $"BUTTON {idx + 1:D2}",
            _ => "",
        };

        private void SelectSwatchForEditor(Border swatch, ColorSwatchInfo info)
        {
            var s = info.Section;
            if (s == SwatchSection.None) return;

            // Restore the previously-selected swatch's default border.
            var prev = GetSelectedSwatch(s);
            if (prev != null && !ReferenceEquals(prev, swatch))
            {
                prev.BorderBrush = GetCachedBrush(85, 85, 85);
                prev.BorderThickness = new Thickness(1);
                prev.Effect = null;
            }

            // Highlight the new selection with cyan + soft glow.
            swatch.BorderBrush = (Brush)(TryFindResource("CyanBrush") ?? Brushes.Cyan);
            swatch.BorderThickness = new Thickness(2);
            swatch.Effect = (System.Windows.Media.Effects.Effect?)TryFindResource("CyanGlowSoftEffect");
            SetSelectedSwatch(s, swatch);

            // Pre-seed palette with the swatch's current colour and reveal the
            // editor row + label.
            var (editor, label, palette) = GetEditorWidgets(s);
            if (editor == null || label == null || palette == null) return;
            var current = info.ColorSource[info.Index];
            using (_suppressor.Begin())
            {
                palette.SelectedColor = Color.FromRgb(current[0], current[1], current[2]);
            }
            label.Text = FormatSwatchLabel(s, info.Index);
            editor.Visibility = Visibility.Visible;
        }

        private void OnInlinePaletteCommit(SwatchSection s, Color c)
        {
            if (_suppressEvents || _plugin == null) return;
            var swatch = GetSelectedSwatch(s);
            if (swatch == null) return;
            var info = (ColorSwatchInfo)swatch.Tag;
            CommitColorToSwatch(swatch, info, c.R, c.G, c.B);
        }

        private void CommitColorToSwatch(Border swatch, ColorSwatchInfo info, byte r, byte g, byte b)
        {
            if (_plugin == null || _data == null) return;
            string cmdName;
            if (!string.IsNullOrEmpty(info.CommandNameOverride))
                cmdName = info.CommandNameOverride;
            else if (!string.IsNullOrEmpty(info.CommandPrefix))
                cmdName = $"{info.CommandPrefix}{info.Index + 1}";
            else
                cmdName = "";
            if (!string.IsNullOrEmpty(cmdName))
                _plugin.WriteColorIfWheelDetected(cmdName, r, g, b);
            info.ColorSource[info.Index][0] = r;
            info.ColorSource[info.Index][1] = g;
            info.ColorSource[info.Index][2] = b;
            swatch.Background = GetCachedBrush(r, g, b);

            // MozaProfile.CaptureFromCurrent explicitly skips wheel-LED fields
            // ("UI handlers must write the overlay directly"), so without an
            // explicit overlay update the next ApplyWheelToHardware tick
            // overwrites the user's pick with the stale overlay value. Pack
            // the full array of the matching section into the active wheel
            // overlay so the change actually survives a restart.
            switch (info.Section)
            {
                case SwatchSection.Rpm:
                    _plugin.UpdateActiveWheelOverlay(o =>
                        o.WheelRpmColors = MozaProfile.PackColors(_data.WheelRpmColors));
                    break;
                case SwatchSection.Flag:
                    _plugin.UpdateActiveWheelOverlay(o =>
                        o.WheelFlagColors = MozaProfile.PackColors(_data.WheelFlagColors));
                    break;
                case SwatchSection.Button:
                    _plugin.UpdateActiveWheelOverlay(o =>
                        o.WheelButtonColors = MozaProfile.PackColors(_data.WheelButtonColors));
                    break;
            }

            info.OnChanged?.Invoke();
            _plugin.SaveSettings();
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
            if (_data == null || _plugin == null) return;
            // Write-only on the wire — the wheel overlay is the canonical store.
            // Repack the full array so null -> default black is preserved.
            if (isBackground)
            {
                var packed = MozaProfile.PackColors(_data.WheelKnobBackgroundColors);
                _plugin.UpdateActiveWheelOverlay(o => o.WheelKnobBackgroundColors = packed);
                // "Inactive" swatch bulk-sets all ring LEDs for this knob to the same color.
                // Writes to hardware + updates MozaData but does NOT persist ring colors —
                // individual ring swatch edits take priority on next save.
                BulkSetKnobRingColor(idx);
            }
            else
            {
                var packed = MozaProfile.PackColors(_data.WheelKnobPrimaryColors);
                _plugin.UpdateActiveWheelOverlay(o => o.WheelKnobPrimaryColors = packed);
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
                _plugin.WriteColorIfWheelDetected($"wheel-knob-bg-color{ledIdx + 1}", r, g, b);
                _data.KnobRingColors[ledIdx][0] = r;
                _data.KnobRingColors[ledIdx][1] = g;
                _data.KnobRingColors[ledIdx][2] = b;
            }
            // Read each ring LED back so swatches reflect wheel ground-truth.
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
            if (_data == null || _plugin == null) return;
            var packed = MozaProfile.PackColors(_data.KnobRingColors);
            _plugin.UpdateActiveWheelOverlay(o => o.WheelKnobRingColors = packed);
        }

        private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var border = (Border)sender;
            var info = (ColorSwatchInfo)border.Tag;

            // RPM / Flag / Button swatches: route to the inline editor (cyan
            // selection ring + PaletteStrip below). Other swatches with no
            // section (legacy knob-row containers in WheelKnobRingSection, the
            // sleep colour swatch which has its own click handler) fall through
            // to the legacy modal dialog.
            if (info.Section != SwatchSection.None)
            {
                SelectSwatchForEditor(border, info);
                return;
            }

            var current = info.ColorSource[info.Index];
            var dialog = new ColorPickerDialog(current[0], current[1], current[2]);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                CommitColorToSwatch(border, info,
                    dialog.SelectedR, dialog.SelectedG, dialog.SelectedB);
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
                FilesTab.Visibility = Visibility.Collapsed;
                return;
            }

            if (!_swatchesBuilt)
                BuildColorSwatches();

            InitTelemetryUI();
            RefreshTelemetryStatus();
            RefreshFilesTab();

            // Use the linked LED driver's model-aware connection check when available
            bool wheelConnected = LinkedLedDriver?.IsConnected() ?? false;
            StatusDot.Fill = wheelConnected ? Brushes.LimeGreen : Brushes.Red;
            StatusText.Text = wheelConnected ? "Connected" : "Disconnected";

            bool isOldProtoDevice = LinkedLedDriver?.ExpectedModelPrefix == MozaDeviceConstants.OldProtocolMarker;
            bool oldWheel = wheelConnected && isOldProtoDevice && _plugin!.IsOldWheelDetected;
            // Wait for wheel-model-name to arrive before declaring the panel
            // "ready": the per-page guid is keyed off the model prefix, and any
            // UI write into a per-page bundle/overlay before that resolves
            // silently drops on disk (no guid → no dict slot → no save).
            // Detection runs in two beats — wheel-telemetry-mode flips
            // NewWheelDetected, then wheel-model-name arrives a few hundred ms
            // later. Gating on IsWheelPageReady delays the tabs until the second
            // beat lands, so the user can't touch a dropdown / palette before
            // their choice can be persisted.
            bool newWheel = wheelConnected && !isOldProtoDevice
                            && _plugin!.IsNewWheelDetected
                            && _plugin!.IsWheelPageReady;

            if (wheelConnected)
            {
                string modelName = _data!.WheelModelName;
                string swVersion = _data.WheelSwVersion;
                string hwVersion = _data.WheelHwVersion;
            }

            using (_suppressor.Begin())
            {
                bool anyWheel = newWheel || oldWheel;
                WheelNotDetectedPanel.Visibility = anyWheel ? Visibility.Collapsed : Visibility.Visible;

                var modelInfoForTabs = newWheel ? _plugin?.WheelModelInfo : null;
                // Tri-state display gate: known-display wheels (HasDisplay==true) show the
                // dashboard tab immediately on connect; known-no-display wheels (false) never
                // show it; unknown models defer to the runtime IsDisplayDetected probe.
                bool showTelemetry = newWheel && (_plugin?.ShouldDriveDashboard() ?? false);
                bool showButtonsTab = newWheel && (modelInfoForTabs?.ButtonLedCount ?? 0) > 0;
                bool showKnobsTab = newWheel && (modelInfoForTabs?.KnobCount ?? 0) > 0;

                DashboardTab.Visibility = showTelemetry ? Visibility.Visible : Visibility.Collapsed;
                RpmTab.Visibility = anyWheel ? Visibility.Visible : Visibility.Collapsed;
                ButtonsTab.Visibility = showButtonsTab ? Visibility.Visible : Visibility.Collapsed;
                KnobsTab.Visibility = showKnobsTab ? Visibility.Visible : Visibility.Collapsed;
                // Files tab: dashboard upload + wheel-side dashboard inventory.
                // Same gate as DashboardTab — dashboard support implies the
                // wheel has a display and the management-session pipeline that
                // both features depend on.
                FilesTab.Visibility = showTelemetry ? Visibility.Visible : Visibility.Collapsed;
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

                    // Idle-effect speed sliders. Read from _data (mirrored from
                    // the overlay by ApplyWheelToHardware on detection, and
                    // updated live by UI handlers).
                    int rpmSpeed = _data.WheelTelemetryIdleSpeedMs;
                    if (rpmSpeed < 0) rpmSpeed = 1000;
                    WheelIdleSpeedSlider.Value = System.Math.Max(WheelIdleSpeedSlider.Minimum, System.Math.Min(WheelIdleSpeedSlider.Maximum, rpmSpeed));
                    WheelIdleSpeedValue.Text = $"{rpmSpeed} ms";
                    int btnSpeed = _data.WheelButtonsIdleSpeedMs;
                    if (btnSpeed < 0) btnSpeed = 1000;
                    WheelButtonIdleSpeedSlider.Value = System.Math.Max(WheelButtonIdleSpeedSlider.Minimum, System.Math.Min(WheelButtonIdleSpeedSlider.Maximum, btnSpeed));
                    WheelButtonIdleSpeedValue.Text = $"{btnSpeed} ms";
                    int knbSpeed = _data.WheelKnobIdleSpeedMs;
                    if (knbSpeed < 0) knbSpeed = 1000;
                    WheelKnobIdleSpeedSlider.Value = System.Math.Max(WheelKnobIdleSpeedSlider.Minimum, System.Math.Min(WheelKnobIdleSpeedSlider.Maximum, knbSpeed));
                    WheelKnobIdleSpeedValue.Text = $"{knbSpeed} ms";
                    UpdateIdleSpeedRowVisibility();

                    // Sleep-light tab — drive the UI from the persisted per-page
                    // bundle (the user's saved choice), NOT from _data which
                    // mirrors the wheel's transient current state. The wheel
                    // returns wheel-idle-timeout=0 when sleep mode is Off,
                    // which doesn't map to any dropdown option → the dropdown
                    // would render empty and look like the value was wiped.
                    var sleepBundle = _plugin!.ActiveWheelSleep;
                    int sleepMode = sleepBundle?.Mode ?? _data.WheelIdleMode;
                    int sleepTimeoutMin = sleepBundle?.TimeoutMin > 0
                        ? sleepBundle.TimeoutMin
                        : _data.WheelIdleTimeout;
                    int sleepSpeed = sleepBundle?.SpeedMs > 0
                        ? sleepBundle.SpeedMs
                        : (_data.WheelIdleSpeed > 0 ? _data.WheelIdleSpeed : 1000);
                    SetComboSafe(WheelSleepModeCombo, sleepMode);
                    SelectSleepTimeoutByMinutes(sleepTimeoutMin);
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

                    // Render LED swatches from the per-game overlay (the user's
                    // saved choice) when available, falling back to _data
                    // (wheel's transient current state) only when nothing has
                    // been saved. Otherwise the wheel's responses for slot N
                    // can mask the user's saved color — observed on the first
                    // RPM swatch where the wheel reports a stale value and the
                    // UI shows it instead of the just-saved user pick.
                    var wheelOv = _plugin.GetCurrentWheelOverlay(_plugin.Settings?.ProfileStore?.CurrentProfile);
                    var rpmOverlay = MergeOverlayIntoData(wheelOv?.WheelRpmColors, _data.WheelRpmColors);
                    var flagOverlay = MergeOverlayIntoData(wheelOv?.WheelFlagColors, _data.WheelFlagColors);
                    var buttonOverlay = MergeOverlayIntoData(wheelOv?.WheelButtonColors, _data.WheelButtonColors);
                    UpdateSwatches(_wheelFlagColorSwatches, flagOverlay, 6);
                    UpdateSwatches(_wheelButtonColorSwatches, buttonOverlay, 14);
                    for (int i = 0; i < 14; i++)
                    {
                        var cb = _wheelButtonDefaultTelemetryChecks[i];
                        if (cb == null) continue;
                        bool want = _data.WheelButtonDefaultDuringTelemetry[i];
                        if ((cb.IsChecked == true) != want) cb.IsChecked = want;
                    }
                    UpdateSwatches(_wheelRpmColorSwatches, rpmOverlay, rpmCount);

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

                    // Push the same data into the visible KnobRingViz UI on the Knobs sub-tab.
                    RefreshKnobRingViz(knobCount, modelInfo?.KnobRingLeds);

                    // Legacy knob-ring section is collapsed unconditionally — the
                    // KnobRingViz path above replaces it. Kept in the visual tree
                    // so the existing swatch-build code-behind keeps resolving.
                    var ringLeds = modelInfo?.KnobRingLeds;
                    bool showRings = knobCount > 0 && ringLeds != null && _plugin!.IsWheelLedGroupPresent(3);
                    WheelKnobRingSection.Visibility = Visibility.Collapsed;
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

                    }
                }

                if (oldWheel)
                {
                    SetComboSafe(EsRpmIndicatorCombo, (int)IndicatorMode.FromEsStored(_data!.WheelRpmIndicatorMode));
                    SetComboSafe(EsRpmDisplayCombo, _data.WheelRpmDisplayMode);
                }
            }

            // Phase 6: refresh Inputs sub-tab + Knobs sub-tab signal-mode rows
            // directly from _data. No dependency on the (now hidden) plugin-pane
            // WheelTab controls — handlers in MozaWheelSettingsControl.Inputs.cs
            // own the persistence path.
            RefreshInputsAndKnobsSignalMode(newWheel);
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
        // s_brushCache moved to WheelUi/WheelUiHelpers.

        // SetComboSafe, Clamp moved to UI/UiHelpers.

        // ===== New wheel handlers =====

        private void WheelTelemetryModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelTelemetryModeCombo.SelectedIndex;
            _data!.WheelTelemetryMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelTelemetryMode = val);
            _plugin.WriteIfWheelDetected("wheel-telemetry-mode", val);
            _plugin.SaveSettings();
        }

        // Idle-effect handlers commit to the per-wheel-page idle bundle on
        // MozaPluginSettings.WheelIdleByPageGuid (schema v9). Idle animation
        // is a property of the wheel, not the game — same reasoning as the
        // sleep-light bundle below.
        private void WheelIdleEffectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelIdleEffectCombo.SelectedIndex;
            if (val < 0) return;
            _data!.WheelTelemetryIdleEffect = val;
            var idle = _plugin.GetOrCreateActiveWheelIdle();
            if (idle != null) idle.TelemetryEffect = val;
            _plugin.WriteIfWheelDetected("wheel-telemetry-idle-effect", val);
            UpdateIdleSpeedRowVisibility();
            _plugin.SaveSettings();
        }

        private void WheelButtonIdleEffectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelButtonIdleEffectCombo.SelectedIndex;
            if (val < 0) return;
            _data!.WheelButtonsIdleEffect = val;
            var idle = _plugin.GetOrCreateActiveWheelIdle();
            if (idle != null) idle.ButtonsEffect = val;
            _plugin.WriteIfWheelDetected("wheel-buttons-idle-effect", val);
            UpdateIdleSpeedRowVisibility();
            _plugin.SaveSettings();
        }

        private void WheelKnobIdleEffectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelKnobIdleEffectCombo.SelectedIndex;
            if (val < 0) return;
            _data!.WheelKnobIdleEffect = val;
            var idle = _plugin.GetOrCreateActiveWheelIdle();
            if (idle != null) idle.KnobEffect = val;
            _plugin.WriteIfWheelDetected("wheel-knob-idle-effect", val);
            UpdateIdleSpeedRowVisibility();
            _plugin.SaveSettings();
        }

        // Per-effect idle speed (cmd 0x1E [group] [effect_id] [BE u16 ms]).
        // The slider value is paired with the currently-selected idle effect at
        // write time; the wire payload is `[effect_id, ms_msb, ms_lsb]`. Stored
        // in the per-wheel-page idle bundle alongside the effect IDs.
        private void WheelIdleSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int ms = (int)System.Math.Round(e.NewValue);
            WheelIdleSpeedValue.Text = $"{ms} ms";
            _data!.WheelTelemetryIdleSpeedMs = ms;
            var idle = _plugin.GetOrCreateActiveWheelIdle();
            if (idle != null) idle.TelemetrySpeedMs = ms;
            int effect = _data.WheelTelemetryIdleEffect;
            if (effect >= 2)
                _plugin.WriteArrayIfWheelDetected("wheel-telemetry-idle-interval", BuildIdleSpeedPayload(effect, ms));
            _plugin.SaveSettings();
        }

        private void WheelButtonIdleSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int ms = (int)System.Math.Round(e.NewValue);
            WheelButtonIdleSpeedValue.Text = $"{ms} ms";
            _data!.WheelButtonsIdleSpeedMs = ms;
            var idle = _plugin.GetOrCreateActiveWheelIdle();
            if (idle != null) idle.ButtonsSpeedMs = ms;
            int effect = _data.WheelButtonsIdleEffect;
            if (effect >= 2)
                _plugin.WriteArrayIfWheelDetected("wheel-buttons-idle-interval", BuildIdleSpeedPayload(effect, ms));
            _plugin.SaveSettings();
        }

        private void WheelKnobIdleSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int ms = (int)System.Math.Round(e.NewValue);
            WheelKnobIdleSpeedValue.Text = $"{ms} ms";
            _data!.WheelKnobIdleSpeedMs = ms;
            var idle = _plugin.GetOrCreateActiveWheelIdle();
            if (idle != null) idle.KnobSpeedMs = ms;
            int effect = _data.WheelKnobIdleEffect;
            if (effect >= 2)
                _plugin.WriteArrayIfWheelDetected("wheel-knob-idle-interval", BuildIdleSpeedPayload(effect, ms));
            _plugin.SaveSettings();
        }

        private void WheelKnobLedModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelKnobLedModeCombo.SelectedIndex;
            if (val < 0) return;
            _data!.WheelKnobLedMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelKnobLedMode = val);
            _plugin.WriteIfWheelDetected("wheel-knob-led-mode", val);
            _plugin.SaveSettings();
        }

        private void WheelButtonLedModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelButtonLedModeCombo.SelectedIndex;
            if (val < 0) return;
            _data!.WheelButtonsLedMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelButtonsLedMode = val);
            _plugin.WriteIfWheelDetected("wheel-buttons-led-mode", val);
            _plugin.SaveSettings();
        }

        // Sleep-light tab handlers. Sleep settings are per-wheel-page (shared
        // across all profiles) — they're a firmware preference, not a per-game
        // decision. UI mutates the dict entry directly via GetOrCreateActiveWheelSleep().
        private void WheelSleepModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelSleepModeCombo.SelectedIndex;
            if (val < 0) return;
            _data!.WheelIdleMode = val;
            var sleep = _plugin.GetOrCreateActiveWheelSleep();
            if (sleep != null) sleep.Mode = val;
            _plugin.WriteIfWheelDetected("wheel-idle-mode", val);
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
            // RefreshWheel only reveals the Sleep tab once IsWheelPageReady is
            // true, so GetOrCreateActiveWheelSleep is guaranteed to return a
            // valid bundle here (page guid is resolvable).
            var sleep = _plugin.GetOrCreateActiveWheelSleep();
            int prev = sleep?.TimeoutMin ?? -2;
            if (sleep != null) sleep.TimeoutMin = minutes;
            MozaLog.Info($"[Moza] SLEEP-USER: bundle.TimeoutMin {prev} -> {minutes} (from dropdown handler, sleep={(sleep==null?"null":"ok")})");
            _plugin.WriteIfWheelDetected("wheel-idle-timeout", minutes);
            _plugin.SaveSettings();
        }

        private void WheelSleepSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int ms = (int)System.Math.Round(e.NewValue);
            WheelSleepSpeedValue.Text = $"{ms} ms";
            _data!.WheelIdleSpeed = ms;
            var sleep = _plugin.GetOrCreateActiveWheelSleep();
            if (sleep != null) sleep.SpeedMs = ms;
            int mode = _data.WheelIdleMode;
            if (mode >= 2)
                _plugin.WriteArrayIfWheelDetected("wheel-idle-speed", BuildIdleSpeedPayload(mode, ms));
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
            int packed = (r << 16) | (g << 8) | b;
            var sleep = _plugin.GetOrCreateActiveWheelSleep();
            if (sleep != null) sleep.Color = new[] { packed };
            _plugin.WriteColorIfWheelDetected("wheel-idle-color", r, g, b);
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
            if (display < 0 || display > 2) return;
            int stored = IndicatorMode.ToEsStored((IndicatorDisplayMode)display);
            // ES wheel uses +1 expression: stored 0 -> raw 1, stored 1 -> raw 2, etc.
            int raw = stored + 1;
            _data!.WheelRpmIndicatorMode = stored;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelRpmIndicatorMode = stored);
            _plugin.WriteIfWheelDetected("wheel-rpm-indicator-mode", raw);
            _plugin.SaveSettings();
        }

        private void EsRpmDisplayCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = EsRpmDisplayCombo.SelectedIndex;
            _data!.WheelRpmDisplayMode = val;
            _plugin.UpdateActiveWheelOverlay(o => o.WheelRpmDisplayMode = val);
            _plugin.WriteIfWheelDetected("wheel-set-rpm-display-mode", val);
            _plugin.SaveSettings();
        }

        // ===== Dashboard Telemetry =====

        private bool _telemetryUIInitialized;

        private void InitTelemetryUI()
        {
            if (_telemetryUIInitialized || _plugin == null) return;
            _telemetryUIInitialized = true;

            using (_suppressor.Begin())
            {
                var s = _plugin.Settings;
                TelemetryEnabledCheck.IsChecked = _plugin.ActiveTelemetryEnabled;

                PopulateDashboardCombo();
                    UpdateFolderInfo();
                // CHANNEL MAPPINGS is always-on (no expander gate). Seed the grid
                // once _plugin is resolved + start the 2 Hz value poller.
                PopulateChannelMappingGrid();
                StartMappingValueTimer();
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

            using (_suppressor.Begin())
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
                    if (!string.IsNullOrEmpty(_plugin.ActiveTelemetryMzdashPath))
                        TelemetryProfileCombo.Items.Add(
                            "[Custom: " + System.IO.Path.GetFileName(
                                _plugin.ActiveTelemetryMzdashPath) + "]");
                }

                // Show wheel-reported slot (ground truth); fall back to saved profile name.
                string? selectedName = null;
                var sender = _plugin.TelemetrySender;
                if (sender != null && state != null && state.ConfigJsonList != null
                    && sender.WheelReportedSlot >= 0
                    && sender.WheelReportedSlot < state.ConfigJsonList.Count)
                {
                    string wheelName = state.ConfigJsonList[sender.WheelReportedSlot];
                    if (!string.IsNullOrEmpty(wheelName))
                        selectedName = wheelName;
                }
                if (string.IsNullOrEmpty(selectedName))
                    selectedName = _plugin.ActiveTelemetryProfileName;
                if (!string.IsNullOrEmpty(selectedName))
                {
                    for (int i = 0; i < TelemetryProfileCombo.Items.Count; i++)
                    {
                        // OrdinalIgnoreCase to match the rest of the dashboard
                        // binding chain (DashboardBindingCoordinator key/name
                        // lookups); a case mismatch between the saved profile
                        // name and the wheel's ConfigJsonList entry used to
                        // leave the combo stuck on the prior selection.
                        if (string.Equals(TelemetryProfileCombo.Items[i]?.ToString(), selectedName, StringComparison.OrdinalIgnoreCase))
                        {
                            TelemetryProfileCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
                if (TelemetryProfileCombo.SelectedIndex < 0 && TelemetryProfileCombo.Items.Count > 0)
                    TelemetryProfileCombo.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// Track whether the dropdown was last populated from wheel-reported
        /// dashboards vs fallback builtins, so we can re-populate when wheel
        /// state first arrives mid-session.
        /// </summary>
        private bool _dashComboFromWheelState;

        // Signature of the data feeding the channel-mapping grid. Composed from
        // (profile-ref-hash, tier-count, total-channel-count, string-channel-count,
        // catalog-count) — any change means the wheel sent more data and we should
        // rebuild. -1 means "never populated".
        private long _lastMappingDataSignature = -1;

        private long ComputeMappingDataSignature()
        {
            if (_plugin == null) return -2;
            var profile = _plugin.TelemetrySender?.Profile;
            int catalogCount = _plugin.WheelChannelCatalogForDiagnostics?.Count ?? 0;
            if (profile == null) return ((long)catalogCount << 40);
            int tiers = profile.Tiers.Count;
            int channels = 0;
            for (int i = 0; i < tiers; i++) channels += profile.Tiers[i].Channels.Count;
            int strings = profile.StringChannels.Count;
            // Pack: profile identity (low bits of hashcode) + counts. Profile rebuilds
            // on dashboard switch get a new object reference → different hash → re-populate.
            long sig = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(profile) & 0xFFFFFFL;
            sig |= (long)(tiers      & 0xFF) << 24;
            sig |= (long)(channels   & 0xFFFF) << 32;
            sig |= (long)(strings    & 0xFF) << 48;
            sig |= (long)(catalogCount & 0xFF) << 56;
            return sig;
        }

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

            // Re-seed CHANNEL MAPPINGS when the data source first appears AND
            // whenever it grows. The wheel streams the catalog/tier list in
            // chunks: a one-shot populate on first byte misses the rest. Re-poll
            // every tick using a cheap signature (profile-ref + tier/channel
            // counts + catalog count) and rebuild only when the signature
            // actually changes. PopulateChannelMappingGrid snapshots the
            // signature itself so other call sites also keep this in sync.
            long sig = ComputeMappingDataSignature();
            if (sig != _lastMappingDataSignature) PopulateChannelMappingGrid();

            bool enabled = _plugin.ActiveTelemetryEnabled;
            var active = _plugin.TelemetrySender;
            bool testMode = active?.TestMode ?? false;
            int framesSent = _plugin.FramesSentForDiagnostics;

            // Sync checkbox to overlay each tick so game/profile switches reflect immediately.
            if (TelemetryEnabledCheck.IsChecked != enabled)
            {
                using (_suppressor.Begin())
                    TelemetryEnabledCheck.IsChecked = enabled;
            }

            // Sender ready for user switching: Active + not in cooldown + no pending apply.
            // IsPendingDashboardApply stays true across the full switch transient so the
            // combo doesn't flicker.
            bool inCooldown = active?.IsInSilenceCooldown ?? false;
            bool pendingApply = _plugin?.IsPendingDashboardApply ?? false;
            bool senderReady = active != null && active.IsActive && !inCooldown && !pendingApply;
            if (!enabled)
                TelemetryStatusLabel.Text = "Disabled";
            else if (testMode)
                TelemetryStatusLabel.Text = $"Test pattern — {framesSent} frames sent";
            else if (active != null && !active.IsActive)
                TelemetryStatusLabel.Text = inCooldown
                    ? "Switching dashboard… (post-emit silence)"
                    : "Connecting to wheel…";
            else if (pendingApply)
            {
                string? why = _plugin?.PendingDashboardApplyDescription;
                TelemetryStatusLabel.Text = string.IsNullOrEmpty(why)
                    ? $"Switching dashboard… — {framesSent} frames sent"
                    : $"Switching dashboard… ({why}) — {framesSent} frames sent";
            }
            else if (inCooldown)
                TelemetryStatusLabel.Text = $"Switching dashboard… — {framesSent} frames sent";
            else
                TelemetryStatusLabel.Text = $"Sending — {framesSent} frames sent";

            TelemetryTestStopBtn.IsEnabled = testMode && senderReady;
            TelemetryTestStartBtn.IsEnabled = !testMode && senderReady;
            TelemetryProfileCombo.IsEnabled = senderReady;

            // Refresh profile info — auto-renegotiate may have swapped
            // the profile on a background thread after a dashboard switch.
        }

        private void TelemetryEnabledCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetTelemetryEnabled(TelemetryEnabledCheck.IsChecked == true);
        }

        private void WheelDisplayBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            WheelDisplayBrightnessValue.Text = $"{val}";
            _data!.DashDisplayBrightness = val;
            _plugin.UpdateActiveProfile(p => p.DashDisplayBrightness = val);
            // Defer wire write + persist until slider settles — avoids flooding
            // the sess=0x02 retransmit queue with intermediate values.
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
            // allowZero: true — slider-to-zero is deliberate user intent; other call sites suppress 0.
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
            _plugin.UpdateActiveProfile(p => p.DashDisplayStandbyMin = minutes);
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
            var active = _plugin.TelemetrySender;
            var state = _plugin.WheelStateForDiagnostics;

            // Wheel-reported mode: dropdown is configJsonList-ordered.
            // Index IS the slot directly.
            if (active != null && state != null && state.ConfigJsonList.Count > 0
                && idx >= 0 && idx < state.ConfigJsonList.Count)
            {
                // OnDashboardSwitched(slot) routes through SwitchToProfile so the
                // EnableHotRenegotiation feature flag is honoured and FF kind=4 is
                // emitted from a single place.
                _plugin.ActiveTelemetryProfileName = selected;
                _plugin.ActiveTelemetryMzdashPath = "";
                _plugin.SaveSettings();

                _plugin.OnDashboardSwitched((uint)idx);

                    PopulateChannelMappingGrid();
                return;
            }

            // Fallback: builtin-profile mode (no wheel state).
            if (selected == "(none)")
            {
                _plugin.ActiveTelemetryProfileName = "";
                _plugin.ActiveTelemetryMzdashPath = "";
                _plugin.SaveSettings();
                _plugin.OnDashboardSwitched();
                    PopulateChannelMappingGrid();
                return;
            }
            if (!selected.StartsWith("[Custom:"))
            {
                _plugin.ActiveTelemetryProfileName = selected;
                _plugin.ActiveTelemetryMzdashPath = "";
                _plugin.SaveSettings();
                _plugin.OnDashboardSwitched();
                    PopulateChannelMappingGrid();
            }
        }

        private void TelemetryClearMzdash_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.ActiveTelemetryProfileName = "";
            _plugin.ActiveTelemetryMzdashPath = "";
            _plugin.SaveSettings();
            _plugin.OnDashboardSwitched();

            using (_suppressor.Begin())
            {
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
            }

            PopulateChannelMappingGrid();
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

            _plugin.ActiveTelemetryMzdashPath = dlg.FileName;
            _plugin.ActiveTelemetryProfileName = "";
            _plugin.SaveSettings();
            // Hot-reload tier def on the existing session — mirrors PitHouse's
            // mid-game dash-change burst on session 0x01.
            _plugin.OnDashboardSwitched();

            using (_suppressor.Begin())
            {
                string label = "[Custom: " + System.IO.Path.GetFileName(dlg.FileName) + "]";
                for (int i = TelemetryProfileCombo.Items.Count - 1; i >= 0; i--)
                    if (TelemetryProfileCombo.Items[i]?.ToString()?.StartsWith("[Custom:") == true)
                        TelemetryProfileCombo.Items.RemoveAt(i);
                TelemetryProfileCombo.Items.Add(label);
                TelemetryProfileCombo.SelectedIndex = TelemetryProfileCombo.Items.Count - 1;
            }

            PopulateChannelMappingGrid();
        }

        private void TelemetrySetFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Select folder containing .mzdash dashboard files";
                dlg.ShowNewFolderButton = false;
                if (!string.IsNullOrEmpty(_plugin.ActiveTelemetryMzdashFolder)
                    && System.IO.Directory.Exists(_plugin.ActiveTelemetryMzdashFolder))
                    dlg.SelectedPath = _plugin.ActiveTelemetryMzdashFolder;

                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                _plugin.ActiveTelemetryMzdashFolder = dlg.SelectedPath;
                _plugin.SaveSettings();
                _plugin.DashCache?.LoadFromFolder(dlg.SelectedPath);
                PopulateDashboardCombo();
                _plugin.ApplyTelemetrySettings();
                UpdateFolderInfo();
            }
        }

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
                // Match the UID-named subfolder case-insensitively. PitHouse
                // normalizes these to lowercase, but a case-sensitive FS
                // (Linux/Wine, case-sensitive NTFS) would still miss a
                // mismatched case from older installs or manual copies.
                string? match = System.IO.Directory.EnumerateDirectories(dashesRoot)
                    .FirstOrDefault(p => string.Equals(
                        new System.IO.DirectoryInfo(p).Name,
                        uidHex,
                        StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    picked = match;
                else
                    failReason = $"No dashboard folder for the connected wheel (UID {uidHex}).\n" +
                                 $"Looked under:\n{dashesRoot}\n\n" +
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

            // Per-wheel-page overlay carries the folder. _plugin's helper writes
            // into the current wheel's overlay; legacy WheelMzdashFolderByUid is
            // no longer maintained.
            _plugin.ActiveTelemetryMzdashFolder = picked;
            _plugin.SaveSettings();
            _plugin.DashCache?.LoadFromFolder(picked);
            PopulateDashboardCombo();
            _plugin.ApplyTelemetrySettings();
            UpdateFolderInfo();
        }

        private void UpdateFolderInfo()
        {
            if (_plugin == null) return;
            var folder = _plugin.ActiveTelemetryMzdashFolder;
            TelemetryFolderInfo.Text = string.IsNullOrEmpty(folder) ? "" : $"Folder: {folder}";
        }

        private void TelemetryTestStart_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var active = _plugin.TelemetrySender;
            if (active == null) return;
            active.TestMode = true;
            if (!_plugin.ActiveTelemetryEnabled)
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
            var active = _plugin.TelemetrySender;
            if (active == null) return;
            active.TestMode = false;
            if (!_plugin.ActiveTelemetryEnabled)
                active.Stop();
            TelemetryTestStartBtn.IsEnabled = true;
            TelemetryTestStopBtn.IsEnabled = false;
        }

        // ===== Channel mappings =====

        // 2 Hz refresh of the "Current value" column. Started from OnLoaded
        // (CHANNEL MAPPINGS card is always visible — no expander gate).
        private DispatcherTimer? _mappingValueTimer;
        private static readonly TimeSpan MappingValueInterval = TimeSpan.FromMilliseconds(500);

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

        // Reset SelectAll to caret-at-end on dropdown open so filter-typing isn't clobbered.
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
            // Snapshot the data signature so the RefreshTelemetryStatus growth
            // detector doesn't re-trigger every 500ms for already-current data.
            _lastMappingDataSignature = ComputeMappingDataSignature();
            if (_plugin == null) { TelemetryChannelGrid.ItemsSource = null; return; }

            // Unsubscribe from prior rows so stale rows can be GC'd and we don't
            // double-fire OnMappingRowPropertyChanged when the same plugin
            // instance re-populates the grid (dashboard switch, Reset).
            if (TelemetryChannelGrid.ItemsSource is IEnumerable<ChannelMappingRow> oldRows)
            {
                foreach (var r in oldRows) r.PropertyChanged -= OnMappingRowPropertyChanged;
            }

            var result = ChannelMappingRowFactory.Build(_plugin);
            if (result.Rows == null)
            {
                TelemetryChannelGrid.ItemsSource = null;
                if (!string.IsNullOrEmpty(result.StatusText))
                    TelemetryMappingStatus.Text = result.StatusText;
                return;
            }

            TelemetryChannelGrid.ItemsSource = result.Rows;
            // Now that initial-state filter passes have run with dropdown auto-open
            // suppressed, allow further user-typed input to open the dropdown.
            // Also subscribe for auto-save on every edit.
            foreach (var r in result.Rows)
            {
                r.AllowDropdownOpen = true;
                r.PropertyChanged += OnMappingRowPropertyChanged;
            }
            if (!string.IsNullOrEmpty(result.StatusText))
                TelemetryMappingStatus.Text = result.StatusText;
        }

    }
}
