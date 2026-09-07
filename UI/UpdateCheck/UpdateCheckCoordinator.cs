using System;
using System.Threading;
using System.Threading.Tasks;
using SimHub.Plugins;
using MozaPlugin.Resources;

namespace MozaPlugin.UI.UpdateCheck
{
    /// <summary>
    /// Schedules the startup update check. Extracted from MozaPlugin — see
    /// MozaPlugin.Bootstrap.cs for the Init call site.
    /// </summary>
    internal sealed class UpdateCheckCoordinator
    {
        private readonly MozaPlugin _plugin;

        // Per-process dedupe so a SimHub game switch (which re-runs Init)
        // doesn't re-query GitHub. Static by design: it must outlive the
        // plugin instance, not the coordinator.
        private static bool s_started;

        internal UpdateCheckCoordinator(MozaPlugin plugin)
        {
            _plugin = plugin;
        }

        /// <summary>
        /// Kicks off the background GitHub Releases query on a thread-pool
        /// thread, with a 24h throttle (LastUpdateCheckUtc) and a per-process
        /// dedupe. Returns immediately; the result is persisted into settings
        /// on completion. Failures swallow silently — the user can still
        /// trigger a foreground check from the About tab.
        /// </summary>
        internal void MaybeStart()
        {
            try
            {
                var settings = _plugin._settings;
                if (settings == null || !settings.UpdateCheckEnabled) return;
                if (s_started) return;
                // A PR channel tracks a moving head — a version cached in a
                // prior session may be stale, and the tracked PR may have
                // closed since. Re-check PR channels on every launch (still
                // once per process via s_started); stable versions are
                // directly comparable and keep the 24h throttle.
                if (!UpdateCheckService.TryParsePrChannelId(settings.UpdateChannelId, out _)
                    && DateTime.UtcNow - settings.LastUpdateCheckUtc < TimeSpan.FromHours(24))
                {
                    MozaLog.Debug("[UpdateCheck] skipped — last check less than 24h ago");
                    return;
                }
                s_started = true;

                var channelId = settings.UpdateChannelId;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var fetch = await UpdateCheckService
                            .FetchSnapshotAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        settings.LastUpdateCheckUtc = DateTime.UtcNow;

                        if (fetch.Snapshot != null)
                        {
                            var snap = fetch.Snapshot;
                            var result = UpdateCheckService.ResolveChannel(
                                snap, channelId, out bool channelFound);
                            if (!channelFound)
                            {
                                // Tracked PR closed/merged and its builds were
                                // cleaned up — fall back to stable.
                                MozaLog.Info(
                                    $"[UpdateCheck] channel {channelId} is gone; falling back to stable");
                                channelId = UpdateCheckService.StableChannelId;
                                settings.UpdateChannelId = channelId;
                                settings.UpdateChannelLabel = "";
                                settings.LastSkippedVersion = "";
                                result = UpdateCheckService.ResolveChannel(
                                    snap, channelId, out _);
                            }
                            else if (UpdateCheckService.TryParsePrChannelId(channelId, out int prNumber))
                            {
                                // PR titles drift; keep the offline label current.
                                foreach (var ch in snap.PrChannels)
                                {
                                    if (ch.Number == prNumber)
                                    {
                                        settings.UpdateChannelLabel = string.Format(
                                            Strings.Option_ReleaseChannelPr, ch.Number, ch.Title);
                                        break;
                                    }
                                }
                            }

                            if (result.Success && !string.IsNullOrEmpty(result.LatestVersion))
                            {
                                settings.LastSeenLatestVersion = result.LatestVersion;
                                settings.LastSeenReleaseUrl = result.ReleaseUrl;
                                settings.LastSeenAssetUrl = result.AssetUrl;
                                settings.LastSeenReleaseNotes = result.ReleaseNotes;
                                MozaLog.Debug(
                                    $"[UpdateCheck] {channelId}: latest={result.LatestVersion} asset={(string.IsNullOrEmpty(result.AssetUrl) ? "(none)" : "ok")}");
                            }
                        }
                        else
                        {
                            MozaLog.Debug(
                                $"[UpdateCheck] {channelId} failed: {fetch.ErrorKind} {fetch.ErrorMessage}");
                        }

                        try { _plugin.SaveCommonSettings("MozaPluginSettings", settings); }
                        catch { /* persistence is best-effort */ }

                        // Repaint the settings pane if it's open so a fresh
                        // result lands immediately — without this the About-card
                        // banner + release notes would only update on the next
                        // tab reopen or manual "Check now" (the header banner
                        // already self-refreshes on its 500ms tick).
                        try
                        {
                            var ctrl = SettingsControl.Instance;
                            ctrl?.Dispatcher?.BeginInvoke(new Action(() =>
                            {
                                try { ctrl.RefreshUpdateNotifications(); } catch { }
                            }));
                        }
                        catch { /* UI refresh is best-effort */ }
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Debug($"[UpdateCheck] background task threw: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[UpdateCheck] scheduler threw: {ex.Message}");
            }
        }
    }
}
