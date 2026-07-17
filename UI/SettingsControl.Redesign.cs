using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MozaPlugin.Resources;
using MozaPlugin.UI;
using SerialTrafficCapture = MozaPlugin.Diagnostics.SerialTrafficCapture;

namespace MozaPlugin
{
    // Partial-class continuation of SettingsControl that holds wiring for the
    // 2026-05 redesign (new top bar, status bar, SectionCard-wrapped sections,
    // SteeringArc / TempCell live header, MozaCurveEditor (5-node curves and
    // 6-band EQ), bandwidth sparklines, Full Diagnostic Report expander).
    // Lives in a separate file to keep the existing SettingsControl.xaml.cs
    // untouched.
    public partial class SettingsControl
    {
        // ---- Bandwidth sparkline state (600 samples = 5 min @ 500ms tick,
        // matches TemperatureSamples so both graphs on the Base tab share the
        // same horizontal timescale). ----
        private const int BandwidthSamples = 600;
        private readonly ObservableCollection<double> _bwInSamples = new ObservableCollection<double>();
        private readonly ObservableCollection<double> _bwOutSamples = new ObservableCollection<double>();
        private DispatcherTimer? _bandwidthTimer;
        private long _bwLastInBytes;
        private long _bwLastOutBytes;
        private DateTime _bwLastTick = DateTime.MinValue;
        private long _bwPeakIn;
        private long _bwPeakOut;
        private long _bwSessionIn;
        private long _bwSessionOut;

        // ---- Temperature-graph sample buffers. The main refresh timer
        // (RefreshBaseTab → UpdateRedesignLiveDisplays) pushes samples at
        // 500 ms; 600 samples × 0.5 s = 5 minutes of rolling history.
        // (Bandwidth sparkline keeps its shorter window; temps trend slowly
        // enough to justify a longer view.) ----
        private const int TemperatureSamples = 600;
        private readonly ObservableCollection<double> _mcuTempSamples = new ObservableCollection<double>();
        private readonly ObservableCollection<double> _mosfetTempSamples = new ObservableCollection<double>();
        private readonly ObservableCollection<double> _motorTempSamples = new ObservableCollection<double>();

        // Peak temps observed since plugin launch, stored as raw 100×°C ints
        // so unit toggling (°C↔°F) still displays correctly via ConvertTemp.
        // -1 sentinel = "never seen a live sample yet" → display blank.
        private int _mcuTempMaxRaw = -1;
        private int _mosfetTempMaxRaw = -1;
        private int _motorTempMaxRaw = -1;

        // ---- mBooster Effects card pedal-trace sparkline. Pushed from
        // UpdateMBoosterCurveMarkers, which already runs at 30 Hz (same
        // cadence as the curve editors' live position dot) — 150 samples ×
        // 1/30s = 5 seconds of rolling history. One buffer per pedal role
        // (Brake=red/In, Throttle=green/Out, Clutch=blue/Third) rather than
        // per selected device — whichever mBooster currently holds a given
        // role feeds that role's buffer every tick, so all three pedals are
        // visible together regardless of which device's tab is open. A role
        // with no assigned device just holds flat at 0. ----
        private const int MBoosterPedalTraceSamples = 150;
        private readonly ObservableCollection<double> _mboosterBrakeTraceSamples = new ObservableCollection<double>();
        private readonly ObservableCollection<double> _mboosterThrottleTraceSamples = new ObservableCollection<double>();
        private readonly ObservableCollection<double> _mboosterClutchTraceSamples = new ObservableCollection<double>();

