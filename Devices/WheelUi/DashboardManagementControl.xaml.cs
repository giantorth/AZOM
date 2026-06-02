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
                MozaLog.Debug("[Moza] UI: DashboardSelectionChanged handler — _plugin null, skipping");
                return;
            }

            // Always re-populate: event can arrive long after the first refresh tick.
            // Wheel-initiated dashboard switches also fire this event (via
            // DashboardBindingCoordinator.RaiseDashboardSelectionChangedInternal),
            // so refreshing the channel-mapping grid here covers both UI- and
            // wheel-initiated switches.
            MozaLog.Debug(
                $"[Moza] UI: DashboardSelectionChanged handler — selected='{_plugin.ActiveTelemetryProfileName}'");
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
                }
                _plugin.DashboardSelectionChanged += OnPluginDashboardSelectionChanged;
                _plugin.Fsr1ActiveIndexChanged += OnFsr1ActiveIndexChanged;
                _dashEventSubscribedPlugin = _plugin;
                MozaLog.Debug(
                    $"[Moza] UI: subscribed to DashboardSelectionChanged (plugin hash={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_plugin)})");
            }

            return true;
        }

        private void RefreshDashboardManagement()
        {
            if (!ResolvePlugin()) return;
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
                    UpdateFolderInfo();
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

            // FSR V1: the dropdown selects which built-in dashboard (group-0x42
            // record type) the plugin streams — there is no wheel-reported
            // configJsonList. List the catalog's live dashboards.
            if (_plugin.IsFsr1DisplayWheel)
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

                var state = _plugin.WheelStateForDiagnostics;
                if (state != null && state.ConfigJsonList.Count > 0)
                {
                    // Wheel-reported dashboards in configJsonList order
                    // (alphabetical). Dropdown index = configJsonList slot
                    // used by SendDashboardSwitch.
                    foreach (var name in state.ConfigJsonList)
                        TelemetryProfileCombo.Items.Add(name);
                }
                else
                {
                    // Fallback: cached dashboard names (wheel state not available yet).
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

                // Show wheel-reported slot (ground truth); fall back to saved profile name.
                string? selectedName = null;
                var sender = _plugin.TelemetrySender;
                if (sender != null && state != null && state.ConfigJsonList != null
                    && sender.WheelReportedSlot >= 0
                    && sender.WheelReportedSlot < state.ConfigJsonList.Count)
                {
                    string wheelName = state.ConfigJsonList[sender.WheelReportedSlot];
                    if (!string.IsNullOrEmpty(wheelName))
                        selectedName = wheelName;
                }
                if (string.IsNullOrEmpty(selectedName))
                    selectedName = _plugin.ActiveTelemetryProfileName;
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
            // FSR V1 rows come from the static catalog, not a tier profile —
            // a fixed signature so the list populates once and doesn't churn.
            if (_plugin.IsFsr1DisplayWheel) return -3;
            var profile = _plugin.TelemetrySender?.Profile;
            int catalogCount = _plugin.WheelChannelCatalogForDiagnostics?.Count ?? 0;
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
            bool testMode = active?.TestMode ?? false;
            int framesSent = _plugin.FramesSentForDiagnostics;

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
            // Surface the pipeline health model first so recovery/park states are
            // truthful instead of mislabeling a parked pipeline as "Connecting…" forever.
            var phase = active?.Phase ?? PipelinePhase.Idle;
            if (!enabled)
                TelemetryStatusLabel.Text = "Disabled";
            else if (testMode)
                TelemetryStatusLabel.Text = $"Test pattern — {framesSent} frames sent";
            else if (phase == PipelinePhase.Parked)
                TelemetryStatusLabel.Text = (active?.Recovery?.ParkIsDegraded ?? false)
                    ? global::MozaPlugin.Resources.Strings.Status_DegradedScreenless
                    : global::MozaPlugin.Resources.Strings.Status_TelemetryParked;
            else if (phase == PipelinePhase.Recovery)
                TelemetryStatusLabel.Text = global::MozaPlugin.Resources.Strings.Status_Recovering;
            else if (active != null && !active.IsActive)
                TelemetryStatusLabel.Text = inCooldown
                    ? "Switching dashboard… (post-emit silence)"
                    : "Connecting to wheel…";
            else if (pendingApply)
            {
                string? why = _plugin?.PendingDashboardApplyDescription;
                TelemetryStatusLabel.Text = string.IsNullOrEmpty(why)
                    ? $"Switching dashboard… — {framesSent} frames sent"
                    : $"Switching dashboard… ({why}) — {framesSent} frames sent";
            }
            else if (inCooldown)
                TelemetryStatusLabel.Text = $"Switching dashboard… — {framesSent} frames sent";
            else
                TelemetryStatusLabel.Text = $"Sending — {framesSent} frames sent";

            TelemetryTestStopBtn.IsEnabled = testMode && senderReady;
            TelemetryTestStartBtn.IsEnabled = !testMode && senderReady;
            TelemetryProfileCombo.IsEnabled = senderReady;

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
                    "[Moza] DisplayBrightnessDebounce_Tick: skipping wire push — _data is sentinel");
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

            // FSR V1: selecting a dashboard sends the group-0x32/0x81 select command
            // (index = combo position). The wheel switches its displayed page.
            if (_plugin.IsFsr1DisplayWheel)
            {
                if (idx >= 0 && idx <= global::MozaPlugin.Telemetry.Fsr1DisplayEmitter.MaxDashboardIndex)
                {
                    _plugin.SetActiveFsr1Index(idx, sendToWheel: true);
                    TelemetryMappingStatus.Text = $"Switched to Dashboard {idx + 1}";
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

        private void TelemetryLoadMzdash_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open .mzdash dashboard file",
                Filter = "MOZA Dashboard|*.mzdash|All Files|*.*",
                DefaultExt = ".mzdash"
            };
            if (dlg.ShowDialog() != true) return;

            _plugin.ActiveTelemetryMzdashPath = dlg.FileName;
            _plugin.ActiveTelemetryProfileName = "";
            _plugin.SaveSettings();
            // Hot-reload tier def on the existing session — mirrors PitHouse's
            // mid-game dash-change burst on session 0x01.
            _plugin.OnDashboardSwitched();

            using (_suppressor.Begin())
            {
                string label = "[Custom: " + System.IO.Path.GetFileName(dlg.FileName) + "]";
                for (int i = TelemetryProfileCombo.Items.Count - 1; i >= 0; i--)
                    if (TelemetryProfileCombo.Items[i]?.ToString()?.StartsWith("[Custom:") == true)
                        TelemetryProfileCombo.Items.RemoveAt(i);
                TelemetryProfileCombo.Items.Add(label);
                TelemetryProfileCombo.SelectedIndex = TelemetryProfileCombo.Items.Count - 1;
            }

            PopulateChannelMappingList();
        }

        private void TelemetrySetFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Select folder containing .mzdash dashboard files";
                dlg.ShowNewFolderButton = false;
                if (!string.IsNullOrEmpty(_plugin.ActiveTelemetryMzdashFolder)
                    && System.IO.Directory.Exists(_plugin.ActiveTelemetryMzdashFolder))
                    dlg.SelectedPath = _plugin.ActiveTelemetryMzdashFolder;

                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                _plugin.ActiveTelemetryMzdashFolder = dlg.SelectedPath;
                _plugin.SaveSettings();
                _plugin.DashCache?.LoadFromFolder(dlg.SelectedPath);
                PopulateDashboardCombo();
                _plugin.ApplyTelemetrySettings();
                UpdateFolderInfo();
            }
        }

        private void TelemetryAutoDetect_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;

            string dashesRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MOZA Pit House", "_dashes");

            if (!System.IO.Directory.Exists(dashesRoot))
            {
                MessageBox.Show(
                    $"MOZA Pit House dashboard folder not found at:\n{dashesRoot}\n\n" +
                    "Install MOZA Pit House and load a dashboard, or use Set Folder… to point at a custom location.",
                    "Auto-detect dashboard folder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            byte[] uid = _plugin.Data?.WheelMcuUid ?? Array.Empty<byte>();
            string uidHex = uid.Length == 12 ? UidToHex(uid) : "";

            string? picked = null;
            string? failReason = null;

            if (!string.IsNullOrEmpty(uidHex))
            {
                // Match the UID-named subfolder case-insensitively. PitHouse
                // normalizes these to lowercase, but a case-sensitive FS
                // (Linux/Wine, case-sensitive NTFS) would still miss a
                // mismatched case from older installs or manual copies.
                string? match = System.IO.Directory.EnumerateDirectories(dashesRoot)
                    .FirstOrDefault(p => string.Equals(
                        new System.IO.DirectoryInfo(p).Name,
                        uidHex,
                        StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    picked = match;
                else
                    failReason = $"No dashboard folder for the connected wheel (UID {uidHex}).\n" +
                                 $"Looked under:\n{dashesRoot}\n\n" +
                                 "Open a dashboard in MOZA Pit House for this wheel first.";
            }
            else
            {
                var guidDirs = System.IO.Directory.GetDirectories(dashesRoot)
                    .Where(p => System.Text.RegularExpressions.Regex.IsMatch(
                        new System.IO.DirectoryInfo(p).Name, "^[0-9a-fA-F]{24}$"))
                    .ToList();
                if (guidDirs.Count == 1)
                    picked = guidDirs[0];
                else if (guidDirs.Count == 0)
                    failReason = "No wheel-specific dashboard folders found under _dashes. " +
                                 "Open MOZA Pit House and load a dashboard, then try again.";
                else
                    failReason = $"Multiple dashboard folders found ({guidDirs.Count}) and no wheel is connected. " +
                                 "Connect your wheel and try again, or use Set Folder… to choose manually.";
            }

            if (picked == null)
            {
                MessageBox.Show(failReason, "Auto-detect dashboard folder",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Per-wheel-page overlay carries the folder. _plugin's helper writes
            // into the current wheel's overlay; legacy WheelMzdashFolderByUid is
            // no longer maintained.
            _plugin.ActiveTelemetryMzdashFolder = picked;
            _plugin.SaveSettings();
            _plugin.DashCache?.LoadFromFolder(picked);
            PopulateDashboardCombo();
            _plugin.ApplyTelemetrySettings();
            UpdateFolderInfo();
        }

        private void UpdateFolderInfo()
        {
            if (_plugin == null) return;
            var folder = _plugin.ActiveTelemetryMzdashFolder;
            TelemetryFolderInfo.Text = string.IsNullOrEmpty(folder) ? "" : $"Folder: {folder}";
        }

        private void TelemetryTestStart_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var active = _plugin.TelemetrySender;
            if (active == null) return;
            active.TestMode = true;
            if (!_plugin.ActiveTelemetryEnabled)
            {
                _plugin.ApplyTelemetrySettings();
                System.Threading.ThreadPool.QueueUserWorkItem(_ => active.Start());
            }
            TelemetryTestStartBtn.IsEnabled = false;
            TelemetryTestStopBtn.IsEnabled = true;
        }

        private void TelemetryTestStop_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var active = _plugin.TelemetrySender;
            if (active == null) return;
            active.TestMode = false;
            if (!_plugin.ActiveTelemetryEnabled)
                active.Stop();
            TelemetryTestStartBtn.IsEnabled = true;
            TelemetryTestStopBtn.IsEnabled = false;
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

            // FSR V1: clear each field override (reverts to catalog defaults).
            if (_plugin.IsFsr1DisplayWheel)
            {
                foreach (var row in _channelRows)
                    _plugin.SetFsr1FieldMapping(row.RecordKey, row.FieldId, "", 0, 0);
                PopulateChannelMappingList();
                TelemetryMappingStatus.Text = $"Reset to defaults at {DateTime.Now:HH:mm:ss}";
                return;
            }

            // Restore each channel to its Telemetry.json default before clearing
            // the override dict — otherwise the live profile keeps the user's
            // last typed value until the next telemetry restart.
            foreach (var row in _channelRows)
                _plugin.UpdateActiveChannelMapping(row.Url, "");
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

            if (e.PropertyName != nameof(ChannelMappingRow.SimHubProperty)) return;
            if (string.IsNullOrEmpty(row.Url)) return;
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

            var result = ChannelMappingRowFactory.Build(_plugin);
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
