using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SimHub.Plugins;
using SimHub.Plugins.OutputPlugins.GraphicalDash;

namespace MozaPlugin.Integration.ControlDash
{
    /// <summary>
    /// Installs the generated dashboards into <c>DashTemplates/&lt;name&gt;/</c> through
    /// Dash Studio's own load + save (<see cref="EditorModel"/>), so the files carry
    /// SimHub's defaults, metadata and thumbnails, and the replaced copy goes to
    /// <c>_Backups</c>. A new version (the generated djson changed) replaces an
    /// installed copy that is byte-identical to the last install; a copy saved in
    /// Dash Studio since is listed in <see cref="PendingNames"/> for the user to
    /// install or dismiss. The sidecar <c>.djson.azom</c> holds the installed
    /// version and the SHA-256 of the file as SimHub saved it.
    /// </summary>
    internal static class DashTemplateInstaller
    {
        private sealed class Template
        {
            public readonly string Name;
            public readonly string Djson;
            /// <summary>SHA-256 of <see cref="Djson"/>.</summary>
            public readonly string Version;

            public Template(string name, string djson)
            {
                Name = name;
                Djson = djson;
                Version = Sha256(Encoding.UTF8.GetBytes(djson));
            }
        }

        private static readonly object InstallGate = new object();
        private static volatile Template[] _pending = new Template[0];

        /// <summary>Dashboards with a new version waiting on the user. Any thread.</summary>
        internal static IReadOnlyList<string> PendingNames => _pending.Select(t => t.Name).ToList();

        /// <summary>
        /// Check and install the dashboards once Dash Studio's dashboard list exists
        /// (<see cref="EditorModel.LoadFromFile"/> needs it): now, or when SimHub
        /// first raises <see cref="PluginManager.DashboardListUpdated"/>.
        /// </summary>
        internal static void InstallWhenDashStudioReady(PluginManager pluginManager, bool fahrenheit)
        {
            var culture = CultureInfo.CurrentUICulture;
            int started = 0;
            void Start()
            {
                if (Interlocked.Exchange(ref started, 1) == 0) InstallInBackground(fahrenheit, culture);
            }
            EventHandler? onListUpdated = null;
            onListUpdated = (s, e) =>
            {
                pluginManager.DashboardListUpdated -= onListUpdated;
                Start();
            };
            pluginManager.DashboardListUpdated += onListUpdated;
            if (GraphicalDashPlugin.GetSettings() != null)
            {
                pluginManager.DashboardListUpdated -= onListUpdated;
                Start();
            }
        }

        private static void InstallInBackground(bool fahrenheit, CultureInfo culture)
        {
            Task.Run(() =>
            {
                Thread.CurrentThread.CurrentUICulture = culture;
                var pending = new List<Template>();
                foreach (var (name, build) in new (string, Func<string>)[]
                {
                    (ControlDashBuilder.DashName, () => ControlDashBuilder.Build(fahrenheit)),
                    (BitePointDashBuilder.DashName, BitePointDashBuilder.Build),
                    (BitePointDashBuilder.MiniDashName, BitePointDashBuilder.BuildMini),
                })
                {
                    try
                    {
                        var template = new Template(name, build());
                        if (NeedsConfirmation(template)) pending.Add(template);
                    }
                    catch (Exception ex)
                    {
                        MozaLog.Warn($"[AZOM] {name} dashboard install failed: {ex.Message}");
                    }
                }
                _pending = pending.ToArray();
            });
        }

        /// <summary>Install every pending dashboard, replacing the user's copy (kept in <c>_Backups</c>).</summary>
        internal static void InstallPending()
        {
            var pending = Interlocked.Exchange(ref _pending, new Template[0]);
            if (pending.Length == 0) return;
            // EditorModel.SaveToFile waits on the UI dispatcher: never run it there.
            Task.Run(() =>
            {
                foreach (var template in pending)
                {
                    try { Install(template); }
                    catch (Exception ex) { MozaLog.Warn($"[AZOM] {template.Name} dashboard install failed: {ex.Message}"); }
                }
            });
        }

