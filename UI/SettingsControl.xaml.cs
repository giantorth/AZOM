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

namespace MozaPlugin.UI
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
                SyncWheelbaseLfeSourceCombo();
                SyncAutoStandbyCombo();
                int kaSec = plugin.Settings.WheelKeepaliveTimeoutSec;
                KeepaliveTimeoutSlider.Value = Math.Max(KeepaliveTimeoutSlider.Minimum, Math.Min(KeepaliveTimeoutSlider.Maximum, kaSec));
                KeepaliveTimeoutValue.Text = $"{kaSec} s";
                // Gearshift coalescing controls (GearshiftVibrateOnNeutralCheck,
                // GearshiftDebounceSlider) are profile-sourced — populated by
                // RefreshBaseTab on every 500 ms tick so a profile switch with
                // the panel open tracks the new game's values. See the comment
                // in RefreshBaseTab for why the constructor copy was removed.
            }

            InitProfilesTab();
            InitRedesignControls();
            InitSdkCard();
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
                ConfigureSdkInApp = NavigateToSdkSettings,
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
            PreviewMouseDown  += (s, ev) => _plugin?.Standby?.NotifyUserActivity();
            PreviewKeyDown    += (s, ev) => _plugin?.Standby?.NotifyUserActivity();
            PreviewMouseWheel += (s, ev) => _plugin?.Standby?.NotifyUserActivity();

            RequestAllSettings();
        }

        private void OnSteeringAngleTick(object? sender, EventArgs e)
        {
            try { UpdateHidInputDisplays(); }
            catch (Exception ex) { MozaLog.DebugIfChanged("ui-tick-hid", $"[AZOM] HID display tick failed: {ex}"); }
        }

        private void OnLoadedStartTimers(object sender, RoutedEventArgs e)
        {
            // WPF can fire Loaded more than once if the control is reparented
            // (SimHub's tab containers do this during settings-panel layout).
            // Calling Start() twice would double the tick rate.
            bool wasRunning = _refreshTimer.IsEnabled;
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
            if (!_steeringAngleTimer.IsEnabled) _steeringAngleTimer.Start();
            if (_bandwidthTimer != null && !_bandwidthTimer.IsEnabled) _bandwidthTimer.Start();
            // Card visibility only — the torque sampler is plugin-side and always on.
            ApplyBaseGraphMode();

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
            _calCountdownTimer?.Stop();
            _baseCalStatusTimer?.Stop();

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
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

    }
}
