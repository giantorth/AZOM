using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MozaControls;
using MozaPlugin.Settings;
using MozaPlugin.Devices.Led;

namespace MozaPlugin.Devices.Ui
{
    // Phase 7 knob page: per-knob KnobRingViz (ring slot count tracks the
    // wheel's per-knob LED count — 12 for most knobs, 8 for the KS Pro middle
    // knob — plus a centre swatch), a single shared PaletteStrip editor below,
    // and bulk actions ("Fill ring with selected", "Copy this knob to all").
    //
    // Renders overlay-first (saved sparse arrays win; _data fills unset slots with
    // the wheel's own values — see RefreshWheel) and persists slot-by-slot:
    //   • Centre swatch  ↔ overlay.WheelKnobPrimaryColors[knob]
    //   • Ring slot N    ↔ overlay.WheelKnobRingColors[WheelModelInfo.KnobRingStartIndex(knob) + N]
    // Wire commands:
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
                    // The chip owns the write. Driving the hidden stub's
                    // SelectedIndex instead drops the edit whenever the two already
                    // agree; the stub is only kept in sync for the XAML handler
                    // contract, silently so it can't double-write.
                    var hidden = knobIdx switch
                    {
                        0 => WiKnobSignalMode0Combo,
                        1 => WiKnobSignalMode1Combo,
                        2 => WiKnobSignalMode2Combo,
                        3 => WiKnobSignalMode3Combo,
                        4 => WiKnobSignalMode4Combo,
                        _ => null
                    };
                    if (hidden != null && hidden.SelectedIndex != v)
                    {
                        using (_suppressor.Begin()) hidden.SelectedIndex = v;
                    }
                    WriteWiKnobSignalMode(knobIdx, v);
                };
                // A ListBox raises SelectionChanged only when the selection MOVES, so
                // clicking the segment that is already lit is silent — no event, no
                // frame, nothing. That is precisely the click a user makes when the
                // wheel disagrees with what the tab shows, and it is the one that has
                // to reach the wheel. Tunnelled Down (not Up) so SelectedIndex is still
                // the pre-click value: equal means the selection is about to not move,
                // which is the case SelectionChanged will not cover.
                chip.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    if (_suppressEvents) return;
                    int clicked = SegmentIndexFromSource(chip, e.OriginalSource);
                    if (clicked < 0 || clicked != chip.SelectedIndex) return;
                    WriteWiKnobSignalMode(knobIdx, clicked, force: true);
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

        // How many BUTTON/KNOB selectors this wheel gets. The catalogued
        // WheelModelInfo.KnobEncoderCount is authoritative when present (>= 0),
        // including the 0 case — firmware answers every wheel-knob-signal-mode index
        // whether or not the encoder is there, so the read answers cannot be counted.
        // Only an uncatalogued rim falls back to the sweep mask, which over-reports
        // (KS: five answers, three knobs) but keeps the selector reachable until that
        // model's real count is recorded. Deliberately NOT WheelModelInfo.KnobCount:
        // that is the knob-LED capability and is 0 on most rims that do have encoders.
        private int ResolveKnobEncoderCount()
        {
            int catalogued = _plugin?.WheelModelInfo?.KnobEncoderCount ?? -1;
            if (catalogued >= 0) return Math.Min(catalogued, MozaData.WheelKnobMax);
            int mask = _data?.WheelKnobSignalModeMask ?? 0;
            int n = 0;
            for (int i = 0; i < MozaData.WheelKnobMax; i++)
                if ((mask & (1 << i)) != 0) n = i + 1;
            return n;
        }

        // True when the wheel has any configurable rotary encoder — either per-knob
        // (signal-mode answers) or the legacy wheel-wide wheel-knob-mode. A model
        // catalogued with 0 encoders never reads either, so both stay false.
        private bool HasKnobEncoders()
            => ResolveKnobEncoderCount() > 0 || (_data?.WheelKnobModeSupported ?? false);

        // Which segment a mouse event landed on, or -1 if it missed the items.
        private static int SegmentIndexFromSource(SegmentedControl chip, object source)
        {
            var container = ItemsControl.ContainerFromElement(chip, source as DependencyObject)
                            as ListBoxItem;
            return container == null ? -1 : chip.ItemContainerGenerator.IndexFromContainer(container);
        }

        // Per-knob signal mode as the UI must render it: the per-(profile × wheel-page)
        // overlay wins, _data (the wheel's own readback) only fills an unset slot —
        // the same overlay-first rule the other input modes follow. Both the chips and
        // the hidden stubs resolve through here so the two surfaces cannot disagree.
        private int ResolveKnobSignalMode(WheelOverride? ov, int logicalKnob)
        {
            if (_data == null || logicalKnob < 0 || logicalKnob >= MozaData.WheelKnobMax) return -1;
            var sig = ov?.WheelKnobSignalModes;
            if (sig != null && logicalKnob < sig.Length && sig[logicalKnob] >= 0)
                return sig[logicalKnob];
            return _data.WheelKnobSignalModes[logicalKnob];
        }

        // Called from RefreshInputsAndKnobsSignalMode — sync per-knob chip
        // visibility + selected index, toggle the per-knob signal grid
        // vs the legacy "All Rotaries" panel based on firmware support, and hide
        // signal-mode cards for knobs that don't exist on this wheel.
        internal void SyncKnobSignalChips()
        {
            if (!_wiKnobsBuilt || _wiKnobSignalChips == null
                || _wiKnobSignalCardWrappers == null || _data == null) return;
            bool perKnob = _data.WheelKnobSignalModeSupported;
            int encoderCount = ResolveKnobEncoderCount();
            // Size the grid to the encoders, not the LED knobs, so a 3-encoder rim
            // fills the row instead of leaving two phantom columns.
            if (WiSignalModeGrid != null)
            {
                int cols = Math.Max(1, encoderCount);
                if (WiSignalModeGrid.Columns != cols) WiSignalModeGrid.Columns = cols;
                // Legacy mode hides the grid; the WiKnobModeLegacyPanel takes over
                // (managed in MozaWheelSettingsControl.Inputs.cs).
                WiSignalModeGrid.Visibility = (perKnob && encoderCount > 0)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            var ov = _plugin?.GetCurrentWheelOverlay(_plugin.Settings?.ProfileStore?.CurrentProfile);
            using (_suppressor.Begin())
            {
                for (int k = 0; k < _wiKnobSignalChips.Length; k++)
                {
                    var chip = _wiKnobSignalChips[k];
                    var card = _wiKnobSignalCardWrappers[k];
                    int v = ResolveKnobSignalMode(ov, k);
                    bool present = k < encoderCount;
                    // Show every present knob's selector once firmware reports
                    // per-knob support. A not-yet-read value (-1) leaves the chip
                    // unselected rather than hidden, so a partial/late read can't
                    // leave the trailing knob boxes blank (only the first drawn).
                    bool show = perKnob && present;
                    card.Visibility = present ? Visibility.Visible : Visibility.Collapsed;
                    chip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    int want = v >= 0 ? v : -1;
                    if (show && chip.SelectedIndex != want) chip.SelectedIndex = want;
                }
            }
        }

        // Reflect the merged (overlay-first) palettes → KnobRingViz on every refresh
        // tick. Also toggles per-knob visibility (Collapsed when knobCount < this
        // knob index) and resizes each viz's ring to match the per-knob LED count
        // (e.g. KS Pro middle knob = 8 dots evenly spaced, not 12 with 4 dimmed).
        private void RefreshKnobRingViz(byte[][] active, byte[][] ring, int knobCount, int[]? ledsPerKnob)
        {
            if (!_wiKnobsBuilt || _wiKnobViz == null || _wiKnobViewWrappers == null || _data == null) return;
            // Match the visible-knob count so 4-knob wheels fill the full row
            // instead of leaving a phantom 5th column. UniformGrid otherwise
            // reserves a slot for the collapsed 5th card. The signal-mode grid
            // sizes itself off the encoder count in SyncKnobSignalChips — the two
            // counts differ on a rim whose encoders have no LED rings.
            int gridCols = Math.Max(1, knobCount);
            if (WiKnobsGrid != null && WiKnobsGrid.Columns != gridCols) WiKnobsGrid.Columns = gridCols;
            int max = _wiKnobViz.Length;
            for (int k = 0; k < max; k++)
            {
                bool present = k < knobCount;
                _wiKnobViewWrappers[k].Visibility = present ? Visibility.Visible : Visibility.Collapsed;
                if (!present) continue;
                var viz = _wiKnobViz[k];
                // Centre = active color
                var ac = active[k];
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
                        var rc = ring[absIdx];
                        var c = Color.FromRgb(rc[0], rc[1], rc[2]);
                        // The ObservableCollection indexer raises Replace even for an
                        // identical value; skip unchanged slots on this 500 ms tick.
                        if (viz.RingColors![i] != c) viz.RingColors[i] = c;
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
                _plugin.HardwareApplier.WriteLedColorIfWheelDetected($"wheel-knob{knob + 1}-active-color", r, g, b, LedKind.Knob);
                // Wheel-LED fields aren't captured by MozaProfile.CaptureFromCurrent —
                // UI handlers push into the wheel overlay directly (this slot only).
                PersistKnobActiveSlot(knob, MozaProfile.PackColor(new[] { r, g, b }));
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
                _plugin.HardwareApplier.WriteLedColorIfWheelDetected($"wheel-knob-bg-color{absIdx + 1}", r, g, b, LedKind.Knob);
                PersistKnobRingSlots(new[] { (absIdx, MozaProfile.PackColor(new[] { r, g, b })) });
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
        // present ring LED on the currently selected knob and persist those slots.
        private void WiKnobFillRing_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (_data == null || _plugin == null || WiKnobPalette == null || _wiSelectedKnob < 0) return;
            var c = WiKnobPalette.SelectedColor;
            int knob = _wiSelectedKnob;
            PersistKnobRingSlots(BulkSetKnobRingColor(knob, c.R, c.G, c.B));
            _plugin.SaveSettings();
            // Mirror in-memory so the next refresh tick paints the new ring
            if (_wiKnobViz != null && knob < _wiKnobViz.Length)
            {
                var viz = _wiKnobViz[knob];
                for (int i = 0; i < viz.RingColors!.Count; i++) viz.RingColors[i] = c;
            }
        }

        // "Copy this knob to all" — apply the selected knob's ACTIVE + ring colours
        // (as rendered: saved slots, else the wheel's values) to every other
        // present knob, and persist every slot written.
        private void WiKnobCopyToAll_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (_data == null || _plugin == null || _wiKnobViz == null || _wiSelectedKnob < 0) return;
            int src = _wiSelectedKnob;
            if (src >= _wiKnobViz.Length) return;
            var srcViz = _wiKnobViz[src];
            var a = srcViz.ActiveColor;
            int srcPacked = MozaProfile.PackColor(new[] { a.R, a.G, a.B });
            int srcLedCount = Math.Min(
                srcViz.RingColors?.Count ?? 0,
                _plugin.WheelModelInfo?.KnobRingLeds != null && src < _plugin.WheelModelInfo.KnobRingLeds.Length
                    ? _plugin.WheelModelInfo.KnobRingLeds[src] : 0);
            int knobCount = Math.Min(_plugin.WheelModelInfo?.KnobCount ?? 0, _wiKnobViz.Length);
            var ringSlots = new System.Collections.Generic.List<(int absIdx, int packed)>();
            for (int k = 0; k < knobCount; k++)
            {
                if (k == src) continue;
                var dstViz = _wiKnobViz[k];
                // Copy active
                // B4: atomic 3-byte write.
                _data.WriteLedColor(_data.WheelKnobPrimaryColors[k], a.R, a.G, a.B);
                // Wheel-LED write + live-cache invalidation (see WriteLedColorIfWheelDetected).
                _plugin.HardwareApplier.WriteLedColorIfWheelDetected($"wheel-knob{k + 1}-active-color", a.R, a.G, a.B, LedKind.Knob);
                PersistKnobActiveSlot(k, srcPacked);
                dstViz.ActiveColor = a;
                // Copy ring (slot by slot — destination may have a different LED count)
                int dstStart = _plugin.WheelModelInfo?.KnobRingStartIndex(k) ?? (k * 12);
                int dstLedCount = _plugin.WheelModelInfo?.KnobRingLeds != null && k < _plugin.WheelModelInfo.KnobRingLeds.Length
                    ? _plugin.WheelModelInfo.KnobRingLeds[k] : 0;
                int common = Math.Min(srcLedCount, dstLedCount);
                for (int i = 0; i < common; i++)
                {
                    int dstAbs = dstStart + i;
                    if (dstAbs >= MozaData.KnobRingLedMax) break;
                    var c = srcViz.RingColors![i];
                    // B4: atomic 3-byte write.
                    _data.WriteLedColor(_data.KnobRingColors[dstAbs], c.R, c.G, c.B);
                    // Wheel-LED write + live-cache invalidation (see WriteLedColorIfWheelDetected).
                    _plugin.HardwareApplier.WriteLedColorIfWheelDetected($"wheel-knob-bg-color{dstAbs + 1}", c.R, c.G, c.B, LedKind.Knob);
                    ringSlots.Add((dstAbs, MozaProfile.PackColor(new[] { c.R, c.G, c.B })));
                    if (dstViz.RingColors != null && i < dstViz.RingColors.Count) dstViz.RingColors[i] = c;
                }
            }
            PersistKnobRingSlots(ringSlots);
            _plugin.SaveSettings();
        }
    }
}
