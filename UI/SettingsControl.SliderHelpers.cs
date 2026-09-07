using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MozaPlugin.Devices;
using MozaPlugin.Resources;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.UI;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;
using SimHub.Plugins.OutputPlugins.EditorControls;
using SimHub.Plugins.OutputPlugins.GraphicalDash.Models;
using static MozaPlugin.UI.UiHelpers;
using SerialTrafficCapture = MozaPlugin.Diagnostics.SerialTrafficCapture;
using CaptureRedactor = MozaPlugin.Diagnostics.CaptureRedactor;

namespace MozaPlugin.UI
{
    public partial class SettingsControl : UserControl
    {

        // ===== Helpers =====

        // SetSliderPercent, SetSliderRaw, SetComboSafe, Clamp moved to UI/UiHelpers.

        private double ConvertTemp(int raw)
        {
            double celsius = raw / 100.0;
            return _data.UseFahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;
        }

        // ===== Generic slider-handler helpers =====

        // Most int-valued sliders share the same body: drop the event if a refresh
        // is mid-flight, round the new value, paint the label, commit it to the
        // data model + device, then queue a settings save. The per-slider commit
        // lambda captures which data field and which device command to use.
        private void OnIntSliderChanged(double newValue, TextBox label, string suffix,
            Action<int> commit)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(newValue);
            label.Text = $"{v}{suffix}";
            commit(v);
            _plugin.SaveSettings();
        }

        // Min/max pair sliders additionally clamp against the sibling bound and
        // bounce the slider back without re-firing this handler.
        private void OnMinMaxSliderChanged(double newValue, Slider self, int otherBound,
            bool isMin, TextBox label, Action<int> commit)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(newValue);
            if (isMin ? v > otherBound : v < otherBound)
            {
                v = otherBound;
                using (_suppressor.Begin()) self.Value = v;
            }
            label.Text = $"{v}%";
            commit(v);
            _plugin.SaveSettings();
        }

        // ===== Slider value box (keyed entry) =====

        // Enter inside KeyDown also moves focus off the box, which then fires
        // LostFocus — we'd commit the same edit twice. Track the most-recent
        // KeyDown-commit so the immediately-following LostFocus is a no-op.
        private TextBox? _suppressLostFocusFor;

        // GotFocus strips the unit suffix (e.g. "100%" → "100", "120 kph" →
        // "120") and selects the digits so the user can only edit the numeric
        // portion. The canonical "{value}{suffix}" form is restored by the
        // slider's ValueChanged handler on commit.
        private void SliderValueBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box) return;
            string raw = box.Text ?? string.Empty;
            string numeric = ExtractNumericPrefix(raw);
            if (numeric != raw) box.Text = numeric;
            box.SelectAll();
        }

        // Pressing Enter while focused on a SliderValueEditBox parses the
        // user's input and pushes it back to the paired slider — which then
        // fires its existing ValueChanged → On*SliderChanged → hardware-write
        // pipeline. Tag is bound (ElementName) to the matching Slider element.
        private void SliderValueBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return) return;
            var box = sender as TextBox;
            ApplyEditedSliderValue(box);
            _suppressLostFocusFor = box;
            // Move focus off so the user sees the canonical re-formatted text.
            box?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }

        private void SliderValueBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var box = sender as TextBox;
            if (box != null && box == _suppressLostFocusFor)
            {
                _suppressLostFocusFor = null;
                return;
            }
            ApplyEditedSliderValue(box);
        }

        private void ApplyEditedSliderValue(TextBox? box)
        {
            if (box == null || box.Tag is not Slider slider) return;

            // The boxes render in the current culture, so accept ',' as well as '.'.
            string token = ExtractNumericPrefix(box.Text ?? string.Empty).Replace(',', '.');
            bool parsed = double.TryParse(token, System.Globalization.NumberStyles.Float,
                                          System.Globalization.CultureInfo.InvariantCulture,
                                          out double parsedValue);

            // Target slider value:
            //   • Valid input → clamped parse result snapped to the slider's step:
            //     a TickFrequency below 1 marks a fractional slider (Deadzone 0.1 kg,
            //     G-force travel 0.5 mm); everything else snaps to whole numbers.
            //   • Empty / invalid input → keep the current slider value; the
            //     bump dance below still fires ValueChanged so the canonical
            //     text gets repainted.
            double step = slider.TickFrequency > 0 && slider.TickFrequency < 1 ? slider.TickFrequency : 1.0;
            double target = parsed
                ? Math.Round(Math.Round(Math.Max(slider.Minimum, Math.Min(slider.Maximum, parsedValue)) / step) * step, 6)
                : slider.Value;

            // ValueChanged is only raised when Value actually changes. If our
            // target matches the current value (same number re-typed or invalid
            // input), force a fire via a tiny bump-and-snap with the event
            // suppressor active for the bumped value — the snap-back assignment
            // then runs the handler and repaints the canonical text.
            if (slider.Value == target)
            {
                double offset = (target < slider.Maximum) ? target + 0.0001 : target - 0.0001;
                using (_suppressor.Begin()) slider.Value = offset;
            }
            slider.Value = target;
        }

        // Leading numeric token — accepts an optional sign and a single decimal
        // separator ('.' or the comma 8 of the shipped locales render), so
        // "120 kph", "100%", " -3.5°", "5,5 kg", "1100" all parse to the digit
        // portion. Empty string when no numeric prefix is present.
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
                if ((c == '.' || c == ',') && !sawDot) { sawDot = true; i++; continue; }
                break;
            }
            return (i > start) ? raw.Substring(start, i - start) : string.Empty;
        }

    }
}
