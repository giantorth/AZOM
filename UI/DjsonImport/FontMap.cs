using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using MozaPlugin.Diagnostics;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>
    /// SimHub font family → a family the wheel actually has.
    ///
    /// <para>The targets are the family strings observed in factory dashboards, NOT the
    /// <c>.ttf</c> filenames in PitHouse's <c>bin/DashboardFonts</c> — those differ
    /// (<c>Arame-Mono.ttf</c> is <c>0Arame</c>, <c>Frizon-2.ttf</c> is <c>FRIZON</c>).
    /// Emitting a filename gives a silent fallback on the wheel, which is why the table
    /// is data rather than derived from a font-directory scan.</para>
    /// </summary>
    public sealed class FontMap
    {
        private readonly Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _available =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Default { get; private set; } = "Lato";

        public FontMap()
        {
            var root = LoadEmbedded("MozaPlugin.Data.DjsonFontMap.json");
            if (root == null) return;

            string? dflt = (string?)root["default"];
            if (!string.IsNullOrWhiteSpace(dflt)) Default = dflt!.Trim();

            if (root["available"] is JArray avail)
                foreach (var a in avail)
                    if (a.Type == JTokenType.String) _available.Add(((string?)a ?? "").Trim());

            if (root["map"] is JObject map)
            {
                foreach (var prop in map.Properties())
                {
                    if (prop.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                    if (prop.Value?.Type != JTokenType.String) continue;
                    string target = ((string?)prop.Value ?? "").Trim();
                    if (target.Length == 0) continue;

                    // A mapping that points outside the available set would render as
                    // the firmware's fallback without saying so — catch it at load.
                    if (_available.Count > 0 && !_available.Contains(target))
                    {
                        MozaLog.Warn($"[AZOM] DjsonFontMap: '{prop.Name}' -> '{target}' "
                                   + "is not in the available list — ignored");
                        continue;
                    }
                    _map[prop.Name.Trim()] = target;
                }
            }
        }

        /// <summary>Resolve a SimHub family name. <paramref name="mapped"/> is false when
        /// the family had no entry and the default was substituted, so the report can
        /// list it as a candidate for the table.</summary>
        public string Resolve(string? family, out bool mapped)
        {
            string f = (family ?? "").Trim();
            mapped = true;
            if (f.Length == 0) return Default;

            // A family the wheel already has needs no remap.
            if (_available.Contains(f)) return f;
            if (_map.TryGetValue(f, out var target)) return target;

            mapped = false;
            return Default;
        }

        private static JObject? LoadEmbedded(string logicalName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(logicalName);
                if (stream == null)
                {
                    MozaLog.Warn($"[AZOM] DjsonImport: embedded resource '{logicalName}' not found");
                    return null;
                }
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return JObject.Parse(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] DjsonImport: failed to load '{logicalName}': {ex.Message}");
                return null;
            }
        }
    }
}
