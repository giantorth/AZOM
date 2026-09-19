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
            double shown = Clamp(value, min, max);
            slider.Value = shown;
            SetValueText(label, $"{shown:F0}%");
        }

        /// <summary>Set slider value (clamped) and paint a label with optional suffix.</summary>
        public static void SetSliderRaw(Slider slider, TextBox label, int value, int min, int max, string suffix)
        {
            int shown = (int)Clamp(value, min, max);
            slider.Value = shown;
            SetValueText(label, $"{shown}{suffix}");
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

        /// <summary>
        /// Case-insensitive comparer that orders embedded digit runs by VALUE,
        /// so "Rally V2" sorts before "Rally V10" instead of after it. Dashboard
        /// names are overwhelmingly "&lt;car or series&gt; &lt;number&gt;", which
        /// plain string ordering shuffles in exactly the way people notice.
        /// Ties break on the ordinal comparison so the order is total and stable.
        /// </summary>
        public static readonly System.Collections.Generic.IComparer<string> NaturalNameComparer
            = new NaturalComparer();

        private sealed class NaturalComparer : System.Collections.Generic.IComparer<string>
        {
            public int Compare(string? a, string? b)
            {
                if (ReferenceEquals(a, b)) return 0;
                if (a == null) return -1;
                if (b == null) return 1;

                int i = 0, j = 0;
                while (i < a.Length && j < b.Length)
                {
                    bool da = char.IsDigit(a[i]), db = char.IsDigit(b[j]);
                    if (da && db)
                    {
                        // Compare the whole digit runs numerically. Leading zeros
                        // are skipped first so "007" and "7" compare equal here
                        // and fall through to the ordinal tiebreak below.
                        int si = i, sj = j;
                        while (si < a.Length && a[si] == '0') si++;
                        while (sj < b.Length && b[sj] == '0') sj++;
                        int ei = si; while (ei < a.Length && char.IsDigit(a[ei])) ei++;
                        int ej = sj; while (ej < b.Length && char.IsDigit(b[ej])) ej++;
                        int la = ei - si, lb = ej - sj;
                        if (la != lb) return la - lb;          // longer run = bigger number
                        for (int k = 0; k < la; k++)
                        {
                            int d = a[si + k] - b[sj + k];
                            if (d != 0) return d;
                        }
                        i = ei; j = ej;
                        continue;
                    }
                    if (da != db) return da ? -1 : 1;          // digits before letters
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
                if (i < a.Length) return 1;
                if (j < b.Length) return -1;
                return string.CompareOrdinal(a, b);
            }
        }
    }
}
