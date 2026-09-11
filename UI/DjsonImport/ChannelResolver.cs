using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MozaPlugin.Diagnostics;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.DjsonImport
{
    /// <summary>One channel as the resolver needs to see it: its canonical URL and the
    /// SimHub property Telemetry.json binds it to. Keeping this a plain value rather than
    /// taking <c>DashboardProfileStore</c> directly is what lets the whole conversion
    /// pipeline run outside the plugin — see <c>PluginChannelCatalog</c> for the adapter.</summary>
    public readonly struct ChannelRow
    {
        public string Url { get; }
        public string DefaultProperty { get; }

        public ChannelRow(string url, string defaultProperty)
        {
            Url = url ?? "";
            DefaultProperty = defaultProperty ?? "";
        }
    }

    /// <summary>How a SimHub property reference was satisfied.</summary>
    public enum ChannelResolution
    {
        /// <summary>Telemetry.json's own <c>simhub_property</c> names this property.</summary>
        Direct,
        /// <summary>Data/DjsonPropertyMap.json <c>aliases</c> redirected it to a channel.</summary>
        Alias,
        /// <summary>Derived in wheel JS from channels that do exist.</summary>
        Synthetic,
        /// <summary>Known dead end — no channel exists and none is derivable.</summary>
        Unmappable,
        /// <summary>Not in any table. Needs investigation or the plugin-side NCalc fallback.</summary>
        Unknown,
    }

    /// <summary>One resolved property reference.</summary>
    public readonly struct ResolvedProperty
    {
        public ChannelResolution Kind { get; }
        /// <summary>A JavaScript fragment producing the value, for the wheel's
        /// <c>binding.methods[0]</c>. Empty when <see cref="IsUsable"/> is false.</summary>
        public string Js { get; }
        /// <summary>Channel URLs this fragment reads. Reported so the caller knows
        /// which channels the dashboard depends on.</summary>
        public IReadOnlyList<string> Urls { get; }
        /// <summary>Why it could not be resolved. Empty on success.</summary>
        public string Reason { get; }

        public ResolvedProperty(ChannelResolution kind, string js, IReadOnlyList<string> urls, string reason)
        {
            Kind = kind;
            Js = js;
            Urls = urls;
            Reason = reason;
        }

        public bool IsUsable => Kind == ChannelResolution.Direct
                             || Kind == ChannelResolution.Alias
                             || Kind == ChannelResolution.Synthetic;

        public static ResolvedProperty Fail(ChannelResolution kind, string reason)
            => new ResolvedProperty(kind, "", Array.Empty<string>(), reason);
    }

    /// <summary>
    /// Maps a SimHub property path (<c>[DataCorePlugin.GameData.NewData.SpeedKmh]</c>) onto
    /// a MOZA channel URL, then onto the <c>Telemetry.get("…").value</c> call the wheel's
    /// JS engine understands.
    ///
    /// <para>Three layers, tried in order: Telemetry.json's own <c>simhub_property</c>
    /// reverse index, then the alias table, then the synthetic-expression table. Anything
    /// still unresolved is reported rather than silently dropped.</para>
    ///
    /// <para>Referencing a channel is free: the plugin streams the wheel's whole live
    /// catalog regardless of what the dashboard binds (see the <c>profile = null</c> comment
    /// in <c>DashboardBindingCoordinator</c>), so there is no per-dashboard channel budget
    /// to allocate against here.</para>
    /// </summary>
    public sealed class ChannelResolver
    {
        // simhub property (normalised) -> channel url
        private readonly Dictionary<string, string> _direct =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _synthetic =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _unmappable =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every URL declared in Telemetry.json, for validating table targets.</summary>
        private readonly HashSet<string> _knownUrls =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // CH(v1/gameData/Foo) inside a synthetic template.
        private static readonly Regex ChTokenRegex =
            new Regex(@"CH\(\s*([A-Za-z0-9_/.&=]+)\s*\)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public ChannelResolver(IEnumerable<ChannelRow> catalog)
        {
            foreach (var entry in catalog)
            {
                if (string.IsNullOrEmpty(entry.Url)) continue;
                _knownUrls.Add(entry.Url);

                // Plugin-locked channels resolve internally and are never a
                // target for a dashboard binding.
                if (entry.DefaultProperty.StartsWith("@internal/", StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(entry.DefaultProperty)) continue;

                // First occurrence wins: three properties (ErsPercent, DrsAvailable,
                // Fuel) are claimed by two URLs each, and Telemetry.json lists the
                // primary channel first in every case.
                string key = Normalize(entry.DefaultProperty);
                if (!_direct.ContainsKey(key)) _direct[key] = entry.Url;
            }

            LoadPropertyMap();
        }

        /// <summary>Strip the optional <c>NewData.</c> segment so both spellings of a
        /// GameData property — SimHub dashboards write <c>GameData.NewData.SpeedKmh</c>,
        /// Telemetry.json writes <c>GameData.SpeedKmh</c> — hit the same key.</summary>
        internal static string Normalize(string? property)
        {
            string p = (property ?? "").Trim();
            return p.Replace("DataCorePlugin.GameData.NewData.", "DataCorePlugin.GameData.");
        }

        private void LoadPropertyMap()
        {
            var root = LoadEmbeddedJson("MozaPlugin.Data.DjsonPropertyMap.json");
            if (root == null) return;

            CopySection(root["aliases"] as JObject, _aliases, validateUrl: true);
            CopySection(root["synthetic"] as JObject, _synthetic, validateUrl: false);
            CopySection(root["unmappable"] as JObject, _unmappable, validateUrl: false);
        }

        private void CopySection(JObject? section, Dictionary<string, string> into, bool validateUrl)
        {
            if (section == null) return;
            foreach (var prop in section.Properties())
            {
                // Leading-underscore keys are documentation, and only string values
                // are entries — the _comment arrays are not.
                if (prop.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                if (prop.Value?.Type != JTokenType.String) continue;

                string value = ((string?)prop.Value ?? "").Trim();
                if (value.Length == 0) continue;

                // A table pointing at a URL that no longer exists would fail silently
                // on the wheel (the widget just reads NaN), so reject it at load.
                if (validateUrl && !_knownUrls.Contains(value))
                {
                    MozaLog.Warn($"[AZOM] DjsonPropertyMap: alias '{prop.Name}' targets "
                               + $"unknown channel '{value}' — ignored");
                    continue;
                }

                into[Normalize(prop.Name)] = value;
            }
        }

        private static JObject? LoadEmbeddedJson(string logicalName)
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

        /// <summary>Resolve one SimHub property path to a wheel-side JS fragment.</summary>
        public ResolvedProperty Resolve(string? property)
        {
            string key = Normalize(property);
            if (key.Length == 0) return ResolvedProperty.Fail(ChannelResolution.Unknown, "empty property");

            if (_direct.TryGetValue(key, out var url))
                return Read(ChannelResolution.Direct, url);

            if (_aliases.TryGetValue(key, out var aliased))
                return Read(ChannelResolution.Alias, aliased);

            if (_synthetic.TryGetValue(key, out var template))
                return ExpandSynthetic(template, key);

            if (_unmappable.TryGetValue(key, out var reason))
                return ResolvedProperty.Fail(ChannelResolution.Unmappable, reason);

            return ResolvedProperty.Fail(ChannelResolution.Unknown,
                "no channel in Telemetry.json and no entry in DjsonPropertyMap.json");
        }

        private static ResolvedProperty Read(ChannelResolution kind, string url)
            => new ResolvedProperty(kind, ChannelRead(url), new[] { url }, "");

        /// <summary>The wheel-side read for one channel. Double quotes match the
        /// dominant ground-truth form and survive JSON escaping unambiguously.</summary>
        internal static string ChannelRead(string url) => $"Telemetry.get(\"{url}\").value";

        private ResolvedProperty ExpandSynthetic(string template, string key)
        {
            var urls = new List<string>();
            bool bad = false;
            string badUrl = "";

            string js = ChTokenRegex.Replace(template, m =>
            {
                string u = m.Groups[1].Value;
                if (!_knownUrls.Contains(u)) { bad = true; badUrl = u; return "NaN"; }
                if (!urls.Contains(u)) urls.Add(u);
                return ChannelRead(u);
            });

            if (bad)
                return ResolvedProperty.Fail(ChannelResolution.Unmappable,
                    $"synthetic expression for '{key}' references unknown channel '{badUrl}'");

            return new ResolvedProperty(ChannelResolution.Synthetic, js, urls, "");
        }
    }
}
