using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MozaPlugin.Resources;
using MozaPlugin.UI.UpdateCheck;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Unified, self-contained banner stack shown at the top of the plugin pane
    /// and every SimHub device page: status hints + update notification + SDK
    /// nudge. Resolves <see cref="MozaPlugin.Instance"/> and repaints itself on a
    /// 500 ms tick (only while loaded), so hosts just drop the tag in — no wiring
    /// required beyond the two optional in-app navigation delegates below.
    ///
    /// The update install is driven through the session-scoped <see
    /// cref="UpdateInstallCoordinator"/> so this control and the plugin pane's
    /// About-tab "Updates" card share one install flow + one dismiss/in-progress
    /// state and never diverge.
    /// </summary>
    public partial class PluginBanners : UserControl
    {
        private readonly DispatcherTimer _timer;
        private IReadOnlyList<StatusHint>? _lastHints;
        private bool _coordinatorHooked;

        /// <summary>
        /// Optional in-app navigation for "Open release notes" (plugin pane:
        /// switch to About &gt; Updates). Null on device pages → the button opens
        /// the GitHub release URL in the browser instead.
        /// </summary>
        public Action? OpenReleaseNotesInApp { get; set; }

        /// <summary>
        /// Optional in-app navigation for the SDK nudge's "Configure" button
        /// (plugin pane: switch to the Options tab, where the SDK toggles live).
        /// Null on device pages → the Configure button is hidden (there is no
        /// such tab to reach from a device
        /// page); Dismiss still works and persists.
        /// </summary>
        public Action? ConfigureSdkInApp { get; set; }

        public PluginBanners()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, __) => Refresh();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_coordinatorHooked)
            {
                UpdateInstallCoordinator.Instance.Progress += OnInstallProgress;
                UpdateInstallCoordinator.Instance.Completed += OnInstallCompleted;
                _coordinatorHooked = true;
            }
            Refresh();
            if (!_timer.IsEnabled) _timer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            // Unsubscribe (a device page may unload/reload), but do NOT cancel an
            // in-flight install — the coordinator runs it to completion regardless
            // of which UI is showing.
            if (_coordinatorHooked)
            {
                UpdateInstallCoordinator.Instance.Progress -= OnInstallProgress;
                UpdateInstallCoordinator.Instance.Completed -= OnInstallCompleted;
                _coordinatorHooked = false;
            }
        }

        private void Refresh()
        {
            RefreshHints();
            // Skip the update repaint mid-install so it doesn't clobber the live
            // progress line (progress + completion are event-driven instead).
            if (!UpdateInstallCoordinator.Instance.InstallInProgress) RefreshUpdateBanner();
            RefreshSdkPrompt();
            RefreshLegacyLfeBanner();
        }

        // ===== 1. Status hints =====

        private void RefreshHints()
        {
            var plugin = MozaPlugin.Instance;
            IReadOnlyList<StatusHint> hints = plugin == null
                ? Array.Empty<StatusHint>()
                : StatusHintBuilder.Build(plugin, DateTime.UtcNow);
            if (!StatusHint.ListEquals(_lastHints, hints))
            {
                HintList.ItemsSource = hints;
                _lastHints = hints;
            }
        }

        // Restart button on the device-definition-deployed banner. It lives inside
        // the ItemsControl template (no x:Name), so disable the clicked button
        // directly to block a double-fire, re-enabling only if the exit request
        // couldn't be issued.
        private void HintRestart_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null) button.IsEnabled = false;
            bool ok = MozaPlugin.Instance?.RestartSimHub() ?? false;
            if (!ok && button != null) button.IsEnabled = true;
        }

        // ===== 2. Update banner =====

        private void RefreshUpdateBanner()
        {
            var s = MozaPlugin.Instance?.Settings;
            string current = DiagnosticsTextBuilder.GetPluginVersion();
            string latest = s?.LastSeenLatestVersion ?? "";
            UpdateInstallCoordinator.Instance.Compute(
                s, current, latest, out bool visible, out bool pendingRestart, out bool hasAsset);
            PaintHeaderBanner(visible, pendingRestart, hasAsset, current, latest);
        }

        private void PaintHeaderBanner(
            bool visible, bool pendingRestart, bool hasAsset, string current, string latest)
        {
            if (HeaderUpdateBanner == null) return;
            if (!visible) { HeaderUpdateBanner.Visibility = Visibility.Collapsed; return; }

            HeaderUpdateBanner.Visibility = Visibility.Visible;
            if (HeaderUpdateBannerProgressText != null)
                HeaderUpdateBannerProgressText.Visibility = Visibility.Collapsed;

            if (pendingRestart)
            {
                if (HeaderUpdateBannerText != null)
                    HeaderUpdateBannerText.Text = string.Format(
                        Strings.Status_InstalledRestartRequired, latest);
                if (HeaderUpdateInstallButton != null)
                    HeaderUpdateInstallButton.Visibility = Visibility.Collapsed;
                if (HeaderUpdateRestartButton != null)
                {
                    HeaderUpdateRestartButton.Visibility = Visibility.Visible;
                    HeaderUpdateRestartButton.IsEnabled = true;
                }
            }
            else
            {
                if (HeaderUpdateBannerText != null)
                    HeaderUpdateBannerText.Text = $"{Strings.Label_UpdateAvailable}: v{current} → v{latest}";
                if (HeaderUpdateInstallButton != null)
                {
                    HeaderUpdateInstallButton.Visibility = hasAsset ? Visibility.Visible : Visibility.Collapsed;
                    HeaderUpdateInstallButton.IsEnabled = true;
                }
                if (HeaderUpdateRestartButton != null)
                    HeaderUpdateRestartButton.Visibility = Visibility.Collapsed;
            }
        }

        // Disable Install + show an indeterminate progress line while the
        // download/extract/swap runs.
        private void SetHeaderState_Installing()
        {
            if (HeaderUpdateBanner == null) return;
            HeaderUpdateBanner.Visibility = Visibility.Visible;
            if (HeaderUpdateInstallButton != null) HeaderUpdateInstallButton.IsEnabled = false;
            if (HeaderUpdateRestartButton != null) HeaderUpdateRestartButton.Visibility = Visibility.Collapsed;
            if (HeaderUpdateBannerProgressText != null)
            {
                HeaderUpdateBannerProgressText.Visibility = Visibility.Visible;
                HeaderUpdateBannerProgressText.Text = Strings.Status_DownloadingStart;
            }
        }

        private async void HeaderUpdateInstall_Click(object sender, RoutedEventArgs e)
        {
            var s = MozaPlugin.Instance?.Settings;
            if (s == null || string.IsNullOrEmpty(s.LastSeenAssetUrl)) return;
            if (UpdateInstallCoordinator.Instance.InstallInProgress) return;
            SetHeaderState_Installing();
            await UpdateInstallCoordinator.Instance.RunInstallAsync(s);
        }

        private void HeaderUpdateRestart_Click(object sender, RoutedEventArgs e)
        {
            if (HeaderUpdateRestartButton != null) HeaderUpdateRestartButton.IsEnabled = false;
            bool ok = MozaPlugin.Instance?.RestartSimHub() ?? false;
            if (!ok && HeaderUpdateRestartButton != null) HeaderUpdateRestartButton.IsEnabled = true;
        }

        private void HeaderUpdateNotes_Click(object sender, RoutedEventArgs e) => OpenReleaseNotes();

        private void HeaderUpdateDismiss_Click(object sender, RoutedEventArgs e)
        {
            // One dismiss flag hides the banner on every surface for the session.
            UpdateInstallCoordinator.Instance.DismissedThisSession = true;
            RefreshUpdateBanner();
        }

        // In-app navigation when hosted on the plugin pane; else open the release
        // page in the browser.
        private void OpenReleaseNotes()
        {
            if (OpenReleaseNotesInApp != null)
            {
                try { OpenReleaseNotesInApp(); return; }
                catch (Exception ex) { MozaLog.Debug($"[PluginBanners] in-app notes nav failed: {ex.Message}"); }
            }

            string url = MozaPlugin.Instance?.Settings?.LastSeenReleaseUrl ?? "";
            if (string.IsNullOrEmpty(url))
                url = "https://github.com/giantorth/AZOM/releases";
            OpenExternalUrl(url);
        }

        // ----- install progress / completion (from the coordinator, on UI thread) -----

        private void OnInstallProgress(InstallProgress p)
        {
            if (HeaderUpdateBannerProgressText == null) return;
            string? text;
            switch (p.Phase)
            {
                case InstallPhase.Downloading:
                    text = p.TotalBytes <= 0
                        ? string.Format(Strings.Status_DownloadingIndeterminate, FormatBytes(p.BytesDownloaded))
                        : string.Format(
                            Strings.Status_Downloading,
                            (int)Math.Round(p.Fraction * 100),
                            FormatBytes(p.BytesDownloaded),
                            FormatBytes(p.TotalBytes));
                    break;
                case InstallPhase.Extracting:
                    text = Strings.Status_Extracting;
                    break;
                case InstallPhase.Installing:
                    text = Strings.Status_Installing;
                    break;
                default:
                    return; // Done/Idle/Failed — final state set by OnInstallCompleted
            }
            HeaderUpdateBannerProgressText.Visibility = Visibility.Visible;
            HeaderUpdateBannerProgressText.Text = text;
        }

        // The header never surfaces install errors: on success it flips to the
        // pending-restart state (the .old probe now returns true), on cancel /
        // failure it falls back to the available state. A plain repaint does both.
        private void OnInstallCompleted(InstallResult r) => RefreshUpdateBanner();

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F0") + " KB";
            return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
        }

        // ===== 3. SDK nudge =====

        private void RefreshSdkPrompt()
        {
            var s = MozaPlugin.Instance?.Settings;
            bool show = s != null && !s.SdkEmulationEnabled && !s.SdkPromptDismissed;
            SdkPromptBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            // Configure can only reach the Options tab on the plugin pane.
            if (SdkPromptConfigureButton != null)
                SdkPromptConfigureButton.Visibility =
                    ConfigureSdkInApp != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SdkPromptConfigure_Click(object sender, RoutedEventArgs e)
        {
            try { ConfigureSdkInApp?.Invoke(); }
            catch (Exception ex) { MozaLog.Debug($"[PluginBanners] SDK nav failed: {ex.Message}"); }
            DismissSdkPrompt(); // both buttons dismiss permanently
        }

        private void SdkPromptDismiss_Click(object sender, RoutedEventArgs e) => DismissSdkPrompt();

        private void DismissSdkPrompt()
        {
            var plugin = MozaPlugin.Instance;
            var s = plugin?.Settings;
            if (s != null && !s.SdkPromptDismissed)
            {
                s.SdkPromptDismissed = true;
                try { plugin!.PersistSettings(); } catch { /* best-effort */ }
            }
            RefreshSdkPrompt();
        }

        // ===== 4. Pre-1.6 wheelbase haptics migration =====

        // Stays up across the restart AND the "add the model-named device" step, so
        // it is persisted rather than session-scoped. Cleared by the import itself
        // once the settings actually land on the new device.
        private void RefreshLegacyLfeBanner()
        {
            var s = MozaPlugin.Instance?.Settings;
            LegacyLfeBanner.Visibility =
                s != null && s.LegacyLfeMigrationPending ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LegacyLfeDismiss_Click(object sender, RoutedEventArgs e)
        {
            var plugin = MozaPlugin.Instance;
            var s = plugin?.Settings;
            if (s != null && s.LegacyLfeMigrationPending)
            {
                s.LegacyLfeMigrationPending = false;
                try { plugin!.PersistSettings(); } catch { /* best-effort */ }
            }
            RefreshLegacyLfeBanner();
        }

        // Open a URL via the OS shell (browser on Windows; winebrowser → xdg-open
        // under Wine/Proton).
        private static void OpenExternalUrl(string url)
        {
            try
            {
                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                })) { }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[PluginBanners] failed to open {url}: {ex.Message}");
            }
        }
    }
}
