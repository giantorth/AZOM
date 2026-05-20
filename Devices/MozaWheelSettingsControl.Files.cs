using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.UI;

namespace MozaPlugin.Devices
{
    // Files sub-tab on the wheel device page — combines the legacy plugin-pane
    // "Upload" + "Wheel Files" tabs. Visible only when the wheel has reported
    // a dashboard (same gate as the Dashboard tab). All handlers used to live
    // on SettingsControl; moved here because the feature is wheel-scoped.
    public partial class MozaWheelSettingsControl
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
                Filter = "Moza dashboard (*.mzdash)|*.mzdash|All files (*.*)|*.*",
                Title = "Pick a .mzdash file to upload",
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
                MessageBox.Show($"Failed to read .mzdash file:\n{ex.Message}",
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
                    UploadStatusText.Text = $"Cannot resolve raw bytes for \"{name}\" — pick a local file instead.";
                return;
            }
            _uploadPickedContent = bytes;
            _uploadPickedName = name;
            _uploadPickedSourceLabel = $"library: {name}";
            // Library/folder entries: try to resolve the source dir from
            // DashCache so widget PNG assets can be looked up. Builtins from
            // embedded resources have no dir → single-file upload.
            _uploadPickedSourceDirectory = DashboardLibraryResolver.ResolveDirectory(_plugin.DashCache, name);
            if (UploadStatusText != null && UploadStatusText.Text.StartsWith("Cannot resolve"))
                UploadStatusText.Text = "idle";
        }

        private void UploadNow_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var ts = _plugin.TelemetrySender;
            if (ts == null)
            {
                if (UploadStatusText != null)
                    UploadStatusText.Text = "Telemetry sender unavailable (plugin not initialised).";
                return;
            }
            if (_uploadPickedContent == null || _uploadPickedContent.Length == 0)
            {
                if (UploadStatusText != null)
                    UploadStatusText.Text = "Pick a .mzdash file or library entry first.";
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
                    ? $"Upload queued — pushing \"{name}\" to the wheel…"
                    : "Upload not started — wheel not connected or no management session yet.";
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
            // Temporarily neutered: completelyRemove RPC wedges wheel firmware until
            // the wheelbase is power-cycled. Button is also IsEnabled="False" in XAML;
            // this guard is defensive in case the XAML flag is flipped without
            // re-validating the RPC behaviour.
            return;
#pragma warning disable CS0162 // Unreachable code — preserved scaffolding
            if (((Button)sender).Tag is not WheelFileRow row) return;
            if (string.IsNullOrEmpty(row.Id))
            {
                MessageBox.Show($"Cannot delete \"{row.Title}\": wheel did not assign an id.",
                    "Moza", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var confirm = MessageBox.Show(
                $"Delete \"{row.Title}\" from the wheel?\n\nDirName: {row.DirName}\nId: {row.Id}",
                "Moza — confirm delete",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;
            var ts = _plugin?.TelemetrySender;
            if (ts == null)
            {
                MessageBox.Show("Telemetry sender unavailable.",
                    "Moza", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            byte[]? reply = ts.SendRpcCall("completelyRemove", row.Id);
            if (reply == null)
                MessageBox.Show(
                    $"completelyRemove(\"{row.Id}\") timed out. The dashboard may still be present on the wheel.",
                    "Moza", MessageBoxButton.OK, MessageBoxImage.Warning);
#pragma warning restore CS0162
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
                    UploadStatusText.Text = $"Upload complete (bytes_written={bw} == total_size={total}, status=0x{status:X2})";
                else if (UploadStatusText.Text.StartsWith("Upload queued"))
                    UploadStatusText.Text = $"Upload stopped (bytes_written={bw} / total_size={total}, status=0x{status:X2})";
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
            // Preserve grid selection across refresh by DirName key.
            string? prevDir = (WheelFilesGrid.SelectedItem as WheelFileRow)?.DirName;
            WheelFilesGrid.ItemsSource = rows;
            if (!string.IsNullOrEmpty(prevDir))
            {
                foreach (var r in rows)
                    if (r.DirName == prevDir) { WheelFilesGrid.SelectedItem = r; break; }
            }
            if (WheelFilesStatusBox != null)
            {
                if (state == null)
                    WheelFilesStatusBox.Text = "(no configJson state received yet)";
                else
                    WheelFilesStatusBox.Text =
                        $"{rows.Count} dashboards (captured {state.CapturedAt:HH:mm:ss})";
            }
        }
    }
}
