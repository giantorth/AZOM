using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MozaControls;

namespace MozaPlugin.Devices
{
    // Phase 7 knob page: per-knob KnobRingViz (ring slot count tracks the
    // wheel's per-knob LED count — 12 for most knobs, 8 for the KS Pro middle
    // knob — plus a centre swatch), a single shared PaletteStrip editor below,
    // and bulk actions ("Fill ring with selected", "Copy this knob to all").
    //
    // Reads/writes the same _data fields as the legacy Border-based UI:
    //   • Centre swatch  ↔ _data.WheelKnobPrimaryColors[knob]
    //   • Ring slot N    ↔ _data.KnobRingColors[WheelModelInfo.KnobRingStartIndex(knob) + N]
    // Wire commands match the existing handlers:
    //   • "wheel-knob{N}-active-color" for the centre swatch
    //   • "wheel-knob-bg-color{ledIndex+1}" for each ring LED
    public partial class MozaWheelSettingsControl
    {
        private KnobRingViz[]? _wiKnobViz;
        private Border[]? _wiKnobViewWrappers;       // per-knob colours card (selection highlight)
        private Border[]? _wiKnobSignalCardWrappers; // per-knob signal-mode card (no selection state)
        private SegmentedControl[]? _wiKnobSignalChips;  // Btn/Knob chip inside each signal-mode card
        private int _wiSelectedKnob = -1;
        private int _wiSelectedSlot = -1;       // -2 = centre, 0..(N-1) = ring slot
        private bool _wiKnobsBuilt;

        private void BuildKnobRingVizPanels()
        {
            if (_wiKnobsBuilt || WiKnobsGrid == null || _data == null) return;
            int max = MozaData.WheelKnobMax;
            _wiKnobViz = new KnobRingViz[max];
            _wiKnobViewWrappers = new Border[max];
            _wiKnobSignalCardWrappers = new Border[max];
            _wiKnobSignalChips = new SegmentedControl[max];

            var borderBrush = (Brush)(TryFindResource("BorderBrush") ?? Brushes.Transparent);
            var bgCard2Brush = (Brush)(TryFindResource("BgCard2Brush") ?? Brushes.Transparent);
            var textDimBrush = (Brush)(TryFindResource("TextDimBrush") ?? Brushes.Gray);
            var textFaintBrush = (Brush)(TryFindResource("TextFaintBrush") ?? Brushes.Gray);

            for (int k = 0; k < max; k++)
            {
                int knobIdx = k;

                // ===== Signal-mode card (top grid) =====
                // Override the default ItemsPanel (Horizontal StackPanel) with a
                // 2-column UniformGrid so the two segments split the chip width
                // equally — the chip itself stretches to the card width.
                var chip = new SegmentedControl
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsPanel = (ItemsPanelTemplate)XamlReader.Parse(
                        "<ItemsPanelTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                            "<UniformGrid Columns=\"2\" Rows=\"1\"/>" +
                        "</ItemsPanelTemplate>"),
                };
                chip.Items.Add(new ListBoxItem
                {
                    Content = "BUTTON",
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                });
                chip.Items.Add(new ListBoxItem
                {
                    Content = "KNOB",
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                });
                chip.SelectionChanged += (_, __) =>
                {
                    if (_suppressEvents) return;
                    int v = chip.SelectedIndex;
                    if (v < 0) return;
                    var hidden = knobIdx switch
                    {
                        0 => WiKnobSignalMode0Combo,
                        1 => WiKnobSignalMode1Combo,
                        2 => WiKnobSignalMode2Combo,
                        3 => WiKnobSignalMode3Combo,
                        4 => WiKnobSignalMode4Combo,
                        _ => null
                    };
                    if (hidden != null && hidden.SelectedIndex != v) hidden.SelectedIndex = v;
                };
                _wiKnobSignalChips[k] = chip;

                var signalCard = new Border
                {
                    Margin = new Thickness(4),
                    Padding = new Thickness(10),
                    CornerRadius = new CornerRadius(5),
                    BorderThickness = new Thickness(1),
                    BorderBrush = borderBrush,
                    Background = bgCard2Brush,
                };
                var signalStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                signalStack.Children.Add(new TextBlock
                {
                    Text = $"KNOB {knobIdx + 1}",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = textFaintBrush,
                    Margin = new Thickness(0, 0, 0, 8),
                });
                signalStack.Children.Add(chip);
                signalCard.Child = signalStack;
                _wiKnobSignalCardWrappers[k] = signalCard;
                if (WiSignalModeGrid != null)
                    WiSignalModeGrid.Children.Add(signalCard);

                // ===== Colours card (bottom grid) =====
                // Seed with 12 slots so the viz renders something before the
                // first RefreshKnobRingViz tick lands. RefreshKnobRingViz
                // resizes the collection per-knob (e.g. 8 for KS Pro middle
                // knob) once WheelModelInfo is known.
                var ringSeed = new System.Collections.ObjectModel.ObservableCollection<Color>();
                for (int i = 0; i < 12; i++) ringSeed.Add(Colors.Black);
                var viz = new KnobRingViz
                {
                    Width = 110, Height = 110,
                    RingColors = ringSeed,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                viz.SlotSelected += (_, slot) => OnKnobSlotSelected(knobIdx, slot);

                var coloursCard = new Border
                {
                    Margin = new Thickness(4),
                    Padding = new Thickness(10),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    BorderBrush = borderBrush,
                    Background = bgCard2Brush,
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                var coloursStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                coloursStack.Children.Add(viz);
                coloursStack.Children.Add(new TextBlock
                {
                    Text = $"KNOB {knobIdx + 1}",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 0),
                    Foreground = textDimBrush,
                });
                coloursCard.Child = coloursStack;

                _wiKnobViz[k] = viz;
                _wiKnobViewWrappers[k] = coloursCard;
                WiKnobsGrid.Children.Add(coloursCard);
            }

            if (WiKnobPalette != null)
                WiKnobPalette.ColorChanged += (_, c) => OnPaletteColorPicked(c);
            _wiKnobsBuilt = true;
        }

