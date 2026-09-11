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

namespace MozaPlugin.UI
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
        // Token source for the in-flight manual "Check now" call. Cancelled
        // on Unload so a slow request doesn't try to update a torn-down UI.
        private CancellationTokenSource? _updateCheckCts;

        // The install flow + its "in progress" / "dismissed this session" state
        // live on UpdateInstallCoordinator (shared with the cross-page
        // PluginBanners control), so both surfaces stay in sync.

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
                    RepopulateChannelCombo();
                }

                RefreshUpdateNotifications();
                RefreshLastCheckedText();
                // Hook the process-singleton coordinator from Loaded only (PluginBanners
                // pattern). Tab containers reparent this control, so Loaded/Unloaded fire
                // repeatedly — re-hook on every Loaded, else the banner goes deaf after the
                // first Unloaded. Hooking here would pin a control SimHub builds on every
                // game switch but never shows.
                Loaded += OnLoadedRehookUpdateBanner;
                Unloaded += OnUnloadedCancelUpdateCheck;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[UpdateBanner] init failed: {ex.Message}");
            }
        }

        private bool _installCoordinatorHooked;

        private void HookInstallCoordinator()
        {
            if (_installCoordinatorHooked) return;
            UpdateInstallCoordinator.Instance.Progress += OnAboutInstallProgress;
            UpdateInstallCoordinator.Instance.Completed += OnAboutInstallCompleted;
            _installCoordinatorHooked = true;
        }

        private void OnLoadedRehookUpdateBanner(object sender, RoutedEventArgs e)
            => HookInstallCoordinator();

        private void OnUnloadedCancelUpdateCheck(object sender, RoutedEventArgs e)
        {
            try { _updateCheckCts?.Cancel(); } catch { }
            // Unsubscribe from the shared install coordinator (re-hooked on the
            // next Loaded), but don't cancel an in-flight install — it runs to
            // completion regardless of UI.
            if (_installCoordinatorHooked)
            {
                UpdateInstallCoordinator.Instance.Progress -= OnAboutInstallProgress;
                UpdateInstallCoordinator.Instance.Completed -= OnAboutInstallCompleted;
                _installCoordinatorHooked = false;
            }
        }

        // Repaints the About > Updates card banner + release-notes panel. Called
        // on construction and after every user action / check / install. Safe on
        // the UI thread; no-ops while an install is mid-flight so it doesn't wipe
        // the live progress UI. The cross-tab header banner is the separate
        // self-refreshing PluginBanners control.
        internal void RefreshUpdateNotifications()
        {
            if (UpdateInstallCoordinator.Instance.InstallInProgress) return;

            string current = DiagnosticsTextBuilder.GetPluginVersion();
            var s = _plugin?.Settings;
            string latest = s?.LastSeenLatestVersion ?? "";
            UpdateInstallCoordinator.Instance.Compute(
                s, current, latest, out bool visible, out bool pendingRestart, out bool hasAsset);

            PaintAboutBanner(visible, pendingRestart, hasAsset, current, latest);
            RefreshReleaseNotes(visible, latest);
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
                    url = "https://github.com/giantorth/AZOM/releases";
                OpenExternalUrl(url);
                return;
            }

            try
            {
                if (MainTabs != null && OptionsTab != null)
                    MainTabs.SelectedItem = OptionsTab;
                // Defer the scroll until the Options tab's content is realized.
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
            UpdateInstallCoordinator.Instance.DismissedThisSession = true;
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

        // Rebuilds the channel picker: Stable + one entry per open PR from the
        // cached release snapshot. Selection keys off the persisted channel id
        // (item Tag), so a snapshot refresh can't silently move the user onto
        // a different channel.
        private void RepopulateChannelCombo()
        {
            if (UpdateChannelCombo == null) return;
            var s = _plugin?.Settings;
            string selectedId = s?.UpdateChannelId ?? "";
            if (selectedId.Length == 0) selectedId = UpdateCheckService.StableChannelId;

            using (_suppressor.Begin())
            {
                UpdateChannelCombo.Items.Clear();
                UpdateChannelCombo.Items.Add(new ComboBoxItem
                {
                    Content = Strings.Option_ReleaseChannelStable,
                    Tag = UpdateCheckService.StableChannelId,
                });

                bool selectedPresent = selectedId == UpdateCheckService.StableChannelId;
                var snap = UpdateCheckService.CachedSnapshot;
                if (snap != null)
                {
                    foreach (var ch in snap.PrChannels)
                    {
                        UpdateChannelCombo.Items.Add(new ComboBoxItem
                        {
                            Content = ch.Title,
                            Tag = ch.ChannelId,
                        });
                        if (ch.ChannelId == selectedId) selectedPresent = true;
                    }
                }
                if (!selectedPresent)
                {
                    // Selected PR channel isn't in the snapshot (offline, or
                    // nothing fetched yet this session) — keep the selection
                    // visible via the persisted label.
                    UpdateChannelCombo.Items.Add(new ComboBoxItem
                    {
                        Content = string.IsNullOrEmpty(s?.UpdateChannelLabel)
                            ? selectedId
                            : s!.UpdateChannelLabel,
                        Tag = selectedId,
                    });
                }

                foreach (var item in UpdateChannelCombo.Items)
                {
                    if (item is ComboBoxItem cbi
                        && string.Equals(cbi.Tag as string, selectedId, StringComparison.Ordinal))
                    {
                        UpdateChannelCombo.SelectedItem = cbi;
                        break;
                    }
                }
            }
        }

        // Refresh the PR-channel list when the picker opens, at most once per
        // 5 minutes (every check also refreshes the shared snapshot).
        private bool _channelListRefreshInFlight;

        private async void UpdateChannelCombo_DropDownOpened(object sender, EventArgs e)
        {
            if (_channelListRefreshInFlight) return;
            var snap = UpdateCheckService.CachedSnapshot;
            if (snap != null && DateTime.UtcNow - snap.FetchedUtc < TimeSpan.FromMinutes(5))
                return;

            _channelListRefreshInFlight = true;
            try
            {
                var fetch = await Task.Run(
                    () => UpdateCheckService.FetchSnapshotAsync(CancellationToken.None))
                    .ConfigureAwait(true);
                if (fetch.Snapshot != null) RepopulateChannelCombo();
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[UpdateBanner] channel list refresh failed: {ex.Message}");
            }
            finally
            {
                _channelListRefreshInFlight = false;
            }
        }

        private void UpdateChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            var s = _plugin?.Settings;
            if (s == null || UpdateChannelCombo == null) return;
            var item = UpdateChannelCombo.SelectedItem as ComboBoxItem;
            string? channelId = item?.Tag as string;
            if (string.IsNullOrEmpty(channelId)) return;
            if (string.Equals(channelId, s.UpdateChannelId, StringComparison.Ordinal)) return;

            s.UpdateChannelId = channelId!;
            s.UpdateChannelLabel = channelId == UpdateCheckService.StableChannelId
                ? ""
                : item?.Content as string ?? "";
            // Channel switch invalidates the cached "last seen" — releases on
            // one channel aren't comparable to another's. Clear so the next
            // check repopulates.
            s.LastSeenLatestVersion = "";
            s.LastSeenReleaseUrl = "";
            s.LastSeenAssetUrl = "";
            s.LastSeenReleaseNotes = "";
            s.LastSkippedVersion = "";
            try { _plugin!.PersistSettings(); } catch { }
            RefreshUpdateNotifications();
            // Fetch + offer the selected channel's build right away rather
            // than on the next launch.
            _ = RunManualCheckAsync();
        }

        private async void UpdateCheckNow_Click(object sender, RoutedEventArgs e)
            => await RunManualCheckAsync();

        // Shared by the Check-now button and a channel switch (which wants
        // the selected channel's build offered immediately).
        private async Task RunManualCheckAsync()
        {
            if (_plugin?.Settings == null || UpdateCheckNowButton == null) return;
            var s = _plugin.Settings;

            // An explicit "Check now" is a clear request to see the current
            // status, so it overrides a prior Dismiss — otherwise the banner
            // would stay hidden for the rest of the session even when the check
            // finds an available update.
            UpdateInstallCoordinator.Instance.DismissedThisSession = false;

            // Cancel any in-flight manual check before kicking off a new one.
            try { _updateCheckCts?.Cancel(); } catch { }
            _updateCheckCts = new CancellationTokenSource();
            var ct = _updateCheckCts.Token;

            UpdateCheckNowButton.IsEnabled = false;
            if (UpdateLastCheckedText != null)
                UpdateLastCheckedText.Text = Strings.Status_UpdateChecking;

            SnapshotFetchResult fetch;
            try
            {
                fetch = await Task.Run(
                    () => UpdateCheckService.FetchSnapshotAsync(ct),
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

            if (fetch.Snapshot != null)
            {
                var result = UpdateCheckService.ResolveChannel(
                    fetch.Snapshot, s.UpdateChannelId, out bool channelFound);
                bool channelGone = !channelFound;
                if (channelGone)
                {
                    // Tracked PR closed/merged and its builds were cleaned up
                    // — fall back to stable.
                    s.UpdateChannelId = UpdateCheckService.StableChannelId;
                    s.UpdateChannelLabel = "";
                    s.LastSkippedVersion = "";
                    result = UpdateCheckService.ResolveChannel(
                        fetch.Snapshot, s.UpdateChannelId, out _);
                }
                RepopulateChannelCombo();

                if (!string.IsNullOrEmpty(result.LatestVersion))
                {
                    s.LastSeenLatestVersion = result.LatestVersion;
                    s.LastSeenReleaseUrl = result.ReleaseUrl;
                    s.LastSeenAssetUrl = result.AssetUrl;
                    s.LastSeenReleaseNotes = result.ReleaseNotes;
                }
                // Empty LatestVersion = the channel resolves to nothing right
                // now (repo without a stable release). Leave cached values
                // alone rather than erasing a previous result.

                try { _plugin.PersistSettings(); } catch { }
                RefreshUpdateNotifications();

                if (UpdateLastCheckedText != null)
                {
                    if (channelGone)
                    {
                        UpdateLastCheckedText.Text = Strings.Status_UpdatePrChannelGone;
                    }
                    else
                    {
                        // Show explicit "up to date" when there's no newer
                        // version available; otherwise show the timestamp.
                        string current = DiagnosticsTextBuilder.GetPluginVersion();
                        bool upToDate = string.IsNullOrEmpty(result.LatestVersion)
                            || !UpdateCheckService.IsUpdateAvailable(
                                result.LatestVersion, current, s.UpdateChannelId);
                        if (upToDate)
                            UpdateLastCheckedText.Text = Strings.Status_UpdateUpToDate;
                        else
                            RefreshLastCheckedText();
                    }
                }
            }
            else
            {
                try { _plugin.PersistSettings(); } catch { }
                if (UpdateLastCheckedText != null)
                {
                    switch (fetch.ErrorKind)
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

        // The About-card Install button starts the shared install flow. The
        // coordinator owns the download/swap; progress + completion come back
        // via OnAboutInstallProgress / OnAboutInstallCompleted.
        private async void UpdateBanner_Install_Click(object sender, RoutedEventArgs e)
        {
            var s = _plugin?.Settings;
            if (s == null || string.IsNullOrEmpty(s.LastSeenAssetUrl)) return;
            if (UpdateInstallCoordinator.Instance.InstallInProgress) return;
            SetBannerState_Installing();
            await UpdateInstallCoordinator.Instance.RunInstallAsync(s);
        }

        // ----- Restart flow (one-click, post-install) -----

        private void UpdateBanner_Restart_Click(object sender, RoutedEventArgs e) => DoRestart();

        // Asks SimHub to exit and relaunch so the freshly-installed DLL loads.
        // Disables the Restart button first so a double-click can't fire two
        // exit requests; re-enables it if the request couldn't be issued so the
        // user can retry or restart manually.
        private void DoRestart()
        {
            if (UpdateBannerRestartButton != null) UpdateBannerRestartButton.IsEnabled = false;
            bool ok = _plugin?.RestartSimHub() ?? false;
            if (!ok && UpdateBannerRestartButton != null) UpdateBannerRestartButton.IsEnabled = true;
        }

        // ----- Shared install coordinator callbacks (About-card painting) -----

        private void OnAboutInstallProgress(InstallProgress p)
        {
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
                    return; // Done/Idle/Failed — final state set by OnAboutInstallCompleted
            }
            if (UpdateBannerProgressText != null) UpdateBannerProgressText.Text = text;
        }

        // Install finished (or a synthesized cancel/error). Success and the
        // "already pending" guard both repaint into the restart-required state;
        // everything else surfaces the error on the About card.
        private void OnAboutInstallCompleted(InstallResult r)
        {
            if (r.Success || r.ErrorKind == InstallErrorKind.OldPending)
                RefreshUpdateNotifications();
            else
                SetBannerState_Failed(r.ErrorKind, r.ErrorMessage);
        }
    }
}
