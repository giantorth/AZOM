using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>Upload a pre-built bundle. Text fields are re-sanitized here as defense in depth.</summary>
        public static async Task<Result> SubmitAsync(
            byte[] bundle, string description, string contact,
            string version, string os, string model, CancellationToken ct)
        {
            using (var content = BuildMultipartContent(bundle, description, contact, version, os, model))
            {
                try
                {
                    using (var resp = await s_http.PostAsync(ReportEndpoint, content, ct).ConfigureAwait(false))
                    {
                        int code = (int)resp.StatusCode;
                        if (resp.IsSuccessStatusCode)
                        {
                            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            string? ticket = null;
                            try { ticket = (string?)JObject.Parse(body)["ticketId"]; } catch { /* body not JSON */ }
                            return new Result { Outcome = Outcome.Success, TicketId = ticket };
                        }
                        // Capture the worker's error body ({"error":"..."}) so a
                        // failure is diagnosable from the log, not just a bare code.
                        string errBody = "";
                        try { errBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
                        if (errBody != null && errBody.Length > 300) errBody = errBody.Substring(0, 300);
                        string detail = string.IsNullOrEmpty(errBody) ? code.ToString() : $"{code}: {errBody}";
                        if (code == 429) return new Result { Outcome = Outcome.RateLimited, Detail = detail };
                        if (code == 413) return new Result { Outcome = Outcome.TooLarge, Detail = detail };
                        return new Result { Outcome = Outcome.ServerError, Detail = detail };
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return new Result { Outcome = Outcome.NetworkError, Detail = "cancelled" };
                }
                catch (OperationCanceledException)
                {
                    return new Result { Outcome = Outcome.NetworkError, Detail = "timeout" };
                }
                catch (HttpRequestException ex)
                {
                    return new Result { Outcome = Outcome.NetworkError, Detail = ex.Message };
                }
            }
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
            var ms = new MemoryStream();
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

            var httpContent = new ByteArrayContent(ms.ToArray());
            httpContent.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");
            return httpContent;
        }
    }
}