        // Called from RefreshInputsAndKnobsSignalMode — sync per-knob chip
        // visibility + selected index from _data, toggle the per-knob signal grid
        // vs the legacy "All Rotaries" panel based on firmware support, and hide
        // signal-mode cards for knobs that don't exist on this wheel.
        internal void SyncKnobSignalChips()
        {
            if (!_wiKnobsBuilt || _wiKnobSignalChips == null
                || _wiKnobSignalCardWrappers == null || _data == null) return;
            bool perKnob = _data.WheelKnobSignalModeSupported;
            int knobCount = _plugin?.WheelModelInfo?.KnobCount ?? 0;
            // Toggle the whole per-knob grid: visible only when firmware supports
            // per-knob mode AND there's at least one knob to show. Legacy mode
            // hides the grid; the WiKnobModeLegacyPanel takes over (managed in
            // MozaWheelSettingsControl.Inputs.cs).
            if (WiSignalModeGrid != null)
                WiSignalModeGrid.Visibility = (perKnob && knobCount > 0)
                    ? Visibility.Visible : Visibility.Collapsed;
            using (_suppressor.Begin())
            {
                for (int k = 0; k < _wiKnobSignalChips.Length; k++)
                {
                    var chip = _wiKnobSignalChips[k];
                    var card = _wiKnobSignalCardWrappers[k];
                    int v = _data.WheelKnobSignalModes[k];
                    bool present = k < knobCount;
                    bool show = perKnob && present && v >= 0;
                    card.Visibility = present ? Visibility.Visible : Visibility.Collapsed;
                    chip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    if (show && chip.SelectedIndex != v) chip.SelectedIndex = v;
                }
            }
        }

