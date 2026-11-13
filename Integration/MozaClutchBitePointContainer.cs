using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using GameReaderCommon;
using GameReaderCommon.Enums;
using GameReaderCommon.Feedback;
using MozaPlugin.Devices.Haptics;
using MozaPlugin.Devices.MBooster;
using MozaPlugin.Resources;
using Newtonsoft.Json;
using SimHub.Plugins.DataPlugins.ShakeItV3.Calibration;
using SimHub.Plugins.DataPlugins.ShakeItV3.Effects;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers;
using SimHub.Plugins.DataPlugins.ShakeItV3.EffectsContainers.Attributes;
using SimHub.Plugins.DataPlugins.ShakeItV3.Filters;
using SimHub.Plugins.DataPlugins.ShakeItV3.Outputs.Audio;
using SimHub.Plugins.UI;

namespace MozaPlugin.Integration
{
    /// <summary>
    /// ShakeIt port of the mBooster tab's Bite Point
    /// (<c>MBoosterEffectWorker.UpdateBitePointRequest</c>): same trigger, waveform
    /// and 20 ms phase steps. The effect gain is Intensity and the effect tone is
    /// Frequency; the mBooster worker scales gain by the same 0.10 ceiling. Also
    /// offered to bass shakers and plain motors (OutputMode.Any).
    ///
    /// SimHub finds it by assembly scan and saves its type name as
    /// ContainerType — renaming the class orphans saved effects.
    /// </summary>
    [ShakeItContainerMetadata(100, "Clutch Bite Point",
        "Pulses as the clutch passes its bite point",
        "AZOM", OutputMode.Any)]
    public class MozaClutchBitePointContainer : EffectsContainerBase<SingleToneOutput>, IClutchBitePointSettings
    {
        private const int DefaultFrequencyHz = 40;
        private const int DefaultTriggerLevelPct = 50;
        private const int DefaultSmoothnessPct = 100;
        private const int DefaultIntensityPct = 50;

        private static readonly long StepTicks =
            (long)(MBoosterEffectWorker.TickPeriodSec * Stopwatch.Frequency);
        // A gap this long means ShakeIt stopped evaluating us (profile switch).
        private static readonly long StaleTicks = Stopwatch.Frequency / 2;

        // SimHub data thread only.
        private MBoosterBitePointTrigger _trigger;
        private bool _waveActive;
        private double _phaseRad;
        private long _waveStart;
        private long _stepsDone;
        private long _lastEvaluated;

        public MozaClutchBitePointContainer()
        {
            Gain = DefaultIntensityPct;
            ((MozaClutchBitePointEffect)Pulse).SetOwner(this);
        }

        /// <summary>Use the current game's calibrated bite point when one exists.</summary>
        public bool UseCalibratedBitePoint { get; set; } = true;

        /// <summary>Fallback bite point, clutch position 0..100.</summary>
        public int TriggerLevelPct { get; set; } = DefaultTriggerLevelPct;

        /// <summary>Clutch travel (%) either side of the bite point that triggers the pulse.</summary>
        public int WidthPct { get; set; } = MBoosterUiConstants.BitePointWidthDefaultPct;

        /// <summary>Shortest pulse, ms (see MBoosterBitePointTrigger).</summary>
        public int MinLengthMs { get; set; } = MBoosterUiConstants.BitePointMinLengthDefaultMs;

        /// <summary>Longest pulse, ms (see MBoosterBitePointTrigger).</summary>
        public int MaxLengthMs { get; set; } = MBoosterUiConstants.BitePointMaxLengthDefaultMs;

        /// <summary>Fire ahead of a fast clutch after a touch (see MBoosterBitePointTrigger).</summary>
        public bool Predict { get; set; }

        /// <summary>Ripple depth, 0..100 (see MBoosterEffectSynthesizer.SynthesizeBitePoint).</summary>
        public int SmoothnessPct { get; set; } = DefaultSmoothnessPct;

