using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MozaPlugin.Resources;

namespace MozaPlugin.Devices.Ui
{
    /// <summary>
    /// Live "active buttons" line for a HID button array: 1-based indices pressed
    /// within the last second, held ones bold. Rebuilds the Inlines only when the
    /// visible content changes (callers tick at ~30 Hz).
    /// </summary>
    internal sealed class ActiveButtonsRenderer
    {
        private readonly TextBlock _target;
        private readonly DateTime[] _lastPressed;
        // "3,7*," — star = pressed; "none" = placeholder shown.
        private string _signature = "";

        public ActiveButtonsRenderer(TextBlock target, int maxButtons)
        {
            _target = target;
            _lastPressed = new DateTime[maxButtons];
        }

        public void ShowNone()
        {
            if (_signature == "none") return;
            _signature = "none";
            _target.Inlines.Clear();
            _target.Inlines.Add(new Run(Strings.Option_None));
        }

        public void Refresh(bool[] states, int count)
        {
            count = Math.Min(count, Math.Min(states.Length, _lastPressed.Length));
            if (count <= 0) { ShowNone(); return; }

            var now = DateTime.UtcNow;
            var sig = new StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                bool pressed = states[i];
                if (pressed) _lastPressed[i] = now;
                if ((now - _lastPressed[i]).TotalSeconds < 1.0)
                {
                    sig.Append(i + 1);
                    if (pressed) sig.Append('*');
                    sig.Append(',');
                }
            }
            if (sig.Length == 0) { ShowNone(); return; }
            string signature = sig.ToString();
            if (signature == _signature) return;
            _signature = signature;

            _target.Inlines.Clear();
            bool first = true;
            for (int i = 0; i < count; i++)
            {
                if ((now - _lastPressed[i]).TotalSeconds >= 1.0) continue;
                if (!first) _target.Inlines.Add(new Run(", "));
                first = false;
                var run = new Run((i + 1).ToString());
                if (states[i])
                {
                    run.FontWeight = FontWeights.Bold;
                    run.Foreground = Brushes.White;
                }
                _target.Inlines.Add(run);
            }
        }
    }
}
