using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MozaPlugin.Diagnostics;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>
    /// A SimHub <c>.simhubdash</c> bundle — the distribution format for a complete
    /// dashboard.
    ///
    /// <para>It is a ZIP of one <c>DashTemplates</c> folder: the main <c>.djson</c>, every
    /// widget <c>.djson</c> it includes, their <c>.ressources</c> image archives, preview
    /// PNGs, <c>.metadata</c>/<c>.carclasses</c> sidecars, and sometimes a <c>_SHFonts</c>
    /// folder of the fonts the author used.</para>
    ///
    /// <para>Rather than teach every stage of the pipeline to read from a ZIP, the bundle
    /// is expanded to a temporary folder and the existing file-based path runs over it
    /// unchanged — which matters because widget inlining and the <c>.ressources</c> lookup
    /// both resolve siblings by path.</para>
    ///
    /// <para>Two traps in the archive format: entries use <b>backslash</b> separators
    /// (they are written on Windows), so they have to be normalised or the whole tree
    /// lands as single files with backslashes in their names; and entry paths are attacker-
    /// controlled, so each one is checked to stay inside the extraction root.</para>
    /// </summary>
    public sealed class SimhubDashBundle : IDisposable
    {
        public const string Extension = ".simhubdash";

        private readonly string _root;
        private bool _disposed;

        /// <summary>The dashboard to convert.</summary>
        public string MainDjsonPath { get; }

        /// <summary>Where the bundle was expanded. Valid until <see cref="Dispose"/>.</summary>
        public string RootDirectory => _root;

        /// <summary>Every <c>.djson</c> the bundle holds, main file first.</summary>
        public IReadOnlyList<string> AllDjson { get; }

        private SimhubDashBundle(string root, string mainDjson, IReadOnlyList<string> allDjson)
        {
            _root = root;
            MainDjsonPath = mainDjson;
            AllDjson = allDjson;
        }

        /// <summary>True when the path names a bundle rather than a bare dashboard.</summary>
        public static bool IsBundle(string? path)
            => !string.IsNullOrEmpty(path)
               && string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Expand a bundle to a temporary folder. Throws <see cref="InvalidDataException"/>
        /// when the archive holds no dashboard.
        /// </summary>
        public static SimhubDashBundle Open(string bundlePath, ConversionReport? report = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "MozaDjsonImport",
                                       Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            int files = 0, skipped = 0;
            try
            {
                using (var zip = ZipFile.OpenRead(bundlePath))
                {
                    foreach (var entry in zip.Entries)
                    {
                        // A directory entry has an empty Name; the tree is created from
                        // the file paths anyway.
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        string? target = SafeTargetPath(root, entry.FullName);
                        if (target == null)
                        {
                            skipped++;
                            report?.Notes.Add($"bundle entry '{entry.FullName}' skipped — "
                                            + "its path escapes the archive folder");
                            continue;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, overwrite: true);
                        files++;
                    }
                }
            }
            catch
            {
                TryDelete(root);
                throw;
            }

            var djson = Directory
                .GetFiles(root, "*.djson", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (djson.Count == 0)
            {
                TryDelete(root);
                throw new InvalidDataException(
                    $"'{Path.GetFileName(bundlePath)}' contains no .djson dashboard");
            }

            string main = PickMain(djson, root, bundlePath);
            var ordered = new List<string> { main };
            ordered.AddRange(djson.Where(p => !string.Equals(p, main, StringComparison.OrdinalIgnoreCase)));

            report?.Notes.Add(
                $"bundle: {files} file(s) expanded, {djson.Count} dashboard(s), "
                + $"main '{Path.GetFileNameWithoutExtension(main)}'"
                + (skipped > 0 ? $", {skipped} skipped" : ""));

            MozaLog.Debug($"[AZOM] DjsonImport: expanded '{Path.GetFileName(bundlePath)}' "
                        + $"({files} files) to {root}");

            return new SimhubDashBundle(root, main, ordered);
        }

        /// <summary>
        /// Which <c>.djson</c> is the dashboard rather than one of its widgets.
        ///
        /// <para>Every bundle examined wraps the dashboard in a folder named after it and
        /// gives the main file the same basename, so that match comes first. The remaining
        /// rules are fallbacks for a bundle shaped differently: SimHub only writes the
        /// preview/metadata sidecars for the dashboard it exported, and failing that the
        /// main file is reliably the largest.</para>
        /// </summary>
        private static string PickMain(List<string> djson, string root, string bundlePath)
        {
            // <folder>/<folder>.djson
            foreach (var path in djson)
            {
                string? dir = Path.GetDirectoryName(path);
                if (dir == null) continue;
                if (string.Equals(Path.GetFileNameWithoutExtension(path),
                                  new DirectoryInfo(dir).Name, StringComparison.OrdinalIgnoreCase))
                    return path;
            }

            // Named after the bundle itself.
            string bundleName = Path.GetFileNameWithoutExtension(bundlePath);
            foreach (var path in djson)
                if (string.Equals(Path.GetFileNameWithoutExtension(path), bundleName,
                                  StringComparison.OrdinalIgnoreCase))
                    return path;

            // The one SimHub exported carries the sidecars.
            foreach (var path in djson)
                if (File.Exists(path + ".metadata") || File.Exists(path + ".png"))
                    return path;

            return djson.OrderByDescending(p => new FileInfo(p).Length).First();
        }

        /// <summary>Map a ZIP entry path to a file under <paramref name="root"/>, or null
        /// when it would escape. Normalises the backslash separators SimHub writes.</summary>
        private static string? SafeTargetPath(string root, string entryPath)
        {
            string relative = (entryPath ?? "")
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            if (relative.Length == 0) return null;

            string full = Path.GetFullPath(Path.Combine(root, relative));
            string rootFull = Path.GetFullPath(root);
            if (!rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                rootFull += Path.DirectorySeparatorChar;

            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TryDelete(_root);
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                // A leftover temp folder is harmless; losing the conversion to a file
                // lock during cleanup would not be.
                MozaLog.Debug($"[AZOM] DjsonImport: could not remove '{dir}': {ex.Message}");
            }
        }
    }
}
