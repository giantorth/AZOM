using System.Windows.Controls;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Shared static helpers used by the plugin-level <see cref="SettingsControl"/>
    /// and per-device settings codebehinds. Kept stateless so each can call them
    /// without holding back-references.
    /// </summary>
    internal static class UiHelpers
    {
        public static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public static void SetComboSafe(ComboBox combo, int index)
        {
            if (index >= 0 && index < combo.Items.Count)
                combo.SelectedIndex = index;
        }

        /// <summary>Overload for any <see cref="Selector"/> (covers ListBox-derived
        /// SegmentedControl + ComboBox in one call site). Clamps to valid range,
        /// no-op outside bounds — matches the ComboBox overload's semantics.</summary>
        public static void SetComboSafe(System.Windows.Controls.Primitives.Selector selector, int index)
        {
            if (index >= 0 && index < selector.Items.Count)
                selector.SelectedIndex = index;
        }

        /// <summary>Set slider value (clamped) and paint a "%" label.</summary>
        public static void SetSliderPercent(Slider slider, TextBlock label, double value, double min, double max)
        {
            slider.Value = Clamp(value, min, max);
            label.Text = $"{value:F0}%";
        }

        /// <summary>Set slider value (clamped) and paint a label with optional suffix.</summary>
        public static void SetSliderRaw(Slider slider, TextBlock label, int value, int min, int max, string suffix)
        {
            slider.Value = Clamp(value, min, max);
            label.Text = $"{value}{suffix}";
        }
    }
}
