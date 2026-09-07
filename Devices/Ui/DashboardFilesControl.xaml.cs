using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.UI;
using MozaPlugin.Resources;

namespace MozaPlugin.Devices.Ui
{
    /// <summary>
    /// Files tab: dashboard upload + on-device dashboard inventory
    /// (Enable/Delete). Shared by the wheel page and the CM2 dash page —
    /// self-contained like <see cref="DashboardManagementControl"/>: own
    /// 500 ms refresh timer gated on Loaded/Unloaded, plugin resolved per
    /// tick so plugin reloads self-heal.
    /// </summary>
    public partial class DashboardFilesControl : UserControl
    {
        private MozaPlugin? _plugin;
        private MozaData? _data;
        private readonly EventSuppressor _suppressor = new EventSuppressor();
        private bool _suppressEvents => _suppressor.Suppressed;

        private readonly DispatcherTimer _refreshTimer;

        /// <summary>When true this control targets the CM2 dash pipeline
        /// (<c>ActiveCm2Sender</c>), not the wheel. Set by the CM2 device page.</summary>
        internal bool IsCm2Target { get; set; }

        private global::MozaPlugin.Telemetry.TelemetrySender? ActiveSender =>
            _plugin == null ? null : (IsCm2Target ? _plugin.ActiveCm2Sender : _plugin.TelemetrySender);

        public DashboardFilesControl()
        {
            using (_suppressor.Begin())
            {
                InitializeComponent();
                ResolvePlugin();
            }

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += (_, _) =>
            {
                try { RefreshFilesTab(); }
                catch (Exception ex) { MozaLog.DebugIfChanged("ui-tick-files", $"[AZOM] Files tab tick failed: {ex}"); }
            };

            // Settings restore and the Studio probe run here, not in the ctor:
            // MozaPlugin.Instance can still be null at construction (see
            // ResolvePlugin above), and IsCm2Target is applied by an object
            // initializer AFTER the ctor returns, so a ctor-time read of it
            // would always see false.
            Loaded += (_, _) =>
            {
                RestoreUploadSourceMode();
                ProbeStudio(force: false);
                RefreshFilesTab();
                if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
            };
            Unloaded += (_, _) => _refreshTimer.Stop();
        }

        private bool ResolvePlugin()
        {
            _plugin = MozaPlugin.Instance;
            if (_plugin == null) return false;
            _data = _plugin.Data;
            return true;
        }

        // Source bytes + name held while the user picks; pushed to the
        // uploader on UploadNow_Click. Decouples picking from uploading so the
        // user can review parsed name/MD5 before sending.
        private byte[]? _uploadPickedContent;
        private string _uploadPickedName = "";
        private string _uploadPickedSourceLabel = "";
        // Directory the mzdash file lives in. Used to find sibling image assets
        // at <dir>/Resource/MD5/<hex>.<ext> for the multi-file upload bundle.
        // Empty for library/embedded picks.
        private string _uploadPickedSourceDirectory = "";
        private bool _uploadLibrarySeeded;

        // MOZA Dashboard Studio (the vendor's standalone editor) state. The exe
        // path is probed once per control instance; the rescan bookkeeping only
        // engages after the user has actually launched Studio from here, so
        // everyone else pays nothing for it.
        private string? _studioExePath;
        private bool _studioProbed;
        private bool _studioUsedThisSession;
        private int _studioRescanTick;
        private string _libraryFolderStamp = "";

        // DashCache.CachedNameCount as of the last seed. The library fills in
        // asynchronously relative to this page — DeviceProber scans the mzdash
        // folder on wheel detect, and wheel downloads land later still — so the
        // refresh tick watches this to re-seed the combo when it changes.
        private int _lastLibraryNameCount = -1;
        // One-shot: load the folder library ourselves if nothing else has, so a
        // wheel-less session still gets a populated picker.
        private bool _libraryColdLoadTried;

        // ── Upload source pickers ───────────────────────────────────────

        // Segment 0 = local .mzdash file, segment 1 = dashboard library.
        private bool LibraryMode => UploadSourceSelector?.SelectedIndex == 1;

        private void UploadSourceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            bool libMode = LibraryMode;
            ApplyUploadSourceVisibility(libMode);
            if (libMode) SeedUploadLibrary(force: false);
            if (_plugin?.Settings != null)
            {
                _plugin.Settings.DashboardUploadSourceMode = libMode
                    ? global::MozaPlugin.Settings.DashboardUploadSource.Library
                    : global::MozaPlugin.Settings.DashboardUploadSource.LocalFile;
                _plugin.SaveSettings();
            }
        }

