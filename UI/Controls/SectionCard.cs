using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MozaControls
{
    public class SectionCard : HeaderedContentControl
    {
        static SectionCard()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(SectionCard),
                new FrameworkPropertyMetadata(typeof(SectionCard)));
        }

        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(SectionCard),
                new PropertyMetadata(null));

        public Geometry? Icon
        {
            get => (Geometry?)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(SectionCard),
                new PropertyMetadata(string.Empty));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public static readonly DependencyProperty SubtitleProperty =
            DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(SectionCard),
                new PropertyMetadata(string.Empty));

        public string Subtitle
        {
            get => (string)GetValue(SubtitleProperty);
            set => SetValue(SubtitleProperty, value);
        }

        public static readonly DependencyProperty AccentBrushProperty =
            DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(SectionCard),
                new PropertyMetadata(null));

        public Brush? AccentBrush
        {
            get => (Brush?)GetValue(AccentBrushProperty);
            set => SetValue(AccentBrushProperty, value);
        }

        // Opt-in per instance (default false) so the many existing SectionCard
        // usages across the app keep their current always-expanded look; only
        // callers that set this get the header chevron + click-to-collapse.
        public static readonly DependencyProperty IsCollapsibleProperty =
            DependencyProperty.Register(nameof(IsCollapsible), typeof(bool), typeof(SectionCard),
                new PropertyMetadata(false));

        public bool IsCollapsible
        {
            get => (bool)GetValue(IsCollapsibleProperty);
            set => SetValue(IsCollapsibleProperty, value);
        }

        public static readonly DependencyProperty IsExpandedProperty =
            DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(SectionCard),
                new PropertyMetadata(true));

        public bool IsExpanded
        {
            get => (bool)GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        private Border? _headerBorder;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (_headerBorder != null)
                _headerBorder.MouseLeftButtonUp -= OnHeaderClicked;
            _headerBorder = GetTemplateChild("PART_Header") as Border;
            if (_headerBorder != null)
                _headerBorder.MouseLeftButtonUp += OnHeaderClicked;
        }

        private void OnHeaderClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!IsCollapsible) return;
            IsExpanded = !IsExpanded;
        }
    }
}