        // Reflect _data → KnobRingViz on every refresh tick. Also toggles
        // per-knob visibility (Collapsed when knobCount < this knob index)
        // and resizes each viz's ring to match the per-knob LED count
        // (e.g. KS Pro middle knob = 8 dots evenly spaced, not 12 with 4 dimmed).
        private void RefreshKnobRingViz(int knobCount, int[]? ledsPerKnob)
        {
            if (!_wiKnobsBuilt || _wiKnobViz == null || _wiKnobViewWrappers == null || _data == null) return;
            // Match the visible-knob count so 4-knob wheels fill the full row
            // instead of leaving a phantom 5th column. UniformGrid otherwise
            // reserves a slot for the collapsed 5th card.
            int gridCols = Math.Max(1, knobCount);
            if (WiKnobsGrid != null && WiKnobsGrid.Columns != gridCols) WiKnobsGrid.Columns = gridCols;
            if (WiSignalModeGrid != null && WiSignalModeGrid.Columns != gridCols) WiSignalModeGrid.Columns = gridCols;
            int max = _wiKnobViz.Length;
            for (int k = 0; k < max; k++)
            {
                bool present = k < knobCount;
                _wiKnobViewWrappers[k].Visibility = present ? Visibility.Visible : Visibility.Collapsed;
                if (!present) continue;
                var viz = _wiKnobViz[k];
                // Centre = active color
                var ac = _data.WheelKnobPrimaryColors[k];
                viz.ActiveColor = Color.FromRgb(ac[0], ac[1], ac[2]);
                // Ring = one dot per physical LED on this knob. Resize the
                // collection if the per-knob count changed (assigning a new
                // ObservableCollection triggers KnobRingViz.OnRingChanged →
                // Rebuild, which re-lays the dots evenly around the ring).
                int ledCount = ledsPerKnob != null && k < ledsPerKnob.Length ? ledsPerKnob[k] : 12;
                if (ledCount <= 0) ledCount = 12;
                int startIdx = _plugin?.WheelModelInfo?.KnobRingStartIndex(k) ?? (k * 12);
                if (viz.RingColors == null || viz.RingColors.Count != ledCount)
                {
                    var fresh = new System.Collections.ObjectModel.ObservableCollection<Color>();
                    for (int i = 0; i < ledCount; i++) fresh.Add(Colors.Black);
                    viz.RingColors = fresh;
                    // If the prior selection points past the new bounds, clear it
                    // so the editor label/palette don't paint into a missing slot.
                    if (_wiSelectedKnob == k && _wiSelectedSlot >= ledCount)
                    {
                        _wiSelectedSlot = -1;
                        viz.SelectedSlot = -1;
                    }
                }
                for (int i = 0; i < ledCount; i++)
                {
                    int absIdx = startIdx + i;
                    if (absIdx < MozaData.KnobRingLedMax)
                    {
                        var rc = _data.KnobRingColors[absIdx];
                        viz.RingColors![i] = Color.FromRgb(rc[0], rc[1], rc[2]);
                    }
                }
            }
        }

        private void OnKnobSlotSelected(int knob, int slot)
        {
            _wiSelectedKnob = knob;
            _wiSelectedSlot = slot;
            HighlightSelectedKnob();
            UpdateEditorLabel();
            if (WiKnobEditorPanel != null) WiKnobEditorPanel.Visibility = Visibility.Visible;
            // Pre-seed palette with the slot's current colour
            if (WiKnobPalette != null && _wiKnobViz != null)
            {
                var viz = _wiKnobViz[knob];
                if (slot == -2) WiKnobPalette.SelectedColor = viz.ActiveColor;
                else if (slot >= 0 && slot < viz.RingColors!.Count) WiKnobPalette.SelectedColor = viz.RingColors[slot];
            }
        }

        private void HighlightSelectedKnob()
        {
            if (_wiKnobViewWrappers == null) return;
            for (int k = 0; k < _wiKnobViewWrappers.Length; k++)
            {
                bool sel = k == _wiSelectedKnob;
                var wrapper = _wiKnobViewWrappers[k];
                wrapper.BorderBrush = sel
                    ? (Brush)(TryFindResource("CyanBrush") ?? Brushes.Cyan)
                    : (Brush)(TryFindResource("BorderBrush") ?? Brushes.Transparent);
                wrapper.Effect = sel ? (Effect?)TryFindResource("CyanGlowSoftEffect") : null;
            }
        }

