using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MozaPlugin.Sdk;
using MozaPlugin.Sdk.Coap;

namespace MozaPlugin
{
    // Partial-class continuation of SettingsControl that holds wiring for the
    // CoAP tab. Stream 7 is now live — the Recent Requests list pulls from
    // MozaPlugin.Instance.SdkServer.RecentRequests and the status text block
    // reflects the live server state. When the server is null (feature
    // disabled) the tab shows persistent settings intent only.
    public partial class SettingsControl
    {
        // Default CoAP port — duplicated here as a const so the validation
        // fallback in SdkCoapPortBox_LostFocus doesn't have to walk back to
        // MozaPluginSettings just to revert. Must stay in sync with the
        // settings default.
        private const int DefaultSdkCoapPort = 5683;
        private const int MinSdkCoapPort = 1024;
        private const int MaxSdkCoapPort = 65535;

        // Backing collection for the recent-requests list — replaced on every
        // refresh tick with the latest snapshot from the live server. The
        // ListBox binds to this ObservableCollection so WPF gets per-row
        // change notifications without rebuilding the visual tree.
        private readonly ObservableCollection<string> _sdkRecentRequests
            = new ObservableCollection<string>();

        // Server instance the UI is currently subscribed to. Tracked so we
        // can unsubscribe cleanly when the server cycles (e.g. Init wired a
        // new one after a settings change + restart).
        private MozaSdkCoapServer? _subscribedSdkServer;

        // True when an append-event has fired since the last UI tick, so
        // RefreshSdkTab can choose to repopulate without re-snapshotting on
        // every 500ms tick when nothing has changed. Volatile so the
        // dispatcher-thread reader sees the receive-thread writer.
        private volatile bool _sdkRecentDirty;

        /// <summary>
        /// Called from the SettingsControl constructor (after
        /// InitializeComponent) to populate the SDK tab's controls from
        /// persisted settings and wire the recent-requests collection.
        /// Suppresses change events while seeding so the toggle/port handlers
        /// don't double-save the values that just came out of the settings
        /// file.
        /// </summary>
        private void InitSdkTab()
        {
            using (_suppressor.Begin())
            {
                if (SdkEmulationEnabledCheck != null)
                    SdkEmulationEnabledCheck.IsChecked = _plugin.Settings.SdkEmulationEnabled;

                if (SdkCoapPortBox != null)
                    SdkCoapPortBox.Text = _plugin.Settings.SdkCoapPort.ToString(CultureInfo.InvariantCulture);

                if (SdkRecentRequestsList != null)
                    SdkRecentRequestsList.ItemsSource = _sdkRecentRequests;
            }

            TrySubscribeToSdkServer();
            RefreshSdkStatus();
            RefreshSdkRecentRequests(force: true);
        }

        /// <summary>
        /// Update the CoAP server status TextBlock from the live
        /// <see cref="MozaPlugin.SdkServer"/> instance. When the feature is
        /// disabled (no server constructed) the text reflects the persisted
        /// intent.
        /// </summary>
        private void RefreshSdkStatus()
        {
            var server = _plugin.SdkServer;

            // Defensive null check: this is called from InitSdkTab before
            // the control may have realized, and from the refresh tick after
            // the user has navigated tabs back and forth.
            if (SdkServerStatusText == null) return;

            if (server != null)
            {
                // Live server — show its self-reported status string.
                SdkServerStatusText.Text = server.Status;
            }
            else if (_plugin.Settings.SdkEmulationEnabled)
            {
                var bind = _plugin.Settings.SdkBindLoopbackOnly ? "127.0.0.1" : "0.0.0.0";
                SdkServerStatusText.Text =
                    $"Enabled — will listen on {bind}:{_plugin.Settings.SdkCoapPort} after plugin restart";
            }
            else
            {
                SdkServerStatusText.Text = "Disabled";
            }
        }

        /// <summary>
        /// Snapshot the server's recent-requests buffer into the bound
        /// ObservableCollection. Called from the dispatcher tick + on the
        /// initial tab populate. Cheap when the dirty flag isn't set; a full
        /// rebuild otherwise (≤20 rows so the cost is negligible).
        /// </summary>
        private void RefreshSdkRecentRequests(bool force)
        {
            if (SdkRecentRequestsList == null) return;
            // Re-subscribe in case the server was (re)created since the last
            // tick — the SettingsControl outlives any single SdkServer
            // instance when SimHub reloads the plugin.
            TrySubscribeToSdkServer();

            var server = _plugin.SdkServer;
            if (server == null)
            {
                if (_sdkRecentRequests.Count != 1
                    || !string.Equals(_sdkRecentRequests[0], "Server not started — enable in toggle above and restart plugin", StringComparison.Ordinal))
                {
                    _sdkRecentRequests.Clear();
                    _sdkRecentRequests.Add("Server not started — enable in toggle above and restart plugin");
                }
                _sdkRecentDirty = false;
                return;
            }

            if (!force && !_sdkRecentDirty) return;
            _sdkRecentDirty = false;

            // Newest-first so the freshest activity is at the top of the
            // list — matches the convention of every other diagnostics panel
            // in this UI (serial trace, sleep trace, etc.).
            var snapshot = server.RecentRequests;
            var rendered = new List<string>(snapshot.Count);
            for (int i = snapshot.Count - 1; i >= 0; i--)
            {
                rendered.Add(FormatRecentRequest(snapshot[i]));
            }

            // Diff-light update: clear + add is fine at this size; trying to
            // diff each row brings little benefit since the buffer slides as
            // a whole.
            _sdkRecentRequests.Clear();
            if (rendered.Count == 0)
            {
                _sdkRecentRequests.Add("No requests yet");
            }
            else
            {
                foreach (var line in rendered) _sdkRecentRequests.Add(line);
            }
        }

