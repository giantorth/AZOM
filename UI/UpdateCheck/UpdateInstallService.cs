using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace MozaPlugin.UI.UpdateCheck
{
    public enum InstallPhase
    {
        Idle = 0,
        Downloading,
        Extracting,
        Installing,
        Done,
        Failed,
    }

    public enum InstallErrorKind
    {
        None = 0,
        Network,           // download HTTP/socket/timeout/TLS
        Http,              // non-2xx download
        Cancelled,
        ZipMalformed,      // ZipArchive failed to open or no MozaPlugin.dll entry
        Validation,        // DLL too small / missing MZ magic
        OldPending,        // MozaPlugin.dll.old exists — caller must restart first
        WriteFailed,       // rename or move failed (perms / locked / etc.)
        Unknown,
    }

    /// <summary>
    /// Streaming progress event delivered as the download/install runs.
    /// </summary>
    public readonly struct InstallProgress
    {
        public InstallPhase Phase { get; }
        public long BytesDownloaded { get; }
        public long TotalBytes { get; } // -1 if Content-Length wasn't provided

        public InstallProgress(InstallPhase phase, long bytesDownloaded, long totalBytes)
        {
            Phase = phase;
            BytesDownloaded = bytesDownloaded;
            TotalBytes = totalBytes;
        }

        public double Fraction
        {
            get
            {
                if (TotalBytes <= 0) return -1;
                if (BytesDownloaded <= 0) return 0;
                if (BytesDownloaded >= TotalBytes) return 1;
                return (double)BytesDownloaded / TotalBytes;
            }
        }
    }

    public readonly struct InstallResult
    {
        public bool Success { get; }
        public InstallErrorKind ErrorKind { get; }
        public string ErrorMessage { get; }

        public InstallResult(bool success, InstallErrorKind kind, string message)
        {
            Success = success;
            ErrorKind = kind;
            ErrorMessage = message ?? "";
        }

        public static InstallResult Ok() => new InstallResult(true, InstallErrorKind.None, "");
        public static InstallResult Fail(InstallErrorKind kind, string message)
            => new InstallResult(false, kind, message);
    }

    /// <summary>
    /// In-app installer for plugin updates. Streams the release ZIP from
    /// GitHub, extracts MozaPlugin.dll, and swaps it into place using the
    /// rename-aside trick (Windows lets a loaded DLL be renamed but not
    /// deleted/overwritten). Caller is responsible for telling the user to
    /// restart SimHub afterward — the previous DLL stays loaded until exit.
    ///
    /// All temp files live alongside the target DLL so move operations
    /// stay within one filesystem.
    /// </summary>
    public static class UpdateInstallService
    {
        public const string OldSuffix = ".old";
        public const string NewSuffix = ".new";
        public const string ZipTempName = "MozaPlugin.update.zip";

        // Minimum plausible DLL size — current release is ~1.5MB. A sub-100KB
        // file means the download truncated or the ZIP entry was wrong.
        private const long MinimumValidDllBytes = 100_000;

        // 32 KB read buffer — matches the default FileStream buffer and keeps
        // progress event cadence reasonable on multi-MB downloads.
        private const int ReadBufferBytes = 32 * 1024;

        /// <summary>
        /// Returns the absolute path of the currently loaded MozaPlugin.dll.
        /// This is the file we'll rename to .old during install.
        /// </summary>
        public static string GetPluginAssemblyPath()
        {
            return Assembly.GetExecutingAssembly().Location;
        }

        /// <summary>
        /// Best-effort cleanup of MozaPlugin.dll.old (left over from a
        /// previous successful install) and any stale MozaPlugin.dll.new
        /// (download interrupted before the swap). Safe to call from Init.
        /// Swallows failures — both files are non-critical and the next
        /// install attempt will retry the deletion.
        /// </summary>
        public static void CleanupLeftoverArtifacts(Action<string>? log = null)
        {
            string dllPath;
            try { dllPath = GetPluginAssemblyPath(); }
            catch { return; }
            if (string.IsNullOrEmpty(dllPath)) return;

            string dir = Path.GetDirectoryName(dllPath) ?? "";
            if (string.IsNullOrEmpty(dir)) return;

            TryDelete(dllPath + OldSuffix, log);
            TryDelete(dllPath + NewSuffix, log);
            TryDelete(Path.Combine(dir, ZipTempName), log);
        }

        /// <summary>
        /// The asset URL comes from the persisted settings file (written by the
        /// update check). Whatever ends up there is downloaded and swapped in as
        /// the plugin DLL, so only accept https on GitHub's release hosts.
        /// </summary>
        internal static bool IsTrustedAssetUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
            string host = uri.Host;
            return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDelete(string path, Action<string>? log)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    log?.Invoke($"[UpdateInstall] cleaned up {Path.GetFileName(path)}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[UpdateInstall] could not delete {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        /// <summary>
        /// Downloads the release ZIP at <paramref name="assetUrl"/>, extracts
        /// MozaPlugin.dll, and swaps it into place. Reports progress through
        /// <paramref name="progress"/> (Downloading → Extracting → Installing
        /// → Done). The currently running DLL is renamed to
        /// <c>MozaPlugin.dll.old</c>; the user must restart SimHub for the new
        /// DLL to load. On failure, any in-place rename is rolled back so the
        /// running plugin remains intact.
        /// </summary>
        public static async Task<InstallResult> DownloadAndInstallAsync(
            string assetUrl,
            HttpClient http,
            IProgress<InstallProgress>? progress,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(assetUrl))
                return InstallResult.Fail(InstallErrorKind.Network, "no asset URL");
            if (!IsTrustedAssetUrl(assetUrl))
                return InstallResult.Fail(InstallErrorKind.Validation, "asset URL is not an https GitHub release URL");
            if (http == null)
                return InstallResult.Fail(InstallErrorKind.Unknown, "http client missing");

            string dllPath;
            try { dllPath = GetPluginAssemblyPath(); }
            catch (Exception ex) { return InstallResult.Fail(InstallErrorKind.Unknown, ex.Message); }
            if (string.IsNullOrEmpty(dllPath))
                return InstallResult.Fail(InstallErrorKind.Unknown, "could not locate plugin DLL");

            string dir = Path.GetDirectoryName(dllPath) ?? "";
            string oldPath = dllPath + OldSuffix;
            string newPath = dllPath + NewSuffix;
            string zipPath = Path.Combine(dir, ZipTempName);

            // Refuse to install if a previous install is still pending (the
            // .old file from that install is still loaded — overwriting it
            // would lose the rollback target). The user must restart first.
            if (File.Exists(oldPath))
            {
                return InstallResult.Fail(
                    InstallErrorKind.OldPending,
                    "a previous install is pending — restart SimHub first");
            }

            // Clean any stale staging files from a prior failed attempt so
            // we start from a known state. Failures here are non-fatal.
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }

            // -------- Download phase --------
            progress?.Report(new InstallProgress(InstallPhase.Downloading, 0, -1));
            var dlResult = await DownloadAsync(assetUrl, http, zipPath, progress, ct).ConfigureAwait(false);
            if (!dlResult.Success)
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                return dlResult;
            }

            // -------- Extract phase --------
            progress?.Report(new InstallProgress(InstallPhase.Extracting, 0, -1));
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var entry = FindDllEntry(zip);
                    if (entry == null)
                    {
                        return InstallResult.Fail(
                            InstallErrorKind.ZipMalformed,
                            "MozaPlugin.dll not found in release ZIP");
                    }
                    entry.ExtractToFile(newPath, overwrite: true);
                }
            }
            catch (InvalidDataException ex)
            {
                return InstallResult.Fail(InstallErrorKind.ZipMalformed, ex.Message);
            }
            catch (Exception ex)
            {
                return InstallResult.Fail(InstallErrorKind.ZipMalformed, ex.Message);
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            }

            // -------- Validate extracted DLL --------
            try
            {
                var info = new FileInfo(newPath);
                if (!info.Exists || info.Length < MinimumValidDllBytes)
                {
                    try { File.Delete(newPath); } catch { }
                    return InstallResult.Fail(
                        InstallErrorKind.Validation,
                        $"extracted DLL too small ({(info.Exists ? info.Length : 0)} bytes)");
                }
                if (!HasPeMagic(newPath))
                {
                    try { File.Delete(newPath); } catch { }
                    return InstallResult.Fail(
                        InstallErrorKind.Validation,
                        "extracted file is not a Windows PE binary");
                }
            }
            catch (Exception ex)
            {
                try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
                return InstallResult.Fail(InstallErrorKind.Validation, ex.Message);
            }

            // -------- Install phase: rename-aside + move-into-place --------
            progress?.Report(new InstallProgress(InstallPhase.Installing, 0, -1));
            bool renamed = false;
            try
            {
                // Step 1: rename currently loaded DLL to .old. Windows allows
                // this on a loaded image; the process keeps using the in-memory
                // copy. On NTFS this is an atomic metadata operation.
                File.Move(dllPath, oldPath);
                renamed = true;

                // Step 2: move staged DLL into the original path. If this
                // fails we MUST roll back by renaming .old back to its
                // original name — otherwise the plugin is broken on restart.
                File.Move(newPath, dllPath);
            }
            catch (Exception ex)
            {
                // Rollback: if the .old rename succeeded but the new move
                // failed, restore the original so the plugin still loads.
                if (renamed)
                {
                    try
                    {
                        if (File.Exists(oldPath) && !File.Exists(dllPath))
                            File.Move(oldPath, dllPath);
                    }
                    catch { /* rollback best-effort */ }
                }
                try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
                return InstallResult.Fail(InstallErrorKind.WriteFailed, ex.Message);
            }

            progress?.Report(new InstallProgress(InstallPhase.Done, 0, -1));
            return InstallResult.Ok();
        }

        // Streams the asset to disk with progress callbacks. Uses ResponseHeadersRead
        // so we can start emitting progress before the body arrives in full.
        private static async Task<InstallResult> DownloadAsync(
            string url, HttpClient http, string destPath,
            IProgress<InstallProgress>? progress, CancellationToken ct)
        {
            try
            {
                using (var resp = await http.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        return InstallResult.Fail(
                            InstallErrorKind.Http,
                            $"HTTP {(int)resp.StatusCode} downloading asset");
                    }
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(
                        destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        bufferSize: ReadBufferBytes, useAsync: true))
                    {
                        var buf = new byte[ReadBufferBytes];
                        long copied = 0;
                        long lastReported = -1;
                        int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                            copied += n;
                            // Report progress at most ~once per 256 KB downloaded
                            // (or at completion) to avoid flooding the dispatcher.
                            if (copied - lastReported >= 256 * 1024 || (total > 0 && copied == total))
                            {
                                lastReported = copied;
                                progress?.Report(new InstallProgress(
                                    InstallPhase.Downloading, copied, total));
                            }
                        }
                        await dst.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
                return InstallResult.Ok();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return InstallResult.Fail(InstallErrorKind.Cancelled, "");
            }
            catch (OperationCanceledException)
            {
                return InstallResult.Fail(InstallErrorKind.Network, "timeout");
            }
            catch (HttpRequestException ex)
            {
                return InstallResult.Fail(InstallErrorKind.Network, ex.Message);
            }
            catch (IOException ex)
            {
                return InstallResult.Fail(InstallErrorKind.WriteFailed, ex.Message);
            }
            catch (Exception ex)
            {
                return InstallResult.Fail(InstallErrorKind.Network, ex.Message);
            }
        }

        // Find the MozaPlugin.dll entry regardless of whether it's at the
        // archive root or inside a versioned folder.
        private static ZipArchiveEntry? FindDllEntry(ZipArchive zip)
        {
            foreach (var entry in zip.Entries)
            {
                if (string.Equals(entry.Name, "MozaPlugin.dll", StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
            return null;
        }

        // PE files start with 'MZ' (0x4D 0x5A) — the DOS stub header. Cheap
        // sanity gate against grabbing the wrong asset or a truncated file.
        private static bool HasPeMagic(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int b1 = fs.ReadByte();
                    int b2 = fs.ReadByte();
                    return b1 == 0x4D && b2 == 0x5A;
                }
            }
            catch { return false; }
        }
    }
}
