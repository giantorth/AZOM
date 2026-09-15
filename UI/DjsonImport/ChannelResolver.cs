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
        /// <summary>Telemetry.json codec name. Decides whether a channel can carry an
        /// arbitrary value or would quantise it.</summary>
        public string Compression { get; }
        /// <summary>Tier cadence in ms (30 / 500 / 2000).</summary>
        public int PackageLevel { get; }

        public ChannelRow(string url, string defaultProperty,
                          string compression = "", int packageLevel = 0)
        {
            Url = url ?? "";
            DefaultProperty = defaultProperty ?? "";
            Compression = compression ?? "";
            PackageLevel = packageLevel;
        }
    }

    /// <summary>A catalog channel repurposed to carry a SimHub value that has no channel
    /// of its own.</summary>
    public sealed class ChannelAllocation
    {
        /// <summary>The canonical channel URL the dashboard will read.</summary>
        public string Url { get; set; } = "";
        /// <summary>What the plugin should evaluate and publish on it — a SimHub property
        /// path, or an NCalc/<c>js:</c> expression.</summary>
        public string Source { get; set; } = "";
        /// <summary>Tier cadence of the borrowed channel, in ms.</summary>
        public int PackageLevel { get; set; }
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
        /// <summary>No channel of its own, so a spare catalog channel was borrowed and
        /// the plugin will publish SimHub's value on it.</summary>
        Allocated,
        /// <summary>Not in any table, and no spare channel was available.</summary>
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
                             || Kind == ChannelResolution.Synthetic
                             || Kind == ChannelResolution.Allocated;

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

        // Bare leaf name -> channel url, for SimHub's shorthand: a dashboard may write
        // [BestLapTime] for [DataCorePlugin.GameData.BestLapTime], and real community
        // dashboards do so constantly. Only GameData properties are indexed here — that
        // is the namespace SimHub resolves the shorthand against.
        private readonly Dictionary<string, string> _byLeaf =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _synthetic =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _unmappable =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _stringValued =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every URL declared in Telemetry.json, for validating table targets.</summary>
        private readonly HashSet<string> _knownUrls =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Channels that can be borrowed to carry a SimHub value with no channel of its
        // own, and the allocations handed out so far. See BuildPool for what qualifies.
        private readonly List<ChannelRow> _pool = new List<ChannelRow>();
        private int _poolNext;
        private readonly Dictionary<string, ChannelAllocation> _allocated =
            new Dictionary<string, ChannelAllocation>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Let unresolvable properties borrow a spare channel instead of being
        /// dropped. Off by default so a plain conversion stays side-effect free.</summary>
        public bool AllocationEnabled { get; set; }

        /// <summary>The borrowings made, in allocation order. The caller must publish
        /// these as per-dashboard channel mappings or the widgets read nothing.</summary>
        public IReadOnlyCollection<ChannelAllocation> Allocations => _allocated.Values;

        /// <summary>How many spare channels remain.</summary>
        public int FreeChannels => Math.Max(0, _pool.Count - _poolNext);

        /// <summary>Return every borrowed channel to the pool. Call between dashboards —
        /// allocations are per-dashboard settings, so they must not carry over.</summary>
        public void ResetAllocations()
        {
            _allocated.Clear();
            _reserved.Clear();
            _poolNext = 0;
        }

        /// <summary>Spare channels left once the reserved set is excluded.</summary>
        public int AvailableChannels
        {
            get
            {
                int n = 0;
                for (int i = _poolNext; i < _pool.Count; i++)
                    if (!_reserved.Contains(_pool[i].Url)) n++;
                return n;
            }
        }

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

                const string gameData = "DataCorePlugin.GameData.";
                if (key.StartsWith(gameData, StringComparison.OrdinalIgnoreCase))
                {
                    string leaf = key.Substring(gameData.Length);
                    if (leaf.Length > 0 && !_byLeaf.ContainsKey(leaf)) _byLeaf[leaf] = entry.Url;
                }
            }

            LoadPropertyMap();
            BuildPool(catalog);
        }

        /// <summary>
        /// The channels that may be borrowed.
        ///
        /// <para>Only the lossless <c>float</c> codec qualifies — 32 bits carrying any
        /// value the property produces. Every other numeric codec in the catalog quantises
        /// to a specific quantity's range (<c>tyre_temp_1</c> is −148~384,
        /// <c>percent_1</c> is 0~100, <c>int30</c> is a gear), so a delta or an RPM put
        /// through one comes out mangled.</para>
        ///
        /// <para>Unmapped channels come first: nothing else claims them, so borrowing one
        /// has no side effect at all. Mapped ones follow, usable only once the caller has
        /// reserved everything the dashboard reads directly — a channel is either read for
        /// its own meaning or lent out, never both. Within each group the fastest tier
        /// goes first so borrowed values update as often as the catalog allows.</para>
        ///
        /// <para>Kept out on purpose: <c>patch/*</c> (the radar <c>ri*</c> slots and the
        /// <c>location_t</c> track-map arrays, both of which have their own pipeline in
        /// the plugin and would fight it), plugin-locked <c>@internal/</c> channels, and
        /// string channels — all 23 of those are mapped, so text still cannot be carried.</para>
        /// </summary>
        private void BuildPool(IEnumerable<ChannelRow> catalog)
        {
            foreach (var row in catalog)
            {
                if (string.IsNullOrEmpty(row.Url)) continue;
                if (!string.Equals(row.Compression, "float", StringComparison.OrdinalIgnoreCase)) continue;
                if (row.Url.IndexOf("/patch/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (row.DefaultProperty.StartsWith("@internal/", StringComparison.Ordinal)) continue;
                _pool.Add(row);
            }

            _pool.Sort((a, b) =>
            {
                bool freeA = string.IsNullOrWhiteSpace(a.DefaultProperty);
                bool freeB = string.IsNullOrWhiteSpace(b.DefaultProperty);
                if (freeA != freeB) return freeA ? -1 : 1;

                // Fastest tier first: a borrowed channel updates at the cadence of the
                // channel it borrowed, so this is what decides whether a converted widget
                // refreshes at 30 ms or 2 s. Everything the dashboard reads directly is
                // already reserved by the caller, so taking a fast slot costs it nothing.
                return a.PackageLevel.CompareTo(b.PackageLevel);
            });
        }

        /// <summary>
        /// Mark channels the dashboard reads for their own meaning, so they are never lent
        /// out. Call before enabling allocation — the converter learns the set from a
        /// first pass with allocation off.
        /// </summary>
        public void ReserveDirect(IEnumerable<string>? urls)
        {
            if (urls == null) return;
            foreach (var url in urls)
                if (!string.IsNullOrEmpty(url)) _reserved.Add(url);
        }

        private readonly HashSet<string> _reserved =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            CopySection(root["string_valued"] as JObject, _stringValued, validateUrl: false);
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

            // SimHub's bare-name shorthand, tried after the alias table so an explicit
            // alias can still redirect a name the catalog also happens to carry.
            if (key.IndexOf('.') < 0 && _byLeaf.TryGetValue(key, out var byLeaf))
                return Read(ChannelResolution.Alias, byLeaf);

            if (_synthetic.TryGetValue(key, out var template))
                return ExpandSynthetic(template, key);

            // Text has nowhere to go: every borrowable channel is numeric, and all 23
            // string channels are already claimed.
            if (_stringValued.TryGetValue(key, out var textReason))
            {
                return ResolvedProperty.Fail(ChannelResolution.Unmappable,
                    $"{textReason} — no string channel is free to carry it");
            }

            // A documented dead end is still worth carrying over SimHub: the note only
            // says the wheel has no channel of its own for it, which is exactly what a
            // borrowed channel fixes.
            _unmappable.TryGetValue(key, out var reason);

            var borrowed = Allocate(property!, key);
            if (borrowed.HasValue) return borrowed.Value;

            if (reason != null)
                return ResolvedProperty.Fail(ChannelResolution.Unmappable, reason);

            return ResolvedProperty.Fail(ChannelResolution.Unknown,
                AllocationEnabled
                    ? "no channel in Telemetry.json and every spare channel is taken"
                    : "no channel in Telemetry.json and no entry in DjsonPropertyMap.json");
        }

        /// <summary>
        /// Borrow a spare channel for <paramref name="source"/>, which may be a plain
        /// SimHub property path or a whole expression. Identical sources share one
        /// channel. Returns null when allocation is off or the pool is exhausted.
        /// </summary>
        public ResolvedProperty? Allocate(string source, string? cacheKey = null)
        {
            if (!AllocationEnabled) return null;
            string trimmed = (source ?? "").Trim();
            if (trimmed.Length == 0) return null;

            string key = cacheKey ?? trimmed;
            if (_allocated.TryGetValue(key, out var existing))
                return Borrowed(existing);

            while (_poolNext < _pool.Count && _reserved.Contains(_pool[_poolNext].Url))
                _poolNext++;
            if (_poolNext >= _pool.Count) return null;

            var row = _pool[_poolNext++];
            var allocation = new ChannelAllocation
            {
                Url = row.Url,
                Source = trimmed,
                PackageLevel = row.PackageLevel,
            };
            _allocated[key] = allocation;
            return Borrowed(allocation);
        }

        private static ResolvedProperty Borrowed(ChannelAllocation a)
            => new ResolvedProperty(ChannelResolution.Allocated,
                                    ChannelRead(a.Url), new[] { a.Url }, "");

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
