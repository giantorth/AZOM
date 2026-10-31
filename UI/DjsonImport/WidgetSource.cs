using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>
    /// The widget <c>.djson</c> files a dashboard includes, located and parsed once per
    /// conversion and shared by every pass. A dashboard can place one widget dozens of
    /// times, and the converter walks the tree several times. Parsed trees are only read.
    /// One instance per conversion; not thread-safe.
    /// </summary>
    public sealed class WidgetSource
    {
        private readonly string _sourceDir;
        private readonly Dictionary<string, (string? path, string? lookupError)> _paths =
            new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (JObject? root, string error)> _roots =
            new Dictionary<string, (JObject?, string)>(StringComparer.OrdinalIgnoreCase);

        public WidgetSource(string sourceDir)
        {
            _sourceDir = sourceDir;
        }

        /// <summary>Same folder first, then SimHub's shared <c>_Library</c> tree one level
        /// up. <paramref name="lookupError"/> is set when the <c>_Library</c> scan threw.</summary>
        public string? Resolve(string fileName, out string? lookupError)
        {
            if (!_paths.TryGetValue(fileName, out var hit))
            {
                hit = Find(fileName);
                _paths[fileName] = hit;
            }
            lookupError = hit.lookupError;
            return hit.path;
        }

        /// <summary>The parsed file, or a null root plus the parse error.</summary>
        public (JObject? root, string error) Load(string path)
        {
            if (!_roots.TryGetValue(path, out var hit))
            {
                hit = DjsonReader.Load(path);
                _roots[path] = hit;
            }
            return hit;
        }

        private (string? path, string? lookupError) Find(string fileName)
        {
            string direct = Path.Combine(_sourceDir, fileName);
            if (File.Exists(direct)) return (direct, null);

            try
            {
                var parent = Directory.GetParent(_sourceDir);
                if (parent == null) return (null, null);
                string library = Path.Combine(parent.FullName, "_Library");
                if (!Directory.Exists(library)) return (null, null);

                foreach (var hit in Directory.GetFiles(library, fileName, SearchOption.AllDirectories))
                    return (hit, null);
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
            return (null, null);
        }
    }
}
