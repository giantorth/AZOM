using System;
using System.Windows;
using System.Windows.Controls;

namespace MozaControls
{
    /// <summary>
    /// Header layout for <c>MozaHeaderTabControl</c>. Child 0 is the tab area,
    /// child 1 the trailing content. Both share one row while the tab area keeps at
    /// least <see cref="MinTabsWidth"/>; otherwise the trailing content moves to its
    /// own right-aligned row above the tabs, so the tabs stay on the bottom border.
    /// </summary>
    public class TabHeaderPanel : Panel
    {
        public static readonly DependencyProperty MinTabsWidthProperty =
            DependencyProperty.Register(nameof(MinTabsWidth), typeof(double), typeof(TabHeaderPanel),
                new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double MinTabsWidth
        {
            get => (double)GetValue(MinTabsWidthProperty);
            set => SetValue(MinTabsWidthProperty, value);
        }

        public static readonly DependencyProperty GapProperty =
            DependencyProperty.Register(nameof(Gap), typeof(double), typeof(TabHeaderPanel),
                new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>Horizontal gap between tabs and trailing content on a shared row.</summary>
        public double Gap
        {
            get => (double)GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        public static readonly DependencyProperty TrailingPaddingProperty =
            DependencyProperty.Register(nameof(TrailingPadding), typeof(double), typeof(TabHeaderPanel),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>Vertical padding above and below the trailing content.</summary>
        public double TrailingPadding
        {
            get => (double)GetValue(TrailingPaddingProperty);
            set => SetValue(TrailingPaddingProperty, value);
        }

        private bool _wrapped;

        private UIElement? Tabs => InternalChildren.Count > 0 ? InternalChildren[0] : null;
        private UIElement? Trailing => InternalChildren.Count > 1 ? InternalChildren[1] : null;

        private static bool HasContent(UIElement? e) =>
            e != null && e.DesiredSize.Width > 0 && e.DesiredSize.Height > 0;

        protected override Size MeasureOverride(Size available)
        {
            var tabs = Tabs;
            var trailing = Trailing;
            var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);

            trailing?.Measure(inf);
            bool hasTrailing = HasContent(trailing);
            double tw = hasTrailing ? trailing!.DesiredSize.Width : 0;
            double th = hasTrailing ? trailing!.DesiredSize.Height + 2 * TrailingPadding : 0;
            double gap = hasTrailing ? Gap : 0;

            bool tabsVisible = tabs != null && tabs.Visibility != Visibility.Collapsed;
            _wrapped = hasTrailing && tabsVisible
                       && !double.IsInfinity(available.Width)
                       && available.Width - tw - gap < MinTabsWidth;

            if (_wrapped)
            {
                tabs!.Measure(new Size(available.Width, double.PositiveInfinity));
                return new Size(Math.Max(tw, tabs.DesiredSize.Width), th + tabs.DesiredSize.Height);
            }

            double tabsAvail = Math.Max(0, available.Width - tw - gap);
            tabs?.Measure(new Size(tabsAvail, double.PositiveInfinity));
            double tabsW = tabs?.DesiredSize.Width ?? 0;
            double tabsH = tabs?.DesiredSize.Height ?? 0;
            return new Size(tabsW + gap + tw, Math.Max(tabsH, th));
        }

        protected override Size ArrangeOverride(Size final)
        {
            var tabs = Tabs;
            var trailing = Trailing;
            bool hasTrailing = HasContent(trailing);
            double tw = hasTrailing ? trailing!.DesiredSize.Width : 0;
            double pad = hasTrailing ? TrailingPadding : 0;
            double th = hasTrailing ? trailing!.DesiredSize.Height : 0;
            double gap = hasTrailing ? Gap : 0;

            if (_wrapped)
            {
                double tabsH = tabs!.DesiredSize.Height;
                trailing!.Arrange(new Rect(Math.Max(0, final.Width - tw), pad, Math.Min(tw, final.Width), th));
                tabs.Arrange(new Rect(0, th + 2 * pad, final.Width, tabsH));
                return final;
            }

            double tabsW = Math.Max(0, final.Width - tw - gap);
            double tabsHeight = tabs?.DesiredSize.Height ?? 0;
            tabs?.Arrange(new Rect(0, Math.Max(0, final.Height - tabsHeight), tabsW, tabsHeight));
            trailing?.Arrange(new Rect(final.Width - tw, Math.Max(0, (final.Height - th) / 2), tw, th));
            return final;
        }
    }
}
