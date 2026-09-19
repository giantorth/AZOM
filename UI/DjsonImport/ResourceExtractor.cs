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

        internal string OutputDir { get; set; } = "";
        internal string? StudioImageRoot { get; set; }
        internal string? SimHubRoot { get; set; }
        internal ConversionReport? Report { get; set; }

        /// <summary>
        /// Resolve an image reference from an item to its <c>MD5/…</c> path.
        ///
        /// <para>Names are looked up in what the archives yielded. A <c>library:</c>
        /// reference — <c>library:Icons\ABS.png</c> — is SimHub's shared image library
        /// at <c>&lt;SimHub&gt;/ImageLibrary/</c>, never in a dashboard's own archive, so
        /// it is read from there on first use and content-addressed like the rest.</para>
        /// </summary>
        public bool TryResolve(string? name, out string src)
        {
            src = "";
            string n = (name ?? "").Trim();
            if (n.Length == 0) return false;
            if (ByName.TryGetValue(n, out var hit)) { src = hit; return true; }

            const string prefix = "library:";
            if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(SimHubRoot)) return false;

            string relative = n.Substring(prefix.Length).Replace('\\', Path.DirectorySeparatorChar)
                                                        .Replace('/', Path.DirectorySeparatorChar)
                                                        .TrimStart(Path.DirectorySeparatorChar);
            string path = Path.Combine(SimHubRoot!, "ImageLibrary", relative);
            if (!File.Exists(path)) return false;

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                Report?.Notes.Add($"library image '{relative}' could not be read: {ex.Message}");
                return false;
            }

            if (!ResourceExtractor.Register(this, n, Path.GetExtension(path), bytes)) return false;
            src = ByName[n];
            return true;
        }
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
        /// <param name="simHubRoot">The SimHub install, for <c>library:</c> image references
        /// (<c>&lt;root&gt;/ImageLibrary/</c>). Null when unknown; those images then report
        /// as not found.</param>
        public static ImageSet Extract(string djsonPath, string outputDir,
                                       string? studioImageRoot, ConversionReport report,
                                       string? simHubRoot = null)
        {
            var set = new ImageSet
            {
                OutputDir = outputDir,
                StudioImageRoot = studioImageRoot,
                SimHubRoot = simHubRoot,
                Report = report,
            };

            foreach (var (name, ext, bytes) in EnumerateSources(djsonPath, report))
                Register(set, name, ext, bytes);

            return set;
        }

        /// <summary>
        /// Content-address one image into the set and write it beside the dashboard (and
        /// into Studio's pool). The type comes from the bytes, not the name: SimHub stores
        /// some archive entries with no extension at all, and a PNG called <c>RPM</c> is
        /// still a PNG.
        /// </summary>
        internal static bool Register(ImageSet set, string name, string declaredExt, byte[] bytes)
        {
            var report = set.Report;
            string ext = SniffExtension(bytes) ?? declaredExt;
            if (!AllowedExtensions.Contains(ext))
            {
                report?.Notes.Add($"image '{name}{declaredExt}' skipped — not a png/jpg/jpeg/bmp/gif");
                return false;
            }
            ext = ext.ToLowerInvariant();

            // Recompute rather than trusting Images[].MD5: the bundle is content-addressed
            // and a stale hash would point the wheel at nothing.
            string hash = Md5Hex(bytes);
            string relative = $"MD5/{hash}{ext}";

            if (!set.ByName.ContainsKey(name)) set.ByName[name] = relative;
            if (!set.Resources.Contains(relative)) set.Resources.Add(relative);

            Write(Path.Combine(set.OutputDir, "Resource", "MD5", $"{hash}{ext}"), bytes, report);
            if (!string.IsNullOrEmpty(set.StudioImageRoot))
                Write(Path.Combine(set.StudioImageRoot!, "MD5", $"{hash}{ext}"), bytes, report);
            return true;
        }

        /// <summary>File type from the first bytes, or null when unrecognised.</summary>
        private static string? SniffExtension(byte[] b)
        {
            if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
            if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
            if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return ".gif";
            if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return ".bmp";
            return null;
        }

        /// <summary>
        /// Images from every <c>.ressources</c> archive beside the dashboard, then any
        /// loose files (a handful of stock templates ship one that way).
        ///
        /// <para>The dashboard's own archive is read first so its names win, but the
        /// sibling archives matter just as much: SimHub keeps one per <c>.djson</c>, and a
        /// dashboard built from <c>WidgetItem</c> includes — as the community ones are —
        /// holds most of its artwork in the widgets' archives, not its own. Reading only
        /// the main file's leaves every inlined widget's images blank.</para>
        /// </summary>
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

            foreach (var sibling in SafeListFiles(dir!, report, "*.djson.ressources"))
            {
                if (string.Equals(sibling, archive, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var entry in ReadArchive(sibling, report)) yield return entry;
            }

            // Widgets included from SimHub's shared _Library tree keep their images in
            // their own archives there, not in the host dashboard's folder.
            string? parent = null;
            try { parent = Directory.GetParent(dir!)?.FullName; } catch { }
            if (parent != null)
            {
                string library = Path.Combine(parent, "_Library");
                if (Directory.Exists(library))
                {
                    IEnumerable<string> archives;
                    try { archives = Directory.GetFiles(library, "*.djson.ressources", SearchOption.AllDirectories); }
                    catch (Exception ex)
                    {
                        report.Notes.Add($"could not scan '{library}': {ex.Message}");
                        archives = Array.Empty<string>();
                    }
                    foreach (var a in archives)
                        foreach (var entry in ReadArchive(a, report)) yield return entry;
                }
            }

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

        private static IEnumerable<string> SafeListFiles(string dir, ConversionReport report,
                                                         string pattern = "*")
        {
            try { return Directory.GetFiles(dir, pattern); }
            catch (Exception ex)
            {
                report.Notes.Add($"could not list '{dir}': {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private static void Write(string path, byte[] bytes, ConversionReport? report)
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
                report?.Notes.Add($"could not write '{path}': {ex.Message}");
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
