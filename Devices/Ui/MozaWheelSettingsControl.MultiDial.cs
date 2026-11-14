using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MozaControls;
using MozaPlugin.Hardware;
using MozaPlugin.Settings;

namespace MozaPlugin.Devices.Ui
{
    // Inputs + Knobs tabs: multi-mode thumb dial. Picking MULTI-MODE on a knob's
    // signal-mode selector turns that knob into a position selector whose positions
    // each emit up/down buttons (optionally with the thumb-wheel side controls), with
    // per-position control labels for dashboards. Only one knob can be the dial; it
    // runs in BUTTON signal mode underneath. Stored per wheel (WheelMultiDialSettings).
    // The card exists on both tabs; every edit re-renders both copies.
    public partial class MozaWheelSettingsControl
    {
        private const int MultiDialLabelMaxChars = 4;

        private sealed class MultiDialView
        {
            public readonly UIElement Card;
            public readonly OffOnToggle Left, Right;
            public readonly StackPanel Rows;
            public readonly List<(TextBox Left, TextBox Right)> Boxes = new List<(TextBox, TextBox)>();
            public MultiDialView(UIElement card, OffOnToggle left, OffOnToggle right, StackPanel rows)
            {
                Card = card; Left = left; Right = right; Rows = rows;
            }
        }

        private MultiDialView[]? _mdViews;
        private DispatcherTimer? _mdLabelPushTimer;

        private MultiDialView[] MultiDialViews => _mdViews ??= new[]
        {
            new MultiDialView(WiMultiDialCard, WiMultiDialLeft, WiMultiDialRight, WiMultiDialLabelRows),
            new MultiDialView(WkMultiDialCard, WkMultiDialLeft, WkMultiDialRight, WkMultiDialLabelRows),
        };

        // Signal-mode selector pick: BUTTON / KNOB, or MULTI-MODE (SignalChipMultiMode).
        private void SelectKnobSignalChip(int knob, int value, bool force = false)
        {
            if (_suppressEvents || _plugin == null) return;
            if (value == SignalChipMultiMode)
            {
                SetMultiDialKnob(knob, force);
                return;
            }
            var bundle = _plugin.ActiveWheelMultiDial;
            if (HardwareApplier.MultiDialForcedButtonKnob(bundle, _plugin.WheelModelInfo) == knob)
            {
                var b = _plugin.GetOrCreateActiveWheelMultiDial();
                if (b != null)
                {
                    b.Enabled = 0;
                    _plugin.HardwareApplier.WriteIfWheelDetected("wheel-multifn-enable", 0, force);
                }
            }
            WriteWiKnobSignalMode(knob, value, force);
            using (_suppressor.Begin())
                RefreshMultiDial(true);
        }

        // Make `knob` the dial (moving it off any other knob) and switch the mode on.
        private void SetMultiDialKnob(int knob, bool force)
        {
            var order = _plugin!.WheelModelInfo?.MultiDialKnobOrder;
            var bundle = _plugin.GetOrCreateActiveWheelMultiDial();
            if (bundle == null || order == null || knob < 0 || knob >= order.Length) return;
            bool wasOn = bundle.Enabled == 1;
            bool knobChanged = bundle.Knob != knob;
            bundle.Knob = knob;
            bundle.Enabled = 1;
            // The dial only works in BUTTON signal mode (also saves + resyncs the chips).
            WriteWiKnobSignalMode(knob, 0, force);
            _plugin.HardwareApplier.WriteIfWheelDetected("wheel-multifn-knob", order[knob], force);
            if (!wasOn || force)
                _plugin.HardwareApplier.WriteIfWheelDetected("wheel-multifn-enable", 1, force);
            _plugin.SaveSettings();
            using (_suppressor.Begin())
                RefreshMultiDial(true);
            // The table length follows the knob's positions (8 on the KS Pro middle dial).
            if (knobChanged) ScheduleMultiDialLabelPush();
        }

        private void RefreshMultiDial(bool newWheelDetected)
        {
            var model = _plugin?.WheelModelInfo;
            var bundle = _plugin?.ActiveWheelMultiDial;
            int knob = newWheelDetected ? HardwareApplier.MultiDialForcedButtonKnob(bundle, model) : -1;
            bool show = knob >= 0 && model != null;
            foreach (var v in MultiDialViews)
            {
                v.Card.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (!show) continue;

                SetToggle(v.Left, bundle!.Left == 1);
                SetToggle(v.Right, bundle.Right == 1);

                EnsureMultiDialLabelRows(v, model!.KnobPositionCount(knob));
                var labels = bundle.Labels;
                for (int i = 0; i < v.Boxes.Count; i++)
                {
                    var l = labels != null && i < labels.Count ? labels[i] : null;
                    SetLabelText(v.Boxes[i].Left, l?.Left ?? "");
                    SetLabelText(v.Boxes[i].Right, l?.Right ?? "");
                }
            }
        }

