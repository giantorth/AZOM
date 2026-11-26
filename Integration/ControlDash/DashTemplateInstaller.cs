using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MozaPlugin.Integration.ControlDash
{
    /// <summary>
    /// Keeps the generated dashboards in <c>DashTemplates/&lt;name&gt;/</c> in step
    /// with their builders (<see cref="ControlDashBuilder"/>,
    /// <see cref="BitePointDashBuilder"/>). Rewrites only when the generated
    /// bytes differ (plugin update, language or temperature-unit change), and
    /// never over a copy the user edited in Dash Studio: the sidecar
    /// <c>.djson.azom</c> holds the SHA-256 of what was last written, so any
    /// other on-disk content is left alone. SimHub's DashTemplates watcher
    /// picks the folder up without a restart.
    /// </summary>
    internal static class DashTemplateInstaller
    {
        internal static void InstallInBackground(bool fahrenheit)
        {
            var culture = CultureInfo.CurrentUICulture;
            Task.Run(() =>
            {
                Thread.CurrentThread.CurrentUICulture = culture;
                Install(ControlDashBuilder.DashName, () => ControlDashBuilder.Build(fahrenheit));
                Install(BitePointDashBuilder.DashName, BitePointDashBuilder.Build);
            });
        }

        private static void Install(string name, Func<DashOutput> build)
        {
            try
            {
                // Same relative path SimHub's GraphicalDashPluginList scans.
                string root = Path.GetFullPath("DashTemplates");
                if (!Directory.Exists(root))
                {
                    MozaLog.Info($"[AZOM] {name} dashboard not installed: {root} not found");
                    return;
                }

                string dir = Path.Combine(root, name);
                string djsonPath = Path.Combine(dir, name + ".djson");
                string metaPath = djsonPath + ".metadata";
                string stampPath = djsonPath + ".azom";

                var output = build();
                var utf8 = new UTF8Encoding(false);
                byte[] djson = utf8.GetBytes(output.Djson);
                byte[] meta = utf8.GetBytes(output.Metadata);
                string newHash = Sha256(djson);

                if (File.Exists(djsonPath))
                {
                    string onDisk = Sha256(File.ReadAllBytes(djsonPath));
                    string? stamp = File.Exists(stampPath) ? File.ReadAllText(stampPath).Trim() : null;
                    if (!string.Equals(stamp, onDisk, StringComparison.OrdinalIgnoreCase))
                    {
                        MozaLog.Info($"[AZOM] {name} dashboard left as-is: {djsonPath} was modified outside the plugin");
                        return;
                    }
                    if (onDisk == newHash && File.Exists(metaPath) && Sha256(File.ReadAllBytes(metaPath)) == Sha256(meta))
                        return; // up to date
                }

                Directory.CreateDirectory(dir);
                WriteAtomic(metaPath, meta);
                WriteAtomic(djsonPath, djson);
                File.WriteAllText(stampPath, newHash, utf8);
                MozaLog.Info($"[AZOM] {name} dashboard written to {dir}");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] {name} dashboard install failed: {ex.Message}");
            }
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
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
