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

namespace MozaPlugin.UI
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
        // matching the temperature history window so those two share a
        // horizontal timescale; the torque card that replaces this one is 2 min). ----
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

        // ---- Live-torque sparkline ----
        // Samples and the wire poll live on MozaPlugin (_plugin.TorqueHistory,
        // fed by a background timer) exactly like the temperature graph. This
        // class only renders a snapshot on the shared 500 ms refresh tick and
        // owns no timer of its own: the first cut sampled on the WPF dispatcher
        // and made the panel lag. Its window is 2 min, NOT the 5 min the two
        // graphs beside it show — 5 min at a property-useful sample rate is more
        // points than the card can resolve.
        private const double TorqueScaleFloorNm = 2.0;  // only used when the rating is unknown
        private double _torqueScaleNm;                  // last MaxValue pushed; avoids redundant sets

        // Temperature-graph history + session peaks live on MozaPlugin
        // (_plugin.TemperatureHistory), sampled by a plugin-lifetime background
        // timer so the graph shows its full window the moment this panel opens
        // rather than only accumulating while the panel is loaded. This class
        // just renders snapshots of that buffer; it owns no temp buffers itself.

        // ---- mBooster Effects card pedal-trace sparkline. Pushed from
        // UpdateMBoosterCurveMarkers, which already runs at 30 Hz (same
        // cadence as the curve editors' live position dot) — 150 samples ×
        // 1/30s = 5 seconds of rolling history. One buffer per pedal role
        // (Brake=red/In, Throttle=green/Out, Clutch=blue/Third) rather than
        // per selected device — whichever mBooster currently holds a given
        // role feeds that role's buffer every tick, so all three pedals are
        // visible together regardless of which device's tab is open. A role
        // with no assigned device just holds flat at 0. ----
        private const int PedalTraceSamples = 150;
        private readonly ObservableCollection<double> _mboosterBrakeTraceSamples = new ObservableCollection<double>();
        private readonly ObservableCollection<double> _mboosterThrottleTraceSamples = new ObservableCollection<double>();
        private readonly ObservableCollection<double> _mboosterClutchTraceSamples = new ObservableCollection<double>();
        // Pedals-tab live-input trace (throttle/brake/clutch) — the scrolling
        // graph that replaced the three progress bars. Its own buffers so it
        // scrolls whenever the Inputs tick runs, independent of the mBooster
        // trace's panel-gated feed.
        private readonly ObservableCollection<double> _pedalBrakeTraceSamples = new ObservableCollection<double>();
        private readonly ObservableCollection<double> _pedalThrottleTraceSamples = new ObservableCollection<double>();
        private readonly ObservableCollection<double> _pedalClutchTraceSamples = new ObservableCollection<double>();

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
                    MBoosterY4Slider, MBoosterY5Slider, MBoosterY6Slider
                });
                BindEditorXToSliders(MBoosterCurveEditor, new[]
                {
                    MBoosterX1Slider, MBoosterX2Slider, MBoosterX3Slider,
                    MBoosterX4Slider, MBoosterX5Slider, MBoosterX6Slider
                });
                BindEditorToSliders(MBoosterInputCurveEditor, new[]
                {
                    MBoosterInputY1Slider, MBoosterInputY2Slider, MBoosterInputY3Slider,
                    MBoosterInputY4Slider, MBoosterInputY5Slider, MBoosterInputY6Slider
                });
                BindEditorXToSliders(MBoosterInputCurveEditor, new[]
                {
                    MBoosterInputX1Slider, MBoosterInputX2Slider, MBoosterInputX3Slider,
                    MBoosterInputX4Slider, MBoosterInputX5Slider, MBoosterInputX6Slider
                });

                // Two-way bindings: CurveEditor.YN ↔ EqNSlider.Value (FFB EQ
                // uses the same line-graph control as the output curves,
                // configured via MozaEqualizerLineStyle / MozaEqualizerLineStyle10).
                BindEditorToSliders(FfbEqualizer, new[]
                {
                    Eq1Slider, Eq2Slider, Eq3Slider, Eq4Slider, Eq5Slider, Eq6Slider
                });
                // 10-band editor: node order is FREQUENCY order (5..100 Hz), which
                // interleaves the new-band sliders — Y10 = Eq6 = 100 Hz, the node
                // LastNodeYMax caps. Both editors stay bound; ApplyEqBandMode only
                // swaps Visibility.
                BindEditorToSliders(FfbEqualizer10, new[]
                {
                    Eq1Slider, Eq7Slider, Eq2Slider, Eq3Slider, Eq8Slider,
                    Eq4Slider, Eq9Slider, Eq5Slider, Eq10Slider, Eq6Slider
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

                // Temperature graph reads from the plugin-lifetime history buffer
                // (kept warm in the background). Seed it once now so the full
                // window is visible immediately on open; the refresh tick then
                // re-syncs it every 500 ms.
                UpdateTemperatureDisplays();

                // mBooster Effects card pedal trace: three series, fixed
                // 0-100% scale (MaxValue set in XAML) — In=Brake, Out=Throttle,
                // Third=Clutch.
                if (MBoosterPedalTraceViz != null)
                {
                    MBoosterPedalTraceViz.InSamples = _mboosterBrakeTraceSamples;
                    MBoosterPedalTraceViz.OutSamples = _mboosterThrottleTraceSamples;
                    MBoosterPedalTraceViz.ThirdSamples = _mboosterClutchTraceSamples;
                }
                // Pedals-tab live-input trace: In=Brake (red), Out=Throttle
                // (green), Third=Clutch (cyan) — same colour language as the bars
                // it replaced.
                if (PedalTraceViz != null)
                {
                    PedalTraceViz.InSamples = _pedalBrakeTraceSamples;
                    PedalTraceViz.OutSamples = _pedalThrottleTraceSamples;
                    PedalTraceViz.ThirdSamples = _pedalClutchTraceSamples;
                }
                for (int i = 0; i < PedalTraceSamples; i++)
                {
                    _mboosterBrakeTraceSamples.Add(0);
                    _mboosterThrottleTraceSamples.Add(0);
                    _mboosterClutchTraceSamples.Add(0);
                    _pedalBrakeTraceSamples.Add(0);
                    _pedalThrottleTraceSamples.Add(0);
                    _pedalClutchTraceSamples.Add(0);
                }

                // Started by OnLoadedStartTimers, not here: SimHub constructs this
                // control on every game switch and only loads it if the user opens
                // the page, and a running DispatcherTimer roots the control.
                _bandwidthTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _bandwidthTimer.Tick += OnBandwidthTick;

                // InitRedesignControls is NOT inside a suppressor scope, so
                // seeding SelectedIndex would fire the handler and write the
                // setting straight back. Suppress, then apply explicitly.
                using (_suppressor.Begin())
                    BaseGraphModeCombo.SelectedIndex = (int)_plugin.Settings.BaseTabGraph;
                ApplyBaseGraphMode();

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
        // corresponding slider's Value. Accepts 5 sliders (curve mode), 6
        // (legacy equalizer mode) or 10 (10-band equalizer).
        private void BindEditorToSliders(MozaControls.MozaCurveEditor editor, Slider[] sliders)
        {
            if (editor == null || sliders == null || sliders.Length < 5) return;
            var ys = new[] {
                MozaControls.MozaCurveEditor.Y1Property, MozaControls.MozaCurveEditor.Y2Property,
                MozaControls.MozaCurveEditor.Y3Property, MozaControls.MozaCurveEditor.Y4Property,
                MozaControls.MozaCurveEditor.Y5Property, MozaControls.MozaCurveEditor.Y6Property,
                MozaControls.MozaCurveEditor.Y7Property, MozaControls.MozaCurveEditor.Y8Property,
                MozaControls.MozaCurveEditor.Y9Property, MozaControls.MozaCurveEditor.Y10Property };
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
        // 6 sliders (mBooster Sim Input Mapping and Pedal Feel — all nodes
        // draggable) or 4 (the wheelbase FFB curve, whose last node is pinned
        // at input=100 via LockLastNodeX so X5 keeps its DP default).
        // Horizontal drag isn't offered on the 6-band EQ.
        private void BindEditorXToSliders(MozaControls.MozaCurveEditor editor, Slider[] sliders)
        {
            if (editor == null || sliders == null || sliders.Length < 4) return;
            var xs = new[] {
                MozaControls.MozaCurveEditor.X1Property, MozaControls.MozaCurveEditor.X2Property,
                MozaControls.MozaCurveEditor.X3Property, MozaControls.MozaCurveEditor.X4Property,
                MozaControls.MozaCurveEditor.X5Property, MozaControls.MozaCurveEditor.X6Property };
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
                UpdateTemperatureDisplays();
                UpdateTorqueDisplays();

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
                // Runs on the 500 ms settings refresh tick — a persistent fault
                // (a null visual during a rebuild, say) emitted 2 lines/s for as
                // long as the tab stayed open. Collapse repeats; a change in the
                // failure mode still comes through.
                MozaLog.DebugIfChanged("redesign-live-update",
                    $"[Redesign] live update failed: {ex.Message}");
            }
        }

        // Push the current legend readings + the background history window into
        // the temp graph. Called every refresh tick and once at init (so the
        // full window shows the instant the panel opens). The graph is fed fresh
        // display-unit arrays each call so a °C/°F toggle reflows the whole
        // window, not just samples taken after the toggle. Disconnected samples
        // render as 0 (the graph's no-data sentinel, filtered from auto-scale).
        private void UpdateTemperatureDisplays()
        {
            bool has = _data.IsBaseConnected;
            string unit = _data.UseFahrenheit ? "°F" : "°C";

            double mcu = has ? ConvertTemp(_data.McuTemp) : 0;
            double mosfet = has ? ConvertTemp(_data.MosfetTemp) : 0;
            double motor = has ? ConvertTemp(_data.MotorTemp) : 0;

            var hist = _plugin.TemperatureHistory?.Take();
            if (hist != null && TemperatureGraphViz != null)
            {
                TemperatureGraphViz.McuSamples    = ToDisplayTemps(hist.Mcu, hist.Connected);
                TemperatureGraphViz.MosfetSamples = ToDisplayTemps(hist.Mosfet, hist.Connected);
                TemperatureGraphViz.MotorSamples  = ToDisplayTemps(hist.Motor, hist.Connected);
            }

            RenderRankedTempLegend(mcu, mosfet, motor, has, unit,
                hist?.McuMaxRaw ?? -1, hist?.MosfetMaxRaw ?? -1, hist?.MotorMaxRaw ?? -1);
        }

        // Raw 100×°C history → display-unit doubles; disconnected samples become
        // 0 so the graph drops them to baseline instead of drawing a stale line.
        private double[] ToDisplayTemps(int[] raw, bool[] connected)
        {
            var outArr = new double[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                outArr[i] = connected[i] ? ConvertTemp(raw[i]) : 0.0;
            return outArr;
        }

        // Repopulate the 3 named temp-legend rows sorted top→bottom by current
        // reading. Per-component brushes stay stable (MCU=red, MOSFET=cyan,
        // Motor=green — match their graph line). Each row shows: dot, name,
        // "{cur} {unit}", "max {peak} {unit}".
        private static readonly string[] _emptyDash = { "—" };
        private void RenderRankedTempLegend(double mcu, double mosfet, double motor, bool has, string unit,
            int mcuMaxRaw, int mosfetMaxRaw, int motorMaxRaw)
        {
            if (TempLegendRow1 == null) return; // legacy XAML — nothing to do

            var red = (Brush?)TryFindResource("RedBrush") ?? Brushes.Red;
            var cyan = (Brush?)TryFindResource("CyanBrush") ?? Brushes.Cyan;
            var green = (Brush?)TryFindResource("GreenBrush") ?? Brushes.LimeGreen;

            var entries = new[]
            {
                (name: Strings.Brand_Mcu,    cur: mcu,    maxRaw: mcuMaxRaw,    brush: red),
                (name: Strings.Brand_Mosfet, cur: mosfet, maxRaw: mosfetMaxRaw, brush: cyan),
                (name: Strings.Brand_Motor,  cur: motor,  maxRaw: motorMaxRaw,  brush: green),
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

        // Under Wine the port key is the tty name; append the COM name Wine gave
        // it so the pill still matches what other tools show. Windows returns the
        // port name unchanged.
        private static string DescribePort(string? portName)
        {
            if (string.IsNullOrEmpty(portName)) return "—";
            string? com = global::MozaPlugin.Protocol.WineComNameResolver.ResolveComName(portName!);
            return com == null ? portName! : $"{portName} ({com})";
        }

        private void UpdateConnectionPill()
        {
            if (ConnectionPill == null) return;
            ConnectionPill.IsConnected = _data.IsConnected;
            ConnectionPill.PortName = DescribePort(_plugin.Connection?.LastPortName);
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

        // ===== Base-tab graph selector (Bandwidth | Torque) =====

        private void BaseGraphModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.BaseTabGraph = BaseGraphModeCombo.SelectedIndex == 1
                ? Settings.BaseGraphMode.Torque
                : Settings.BaseGraphMode.Bandwidth;
            _plugin.SaveSettings();
            ApplyBaseGraphMode();
        }

        /// <summary>Show the selected card. Purely visual: the torque sampler on
        /// the plugin runs at one rate regardless, so the history is already
        /// populated whichever card was last selected.</summary>
        private void ApplyBaseGraphMode()
        {
            bool torque = _plugin.Settings.BaseTabGraph == Settings.BaseGraphMode.Torque;

            if (BandwidthCard != null)
                BandwidthCard.Visibility = torque ? Visibility.Collapsed : Visibility.Visible;
            if (TorqueCard != null)
                TorqueCard.Visibility = torque ? Visibility.Visible : Visibility.Collapsed;
        }

        // Renders a snapshot of the plugin-side history. Called from the shared
        // 500 ms refresh tick, so this costs ONE geometry rebuild per half second
        // — the same as the temperature graph beside it. MaxValue is written only
        // when it actually changes, because that DP's callback also rebuilds.
        private void UpdateTorqueDisplays()
        {
            if (_plugin.Settings.BaseTabGraph != Settings.BaseGraphMode.Torque)
                return;

            var hist = _plugin.TorqueHistory;
            if (hist == null) return;

            bool connected = _data.IsBaseConnected;
            double nm = connected ? _data.LiveTorqueNm : 0.0;
            // Two different peaks by design. The legend shows the per-game-session
            // one users also get as AZOM.MaxTorque; the autoscale fallback below
            // uses the ring's own never-reset peak, because a ceiling that drops
            // back at every game start would rescale the trace mid-session.
            double peak = _data.LiveTorquePeakNm;

            // Ceiling is the base's RATED torque, so the trace shows real
            // headroom instead of being restretched every time a new peak
            // arrives. Auto-scale (floored) only when the rating isn't
            // established — see BaseModelInfo.RatedNm.
            double rated = Devices.BaseModelInfo.RatedNm(_data.BaseModelName);
            double scale = rated > 0
                ? rated
                : Math.Max(TorqueScaleFloorNm, Math.Ceiling(hist.PeakNm));

            if (TorqueGraphViz != null)
            {
                TorqueGraphViz.InSamples = hist.Take();
                if (Math.Abs(scale - _torqueScaleNm) > 0.001)
                {
                    TorqueGraphViz.MaxValue = scale;
                    _torqueScaleNm = scale;
                }
            }

            if (TorqueValueText != null)
                TorqueValueText.Text = connected ? $"{nm:F1} Nm" : "--";
            if (TorquePeakText != null)
                TorquePeakText.Text = peak > 0 ? $"max {peak:F1} Nm" : "";
            if (TorqueScaleText != null)
            {
                // Ratings aren't all whole numbers (R5 = 5.5), so only spend the
                // decimal where there is one.
                string scaleNm = scale == Math.Floor(scale) ? $"{scale:F0}" : $"{scale:F1}";
                TorqueScaleText.Text = rated > 0 ? $"{scaleNm} Nm" : $"{scaleNm} Nm (auto)";
            }
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
