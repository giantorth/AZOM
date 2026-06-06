using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MozaPlugin.UI
{
    // Turns a value TextBox into an editable, slider-bound entry box — the same
    // UX the main settings page wires up by hand, packaged as an attached
    // behavior so device controls get it with a single XAML attribute:
    //
    //   <TextBox Style="{StaticResource SliderValueEditBox}"
    //            ui:SliderValueBox.Slider="{Binding ElementName=FooSlider}"/>
    //
    // Focus strips the unit suffix ("1000 ms" -> "1000") and selects the digits.
    // Enter or blur parses the leading numeric token, clamps + integer-snaps it,
    // and pushes it to the slider — whose existing ValueChanged handler repaints
    // the canonical "{value}{suffix}" text and drives the hardware write. If the
    // input is invalid or unchanged, the pre-edit text is restored.
    public static class SliderValueBox
    {
        public static readonly DependencyProperty SliderProperty =
            DependencyProperty.RegisterAttached(
                "Slider", typeof(Slider), typeof(SliderValueBox),
                new PropertyMetadata(null, OnSliderChanged));

        public static void SetSlider(DependencyObject o, Slider value) => o.SetValue(SliderProperty, value);
        public static Slider GetSlider(DependencyObject o) => (Slider)o.GetValue(SliderProperty);

        // Pre-edit text, stashed on focus so an invalid/unchanged entry can be
        // restored without the slider's ValueChanged having to fire.
        private static readonly DependencyProperty OriginalTextProperty =
            DependencyProperty.RegisterAttached(
                "OriginalText", typeof(string), typeof(SliderValueBox), new PropertyMetadata(null));

        private static void OnSliderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBox box) return;
            box.GotFocus -= OnGotFocus;
            box.KeyDown -= OnKeyDown;
            box.LostFocus -= OnLostFocus;
            if (e.NewValue is Slider)
            {
                box.GotFocus += OnGotFocus;
                box.KeyDown += OnKeyDown;
                box.LostFocus += OnLostFocus;
            }
        }

        private static void OnGotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box) return;
            string raw = box.Text ?? string.Empty;
            box.SetValue(OriginalTextProperty, raw);
            string numeric = ExtractNumericPrefix(raw);
            if (numeric != raw) box.Text = numeric;
            box.SelectAll();
        }

        private static void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return) return;
            if (sender is TextBox box)
            {
                Apply(box);
                // Drop focus so the canonical re-formatted text shows. This fires
                // LostFocus too, but the text is already canonical by then so the
                // second Apply is a no-op.
                box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
            e.Handled = true;
        }

        private static void OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box) Apply(box);
        }

        private static void Apply(TextBox box)
        {
            if (GetSlider(box) is not Slider slider) return;

            string token = ExtractNumericPrefix(box.Text ?? string.Empty);
            bool parsed = double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture,
                                          out double value);
            if (parsed)
            {
                double target = Math.Round(Math.Max(slider.Minimum, Math.Min(slider.Maximum, value)));
                if (slider.Value != target)
                {
                    // ValueChanged repaints the box with the canonical text and
                    // writes the hardware. Refresh the stash so the follow-up
                    // LostFocus (after Enter) doesn't clobber it.
                    slider.Value = target;
                    box.SetValue(OriginalTextProperty, box.Text);
                    return;
                }
            }
            // Unchanged or invalid input → restore the pre-edit canonical text.
            if (box.GetValue(OriginalTextProperty) is string orig) box.Text = orig;
        }

        // Leading numeric token — accepts an optional sign and a single decimal
        // point, so "120 kph", "100%", " -3.5", "1100" all yield the digit
        // portion. Empty string when there is no numeric prefix.
        private static string ExtractNumericPrefix(string raw)
        {
            int i = 0, n = raw.Length;
            while (i < n && char.IsWhiteSpace(raw[i])) i++;
            int start = i;
            if (i < n && (raw[i] == '-' || raw[i] == '+')) i++;
            bool sawDot = false;
            while (i < n)
            {
                char c = raw[i];
                if (char.IsDigit(c)) { i++; continue; }
                if (c == '.' && !sawDot) { sawDot = true; i++; continue; }
                break;
            }
            return (i > start) ? raw.Substring(start, i - start) : string.Empty;
        }
    }
}