        /// <summary>
        /// Called from the existing constructor after InitializeComponent runs.
        /// Wires the new controls' bindings + initial values. Safe to invoke
        /// even if a subset of the new controls aren't present (FindName guard).
        /// </summary>
        private void InitRedesignControls()
        {
            try
            {
                // Two-way bindings: CurveEditor.YN ↔ underlying slider.Value
                BindEditorToSliders(FfbCurveEditor, new[]
                {
                    FfbCurveY1Slider, FfbCurveY2Slider, FfbCurveY3Slider,
                    FfbCurveY4Slider, FfbCurveY5Slider
                });
                // X1..X4 draggable (point 5 pinned at input=100 via LockLastNodeX).
                BindEditorXToSliders(FfbCurveEditor, new[]
                {
                    FfbCurveX1Slider, FfbCurveX2Slider, FfbCurveX3Slider, FfbCurveX4Slider
                });
                BindEditorToSliders(HandbrakeCurveEditor, new[]
                {
                    HbY1Slider, HbY2Slider, HbY3Slider, HbY4Slider, HbY5Slider
                });
                BindEditorToSliders(ThrottleCurveEditor, new[]
                {
                    ThrottleY1Slider, ThrottleY2Slider, ThrottleY3Slider,
                    ThrottleY4Slider, ThrottleY5Slider
                });
                BindEditorToSliders(BrakeCurveEditor, new[]
                {
                    BrakeY1Slider, BrakeY2Slider, BrakeY3Slider,
                    BrakeY4Slider, BrakeY5Slider
                });
                BindEditorToSliders(ClutchCurveEditor, new[]
                {
                    ClutchY1Slider, ClutchY2Slider, ClutchY3Slider,
                    ClutchY4Slider, ClutchY5Slider
                });
                BindEditorToSliders(MBoosterCurveEditor, new[]
                {
                    MBoosterY1Slider, MBoosterY2Slider, MBoosterY3Slider,
                    MBoosterY4Slider, MBoosterY5Slider
                });
                BindEditorXToSliders(MBoosterCurveEditor, new[]
                {
                    MBoosterX1Slider, MBoosterX2Slider, MBoosterX3Slider,
                    MBoosterX4Slider, MBoosterX5Slider
                });
                BindEditorToSliders(MBoosterInputCurveEditor, new[]
                {
                    MBoosterInputY1Slider, MBoosterInputY2Slider, MBoosterInputY3Slider,
                    MBoosterInputY4Slider, MBoosterInputY5Slider
                });

                // Two-way bindings: CurveEditor.YN ↔ EqNSlider.Value (FFB EQ
                // now uses the same line-graph control as the output curves,
                // configured for 6 nodes / 0-400 range via MozaEqualizerLineStyle).
                BindEditorToSliders(FfbEqualizer, new[]
                {
                    Eq1Slider, Eq2Slider, Eq3Slider, Eq4Slider, Eq5Slider, Eq6Slider
                });

                // Bandwidth sparkline data sources — single dual-line control on
                // the Base tab now hosts both series (in=cyan, out=amber).
                if (BandwidthGraphViz != null)
                {
                    BandwidthGraphViz.InSamples = _bwInSamples;
                    BandwidthGraphViz.OutSamples = _bwOutSamples;
                    // Capacity readout mirrors the graph's saturation ceiling so
                    // the label and the chart's full-scale line agree.
                    if (BandwidthCapacityText != null && BandwidthGraphViz.MaxValue > 0)
                        BandwidthCapacityText.Text = FormatBytesPerSec(BandwidthGraphViz.MaxValue);
                }
                for (int i = 0; i < BandwidthSamples; i++)
                {
                    _bwInSamples.Add(0);
                    _bwOutSamples.Add(0);
                }

                // Temperature graph: prime the three rolling buffers + bind.
                if (TemperatureGraphViz != null)
                {
                    TemperatureGraphViz.McuSamples    = _mcuTempSamples;
                    TemperatureGraphViz.MosfetSamples = _mosfetTempSamples;
                    TemperatureGraphViz.MotorSamples  = _motorTempSamples;
                }
                for (int i = 0; i < TemperatureSamples; i++)
                {
                    _mcuTempSamples.Add(0);
                    _mosfetTempSamples.Add(0);
                    _motorTempSamples.Add(0);
                }

                // mBooster Effects card pedal trace: three series, fixed
                // 0-100% scale (MaxValue set in XAML) — In=Brake, Out=Throttle,
                // Third=Clutch.
                if (MBoosterPedalTraceViz != null)
                {
                    MBoosterPedalTraceViz.InSamples = _mboosterBrakeTraceSamples;
                    MBoosterPedalTraceViz.OutSamples = _mboosterThrottleTraceSamples;
                    MBoosterPedalTraceViz.ThirdSamples = _mboosterClutchTraceSamples;
                }
                for (int i = 0; i < MBoosterPedalTraceSamples; i++)
                {
                    _mboosterBrakeTraceSamples.Add(0);
                    _mboosterThrottleTraceSamples.Add(0);
                    _mboosterClutchTraceSamples.Add(0);
                }

                _bandwidthTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _bandwidthTimer.Tick += OnBandwidthTick;
                _bandwidthTimer.Start();

                // Wire the custom hue picker so every PaletteStrip's CUSTOM chip
                // opens the existing ColorPickerDialog. Set globally (static) but
                // each plugin init resets it — that's fine since the factory is
                // pure (no closures over instance state).
                MozaControls.PaletteStrip.CustomPickerFactory = (current) =>
                {
                    var dlg = new ColorPickerDialog(current.R, current.G, current.B)
                    {
                        Owner = System.Windows.Application.Current?.MainWindow,
                    };
                    if (dlg.ShowDialog() == true)
                        return System.Windows.Media.Color.FromRgb(dlg.SelectedR, dlg.SelectedG, dlg.SelectedB);
                    return null;
                };

                // Status bar removed — its content was placeholder strings.

                // About-tab version line — same source as the diagnostics dump.
                if (AboutVersionText != null)
                    AboutVersionText.Text = "v" + DiagnosticsTextBuilder.GetPluginVersion();

                // Update-notification banner + settings (About tab). Reads
                // persisted state from MozaPluginSettings populated by the
                // fire-and-forget check kicked off from MozaPlugin.Init().
                InitUpdateBannerControls();

                // Connection pill initial sync
                UpdateConnectionPill();

                // Pedal sub-selector starts on Throttle
                SelectPedalGroup("throttle");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[Redesign] init failed: {ex.Message}");
            }
        }

