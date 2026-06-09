using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MozaPlugin.Telemetry.Protocol;
using MozaPlugin.Telemetry.TestMode;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// Builds and stores multi-stream dashboard profiles from bundled data and .mzdash files.
    /// </summary>
    public class DashboardProfileStore
    {
        private volatile Dictionary<string, TelemetryChannelInfo>? _telemetryMap;
        private volatile List<MultiStreamProfile>? _builtinProfiles;
        private readonly object _builtinLock = new object();

        // Match Telemetry.get() with plain quotes ('...', "...") and escaped quotes (\"...\")
        // The F1 mzdash has FuelRemainder in escaped double quotes: Telemetry.get(\"v1/gameData/FuelRemainder\")
        private static readonly Regex TelemetryGetRegex =
            new Regex(@"Telemetry\.get\(\\?[""'](v1/gameData/[^""'\\]+)\\?[""']\)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Newer mzdash format (Core, AMG-GT3 KS) embeds URL as a bare string in
        // binding/methods entries (no Telemetry.get() wrapper). Match raw
        // `v1/gameData/<name>` and nested `v1/gameData/<seg>/<seg>/...` so we
        // don't miss those channels (Telemetry.json's `v1/gameData/patch/*`
        // namespace would otherwise be truncated to `v1/gameData/patch`).
        private static readonly Regex RawUrlRegex =
            new Regex(@"v1/gameData/[A-Za-z0-9_]+(?:/[A-Za-z0-9_]+)*",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>URL suffix → SimHub field mapping.</summary>
        // URL → SimHubField, property path, and scale all live in
        // Data/Telemetry.json (simhub_field, simhub_property, simhub_scale).
        // The earlier hardcoded UrlFieldMap + DefaultPropertyPaths duplicated
        // that data; kept removed so the JSON stays authoritative.

        /// <summary>
        /// Build a multi-tier <see cref="MultiStreamProfile"/> from the wheel's
        /// advertised channel catalog. Eliminates the mzdash dependency for
        /// telemetry — plugin subscribes to whatever channels the wheel
        /// declared and pulls per-channel metadata (compression, SimHub
        /// property, scale, package_level) from the bundled
        /// <c>Data/Telemetry.json</c> resource. Channels group into tiers by
        /// their declared <c>package_level</c> (2000/500/30 ms) so the wire
        /// subscription matches PitHouse's slow/medium/fast layout.
        ///
        /// Falls back to heuristic compression / SimHub field mapping for URLs
        /// the JSON doesn't declare (e.g. firmware-only channels).
        /// </summary>
        // Strings are out-of-band (sess=0x01 type=0x05), not bit-packed into
        // any tier. Both BuildStringChannel overloads route the resulting
        // ChannelDefinition to MultiStreamProfile.StringChannels.
        private static ChannelDefinition BuildStringChannel(string url, TelemetryChannelInfo info)
        {
            double scale = info.SimHubPropertyScale == 0 ? 1.0 : info.SimHubPropertyScale;
            return BuildStringChannel(
                url, info.Name, info.Compression, info.Field,
                info.SimHubProperty ?? "", scale, info.PackageLevel,
                info.Range, info.DataType);
        }

        private static ChannelDefinition BuildStringChannel(
            string url, string name, string compression, SimHubField field,
            string property, double scale, int packageLevel,
            string? rangeStr, string? dataType)
        {
            var ch = new ChannelDefinition
            {
                Name                = name,
                Url                 = url,
                Compression         = compression,
                BitWidth            = 0,
                SimHubField         = field,
                SimHubProperty      = property,
                SimHubPropertyScale = scale,
                PackageLevel        = packageLevel,
                TestSignal          = TestSignalCatalog.Resolve(name, rangeStr, dataType, compression),
            };
            StringChannelDefaults.ApplyIfEmpty(ch);
            return ch;
        }

        // Bulky track-map / radar position channels gated behind the radar
        // setting: the per-opponent location array (patch/Location,
        // patch/Location_N) and radar index channels (patch/riN).
        // OpponentCount / PlayerIndex are deliberately NOT gated — they are
        // 1-byte scalar session fields ordinary dashboards read for
        // car-count / grid-position display (the FSR2 "LD - Marco" dash
        // shows patch/OpponentCount+1 as the entry count), so they must
        // emit regardless of the radar setting. Other patch/ channels
        // (TrackPositionPercent, TrackName) and non-patch Heading untouched.
        internal static bool IsRadarTrackMapChannel(string url)
        {
            if (string.IsNullOrEmpty(url) || url.IndexOf("/patch/", StringComparison.Ordinal) < 0)
                return false;
            string suffix = url.Substring(url.LastIndexOf('/') + 1);
            if (suffix == "Location")
                return true;
            if (suffix.StartsWith("Location_", StringComparison.Ordinal))
                return AllDigits(suffix, "Location_".Length);
            if (suffix.StartsWith("ri", StringComparison.Ordinal) && suffix.Length > 2)
                return AllDigits(suffix, 2);
            return false;
        }

        private static bool AllDigits(string s, int start)
        {
            if (start >= s.Length) return false;
            for (int i = start; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        public MultiStreamProfile BuildProfileFromCatalog(
            IReadOnlyList<string> catalog,
            string profileName = "WheelCatalog",
            bool includeRadarTrackMap = true)
        {
            var telemetryMap = GetTelemetryMap();
            // Build a ChannelDefinition per catalog URL, looking up each
            // channel's compression / package_level / SimHub property in
            // Telemetry.json. URLs not in the JSON fall back to heuristic
            // compression and SimHubField mapping.
            var perTier = new Dictionary<int, List<ChannelDefinition>>();
            var stringChannels = new List<ChannelDefinition>();
            // Dedup by URL. The wheel re-advertises a dash's channels at fresh
            // catalog idxs (a 2nd generation within one switch), and
            // ChannelCatalogParser.CommitLiveSet's same-burst UNION keeps both
            // idx ranges. Without this guard each duplicate URL becomes a
            // redundant channel — Marco synthesised 127ch/6t for ~75 real
            // channels, doubling every value frame and the fast-tier count,
            // which lags the wheel's per-tick render. Keep the first
            // occurrence; genuinely-new URLs in later batches still pass.
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var url in catalog)
            {
                if (string.IsNullOrEmpty(url)) continue;
                if (!includeRadarTrackMap && IsRadarTrackMapChannel(url)) continue;
                if (!seenUrls.Add(url)) continue;
                string suffix = url.Substring(url.LastIndexOf('/') + 1);
                ChannelDefinition ch;
                int packageLevel;
                if (telemetryMap.TryGetValue(url, out var info))
                {
                    packageLevel = info.PackageLevel;
                    if (string.Equals(info.Compression, "string", StringComparison.OrdinalIgnoreCase))
                    {
                        stringChannels.Add(BuildStringChannel(url, info));
                        continue;
                    }
                    int bitWidth = CompressionTable.TryGetByName(info.Compression, out var ct) ? ct.BitWidth : 32;
                    double scale = info.SimHubPropertyScale == 0 ? 1.0 : info.SimHubPropertyScale;
                    ch = new ChannelDefinition
                    {
                        Name = info.Name,
                        Url = url,
                        Compression = info.Compression,
                        BitWidth = bitWidth,
                        SimHubField = info.Field,
                        SimHubProperty = info.SimHubProperty,
                        SimHubPropertyScale = scale,
                        PackageLevel = packageLevel,
                        TestSignal = TestSignalCatalog.Resolve(info.Name, info.Range, info.DataType, info.Compression),
                    };
                }
                else
                {
                    // Fallback: URL not in Telemetry.json — heuristic compression
                    // only. No SimHubField / property mapping (those live in
                    // Telemetry.json now); user can manually map via UI.
                    string compression = PickCompressionForUrl(suffix);
                    int bitWidth = CompressionTable.TryGetByName(compression, out var ct2) ? ct2.BitWidth : 32;
                    packageLevel = 30;
                    ch = new ChannelDefinition
                    {
                        Name = suffix,
                        Url = url,
                        Compression = compression,
                        BitWidth = bitWidth,
                        SimHubField = SimHubField.Zero,
                        SimHubProperty = "",
                        SimHubPropertyScale = 1.0,
                        PackageLevel = packageLevel,
                        TestSignal = TestSignalCatalog.Resolve(suffix, null, null, compression),
                    };
                }
                if (!perTier.TryGetValue(packageLevel, out var list))
                {
                    list = new List<ChannelDefinition>();
                    perTier[packageLevel] = list;
                }
                list.Add(ch);
            }

            // Sort tiers by package_level descending so flag=0 is the slowest
            // tier (PitHouse convention: slowest tier first, fastest last).
            //
            // KNOWN INCONSISTENCY: BuildMultiStreamProfile (this same file,
            // l. ~590) sorts ascending — opposite ordering. docs/protocol/
            // telemetry/live-stream.md table also contradicts this comment
            // (claims base = pl 30 = fastest). Live R5 capture 2026-04-29 saw
            // flag base ≈ 0x0c (not 0x00), so the absolute base byte is per-
            // connection. Tracked: docs/protocol/open-questions.md "Tier-flag
            // → package_level mapping inverted".
            var sortedLevels = new List<int>(perTier.Keys);
            sortedLevels.Sort((a, b) => b.CompareTo(a));

            // Split each package_level's channels into sub-tiers whose value
            // frame stays within MaxTierDataBytes. A value frame is a single
            // group-0x43 packet, so it must (a) keep its 1-byte length field
            // (N = 10 + dataLen) from overflowing, and (b) fit the wheel's
            // value-frame buffer — PitHouse never exceeds 55 B (measured
            // across FSR2 captures) and splits big channel sets accordingly
            // (e.g. 64-bit location_t track-map channels at 6/flag, 48 B), so
            // we match it at 55 B. The sub-tiers all ride in ONE broadcast
            // with ONE END marker (see TierDefinitionBuilder
            // .DetectSubTiersPerBroadcast) — the layout PitHouse emits and the
            // wheel binds. Each sub-tier shares the package_level (same emit
            // rate) and gets its own consecutive flag downstream.
            const int MaxTierDataBytes = 55;
            var tiers = new List<DashboardProfile>();
            foreach (var level in sortedLevels)
            {
                var chs = perTier[level];
                int start = 0;
                while (start < chs.Count)
                {
                    int subBits = 0, end = start;
                    while (end < chs.Count)
                    {
                        int next = subBits + chs[end].BitWidth;
                        // Always take at least one channel; otherwise stop
                        // before this sub-tier would exceed the byte cap.
                        if (end > start && (next + 7) / 8 > MaxTierDataBytes) break;
                        subBits = next;
                        end++;
                    }
                    tiers.Add(new DashboardProfile
                    {
                        Name = start == 0 ? $"L{level}" : $"L{level}_{start}",
                        Channels = chs.GetRange(start, end - start),
                        PackageLevel = level,
                        TotalBits = subBits,
                        TotalBytes = (subBits + 7) / 8,
                    });
                    start = end;
                }
            }

            // Always emit at least one tier (firmware expects subscription).
            if (tiers.Count == 0)
            {
                tiers.Add(new DashboardProfile
                {
                    Name = profileName,
                    Channels = new List<ChannelDefinition>(),
                    PackageLevel = 30,
                });
            }

            stringChannels.Sort((a, b) => string.Compare(a.Url, b.Url, StringComparison.OrdinalIgnoreCase));

            return new MultiStreamProfile
            {
                Name = profileName,
                PageCount = 1,
                Tiers = tiers,
                StringChannels = stringChannels,
            };
        }

        /// <summary>
        /// Heuristic compression picker. Wheel firmware ultimately decides;
        /// these defaults match what the host is most likely to send for
        /// standard simracing channels and align with codes confirmed by
        /// PitHouse captures (e.g. CurrentLapTime → float, ABSActive → bool).
        /// </summary>
        public static string PickCompressionForUrl(string suffix)
        {
            // Boolean state flags
            if (suffix.EndsWith("Active", StringComparison.OrdinalIgnoreCase)
                || suffix.EndsWith("Enabled", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("ABSActive", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("TCActive", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("DrsState", StringComparison.OrdinalIgnoreCase))
                return "bool";
            if (suffix.Equals("Gear", StringComparison.OrdinalIgnoreCase))
                return "uint30"; // PitHouse capture: comp=0x0d width=5 — covers reverse (-1=31)
            if (suffix.EndsWith("Level", StringComparison.OrdinalIgnoreCase))
                return "uint8";
            if (suffix.Equals("Rpm", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("Rpms", StringComparison.OrdinalIgnoreCase))
                return "uint16_t";
            if (suffix.Equals("CurrentLap", StringComparison.OrdinalIgnoreCase)
                || suffix.EndsWith("LapNumber", StringComparison.OrdinalIgnoreCase)
                || suffix.EndsWith("LapCount", StringComparison.OrdinalIgnoreCase))
                return "uint16_t";
            if (suffix.EndsWith("LapTime", StringComparison.OrdinalIgnoreCase)
                || suffix.Equals("GAP", StringComparison.OrdinalIgnoreCase))
                return "float";
            if (suffix.Equals("SpeedKmh", StringComparison.OrdinalIgnoreCase))
                return "float_6000_1";
            if (suffix.StartsWith("TyreTemp", StringComparison.OrdinalIgnoreCase))
                return "tyre_temp_1";
            if (suffix.StartsWith("TyrePressure", StringComparison.OrdinalIgnoreCase))
                return "tyre_pressure_1";
            if (suffix.StartsWith("TyreWear", StringComparison.OrdinalIgnoreCase))
                return "percent_1";
            if (suffix.EndsWith("Percent", StringComparison.OrdinalIgnoreCase)
                || suffix.EndsWith("Throttle", StringComparison.OrdinalIgnoreCase)
                || suffix.EndsWith("Brake", StringComparison.OrdinalIgnoreCase))
                return "percent_1";
            // Fallback — float covers most signed continuous values
            return "float";
        }



        public IReadOnlyList<MultiStreamProfile> BuiltinProfiles
        {
            get
            {
                if (_builtinProfiles == null)
                {
                    lock (_builtinLock)
                    {
                        if (_builtinProfiles == null)
                            LoadBuiltinProfiles();
                    }
                }
                return _builtinProfiles!;
            }
        }

        private void LoadBuiltinProfiles()
        {
            _builtinProfiles = new List<MultiStreamProfile>();
            var assembly = Assembly.GetExecutingAssembly();

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.EndsWith(".mzdash", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null) continue;
                    using var reader = new StreamReader(stream);
                    string content = reader.ReadToEnd();

                    string displayName = resourceName
                        .Replace("MozaPlugin.Data.Dashes.", "")
                        .Replace(".mzdash", "")
                        .Replace("_", " ");

                    var profile = ParseMzdashContent(displayName, content);
                    if (profile != null)
                        _builtinProfiles.Add(profile);
                }
                catch (Exception ex)
                {
                    MozaLog.Warn($"[AZOM] Failed to load builtin profile {resourceName}: {ex.Message}");
                }
            }

            // Emit one aggregated debug-line listing any channels whose
            // test-mode bounds fell through to the compression-table default
            // (i.e. neither a TestSignalOverrides entry nor a parseable
            // Telemetry.json range). Helps identify gaps to plug.
            TestSignalCatalog.FlushFallbackLog();
        }

        /// <summary>
        /// Parse a .mzdash file from disk and build a multi-stream profile.
        /// </summary>
        public MultiStreamProfile? ParseMzdash(string path)
        {
            try
            {
                string content = File.ReadAllText(path);
                string name = Path.GetFileNameWithoutExtension(path);
                return ParseMzdashContent(name, content);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Failed to parse .mzdash {path}: {ex.Message}");
                return null;
            }
        }

        internal MultiStreamProfile? ParseMzdashContent(string name, string content)
        {
            // Extract Telemetry.get() URLs (legacy format, e.g. F1 mzdash)
            var allUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in TelemetryGetRegex.Matches(content))
                allUrls.Add(m.Groups[1].Value);
            // Also extract raw `v1/gameData/...` refs (newer format like Core
            // and AMG GT3 KS — URL is a plain string in binding/methods,
            // no Telemetry.get() wrapper).
            foreach (Match m in RawUrlRegex.Matches(content))
                allUrls.Add(m.Value);

            if (allUrls.Count == 0)
                return null;

            // Tier emission strategy: pkg_level grouping (matches PitHouse
            // in-game capture 2026-04-29: 3 tiers × N channels each, one tier
            // per update rate 30/500/2000 ms). Earlier per-widget strategy
            // generated 1-channel-per-tier malformed for the wheel parser —
            // wheel expects channels of same package_level packed in a single
            // 0x01 record. Falls back to per-widget only when pkg_level
            // grouping yields no tiers (no channels found in widget walk).
            MultiStreamProfile? profile = BuildMultiStreamProfile(name, allUrls);

            if (profile == null || profile.Tiers.Count == 0)
            {
                try
                {
                    var json = JObject.Parse(content);
                    var perWidget = BuildPerWidgetProfile(name, json);
                    if (perWidget != null && perWidget.Tiers.Count > 0)
                        profile = perWidget;
                }
                catch (Exception ex)
                {
                    MozaLog.Debug($"[AZOM] mzdash widget-tree parse failed for '{name}': {ex.Message}");
                }
            }

            try
            {
                var json = JObject.Parse(content);
                var children = json["children"] as JArray;
                if (children != null && children.Count > 0 && profile != null)
                    profile.PageCount = children.Count;
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] mzdash page-count parse failed for '{name}': {ex.Message}");
            }

            return profile;
        }

        /// <summary>
        /// Walk mzdash JSON children tree, emit one DashboardProfile per widget
        /// that binds telemetry URLs. Tier flag bytes assigned sequentially in
        /// walk order (0..N-1, no gaps — gaps crashed real W17 display in
        /// 2026-04-29 testing). Channel order within tier follows the URL
        /// discovery order in widget JSON (matches PitHouse capture; not
        /// alphabetic).
        /// </summary>
        private MultiStreamProfile? BuildPerWidgetProfile(string name, JObject root)
        {
            var widgetTiers = new List<DashboardProfile>();
            var telemetryMap = GetTelemetryMap();
            // Captured by Walk() closure when a widget binds a string-typed
            // channel — routed to MultiStreamProfile.StringChannels at the end.
            var perWidgetStringChannels = new List<ChannelDefinition>();

            void Walk(JToken node)
            {
                if (node is JObject obj)
                {
                    var localText = new StringBuilder();
                    foreach (var prop in obj.Properties())
                    {
                        if (prop.Name == "children") continue;
                        if (prop.Value is JValue v && v.Type == JTokenType.String)
                            localText.Append((string?)v.Value).Append('\n');
                        else if (prop.Value is JObject || prop.Value is JArray)
                            localText.Append(prop.Value.ToString(Newtonsoft.Json.Formatting.None)).Append('\n');
                    }
                    string text = localText.ToString();

                    // Preserve discovery order; dedupe within widget only.
                    var urls = new List<string>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Match m in TelemetryGetRegex.Matches(text))
                    {
                        var u = m.Groups[1].Value;
                        if (seen.Add(u)) urls.Add(u);
                    }
                    foreach (Match m in RawUrlRegex.Matches(text))
                    {
                        if (seen.Add(m.Value)) urls.Add(m.Value);
                    }

                    if (urls.Count > 0)
                    {
                        var channels = new List<ChannelDefinition>();
                        int packageLevel = 30;
                        foreach (var url in urls)
                        {
                            string suffix = url.Contains('/') ? url.Substring(url.LastIndexOf('/') + 1) : url;
                            int bitWidth;
                            string compression;
                            string property = "";
                            SimHubField field = SimHubField.Zero;
                            int level = 30;
                            string chName = suffix;
                            double scale = 1.0;
                            string? rangeStr = null;
                            string? dataType = null;
                            if (telemetryMap.TryGetValue(url, out var info))
                            {
                                compression = info.Compression;
                                bitWidth = CompressionTable.TryGetByName(compression, out var ct3) ? ct3.BitWidth : 32;
                                field = info.Field;
                                property = info.SimHubProperty ?? "";
                                level = info.PackageLevel;
                                chName = info.Name;
                                scale = info.SimHubPropertyScale == 0 ? 1.0 : info.SimHubPropertyScale;
                                rangeStr = info.Range;
                                dataType = info.DataType;
                            }
                            else
                            {
                                compression = PickCompressionForUrl(suffix);
                                bitWidth = CompressionTable.TryGetByName(compression, out var ct4) ? ct4.BitWidth : 32;
                            }
                            if (string.Equals(compression, "string", StringComparison.OrdinalIgnoreCase))
                            {
                                perWidgetStringChannels.Add(BuildStringChannel(
                                    url, chName, compression, field, property, scale, level,
                                    rangeStr, dataType));
                                continue;
                            }
                            // Tier's package_level = fastest (lowest) of its
                            // channels — drives plugin tick scheduling.
                            if (level < packageLevel) packageLevel = level;
                            channels.Add(new ChannelDefinition
                            {
                                Name = chName,
                                Url = url,
                                Compression = compression,
                                BitWidth = bitWidth,
                                SimHubField = field,
                                SimHubProperty = property,
                                SimHubPropertyScale = scale,
                                PackageLevel = level,
                                TestSignal = TestSignalCatalog.Resolve(chName, rangeStr, dataType, compression),
                            });
                        }

                        int totalBits = channels.Sum(c => c.BitWidth);
                        widgetTiers.Add(new DashboardProfile
                        {
                            Name = name,
                            Channels = channels,  // preserve binding order, do NOT sort
                            PackageLevel = packageLevel,
                            TotalBits = totalBits,
                            TotalBytes = (totalBits + 7) / 8,
                        });
                    }

                    if (obj["children"] is JArray childArr)
                        foreach (var c in childArr) Walk(c);
                }
                else if (node is JArray arr)
                {
                    foreach (var c in arr) Walk(c);
                }
            }

            Walk(root);
            if (widgetTiers.Count == 0) return null;

            // Dedupe tiers with identical channel-URL sequences and sort by
            // first-channel URL so flag bytes 0..N-1 follow wheel-catalog
            // alphabetic order (CurrentLap=flag0, CurrentLapTime=flag1, ...).
            var unique = new List<DashboardProfile>();
            var seenSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tier in widgetTiers)
            {
                var key = string.Join("|", tier.Channels.Select(c => c.Url));
                if (seenSets.Add(key)) unique.Add(tier);
            }
            unique.Sort((a, b) =>
            {
                string ua = a.Channels.Count > 0 ? a.Channels[0].Url : "";
                string ub = b.Channels.Count > 0 ? b.Channels[0].Url : "";
                return string.Compare(ua, ub, StringComparison.OrdinalIgnoreCase);
            });

            // Dedupe string channels by URL (Walk may revisit a widget twice
            // during retransmits or repeated children) and sort alphabetically.
            var stringDedup = new Dictionary<string, ChannelDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in perWidgetStringChannels)
                if (!stringDedup.ContainsKey(s.Url)) stringDedup[s.Url] = s;
            var stringChannels = stringDedup.Values
                .OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new MultiStreamProfile
            {
                Name = name,
                Tiers = unique,
                StringChannels = stringChannels,
            };
        }

        /// <summary>
        /// Apply a per-channel user mapping to a loaded profile, overriding
        /// <see cref="ChannelDefinition.SimHubProperty"/> by channel URL. Entries
        /// with an empty/whitespace value are ignored (the channel keeps its
        /// JSON default). To revert a user override, remove the entire dashboard
        /// entry from the settings map (see <c>ClearCurrentDashboardMappings</c>).
        /// Unknown URLs are ignored.
        /// </summary>
        public static void ApplyUserMappings(MultiStreamProfile? profile,
            IReadOnlyDictionary<string, string>? overrides)
        {
            if (profile == null || overrides == null || overrides.Count == 0) return;

            foreach (var tier in profile.Tiers)
            {
                foreach (var ch in tier.Channels)
                {
                    // Plugin-locked channels (value sourced internally) ignore user mappings.
                    if (IsInternalChannel(ch.SimHubProperty)) continue;

                    if (overrides.TryGetValue(ch.Url, out var path) && !string.IsNullOrWhiteSpace(path))
                        ch.SimHubProperty = path.Trim();
                }
            }
            foreach (var ch in profile.StringChannels)
            {
                if (IsInternalChannel(ch.SimHubProperty)) continue;
                if (overrides.TryGetValue(ch.Url, out var path) && !string.IsNullOrWhiteSpace(path))
                    ch.SimHubProperty = path.Trim();
            }
        }

        /// <summary>True for sentinel property paths resolved internally by the plugin.</summary>
        public static bool IsInternalChannel(string? simHubProperty)
            => !string.IsNullOrEmpty(simHubProperty)
               && simHubProperty!.StartsWith("@internal/", StringComparison.Ordinal);

        // Cache of file path → (lastWriteTime, key) so repeated ApplyTelemetrySettings
        // calls don't re-hash multi-KB mzdash files. Invalidated by mtime change. LRU-
        // bounded so swapping among many dashboards doesn't grow forever.
        private static readonly Dictionary<string, (long writeTimeTicks, string key)> _keyCache
            = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<string> _keyCacheLru = new LinkedList<string>();
        private static readonly object _keyCacheLock = new object();
        private const int KeyCacheMax = 100;

        /// <summary>
        /// Build a stable identity for a dashboard so mappings can be keyed per-dashboard.
        /// Builtin profiles (no file path) use <c>"builtin:&lt;name&gt;"</c>. User-loaded
        /// .mzdash files use <c>"file:&lt;filename&gt;:&lt;sha1-first-8&gt;"</c> so identically-named
        /// files with different contents don't share mappings.
        /// </summary>
        public static string GetDashboardKey(string? loadedPath, MultiStreamProfile profile)
        {
            if (string.IsNullOrEmpty(loadedPath))
                return "builtin:" + (profile?.Name ?? "");

            long mtime;
            try { mtime = File.GetLastWriteTimeUtc(loadedPath!).Ticks; }
            catch { mtime = 0; }

            if (mtime != 0)
            {
                lock (_keyCacheLock)
                {
                    if (_keyCache.TryGetValue(loadedPath!, out var hit) && hit.writeTimeTicks == mtime)
                    {
                        var node = _keyCacheLru.Find(loadedPath!);
                        if (node != null) { _keyCacheLru.Remove(node); _keyCacheLru.AddFirst(node); }
                        return hit.key;
                    }
                }
            }

            string filename = Path.GetFileName(loadedPath);
            string hash;
            try
            {
                using var sha = SHA1.Create();
                byte[] digest = sha.ComputeHash(File.ReadAllBytes(loadedPath));
                var sb = new StringBuilder(8);
                for (int i = 0; i < 4; i++) sb.Append(digest[i].ToString("x2"));
                hash = sb.ToString();
            }
            catch
            {
                hash = "nohash";
            }
            string result = "file:" + filename + ":" + hash;

            if (mtime != 0)
            {
                lock (_keyCacheLock)
                {
                    if (_keyCache.ContainsKey(loadedPath!))
                    {
                        var existing = _keyCacheLru.Find(loadedPath!);
                        if (existing != null) _keyCacheLru.Remove(existing);
                    }
                    _keyCache[loadedPath!] = (mtime, result);
                    _keyCacheLru.AddFirst(loadedPath!);
                    while (_keyCacheLru.Count > KeyCacheMax)
                    {
                        var stale = _keyCacheLru.Last!.Value;
                        _keyCacheLru.RemoveLast();
                        _keyCache.Remove(stale);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Build a MultiStreamProfile from a list of channel URLs.
        /// Channels are grouped by package_level and sorted alphabetically within each tier.
        /// Any package_level value found in Telemetry.json gets its own tier.
        /// </summary>
        public MultiStreamProfile BuildMultiStreamProfile(string name, IEnumerable<string> urls)
        {
            var map = GetTelemetryMap();
            var byLevel = new Dictionary<int, List<ChannelDefinition>>();
            var stringChannels = new List<ChannelDefinition>();

            foreach (var url in urls)
            {
                if (!map.TryGetValue(url, out var info))
                    continue;

                if (string.Equals(info.Compression, "string", StringComparison.OrdinalIgnoreCase))
                {
                    stringChannels.Add(BuildStringChannel(url, info));
                    continue;
                }

                if (!CompressionTable.TryGetByName(info.Compression, out var ct5))
                    continue;
                int bits = ct5.BitWidth;

                int level = info.PackageLevel;
                if (!byLevel.ContainsKey(level))
                    byLevel[level] = new List<ChannelDefinition>();

                double scale = info.SimHubPropertyScale == 0 ? 1.0 : info.SimHubPropertyScale;
                byLevel[level].Add(new ChannelDefinition
                {
                    Name                = info.Name,
                    Url                 = url,
                    Compression         = info.Compression,
                    BitWidth            = bits,
                    SimHubField         = info.Field,
                    SimHubProperty      = info.SimHubProperty ?? "",
                    SimHubPropertyScale = scale,
                    PackageLevel        = level,
                    TestSignal          = TestSignalCatalog.Resolve(info.Name, info.Range, info.DataType, info.Compression),
                });
            }

            // Build tiers sorted by package_level ascending (flag offset = index)
            var tiers = byLevel.Keys
                .OrderBy(level => level)
                .Select(level => BuildTierProfile(name, byLevel[level], level))
                .ToList();

            stringChannels.Sort((a, b) => string.Compare(a.Url, b.Url, StringComparison.OrdinalIgnoreCase));

            return new MultiStreamProfile
            {
                Name           = name,
                Tiers          = tiers,
                StringChannels = stringChannels,
            };
        }

        private static DashboardProfile BuildTierProfile(string name, List<ChannelDefinition> channels, int level)
        {
            // Sort alphabetically by URL within the tier
            var sorted = channels
                .OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int totalBits = sorted.Sum(c => c.BitWidth);
            return new DashboardProfile
            {
                Name         = name,
                Channels     = sorted,
                TotalBits    = totalBits,
                TotalBytes   = (totalBits + 7) / 8,
                PackageLevel = level,
            };
        }

        private Dictionary<string, TelemetryChannelInfo> GetTelemetryMap()
        {
            var map = _telemetryMap;
            if (map == null)
            {
                map = LoadTelemetryJson();
                _telemetryMap = map;
            }
            return map;
        }

        private Dictionary<string, TelemetryChannelInfo> LoadTelemetryJson()
        {
            var result = new Dictionary<string, TelemetryChannelInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("MozaPlugin.Data.Telemetry.json");
                if (stream == null)
                {
                    MozaLog.Warn("[AZOM] Telemetry.json embedded resource not found");
                    return result;
                }

                using var reader = new StreamReader(stream);
                var json = JObject.Parse(reader.ReadToEnd());
                var sectors = json["sectors"] as JArray;
                if (sectors == null) return result;

                foreach (var sector in sectors)
                {
                    string? url         = sector["url"]?.ToString();
                    string? compression = sector["compression"]?.ToString();
                    string? name        = sector["name"]?.ToString();
                    int packageLevel    = sector["package_level"]?.Value<int>() ?? 30;
                    string? simHubProp  = sector["simhub_property"]?.ToString();
                    double scale        = sector["simhub_scale"]?.Value<double>() ?? 1.0;
                    string? fieldStr    = sector["simhub_field"]?.ToString();
                    SimHubField field = SimHubField.Zero;
                    if (!string.IsNullOrEmpty(fieldStr))
                        Enum.TryParse(fieldStr, ignoreCase: true, out field);
                    // range is localised ({zh_CN, en_US}); pull en_US.
                    string? rangeStr   = sector["range"]?["en_US"]?.ToString();
                    string? dataType   = sector["data_type"]?.ToString();

                    if (url == null || compression == null) continue;
                    result[url] = new TelemetryChannelInfo(
                        name ?? url, compression, packageLevel, simHubProp ?? "", scale, field,
                        rangeStr, dataType);
                }

                // Drift guard for the deterministic tyre codec families. These
                // regressed once — stale "float" in the JSON silently overrode
                // the firmware codec, leaving tyre-pressure/temp widgets dead
                // (issue #43). The codec is uniquely determined by the URL and
                // wheel-model-independent (verified W13 + W17 against PitHouse:
                // tyre_pressure_1 = 0x16/12, tyre_temp_1 = 0x11/14), so a
                // base-unit TyrePressure*/TyreTemp* channel set to anything else
                // is almost certainly a mistake. Unit variants (&unit=F, &kpa,
                // &B) use raw float (different scaling) and are skipped.
                var tyreDrift = new List<string>();
                foreach (var kv in result)
                {
                    string chUrl = kv.Key;
                    if (chUrl.IndexOf('&') >= 0) continue;
                    string suffix = chUrl.Contains('/')
                        ? chUrl.Substring(chUrl.LastIndexOf('/') + 1) : chUrl;
                    string? expected =
                        suffix.StartsWith("TyrePressure", StringComparison.OrdinalIgnoreCase) ? "tyre_pressure_1" :
                        suffix.StartsWith("TyreTemp", StringComparison.OrdinalIgnoreCase) ? "tyre_temp_1" :
                        null;
                    if (expected != null && !string.Equals(kv.Value.Compression, expected,
                            StringComparison.OrdinalIgnoreCase))
                        tyreDrift.Add($"{suffix}=\"{kv.Value.Compression}\" (expected {expected})");
                }
                if (tyreDrift.Count > 0)
                    MozaLog.Warn(
                        $"[AZOM] Telemetry.json tyre codec drift — {tyreDrift.Count} channel(s) not using "
                        + "the firmware codec PitHouse emits; these widgets will not render: "
                        + string.Join(", ", tyreDrift));
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Failed to load Telemetry.json: {ex.Message}");
            }

            return result;
        }

        private struct TelemetryChannelInfo
        {
            public string Name;
            public string Compression;
            public int    PackageLevel;
            public string SimHubProperty;
            public double SimHubPropertyScale;
            public SimHubField Field;
            // Raw fields from Telemetry.json carried through for test-mode
            // sweep bounds. See Telemetry/TestMode/TestSignalCatalog.cs.
            public string? Range;
            public string? DataType;

            public TelemetryChannelInfo(string name, string compression, int packageLevel,
                string simHubProperty, double simHubPropertyScale, SimHubField field,
                string? range = null, string? dataType = null)
            {
                Name                = name;
                Compression         = compression;
                PackageLevel        = packageLevel;
                SimHubProperty      = simHubProperty;
                SimHubPropertyScale = simHubPropertyScale;
                Field               = field;
                Range               = range;
                DataType            = dataType;
            }
        }
    }
}
