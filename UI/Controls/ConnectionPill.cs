using System.Windows;
using System.Windows.Controls;

namespace MozaControls
{
    /// <summary>
    /// Top-bar connection indicator: pulsing green dot + COM port + status text.
    /// Templated visual; data is pushed by SettingsControl code-behind into
    /// <see cref="IsConnected"/>, <see cref="PortName"/>, <see cref="StatusText"/>.
    /// </summary>
    public class ConnectionPill : Control
    {
        static ConnectionPill()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(ConnectionPill),
                new FrameworkPropertyMetadata(typeof(ConnectionPill)));
        }

        public static readonly DependencyProperty IsConnectedProperty =
            DependencyProperty.Register(nameof(IsConnected), typeof(bool), typeof(ConnectionPill),
                new PropertyMetadata(false));
        public bool IsConnected { get => (bool)GetValue(IsConnectedProperty); set => SetValue(IsConnectedProperty, value); }

        public static readonly DependencyProperty PortNameProperty =
            DependencyProperty.Register(nameof(PortName), typeof(string), typeof(ConnectionPill),
                new PropertyMetadata("—"));
        public string PortName { get => (string)GetValue(PortNameProperty); set => SetValue(PortNameProperty, value); }

        public static readonly DependencyProperty StatusTextProperty =
            DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(ConnectionPill),
                new PropertyMetadata("Disconnected"));
        public string StatusText { get => (string)GetValue(StatusTextProperty); set => SetValue(StatusTextProperty, value); }
    }
}
