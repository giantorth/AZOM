using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MozaControls
{
    /// <summary>
    /// Attached support for the <c>MozaHeaderTabControl</c> template: a single-row
    /// tab strip that scrolls horizontally when the tabs overflow, plus a trailing
    /// slot on the right of the strip (device pages put their profile list there).
    /// </summary>
    public static class TabStrip
    {
        private const double EdgePad = 4;

        /// <summary>Content rendered at the right end of the tab strip.</summary>
        public static readonly DependencyProperty TrailingContentProperty =
            DependencyProperty.RegisterAttached(
                "TrailingContent", typeof(object), typeof(TabStrip), new PropertyMetadata(null));

        public static void SetTrailingContent(DependencyObject o, object value) => o.SetValue(TrailingContentProperty, value);
        public static object GetTrailingContent(DependencyObject o) => o.GetValue(TrailingContentProperty);

        /// <summary>Set on the template's tab ScrollViewer to wire arrows, wheel and selection tracking.</summary>
        public static readonly DependencyProperty IsScrollHostProperty =
            DependencyProperty.RegisterAttached(
                "IsScrollHost", typeof(bool), typeof(TabStrip), new PropertyMetadata(false, OnIsScrollHostChanged));

        public static void SetIsScrollHost(DependencyObject o, bool value) => o.SetValue(IsScrollHostProperty, value);
        public static bool GetIsScrollHost(DependencyObject o) => (bool)o.GetValue(IsScrollHostProperty);

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached(
                "State", typeof(Wiring), typeof(TabStrip), new PropertyMetadata(null));

        private static void OnIsScrollHostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer sv) return;
            sv.Loaded -= OnScrollHostLoaded;
            sv.Unloaded -= OnScrollHostUnloaded;
            (sv.GetValue(StateProperty) as Wiring)?.Detach();
            sv.ClearValue(StateProperty);
            if (e.NewValue is true)
            {
                sv.Loaded += OnScrollHostLoaded;
                sv.Unloaded += OnScrollHostUnloaded;
                if (sv.IsLoaded) OnScrollHostLoaded(sv, null);
            }
        }

        private static void OnScrollHostLoaded(object sender, RoutedEventArgs? e)
        {
            var sv = (ScrollViewer)sender;
            if (sv.GetValue(StateProperty) != null) return;
            if (sv.TemplatedParent is not TabControl tc) return;
            var w = new Wiring(sv, tc,
                tc.Template?.FindName("PART_TabScrollLeft", tc) as ButtonBase,
                tc.Template?.FindName("PART_TabScrollRight", tc) as ButtonBase);
            sv.SetValue(StateProperty, w);
            w.Attach();
        }

        private static void OnScrollHostUnloaded(object sender, RoutedEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            (sv.GetValue(StateProperty) as Wiring)?.Detach();
            sv.ClearValue(StateProperty);
        }

        private sealed class Wiring
        {
            private readonly ScrollViewer _sv;
            private readonly TabControl _tc;
            private readonly ButtonBase? _left;
            private readonly ButtonBase? _right;

            internal Wiring(ScrollViewer sv, TabControl tc, ButtonBase? left, ButtonBase? right)
            {
                _sv = sv; _tc = tc; _left = left; _right = right;
            }

            internal void Attach()
            {
                _sv.ScrollChanged += OnScrollChanged;
                _sv.PreviewMouseWheel += OnPreviewMouseWheel;
                _tc.SelectionChanged += OnSelectionChanged;
                if (_left != null) _left.Click += OnLeftClick;
                if (_right != null) _right.Click += OnRightClick;
                UpdateArrows();
                QueueRevealSelected();
            }

            internal void Detach()
            {
                _sv.ScrollChanged -= OnScrollChanged;
                _sv.PreviewMouseWheel -= OnPreviewMouseWheel;
                _tc.SelectionChanged -= OnSelectionChanged;
                if (_left != null) _left.Click -= OnLeftClick;
                if (_right != null) _right.Click -= OnRightClick;
            }

            private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
            {
                UpdateArrows();
                // Resize or tab visibility change: keep the active tab on screen.
                if (e.ViewportWidthChange != 0 || e.ExtentWidthChange != 0)
                    QueueRevealSelected();
            }

            private void UpdateArrows()
            {
                bool overflow = _sv.ScrollableWidth > 0.5;
                var vis = overflow ? Visibility.Visible : Visibility.Collapsed;
                if (_left != null)
                {
                    if (_left.Visibility != vis) _left.Visibility = vis;
                    _left.IsEnabled = _sv.HorizontalOffset > 0.5;
                }
                if (_right != null)
                {
                    if (_right.Visibility != vis) _right.Visibility = vis;
                    _right.IsEnabled = _sv.HorizontalOffset < _sv.ScrollableWidth - 0.5;
                }
            }

            private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
            {
                if (e.Handled) return;
                e.Handled = true;
                if (_sv.ScrollableWidth > 0.5)
                {
                    _sv.ScrollToHorizontalOffset(_sv.HorizontalOffset - e.Delta * 0.5);
                    return;
                }
                // Nothing to scroll: ScrollViewer would still swallow the wheel, so
                // hand it to the page's scroller instead.
                if (VisualTreeHelper.GetParent(_sv) is UIElement parent)
                {
                    parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                    {
                        RoutedEvent = UIElement.MouseWheelEvent,
                        Source = _sv,
                    });
                }
            }

            private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
            {
                // Bubbled SelectionChanged from combos/lists inside tab content.
                if (!ReferenceEquals(e.OriginalSource, _tc)) return;
                QueueRevealSelected();
            }

            private void QueueRevealSelected()
            {
                _sv.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RevealSelected));
            }

            // Scrolls the strip directly rather than BringIntoView, which would
            // also scroll SimHub's page scroller up to the strip on programmatic
            // selection changes.
            private void RevealSelected()
            {
                if (_tc.SelectedItem == null) return;
                if (_tc.ItemContainerGenerator.ContainerFromItem(_tc.SelectedItem) is not FrameworkElement item) return;
                if (!TryGetSpan(item, out double l, out double r)) return;
                ScrollToShow(l, r);
            }

            private void ScrollToShow(double l, double r)
            {
                double off = _sv.HorizontalOffset, vw = _sv.ViewportWidth;
                if (l - EdgePad < off) _sv.ScrollToHorizontalOffset(Math.Max(0, l - EdgePad));
                else if (r + EdgePad > off + vw) _sv.ScrollToHorizontalOffset(r + EdgePad - vw);
            }

            // Tab-granular steps: reveal the next partially/fully hidden tab.
            private void OnLeftClick(object sender, RoutedEventArgs e)
            {
                double off = _sv.HorizontalOffset;
                double target = -1;
                foreach (var item in VisibleTabs())
                {
                    if (!TryGetSpan(item, out double l, out _)) continue;
                    if (l < off - 1 && l > target) target = l;
                }
                _sv.ScrollToHorizontalOffset(target < 0 ? 0 : Math.Max(0, target - EdgePad));
            }

            private void OnRightClick(object sender, RoutedEventArgs e)
            {
                double edge = _sv.HorizontalOffset + _sv.ViewportWidth;
                double target = double.MaxValue;
                foreach (var item in VisibleTabs())
                {
                    if (!TryGetSpan(item, out _, out double r)) continue;
                    if (r > edge + 1 && r < target) target = r;
                }
                _sv.ScrollToHorizontalOffset(target == double.MaxValue
                    ? _sv.ScrollableWidth
                    : Math.Min(_sv.ScrollableWidth, target + EdgePad - _sv.ViewportWidth));
            }

            private System.Collections.Generic.IEnumerable<FrameworkElement> VisibleTabs()
            {
                foreach (var it in _tc.Items)
                {
                    if (_tc.ItemContainerGenerator.ContainerFromItem(it) is FrameworkElement fe
                        && fe.Visibility == Visibility.Visible)
                        yield return fe;
                }
            }

            // Item span in scroll-content coordinates.
            private bool TryGetSpan(FrameworkElement item, out double left, out double right)
            {
                left = right = 0;
                if (item.Visibility != Visibility.Visible || _sv.Content is not Visual host) return false;
                if (!item.IsDescendantOf(host)) return false;
                var p = item.TransformToAncestor(host).Transform(new Point(0, 0));
                left = p.X;
                right = p.X + item.ActualWidth;
                return true;
            }
        }
    }
}
