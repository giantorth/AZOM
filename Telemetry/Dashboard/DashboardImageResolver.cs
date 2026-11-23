using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// Finds the widget images an mzdash references (<c>MD5/&lt;32hex&gt;.&lt;ext&gt;</c>)
    /// on disk. Shared by the upload bundle and the Studio preview render.
    /// </summary>
    internal static class DashboardImageResolver
    {
        internal readonly struct ImageRef
        {
            public ImageRef(string hex, string ext, string? path, IReadOnlyList<string> searched)
            {
                Hex = hex;
                Ext = ext;
                Path = path;
                Searched = searched;
            }

            /// <summary>Lowercase 32-hex content hash.</summary>
            public string Hex { get; }
            /// <summary>Lowercase extension, no dot.</summary>
            public string Ext { get; }
            /// <summary>Resolved file, or null when no search directory has it.</summary>
            public string? Path { get; }
            /// <summary>Every path tried, for the not-found log.</summary>
            public IReadOnlyList<string> Searched { get; }
            public string FileName => Hex + "." + Ext;
        }

        // Forward slash in widget src attributes, backslash in some
        // PitHouse-generated JSON. The wheel's image store keeps the original
        // extension (a 2026-08-16 PitHouse capture bundled
        // `/home/moza/resource/images/MD5/<hex>.jpg`), so every raster format
        // the display firmware renders is accepted.
        private static readonly Regex ImageRefRegex = new Regex(
            @"MD5[/\\]([0-9a-fA-F]{32})\.(png|jpg|jpeg|bmp|gif)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Directories to search for <c>&lt;32hex&gt;.&lt;ext&gt;</c>, most
        /// specific first.
        /// <list type="bullet">
        /// <item><c>&lt;dir&gt;/Resource/MD5/</c> — a dashboard downloaded from
        /// the wheel, or a PitHouse export bundle, carries its own copies.
        /// Lowercase variants too: Windows is case-insensitive, Proton is not.</item>
        /// <item><c>&lt;imageRoot&gt;/MD5/</c> — the SHARED pool Dashboard Studio
        /// authors against (default <c>%LOCALAPPDATA%\MOZA Pit House\_dashes\images</c>).
        /// Studio never copies an image into the project folder, so a Studio
        /// project has no <c>Resource/</c> subtree at all.</item>
        /// </list>
        /// </summary>
        public static IEnumerable<string> SearchDirectories(string? sourceDirectory)
        {
            if (!string.IsNullOrEmpty(sourceDirectory))
            {
                yield return System.IO.Path.Combine(sourceDirectory!, "Resource", "MD5");
                yield return System.IO.Path.Combine(sourceDirectory!, "resource", "MD5");
                yield return System.IO.Path.Combine(sourceDirectory!, "Resource", "md5");
                yield return System.IO.Path.Combine(sourceDirectory!, "resource", "md5");
            }

            string? imageRoot = null;
            try { imageRoot = UI.DashboardStudioLauncher.ResolveImageRoot(); }
            catch { /* discovery is best-effort; the sourceDir paths still apply */ }
            if (!string.IsNullOrEmpty(imageRoot))
            {
                yield return System.IO.Path.Combine(imageRoot!, "MD5");
                yield return System.IO.Path.Combine(imageRoot!, "md5");
            }
        }

        /// <summary>Each distinct image the mzdash text references, resolved or not.</summary>
        public static List<ImageRef> Resolve(string mzdashText, string? sourceDirectory)
        {
            var result = new List<ImageRef>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string>? dirs = null;
            foreach (Match m in ImageRefRegex.Matches(mzdashText))
            {
                string hex = m.Groups[1].Value.ToLowerInvariant();
                string ext = m.Groups[2].Value.ToLowerInvariant();
                if (!seen.Add(hex)) continue;

                dirs ??= new List<string>(SearchDirectories(sourceDirectory));
                string fileName = hex + "." + ext;
                var searched = new List<string>(dirs.Count);
                string? found = null;
                foreach (var dir in dirs)
                {
                    string p = System.IO.Path.Combine(dir, fileName);
                    searched.Add(p);
                    if (File.Exists(p)) { found = p; break; }
                }
                result.Add(new ImageRef(hex, ext, found, searched));
            }
            return result;
        }
    }
}