        /// <summary>
        /// Ripple rate, Hz. Before it existed the ripple played the tone's beat
        /// against 50 Hz; set it to that (MBoosterUiConstants.BitePointLegacyPulseRateHz,
        /// 10 for 40 Hz) for the old output.
        /// </summary>
        public int PulseRateHz { get; set; } = MBoosterUiConstants.BitePointPulseRateDefaultHz;

        /// <summary>Fire passing the bite point pressing, releasing or both.</summary>
        public MBoosterBitePointDirection Direction { get; set; } = MBoosterBitePointDirection.Both;

        [JsonIgnore]
        public EffectBase Pulse { get; } = new MozaClutchBitePointEffect();

        protected override IEnumerable<EffectBase> AvailableEffects
        {
            get { yield return Pulse; }
        }

        // No response curve: the waveform level passes straight through.
        public override FilterBase Filter => null!;

        // Motors: the plugin tab's gate, GameRunning alone (Evaluate applies it).
        // Shakers: ShakeIt's in-game gate, as its own Custom Effect does there.
        [JsonIgnore]
        public override bool IsAllowedOutOfGame => ParentProfile?.OutputMode != OutputMode.Shakers;

        public override void SetAudioDefaults(SingleToneOutput output)
        {
            output.Frequency = DefaultFrequencyHz;
            output.UsePrehemptiveMode = false;
        }

        public override void ProcessEffects(GameData data, CalibratedFeedbackData calibratedFeedback, bool parentEnabled)
        {
            if (!IsEnabled || !parentEnabled) ResetState();
            base.ProcessEffects(data, calibratedFeedback, parentEnabled);
        }

        /// <returns>Waveform level 0..1 for this ShakeIt frame.</returns>
        internal double Evaluate(GameData data)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastEvaluated != 0 && now - _lastEvaluated > StaleTicks) ResetState();
            _lastEvaluated = now;

            var plugin = MozaPlugin.Instance;
            if (!data.GameRunning || plugin == null || !plugin.TryGetBitePointClutchPosition(out double pos))
            {
                ResetState();
                return 0;
            }

            int calibrated = plugin.CurrentClutchBitePointPct;
            int triggerPct = UseCalibratedBitePoint && calibrated >= 0 ? calibrated : TriggerLevelPct;
            int widthPct = Math.Max(MBoosterUiConstants.BitePointWidthMinPct,
                Math.Min(MBoosterUiConstants.BitePointWidthMaxPct, WidthPct));
            int minMs = Math.Max(MBoosterUiConstants.BitePointMinLengthMinMs,
                Math.Min(MBoosterUiConstants.BitePointMinLengthMaxMs, MinLengthMs));
            int maxMs = Math.Max(MBoosterUiConstants.BitePointMaxLengthMinMs,
                Math.Min(MBoosterUiConstants.BitePointMaxLengthMaxMs, MaxLengthMs));
            if (!_trigger.Update(Clamp01(pos), Clamp01(triggerPct / 100.0), widthPct / 100.0,
                    minMs / 1000.0, maxMs / 1000.0, Direction, Predict, now))
            {
                _waveActive = false;
                return 0;
            }

