using System;
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
        Http,       // non-2xx (excluding 404 on dev-latest, which is "no release yet")
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
    /// Queries the GitHub Releases API for the latest stable or dev release of
    /// this plugin, parses the response, and exposes a SemVer comparator used
    /// by the banner-rendering code to decide whether the running build is out
    /// of date.
    /// </summary>
    public static class UpdateCheckService
    {
        private const string RepoOwner = "giantorth";
        private const string RepoName = "moza-simhub-plugin";
        private const string DevTag = "dev-latest";
        private const int TimeoutSeconds = 10;

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

        public static async Task<UpdateCheckResult> CheckAsync(
            UpdateChannel channel, CancellationToken ct)
        {
            string url = channel == UpdateChannel.Dev
                ? $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/tags/{DevTag}"
                : $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

            HttpResponseMessage? resp = null;
            string body;
            try
            {
                resp = await s_http.GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Genuine cancel from the caller — distinguish from a timeout
                // (which also surfaces as Task/OperationCanceledException on
                // .NET Framework's HttpClient) by checking the token state.
                return UpdateCheckResult.Fail(UpdateCheckErrorKind.Cancelled, "");
            }
            catch (OperationCanceledException)
            {
                // .NET Framework HttpClient maps timeouts to a cancelled task
                // whose token is not the one the caller passed in.
                return UpdateCheckResult.Fail(UpdateCheckErrorKind.Network, "timeout");
            }
            catch (HttpRequestException ex)
            {
                return UpdateCheckResult.Fail(UpdateCheckErrorKind.Network, ex.Message);
            }
            catch (Exception ex)
            {
                return UpdateCheckResult.Fail(UpdateCheckErrorKind.Network, ex.Message);
            }

            try
            {
                if ((int)resp.StatusCode == 404 && channel == UpdateChannel.Dev)
                {
                    // No dev-latest tag yet — not an error, just nothing to compare against.
                    return UpdateCheckResult.NoReleaseAvailable();
                }
                if (!resp.IsSuccessStatusCode)
                {
                    return UpdateCheckResult.Fail(
                        UpdateCheckErrorKind.Http,
                        $"HTTP {(int)resp.StatusCode}");
                }
                body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            finally
            {
                resp?.Dispose();
            }

            try
            {
                var json = JObject.Parse(body);
                string tagName = (string?)json["tag_name"] ?? "";
                string name = (string?)json["name"] ?? "";
                string htmlUrl = (string?)json["html_url"] ?? "";
                string notes = (string?)json["body"] ?? "";
                string assetUrl = ExtractAssetUrl(json);

                string version = ExtractVersion(channel, tagName, name);
                if (string.IsNullOrEmpty(version))
                {
                    return UpdateCheckResult.Fail(
                        UpdateCheckErrorKind.Parse,
                        "could not extract version from response");
                }
                return UpdateCheckResult.Ok(version, htmlUrl, notes, assetUrl);
            }
            catch (Exception ex)
            {
                return UpdateCheckResult.Fail(UpdateCheckErrorKind.Parse, ex.Message);
            }
        }

        // Pull the browser_download_url for the first ZIP asset that looks
        // like our plugin bundle. Both stable (`MozaPlugin_v0.9.2.zip`) and
        // dev (`MozaPlugin_dev.zip`) follow the `MozaPlugin*.zip` pattern,
        // so a startswith+endswith match handles both. Returns "" if no
        // matching asset is found — the caller treats absent asset URL as
        // "in-app install unavailable, fall back to release-notes link".
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

        // Stable: tag_name is "v1.2.3" → "1.2.3".
        // Dev: tag_name is the literal "dev-latest" (a moving tag); the
        //      release `name` is "dev-latest (0.0.1-dev.<sha>)", so we pull
        //      the inner version from the parenthesised portion.
        internal static string ExtractVersion(UpdateChannel channel, string tagName, string name)
        {
            if (channel == UpdateChannel.Stable)
            {
                return StripLeadingV(tagName);
            }

            // Dev path — try the parenthesised name first.
            if (!string.IsNullOrEmpty(name))
            {
                var m = Regex.Match(name, @"\(([^)]+)\)");
                if (m.Success)
                {
                    string inner = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(inner)) return StripLeadingV(inner);
                }
            }

            // Fall back to tag_name. If the tag is literally "dev-latest",
            // we have nothing comparable; return empty so the caller treats
            // it as "no release available".
            if (!string.IsNullOrEmpty(tagName) && tagName != DevTag)
            {
                return StripLeadingV(tagName);
            }
            return "";
        }

        private static string StripLeadingV(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s[0] == 'v' || s[0] == 'V') return s.Substring(1);
            return s;
        }

        /// <summary>
        /// Decide whether <paramref name="latest"/> represents a build the
        /// user should be offered as an update over <paramref name="current"/>.
        /// SemVer ordering alone is wrong for the dev channel: two dev builds
        /// in the same stable cycle share the MAJ.MIN.PAT core (the workflow
        /// stamps "<i>next</i>-dev.&lt;sha&gt;" where <i>next</i> only bumps
        /// when a new stable is tagged), so SemVer falls through to comparing
        /// the 7-char git SHA as an alphanumeric prerelease identifier — a
        /// near-random ordering that hides ~half of newer dev builds.
        ///
        /// The dev-latest GitHub tag is a rolling pointer reset on every push
        /// to dev (see .github/workflows/dev-build.yml), so any dev-formatted
        /// remote version whose SHA differs from the running one is, by
        /// construction, newer. We special-case that here and otherwise defer
        /// to spec-correct SemVer comparison.
        /// </summary>
        public static bool IsUpdateAvailable(
            string latest, string current, UpdateChannel channel)
        {
            if (string.IsNullOrEmpty(latest)) return false;
            if (string.IsNullOrEmpty(current)) return true;

            if (channel == UpdateChannel.Dev
                && TryParseDevBuildSha(latest, out int[]? coreL, out string shaL)
                && TryParseDevBuildSha(current, out int[]? coreC, out string shaC)
                && coreL![0] == coreC![0]
                && coreL[1] == coreC[1]
                && coreL[2] == coreC[2]
                && !string.Equals(shaL, shaC, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return CompareSemVer(latest, current) > 0;
        }

        // Returns true when `version` matches the CI dev-build shape
        // "MAJ.MIN.PAT-dev.<sha>" (any optional +build metadata stripped).
        // The SHA portion may be any non-empty token after "dev."; we keep it
        // lenient so a future commit-hash length change doesn't silently
        // disable the dev-channel comparison override.
        internal static bool TryParseDevBuildSha(
            string version, out int[]? core, out string sha)
        {
            core = null;
            sha = "";
            if (string.IsNullOrEmpty(version)) return false;

            // Drop +build metadata per SemVer §10 before parsing.
            int plus = version.IndexOf('+');
            string v = plus >= 0 ? version.Substring(0, plus) : version;

            int dash = v.IndexOf('-');
            if (dash < 0) return false;

            string coreStr = v.Substring(0, dash);
            string pre = v.Substring(dash + 1);

            // Prerelease must start with "dev." and have a non-empty tail.
            const string devPrefix = "dev.";
            if (!pre.StartsWith(devPrefix, StringComparison.Ordinal)) return false;
            string shaPart = pre.Substring(devPrefix.Length);
            if (string.IsNullOrEmpty(shaPart)) return false;
            // A SemVer prerelease can have more dot-separated identifiers
            // after the SHA in principle; treat the next segment, if any, as
            // belonging to the SHA identifier so we don't accidentally match
            // unrelated formats. The CI never emits a second identifier.
            int nextDot = shaPart.IndexOf('.');
            if (nextDot >= 0) return false;

            var parts = coreStr.Split('.');
            if (parts.Length < 3) return false;
            var parsed = new int[3];
            for (int i = 0; i < 3; i++)
            {
                if (!int.TryParse(parts[i], out parsed[i])) return false;
            }

            core = parsed;
            sha = shaPart;
            return true;
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
