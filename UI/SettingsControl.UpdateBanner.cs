using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MozaPlugin.Resources;
using MozaPlugin.UI;
using MozaPlugin.UI.UpdateCheck;

namespace MozaPlugin
{
    // Partial-class continuation of SettingsControl that owns the in-plugin
    // update-notification surface: the "update available" banner inside the
    // About card, plus the enable toggle, channel selector, "Check now"
    // button, and "last checked" status label below it.
    //
    // Persist-then-render: the background check in MozaPlugin.Init() writes
    // its result into MozaPluginSettings; this partial reads from those
    // settings on construction (via InitUpdateBannerControls) and after every
    // user action. We deliberately do NOT subscribe to a live event from the
    // background check — if the user has the About tab open when an auto-
    // check completes, the banner will update on next open. The "Check now"
    // button is the live, manual path.
    public partial class SettingsControl
    {
        // Guards "Dismiss" — set true when the user clicks Dismiss so the
        // banner stays hidden for the rest of this session even if every
        // condition for showing it still holds. Cleared on plugin reload
        // (because we're a new SettingsControl instance).
        private bool _updateBannerDismissedThisSession;

        // Token source for the in-flight manual "Check now" call. Cancelled
        // on Unload so a slow request doesn't try to update a torn-down UI.
        private CancellationTokenSource? _updateCheckCts;

        // Same idea for the in-flight install (download + extract + swap).
        // Cancelled on Unload — but note that if the cancel lands AFTER the
        // file swap, the install completed and the banner just won't update;
        // RefreshUpdateBannerFromSettings re-detects the .old file next open.
        private CancellationTokenSource? _updateInstallCts;

        private void InitUpdateBannerControls()
        {
            try
            {
                if (UpdateCheckEnabledToggle == null) return; // legacy XAML — nothing to do

                var s = _plugin?.Settings;
                if (s == null) return;

                using (_suppressor.Begin())
                {
                    UpdateCheckEnabledToggle.IsChecked = s.UpdateCheckEnabled;
                    UpdateChannelCombo.SelectedIndex = (int)s.UpdateChannel;
                }

                RefreshUpdateBannerFromSettings();
                RefreshLastCheckedText();
                Unloaded += OnUnloadedCancelUpdateCheck;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[UpdateBanner] init failed: {ex.Message}");
            }
        }

        private void OnUnloadedCancelUpdateCheck(object sender, RoutedEventArgs e)
        {
            try { _updateCheckCts?.Cancel(); } catch { }
            try { _updateInstallCts?.Cancel(); } catch { }
        }

        // An install completed earlier this SimHub session (or in a prior
        // session that wasn't followed by a restart) — the rename-aside
        // dropped a MozaPlugin.dll.old next to the loaded DLL. Until SimHub
        // restarts, we can't safely install again because the .old file is
        // the rollback target for the previous install.
        private static bool IsInstallPending()
        {
            try
            {
                string p = UpdateInstallService.GetPluginAssemblyPath();
                return !string.IsNullOrEmpty(p)
                    && File.Exists(p + UpdateInstallService.OldSuffix);
            }
            catch { return false; }
        }

        // Reads the persisted "last seen" version + skip state and decides
        // whether to show the banner. Safe to call from the UI thread; never
        // touches the network.
        private void RefreshUpdateBannerFromSettings()
        {
            if (UpdateBannerBorder == null) return;

            var s = _plugin?.Settings;
            if (s == null) { UpdateBannerBorder.Visibility = Visibility.Collapsed; return; }

            if (_updateBannerDismissedThisSession || !s.UpdateCheckEnabled)
            {
                UpdateBannerBorder.Visibility = Visibility.Collapsed;
                return;
            }

            string latest = s.LastSeenLatestVersion ?? "";
            if (string.IsNullOrEmpty(latest))
            {
                UpdateBannerBorder.Visibility = Visibility.Collapsed;
                return;
            }

            string current = DiagnosticsTextBuilder.GetPluginVersion();
            if (!UpdateCheckService.IsUpdateAvailable(latest, current, s.UpdateChannel))
            {
                UpdateBannerBorder.Visibility = Visibility.Collapsed;
                return;
            }

            if (!string.IsNullOrEmpty(s.LastSkippedVersion)
                && string.Equals(s.LastSkippedVersion, latest, StringComparison.Ordinal))
            {
                UpdateBannerBorder.Visibility = Visibility.Collapsed;
                return;
            }

            UpdateBannerText.Text = $"{Strings.Label_UpdateAvailable}: v{current} → v{latest}";
            UpdateBannerBorder.Visibility = Visibility.Visible;

            if (IsInstallPending())
            {
                // Previous install completed but SimHub hasn't been restarted
                // yet — show the "restart required" state instead of inviting
                // another install that would fail with OldPending.
                SetBannerState_Installed(latest);
            }
            else
            {
                SetBannerState_Available(hasAsset: !string.IsNullOrEmpty(s.LastSeenAssetUrl));
            }
        }