        /// <summary>Keep the user's copies; ask again only for a later version.</summary>
        internal static void DismissPending(MozaPlugin plugin)
        {
            var pending = Interlocked.Exchange(ref _pending, new Template[0]);
            var settings = plugin.Settings;
            if (pending.Length == 0 || settings == null) return;
            foreach (var template in pending)
                settings.DashTemplateDismissedVersions[template.Name] = template.Version;
            plugin.PersistSettings();
        }

        /// <summary>Install a new or new-version dashboard whose copy is untouched;
        /// true when the copy was changed outside the plugin and the user decides.</summary>
        private static bool NeedsConfirmation(Template template)
        {
            string djsonPath = DjsonPath(template.Name, out string root);
            if (!Directory.Exists(root))
            {
                MozaLog.Info($"[AZOM] {template.Name} dashboard not installed: {root} not found");
                return false;
            }
            if (!File.Exists(djsonPath))
            {
                Install(template);
                return false;
            }

            // Line 1: installed version; line 2: SHA-256 of the file as saved. A
            // single line (1.7.5) is the SHA-256 of the file as written.
            string stampPath = djsonPath + ".azom";
            string[] stamp = File.Exists(stampPath) ? File.ReadAllLines(stampPath) : new string[0];
            string? installed = stamp.Length >= 2 ? stamp[0].Trim() : null;
            string? saved = stamp.Length >= 2 ? stamp[1].Trim() : stamp.Length == 1 ? stamp[0].Trim() : null;
            if (installed == template.Version) return false;

            if (saved != null && string.Equals(Sha256(File.ReadAllBytes(djsonPath)), saved, StringComparison.OrdinalIgnoreCase))
            {
                Install(template);
                return false;
            }
            var dismissed = MozaPlugin.Instance?.Settings?.DashTemplateDismissedVersions;
            if (dismissed != null && dismissed.TryGetValue(template.Name, out var version) && version == template.Version)
                return false;
            MozaLog.Info($"[AZOM] {template.Name} dashboard: new version waiting for confirmation, {djsonPath} was saved outside the plugin");
            return true;
        }

        /// <summary>Have Dash Studio load the generated djson and save it as the
        /// dashboard, with thumbnails. Not on the UI thread.</summary>
        private static void Install(Template template)
        {
            lock (InstallGate)
            {
                string djsonPath = DjsonPath(template.Name, out _);
                var dispatcher = Application.Current?.Dispatcher
                                 ?? throw new InvalidOperationException("no SimHub UI dispatcher");

                // Loading creates JavascriptExtensions/Videos next to the file, so stage it in its own folder.
                string staging = Path.Combine(Path.GetTempPath(), "AZOM-dash-" + Guid.NewGuid().ToString("N"));
                string source = Path.Combine(staging, template.Name + ".djson");
                string? before = File.Exists(djsonPath) ? Sha256(File.ReadAllBytes(djsonPath)) : null;
                try
                {
                    Directory.CreateDirectory(staging);
                    File.WriteAllText(source, template.Djson, new UTF8Encoding(false));
                    // The model's WPF objects belong to the UI thread; SaveToFile dispatches to it itself.
                    var model = dispatcher.Invoke(() => EditorModel.LoadFromFile(source, false));
                    Directory.CreateDirectory(Path.GetDirectoryName(djsonPath)!);
                    model.SaveToFile(djsonPath, true);
                }
                finally
                {
                    try { Directory.Delete(staging, true); } catch { }
                    // Stamp whenever the djson was replaced, even if a later save step
                    // (metadata, thumbnails) failed: it is ours now, not the user's.
                    string? after = File.Exists(djsonPath) ? Sha256(File.ReadAllBytes(djsonPath)) : null;
                    if (after != null && after != before)
                        File.WriteAllLines(djsonPath + ".azom", new[] { template.Version, after }, new UTF8Encoding(false));
                }
                MozaLog.Info($"[AZOM] {template.Name} dashboard installed to {Path.GetDirectoryName(djsonPath)}");
            }
        }

        private static string DjsonPath(string name, out string root)
        {
            // Same relative path SimHub's GraphicalDashPluginList scans.
            root = Path.GetFullPath("DashTemplates");
            return Path.Combine(root, name, name + ".djson");
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder(64);
                foreach (byte b in sha.ComputeHash(bytes)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
