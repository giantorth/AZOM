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
    /// Engine vibration at the firing frequency (RPM / 60 × cylinders / 2),
    /// played only while the clutch is closed, so the engine is felt through
    /// the pedal when it's connected to the drivetrain, or always if chosen. Clutch position and bite
    /// point are the ones Clutch Bite Point uses (MozaPlugin.MBoosterBitePoint.cs).
    /// Strength rises from Idle strength at 0 RPM to full at redline; the effect
    /// gain scales it. The tone follows the firing frequency exactly: SimHub's
    /// tone renderer plays a CustomEffect's TargetFrequency.
    ///
    /// On top of the tone, strength is shaped once per engine cycle (RPM / 120):
    /// Lope pulses it, and below 100 % Smoothness each cycle gets a random
    /// strength. Outputs only take a new strength each frame, so the lope fades
    /// out before it would alias into buzz, and roughness never varies faster
    /// than that limit.
    ///
    /// SimHub saves the class name as ContainerType — renaming it orphans
    /// saved effects.
    /// </summary>
    [ShakeItContainerMetadata(101, "Clutch Engine Vibration",
        "Engine vibration at the firing frequency while the clutch is closed",
        "AZOM", OutputMode.Any)]
    public class MozaClutchEngineContainer : EffectsContainerBase<MozaFiringToneOutput>, IClutchBitePointSettings
    {
        private const int DefaultIntensityPct = 50;
        public const int CylindersMin = 1, CylindersMax = 16, CylindersDefault = 4;
        public const int MaxFrequencyMinHz = 20, MaxFrequencyMaxHz = 250, MaxFrequencyDefaultHz = 100;
        public const int IdleStrengthDefaultPct = 50;
        // Lowest tone played, and below this RPM the engine counts as off.
        private const double MinFrequencyHz = 5;
        private const double EngineOffRpm = 100;
        // Pedal travel counted as fully released (Released mode).
        private const double ReleasedLevel = 0.05;
        // Extra travel on the way out of a closed state, so a pedal resting on the edge doesn't chatter.
        private const double Hysteresis = 0.01;
        public const int LopeDefaultPct = 0;
        public const int SmoothnessDefaultPct = 100;
        // Fastest cycle-rate shaping: the mBooster takes a new strength every
        // 20 ms, and four frames per cycle still reads as a pulse.
        private const double ShapeMaxHz = 12.5;
        // Lope is at full depth up to this fraction of the limit, gone at it.
        private const double LopeFadeStart = 0.6;
        // Frames per cycle needed at the measured ShakeIt frame rate.
        private const double FramesPerShapeCycle = 4;
        // A longer gap between frames is a pause, not elapsed engine time.
        private const double MaxFrameSec = 0.1;
        // Weight of each new frame interval in the frame-rate average.
        private const double FrameAverageWeight = 0.05;

        // SimHub data thread only.
        private bool _closed;
        private long _lastFrameTicks;
        // Average frame interval, 0 until measured.
        private double _frameSec;
        // Position within the current shaping cycle, 0..1.
        private double _cyclePhase;
        // Random strength dips (0..1) of the previous and current cycle.
        private double _roughFrom, _roughTo;
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

        public int Cylinders { get; set; } = CylindersDefault;

        /// <summary>The firing frequency is capped here.</summary>
        public int MaxFrequencyHz { get; set; } = MaxFrequencyDefaultHz;

        /// <summary>Strength (%) at 0 RPM, rising to 100 at redline.</summary>
        public int IdleStrengthPct { get; set; } = IdleStrengthDefaultPct;

        /// <summary>Depth (%) of the once-per-cycle pulse; 0 = steady.</summary>
        public int LopePct { get; set; } = LopeDefaultPct;

        /// <summary>100 = every cycle the same; lower = random cycle-to-cycle strength.</summary>
        public int SmoothnessPct { get; set; } = SmoothnessDefaultPct;

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
            output.Frequency = (int)MinFrequencyHz;
            output.UseHighFrequency = false;
            output.UseWhiteNoise = false;
            output.UsePrehemptiveMode = false;
        }

        public override void ProcessEffects(GameData data, CalibratedFeedbackData calibratedFeedback, bool parentEnabled)
        {
            if (!IsEnabled || !parentEnabled) _closed = false;
            base.ProcessEffects(data, calibratedFeedback, parentEnabled);
        }

        /// <param name="frequencyHz">Firing frequency to play.</param>
        /// <returns>Strength 0..1 for this ShakeIt frame.</returns>
        internal double Evaluate(GameData data, out double frequencyHz)
        {
            double dt = FrameSeconds();
            frequencyHz = MinFrequencyHz;
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

            int cylinders = Math.Max(CylindersMin, Math.Min(CylindersMax, Cylinders));
            double maxHz = Math.Max(MaxFrequencyMinHz, Math.Min(MaxFrequencyMaxHz, MaxFrequencyHz));
            frequencyHz = Math.Max(MinFrequencyHz, Math.Min(maxHz, rpm / 60.0 * cylinders / 2.0));

            double idle = Clamp01(IdleStrengthPct / 100.0);
            double strength = idle + (1 - idle) * EngineVibrationMath.RedlineFraction(rpm, nd.MaxRpm);
            return strength * closed * CycleShape(rpm, dt);
        }

        // Seconds since the previous frame; 0 on the first one and after a pause.
        private double FrameSeconds()
        {
            long now = Stopwatch.GetTimestamp();
            long last = _lastFrameTicks;
            _lastFrameTicks = now;
            if (last == 0) return 0;
            double dt = (now - last) / (double)Stopwatch.Frequency;
            if (dt <= 0 || dt > MaxFrameSec) return 0;
            _frameSec = _frameSec > 0 ? _frameSec + FrameAverageWeight * (dt - _frameSec) : dt;
            return dt;
        }

        // Strength factor (0..1) from Lope and Smoothness for this frame.
        private double CycleShape(double rpm, double dt)
        {
            double maxHz = _frameSec > 0 ? Math.Min(ShapeMaxHz, 1 / (_frameSec * FramesPerShapeCycle)) : ShapeMaxHz;
            double cycleHz = rpm / 120.0;
            _cyclePhase += Math.Min(cycleHz, maxHz) * dt;
            if (_cyclePhase >= 1)
            {
                _cyclePhase -= Math.Floor(_cyclePhase);
                _roughFrom = _roughTo;
                _roughTo = _random.NextDouble();
            }

            double lope = Clamp01(LopePct / 100.0) * (1 - SmoothStep(LopeFadeStart * maxHz, maxHz, cycleHz));
            double pulse = 0.5 - 0.5 * Math.Cos(2 * Math.PI * _cyclePhase);
            double rough = 1 - Clamp01(SmoothnessPct / 100.0);
            double dip = _roughFrom + (_roughTo - _roughFrom) * SmoothStep(0, 1, _cyclePhase);
            return (1 - lope * (1 - pulse)) * (1 - rough * dip);
        }

        private static double SmoothStep(double edge0, double edge1, double x)
        {
            double t = Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3 - 2 * t);
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
                Slider(Strings.SliderLabel_Cylinders, nameof(Cylinders), CylindersMin, CylindersMax),
                Slider(Strings.SliderLabel_MaxFrequencyHz, nameof(MaxFrequencyHz), MaxFrequencyMinHz, MaxFrequencyMaxHz, 5),
                Slider(Strings.SliderLabel_IdleStrength, nameof(IdleStrengthPct), 0, 100),
                Slider(Strings.SliderLabel_Lope, nameof(LopePct), 0, 100),
                Slider(Strings.SliderLabel_Smoothness, nameof(SmoothnessPct), 0, 100)));
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