        // ----- Banner state machine -----

        // Default state: update is available and (optionally) installable.
        // Hides Install when no asset URL is cached (manual hand-cut release
        // or 404 path) so the user can only click "Open release notes".
        private void SetBannerState_Available(bool hasAsset)
        {
            if (UpdateBannerInstallButton != null)
            {
                UpdateBannerInstallButton.Visibility = hasAsset ? Visibility.Visible : Visibility.Collapsed;
                UpdateBannerInstallButton.IsEnabled = true;
            }
            if (UpdateBannerOpenButton != null) UpdateBannerOpenButton.Visibility = Visibility.Visible;
            if (UpdateBannerSkipButton != null)
            {
                UpdateBannerSkipButton.Visibility = Visibility.Visible;
                UpdateBannerSkipButton.IsEnabled = true;
            }
            if (UpdateBannerDismissButton != null)
            {
                UpdateBannerDismissButton.Visibility = Visibility.Visible;
                UpdateBannerDismissButton.IsEnabled = true;
            }
            if (UpdateBannerProgressText != null)
                UpdateBannerProgressText.Visibility = Visibility.Collapsed;
        }

        // Mid-install: download/extract/swap underway. All actions disabled
        // (cancellation only via tab close, which fires the Unloaded handler).
        private void SetBannerState_Installing()
        {
            if (UpdateBannerInstallButton != null) UpdateBannerInstallButton.IsEnabled = false;
            if (UpdateBannerSkipButton != null) UpdateBannerSkipButton.IsEnabled = false;
            if (UpdateBannerDismissButton != null) UpdateBannerDismissButton.IsEnabled = false;
            if (UpdateBannerProgressText != null)
            {
                UpdateBannerProgressText.Visibility = Visibility.Visible;
                UpdateBannerProgressText.Text = Strings.Status_DownloadingStart;
            }
        }

        // Install succeeded — DLL is swapped, restart required. Hide the
        // Install + Skip buttons (re-installing would just fail); keep
        // Open release notes + Dismiss for navigation.
        private void SetBannerState_Installed(string version)
        {
            if (UpdateBannerInstallButton != null) UpdateBannerInstallButton.Visibility = Visibility.Collapsed;
            if (UpdateBannerSkipButton != null) UpdateBannerSkipButton.Visibility = Visibility.Collapsed;
            if (UpdateBannerOpenButton != null) UpdateBannerOpenButton.Visibility = Visibility.Visible;
            if (UpdateBannerDismissButton != null)
            {
                UpdateBannerDismissButton.Visibility = Visibility.Visible;
                UpdateBannerDismissButton.IsEnabled = true;
            }
            if (UpdateBannerProgressText != null)
            {
                UpdateBannerProgressText.Visibility = Visibility.Visible;
                UpdateBannerProgressText.Text = string.Format(
                    Strings.Status_InstalledRestartRequired, version);
            }
        }

        // Install failed — re-enable actions and show what went wrong.
        // Cancellation just clears the progress line silently.
        private void SetBannerState_Failed(InstallErrorKind kind, string detail)
        {
            if (UpdateBannerInstallButton != null) UpdateBannerInstallButton.IsEnabled = true;
            if (UpdateBannerSkipButton != null) UpdateBannerSkipButton.IsEnabled = true;
            if (UpdateBannerDismissButton != null) UpdateBannerDismissButton.IsEnabled = true;
            if (UpdateBannerProgressText == null) return;

            if (kind == InstallErrorKind.Cancelled)
            {
                UpdateBannerProgressText.Visibility = Visibility.Collapsed;
                return;
            }

            string desc;
            switch (kind)
            {
                case InstallErrorKind.Network:
                case InstallErrorKind.Http:
                    desc = Strings.Status_UpdateFailedNetwork;
                    break;
                case InstallErrorKind.ZipMalformed:
                case InstallErrorKind.Validation:
                    desc = Strings.Status_InstallFailedBadPackage;
                    break;
                case InstallErrorKind.OldPending:
                    desc = Strings.Status_InstallFailedPendingRestart;
                    break;
                case InstallErrorKind.WriteFailed:
                    desc = Strings.Status_InstallFailedWriteDenied;
                    break;
                default:
                    desc = string.IsNullOrEmpty(detail) ? "unknown" : detail;
                    break;
            }
            UpdateBannerProgressText.Visibility = Visibility.Visible;
            UpdateBannerProgressText.Text = string.Format(Strings.Status_InstallFailed, desc);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F0") + " KB";
            return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
        }

