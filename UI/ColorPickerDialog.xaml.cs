using System;
using System.Windows;
using System.Windows.Media;

namespace MozaPlugin
{
    public partial class ColorPickerDialog : Window
    {
        private static byte s_lastR, s_lastG, s_lastB;
        private static bool s_hasLastColor;

        public byte SelectedR { get; private set; }
        public byte SelectedG { get; private set; }
        public byte SelectedB { get; private set; }

        public ColorPickerDialog(byte r, byte g, byte b)
        {
            InitializeComponent();
            RSlider.Value = r;
            GSlider.Value = g;
            BSlider.Value = b;
            Palette.SelectedColor = Color.FromRgb(r, g, b);
            Palette.ColorChanged += (_, c) =>
            {
                // Suppress the picker's recursive call back into Palette during slider updates.
                RSlider.Value = c.R;
                GSlider.Value = c.G;
                BSlider.Value = c.B;
            };
            UpdatePreview();

            if (s_hasLastColor)
            {
                LastColorSwatch.Background = new SolidColorBrush(Color.FromRgb(s_lastR, s_lastG, s_lastB));
                LastColorSwatch.Tag = $"{s_lastR},{s_lastG},{s_lastB}";
                LastColorPanel.Visibility = Visibility.Visible;
            }
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (RSlider == null) return;
            byte r = (byte)RSlider.Value;
            byte g = (byte)GSlider.Value;
            byte b = (byte)BSlider.Value;

            RValue.Text = r.ToString();
            GValue.Text = g.ToString();
            BValue.Text = b.ToString();

            ColorPreview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            SelectedR = (byte)RSlider.Value;
            SelectedG = (byte)GSlider.Value;
            SelectedB = (byte)BSlider.Value;
            s_lastR = SelectedR;
            s_lastG = SelectedG;
            s_lastB = SelectedB;
            s_hasLastColor = true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Preset_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!(sender is System.Windows.Controls.Border b)) return;
            var parts = ((string)b.Tag).Split(',');
            if (parts.Length != 3) return;
            if (!byte.TryParse(parts[0], out byte r)) return;
            if (!byte.TryParse(parts[1], out byte g)) return;
            if (!byte.TryParse(parts[2], out byte bl)) return;
            RSlider.Value = r;
            GSlider.Value = g;
            BSlider.Value = bl;
        }
    }
}
