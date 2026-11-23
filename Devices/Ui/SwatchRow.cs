using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MozaPlugin.Devices.Ui
{
    /// <summary>
    /// A labelled row of clickable colour swatches with an LED number above each
    /// (a Grid, so every number sits over its own swatch column by construction).
    /// </summary>
    internal static class SwatchRow
    {
        /// <param name="owner">Element whose resources supply the styles.</param>
        /// <param name="tagFor">Swatch <c>Tag</c> per column (handed back to the click handler).</param>
        /// <param name="swatches">The swatches, by column.</param>
        public static Grid Build(FrameworkElement owner, string label, int count,
            Func<int, object> tagFor, MouseButtonEventHandler onClick, out Border[] swatches)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int c = 0; c < count; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var caption = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 12, 2),
                Style = owner.TryFindResource("LabelText") as Style,
            };
            Grid.SetRow(caption, 1);
            Grid.SetColumn(caption, 0);
            grid.Children.Add(caption);

            swatches = new Border[count];
            for (int c = 0; c < count; c++)
            {
                var num = new TextBlock
                {
                    Text = (c + 1).ToString(),
                    Width = 28,
                    TextAlignment = TextAlignment.Center,
                    Opacity = 0.6,
                    Margin = new Thickness(1, 0, 1, 2),
                };
                Grid.SetRow(num, 0);
                Grid.SetColumn(num, c + 1);
                grid.Children.Add(num);

                var swatch = new Border
                {
                    Width = 28,
                    Height = 24,
                    Margin = new Thickness(1, 2, 1, 2),
                    BorderBrush = owner.TryFindResource("BorderBrightBrush") as Brush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Black,
                    Tag = tagFor(c),
                };
                swatch.MouseLeftButtonUp += onClick;
                Grid.SetRow(swatch, 1);
                Grid.SetColumn(swatch, c + 1);
                grid.Children.Add(swatch);
                swatches[c] = swatch;
            }
            return grid;
        }
    }
}