        private void ApplyUploadSourceVisibility(bool libMode)
        {
            if (UploadFilePanel != null)
                UploadFilePanel.Visibility = libMode ? Visibility.Collapsed : Visibility.Visible;
            if (UploadLibraryPanel != null)
                UploadLibraryPanel.Visibility = libMode ? Visibility.Visible : Visibility.Collapsed;
            // Lives in the source card to the right, but tracks the same mode.
            if (UploadFolderPanel != null)
                UploadFolderPanel.Visibility = libMode ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Re-apply the persisted source selection. Plugin-global, so the wheel page
        /// and the CM2 dash page share it — see the field comment on
        /// <see cref="MozaPluginSettings.DashboardUploadSourceMode"/>.
        /// Idempotent: safe to re-run on every page revisit.
        /// </summary>
        private void RestoreUploadSourceMode()
        {
            if (!ResolvePlugin() || _plugin?.Settings == null) return;
            bool lib = _plugin.Settings.DashboardUploadSourceMode
                       == global::MozaPlugin.Settings.DashboardUploadSource.Library;
            using (_suppressor.Begin())
            {
                if (UploadSourceSelector != null) UploadSourceSelector.SelectedIndex = lib ? 1 : 0;
                ApplyUploadSourceVisibility(lib);
            }
            // Outside the suppressor: SeedUploadLibrary opens its own scope and
            // its tail needs the selection handler to actually run.
            if (lib) SeedUploadLibrary(force: false);
        }

        private void UploadPickFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = Strings.Upload_FileDialog_Filter,
                Title = Strings.Upload_FileDialog_Title,
            };
            // Reopen where they were last time. Only the DIRECTORY is persisted
            // — restoring the file itself would either re-read an unbounded file
            // on the UI thread at page load, or paint a filename while
            // _uploadPickedContent is null and the Upload button stays disabled.
            var lastDir = _plugin?.Settings?.LastUploadFileDirectory;
            if (!string.IsNullOrEmpty(lastDir) && System.IO.Directory.Exists(lastDir))
                dlg.InitialDirectory = lastDir;
            if (dlg.ShowDialog() != true) return;
            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(dlg.FileName);
                _uploadPickedContent = bytes;
                _uploadPickedName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName) ?? "";
                _uploadPickedSourceLabel = dlg.FileName;
                _uploadPickedSourceDirectory = System.IO.Path.GetDirectoryName(dlg.FileName) ?? "";
                if (UploadPickedFileText != null)
                    UploadPickedFileText.Text = dlg.FileName;
                if (_plugin?.Settings != null)
                {
                    _plugin.Settings.LastUploadFileDirectory = _uploadPickedSourceDirectory;
                    _plugin.SaveSettings();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Strings.Dialog_ReadMzdashFailed, ex.Message),
                    "Moza", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UploadLibraryRefresh_Click(object sender, RoutedEventArgs e)
        {
            // Re-read the folder from disk, not just the combo from the cache —
            // this is the "I came back from Dashboard Studio" path, and a file
            // written since the last ApplyUploadFolder is otherwise invisible.
            // Also re-probe the exe so a mid-session PitHouse install recovers
            // without restarting SimHub.
            ProbeStudio(force: true);
            var folder = _plugin?.ActiveTelemetryMzdashFolder;
            if (!string.IsNullOrEmpty(folder))
            {
                _plugin!.ReloadDashboardLibrary(folder);
                _libraryFolderStamp = ComputeLibraryStamp(_plugin.DashboardLibraryFolders(folder));
            }
            SeedUploadLibrary(force: true);
        }

        // ── MOZA Dashboard Studio ───────────────────────────────────────
        // The vendor editor is a standalone exe. Edit hands it the selected
        // dashboard's absolute path; New hands it the connected display's
        // descriptor. See UI/DashboardStudioLauncher.cs for the captured
        // command-line forms.

        private void ProbeStudio(bool force)
        {
            if (_studioProbed && !force) return;
            _studioProbed = true;
            _studioExePath = DashboardStudioLauncher.FindStudioExe(force);
            bool ok = _studioExePath != null;
            if (StudioEditButton != null) StudioEditButton.IsEnabled = ok;
            if (StudioCreateButton != null) StudioCreateButton.IsEnabled = ok;
            // A disabled button with no explanation is the worse failure.
            if (!ok && StudioStatusText != null) StudioStatusText.Text = Strings.Studio_NotInstalled;
        }

        private void StudioEdit_Click(object sender, RoutedEventArgs e)
        {
            string? path = ResolveEditablePath();
            if (path == null)
            {
                // Opening Studio bare here would silently discard what the user
                // selected, which reads as a broken button.
                if (StudioStatusText != null) StudioStatusText.Text = Strings.Studio_NoEditablePath;
                return;
            }
            ApplyLaunchResult(DashboardStudioLauncher.LaunchEdit(path));
        }

        /// <summary>
        /// The on-disk .mzdash behind the current pick, or null when there isn't
        /// one. Library entries that came from the wheel cache or an embedded
        /// builtin have no file on disk. Studio accepts any absolute path, so
        /// the configured library folder can live anywhere.
        /// </summary>
        private string? ResolveEditablePath()
        {
            if (!LibraryMode)
                return System.IO.File.Exists(_uploadPickedSourceLabel) ? _uploadPickedSourceLabel : null;
            if (UploadLibraryCombo?.SelectedItem is not string name || _plugin?.DashCache == null)
                return null;
            var p = _plugin.DashCache.TryGetFolderFilePath(name);
            return !string.IsNullOrEmpty(p) && System.IO.File.Exists(p) ? p : null;
        }

        private void StudioCreate_Click(object sender, RoutedEventArgs e)
        {
            var infos = ResolveIdealDeviceInfos();
            string? json = infos.Count > 0
                ? DashboardStudioLauncher.BuildIdealDeviceInfosJson(infos)
                : null;
            var result = DashboardStudioLauncher.LaunchCreate(json);
            ApplyLaunchResult(result);
            if (result.Outcome != DashboardStudioLauncher.LaunchOutcome.Started
                || StudioStatusText == null) return;
            if (json == null) { StudioStatusText.Text = Strings.Studio_NoDeviceInfo; return; }
            // Studio saves NEW projects under its own projectRoot, which need
            // not be this library's folder — say where, so the user can find it.
            var root = DashboardStudioLauncher.ResolveProjectRoot();
            if (!string.IsNullOrEmpty(root))
                StudioStatusText.Text = string.Format(Strings.Studio_NewDashboardSavesTo, root);
        }

        /// <summary>
        /// The connected target's display descriptor, sourced from the ACTIVE
        /// sender's configJson state. Deliberately not
        /// <c>_plugin.WheelStateForDiagnostics</c>, which is hardcoded to the
        /// wheel sender and would seed a CM2 project with the wheel's hardware
        /// version.
        /// </summary>
        private IReadOnlyList<WheelDashboardDeviceInfo> ResolveIdealDeviceInfos()
        {
            var st = ActiveSender?.WheelState;
            if (st == null) return Array.Empty<WheelDashboardDeviceInfo>();
            foreach (var d in st.EnabledDashboards)
                if (d.IdealDeviceInfos.Count > 0) return d.IdealDeviceInfos;
            foreach (var d in st.DisabledDashboards)
                if (d.IdealDeviceInfos.Count > 0) return d.IdealDeviceInfos;
            // Nothing known: the caller launches Studio UNSEEDED rather than
            // falling back to the exe's built-in RS21-W08 literal, which
            // describes one specific wheel's display.
            return Array.Empty<WheelDashboardDeviceInfo>();
        }

        private void ApplyLaunchResult(DashboardStudioLauncher.LaunchResult r)
        {
            if (r.Outcome == DashboardStudioLauncher.LaunchOutcome.Started)
                _studioUsedThisSession = true;
            if (StudioStatusText == null) return;
            StudioStatusText.Text = r.Outcome switch
            {
                DashboardStudioLauncher.LaunchOutcome.Started => Strings.Studio_Launched,
                DashboardStudioLauncher.LaunchOutcome.NotFound => Strings.Studio_NotInstalled,
                _ => string.Format(Strings.Studio_LaunchFailed, r.Error ?? ""),
            };
        }

        private void SeedUploadLibrary(bool force)
        {
            if (UploadLibraryCombo == null || _plugin == null) return;
            if (_uploadLibrarySeeded && !force) return;
            using (_suppressor.Begin())
            {
                string? prev = UploadLibraryCombo.SelectedItem as string;
                // Cold open (or first seed after a plugin reload): fall back to
                // the name persisted from last session.
                if (string.IsNullOrEmpty(prev)) prev = _plugin.Settings?.LastUploadLibraryName;
                UploadLibraryCombo.Items.Clear();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_plugin.DashCache != null)
                {
                    foreach (var name in _plugin.DashCache.CachedNames)
                        if (seen.Add(name)) UploadLibraryCombo.Items.Add(name);
                }
                foreach (var p in _plugin.DashProfileStore.BuiltinProfiles)
                    if (seen.Add(p.Name)) UploadLibraryCombo.Items.Add(p.Name);
                if (!string.IsNullOrEmpty(prev) && UploadLibraryCombo.Items.Contains(prev))
                    UploadLibraryCombo.SelectedItem = prev;
                else if (UploadLibraryCombo.Items.Count > 0 && UploadLibraryCombo.SelectedItem == null)
                    UploadLibraryCombo.SelectedIndex = 0;
            }
            // Only latch once the library actually had something in it. On a
            // cold start this control loads BEFORE DeviceProber has scanned the
            // mzdash folder, so an unconditional latch left the combo
            // permanently empty until the user pressed Refresh.
            _uploadLibrarySeeded = UploadLibraryCombo.Items.Count > 0;
            _lastLibraryNameCount = _plugin.DashCache?.CachedNameCount ?? 0;
            // The selection above was restored INSIDE the suppressor, so the
            // SelectionChanged handler was skipped and _uploadPickedContent is
            // still null — the combo would show a pick that Upload (and Edit)
            // can't act on. Resolve it here, outside the scope.
            if (_uploadPickedContent == null
                && UploadLibraryCombo.SelectedItem is string selected
                && !string.IsNullOrEmpty(selected))
            {
                ApplyLibrarySelection(selected);
            }
            UpdateUploadFolderInfo();
        }

        // ── Dashboard-library folder ─────────────────────────────────────
        // The mzdash folder populates the upload library; telemetry binds to
        // the wheel's live catalog, so the folder controls live here next to
        // the library picker.

        private void UploadSetFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = Strings.Upload_FolderDialog_Description;
                dlg.ShowNewFolderButton = false;
                if (!string.IsNullOrEmpty(_plugin.ActiveTelemetryMzdashFolder)
                    && System.IO.Directory.Exists(_plugin.ActiveTelemetryMzdashFolder))
                    dlg.SelectedPath = _plugin.ActiveTelemetryMzdashFolder;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                ApplyUploadFolder(dlg.SelectedPath);
            }
        }

        private void UploadAutoDetect_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;

            string dashesRoot = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MOZA Pit House", "_dashes");

            if (!System.IO.Directory.Exists(dashesRoot))
            {
                MessageBox.Show(
                    string.Format(Strings.Upload_AutoDetect_NotFound, dashesRoot),
                    Strings.Upload_AutoDetect_Caption,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            byte[] uid = _plugin.Data?.WheelMcuUid ?? Array.Empty<byte>();
            string uidHex = uid.Length == 12 ? WheelUiHelpers.UidToHex(uid) : "";

            string? picked = null;
            string? failReason = null;

            if (!string.IsNullOrEmpty(uidHex))
            {
                string? match = System.IO.Directory.EnumerateDirectories(dashesRoot)
                    .FirstOrDefault(p => string.Equals(
                        new System.IO.DirectoryInfo(p).Name,
                        uidHex,
                        StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    picked = match;
                else
                    failReason = string.Format(Strings.Upload_AutoDetect_NoFolderForWheel, uidHex, dashesRoot);
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
                    failReason = Strings.Upload_AutoDetect_NoFolders;
                else
                    failReason = string.Format(Strings.Upload_AutoDetect_Multiple, guidDirs.Count);
            }

            if (picked == null)
            {
                MessageBox.Show(failReason, Strings.Upload_AutoDetect_Caption,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApplyUploadFolder(picked);
        }

        private void ApplyUploadFolder(string path)
        {
            if (_plugin == null) return;
            _plugin.ActiveTelemetryMzdashFolder = path;
            _plugin.SaveSettings();
            _plugin.ReloadDashboardLibrary(path);
            // Fresh baseline so the post-Studio rescan doesn't fire once purely
            // because the folder changed underneath it.
            _libraryFolderStamp = ComputeLibraryStamp(_plugin.DashboardLibraryFolders(path));
            SeedUploadLibrary(force: true);
            UpdateUploadFolderInfo();
        }

        private void UpdateUploadFolderInfo()
        {
            if (UploadFolderInfo == null) return;
            // Report every folder the library actually reads, not just the
            // configured one — Dashboard Studio's project root is in there too,
            // and a dashboard appearing from a path the user never set is
            // otherwise baffling.
            var folders = _plugin?.DashboardLibraryFolders();
            UploadFolderInfo.Text = (folders == null || folders.Count == 0)
                ? ""
                : string.Format(Strings.Upload_FolderPrefix, string.Join("  +  ", folders));
        }

        private void UploadLibraryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            if (UploadLibraryCombo?.SelectedItem is not string name || string.IsNullOrEmpty(name))
                return;
            ApplyLibrarySelection(name);
        }

        /// <summary>
        /// Resolve one library name into the pending upload state. Extracted
        /// from the SelectionChanged handler so <see cref="SeedUploadLibrary"/>
        /// can call it for a selection it restored under the event suppressor.
        /// </summary>
        private void ApplyLibrarySelection(string name)
        {
            if (_plugin == null) return;
            byte[]? bytes = DashboardLibraryResolver.ResolveBytes(_plugin.DashCache, _plugin.DashProfileStore, name);
            if (bytes == null)
            {
                _uploadPickedContent = null;
                _uploadPickedName = "";
                _uploadPickedSourceLabel = "";
                _uploadPickedSourceDirectory = "";
                if (UploadStatusText != null)
                    UploadStatusText.Text = string.Format(Strings.Upload_CannotResolveBytes, name);
                return;
            }
            _uploadPickedContent = bytes;
            _uploadPickedName = name;
            _uploadPickedSourceLabel = $"library: {name}";
            // Library/folder entries: try to resolve the source dir from
            // DashCache so widget image assets can be looked up. Builtins from
            // embedded resources have no dir → single-file upload.
            _uploadPickedSourceDirectory = DashboardLibraryResolver.ResolveDirectory(_plugin.DashCache, name);
            if (UploadStatusText != null
                && UiHelpers.StatusMatchesFormatPrefix(UploadStatusText.Text, Strings.Upload_CannotResolveBytes))
                UploadStatusText.Text = "";
            if (_plugin.Settings != null)
            {
                _plugin.Settings.LastUploadLibraryName = name;
                _plugin.SaveSettings();
            }
        }

        private void UploadNow_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var ts = ActiveSender;
            if (ts == null)
            {
                if (UploadStatusText != null)
                    UploadStatusText.Text = Strings.Status_TelemetrySenderUnavailableInit;
                return;
            }
            if (_uploadPickedContent == null || _uploadPickedContent.Length == 0)
            {
                if (UploadStatusText != null)
                    UploadStatusText.Text = Strings.Status_PickMzdashFirst;
                return;
            }
            string name = !string.IsNullOrEmpty(_uploadPickedName) ? _uploadPickedName : "dashboard";
            string? sourceDir = string.IsNullOrEmpty(_uploadPickedSourceDirectory)
                ? null
                : _uploadPickedSourceDirectory;
            bool queued = ts.TriggerManualUpload(_uploadPickedContent, name, sourceDir);
            if (UploadStatusText != null)
            {
                UploadStatusText.Text = queued
                    ? string.Format(Strings.Upload_Queued, name)
                    : Strings.Upload_NotStarted;
            }
        }

        // ── Wheel-side dashboard inventory ──────────────────────────────

        public sealed class WheelFileRow
        {
            public string State { get; set; } = "";       // "enabled" / "disabled"
            public string Title { get; set; } = "";
            public string DirName { get; set; } = "";
            // Not shown in the grid; still part of the rebind signature so a
            // hash change on an otherwise-identical row refreshes it.
            public string Hash { get; set; } = "";
            public string LastModified { get; set; } = "";
            public string Id { get; set; } = "";
        }

        // DataGrid's template wraps its rows in a ScrollViewer that marks the
        // wheel event handled even though this grid is unbounded and never
        // scrolls itself, so SimHub's host scroller never sees it. Re-raise on
        // the parent. (The sibling DashboardManagementControl uses ItemsControl,
        // which has no inner ScrollViewer and needs none of this.)
        private void WheelFilesGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            e.Handled = true;
            if (((FrameworkElement)sender).Parent is not UIElement parent) return;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = sender,
            });
        }

        private void WheelFilesRefresh_Click(object sender, RoutedEventArgs e)
        {
            // Drop the signature so the grid rebinds even when the wheel state
            // is unchanged — otherwise the button looks dead.
            _lastFilesGridSignature = "\0";
            RefreshFilesTab();
        }

        private void WheelFilesDelete_Click(object sender, RoutedEventArgs e)
        {
            // Delete = completelyRemove(id) RPC + library-list re-send without
            // the name — the PitHouse exchange captured 2026-08-16. See
            // docs/protocol/dashboard-upload/config-rpc-session-09.md.
            if (((Button)sender).Tag is not WheelFileRow row) return;
            if (string.IsNullOrEmpty(row.Id))
            {
                MessageBox.Show(string.Format(Strings.Dialog_CannotDeleteNoId, row.Title),
                    "Moza", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var confirm = MessageBox.Show(
                string.Format(Strings.Dialog_ConfirmDelete_Body, row.Title, row.DirName, row.Id),
                Strings.Dialog_ConfirmDelete_Caption,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;
            var ts = ActiveSender;
            if (ts == null)
            {
                MessageBox.Show(Strings.Dialog_TelemetrySenderUnavailable,
                    "Moza", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            // The RPC blocks on a reply the wheel answers only with a state
            // push — run off the UI thread; the follow-up state push refreshes
            // the grid via the normal tick.
            string dirName = row.DirName;
            string id = row.Id;
            var plugin = _plugin;
            MozaLog.Info($"[AZOM] Delete requested: \"{dirName}\" id={id}");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Don't delete the CURRENTLY-RENDERED dash out from under
                    // the wheel — switch away first. WheelReportedSlot is -1
                    // until the wheel reports a switch this session (cold
                    // start / post-reconnect), so an UNKNOWN rendered slot is
                    // treated as possibly-active and switched away too.
                    var state = plugin?.WheelStateForDiagnostics;
                    var list = state?.ConfigJsonList;
                    int targetSlot = -1, fallbackSlot = -1;
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (string.Equals(list[i], dirName, StringComparison.Ordinal))
                                targetSlot = i;
                            else if (fallbackSlot < 0)
                                fallbackSlot = i;
                        }
                    }
                    bool deletingActive = targetSlot >= 0
                        && (ts.WheelReportedSlot == targetSlot
                            || ts.WheelReportedSlot < 0
                            || string.Equals(plugin?.ActiveTelemetryProfileName, dirName,
                                StringComparison.OrdinalIgnoreCase));
                    if (deletingActive && fallbackSlot >= 0)
                    {
                        MozaLog.Info(
                            $"[AZOM] Deleting the active/possibly-active dashboard — switching to " +
                            $"slot {fallbackSlot} (\"{list![fallbackSlot]}\") first");
                        plugin!.OnDashboardSwitched((uint)fallbackSlot);
                        System.Threading.Thread.Sleep(1500);
                    }

                    // Arm BEFORE the verb: SendRpcCall blocks its full 2 s
                    // timeout (sess=0x09 has no reply route) while the wheel's
                    // confirm delta lands ~0.6 s in, so arming afterwards makes
                    // the confirm hook miss the very delta it waits for.
                    ts.RemoveDashboardFromLibrary(dirName, id);
                    byte[]? reply = ts.SendRpcCall("completelyRemove", id);
                    MozaLog.Info(
                        $"[AZOM] completelyRemove(\"{dirName}\") sent; " +
                        $"reply={(reply == null ? "none (state-push expected)" : reply.Length + "B")}");
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] completelyRemove(\"{dirName}\") failed: {ex.Message}");
                }
            });
        }

        private void WheelFilesEnable_Click(object sender, RoutedEventArgs e)
        {
            // Enable = RE-UPLOAD. A disabled wheel-side entry has no installed
            // files — the library-list declaration only triggers an install
            // when a freshly staged bundle exists, i.e. right after an upload
            // (every observed successful enable — radarrr, porn, F1 — happened
            // immediately post-upload; list-only declarations of long-disabled
            // dashes do nothing). PitHouse has no enable button for the same
            // reason: its "enable" is re-sending the dash. The upload path's
            // post-success flow handles declaration + reconcile.
            if (((Button)sender).Tag is not WheelFileRow row) return;
            if (string.IsNullOrEmpty(row.DirName)) return;
            var ts = ActiveSender;
            if (ts == null || _plugin == null)
            {
                MessageBox.Show(Strings.Dialog_TelemetrySenderUnavailable,
                    "Moza", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            byte[]? bytes = DashboardLibraryResolver.ResolveBytes(
                _plugin.DashCache, _plugin.DashProfileStore, row.DirName);
            if (bytes == null)
            {
                if (WheelFilesStatusBox != null)
                    WheelFilesStatusBox.Text = string.Format(
                        Strings.Upload_CannotResolveBytes, row.DirName);
                return;
            }
            string dir = DashboardLibraryResolver.ResolveDirectory(_plugin.DashCache, row.DirName);
            bool queued = ts.TriggerManualUpload(
                bytes, row.DirName, string.IsNullOrEmpty(dir) ? null : dir);
            if (WheelFilesStatusBox != null)
                WheelFilesStatusBox.Text = queued
                    ? string.Format(Strings.Upload_Queued, row.DirName)
                    : Strings.Upload_NotStarted;
        }

        // ── Refresh ─────────────────────────────────────────────────────

        internal void RefreshFilesTab()
        {
            if (!ResolvePlugin()) return;
            MaybeReseedLibrary();
            MaybeRescanLibraryAfterStudio();
            RefreshDashboardUploadStatus();
            RefreshWheelFilesGrid();
        }

        /// <summary>
        /// Re-seed the library combo when the cache's contents changed since the
        /// last seed. The library is populated on someone else's schedule —
        /// <c>DeviceProber</c> scans the mzdash folder only once a wheel is
        /// detected, and wheel-cache downloads land later still — so on a cold
        /// start this page renders before there is anything to show.
        ///
        /// <para>Costs two dictionary <c>Count</c> reads per tick
        /// (<see cref="DashboardCache.CachedNameCount"/> is O(1) by design);
        /// the actual rebuild only runs on a real change.</para>
        /// </summary>
        private void MaybeReseedLibrary()
        {
            if (_plugin?.DashCache == null) return;

            // Nothing has loaded the folder library yet (no wheel detected this
            // session). Do it once ourselves rather than showing an empty picker.
            if (!_libraryColdLoadTried
                && _plugin.DashCache.FolderProfileCount == 0
                && !string.IsNullOrEmpty(_plugin.ActiveTelemetryMzdashFolder))
            {
                _libraryColdLoadTried = true;
                // ReadAllBytes + ParseMzdash per file, recursive — off the dispatcher
                // (DeviceProber runs the same load on the serial read thread).
                var plugin = _plugin;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { plugin.ReloadDashboardLibrary(); }
                    catch (Exception ex) { MozaLog.Warn($"[AZOM] Dashboard library cold load failed: {ex.Message}"); }
                });
            }

            int count = _plugin.DashCache.CachedNameCount;
            if (count == _lastLibraryNameCount || count == 0) return;
            // Only in library mode. SeedUploadLibrary's tail resolves the
            // selection into _uploadPickedContent, which in local-file mode
            // would quietly replace what the user is about to upload.
            // Deliberately do NOT consume the count here, so switching back to
            // library mode re-seeds on the next tick.
            if (!LibraryMode) return;
            SeedUploadLibrary(force: true);   // updates _lastLibraryNameCount
        }

        /// <summary>
        /// Pick up dashboards Studio wrote while the user was away, without
        /// hooking Process.Exited (which fires on a ThreadPool thread, may
        /// arrive after this control is unloaded, and misses a Studio the user
        /// opened outside our button).
        ///
        /// <para>Gated three ways so the cost is near zero: only after Studio
        /// was launched from here, only every 10th tick (~5 s), and only when a
        /// cheap directory-timestamp stamp actually changed. The reload is
        /// ReadAllBytes + ParseMzdash per file, recursive — it must never run
        /// unconditionally on the 500 ms tick.</para>
        ///
        /// <para>The stamp covers EVERY library folder, not just the configured
        /// one: Studio saves a newly created dashboard into its own project
        /// root, so watching only the configured folder would miss exactly the
        /// case this exists for.</para>
        /// </summary>
        private void MaybeRescanLibraryAfterStudio()
        {
            if (!_studioUsedThisSession || _plugin == null) return;
            if (++_studioRescanTick < 10) return;
            _studioRescanTick = 0;

            string stamp = ComputeLibraryStamp(_plugin.DashboardLibraryFolders());
            if (stamp.Length == 0 || stamp == _libraryFolderStamp) return;
            _libraryFolderStamp = stamp;

            _plugin.ReloadDashboardLibrary();
            SeedUploadLibrary(force: true);
            if (StudioStatusText != null)
                StudioStatusText.Text = string.Format(
                    Strings.Studio_LibraryReloaded, UploadLibraryCombo?.Items.Count ?? 0);
        }

        /// <summary>
        /// Cheap change signal across every library folder: each root's own
        /// write time plus its newest immediate-subdirectory write time. Studio
        /// saves into &lt;root&gt;/&lt;Name&gt;/, so a re-save moves the
        /// subdirectory even when the root itself is untouched. Deliberately NOT
        /// recursive. Returns "" when nothing could be read, which suppresses
        /// the rescan.
        /// </summary>
        private static string ComputeLibraryStamp(IReadOnlyList<string> folders)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in folders)
            {
                string one = ComputeLibraryStamp(f);
                if (one.Length == 0) continue;
                sb.Append(one).Append('|');
            }
            return sb.ToString();
        }

        private static string ComputeLibraryStamp(string folder)
        {
            try
            {
                var root = new System.IO.DirectoryInfo(folder);
                if (!root.Exists) return "";
                long newest = root.LastWriteTimeUtc.Ticks;
                int count = 0;
                foreach (var sub in root.EnumerateDirectories())
                {
                    count++;
                    long t = sub.LastWriteTimeUtc.Ticks;
                    if (t > newest) newest = t;
                }
                return count + ":" + newest;
            }
            catch { return ""; }
        }

        private void RefreshDashboardUploadStatus()
        {
            if (UploadInfoProgressText == null || _plugin == null || _data == null) return;
            var ts = ActiveSender;

            bool inFlight = ts?.IsUploadInFlight ?? false;
            uint bw = ts?.UploadLastBytesWritten ?? 0;
            uint total = ts?.UploadLastTotalSize ?? 0;
            byte status = ts?.UploadLastStatusByte ?? 0;
            int pct = total == 0 ? 0 : (int)(bw * 100L / total);

            UploadInfoProgressText.Text =
                  inFlight    ? string.Format(Strings.Upload_StatusUploading, pct)
                : total == 0  ? Strings.Status_Idle
                : bw == total ? Strings.Upload_StatusComplete
                              : string.Format(Strings.Upload_StatusStopped, pct);
            UploadInfoProgressText.Foreground = inFlight
                ? (Brush)(TryFindResource("AmberBrush") ?? Brushes.Goldenrod)
                : (Brush)(TryFindResource("TextDimBrush") ?? Brushes.Gray);

            if (UploadStatusText != null && !inFlight && total != 0)
            {
                if (bw == total)
                    UploadStatusText.Text = string.Format(Strings.Upload_Complete, status.ToString("X2"));
                else if (UiHelpers.StatusMatchesFormatPrefix(UploadStatusText.Text, Strings.Upload_Queued))
                    UploadStatusText.Text = string.Format(Strings.Upload_Stopped, pct, status.ToString("X2"));
            }

            // Enable the upload button only when the wheel is connected and a
            // management session has been negotiated — TriggerManualUpload
            // rejects otherwise.
            if (UploadNowButton != null)
                UploadNowButton.IsEnabled = ts != null
                    && _uploadPickedContent != null
                    && _uploadPickedContent.Length > 0
                    && _data.IsConnected;
        }

        // Signature of the rows currently bound to the grid. Rebinding fresh
        // row objects on every 500 ms tick recreates the row buttons under the
        // pointer and eats clicks — rebind only when the data changed.
        private string _lastFilesGridSignature = "\0";

        private void RefreshWheelFilesGrid()
        {
            if (WheelFilesGrid == null || _plugin == null) return;
            var state = _plugin.WheelStateForDiagnostics;
            var senderForRows = ActiveSender;
            var rows = new List<WheelFileRow>();
            if (state != null)
            {
                foreach (var d in state.EnabledDashboards)
                    rows.Add(new WheelFileRow
                    {
                        State = "enabled",
                        Title = d.Title,
                        DirName = d.DirName,
                        Hash = d.Hash,
                        LastModified = d.LastModified,
                        Id = d.Id,
                    });
                foreach (var d in state.DisabledDashboards)
                    rows.Add(new WheelFileRow
                    {
                        // A just-uploaded dash sits in disabledManager until the
                        // wheel acts on our enable declaration, and that
                        // confirming delta often never arrives mid-session —
                        // show the declaration rather than a stale "disabled".
                        State = (senderForRows?.IsEnableDeclared(d.DirName) ?? false)
                            ? "enabling…" : "disabled",
                        Title = d.Title,
                        DirName = d.DirName,
                        Hash = d.Hash,
                        LastModified = d.LastModified,
                        Id = d.Id,
                    });
                // A freshly-uploaded dash can sit in the wheel's slot table with
                // NO manager entry yet (wire-observed 2026-08-18 upload #2:
                // configJsonList grew while enabled/disabled counts held) — the
                // managers alone therefore cannot render it. Add slot-table
                // names that no manager claims.
                foreach (var name in state.ConfigJsonList)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    bool have = false;
                    foreach (var r in rows)
                        if (string.Equals(r.DirName, name, StringComparison.Ordinal)) { have = true; break; }
                    if (have) continue;
                    rows.Add(new WheelFileRow
                    {
                        State = (senderForRows?.IsEnableDeclared(name) ?? false)
                            ? "enabling…" : "on wheel",
                        Title = name,
                        DirName = name,
                    });
                }
                // Ordinal by dirName — the order the wheel keeps its own table
                // in, so a new dash lands at its slot position instead of the
                // bottom of an enabled-then-disabled concatenation.
                rows.Sort((a, b) => string.CompareOrdinal(a.DirName, b.DirName));
            }

            var sigBuilder = new System.Text.StringBuilder(rows.Count * 64);
            foreach (var r in rows)
                sigBuilder.Append(r.State).Append('|').Append(r.DirName).Append('|')
                          .Append(r.Hash).Append('|').Append(r.Id).Append('|')
                          .Append(r.LastModified).Append('\n');
            string sig = sigBuilder.ToString();
            if (sig != _lastFilesGridSignature)
            {
                _lastFilesGridSignature = sig;
                // Preserve grid selection across rebind by DirName key.
                string? prevDir = (WheelFilesGrid.SelectedItem as WheelFileRow)?.DirName;
                WheelFilesGrid.ItemsSource = rows;
                if (!string.IsNullOrEmpty(prevDir))
                {
                    foreach (var r in rows)
                        if (r.DirName == prevDir) { WheelFilesGrid.SelectedItem = r; break; }
                }
            }
            if (WheelFilesStatusBox != null)
            {
                if (state == null)
                    WheelFilesStatusBox.Text = Strings.Status_NoConfigJsonState;
                else
                    WheelFilesStatusBox.Text =
                        $"{rows.Count} dashboards (captured {state.CapturedAt:HH:mm:ss})";
            }
        }
    }
}
