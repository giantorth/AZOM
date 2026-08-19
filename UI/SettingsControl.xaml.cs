using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MozaPlugin.Devices;
using MozaPlugin.Resources;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.UI;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.Dash.TemplatingCommon;
using SimHub.Plugins.OutputPlugins.EditorControls;
using SimHub.Plugins.OutputPlugins.GraphicalDash.Models;
using static MozaPlugin.UI.UiHelpers;
using SerialTrafficCapture = MozaPlugin.Diagnostics.SerialTrafficCapture;
using CaptureRedactor = MozaPlugin.Diagnostics.CaptureRedactor;

namespace MozaPlugin
{
    public partial class SettingsControl : UserControl
    {
        // Static reference so per-device controls (e.g. MozaWheelSettingsControl's
        // Inputs sub-tab) can forward user input back to the existing plugin-pane
        // handlers + settings persistence path. Cleared in OnUnloadedStopTimers.
        internal static SettingsControl? Instance { get; private set; }

        private readonly MozaPlugin _plugin;
        private readonly MozaDeviceManager _device;
        private readonly MozaData _data;
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _steeringAngleTimer;
        // Rotation-limit readback: the slider floor (60°) sits below PitHouse's
        // 90°, and firmware clamps silently — measured floor 60° on tested
        // base, other firmware may differ. After the last slider tick re-read
        // the stored value and log write vs. device truth. Two-phase one-shot:
        // debounce → issue reads, then one more tick to log the settled reply.
        private readonly DispatcherTimer _rotationReadbackTimer;
        private bool _rotationReadbackLogPhase;
        private int _rotationLastWrittenDeg;
        private readonly EventSuppressor _suppressor = new EventSuppressor();
        private bool _suppressEvents => _suppressor.Suppressed;

        // Per-pedal Y-curve UI bindings, cached after InitializeComponent so
        // ApplyPedalCurvePreset can take an arrays pair instead of 10 args.
        private Slider[]? _throttleCurveSliders, _brakeCurveSliders, _clutchCurveSliders;
        private TextBox[]? _throttleCurveLabels, _brakeCurveLabels, _clutchCurveLabels;


        public SettingsControl(MozaPlugin plugin)
        {
            _plugin = plugin;
            _device = plugin.DeviceManager;
            _data = plugin.Data;

            using (_suppressor.Begin())
            {
                InitializeComponent();
                _throttleCurveSliders = new[] { ThrottleY1Slider, ThrottleY2Slider, ThrottleY3Slider, ThrottleY4Slider, ThrottleY5Slider };
                _brakeCurveSliders    = new[] { BrakeY1Slider,    BrakeY2Slider,    BrakeY3Slider,    BrakeY4Slider,    BrakeY5Slider };
                _clutchCurveSliders   = new[] { ClutchY1Slider,   ClutchY2Slider,   ClutchY3Slider,   ClutchY4Slider,   ClutchY5Slider };
                _throttleCurveLabels  = new[] { ThrottleY1Value,  ThrottleY2Value,  ThrottleY3Value,  ThrottleY4Value,  ThrottleY5Value };
                _brakeCurveLabels     = new[] { BrakeY1Value,     BrakeY2Value,     BrakeY3Value,     BrakeY4Value,     BrakeY5Value };
                _clutchCurveLabels    = new[] { ClutchY1Value,    ClutchY2Value,    ClutchY3Value,    ClutchY4Value,    ClutchY5Value };
                ConnectionToggle.IsChecked = plugin.ConnectionEnabled;
                AutoApplyProfileCheck.IsChecked = plugin.Settings.AutoApplyProfileOnLaunch;
                ShowAllTabsCheck.IsChecked = plugin.Settings.ShowAllTabs;
                SyncAutoStandbyCombo();
                LimitWheelUpdatesCheck.IsChecked = plugin.Settings.LimitWheelUpdates;
                AlwaysResendBitmaskCheck.IsChecked = plugin.Settings.AlwaysResendBitmask;
                int kaSec = plugin.Settings.WheelKeepaliveTimeoutSec;
                KeepaliveTimeoutSlider.Value = Math.Max(KeepaliveTimeoutSlider.Minimum, Math.Min(KeepaliveTimeoutSlider.Maximum, kaSec));
                KeepaliveTimeoutValue.Text = $"{kaSec} s";
                // Gearshift coalescing controls (GearshiftVibrateOnNeutralCheck,
                // GearshiftDebounceSlider) are profile-sourced — populated by
                // RefreshBaseTab on every 500 ms tick so a profile switch with
                // the panel open tracks the new game's values. See the comment
                // in RefreshBaseTab for why the constructor copy was removed.
                DisableSerialProbeFallbackCheck.IsChecked = plugin.Settings.DisableSerialProbeFallback;
                DisableAb9DetectionCheck.IsChecked = plugin.Settings.DisableAb9Detection;
            }

            InitProfilesTab();
            InitRedesignControls();
            InitSdkTab();
            InitLanguageCombo();

            // Inline PitHouse import wizard (Import tab). Instantiated here
            // rather than as a named XAML element because a generated typed
            // field of MozaPlugin.UI.Import.* collides with the MozaPlugin
            // class name. Hand it the plugin and route Apply to ApplyImportPlan.
            var importControl = new UI.Import.PitHouseImportControl();
            importControl.Initialize(_plugin);
            importControl.ApplyRequested += ApplyImportPlan;
            ImportTab.Content = importControl;

            Instance = this;

            // Host the shared banner control (instantiated here — a generated
            // typed field of MozaPlugin.UI.* collides with the MozaPlugin class
            // name). Wire its in-app navigation; device-page hosts leave these
            // null and fall back to the external URL / a hidden Configure button.
            BannersHost.Content = new UI.PluginBanners
            {
                OpenReleaseNotesInApp = OpenReleaseNotes,
                ConfigureSdkInApp = NavigateToSdkTab,
            };

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += RefreshDisplay;

            _steeringAngleTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _steeringAngleTimer.Tick += OnSteeringAngleTick;

            _rotationReadbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _rotationReadbackTimer.Tick += OnRotationReadbackTick;

            Loaded   += OnLoadedStartTimers;
            Unloaded += OnUnloadedStopTimers;

            // Any interaction with the settings pane counts as activity, so
            // auto-standby never powers the wheel down mid-configuration.
            // Preview (tunneling) events fire on the root first regardless of
            // which child handles them.
            PreviewMouseDown  += (s, ev) => _plugin?.NotifyUserActivity();
            PreviewKeyDown    += (s, ev) => _plugin?.NotifyUserActivity();
            PreviewMouseWheel += (s, ev) => _plugin?.NotifyUserActivity();

            RequestAllSettings();
        }

        private void OnSteeringAngleTick(object? sender, EventArgs e) => UpdateHidInputDisplays();

        private void OnLoadedStartTimers(object sender, RoutedEventArgs e)
        {
            // WPF can fire Loaded more than once if the control is reparented
            // (SimHub's tab containers do this during settings-panel layout).
            // Calling Start() twice would double the tick rate.
            bool wasRunning = _refreshTimer.IsEnabled;
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
            if (!_steeringAngleTimer.IsEnabled) _steeringAngleTimer.Start();
            if (_bandwidthTimer != null && !_bandwidthTimer.IsEnabled) _bandwidthTimer.Start();

            // A genuine (re)load — not just a redundant Loaded firing while
            // everything's already running — means this control's timers were
            // stopped (OnUnloadedStopTimers) for however long it was off-screen
            // (navigated away to another plugin's page, or the settings window
            // was closed). RefreshMBoosterTab never ran during that window, so
            // if the active SimHub profile changed while this page was hidden,
            // waiting for _refreshTimer's first post-reload tick would show up
            // to 500ms of the PREVIOUS profile's mBooster values the instant the
            // tab becomes visible again. Force one immediate, synchronous
            // reseed instead of waiting for that first tick.
            if (!wasRunning)
            {
                _mboosterUiSeeded = false;
                RefreshMBoosterTab();
            }
        }

