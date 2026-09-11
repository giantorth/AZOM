using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>The images a converted dashboard references.</summary>
    public sealed class ImageSet
    {
        /// <summary>SimHub image name (no extension) → the mzdash-relative
        /// <c>MD5/&lt;md5&gt;.&lt;ext&gt;</c> path.</summary>
        public Dictionary<string, string> ByName { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The same paths, for the document's <c>imageResources</c> array.</summary>
        public List<string> Resources { get; } = new List<string>();
    }

    /// <summary>
    /// Extracts a SimHub dashboard's images into the content-addressed layout the wheel
    /// expects.
    ///
    /// <para>SimHub does <b>not</b> embed image bytes in the <c>.djson</c> — the top-level
    /// <c>Images[]</c> array is metadata only (name, extension, size, MD5) and the bytes
    /// live in a sibling ZIP named <c>&lt;Dashboard&gt;.djson.ressources</c> (SimHub's own
    /// French spelling, two s). Items reference an image by bare name.</para>
    ///
    /// <para>Files are written to <c>Resource/MD5/&lt;md5&gt;.&lt;ext&gt;</c> beside the
    /// dashboard, which is where <c>DashboardUploader</c> looks when building the upload
    /// bundle, <b>and</b> copied into Dashboard Studio's shared image pool — Studio
    /// resolves <c>image.src</c> against its <c>imageRoot</c>, not the project folder, so
    /// without the second copy the editor shows blanks even though the wheel is fine.</para>
    /// </summary>
    public static class ResourceExtractor
    {
        /// <summary>Extensions the upload path's image scanner recognises.</summary>
        private static readonly HashSet<string> AllowedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

        /// <summary>Extract every image the dashboard could reference.</summary>
        /// <param name="djsonPath">The source dashboard file.</param>
        /// <param name="outputDir">The converted dashboard's folder; images land under
        /// <c>Resource/MD5/</c> inside it.</param>
        /// <param name="studioImageRoot">Dashboard Studio's shared image pool, or null to
        /// skip that copy.</param>
        public static ImageSet Extract(string djsonPath, string outputDir,
                                       string? studioImageRoot, ConversionReport report)
        {
            var set = new ImageSet();
            string md5Dir = Path.Combine(outputDir, "Resource", "MD5");

            foreach (var (name, ext, bytes) in EnumerateSources(djsonPath, report))
            {
                if (!AllowedExtensions.Contains(ext))
                {
                    report.Notes.Add($"image '{name}{ext}' skipped — "
                                   + "the upload path only carries png/jpg/jpeg/bmp/gif");
                    continue;
                }

                // Recompute rather than trusting Images[].MD5: the bundle is
                // content-addressed and a stale hash would point the wheel at nothing.
                string hash = Md5Hex(bytes);
                string relative = $"MD5/{hash}{ext.ToLowerInvariant()}";

                if (!set.ByName.ContainsKey(name)) set.ByName[name] = relative;
                if (!set.Resources.Contains(relative)) set.Resources.Add(relative);

                Write(Path.Combine(md5Dir, $"{hash}{ext.ToLowerInvariant()}"), bytes, report);

                if (!string.IsNullOrEmpty(studioImageRoot))
                {
                    Write(Path.Combine(studioImageRoot!, "MD5", $"{hash}{ext.ToLowerInvariant()}"),
                          bytes, report);
                }
            }

            return set;
        }

        /// <summary>Images from the sibling archive first, then any loose files beside the
        /// dashboard (a handful of stock templates ship one that way).</summary>
        private static IEnumerable<(string name, string ext, byte[] bytes)> EnumerateSources(
            string djsonPath, ConversionReport report)
        {
            string archive = djsonPath + ".ressources";
            if (File.Exists(archive))
            {
                foreach (var entry in ReadArchive(archive, report)) yield return entry;
            }

            string? dir = Path.GetDirectoryName(djsonPath);
            if (string.IsNullOrEmpty(dir)) yield break;

            foreach (var file in SafeListFiles(dir!, report))
            {
                string ext = Path.GetExtension(file);
                if (!AllowedExtensions.Contains(ext)) continue;

                // SimHub's own per-screen preview thumbnails are named after the
                // dashboard (Foo.djson.png, Foo.djson.00.png) and are not content.
                string fileName = Path.GetFileName(file);
                if (fileName.IndexOf(".djson.", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                byte[] bytes;
                try { bytes = File.ReadAllBytes(file); }
                catch (Exception ex)
                {
                    report.Notes.Add($"could not read '{fileName}': {ex.Message}");
                    continue;
                }
                yield return (Path.GetFileNameWithoutExtension(file), ext, bytes);
            }
        }

        private static IEnumerable<(string name, string ext, byte[] bytes)> ReadArchive(
            string archivePath, ConversionReport report)
        {
            var results = new List<(string, string, byte[])>();
            try
            {
                using var zip = ZipFile.OpenRead(archivePath);
                foreach (var entry in zip.Entries)
                {
                    if (entry.Length == 0) continue;
                    try
                    {
                        using var stream = entry.Open();
                        using var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        results.Add((Path.GetFileNameWithoutExtension(entry.Name),
                                     Path.GetExtension(entry.Name),
                                     ms.ToArray()));
                    }
                    catch (Exception ex)
                    {
                        report.Notes.Add($"image '{entry.Name}' failed to extract: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                report.Notes.Add($"resource archive '{Path.GetFileName(archivePath)}' "
                               + $"could not be opened: {ex.Message}");
            }
            return results;
        }

        private static IEnumerable<string> SafeListFiles(string dir, ConversionReport report)
        {
            try { return Directory.GetFiles(dir); }
            catch (Exception ex)
            {
                report.Notes.Add($"could not list '{dir}': {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private static void Write(string path, byte[] bytes, ConversionReport report)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Content-addressed: identical bytes, identical name. Rewriting is
                // pointless and would churn Studio's shared pool on every conversion.
                if (File.Exists(path) && new FileInfo(path).Length == bytes.Length) return;

                File.WriteAllBytes(path, bytes);
            }
            catch (Exception ex)
            {
                report.Notes.Add($"could not write '{path}': {ex.Message}");
            }
        }

        internal static string Md5Hex(byte[] bytes)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(bytes);
            var sb = new StringBuilder(32);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
