using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
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
        // RefreshUpdateNotifications re-detects the .old file next open.
        private CancellationTokenSource? _updateInstallCts;

        // True while a download+install is running. Gates the banner repaint
        // paths (the 500ms tick and RefreshUpdateNotifications) so they don't
        // clobber the live "Downloading…/Installing…" progress UI mid-flight.
        private bool _installInProgress;

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

                RefreshUpdateNotifications();
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

        // Computes whether an update notification should be shown right now and
        // in which mode. Pure read of persisted state + the .old pending-file
        // probe; never touches the network. `pendingRestart` means an install
        // already completed this/last session and SimHub must restart to load
        // it — in that mode we never offer another install (it would fail with
        // OldPending) and instead surface the Restart button.
        private void ComputeUpdateVisibility(
            string current, string latest,
            out bool visible, out bool pendingRestart, out bool hasAsset)
        {
            visible = false;
            pendingRestart = false;
            hasAsset = false;

            var s = _plugin?.Settings;
            if (s == null) return;
            if (_updateBannerDismissedThisSession || !s.UpdateCheckEnabled) return;
            if (string.IsNullOrEmpty(latest)) return;
            if (!UpdateCheckService.IsUpdateAvailable(latest, current, s.UpdateChannel)) return;
            if (!string.IsNullOrEmpty(s.LastSkippedVersion)
                && string.Equals(s.LastSkippedVersion, latest, StringComparison.Ordinal)) return;

            visible = true;
            hasAsset = !string.IsNullOrEmpty(s.LastSeenAssetUrl);
            pendingRestart = IsInstallPending();
        }

        // Full repaint of every update surface: the cross-tab header banner,
        // the About > Updates card banner, and the release-notes panel. Called
        // on construction and after every user action / check / install. Safe
        // on the UI thread; no-ops while an install is mid-flight so it doesn't
        // wipe the live progress UI.
        internal void RefreshUpdateNotifications()
        {
            if (_installInProgress) return;

            string current = DiagnosticsTextBuilder.GetPluginVersion();
            string latest = _plugin?.Settings?.LastSeenLatestVersion ?? "";
            ComputeUpdateVisibility(current, latest, out bool visible, out bool pendingRestart, out bool hasAsset);

            PaintAboutBanner(visible, pendingRestart, hasAsset, current, latest);
            PaintHeaderBanner(visible, pendingRestart, hasAsset, current, latest);
            RefreshReleaseNotes(visible, latest);
        }

        // Repaints ONLY the header banner. Driven by the 500ms RefreshDisplay
        // tick so the cross-tab notification appears/updates live (e.g. when
        // the background auto-check completes while the user is on another
        // tab). The heavier About-card banner + notes stay on the
        // construct/user-action cadence to keep transient error text readable.
        internal void RefreshHeaderBanner()
        {
            if (_installInProgress) return;

            string current = DiagnosticsTextBuilder.GetPluginVersion();
            string latest = _plugin?.Settings?.LastSeenLatestVersion ?? "";
            ComputeUpdateVisibility(current, latest, out bool visible, out bool pendingRestart, out bool hasAsset);
            PaintHeaderBanner(visible, pendingRestart, hasAsset, current, latest);
        }

        // Guards the firmware-warning "Dismiss" for this session. Unlike the
        // update banner this auto-rearms: if the storm clears the dismiss is
        // dropped, so a fresh storm later in the session shows the warning again.
        private bool _firmwareWarningDismissedThisSession;

        // Repaints the wheel firmware-error warning banner. Driven by the 500ms
        // RefreshDisplay tick alongside RefreshHeaderBanner. Shows whenever the
        // wheel is mid param-read storm (the detector in FirmwareDebugLog) and
        // the user hasn't dismissed the current burst.
        internal void RefreshFirmwareWarningBanner()
        {
            if (HeaderFirmwareWarningBanner == null) return;

            bool storm = _plugin?.WheelParamStormActive ?? false;
            if (!storm)
            {
                // Storm gone — rearm so the next one is announced.
                _firmwareWarningDismissedThisSession = false;
                HeaderFirmwareWarningBanner.Visibility = Visibility.Collapsed;
                return;
            }

            HeaderFirmwareWarningBanner.Visibility =
                _firmwareWarningDismissedThisSession ? Visibility.Collapsed : Visibility.Visible;
        }

        private void HeaderFirmwareCapture_Click(object sender, RoutedEventArgs e)
        {
            // Take the user to the serial-capture section (About tab) so they can
            // enable capture and grab the wheel traffic for us. Navigation only —
            // the user starts/stops + exports from the existing controls there.
            try
            {
                if (MainTabs != null && AboutTab != null)
                    MainTabs.SelectedItem = AboutTab;
                // Scroll the capture controls into view once the tab has laid out.
                Dispatcher.BeginInvoke(
                    new Action(() => { try { SerialCaptureToggleButton?.BringIntoView(); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[AZOM] Firmware-warning navigate failed: {ex}");
            }
        }

        private void HeaderFirmwareDismiss_Click(object sender, RoutedEventArgs e)
        {
            _firmwareWarningDismissedThisSession = true;
            if (HeaderFirmwareWarningBanner != null)
                HeaderFirmwareWarningBanner.Visibility = Visibility.Collapsed;
        }

        private void PaintAboutBanner(
            bool visible, bool pendingRestart, bool hasAsset, string current, string latest)
        {
            if (UpdateBannerBorder == null) return;
            if (!visible) { UpdateBannerBorder.Visibility = Visibility.Collapsed; return; }

            UpdateBannerBorder.Visibility = Visibility.Visible;
            if (pendingRestart)
            {
                SetBannerState_Installed(latest);
            }
            else
            {
                if (UpdateBannerText != null)
                    UpdateBannerText.Text = $"{Strings.Label_UpdateAvailable}: v{current} → v{latest}";
                SetBannerState_Available(hasAsset);
            }
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

        // The markdown source currently rendered into the RichTextBox, so we
        // only rebuild the FlowDocument when the notes actually change (avoids
        // resetting scroll/selection on every unrelated banner repaint).
        private string? _renderedReleaseNotes;

        // Populates the About-card "What's new in vX" panel from the cached
        // release body. Hidden when there's nothing to show or no active
        // notification.
        private void RefreshReleaseNotes(bool visible, string latest)
        {
            if (UpdateReleaseNotesPanel == null) return;

            string notes = _plugin?.Settings?.LastSeenReleaseNotes ?? "";
            if (!visible || string.IsNullOrWhiteSpace(notes))
            {
                UpdateReleaseNotesPanel.Visibility = Visibility.Collapsed;
                return;
            }

            if (UpdateReleaseNotesHeader != null)
                UpdateReleaseNotesHeader.Text = string.Format(Strings.Label_WhatsNew, latest);
            if (UpdateReleaseNotesBox != null
                && !string.Equals(_renderedReleaseNotes, notes, StringComparison.Ordinal))
            {
                UpdateReleaseNotesBox.Document = BuildReleaseNotesDocument(notes);
                _renderedReleaseNotes = notes;
            }
            UpdateReleaseNotesPanel.Visibility = Visibility.Visible;
        }

        // ----- Lightweight markdown → FlowDocument renderer -----
        //
        // GitHub release bodies (especially the auto-generated "What's Changed"
        // list) use a small, predictable subset of markdown: ATX headings,
        // '-'/'*'/'+' bullets, '1.' ordered items, **bold**, `inline code`,
        // [label](url) links, and bare URLs. We render exactly that subset —
        // a full CommonMark engine would be overkill for a changelog blurb and
        // would mean pulling in a dependency.

        private static readonly Regex s_inlineRx = new Regex(
            @"(?<code>`[^`]+`)" +
            @"|(?<bold>\*\*[^*]+\*\*|__[^_]+__)" +
            @"|(?<link>\[[^\]]+\]\([^)\s]+\))" +
            @"|(?<url>https?://[^\s)]+)",
            RegexOptions.Compiled);

        private FlowDocument BuildReleaseNotesDocument(string md)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                TextAlignment = TextAlignment.Left,
            };
            if (UpdateReleaseNotesBox != null)
            {
                doc.FontFamily = UpdateReleaseNotesBox.FontFamily;
                doc.FontSize = UpdateReleaseNotesBox.FontSize;
            }
            double baseSize = doc.FontSize > 0 ? doc.FontSize : 12.0;

            if (string.IsNullOrEmpty(md)) return doc;

            var lines = md.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            foreach (var rawLine in lines)
            {
                string trimmed = rawLine.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed == "---" || trimmed == "***" || trimmed == "___") continue;

                // Heading (#, ##, ###)
                if (trimmed[0] == '#')
                {
                    int h = 0;
                    while (h < trimmed.Length && trimmed[h] == '#') h++;
                    string htext = trimmed.Substring(h).TrimStart();
                    var hp = new Paragraph { Margin = new Thickness(0, 8, 0, 4), FontWeight = FontWeights.Bold };
                    hp.FontSize = baseSize + (h <= 1 ? 3 : h == 2 ? 2 : 1);
                    AppendInlines(hp, htext);
                    doc.Blocks.Add(hp);
                    continue;
                }

                // Unordered bullet
                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
                {
                    var bp = new Paragraph { Margin = new Thickness(12, 1, 0, 1), TextIndent = -10 };
                    bp.Inlines.Add(new Run("•  "));
                    AppendInlines(bp, trimmed.Substring(2));
                    doc.Blocks.Add(bp);
                    continue;
                }

                // Ordered item (1. ...)
                var om = Regex.Match(trimmed, @"^(\d+)\.\s+(.*)$");
                if (om.Success)
                {
                    var op = new Paragraph { Margin = new Thickness(12, 1, 0, 1), TextIndent = -14 };
                    op.Inlines.Add(new Run(om.Groups[1].Value + ".  "));
                    AppendInlines(op, om.Groups[2].Value);
                    doc.Blocks.Add(op);
                    continue;
                }

                // Plain paragraph
                var p = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                AppendInlines(p, trimmed);
                doc.Blocks.Add(p);
            }

            return doc;
        }

        // Splits a single line into styled inlines (plain text + bold + code +
        // links). Unmatched text is emitted verbatim.
        private void AppendInlines(Paragraph p, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int pos = 0;
            foreach (Match m in s_inlineRx.Matches(text))
            {
                if (m.Index > pos)
                    p.Inlines.Add(new Run(text.Substring(pos, m.Index - pos)));

                if (m.Groups["code"].Success)
                {
                    string code = m.Value.Substring(1, m.Value.Length - 2);
                    p.Inlines.Add(new Run(code)
                    {
                        FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New, monospace"),
                    });
                }
                else if (m.Groups["bold"].Success)
                {
                    p.Inlines.Add(new Bold(new Run(m.Value.Substring(2, m.Value.Length - 4))));
                }
                else if (m.Groups["link"].Success)
                {
                    var lm = Regex.Match(m.Value, @"^\[([^\]]+)\]\(([^)\s]+)\)$");
                    AddHyperlink(p, lm.Groups[1].Value, lm.Groups[2].Value);
                }
                else if (m.Groups["url"].Success)
                {
                    AddHyperlink(p, m.Value, m.Value);
                }

                pos = m.Index + m.Length;
            }
            if (pos < text.Length)
                p.Inlines.Add(new Run(text.Substring(pos)));
        }

        private void AddHyperlink(Paragraph p, string label, string url)
        {
            try
            {
                var link = new Hyperlink(new Run(label)) { NavigateUri = new Uri(url) };
                link.RequestNavigate += ReleaseNotesLink_RequestNavigate;
                if (TryFindResource("CyanBrush") is System.Windows.Media.Brush brush)
                    link.Foreground = brush;
                p.Inlines.Add(link);
            }
            catch
            {
                // Malformed URL — fall back to plain label text.
                p.Inlines.Add(new Run(label));
            }
        }

        private void ReleaseNotesLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            OpenExternalUrl(e.Uri?.ToString() ?? "");
            e.Handled = true;
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
            if (UpdateBannerRestartButton != null)
                UpdateBannerRestartButton.Visibility = Visibility.Collapsed;
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

        // Install succeeded — DLL is swapped, restart required. Replace the
        // "update available" headline with the restart prompt (so the stale
        // "new update" status is cleared the moment the install lands), hide
        // Install + Skip (re-installing would just fail), and surface the
        // one-click Restart button. Open release notes + Dismiss stay for
        // navigation.
        private void SetBannerState_Installed(string version)
        {
            if (UpdateBannerText != null)
                UpdateBannerText.Text = string.Format(
                    Strings.Status_InstalledRestartRequired, version);
            if (UpdateBannerInstallButton != null) UpdateBannerInstallButton.Visibility = Visibility.Collapsed;
            if (UpdateBannerSkipButton != null) UpdateBannerSkipButton.Visibility = Visibility.Collapsed;
            if (UpdateBannerRestartButton != null)
            {
                UpdateBannerRestartButton.Visibility = Visibility.Visible;
                UpdateBannerRestartButton.IsEnabled = true;
            }
            if (UpdateBannerOpenButton != null) UpdateBannerOpenButton.Visibility = Visibility.Visible;
            if (UpdateBannerDismissButton != null)
            {
                UpdateBannerDismissButton.Visibility = Visibility.Visible;
                UpdateBannerDismissButton.IsEnabled = true;
            }
            if (UpdateBannerProgressText != null)
                UpdateBannerProgressText.Visibility = Visibility.Collapsed;
        }

        // Header-banner equivalent of SetBannerState_Installing: disable the
        // Install button and show an indeterminate progress line while the
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

        private void UpdateBanner_OpenNotes_Click(object sender, RoutedEventArgs e) => OpenReleaseNotes();

        // Prefer the in-app "What's new" panel so the user doesn't have to leave
        // SimHub: switch to the About tab and scroll the Updates section into
        // view. Only falls back to the GitHub release page when we have no
        // embedded notes to show (e.g. a hand-cut release with an empty body).
        private void OpenReleaseNotes()
        {
            string notes = _plugin?.Settings?.LastSeenReleaseNotes ?? "";
            if (string.IsNullOrWhiteSpace(notes))
            {
                string url = _plugin?.Settings?.LastSeenReleaseUrl ?? "";
                if (string.IsNullOrEmpty(url))
                    url = "https://github.com/giantorth/moza-simhub-plugin/releases";
                OpenExternalUrl(url);
                return;
            }

            try
            {
                if (MainTabs != null && AboutTab != null)
                    MainTabs.SelectedItem = AboutTab;
                // Defer the scroll until the About tab's content is realized.
                Dispatcher.BeginInvoke(
                    new Action(() => { try { UpdatesSection?.BringIntoView(); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[UpdateBanner] navigate to notes failed: {ex.Message}");
            }
        }

        private void UpdateBanner_Skip_Click(object sender, RoutedEventArgs e)
        {
            var s = _plugin?.Settings;
            if (s == null) return;
            s.LastSkippedVersion = s.LastSeenLatestVersion ?? "";
            try { _plugin!.PersistSettings(); } catch { /* persistence is best-effort */ }
            RefreshUpdateNotifications();
        }

        private void UpdateBanner_Dismiss_Click(object sender, RoutedEventArgs e)
        {
            _updateBannerDismissedThisSession = true;
            RefreshUpdateNotifications();
        }

        // ----- Settings handlers -----

        private void UpdateCheckEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = _plugin?.Settings;
            if (s == null) return;
            s.UpdateCheckEnabled = UpdateCheckEnabledToggle?.IsChecked == true;
            try { _plugin!.PersistSettings(); } catch { }
            RefreshUpdateNotifications();
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
            s.LastSeenReleaseNotes = "";
            s.LastSkippedVersion = "";
            try { _plugin!.PersistSettings(); } catch { }
            RefreshUpdateNotifications();
        }

        private async void UpdateCheckNow_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin?.Settings == null || UpdateCheckNowButton == null) return;
            var s = _plugin.Settings;

            // An explicit "Check now" is a clear request to see the current
            // status, so it overrides a prior Dismiss — otherwise the banner
            // would stay hidden for the rest of the session even when the check
            // finds an available update.
            _updateBannerDismissedThisSession = false;

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
                    s.LastSeenReleaseNotes = result.ReleaseNotes;
                }
                // result.Success with empty LatestVersion = 404 on dev-latest
                // (no dev release published yet). Leave cached values alone
                // so a previous stable-channel result doesn't get erased.

                try { _plugin.PersistSettings(); } catch { }
                RefreshUpdateNotifications();

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

        // Both the About-card and header Install buttons route here so the
        // download/install logic lives in exactly one place.
        private async void UpdateBanner_Install_Click(object sender, RoutedEventArgs e)
            => await RunInstallAsync();

        private async void HeaderUpdateInstall_Click(object sender, RoutedEventArgs e)
            => await RunInstallAsync();

        private async Task RunInstallAsync()
        {
            var s = _plugin?.Settings;
            if (s == null) return;
            if (string.IsNullOrEmpty(s.LastSeenAssetUrl)) return;
            if (_installInProgress) return;

            // Defensive: if a previous install is still pending the swap
            // would fail with OldPending. Surface the restart-required UI
            // instead of even attempting the network call.
            if (IsInstallPending())
            {
                RefreshUpdateNotifications();
                return;
            }

            try { _updateInstallCts?.Cancel(); } catch { }
            _updateInstallCts = new CancellationTokenSource();
            var ct = _updateInstallCts.Token;

            _installInProgress = true;
            SetBannerState_Installing();
            SetHeaderState_Installing();

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
                _installInProgress = false;
                SetBannerState_Failed(InstallErrorKind.Cancelled, "");
                RefreshHeaderBanner();
                return;
            }
            catch (Exception ex)
            {
                _installInProgress = false;
                MozaLog.Warn($"[UpdateInstall] threw: {ex.GetType().Name}: {ex.Message}");
                SetBannerState_Failed(InstallErrorKind.Unknown, ex.Message);
                RefreshHeaderBanner();
                return;
            }

            _installInProgress = false;

            if (result.Success)
            {
                MozaLog.Info($"[UpdateInstall] installed v{s.LastSeenLatestVersion} — restart required");
                // Repaint both surfaces into the pending-restart state: clears
                // the "update available" wording and shows the Restart button.
                RefreshUpdateNotifications();
            }
            else
            {
                MozaLog.Warn($"[UpdateInstall] failed: {result.ErrorKind} {result.ErrorMessage}");
                // About card keeps the detailed error; header resets to its
                // available/pending state (it doesn't surface install errors).
                SetBannerState_Failed(result.ErrorKind, result.ErrorMessage);
                RefreshHeaderBanner();
            }
        }

        // ----- Restart flow (one-click, post-install) -----

        private void UpdateBanner_Restart_Click(object sender, RoutedEventArgs e) => DoRestart();
        private void HeaderUpdateRestart_Click(object sender, RoutedEventArgs e) => DoRestart();

        // Restart button on the device-definition-deployed status-hint banner.
        // The button lives inside the HintBanners ItemsControl template (no
        // x:Name), so disable the clicked button directly to block a double-fire,
        // re-enabling it only if the exit request couldn't be issued.
        private void HintRestart_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null) button.IsEnabled = false;
            bool ok = _plugin?.RestartSimHub() ?? false;
            if (!ok && button != null) button.IsEnabled = true;
        }

        // Asks SimHub to exit and relaunch so the freshly-installed DLL loads.
        // Disables the Restart buttons first so a double-click can't fire two
        // exit requests; re-enables them if the request couldn't be issued so
        // the user can retry or restart manually.
        private void DoRestart()
        {
            if (UpdateBannerRestartButton != null) UpdateBannerRestartButton.IsEnabled = false;
            if (HeaderUpdateRestartButton != null) HeaderUpdateRestartButton.IsEnabled = false;

            bool ok = _plugin?.RestartSimHub() ?? false;
            if (!ok)
            {
                if (UpdateBannerRestartButton != null) UpdateBannerRestartButton.IsEnabled = true;
                if (HeaderUpdateRestartButton != null) HeaderUpdateRestartButton.IsEnabled = true;
            }
        }

        // ----- Header banner button handlers -----

        private void HeaderUpdateNotes_Click(object sender, RoutedEventArgs e) => OpenReleaseNotes();

        private void HeaderUpdateDismiss_Click(object sender, RoutedEventArgs e)
        {
            // One dismiss flag hides both the header and the About-card banner
            // for the rest of the session (cleared on plugin reload).
            _updateBannerDismissedThisSession = true;
            RefreshUpdateNotifications();
        }

        private void OnInstallProgress(InstallProgress p)
        {
            string? text = null;
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
                case InstallPhase.Done:
                    // Final UI state is set by RefreshUpdateNotifications after
                    // the await completes — no-op here.
                    return;
            }

            if (text == null) return;
            if (UpdateBannerProgressText != null) UpdateBannerProgressText.Text = text;
            if (HeaderUpdateBannerProgressText != null) HeaderUpdateBannerProgressText.Text = text;
        }
    }
}
