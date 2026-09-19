using System;
using System.Windows;
using System.Windows.Media;
using MozaControls;

namespace MozaPlugin.UI
{
    public partial class ColorPickerDialog : Window
    {
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
            MozaPalette.SetSavedColor(Color.FromRgb(SelectedR, SelectedG, SelectedB));
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