        // Two-way bind a MozaCurveEditor's YN dependency properties to the
        // corresponding slider's Value. Accepts 5 sliders (curve mode) or 6
        // (equalizer mode); the 6th slot binds to Y6Property.
        private void BindEditorToSliders(MozaControls.MozaCurveEditor editor, Slider[] sliders)
        {
            if (editor == null || sliders == null || sliders.Length < 5) return;
            var ys = new[] {
                MozaControls.MozaCurveEditor.Y1Property, MozaControls.MozaCurveEditor.Y2Property,
                MozaControls.MozaCurveEditor.Y3Property, MozaControls.MozaCurveEditor.Y4Property,
                MozaControls.MozaCurveEditor.Y5Property, MozaControls.MozaCurveEditor.Y6Property };
            int n = Math.Min(sliders.Length, ys.Length);
            for (int i = 0; i < n; i++)
            {
                var b = new Binding(nameof(Slider.Value))
                {
                    Source = sliders[i],
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                };
                BindingOperations.SetBinding(editor, ys[i], b);
            }
        }

        // Two-way bind a MozaCurveEditor's X dependency properties to sliders —
        // only meaningful when the editor has AllowHorizontalDrag="True". Accepts
        // 5 sliders (mBooster Sim Input Mapping — all nodes draggable) or 4 (the
        // wheelbase FFB curve, whose last node is pinned at input=100 via
        // LockLastNodeX so X5 keeps its DP default). No X6 — horizontal drag
        // isn't offered on the 6-band EQ.
        private void BindEditorXToSliders(MozaControls.MozaCurveEditor editor, Slider[] sliders)
        {
            if (editor == null || sliders == null || sliders.Length < 4) return;
            var xs = new[] {
                MozaControls.MozaCurveEditor.X1Property, MozaControls.MozaCurveEditor.X2Property,
                MozaControls.MozaCurveEditor.X3Property, MozaControls.MozaCurveEditor.X4Property,
                MozaControls.MozaCurveEditor.X5Property };
            int n = Math.Min(sliders.Length, xs.Length);
            for (int i = 0; i < n; i++)
            {
                var b = new Binding(nameof(Slider.Value))
                {
                    Source = sliders[i],
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                };
                BindingOperations.SetBinding(editor, xs[i], b);
            }
        }

