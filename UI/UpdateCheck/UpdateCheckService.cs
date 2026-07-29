using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.UpdateCheck
{
    public enum UpdateCheckErrorKind
    {
        None = 0,
        Network,    // DNS, socket, timeout, TLS
        Http,       // non-2xx
        Parse,      // JSON malformed or missing required fields
        Cancelled,
    }

    public readonly struct UpdateCheckResult
    {
        public bool Success { get; }
        public string LatestVersion { get; }
        public string ReleaseUrl { get; }
        public string ReleaseNotes { get; }
        // browser_download_url of the first MozaPlugin*.zip asset (empty if
        // the release has no matching asset — happens for hand-cut tags).
        // Used by the in-app installer to fetch the new DLL.
        public string AssetUrl { get; }
        public UpdateCheckErrorKind ErrorKind { get; }
        public string ErrorMessage { get; }

        public UpdateCheckResult(
            bool success,
            string latestVersion,
            string releaseUrl,
            string releaseNotes,
            string assetUrl,
            UpdateCheckErrorKind errorKind,
            string errorMessage)
        {
            Success = success;
            LatestVersion = latestVersion ?? "";
            ReleaseUrl = releaseUrl ?? "";
            ReleaseNotes = releaseNotes ?? "";
            AssetUrl = assetUrl ?? "";
            ErrorKind = errorKind;
            ErrorMessage = errorMessage ?? "";
        }

        public static UpdateCheckResult Ok(string version, string url, string notes, string assetUrl)
            => new UpdateCheckResult(true, version, url, notes, assetUrl, UpdateCheckErrorKind.None, "");

        public static UpdateCheckResult NoReleaseAvailable()
            => new UpdateCheckResult(true, "", "", "", "", UpdateCheckErrorKind.None, "");

        public static UpdateCheckResult Fail(UpdateCheckErrorKind kind, string message)
            => new UpdateCheckResult(false, "", "", "", "", kind, message);
    }

    /// <summary>
    /// Queries the GitHub Releases API, parses the list into an
    /// <see cref="UpdateSnapshot"/> (newest stable + one channel per open PR
    /// with builds), and exposes the comparator the banner-rendering code uses
    /// to decide whether the running build is out of date.
    /// </summary>
    public static class UpdateCheckService
    {
        private const string RepoOwner = "giantorth";
        private const string RepoName = "AZOM"; // repo renamed from moza-simhub-plugin
        private const int TimeoutSeconds = 10;

        /// <summary>Channel id of the stable release stream. PR channels are
        /// "pr/&lt;N&gt;" (see <see cref="PrChannelId"/>).</summary>
        public const string StableChannelId = "stable";

        // Stable releases are plain vX.Y.Z tags; PR builds are tagged
        // pr-<N>-<sha7> and named "<title> (<version>)" by
        // .github/workflows/pr-build.yml. The version regex is end-anchored so
        // parentheses inside a PR title can't shift the match.
        private static readonly Regex s_stableTagRx =
            new Regex(@"^v\d+\.\d+\.\d+$", RegexOptions.Compiled);
        private static readonly Regex s_prTagRx =
            new Regex(@"^pr-(\d+)-([0-9a-f]{7,40})$", RegexOptions.Compiled);
        private static readonly Regex s_prNameVersionRx =
            new Regex(@"\((\d+\.\d+\.\d+-pr\.\d+\.[0-9a-f]+)\)\s*$", RegexOptions.Compiled);

        // Single-instance HttpClient lives for the lifetime of the plugin
        // AppDomain. SimHub keeps the AppDomain alive across plugin reloads,
        // so disposing this in End() would break the next Init. Exposed
        // via Http so the in-app installer can reuse the same User-Agent /
        // TLS-protocol configuration without a second client. The 10s
        // Timeout only applies to header-reading (UpdateInstallService uses
        // HttpCompletionOption.ResponseHeadersRead for asset downloads, so
        // multi-MB body streams aren't timeout-capped).
        private static readonly HttpClient s_http;
        public static HttpClient Http => s_http;

        static UpdateCheckService()
        {
            // .NET Framework 4.8 on older Windows defaults to TLS 1.0/1.1;
            // GitHub requires TLS 1.2+. Set defensively, OR-in so we don't
            // disable other protocols a host process may need elsewhere.
            try
            {
                ServicePointManager.SecurityProtocol |=
                    SecurityProtocolType.Tls12;
                // Tls13 is .NET Framework 4.8+ but only fires when the OS
                // supports it (Win10 2004+). Cast to underlying enum value to
                // avoid a compile-time miss on older reference assemblies.
                const SecurityProtocolType tls13 = (SecurityProtocolType)12288;
                ServicePointManager.SecurityProtocol |= tls13;
            }
            catch { /* SecurityProtocol is best-effort */ }

            s_http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            };

            // GitHub returns 403 without a User-Agent. Include the plugin
            // version so abuse reports can find us quickly.
            string version;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = (AssemblyInformationalVersionAttribute?)Attribute
                    .GetCustomAttribute(asm, typeof(AssemblyInformationalVersionAttribute));
                version = info?.InformationalVersion ?? "unknown";
                int plus = version.IndexOf('+');
                if (plus >= 0) version = version.Substring(0, plus);
            }
            catch { version = "unknown"; }

            s_http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"MozaPlugin/{version} (+https://github.com/{RepoOwner}/{RepoName})");
            s_http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        // ----- Channel ids -----

        public static string PrChannelId(int prNumber) => "pr/" + prNumber;

        public static bool TryParsePrChannelId(string? channelId, out int prNumber)
        {
            prNumber = 0;
            return channelId != null
                && channelId.StartsWith("pr/", StringComparison.Ordinal)
                && int.TryParse(channelId.Substring(3), out prNumber)
                && prNumber > 0;
        }

        // ----- Snapshot fetch + cache -----

        private static UpdateSnapshot? s_snapshot;

        /// <summary>
        /// Most recent successfully fetched snapshot (null before the first
        /// fetch this process). Shared by the startup check, manual checks and
        /// the channel dropdown so one API call serves all surfaces.
        /// </summary>
        public static UpdateSnapshot? CachedSnapshot => Volatile.Read(ref s_snapshot);

        /// <summary>
        /// Fetches the release list and parses it into a snapshot: newest
        /// stable release + newest build per open PR. Caches on success.
        /// </summary>
        public static async Task<SnapshotFetchResult> FetchSnapshotAsync(CancellationToken ct)
        {
            string listUrl =
                $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=100";
            var page = await HttpGetAsync(listUrl, ct).ConfigureAwait(false);
            if (page.Body == null)
                return SnapshotFetchResult.Fail(page.ErrorKind, page.ErrorMessage);

            ReleaseEntry? stable = null;
            var prChannels = new Dictionary<int, PrChannel>();
            try
            {
                foreach (var token in JArray.Parse(page.Body))
                {
                    if (token is not JObject rel) continue;
                    if ((bool?)rel["draft"] == true) continue;
                    string tag = (string?)rel["tag_name"] ?? "";
                    string name = (string?)rel["name"] ?? "";

                    if (s_stableTagRx.IsMatch(tag) && (bool?)rel["prerelease"] != true)
                    {
                        // Max by SemVer, not list order — the API sorts by
                        // creation time, which a re-published tag can skew.
                        var entry = MakeEntry(rel, tag, StripLeadingV(tag));
                        if (stable == null || CompareSemVer(entry.Version, stable.Version) > 0)
                            stable = entry;
                        continue;
                    }

                    var m = s_prTagRx.Match(tag);
                    if (!m.Success) continue; // legacy dev-* tags, hand-cut tags
                    string version = ExtractPrVersionFromName(name);
                    if (version.Length == 0) continue; // renamed by hand — skip, don't fail
                    int prNumber = int.Parse(m.Groups[1].Value);
                    var prEntry = MakeEntry(rel, tag, version);
                    if (!prChannels.TryGetValue(prNumber, out var existing)
                        || prEntry.PublishedAtUtc > existing.Newest.PublishedAtUtc)
                    {
                        prChannels[prNumber] = new PrChannel(
                            prNumber, ExtractPrTitleFromName(name, prNumber), prEntry);
                    }
                }
            }
            catch (Exception ex)
            {
                return SnapshotFetchResult.Fail(UpdateCheckErrorKind.Parse, ex.Message);
            }

            if (stable == null)
            {
                // A page full of prereleases could push every stable off page 1
                // — ask for the canonical latest directly. Null (e.g. 404 on a
                // repo with no stable release yet) is a valid snapshot state.
                stable = await FetchLatestStableAsync(ct).ConfigureAwait(false);
            }

            var snapshot = new UpdateSnapshot(
                stable,
                prChannels.Values.OrderBy(c => c.Number).ToList(),
                DateTime.UtcNow);
            Volatile.Write(ref s_snapshot, snapshot);
            return SnapshotFetchResult.Ok(snapshot);
        }

        private static async Task<ReleaseEntry?> FetchLatestStableAsync(CancellationToken ct)
        {
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var resp = await HttpGetAsync(url, ct).ConfigureAwait(false);
            if (resp.Body == null) return null;
            try
            {
                var rel = JObject.Parse(resp.Body);
                string tag = (string?)rel["tag_name"] ?? "";
                if (!s_stableTagRx.IsMatch(tag)) return null;
                return MakeEntry(rel, tag, StripLeadingV(tag));
            }
            catch { return null; }
        }

        private static ReleaseEntry MakeEntry(JObject rel, string tag, string version)
        {
            DateTime published =
                ((DateTime?)rel["published_at"])?.ToUniversalTime() ?? DateTime.MinValue;
            return new ReleaseEntry(
                tag,
                version,
                (string?)rel["html_url"] ?? "",
                (string?)rel["body"] ?? "",
                ExtractAssetUrl(rel),
                published);
        }

        /// <summary>
        /// Maps the selected channel onto the flat result shape the persist +
        /// banner pipeline consumes. <paramref name="channelFound"/> is false
        /// when a PR channel has no builds left (PR closed/merged and its
        /// releases cleaned up) — callers fall back to stable.
        /// </summary>
        public static UpdateCheckResult ResolveChannel(
            UpdateSnapshot snapshot, string? channelId, out bool channelFound)
        {
            channelFound = true;
            if (TryParsePrChannelId(channelId, out int prNumber))
            {
                foreach (var ch in snapshot.PrChannels)
                {
                    if (ch.Number != prNumber) continue;
                    var e = ch.Newest;
                    return UpdateCheckResult.Ok(e.Version, e.HtmlUrl, e.Notes, e.AssetUrl);
                }
                channelFound = false;
                return UpdateCheckResult.NoReleaseAvailable();
            }

            var st = snapshot.Stable;
            return st == null
                ? UpdateCheckResult.NoReleaseAvailable()
                : UpdateCheckResult.Ok(st.Version, st.HtmlUrl, st.Notes, st.AssetUrl);
        }

        private sealed class HttpTextResult
        {
            public string? Body;
            public UpdateCheckErrorKind ErrorKind;
            public string ErrorMessage = "";
        }

        private static async Task<HttpTextResult> HttpGetAsync(string url, CancellationToken ct)
        {
            HttpResponseMessage? resp = null;
            try
            {
                resp = await s_http.GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Genuine cancel from the caller — distinguish from a timeout
                // (which also surfaces as Task/OperationCanceledException on
                // .NET Framework's HttpClient) by checking the token state.
                return new HttpTextResult { ErrorKind = UpdateCheckErrorKind.Cancelled };
            }
            catch (OperationCanceledException)
            {
                // .NET Framework HttpClient maps timeouts to a cancelled task
                // whose token is not the one the caller passed in.
                return new HttpTextResult
                {
                    ErrorKind = UpdateCheckErrorKind.Network,
                    ErrorMessage = "timeout",
                };
            }
            catch (Exception ex)
            {
                return new HttpTextResult
                {
                    ErrorKind = UpdateCheckErrorKind.Network,
                    ErrorMessage = ex.Message,
                };
            }

            try
            {
                if (!resp.IsSuccessStatusCode)
                {
                    return new HttpTextResult
                    {
                        ErrorKind = UpdateCheckErrorKind.Http,
                        ErrorMessage = $"HTTP {(int)resp.StatusCode}",
                    };
                }
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return new HttpTextResult { Body = body };
            }
            finally
            {
                resp?.Dispose();
            }
        }

        // Pull the browser_download_url for the first ZIP asset that looks
        // like our plugin bundle. Both stable (`MozaPlugin_v1.5.1.zip`) and
        // PR builds (`MozaPlugin_pr42_a1b2c3d.zip`) follow the
        // `MozaPlugin*.zip` pattern, so a startswith+endswith match handles
        // both. Returns "" if no matching asset is found — the caller treats
        // absent asset URL as "in-app install unavailable, fall back to
        // release-notes link".
        internal static string ExtractAssetUrl(JObject json)
        {
            try
            {
                var assets = json["assets"] as JArray;
                if (assets == null) return "";
                foreach (var asset in assets)
                {
                    string assetName = (string?)asset["name"] ?? "";
                    if (assetName.StartsWith("MozaPlugin", StringComparison.OrdinalIgnoreCase)
                        && assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        return (string?)asset["browser_download_url"] ?? "";
                    }
                }
            }
            catch { /* malformed assets array — fall through to empty */ }
            return "";
        }

        // Pulls "1.5.3-pr.42.a1b2c3d" out of a release named
        // "<title> (1.5.3-pr.42.a1b2c3d)". Empty when the name doesn't match —
        // the caller skips that release rather than failing the fetch.
        internal static string ExtractPrVersionFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var m = s_prNameVersionRx.Match(name);
            return m.Success ? m.Groups[1].Value : "";
        }

        // Pulls the PR title out of the same name shape; falls back to "#<N>"
        // when the name was edited out of recognition.
        internal static string ExtractPrTitleFromName(string name, int prNumber)
        {
            string fallback = "#" + prNumber;
            if (string.IsNullOrEmpty(name)) return fallback;
            string s = name;
            var m = s_prNameVersionRx.Match(s);
            if (m.Success) s = s.Substring(0, m.Index);
            // Early PR releases were named "PR #<N>: <title> (…)" — tolerate
            // the legacy prefix.
            string prefix = $"PR #{prNumber}:";
            if (s.StartsWith(prefix, StringComparison.Ordinal)) s = s.Substring(prefix.Length);
            s = s.Trim();
            return s.Length > 0 ? s : fallback;
        }

        private static string StripLeadingV(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s[0] == 'v' || s[0] == 'V') return s.Substring(1);
            return s;
        }

        /// <summary>
        /// Decide whether <paramref name="latest"/> represents a build the
        /// user should be offered over <paramref name="current"/> on the
        /// selected channel. A PR channel tracks a moving head, so any
        /// differing build is an update — a newer commit, or the target build
        /// right after a channel switch. On stable, a running prerelease build
        /// (a PR/dev build whose channel the user left) is offered the stable
        /// release even though its numeric core is lower — that downgrade is
        /// the point of switching back. Stable-on-stable only ever moves
        /// forward, by spec-correct SemVer.
        /// </summary>
        public static bool IsUpdateAvailable(
            string latest, string current, string? channelId)
        {
            if (string.IsNullOrEmpty(latest)) return false;
            if (string.IsNullOrEmpty(current)) return true;

            if (TryParsePrChannelId(channelId, out _))
                return !string.Equals(latest, current, StringComparison.Ordinal);

            if (HasPrereleaseTag(current))
                return !string.Equals(latest, current, StringComparison.Ordinal);

            return CompareSemVer(latest, current) > 0;
        }

        // True when the version carries a SemVer prerelease part ("-…", any
        // "+build" metadata ignored) — i.e. the running DLL came from a PR
        // build (or a legacy dev build) rather than a stable tag.
        internal static bool HasPrereleaseTag(string version)
        {
            int plus = version.IndexOf('+');
            string v = plus >= 0 ? version.Substring(0, plus) : version;
            return v.IndexOf('-') >= 0;
        }

        /// <summary>
        /// Compare two SemVer strings. Returns &lt;0 if a&lt;b, 0 if equal,
        /// &gt;0 if a&gt;b. Tolerates malformed input by treating
        /// unparseable strings as equal — better to under-report an update
        /// than to spam users with a banner driven by a parser bug.
        /// </summary>
        public static int CompareSemVer(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return 0;
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 0;
            if (string.IsNullOrEmpty(a)) return -1;
            if (string.IsNullOrEmpty(b)) return 1;

            ParseVersion(a, out int[] coreA, out string preA);
            ParseVersion(b, out int[] coreB, out string preB);

            for (int i = 0; i < 3; i++)
            {
                int c = coreA[i].CompareTo(coreB[i]);
                if (c != 0) return c;
            }

            // Per SemVer §11: a version without prerelease > version with prerelease
            bool aHasPre = !string.IsNullOrEmpty(preA);
            bool bHasPre = !string.IsNullOrEmpty(preB);
            if (!aHasPre && !bHasPre) return 0;
            if (!aHasPre) return 1;
            if (!bHasPre) return -1;

            // Both have prerelease — compare dot-separated identifiers.
            var idsA = preA.Split('.');
            var idsB = preB.Split('.');
            int n = Math.Min(idsA.Length, idsB.Length);
            for (int i = 0; i < n; i++)
            {
                int cmp = CompareIdentifier(idsA[i], idsB[i]);
                if (cmp != 0) return cmp;
            }
            // All shared identifiers equal — more identifiers wins.
            return idsA.Length.CompareTo(idsB.Length);
        }

        private static int CompareIdentifier(string x, string y)
        {
            bool xNum = int.TryParse(x, out int xi);
            bool yNum = int.TryParse(y, out int yi);
            if (xNum && yNum) return xi.CompareTo(yi);
            // Per SemVer §11: numeric identifiers have lower precedence than
            // alphanumeric ones.
            if (xNum) return -1;
            if (yNum) return 1;
            return string.CompareOrdinal(x, y);
        }

        // Splits a version string into 3-part numeric core + prerelease tail.
        // Build metadata (after '+') is ignored per SemVer §10.
        private static void ParseVersion(string s, out int[] core, out string prerelease)
        {
            core = new int[3];
            prerelease = "";

            // Strip build metadata.
            int plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);

            // Split off prerelease.
            int dash = s.IndexOf('-');
            string coreStr;
            if (dash >= 0)
            {
                coreStr = s.Substring(0, dash);
                prerelease = s.Substring(dash + 1);
            }
            else
            {
                coreStr = s;
            }

            var parts = coreStr.Split('.');
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out core[i])) core[i] = 0;
            }
        }
    }
}
