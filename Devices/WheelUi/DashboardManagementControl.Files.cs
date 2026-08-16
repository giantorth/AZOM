using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.UI;
using MozaPlugin.Resources;

namespace MozaPlugin.Devices.WheelUi
{
    // Files section: dashboard upload + on-device dashboard inventory.
    public partial class DashboardManagementControl
    {
        // Source bytes + name held while the user picks; pushed to the
        // uploader on UploadNow_Click. Decouples picking from uploading so the
        // user can review parsed name/MD5 before sending.
        private byte[]? _uploadPickedContent;
        private string _uploadPickedName = "";
        private string _uploadPickedSourceLabel = "";
        // Directory the mzdash file lives in. Used to find sibling PNGs at
        // <dir>/Resource/MD5/<hex>.png for the multi-file upload bundle.
        // Empty for library/embedded picks.
        private string _uploadPickedSourceDirectory = "";
        private bool _uploadLibrarySeeded;

        // ── Upload source pickers ───────────────────────────────────────

        private void UploadSourceRadio_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            bool libMode = UploadSourceLibraryRadio?.IsChecked == true;
            if (UploadFilePanel != null)
                UploadFilePanel.Visibility = libMode ? Visibility.Collapsed : Visibility.Visible;
            if (UploadLibraryPanel != null)
                UploadLibraryPanel.Visibility = libMode ? Visibility.Visible : Visibility.Collapsed;
            if (libMode) SeedUploadLibrary(force: false);
        }