        // Called from existing RefreshBaseTab — pushes new live-display values.
        private void UpdateRedesignLiveDisplays()
        {
            try
            {
                bool has = _data.IsBaseConnected;
                string unit = _data.UseFahrenheit ? "°F" : "°C";

                // Convert each raw temp to the display unit, push onto the
                // rolling buffer, and update the legend text. When the base is
                // disconnected the legend reads "—" but the buffer still slides
                // (zeros) so the graph trails off to baseline.
                double mcu = has ? ConvertTemp(_data.McuTemp) : 0;
                double mosfet = has ? ConvertTemp(_data.MosfetTemp) : 0;
                double motor = has ? ConvertTemp(_data.MotorTemp) : 0;

                PushTemperatureSample(_mcuTempSamples, mcu);
                PushTemperatureSample(_mosfetTempSamples, mosfet);
                PushTemperatureSample(_motorTempSamples, motor);

                // Track session-peak per sensor (raw value, so unit-toggle works).
                if (has)
                {
                    if (_data.McuTemp > _mcuTempMaxRaw) _mcuTempMaxRaw = _data.McuTemp;
                    if (_data.MosfetTemp > _mosfetTempMaxRaw) _mosfetTempMaxRaw = _data.MosfetTemp;
                    if (_data.MotorTemp > _motorTempMaxRaw) _motorTempMaxRaw = _data.MotorTemp;
                }

                RenderRankedTempLegend(mcu, mosfet, motor, has, unit);

                if (SteeringArcViz != null)
                {
                    // The arc's Angle is fed GetCurrentAngleDegrees(MaxAngle * 2),
                    // which halves the range internally and so returns
                    // ±_data.MaxAngle at full lock. The arc's MaxAngle must equal
                    // that same value for the dot to reach the arc endpoint at the
                    // configured rotation limit — doubling it filled only halfway.
                    double maxA = _data.MaxAngle > 0 ? _data.MaxAngle : 270;
                    SteeringArcViz.MaxAngle = maxA;
                }
                UpdateConnectionPill();
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Redesign] live update failed: {ex.Message}");
            }
        }

        private static void PushTemperatureSample(ObservableCollection<double> series, double value)
        {
            series.Add(value);
            while (series.Count > TemperatureSamples) series.RemoveAt(0);
        }

        // Repopulate the 3 named temp-legend rows sorted top→bottom by current
        // reading. Per-component brushes stay stable (MCU=red, MOSFET=cyan,
        // Motor=green — match their graph line). Each row shows: dot, name,
        // "{cur} {unit}", "max {peak} {unit}".
        private static readonly string[] _emptyDash = { "—" };
        private void RenderRankedTempLegend(double mcu, double mosfet, double motor, bool has, string unit)
        {
            if (TempLegendRow1 == null) return; // legacy XAML — nothing to do

            var red = (Brush?)TryFindResource("RedBrush") ?? Brushes.Red;
            var cyan = (Brush?)TryFindResource("CyanBrush") ?? Brushes.Cyan;
            var green = (Brush?)TryFindResource("GreenBrush") ?? Brushes.LimeGreen;

            var entries = new[]
            {
                (name: Strings.Brand_Mcu,    cur: mcu,    maxRaw: _mcuTempMaxRaw,    brush: red),
                (name: Strings.Brand_Mosfet, cur: mosfet, maxRaw: _mosfetTempMaxRaw, brush: cyan),
                (name: Strings.Brand_Motor,  cur: motor,  maxRaw: _motorTempMaxRaw,  brush: green),
            };
            // OrderByDescending is stable — components with equal temps (e.g. all
            // zero when disconnected) keep declaration order so rows don't jitter.
            var ranked = entries.OrderByDescending(e => e.cur).ToArray();

            var rows = new (Ellipse dot, TextBlock name, TextBlock value, TextBlock max)[]
            {
                (TempLegendDot1, TempLegendName1, TempLegendValue1, TempLegendMax1),
                (TempLegendDot2, TempLegendName2, TempLegendValue2, TempLegendMax2),
                (TempLegendDot3, TempLegendName3, TempLegendValue3, TempLegendMax3),
            };

            for (int i = 0; i < 3; i++)
            {
                var (dot, name, value, max) = rows[i];
                if (dot == null) continue;

                var e = ranked[i];
                dot.Fill = e.brush;
                name.Text = e.name;
                value.Text = has ? $"{e.cur:F0} {unit}" : "—";
                value.Foreground = e.brush;
                max.Text = e.maxRaw >= 0
                    ? $"max {ConvertTemp(e.maxRaw):F0} {unit}"
                    : "";
            }

            // Legacy hidden labels — kept written so any code reading them by
            // name still sees the live value. Cheap; collapses to no-op cost.
            if (McuTempLegend != null)    McuTempLegend.Text    = has ? $"{mcu:F0} {unit}" : "—";
            if (MosfetTempLegend != null) MosfetTempLegend.Text = has ? $"{mosfet:F0} {unit}" : "—";
            if (MotorTempLegend != null)  MotorTempLegend.Text  = has ? $"{motor:F0} {unit}" : "—";
        }

