using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using MozaPlugin.Devices;
using MozaPlugin.Devices.StalksTruckSim;
using MozaPlugin.Hardware;
using MozaPlugin.Protocol;
using MozaPlugin.Resources;
using MozaPlugin.Settings;
using MozaPlugin.Telemetry;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.TileServer;
using MozaPlugin.UI.UpdateCheck;
using Timer = System.Timers.Timer;

namespace MozaPlugin
{
    public partial class MozaPlugin
    {

        // Kicks off the background GitHub Releases query on a thread-pool
        // thread, with a 24h throttle (LastUpdateCheckUtc) and a per-process
        // dedupe (s_updateCheckStarted). Returns immediately; the result is
        // persisted into _settings on completion. Failures swallow silently
        // — the user can still trigger a foreground check from the About tab.
        private void MaybeStartUpdateCheck()
        {
            try
            {
                if (_settings == null || !_settings.UpdateCheckEnabled) return;
                if (s_updateCheckStarted) return;
                // A PR channel tracks a moving head — a version cached in a
                // prior session may be stale, and the tracked PR may have
                // closed since. Re-check PR channels on every launch (still
                // once per process via s_updateCheckStarted); stable versions
                // are directly comparable and keep the 24h throttle.
                if (!UpdateCheckService.TryParsePrChannelId(_settings.UpdateChannelId, out _)
                    && DateTime.UtcNow - _settings.LastUpdateCheckUtc < TimeSpan.FromHours(24))
                {
                    MozaLog.Debug("[UpdateCheck] skipped — last check less than 24h ago");
                    return;
                }
                s_updateCheckStarted = true;

                var channelId = _settings.UpdateChannelId;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var fetch = await UpdateCheckService
                            .FetchSnapshotAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        _settings.LastUpdateCheckUtc = DateTime.UtcNow;

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
                                _settings.UpdateChannelId = channelId;
                                _settings.UpdateChannelLabel = "";
                                _settings.LastSkippedVersion = "";
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
                                        _settings.UpdateChannelLabel = string.Format(
                                            Strings.Option_ReleaseChannelPr, ch.Number, ch.Title);
                                        break;
                                    }
                                }
                            }

                            if (result.Success && !string.IsNullOrEmpty(result.LatestVersion))
                            {
                                _settings.LastSeenLatestVersion = result.LatestVersion;
                                _settings.LastSeenReleaseUrl = result.ReleaseUrl;
                                _settings.LastSeenAssetUrl = result.AssetUrl;
                                _settings.LastSeenReleaseNotes = result.ReleaseNotes;
                                MozaLog.Debug(
                                    $"[UpdateCheck] {channelId}: latest={result.LatestVersion} asset={(string.IsNullOrEmpty(result.AssetUrl) ? "(none)" : "ok")}");
                            }
                        }
                        else
                        {
                            MozaLog.Debug(
                                $"[UpdateCheck] {channelId} failed: {fetch.ErrorKind} {fetch.ErrorMessage}");
                        }

                        try { this.SaveCommonSettings("MozaPluginSettings", _settings); }
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