        private static void SetToggle(OffOnToggle toggle, bool on)
        {
            if ((toggle.IsChecked == true) != on) toggle.IsChecked = on;
        }

        // Leave a box the user is typing in alone — the refresh tick would fight them.
        private static void SetLabelText(TextBox box, string text)
        {
            if (!box.IsKeyboardFocusWithin && box.Text != text) box.Text = text;
        }

        // Two side-by-side column sets: positions 1..half, then the rest (6/6, or 4/4
        // on an 8-position knob).
        private void EnsureMultiDialLabelRows(MultiDialView view, int count)
        {
            if (view.Boxes.Count == count) return;
            view.Rows.Children.Clear();
            view.Boxes.Clear();
            var dim = (System.Windows.Media.Brush)(TryFindResource("TextDimBrush") ?? System.Windows.Media.Brushes.Gray);
            var boxStyle = (Style)FindResource("DialLabelBox");
            var numStyle = (Style)FindResource("DialText");
            var sep = (System.Windows.Media.Brush)(TryFindResource("BorderBrightBrush") ?? System.Windows.Media.Brushes.Gray);
            int half = (count + 1) / 2;
            StackPanel? column = null;
            for (int i = 0; i < count; i++)
            {
                if (i == 0 || i == half)
                {
                    if (i == half)
                        view.Rows.Children.Add(new Border
                        {
                            Width = 1,
                            Margin = new Thickness(12, 0, 12, 4),
                            Background = sep,
                        });
                    column = new StackPanel();
                    view.Rows.Children.Add(column);
                }
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
                var left = new TextBox { Style = boxStyle, Tag = i, MaxLength = MultiDialLabelMaxChars };
                var right = new TextBox { Style = boxStyle, Tag = i, MaxLength = MultiDialLabelMaxChars };
                var num = new TextBlock
                {
                    Style = numStyle, FontSize = 13, Text = (i + 1).ToString(), Foreground = dim,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(left, 0);
                Grid.SetColumn(num, 1);
                Grid.SetColumn(right, 2);
                left.TextChanged += WiMultiDialLabel_TextChanged;
                right.TextChanged += WiMultiDialLabel_TextChanged;
                row.Children.Add(left);
                row.Children.Add(num);
                row.Children.Add(right);
                column!.Children.Add(row);
                view.Boxes.Add((left, right));
            }
        }

        private void WiMultiDialSide_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var bundle = _plugin.GetOrCreateActiveWheelMultiDial();
            if (bundle == null) return;
            bool left = ReferenceEquals(sender, WiMultiDialLeft) || ReferenceEquals(sender, WkMultiDialLeft);
            int on = ((OffOnToggle)sender).IsChecked == true ? 1 : 0;
            if (left) bundle.Left = on;
            else bundle.Right = on;
            int side = left ? 0 : 1;
            _plugin.HardwareApplier.WriteIfWheelDetected(left ? "wheel-multifn-left" : "wheel-multifn-right",
                HardwareApplier.MultiDialSideValue(side, on));
            _plugin.SaveSettings();
            using (_suppressor.Begin())
                RefreshMultiDial(true);
        }

        private void WiMultiDialLabel_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var bundle = _plugin.GetOrCreateActiveWheelMultiDial();
            if (bundle == null) return;
            // The copy being typed in is the source; the other one is re-rendered below.
            MultiDialView? src = null;
            foreach (var v in MultiDialViews)
                foreach (var (l, r) in v.Boxes)
                    if (ReferenceEquals(sender, l) || ReferenceEquals(sender, r)) src = v;
            if (src == null) return;
            var boxes = src.Boxes;
            int count = Math.Max(boxes.Count, bundle.Labels?.Count ?? 0);
            var next = new List<MultiDialLabel>(count);
            for (int i = 0; i < count; i++)
            {
                if (i < boxes.Count)
                    next.Add(new MultiDialLabel { Left = boxes[i].Left.Text, Right = boxes[i].Right.Text });
                else
                    next.Add(bundle.Labels![i]);
            }
            bundle.Labels = next;
            _plugin.SaveSettings();
            using (_suppressor.Begin())
                RefreshMultiDial(true);
            ScheduleMultiDialLabelPush();
        }

        // Typing resends the whole table; wait for a pause so one multi-chunk record
        // per edit goes out instead of one per keystroke.
        private void ScheduleMultiDialLabelPush()
        {
            if (_mdLabelPushTimer == null)
            {
                _mdLabelPushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _mdLabelPushTimer.Tick += (s, e) =>
                {
                    _mdLabelPushTimer!.Stop();
                    _plugin?.RequestMultiDialLabelPush();
                };
            }
            _mdLabelPushTimer.Stop();
            _mdLabelPushTimer.Start();
        }
    }
}
