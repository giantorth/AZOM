using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
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
        // Backing collection for the CoAP recent-requests list — replaced on
        // every refresh tick with the latest snapshot from the live server.
        // The ListBox binds to this ObservableCollection so WPF gets per-row
        // change notifications without rebuilding the visual tree.
        private readonly ObservableCollection<string> _sdkRecentRequests
            = new ObservableCollection<string>();

        // Parallel buffer for the PitHouse UDP control server. Same pattern,
        // separate list so the two protocols are visually distinct in the
        // SDK tab.
        private readonly ObservableCollection<string> _controlUdpRecentRequests
            = new ObservableCollection<string>();

        // Server instances the UI is currently subscribed to. Tracked so we
        // can unsubscribe cleanly when a server cycles (e.g. Init wired a
        // new one after a settings change + restart).
        private MozaSdkCoapServer? _subscribedSdkServer;
        private Sdk.PitHouseUdp.MozaControlUdpServer? _subscribedControlUdpServer;

        // Dirty flags raised by the server-thread event handlers; the
        // dispatcher-tick handler polls them on the UI thread. Volatile so
        // the dispatcher-thread reader sees the receive-thread writer.
        private volatile bool _sdkRecentDirty;
        private volatile bool _controlUdpRecentDirty;

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

                if (UdpControlEnabledCheck != null)
                    UdpControlEnabledCheck.IsChecked = _plugin.Settings.UdpControlEnabled;

                if (SdkRecentRequestsList != null)
                    SdkRecentRequestsList.ItemsSource = _sdkRecentRequests;

                if (ControlUdpRecentRequestsList != null)
                    ControlUdpRecentRequestsList.ItemsSource = _controlUdpRecentRequests;
            }

            TrySubscribeToSdkServer();
            TrySubscribeToControlUdpServer();
            RefreshSdkStatus();
            RefreshSdkRecentRequests(force: true);
            RefreshControlUdpRecentRequests(force: true);
        }

        /// <summary>
        /// Update the SDK status TextBlock with one line per component —
        /// CoAP listener (port 40266), PitHouse UDP control listener
        /// (port 40288), and the stub manager process. When the feature is
        /// disabled (no instances constructed) the block reports the
        /// persisted intent in a single line. All three components share the
        /// same enable gate so they normally come up and go down together;
        /// independent failures (port already in use, stub couldn't extract,
        /// etc.) surface as per-line status text so the user can tell which
        /// piece broke.
        /// </summary>
        private void RefreshSdkStatus()
        {
            // Defensive null check: this is called from InitSdkTab before
            // the control may have realized, and from the refresh tick after
            // the user has navigated tabs back and forth.
            if (SdkServerStatusText == null) return;

            bool anyEnabled = _plugin.Settings.SdkEmulationEnabled
                              || _plugin.Settings.UdpControlEnabled;
            if (!anyEnabled
                && _plugin.SdkServer == null
                && _plugin.ControlUdpServer == null
                && _plugin.SdkStubManager == null)
            {
                SdkServerStatusText.Text = "Disabled";
                return;
            }

            // Each component has its own enable gate, so describe each
            // intent against its own flag — a user might run CoAP without
            // UDP control (or vice-versa) and the status text should
            // reflect exactly that. The stub manager tracks CoAP since
            // it only matters for the official SDK DLL name probe.
            var sb = new StringBuilder();
            sb.Append("CoAP listener (").Append(MozaSdkCoapServer.CoapPort).Append("): ")
              .AppendLine(DescribeServerStatus(_plugin.SdkServer?.Status, _plugin.Settings.SdkEmulationEnabled));
            sb.Append("UDP control listener (")
              .Append(Sdk.PitHouseUdp.MozaControlUdpServer.ControlPort).Append("): ")
              .AppendLine(DescribeServerStatus(_plugin.ControlUdpServer?.Status, _plugin.Settings.UdpControlEnabled));
            sb.Append("Stub manager: ").Append(DescribeStubStatus(_plugin.SdkStubManager, _plugin.Settings.SdkEmulationEnabled));

            SdkServerStatusText.Text = sb.ToString();
        }

        private static string DescribeServerStatus(string? liveStatus, bool enabledIntent)
        {
            if (!string.IsNullOrEmpty(liveStatus)) return liveStatus!;
            // The toggles are live, so an "enabled" intent with no live status
            // yet is just the brief window before the background start finishes.
            return enabledIntent ? "Starting…" : "Disabled";
        }

        private static string DescribeStubStatus(Sdk.CoapStubManager? stub, bool enabledIntent)
        {
            if (stub == null)
                return enabledIntent ? "Starting…" : "Disabled";
            return stub.IsRunning
                ? $"Running (PID {stub.ProcessId})"
                : "Stopped";
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
                    || !string.Equals(_sdkRecentRequests[0], "Server not started — enable with the toggle above", StringComparison.Ordinal))
                {
                    _sdkRecentRequests.Clear();
                    _sdkRecentRequests.Add("Server not started — enable with the toggle above");
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
        /// Drop both server subscriptions so neither keeps this control
        /// alive via its event-handler list. Called from
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
            if (_subscribedControlUdpServer != null)
            {
                try { _subscribedControlUdpServer.RecentRequestAppended -= OnControlUdpRecentRequestAppended; }
                catch { }
                _subscribedControlUdpServer = null;
            }
        }

        // ===== PitHouse UDP control server — parallel to the CoAP block above =====

        /// <summary>
        /// Snapshot the UDP server's recent-requests buffer into the bound
        /// ObservableCollection. Same shape as <see cref="RefreshSdkRecentRequests"/>;
        /// rebuilt only when the dirty flag has fired (or <paramref name="force"/>).
        /// </summary>
        private void RefreshControlUdpRecentRequests(bool force)
        {
            if (ControlUdpRecentRequestsList == null) return;
            TrySubscribeToControlUdpServer();

            var server = _plugin.ControlUdpServer;
            if (server == null)
            {
                if (_controlUdpRecentRequests.Count != 1
                    || !string.Equals(_controlUdpRecentRequests[0], "Server not started — enable with the toggle above", StringComparison.Ordinal))
                {
                    _controlUdpRecentRequests.Clear();
                    _controlUdpRecentRequests.Add("Server not started — enable with the toggle above");
                }
                _controlUdpRecentDirty = false;
                return;
            }

            if (!force && !_controlUdpRecentDirty) return;
            _controlUdpRecentDirty = false;

            var snapshot = server.RecentRequests;
            var rendered = new List<string>(snapshot.Count);
            for (int i = snapshot.Count - 1; i >= 0; i--)
            {
                rendered.Add(FormatControlUdpRecentRequest(snapshot[i]));
            }

            _controlUdpRecentRequests.Clear();
            if (rendered.Count == 0)
            {
                _controlUdpRecentRequests.Add("No requests yet — third-party tools only talk here when they read or write a setting");
            }
            else
            {
                foreach (var line in rendered) _controlUdpRecentRequests.Add(line);
            }
        }

        private static string FormatControlUdpRecentRequest(Sdk.PitHouseUdp.MozaControlUdpServer.RecentRequest row)
        {
            // HH:mm:ss.fff  PacketId N  Operation  Detail  (Nms)
            string time = row.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string pid = row.PacketId >= 0
                ? $"PacketId {row.PacketId,-3}"
                : "PacketId ?  ";
            string detail = string.IsNullOrEmpty(row.Detail) ? "" : " " + row.Detail;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}  {1}  {2}{3}  ({4}ms)",
                time, pid, row.Operation, detail, row.DurationMs);
        }

        private void TrySubscribeToControlUdpServer()
        {
            var current = _plugin.ControlUdpServer;
            if (ReferenceEquals(current, _subscribedControlUdpServer)) return;

            if (_subscribedControlUdpServer != null)
            {
                try { _subscribedControlUdpServer.RecentRequestAppended -= OnControlUdpRecentRequestAppended; }
                catch { /* receiver may already have been torn down */ }
            }
            _subscribedControlUdpServer = current;
            if (_subscribedControlUdpServer != null)
                _subscribedControlUdpServer.RecentRequestAppended += OnControlUdpRecentRequestAppended;
        }

        private void OnControlUdpRecentRequestAppended()
        {
            // Fires on the UDP server's receive thread — DO NOT touch WPF.
            _controlUdpRecentDirty = true;
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
            RefreshControlUdpRecentRequests(force: false);
        }

        // ===== Event handlers =====

        private void SdkEmulationEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = SdkEmulationEnabledCheck.IsChecked == true;
            _plugin.Settings.SdkEmulationEnabled = on;
            _plugin.SaveSettings();
            // Apply the change live — no plugin restart needed. Off-loaded to the
            // ThreadPool because the stub spawn/teardown (CreateProcess +
            // JobObject under Wine) can take a moment and must not stall the WPF
            // thread; the 500 ms RefreshSdkTabTick renders the resulting status.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { _plugin.SetSdkEmulationEnabled(on); }
                catch { /* helper logs its own failures; status reflects them */ }
            });
            RefreshSdkStatus();
        }

        private void UdpControlEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool on = UdpControlEnabledCheck.IsChecked == true;
            _plugin.Settings.UdpControlEnabled = on;
            _plugin.SaveSettings();
            System.Threading.Tasks.Task.Run(() =>
            {
                try { _plugin.SetUdpControlEnabled(on); }
                catch { /* helper logs its own failures; status reflects them */ }
            });
            RefreshSdkStatus();
        }

        private void SdkRefreshStatusButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshSdkStatus();
            RefreshSdkRecentRequests(force: true);
            RefreshControlUdpRecentRequests(force: true);
        }
    }
}
