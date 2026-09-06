using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MozaPlugin.Settings;

namespace MozaPlugin.UI.UpdateCheck
{
    /// <summary>
    /// Session-scoped coordinator for the in-app update install. Owns the single
    /// install flow plus the shared "install in progress" / "dismissed this
    /// session" state, so every surface that renders the update banner stays in
    /// sync: the cross-tab <c>PluginBanners</c> control (plugin pane + every
    /// device page) and the About-tab "Updates" card.
    ///
    /// Deliberately UI-agnostic — it raises <see cref="Progress"/> / <see
    /// cref="Completed"/> events and each surface paints itself. A process
    /// singleton, so its state survives the plugin's game-switch reload (a
    /// dismissal stays dismissed until an explicit "Check now" or a real
    /// process exit).
    /// </summary>
    internal sealed class UpdateInstallCoordinator
    {
        public static UpdateInstallCoordinator Instance { get; } = new UpdateInstallCoordinator();

        private UpdateInstallCoordinator() { }

        private CancellationTokenSource? _installCts;

        /// <summary>
        /// True while a download+install is running. Surfaces gate their repaint
        /// paths on this so a 500 ms refresh tick doesn't clobber live progress.
        /// </summary>
        public bool InstallInProgress { get; private set; }

        /// <summary>
        /// Set when the user dismisses the update banner from any surface; hides
        /// it everywhere for the rest of the session. Reset by an explicit
        /// "Check now" (a clear request to see current status).
        /// </summary>
        public bool DismissedThisSession { get; set; }

        /// <summary>Streaming progress (Downloading → Extracting → Installing).
        /// Raised on the UI thread — <see cref="RunInstallAsync"/> captures the
        /// caller's <see cref="SynchronizationContext"/> via <see cref="Progress{T}"/>.</summary>
        public event Action<InstallProgress>? Progress;

        /// <summary>Fired once when an install finishes — success, or a
        /// synthesized failure for cancellation / unexpected exception so
        /// subscribers can repaint uniformly. Raised on the UI thread.</summary>
        public event Action<InstallResult>? Completed;

        /// <summary>
        /// An install completed earlier (this session or a prior one without a
        /// restart) — a <c>MozaPlugin.dll.old</c> sits next to the loaded DLL.
        /// Until SimHub restarts we can't install again (the .old is the rollback
        /// target). Pure file probe; never touches the network.
        /// </summary>
        public static bool IsInstallPending()
        {
            try
            {
                string p = UpdateInstallService.GetPluginAssemblyPath();
                return !string.IsNullOrEmpty(p)
                    && File.Exists(p + UpdateInstallService.OldSuffix);
            }
            catch { return false; }
        }

        /// <summary>
        /// Whether an update notification should show right now and in which mode.
        /// Pure read of persisted state + the .old pending probe + the session
        /// dismiss flag. <paramref name="pendingRestart"/> means an install
        /// already landed and SimHub must restart — never offer another install
        /// in that mode (it would fail with OldPending).
        /// </summary>
        public void Compute(
            MozaPluginSettings? s, string current, string latest,
            out bool visible, out bool pendingRestart, out bool hasAsset)
        {
            visible = false;
            pendingRestart = false;
            hasAsset = false;

            if (s == null) return;
            if (DismissedThisSession || !s.UpdateCheckEnabled) return;
            if (string.IsNullOrEmpty(latest)) return;
            if (!UpdateCheckService.IsUpdateAvailable(latest, current, s.UpdateChannelId)) return;
            if (!string.IsNullOrEmpty(s.LastSkippedVersion)
                && string.Equals(s.LastSkippedVersion, latest, StringComparison.Ordinal)) return;

            visible = true;
            hasAsset = !string.IsNullOrEmpty(s.LastSeenAssetUrl);
            pendingRestart = IsInstallPending();
        }

        /// <summary>
        /// Runs the download+extract+swap. Re-entry guarded. Raises <see
        /// cref="Progress"/> as it streams and <see cref="Completed"/> exactly
        /// once at the end. Must be called on the UI thread so the captured
        /// context marshals the events back to it.
        /// </summary>
        public async Task RunInstallAsync(MozaPluginSettings? s)
        {
            if (s == null) return;
            if (string.IsNullOrEmpty(s.LastSeenAssetUrl)) return;
            if (InstallInProgress) return;

            // Defensive: a pending .old means the swap would fail with OldPending.
            // Surface the restart-required state instead of hitting the network.
            if (IsInstallPending())
            {
                Completed?.Invoke(InstallResult.Fail(InstallErrorKind.OldPending, ""));
                return;
            }

            try { _installCts?.Cancel(); } catch { }
            _installCts = new CancellationTokenSource();
            var ct = _installCts.Token;

            InstallInProgress = true;

            // Progress<T> captures the originating (UI) SynchronizationContext so
            // the callback marshals back to the UI thread automatically.
            var progress = new Progress<InstallProgress>(p => Progress?.Invoke(p));

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
                InstallInProgress = false;
                Completed?.Invoke(InstallResult.Fail(InstallErrorKind.Cancelled, ""));
                return;
            }
            catch (Exception ex)
            {
                InstallInProgress = false;
                MozaLog.Warn($"[UpdateInstall] threw: {ex.GetType().Name}: {ex.Message}");
                Completed?.Invoke(InstallResult.Fail(InstallErrorKind.Unknown, ex.Message));
                return;
            }

            InstallInProgress = false;
            Completed?.Invoke(result);
        }

        /// <summary>Cancels an in-flight install (e.g. when the last UI host unloads).</summary>
        public void CancelInstall()
        {
            try { _installCts?.Cancel(); } catch { }
        }
    }
}
