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
using MozaPlugin.Resources;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.UI;
using SimHub.Plugins.OutputPlugins.Dash.GLCDTemplating;
using SimHub.Plugins.OutputPlugins.EditorControls;
using SimHub.Plugins.OutputPlugins.GraphicalDash.Models;
using static MozaPlugin.UI.UiHelpers;
using static MozaPlugin.Devices.Ui.WheelUiHelpers;
using MozaPlugin.Settings;

namespace MozaPlugin.Devices.Ui
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
                // Resolve only — the plugin-event subscriptions belong to Loaded/
                // Unloaded, or a page SimHub constructs but never shows stays rooted
                // by the plugin's event lists for the plugin's lifetime.
                ResolvePlugin(subscribe: false);
            }

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += OnRefreshTick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnRefreshTick(object? sender, EventArgs e)
        {
            try { RefreshDashboardManagement(); }
            catch (Exception ex) { MozaLog.DebugIfChanged("ui-tick-dashmgmt", $"[AZOM] Dashboard tab tick failed: {ex}"); }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ResolvePlugin();
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopMappingValueTimer();
            try { _plugin?.Fsr1Probe.SetVizActive(false); } catch { }
            _refreshTimer.Stop();
            // InitTelemetryUI is one-shot (guarded by _telemetryUIInitialized) but the two
            // lifecycle bits above (value timer + viz publishing) are torn down on every
            // unload. Clear the guard so the next load re-runs InitTelemetryUI and re-arms
            // them — otherwise after one tab-switch the byte preview + value column stay dead.
            _telemetryUIInitialized = false;

            if (_dashEventSubscribedPlugin != null)
            {
                try { _dashEventSubscribedPlugin.DashboardSelectionChanged -= OnPluginDashboardSelectionChanged; }
                catch { }
                try { _dashEventSubscribedPlugin.Fsr1ActiveIndexChanged -= OnFsr1ActiveIndexChanged; }
                catch { }
                try { _dashEventSubscribedPlugin.Cm1ActiveIndexChanged -= OnCm1ActiveIndexChanged; }
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

        private bool ResolvePlugin(bool subscribe = true)
        {
            _plugin = MozaPlugin.Instance;
            if (_plugin == null) return false;
            _device = _plugin.DeviceManager;
            _data = _plugin.Data;
            _settings = _plugin.Settings;

            // Re-subscribe to dashboard events when plugin instance changes (Refresh tick self-heals on reload).
            if (subscribe && !ReferenceEquals(_dashEventSubscribedPlugin, _plugin))
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
            UpdateMasterMapperVisibility();
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
            // Re-populate the dashboard dropdown when the wheel's slot-ordered
            // ConfigJsonList changes content — an upload/enable/delete changes
            // the list (and its slot order), and without this the combo keeps
            // offering the stale pre-upload list until a selection event
            // happens to fire.
            var wheelState = _plugin?.WheelStateForDiagnostics;
            string listSig = wheelState == null
                ? ""
                : string.Join("\n", wheelState.ConfigJsonList);
            if (listSig != _lastDashComboListSignature)
            {
                _lastDashComboListSignature = listSig;
                PopulateDashboardCombo();
            }
            RefreshTelemetryStatus();
            RefreshDisplaySection();
        }

        private string _lastDashComboListSignature = "\0";

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

                // VGS-only: the display-rotation row is shown only for wheels whose
                // model carries the rotation IMU. Non-VGS display wheels never see it.
                bool supportsRotation = _plugin.WheelModelInfo?.SupportsDisplayRotation == true;
                WheelDisplayRotationPanel.Visibility =
                    supportsRotation ? Visibility.Visible : Visibility.Collapsed;
                if (supportsRotation)
                {
                    int r = _data.DashDisplayRotation;
                    if (r < 0)
                    {
                        var profile = _plugin.Settings?.ProfileStore?.CurrentProfile;
                        r = profile?.DashDisplayRotation ?? -1;
                        if (r < 0) r = _plugin.Settings?.DashDisplayRotation ?? 0;
                    }
                    SelectWheelDisplayRotationByMode(r);
                }
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
                TelemetryEnabledCheck.IsChecked = IsCm2Target
                    ? _plugin.ActiveDashTelemetryEnabled
                    : _plugin.ActiveTelemetryEnabled;

                PopulateDashboardCombo();
                // CHANNEL MAPPINGS is always-on (no expander gate). Bind the
                // ItemsControl to its ObservableCollection once, then seed the
                // list + start the 2 Hz value poller.
                TelemetryChannelList.ItemsSource = _channelRows;
                Fsr1VizList.ItemsSource = _fsr1VizRows;
                PopulateChannelMappingList();
                StartMappingValueTimer();
                // Ask the FSR1 driver to publish live numeric snapshots; the 2 Hz value
                // timer reads them into the byte strip. No-op for non-FSR1 wheels.
                _plugin.Fsr1Probe.SetVizActive(true);
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
                    int min = global::MozaPlugin.Telemetry.Display.Cm1DisplayEmitter.MinDashboardIndex;
                    int max = global::MozaPlugin.Telemetry.Display.Cm1DisplayEmitter.MaxDashboardIndex;
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
                    int max = global::MozaPlugin.Telemetry.Display.Fsr1DisplayEmitter.MaxDashboardIndex;
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

                // Selection preference: the saved selection name first (updated
                // by both UI picks and wheel-initiated switches via
                // DashboardBindingCoordinator), the device-reported slot as
                // fallback. Reported-slot-first made the combo snap BACK to the
                // pre-switch dash on every pick: this populate runs from the
                // DashboardSelectionChanged event immediately after a switch,
                // before the wheel's type-04 slot echo lands, so the stale
                // reported slot kept overriding the user's fresh choice.
                string? savedName = IsCm2Target ? _plugin.ActiveCm2DashboardName : _plugin.ActiveTelemetryProfileName;
                bool savedInList = false;
                if (!string.IsNullOrEmpty(savedName))
                {
                    foreach (var item in TelemetryProfileCombo.Items)
                    {
                        if (string.Equals(item?.ToString(), savedName, StringComparison.OrdinalIgnoreCase))
                        {
                            savedInList = true;
                            break;
                        }
                    }
                }
                string? selectedName = savedInList ? savedName : null;
                if (string.IsNullOrEmpty(selectedName)
                    && sender != null && state != null && state.ConfigJsonList != null
                    && sender.WheelReportedSlot >= 0
                    && sender.WheelReportedSlot < state.ConfigJsonList.Count)
                {
                    string reportedName = state.ConfigJsonList[sender.WheelReportedSlot];
                    if (!string.IsNullOrEmpty(reportedName))
                        selectedName = reportedName;
                }
                if (string.IsNullOrEmpty(selectedName))
                    selectedName = savedName;
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

        /// <summary>Content signature of the ConfigJsonList the dropdown was
        /// last populated from. The list changes mid-session on upload/delete
        /// (host-predicted ordinal insert / confirmed-delete compaction) with
        /// no slot change, so without this the new dash never appears until a
        /// reconnect. 0 = never populated.</summary>
        private int _lastPopulatedListSignature;

        private static int ConfigJsonListSignature(WheelDashboardState? state)
        {
            var list = state?.ConfigJsonList;
            if (list == null || list.Count == 0) return 0;
            int h = 17 + list.Count * 31;
            foreach (var n in list)
                h = unchecked(h * 31 + StringComparer.Ordinal.GetHashCode(n ?? ""));
            return h;
        }

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

        // Observable backing for the FSR1 live byte-strip viz (Fsr1VizList). Bound once
        // in InitTelemetryUI; refreshed in place each value-timer tick from the driver's
        // published snapshot, rebuilt only when the field layout changes (split/merge/edit).
        private readonly ObservableCollection<Fsr1VizRowVm> _fsr1VizRows
            = new ObservableCollection<Fsr1VizRowVm>();

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
            int listSig = ConfigJsonListSignature(state);
            bool needPopulate =
                (!_dashComboFromWheelState && state != null && state.ConfigJsonList.Count > 0)
                || (curWheelSlot != _lastPopulatedWheelSlot)
                || (listSig != 0 && listSig != _lastPopulatedListSignature);
            if (needPopulate)
            {
                if (state != null && state.ConfigJsonList.Count > 0)
                    _dashComboFromWheelState = true;
                _lastPopulatedListSignature = listSig;
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

            // Dash page owns its own enable flag and its own sender: a dash-only
            // rig resolves no wheel page GUID, so ActiveTelemetryEnabled is pinned
            // false there and would label a live CM2 "Disabled" (bundle 8MNSN33G).
            bool enabled = IsCm2Target ? _plugin.ActiveDashTelemetryEnabled : _plugin.ActiveTelemetryEnabled;
            var active = ActiveSender;
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
            bool fsr1 = !IsCm2Target && (_plugin?.IsFsr1DisplayWheel ?? false);
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

            // Single-byte probe diagnostic: FSR1-only, live whenever its driver runs.
            bool probeOn = _plugin?.Fsr1Probe.Active ?? false;
            var probeVis = fsr1 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            Fsr1ProbeBtn.Visibility = probeVis;
            Fsr1ProbeBtn.IsEnabled = fsr1Running;
            Fsr1ProbeBtn.Content = probeOn ? "Stop probe" : "Probe bytes";
            var stepVis = (fsr1 && probeOn) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            Fsr1ProbePrevBtn.Visibility = stepVis;
            Fsr1ProbeNextBtn.Visibility = stepVis;
            Fsr1ProbeLabel.Visibility = stepVis;
            if (probeOn)
                Fsr1ProbeLabel.Text = _plugin?.Fsr1Probe.TargetLabel() ?? "—";

            // Refresh profile info — auto-renegotiate may have swapped
            // the profile on a background thread after a dashboard switch.
        }

        private void TelemetryEnabledCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            bool on = TelemetryEnabledCheck.IsChecked == true;
            // The dash page owns its own flag — the wheel-page flag is unwritable
            // when no wheel is attached (hub-only / dash-only rig).
            if (IsCm2Target) _plugin.SetDashTelemetryEnabled(on);
            else _plugin.SetTelemetryEnabled(on);
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
            // FSR1 wheel display: brightness is a group-0x32 EEPROM param write, not
            // a session property — route to the dedicated pair (debounced = committed).
            if (!IsCm2Target && _plugin.IsFsr1DisplayWheel)
                _plugin.SendFsr1DisplayBrightness(val);
            else
                // allowZero: true — slider-to-zero is deliberate user intent; other call sites suppress 0.
                // ActiveSender (not the hardcoded main sender): on the CM2 device page this
                // routes to the _cm2Sender that drives the CM2 screen (decoupled).
                ActiveSender?.SendDashDisplayBrightness(val, allowZero: true);
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
            // ActiveSender: on the CM2 device page this targets the _cm2Sender (decoupled).
            ActiveSender?.SendDashDisplayStandbyMinutes(minutes);
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

        // VGS display-rotation mode (0=off, 1=smooth, 2=immediate). Wheel-only
        // feature: always routes to the main TelemetrySender (never the CM2 sender)
        // and the row is only visible for VGS wheels on the wheel page.
        private void WheelDisplayRotationCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var item = WheelDisplayRotationCombo.SelectedItem as ComboBoxItem;
            if (item?.Tag is not string tagStr) return;
            if (!int.TryParse(tagStr, out int mode)) return;
            _data!.DashDisplayRotation = mode;
            _plugin.UpdateActiveProfile(p => p.DashDisplayRotation = mode);
            _plugin.TelemetrySender?.SendDashDisplayRotation(mode);
            _plugin.SaveSettings();
        }

        private void SelectWheelDisplayRotationByMode(int mode)
        {
            for (int i = 0; i < WheelDisplayRotationCombo.Items.Count; i++)
            {
                if (WheelDisplayRotationCombo.Items[i] is ComboBoxItem cbi
                    && cbi.Tag is string tag
                    && int.TryParse(tag, out int m)
                    && m == mode)
                {
                    WheelDisplayRotationCombo.SelectedIndex = i;
                    return;
                }
            }
            WheelDisplayRotationCombo.SelectedIndex = -1;
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
                int min = global::MozaPlugin.Telemetry.Display.Cm1DisplayEmitter.MinDashboardIndex;
                int page = idx + min;
                if (idx >= 0 && page <= global::MozaPlugin.Telemetry.Display.Cm1DisplayEmitter.MaxDashboardIndex)
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
                if (idx >= 0 && idx <= global::MozaPlugin.Telemetry.Display.Fsr1DisplayEmitter.MaxDashboardIndex)
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
                    // An exception escaping a pool work item terminates the process.
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { active.Start(); }
                        catch (Exception ex) { MozaLog.Warn($"[AZOM] Test-pattern sender start failed: {ex.Message}"); }
                    });
                }
                else if (!turningOn && active.IsActive)
                {
                    active.Stop();
                }
            }

            RefreshTelemetryStatus();
        }

        // FSR V1 single-byte probe diagnostic: streams an all-zero 0x42 record with one
        // data byte ramping 0..255, so exactly one box on the wheel animates. Step the
        // offset with ◀/▶ to map each box → payload byte(s). See Fsr1DisplayEmitter.BuildProbeRecord.
        private void Fsr1ProbeToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.Fsr1Probe.SetProbe(!_plugin.Fsr1Probe.Active);
            RefreshTelemetryStatus();
        }

        private void Fsr1ProbePrev_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.Fsr1Probe.Step(-1);
            RefreshTelemetryStatus();
        }

        private void Fsr1ProbeNext_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.Fsr1Probe.Step(+1);
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
            UpdateFsr1Viz();
        }

        // Refresh the FSR1 live byte-strip from the driver's latest published snapshot.
        // Rebuild the VM list only when the field layout changes (split/merge/edit shifts a
        // StructKey); otherwise update each box's hex + value in place to avoid flicker. The
        // whole strip is hidden when no snapshot is available (non-FSR1 wheel / driver idle).
        private void UpdateFsr1Viz()
        {
            if (_plugin == null || Fsr1VizPanel == null) return;
            var records = _plugin.Fsr1Probe.GetVizSnapshot()?.Records;
            if (records == null || records.Length == 0)
            {
                if (_fsr1VizRows.Count > 0) _fsr1VizRows.Clear();
                if (Fsr1VizPanel.Visibility != Visibility.Collapsed)
                    Fsr1VizPanel.Visibility = Visibility.Collapsed;
                return;
            }

            bool sameLayout = _fsr1VizRows.Count == records.Length;
            if (sameLayout)
            {
                for (int i = 0; i < records.Length; i++)
                {
                    if (_fsr1VizRows[i].StructKey != Fsr1VizRowVm.BuildKey(records[i]))
                    {
                        sameLayout = false;
                        break;
                    }
                }
            }

            if (sameLayout)
            {
                for (int i = 0; i < records.Length; i++)
                    _fsr1VizRows[i].Update(records[i]);
            }
            else
            {
                _fsr1VizRows.Clear();
                foreach (var rec in records)
                    _fsr1VizRows.Add(new Fsr1VizRowVm(rec));
            }

            if (Fsr1VizPanel.Visibility != Visibility.Visible)
                Fsr1VizPanel.Visibility = Visibility.Visible;
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

            // FSR V1: per active record, drop every field override (channel/scale/bias)
            // so the record reverts to the catalog defaults.
            if (!IsCm2Target && _plugin.IsFsr1DisplayWheel)
            {
                var clearedRecords = new HashSet<string>();
                foreach (var row in _channelRows)
                    if (row.IsFsr1 && !string.IsNullOrEmpty(row.RecordKey) && clearedRecords.Add(row.RecordKey))
                        _plugin.ClearFsr1FieldOverrides(row.RecordKey);
                PopulateChannelMappingList();
                TelemetryMappingStatus.Text = $"Reset to defaults at {DateTime.Now:HH:mm:ss}";
                return;
            }

            // Restore each channel to its catalog default before clearing the
            // override dict — otherwise the live profile keeps the user's last typed
            // value until the next telemetry restart. CM2 targets its own sender/keys.
            var resetSender = IsCm2Target ? _plugin.ActiveCm2Sender : null;
            foreach (var row in _channelRows)
                _plugin.ChannelMapping.UpdateActive(row.Url, "", resetSender);
            if (IsCm2Target)
                _plugin.ChannelMapping.ClearCurrentDashboard(MozaPlugin.Cm2PageGuid, MozaPlugin.Cm2DashKey);
            else
                _plugin.ChannelMapping.ClearCurrentDashboard();
            PopulateChannelMappingList();
            TelemetryMappingStatus.Text = $"Reset to defaults at {DateTime.Now:HH:mm:ss}";
        }

        // ── Master channel mapper (Telemetry.json defaults) ─────────────
        // The card-header button opens the per-game default editor (the active SimHub
        // profile's set). Its edits sit BELOW the per-dashboard overrides this page
        // writes, so a channel customised here keeps its per-dashboard value;
        // everything else follows the new default.

        /// <summary>Hide the master-mapper button on the FSR1 and CM1 surfaces — their
        /// field defaults come from Fsr1DashboardCatalog / Cm1DashboardCatalog, not
        /// Telemetry.json, so the dialog would have no effect there. Re-evaluated every
        /// refresh tick: the CM1 discriminator and the wheel model both resolve after
        /// the one-shot InitTelemetryUI has run.</summary>
        private void UpdateMasterMapperVisibility()
        {
            if (MasterMapperButton == null || _plugin == null) return;
            bool telemetryJsonSurface = !IsCm1 && !(!IsCm2Target && _plugin.IsFsr1DisplayWheel);
            var want = telemetryJsonSurface ? Visibility.Visible : Visibility.Collapsed;
            if (MasterMapperButton.Visibility != want) MasterMapperButton.Visibility = want;
        }

        private void MasterChannelMapper_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            try
            {
                // Pass this control as the backdrop source: the dialog walks up from it
                // to find the opaque colour SimHub paints behind the plugin page, so it
                // matches the host instead of a hardcoded shade.
                var dlg = new MasterChannelMapperDialog(_plugin, this) { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                MozaLog.Warn("[AZOM] master channel mapper failed to open: " + ex.Message);
                TelemetryMappingStatus.Text = Strings.Status_MasterMapperUnavailable;
                return;
            }
            // Repaint against the new effective defaults (a channel with no per-dashboard
            // override now resolves through the global layer).
            PopulateChannelMappingList();
        }

        // ── Inline editor handlers ─────────────────────────────────────
        // Dual-mode: the pencil opens the simple inline editor (searchable property
        // list + FSR1/CM1 steppers); the ƒₓ button opens SimHub's formula dialog for
        // advanced [property] NCalc / js: editing. Each row's button passes the bound
        // ChannelMappingRow via Tag; the row model owns the simple-editor state.

        private void EditMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;
            // Only one inline editor expanded at a time to keep the list scannable.
            foreach (var r in _channelRows)
                if (!ReferenceEquals(r, row) && r.IsEditing) r.CancelEdit();
            row.BeginEdit();
            // FSR1: arm the field-probe on this field's current span so the wheel
            // lights exactly the box(es) it feeds — stepping the edges moves the lit
            // box live. Cleared on Commit/Cancel below.
            if (row.IsFsr1 && !string.IsNullOrEmpty(row.RecordKey) && !string.IsNullOrEmpty(row.FieldId))
                _plugin?.Fsr1Probe.SetFieldProbe(row.RecordKey, row.FieldId);
            // Focus the filter once the row's editor container is realized.
            Dispatcher.BeginInvoke(new Action(() => FocusInlineFilter(row)), DispatcherPriority.Render);
        }

        private void CommitMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;
            row.CommitEdit();
            if (row.IsFsr1) _plugin?.Fsr1Probe.ClearFieldProbe();
        }

        private void CancelMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;
            row.CancelEdit();
            if (row.IsFsr1) _plugin?.Fsr1Probe.ClearFieldProbe();
        }

        // Advanced edit: open SimHub's own formula editor (BindingEditor) against the
        // shared engine and a working copy of the row's formula. On OK, write the
        // result back through the row (which serializes it into SimHubProperty and
        // persists). Mirrors SimHub's FormulaPickerButton click logic, but with our
        // own compact ƒₓ button instead of the heavyweight templated control.
        private async void AdvancedEditMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;
            var engine = row.Engine;
            if (engine == null)
            {
                TelemetryMappingStatus.Text = "Formula editor unavailable (SimHub engine not loaded)";
                return;
            }

            // Work on a throwaway ExpressionValue so the dialog never mutates the row's
            // live Expression mid-edit; copy back only on OK.
            var src = row.Expression;
            var working = new ExpressionValue();
            working.UseJavascript = src.UseJavascript;
            working.Expression = src.Expression;
            working.PreExpression = src.PreExpression;

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
                TelemetryMappingStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                MozaLog.Warn("[AZOM] formula editor failed: " + ex.Message);
                TelemetryMappingStatus.Text = "Formula editor failed (see log)";
            }
        }

        // Revert one field to its catalog default: clear the stored override and
        // re-seed the row's editor state from the catalog so the steppers reset.
        private void FieldReset_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            if ((sender as FrameworkElement)?.Tag is not ChannelMappingRow row) return;

            if (row.IsCm1)
            {
                if (string.IsNullOrEmpty(row.FieldId)) return;
                _plugin.SetCm1FieldMapping(row.FieldId, "", null);
            }
            else if (row.IsFsr1)
            {
                if (string.IsNullOrEmpty(row.RecordKey) || string.IsNullOrEmpty(row.FieldId)) return;
                _plugin.SetFsr1FieldMapping(row.RecordKey, row.FieldId, null);
            }
            else return;

            // Rebuild the list so the reset row re-seeds from catalog defaults; the
            // field-probe (if armed) is released by the repopulate's editor teardown.
            _plugin.Fsr1Probe.ClearFieldProbe();
            PopulateChannelMappingList();
            TelemetryMappingStatus.Text = $"Reset field at {DateTime.Now:HH:mm:ss}";
        }


        private void FocusInlineFilter(ChannelMappingRow row)
        {
            // Walk the ItemsControl's container for this row to find the
            // EditFilterBox TextBox and steal focus.
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
            // (property + input scale + gain overrides). Build a deviation-only
            // mapping (defaults stay unpersisted).
            if (row.IsFsr1)
            {
                if (e.PropertyName != nameof(ChannelMappingRow.SimHubProperty)
                    && e.PropertyName != nameof(ChannelMappingRow.InMin)
                    && e.PropertyName != nameof(ChannelMappingRow.InMax)
                    && e.PropertyName != nameof(ChannelMappingRow.Scale)
                    && e.PropertyName != nameof(ChannelMappingRow.Bias)) return;
                if (string.IsNullOrEmpty(row.RecordKey) || string.IsNullOrEmpty(row.FieldId)) return;
                _plugin.SetFsr1FieldMapping(row.RecordKey, row.FieldId, BuildFsr1MappingFromRow(row));
                TelemetryMappingStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
                return;
            }

            // CM1 base-bridged dash: flat field store keyed by field id (property + gain).
            if (row.IsCm1)
            {
                if (e.PropertyName != nameof(ChannelMappingRow.SimHubProperty)
                    && e.PropertyName != nameof(ChannelMappingRow.Scale)) return;
                if (string.IsNullOrEmpty(row.FieldId)) return;
                _plugin.SetCm1FieldMapping(row.FieldId, row.SimHubProperty, Cm1ScaleOverride(row));
                TelemetryMappingStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
                return;
            }

            if (e.PropertyName != nameof(ChannelMappingRow.SimHubProperty)) return;
            if (string.IsNullOrEmpty(row.Url)) return;
            if (IsCm2Target)
                _plugin.ChannelMapping.Set(row.Url, row.SimHubProperty,
                    MozaPlugin.Cm2PageGuid, MozaPlugin.Cm2DashKey, _plugin.ActiveCm2Sender);
            else
                _plugin.ChannelMapping.Set(row.Url, row.SimHubProperty);
            TelemetryMappingStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
        }

        // Build a deviation-only FSR1 field mapping from a UI row: the gain overrides
        // are set ONLY when they differ from the field's CATALOG gain, so an unedited
        // field prunes to nothing (dict-missing ≠ explicit-off) and a row left at the
        // catalog value can't persist a unity gain over a ×1000 / +300 default.
        // Geometry is catalog-fixed.
        private static Fsr1FieldMapping BuildFsr1MappingFromRow(ChannelMappingRow row)
        {
            return new Fsr1FieldMapping
            {
                Property = (row.SimHubProperty ?? "").Trim(),
                InMin = row.InMin,
                InMax = row.InMax,
                Scale = row.Scale != row.DefaultScale ? row.Scale : (double?)null,
                Bias = row.Bias != row.DefaultBias ? row.Bias : (double?)null,
            };
        }

        // CM1 carries only a per-field gain override; persist null (use catalog scale)
        // when the row sits at the field's catalog default so it prunes cleanly.
        private static double? Cm1ScaleOverride(ChannelMappingRow row)
        {
            var f = Telemetry.Display.Cm1DashboardCatalog.ByFieldId(row.FieldId);
            double defScale = f?.Scale ?? 1.0;
            return row.Scale != defScale ? row.Scale : (double?)null;
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