        private void UpdateEditorLabel()
        {
            if (WiKnobEditorLabel == null) return;
            string slotName;
            if (_wiSelectedSlot == -2) slotName = "ACTIVE";
            else if (_wiSelectedSlot >= 0)
            {
                int ringCount = (_wiKnobViz != null && _wiSelectedKnob >= 0
                                 && _wiSelectedKnob < _wiKnobViz.Length
                                 && _wiKnobViz[_wiSelectedKnob].RingColors != null)
                    ? _wiKnobViz[_wiSelectedKnob].RingColors!.Count
                    : 12;
                slotName = $"LED {_wiSelectedSlot + 1:D2}/{ringCount:D2}";
            }
            else slotName = "—";
            WiKnobEditorLabel.Text = $"EDITING · KNOB {_wiSelectedKnob + 1} · {slotName}";
        }

        private void OnPaletteColorPicked(Color c)
        {
            if (_suppressEvents) return;
            if (_data == null || _plugin == null) return;
            if (_wiSelectedKnob < 0) return;
            int knob = _wiSelectedKnob;
            int slot = _wiSelectedSlot;
            byte r = c.R, g = c.G, b = c.B;

            if (slot == -2)
            {
                // Active (centre) — write per-knob primary
                // B4: atomic 3-byte write — races against serial read thread
                // writing the same slot via wheel-knob{N}-active-color response.
                _data.WriteLedColor(_data.WheelKnobPrimaryColors[knob], r, g, b);
                // Wheel-LED write + live-cache invalidation. Writes during active
                // telemetry are safe — the next live frame overpaints. Cache
                // invalidation forces the live pipeline to re-send rather than
                // dedup'ing against the stale frame.
                _plugin.WriteLedColorIfWheelDetected($"wheel-knob{knob + 1}-active-color", r, g, b, LedKind.Knob);
                // Wheel-LED fields aren't captured by MozaProfile.CaptureFromCurrent —
                // UI handlers must push into the wheel overlay directly, otherwise
                // ApplyWheelToHardware on the next tick writes the stale overlay
                // value back over the user's pick.
                var packed = MozaProfile.PackColors(_data.WheelKnobPrimaryColors);
                _plugin.UpdateActiveWheelOverlay(o => o.WheelKnobPrimaryColors = packed);
                _plugin.SaveSettings();
            }
            else if (slot >= 0)
            {
                int startIdx = _plugin.WheelModelInfo?.KnobRingStartIndex(knob) ?? (knob * 12);
                int ledCount = _plugin.WheelModelInfo?.KnobRingLeds != null
                               && knob < _plugin.WheelModelInfo.KnobRingLeds.Length
                               ? _plugin.WheelModelInfo.KnobRingLeds[knob] : 0;
                if (slot >= ledCount) return;
                int absIdx = startIdx + slot;
                if (absIdx >= MozaData.KnobRingLedMax) return;
                // B4: atomic 3-byte write — races against serial read thread.
                _data.WriteLedColor(_data.KnobRingColors[absIdx], r, g, b);
                // A4: gated wheel-LED write — see OnPaletteColorPicked active branch above.
                _plugin.WriteLedColorIfWheelDetected($"wheel-knob-bg-color{absIdx + 1}", r, g, b, LedKind.Knob);
                PersistKnobRingColors();
                _plugin.SaveSettings();
            }

            // Immediate visual feedback via the next refresh tick, but also push now
            if (_wiKnobViz != null && knob < _wiKnobViz.Length)
            {
                var viz = _wiKnobViz[knob];
                if (slot == -2) viz.ActiveColor = c;
                else if (slot >= 0 && slot < viz.RingColors!.Count) viz.RingColors[slot] = c;
            }
        }