        // Called from existing OnSteeringAngleTick — every ~33ms.
        private void UpdateRedesignSteeringAngle(double degrees, bool valid)
        {
            if (SteeringArcViz == null) return;
            SteeringArcViz.Angle = valid ? degrees : 0;
        }

        private void UpdateConnectionPill()
        {
            if (ConnectionPill == null) return;
            ConnectionPill.IsConnected = _data.IsConnected;
            ConnectionPill.PortName = _plugin.Connection?.LastPortName ?? "—";
            if (!_data.IsConnected)
            {
                ConnectionPill.StatusText = global::MozaPlugin.Resources.Strings.Status_Disconnected;
            }
            else
            {
                // Widen the connected pill with telemetry phase so Recovery/Parked
                // are visible at a glance without opening the Diagnostics tab.
                var phase = _plugin.TelemetrySender?.Phase ?? global::MozaPlugin.Telemetry.PipelinePhase.Idle;
                string connected = global::MozaPlugin.Resources.Strings.Status_Connected;
                if (phase == global::MozaPlugin.Telemetry.PipelinePhase.Recovery)
                    ConnectionPill.StatusText = connected + " · " + global::MozaPlugin.Resources.Strings.Status_Recovering;
                else if (phase == global::MozaPlugin.Telemetry.PipelinePhase.Parked)
                    ConnectionPill.StatusText = connected + " · " + global::MozaPlugin.Resources.Strings.Status_Parked;
                else
                    ConnectionPill.StatusText = connected;
            }
        }

        private void OnBandwidthTick(object? sender, EventArgs e)
        {
            try
            {
                long inBytes = SerialTrafficCapture.Instance.TotalRxBytes;
                long outBytes = SerialTrafficCapture.Instance.TotalTxBytes;

                var now = DateTime.UtcNow;
                if (_bwLastTick != DateTime.MinValue)
                {
                    double elapsed = (now - _bwLastTick).TotalSeconds;
                    if (elapsed > 0)
                    {
                        long inDelta = Math.Max(0, inBytes - _bwLastInBytes);
                        long outDelta = Math.Max(0, outBytes - _bwLastOutBytes);
                        double inRate = inDelta / elapsed;
                        double outRate = outDelta / elapsed;

                        PushBandwidthSample(_bwInSamples, inRate);
                        PushBandwidthSample(_bwOutSamples, outRate);

                        _bwSessionIn += inDelta;
                        _bwSessionOut += outDelta;
                        if (inRate > _bwPeakIn) _bwPeakIn = (long)inRate;
                        if (outRate > _bwPeakOut) _bwPeakOut = (long)outRate;

                        BandwidthInValueText.Text = FormatBytesPerSec(inRate);
                        BandwidthOutValueText.Text = FormatBytesPerSec(outRate);
                        // Inline "max NN" suffix once any traffic has been seen.
                        BandwidthInPeakText.Text  = _bwPeakIn  > 0 ? $"max {FormatBytesPerSec(_bwPeakIn)}"  : "";
                        BandwidthOutPeakText.Text = _bwPeakOut > 0 ? $"max {FormatBytesPerSec(_bwPeakOut)}" : "";
                        BandwidthInSessionText.Text = FormatBytesTotal(_bwSessionIn);
                        BandwidthOutSessionText.Text = FormatBytesTotal(_bwSessionOut);
                    }
                }
                _bwLastInBytes = inBytes;
                _bwLastOutBytes = outBytes;
                _bwLastTick = now;
            }
            catch
            {
                // Bandwidth display is non-critical; swallow errors so the
                // timer continues ticking and other UI keeps refreshing.
            }
        }

