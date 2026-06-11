using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MozaControls;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.UI;
using static MozaPlugin.UI.UiHelpers;
using static MozaPlugin.Devices.WheelUi.WheelUiHelpers;

namespace MozaPlugin.Devices.WheelUi
{
    /// <summary>
    /// Shared dashboard-management surface (dash selection + channel mapping +
    /// file inventory/upload), hosted by the wheel device page and the CM2
    /// device page. Self-contained: resolves the plugin and self-refreshes.
    /// </summary>
    public partial class DashboardManagementControl : UserControl
    {
        private MozaPlugin? _plugin;
        private MozaDeviceManager? _device;
        private MozaData? _data;
        private MozaPluginSettings? _settings;
        private readonly EventSuppressor _suppressor = new EventSuppressor();
        private bool _suppressEvents => _suppressor.Suppressed;

        // Plugin instance we've attached DashboardSelectionChanged to. Tracked
        // separately from _plugin so re-resolving (after a plugin reload while
        // the control is reused, or when ResolvePlugin first sees a non-null
        // Instance) re-subscribes to the new instance and detaches the old.
        private MozaPlugin? _dashEventSubscribedPlugin;

        private readonly DispatcherTimer _refreshTimer;

        // Debounce: commit display brightness only after the slider settles,
        // so rapid drags don't flood the sess=0x02 retransmit queue.
        private DispatcherTimer? _displayBrightnessDebounce;
        private static readonly TimeSpan DisplayBrightnessDebounce = TimeSpan.FromMilliseconds(500);

        public DashboardManagementControl()
        {
            using (_suppressor.Begin())
            {
                InitializeComponent();
                ResolvePlugin();
            }

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += OnRefreshTick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnRefreshTick(object? sender, EventArgs e) => RefreshDashboardManagement();

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ResolvePlugin();
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopMappingValueTimer();
            _refreshTimer.Stop();

            if (_dashEventSubscribedPlugin != null)
            {
                try { _dashEventSubscribedPlugin.DashboardSelectionChanged -= OnPluginDashboardSelectionChanged; }
                catch { }
                try { _dashEventSubscribedPlugin.Fsr1ActiveIndexChanged -= OnFsr1ActiveIndexChanged; }
                catch { }
                _dashEventSubscribedPlugin = null;
            }

            // Flush any pending brightness debounce so the user's latest
            // slider value reaches the wheel + persisted settings even if
            // they navigate away before the 1s debounce expires.
            if (_displayBrightnessDebounce != null && _displayBrightnessDebounce.IsEnabled)
                DisplayBrightnessDebounce_Tick(this, EventArgs.Empty);
        }

