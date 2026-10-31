using System.Windows;
using System.Windows.Controls.Primitives;

namespace MozaControls
{
    /// <summary>
    /// Segmented Off/On control that drops in where the existing UI used a
    /// <see cref="System.Windows.Controls.CheckBox"/>. Surfaces the same
    /// Checked/Unchecked/Click events so existing handlers keep firing.
    /// </summary>
    public class OffOnToggle : ToggleButton
    {
        static OffOnToggle()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(OffOnToggle),
                new FrameworkPropertyMetadata(typeof(OffOnToggle)));
        }
    }
}