        // Updates the "last checked" status line. Uses the same persisted
        // DateTime the throttle logic in MozaPlugin.Init() reads from, so the
        // UI never disagrees with the actual check cadence.
        private void RefreshLastCheckedText()
        {
            if (UpdateLastCheckedText == null) return;
            var s = _plugin?.Settings;
            if (s == null) { UpdateLastCheckedText.Text = ""; return; }

            if (s.LastUpdateCheckUtc == DateTime.MinValue)
            {
                UpdateLastCheckedText.Text = Strings.Status_UpdateNeverChecked;
                return;
            }

            // Render in the user's local time — they're looking at "when did
            // I last check?" through their own clock, not UTC.
            var local = s.LastUpdateCheckUtc.ToLocalTime();
            UpdateLastCheckedText.Text = local.ToString("yyyy-MM-dd HH:mm");
        }

        // ----- Banner button handlers -----

        private void UpdateBanner_OpenNotes_Click(object sender, RoutedEventArgs e)
        {
            var s = _plugin?.Settings;
            string url = s?.LastSeenReleaseUrl ?? "";
            if (string.IsNullOrEmpty(url))
            {
                // Fall back to the repo Releases page if the html_url wasn't captured.
                url = "https://github.com/giantorth/moza-simhub-plugin/releases";
            }
            OpenExternalUrl(url);
        }

        private void UpdateBanner_Skip_Click(object sender, RoutedEventArgs e)
        {
            var s = _plugin?.Settings;
            if (s == null) return;
            s.LastSkippedVersion = s.LastSeenLatestVersion ?? "";
            try { _plugin!.PersistSettings(); } catch { /* persistence is best-effort */ }
            RefreshUpdateBannerFromSettings();
        }

        private void UpdateBanner_Dismiss_Click(object sender, RoutedEventArgs e)
        {
            _updateBannerDismissedThisSession = true;
            RefreshUpdateBannerFromSettings();
        }

        // ----- Settings handlers -----

        private void UpdateCheckEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = _plugin?.Settings;
            if (s == null) return;
            s.UpdateCheckEnabled = UpdateCheckEnabledToggle?.IsChecked == true;
            try { _plugin!.PersistSettings(); } catch { }
            RefreshUpdateBannerFromSettings();
        }

        private void UpdateChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = _plugin?.Settings;
            if (s == null || UpdateChannelCombo == null) return;
            int idx = UpdateChannelCombo.SelectedIndex;
            if (idx < 0) return;
            var picked = idx == (int)UpdateChannel.Dev ? UpdateChannel.Dev : UpdateChannel.Stable;
            if (picked == s.UpdateChannel) return;

