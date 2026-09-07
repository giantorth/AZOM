using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MozaPlugin.Diagnostics;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.UI.BugReport
{
    /// <summary>
    /// Client side of the "Submit bug report" feature: sanitizes the user's
    /// free text and uploads a diagnostics bundle to the Cloudflare Worker
    /// (see <c>worker/</c>). Bundle assembly lives in the UI code-behind (it
    /// needs the live plugin/data + capture snapshots); this class owns text
    /// sanitization, the HTTP upload, and the response mapping.
    /// </summary>
    internal static class BugReportService
    {
        // Worker endpoint. Set at deploy time — see worker/README.md. Kept as a
        // single constant so there is exactly one place to point at the deployed
        // Worker (or a `wrangler dev` URL while testing).
        public const string ReportEndpoint = "https://bugreport.giant.orth.cc/report";

        // Same Worker on workers.dev, which is outside the zone and so cannot be
        // CDN-challenged. See worker/README.md § HTTP surface.
        public const string ReportEndpointFallback =
            "https://moza-bugreport.ryan-894.workers.dev/report";

        // Tried in order; only an edge block or transport failure advances.
        private static readonly string[] s_endpoints = { ReportEndpoint, ReportEndpointFallback };

        public const int MaxDescriptionChars = 2000;
        public const int MaxContactChars = 200;
        // Client-side size guard. The Worker independently rejects oversized
        // bodies; this lets us drop the rolling segment and retry before upload.
        public const long MaxUploadBytes = 10L * 1024 * 1024;
        // Local double-submit guard; the real per-IP limits live in the Worker.
        public static readonly TimeSpan SubmitCooldown = TimeSpan.FromSeconds(60);

        public enum Outcome { Success, RateLimited, TooLarge, NetworkError, ServerError }

        public struct Result
        {
            public Outcome Outcome;
            public string? TicketId;
            public string? Detail;
            // HTTP status when the server answered at all (0 for a transport
            // failure), plus a compact code the status line can show verbatim
            // ("HTTP 403", "network: timeout") so a refused user can quote it.
            public int StatusCode;
            public string? ShortCode;
            // Refused by the CDN, not the Worker: a retry cannot help, so the UI
            // offers the browser hand-off.
            public bool EdgeBlocked;
        }

        // Dedicated client (not UpdateCheckService.Http): a multi-MB body upload
        // on a slow uplink can outlast that client's 10s header-read timeout, so
        // this one uses a generous timeout. TLS 1.2/1.3 is enabled process-wide
        // (ServicePointManager) here as well, in case the update-check client
        // was never touched this session.
        private static readonly HttpClient s_http;

        static BugReportService()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                const SecurityProtocolType tls13 = (SecurityProtocolType)12288;
                ServicePointManager.SecurityProtocol |= tls13;
            }
            catch { /* best-effort */ }

            s_http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            string version;
            try { version = DiagnosticsTextBuilder.GetPluginVersion(); }
            catch { version = "unknown"; }
            s_http.DefaultRequestHeaders.UserAgent.ParseAdd($"MozaPlugin/{version}");
        }

        /// <summary>
        /// Clean user-entered text before it goes into the bundle and over the
        /// wire: normalize newlines, drop control chars (tabs → space), collapse
        /// runs of blank lines, and hard-cap the length. When
        /// <paramref name="singleLine"/> is set, newlines collapse to spaces.
        /// </summary>
        public static string SanitizeUserText(string? input, int maxLen, bool singleLine = false)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var s = input!.Replace("\r\n", "\n").Replace('\r', '\n');
            var sb = new StringBuilder(Math.Min(s.Length, maxLen));
            int newlineRun = 0;
            foreach (var c in s)
            {
                if (c == '\n')
                {
                    if (singleLine) { sb.Append(' '); continue; }
                    newlineRun++;
                    if (newlineRun <= 2) sb.Append('\n'); // collapse 3+ blank lines to one gap
                    continue;
                }
                newlineRun = 0;
                if (c == '\t') { sb.Append(' '); continue; }
                if (char.IsControl(c)) continue; // strip other control chars
                sb.Append(c);
                if (sb.Length >= maxLen) break;
            }
            var result = sb.ToString().Trim();
            if (result.Length > maxLen) result = result.Substring(0, maxLen);
            return result;
        }

        /// <summary>
        /// Upload a pre-built bundle. Text fields are re-sanitized here as
        /// defense in depth. Every attempt is written to
        /// <see cref="BugReportUploadLog"/> (and, on failure, to the plugin log)
        /// with the response detail needed to tell a Worker-level rejection
        /// apart from an edge/proxy/TLS one — a user whose uploads are refused
        /// can then export the bundle by hand and the reason travels with it.
        /// </summary>
        public static async Task<Result> SubmitAsync(
            byte[] bundle, string description, string contact,
            string version, string os, string model, CancellationToken ct)
        {
            var rec = new StringBuilder();
            rec.AppendLine($"=== Attempt {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ===");
            rec.AppendLine($"  Bundle:      {bundle.Length:N0} bytes");
            rec.AppendLine($"  Plugin / OS: {version} / {os}");
            rec.AppendLine($"  Wheel:       {model}");
            rec.AppendLine($"  Text:        description {description.Length} chars, contact " +
                           $"{(string.IsNullOrEmpty(contact) ? "none" : contact.Length + " chars")}");
            rec.AppendLine($"  TLS policy:  {ServicePointManager.SecurityProtocol}");

            Result result = default;
            int used = 0;
            bool edgeBlockSeen = false;
            try
            {
                for (int i = 0; i < s_endpoints.Length; i++)
                {
                    used = i;
                    var attempt = await PostOnceAsync(
                        s_endpoints[i], rec, bundle, description, contact,
                        version, os, model, ct).ConfigureAwait(false);
                    result = attempt.Result;
                    // Sticky: a primary challenge still wants the hand-off even if the
                    // fallback then died on transport.
                    result.EdgeBlocked = edgeBlockSeen |= attempt.EdgeBlocked;

                    if (result.Outcome == Outcome.Success) break;
                    // A Worker answer is the final word — a blind retry of a 429
                    // or a rejected body would only double-post.
                    if (!attempt.EdgeBlocked && !attempt.Transport) break;
                    if (i + 1 >= s_endpoints.Length) break;

                    rec.AppendLine($"  Failover:    {(attempt.EdgeBlocked ? "CDN edge refusal" : "no response")}" +
                                   $" — retrying on {s_endpoints[i + 1]}");
                }
            }
            catch (Exception ex)
            {
                // Anything unexpected still leaves a record behind before it
                // goes back to the caller's error path.
                rec.AppendLine($"  Exception:   {DescribeException(ex)}");
                rec.AppendLine("  Outcome:     unhandled — rethrown to caller");
                BugReportUploadLog.Record(rec.ToString());
                throw;
            }

            if (result.Outcome == Outcome.Success && used > 0)
                MozaLog.Warn($"[AZOM] Bug report accepted on fallback endpoint {s_endpoints[used]} — " +
                             "the primary was refused by the CDN edge");

            if (result.Outcome != Outcome.Success)
                await AppendConnectivityProbeAsync(rec, ct).ConfigureAwait(false);

            BugReportUploadLog.Record(rec.ToString());

            // Failures also go to the plugin log, so a user who sends only the
            // SimHub log (or no bundle at all) still hands over the whole reason.
            if (result.Outcome != Outcome.Success)
                MozaLog.Warn(
                    $"[AZOM] Bug report not accepted ({result.Outcome}, {result.ShortCode}) — detail follows\n{rec}");

            return result;
        }

        /// <summary>One POST to one endpoint, plus why it failed if it did.</summary>
        private struct Attempt
        {
            public Result Result;
            // The CDN answered instead of the Worker, so nothing was stored.
            public bool EdgeBlocked;
            // No response at all, excluding a user cancel.
            public bool Transport;
        }

        // Content is rebuilt per attempt: an HttpContent is single-use.
        private static async Task<Attempt> PostOnceAsync(
            string endpoint, StringBuilder rec, byte[] bundle, string description, string contact,
            string version, string os, string model, CancellationToken ct)
        {
            rec.AppendLine($"  Request:     POST {endpoint}");
            var sw = Stopwatch.StartNew();
            using (var content = BuildMultipartContent(bundle, description, contact, version, os, model))
            {
                try
                {
                    using (var resp = await s_http.PostAsync(endpoint, content, ct).ConfigureAwait(false))
                    {
                        int code = (int)resp.StatusCode;
                        // Read the body once: on success it carries the ticket,
                        // on failure the Worker's {"error":"..."} (or the edge's
                        // HTML block page, which is itself the diagnosis).
                        string body = "";
                        try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
                        catch (Exception ex) { body = $"(body read failed: {ex.GetType().Name}: {ex.Message})"; }

                        rec.AppendLine($"  Response:    HTTP {code} {resp.ReasonPhrase} in {sw.ElapsedMilliseconds} ms");
                        AppendResponseHeaders(rec, resp);
                        rec.AppendLine($"  Body:        {Snip(body, 1500)}");

                        if (resp.IsSuccessStatusCode)
                        {
                            string? ticket = null;
                            try { ticket = (string?)JObject.Parse(body)["ticketId"]; } catch { /* body not JSON */ }
                            rec.AppendLine($"  Outcome:     Success (ticket {ticket ?? "not in response"})");
                            return new Attempt
                            {
                                Result = new Result
                                {
                                    Outcome = Outcome.Success,
                                    TicketId = ticket,
                                    StatusCode = code,
                                    ShortCode = $"HTTP {code}",
                                },
                            };
                        }

                        bool edge = IsEdgeBlock(resp, body);
                        if (edge)
                            rec.AppendLine("  Edge block:  yes — CDN challenge/block, request never reached the Worker");

                        string errBody = Snip(body, 300);
                        string detail = string.IsNullOrEmpty(errBody) ? code.ToString() : $"{code}: {errBody}";
                        var outcome = code == 429 ? Outcome.RateLimited
                                    : code == 413 ? Outcome.TooLarge
                                    : Outcome.ServerError;
                        rec.AppendLine($"  Outcome:     {outcome}");
                        return new Attempt
                        {
                            Result = new Result
                            {
                                Outcome = outcome,
                                Detail = detail,
                                StatusCode = code,
                                ShortCode = $"HTTP {code}",
                            },
                            EdgeBlocked = edge,
                        };
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is HttpRequestException)
                {
                    bool cancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
                    string shortCode = cancelled ? "network: cancelled"
                                     : ex is OperationCanceledException ? "network: timeout"
                                     : $"network: {InnermostName(ex)}";
                    rec.AppendLine($"  Response:    none after {sw.ElapsedMilliseconds} ms");
                    rec.AppendLine($"  Exception:   {DescribeException(ex)}");
                    rec.AppendLine("  Outcome:     NetworkError");
                    return new Attempt
                    {
                        Result = new Result
                        {
                            Outcome = Outcome.NetworkError,
                            Detail = cancelled ? "cancelled" : DescribeException(ex),
                            ShortCode = shortCode,
                        },
                        // A cancel must not burn the fallback; a real fault should.
                        // Risk accepted: a duplicate store if only the response was lost.
                        Transport = !cancelled,
                    };
                }
            }
        }

        // cf-mitigated is Cloudflare's challenge/block marker; an HTML body is the
        // other tell, since every Worker answer is JSON or empty.
        private static bool IsEdgeBlock(HttpResponseMessage resp, string body)
        {
            if (resp.Headers.Contains("cf-mitigated")) return true;

            var mediaType = resp.Content?.Headers?.ContentType?.MediaType;
            if (mediaType != null &&
                mediaType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            if (!string.IsNullOrEmpty(body))
            {
                if (body.IndexOf("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (body.IndexOf("cf-browser-verification", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (body.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Runs only after a failed submit. Separates "nothing between us and the
        /// Worker is working" (DNS/TLS/proxy) from "the Worker itself refused":
        /// the Worker serves nothing at the origin root, so its answer there is a
        /// bodyless 404 carrying <c>cf-ray</c>. Anything else — HTML, a non-empty
        /// body, 403, timeout — points at the edge, a corporate proxy, or
        /// intercepting AV.
        /// </summary>
        private static async Task AppendConnectivityProbeAsync(StringBuilder rec, CancellationToken ct)
        {
            Uri uri;
            try { uri = new Uri(ReportEndpoint); }
            catch { return; }

            rec.AppendLine("  --- connectivity probe ---");

            try
            {
                var addrs = await Dns.GetHostAddressesAsync(uri.Host).ConfigureAwait(false);
                var text = new List<string>(addrs.Length);
                foreach (var a in addrs) text.Add(a.ToString());
                rec.AppendLine($"  DNS {uri.Host}: {(text.Count == 0 ? "(no addresses)" : string.Join(", ", text))}");
            }
            catch (Exception ex)
            {
                rec.AppendLine($"  DNS {uri.Host}: FAILED — {DescribeException(ex)}");
            }

            try
            {
                var via = WebRequest.GetSystemWebProxy()?.GetProxy(uri);
                // IWebProxy hands back the request URI itself when it is direct.
                rec.AppendLine(via == null || via.Equals(uri)
                    ? "  Proxy:       none for this host"
                    : $"  Proxy:       {via}");
            }
            catch (Exception ex)
            {
                rec.AppendLine($"  Proxy:       (lookup failed: {ex.GetType().Name})");
            }

            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    // Short: the user is already waiting on a failed submit.
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    string probeUrl = uri.GetLeftPart(UriPartial.Authority) + "/";
                    using (var resp = await s_http.GetAsync(probeUrl, cts.Token).ConfigureAwait(false))
                    {
                        string body = "";
                        try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                        rec.AppendLine($"  GET {probeUrl}: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                        AppendResponseHeaders(rec, resp);
                        rec.AppendLine($"  GET body:    {Snip(body, 300)}");
                    }
                }
            }
            catch (Exception ex)
            {
                rec.AppendLine($"  GET probe:   FAILED — {DescribeException(ex)}");
            }
        }

        // cf-ray/cf-mitigated identify a Cloudflare edge block (WAF / bot fight)
        // as opposed to a Worker-level rejection; server/via expose an
        // intercepting proxy; retry-after times a rate limit.
        private static readonly string[] s_headersOfInterest =
        {
            "cf-ray", "cf-mitigated", "cf-cache-status", "server", "via",
            "retry-after", "content-type", "content-length",
        };

        private static void AppendResponseHeaders(StringBuilder rec, HttpResponseMessage resp)
        {
            var parts = new List<string>();
            foreach (var key in s_headersOfInterest)
            {
                if (resp.Headers.TryGetValues(key, out var values))
                    parts.Add($"{key}={string.Join(",", values)}");
                else if (resp.Content != null && resp.Content.Headers.TryGetValues(key, out var contentValues))
                    parts.Add($"{key}={string.Join(",", contentValues)}");
            }
            rec.AppendLine($"  Headers:     {(parts.Count == 0 ? "(none of interest)" : string.Join("; ", parts))}");
        }

        /// <summary>Full exception chain — the cause of a TLS/proxy failure is always in an inner exception.</summary>
        private static string DescribeException(Exception? ex)
        {
            var sb = new StringBuilder();
            for (int depth = 0; ex != null && depth < 5; depth++, ex = ex.InnerException)
            {
                if (depth > 0) sb.Append(" → ");
                sb.Append(ex.GetType().Name).Append(": ").Append(Snip(ex.Message, 300));
                if (ex is WebException we) sb.Append($" [WebExceptionStatus={we.Status}]");
                if (ex is SocketException se) sb.Append($" [SocketError={se.SocketErrorCode}/{se.NativeErrorCode}]");
            }
            return sb.ToString();
        }

        private static string InnermostName(Exception ex)
        {
            var cur = ex;
            for (int depth = 0; cur.InnerException != null && depth < 5; depth++) cur = cur.InnerException;
            return cur is SocketException se ? se.SocketErrorCode.ToString() : cur.GetType().Name;
        }

        /// <summary>Single-line, length-capped text for a log record.</summary>
        private static string Snip(string? text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var flat = text!.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max) + $"… (+{flat.Length - max} chars)";
        }

        // Hand-build the multipart/form-data body. .NET Framework's
        // MultipartFormDataContent emits Content-Disposition names *unquoted*
        // (name=bundle), which Cloudflare Workers' formData() parser rejects →
        // HTTP 400. We emit RFC 7578-compliant quoted names in the exact byte
        // layout verified against the deployed worker.
        private static HttpContent BuildMultipartContent(
            byte[] bundle, string description, string contact, string version, string os, string model)
        {
            string boundary = "MozaReport" + Guid.NewGuid().ToString("N");
            // Pre-sized, and handed to the content as a buffer slice below: the
            // default doubling growth plus a ToArray() copy tripled a 10 MB bundle
            // in large-object heap space inside the x86 process.
            var ms = new MemoryStream(bundle.Length + 2048);
            void Ascii(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
            void Utf8(string s) { var b = Encoding.UTF8.GetBytes(s); ms.Write(b, 0, b.Length); }
            void Field(string name, string value)
            {
                Ascii($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n");
                Utf8(value);
                Ascii("\r\n");
            }

            Field("description", SanitizeUserText(description, MaxDescriptionChars));
            if (!string.IsNullOrEmpty(contact))
                Field("contact", SanitizeUserText(contact, MaxContactChars, singleLine: true));
            Field("version", version ?? "");
            Field("os", os ?? "");
            Field("model", model ?? "");

            Ascii($"--{boundary}\r\nContent-Disposition: form-data; name=\"bundle\"; filename=\"bundle.zip\"\r\nContent-Type: application/zip\r\n\r\n");
            ms.Write(bundle, 0, bundle.Length);
            Ascii($"\r\n--{boundary}--\r\n");

            var httpContent = new ByteArrayContent(ms.GetBuffer(), 0, (int)ms.Length);
            httpContent.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");
            return httpContent;
        }
    }
}
