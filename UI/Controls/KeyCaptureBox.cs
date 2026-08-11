using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MozaPlugin.Devices.StalksTruckSim;
using MozaPlugin.Resources;

namespace MozaControls
{
    /// <summary>
    /// Click-to-arm key binder. Shows the layout-aware name of the bound key
    /// (<see cref="KeyCode"/>, 0 = not set); while armed, the next physical key
    /// press is captured. Escape or focus loss cancels. <see cref="KeyChanged"/>
    /// fires only on a real capture, never on programmatic KeyCode changes.
    /// </summary>
    public class KeyCaptureBox : Control
    {
        static KeyCaptureBox()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(KeyCaptureBox),
                new FrameworkPropertyMetadata(typeof(KeyCaptureBox)));
        }

        public KeyCaptureBox()
        {
            Focusable = true;
        }

        public static readonly DependencyProperty KeyCodeProperty =
            DependencyProperty.Register(nameof(KeyCode), typeof(int), typeof(KeyCaptureBox),
                new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    (d, _) => ((KeyCaptureBox)d).RefreshDisplayText()));

        /// <summary>Bound scan code (Set-1; extended keys carry 0xE0 in the high byte). 0 = not set.</summary>
        public int KeyCode
        {
            get => (int)GetValue(KeyCodeProperty);
            set => SetValue(KeyCodeProperty, value);
        }

        private static readonly DependencyPropertyKey IsCapturingKey =
            DependencyProperty.RegisterReadOnly(nameof(IsCapturing), typeof(bool), typeof(KeyCaptureBox),
                new PropertyMetadata(false, (d, _) => ((KeyCaptureBox)d).RefreshDisplayText()));
        public static readonly DependencyProperty IsCapturingProperty = IsCapturingKey.DependencyProperty;
        public bool IsCapturing => (bool)GetValue(IsCapturingProperty);

        private static readonly DependencyPropertyKey DisplayTextKey =
            DependencyProperty.RegisterReadOnly(nameof(DisplayText), typeof(string), typeof(KeyCaptureBox),
                new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty DisplayTextProperty = DisplayTextKey.DependencyProperty;
        public string DisplayText => (string)GetValue(DisplayTextProperty);

        public static readonly RoutedEvent KeyChangedEvent =
            EventManager.RegisterRoutedEvent(nameof(KeyChanged), RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(KeyCaptureBox));

        /// <summary>Raised when the user captures a different key.</summary>
        public event RoutedEventHandler KeyChanged
        {
            add => AddHandler(KeyChangedEvent, value);
            remove => RemoveHandler(KeyChangedEvent, value);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            RefreshDisplayText();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            SetValue(IsCapturingKey, true);
            e.Handled = true;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (!IsCapturing)
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    SetValue(IsCapturingKey, true);
                    e.Handled = true;
                }
                else base.OnPreviewKeyDown(e);
                return;
            }

            e.Handled = true;
            if (e.IsRepeat) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.None || key == Key.ImeProcessed || key == Key.DeadCharProcessed) return;
            if (key == Key.Escape) { SetValue(IsCapturingKey, false); return; }
            if (key == Key.LWin || key == Key.RWin) return;

            ushort code = KeyCodes.FromVirtualKey(KeyInterop.VirtualKeyFromKey(key));
            if (code == 0) return;
            SetValue(IsCapturingKey, false);
            if (code != KeyCode)
            {
                SetCurrentValue(KeyCodeProperty, (int)code);
                RaiseEvent(new RoutedEventArgs(KeyChangedEvent));
            }
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            if (IsCapturing) e.Handled = true;
            else base.OnPreviewKeyUp(e);
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            SetValue(IsCapturingKey, false);
            base.OnLostKeyboardFocus(e);
        }

        private void RefreshDisplayText()
        {
            string text = IsCapturing ? Strings.KeyCapture_Prompt
                : KeyCode == 0 ? Strings.KeyCapture_None
                : KeyCodes.DisplayName((ushort)KeyCode);
            SetValue(DisplayTextKey, text);
        }
    }
}
