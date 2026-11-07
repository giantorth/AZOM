using System;
using System.IO;
using System.Reflection;
using MozaPlugin.Diagnostics;
using MozaPlugin.Telemetry.Dashboard;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Resolves dashboard library entries (cache + builtin embedded resources)
    /// to source directories and raw mzdash bytes. Used by the upload UI to
    /// resolve "pick from library" selections.
    /// </summary>
    internal static class DashboardLibraryResolver
    {
        /// <summary>
        /// Resolve the on-disk directory for a library-picked dashboard so the
        /// upload bundle can find sibling PNG widget assets at
        /// <c>&lt;dir&gt;/Resource/MD5/&lt;hex&gt;.png</c>. Returns empty when
        /// the dashboard came from the wheel cache or an embedded builtin
        /// (no source directory) — the upload then ships single-file.
        /// </summary>
        public static string ResolveDirectory(DashboardCache? dashCache, string name)
        {
            if (dashCache == null) return "";
            string? filePath = dashCache.TryGetFolderFilePath(name);
            if (string.IsNullOrEmpty(filePath)) return "";
            return Path.GetDirectoryName(filePath) ?? "";
        }

        /// <summary>
        /// Resolve raw mzdash bytes for a library entry. A folder entry is
        /// re-read from its file: the library scan is a snapshot, and an edit
        /// saved since (Studio, PitHouse) must be what uploads — the stale
        /// bytes hash-match the wheel's copy and the upload is skipped. Then
        /// the cache (folder snapshot or wheel download), then the embedded
        /// builtin. Returns null when nothing resolves or the file exists but
        /// can't be read (e.g. mid-save).
        /// </summary>
        public static byte[]? ResolveBytes(
            DashboardCache? dashCache,
            DashboardProfileStore profileStore,
            string name)
        {
            if (dashCache != null)
            {
                string? path = dashCache.TryGetFolderFilePath(name);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    try { return File.ReadAllBytes(path); }
                    catch (Exception ex)
                    {
                        MozaLog.Warn($"[AZOM] Cannot read dashboard '{name}' from {path}: {ex.Message}");
                        return null;
                    }
                }
                var bytes = dashCache.TryGetRawContent(name);
                if (bytes != null) return bytes;
            }
            // Builtins: read from embedded resource (mirrors
            // MozaPlugin.ApplyTelemetrySettings' builtin fallback).
            foreach (var p in profileStore.BuiltinProfiles)
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                string resourceName = $"MozaPlugin.Data.Dashes.{p.Name.Replace(" ", "_")}.mzdash";
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) return null;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
            return null;
        }
    }
}