            s.UpdateChannel = picked;
            // Channel switch invalidates the cached "last seen" — what was the
            // newest stable release isn't necessarily comparable to the newest
            // dev build (and vice versa). Clear so the next check repopulates.
            s.LastSeenLatestVersion = "";
            s.LastSeenReleaseUrl = "";
            s.LastSeenAssetUrl = "";
            s.LastSkippedVersion = "";
            try { _plugin!.PersistSettings(); } catch { }
            RefreshUpdateBannerFromSettings();
        }

        private async void UpdateCheckNow_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin?.Settings == null || UpdateCheckNowButton == null) return;
            var s = _plugin.Settings;

            // Cancel any in-flight manual check before kicking off a new one.
            try { _updateCheckCts?.Cancel(); } catch { }
            _updateCheckCts = new CancellationTokenSource();
            var ct = _updateCheckCts.Token;

            UpdateCheckNowButton.IsEnabled = false;
            if (UpdateLastCheckedText != null)
                UpdateLastCheckedText.Text = Strings.Status_UpdateChecking;

            UpdateCheckResult result;
            try
            {
                result = await Task.Run(
                    () => UpdateCheckService.CheckAsync(s.UpdateChannel, ct),
                    ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return; // unloaded mid-check; nothing to update
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[UpdateBanner] manual check threw: {ex.Message}");
                if (UpdateLastCheckedText != null)
                    UpdateLastCheckedText.Text = Strings.Status_UpdateFailedNetwork;
                if (UpdateCheckNowButton != null) UpdateCheckNowButton.IsEnabled = true;
                return;
            }

            // Persist the timestamp regardless of outcome so the throttle
            // logic doesn't refire on every plugin Init right after a failed
            // manual check.
            s.LastUpdateCheckUtc = DateTime.UtcNow;

            if (result.Success)
            {
                if (!string.IsNullOrEmpty(result.LatestVersion))
                {
                    s.LastSeenLatestVersion = result.LatestVersion;
                    s.LastSeenReleaseUrl = result.ReleaseUrl;
                    s.LastSeenAssetUrl = result.AssetUrl;
                }
                // result.Success with empty LatestVersion = 404 on dev-latest
                // (no dev release published yet). Leave cached values alone
                // so a previous stable-channel result doesn't get erased.

                try { _plugin.PersistSettings(); } catch { }
                RefreshUpdateBannerFromSettings();

                if (UpdateLastCheckedText != null)
                {
                    // Show explicit "up to date" when there's no newer version
                    // available; otherwise show the timestamp.
                    string current = DiagnosticsTextBuilder.GetPluginVersion();
                    bool upToDate = string.IsNullOrEmpty(result.LatestVersion)
                        || !UpdateCheckService.IsUpdateAvailable(
                            result.LatestVersion, current, s.UpdateChannel);
                    if (upToDate)
                        UpdateLastCheckedText.Text = Strings.Status_UpdateUpToDate;
                    else
                        RefreshLastCheckedText();
                }
            }
            else
            {
                try { _plugin.PersistSettings(); } catch { }
                if (UpdateLastCheckedText != null)
                {
                    switch (result.ErrorKind)
                    {
                        case UpdateCheckErrorKind.Http:
                            UpdateLastCheckedText.Text = Strings.Status_UpdateFailedHttp;
                            break;
                        case UpdateCheckErrorKind.Parse:
                            UpdateLastCheckedText.Text = Strings.Status_UpdateFailedParse;
                            break;
                        default:
                            UpdateLastCheckedText.Text = Strings.Status_UpdateFailedNetwork;
                            break;
                    }
                }
            }

            if (UpdateCheckNowButton != null) UpdateCheckNowButton.IsEnabled = true;
        }

        // ----- Install flow -----

        private async void UpdateBanner_Install_Click(object sender, RoutedEventArgs e)
        {
            var s = _plugin?.Settings;
            if (s == null) return;
            if (string.IsNullOrEmpty(s.LastSeenAssetUrl)) return;

            // Defensive: if a previous install is still pending the swap
            // would fail with OldPending. Surface the restart-required UI
            // instead of even attempting the network call.
            if (IsInstallPending())
            {
                SetBannerState_Installed(s.LastSeenLatestVersion ?? "");
                return;
            }

            try { _updateInstallCts?.Cancel(); } catch { }
            _updateInstallCts = new CancellationTokenSource();
            var ct = _updateInstallCts.Token;

            SetBannerState_Installing();

            // Progress is delivered on the Task scheduler; Progress<T>
            // captures the originating SynchronizationContext (WPF dispatcher)
            // so the callback marshals back to the UI thread automatically.
            var progress = new Progress<InstallProgress>(OnInstallProgress);

            InstallResult result;
            try
            {
                result = await Task.Run(
                    () => UpdateInstallService.DownloadAndInstallAsync(
                        s.LastSeenAssetUrl,
                        UpdateCheckService.Http,
                        progress,
                        ct),
                    ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                SetBannerState_Failed(InstallErrorKind.Cancelled, "");
                return;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[UpdateInstall] threw: {ex.GetType().Name}: {ex.Message}");
                SetBannerState_Failed(InstallErrorKind.Unknown, ex.Message);
                return;
            }

            if (result.Success)
            {
                MozaLog.Info($"[UpdateInstall] installed v{s.LastSeenLatestVersion} — restart required");
                SetBannerState_Installed(s.LastSeenLatestVersion ?? "");
            }
            else
            {
                MozaLog.Warn($"[UpdateInstall] failed: {result.ErrorKind} {result.ErrorMessage}");
                SetBannerState_Failed(result.ErrorKind, result.ErrorMessage);
            }
        }

        private void OnInstallProgress(InstallProgress p)
        {
            if (UpdateBannerProgressText == null) return;
            switch (p.Phase)
            {
                case InstallPhase.Downloading:
                    if (p.TotalBytes <= 0)
                    {
                        UpdateBannerProgressText.Text = string.Format(
                            Strings.Status_DownloadingIndeterminate,
                            FormatBytes(p.BytesDownloaded));
                    }
                    else
                    {
                        int pct = (int)Math.Round(p.Fraction * 100);
                        UpdateBannerProgressText.Text = string.Format(
                            Strings.Status_Downloading,
                            pct,
                            FormatBytes(p.BytesDownloaded),
                            FormatBytes(p.TotalBytes));
                    }
                    break;
                case InstallPhase.Extracting:
                    UpdateBannerProgressText.Text = Strings.Status_Extracting;
                    break;
                case InstallPhase.Installing:
                    UpdateBannerProgressText.Text = Strings.Status_Installing;
                    break;
                case InstallPhase.Done:
                    // Final UI state is set by SetBannerState_Installed after
                    // the await completes — no-op here.
                    break;
            }
        }
    }
}