        private void UploadPickFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = Strings.Upload_FileDialog_Filter,
                Title = Strings.Upload_FileDialog_Title,
            };
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
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Strings.Dialog_ReadMzdashFailed, ex.Message),
                    "Moza", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UploadLibraryRefresh_Click(object sender, RoutedEventArgs e)
        {
            SeedUploadLibrary(force: true);
        }

        private void SeedUploadLibrary(bool force)
        {
            if (UploadLibraryCombo == null || _plugin == null) return;
            if (_uploadLibrarySeeded && !force) return;
            using (_suppressor.Begin())
            {
                string? prev = UploadLibraryCombo.SelectedItem as string;
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
            _uploadLibrarySeeded = true;
            UpdateUploadFolderInfo();
        }

        // ── Dashboard-library folder (relocated from the telemetry section) ──
        // The mzdash folder is only needed to populate the upload library;
        // telemetry binds to the wheel's live catalog, so the folder controls
        // live here next to the library picker.

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
            _plugin.DashCache?.LoadFromFolder(path);
            SeedUploadLibrary(force: true);
            UpdateUploadFolderInfo();
        }

        private void UpdateUploadFolderInfo()
        {
            if (UploadFolderInfo == null) return;
            var folder = _plugin?.ActiveTelemetryMzdashFolder;
            UploadFolderInfo.Text = string.IsNullOrEmpty(folder) ? "" : string.Format(Strings.Upload_FolderPrefix, folder);
        }

        private void UploadLibraryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            if (UploadLibraryCombo?.SelectedItem is not string name || string.IsNullOrEmpty(name))
                return;
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
            // DashCache so widget PNG assets can be looked up. Builtins from
            // embedded resources have no dir → single-file upload.
            _uploadPickedSourceDirectory = DashboardLibraryResolver.ResolveDirectory(_plugin.DashCache, name);
            if (UploadStatusText != null
                && UiHelpers.StatusMatchesFormatPrefix(UploadStatusText.Text, Strings.Upload_CannotResolveBytes))
                UploadStatusText.Text = Strings.Status_Idle;
        }

        private void UploadNow_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var ts = _plugin.TelemetrySender;
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
            public string Hash { get; set; } = "";
            public string HashShort => string.IsNullOrEmpty(Hash) ? "" :
                (Hash.Length > 12 ? Hash.Substring(0, 12) + "…" : Hash);
            public string LastModified { get; set; } = "";
            public string Id { get; set; } = "";
        }

        private void WheelFilesRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshFilesTab();
        }

        private void WheelFilesDelete_Click(object sender, RoutedEventArgs e)
        {
            // Delete = completelyRemove(id) RPC + library-list re-send without
            // the name — the exact PitHouse exchange captured 2026-08-16
            // (bridge-enable-toggle: RPC at t=1489.8, list re-send at t=1490.6,
            // wheel state push confirms removal ~0.5 s later). The earlier
            // "wedges wheel firmware" neutering traced back to the RPC
            // envelope's comp_size missing the protocol-wide +4 — the wheel was
            // choking on a truncated zlib stream, not on the verb.
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
            var ts = _plugin?.TelemetrySender;
            if (ts == null)
            {
                MessageBox.Show(Strings.Dialog_TelemetrySenderUnavailable,
                    "Moza", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            // The RPC call blocks on a reply the wheel may answer only with a
            // state push — run it off the UI thread; the wheel's follow-up
            // state push refreshes the grid via the normal tick.
            string dirName = row.DirName;
            string id = row.Id;
            MozaLog.Info($"[AZOM] Delete requested: \"{dirName}\" id={id}");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    byte[]? reply = ts.SendRpcCall("completelyRemove", id);
                    MozaLog.Info(
                        $"[AZOM] completelyRemove(\"{dirName}\") sent; " +
                        $"reply={(reply == null ? "none (state-push expected)" : reply.Length + "B")}");
                    ts.RemoveDashboardFromLibrary(dirName);
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] completelyRemove(\"{dirName}\") failed: {ex.Message}");
                }
            });
        }

        private void WheelFilesEnable_Click(object sender, RoutedEventArgs e)
        {
            // Enable = the host's configJson() library list naming the dash —
            // the wheel syncs enablement against that list (observed flip
            // ~108 ms after the list re-send in the 2026-08-16 ground truth).
            if (((Button)sender).Tag is not WheelFileRow row) return;
            if (string.IsNullOrEmpty(row.DirName)) return;
            var ts = _plugin?.TelemetrySender;
            if (ts == null)
            {
                MessageBox.Show(Strings.Dialog_TelemetrySenderUnavailable,
                    "Moza", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            ts.EnableUploadedDashboard(row.DirName);
            if (WheelFilesStatusBox != null)
                WheelFilesStatusBox.Text = $"{Strings.Label_Enable}: {row.DirName}";
        }

        // ── Refresh ─────────────────────────────────────────────────────

        internal void RefreshFilesTab()
        {
            RefreshDashboardUploadStatus();
            RefreshWheelFilesGrid();
        }

        private void RefreshDashboardUploadStatus()
        {
            if (UploadInfoNameText == null || _plugin == null || _data == null) return;
            var ts = _plugin.TelemetrySender;

            string activeName = ts?.MzdashName ?? "";
            string displayName = !string.IsNullOrEmpty(_uploadPickedName)
                ? _uploadPickedName
                : (!string.IsNullOrEmpty(activeName) ? activeName : "—");
            UploadInfoNameText.Text = displayName;

            int rawSize = _uploadPickedContent?.Length ?? ts?.MzdashContent?.Length ?? 0;
            UploadInfoRawSizeText.Text = rawSize > 0 ? $"{rawSize:N0} bytes" : "—";

            byte[]? bytes = _uploadPickedContent ?? ts?.MzdashContent;
            UploadInfoMd5Text.Text = bytes != null && bytes.Length > 0
                ? FileTransferBuilder.Md5Hex(FileTransferBuilder.ComputeMd5(bytes))
                : "—";

            bool inFlight = ts?.IsUploadInFlight ?? false;
            UploadInfoInFlightText.Text = inFlight ? "yes" : "no";
            UploadInfoInFlightText.Foreground = inFlight ? Brushes.Goldenrod : Brushes.Gray;

            uint bw = ts?.UploadLastBytesWritten ?? 0;
            uint total = ts?.UploadLastTotalSize ?? 0;
            UploadInfoProgressText.Text = total == 0
                ? "—"
                : $"{bw:N0} / {total:N0}" + (bw == total && total != 0 ? "  (complete)" : "");

            byte status = ts?.UploadLastStatusByte ?? 0;
            UploadInfoStatusByteText.Text = status == 0 ? "—" : $"0x{status:X2}";

            if (UploadStatusText != null && !inFlight && total != 0)
            {
                if (bw == total)
                    UploadStatusText.Text = string.Format(Strings.Upload_Complete, bw, total, status.ToString("X2"));
                else if (UiHelpers.StatusMatchesFormatPrefix(UploadStatusText.Text, Strings.Upload_Queued))
                    UploadStatusText.Text = string.Format(Strings.Upload_Stopped, bw, total, status.ToString("X2"));
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

        // Signature of the rows currently bound to the grid. The refresh tick
        // runs every 500 ms; re-setting ItemsSource with fresh row objects on
        // every tick recreates the row buttons under the pointer and eats
        // clicks (observed: several Delete presses before one reached the
        // handler). Rebind only when the data actually changed.
        private string _lastFilesGridSignature = "\0";

        private void RefreshWheelFilesGrid()
        {
            if (WheelFilesGrid == null || _plugin == null) return;
            var state = _plugin.WheelStateForDiagnostics;
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
                        State = "disabled",
                        Title = d.Title,
                        DirName = d.DirName,
                        Hash = d.Hash,
                        LastModified = d.LastModified,
                        Id = d.Id,
                    });
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
