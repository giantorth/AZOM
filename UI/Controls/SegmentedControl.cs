using System.Windows;
using System.Windows.Controls;

namespace MozaControls
{
    /// <summary>
    /// Horizontal segmented (radio-group) control. Subclasses <see cref="ListBox"/>
    /// so existing code that read <c>combo.SelectedIndex</c> / wired
    /// <c>SelectionChanged</c> can swap with minimal change.
    /// </summary>
    public class SegmentedControl : ListBox
    {
        static SegmentedControl()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(SegmentedControl),
                new FrameworkPropertyMetadata(typeof(SegmentedControl)));
        }

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(SegmentedControl),
                new PropertyMetadata(string.Empty));

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }
    }
}