        private static string FormatRecentRequest(MozaSdkCoapServer.RecentRequest row)
        {
            // HH:mm:ss.fff  GET  /uri  ->  2.05 (1ms)
            string time = row.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string code = CoapCode.Format(row.ResponseCode);
            string uri = string.IsNullOrEmpty(row.Uri) ? "(ping)" : row.Uri;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}  {1,-5} {2}  ->  {3} ({4}ms)",
                time, row.Verb, uri, code, row.DurationMs);
        }

        /// <summary>
        /// Wire the <see cref="MozaSdkCoapServer.RecentRequestAppended"/>
        /// event to mark the UI as dirty. The dispatcher-tick handler reads
        /// the dirty flag on its next pass and re-snapshots the buffer.
        /// Idempotent — subscribing twice to the same server is a no-op.
        /// </summary>
        private void TrySubscribeToSdkServer()
        {
            var current = _plugin.SdkServer;
            if (ReferenceEquals(current, _subscribedSdkServer)) return;

            if (_subscribedSdkServer != null)
            {
                try { _subscribedSdkServer.RecentRequestAppended -= OnSdkRecentRequestAppended; }
                catch { /* receiver may already have been torn down */ }
            }
            _subscribedSdkServer = current;
            if (_subscribedSdkServer != null)
                _subscribedSdkServer.RecentRequestAppended += OnSdkRecentRequestAppended;
        }

        private void OnSdkRecentRequestAppended()
        {
            // Fires on the server's receive thread — DO NOT touch WPF here.
            // The next dispatcher tick will pick up the dirty flag and
            // snapshot the buffer on the UI thread.
            _sdkRecentDirty = true;
        }

        /// <summary>
        /// Drop the event subscription so the server doesn't keep this
        /// control alive via its event-handler list. Called from
        /// OnUnloadedStopTimers; re-subscription happens lazily on the next
        /// refresh tick when the control is reloaded.
        /// </summary>
        private void UnsubscribeFromSdkServer()
        {
            if (_subscribedSdkServer != null)
            {
                try { _subscribedSdkServer.RecentRequestAppended -= OnSdkRecentRequestAppended; }
                catch { }
                _subscribedSdkServer = null;
            }
        }

        /// <summary>
        /// Tick hook used by the SettingsControl refresh DispatcherTimer to
        /// poll the server's live state. Splits the work in two: the status
        /// text always re-renders (cheap), the recent-requests list only
        /// rebuilds when the dirty flag has been raised.
        /// </summary>
        private void RefreshSdkTabTick()
        {
            RefreshSdkStatus();
            RefreshSdkRecentRequests(force: false);
        }

        // ===== Event handlers =====

        private void SdkEmulationEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.SdkEmulationEnabled = SdkEmulationEnabledCheck.IsChecked == true;
            _plugin.SaveSettings();
            RefreshSdkStatus();
        }

        private void SdkCoapPortBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            CommitSdkCoapPort();
        }

        private void SdkCoapPortBox_KeyDown(object sender, KeyEventArgs e)
        {
            // Commit on Enter, revert on Escape. Mirrors the implicit
            // commit-on-LostFocus path so the user gets immediate feedback
            // when they hit Enter instead of having to tab away.
            if (e.Key == Key.Enter)
            {
                CommitSdkCoapPort();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                using (_suppressor.Begin())
                {
                    SdkCoapPortBox.Text = _plugin.Settings.SdkCoapPort.ToString(CultureInfo.InvariantCulture);
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// Parse the port TextBox, range-validate, and either save or revert.
        /// On invalid input the textbox snaps back to the previously-saved
        /// value (or the 5683 default if no value was persisted). No popup —
        /// the snap-back is the feedback.
        /// </summary>
        private void CommitSdkCoapPort()
        {
            int previous = _plugin.Settings.SdkCoapPort;
            if (previous < MinSdkCoapPort || previous > MaxSdkCoapPort)
                previous = DefaultSdkCoapPort;

            string raw = SdkCoapPortBox.Text?.Trim() ?? string.Empty;
            bool ok = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                      && parsed >= MinSdkCoapPort
                      && parsed <= MaxSdkCoapPort;

            int next = ok ? parsed : previous;

            if (next != _plugin.Settings.SdkCoapPort)
            {
                _plugin.Settings.SdkCoapPort = next;
                _plugin.SaveSettings();
            }

            // Always normalize the textbox content so trailing whitespace /
            // a rejected entry visibly reverts to the canonical integer.
            using (_suppressor.Begin())
            {
                SdkCoapPortBox.Text = next.ToString(CultureInfo.InvariantCulture);
            }

            RefreshSdkStatus();
        }

        private void SdkRefreshStatusButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshSdkStatus();
            RefreshSdkRecentRequests(force: true);
        }
    }
}