            if (!_waveActive)
            {
                _waveActive = true;
                _phaseRad = 0;
                _waveStart = now;
                _stepsDone = 0;
            }
            // The worker advances one step before each sample, so step 1 is the first.
            long due = (now - _waveStart) / StepTicks + 1;
            if (due > _stepsDone)
            {
                int rateHz = Math.Max(MBoosterUiConstants.BitePointPulseRateMinHz,
                    Math.Min(MBoosterUiConstants.BitePointPulseRateMaxHz, PulseRateHz));
                _phaseRad = EngineVibrationMath.AdvancePhase(_phaseRad, rateHz,
                    MBoosterEffectWorker.TickPeriodSec * (due - _stepsDone));
                _stepsDone = due;
            }
            return MBoosterEffectSynthesizer.SynthesizeBitePoint(1.0, _phaseRad, Clamp01(SmoothnessPct / 100.0));
        }

        // ShakeIt stopped evaluating the effect (out of game on shakers).
        internal void OnIdle() => ResetState();

        private void ResetState()
        {
            _trigger.Reset();
            _waveActive = false;
        }

        protected override UserControl GetEditControl()
        {
            var predict = new CheckBox { Content = Strings.Label_BitePrediction, Margin = new Thickness(0, 0, 0, 6) };
            predict.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Predict)) { Mode = BindingMode.TwoWay });

            var panel = new StackPanel();
            panel.Children.Add(new MozaClutchBitePointCalibrationView(this));
            panel.Children.Add(EditorSections.Section(Strings.Section_ShakeItTrigger,
                DirectionPicker(),
                Slider(Strings.SliderLabel_BiteWidth, nameof(WidthPct),
                    MBoosterUiConstants.BitePointWidthMinPct, MBoosterUiConstants.BitePointWidthMaxPct),
                predict));
            panel.Children.Add(EditorSections.Section(Strings.Section_ShakeItPulse,
                Slider(Strings.SliderLabel_BiteMinLength, nameof(MinLengthMs),
                    MBoosterUiConstants.BitePointMinLengthMinMs, MBoosterUiConstants.BitePointMinLengthMaxMs, 10),
                Slider(Strings.SliderLabel_BiteMaxLength, nameof(MaxLengthMs),
                    MBoosterUiConstants.BitePointMaxLengthMinMs, MBoosterUiConstants.BitePointMaxLengthMaxMs, 50),
                Slider(Strings.SliderLabel_PulseRateHz, nameof(PulseRateHz),
                    MBoosterUiConstants.BitePointPulseRateMinHz, MBoosterUiConstants.BitePointPulseRateMaxHz),
                Slider(Strings.SliderLabel_Smoothness, nameof(SmoothnessPct), 0, 100)));
            return new UserControl { Content = panel, DataContext = this };
        }

        private FrameworkElement DirectionPicker()
        {
            var combo = new ComboBox { MinWidth = 140, Margin = new Thickness(8, 0, 0, 0) };
            combo.Items.Add(Strings.Option_BiteTriggerBoth);
            combo.Items.Add(Strings.Option_BiteTriggerPress);
            combo.Items.Add(Strings.Option_BiteTriggerRelease);
            combo.SelectedIndex = (int)Direction;
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedIndex >= 0) Direction = (MBoosterBitePointDirection)combo.SelectedIndex;
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = Strings.Label_BiteTriggerOn, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(combo);
            return row;
        }

        private static TitledSlider Slider(string title, string path, double min, double max, double step = 1)
        {
            var slider = new TitledSlider
            {
                Title = title,
                Minimum = min,
                Maximum = max,
                TickFrequency = step,
                IsSnapToTickEnabled = true,
            };
            slider.SetBinding(TitledSlider.ValueProperty, new Binding(path) { Mode = BindingMode.TwoWay });
            return slider;
        }

        private static double Clamp01(double v)
        {
            if (double.IsNaN(v) || v < 0) return 0;
            return v > 1 ? 1 : v;
        }
    }

    /// <summary>The single (mono) effect of <see cref="MozaClutchBitePointContainer"/>.</summary>
    public sealed class MozaClutchBitePointEffect : EffectBase
    {
        public MozaClutchBitePointEffect() : base(Strings.Section_ClutchBitePoint, FFBPlacement.All) { }

        // Needs no game data: the trigger is the pedal's own position.
        public override bool GetIsSupportedOnCurrentGame(FeedbackCapabilities caps) => true;

        public override void Idle()
        {
            base.Idle();
            _owner?.OnIdle();
        }

        private MozaClutchBitePointContainer? _owner;

        internal void SetOwner(MozaClutchBitePointContainer owner) => _owner = owner;

        public override double GetEffectValue(GameData data, CalibratedFeedbackData calibratedFeedback, EffectsContainerBase effectsContainer)
            => (effectsContainer as MozaClutchBitePointContainer)?.Evaluate(data) ?? 0;
    }
}
