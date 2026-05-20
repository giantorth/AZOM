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
    }
}
