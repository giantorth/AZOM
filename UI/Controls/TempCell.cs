using System;
using System.Windows;
using System.Windows.Controls;

namespace MozaControls
{
    /// <summary>
    /// Compact temperature meter card. Label + value + gradient bar + marker.
    /// Colour-codes the value text when crossing warn/danger thresholds.
    /// </summary>
    public class TempCell : Control
    {
        static TempCell()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(TempCell),
                new FrameworkPropertyMetadata(typeof(TempCell)));
        }

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(TempCell),
                new PropertyMetadata(string.Empty));
        public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(TempCell),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));
        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

        public static readonly DependencyProperty UnitProperty =
            DependencyProperty.Register(nameof(Unit), typeof(string), typeof(TempCell),
                new PropertyMetadata("°C"));
        public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

        public static readonly DependencyProperty MaxValueProperty =
            DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(TempCell),
                new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));
        public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }

        public static readonly DependencyProperty WarnThresholdProperty =
            DependencyProperty.Register(nameof(WarnThreshold), typeof(double), typeof(TempCell),
                new FrameworkPropertyMetadata(65.0, FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));
        public double WarnThreshold { get => (double)GetValue(WarnThresholdProperty); set => SetValue(WarnThresholdProperty, value); }

        public static readonly DependencyProperty DangerThresholdProperty =
            DependencyProperty.Register(nameof(DangerThreshold), typeof(double), typeof(TempCell),
                new FrameworkPropertyMetadata(85.0, FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));
        public double DangerThreshold { get => (double)GetValue(DangerThresholdProperty); set => SetValue(DangerThresholdProperty, value); }

        public static readonly DependencyProperty HasValueProperty =
            DependencyProperty.Register(nameof(HasValue), typeof(bool), typeof(TempCell),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));
        public bool HasValue { get => (bool)GetValue(HasValueProperty); set => SetValue(HasValueProperty, value); }

        // Read-only state surfaced to the template
        private static readonly DependencyPropertyKey StateKey =
            DependencyProperty.RegisterReadOnly(nameof(State), typeof(string), typeof(TempCell),
                new PropertyMetadata("normal"));
        public static readonly DependencyProperty StateProperty = StateKey.DependencyProperty;
        public string State => (string)GetValue(StateProperty);

        private static readonly DependencyPropertyKey MarkerPercentKey =
            DependencyProperty.RegisterReadOnly(nameof(MarkerPercent), typeof(double), typeof(TempCell),
                new PropertyMetadata(0.0));
        public static readonly DependencyProperty MarkerPercentProperty = MarkerPercentKey.DependencyProperty;
        public double MarkerPercent => (double)GetValue(MarkerPercentProperty);

        private static readonly DependencyPropertyKey ValueTextKey =
            DependencyProperty.RegisterReadOnly(nameof(ValueText), typeof(string), typeof(TempCell),
                new PropertyMetadata("—"));
        public static readonly DependencyProperty ValueTextProperty = ValueTextKey.DependencyProperty;
        public string ValueText => (string)GetValue(ValueTextProperty);

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((TempCell)d).Recompute();

        private void Recompute()
        {
            double max = Math.Max(1, MaxValue);
            double pct = Math.Max(0, Math.Min(100, Value / max * 100));
            SetValue(MarkerPercentKey, pct);

            string state = Value >= DangerThreshold ? "danger"
                         : Value >= WarnThreshold ? "warn"
                         : "normal";
            SetValue(StateKey, state);
            SetValue(ValueTextKey, HasValue ? $"{Math.Round(Value)}" : "—");
        }
    }
}