        // "Fill ring with selected" — write the current palette colour to every
        // present ring LED on the currently selected knob. Uses the existing
        // BulkSetKnobRingColor helper for the wire fan-out by temporarily routing
        // WheelKnobBackgroundColors through it.
        private void WiKnobFillRing_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (_data == null || _plugin == null || WiKnobPalette == null || _wiSelectedKnob < 0) return;
            var c = WiKnobPalette.SelectedColor;
            int knob = _wiSelectedKnob;
            // B4: atomic 3-byte write.
            _data.WriteLedColor(_data.WheelKnobBackgroundColors[knob], c.R, c.G, c.B);
            BulkSetKnobRingColor(knob);
            PersistKnobRingColors();
            _plugin.SaveSettings();
            // Mirror in-memory so the next refresh tick paints the new ring
            if (_wiKnobViz != null && knob < _wiKnobViz.Length)
            {
                var viz = _wiKnobViz[knob];
                for (int i = 0; i < viz.RingColors!.Count; i++) viz.RingColors[i] = c;
            }
        }

        // "Copy this knob to all" — apply the selected knob's ACTIVE + ring colours
        // to every other present knob.
        private void WiKnobCopyToAll_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (_data == null || _plugin == null || _wiSelectedKnob < 0) return;
            int src = _wiSelectedKnob;
            var srcActive = _data.WheelKnobPrimaryColors[src];
            int srcStart = _plugin.WheelModelInfo?.KnobRingStartIndex(src) ?? (src * 12);
            int srcLedCount = _plugin.WheelModelInfo?.KnobRingLeds != null && src < _plugin.WheelModelInfo.KnobRingLeds.Length
                ? _plugin.WheelModelInfo.KnobRingLeds[src] : 0;
            int knobCount = _plugin.WheelModelInfo?.KnobCount ?? 0;
            for (int k = 0; k < knobCount; k++)
            {
                if (k == src) continue;
                // Copy active
                // B4: atomic 3-byte write.
                _data.WriteLedColor(_data.WheelKnobPrimaryColors[k], srcActive[0], srcActive[1], srcActive[2]);
                // Wheel-LED write + live-cache invalidation (see WriteLedColorIfWheelDetected).
                _plugin.WriteLedColorIfWheelDetected($"wheel-knob{k + 1}-active-color", srcActive[0], srcActive[1], srcActive[2], LedKind.Knob);
                // Copy ring (slot by slot — destination may have a different LED count)
                int dstStart = _plugin.WheelModelInfo?.KnobRingStartIndex(k) ?? (k * 12);
                int dstLedCount = _plugin.WheelModelInfo?.KnobRingLeds != null && k < _plugin.WheelModelInfo.KnobRingLeds.Length
                    ? _plugin.WheelModelInfo.KnobRingLeds[k] : 0;
                int common = Math.Min(srcLedCount, dstLedCount);
                for (int i = 0; i < common; i++)
                {
                    int srcAbs = srcStart + i;
                    int dstAbs = dstStart + i;
                    if (srcAbs >= MozaData.KnobRingLedMax || dstAbs >= MozaData.KnobRingLedMax) break;
                    var c = _data.KnobRingColors[srcAbs];
                    // B4: atomic 3-byte write.
                    _data.WriteLedColor(_data.KnobRingColors[dstAbs], c[0], c[1], c[2]);
                    // Wheel-LED write + live-cache invalidation (see WriteLedColorIfWheelDetected).
                    _plugin.WriteLedColorIfWheelDetected($"wheel-knob-bg-color{dstAbs + 1}", c[0], c[1], c[2], LedKind.Knob);
                }
            }
            // Pack the full per-knob active colour array into the overlay once
            // after all knobs have been copied — same persistence pattern as
            // the per-swatch path; ring colours go through PersistKnobRingColors.
            var packedActive = MozaProfile.PackColors(_data.WheelKnobPrimaryColors);
            _plugin.UpdateActiveWheelOverlay(o => o.WheelKnobPrimaryColors = packedActive);
            PersistKnobRingColors();
            _plugin.SaveSettings();
        }
    }
}