        private void OnUnloadedStopTimers(object sender, RoutedEventArgs e)
        {
            // Stop only — leave Tick handlers attached so a subsequent Loaded
            // re-Start picks up where it left off. Detaching here permanently
            // killed the timers if the control was reloaded. The _bandwidthTimer
            // MUST be stopped on Unload too: its Tick captures `this`, so a
            // running DispatcherTimer roots the entire SettingsControl past
            // panel-close. Across many open/close cycles this leaked one
            // SettingsControl + _plugin/_data graph per cycle until process exit.
            _refreshTimer.Stop();
            _steeringAngleTimer.Stop();
            _rotationReadbackTimer.Stop();
            _bandwidthTimer?.Stop();
            UnsubscribeStalks();
            // Closing the settings panel takes the sustained Engine/ABS/
            // Traction Control/Wheel Spin/Gear Shift/Road Texture/Lockup/
            // Threshold/Brake Fade test toggles out of view — stop them so
            // a forgotten toggle doesn't leave the pedal buzzing
            // indefinitely with no UI left to turn it off.
            if (MBoosterEngineTestToggle?.IsChecked == true)
                CurrentMBoosterController()?.SetEngineTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterAbsTestToggle?.IsChecked == true)
                CurrentMBoosterController()?.SetAbsTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterTcTestToggle?.IsChecked == true)
                CurrentMBoosterController()?.SetTcTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterWheelSpinTestToggle?.IsChecked == true)
                CurrentMBoosterController()?.SetWheelSpinTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterGearShiftTestToggle?.IsChecked == true)
                CurrentMBoosterController()?.SetGearShiftTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterRoadTextureTestToggle?.IsChecked == true)
                CurrentMBoosterController()?.SetRoadTextureTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterLockupTestToggle?.IsChecked == true)
                CurrentMBoosterController()?.SetLockupTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterThresholdTestToggle?.IsChecked == true)
                CurrentMBoosterController()?.SetThresholdTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterBrakeFadeTestToggle?.IsChecked == true)
                CurrentMBoosterController()?.SetBrakeFadeTestActive(false);
            if (MBoosterGForceTestToggle?.IsChecked == true)
                CurrentMBoosterController()?.SetGForceTestActive(false, _mboosterEffectPedalIndex);
            StopAllCustomEffectTests();
            // SDK CoAP server fires RecentRequestAppended on its receive
            // thread; unsubscribe so a torn-down SettingsControl can be GC'd
            // without the server's event list pinning it. Re-subscribe
            // happens on the next refresh tick after Loaded fires.
            UnsubscribeFromSdkServer();
            if (ReferenceEquals(Instance, this)) Instance = null;
        }


        // ===== Refresh =====

        private void RequestAllSettings()
        {
            _device.ReadSettings(
                "base-limit", "base-ffb-strength", "base-torque", "base-speed",
                "base-damper", "base-friction", "base-inertia", "base-spring",
                "main-get-damper-gain", "main-get-friction-gain",
                "main-get-inertia-gain", "main-get-spring-gain",
                "base-protection", "base-natural-inertia",
                "base-speed-damping", "base-speed-damping-point",
                "base-soft-limit-stiffness", "base-soft-limit-retain",
                "base-ffb-reverse", "main-get-work-mode", "main-get-led-status",
                "main-get-ble-mode",
                "base-mcu-temp", "base-mosfet-temp", "base-motor-temp"
            );
        }

        private void RefreshDisplay(object sender, EventArgs e)
        {
            // All top-of-pane banners (status hints + update + SDK nudge) are
            // owned by the self-refreshing PluginBanners control now.

            using (_suppressor.Begin())
            {
                RefreshBaseTab();
                RefreshHandbrakeTab();
                RefreshPedalsTab();
                RefreshHgpTab();
                RefreshSgpTab();
                RefreshHubTab();
                RefreshAb9Tab();
                RefreshStalksTab();
                RefreshMBoosterTab();
                InitTelemetryTab();
                RefreshSdkTabTick();
                // Last: the per-tab refreshes above each set their own tab's
                // Visibility from detection, so the override only sticks if it
                // runs after them. Turning it back off needs no undo here —
                // those same assignments re-collapse on the next tick.
                ApplyShowAllTabs();
            }
        }

        // Every tab the pane can hide: the detection-gated device tabs, plus the
        // ones hidden unconditionally in XAML — the wheel tab (migrated to the
        // per-wheel device page) and the dashboard upload / wheel files tabs
        // (feature still in development). Nothing else drives the latter three's
        // Visibility, so ShowAllTabs is the only thing that surfaces them.
        private TabItem[] HideableTabs => new[]
        {
            BaseLfeTab, HandbrakeTab, PedalsTab, Ab9Tab,
            HgpTab, SgpTab, MBoosterTab, HubTab, StalksTab,
        };

        private void ApplyShowAllTabs()
        {
            if (_plugin?.Settings?.ShowAllTabs != true) return;

            foreach (var tab in HideableTabs)
            {
                if (tab != null)
                    tab.Visibility = Visibility.Visible;
            }
        }

        private void ShowAllTabsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.ShowAllTabs = ShowAllTabsCheck.IsChecked == true;
            _plugin.SaveSettings();
            // Show immediately; hiding falls out of the next tick's per-tab pass.
            ApplyShowAllTabs();
        }

        private void UpdateHidInputDisplays()
        {
            var hidReader = _plugin.HidReader;
            bool connected = hidReader != null && _data.IsHidConnected;

            if (connected && _data.MaxAngle > 0)
            {
                double deg = hidReader!.GetCurrentAngleDegrees(_data.MaxAngle * 2);
                SteeringAngleLabel.Text = $"{deg:0;-0;0}°";
                UpdateRedesignSteeringAngle(deg, valid: true);
            }
            else
            {
                SteeringAngleLabel.Text = "--";
                UpdateRedesignSteeringAngle(0, valid: false);
            }

            HandbrakeBar.Value = connected ? _data.HandbrakePosition : 0;

            // Pedals-tab live-input trace (replaced the throttle/brake/clutch
            // bars). Pushed every frame so the sparkline scrolls whether or not a
            // pedal is moving; same merged 0-100 values as the mBooster trace.
            PushTraceSample(_pedalBrakeTraceSamples,    connected ? _data.BrakePosition : 0);
            PushTraceSample(_pedalThrottleTraceSamples, connected ? _data.ThrottlePosition : 0);
            PushTraceSample(_pedalClutchTraceSamples,   connected ? _data.ClutchPosition : 0);

            UpdateHandbrakeButtonStatus(connected);
            UpdateMBoosterCurveMarkers(connected);

            // Phase 6: fan out live HID data to the per-device wheel control's
            // Inputs sub-tab so its paddle bars + active-button text update at
            // the same 30 Hz cadence as the (now hidden) plugin-pane controls.
            try { global::MozaPlugin.Devices.MozaWheelSettingsControl.Instance?.PushInputsLiveData(_data); }
            catch { }
        }

        // Live position markers on the mBooster tab's two curve editors,
        // driven by the currently selected device's latest HID position.
        // Runs at the same 30 Hz cadence as the standard pedal bars above
        // instead of the 500ms general refresh — that felt sluggish for
        // direct pedal feedback.
        private void UpdateMBoosterCurveMarkers(bool hidConnected)
        {
            if (MBoosterDevicePanel.Visibility != Visibility.Visible) return;
            var selected = _plugin?.MBoosterRegistry?.FindByIdentity(_mboosterSelectedIdentity ?? "");
            if (selected == null) return;
            // Track the SELECTED pedal's own axis, not always the master's —
            // otherwise every pedal page showed the master (throttle) input.
            int idx = _mboosterEffectPedalIndex;
            double preCurve = (idx >= 0 && idx < selected.LastAxisRawPercentPreCurve.Length)
                ? selected.LastAxisRawPercentPreCurve[idx] : selected.LastRawPercentPreCurve;

            // Pedal Feel's curve is now a REAL hardware effect (see
            // MozaMBoosterRegistry.ComputeFeelCurve) — the device reshapes
            // the raw force before this HID read ever sees it, so AZOM has
            // no live "input to that curve" value to plot; no marker shown.
            // The Sim Input Mapping curve is the opposite: purely host-side
            // (see EvaluateCurveArbitraryX), so its live marker uses
            // preCurve — the already-hardware-shaped raw position that's
            // actually fed INTO this curve, not pct (which is the curve's
            // own output).
            MBoosterInputCurveEditor.LiveX = double.NaN;
            MBoosterCurveEditor.LiveX = preCurve;

            // Live "position % · kg force" readout above the Pedal Feel
            // curve editor (MBoosterPedalFeelLiveLabel). kg is an estimate,
            // not a directly-read sensor value: the raw HID axis only ever
            // reports a 0-100% position, calibrated so 100% == this pedal's
            // Max Threshold force (see docs/protocol/devices/mbooster.md
            // "Sim Input Mapping") — so force = pct/100 * that threshold.
            // Resolution order matches the removed ResolveFullScaleKg: the
            // user's own MaxThresholdKg override, else the device's own
            // mbooster-brake-threshold read-back, else a 200kg last resort.
            if (hidConnected)
            {
                var cfg = PeekMBoosterEffectTarget();
                double fullScaleKg = (cfg != null && cfg.MaxThresholdKg >= 0) ? cfg.MaxThresholdKg
                    : (selected.DeviceReportedMaxThresholdKg > 0 ? selected.DeviceReportedMaxThresholdKg : 200.0);
                double kg = preCurve / 100.0 * fullScaleKg;
                MBoosterPedalFeelLiveLabel.Text = $"{Strings.Label_OutputForce}: {preCurve:F0}% · {kg:F1} kg";
            }
            else
            {
                MBoosterPedalFeelLiveLabel.Text = Strings.Label_OutputForce;
            }

            // Effects card pedal trace — same 30 Hz cadence as the Inputs
            // tab's pedal bars above, and the same merged 0-100 values
            // (_data.*Position), so it shows every connected pedal's live
            // position — mBooster or not, whichever device currently holds
            // each role — not just an mBooster's own HID reading.
            PushTraceSample(_mboosterBrakeTraceSamples, hidConnected ? _data.BrakePosition : 0);
            PushTraceSample(_mboosterThrottleTraceSamples, hidConnected ? _data.ThrottlePosition : 0);
            PushTraceSample(_mboosterClutchTraceSamples, hidConnected ? _data.ClutchPosition : 0);
        }

        // Append one sample to a rolling trace buffer, trimming to PedalTraceSamples.
        // Shared by the mBooster page trace and the Pedals-tab trace.
        private static void PushTraceSample(ObservableCollection<double> samples, double value)
        {
            samples.Add(value);
            while (samples.Count > PedalTraceSamples)
                samples.RemoveAt(0);
        }

        private void UpdateHandbrakeButtonStatus(bool connected)
        {
            if (_data.HandbrakeMode != 1) return;

            bool pressed = connected && _data.HandbrakeButtonPressed;
            HandbrakeButtonStatus.Text = pressed ? "Pressed" : "Released";
            HandbrakeButtonStatus.FontWeight = pressed ? FontWeights.Bold : FontWeights.Normal;
            HandbrakeButtonStatus.Foreground = pressed ? Brushes.White : Brushes.Gray;
        }

        private void RefreshBaseTab()
        {
            ConnectionIndicator.Fill = _data.IsConnected ? Brushes.LimeGreen : Brushes.Gray;
            ConnectionLabel.Text = _data.IsConnected ? "Connected" : "Disconnected";
            UpdateRedesignLiveDisplays();

            string tempUnit = _data.UseFahrenheit ? "°F" : "°C";
            McuTempLabel.Text = _data.IsBaseConnected ? $"{ConvertTemp(_data.McuTemp):F0} {tempUnit}" : "--";
            MosfetTempLabel.Text = _data.IsBaseConnected ? $"{ConvertTemp(_data.MosfetTemp):F0} {tempUnit}" : "--";
            MotorTempLabel.Text = _data.IsBaseConnected ? $"{ConvertTemp(_data.MotorTemp):F0} {tempUnit}" : "--";

            // Reverse expression: *2 (raw → display degrees)
            // Floor 60° = measured firmware clamp (PitHouse stops at 90°);
            // must stay even — the wire's half-degree raw makes odd degrees
            // unrepresentable — and matched to the XAML slider Minimum.
            double rot = _data.Limit * 2.0;
            RotationSlider.Value = Clamp(rot, 60, 2700);
            SetValueText(RotationValue, $"{rot:F0}°");

            double ffb = _data.FfbStrength / 10.0;
            FfbStrengthSlider.Value = Clamp(ffb, 0, 100);
            SetValueText(FfbStrengthValue, $"{ffb:F0}%");

            double interp = _data.Interpolation / 10.0;   // wire 0-100 -> display 0-10
            InterpolationSlider.Value = Clamp(interp, 0, 10);
            SetValueText(InterpolationValue, $"{interp:F0}");

            TorqueSlider.Value = Clamp(_data.Torque, 50, 100);
            SetValueText(TorqueValue, $"{_data.Torque}%");

            // Performance output (cmd 0x1E base = TempStrategy): 0 = Reserved, 1 = Full
            int perf = _data.TempStrategy;
            if (perf >= 0 && perf < PerformanceOutputCombo.Items.Count)
                PerformanceOutputCombo.SelectedIndex = perf;
            // Gearshift vibration intensity (cmd 0x2E base): 0..5
            int gs = _data.GearshiftVibration;
            if (gs < 0) gs = 0;
            if (gs > 5) gs = 5;
            GearshiftVibrationSlider.Value = gs;
            SetValueText(GearshiftVibrationValue, gs.ToString());

            // Plugin-side gearshift event coalescing — per-profile (so each
            // game can pick its own tuning) with flat-field fallback. Refreshed
            // on every tick so a profile-switch with the settings tab open
            // pulls the new game's values; without this, the controls held
            // stale values from the previous profile and any user edit
            // silently overwrote the new active profile's stored values.
            // Suppressor on the surrounding RefreshDisplay scope swallows the
            // ValueChanged events these assignments raise.
            var gsProfile = _plugin.Settings.ProfileStore?.CurrentProfile;
            bool von = gsProfile?.GearshiftVibrateOnNeutral == 1
                || (gsProfile?.GearshiftVibrateOnNeutral == -1 && _plugin.Settings.GearshiftVibrateOnNeutral);
            GearshiftVibrateOnNeutralCheck.IsChecked = von;
            int dbMs = gsProfile?.GearshiftDebounceMs ?? -1;
            if (dbMs < 0) dbMs = _plugin.Settings.GearshiftDebounceMs;
            if (dbMs < 0) dbMs = 500;
            if (dbMs > 1000) dbMs = 1000;
            dbMs = ((dbMs + 25) / 50) * 50;
            GearshiftDebounceSlider.Value = dbMs;
            GearshiftDebounceValue.Text = $"{dbMs} ms";

            // Wheelbase LFE effects (base fw >= 1.2.10.10). On LFE-capable
            // firmware the complex effects card is shown and the classic
            // gearshift card hidden (the complex gearshift replaces it);
            // otherwise the reverse. Re-evaluated each tick so the card appears
            // within one refresh of the firmware-version read landing.
            // The classic gearshift card stays visible on all firmware (its bump
            // command coexists with the LFE channels); the LFE card is shown
            // additionally, full-width below, only on LFE-capable firmware.
            // Hide the LFE tab while the ShakeIt haptics device is deployed — that
            // device owns the LFE output, so the two must not both edit the base.
            bool lfeSupported = _data.BaseSupportsLfe && _plugin?.IsShakeItLfeDeviceDeployed != true;
            BaseLfeTab.Visibility = lfeSupported
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (lfeSupported)
                SeedBaseLfeControls(gsProfile?.BaseLfe);

            // FFB EQ band mode (6 legacy bands vs 10-band fw >= 1.2.10.10) —
            // re-evaluated each tick like the LFE gate above.
            bool eq10 = _data.BaseSupportsEq10;
            ApplyEqBandMode(eq10);

            double spd = _data.Speed / 10.0;
            SpeedSlider.Value = Clamp(spd, 0, 200);
            SetValueText(SpeedValue, $"{spd:F0}%");

            SetSliderPercent(DamperSlider, DamperValue, _data.Damper / 10.0, 0, 100);
            SetSliderPercent(FrictionSlider, FrictionValue, _data.Friction / 10.0, 0, 100);
            InertiaSlider.Value = Clamp(_data.Inertia / 10.0, 100, 500);
            SetValueText(InertiaValue, $"{_data.Inertia / 10.0:F0}");
            SetSliderPercent(SpringSlider, SpringValue, _data.Spring / 10.0, 0, 100);

            FfbReverseCheck.IsChecked = _data.FfbReverse != 0;

            SetSliderPercent(GameDamperSlider, GameDamperValue, _data.GameDamper / 2.55, 0, 100);
            SetSliderPercent(GameFrictionSlider, GameFrictionValue, _data.GameFriction / 2.55, 0, 100);
            SetSliderPercent(GameInertiaSlider, GameInertiaValue, _data.GameInertia / 2.55, 0, 100);
            SetSliderPercent(GameSpringSlider, GameSpringValue, _data.GameSpring / 2.55, 0, 100);

            SpeedDampingSlider.Value = Clamp(_data.SpeedDamping, 0, 100);
            SetValueText(SpeedDampingValue, $"{_data.SpeedDamping}%");
            SpeedDampingPointSlider.Value = Clamp(_data.SpeedDampingPoint, 0, 400);
            SetValueText(SpeedDampingPointValue, $"{_data.SpeedDampingPoint} kph");

            ProtectionCheck.IsChecked = _data.Protection != 0;
            NaturalInertiaSlider.Value = Clamp(_data.NaturalInertia, 100, 4000);
            SetValueText(NaturalInertiaValue, $"{_data.NaturalInertia}");

            double stiff = (_data.SoftLimitStiffness / (400.0 / 9.0)) - 2.25 + 1.0;
            stiff = Math.Round(Clamp(stiff, 1, 10));
            SoftLimitStiffnessSlider.Value = stiff;
            SetValueText(SoftLimitStiffnessValue, $"{stiff:F0}");
            SoftLimitRetainCheck.IsChecked = _data.SoftLimitRetain != 0;

            StandbyCheck.IsChecked = _data.WorkMode != 0;
            SyncAutoStandbyCombo();
            LedStatusCheck.IsChecked = _data.LedStatus != 0;
            BluetoothCheck.IsChecked = _data.BleMode == 0;

            // FFB Equalizer (100% = flat). Ranges depend on the band mode:
            // legacy 0-400 on all six; 10-band fw is 0-500 except the 100 Hz
            // band (Eq6), which keeps its 0-100 cap. SetSliderRaw clamps, so
            // the ranges are load-bearing.
            int eqHi = eq10 ? 500 : 400;
            SetSliderRaw(Eq1Slider, Eq1Value, _data.Equalizer1, 0, eqHi, "%");
            SetSliderRaw(Eq2Slider, Eq2Value, _data.Equalizer2, 0, eqHi, "%");
            SetSliderRaw(Eq3Slider, Eq3Value, _data.Equalizer3, 0, eqHi, "%");
            SetSliderRaw(Eq4Slider, Eq4Value, _data.Equalizer4, 0, eqHi, "%");
            SetSliderRaw(Eq5Slider, Eq5Value, _data.Equalizer5, 0, eqHi, "%");
            SetSliderRaw(Eq6Slider, Eq6Value, _data.Equalizer6, 0, eq10 ? 100 : 400, "%");
            if (eq10)
            {
                SetSliderRaw(Eq7Slider, Eq7Value, _data.Equalizer7, 0, 500, "%");
                SetSliderRaw(Eq8Slider, Eq8Value, _data.Equalizer8, 0, 500, "%");
                SetSliderRaw(Eq9Slider, Eq9Value, _data.Equalizer9, 0, 500, "%");
                SetSliderRaw(Eq10Slider, Eq10Value, _data.Equalizer10, 0, 500, "%");
            }

            // FFB Curve — X1..X4 are the draggable input positions of points 1-4
            // (point 5 fixed at input=100%); Y1..Y5 the output values.
            SetSliderRaw(FfbCurveX1Slider, FfbCurveX1Value, _data.FfbCurveX1, 0, 100, "");
            SetSliderRaw(FfbCurveX2Slider, FfbCurveX2Value, _data.FfbCurveX2, 0, 100, "");
            SetSliderRaw(FfbCurveX3Slider, FfbCurveX3Value, _data.FfbCurveX3, 0, 100, "");
            SetSliderRaw(FfbCurveX4Slider, FfbCurveX4Value, _data.FfbCurveX4, 0, 100, "");
            SetSliderRaw(FfbCurveY1Slider, FfbCurveY1Value, _data.FfbCurveY1, 0, 100, "");
            SetSliderRaw(FfbCurveY2Slider, FfbCurveY2Value, _data.FfbCurveY2, 0, 100, "");
            SetSliderRaw(FfbCurveY3Slider, FfbCurveY3Value, _data.FfbCurveY3, 0, 100, "");
            SetSliderRaw(FfbCurveY4Slider, FfbCurveY4Value, _data.FfbCurveY4, 0, 100, "");
            SetSliderRaw(FfbCurveY5Slider, FfbCurveY5Value, _data.FfbCurveY5, 0, 100, "");
        }

        // ===== Base tab slider handlers =====
        // Each handler writes to device AND updates _data so the refresh timer doesn't revert.

        private void RotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int deg = (int)Math.Round(e.NewValue);
            // Expression: /2 (display degrees → raw)
            int raw = deg / 2;
            RotationValue.Text = $"{deg}°";
            _data.Limit = raw;
            _data.MaxAngle = raw;
            _plugin.WriteIfBaseConnected("base-limit", raw);
            _plugin.WriteIfBaseConnected("base-max-angle", raw);
            _plugin.SaveSettings();

            // Diagnostic — confirms the value reached the active profile.
            // CaptureFromCurrent inside SaveSettings should have set
            // profile.Limit from _data.Limit; the post-capture value lets us
            // verify the persist path on future bug reports. Debug-level so
            // it doesn't spam SimHub.txt during slider drags (~100 ticks per
            // full sweep with the 10° snap); the in-process MozaLog ring
            // buffer still records it for the Diagnostics export bundle.
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            MozaLog.Debug(
                $"[AZOM] Rotation slider → {deg}° (raw={raw}); " +
                $"active profile='{profile?.Name ?? "(none)"}', " +
                $"profile.Limit={profile?.Limit.ToString() ?? "n/a"}, " +
                $"baseConnected={_data.IsBaseConnected}");

            // Restart the readback debounce so it fires once, after the last
            // tick of a drag.
            _rotationLastWrittenDeg = deg;
            _rotationReadbackLogPhase = false;
            _rotationReadbackTimer.Stop();
            _rotationReadbackTimer.Interval = TimeSpan.FromMilliseconds(400);
            _rotationReadbackTimer.Start();
        }

        private void OnRotationReadbackTick(object? sender, EventArgs e)
        {
            if (!_rotationReadbackLogPhase)
            {
                // Phase 1: ask the base what it actually stored. The replies
                // land in _data.Limit/_data.MaxAngle and the refresh tick
                // snaps the slider to device truth.
                _plugin.ReadIfBaseConnected("base-limit");
                _plugin.ReadIfBaseConnected("base-max-angle");
                _rotationReadbackLogPhase = true;
                _rotationReadbackTimer.Interval = TimeSpan.FromMilliseconds(500);
                return;
            }

            _rotationReadbackTimer.Stop();
            int reportedDeg = _data.Limit * 2;
            if (reportedDeg == _rotationLastWrittenDeg)
                MozaLog.Debug($"[AZOM] Rotation readback: device kept {reportedDeg}°");
            else
                MozaLog.Info($"[AZOM] Rotation readback: wrote {_rotationLastWrittenDeg}°, device reports {reportedDeg}° (firmware clamp)");
        }

        private void FfbStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            FfbStrengthValue.Text = $"{pct}%";
            _data.FfbStrength = raw;
            _plugin.WriteIfBaseConnected("base-ffb-strength", raw);
            _plugin.SaveSettings();
        }

        private void InterpolationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int display = (int)Math.Round(e.NewValue);   // 0-10
            int raw = display * 10;                       // wire value 0-100
            InterpolationValue.Text = $"{display}";
            _data.Interpolation = raw;
            _plugin.WriteIfBaseConnected("main-set-interpolation", raw);
            _plugin.SaveSettings();
        }

        private void TorqueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            TorqueValue.Text = $"{val}%";
            _data.Torque = val;
            _plugin.WriteIfBaseConnected("base-torque", val);
            _plugin.SaveSettings();
        }

        private void PerformanceOutputCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = PerformanceOutputCombo.SelectedIndex;
            if (val < 0) return;
            _data.TempStrategy = val;
            _plugin.WriteIfBaseConnected("base-temp-strategy", val);
            _plugin.SaveSettings();
        }

        private void GearshiftVibrationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            GearshiftVibrationValue.Text = val.ToString();
            _data.GearshiftVibration = val;
            _plugin.WriteIfBaseConnected("base-gearshift-vibration", val);
            _plugin.SaveSettings();
        }

        private void GearshiftVibrateOnNeutralCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = GearshiftVibrateOnNeutralCheck.IsChecked == true;
            _plugin.UpdateActiveProfile(p => p.GearshiftVibrateOnNeutral = on ? 1 : 0);
            _plugin.SaveSettings();
        }

        private void GearshiftDebounceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            // Snap to 50 ms grid (IsSnapToTickEnabled + TickFrequency=50 already
            // enforces this on user input, but be defensive against external
            // sources that bypass the tick grid).
            val = ((val + 25) / 50) * 50;
            if (val < 0) val = 0;
            if (val > 1000) val = 1000;
            GearshiftDebounceValue.Text = $"{val} ms";
            _plugin.UpdateActiveProfile(p => p.GearshiftDebounceMs = val);
            _plugin.SaveSettings();
        }

        // ===== Wheelbase LFE effects (base fw >= 1.2.10.10) =====
        // Profile-tier host-rendered effects — mutate BaseLfeSettings and Save.
        // Each parameter is dual-mode: a slider, or an NCalc/property formula
        // (ƒₓ) that overrides it. A set formula disables the slider and shows "ƒₓ".

        private void SeedBaseLfeControls(BaseLfeSettings? fx)
        {
            // Populate the preset dropdown once (rebuilding items per tick would
            // close an open dropdown every 500 ms); then keep its selection synced to
            // the active profile's saved preset each tick (handles profile switches).
            if (!_lfePresetsInitialized)
            {
                _lfePresetsInitialized = true;
                BaseLfeScope.Poll = () => _plugin.GetLfeScopeSamples();
                SeedBuiltInPresets();
                RefreshLfePresetList();
            }
            SelectLfePreset();

            fx ??= new BaseLfeSettings();
            var eng = fx.Engine ?? new BaseLfeChannel();
            var ab = fx.Abs ?? new BaseLfeChannel();
            var gs = fx.Gearshift ?? new BaseLfeChannel();

            BaseLfeEngineEnable.IsChecked = eng.Enabled;
            BaseLfeEngineMode.SelectedIndex = (int)eng.Mode;
            BaseLfeAbsMode.SelectedIndex = (int)ab.Mode;
            BaseLfeGearshiftMode.SelectedIndex = (int)gs.Mode;
            BaseLfeEngineTriggerMode.SelectedIndex = (int)eng.TriggerMode;
            BaseLfeAbsTriggerMode.SelectedIndex = (int)ab.TriggerMode;
            BaseLfeGearshiftTriggerMode.SelectedIndex = (int)gs.TriggerMode;
            SeedLfeTrigger(BaseLfeEngineTriggerText, eng.TriggerFormula);
            SetLfeFreqSliderRange(BaseLfeEngineFrequencySlider, eng.Mode);
            SeedLfeParam(BaseLfeEngineFrequencySlider, BaseLfeEngineFrequencyValue, BaseLfeEngineFrequencyFormulaText, eng.Frequency, eng.FrequencyFormula);
            SeedLfeParam(BaseLfeEngineIntensity, BaseLfeEngineIntensityValue, BaseLfeEngineIntensityFormulaText, eng.Intensity, eng.IntensityFormula);
            SeedLfeParam(BaseLfeEngineSmoothness, BaseLfeEngineSmoothnessValue, BaseLfeEngineSmoothnessFormulaText, eng.Smoothness, eng.SmoothnessFormula);

            BaseLfeAbsEnable.IsChecked = ab.Enabled;
            SeedLfeTrigger(BaseLfeAbsTriggerText, ab.TriggerFormula);
            SetLfeFreqSliderRange(BaseLfeAbsFrequencySlider, ab.Mode);
            SeedLfeParam(BaseLfeAbsFrequencySlider, BaseLfeAbsFrequencyValue, BaseLfeAbsFrequencyFormulaText, ab.Frequency, ab.FrequencyFormula);
            SeedLfeParam(BaseLfeAbsIntensity, BaseLfeAbsIntensityValue, BaseLfeAbsIntensityFormulaText, ab.Intensity, ab.IntensityFormula);
            SeedLfeParam(BaseLfeAbsSmoothness, BaseLfeAbsSmoothnessValue, BaseLfeAbsSmoothnessFormulaText, ab.Smoothness, ab.SmoothnessFormula);

            BaseLfeGearshiftEnable.IsChecked = gs.Enabled;
            SeedLfeTrigger(BaseLfeGearshiftTriggerText, gs.TriggerFormula);
            SetLfeFreqSliderRange(BaseLfeGearshiftFrequencySlider, gs.Mode);
            SeedLfeParam(BaseLfeGearshiftFrequencySlider, BaseLfeGearshiftFrequencyValue, BaseLfeGearshiftFrequencyFormulaText, gs.Frequency, gs.FrequencyFormula);
            SeedLfeParam(BaseLfeGearshiftIntensity, BaseLfeGearshiftIntensityValue, BaseLfeGearshiftIntensityFormulaText, gs.Intensity, gs.IntensityFormula);
            SeedLfeParam(BaseLfeGearshiftSmoothness, BaseLfeGearshiftSmoothnessValue, BaseLfeGearshiftSmoothnessFormulaText, gs.Smoothness, gs.SmoothnessFormula);

            // Frequency band — shown only while a frequency formula is active.
            SeedLfeFreqLimits(BaseLfeEngineFreqLimits, BaseLfeEngineFreqRange, BaseLfeEngineFreqRangeText, eng);
            SeedLfeFreqLimits(BaseLfeAbsFreqLimits, BaseLfeAbsFreqRange, BaseLfeAbsFreqRangeText, ab);
            SeedLfeFreqLimits(BaseLfeGearshiftFreqLimits, BaseLfeGearshiftFreqRange, BaseLfeGearshiftFreqRangeText, gs);

            // Edge refinements (vibrate-on-neutral + debounce) — every channel has
            // them, shown only while that channel is in On-change mode.
            SeedLfeEdge(BaseLfeEngineEdgeOptions, BaseLfeEngineVibrateOnNeutral, BaseLfeEngineDebounceSlider, BaseLfeEngineDebounceValue, eng);
            SeedLfeEdge(BaseLfeAbsEdgeOptions, BaseLfeAbsVibrateOnNeutral, BaseLfeAbsDebounceSlider, BaseLfeAbsDebounceValue, ab);
            SeedLfeEdge(BaseLfeGearshiftEdgeOptions, BaseLfeGearshiftVibrateOnNeutral, BaseLfeGearshiftDebounceSlider, BaseLfeGearshiftDebounceValue, gs);

            // Live formula readouts next to ƒ(x) (shown only when that param has a
            // formula). Re-evaluated each RefreshDisplay tick. Frequency uses the
            // channel's own rescale so it matches the value the worker sends.
            UpdateLfeCalc(BaseLfeEngineTriggerCalc, eng.TriggerFormula, r => r);
            UpdateLfeCalc(BaseLfeEngineFrequencyCalc, eng.FrequencyFormula, eng.RescaleFreq);
            UpdateLfeCalc(BaseLfeEngineIntensityCalc, eng.IntensityFormula, r => Math.Max(0, Math.Min(100, r)));
            UpdateLfeCalc(BaseLfeEngineSmoothnessCalc, eng.SmoothnessFormula, r => Math.Max(0, Math.Min(100, r)));
            UpdateLfeCalc(BaseLfeAbsTriggerCalc, ab.TriggerFormula, r => r);
            UpdateLfeCalc(BaseLfeAbsFrequencyCalc, ab.FrequencyFormula, ab.RescaleFreq);
            UpdateLfeCalc(BaseLfeAbsIntensityCalc, ab.IntensityFormula, r => Math.Max(0, Math.Min(100, r)));
            UpdateLfeCalc(BaseLfeAbsSmoothnessCalc, ab.SmoothnessFormula, r => Math.Max(0, Math.Min(100, r)));
            UpdateLfeCalc(BaseLfeGearshiftTriggerCalc, gs.TriggerFormula, r => r);
            UpdateLfeCalc(BaseLfeGearshiftFrequencyCalc, gs.FrequencyFormula, gs.RescaleFreq);
            UpdateLfeCalc(BaseLfeGearshiftIntensityCalc, gs.IntensityFormula, r => Math.Max(0, Math.Min(100, r)));
            UpdateLfeCalc(BaseLfeGearshiftSmoothnessCalc, gs.SmoothnessFormula, r => Math.Max(0, Math.Min(100, r)));
        }

        // Evaluate a param's formula and show the shaped result next to ƒ(x)
        // (hidden when there is no formula — the slider/value box shows it then).
        private void UpdateLfeCalc(TextBlock calc, string? formula, Func<double, double> shape)
        {
            bool has = !string.IsNullOrWhiteSpace(formula);
            calc.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has) return;
            double v = shape(_plugin.EvalHapticsFormula(formula));
            calc.Text = Math.Abs(v) >= 10
                ? Math.Round(v).ToString("0")
                : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void SeedLfeFreqLimits(FrameworkElement panel, MozaControls.MozaRangeSlider range, TextBlock readout, BaseLfeChannel ch)
        {
            panel.Visibility = string.IsNullOrWhiteSpace(ch.FrequencyFormula) ? Visibility.Collapsed : Visibility.Visible;
            double lo = Math.Max(0, Math.Min(200, ch.FrequencyMin));
            double hi = Math.Max(0, Math.Min(200, ch.FrequencyMax));
            range.LowValue = lo;
            range.HighValue = hi;
            readout.Text = LfeRangeText(lo, hi);
        }

        private static string LfeRangeText(double lo, double hi) => $"{(int)Math.Round(lo)} – {(int)Math.Round(hi)}";

        private static void SeedLfeEdge(FrameworkElement panel, System.Windows.Controls.Primitives.ToggleButton neutral, Slider debSlider, TextBox debBox, BaseLfeChannel ch)
        {
            panel.Visibility = ch.TriggerMode == BaseLfeTriggerMode.OnChange ? Visibility.Visible : Visibility.Collapsed;
            neutral.IsChecked = ch.VibrateOnNeutral;
            int db = ch.DebounceMs;
            if (db < 0) db = 50;
            if (db > 1000) db = 1000;
            db = ((db + 25) / 50) * 50;
            debSlider.Value = db;
            debBox.Text = $"{db} ms";
        }

        // The frequency slider's range follows the slot's mode (Custom = full wire
        // range); the formula-limits band uses the same per-mode ranges via ApplyMode.
        private static (double min, double max) LfeFreqRange(BaseLfeMode mode) => mode switch
        {
            BaseLfeMode.Engine => (30, 130),
            BaseLfeMode.Abs => (5, 30),
            BaseLfeMode.Gearshift => (20, 100),
            _ => (0, 200),   // Custom → full 0..200 Hz
        };
        private static void SetLfeFreqSliderRange(Slider slider, BaseLfeMode mode)
        {
            var (min, max) = LfeFreqRange(mode);
            slider.Minimum = min;   // Min first: new min <= old max in every mode transition
            slider.Maximum = max;
        }

        // A formula overrides the slider: hide the slider + value box and show the
        // formula string in their place (full text in the tooltip). No formula →
        // slider + editable value box, formula line hidden.
        private static void SeedLfeParam(Slider slider, TextBox box, TextBlock formulaText, double sliderVal, string? formula)
        {
            bool hasFormula = !string.IsNullOrWhiteSpace(formula);
            slider.Value = sliderVal;                     // kept as the revert-to value; WPF clamps to Min/Max
            slider.Visibility = hasFormula ? Visibility.Collapsed : Visibility.Visible;
            box.Visibility = hasFormula ? Visibility.Collapsed : Visibility.Visible;
            box.Text = ((int)slider.Value).ToString();
            formulaText.Visibility = hasFormula ? Visibility.Visible : Visibility.Collapsed;
            formulaText.Text = hasFormula ? formula : "";
            formulaText.ToolTip = hasFormula ? formula : null;
        }

        private static void SeedLfeTrigger(TextBlock text, string? formula)
        {
            bool has = !string.IsNullOrWhiteSpace(formula);
            text.Text = has ? formula : Strings.Label_AlwaysOn;
            text.ToolTip = has ? formula : null;
            // Match the frequency/intensity/smoothness formula lines: cyan mono for
            // a live formula, dim UI font for the "(always on)" placeholder.
            text.Foreground = (System.Windows.Media.Brush)text.FindResource(has ? "CyanBrush" : "TextDimBrush");
            text.FontFamily = (System.Windows.Media.FontFamily)text.FindResource(has ? "FontMono" : "FontUi");
        }

        // Slider handlers only fire on user drag (a set formula disables the
        // slider). Each writes the channel's slider value.
        // Engine ---------------------------------------------------------------
        private void BaseLfeEngineEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = BaseLfeEngineEnable.IsChecked == true;
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Engine.Enabled = on; });
            _plugin.SaveSettings();
        }
        private void BaseLfeEngineFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(Math.Max(BaseLfeEngineFrequencySlider.Minimum, Math.Min(BaseLfeEngineFrequencySlider.Maximum, e.NewValue)));
            BaseLfeEngineFrequencyValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Engine.Frequency = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeEngineIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            BaseLfeEngineIntensityValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Engine.Intensity = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeEngineSmoothness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            BaseLfeEngineSmoothnessValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Engine.Smoothness = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeEngineTest_Click(object sender, RoutedEventArgs e)
            => _plugin.TriggerBaseLfeEngineTest();

        // ABS ------------------------------------------------------------------
        private void BaseLfeAbsEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = BaseLfeAbsEnable.IsChecked == true;
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Abs.Enabled = on; });
            _plugin.SaveSettings();
        }
        private void BaseLfeAbsFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(Math.Max(BaseLfeAbsFrequencySlider.Minimum, Math.Min(BaseLfeAbsFrequencySlider.Maximum, e.NewValue)));
            BaseLfeAbsFrequencyValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Abs.Frequency = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeAbsIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            BaseLfeAbsIntensityValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Abs.Intensity = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeAbsSmoothness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            BaseLfeAbsSmoothnessValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Abs.Smoothness = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeAbsTest_Click(object sender, RoutedEventArgs e)
            => _plugin.TriggerBaseLfeAbsTest();

        // Gearshift ------------------------------------------------------------
        private void BaseLfeGearshiftEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = BaseLfeGearshiftEnable.IsChecked == true;
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Gearshift.Enabled = on; });
            _plugin.SaveSettings();
        }
        private void BaseLfeGearshiftFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(Math.Max(BaseLfeGearshiftFrequencySlider.Minimum, Math.Min(BaseLfeGearshiftFrequencySlider.Maximum, e.NewValue)));
            BaseLfeGearshiftFrequencyValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Gearshift.Frequency = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeGearshiftIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            BaseLfeGearshiftIntensityValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Gearshift.Intensity = v; });
            _plugin.SaveSettings();
        }
        private void BaseLfeGearshiftSmoothness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            BaseLfeGearshiftSmoothnessValue.Text = v.ToString();
            _plugin.UpdateActiveProfile(p => { (p.BaseLfe ??= new BaseLfeSettings()).Gearshift.Smoothness = v; });
            _plugin.SaveSettings();
        }
        // Edge refinements (Tag = channel). Vibrate-on-neutral + debounce, per channel.
        private void BaseLfeVibrateOnNeutral_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (sender is not System.Windows.Controls.Primitives.ToggleButton tog || tog.Tag is not string ch) return;
            bool on = tog.IsChecked == true;
            _plugin.UpdateActiveProfile(p => LfeChannelForTag(p.BaseLfe ??= new BaseLfeSettings(), ch).VibrateOnNeutral = on);
            _plugin.SaveSettings();
        }
        private void BaseLfeDebounce_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            if (sender is not Slider sl || sl.Tag is not string ch) return;
            int val = (int)Math.Round(e.NewValue);
            val = ((val + 25) / 50) * 50;
            if (val < 0) val = 0;
            if (val > 1000) val = 1000;
            if (FindName($"BaseLfe{LfeCap(ch)}DebounceValue") is TextBox box) box.Text = $"{val} ms";
            _plugin.UpdateActiveProfile(p => LfeChannelForTag(p.BaseLfe ??= new BaseLfeSettings(), ch).DebounceMs = val);
            _plugin.SaveSettings();
        }
        private void BaseLfeGearshiftTest_Click(object sender, RoutedEventArgs e)
            => _plugin.TriggerBaseLfeGearshiftTest();

        // Presets Test: fire every ENABLED slot's mode-based test at once. They
        // sum on the base, so this previews the combined feel of the whole setup.
        private void BaseLfePresetsTest_Click(object sender, RoutedEventArgs e)
        {
            var lfe = _plugin.Settings?.ProfileStore?.CurrentProfile?.BaseLfe;
            if (lfe == null) return;
            if (lfe.Engine?.Enabled == true) _plugin.TriggerBaseLfeEngineTest();
            if (lfe.Abs?.Enabled == true) _plugin.TriggerBaseLfeAbsTest();
            if (lfe.Gearshift?.Enabled == true) _plugin.TriggerBaseLfeGearshiftTest();
        }

        // Frequency clamp band (Tag = channel) — double-ended slider, 0..200 Hz.
        private void BaseLfeFreqRange_RangeChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            if (sender is not MozaControls.MozaRangeSlider rs || rs.Tag is not string ch) return;
            float lo = (float)rs.LowValue, hi = (float)rs.HighValue;
            if (FindName($"BaseLfe{LfeCap(ch)}FreqRangeText") is TextBlock t) t.Text = LfeRangeText(lo, hi);
            _plugin.UpdateActiveProfile(p =>
            {
                var c = LfeChannelForTag(p.BaseLfe ??= new BaseLfeSettings(), ch);
                c.FrequencyMin = lo; c.FrequencyMax = hi;
            });
            _plugin.SaveSettings();
        }

        // "engine" → "Engine" (element-name prefix for the channel's controls).
        private static string LfeCap(string ch) => char.ToUpperInvariant(ch[0]) + ch.Substring(1);

        // Slot role (Tag = channel). Applies a trigger/limits/character template
        // for the chosen effect type; Custom leaves the slot's values untouched.
        // The slot's fixed wire id / render path is unaffected. Re-seed to reflect
        // the applied template across all the slot's controls.
        private void BaseLfeMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (sender is not System.Windows.Controls.Primitives.Selector sel || sel.Tag is not string ch) return;
            int idx = sel.SelectedIndex;
            if (idx < 0) return;
            var mode = (BaseLfeMode)idx;
            _plugin.UpdateActiveProfile(p => BaseLfeSettings.ApplyMode(LfeChannelForTag(p.BaseLfe ??= new BaseLfeSettings(), ch), mode));
            _plugin.SaveSettings();
            RefreshDisplay(this, EventArgs.Empty);
        }

        // Trigger mode: Level (continuous) vs On-change (burst). Tag = channel name.
        // The edge refinements (neutral/debounce) only apply in On-change mode.
        private void BaseLfeTriggerMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (sender is not System.Windows.Controls.Primitives.Selector sel || sel.Tag is not string ch) return;
            var mode = sel.SelectedIndex == 1 ? BaseLfeTriggerMode.OnChange : BaseLfeTriggerMode.Level;
            _plugin.UpdateActiveProfile(p => LfeChannelForTag(p.BaseLfe ??= new BaseLfeSettings(), ch).TriggerMode = mode);
            _plugin.SaveSettings();
            if (FindName($"BaseLfe{LfeCap(ch)}EdgeOptions") is FrameworkElement panel)
                panel.Visibility = mode == BaseLfeTriggerMode.OnChange ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Preset library ────────────────────────────────────────────────────
        // Built-in presets are code-generated (the original preset buttons); user
        // presets persist in MozaPluginSettings.BaseLfePresets. A user preset shadows
        // a same-named built-in (so a built-in can be "edited"); deleting it restores
        // the built-in. The list is rebuilt only on init/load/save/delete — NOT per
        // RefreshDisplay tick — so the dropdown selection isn't clobbered.
        private List<BaseLfePreset> _lfePresets = new List<BaseLfePreset>();
        private bool _lfePresetsInitialized;

        // Bump when FactoryLfePresets changes so existing libraries get refreshed.
        private const int CurrentLfeSeedVersion = 2;

        // The factory presets, generated from code. Seeded into the editable list;
        // thereafter they're ordinary editable/deletable presets.
        private static List<BaseLfePreset> FactoryLfePresets() => new List<BaseLfePreset>
        {
            new BaseLfePreset { Name = Strings.Label_Defaults, Settings = new BaseLfeSettings() },
            new BaseLfePreset { Name = Strings.Preset_AdditiveEngine, Settings = BaseLfeSettings.AdditiveEngine() },
            new BaseLfePreset { Name = Strings.Preset_BigRig, Settings = BaseLfeSettings.BigRig() },
            new BaseLfePreset { Name = Strings.Preset_DetunedV8, Settings = BaseLfeSettings.DetunedV8() },
            new BaseLfePreset { Name = Strings.Preset_RoadRumble, Settings = BaseLfeSettings.RoadRumble() },
        };

        // Seed / refresh the factory presets into the persisted list up to the current
        // seed version. Adds missing factory presets and refreshes factory-NAMED ones
        // to the latest definition; user-named presets are never touched. A factory
        // preset the user renamed survives; one they edited in place is refreshed.
        private void SeedBuiltInPresets()
        {
            var settings = _plugin.Settings;
            if (settings == null || settings.BaseLfePresetsSeedVersion >= CurrentLfeSeedVersion) return;
            var list = settings.BaseLfePresets ??= new List<BaseLfePreset>();
            foreach (var f in FactoryLfePresets())
            {
                var existing = list.Find(u => string.Equals(u.Name, f.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null) existing.Settings = f.Settings;
                else list.Add(f);
            }
            settings.BaseLfePresetsSeedVersion = CurrentLfeSeedVersion;
            _plugin.SaveSettings();
        }

        // Rebuild the dropdown items from the persisted list (factory + user, all
        // uniform). Selection is then synced from the profile.
        private void RefreshLfePresetList()
        {
            var list = new List<BaseLfePreset>(_plugin.Settings?.BaseLfePresets ?? new List<BaseLfePreset>());
            _lfePresets = list;
            using (_suppressor.Begin())
            {
                BaseLfePresetCombo.ItemsSource = null;
                BaseLfePresetCombo.ItemsSource = list;
            }
            SelectLfePreset();
        }

        // Sync the dropdown (+ name box) to the active profile's saved preset name.
        // Suppressed, and only when the selection actually changes, so it never fires
        // an apply and never clobbers what the user is typing in the name box.
        private void SelectLfePreset()
        {
            string? name = _plugin.Settings?.ProfileStore?.CurrentProfile?.BaseLfePresetName;
            int idx = string.IsNullOrEmpty(name)
                ? -1 : _lfePresets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (BaseLfePresetCombo.SelectedIndex == idx) return;
            using (_suppressor.Begin())
            {
                BaseLfePresetCombo.SelectedIndex = idx;
                BaseLfePresetName.Text = idx >= 0 ? _lfePresets[idx].Name : "";
            }
        }

        // Picking a preset applies it to the current effects, records it on the
        // profile (so the dropdown remembers it across restarts), and fills the name
        // box. Programmatic reselects (seed / after save) are suppressed.
        private void BaseLfePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (BaseLfePresetCombo.SelectedItem is not BaseLfePreset p) return;
            BaseLfePresetName.Text = p.Name;
            _plugin.UpdateActiveProfile(prof => { prof.BaseLfe = p.Settings?.Clone() ?? new BaseLfeSettings(); prof.BaseLfePresetName = p.Name; });
            _plugin.SaveSettings();
            RefreshDisplay(this, EventArgs.Empty);
        }

        // Save the current effects under the name box's text. Overwrites a same-named
        // user preset, else adds one (which shadows a same-named built-in). Marks it
        // active on the profile so the dropdown reflects it.
        private void BaseLfePresetSave_Click(object sender, RoutedEventArgs e)
        {
            var settings = _plugin.Settings;
            if (settings == null) return;
            string name = BaseLfePresetName.Text?.Trim() ?? "";
            if (name.Length == 0) return;
            var snapshot = (settings.ProfileStore?.CurrentProfile?.BaseLfe ?? new BaseLfeSettings()).Clone();
            var users = settings.BaseLfePresets ??= new List<BaseLfePreset>();
            var existing = users.Find(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Settings = snapshot;
            else users.Add(new BaseLfePreset { Name = name, BuiltIn = false, Settings = snapshot });
            _plugin.UpdateActiveProfile(prof => prof.BaseLfePresetName = name);
            _plugin.SaveSettings();
            RefreshLfePresetList();
        }

        // Delete the selected preset (any preset — factory presets are ordinary now).
        private void BaseLfePresetDelete_Click(object sender, RoutedEventArgs e)
        {
            if (BaseLfePresetCombo.SelectedItem is not BaseLfePreset p) return;
            _plugin.Settings?.BaseLfePresets?.RemoveAll(u => string.Equals(u.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            _plugin.UpdateActiveProfile(prof => { if (string.Equals(prof.BaseLfePresetName, p.Name, StringComparison.OrdinalIgnoreCase)) prof.BaseLfePresetName = ""; });
            _plugin.SaveSettings();
            BaseLfePresetName.Text = "";
            RefreshLfePresetList();
        }

        // Export the selected preset to its own JSON file (share / back up one preset).
        private void BaseLfePresetExport_Click(object sender, RoutedEventArgs e)
        {
            if (BaseLfePresetCombo.SelectedItem is not BaseLfePreset p) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Moza LFE preset (*.json)|*.json|All files (*.*)|*.*",
                FileName = SanitizeFileName(p.Name) + ".json",
                DefaultExt = ".json",
            };
            try
            {
                if (dlg.ShowDialog() != true) return;
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(p, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(dlg.FileName, json);
            }
            catch (Exception ex) { MozaLog.Warn("[AZOM] LFE preset export failed: " + ex.Message); }
        }

        // Import a preset from a JSON file, merging by name (same name overwrites).
        // Accepts a single preset object or an array (older all-presets exports).
        private void BaseLfePresetImport_Click(object sender, RoutedEventArgs e)
        {
            var settings = _plugin.Settings;
            if (settings == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Moza LFE preset (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            try
            {
                if (dlg.ShowDialog() != true) return;
                var incoming = ParsePresetJson(System.IO.File.ReadAllText(dlg.FileName));
                var list = settings.BaseLfePresets ??= new List<BaseLfePreset>();
                foreach (var p in incoming)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;
                    var settingsClone = p.Settings?.Clone() ?? new BaseLfeSettings();
                    var existing = list.Find(u => string.Equals(u.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) existing.Settings = settingsClone;
                    else list.Add(new BaseLfePreset { Name = p.Name.Trim(), Settings = settingsClone });
                }
                _plugin.SaveSettings();
                RefreshLfePresetList();
            }
            catch (Exception ex) { MozaLog.Warn("[AZOM] LFE preset import failed: " + ex.Message); }
        }

        // A preset file is a single object; an array (old all-presets export) also works.
        private static List<BaseLfePreset> ParsePresetJson(string text)
        {
            if (text != null && text.TrimStart().StartsWith("[", StringComparison.Ordinal))
                return Newtonsoft.Json.JsonConvert.DeserializeObject<List<BaseLfePreset>>(text) ?? new List<BaseLfePreset>();
            var one = Newtonsoft.Json.JsonConvert.DeserializeObject<BaseLfePreset>(text ?? "");
            return one != null ? new List<BaseLfePreset> { one } : new List<BaseLfePreset>();
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "preset" : name;
        }

        // ƒₓ — open SimHub's formula editor for the tagged "channel:param" field.
        // Mirrors MBoosterAdvancedEditFormula_Click. Empty result clears back to
        // the slider.
        private async void BaseLfeFx_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string tag) return;
            var engine = _plugin.ChannelFormulaEngine;
            if (engine == null)
            {
                MozaLog.Warn("[AZOM] LFE formula editor unavailable (SimHub engine not loaded)");
                return;
            }
            var lfe = _plugin.Settings?.ProfileStore?.CurrentProfile?.BaseLfe ?? new BaseLfeSettings();
            var ev = LfeMakeExpression(GetLfeFormula(lfe, tag));
            var working = new ExpressionValue { UseJavascript = ev.UseJavascript, Expression = ev.Expression, PreExpression = ev.PreExpression };
            var data = new DashboardBindingData
            {
                Formula = working,
                Mode = string.IsNullOrWhiteSpace(working.Expression) ? BindingMode.None : BindingMode.Formula,
                TargetPropertyName = tag,
                TargetType = typeof(double),
            };
            try
            {
                var editor = new BindingEditor(engine) { DataContext = data };
                var result = await editor.ShowDialogWindowAsync(this);
                if ((int)result != 1) return;
                string formula = data.Mode == BindingMode.Formula ? LfeSerializeExpression(data.Formula) : "";
                _plugin.UpdateActiveProfile(p => SetLfeFormula(p.BaseLfe ??= new BaseLfeSettings(), tag, formula));
                _plugin.SaveSettings();
                RefreshDisplay(this, EventArgs.Empty);
            }
            catch (Exception ex) { MozaLog.Warn("[AZOM] LFE formula editor failed: " + ex.Message); }
        }

        private static BaseLfeChannel LfeChannelForTag(BaseLfeSettings lfe, string tag)
        {
            if (tag.StartsWith("engine", StringComparison.Ordinal)) return lfe.Engine ??= new BaseLfeChannel();
            if (tag.StartsWith("abs", StringComparison.Ordinal)) return lfe.Abs ??= new BaseLfeChannel();
            return lfe.Gearshift ??= new BaseLfeChannel();
        }
        private static string GetLfeFormula(BaseLfeSettings lfe, string tag)
        {
            var ch = LfeChannelForTag(lfe, tag);
            if (tag.EndsWith("trigger", StringComparison.Ordinal)) return ch.TriggerFormula;
            if (tag.EndsWith("frequency", StringComparison.Ordinal)) return ch.FrequencyFormula;
            if (tag.EndsWith("intensity", StringComparison.Ordinal)) return ch.IntensityFormula;
            return ch.SmoothnessFormula;
        }
        private static void SetLfeFormula(BaseLfeSettings lfe, string tag, string formula)
        {
            var ch = LfeChannelForTag(lfe, tag);
            if (tag.EndsWith("trigger", StringComparison.Ordinal)) ch.TriggerFormula = formula;
            else if (tag.EndsWith("frequency", StringComparison.Ordinal)) ch.FrequencyFormula = formula;
            else if (tag.EndsWith("intensity", StringComparison.Ordinal)) ch.IntensityFormula = formula;
            else ch.SmoothnessFormula = formula;
        }

        // Stored-string <-> ExpressionValue (mirror MBoosterCustomEffectRow).
        private static ExpressionValue LfeMakeExpression(string? stored)
        {
            var ev = new ExpressionValue();
            var s = (stored ?? "").Trim();
            if (s.Length == 0) { ev.UseJavascript = false; ev.Expression = ""; }
            else if (s.StartsWith("js:", StringComparison.OrdinalIgnoreCase)) { ev.UseJavascript = true; ev.Expression = s.Substring(3); }
            else { ev.UseJavascript = false; ev.Expression = global::MozaPlugin.Telemetry.NCalcExpressionEvaluator.LooksLikeExpression(s) ? s : "[" + s + "]"; }
            return ev;
        }
        private static string LfeSerializeExpression(ExpressionValue ev)
        {
            var expr = (ev?.Expression ?? "").Trim();
            if (expr.Length == 0) return "";
            if (ev!.UseJavascript) return "js:" + expr;
            if (expr.Length >= 2 && expr[0] == '[' && expr[expr.Length - 1] == ']')
            {
                var inner = expr.Substring(1, expr.Length - 2);
                if (inner.IndexOf('[') < 0 && inner.IndexOf(']') < 0
                    && !global::MozaPlugin.Telemetry.NCalcExpressionEvaluator.LooksLikeExpression(inner))
                    return inner;
            }
            return expr;
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            SpeedValue.Text = $"{pct}%";
            _data.Speed = raw;
            _plugin.WriteIfBaseConnected("base-speed", raw);
            _plugin.SaveSettings();
        }

        private void DamperSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            DamperValue.Text = $"{pct}%";
            _data.Damper = raw;
            _plugin.WriteIfBaseConnected("base-damper", raw);
            _plugin.SaveSettings();
        }

        private void FrictionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            FrictionValue.Text = $"{pct}%";
            _data.Friction = raw;
            _plugin.WriteIfBaseConnected("base-friction", raw);
            _plugin.SaveSettings();
        }

        private void InertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            int raw = val * 10;
            InertiaValue.Text = $"{val}";
            _data.Inertia = raw;
            _plugin.WriteIfBaseConnected("base-inertia", raw);
            _plugin.SaveSettings();
        }

        private void SpringSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            SpringValue.Text = $"{pct}%";
            _data.Spring = raw;
            _plugin.WriteIfBaseConnected("base-spring", raw);
            _plugin.SaveSettings();
        }

        private void GameDamperSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameDamperValue.Text = $"{pct}%";
            _data.GameDamper = raw;
            _plugin.WriteIfBaseConnected("main-set-damper-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameFrictionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameFrictionValue.Text = $"{pct}%";
            _data.GameFriction = raw;
            _plugin.WriteIfBaseConnected("main-set-friction-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameInertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameInertiaValue.Text = $"{pct}%";
            _data.GameInertia = raw;
            _plugin.WriteIfBaseConnected("main-set-inertia-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameSpringSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameSpringValue.Text = $"{pct}%";
            _data.GameSpring = raw;
            _plugin.WriteIfBaseConnected("main-set-spring-gain", raw);
            _plugin.SaveSettings();
        }

        private void SpeedDampingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            SpeedDampingValue.Text = $"{val}%";
            _data.SpeedDamping = val;
            _plugin.WriteIfBaseConnected("base-speed-damping", val);
            _plugin.SaveSettings();
        }

        private void SpeedDampingPointSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            SpeedDampingPointValue.Text = $"{val} kph";
            _data.SpeedDampingPoint = val;
            _plugin.WriteIfBaseConnected("base-speed-damping-point", val);
            _plugin.SaveSettings();
        }

        private void NaturalInertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            NaturalInertiaValue.Text = $"{val}";
            _data.NaturalInertia = val;
            _plugin.WriteIfBaseConnected("base-natural-inertia", val);
            _plugin.SaveSettings();
        }

        private void SoftLimitStiffnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int display = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(display * (400.0 / 9.0) - (400.0 / 9.0) + 100.0);
            SoftLimitStiffnessValue.Text = $"{display}";
            _data.SoftLimitStiffness = raw;
            _plugin.WriteIfBaseConnected("base-soft-limit-stiffness", raw);
            _plugin.SaveSettings();
        }

        // ===== Checkbox handlers =====

        private void FfbReverseCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = FfbReverseCheck.IsChecked == true ? 1 : 0;
            _data.FfbReverse = val;
            _plugin.WriteIfBaseConnected("base-ffb-reverse", val);
            _plugin.SaveSettings();
        }

        private void ProtectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = ProtectionCheck.IsChecked == true ? 1 : 0;
            _data.Protection = val;
            _plugin.WriteIfBaseConnected("base-protection", val);
            _plugin.SaveSettings();
        }

        private void SoftLimitRetainCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = SoftLimitRetainCheck.IsChecked == true ? 1 : 0;
            _data.SoftLimitRetain = val;
            _plugin.WriteIfBaseConnected("base-soft-limit-retain", val);
            _plugin.SaveSettings();
        }

        private void StandbyCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = StandbyCheck.IsChecked == true ? 1 : 0;
            _data.WorkMode = val;
            _plugin.WriteIfBaseConnected("main-set-work-mode", val);
            _plugin.SaveSettings();
        }

        private void AutoStandbyTimeoutCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (!(AutoStandbyTimeoutCombo.SelectedItem is ComboBoxItem item
                  && item.Tag is string tag && int.TryParse(tag, out int minutes)))
                return;

            if (minutes <= 0)
            {
                // "Disabled" — turn auto-standby off and wake the base if we put
                // it to sleep. The timeout value is left as-is for next time.
                _plugin.Settings.AutoStandbyWhenNoGame = false;
                _plugin.SaveSettings();
                _plugin.CancelAutoStandby();
            }
            else
            {
                _plugin.Settings.AutoStandbyWhenNoGame = true;
                _plugin.Settings.AutoStandbyTimeoutMinutes = minutes;
                _plugin.SaveSettings();
                // Selecting a timeout counts as activity so we never standby
                // immediately; the idle timer starts fresh from here.
                _plugin.NotifyUserActivity();
                _plugin.ApplyAutoStandby();
            }
        }

        // Selects "Disabled" when auto-standby is off, else the saved timeout.
        private void SyncAutoStandbyCombo()
        {
            int target = _plugin.Settings.AutoStandbyWhenNoGame
                ? _plugin.Settings.AutoStandbyTimeoutMinutes
                : 0;
            foreach (var obj in AutoStandbyTimeoutCombo.Items)
            {
                if (obj is ComboBoxItem it && it.Tag is string t
                    && int.TryParse(t, out int m) && m == target)
                {
                    AutoStandbyTimeoutCombo.SelectedItem = it;
                    return;
                }
            }
        }

        private void LedStatusCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = LedStatusCheck.IsChecked == true ? 1 : 0;
            _data.LedStatus = val;
            _plugin.WriteIfBaseConnected("main-set-led-status", val);
            _plugin.SaveSettings();
        }

        // ===== RPM range slider handlers =====

        // ===== Handbrake tab =====

        // ===== Handbrake Range + Curve + Calibration =====

        private void RefreshHandbrakeTab()
        {
            bool detected = _plugin.IsHandbrakeDetected;
            HandbrakeTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;

            if (!detected) return;

            SetComboSafe(HandbrakeModeCombo, _data.HandbrakeMode);

            bool buttonMode = _data.HandbrakeMode == 1;
            HandbrakeThresholdPanel.Visibility = buttonMode ? Visibility.Visible : Visibility.Collapsed;
            HandbrakeAxisPanel.Visibility = buttonMode ? Visibility.Collapsed : Visibility.Visible;
            HandbrakeButtonStatus.Visibility = buttonMode ? Visibility.Visible : Visibility.Collapsed;

            HandbrakeThresholdSlider.Value = Clamp(_data.HandbrakeButtonThreshold, 0, 100);
            SetValueText(HandbrakeThresholdValue, $"{_data.HandbrakeButtonThreshold}%");

            HandbrakeDirectionCheck.IsChecked = _data.HandbrakeDirection != 0;

            HandbrakeMinSlider.Value = Clamp(_data.HandbrakeMin, 0, 100);
            SetValueText(HandbrakeMinValue, $"{_data.HandbrakeMin}%");
            HandbrakeMaxSlider.Value = Clamp(_data.HandbrakeMax, 0, 100);
            SetValueText(HandbrakeMaxValue, $"{_data.HandbrakeMax}%");

            SetSliderRaw(HbY1Slider, HbY1Value, _data.HandbrakeCurve[0], 0, 100, "");
            SetSliderRaw(HbY2Slider, HbY2Value, _data.HandbrakeCurve[1], 0, 100, "");
            SetSliderRaw(HbY3Slider, HbY3Value, _data.HandbrakeCurve[2], 0, 100, "");
            SetSliderRaw(HbY4Slider, HbY4Value, _data.HandbrakeCurve[3], 0, 100, "");
            SetSliderRaw(HbY5Slider, HbY5Value, _data.HandbrakeCurve[4], 0, 100, "");
        }

        private void HandbrakeModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = HandbrakeModeCombo.SelectedIndex;
            _data.HandbrakeMode = val;
            _plugin.WriteIfHandbrakeDetected("handbrake-mode", val);
            _plugin.SaveSettings();
        }

        private void HandbrakeThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            HandbrakeThresholdValue.Text = $"{val}%";
            _data.HandbrakeButtonThreshold = val;
            _plugin.WriteIfHandbrakeDetected("handbrake-button-threshold", val);
            _plugin.SaveSettings();
        }

        private void HandbrakeDirectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = HandbrakeDirectionCheck.IsChecked == true ? 1 : 0;
            _data.HandbrakeDirection = val;
            _plugin.WriteIfHandbrakeDetected("handbrake-direction", val);
            _plugin.SaveSettings();
        }

        // ===== Connection toggle =====

        private void ConnectionToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.SetConnectionEnabled(ConnectionToggle.IsChecked == true);
        }

        private void SoftRebootButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                Strings.Dialog_RestartWheelbase_Body,
                Strings.Dialog_RestartWheelbase_Caption,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;
            _plugin.WriteIfBaseConnected("main-soft-reboot", 1);
        }

        // ===== Helpers =====

        // SetSliderPercent, SetSliderRaw, SetComboSafe, Clamp moved to UI/UiHelpers.

        private double ConvertTemp(int raw)
        {
            double celsius = raw / 100.0;
            return _data.UseFahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;
        }

        // ===== Generic slider-handler helpers =====

        // Most int-valued sliders share the same body: drop the event if a refresh
        // is mid-flight, round the new value, paint the label, commit it to the
        // data model + device, then queue a settings save. The per-slider commit
        // lambda captures which data field and which device command to use.
        private void OnIntSliderChanged(double newValue, TextBox label, string suffix,
            Action<int> commit)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(newValue);
            label.Text = $"{v}{suffix}";
            commit(v);
            _plugin.SaveSettings();
        }

        // Min/max pair sliders additionally clamp against the sibling bound and
        // bounce the slider back without re-firing this handler.
        private void OnMinMaxSliderChanged(double newValue, Slider self, int otherBound,
            bool isMin, TextBox label, Action<int> commit)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(newValue);
            if (isMin ? v > otherBound : v < otherBound)
            {
                v = otherBound;
                using (_suppressor.Begin()) self.Value = v;
            }
            label.Text = $"{v}%";
            commit(v);
            _plugin.SaveSettings();
        }

        // ===== Slider value box (keyed entry) =====

        // Enter inside KeyDown also moves focus off the box, which then fires
        // LostFocus — we'd commit the same edit twice. Track the most-recent
        // KeyDown-commit so the immediately-following LostFocus is a no-op.
        private TextBox? _suppressLostFocusFor;

        // GotFocus strips the unit suffix (e.g. "100%" → "100", "120 kph" →
        // "120") and selects the digits so the user can only edit the numeric
        // portion. The canonical "{value}{suffix}" form is restored by the
        // slider's ValueChanged handler on commit.
        private void SliderValueBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box) return;
            string raw = box.Text ?? string.Empty;
            string numeric = ExtractNumericPrefix(raw);
            if (numeric != raw) box.Text = numeric;
            box.SelectAll();
        }

        // Pressing Enter while focused on a SliderValueEditBox parses the
        // user's input and pushes it back to the paired slider — which then
        // fires its existing ValueChanged → On*SliderChanged → hardware-write
        // pipeline. Tag is bound (ElementName) to the matching Slider element.
        private void SliderValueBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return) return;
            var box = sender as TextBox;
            ApplyEditedSliderValue(box);
            _suppressLostFocusFor = box;
            // Move focus off so the user sees the canonical re-formatted text.
            box?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }

        private void SliderValueBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var box = sender as TextBox;
            if (box != null && box == _suppressLostFocusFor)
            {
                _suppressLostFocusFor = null;
                return;
            }
            ApplyEditedSliderValue(box);
        }

        private void ApplyEditedSliderValue(TextBox? box)
        {
            if (box == null || box.Tag is not Slider slider) return;

            string token = ExtractNumericPrefix(box.Text ?? string.Empty);
            bool parsed = double.TryParse(token, System.Globalization.NumberStyles.Float,
                                          System.Globalization.CultureInfo.InvariantCulture,
                                          out double parsedValue);

            // Target slider value:
            //   • Valid input → clamped + integer-snapped parse result.
            //   • Empty / invalid input → keep the current slider value; the
            //     bump dance below still fires ValueChanged so the canonical
            //     text gets repainted.
            double target = parsed
                ? Math.Round(Math.Max(slider.Minimum, Math.Min(slider.Maximum, parsedValue)))
                : slider.Value;

            // ValueChanged is only raised when Value actually changes. If our
            // target matches the current value (same number re-typed or invalid
            // input), force a fire via a tiny bump-and-snap with the event
            // suppressor active for the bumped value — the snap-back assignment
            // then runs the handler and repaints the canonical text.
            if (slider.Value == target)
            {
                double offset = (target < slider.Maximum) ? target + 0.0001 : target - 0.0001;
                using (_suppressor.Begin()) slider.Value = offset;
            }
            slider.Value = target;
        }

        // Leading numeric token — accepts an optional sign and a single
        // decimal point, so "120 kph", "100%", " -3.5°", "1100" all parse to
        // the digit portion. Empty string when no numeric prefix is present.
        private static string ExtractNumericPrefix(string raw)
        {
            int i = 0, n = raw.Length;
            while (i < n && char.IsWhiteSpace(raw[i])) i++;
            int start = i;
            if (i < n && (raw[i] == '-' || raw[i] == '+')) i++;
            bool sawDot = false;
            while (i < n)
            {
                char c = raw[i];
                if (char.IsDigit(c)) { i++; continue; }
                if (c == '.' && !sawDot) { sawDot = true; i++; continue; }
                break;
            }
            return (i > start) ? raw.Substring(start, i - start) : string.Empty;
        }

        // ===== FFB Equalizer handlers =====

        // EQ write commands in register order. Shared with the AZOM step
        // actions so the button macros and the bindings drive identical values.
        private static readonly string[] EqCommands = BaseSettingCatalog.EqRegisterCommands;

        private void Eq1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq1Value, "%", v => { _data.Equalizer1 = v; _plugin.WriteIfBaseConnected(EqCommands[0], v); });
        private void Eq2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq2Value, "%", v => { _data.Equalizer2 = v; _plugin.WriteIfBaseConnected(EqCommands[1], v); });
        private void Eq3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq3Value, "%", v => { _data.Equalizer3 = v; _plugin.WriteIfBaseConnected(EqCommands[2], v); });
        private void Eq4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq4Value, "%", v => { _data.Equalizer4 = v; _plugin.WriteIfBaseConnected(EqCommands[3], v); });
        private void Eq5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq5Value, "%", v => { _data.Equalizer5 = v; _plugin.WriteIfBaseConnected(EqCommands[4], v); });
        private void Eq6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq6Value, "%", v => { _data.Equalizer6 = v; _plugin.WriteIfBaseConnected(EqCommands[5], v); });
        private void Eq7Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq7Value, "%", v => { _data.Equalizer7 = v; _plugin.WriteIfBaseConnected(EqCommands[6], v); });
        private void Eq8Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq8Value, "%", v => { _data.Equalizer8 = v; _plugin.WriteIfBaseConnected(EqCommands[7], v); });
        private void Eq9Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq9Value, "%", v => { _data.Equalizer9 = v; _plugin.WriteIfBaseConnected(EqCommands[8], v); });
        private void Eq10Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, Eq10Value, "%", v => { _data.Equalizer10 = v; _plugin.WriteIfBaseConnected(EqCommands[9], v); });

        // 10-band mappings in FREQUENCY order (5/10/15/25/30/40/50/60/80/100 Hz)
        // — the new registers interleave. Keep in sync with the FfbEqualizer10
        // slider binding in SettingsControl.Redesign.cs.
        private static readonly string[] Eq10Commands = BaseSettingCatalog.Eq10FreqOrderCommands;
        private Slider[] Eq10Sliders() => new[] {
            Eq1Slider, Eq7Slider, Eq2Slider, Eq3Slider, Eq8Slider,
            Eq4Slider, Eq9Slider, Eq5Slider, Eq10Slider, Eq6Slider };
        private TextBox[] Eq10Labels() => new[] {
            Eq1Value, Eq7Value, Eq2Value, Eq3Value, Eq8Value,
            Eq4Value, Eq9Value, Eq5Value, Eq10Value, Eq6Value };
        private Action<int>[] Eq10DataSetters() => new Action<int>[] {
            v => _data.Equalizer1 = v, v => _data.Equalizer7 = v,
            v => _data.Equalizer2 = v, v => _data.Equalizer3 = v,
            v => _data.Equalizer8 = v, v => _data.Equalizer4 = v,
            v => _data.Equalizer9 = v, v => _data.Equalizer5 = v,
            v => _data.Equalizer10 = v, v => _data.Equalizer6 = v };

        // Swap the EQ card between the 6-band and 10-band presentations. Runs
        // inside the refresh tick's suppressor, so the slider Maximum coercion
        // on a mode flip never reaches the device-write path.
        private bool? _eq10ModeApplied;
        private void ApplyEqBandMode(bool eq10)
        {
            if (_eq10ModeApplied == eq10) return;
            _eq10ModeApplied = eq10;
            FfbEqualizer.Visibility = eq10 ? Visibility.Collapsed : Visibility.Visible;
            FfbEqualizer10.Visibility = eq10 ? Visibility.Visible : Visibility.Collapsed;
            Eq1Slider.Maximum = eq10 ? 500 : 400;
            Eq2Slider.Maximum = eq10 ? 500 : 400;
            Eq3Slider.Maximum = eq10 ? 500 : 400;
            Eq4Slider.Maximum = eq10 ? 500 : 400;
            Eq5Slider.Maximum = eq10 ? 500 : 400;
            Eq6Slider.Maximum = eq10 ? 100 : 400;
            FfbEqCard.Subtitle = eq10 ? Strings.Subtitle_FfbEqualizer10 : Strings.Subtitle_FfbEqualizer;
        }

        // Apply a 6-band value set (10/15/25/40/60/100 Hz register order).
        private void ApplyFfbEqPreset(int[] p)
        {
            using (_suppressor.Begin())
            {
                Eq1Slider.Value = p[0]; Eq1Value.Text = $"{p[0]}%"; _data.Equalizer1 = p[0];
                Eq2Slider.Value = p[1]; Eq2Value.Text = $"{p[1]}%"; _data.Equalizer2 = p[1];
                Eq3Slider.Value = p[2]; Eq3Value.Text = $"{p[2]}%"; _data.Equalizer3 = p[2];
                Eq4Slider.Value = p[3]; Eq4Value.Text = $"{p[3]}%"; _data.Equalizer4 = p[3];
                Eq5Slider.Value = p[4]; Eq5Value.Text = $"{p[4]}%"; _data.Equalizer5 = p[4];
                Eq6Slider.Value = p[5]; Eq6Value.Text = $"{p[5]}%"; _data.Equalizer6 = p[5];
            }
            for (int i = 0; i < 6; i++)
                _plugin.WriteIfBaseConnected(EqCommands[i], p[i]);
            _plugin.SaveSettings();
        }

        private void ApplyFfbEqPreset10(int[] p)
        {
            var sliders = Eq10Sliders();
            var labels = Eq10Labels();
            var setters = Eq10DataSetters();
            using (_suppressor.Begin())
            {
                for (int i = 0; i < 10; i++)
                {
                    sliders[i].Value = p[i];
                    labels[i].Text = $"{p[i]}%";
                    setters[i](p[i]);
                }
            }
            for (int i = 0; i < 10; i++)
                _plugin.WriteIfBaseConnected(Eq10Commands[i], p[i]);
            _plugin.SaveSettings();
        }

        // PitHouse "sensitivity" presets 0..10 — one-shot macros writing
        // road-sensitivity (0x0C = 10 + 4*N) plus a canned EQ curve; no
        // dedicated sensitivity register exists, so the buttons are momentary.
        // Values in frequency order 5/10/15/25/30/40/50/60/80/100 Hz. On
        // legacy firmware only the six old registers are written (columns
        // via Eq6FreqColumns) — the four new bands are skipped.
        private static readonly int[][] EqSensitivityPresets = BaseSettingCatalog.EqSensitivityPresets;

        // Frequency-order columns carried by the legacy registers Eq1..Eq6
        // (5/15/25/40/60/100 Hz).
        private static readonly int[] Eq6FreqColumns = BaseSettingCatalog.Eq6FreqColumns;

        private void EqSensitivity_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !int.TryParse(b.Tag as string, out int n)
                || n < 0 || n > 10)
                return;

            int sensitivity = 10 + 4 * n;
            _data.RoadSensitivity = sensitivity;
            _plugin.WriteIfBaseConnected("base-road-sensitivity", sensitivity);

            int[] p = EqSensitivityPresets[n];
            if (_data.BaseSupportsEq10)
            {
                ApplyFfbEqPreset10(p);
            }
            else
            {
                var six = new int[6];
                for (int i = 0; i < 6; i++) six[i] = p[Eq6FreqColumns[i]];
                ApplyFfbEqPreset(six);
            }
        }

        // ===== FFB Curve handlers =====

        // Presets: [Y1, Y2, Y3, Y4, Y5] — X breakpoints are fixed at [20, 40, 60, 80]
        private static readonly int[][] FfbCurvePresets =
        {
            new[] { 20, 40, 60, 80, 100 }, // Linear
            new[] {  8, 24, 76, 92, 100 }, // S-Curve
            new[] {  6, 14, 28, 54, 100 }, // Exponential
            new[] { 46, 72, 86, 94, 100 }, // Parabolic
        };

        private void ApplyFfbCurvePreset(int[] p)
        {
            using (_suppressor.Begin())
            {
                // Presets are Y-shapes defined at the standard breakpoints, so
                // snap any dragged X positions back to 20/40/60/80.
                FfbCurveX1Slider.Value = 20; FfbCurveX1Value.Text = "20"; _data.FfbCurveX1 = 20;
                FfbCurveX2Slider.Value = 40; FfbCurveX2Value.Text = "40"; _data.FfbCurveX2 = 40;
                FfbCurveX3Slider.Value = 60; FfbCurveX3Value.Text = "60"; _data.FfbCurveX3 = 60;
                FfbCurveX4Slider.Value = 80; FfbCurveX4Value.Text = "80"; _data.FfbCurveX4 = 80;
                FfbCurveY1Slider.Value = p[0]; FfbCurveY1Value.Text = $"{p[0]}"; _data.FfbCurveY1 = p[0];
                FfbCurveY2Slider.Value = p[1]; FfbCurveY2Value.Text = $"{p[1]}"; _data.FfbCurveY2 = p[1];
                FfbCurveY3Slider.Value = p[2]; FfbCurveY3Value.Text = $"{p[2]}"; _data.FfbCurveY3 = p[2];
                FfbCurveY4Slider.Value = p[3]; FfbCurveY4Value.Text = $"{p[3]}"; _data.FfbCurveY4 = p[3];
                FfbCurveY5Slider.Value = p[4]; FfbCurveY5Value.Text = $"{p[4]}"; _data.FfbCurveY5 = p[4];
            }
            // Always write fixed X breakpoints first
            _plugin.WriteIfBaseConnected("base-ffb-curve-x1", 20); _plugin.WriteIfBaseConnected("base-ffb-curve-x2", 40);
            _plugin.WriteIfBaseConnected("base-ffb-curve-x3", 60); _plugin.WriteIfBaseConnected("base-ffb-curve-x4", 80);
            _plugin.WriteIfBaseConnected("base-ffb-curve-y1", p[0]); _plugin.WriteIfBaseConnected("base-ffb-curve-y2", p[1]);
            _plugin.WriteIfBaseConnected("base-ffb-curve-y3", p[2]); _plugin.WriteIfBaseConnected("base-ffb-curve-y4", p[3]);
            _plugin.WriteIfBaseConnected("base-ffb-curve-y5", p[4]);
            _plugin.SaveSettings();
        }

        private void FfbCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[0]);
        private void FfbCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[1]);
        private void FfbCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[2]);
        private void FfbCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[3]);

        private void FfbCurveX1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveX1Value, "", v => { _data.FfbCurveX1 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-x1", v); });
        private void FfbCurveX2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveX2Value, "", v => { _data.FfbCurveX2 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-x2", v); });
        private void FfbCurveX3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveX3Value, "", v => { _data.FfbCurveX3 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-x3", v); });
        private void FfbCurveX4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveX4Value, "", v => { _data.FfbCurveX4 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-x4", v); });
        private void FfbCurveY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY1Value, "", v => { _data.FfbCurveY1 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-y1", v); });
        private void FfbCurveY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY2Value, "", v => { _data.FfbCurveY2 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-y2", v); });
        private void FfbCurveY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY3Value, "", v => { _data.FfbCurveY3 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-y3", v); });
        private void FfbCurveY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY4Value, "", v => { _data.FfbCurveY4 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-y4", v); });
        private void FfbCurveY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, FfbCurveY5Value, "", v => { _data.FfbCurveY5 = v; _plugin.WriteIfBaseConnected("base-ffb-curve-y5", v); });

        // ===== Bluetooth + Base Calibration =====

        private void BluetoothCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = BluetoothCheck.IsChecked == true ? 0 : 85;
            _data.BleMode = val;
            _plugin.WriteIfBaseConnected("main-set-ble-mode", val);
            _plugin.SaveSettings();
        }

        private void BaseCalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.WriteIfBaseConnected("base-calibration", 1);
            BaseCalibrateStatus.Text = Strings.Status_CalibrationSent;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, _) => { BaseCalibrateStatus.Text = ""; ((DispatcherTimer)s!).Stop(); };
            timer.Start();
        }

        // ===== Handbrake Range + Curve + Calibration =====

        private static readonly int[][] HbCurvePresets =
        {
            new[] { 20, 40,  60,  80, 100 }, // Linear
            new[] {  8, 24,  76,  92, 100 }, // S Curve
            new[] {  6, 14,  28,  54, 100 }, // Exponential
            new[] { 46, 72,  86,  94, 100 }, // Parabolic
        };

        private void ApplyHbCurvePreset(int[] p)
        {
            using (_suppressor.Begin())
            {
                HbY1Slider.Value = p[0]; HbY1Value.Text = $"{p[0]}"; _data.HandbrakeCurve[0] = p[0];
                HbY2Slider.Value = p[1]; HbY2Value.Text = $"{p[1]}"; _data.HandbrakeCurve[1] = p[1];
                HbY3Slider.Value = p[2]; HbY3Value.Text = $"{p[2]}"; _data.HandbrakeCurve[2] = p[2];
                HbY4Slider.Value = p[3]; HbY4Value.Text = $"{p[3]}"; _data.HandbrakeCurve[3] = p[3];
                HbY5Slider.Value = p[4]; HbY5Value.Text = $"{p[4]}"; _data.HandbrakeCurve[4] = p[4];
            }
            for (int i = 0; i < 5; i++)
                _plugin.WriteFloatIfHandbrakeDetected($"handbrake-y{i + 1}", p[i]);
            _plugin.SaveSettings();
        }

        private void HbCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[0]);
        private void HbCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[1]);
        private void HbCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[2]);
        private void HbCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[3]);

        private void HandbrakeMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, HandbrakeMinSlider, _data.HandbrakeMax, isMin: true, HandbrakeMinValue, v => { _data.HandbrakeMin = v; _plugin.WriteIfHandbrakeDetected("handbrake-min", v); });
        private void HandbrakeMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, HandbrakeMaxSlider, _data.HandbrakeMin, isMin: false, HandbrakeMaxValue, v => { _data.HandbrakeMax = v; _plugin.WriteIfHandbrakeDetected("handbrake-max", v); });
        private void HbY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY1Value, "", v => { _data.HandbrakeCurve[0] = v; _plugin.WriteFloatIfHandbrakeDetected("handbrake-y1", v); });
        private void HbY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY2Value, "", v => { _data.HandbrakeCurve[1] = v; _plugin.WriteFloatIfHandbrakeDetected("handbrake-y2", v); });
        private void HbY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY3Value, "", v => { _data.HandbrakeCurve[2] = v; _plugin.WriteFloatIfHandbrakeDetected("handbrake-y3", v); });
        private void HbY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY4Value, "", v => { _data.HandbrakeCurve[3] = v; _plugin.WriteFloatIfHandbrakeDetected("handbrake-y4", v); });
        private void HbY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, HbY5Value, "", v => { _data.HandbrakeCurve[4] = v; _plugin.WriteFloatIfHandbrakeDetected("handbrake-y5", v); });

        private const int CalibrationSeconds = 5;

        // Shared single-button calibration flow: send start, show a live
        // countdown instruction, then auto-send stop after CalibrationSeconds.
        private void RunCalibrationCountdown(Button startButton, TextBlock status,
            string instructionFormat, Action sendStart, Action sendStop)
        {
            sendStart();
            startButton.IsEnabled = false;
            int remaining = CalibrationSeconds;
            status.Text = string.Format(instructionFormat, remaining);
            status.Visibility = Visibility.Visible;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, _) =>
            {
                remaining--;
                if (remaining > 0)
                {
                    status.Text = string.Format(instructionFormat, remaining);
                    return;
                }
                ((DispatcherTimer)s!).Stop();
                sendStop();
                status.Text = Strings.Status_Done;
                startButton.IsEnabled = true;
            };
            timer.Start();
        }

        private void HbCalStartButton_Click(object sender, RoutedEventArgs e) =>
            RunCalibrationCountdown(HbCalStartButton, HbCalStatus, Strings.Hint_CalibrateHandbrake,
                () => _plugin.WriteIfHandbrakeDetected("handbrake-cal-start", 1),
                () => _plugin.WriteIfHandbrakeDetected("handbrake-cal-stop", 1));

        // ===== Pedals Tab =====

        // Presets: [Y1, Y2, Y3, Y4, Y5]
        private static readonly int[][] PedalCurvePresets =
        {
            new[] { 20, 40,  60,  80, 100 }, // Linear
            new[] {  8, 24,  76,  92, 100 }, // S Curve
            new[] {  6, 14,  28,  54, 100 }, // Exponential
            new[] { 46, 72,  86,  94, 100 }, // Parabolic
        };

        private void RefreshPedalsTab()
        {
            bool detected = _plugin.IsPedalsDetected;
            PedalsTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;
            if (!detected) return;

            ThrottleDirCheck.IsChecked = _data.PedalsThrottleDir != 0;
            ThrottleMinSlider.Value = Clamp(_data.PedalsThrottleMin, 0, 100);
            SetValueText(ThrottleMinValue, $"{_data.PedalsThrottleMin}%");
            ThrottleMaxSlider.Value = Clamp(_data.PedalsThrottleMax, 0, 100);
            SetValueText(ThrottleMaxValue, $"{_data.PedalsThrottleMax}%");
            SetSliderRaw(ThrottleY1Slider, ThrottleY1Value, _data.PedalsThrottleCurve[0], 0, 100, "");
            SetSliderRaw(ThrottleY2Slider, ThrottleY2Value, _data.PedalsThrottleCurve[1], 0, 100, "");
            SetSliderRaw(ThrottleY3Slider, ThrottleY3Value, _data.PedalsThrottleCurve[2], 0, 100, "");
            SetSliderRaw(ThrottleY4Slider, ThrottleY4Value, _data.PedalsThrottleCurve[3], 0, 100, "");
            SetSliderRaw(ThrottleY5Slider, ThrottleY5Value, _data.PedalsThrottleCurve[4], 0, 100, "");

            BrakeDirCheck.IsChecked = _data.PedalsBrakeDir != 0;
            BrakeMinSlider.Value = Clamp(_data.PedalsBrakeMin, 0, 100);
            SetValueText(BrakeMinValue, $"{_data.PedalsBrakeMin}%");
            BrakeMaxSlider.Value = Clamp(_data.PedalsBrakeMax, 0, 100);
            SetValueText(BrakeMaxValue, $"{_data.PedalsBrakeMax}%");
            BrakeAngleRatioSlider.Value = Clamp(_data.PedalsBrakeAngleRatio, 0, 100);
            SetValueText(BrakeAngleRatioValue, $"{_data.PedalsBrakeAngleRatio}%");
            SetSliderRaw(BrakeY1Slider, BrakeY1Value, _data.PedalsBrakeCurve[0], 0, 100, "");
            SetSliderRaw(BrakeY2Slider, BrakeY2Value, _data.PedalsBrakeCurve[1], 0, 100, "");
            SetSliderRaw(BrakeY3Slider, BrakeY3Value, _data.PedalsBrakeCurve[2], 0, 100, "");
            SetSliderRaw(BrakeY4Slider, BrakeY4Value, _data.PedalsBrakeCurve[3], 0, 100, "");
            SetSliderRaw(BrakeY5Slider, BrakeY5Value, _data.PedalsBrakeCurve[4], 0, 100, "");

            ClutchDirCheck.IsChecked = _data.PedalsClutchDir != 0;
            ClutchMinSlider.Value = Clamp(_data.PedalsClutchMin, 0, 100);
            SetValueText(ClutchMinValue, $"{_data.PedalsClutchMin}%");
            ClutchMaxSlider.Value = Clamp(_data.PedalsClutchMax, 0, 100);
            SetValueText(ClutchMaxValue, $"{_data.PedalsClutchMax}%");
            SetSliderRaw(ClutchY1Slider, ClutchY1Value, _data.PedalsClutchCurve[0], 0, 100, "");
            SetSliderRaw(ClutchY2Slider, ClutchY2Value, _data.PedalsClutchCurve[1], 0, 100, "");
            SetSliderRaw(ClutchY3Slider, ClutchY3Value, _data.PedalsClutchCurve[2], 0, 100, "");
            SetSliderRaw(ClutchY4Slider, ClutchY4Value, _data.PedalsClutchCurve[3], 0, 100, "");
            SetSliderRaw(ClutchY5Slider, ClutchY5Value, _data.PedalsClutchCurve[4], 0, 100, "");
        }

        // ===== Hub Tab =====

        private void RefreshHubTab()
        {
            bool detected = _plugin.IsHubDetected;
            HubTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;
            if (!detected) return;

            // Pedals port: high byte >= 1 means connected (foxblat convention)
            UpdateHubPortIndicator(HubPedals1Dot, HubPedals1Label, _data.HubPedals1Power, isPedals: true);

            // Accessory ports: value <= 1 means connected
            UpdateHubPortIndicator(HubPort1Dot, HubPort1Label, _data.HubPort1Power, isPedals: false);
            UpdateHubPortIndicator(HubPort2Dot, HubPort2Label, _data.HubPort2Power, isPedals: false);
            UpdateHubPortIndicator(HubPort3Dot, HubPort3Label, _data.HubPort3Power, isPedals: false);
        }

        private static void UpdateHubPortIndicator(Ellipse dot, TextBlock label, int value, bool isPedals)
        {
            if (value < 0)
            {
                dot.Fill = Brushes.Gray;
                label.Text = "--";
                return;
            }

            bool connected = isPedals ? (value >> 8) >= 1 : value <= 1;
            dot.Fill = connected ? Brushes.LimeGreen : Brushes.Gray;
            label.Text = connected ? "Connected" : "Disconnected";
        }

        private void ApplyPedalCurvePreset(string pedal, int[] curve, int[] dataArray,
            Slider[] sliders, TextBox[] labels)
        {
            using (_suppressor.Begin())
            {
                for (int i = 0; i < 5; i++)
                {
                    sliders[i].Value = curve[i];
                    labels[i].Text = $"{curve[i]}";
                    dataArray[i] = curve[i];
                }
            }
            for (int i = 0; i < 5; i++)
                _plugin.WriteFloatIfPedalsDetected($"pedals-{pedal}-y{i + 1}", curve[i]);
            _plugin.SaveSettings();
        }

        // Throttle presets
        private void ThrottleCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("throttle", PedalCurvePresets[0], _data.PedalsThrottleCurve, _throttleCurveSliders!, _throttleCurveLabels!);
        private void ThrottleCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("throttle", PedalCurvePresets[1], _data.PedalsThrottleCurve, _throttleCurveSliders!, _throttleCurveLabels!);
        private void ThrottleCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("throttle", PedalCurvePresets[2], _data.PedalsThrottleCurve, _throttleCurveSliders!, _throttleCurveLabels!);
        private void ThrottleCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyPedalCurvePreset("throttle", PedalCurvePresets[3], _data.PedalsThrottleCurve, _throttleCurveSliders!, _throttleCurveLabels!);

        // Brake presets
        private void BrakeCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("brake", PedalCurvePresets[0], _data.PedalsBrakeCurve, _brakeCurveSliders!, _brakeCurveLabels!);
        private void BrakeCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("brake", PedalCurvePresets[1], _data.PedalsBrakeCurve, _brakeCurveSliders!, _brakeCurveLabels!);
        private void BrakeCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("brake", PedalCurvePresets[2], _data.PedalsBrakeCurve, _brakeCurveSliders!, _brakeCurveLabels!);
        private void BrakeCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyPedalCurvePreset("brake", PedalCurvePresets[3], _data.PedalsBrakeCurve, _brakeCurveSliders!, _brakeCurveLabels!);

        // Clutch presets
        private void ClutchCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("clutch", PedalCurvePresets[0], _data.PedalsClutchCurve, _clutchCurveSliders!, _clutchCurveLabels!);
        private void ClutchCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyPedalCurvePreset("clutch", PedalCurvePresets[1], _data.PedalsClutchCurve, _clutchCurveSliders!, _clutchCurveLabels!);
        private void ClutchCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("clutch", PedalCurvePresets[2], _data.PedalsClutchCurve, _clutchCurveSliders!, _clutchCurveLabels!);
        private void ClutchCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyPedalCurvePreset("clutch", PedalCurvePresets[3], _data.PedalsClutchCurve, _clutchCurveSliders!, _clutchCurveLabels!);

        // Throttle direction + range + curve sliders
        private void ThrottleDirCheck_Click(object sender, RoutedEventArgs e) { if (_suppressEvents) return; int v = ThrottleDirCheck.IsChecked == true ? 1 : 0; _data.PedalsThrottleDir = v; _plugin.WriteIfPedalsDetected("pedals-throttle-dir", v); _plugin.SaveSettings(); }
        private void ThrottleMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, ThrottleMinSlider, _data.PedalsThrottleMax, isMin: true, ThrottleMinValue, v => { _data.PedalsThrottleMin = v; _plugin.WriteIfPedalsDetected("pedals-throttle-min", v); });
        private void ThrottleMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, ThrottleMaxSlider, _data.PedalsThrottleMin, isMin: false, ThrottleMaxValue, v => { _data.PedalsThrottleMax = v; _plugin.WriteIfPedalsDetected("pedals-throttle-max", v); });
        private void ThrottleY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ThrottleY1Value, "", v => { _data.PedalsThrottleCurve[0] = v; _plugin.WriteFloatIfPedalsDetected("pedals-throttle-y1", v); });
        private void ThrottleY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ThrottleY2Value, "", v => { _data.PedalsThrottleCurve[1] = v; _plugin.WriteFloatIfPedalsDetected("pedals-throttle-y2", v); });
        private void ThrottleY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ThrottleY3Value, "", v => { _data.PedalsThrottleCurve[2] = v; _plugin.WriteFloatIfPedalsDetected("pedals-throttle-y3", v); });
        private void ThrottleY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ThrottleY4Value, "", v => { _data.PedalsThrottleCurve[3] = v; _plugin.WriteFloatIfPedalsDetected("pedals-throttle-y4", v); });
        private void ThrottleY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ThrottleY5Value, "", v => { _data.PedalsThrottleCurve[4] = v; _plugin.WriteFloatIfPedalsDetected("pedals-throttle-y5", v); });

        // Throttle calibration
        private void ThrottleCalStartButton_Click(object sender, RoutedEventArgs e) =>
            RunCalibrationCountdown(ThrottleCalStartButton, ThrottleCalStatus, Strings.Hint_CalibratePedal,
                () => _plugin.WriteIfPedalsDetected("pedals-throttle-cal-start", 1),
                () => _plugin.WriteIfPedalsDetected("pedals-throttle-cal-stop", 1));

        // Brake direction + range + curve sliders
        private void BrakeDirCheck_Click(object sender, RoutedEventArgs e) { if (_suppressEvents) return; int v = BrakeDirCheck.IsChecked == true ? 1 : 0; _data.PedalsBrakeDir = v; _plugin.WriteIfPedalsDetected("pedals-brake-dir", v); _plugin.SaveSettings(); }
        private void BrakeAngleRatioSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeAngleRatioValue.Text = $"{v}%"; _data.PedalsBrakeAngleRatio = v; _plugin.WriteFloatIfPedalsDetected("pedals-brake-angle-ratio", v); _plugin.SaveSettings(); }
        private void BrakeMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, BrakeMinSlider, _data.PedalsBrakeMax, isMin: true, BrakeMinValue, v => { _data.PedalsBrakeMin = v; _plugin.WriteIfPedalsDetected("pedals-brake-min", v); });
        private void BrakeMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, BrakeMaxSlider, _data.PedalsBrakeMin, isMin: false, BrakeMaxValue, v => { _data.PedalsBrakeMax = v; _plugin.WriteIfPedalsDetected("pedals-brake-max", v); });
        private void BrakeY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, BrakeY1Value, "", v => { _data.PedalsBrakeCurve[0] = v; _plugin.WriteFloatIfPedalsDetected("pedals-brake-y1", v); });
        private void BrakeY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, BrakeY2Value, "", v => { _data.PedalsBrakeCurve[1] = v; _plugin.WriteFloatIfPedalsDetected("pedals-brake-y2", v); });
        private void BrakeY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, BrakeY3Value, "", v => { _data.PedalsBrakeCurve[2] = v; _plugin.WriteFloatIfPedalsDetected("pedals-brake-y3", v); });
        private void BrakeY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, BrakeY4Value, "", v => { _data.PedalsBrakeCurve[3] = v; _plugin.WriteFloatIfPedalsDetected("pedals-brake-y4", v); });
        private void BrakeY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, BrakeY5Value, "", v => { _data.PedalsBrakeCurve[4] = v; _plugin.WriteFloatIfPedalsDetected("pedals-brake-y5", v); });

        // Brake calibration
        private void BrakeCalStartButton_Click(object sender, RoutedEventArgs e) =>
            RunCalibrationCountdown(BrakeCalStartButton, BrakeCalStatus, Strings.Hint_CalibratePedal,
                () => _plugin.WriteIfPedalsDetected("pedals-brake-cal-start", 1),
                () => _plugin.WriteIfPedalsDetected("pedals-brake-cal-stop", 1));

        // Clutch direction + range + curve sliders
        private void ClutchDirCheck_Click(object sender, RoutedEventArgs e) { if (_suppressEvents) return; int v = ClutchDirCheck.IsChecked == true ? 1 : 0; _data.PedalsClutchDir = v; _plugin.WriteIfPedalsDetected("pedals-clutch-dir", v); _plugin.SaveSettings(); }
        private void ClutchMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, ClutchMinSlider, _data.PedalsClutchMax, isMin: true, ClutchMinValue, v => { _data.PedalsClutchMin = v; _plugin.WriteIfPedalsDetected("pedals-clutch-min", v); });
        private void ClutchMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnMinMaxSliderChanged(e.NewValue, ClutchMaxSlider, _data.PedalsClutchMin, isMin: false, ClutchMaxValue, v => { _data.PedalsClutchMax = v; _plugin.WriteIfPedalsDetected("pedals-clutch-max", v); });
        private void ClutchY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ClutchY1Value, "", v => { _data.PedalsClutchCurve[0] = v; _plugin.WriteFloatIfPedalsDetected("pedals-clutch-y1", v); });
        private void ClutchY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ClutchY2Value, "", v => { _data.PedalsClutchCurve[1] = v; _plugin.WriteFloatIfPedalsDetected("pedals-clutch-y2", v); });
        private void ClutchY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ClutchY3Value, "", v => { _data.PedalsClutchCurve[2] = v; _plugin.WriteFloatIfPedalsDetected("pedals-clutch-y3", v); });
        private void ClutchY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ClutchY4Value, "", v => { _data.PedalsClutchCurve[3] = v; _plugin.WriteFloatIfPedalsDetected("pedals-clutch-y4", v); });
        private void ClutchY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, ClutchY5Value, "", v => { _data.PedalsClutchCurve[4] = v; _plugin.WriteFloatIfPedalsDetected("pedals-clutch-y5", v); });

        // Clutch calibration
        private void ClutchCalStartButton_Click(object sender, RoutedEventArgs e) =>
            RunCalibrationCountdown(ClutchCalStartButton, ClutchCalStatus, Strings.Hint_CalibratePedal,
                () => _plugin.WriteIfPedalsDetected("pedals-clutch-cal-start", 1),
                () => _plugin.WriteIfPedalsDetected("pedals-clutch-cal-stop", 1));

        // ===== Options tab =====

        private void AutoApplyProfileCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.AutoApplyProfileOnLaunch = AutoApplyProfileCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void LimitWheelUpdatesCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.LimitWheelUpdates = LimitWheelUpdatesCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void AlwaysResendBitmaskCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.AlwaysResendBitmask = AlwaysResendBitmaskCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void KeepaliveTimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int sec = (int)Math.Round(e.NewValue);
            KeepaliveTimeoutValue.Text = $"{sec} s";
            _plugin.Settings.WheelKeepaliveTimeoutSec = sec;
            _plugin.SaveSettings();
        }

        private void DisableSerialProbeFallbackCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.DisableSerialProbeFallback = DisableSerialProbeFallbackCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void DisableAb9DetectionCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.DisableAb9Detection = DisableAb9DetectionCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void RedeployDefinitionsButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                Strings.Dialog_RedeployDefinitions_Body,
                Strings.Dialog_RedeployDefinitions_Caption,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            var wheelbasePid = DeviceDefinitionDeployer.ResolveWheelbasePid(_plugin.Connection);
            var deployed = DeviceDefinitionDeployer.DeployAllKnown(
                wheelbasePid, DeviceDefinitionDeployer.ResolveDashboardPid(wheelbasePid));

            if (deployed.Written > 0)
                _plugin.DeviceDefinitionDeployed = true;

            RedeployDefinitionsStatusText.Text = string.Format(
                Strings.Status_RedeployedFmt, deployed.Written, deployed.Total, wheelbasePid);
        }

        private void ClearAllSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                Strings.Dialog_ClearAllSettings_Body,
                Strings.Dialog_ClearAllSettings_Caption,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            _plugin.ClearSettings();

            using (_suppressor.Begin())
            {
                AutoApplyProfileCheck.IsChecked = _plugin.Settings.AutoApplyProfileOnLaunch;
                ShowAllTabsCheck.IsChecked = _plugin.Settings.ShowAllTabs;
                LimitWheelUpdatesCheck.IsChecked = _plugin.Settings.LimitWheelUpdates;
                ConnectionToggle.IsChecked = _plugin.Settings.ConnectionEnabled;
                ProfileListControl.DataContext = null;
                ProfileListControl.DataContext = _plugin.ProfileStore;
            }
        }

        // ===== Profile system (SimHub native) =====

        private MozaProfileStore ProfileStore => _plugin.ProfileStore;

        private void InitProfilesTab()
        {
            ProfileListControl.DataContext = ProfileStore;
        }

        // ===== Telemetry (Options tab) =====

        private bool _telemetryUIInitialized;

        private void InitTelemetryTab()
        {
            // One-shot init for controls whose state is purely settings-driven
            // and doesn't change after load (upload/download toggles — hidden
            // anyway). The firmware-era combo isn't covered here because it
            // needs to re-sync once a wheel identifies: per-page-GUID lookup
            // returns Auto before the wheel's model name is known, and the
            // settings-tab is built before that point. See RefreshTelemetryTab.
            if (!_telemetryUIInitialized)
            {
                _telemetryUIInitialized = true;
                using (_suppressor.Begin())
                {
                    var s = _plugin.Settings;
                    UploadDashboardCheck.IsChecked = s.TelemetryUploadDashboard;
                    DownloadDashboardCheck.IsChecked = s.TelemetryDownloadDashboard;
                }
            }

            RefreshTelemetryTab();
        }

        // Re-syncs UI controls that depend on per-wheel state which only
        // resolves after the wheel identifies. Safe to call repeatedly; uses
        // the suppressor to keep SelectionChanged handlers from firing on
        // programmatic writes.
        // ComboBox item order ↔ MozaWheelEra. The enum is non-contiguous
        // (value 2 is the retired Era2025 hole), so map by position rather than
        // casting the index. Keep in lockstep with the FirmwareEraCombo items
        // in SettingsControl.xaml.
        private static readonly MozaWheelEra[] EraComboOrder =
        {
            MozaWheelEra.Auto,
            MozaWheelEra.Era2024,
            MozaWheelEra.Era2026,
        };

        private void RefreshTelemetryTab()
        {
            int desired = System.Array.IndexOf(EraComboOrder, _plugin.ActiveTelemetryWheelEra);
            if (desired < 0) desired = 0; // fall back to Auto
            if (FirmwareEraCombo.SelectedIndex != desired)
            {
                using (_suppressor.Begin())
                {
                    FirmwareEraCombo.SelectedIndex = desired;
                }
            }
        }

        private void UploadDashboard_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.TelemetryUploadDashboard = UploadDashboardCheck.IsChecked == true;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

        private void DownloadDashboard_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.TelemetryDownloadDashboard = DownloadDashboardCheck.IsChecked == true;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

        private void FirmwareEra_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            // Map combo index → enum via EraComboOrder (the enum is
            // non-contiguous). -1 (no selection) falls back to Auto so the
            // plugin stays in a valid state even if the combo is deselected.
            int idx = FirmwareEraCombo.SelectedIndex;
            _plugin.ActiveTelemetryWheelEra = (idx >= 0 && idx < EraComboOrder.Length)
                ? EraComboOrder[idx]
                : MozaWheelEra.Auto;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

        // ── Diagnostics tab ─────────────────────────────────────────────
        // ===== About-tab link handlers =====

        private const string AboutGitHubUrl  = "https://github.com/giantorth/moza-simhub-plugin";
        private const string AboutDiscordUrl = "https://discord.gg/J4enw43e62";
        private const string AboutSponsorUrl = "https://github.com/sponsors/giantorth";
        private const string AboutKofiUrl    = "https://ko-fi.com/giantorth";

        private void AboutGitHubButton_Click(object sender, System.Windows.RoutedEventArgs e)  => OpenExternalUrl(AboutGitHubUrl);
        private void AboutDiscordButton_Click(object sender, System.Windows.RoutedEventArgs e) => OpenExternalUrl(AboutDiscordUrl);
        private void AboutSponsorButton_Click(object sender, System.Windows.RoutedEventArgs e) => OpenExternalUrl(AboutSponsorUrl);
        private void AboutKofiButton_Click(object sender, System.Windows.RoutedEventArgs e)    => OpenExternalUrl(AboutKofiUrl);

        // Open a URL via the OS shell. On Windows this hits the default
        // browser; under Wine/Proton it routes through winebrowser which
        // forwards to the host's xdg-open.
        private static void OpenExternalUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[About] failed to open {url}: {ex.Message}");
            }
        }

        // Live state TextBlocks were removed (the FULL DIAGNOSTIC REPORT expander
        // shows the same content); BuildDiagnosticsDump now sources every line
        // straight from DiagnosticsTextBuilder.

        private void DiagCopyAll_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try { System.Windows.Clipboard.SetText(BuildDiagnosticsDump()); }
            catch { /* clipboard may be contested under Wine */ }
        }

        private string BuildDiagnosticsDump()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Plugin ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildPluginInfo());
            sb.AppendLine();
            sb.AppendLine("=== USB detection ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildUsbDetection(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Standalone peripherals (own USB port) ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildStandalonePeripherals(_plugin, _data));
            sb.AppendLine();
            sb.AppendLine("=== mBooster pedals ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildMBoosterDevices(_plugin, _data));
            sb.AppendLine();
            sb.AppendLine("=== Stalks ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildStalks(_plugin, _data));
            sb.AppendLine();
            sb.AppendLine("=== Wheel identity ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildWheelIdentity(_data, _plugin.DetectionState));
            sb.AppendLine();
            sb.AppendLine("=== Display sub-device identity ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildDisplayIdentity(_data));
            sb.AppendLine();
            sb.AppendLine("=== Standalone dashboard ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildStandaloneDashboardState(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Dashboard state ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildDashboardState(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Tile-server state ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildTileServer(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Session state ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildSessionState(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Wheel channel catalog ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildWheelCatalog(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Last subscription sent ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildSubscription(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Wheel response on 0x02 (post-subscription window) ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildSubscriptionResponse(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Firmware debug (wire group 0x0E) ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildFirmwareDebug(_plugin));
            sb.AppendLine();
            sb.AppendLine("=== Device display log (session FF kind=14) ===");
            sb.AppendLine(DiagnosticsTextBuilder.BuildDeviceLog(_plugin));
            return sb.ToString();
        }

        // ── Diagnostics bundle export ────────────────────────────────────
        // Capture is always-on (dual-segment ring in SerialTrafficCapture). The
        // "Export bundle" button (in the Report-a-problem card) saves the same
        // bundle locally; BuildBundleContent lives in SettingsControl.BugReport.cs.

        private void SerialCaptureExport_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var modelSlug = DiagnosticsBundleWriter.BuildWheelModelFilenameSlug(_data?.WheelModelName);
            var prefix = string.IsNullOrEmpty(modelSlug) ? "" : modelSlug + "-";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"{prefix}moza-diagnostics-bundle-{stamp}.zip",
                Filter = Strings.Diag_ZipFilter,
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(System.Windows.Window.GetWindow(this)) != true) return;

            try
            {
                DiagnosticsBundleWriter.Write(dlg.FileName, BuildBundleContent(reportText: null));
                SetBugReportStatus(string.Format(Strings.Status_ExportedTo, dlg.FileName));
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Diagnostics export failed: {ex}");
                System.Windows.MessageBox.Show(
                    System.Windows.Window.GetWindow(this),
                    string.Format(Strings.Dialog_ExportFailed, ex.Message),
                    "AZOM",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        // Build a filesystem-safe slug from the active wheel's firmware model name
        // for use as a filename prefix on diagnostics bundles. Prefers the curated
        // friendly name (e.g. "CS Pro" for firmware "W17"); falls back to the raw
        // prefix for unknown wheels. Returns "" when no model is known yet so the
        // caller can omit the prefix entirely rather than emit a leading dash.
        
        // ===== AB9 Active Shifter Tab =====

        // Tracks whether the slider/combo values have been seeded from the profile
        // — without this, the first refresh tick would race the user's drag and
        // immediately overwrite an in-flight slider value with the saved one.
        private void RefreshAb9Tab()
        {
            if (_plugin?.Ab9Manager == null) { Ab9Tab.Visibility = Visibility.Collapsed; return; }

            bool connected = _plugin.Ab9Manager.IsConnected;
            bool detected  = _plugin.IsAb9Detected;

            Ab9Tab.Visibility = (connected || detected)
                ? Visibility.Visible : Visibility.Collapsed;

            // AB9 and AB6 share this lane, so the header names whichever answered.
            // Only derive it while the lane is live: DiscoveredPid is never cleared
            // on Disconnect, so an unplugged AB6 would otherwise keep labelling the
            // tab after an AB9 is plugged in. The x:Static default resolves once, so
            // the neutral branch has to restore it explicitly.
            Ab9Tab.Header = (connected || detected)
                ? global::MozaPlugin.Protocol.MozaUsbIds.ActiveShifterShortName(
                      _plugin.Ab9Manager.Connection.DiscoveredPid)
                : (object)Strings.TabHeader_Ab9Shifter;

            if (!connected && !detected) return;

            // Re-seed the controls from the active profile every refresh tick so
            // the tab follows per-game profile switches (matching the other
            // tabs). Events are suppressed (RefreshDisplay holds the suppressor;
            // the nested Begin below is depth-counted). A missing Ab9 block
            // shows defaults. The slider handlers write profile.Ab9 synchronously
            // so re-seeding can't fight a live drag.
            var ab9 = _plugin.Settings?.ProfileStore?.CurrentProfile?.Ab9 ?? new Ab9Settings();
            using (_suppressor.Begin())
            {
                SetAb9InputModeCombo(ab9.InputMode);
                SetAb9ModeCombo(ab9.Mode);
                SetAb9Slider(Ab9MechResistanceSlider,    Ab9MechResistanceValue,    ab9.MechanicalResistance);
                SetAb9Slider(Ab9SpringSlider,            Ab9SpringValue,            ab9.Spring);
                SetAb9Slider(Ab9DampingSlider,           Ab9DampingValue,           ab9.NaturalDamping);
                SetAb9Slider(Ab9FrictionSlider,          Ab9FrictionValue,          ab9.NaturalFriction);
                SetAb9Slider(Ab9MaxTorqueSlider,         Ab9MaxTorqueValue,         ab9.MaxTorqueLimit);
                SetAb9Slider(Ab9EngineVibIntensitySlider, Ab9EngineVibIntensityValue, ab9.EngineVibrationIntensity);
                Ab9EngineVibFreqSlider.Value = ab9.EngineVibrationFrequency;
                SetValueText(Ab9EngineVibFreqValue, ab9.EngineVibrationFrequency + " Hz");
                SetAb9Slider(Ab9GearShiftVibSlider,       Ab9GearShiftVibValue,       ab9.GearShiftVibrationIntensity);
                Ab9GearShiftVibrateOnNeutralCheck.IsChecked = ab9.GearShiftVibrateOnNeutral;
                int ab9DbMs = ab9.GearShiftDebounceMs;
                if (ab9DbMs < 0) ab9DbMs = 0;
                if (ab9DbMs > 1000) ab9DbMs = 1000;
                // Snap to 50 ms grid in case a persisted value came from a
                // manual edit / older build before the slider enforced ticks.
                ab9DbMs = ((ab9DbMs + 25) / 50) * 50;
                Ab9GearShiftDebounceSlider.Value = ab9DbMs;
                Ab9GearShiftDebounceValue.Text = $"{ab9DbMs} ms";
            }
        }

        private void SetAb9Slider(Slider slider, TextBox value, byte v)
        {
            slider.Value = v;
            value.Text = v.ToString();
        }

        private void SetAb9ModeCombo(Ab9Mode mode)
        {
            for (int i = 0; i < Ab9ModeCombo.Items.Count; i++)
            {
                var item = Ab9ModeCombo.Items[i] as ComboBoxItem;
                if (item?.Tag is string tag && byte.TryParse(tag, out byte val) && val == (byte)mode)
                {
                    Ab9ModeCombo.SelectedIndex = i;
                    return;
                }
            }
            Ab9ModeCombo.SelectedIndex = -1;
        }

        private void SetAb9InputModeCombo(Ab9InputMode mode)
        {
            for (int i = 0; i < Ab9InputModeCombo.Items.Count; i++)
            {
                var item = Ab9InputModeCombo.Items[i] as ComboBoxItem;
                if (item?.Tag is string tag && byte.TryParse(tag, out byte val) && val == (byte)mode)
                {
                    Ab9InputModeCombo.SelectedIndex = i;
                    return;
                }
            }
            Ab9InputModeCombo.SelectedIndex = -1;
        }

        private void Ab9InputModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (Ab9InputModeCombo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string tag || !byte.TryParse(tag, out byte val)) return;

            var mode = (Ab9InputMode)val;
            GetOrCreateAb9Profile().InputMode = mode;
            _plugin.Ab9Manager?.SendInputMode(mode);
            _plugin.SaveSettings();
        }

        private Ab9Settings GetOrCreateAb9Profile()
        {
            var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
            if (profile == null) return new Ab9Settings();
            if (profile.Ab9 == null) profile.Ab9 = new Ab9Settings();
            return profile.Ab9;
        }

        private void Ab9ModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (Ab9ModeCombo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string tag || !byte.TryParse(tag, out byte val)) return;

            var mode = (Ab9Mode)val;
            GetOrCreateAb9Profile().Mode = mode;
            _plugin.Ab9Manager?.SendMode(mode);
            _plugin.SaveSettings();
        }

        private void Ab9MechResistanceSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9MechResistanceSlider, Ab9MechResistanceValue, Ab9Slider.MechanicalResistance, e.NewValue);

        private void Ab9SpringSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9SpringSlider, Ab9SpringValue, Ab9Slider.Spring, e.NewValue);

        private void Ab9DampingSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9DampingSlider, Ab9DampingValue, Ab9Slider.NaturalDamping, e.NewValue);

        private void Ab9FrictionSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9FrictionSlider, Ab9FrictionValue, Ab9Slider.NaturalFriction, e.NewValue);

        private void Ab9MaxTorqueSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
            => HandleAb9SliderChanged(Ab9MaxTorqueSlider, Ab9MaxTorqueValue, Ab9Slider.MaxTorqueLimit, e.NewValue);

        private void HandleAb9SliderChanged(Slider slider, TextBox label, Ab9Slider which, double newValue)
        {
            if (_suppressEvents) return;
            byte v = (byte)Math.Max(0, Math.Min(100, (int)Math.Round(newValue)));
            label.Text = v.ToString();

            var ab9 = GetOrCreateAb9Profile();
            switch (which)
            {
                case Ab9Slider.MechanicalResistance: ab9.MechanicalResistance = v; break;
                case Ab9Slider.Spring:               ab9.Spring = v;               break;
                case Ab9Slider.NaturalDamping:       ab9.NaturalDamping = v;       break;
                case Ab9Slider.NaturalFriction:      ab9.NaturalFriction = v;      break;
                case Ab9Slider.MaxTorqueLimit:       ab9.MaxTorqueLimit = v;       break;
            }
            _plugin.Ab9Manager?.SendSlider(which, v);
            _plugin.SaveSettings();
        }

        // Engine Vibration intensity (host-rendered). The 91 Hz worker thread
        // reads the new value from the profile on its next tick — no device
        // command is sent.
        private void Ab9EngineVibIntensitySlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            byte v = (byte)Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            Ab9EngineVibIntensityValue.Text = v.ToString();
            GetOrCreateAb9Profile().EngineVibrationIntensity = v;
            _plugin.SaveSettings();
        }

        // Engine Vibration frequency slider — literal target Hz (0..200) of
        // the AB9 oscillator. Host-rendered, no device-side write; the worker
        // thread picks up the new value on its next tick.
        private void Ab9EngineVibFreqSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            ushort v = (ushort)Math.Max(0, Math.Min(200, (int)Math.Round(e.NewValue)));
            Ab9EngineVibFreqValue.Text = v + " Hz";
            GetOrCreateAb9Profile().EngineVibrationFrequency = v;
            _plugin.SaveSettings();
        }

        // Gear-shift vibration intensity. Fires one 0x0A 0x01 config write per
        // change so the AB9 firmware persists the new stored intensity for its
        // autonomous shift-rumble.
        private void Ab9GearShiftVibSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            byte v = (byte)Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            Ab9GearShiftVibValue.Text = v.ToString();
            GetOrCreateAb9Profile().GearShiftVibrationIntensity = v;
            _plugin.Ab9Manager?.SendGearShiftVibrationIntensity(v);
            _plugin.SaveSettings();
        }

        // AB9-only "vibrate on neutral" — gates whether the per-shift
        // 0x0D 0x06 (Disengage) trigger fires on any-gear→neutral transitions.
        // Independent from the wheelbase GearshiftVibrateOnNeutralCheck so the
        // AB9 can pulse for downshifts into N while the wheelbase stays quiet.
        private void Ab9GearShiftVibrateOnNeutralCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = Ab9GearShiftVibrateOnNeutralCheck.IsChecked == true;
            GetOrCreateAb9Profile().GearShiftVibrateOnNeutral = on;
            _plugin.SaveSettings();
        }

        // AB9-only shift debounce. Same 0..1000 ms range and 50 ms grid as
        // the wheelbase slider, but stored on Ab9Settings so the two devices
        // can be tuned independently.
        private void Ab9GearShiftDebounceSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            val = ((val + 25) / 50) * 50;
            if (val < 0) val = 0;
            if (val > 1000) val = 1000;
            Ab9GearShiftDebounceValue.Text = $"{val} ms";
            GetOrCreateAb9Profile().GearShiftDebounceMs = val;
            _plugin.SaveSettings();
        }

        // =====================================================================
        // mBooster tab — multi-device, multi-pedal. MBoosterDeviceRowsList (one
        // row per connected PEDAL, not per physical connection — see
        // MBoosterDeviceRow.cs) selects the active device+pedal; the settings
        // panel below populates from that selection's per-device/per-pedal
        // entry in MozaProfile.MBoosterSettings (lazily created).
        // =====================================================================

        private string? _mboosterSelectedIdentity;
        private bool _mboosterUiSeeded;
        // Separate one-shot gate for the no-device demo seed (Show-all-tabs).
        // Seeded once on entry so a demo drag isn't reset every 500 ms tick;
        // cleared when a real device appears so demo defaults re-apply if it
        // later disconnects.
        private bool _mboosterDemoSeeded;

        // One row per connected PEDAL (HID axis) across every connected
        // mBooster — see MBoosterDeviceRow.cs. Replaces the old trio of
        // "Device combo" + "per-axis Pedal Roles panel" + "Configure pedal
        // combo", which all overlapped the same selection/role concerns.
        // Rebuilt only when the signature (identity+axisCount+connected-mask
        // per device) changes, so a mid-click isn't disrupted by the 500ms
        // refresh tick; Role/IsSelected on existing rows are still resynced
        // every refresh (cheap — just property pushes, not a visual rebuild)
        // so an out-of-band profile switch or role edit is reflected promptly.
        private readonly System.Collections.ObjectModel.ObservableCollection<MBoosterDeviceRow> _mboosterDeviceRows =
            new System.Collections.ObjectModel.ObservableCollection<MBoosterDeviceRow>();
        private string? _mboosterDeviceRowsSignature;

        // Active-profile name + device identity the tab was last seeded for.
        // mBooster settings are per-profile (GetOrCreateMBoosterSettings reads
        // ProfileStore.CurrentProfile) and per-device; the seed-once gate below
        // must re-fire when either changes, or the tab keeps showing — and
        // edits keep writing against — the previously-seeded profile/device.
        private string? _mboosterSeededProfileName;
        private string? _mboosterSeededIdentity;

        // Custom Effects (Experimental) — dynamic per-device list, rebuilt
        // (not incrementally synced) on every seed/device-switch. See
        // PopulateMBoosterCustomEffectsList.
        private readonly System.Collections.ObjectModel.ObservableCollection<MBoosterCustomEffectRow> _mboosterCustomEffectRows =
            new System.Collections.ObjectModel.ObservableCollection<MBoosterCustomEffectRow>();

        private void RefreshMBoosterTab()
        {
            if (_plugin == null) { MBoosterTab.Visibility = Visibility.Collapsed; return; }
            var registry = _plugin.MBoosterRegistry;
            if (registry == null) { MBoosterTab.Visibility = Visibility.Collapsed; return; }
            var devices = registry.Devices;
            if (devices.Count == 0)
            {
                _mboosterUiSeeded = false;
                if (_plugin.Settings?.ShowAllTabs == true)
                {
                    // Demo mode: no mBooster is connected, but the tab was force-
                    // shown via Show-all-tabs. Surface the full per-pedal config UI
                    // so it can be demonstrated. With no device to drive, every
                    // control handler no-ops (each guards on a null effect target /
                    // null controller), so nothing reaches hardware. The tab's own
                    // visibility is owned by ApplyShowAllTabs.
                    ShowMBoosterDemoPanels();
                    return;
                }
                MBoosterTab.Visibility = Visibility.Collapsed;
                return;
            }
            MBoosterTab.Visibility = Visibility.Visible;
            // A real device is present — drop the demo-seed latch so demo
            // defaults re-apply if it later disconnects with the tab still shown.
            _mboosterDemoSeeded = false;

            // Rebuild the device/pedal row list if any device's identity, axis
            // count, or connectivity changed. One row per connected PEDAL — a
            // chained mBooster hosting several pedals on one connection gets
            // one row per detected axis, not one row for the whole connection
            // (see MBoosterDeviceRow.cs — this replaces the old trio of Device
            // combo + per-axis Pedal Roles panel + Configure-pedal combo).
            var sigBuilder = new System.Text.StringBuilder();
            foreach (var c in devices)
            {
                int sigAxisCount = c.AxisCount > 0 ? c.AxisCount : 1;
                sigBuilder.Append(c.Identity).Append('|').Append(sigAxisCount).Append('|');
                var sigConnected = c.ConnectedAxes;
                if (sigConnected != null)
                    for (int k = 0; k < sigConnected.Length; k++) sigBuilder.Append(sigConnected[k] ? '1' : '0');
                sigBuilder.Append(';');
            }
            string rowsSignature = sigBuilder.ToString();
            bool rowsStale = !string.Equals(rowsSignature, _mboosterDeviceRowsSignature, StringComparison.Ordinal);

            using (_suppressor.Begin())
            {
                // Keep the existing (identity, axis) selection if it's still
                // valid (device still connected, axis still in range and
                // connected); otherwise fall back to axis 0 of the first device.
                bool selectionValid = false;
                bool selectedDeviceStillPresent = false;
                int sameDeviceRetargetAxis = 0;
                foreach (var c in devices)
                {
                    if (!string.Equals(c.Identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)) continue;
                    selectedDeviceStillPresent = true;
                    int axisCount = c.AxisCount > 0 ? c.AxisCount : 1;
                    var connected = c.ConnectedAxes;
                    bool selAxisKnownConnected = connected != null && _mboosterEffectPedalIndex < connected.Length
                        ? connected[_mboosterEffectPedalIndex]
                        : _mboosterEffectPedalIndex == 0;
                    selectionValid = _mboosterEffectPedalIndex >= 0 && _mboosterEffectPedalIndex < axisCount && selAxisKnownConnected;
                    if (!selectionValid && connected != null)
                    {
                        // The device's real wired axis just became known (the
                        // "PD Linked" diagnostic landed) and it isn't the axis-0
                        // placeholder we'd optimistically selected before that —
                        // a standalone unit's sole pedal commonly reports on a
                        // non-zero axis regardless of chain status (see
                        // MBoosterDeviceController's ConnectedAxes doc comment).
                        // Follow the SAME physical device onto whichever axis is
                        // now known-connected instead of falling through to
                        // devices[0] below, which would silently reassign the
                        // user's selection to a different device a couple
                        // seconds after they picked one at startup.
                        for (int axis = 0; axis < connected.Length; axis++)
                        {
                            if (connected[axis]) { sameDeviceRetargetAxis = axis; break; }
                        }
                    }
                    break;
                }
                if (!selectionValid)
                {
                    if (selectedDeviceStillPresent)
                        _mboosterEffectPedalIndex = sameDeviceRetargetAxis;
                    else
                    {
                        _mboosterSelectedIdentity = devices[0].Identity;
                        _mboosterEffectPedalIndex = 0;
                    }
                }

                if (rowsStale)
                {
                    _mboosterDeviceRowsSignature = rowsSignature;
                    _mboosterDeviceRows.Clear();
                    foreach (var c in devices)
                    {
                        var rowSettings = _plugin.GetOrCreateMBoosterSettings(c.Identity);
                        int axisCount = c.AxisCount > 0 ? c.AxisCount : 1;
                        string deviceLabel = BuildMBoosterComboLabel(c);
                        var connectedAxes = c.ConnectedAxisIndices();

                        // Only label rows "— Pedal N" when this device genuinely
                        // hosts more than one wired pedal — not just because its
                        // HID interface happens to expose 3 axes.
                        bool multiplePedals = connectedAxes.Count > 1;
                        int shown = 0;
                        foreach (int axis in connectedAxes)
                        {
                            ++shown;
                            string label = multiplePedals
                                ? $"{deviceLabel} — {string.Format(Strings.Label_PedalAxis, shown)}"
                                : deviceLabel;
                            // Resolve against the CONNECTED axis count, not the
                            // raw HID axis count — a chain-capable hub exposes
                            // all 3 GenericDesktop axes even with only one pedal
                            // plugged in, so raw axisCount would silently override
                            // that pedal's own Role with the axis-order default.
                            var role = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResolveAxisRole(rowSettings, axis, connectedAxes.Count);
                            bool isSelected = string.Equals(c.Identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)
                                && axis == _mboosterEffectPedalIndex;
                            _mboosterDeviceRows.Add(new MBoosterDeviceRow(c.Identity, axis, label, isSelected, role,
                                rowSettings.DisplayName, OnMBoosterDeviceRowRoleChanged, OnMBoosterDeviceRowDisplayNameChanged));
                        }
                    }
                    MBoosterDeviceRowsList.ItemsSource = _mboosterDeviceRows;
                }
                else
                {
                    foreach (var row in _mboosterDeviceRows)
                    {
                        var rowController = registry.FindByIdentity(row.Identity);
                        var rowSettings = _plugin.GetOrCreateMBoosterSettings(row.Identity);
                        int axisCount = rowController != null && rowController.AxisCount > 0 ? rowController.AxisCount : 1;
                        if (axisCount > MBoosterDeviceController.MaxAxes) axisCount = MBoosterDeviceController.MaxAxes;
                        int connectedAxisCount = 0;
                        if (rowController != null)
                            for (int axis = 0; axis < axisCount; axis++)
                                if (rowController.IsAxisConnected(axis)) connectedAxisCount++;
                        row.RoleIndex = (int)global::MozaPlugin.Devices.MozaMBoosterRegistry.ResolveAxisRole(rowSettings, row.AxisIndex, connectedAxisCount);
                        row.IsSelected = string.Equals(row.Identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)
                            && row.AxisIndex == _mboosterEffectPedalIndex;
                        // DisplayName is per-profile like every other mBooster
                        // setting, so a profile switch can change it without
                        // changing rowsSignature (which only tracks physical
                        // connectivity). The setter no-ops unless the value
                        // actually differs, and OnMBoosterDeviceRowDisplayNameChanged
                        // recomputes Label when it fires.
                        row.DisplayName = rowSettings.DisplayName;
                    }
                }
            }

            var selected = registry.FindByIdentity(_mboosterSelectedIdentity ?? "");
            if (selected == null)
            {
                MBoosterDevicePanel.Visibility = Visibility.Collapsed;
                return;
            }

            MBoosterDevicePanel.Visibility = Visibility.Visible;

            // Live position marker (on the curve editors) is updated at
            // 30 Hz from UpdateHidInputDisplays (UpdateMBoosterCurveMarkers)
            // instead of here — this 500ms pass felt sluggish for direct
            // pedal feedback.

            // Resynced on EVERY pass, not gated by the seed-once latch below —
            // both depend on state that can resolve strictly AFTER the tab's
            // first seed: the pedal's Role may still be sitting on a fresh,
            // Disabled-default MBoosterDeviceSettings if this first seed raced
            // OnMBoosterSerialResolved (which migrates the real saved settings
            // in under the device's serial key, asynchronously, once the
            // serial has actually been read back over the wire — see
            // MozaPlugin.GetOrCreateMBoosterSettings/OnMBoosterSerialResolved);
            // AxisTypes (passive-pedal detection) similarly only populates once
            // the 0x0E diagnostic arrives. Neither call bumps _mboosterUiSeeded
            // above, and re-selecting the identity string never changes once
            // that race resolves, so gating these behind the seed-once latch
            // left a pedal that's genuinely Brake (or genuinely active)
            // permanently showing as if it weren't, from first tab-open until
            // the user forced a reseed some other way (switching pedals/
            // profiles). Cheap, idempotent Visibility pushes — safe every tick,
            // same reasoning as the per-row Role/IsSelected resync above.
            UpdateMBoosterEffectPassiveState();
            UpdateMBoosterConfigVisibilityForRole();

            // Re-seed when the active profile or the selected device changed
            // since the last seed — otherwise the gate below keeps the
            // previously-seeded values on screen while edits write to the
            // now-current profile/device (mBooster settings are per-profile,
            // per-device).
            var currentProfileName = _plugin?.Settings?.ProfileStore?.CurrentProfile?.Name;
            if (!string.Equals(currentProfileName, _mboosterSeededProfileName, StringComparison.Ordinal)
                || !string.Equals(selected.Identity, _mboosterSeededIdentity, StringComparison.OrdinalIgnoreCase))
                _mboosterUiSeeded = false;

            if (_mboosterUiSeeded) return;
            // Seed slider/checkbox values from the profile entry. _plugin is
            // never null past Init (the constructor stores it); guard anyway.
            if (_plugin == null) return;
            var s = _plugin.GetOrCreateMBoosterSettings(selected.Identity);
            using (_suppressor.Begin())
            {
                // Role is seeded per-row by the device rows block above (each
                // MBoosterDeviceRow owns its own Role), not here.
                // _mboosterEffectPedalIndex is resolved/validated by the device
                // rows block above too — here we just seed whichever pedal it
                // settled on. (Test toggles are never persisted;
                // SeedMBoosterEffectControls always clears them.)
                SeedMBoosterEffectControls(PeekMBoosterEffectTarget());
                MBoosterBrakeFadeEnable.IsChecked = s.BrakeFade?.Enabled ?? false;
                MBoosterBrakeFadeOnsetSlider.Value = s.BrakeFade?.BrakeFadeOnsetC ?? 550;
                SetValueText(MBoosterBrakeFadeOnsetValue, MBoosterBrakeFadeOnsetSlider.Value.ToString("F0"));
                // Never persisted — always starts off for a freshly-shown tab.
                MBoosterBrakeFadeTestToggle.IsChecked = false;
                SeedMBoosterConfigControls(PeekMBoosterEffectTarget());
            }
            PopulateMBoosterCustomEffectsList(PeekMBoosterEffectTarget());
            _mboosterUiSeeded = true;
            _mboosterSeededProfileName = currentProfileName;
            _mboosterSeededIdentity = selected.Identity;
        }

        /// <summary>Click handler for a pedal row's label Button (see
        /// MBoosterDeviceRow.cs's doc comment for why this is a plain Click
        /// event rather than RadioButton+GroupName+TwoWay binding). The row
        /// itself is the sender's DataContext, courtesy of the ItemsControl's
        /// DataTemplate.</summary>
        private void MBoosterDeviceRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not MBoosterDeviceRow row) return;
            OnMBoosterDeviceRowSelected(row.Identity, row.AxisIndex);
        }

        /// <summary>Row selection logic — fires when a pedal row's label Button
        /// is clicked. Selects BOTH the physical device AND the specific pedal
        /// (axis) on it in one step, replacing what the old MBoosterDeviceCombo_Changed
        /// (device only) + MBoosterEffectPedalCombo_SelectionChanged (pedal only)
        /// used to do separately.</summary>
        private void OnMBoosterDeviceRowSelected(string identity, int axisIndex)
        {
            if (_suppressEvents) return;
            if (string.Equals(identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase)
                && axisIndex == _mboosterEffectPedalIndex)
                return;
            // Stop any sustained Engine/ABS/Traction Control/Wheel Spin/
            // Gear Shift/Road Texture/Lockup/Threshold/Brake Fade test on
            // the pedal we're navigating away from — otherwise it keeps
            // buzzing with no visible toggle left to turn it off (the new
            // pedal's tab reseeds its own, unrelated toggle state).
            if (MBoosterEngineTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetEngineTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterAbsTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetAbsTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterTcTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetTcTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterWheelSpinTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetWheelSpinTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterGearShiftTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetGearShiftTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterRoadTextureTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetRoadTextureTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterLockupTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetLockupTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterThresholdTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetThresholdTestActive(false, _mboosterEffectPedalIndex);
            if (MBoosterBrakeFadeTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetBrakeFadeTestActive(false);
            if (MBoosterGForceTestToggle.IsChecked == true)
                CurrentMBoosterController()?.SetGForceTestActive(false, _mboosterEffectPedalIndex);
            StopAllCustomEffectTests();
            _mboosterSelectedIdentity = identity;
            _mboosterEffectPedalIndex = axisIndex;
            _mboosterUiSeeded = false;
            RefreshMBoosterTab();
        }

        /// <summary>
        /// Write <paramref name="role"/> to wherever <see cref="MozaMBoosterRegistry.ResolveAxisRole"/>
        /// actually reads it for (<paramref name="axisCount"/>, <paramref name="axisIndex"/>)
        /// — <c>Role</c> for a standalone pedal, <c>AxisRoles[axisIndex]</c> for
        /// one pedal of a chain (seeding the array from the currently-resolved
        /// roles first, so unedited axes keep their effective role).
        /// </summary>
        private static void SetMBoosterPedalRole(MBoosterDeviceSettings s, int axisCount, int axisIndex, MBoosterRole role)
        {
            if (axisCount <= 1) { s.Role = role; return; }
            var roles = s.AxisRoles;
            if (roles == null || roles.Length != axisCount)
            {
                var seeded = new MBoosterRole[axisCount];
                for (int i = 0; i < axisCount; i++)
                    seeded[i] = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResolveAxisRole(s, i, axisCount);
                s.AxisRoles = roles = seeded;
            }
            if (axisIndex >= 0 && axisIndex < roles.Length)
                roles[axisIndex] = role;
        }

        /// <summary>Role-combo callback for <see cref="MBoosterDeviceRow"/> — edits
        /// THAT row's pedal Role regardless of which row is currently selected.
        /// Enforces "only one pedal may occupy a role": assigning a non-Disabled
        /// role clears it off any OTHER pedal that already held it (a physical
        /// pedal can only be one thing, so two rows both claiming Brake is
        /// always a mistake, not a valid state).</summary>
        private void OnMBoosterDeviceRowRoleChanged(string identity, int axisIndex, MBoosterRole role)
        {
            if (_suppressEvents) return;
            if (_plugin == null) return;
            var s = _plugin.GetOrCreateMBoosterSettings(identity);
            var controller = _plugin.MBoosterRegistry?.FindByIdentity(identity);
            int axisCount = controller != null && controller.AxisCount > 0 ? controller.AxisCount : 1;
            SetMBoosterPedalRole(s, axisCount, axisIndex, role);
            _plugin.SaveSettings();
            if (role != MBoosterRole.Disabled)
                ClearDuplicateMBoosterRoleAssignments(identity, axisIndex, role);
            if (string.Equals(identity, _mboosterSelectedIdentity, StringComparison.OrdinalIgnoreCase) && axisIndex == _mboosterEffectPedalIndex)
                UpdateMBoosterConfigVisibilityForRole();
        }

        /// <summary>Bumps every OTHER known pedal row currently showing
        /// <paramref name="role"/> to Disabled — setting a row's RoleIndex
        /// recurses into <see cref="OnMBoosterDeviceRowRoleChanged"/>, which
        /// persists it through the same Role/AxisRoles write path (and won't
        /// recurse further, since Disabled never triggers another clear
        /// pass). Only considers rows already built (<see cref="_mboosterDeviceRows"/>)
        /// — a pedal not yet known to the UI can't visibly collide with one
        /// that is.</summary>
        private void ClearDuplicateMBoosterRoleAssignments(string keepIdentity, int keepAxisIndex, MBoosterRole role)
        {
            foreach (var row in _mboosterDeviceRows)
            {
                if (string.Equals(row.Identity, keepIdentity, StringComparison.OrdinalIgnoreCase) && row.AxisIndex == keepAxisIndex)
                    continue;
                if (row.RoleIndex == (int)role)
                    row.RoleIndex = (int)MBoosterRole.Disabled;
            }
        }

        /// <summary>DisplayName edit callback — fires from MBoosterDeviceRow
        /// .DisplayName's setter (only when the value actually changes), both
        /// for a genuine user edit (the TwoWay-bound TextBox shown for the
        /// selected row) and RefreshMBoosterTab's per-tick resync (e.g. after
        /// a profile switch changes the saved name). Persists the value and
        /// recomputes every row sharing this identity's Label immediately —
        /// Label isn't part of rowsSignature, so RefreshMBoosterTab wouldn't
        /// otherwise notice a DisplayName change until some other signature
        /// field happened to change too.</summary>
        private void OnMBoosterDeviceRowDisplayNameChanged(string identity, string newDisplayName)
        {
            if (_plugin == null) return;
            var s = _plugin.GetOrCreateMBoosterSettings(identity);
            s.DisplayName = newDisplayName ?? "";
            _plugin.SaveSettings();

            var controller = _plugin.MBoosterRegistry?.FindByIdentity(identity);
            if (controller == null) return;
            string deviceLabel = BuildMBoosterComboLabel(controller);

            int matchCount = 0;
            foreach (var row in _mboosterDeviceRows)
                if (string.Equals(row.Identity, identity, StringComparison.OrdinalIgnoreCase)) matchCount++;
            bool multiplePedals = matchCount > 1;

            int shown = 0;
            foreach (var row in _mboosterDeviceRows)
            {
                if (!string.Equals(row.Identity, identity, StringComparison.OrdinalIgnoreCase)) continue;
                ++shown;
                row.Label = multiplePedals
                    ? $"{deviceLabel} — {string.Format(Strings.Label_PedalAxis, shown)}"
                    : deviceLabel;
            }
        }

        private MBoosterDeviceSettings? CurrentMBoosterSettings()
        {
            if (_mboosterSelectedIdentity == null) return null;
            return _plugin.GetOrCreateMBoosterSettings(_mboosterSelectedIdentity);
        }

        // Which pedal ALL the config sections below the role selector currently
        // edit (0 = master/host at device 0x12; 1/2 = chained pedals at
        // 0x1d/0x1e). Set by the per-pedal config combo; Pedal Feel, Sim Input
        // Mapping, Effects and Calibration are all stored (and effects sent) per
        // pedal. (Kept the "Effect" names to limit churn from the earlier
        // effects-only version — it now scopes every config section.)
        private int _mboosterEffectPedalIndex;

        /// <summary>The full per-pedal config the settings sections edit — the
        /// master's flat fields for pedal 0, else the chained pedal's per-pedal
        /// entry (created on demand so edits persist). A lane whose SOLE
        /// connected pedal is this (non-zero) axis with no per-pedal entry
        /// edits the flat fields instead (and never creates the entry): that's
        /// where the config landed while the UI still showed the axis-0 row,
        /// and creating an empty entry here would orphan it — see
        /// MBoosterDeviceController.SoleConnectedAxis. Null if no device
        /// selected. Covers effects + calibration + sim input + pedal feel.</summary>
        private IMBoosterPedalConfig? CurrentMBoosterEffectTarget() =>
            MozaMBoosterRegistry.GetOrCreatePedalConfig(
                CurrentMBoosterSettings(),
                _mboosterEffectPedalIndex,
                CurrentMBoosterController()?.SoleConnectedAxis() ?? -1);

        /// <summary>The per-pedal config for the selected pedal WITHOUT creating a
        /// missing entry — used when seeding controls so merely viewing a chained
        /// pedal doesn't persist an empty entry. Falls back to master defaults.
        /// Same sole-connected-pedal flat-fields fallback as
        /// <see cref="CurrentMBoosterEffectTarget"/> so seeding shows the config
        /// that pedal actually runs with.</summary>
        private IMBoosterPedalConfig? PeekMBoosterEffectTarget() =>
            MozaMBoosterRegistry.PeekPedalConfig(
                CurrentMBoosterSettings(),
                _mboosterEffectPedalIndex,
                CurrentMBoosterController()?.SoleConnectedAxis() ?? -1);

        /// <summary>Seed the eight vibration-effect cards' controls from one
        /// pedal's effect settings. Assumes the event suppressor is active. Brake
        /// Fade is seeded separately by <see cref="RefreshMBoosterTab"/> since it's
        /// per-lane (master pedal only).</summary>
        private void SeedMBoosterEffectControls(IMBoosterEffects? fx)
        {
            MBoosterTcEnable.IsChecked       = fx?.TractionControl?.Enabled          ?? false;
            MBoosterTcFrequencySlider.Value  = fx?.TractionControl?.FrequencyHz      ?? MBoosterUiConstants.TractionControlFreqMinHz;
            SetValueText(MBoosterTcFrequencyValue, MBoosterTcFrequencySlider.Value.ToString("F0"));
            MBoosterTcIntensity.Value        = fx?.TractionControl?.IntensityPct     ?? 50;
            SetValueText(MBoosterTcIntensityValue, (fx?.TractionControl?.IntensityPct ?? 50).ToString());
            MBoosterTcTestToggle.IsChecked = false;
            MBoosterWheelSpinEnable.IsChecked       = fx?.WheelSpin?.Enabled          ?? false;
            MBoosterWheelSpinFrequencySlider.Value  = fx?.WheelSpin?.FrequencyHz      ?? MBoosterUiConstants.WheelSpinFreqMinHz;
            SetValueText(MBoosterWheelSpinFrequencyValue, MBoosterWheelSpinFrequencySlider.Value.ToString("F0"));
            MBoosterWheelSpinIntensity.Value        = fx?.WheelSpin?.IntensityPct     ?? 50;
            SetValueText(MBoosterWheelSpinIntensityValue, (fx?.WheelSpin?.IntensityPct ?? 50).ToString());
            MBoosterWheelSpinTestToggle.IsChecked = false;
            MBoosterGearShiftEnable.IsChecked       = fx?.GearShift?.Enabled          ?? false;
            MBoosterGearShiftFrequencySlider.Value  = fx?.GearShift?.FrequencyHz      ?? MBoosterUiConstants.GearShiftFreqMinHz;
            SetValueText(MBoosterGearShiftFrequencyValue, MBoosterGearShiftFrequencySlider.Value.ToString("F0"));
            MBoosterGearShiftIntensity.Value        = fx?.GearShift?.IntensityPct     ?? 50;
            SetValueText(MBoosterGearShiftIntensityValue, (fx?.GearShift?.IntensityPct ?? 50).ToString());
            MBoosterGearShiftVibrateOnNeutralCheck.IsChecked = fx?.GearShift?.VibrateOnNeutral ?? false;
            int gearShiftDebounceMs = fx?.GearShift?.DebounceMs ?? 500;
            MBoosterGearShiftDebounceSlider.Value = gearShiftDebounceMs;
            MBoosterGearShiftDebounceValue.Text = $"{gearShiftDebounceMs} ms";
            MBoosterGearShiftTestToggle.IsChecked = false;
            MBoosterAbsEnable.IsChecked       = fx?.Abs?.Enabled          ?? false;
            MBoosterAbsFrequencySlider.Value  = fx?.Abs?.FrequencyHz      ?? MBoosterUiConstants.AbsFreqMinHz;
            SetValueText(MBoosterAbsFrequencyValue, MBoosterAbsFrequencySlider.Value.ToString("F0"));
            MBoosterAbsIntensity.Value        = fx?.Abs?.IntensityPct     ?? 50;
            SetValueText(MBoosterAbsIntensityValue, (fx?.Abs?.IntensityPct ?? 50).ToString());
            MBoosterAbsSmoothness.Value       = fx?.Abs?.SmoothnessPct    ?? 100;
            SetValueText(MBoosterAbsSmoothnessValue, (fx?.Abs?.SmoothnessPct ?? 100).ToString());
            MBoosterAbsTestToggle.IsChecked = false;
            MBoosterLockupEnable.IsChecked = fx?.Lockup?.Enabled ?? false;
            MBoosterLockupFrequencySlider.Value = fx?.Lockup?.FrequencyHz ?? MBoosterUiConstants.LockupFreqMinHz;
            SetValueText(MBoosterLockupFrequencyValue, MBoosterLockupFrequencySlider.Value.ToString("F0"));
            MBoosterLockupIntensity.Value = fx?.Lockup?.IntensityPct ?? 50;
            SetValueText(MBoosterLockupIntensityValue, (fx?.Lockup?.IntensityPct ?? 50).ToString());
            MBoosterLockupTestToggle.IsChecked = false;
            MBoosterThresholdEnable.IsChecked = fx?.Threshold?.Enabled ?? false;
            MBoosterThresholdTriggerLevel.Value = fx?.Threshold?.TriggerLevelPct ?? 60;
            SetValueText(MBoosterThresholdTriggerLevelValue, (fx?.Threshold?.TriggerLevelPct ?? 60).ToString());
            MBoosterThresholdFrequencySlider.Value = fx?.Threshold?.FrequencyHz ?? MBoosterUiConstants.ThresholdFreqMinHz;
            SetValueText(MBoosterThresholdFrequencyValue, MBoosterThresholdFrequencySlider.Value.ToString("F0"));
            MBoosterThresholdIntensity.Value = fx?.Threshold?.IntensityPct ?? 50;
            SetValueText(MBoosterThresholdIntensityValue, (fx?.Threshold?.IntensityPct ?? 50).ToString());
            MBoosterThresholdDecay.Value = fx?.Threshold?.DecayPct ?? 20;
            SetValueText(MBoosterThresholdDecayValue, (fx?.Threshold?.DecayPct ?? 20).ToString());
            MBoosterThresholdTestToggle.IsChecked = false;
            MBoosterEngineEnable.IsChecked    = fx?.Engine?.Enabled       ?? false;
            MBoosterEngineIntensity.Value     = fx?.Engine?.IntensityPct  ?? 50;
            SetValueText(MBoosterEngineIntensityValue, (fx?.Engine?.IntensityPct ?? 50).ToString());
            MBoosterEngineTestToggle.IsChecked = false;
            MBoosterRoadTextureEnable.IsChecked = fx?.RoadTexture?.Enabled ?? false;
            MBoosterRoadTextureIntensity.Value = fx?.RoadTexture?.IntensityPct ?? 50;
            SetValueText(MBoosterRoadTextureIntensityValue, (fx?.RoadTexture?.IntensityPct ?? 50).ToString());
            MBoosterRoadTextureSmoothness.Value = fx?.RoadTexture?.SmoothnessPct ?? 50;
            SetValueText(MBoosterRoadTextureSmoothnessValue, (fx?.RoadTexture?.SmoothnessPct ?? 50).ToString());
            MBoosterRoadTextureTestToggle.IsChecked = false;
            MBoosterGForceEnable.IsChecked = fx?.GForce?.Enabled ?? false;
            MBoosterGForceMaxTravel.Value = fx?.GForce?.MaxTravelMm ?? 10;
            SetValueText(MBoosterGForceMaxTravelValue, MBoosterGForceMaxTravel.Value.ToString("0.#"));
            MBoosterGForceResponseSpeed.Value = fx?.GForce?.ResponseSpeedPct ?? 50;
            SetValueText(MBoosterGForceResponseSpeedValue, (fx?.GForce?.ResponseSpeedPct ?? 50).ToString());
            MBoosterGForceTestToggle.IsChecked = false;
        }

        /// <summary>Seed the Calibration, Sim Input Mapping and Pedal Feel controls
        /// from one pedal's config (master flat fields or a chained pedal's entry;
        /// null = defaults). Assumes the event suppressor is active.</summary>
        private void SeedMBoosterConfigControls(IMBoosterPedalConfig? fx)
        {
            // Calibration
            MBoosterDirCheck.IsChecked = (fx?.Direction == 1);
            int min = fx?.Min ?? -1;
            MBoosterMinSlider.Value = min >= 0 ? min : 0;
            SetValueText(MBoosterMinValue, MBoosterMinSlider.Value.ToString("F0"));
            int max = fx?.Max ?? -1;
            MBoosterMaxSlider.Value = max >= 0 ? max : 0;
            SetValueText(MBoosterMaxValue, MBoosterMaxSlider.Value.ToString("F0"));
            var curve = (fx?.CurveY != null && fx.CurveY.Length == MBoosterUiConstants.SimInputMappingNodeCount) ? fx.CurveY : MBoosterOutputCurveDefault;
            MBoosterY1Slider.Value = curve[0]; SetValueText(MBoosterY1Value, curve[0].ToString("F0"));
            MBoosterY2Slider.Value = curve[1]; SetValueText(MBoosterY2Value, curve[1].ToString("F0"));
            MBoosterY3Slider.Value = curve[2]; SetValueText(MBoosterY3Value, curve[2].ToString("F0"));
            MBoosterY4Slider.Value = curve[3]; SetValueText(MBoosterY4Value, curve[3].ToString("F0"));
            MBoosterY5Slider.Value = curve[4]; SetValueText(MBoosterY5Value, curve[4].ToString("F0"));
            MBoosterY6Slider.Value = curve[5]; SetValueText(MBoosterY6Value, curve[5].ToString("F0"));
            var curveX = (fx?.CurveX != null && fx.CurveX.Length == MBoosterUiConstants.SimInputMappingNodeCount) ? fx.CurveX : MBoosterOutputCurveDefault;
            MBoosterX1Slider.Value = curveX[0]; SetValueText(MBoosterX1Value, curveX[0].ToString("F0"));
            MBoosterX2Slider.Value = curveX[1]; SetValueText(MBoosterX2Value, curveX[1].ToString("F0"));
            MBoosterX3Slider.Value = curveX[2]; SetValueText(MBoosterX3Value, curveX[2].ToString("F0"));
            MBoosterX4Slider.Value = curveX[3]; SetValueText(MBoosterX4Value, curveX[3].ToString("F0"));
            MBoosterX5Slider.Value = curveX[4]; SetValueText(MBoosterX5Value, curveX[4].ToString("F0"));
            MBoosterX6Slider.Value = curveX[5]; SetValueText(MBoosterX6Value, curveX[5].ToString("F0"));
            // Sim Input Mapping
            float ratio = fx?.SensorOutputRatioPct ?? -1;
            MBoosterRatioSlider.Value = ratio >= 0 ? ratio : 0;
            SetValueText(MBoosterRatioValue, $"{MBoosterRatioSlider.Value:F0}%");
            float thr = fx?.MaxThresholdKg ?? -1;
            MBoosterMaxThresholdSlider.Value = thr >= 0 ? thr : 100;
            SetValueText(MBoosterMaxThresholdValue, MBoosterMaxThresholdSlider.Value.ToString("F0"));
            // Pedal Feel
            var inputCurve = (fx?.InputCurveY != null && fx.InputCurveY.Length == MBoosterUiConstants.PedalFeelNodeCount)
                ? fx.InputCurveY : MBoosterInputCurveDefault;
            MBoosterInputY1Slider.Value = inputCurve[0]; SetValueText(MBoosterInputY1Value, inputCurve[0].ToString("F0"));
            MBoosterInputY2Slider.Value = inputCurve[1]; SetValueText(MBoosterInputY2Value, inputCurve[1].ToString("F0"));
            MBoosterInputY3Slider.Value = inputCurve[2]; SetValueText(MBoosterInputY3Value, inputCurve[2].ToString("F0"));
            MBoosterInputY4Slider.Value = inputCurve[3]; SetValueText(MBoosterInputY4Value, inputCurve[3].ToString("F0"));
            MBoosterInputY5Slider.Value = inputCurve[4]; SetValueText(MBoosterInputY5Value, inputCurve[4].ToString("F0"));
            MBoosterInputY6Slider.Value = inputCurve[5]; SetValueText(MBoosterInputY6Value, inputCurve[5].ToString("F0"));
            var inputCurveX = (fx?.InputCurveX != null && fx.InputCurveX.Length == MBoosterUiConstants.PedalFeelNodeCount)
                ? fx.InputCurveX : MBoosterInputCurveDefault;
            MBoosterInputX1Slider.Value = inputCurveX[0]; SetValueText(MBoosterInputX1Value, inputCurveX[0].ToString("F0"));
            MBoosterInputX2Slider.Value = inputCurveX[1]; SetValueText(MBoosterInputX2Value, inputCurveX[1].ToString("F0"));
            MBoosterInputX3Slider.Value = inputCurveX[2]; SetValueText(MBoosterInputX3Value, inputCurveX[2].ToString("F0"));
            MBoosterInputX4Slider.Value = inputCurveX[3]; SetValueText(MBoosterInputX4Value, inputCurveX[3].ToString("F0"));
            MBoosterInputX5Slider.Value = inputCurveX[4]; SetValueText(MBoosterInputX5Value, inputCurveX[4].ToString("F0"));
            MBoosterInputX6Slider.Value = inputCurveX[5]; SetValueText(MBoosterInputX6Value, inputCurveX[5].ToString("F0"));
            float ts = fx?.TravelStartMm ?? -1;
            MBoosterTravelRangeSlider.LowValue = ts >= 0 ? ts : MBoosterUiConstants.TravelMinMm;
            float te = fx?.TravelEndMm ?? -1;
            MBoosterTravelRangeSlider.HighValue = te >= 0 ? te : MBoosterUiConstants.TravelMinMm + MBoosterUiConstants.TravelMaxGapMm;
            float ef = fx?.EndstopFrontStiffness ?? -1;
            MBoosterEndstopFrontSlider.Value = ef >= 0 ? ef : 1;
            SetValueText(MBoosterEndstopFrontValue, MBoosterEndstopFrontSlider.Value.ToString("F0"));
            float ee = fx?.EndstopEndStiffness ?? -1;
            MBoosterEndstopEndSlider.Value = ee >= 0 ? ee : 1;
            SetValueText(MBoosterEndstopEndValue, MBoosterEndstopEndSlider.Value.ToString("F0"));
            float dz = fx?.DeadzoneKg ?? -1;
            MBoosterDeadzoneSlider.Value = dz >= 0 ? dz : 0;
            SetValueText(MBoosterDeadzoneValue, MBoosterDeadzoneSlider.Value.ToString("F1"));
            float mf = fx?.MaxForceKg ?? -1;
            MBoosterMaxForceSlider.Value = mf >= 0 ? mf : 200;
            SetValueText(MBoosterMaxForceValue, MBoosterMaxForceSlider.Value.ToString("F0"));
            float nf = fx?.NaturalFrictionPct ?? -1;
            MBoosterNaturalFrictionSlider.Value = nf >= 0 ? nf : 0;
            SetValueText(MBoosterNaturalFrictionValue, MBoosterNaturalFrictionSlider.Value.ToString("F0"));
            bool frictionEnabled = fx?.NaturalFrictionEnabled ?? true;
            MBoosterNaturalFrictionEnable.IsChecked = frictionEnabled;
            MBoosterNaturalFrictionSlider.IsEnabled = frictionEnabled;

            var sd = fx?.SegmentedDamping;
            bool dampingEnabled = sd?.DampingEnabled ?? true;
            MBoosterSegDampEnable.IsChecked = dampingEnabled;
            MBoosterSegDampPressedPlot.IsEnabled = dampingEnabled;
            MBoosterSegDampReleasedPlot.IsEnabled = dampingEnabled;
            MBoosterSegDampPressedPlot.Divider1 = (sd?.Divider1Pressed ?? -1) >= 0 ? sd!.Divider1Pressed : MBoosterUiConstants.SegDampDivider1PressedDefaultPct;
            MBoosterSegDampPressedPlot.Divider2 = (sd?.Divider2Pressed ?? -1) >= 0 ? sd!.Divider2Pressed : MBoosterUiConstants.SegDampDivider2PressedDefaultPct;
            MBoosterSegDampPressedPlot.Seg1Value = (sd?.Seg1Pressed ?? -1) >= 0 ? sd!.Seg1Pressed : MBoosterUiConstants.SegDampSegDefaultPct;
            MBoosterSegDampPressedPlot.Seg2Value = (sd?.Seg2Pressed ?? -1) >= 0 ? sd!.Seg2Pressed : MBoosterUiConstants.SegDampSegDefaultPct;
            MBoosterSegDampPressedPlot.Seg3Value = (sd?.Seg3Pressed ?? -1) >= 0 ? sd!.Seg3Pressed : MBoosterUiConstants.SegDampSegDefaultPct;

            MBoosterSegDampReleasedPlot.Divider1 = (sd?.Divider1Released ?? -1) >= 0 ? sd!.Divider1Released : MBoosterUiConstants.SegDampDivider1ReleasedDefaultPct;
            MBoosterSegDampReleasedPlot.Divider2 = (sd?.Divider2Released ?? -1) >= 0 ? sd!.Divider2Released : MBoosterUiConstants.SegDampDivider2ReleasedDefaultPct;
            MBoosterSegDampReleasedPlot.Seg1Value = (sd?.Seg1Released ?? -1) >= 0 ? sd!.Seg1Released : MBoosterUiConstants.SegDampSegDefaultPct;
            MBoosterSegDampReleasedPlot.Seg2Value = (sd?.Seg2Released ?? -1) >= 0 ? sd!.Seg2Released : MBoosterUiConstants.SegDampSegDefaultPct;
            MBoosterSegDampReleasedPlot.Seg3Value = (sd?.Seg3Released ?? -1) >= 0 ? sd!.Seg3Released : MBoosterUiConstants.SegDampSegDefaultPct;
        }

        private MBoosterDeviceController? CurrentMBoosterController()
        {
            return _plugin?.MBoosterRegistry?.FindByIdentity(_mboosterSelectedIdentity ?? "");
        }

        /// <summary>
        /// Device combo label: port/identity, prefixed with the user's
        /// DisplayName when set — the whole point of that field is telling
        /// two same-role mBoosters apart in this exact list. See
        /// MBoosterDeviceSettings.DisplayName.
        /// </summary>
        private string BuildMBoosterComboLabel(MBoosterDeviceController c)
        {
            string baseLabel = $"{MBoosterDeviceController.ShortIdentity(c.Identity)} ({c.PortName})";
            var name = _plugin?.GetOrCreateMBoosterSettings(c.Identity)?.DisplayName;
            return string.IsNullOrWhiteSpace(name) ? baseLabel : $"{name} — {baseLabel}";
        }

        // ===== Custom Effects (Experimental) =====
        // Dynamic per-device list — unlike the eight built-in effects (static
        // named XAML controls wired one-by-one), the count here is
        // user-defined, so the ItemsControl is bound to an ObservableCollection
        // of row view-models (MBoosterCustomEffectRow) instead. Rebuilt wholesale
        // (not incrementally synced) whenever the tab reseeds — simpler than
        // diffing, and this list is edited far less often than a slider is dragged.

        private void PopulateMBoosterCustomEffectsList(IMBoosterEffects? fx)
        {
            MBoosterCustomEffectsList.ItemsSource = _mboosterCustomEffectRows;
            _mboosterCustomEffectRows.Clear();
            var list = fx?.CustomEffects;
            if (list == null || _plugin == null) return;
            // Snapshot once so every row shares one backing list for the
            // simple editor, same as ChannelMappingRowFactory.Build does for
            // the (unrelated) telemetry channel-mapping rows.
            var plugin = _plugin;
            var props = plugin.GetAllSimHubPropertyNames();
            var engine = plugin.ChannelFormulaEngine;
            for (int i = 0; i < list.Count; i++)
            {
                _mboosterCustomEffectRows.Add(new MBoosterCustomEffectRow(list[i], () => plugin.SaveSettings(), OnCustomEffectTestToggle)
                {
                    AllProperties = props,
                    Engine = engine,
                });
            }
        }

        private void OnCustomEffectTestToggle(string effectId, bool on)
        {
            // Resolved at call time (not captured at row-construction time) so
            // this always targets whichever device is currently selected —
            // matters for StopAllCustomEffectTests, called just BEFORE the
            // selected device changes.
            CurrentMBoosterController()?.SetCustomEffectTestActive(effectId, on, _mboosterEffectPedalIndex);
        }

        /// <summary>
        /// Turn off every custom effect's sustained Test toggle for the
        /// currently-selected device — mirrors the explicit stop calls for
        /// the eight built-in effects' Test toggles in
        /// <see cref="OnMBoosterDeviceRowSelected"/> and
        /// <see cref="OnUnloadedStopTimers"/>, so a forgotten toggle doesn't
        /// leave the pedal buzzing with no UI left to turn it off.
        /// </summary>
        private void StopAllCustomEffectTests()
        {
            foreach (var row in _mboosterCustomEffectRows)
                if (row.TestActive) row.TestActive = false;
        }

        private void MBoosterAddCustomEffectButton_Click(object sender, RoutedEventArgs e)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.CustomEffects ??= new List<MBoosterCustomEffect>();
            var effect = new MBoosterCustomEffect
            {
                Name = $"{Strings.DefaultName_CustomEffect} {s.CustomEffects.Count + 1}",
            };
            s.CustomEffects.Add(effect);
            _plugin.SaveSettings();
            _mboosterCustomEffectRows.Add(new MBoosterCustomEffectRow(effect, () => _plugin.SaveSettings(), OnCustomEffectTestToggle)
            {
                AllProperties = _plugin?.GetAllSimHubPropertyNames() ?? Array.Empty<string>(),
                Engine = _plugin?.ChannelFormulaEngine,
            });
        }

        private void MBoosterDeleteCustomEffect_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not MBoosterCustomEffectRow row) return;
            if (row.TestActive) CurrentMBoosterController()?.SetCustomEffectTestActive(row.Id, false, _mboosterEffectPedalIndex);
            var s = CurrentMBoosterEffectTarget();
            s?.CustomEffects?.RemoveAll(c => string.Equals(c.Id, row.Id, StringComparison.Ordinal));
            _plugin.SaveSettings();
            _mboosterCustomEffectRows.Remove(row);
        }

        // ── Formula editing — same dual-mode (pencil + ƒₓ) handlers as
        // DashboardManagementControl's channel-mapping list, scoped to the
        // custom-effects row collection instead. See
        // MBoosterCustomEffectRow's "Formula editing" region.

        private void MBoosterEditFormula_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not MBoosterCustomEffectRow row) return;
            // Only one inline editor expanded at a time, same as the channel mapper.
            foreach (var r in _mboosterCustomEffectRows)
                if (!ReferenceEquals(r, row) && r.IsEditing) r.CancelEdit();
            row.BeginEdit();
        }

        private void MBoosterCommitFormula_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not MBoosterCustomEffectRow row) return;
            row.CommitEdit();
        }

        private void MBoosterCancelFormula_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not MBoosterCustomEffectRow row) return;
            row.CancelEdit();
        }

        /// <summary>
        /// Advanced edit: open SimHub's own formula editor (BindingEditor)
        /// against the shared engine and a working copy of the row's
        /// formula. On OK, write the result back through the row (which
        /// serializes it into Formula and persists). Mirrors
        /// DashboardManagementControl.AdvancedEditMapping_Click.
        /// </summary>
        private async void MBoosterAdvancedEditFormula_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not MBoosterCustomEffectRow row) return;
            var engine = row.Engine;
            if (engine == null)
            {
                MozaLog.Warn("[AZOM] mBooster custom-effect formula editor unavailable (SimHub engine not loaded)");
                return;
            }

            // Work on a throwaway ExpressionValue so the dialog never mutates
            // the row's live Expression mid-edit; copy back only on OK.
            var src = row.Expression;
            var working = new ExpressionValue
            {
                UseJavascript = src.UseJavascript,
                Expression = src.Expression,
                PreExpression = src.PreExpression,
            };

            var data = new DashboardBindingData
            {
                Formula = working,
                Mode = string.IsNullOrWhiteSpace(working.Expression) ? BindingMode.None : BindingMode.Formula,
                TargetPropertyName = row.Name,
                TargetType = typeof(double),
            };

            try
            {
                var editor = new BindingEditor(engine) { DataContext = data };
                var result = await editor.ShowDialogWindowAsync(this);
                if ((int)result != 1) return; // not OK
                if (data.Mode == BindingMode.Formula)
                    row.ApplyEditedFormula(data.Formula?.Expression, data.Formula?.UseJavascript ?? false);
                else
                    row.ApplyEditedFormula("", false); // cleared
            }
            catch (Exception ex)
            {
                MozaLog.Warn("[AZOM] mBooster custom-effect formula editor failed: " + ex.Message);
            }
        }

        // PopulateMBoosterAxisRoles / OnMBoosterAxisRoleChanged / PopulateMBoosterEffectPedalCombo /
        // MBoosterEffectPedalCombo_SelectionChanged used to live here — all three retired in favor
        // of the single unified per-pedal MBoosterDeviceRow list built in RefreshMBoosterTab.

        /// <summary>
        /// Hide the vibration-effect cards when the selected pedal is passive
        /// (type 2 = no motor, e.g. a CRP2 — effects can't play there). When the
        /// device hasn't reported pedal types (0x0E diagnostic not received) the
        /// cards stay visible (best-effort). See MBoosterDeviceController.AxisTypes.
        ///
        /// The same gate hides the Pedal Feel controls that are real hardware
        /// writes on brake-named SINGLETON cmdIds — Travel (0x84/0x85), End Stop
        /// (0xB2), Deadzone/Max Force (0xAB selectors 0x07/0x0E), Natural
        /// Friction (0xAE) and Segmented Damping (0xB7). None of them carries a
        /// per-pedal selector, so editing them from a passive pedal's page
        /// didn't configure that pedal — it silently overwrote the ACTIVE
        /// pedal's registers (bundle KY3HK4QP: the passive throttle page's
        /// 3.8/35.9mm travel is what the brake unit committed as Params 48/49).
        /// Inferred from the wire shape rather than from a Pit House capture of a
        /// passive-pedal edit — see docs/protocol/devices/mbooster.md.
        /// The input curve (host-side-only) and the per-role output curve stay
        /// visible for every pedal.
        /// </summary>
        private void UpdateMBoosterEffectPassiveState()
        {
            var types = CurrentMBoosterController()?.AxisTypes;
            bool passive = types != null
                && _mboosterEffectPedalIndex >= 0 && _mboosterEffectPedalIndex < types.Length
                && types[_mboosterEffectPedalIndex] == 2;
            MBoosterEffectsCardsPanel.Visibility = passive ? Visibility.Collapsed : Visibility.Visible;
            MBoosterEffectsPassiveNote.Visibility = passive ? Visibility.Visible : Visibility.Collapsed;
            var hwVisibility = passive ? Visibility.Collapsed : Visibility.Visible;
            MBoosterTravelEndstopPanel.Visibility = hwVisibility;
            MBoosterDeadzoneMaxForcePanel.Visibility = hwVisibility;
            MBoosterNaturalFrictionPanel.Visibility = hwVisibility;
            MBoosterSegDampCard.Visibility = hwVisibility;
        }

        /// <summary>
        /// Show the load-cell-only Sim Input controls (Sensor Output Ratio + Max
        /// Threshold) only when the selected pedal is a BRAKE — a throttle/clutch
        /// has no pressure sensor. Pedal travel, endstop, the output curve and
        /// Pedal Feel all stay visible for every pedal mode.
        /// </summary>
        private void UpdateMBoosterConfigVisibilityForRole()
        {
            bool isBrake = MBoosterSelectedPedalRolePrefix() == "brake";
            MBoosterBrakeOnlyPanel.Visibility = isBrake ? Visibility.Visible : Visibility.Collapsed;
        }

        // Force every device-gated mBooster panel visible for the no-hardware
        // demo case (Show-all-tabs with nothing connected). Deliberately shows
        // the role-specific panels too (effects cards, brake-only Sim Input) so
        // the whole surface is demonstrable — the normal device-driven path
        // (RefreshMBoosterTab + the role/passive updaters) re-gates them the
        // moment a real device appears, so this never fights real functionality.
        private void ShowMBoosterDemoPanels()
        {
            MBoosterDevicePanel.Visibility = Visibility.Visible;
            MBoosterEffectsCardsPanel.Visibility = Visibility.Visible;
            MBoosterEffectsPassiveNote.Visibility = Visibility.Collapsed;
            MBoosterBrakeOnlyPanel.Visibility = Visibility.Visible;
            MBoosterTravelEndstopPanel.Visibility = Visibility.Visible;
            MBoosterDeadzoneMaxForcePanel.Visibility = Visibility.Visible;
            MBoosterNaturalFrictionPanel.Visibility = Visibility.Visible;
            MBoosterSegDampCard.Visibility = Visibility.Visible;

            // Seed every control to its default once. The curve editors take no
            // node data of their own — they two-way bind to the hidden data-store
            // sliders (BindEditorToSliders), so without a seed those sliders sit at
            // 0 and the editors draw a collapsed/garbage curve. A null target makes
            // both seeders fall back to their own Linear-preset default array
            // (MBoosterOutputCurveDefault / MBoosterInputCurveDefault) and sane
            // per-control defaults; the seed writes go through _suppressor so the
            // slider ValueChanged handlers (which would no-op on the null target
            // anyway) stay quiet while the bindings still update the editors.
            if (_mboosterDemoSeeded) return;
            using (_suppressor.Begin())
            {
                SeedMBoosterConfigControls(null);
                SeedMBoosterEffectControls(null);
            }
            _mboosterDemoSeeded = true;
        }

        // ===== Effect handlers =====
        // All five effects (ABS, Engine, Road Texture, Lockup, Threshold)
        // have now been rebuilt with Enable + sustained Test toggles — see
        // docs/protocol/devices/mbooster.md "Effects card UI (mid-rebuild)"
        // for the history.

        private void MBoosterAbsEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Abs ??= new MBoosterEffectSettings()).Enabled = MBoosterAbsEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterAbsIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterAbsIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Abs ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // Fixed vibration frequency (5-30Hz) — replaces the old ABS-
        // activation-depth mapping (which SimHub's bool AbsActive collapsed
        // to a constant 30Hz anyway). See MBoosterEffectSettings.FrequencyHz
        // and MBoosterEffectWorker.UpdateAbsRequest.
        private void MBoosterAbsFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.AbsFreqMinHz, Math.Min((int)MBoosterUiConstants.AbsFreqMaxHz, (int)Math.Round(e.NewValue)));
            MBoosterAbsFrequencyValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Abs ??= new MBoosterEffectSettings()).FrequencyHz = v;
            _plugin.SaveSettings();
        }
        // Pulse modulation depth (0-100%) — 100 (default) is the exact
        // original verified waveform; 0 widens it to a sharper, choppier
        // full-swing pulse. See MBoosterEffectSettings.SmoothnessPct and
        // MBoosterEffectSynthesizer.SynthesizeAbs.
        private void MBoosterAbsSmoothness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterAbsSmoothnessValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Abs ??= new MBoosterEffectSettings()).SmoothnessPct = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — substitutes live brake position for
        // absActive (there's no live ABS-activation signal to press against
        // outside a real ABS event), vibrating continuously at the live
        // Frequency/Intensity/Smoothness slider values for as long as it's
        // on. See MBoosterDeviceController.SetAbsTestActive and
        // MBoosterEffectWorker's _absTestSustained.
        private void MBoosterAbsTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetAbsTestActive(MBoosterAbsTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterTcEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.TractionControl ??= new MBoosterEffectSettings()).Enabled = MBoosterTcEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterTcIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterTcIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.TractionControl ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // Fixed vibration frequency (10-100Hz). See MBoosterEffectSettings
        // .FrequencyHz and MBoosterEffectWorker.UpdateTractionControlRequest.
        private void MBoosterTcFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.TractionControlFreqMinHz, Math.Min((int)MBoosterUiConstants.TractionControlFreqMaxHz, (int)Math.Round(e.NewValue)));
            MBoosterTcFrequencyValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.TractionControl ??= new MBoosterEffectSettings()).FrequencyHz = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — substitutes live throttle position for
        // tcActive (there's no live TC-activation signal to press against
        // outside a real TC event), vibrating continuously at the live
        // Frequency/Intensity slider values for as long as it's on. See
        // MBoosterDeviceController.SetTcTestActive and
        // MBoosterEffectWorker's _tcTestSustained.
        private void MBoosterTcTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetTcTestActive(MBoosterTcTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterWheelSpinEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.WheelSpin ??= new MBoosterEffectSettings()).Enabled = MBoosterWheelSpinEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterWheelSpinIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterWheelSpinIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.WheelSpin ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // Fixed vibration frequency (10-100Hz) — same range as Traction
        // Control. See MBoosterEffectSettings.FrequencyHz and
        // MBoosterEffectWorker.UpdateWheelSpinRequest.
        private void MBoosterWheelSpinFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.WheelSpinFreqMinHz, Math.Min((int)MBoosterUiConstants.WheelSpinFreqMaxHz, (int)Math.Round(e.NewValue)));
            MBoosterWheelSpinFrequencyValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.WheelSpin ??= new MBoosterEffectSettings()).FrequencyHz = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — substitutes live throttle position for
        // the wheelspin heuristic (there's no live wheelspin signal to
        // press against outside a real spin event), vibrating continuously
        // at the live Frequency/Intensity slider values for as long as it's
        // on. See MBoosterDeviceController.SetWheelSpinTestActive and
        // MBoosterEffectWorker's _wheelSpinTestSustained.
        private void MBoosterWheelSpinTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetWheelSpinTestActive(MBoosterWheelSpinTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterGearShiftEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GearShift ??= new MBoosterEffectSettings()).Enabled = MBoosterGearShiftEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterGearShiftIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterGearShiftIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GearShift ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // Fixed vibration frequency (10-100Hz) — same range as Traction
        // Control/Wheel Spin. See MBoosterEffectSettings.FrequencyHz and
        // MBoosterEffectWorker.UpdateGearShiftRequest.
        private void MBoosterGearShiftFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.GearShiftFreqMinHz, Math.Min((int)MBoosterUiConstants.GearShiftFreqMaxHz, (int)Math.Round(e.NewValue)));
            MBoosterGearShiftFrequencyValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GearShift ??= new MBoosterEffectSettings()).FrequencyHz = v;
            _plugin.SaveSettings();
        }
        // Whether a shift landing in Neutral still fires the pulse — off by
        // default (an H-pattern shift produces two transitions, e.g.
        // "1"->"N"->"2", and the engagement bump into the new gear is
        // normally what's wanted). Same knob/rationale as the wheelbase's
        // own GearshiftVibrateOnNeutralCheck.
        private void MBoosterGearShiftVibrateOnNeutralCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GearShift ??= new MBoosterEffectSettings()).VibrateOnNeutral = MBoosterGearShiftVibrateOnNeutralCheck.IsChecked == true;
            _plugin.SaveSettings();
        }
        // Minimum time (ms) between fired pulses — absorbs an H-pattern's
        // double transition (gear->N->gear) so one physical shift doesn't
        // fire twice. Same range/step/default as the wheelbase's own
        // GearshiftDebounceSlider.
        private void MBoosterGearShiftDebounceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            // Snap to 50 ms grid (IsSnapToTickEnabled + TickFrequency=50
            // already enforces this on user input, but be defensive against
            // external sources that bypass the tick grid).
            v = ((v + 25) / 50) * 50;
            v = Math.Max((int)MBoosterUiConstants.GearShiftDebounceMinMs, Math.Min((int)MBoosterUiConstants.GearShiftDebounceMaxMs, v));
            MBoosterGearShiftDebounceValue.Text = $"{v} ms";
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GearShift ??= new MBoosterEffectSettings()).DebounceMs = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — bypasses the real pulse/debounce/neutral
        // machinery entirely, vibrating continuously at the live Frequency/
        // Intensity slider values for as long as it's on (there's no live
        // "gear just changed" signal to press against outside a real
        // shift). See MBoosterDeviceController.SetGearShiftTestActive and
        // MBoosterEffectWorker's _gearShiftTestSustained.
        private void MBoosterGearShiftTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetGearShiftTestActive(MBoosterGearShiftTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterLockupEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Lockup ??= new MBoosterEffectSettings()).Enabled = MBoosterLockupEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        // Fixed vibration frequency (10-100Hz) — replaces the old brake-
        // position mapping. See MBoosterEffectSettings.FrequencyHz and
        // MBoosterEffectWorker.UpdateLockupRequest.
        private void MBoosterLockupFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.LockupFreqMinHz, Math.Min((int)MBoosterUiConstants.LockupFreqMaxHz, (int)Math.Round(e.NewValue)));
            MBoosterLockupFrequencyValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Lockup ??= new MBoosterEffectSettings()).FrequencyHz = v;
            _plugin.SaveSettings();
        }
        private void MBoosterLockupIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterLockupIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Lockup ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — bypasses the wheel-slip detection
        // heuristic entirely, substituting live brake position for it
        // (there's no live "is the wheel actually locking" signal to
        // preview against outside a real drive), vibrating continuously at
        // the live Frequency/Intensity slider values for as long as it's
        // on. See MBoosterDeviceController.SetLockupTestActive and
        // MBoosterEffectWorker's _lockupTestSustained.
        private void MBoosterLockupTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetLockupTestActive(MBoosterLockupTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterThresholdEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Threshold ??= new MBoosterEffectSettings()).Enabled = MBoosterThresholdEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        // Brake position (%) at which the rising-edge trigger fires. The
        // release threshold stays a fixed 30 points below this. See
        // MBoosterEffectSettings.TriggerLevelPct and
        // MBoosterEffectWorker.UpdateThresholdRequest.
        private void MBoosterThresholdTriggerLevel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.ThresholdTriggerMinPct, Math.Min((int)MBoosterUiConstants.ThresholdTriggerMaxPct, (int)Math.Round(e.NewValue)));
            MBoosterThresholdTriggerLevelValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Threshold ??= new MBoosterEffectSettings()).TriggerLevelPct = v;
            _plugin.SaveSettings();
        }
        // Fixed vibration frequency (5-100Hz) — replaces the old brake-
        // position mapping. See MBoosterEffectSettings.FrequencyHz and
        // MBoosterEffectWorker.UpdateThresholdRequest.
        private void MBoosterThresholdFrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.ThresholdFreqMinHz, Math.Min((int)MBoosterUiConstants.ThresholdFreqMaxHz, (int)Math.Round(e.NewValue)));
            MBoosterThresholdFrequencyValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Threshold ??= new MBoosterEffectSettings()).FrequencyHz = v;
            _plugin.SaveSettings();
        }
        private void MBoosterThresholdIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterThresholdIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Threshold ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // How much the pulse fades after its initial burst (0 = barely
        // decays, 100 = drops to silence immediately). See
        // MBoosterEffectSettings.DecayPct and
        // MBoosterEffectSynthesizer.SynthesizeThreshold.
        private void MBoosterThresholdDecay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterThresholdDecayValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Threshold ??= new MBoosterEffectSettings()).DecayPct = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — skips the rising-edge hysteresis entirely,
        // substituting live brake position for it, vibrating continuously
        // at the live Frequency/Intensity/Decay slider values for as long
        // as it's on. See MBoosterDeviceController.SetThresholdTestActive
        // and MBoosterEffectWorker's _thresholdTestSustained.
        private void MBoosterThresholdTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetThresholdTestActive(MBoosterThresholdTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterEngineEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Engine ??= new MBoosterEffectSettings()).Enabled = MBoosterEngineEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        private void MBoosterEngineIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterEngineIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.Engine ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — unlike the other effects' fire-and-forget
        // 1s Test button, this vibrates continuously at the live Frequency/
        // Intensity slider values (both tracked in real time, not a
        // snapshot) for as long as it's on. See
        // MBoosterDeviceController.SetEngineTestActive and
        // MBoosterEffectWorker's _engineTestSustained.
        private void MBoosterEngineTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetEngineTestActive(MBoosterEngineTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        private void MBoosterRoadTextureEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.RoadTexture ??= new MBoosterEffectSettings()).Enabled = MBoosterRoadTextureEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        // Both Intensity and Smoothness are sent to the device as raw
        // percentages — the firmware applies them to the streamed noise
        // signal internally (confirmed from capture: neither affects the
        // noise's shape as transmitted). See
        // MozaMBoosterProtocol.EncodeRoadTextureLevel and
        // MBoosterEffectWorker.ProcessRoadTextureEffect.
        private void MBoosterRoadTextureIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterRoadTextureIntensityValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.RoadTexture ??= new MBoosterEffectSettings()).IntensityPct = v;
            _plugin.SaveSettings();
        }
        private void MBoosterRoadTextureSmoothness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterRoadTextureSmoothnessValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.RoadTexture ??= new MBoosterEffectSettings()).SmoothnessPct = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — bypasses Enabled and the game-running/
        // speed gate entirely (there's no live "how rough is the road"
        // signal to preview against outside a real drive), running
        // continuously at the live Intensity/Smoothness slider values. See
        // MBoosterDeviceController.SetRoadTextureTestActive and
        // MBoosterEffectWorker's _roadTextureTestSustained.
        private void MBoosterRoadTextureTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetRoadTextureTestActive(MBoosterRoadTextureTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        // Brake Fade — NOT a vibration effect. Dynamically rewrites the real
        // Travel End AND Max Threshold hardware calibrations in lockstep
        // while brake temp is above BrakeFadeOnsetC (more travel AND more
        // force needed to reach 100%), restoring the user's own configured
        // values as it cools. See MBoosterEffectWorker.UpdateBrakeFade.
        private void MBoosterBrakeFadeEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            (s.BrakeFade ??= new MBoosterEffectSettings()).Enabled = MBoosterBrakeFadeEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        // Brake temperature (°C) above which Travel End and Max Threshold
        // start ramping — see MBoosterEffectSettings.BrakeFadeOnsetC.
        private void MBoosterBrakeFadeOnsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max((int)MBoosterUiConstants.BrakeFadeOnsetMinC, Math.Min((int)MBoosterUiConstants.BrakeFadeOnsetMaxC, (int)Math.Round(e.NewValue)));
            MBoosterBrakeFadeOnsetValue.Text = v.ToString();
            var s = CurrentMBoosterSettings();
            if (s == null) return;
            (s.BrakeFade ??= new MBoosterEffectSettings()).BrakeFadeOnsetC = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — bypasses Enabled and the brake-temperature
        // gate entirely (there's no live "how hot are the brakes" signal to
        // preview against outside a real drive), forcing Travel End and Max
        // Threshold to their Brake Fade caps for as long as it's on. Each
        // independently requires its own configured base value — otherwise
        // that one is a no-op (the other can still preview on its own). See
        // MBoosterDeviceController.SetBrakeFadeTestActive and
        // MBoosterEffectWorker's _brakeFadeTestActive.
        private void MBoosterBrakeFadeTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetBrakeFadeTestActive(MBoosterBrakeFadeTestToggle.IsChecked == true);
        }

        private void MBoosterGForceEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GForce ??= new MBoosterEffectSettings()).Enabled = MBoosterGForceEnable.IsChecked == true;
            _plugin.SaveSettings();
        }
        // 0-15mm, half-mm steps (matches Pit House's own "Max Pedal
        // Travelment" slider) — see MBoosterEffectSettings.MaxTravelMm.
        private void MBoosterGForceMaxTravel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            double v = Math.Round(e.NewValue * 2) / 2.0;
            v = Math.Max(MBoosterUiConstants.GForceMaxTravelMinMm, Math.Min(MBoosterUiConstants.GForceMaxTravelMaxMm, v));
            MBoosterGForceMaxTravelValue.Text = v.ToString("0.#");
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GForce ??= new MBoosterEffectSettings()).MaxTravelMm = (float)v;
            _plugin.SaveSettings();
        }
        // 0-100% — sent to the firmware unshaped every frame (it does the
        // actual ramping, not the plugin) — see
        // MBoosterEffectSettings.ResponseSpeedPct.
        private void MBoosterGForceResponseSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = Math.Max(0, Math.Min(100, (int)Math.Round(e.NewValue)));
            MBoosterGForceResponseSpeedValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            (s.GForce ??= new MBoosterEffectSettings()).ResponseSpeedPct = v;
            _plugin.SaveSettings();
        }
        // Sustained test toggle — alternates the commanded travel offset
        // forward/backward, mirroring Pit House's own "Test" demo (bypasses
        // Enabled and the game-running gate). See
        // MBoosterDeviceController.SetGForceTestActive.
        private void MBoosterGForceTestToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CurrentMBoosterController()?.SetGForceTestActive(MBoosterGForceTestToggle.IsChecked == true, _mboosterEffectPedalIndex);
        }

        // ===== Calibration (experimental) ===================================

        private void MBoosterDirCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.Direction = MBoosterDirCheck.IsChecked == true ? 1 : 0;
            _plugin.SaveSettings();
        }
        private void MBoosterMinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            MBoosterMinValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.Min = v;
            _plugin.SaveSettings();
        }
        private void MBoosterMaxSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            MBoosterMaxValue.Text = v.ToString();
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.Max = v;
            _plugin.SaveSettings();
        }

        // Sim Input Mapping output curve presets (6 nodes) — derived by
        // sampling the existing 5-point PedalCurvePresets shapes at this
        // curve's own fixed breakpoints (100/7 * k for k=1..6, matching
        // MozaMBoosterRegistry.DefaultCurveX), not new hand-picked values.
        // Linear is the identity (Y[k] == breakpoint[k]), so it also serves
        // as the default X breakpoints below.
        private static readonly int[][] MBoosterCurvePresets =
        {
            new[] { 14, 29, 43, 57, 71, 86 }, // Linear
            new[] { 5, 12, 30, 70, 88, 95 },  // S Curve
            new[] { 4, 9, 16, 25, 41, 66 },   // Exponential
            new[] { 34, 59, 75, 84, 91, 96 }, // Parabolic
        };
        private static readonly float[] MBoosterOutputCurveDefault =
            Array.ConvertAll(MBoosterCurvePresets[0], x => (float)x);

        // Sim Input Mapping output curve (6-point) — PURELY host-side, no
        // wire command (see MozaMBoosterRegistry.EvaluateCurveArbitraryX and
        // docs/protocol/devices/mbooster.md "Sim Input Mapping"): remaps the
        // pedal's already-hardware-shaped raw HID position into what AZOM
        // reports as game telemetry. Nodes are also draggable horizontally
        // (AllowHorizontalDrag on the editor) so "100% output before 100%
        // input" works — see MozaMBoosterRegistry.OnHidAxisUpdate, which
        // evaluates (CurveX, CurveY) directly at the live position rather
        // than resampling to any fixed set of breakpoints.
        private void SetMBoosterCurveY(int index, int v)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            if (s.CurveY == null || s.CurveY.Length != MBoosterUiConstants.SimInputMappingNodeCount)
                s.CurveY = (float[])MBoosterOutputCurveDefault.Clone();
            s.CurveY[index] = v;
            _plugin.SaveSettings();
        }

        private void SetMBoosterCurveX(int index, int v)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            if (s.CurveX == null || s.CurveX.Length != MBoosterUiConstants.SimInputMappingNodeCount)
                s.CurveX = (float[])MBoosterOutputCurveDefault.Clone();
            if (s.CurveY == null || s.CurveY.Length != MBoosterUiConstants.SimInputMappingNodeCount)
                s.CurveY = (float[])MBoosterOutputCurveDefault.Clone();
            s.CurveX[index] = v;
            _plugin.SaveSettings();
        }

        /// <summary>The wire-command role prefix (throttle/brake/clutch) for the
        /// currently-selected config pedal, or null if it has no game role.</summary>
        private string? MBoosterSelectedPedalRolePrefix()
        {
            var s = CurrentMBoosterSettings();
            var c = CurrentMBoosterController();
            if (s == null || c == null) return null;
            // Resolve against the CONNECTED axis count, not the raw HID axis
            // count (bug: a chain-capable hub exposes all 3 GenericDesktop
            // axes even with only one pedal plugged in, so using the raw
            // count here fell into ResolveAxisRole's axis-order fallback
            // instead of reading the single pedal's own Role — silently
            // showing "Throttle" for axis 0 regardless of what the Role
            // dropdown said, and hiding the brake-only Sensor Output Ratio /
            // Max Threshold sliders even for a pedal explicitly set to
            // Brake). Same fix as RefreshMBoosterTab already applies when
            // building the device row list.
            int connectedAxisCount = c.ConnectedAxisIndices().Count;
            if (connectedAxisCount <= 0) connectedAxisCount = 1;
            var role = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResolveAxisRole(s, _mboosterEffectPedalIndex, connectedAxisCount);
            return role == global::MozaPlugin.Devices.MBoosterRole.Throttle ? "throttle"
                 : role == global::MozaPlugin.Devices.MBoosterRole.Brake ? "brake"
                 : role == global::MozaPlugin.Devices.MBoosterRole.Clutch ? "clutch" : null;
        }

        private void MBoosterY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY1Value, "", v => SetMBoosterCurveY(0, v));
        private void MBoosterY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY2Value, "", v => SetMBoosterCurveY(1, v));
        private void MBoosterY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY3Value, "", v => SetMBoosterCurveY(2, v));
        private void MBoosterY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY4Value, "", v => SetMBoosterCurveY(3, v));
        private void MBoosterY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY5Value, "", v => SetMBoosterCurveY(4, v));
        private void MBoosterY6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterY6Value, "", v => SetMBoosterCurveY(5, v));

        private void MBoosterX1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX1Value, "", v => SetMBoosterCurveX(0, v));
        private void MBoosterX2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX2Value, "", v => SetMBoosterCurveX(1, v));
        private void MBoosterX3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX3Value, "", v => SetMBoosterCurveX(2, v));
        private void MBoosterX4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX4Value, "", v => SetMBoosterCurveX(3, v));
        private void MBoosterX5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX5Value, "", v => SetMBoosterCurveX(4, v));
        private void MBoosterX6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterX6Value, "", v => SetMBoosterCurveX(5, v));

        private void ApplyMBoosterCurvePreset(int[] curve)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            int n = MBoosterUiConstants.SimInputMappingNodeCount;
            if (s.CurveY == null || s.CurveY.Length != n) s.CurveY = new float[n];
            // Presets are a clean, standard shape — reset any dragged X
            // positions back to the fixed breakpoints too.
            s.CurveX = null;
            using (_suppressor.Begin())
            {
                MBoosterY1Slider.Value = curve[0]; SetValueText(MBoosterY1Value, curve[0].ToString());
                MBoosterY2Slider.Value = curve[1]; SetValueText(MBoosterY2Value, curve[1].ToString());
                MBoosterY3Slider.Value = curve[2]; SetValueText(MBoosterY3Value, curve[2].ToString());
                MBoosterY4Slider.Value = curve[3]; SetValueText(MBoosterY4Value, curve[3].ToString());
                MBoosterY5Slider.Value = curve[4]; SetValueText(MBoosterY5Value, curve[4].ToString());
                MBoosterY6Slider.Value = curve[5]; SetValueText(MBoosterY6Value, curve[5].ToString());
                MBoosterX1Slider.Value = MBoosterOutputCurveDefault[0]; SetValueText(MBoosterX1Value, MBoosterOutputCurveDefault[0].ToString("F0"));
                MBoosterX2Slider.Value = MBoosterOutputCurveDefault[1]; SetValueText(MBoosterX2Value, MBoosterOutputCurveDefault[1].ToString("F0"));
                MBoosterX3Slider.Value = MBoosterOutputCurveDefault[2]; SetValueText(MBoosterX3Value, MBoosterOutputCurveDefault[2].ToString("F0"));
                MBoosterX4Slider.Value = MBoosterOutputCurveDefault[3]; SetValueText(MBoosterX4Value, MBoosterOutputCurveDefault[3].ToString("F0"));
                MBoosterX5Slider.Value = MBoosterOutputCurveDefault[4]; SetValueText(MBoosterX5Value, MBoosterOutputCurveDefault[4].ToString("F0"));
                MBoosterX6Slider.Value = MBoosterOutputCurveDefault[5]; SetValueText(MBoosterX6Value, MBoosterOutputCurveDefault[5].ToString("F0"));
            }
            for (int i = 0; i < n; i++)
                s.CurveY[i] = curve[i];
            _plugin.SaveSettings();
        }

        private void MBoosterCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyMBoosterCurvePreset(MBoosterCurvePresets[0]);
        private void MBoosterCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyMBoosterCurvePreset(MBoosterCurvePresets[1]);
        private void MBoosterCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyMBoosterCurvePreset(MBoosterCurvePresets[2]);
        private void MBoosterCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyMBoosterCurvePreset(MBoosterCurvePresets[3]);

        // Pedal Feel curve presets (6 nodes) — derived by sampling the
        // existing 5-point PedalCurvePresets shapes (Linear/S-Curve/
        // Exponential/Parabolic) at this curve's own fixed breakpoints
        // (8.05/19.5/44.2/72.4/90.0/97.9% — see
        // MozaMBoosterRegistry.FeelCurveFractions), not new hand-picked
        // values, so the presets keep the same visual identity users
        // already know from the 5-point curve.
        private static readonly int[][] MBoosterInputCurvePresets =
        {
            new[] { 8, 19, 44, 72, 90, 98 },  // Linear
            new[] { 3, 8, 33, 88, 97, 99 },   // S Curve
            new[] { 2, 6, 16, 42, 76, 95 },   // Exponential
            new[] { 20, 45, 76, 91, 97, 99 }, // Parabolic
        };
        private static readonly float[] MBoosterInputCurveDefault =
            Array.ConvertAll(MBoosterInputCurvePresets[0], x => (float)x);

        // Pedal Feel input curve — CONFIRMED real hardware calibration (see
        // MozaMBoosterRegistry.ComputeFeelCurve and
        // MBoosterDeviceController.PushFeelCurveResync): its 6 nodes (0-100%
        // of the Deadzone-Max Force span) populate mbooster-brake-
        // feelcurve-1..6 directly. Unlike SetMBoosterCurveY (host-side,
        // never pushes), every edit here calls PushMBoosterFeelCurve.
        private void SetMBoosterInputCurveY(int index, int v)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            int n = global::MozaPlugin.Devices.MBoosterUiConstants.PedalFeelNodeCount;
            if (s.InputCurveY == null || s.InputCurveY.Length != n)
                s.InputCurveY = Array.ConvertAll(MBoosterInputCurvePresets[0], x => (float)x);
            s.InputCurveY[index] = v;
            PushMBoosterFeelCurve(s);
        }

        private void MBoosterInputY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY1Value, "", v => SetMBoosterInputCurveY(0, v));
        private void MBoosterInputY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY2Value, "", v => SetMBoosterInputCurveY(1, v));
        private void MBoosterInputY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY3Value, "", v => SetMBoosterInputCurveY(2, v));
        private void MBoosterInputY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY4Value, "", v => SetMBoosterInputCurveY(3, v));
        private void MBoosterInputY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY5Value, "", v => SetMBoosterInputCurveY(4, v));
        private void MBoosterInputY6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputY6Value, "", v => SetMBoosterInputCurveY(5, v));

        // Pedal Feel node X position (0-100% of the Deadzone-Max Force span)
        // — CONFIRMED real hardware calibration alongside InputCurveY (see
        // MBoosterDeviceSettings.InputCurveX and
        // MBoosterDeviceController.PushFeelCurveResync): every edit here
        // pushes the same atomic (X, Y) resync SetMBoosterInputCurveY does.
        private void SetMBoosterInputCurveX(int index, int v)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            int n = global::MozaPlugin.Devices.MBoosterUiConstants.PedalFeelNodeCount;
            if (s.InputCurveX == null || s.InputCurveX.Length != n)
                s.InputCurveX = Array.ConvertAll(MBoosterInputCurvePresets[0], x => (float)x);
            s.InputCurveX[index] = v;
            PushMBoosterFeelCurve(s);
        }

        private void MBoosterInputX1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputX1Value, "", v => SetMBoosterInputCurveX(0, v));
        private void MBoosterInputX2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputX2Value, "", v => SetMBoosterInputCurveX(1, v));
        private void MBoosterInputX3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputX3Value, "", v => SetMBoosterInputCurveX(2, v));
        private void MBoosterInputX4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputX4Value, "", v => SetMBoosterInputCurveX(3, v));
        private void MBoosterInputX5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputX5Value, "", v => SetMBoosterInputCurveX(4, v));
        private void MBoosterInputX6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) => OnIntSliderChanged(e.NewValue, MBoosterInputX6Value, "", v => SetMBoosterInputCurveX(5, v));

        private void ApplyMBoosterInputCurvePreset(int[] curve)
        {
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            int n = global::MozaPlugin.Devices.MBoosterUiConstants.PedalFeelNodeCount;
            if (s.InputCurveY == null || s.InputCurveY.Length != n) s.InputCurveY = new float[n];
            // Presets are a clean, standard shape — reset any dragged X
            // positions back to the fixed breakpoints too (same convention
            // as ApplyMBoosterCurvePreset for Sim Input Mapping).
            s.InputCurveX = null;
            using (_suppressor.Begin())
            {
                MBoosterInputY1Slider.Value = curve[0]; SetValueText(MBoosterInputY1Value, curve[0].ToString());
                MBoosterInputY2Slider.Value = curve[1]; SetValueText(MBoosterInputY2Value, curve[1].ToString());
                MBoosterInputY3Slider.Value = curve[2]; SetValueText(MBoosterInputY3Value, curve[2].ToString());
                MBoosterInputY4Slider.Value = curve[3]; SetValueText(MBoosterInputY4Value, curve[3].ToString());
                MBoosterInputY5Slider.Value = curve[4]; SetValueText(MBoosterInputY5Value, curve[4].ToString());
                MBoosterInputY6Slider.Value = curve[5]; SetValueText(MBoosterInputY6Value, curve[5].ToString());
                MBoosterInputX1Slider.Value = MBoosterInputCurveDefault[0]; SetValueText(MBoosterInputX1Value, MBoosterInputCurveDefault[0].ToString("F0"));
                MBoosterInputX2Slider.Value = MBoosterInputCurveDefault[1]; SetValueText(MBoosterInputX2Value, MBoosterInputCurveDefault[1].ToString("F0"));
                MBoosterInputX3Slider.Value = MBoosterInputCurveDefault[2]; SetValueText(MBoosterInputX3Value, MBoosterInputCurveDefault[2].ToString("F0"));
                MBoosterInputX4Slider.Value = MBoosterInputCurveDefault[3]; SetValueText(MBoosterInputX4Value, MBoosterInputCurveDefault[3].ToString("F0"));
                MBoosterInputX5Slider.Value = MBoosterInputCurveDefault[4]; SetValueText(MBoosterInputX5Value, MBoosterInputCurveDefault[4].ToString("F0"));
                MBoosterInputX6Slider.Value = MBoosterInputCurveDefault[5]; SetValueText(MBoosterInputX6Value, MBoosterInputCurveDefault[5].ToString("F0"));
            }
            for (int i = 0; i < n; i++)
                s.InputCurveY[i] = curve[i];
            PushMBoosterFeelCurve(s);
            _plugin.SaveSettings();
        }

        private void MBoosterInputCurvePreset_Linear(object s, RoutedEventArgs e)      => ApplyMBoosterInputCurvePreset(MBoosterInputCurvePresets[0]);
        private void MBoosterInputCurvePreset_SCurve(object s, RoutedEventArgs e)      => ApplyMBoosterInputCurvePreset(MBoosterInputCurvePresets[1]);
        private void MBoosterInputCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyMBoosterInputCurvePreset(MBoosterInputCurvePresets[2]);
        private void MBoosterInputCurvePreset_Parabolic(object s, RoutedEventArgs e)   => ApplyMBoosterInputCurvePreset(MBoosterInputCurvePresets[3]);

        // Start/End of pedal travel (mm) — a real hardware calibration
        // write, reverse-engineered from two real Pit House USB captures:
        // wire commands mbooster-brake-travel-start/-end (cmdIds 0x84/0x85),
        // 2-byte ints, same shape as Min/Max. See
        // MozaMBoosterProtocol.EncodeTravelMm and
        // docs/protocol/devices/mbooster.md "Pedal Feel". MozaRangeSlider
        // has no built-in "changed" CLR event (its Low/HighValue are plain
        // DPs), so it raises RangeChanged instead of the ValueChanged the
        // other mBooster sliders use.
        /// <summary>Motor/config device id for the currently-selected mBooster
        /// pedal's PHYSICAL (per-unit) calibration writes — travel, endstop,
        /// max threshold, sensor ratio, curve7 — routed by ROLE through the
        /// calibration-derived chain map (same as the effect worker; see
        /// MBoosterDeviceController.MotorDeviceForRole), NOT the raw HID axis.
        /// The motor/config device id follows the chain plug position, which
        /// doesn't match the HID axis order, so an axis-index device sends
        /// these to the wrong physical pedal. Falls back to the axis device
        /// until the map resolves. (Direction/Min/Max/output-curve stay on the
        /// host 0x12, which aggregates the output mapping.)</summary>
        private static byte MBoosterCalibDevice(global::MozaPlugin.Devices.MBoosterDeviceController? controller, int axisIndex)
        {
            if (controller == null) return global::MozaPlugin.Protocol.MozaProtocol.DeviceMain;
            // Resolve against the CONNECTED axis count, not the raw HID axis
            // count — same fix as MBoosterSelectedPedalRolePrefix. Otherwise
            // a chain-capable hub with fewer pedals wired than raw axis slots
            // falls into ResolveAxisRole's axis-order fallback here too,
            // routing calibration writes (Travel/Endstop/Max Threshold/
            // Sensor Ratio) to the wrong physical MotorDeviceForRole.
            int axisCount = controller.ConnectedAxisIndices().Count;
            if (axisCount <= 0) axisCount = 1;
            var role = global::MozaPlugin.Devices.MozaMBoosterRegistry.ResolveAxisRole(controller.CurrentSettings, axisIndex, axisCount);
            int roleIdx = role == global::MozaPlugin.Devices.MBoosterRole.Throttle ? 0
                        : role == global::MozaPlugin.Devices.MBoosterRole.Brake ? 1
                        : role == global::MozaPlugin.Devices.MBoosterRole.Clutch ? 2 : -1;
            return controller.MotorDeviceForRole(roleIdx, axisIndex);
        }

        /// <summary>
        /// Park a slider-driven mBooster calibration write on the selected
        /// pedal's own unit, coalesced so a drag emits one write set instead
        /// of one per tick (see MBoosterDeviceController.QueueCalibWrite).
        /// </summary>
        private void QueueMBoosterCalibPush(string key, Action<MBoosterDeviceController, byte> push)
        {
            var controller = CurrentMBoosterController();
            if (controller == null) return;
            byte dev = MBoosterCalibDevice(controller, _mboosterEffectPedalIndex);
            controller.QueueCalibWrite($"{dev:x2}:{key}", () => push(controller, dev));
        }

        /// <summary>
        /// Park a Deadzone/Max Force write (cmdId 0xAB selectors 0x07-0x0E) —
        /// separate from <see cref="QueueMBoosterCalibPush"/> because neither
        /// capture that confirmed this family (max-force-24-75-128-166-200
        /// .pcapng, deadzone-0-5-11-14.pcapng) included the curve7-1..6
        /// resync that helper tacks on for every other calibration write, so
        /// reusing it here would send frames Pit House itself never sends
        /// for this field. Uses the CURRENT value of whichever of the two
        /// fields didn't just change, since the device has no partial-update
        /// form for this 8-value family — see
        /// MBoosterDeviceController.PushFeelCurveResync.
        /// </summary>
        private void PushMBoosterFeelCurve(IMBoosterPedalConfig s)
        {
            var controller = CurrentMBoosterController();
            if (controller == null) return;
            byte dev = MBoosterCalibDevice(controller, _mboosterEffectPedalIndex);
            double dz = s.DeadzoneKg >= 0 ? s.DeadzoneKg : 0;
            double mf = s.MaxForceKg >= 0 ? s.MaxForceKg : 200;
            float[]? curveY = s.InputCurveY;
            float[]? curveX = s.InputCurveX;
            controller.QueueCalibWrite($"{dev:x2}:feel-curve", () =>
                controller.PushFeelCurveResync(dz, mf, curveY, curveX, dev));
        }

        private void MBoosterTravelRangeSlider_RangeChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.TravelStartMm = (float)MBoosterTravelRangeSlider.LowValue;
            s.TravelEndMm = (float)MBoosterTravelRangeSlider.HighValue;
            // Travel is a physical setting on every pedal mode — push to THIS
            // pedal's own mBooster unit (device 0x12 host / 0x1d / 0x1e chain).
            float startMm = s.TravelStartMm, endMm = s.TravelEndMm;
            QueueMBoosterCalibPush("travel", (c, dev) =>
            {
                c.SendIntWrite("mbooster-brake-travel-start",
                    global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeTravelMm(startMm), dev);
                c.SendIntWrite("mbooster-brake-travel-end",
                    global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeTravelMm(endMm), dev);
            });
            _plugin.SaveSettings();
        }

        // Deadzone at the start of pedal travel (0..40kg) — CONFIRMED real
        // hardware calibration (mbooster-brake-deadzone, cmdId 0xAB selector
        // 0x07), reverse-engineered from deadzone-0-5-11-14.pcapng (bug
        // bundle 5VR5AQ8Y). See MBoosterDeviceController.PushFeelCurveResync
        // and PushMBoosterFeelCurve. Decimal precision (0.1kg ticks), so this
        // doesn't reuse OnIntSliderChanged (which rounds to whole numbers
        // like the other mBooster sliders).
        private void MBoosterDeadzoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            double v = Math.Round(e.NewValue, 1);
            SetValueText(MBoosterDeadzoneValue, v.ToString("F1"));
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.DeadzoneKg = (float)v;
            PushMBoosterFeelCurve(s);
            _plugin.SaveSettings();
        }

        // Max Force (0..200kg) — the force at which the pedal's raw HID axis
        // reaches 100% travel. CONFIRMED real hardware calibration
        // (mbooster-brake-maxforce, cmdId 0xAB selector 0x0E), reverse-
        // engineered from max-force-24-75-128-166-200.pcapng (bug bundle
        // 5VR5AQ8Y) — not clamped to Max Threshold on the wire. See
        // MBoosterDeviceController.PushFeelCurveResync and
        // PushMBoosterFeelCurve.
        private void MBoosterMaxForceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterMaxForceValue, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                s.MaxForceKg = v;
                PushMBoosterFeelCurve(s);
            });

        // Sensor Output Ratio — blend between the mBooster's angle sensor
        // (0%) and its load cell (100%). Live-pushes on every drag, same as
        // the wheelbase Brake tab's BrakeAngleRatioSlider (pedals-brake-angle-ratio) —
        // this is the mBooster-side twin of that control (mbooster-brake-angle-ratio).
        private void MBoosterRatioSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int v = (int)Math.Round(e.NewValue);
            SetValueText(MBoosterRatioValue, $"{v}%");
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.SensorOutputRatioPct = v;
            QueueMBoosterCalibPush("angle-ratio",
                (c, dev) => c.SendFloatWrite("mbooster-brake-angle-ratio", v, dev));
            _plugin.SaveSettings();
        }

        // Max Threshold (kg) — Pit House's load-cell-force-for-100%-output
        // setting. Reverse-engineered from a real capture: wire command
        // mbooster-brake-threshold (cmdId 0xB3), a 4-byte big-endian raw
        // uint (NOT a float) on a fixed 0-200kg scale — see
        // MozaMBoosterProtocol.EncodeThresholdKg and
        // docs/protocol/devices/mbooster.md "Sim Input Mapping".
        private void MBoosterMaxThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterMaxThresholdValue, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                s.MaxThresholdKg = v;
                QueueMBoosterCalibPush("brake-threshold", (c, dev) =>
                    c.SendIntWrite("mbooster-brake-threshold",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeThresholdKg(v), dev));
            });

        // End Stop Stiffness (Front Limit / End Limit), 1-10 — Pit House's
        // own hardware calibration. Reverse-engineered from two real
        // captures: both share wire command cmdId 0xB2 with a selector byte
        // (mbooster-brake-endstop-front/-end), 2-byte int on a fixed 1-10
        // scale — see MozaMBoosterProtocol.EncodeEndstopStiffness and
        // docs/protocol/devices/mbooster.md "Pedal Feel".
        private void MBoosterEndstopFrontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterEndstopFrontValue, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                s.EndstopFrontStiffness = v;
                QueueMBoosterCalibPush("endstop-front", (c, dev) =>
                    c.SendIntWrite("mbooster-brake-endstop-front",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeEndstopStiffness(v), dev));
            });

        private void MBoosterEndstopEndSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterEndstopEndValue, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                s.EndstopEndStiffness = v;
                QueueMBoosterCalibPush("endstop-end", (c, dev) =>
                    c.SendIntWrite("mbooster-brake-endstop-end",
                        global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeEndstopStiffness(v), dev));
            });

        // Natural Friction (0-100%) — simulates a frictional force
        // independent of game output. Reverse-engineered from two real Pit
        // House USB captures (a toggle on/off, and a 0/25/50/75/100% slider
        // sweep — see docs/protocol/devices/mbooster.md "Pedal Feel"): wire
        // cmdId 0xAE, sharing the same "prefix bytes + selector" shape as
        // End Stop Stiffness (0xB2). Every capture write sent BOTH
        // selectors with the IDENTICAL value in the same burst, so this
        // control always writes mbooster-brake-friction-0 and -1 together
        // rather than exposing them as separate sliders. There is no
        // separate wire enable bit — the capture's toggle-off write simply
        // sent raw 0 (confirmed via the firmware's own debug log echoing
        // it as fixed-point 0.0).
        private void MBoosterNaturalFrictionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            OnIntSliderChanged(e.NewValue, MBoosterNaturalFrictionValue, "", v =>
            {
                var s = CurrentMBoosterEffectTarget();
                if (s == null) return;
                s.NaturalFrictionPct = v;
                int raw = global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeFrictionPct(v);
                QueueMBoosterCalibPush("friction", (c, dev) =>
                {
                    c.SendIntWrite("mbooster-brake-friction-0", raw, dev);
                    c.SendIntWrite("mbooster-brake-friction-1", raw, dev);
                });
            });

        // Master on/off for Natural Friction — see
        // MBoosterDeviceSettings.NaturalFrictionEnabled. Off pushes raw 0
        // immediately (same effect as dragging the slider to 0, per Pit
        // House's own toggle-off capture) without touching the stored
        // NaturalFrictionPct, so switching back on restores it. The slider
        // is disabled while off to avoid a drag implicitly re-enabling it.
        private void MBoosterNaturalFrictionEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            s.NaturalFrictionEnabled = MBoosterNaturalFrictionEnable.IsChecked == true;
            MBoosterNaturalFrictionSlider.IsEnabled = s.NaturalFrictionEnabled;
            _plugin.SaveSettings();
            float pct = s.NaturalFrictionEnabled ? (float)MBoosterNaturalFrictionSlider.Value : 0f;
            int raw = global::MozaPlugin.Protocol.MozaMBoosterProtocol.EncodeFrictionPct(pct);
            QueueMBoosterCalibPush("friction", (c, dev) =>
            {
                c.SendIntWrite("mbooster-brake-friction-0", raw, dev);
                c.SendIntWrite("mbooster-brake-friction-1", raw, dev);
            });
        }

        // Segmented Damping — "When Pressed". Reverse-engineered from real
        // Pit House USB captures (see docs/protocol/devices/mbooster.md
        // "Segmented Damping"): a SINGLE wire command (cmdId 0xB7) carries
        // the entire feature's state — both "When Pressed" and "When
        // Released" — as one 10-field snapshot, so every edit here must
        // resend all 10 fields, not just the ones this plot owns. The
        // "*Released" fields have no UI yet; they're sent using Pit
        // House's own factory defaults (or whatever was last saved) until
        // "When Released" gets its own plot.
        private void MBoosterSegDampPressedPlot_ValuesChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            var sd = s.SegmentedDamping ??= new MBoosterSegmentedDampingSettings();
            sd.Divider1Pressed = (float)MBoosterSegDampPressedPlot.Divider1;
            sd.Divider2Pressed = (float)MBoosterSegDampPressedPlot.Divider2;
            sd.Seg1Pressed = (float)MBoosterSegDampPressedPlot.Seg1Value;
            sd.Seg2Pressed = (float)MBoosterSegDampPressedPlot.Seg2Value;
            sd.Seg3Pressed = (float)MBoosterSegDampPressedPlot.Seg3Value;
            PushSegmentedDamping(sd);
        }

        // Segmented Damping — "When Released". Same shared wire command as
        // "When Pressed" (see that handler and docs/protocol/devices/
        // mbooster.md "Segmented Damping") — every edit here ALSO resends
        // the current Pressed fields alongside the updated Released ones,
        // since the frame is always a whole-feature snapshot.
        private void MBoosterSegDampReleasedPlot_ValuesChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            var sd = s.SegmentedDamping ??= new MBoosterSegmentedDampingSettings();
            sd.Divider1Released = (float)MBoosterSegDampReleasedPlot.Divider1;
            sd.Divider2Released = (float)MBoosterSegDampReleasedPlot.Divider2;
            sd.Seg1Released = (float)MBoosterSegDampReleasedPlot.Seg1Value;
            sd.Seg2Released = (float)MBoosterSegDampReleasedPlot.Seg2Value;
            sd.Seg3Released = (float)MBoosterSegDampReleasedPlot.Seg3Value;
            PushSegmentedDamping(sd);
        }

        /// <summary>
        /// Save + send the ONE Segmented Damping wire frame (cmdId 0xB7)
        /// covering both "When Pressed" and "When Released" — shared by
        /// both plots' change handlers since either one touching its own
        /// half still has to resend the other half's current values (the
        /// wire command has no partial-update form). Not-yet-set fields
        /// (-1 sentinel) fall back to Pit House's own factory defaults, same
        /// as <see cref="MozaPlugin.ApplyMBoosterToHardware"/> does on connect.
        /// When <see cref="MBoosterSegmentedDampingSettings.DampingEnabled"/>
        /// is off, every segment field is forced to 0% regardless of what's
        /// stored/displayed — same wire effect as the user zeroing all six
        /// sliders themselves.
        /// </summary>
        private void PushSegmentedDamping(MBoosterSegmentedDampingSettings sd)
        {
            _plugin.SaveSettings();

            bool enabled = sd.DampingEnabled;

            // Built inside the parked action so the flush sends whatever the
            // plots hold when the drag settles, not a mid-drag snapshot.
            QueueMBoosterCalibPush("segdamp", (c, dev) =>
                c.SendOneShot(global::MozaPlugin.Protocol.MozaMBoosterProtocol.BuildSegmentedDampingFrame(
                    sd.Divider1Pressed >= 0 ? sd.Divider1Pressed : MBoosterUiConstants.SegDampDivider1PressedDefaultPct,
                    sd.Divider2Pressed >= 0 ? sd.Divider2Pressed : MBoosterUiConstants.SegDampDivider2PressedDefaultPct,
                    sd.Divider1Released >= 0 ? sd.Divider1Released : MBoosterUiConstants.SegDampDivider1ReleasedDefaultPct,
                    sd.Divider2Released >= 0 ? sd.Divider2Released : MBoosterUiConstants.SegDampDivider2ReleasedDefaultPct,
                    !enabled ? 0 : sd.Seg1Pressed >= 0 ? sd.Seg1Pressed : MBoosterUiConstants.SegDampSegDefaultPct,
                    !enabled ? 0 : sd.Seg1Released >= 0 ? sd.Seg1Released : MBoosterUiConstants.SegDampSegDefaultPct,
                    !enabled ? 0 : sd.Seg2Pressed >= 0 ? sd.Seg2Pressed : MBoosterUiConstants.SegDampSegDefaultPct,
                    !enabled ? 0 : sd.Seg2Released >= 0 ? sd.Seg2Released : MBoosterUiConstants.SegDampSegDefaultPct,
                    !enabled ? 0 : sd.Seg3Pressed >= 0 ? sd.Seg3Pressed : MBoosterUiConstants.SegDampSegDefaultPct,
                    !enabled ? 0 : sd.Seg3Released >= 0 ? sd.Seg3Released : MBoosterUiConstants.SegDampSegDefaultPct,
                    dev)));
        }

        // Master on/off for the whole Segmented Damping feature — see
        // MBoosterSegmentedDampingSettings.DampingEnabled and
        // PushSegmentedDamping's zero-forcing above.
        private void MBoosterSegDampEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = CurrentMBoosterEffectTarget();
            if (s == null) return;
            var sd = s.SegmentedDamping ??= new MBoosterSegmentedDampingSettings();
            sd.DampingEnabled = MBoosterSegDampEnable.IsChecked == true;
            MBoosterSegDampPressedPlot.IsEnabled = sd.DampingEnabled;
            MBoosterSegDampReleasedPlot.IsEnabled = sd.DampingEnabled;
            PushSegmentedDamping(sd);
        }

        private void MBoosterReadCalButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentMBoosterController()?.RequestCalibrationReads();
        }
        private void MBoosterApplyCalButton_Click(object sender, RoutedEventArgs e)
        {
            var c = CurrentMBoosterController();
            var s = CurrentMBoosterSettings();
            if (c == null || s == null) return;
            _plugin.ApplyMBoosterToHardware(c, s);
        }

        // ------- Language picker (Options tab) -------
        // null Culture = "Auto" row; otherwise a BCP-47 tag the user picked
        // explicitly. Display is the language's own name so a user who can't
        // read the current UI can still find theirs.
        private sealed class LanguageOption
        {
            public string? Culture { get; set; }
            public string Display { get; set; } = "";
            public override string ToString() => Display;
        }

        private void InitLanguageCombo()
        {
            using (_suppressor.Begin())
            {
                var items = new List<LanguageOption>
                {
                    new LanguageOption { Culture = null, Display = "Auto" },
                };
                foreach (var code in LanguageResolver.SupportedCultures)
                {
                    var display = LanguageResolver.DisplayNames.TryGetValue(code, out var name) ? name : code;
                    items.Add(new LanguageOption { Culture = code, Display = display });
                }
                LanguageCombo.ItemsSource = items;

                var current = _plugin.Settings.PreferredLanguage;
                LanguageCombo.SelectedItem = items.Find(i =>
                    string.Equals(i.Culture ?? "", current ?? "", StringComparison.OrdinalIgnoreCase))
                    ?? items[0];
            }
        }

        private void LanguageCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (LanguageCombo.SelectedItem is not LanguageOption opt) return;
            _plugin.Settings.PreferredLanguage = opt.Culture; // null = Auto
            _plugin.SaveSettings();
        }

    }
}
