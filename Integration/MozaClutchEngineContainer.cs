using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    /// <summary>When <see cref="MozaClutchEngineContainer"/> counts the clutch as closed.</summary>
    public enum MozaClutchClosedMode
    {
        // Full with the clutch out of the bite zone, fading to nothing across it.
        Fade,
        // Whenever the pedal is pressed less than the bite point.
        BelowBitePoint,
        // Only with the pedal (nearly) fully released.
        Released,
        // Regardless of the clutch; no clutch source needed.
        Always,
    }

    /// <summary>
    /// Engine vibration played only while the clutch is closed, so the engine is
    /// felt through the pedal when it's connected to the drivetrain, or always if
    /// chosen. Clutch position and bite point are the ones Clutch Bite Point uses
    /// (MozaPlugin.MBoosterBitePoint.cs).
    ///
    /// RPM drives pitch and strength alike: the tone runs from Min frequency at
    /// 0 RPM to Max frequency at redline (SimHub's tone renderer plays a
    /// CustomEffect's TargetFrequency), strength from Idle strength to full; the
    /// effect gain scales it.
    ///
    /// An output plays one tone at a time, so roughness can't be layered over it;
    /// it modulates that tone instead. One signal swings the frequency by up to ±
    /// Frequency roughness and dips the strength by up to Intensity roughness: a
    /// sine at Roughness rate, blended toward per-frame random values by White
    /// noise. MOZA outputs take a new value every 20 ms, so the rate stops at 25 Hz.
    ///
    /// SimHub saves the class name as ContainerType — renaming it orphans
    /// saved effects.
    /// </summary>
    [ShakeItContainerMetadata(101, "Clutch Engine Vibration",
        "Engine vibration while the clutch is closed",
        "AZOM", OutputMode.Any)]
    public class MozaClutchEngineContainer : EffectsContainerBase<MozaFiringToneOutput>, IClutchBitePointSettings
    {
        private const int DefaultIntensityPct = 50;
        public const int FrequencyMinHz = 5, FrequencyMaxHz = 250;
        public const int MinFrequencyDefaultHz = 20, MaxFrequencyDefaultHz = 100;
        public const int IdleStrengthDefaultPct = 50;
        public const int RoughnessRateMinHz = 1, RoughnessRateMaxHz = 25, RoughnessRateDefaultHz = 12;
        public const int FrequencyRoughnessDefaultPct = 10;
        public const int IntensityRoughnessDefaultPct = 30;
        public const int WhiteNoiseDefaultPct = 50;
        // Below this RPM the engine counts as off.
        private const double EngineOffRpm = 100;
        // Pedal travel counted as fully released (Released mode).
        private const double ReleasedLevel = 0.05;
        // Extra travel on the way out of a closed state, so a pedal resting on the edge doesn't chatter.
        private const double Hysteresis = 0.01;

        // SimHub data thread only.
        private bool _closed;
        private long _lastRoughTicks;
        private double _roughPhaseRad;
        private readonly Random _random = new Random();

        public MozaClutchEngineContainer()
        {
            Gain = DefaultIntensityPct;
            Vibration.SetOwner(this);
        }

        public bool UseCalibratedBitePoint { get; set; } = true;

        public int TriggerLevelPct { get; set; } = 50;

        public MozaClutchClosedMode ClosedMode { get; set; } = MozaClutchClosedMode.Fade;

        /// <summary>Fade only: clutch travel (%) either side of the bite point the fade spans.</summary>
        public int WidthPct { get; set; } = MBoosterUiConstants.BitePointWidthDefaultPct;

        /// <summary>Tone at 0 RPM, rising linearly to <see cref="MaxFrequencyHz"/> at redline.</summary>
        public int MinFrequencyHz { get; set; } = MinFrequencyDefaultHz;

        /// <summary>Tone at redline.</summary>
        public int MaxFrequencyHz { get; set; } = MaxFrequencyDefaultHz;

        /// <summary>Strength (%) at 0 RPM, rising to 100 at redline.</summary>
        public int IdleStrengthPct { get; set; } = IdleStrengthDefaultPct;

        /// <summary>Rate of the roughness sine, Hz.</summary>
        public int RoughnessRateHz { get; set; } = RoughnessRateDefaultHz;

        /// <summary>Frequency swing (± % of the tone); 0 = steady pitch.</summary>
        public int FrequencyRoughnessPct { get; set; } = FrequencyRoughnessDefaultPct;

        /// <summary>Deepest strength dip (%); 0 = steady strength.</summary>
        public int IntensityRoughnessPct { get; set; } = IntensityRoughnessDefaultPct;

        /// <summary>Share (%) of the roughness signal that is random instead of the sine.</summary>
        public int WhiteNoisePct { get; set; } = WhiteNoiseDefaultPct;

        [JsonIgnore]
        public MozaClutchEngineEffect Vibration { get; } = new MozaClutchEngineEffect();

        protected override IEnumerable<EffectBase> AvailableEffects
        {
            get { yield return Vibration; }
        }

        // No response curve: the strength passes straight through.
        public override FilterBase Filter => null!;

        // Motors: GameRunning alone (Evaluate applies it). Shakers: ShakeIt's in-game gate.
        [JsonIgnore]
        public override bool IsAllowedOutOfGame => ParentProfile?.OutputMode != OutputMode.Shakers;

        public override void SetAudioDefaults(MozaFiringToneOutput output)
        {
            output.Frequency = FrequencyMinHz;
            output.UseHighFrequency = false;
            output.UseWhiteNoise = false;
            output.UsePrehemptiveMode = false;
        }

        public override void ProcessEffects(GameData data, CalibratedFeedbackData calibratedFeedback, bool parentEnabled)
        {
            if (!IsEnabled || !parentEnabled) _closed = false;
            base.ProcessEffects(data, calibratedFeedback, parentEnabled);
        }

        /// <param name="frequencyHz">Tone to play.</param>
        /// <returns>Strength 0..1 for this ShakeIt frame.</returns>
        internal double Evaluate(GameData data, out double frequencyHz)
        {
            double minHz = ClampFrequency(MinFrequencyHz);
            frequencyHz = minHz;
            var plugin = MozaPlugin.Instance;
            var nd = data?.NewData;
            bool always = ClosedMode == MozaClutchClosedMode.Always;
            double pos = 0;
            if (data == null || !data.GameRunning || nd == null || plugin == null
                || (!always && !plugin.TryGetBitePointClutchPosition(out pos)))
            {
                _closed = false;
                return 0;
            }
            double rpm = nd.Rpms;
            if (double.IsNaN(rpm) || rpm < EngineOffRpm) return 0;

            double closed = 1;
            if (!always)
            {
                int calibrated = plugin.CurrentClutchBitePointPct;
                double bite = Clamp01((UseCalibratedBitePoint && calibrated >= 0 ? calibrated : TriggerLevelPct) / 100.0);
                closed = ClosedFactor(Clamp01(pos), bite);
                if (closed <= 0) return 0;
            }

            double rpmFraction = EngineVibrationMath.RedlineFraction(rpm, nd.MaxRpm);
            double rough = RoughnessSignal();
            double toneHz = minHz + (ClampFrequency(MaxFrequencyHz) - minHz) * rpmFraction;
            frequencyHz = ClampFrequency(toneHz * (1 + Clamp01(FrequencyRoughnessPct / 100.0) * rough));

            double idle = Clamp01(IdleStrengthPct / 100.0);
            double strength = idle + (1 - idle) * rpmFraction;
            double intensity = 1 - Clamp01(IntensityRoughnessPct / 100.0) * (1 - rough) / 2;
            return strength * closed * intensity;
        }

        private static double ClampFrequency(double hz) => Math.Max(FrequencyMinHz, Math.Min(FrequencyMaxHz, hz));

        // -1..1 for this frame: the roughness sine blended toward a fresh random value by White noise.
        private double RoughnessSignal()
        {
            long now = Stopwatch.GetTimestamp();
            double dt = _lastRoughTicks != 0 ? (now - _lastRoughTicks) / (double)Stopwatch.Frequency : 0;
            _lastRoughTicks = now;
            int rateHz = Math.Max(RoughnessRateMinHz, Math.Min(RoughnessRateMaxHz, RoughnessRateHz));
            _roughPhaseRad = EngineVibrationMath.AdvancePhase(_roughPhaseRad, rateHz, dt);
            double noise = Clamp01(WhiteNoisePct / 100.0);
            return (1 - noise) * Math.Sin(_roughPhaseRad) + noise * (2 * _random.NextDouble() - 1);
        }

        // 1 with the clutch closed, 0 open; Fade ramps across the bite zone.
        private double ClosedFactor(double pos, double bite)
        {
            switch (ClosedMode)
            {
                case MozaClutchClosedMode.BelowBitePoint:
                    _closed = pos < (_closed ? bite + Hysteresis : bite);
                    return _closed ? 1 : 0;
                case MozaClutchClosedMode.Released:
                    _closed = pos <= (_closed ? ReleasedLevel + Hysteresis : ReleasedLevel);
                    return _closed ? 1 : 0;
                default:
                    double width = Math.Max(MBoosterUiConstants.BitePointWidthMinPct,
                        Math.Min(MBoosterUiConstants.BitePointWidthMaxPct, WidthPct)) / 100.0;
                    double top = MBoosterBitePointTrigger.ZoneTop(bite, width);
                    double bottom = MBoosterBitePointTrigger.ZoneBottom(bite, width);
                    if (top <= bottom) return pos < top ? 1 : 0;
                    return Clamp01((top - pos) / (top - bottom));
            }
        }

        // ShakeIt stopped evaluating the effect (out of game on shakers).
        internal void OnIdle() => _closed = false;

        protected override UserControl GetEditControl()
        {
            // Width only shapes the Fade ramp.
            var width = Slider(Strings.SliderLabel_BiteWidth, nameof(WidthPct),
                MBoosterUiConstants.BitePointWidthMinPct, MBoosterUiConstants.BitePointWidthMaxPct);
            // The bite point is unused when the effect always runs.
            var calibration = new MozaClutchBitePointCalibrationView(this);
            void ShowForMode()
            {
                width.Visibility = ClosedMode == MozaClutchClosedMode.Fade ? Visibility.Visible : Visibility.Collapsed;
                calibration.Visibility = ClosedMode == MozaClutchClosedMode.Always ? Visibility.Collapsed : Visibility.Visible;
            }
            ShowForMode();

            var panel = new StackPanel();
            panel.Children.Add(calibration);
            panel.Children.Add(EditorSections.Section(Strings.Label_ClutchClosed, ClosedModePicker(ShowForMode), width));
            panel.Children.Add(EditorSections.Section(Strings.Section_ShakeItEngine,
                Slider(Strings.SliderLabel_MinFrequencyHz, nameof(MinFrequencyHz), FrequencyMinHz, FrequencyMaxHz),
                Slider(Strings.SliderLabel_MaxFrequencyHz, nameof(MaxFrequencyHz), FrequencyMinHz, FrequencyMaxHz),
                Slider(Strings.SliderLabel_IdleStrength, nameof(IdleStrengthPct), 0, 100)));
            panel.Children.Add(EditorSections.Section(Strings.Section_ShakeItRoughness,
                Slider(Strings.SliderLabel_RoughnessRateHz, nameof(RoughnessRateHz), RoughnessRateMinHz, RoughnessRateMaxHz),
                Slider(Strings.SliderLabel_FrequencyRoughness, nameof(FrequencyRoughnessPct), 0, 100),
                Slider(Strings.SliderLabel_IntensityRoughness, nameof(IntensityRoughnessPct), 0, 100),
                Slider(Strings.SliderLabel_WhiteNoise, nameof(WhiteNoisePct), 0, 100)));
            return new UserControl { Content = panel, DataContext = this };
        }

        private FrameworkElement ClosedModePicker(Action changed)
        {
            var combo = new ComboBox { MinWidth = 160, Margin = new Thickness(8, 0, 0, 0) };
            combo.Items.Add(Strings.Option_ClutchClosedFade);
            combo.Items.Add(Strings.Option_ClutchClosedBelowBite);
            combo.Items.Add(Strings.Option_ClutchClosedReleased);
            combo.Items.Add(Strings.Option_ClutchClosedAlways);
            combo.SelectedIndex = (int)ClosedMode;
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedIndex < 0) return;
                ClosedMode = (MozaClutchClosedMode)combo.SelectedIndex;
                changed();
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = Strings.SliderLabel_Mode, VerticalAlignment = VerticalAlignment.Center });
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

    /// <summary>
    /// Tone output of <see cref="MozaClutchEngineContainer"/>: the effect sets the
    /// frequency (CustomEffect.TargetFrequency), so the stock frequency controls
    /// are hidden, as SimHub's own NoToneOutput hides them.
    /// </summary>
    public class MozaFiringToneOutput : ToneOutput
    {
        protected override UserControl GetEditControl() => null!;

        protected override UserControl GetTitleControl() => null!;
    }

    /// <summary>
    /// The single (mono) effect of <see cref="MozaClutchEngineContainer"/>. A
    /// CustomEffect only so SimHub's ToneRenderer plays its TargetFrequency; the
    /// formula members are unused.
    /// </summary>
    public sealed class MozaClutchEngineEffect : CustomEffect
    {
        public MozaClutchEngineEffect() : base(null!, Strings.Section_ClutchEngine, FFBPlacement.All, null!, null!) { }

        public override bool GetIsSupportedOnCurrentGame(FeedbackCapabilities caps) => true;

        public override void Idle()
        {
            base.Idle();
            _owner?.OnIdle();
        }

        private MozaClutchEngineContainer? _owner;

        internal void SetOwner(MozaClutchEngineContainer owner) => _owner = owner;

        public override double GetEffectValue(GameData data, CalibratedFeedbackData calibratedFeedback, EffectsContainerBase effectsContainer)
        {
            if (!(effectsContainer is MozaClutchEngineContainer owner))
            {
                TargetFrequency = null;
                return 0;
            }
            double value = owner.Evaluate(data, out double frequencyHz);
            TargetFrequency = frequencyHz;
            return value;
        }
    }
}
