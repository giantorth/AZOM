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

        /// <summary>Write the canonical text into a slider's value box, but
        /// don't trample what the user is currently typing — focused boxes own
        /// their content until the user commits via Enter or LostFocus. The
        /// 500-ms refresh tick used to clobber keystrokes here without this
        /// guard.</summary>
        public static void SetValueText(TextBox box, string text)
        {
            if (box.IsKeyboardFocused) return;
            box.Text = text;
        }

        /// <summary>Set slider value (clamped) and paint a "%" label.</summary>
        public static void SetSliderPercent(Slider slider, TextBox label, double value, double min, double max)
        {
            slider.Value = Clamp(value, min, max);
            SetValueText(label, $"{value:F0}%");
        }

        /// <summary>Set slider value (clamped) and paint a label with optional suffix.</summary>
        public static void SetSliderRaw(Slider slider, TextBox label, int value, int min, int max, string suffix)
        {
            slider.Value = Clamp(value, min, max);
            SetValueText(label, $"{value}{suffix}");
        }

        /// <summary>
        /// True when <paramref name="text"/> begins with the literal prefix of a
        /// composite-format string (everything before its first <c>{</c>
        /// placeholder). Used by status-text state machines that previously did
        /// <c>Text.StartsWith("English literal")</c> — that broke once the status
        /// was localized, so we compare against the active culture's format prefix
        /// instead. Format strings with no placeholder compare against the whole
        /// string.
        /// </summary>
        public static bool StatusMatchesFormatPrefix(string text, string format)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(format)) return false;
            int brace = format.IndexOf('{');
            string prefix = brace >= 0 ? format.Substring(0, brace) : format;
            return prefix.Length > 0 && text.StartsWith(prefix, System.StringComparison.Ordinal);
        }
    }
}
