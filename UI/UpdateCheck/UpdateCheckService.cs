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
        public UpdateCheckErrorKind ErrorKind { get; }
        public string ErrorMessage { get; }

        public UpdateCheckResult(
            bool success,
            string latestVersion,
            string releaseUrl,
            string releaseNotes,
            UpdateCheckErrorKind errorKind,
            string errorMessage)
        {
            Success = success;
            LatestVersion = latestVersion ?? "";
            ReleaseUrl = releaseUrl ?? "";
            ReleaseNotes = releaseNotes ?? "";
            ErrorKind = errorKind;
            ErrorMessage = errorMessage ?? "";
        }

        public static UpdateCheckResult Ok(string version, string url, string notes)
            => new UpdateCheckResult(true, version, url, notes, UpdateCheckErrorKind.None, "");

        public static UpdateCheckResult NoReleaseAvailable()
            => new UpdateCheckResult(true, "", "", "", UpdateCheckErrorKind.None, "");

        public static UpdateCheckResult Fail(UpdateCheckErrorKind kind, string message)
            => new UpdateCheckResult(false, "", "", "", kind, message);
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
        // so disposing this in End() would break the next Init.
        private static readonly HttpClient s_http;

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

                string version = ExtractVersion(channel, tagName, name);
                if (string.IsNullOrEmpty(version))
                {
                    return UpdateCheckResult.Fail(
                        UpdateCheckErrorKind.Parse,
                        "could not extract version from response");
                }
                return UpdateCheckResult.Ok(version, htmlUrl, notes);
            }
            catch (Exception ex)
            {
                return UpdateCheckResult.Fail(UpdateCheckErrorKind.Parse, ex.Message);
            }
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