        private static void PushBandwidthSample(ObservableCollection<double> series, double value)
        {
            series.Add(value);
            while (series.Count > BandwidthSamples) series.RemoveAt(0);
        }

        private static string FormatBytesPerSec(double bps)
        {
            if (bps < 1024) return $"{bps:F0} B/s";
            if (bps < 1024 * 1024) return $"{bps / 1024:F1} KB/s";
            return $"{bps / (1024.0 * 1024):F2} MB/s";
        }

        private static string FormatBytesTotal(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        // -------- Pedal sub-selector --------

        private void PedalSelector_Throttle_Click(object sender, RoutedEventArgs e) => SelectPedalGroup("throttle");
        private void PedalSelector_Brake_Click(object sender, RoutedEventArgs e) => SelectPedalGroup("brake");
        private void PedalSelector_Clutch_Click(object sender, RoutedEventArgs e) => SelectPedalGroup("clutch");

        private void SelectPedalGroup(string which)
        {
            if (ThrottlePedalGroup == null || BrakePedalGroup == null || ClutchPedalGroup == null) return;
            ThrottlePedalGroup.Visibility = which == "throttle" ? Visibility.Visible : Visibility.Collapsed;
            BrakePedalGroup.Visibility    = which == "brake"    ? Visibility.Visible : Visibility.Collapsed;
            ClutchPedalGroup.Visibility   = which == "clutch"   ? Visibility.Visible : Visibility.Collapsed;

            // Restyle the chips — selected one gets the primary cyan, others ghost
            var primary = (Style)FindResource("MozaButtonPrimary");
            var ghost   = (Style)FindResource("MozaButtonGhost");
            PedalSelectorThrottle.Style = which == "throttle" ? primary : ghost;
            PedalSelectorBrake.Style    = which == "brake"    ? primary : ghost;
            PedalSelectorClutch.Style   = which == "clutch"   ? primary : ghost;
        }

        // -------- Full Diagnostic Report expander handler --------

        private bool _fullDiagExpanded;
        private void FullDiagToggle_Click(object sender, RoutedEventArgs e)
        {
            _fullDiagExpanded = !_fullDiagExpanded;
            if (_fullDiagExpanded)
            {
                try
                {
                    var report = BuildDiagnosticsDump();
                    FullDiagReportBox.Text = report;
                    int lineCount = report.Split('\n').Length;
                    FullDiagSummaryText.Text = string.Format(
                        CultureInfo.CurrentCulture, Strings.Hint_FullDiagRendered, lineCount);
                }
                catch (Exception ex)
                {
                    FullDiagReportBox.Text = $"[error rendering diagnostic report]\n{ex}";
                    FullDiagSummaryText.Text = Strings.Hint_FullDiagRenderFailed;
                }
                FullDiagReportBox.Visibility = Visibility.Visible;
                FullDiagToggleButton.Content = Strings.Button_Collapse;
            }
            else
            {
                FullDiagReportBox.Visibility = Visibility.Collapsed;
                FullDiagToggleButton.Content = Strings.Button_Expand;
            }
        }

    }
}