        private void OnPluginDashboardSelectionChanged(object? sender, EventArgs e)
        {
            // Profile load runs on SimHub's profile-event thread, not the UI thread.
            // Marshal to the dispatcher before touching ComboBox.Items / SelectedIndex.
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnPluginDashboardSelectionChanged(sender, e)));
                return;
            }

            if (_plugin == null)
            {
                MozaLog.Debug("[AZOM] UI: DashboardSelectionChanged handler — _plugin null, skipping");
                return;
            }

            // Always re-populate: event can arrive long after the first refresh tick.
            // Wheel-initiated dashboard switches also fire this event (via
            // DashboardBindingCoordinator.RaiseDashboardSelectionChangedInternal),
            // so refreshing the channel-mapping grid here covers both UI- and
            // wheel-initiated switches.
            MozaLog.Debug(
                $"[AZOM] UI: DashboardSelectionChanged handler — selected='{_plugin.ActiveTelemetryProfileName}'");
            PopulateDashboardCombo();
            PopulateChannelMappingList();
        }

        // FSR V1: the wheel reported a dashboard switch (parsed from its Param-6 log,
        // or our own select was applied) — re-select the dropdown to match.
        private void OnFsr1ActiveIndexChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnFsr1ActiveIndexChanged(sender, e)));
                return;
            }
            if (_plugin == null || !_plugin.IsFsr1DisplayWheel) return;
            PopulateDashboardCombo();
            // The channel-mapping list follows the active dashboard (CM2-like) — the
            // active page's record type(s) determine which fields are shown.
            PopulateChannelMappingList();
        }

        // CM1 base-bridged dash reported a page switch (Param-6 log) or our select was
        // applied — re-select the dropdown on the CM2/CM1 page to match.
        private void OnCm1ActiveIndexChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnCm1ActiveIndexChanged(sender, e)));
                return;
            }
            if (!IsCm1) return;
            PopulateDashboardCombo();
        }

        private bool ResolvePlugin()
        {
            _plugin = MozaPlugin.Instance;
            if (_plugin == null) return false;
            _device = _plugin.DeviceManager;
            _data = _plugin.Data;
            _settings = _plugin.Settings;

            // Re-subscribe to dashboard events when plugin instance changes (Refresh tick self-heals on reload).
            if (!ReferenceEquals(_dashEventSubscribedPlugin, _plugin))
            {
                if (_dashEventSubscribedPlugin != null)
                {
                    try { _dashEventSubscribedPlugin.DashboardSelectionChanged -= OnPluginDashboardSelectionChanged; }
                    catch { }
                    try { _dashEventSubscribedPlugin.Fsr1ActiveIndexChanged -= OnFsr1ActiveIndexChanged; }
                    catch { }
                    try { _dashEventSubscribedPlugin.Cm1ActiveIndexChanged -= OnCm1ActiveIndexChanged; }
                    catch { }
                }
                _plugin.DashboardSelectionChanged += OnPluginDashboardSelectionChanged;
                _plugin.Fsr1ActiveIndexChanged += OnFsr1ActiveIndexChanged;
                _plugin.Cm1ActiveIndexChanged += OnCm1ActiveIndexChanged;
                _dashEventSubscribedPlugin = _plugin;
                MozaLog.Debug(
                    $"[AZOM] UI: subscribed to DashboardSelectionChanged (plugin hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_plugin)})");
            }

            return true;
        }

        /// <summary>When true this control configures the CM2 dash pipeline
        /// (<see cref="MozaPlugin.Cm2Sender"/>, keyed under the CM2 device GUID),
        /// not the wheel. Set by the CM2 device page. Default false = wheel.</summary>
        internal bool IsCm2Target { get; set; }

        /// <summary>The telemetry sender this control configures (wheel or CM2).</summary>
        private global::MozaPlugin.Telemetry.TelemetrySender? ActiveSender =>
            _plugin == null ? null : (IsCm2Target ? _plugin.ActiveCm2Sender : _plugin.TelemetrySender);

        /// <summary>True when this (CM2) page's dash is a confirmed CM1 (group-0x35) —
        /// it's driven by the Cm1DisplayDriver with a flat field set, not tier-def.</summary>
        private bool IsCm1 => IsCm2Target && (_plugin?.DashIsCm1 ?? false);

        private void RefreshDashboardManagement()
        {
            if (!ResolvePlugin()) return;
            // CM2 page: refresh its dashboard dropdown (from the CM2 sender's reported
            // list) + channel-mapping list. Skip the wheel-only status / display /
            // files sections.
            if (IsCm2Target)
            {
                InitTelemetryUI();
                // CM1: static page list (group-0x35) + flat field rows — no tier-def
                // slot / ConfigJsonList to track.
                if (IsCm1)
                {
                    PopulateDashboardCombo();
                    long sigc = ComputeMappingDataSignature();
                    if (sigc != _lastMappingDataSignature) PopulateChannelMappingList();
                    return;
                }
                int cm2Slot = ActiveSender?.WheelReportedSlot ?? -1;
                bool cm2StateReady = (ActiveSender?.WheelState?.ConfigJsonList?.Count ?? 0) > 0;
                if (cm2Slot != _lastPopulatedWheelSlot || (cm2StateReady && !_dashComboFromWheelState))
                {
                    if (cm2StateReady) _dashComboFromWheelState = true;
                    PopulateDashboardCombo();
                }
                long sig = ComputeMappingDataSignature();
                if (sig != _lastMappingDataSignature) PopulateChannelMappingList();
                return;
            }
            InitTelemetryUI();
            RefreshTelemetryStatus();
            RefreshFilesTab();
            RefreshDisplaySection();
        }

        // Seed the Display brightness slider + standby combo from device state.
        private void RefreshDisplaySection()
        {
            if (_plugin == null || _data == null) return;
            using (_suppressor.Begin())
            {
                // Fallback chain when _data hasn't been populated yet
                // (window between connect and first ApplyDashToHardware):
                // active profile → settings default (100). Avoids showing 0
                // on the slider — which would lie about the wheel's real
                // brightness and let a track-click commit 0 to the wire.
                int b = _data.DashDisplayBrightness;
                if (b < 0)
                {
                    var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
                    b = profile?.DashDisplayBrightness ?? -1;
                    if (b < 0) b = _plugin.Settings?.DashDisplayBrightness ?? 100;
                }
                if (b < 0) b = 0; else if (b > 100) b = 100;
                WheelDisplayBrightnessSlider.Value = b;
                WheelDisplayBrightnessValue.Text = $"{b}";
                SelectWheelDisplayStandbyByMinutes(_data.DashDisplayStandbyMin);
            }
        }

        // ===== Dashboard Telemetry =====

        private bool _telemetryUIInitialized;

        private void InitTelemetryUI()
        {
            if (_telemetryUIInitialized || _plugin == null) return;
            _telemetryUIInitialized = true;

            using (_suppressor.Begin())
            {
                var s = _plugin.Settings;
                TelemetryEnabledCheck.IsChecked = _plugin.ActiveTelemetryEnabled;

                PopulateDashboardCombo();
                // CHANNEL MAPPINGS is always-on (no expander gate). Bind the
                // ItemsControl to its ObservableCollection once, then seed the
                // list + start the 2 Hz value poller.
                TelemetryChannelList.ItemsSource = _channelRows;
                PopulateChannelMappingList();
                StartMappingValueTimer();
            }
        }

        /// <summary>
        /// Populate the dashboard dropdown from the wheel's reported dashboard
        /// list (session 0x09 configJson state push). Falls back to builtin
        /// profile names if the wheel hasn't pushed state yet.
        /// </summary>
        private void PopulateDashboardCombo()
        {
            if (_plugin == null) return;

            // CM1 base-bridged dash: built-in pages selected via the 0x32/0x81 switch
            // (1-based 1..N); no tier-def ConfigJsonList. Mirrors the FSR1 branch.
            if (IsCm1)
            {
                using (_suppressor.Begin())
                {
                    TelemetryProfileCombo.Items.Clear();
                    int min = global::MozaPlugin.Telemetry.Cm1DisplayEmitter.MinDashboardIndex;
                    int max = global::MozaPlugin.Telemetry.Cm1DisplayEmitter.MaxDashboardIndex;
                    for (int i = min; i <= max; i++)
                        TelemetryProfileCombo.Items.Add($"Dashboard {i}");
                    int active = _plugin.GetActiveCm1Index();
                    int idx = active - min;
                    TelemetryProfileCombo.SelectedIndex = (idx >= 0 && idx < TelemetryProfileCombo.Items.Count) ? idx : 0;
                    _lastPopulatedWheelSlot = -1;
                }
                return;
            }

            // FSR V1: the dropdown selects which built-in dashboard (group-0x42
            // record type) the plugin streams — there is no wheel-reported
            // configJsonList. List the catalog's live dashboards. (Wheel target only;
            // a CM2 is always tier-def.)
            if (!IsCm2Target && _plugin.IsFsr1DisplayWheel)
            {
                // 19 built-in dashboard positions (index 0..18). Selecting one sends
                // the group-0x32/0x81 select command; the wheel can also switch itself
                // (auto-followed via OnFsr1ActiveIndexChanged).
                using (_suppressor.Begin())
                {
                    TelemetryProfileCombo.Items.Clear();
                    int max = global::MozaPlugin.Telemetry.Fsr1DisplayEmitter.MaxDashboardIndex;
                    for (int i = 0; i <= max; i++)
                        TelemetryProfileCombo.Items.Add($"Dashboard {i + 1}");
                    int active = _plugin.GetActiveFsr1Index();
                    TelemetryProfileCombo.SelectedIndex = (active >= 0 && active <= max) ? active : 0;
                    _lastPopulatedWheelSlot = -1;
                }
                return;
            }

            using (_suppressor.Begin())
            {
                TelemetryProfileCombo.Items.Clear();

                // Source dashboards from the target's OWN sender state (wheel sender
                // for the wheel page, CM2 sender for the CM2 page).
                var sender = ActiveSender;
                var state = sender?.WheelState;
                if (state != null && state.ConfigJsonList.Count > 0)
                {
                    // Device-reported dashboards in configJsonList order.
                    // Dropdown index = configJsonList slot used by the switch command.
                    foreach (var name in state.ConfigJsonList)
                        TelemetryProfileCombo.Items.Add(name);
                }
                else if (!IsCm2Target)
                {
                    // Wheel-only fallback: cached dashboard names (state not up yet).
                    TelemetryProfileCombo.Items.Add("(none)");
                    if (_plugin.DashCache != null)
                    {
                        foreach (var name in _plugin.DashCache.CachedNames)
                            TelemetryProfileCombo.Items.Add(name);
                    }
                    if (!string.IsNullOrEmpty(_plugin.ActiveTelemetryMzdashPath))
                        TelemetryProfileCombo.Items.Add(
                            "[Custom: " + System.IO.Path.GetFileName(
                                _plugin.ActiveTelemetryMzdashPath) + "]");
                }

                // Show the device-reported slot (ground truth); fall back to the
                // target's saved selection name.
                string? selectedName = null;
                if (sender != null && state != null && state.ConfigJsonList != null
                    && sender.WheelReportedSlot >= 0
                    && sender.WheelReportedSlot < state.ConfigJsonList.Count)
                {
                    string reportedName = state.ConfigJsonList[sender.WheelReportedSlot];
                    if (!string.IsNullOrEmpty(reportedName))
                        selectedName = reportedName;
                }
                if (string.IsNullOrEmpty(selectedName))
                    selectedName = IsCm2Target ? _plugin.ActiveCm2DashboardName : _plugin.ActiveTelemetryProfileName;
                if (!string.IsNullOrEmpty(selectedName))
                {
                    for (int i = 0; i < TelemetryProfileCombo.Items.Count; i++)
                    {
                        // OrdinalIgnoreCase to match the rest of the dashboard
                        // binding chain (DashboardBindingCoordinator key/name
                        // lookups); a case mismatch between the saved profile
                        // name and the wheel's ConfigJsonList entry used to
                        // leave the combo stuck on the prior selection.
                        if (string.Equals(TelemetryProfileCombo.Items[i]?.ToString(), selectedName, StringComparison.OrdinalIgnoreCase))
                        {
                            TelemetryProfileCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
                if (TelemetryProfileCombo.SelectedIndex < 0 && TelemetryProfileCombo.Items.Count > 0)
                    TelemetryProfileCombo.SelectedIndex = 0;

                // Snapshot the wheel-reported slot we just populated against,
                // so RefreshTelemetryStatus only triggers a repopulate when
                // the wheel reports a new slot rather than on every tick.
                _lastPopulatedWheelSlot = sender?.WheelReportedSlot ?? -1;
            }
        }

        /// <summary>
        /// Track whether the dropdown was last populated from wheel-reported
        /// dashboards vs fallback builtins, so we can re-populate when wheel
        /// state first arrives mid-session.
        /// </summary>
        private bool _dashComboFromWheelState;

        /// <summary>
        /// Slot value the dropdown was last populated against
        /// (<see cref="TelemetrySender.WheelReportedSlot"/>). Repopulate when
        /// this changes — handles host-initiated startup switches (kind=4
        /// emitted before wheel state finished arriving) where the wheel's
        /// echo arrives a beat after the one-shot wheel-state population,
        /// and the dropdown would otherwise stay on the pre-switch slot
        /// forever. Initialised to int.MinValue so the first observation
        /// of any slot (including -1) is treated as a change.
        /// </summary>
        private int _lastPopulatedWheelSlot = int.MinValue;

        // Signature of the data feeding the channel-mapping list. Composed from
        // (profile-ref-hash, tier-count, total-channel-count, string-channel-count,
        // catalog-count) — any change means the wheel sent more data and we should
        // rebuild. -1 means "never populated".
        private long _lastMappingDataSignature = -1;

        // Observable backing for the CHANNEL MAPPINGS ItemsControl. Bound once
        // in InitTelemetryUI; PopulateChannelMappingList clears + repopulates
        // in place so the XAML never needs to rebind ItemsSource.
        private readonly ObservableCollection<ChannelMappingRow> _channelRows
            = new ObservableCollection<ChannelMappingRow>();

        private long ComputeMappingDataSignature()
        {
            if (_plugin == null) return -2;
            // CM1 rows come from the static flat catalog — fixed signature (populate once).
            if (IsCm1) return -4;
            // FSR V1 rows come from the static catalog but follow the ACTIVE dashboard,
            // so the signature varies with the active index (re-populate on switch; the
            // Fsr1ActiveIndexChanged event also drives this). Distinct from the other
            // negative sentinels (-2/-4) and from positive profile signatures.
            if (!IsCm2Target && _plugin.IsFsr1DisplayWheel) return -100L - _plugin.GetActiveFsr1Index();
            var profile = ActiveSender?.Profile;
            // CM2 has no wheel-catalog fallback — its list comes from its own profile.
            int catalogCount = IsCm2Target ? 0 : (_plugin.WheelChannelCatalogForDiagnostics?.Count ?? 0);
            if (profile == null) return ((long)catalogCount << 40);
            int tiers = profile.Tiers.Count;
            int channels = 0;
            for (int i = 0; i < tiers; i++) channels += profile.Tiers[i].Channels.Count;
            int strings = profile.StringChannels.Count;
            // Pack: profile identity (low bits of hashcode) + counts. Profile rebuilds
            // on dashboard switch get a new object reference → different hash → re-populate.
            long sig = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(profile) & 0xFFFFFFL;
            sig |= (long)(tiers      & 0xFF) << 24;
            sig |= (long)(channels   & 0xFFFF) << 32;
            sig |= (long)(strings    & 0xFF) << 48;
            sig |= (long)(catalogCount & 0xFF) << 56;
            return sig;
        }

        private void RefreshTelemetryStatus()
        {
            if (_plugin == null) return;

            // Re-populate dropdown when wheel state first becomes available
            // OR when the wheel-reported slot changes. The slot check catches
            // the startup case where the plugin emits kind=4 to apply the
            // saved profile's dashboard before the wheel's b2h type-04 echo
            // lands — without it the dropdown shows the wheel's pre-switch
            // dash forever even though the wheel switched correctly.
            var state = _plugin.WheelStateForDiagnostics;
            var senderForCombo = _plugin.TelemetrySender;
            int curWheelSlot = senderForCombo?.WheelReportedSlot ?? -1;
            bool needPopulate =
                (!_dashComboFromWheelState && state != null && state.ConfigJsonList.Count > 0)
                || (curWheelSlot != _lastPopulatedWheelSlot);
            if (needPopulate)
            {
                if (state != null && state.ConfigJsonList.Count > 0)
                    _dashComboFromWheelState = true;
                PopulateDashboardCombo();
            }

            // Re-seed CHANNEL MAPPINGS when the data source first appears AND
            // whenever it grows. The wheel streams the catalog/tier list in
            // chunks: a one-shot populate on first byte misses the rest. Re-poll
            // every tick using a cheap signature (profile-ref + tier/channel
            // counts + catalog count) and rebuild only when the signature
            // actually changes. PopulateChannelMappingList snapshots the
            // signature itself so other call sites also keep this in sync.
            long sig = ComputeMappingDataSignature();
            if (sig != _lastMappingDataSignature) PopulateChannelMappingList();

            bool enabled = _plugin.ActiveTelemetryEnabled;
            var active = _plugin.TelemetrySender;
            bool testMode = _plugin.DashboardTestPatternActive;

            // Sync checkbox to overlay each tick so game/profile switches reflect immediately.
            if (TelemetryEnabledCheck.IsChecked != enabled)
            {
                using (_suppressor.Begin())
                    TelemetryEnabledCheck.IsChecked = enabled;
            }

            // Sender ready for user switching: Active + not in cooldown + no pending apply.
            // IsPendingDashboardApply stays true across the full switch transient so the
            // combo doesn't flicker.
            bool inCooldown = active?.IsInSilenceCooldown ?? false;
            bool pendingApply = _plugin?.IsPendingDashboardApply ?? false;
            bool senderReady = active != null && active.IsActive && !inCooldown && !pendingApply;
            // FSR V1 renders via its standalone 0x42 driver, not the tier-def sender — that
            // sender never goes Active, so gate the selector + status on the driver instead.
            bool fsr1 = _plugin?.IsFsr1DisplayWheel ?? false;
            bool fsr1Running = fsr1 && (_plugin?.IsFsr1DriverRunning ?? false);
            bool selectorReady = senderReady || fsr1Running;
            // Surface the pipeline health model first so recovery/park states are
            // truthful instead of mislabeling a parked pipeline as "Connecting…" forever.
            var phase = active?.Phase ?? PipelinePhase.Idle;
            if (!enabled)
                DashboardTelemetryCard.Subtitle = "Disabled";
            else if (testMode)
                DashboardTelemetryCard.Subtitle = "Test pattern";
            else if (fsr1)
                DashboardTelemetryCard.Subtitle = fsr1Running ? "Connected" : "Connecting to wheel…";
            else if (phase == PipelinePhase.Parked)
                DashboardTelemetryCard.Subtitle = (active?.Recovery?.ParkIsDegraded ?? false)
                    ? global::MozaPlugin.Resources.Strings.Status_DegradedScreenless
                    : global::MozaPlugin.Resources.Strings.Status_TelemetryParked;
            else if (phase == PipelinePhase.Recovery)
                DashboardTelemetryCard.Subtitle = global::MozaPlugin.Resources.Strings.Status_Recovering;
            else if (active != null && !active.IsActive)
                DashboardTelemetryCard.Subtitle = inCooldown
                    ? "Switching dashboard… (post-emit silence)"
                    : "Connecting to wheel…";
            else if (pendingApply)
            {
                string? why = _plugin?.PendingDashboardApplyDescription;
                DashboardTelemetryCard.Subtitle = string.IsNullOrEmpty(why)
                    ? "Switching dashboard…"
                    : $"Switching dashboard… ({why})";
            }
            else if (inCooldown)
                DashboardTelemetryCard.Subtitle = "Switching dashboard…";
            else
                DashboardTelemetryCard.Subtitle = "Connected";

            // The standalone FSR1/CM1 drivers render the pattern without a tier-def
            // sender, so the button is live whenever any display pipeline is running.
            TelemetryTestBtn.IsEnabled = senderReady || (_plugin?.IsAnyDashboardDisplayRunning ?? false);
            TelemetryTestBtn.Content = testMode
                ? global::MozaPlugin.Resources.Strings.Button_StopTest
                : global::MozaPlugin.Resources.Strings.Button_SendTestPattern;
            TelemetryProfileCombo.IsEnabled = selectorReady;

            // Byte-ruler diagnostic button: FSR1-only, live whenever its driver runs.
            bool rulerOn = _plugin?.Fsr1ByteRulerActive ?? false;
            Fsr1ByteRulerBtn.Visibility = fsr1 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            Fsr1ByteRulerBtn.IsEnabled = fsr1Running;
            Fsr1ByteRulerBtn.Content = rulerOn ? "Stop ruler" : "Byte ruler";

            // Refresh profile info — auto-renegotiate may have swapped
            // the profile on a background thread after a dashboard switch.
        }

        private void TelemetryEnabledCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetTelemetryEnabled(TelemetryEnabledCheck.IsChecked == true);
        }

        private void WheelDisplayBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            WheelDisplayBrightnessValue.Text = $"{val}";
            _data!.DashDisplayBrightness = val;
            _plugin.UpdateActiveProfile(p => p.DashDisplayBrightness = val);
            // Defer wire write + persist until slider settles — avoids flooding
            // the sess=0x02 retransmit queue with intermediate values.
            if (_displayBrightnessDebounce == null)
            {
                _displayBrightnessDebounce = new DispatcherTimer { Interval = DisplayBrightnessDebounce };
                _displayBrightnessDebounce.Tick += DisplayBrightnessDebounce_Tick;
            }
            _displayBrightnessDebounce.Stop();
            _displayBrightnessDebounce.Start();
        }

        private void DisplayBrightnessDebounce_Tick(object? sender, EventArgs e)
        {
            _displayBrightnessDebounce?.Stop();
            if (_plugin == null || _data == null) return;
            int val = _data.DashDisplayBrightness;
            // Sentinel guard: a real slider drag writes _data in ValueChanged
            // before arming this timer, so val<0 here means the timer fired
            // without any user input behind it (stale arm). Refuse the push
            // so we don't clobber the wheel's existing brightness with 0.
            if (val < 0)
            {
                global::MozaPlugin.MozaLog.Debug(
                    "[AZOM] DisplayBrightnessDebounce_Tick: skipping wire push — _data is sentinel");
                return;
            }
            if (val > 100) val = 100;
            // allowZero: true — slider-to-zero is deliberate user intent; other call sites suppress 0.
            _plugin.TelemetrySender?.SendDashDisplayBrightness(val, allowZero: true);
            _plugin.SaveSettings();
        }

        private void WheelDisplayStandbyCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var item = WheelDisplayStandbyCombo.SelectedItem as ComboBoxItem;
            if (item?.Tag is not string tagStr) return;
            if (!int.TryParse(tagStr, out int minutes)) return;
            _data!.DashDisplayStandbyMin = minutes;
            _plugin.UpdateActiveProfile(p => p.DashDisplayStandbyMin = minutes);
            _plugin.TelemetrySender?.SendDashDisplayStandbyMinutes(minutes);
            _plugin.SaveSettings();
        }

        private void SelectWheelDisplayStandbyByMinutes(int minutes)
        {
            for (int i = 0; i < WheelDisplayStandbyCombo.Items.Count; i++)
            {
                if (WheelDisplayStandbyCombo.Items[i] is ComboBoxItem cbi
                    && cbi.Tag is string tag
                    && int.TryParse(tag, out int m)
                    && m == minutes)
                {
                    WheelDisplayStandbyCombo.SelectedIndex = i;
                    return;
                }
            }
            WheelDisplayStandbyCombo.SelectedIndex = -1;
        }

        private void TelemetryProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var selected = TelemetryProfileCombo.SelectedItem?.ToString();
            if (selected == null) return;

            int idx = TelemetryProfileCombo.SelectedIndex;

            // CM1 base-bridged dash: selecting a page sends the group-0x32/0x81 select to
            // dev 0x14 (1-based page = combo index + min). Checked before the FSR1 branch
            // because the wheel may itself be an FSR1 (this is the dash page).
            if (IsCm1)
            {
                int min = global::MozaPlugin.Telemetry.Cm1DisplayEmitter.MinDashboardIndex;
                int page = idx + min;
                if (idx >= 0 && page <= global::MozaPlugin.Telemetry.Cm1DisplayEmitter.MaxDashboardIndex)
                {
                    _plugin.SetActiveCm1Index(page, sendToWheel: true);
                    PopulateChannelMappingList();
                    TelemetryMappingStatus.Text = $"CM1 → Dashboard {page}";
                }
                return;
            }

            // FSR V1: selecting a dashboard sends the group-0x32/0x81 select command
            // (index = combo position). The wheel switches its displayed page.
            if (!IsCm2Target && _plugin.IsFsr1DisplayWheel)
            {
                if (idx >= 0 && idx <= global::MozaPlugin.Telemetry.Fsr1DisplayEmitter.MaxDashboardIndex)
                {
                    _plugin.SetActiveFsr1Index(idx, sendToWheel: true);
                    TelemetryMappingStatus.Text = $"Switched to Dashboard {idx + 1}";
                }
                return;
            }

            // CM2 page: switch the CM2's OWN dashboard (its sender's reported list),
            // persist the CM2's selection, and emit FF kind=4 on the CM2 sender.
            if (IsCm2Target)
            {
                var cm2State = _plugin.ActiveCm2Sender?.WheelState;
                if (cm2State != null && idx >= 0 && idx < cm2State.ConfigJsonList.Count)
                {
                    _plugin.ActiveCm2DashboardName = selected;
                    _plugin.SaveSettings();
                    _plugin.OnCm2DashboardSwitched((uint)idx);
                    PopulateChannelMappingList();
                    TelemetryMappingStatus.Text = $"CM2 → {selected}";
                }
                return;
            }

            var active = _plugin.TelemetrySender;
            var state = _plugin.WheelStateForDiagnostics;

            // Wheel-reported mode: dropdown is configJsonList-ordered.
            // Index IS the slot directly.
            if (active != null && state != null && state.ConfigJsonList.Count > 0
                && idx >= 0 && idx < state.ConfigJsonList.Count)
            {
                // OnDashboardSwitched(slot) routes through SwitchToProfile so the
                // EnableHotRenegotiation feature flag is honoured and FF kind=4 is
                // emitted from a single place.
                _plugin.ActiveTelemetryProfileName = selected;
                _plugin.ActiveTelemetryMzdashPath = "";
                _plugin.SaveSettings();

                _plugin.OnDashboardSwitched((uint)idx);

                    PopulateChannelMappingList();
                return;
            }

            // Fallback: builtin-profile mode (no wheel state).
            if (selected == "(none)")
            {
                _plugin.ActiveTelemetryProfileName = "";
                _plugin.ActiveTelemetryMzdashPath = "";
                _plugin.SaveSettings();
                _plugin.OnDashboardSwitched();
                    PopulateChannelMappingList();
                return;
            }
            if (!selected.StartsWith("[Custom:"))
            {
                _plugin.ActiveTelemetryProfileName = selected;
                _plugin.ActiveTelemetryMzdashPath = "";
                _plugin.SaveSettings();
                _plugin.OnDashboardSwitched();
                    PopulateChannelMappingList();
            }
        }

        private void TelemetryClearMzdash_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.ActiveTelemetryProfileName = "";
            _plugin.ActiveTelemetryMzdashPath = "";
            _plugin.SaveSettings();
            _plugin.OnDashboardSwitched();

            using (_suppressor.Begin())
            {
                // Drop any stale [Custom: ...] entry so the dropdown doesn't keep
                // showing a previously-loaded mzdash filename.
                for (int i = TelemetryProfileCombo.Items.Count - 1; i >= 0; i--)
                    if (TelemetryProfileCombo.Items[i]?.ToString()?.StartsWith("[Custom:") == true)
                        TelemetryProfileCombo.Items.RemoveAt(i);
                // Select "(none)".
                for (int i = 0; i < TelemetryProfileCombo.Items.Count; i++)
                {
                    if (TelemetryProfileCombo.Items[i]?.ToString() == "(none)")
                    {
                        TelemetryProfileCombo.SelectedIndex = i;
                        break;
                    }
                }
            }

            PopulateChannelMappingList();
        }

        // Single toggle: Send Test Pattern ⇄ Stop Test. Button content + enabled
        // state are kept in sync each tick by RefreshTelemetryStatus.
        private void TelemetryTestToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;

            bool turningOn = !_plugin.DashboardTestPatternActive;
            // Drives every display pipeline: tier-def senders via TestMode, the
            // standalone FSR1/CM1 drivers via the shared flag (they pick it up on
            // their next tick — no start needed while they're already running).
            _plugin.SetDashboardTestPattern(turningOn);

            // A tier-def DISPLAY wheel (W17/W18) renders only while its sender is
            // Active; bring it up on demand. Never start it for an FSR1 (its screen
            // is the 0x42 driver — starting the idle wheel sender kicks a phantom
            // cold-start) or for a wheel whose screen isn't the tier-def sender.
            var active = _plugin.TelemetrySender;
            if (active != null && _plugin.WheelUsesTierDefDisplaySender && !_plugin.ActiveTelemetryEnabled)
            {
                if (turningOn && !active.IsActive)
                {
                    _plugin.ApplyTelemetrySettings();
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => active.Start());
                }
                else if (!turningOn && active.IsActive)
                {
                    active.Stop();
                }
            }

            RefreshTelemetryStatus();
        }

        // FSR V1 byte-ruler diagnostic: fills every 0x42 data byte with its own payload
        // offset so each box on the wheel shows which byte(s) feed it (offset/width/scale
        // in one screenshot). See Fsr1DisplayEmitter.BuildByteRulerRecord.
        private void Fsr1ByteRulerToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            bool on = !_plugin.Fsr1ByteRulerActive;
            _plugin.SetFsr1ByteRuler(on);
            RefreshTelemetryStatus();
        }

        // ===== Channel mappings =====

        // 2 Hz refresh of the "Current value" column. Started from InitTelemetryUI
        // (CHANNEL MAPPINGS card is always visible — no expander gate).
        private DispatcherTimer? _mappingValueTimer;
        private static readonly TimeSpan MappingValueInterval = TimeSpan.FromMilliseconds(500);

        private void StartMappingValueTimer()
        {
            if (_mappingValueTimer != null) return;
            _mappingValueTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = MappingValueInterval,
            };
            _mappingValueTimer.Tick += MappingValueTimer_Tick;
            _mappingValueTimer.Start();
            // Push values immediately so the user doesn't see "—" for the first 500ms.
            MappingValueTimer_Tick(this, EventArgs.Empty);
        }

        private void StopMappingValueTimer()
        {
            if (_mappingValueTimer == null) return;
            _mappingValueTimer.Stop();
            _mappingValueTimer.Tick -= MappingValueTimer_Tick;
            _mappingValueTimer = null;
        }

        private void MappingValueTimer_Tick(object? sender, EventArgs e)
        {
            if (_plugin == null) return;
            foreach (var row in _channelRows)
            {
                if (string.IsNullOrEmpty(row.SimHubProperty))
                {
                    row.CurrentValueText = "";
                    continue;
                }
                var raw = _plugin.GetPropertyValueForDisplay(row.SimHubProperty);
                row.CurrentValueText = FormatPropertyValue(raw);
            }
        }

        private void TelemetryResetMappings_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;

            // CM1 base-bridged dash: clear all flat field mappings.
            if (IsCm1)
            {
                _plugin.ClearCm1Mappings();
                PopulateChannelMappingList();
                TelemetryMappingStatus.Text = $"Reset to defaults at {DateTime.Now:HH:mm:ss}";
                return;
            }

            // FSR V1: clear each field override (reverts to catalog defaults).
            if (!IsCm2Target && _plugin.IsFsr1DisplayWheel)
            {
                foreach (var row in _channelRows)
                    _plugin.SetFsr1FieldMapping(row.RecordKey, row.FieldId, "", 0, 0);
                PopulateChannelMappingList();
                TelemetryMappingStatus.Text = $"Reset to defaults at {DateTime.Now:HH:mm:ss}";
                return;
            }

            // Restore each channel to its catalog default before clearing the
            // override dict — otherwise the live profile keeps the user's last typed
            // value until the next telemetry restart. CM2 targets its own sender/keys.
            var resetSender = IsCm2Target ? _plugin.ActiveCm2Sender : null;
            foreach (var row in _channelRows)
                _plugin.UpdateActiveChannelMapping(row.Url, "", resetSender);
            if (IsCm2Target)
                _plugin.ClearCurrentDashboardMappings(MozaPlugin.Cm2PageGuid, MozaPlugin.Cm2DashKey);
            else
                _plugin.ClearCurrentDashboardMappings();
            PopulateChannelMappingList();
            TelemetryMappingStatus.Text = $"Reset to defaults at {DateTime.Now:HH:mm:ss}";
        }

        // ── Inline editor handlers ─────────────────────────────────────
        // Each row's pencil button passes the bound ChannelMappingRow via Tag;
        // OK / Cancel buttons inside the editor do the same. The row model
        // owns the editor state (IsEditing / EditFilter / PendingProperty),
        // so the click handlers stay one-liners.

        private void EditMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;
            // Close any other row that's editing — only one inline editor
            // expanded at a time to keep the list scannable.
            foreach (var r in _channelRows)
                if (!ReferenceEquals(r, row) && r.IsEditing) r.CancelEdit();
            row.BeginEdit();
            // Focus the filter TextBox once the row's editor container is
            // visible. The Loaded event on the container would be reliable,
            // but the cheaper path is a Dispatcher.BeginInvoke at Render
            // priority so the visual tree has rebuilt by the time we run.
            Dispatcher.BeginInvoke(new Action(() => FocusInlineFilter(row)), DispatcherPriority.Render);
        }

        private void CommitMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;
            row.CommitEdit();
        }

        private void CancelMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;
            row.CancelEdit();
        }

        private void FocusInlineFilter(ChannelMappingRow row)
        {
            // Walk the ItemsControl's container for this row to find the
            // EditFilterBox TextBox and steal focus. Item containers are
            // ContentPresenter (default ItemsControl); the named TextBox lives
            // inside that container's template-instance.
            if (TelemetryChannelList == null) return;
            var container = TelemetryChannelList.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement;
            if (container == null) return;
            var tb = FindDescendant<TextBox>(container, "EditFilterBox");
            tb?.Focus();
            tb?.SelectAll();
        }

        private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T fe && fe.Name == name) return fe;
                var nested = FindDescendant<T>(child, name);
                if (nested != null) return nested;
            }
            return null;
        }

        // Subscribed once per row by PopulateChannelMappingList. Auto-saves the
        // mapping (debounced 500ms inside SaveSettings) AND live-rewires the
        // active profile's channel — no telemetry restart needed because the
        // wire format (tier-def, channel indices, compression) is unchanged.
        private void OnMappingRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_plugin == null) return;
            if (sender is not ChannelMappingRow row) return;

            // FSR V1 dashboard fields persist to the dedicated per-field store
            // (property + input scale). React to the property OR either scale bound.
            if (row.IsFsr1)
            {
                if (e.PropertyName != nameof(ChannelMappingRow.SimHubProperty)
                    && e.PropertyName != nameof(ChannelMappingRow.InMin)
                    && e.PropertyName != nameof(ChannelMappingRow.InMax)) return;
                if (string.IsNullOrEmpty(row.RecordKey) || string.IsNullOrEmpty(row.FieldId)) return;
                _plugin.SetFsr1FieldMapping(row.RecordKey, row.FieldId, row.SimHubProperty, row.InMin, row.InMax);
                TelemetryMappingStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
                return;
            }

            // CM1 base-bridged dash: flat field store keyed by field id.
            if (row.IsCm1)
            {
                if (e.PropertyName != nameof(ChannelMappingRow.SimHubProperty)) return;
                if (string.IsNullOrEmpty(row.FieldId)) return;
                _plugin.SetCm1FieldMapping(row.FieldId, row.SimHubProperty);
                TelemetryMappingStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
                return;
            }

            if (e.PropertyName != nameof(ChannelMappingRow.SimHubProperty)) return;
            if (string.IsNullOrEmpty(row.Url)) return;
            if (IsCm2Target)
                _plugin.SetChannelMapping(row.Url, row.SimHubProperty,
                    MozaPlugin.Cm2PageGuid, MozaPlugin.Cm2DashKey, _plugin.ActiveCm2Sender);
            else
                _plugin.SetChannelMapping(row.Url, row.SimHubProperty);
            TelemetryMappingStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
        }

        private void PopulateChannelMappingList()
        {
            // Snapshot the data signature so the RefreshTelemetryStatus growth
            // detector doesn't re-trigger every 500ms for already-current data.
            _lastMappingDataSignature = ComputeMappingDataSignature();

            // Unsubscribe from prior rows so stale rows can be GC'd and we
            // don't double-fire OnMappingRowPropertyChanged when re-populating
            // (dashboard switch, Reset, catalog growth).
            foreach (var r in _channelRows) r.PropertyChanged -= OnMappingRowPropertyChanged;
            _channelRows.Clear();

            if (_plugin == null)
            {
                SetMappingListLoading(true);
                return;
            }

            var result = IsCm1
                ? ChannelMappingRowFactory.BuildForCm1(_plugin)
                : IsCm2Target
                    ? ChannelMappingRowFactory.BuildForCm2(_plugin, _plugin.ActiveCm2Sender)
                    : ChannelMappingRowFactory.Build(_plugin);
            if (result.Rows == null)
            {
                // No profile + no catalog yet — show loading state, leave the
                // header hidden so we don't render a bare table.
                SetMappingListLoading(true);
                if (!string.IsNullOrEmpty(result.StatusText))
                    TelemetryMappingStatus.Text = result.StatusText;
                return;
            }

            SetMappingListLoading(false);
            foreach (var r in result.Rows)
            {
                r.PropertyChanged += OnMappingRowPropertyChanged;
                _channelRows.Add(r);
            }
            if (!string.IsNullOrEmpty(result.StatusText))
                TelemetryMappingStatus.Text = result.StatusText;
        }

        private void SetMappingListLoading(bool loading)
        {
            if (TelemetryChannelListLoading != null)
                TelemetryChannelListLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            if (TelemetryChannelListHeader != null)
                TelemetryChannelListHeader.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
