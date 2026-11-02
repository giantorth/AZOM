using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Renders dashboard preview PNGs with Studio's headless
    /// <c>--update-preview-image &lt;mzdash&gt;</c> mode — the same call PitHouse
    /// makes for its library thumbnails (docs/dashboard-studio.md). Studio writes
    /// <c>1.png</c>, <c>2.png</c>… (one per page) beside the mzdash and exits.
    ///
    /// <para>Renders from a COPY under a plugin-owned cache keyed by the mzdash
    /// content hash, so MOZA's trees are never written and an unchanged
    /// dashboard is rendered once.</para>
    /// </summary>
    internal static class DashboardPreviewRenderer
    {
        private const string DoneMarker = ".rendered";
        private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(60);
        private const int MaxCachedEntries = 64;

        public static string CacheRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MozaSimHubPlugin", "DashboardPreviews");

        // One Studio at a time: rapid dropdown changes queue rather than fan out.
        private static readonly SemaphoreSlim s_renderGate = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, Task<IReadOnlyList<string>>> s_inFlight =
            new Dictionary<string, Task<IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object s_lock = new object();

        public static bool IsAvailable => DashboardStudioLauncher.FindStudioExe() != null;

        /// <summary>
        /// Preview PNG paths for <paramref name="mzdash"/>, page order. Empty when
        /// Studio is missing or the render produced nothing. Completes synchronously
        /// on a cache hit.
        /// </summary>
        /// <param name="sourceDir">Directory the mzdash came from, or empty. Its
        /// <c>Resource\</c> subtree is copied alongside so per-dashboard widget
        /// images resolve; the shared pool resolves via Studio's own imageRoot.</param>
        public static Task<IReadOnlyList<string>> GetOrRenderAsync(string name, byte[] mzdash, string? sourceDir)
        {
            string key = HashHex(mzdash);
            string entryDir = Path.Combine(CacheRoot, key);
            var cached = ReadRendered(entryDir);
            if (cached != null) return Task.FromResult(cached);

            lock (s_lock)
            {
                if (s_inFlight.TryGetValue(key, out var running)) return running;
                var task = Task.Run(() => RenderAsync(key, entryDir, name, mzdash, sourceDir));
                s_inFlight[key] = task;
                task.ContinueWith(_ => { lock (s_lock) s_inFlight.Remove(key); },
                    TaskScheduler.Default);
                return task;
            }
        }

        private static async Task<IReadOnlyList<string>> RenderAsync(
            string key, string entryDir, string name, byte[] mzdash, string? sourceDir)
        {
            await s_renderGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cached = ReadRendered(entryDir);
                if (cached != null) return cached;

                string? exe = DashboardStudioLauncher.FindStudioExe();
                if (exe == null) return Array.Empty<string>();

                string safe = SafeFileName(name);
                string projectDir = Path.Combine(entryDir, safe);
                if (Directory.Exists(entryDir)) Directory.Delete(entryDir, recursive: true);
                Directory.CreateDirectory(projectDir);
                string mzdashPath = Path.Combine(projectDir, safe + ".mzdash");
                File.WriteAllBytes(mzdashPath, mzdash);
                CopyResources(sourceDir, projectDir);

                var sw = Stopwatch.StartNew();
                if (!RunStudio(exe, mzdashPath, name)) return Array.Empty<string>();

                var pngs = PagePngs(projectDir);
                if (pngs.Count == 0)
                {
                    MozaLog.Warn($"[AZOM] Dashboard preview: Studio wrote no images for '{name}'");
                    return pngs;
                }
                File.WriteAllText(Path.Combine(entryDir, DoneMarker), name, Encoding.UTF8);
                MozaLog.Debug($"[AZOM] Dashboard preview: '{name}' rendered {pngs.Count} page(s) in {sw.ElapsedMilliseconds} ms");
                Prune(key);
                return pngs;
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Dashboard preview failed for '{name}': {ex.GetType().Name}: {ex.Message}");
                return Array.Empty<string>();
            }
            finally
            {
                s_renderGate.Release();
            }
        }

        private static bool RunStudio(string exe, string mzdashPath, string name)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--update-preview-image " +
                            DashboardStudioLauncher.QuoteArg(mzdashPath.Replace('\\', '/')),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            if (proc.WaitForExit((int)RenderTimeout.TotalMilliseconds)) return true;
            MozaLog.Warn($"[AZOM] Dashboard preview: Studio timed out rendering '{name}'");
            try { proc.Kill(); } catch { }
            return false;
        }

        private static IReadOnlyList<string>? ReadRendered(string entryDir)
        {
            try
            {
                if (!File.Exists(Path.Combine(entryDir, DoneMarker))) return null;
                var project = Directory.GetDirectories(entryDir).FirstOrDefault();
                if (project == null) return null;
                var pngs = PagePngs(project);
                return pngs.Count > 0 ? pngs : null;
            }
            catch { return null; }
        }

        // Studio names pages 1.png, 2.png…; widget images live under Resource\.
        private static List<string> PagePngs(string dir)
        {
            var pages = new List<(int, string)>();
            foreach (var f in Directory.GetFiles(dir, "*.png", SearchOption.TopDirectoryOnly))
                if (int.TryParse(Path.GetFileNameWithoutExtension(f), out int n))
                    pages.Add((n, f));
            return pages.OrderBy(p => p.Item1).Select(p => p.Item2).ToList();
        }

        private static void CopyResources(string? sourceDir, string projectDir)
        {
            if (string.IsNullOrEmpty(sourceDir)) return;
            string src = Path.Combine(sourceDir!, "Resource");
            if (!Directory.Exists(src)) return;
            foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(projectDir, "Resource", file.Substring(src.Length).TrimStart('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }

        private static void Prune(string keep)
        {
            try
            {
                var stale = new DirectoryInfo(CacheRoot).GetDirectories()
                    .Where(d => !string.Equals(d.Name, keep, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .Skip(MaxCachedEntries - 1);
                foreach (var d in stale)
                {
                    try { d.Delete(recursive: true); } catch { }
                }
            }
            catch { }
        }

        private static string HashHex(byte[] data)
        {
            using var sha = SHA1.Create();
            var sb = new StringBuilder(40);
            foreach (byte b in sha.ComputeHash(data)) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            string s = sb.ToString().Trim().TrimEnd('.');
            return s.Length == 0 ? "dashboard" : s;
        }
    }
}
