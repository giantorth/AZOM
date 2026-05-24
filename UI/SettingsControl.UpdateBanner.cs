using System;
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
            if (UpdateCheckService.CompareSemVer(latest, current) <= 0)
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
                        || UpdateCheckService.CompareSemVer(result.LatestVersion, current) <= 0;
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
    }
}
