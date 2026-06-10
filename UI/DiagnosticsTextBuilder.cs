using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using MozaPlugin.Devices;
using MozaPlugin.Protocol;

namespace MozaPlugin.UI
{
    /// <summary>Pure-function text builders for the Diagnostics tab panels.</summary>
    internal static class DiagnosticsTextBuilder
    {
        // ── Field formatters ────────────────────────────────────────────

        public static string Blank(string s) => string.IsNullOrEmpty(s) ? "—" : s;
        public static string Redact(string s) => MozaLog.RedactId(s);
        public static string RedactBytes(byte[] b) => MozaLog.RedactBytesHex(b);
        public static string Hex(byte[] b) => b == null || b.Length == 0 ? "—" : BitConverter.ToString(b);
        public static string HexRaw(byte[] b) => b == null || b.Length == 0 ? "—" : BitConverter.ToString(b).Replace("-", "");
        public static string JoinList(IReadOnlyList<string> l)
            => l == null || l.Count == 0 ? "(empty)" : string.Join(", ", l);
        public static string TruncateId(string id)
            => string.IsNullOrEmpty(id) ? "—" : (id.Length > 40 ? id.Substring(0, 40) + "…" : id);

        /// <summary>Plugin assembly version (AssemblyInformationalVersion, +sha stripped).</summary>
        public static string GetPluginVersion()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = (AssemblyInformationalVersionAttribute?)Attribute
                    .GetCustomAttribute(asm, typeof(AssemblyInformationalVersionAttribute));
                var s = info?.InformationalVersion;
                if (!string.IsNullOrEmpty(s))
                {
                    int plus = s!.IndexOf('+');
                    return plus >= 0 ? s.Substring(0, plus) : s;
                }
                return asm.GetName().Version?.ToString() ?? "unknown";
            }
            catch { return "unknown"; }
        }

        // ── Per-panel builders ──────────────────────────────────────────

        public static string BuildPluginInfo() => $"Version:        {GetPluginVersion()}";

        public static string BuildUsbDetection(MozaPlugin plugin)
        {
            var sb = new StringBuilder();
            var ports = MozaPortDiscovery.Instance.Enumerate();
            string fallbackState;
            if (plugin.Settings.DisableSerialProbeFallback)
                fallbackState = "DISABLED";
            else if (ports.Count > 0)
                fallbackState = "armed (probes only unclassified COM ports)";
            else
                fallbackState = "armed (active — registry empty)";
            sb.AppendLine($"Source:         Registry  (probe fallback: {fallbackState})");

            if (ports.Count == 0)
            {
                sb.AppendLine("Discovered:     (no MOZA devices in registry)");
            }
            else
            {
                sb.AppendLine($"Discovered:     {ports.Count} device(s)");
                for (int i = 0; i < ports.Count; i++)
                {
                    var p = ports[i];
                    sb.AppendLine($"  {p.PortName,-6} VID 0x{p.Vid:X4}  PID 0x{p.Pid:X4}  {p.FriendlyName}");
                }
            }

            string wheelbasePort = plugin.Connection?.LastPortName ?? "";
            sb.Append("Assignments:    Wheelbase ");
            sb.Append(string.IsNullOrEmpty(wheelbasePort) ? "(disconnected)" : "→ " + wheelbasePort);
            string ab9Port = plugin.Ab9Manager?.Connection?.LastPortName ?? "";
            sb.Append("  |  AB9 ");
            sb.Append(string.IsNullOrEmpty(ab9Port) ? "(disconnected)" : "→ " + ab9Port);
            string hubPort = plugin.HubConnection?.IsConnected == true
                ? plugin.HubConnection.LastPortName ?? "" : "";
            sb.Append("  |  Hub ");
            sb.Append(string.IsNullOrEmpty(hubPort) ? "(disconnected)" : "→ " + hubPort);
            sb.AppendLine();
            // Base-aux pipe only exists after a base→hub migration (broken base,
            // wheel on hub); omit the line entirely in the common case.
            string baseAuxPort = plugin.BaseAuxConnection?.IsConnected == true
                ? plugin.BaseAuxConnection.LastPortName ?? "" : "";
            if (!string.IsNullOrEmpty(baseAuxPort))
                sb.AppendLine($"                Base(aux) → {baseAuxPort}  (wheel driven via hub)");

            // Classified open-failure surface. AccessDenied here is the
            // "port held by another app" footgun (PitHouse etc.); a stuck
            // ConsecutiveOpenFails count with PortVanished points at hot-
            // unplug or Wine pty teardown rather than user misconfig.
            var conn = plugin.Connection;
            if (conn != null)
            {
                var f = conn.LastFailure;
                sb.AppendLine(
                    $"LastFailure:    kind={f.Kind} port={f.PortName ?? "-"} " +
                    $"consecutive={conn.ConsecutiveOpenFailures}");
            }
            return sb.ToString();
        }

        public static string BuildMBoosterDevices(MozaPlugin plugin)
        {
            var registry = plugin.MBoosterRegistry;
            if (registry == null || registry.Devices.Count == 0)
                return "(no mBooster pedals detected — registry-only discovery; requires VID 0x346E PID 0x0008 in Windows USB enum)";

            var sb = new StringBuilder();
            var devs = registry.Devices;
            sb.AppendLine($"Discovered:     {devs.Count} mBooster device(s)");
            for (int i = 0; i < devs.Count; i++)
            {
                var d = devs[i];
                string id = MBoosterDeviceController.ShortIdentity(d.Identity);
                string state =
                    d.Detected      ? "detected"
                    : d.IsConnected ? "connected (probing)"
                                    : "disconnected";
                string roleStr;
                string dispNameStr;
                var s = d.CurrentSettings;
                if (s != null)
                {
                    roleStr     = s.Role.ToString();
                    dispNameStr = string.IsNullOrEmpty(s.DisplayName) ? "—" : s.DisplayName;
                }
                else
                {
                    roleStr = "(no settings row)";
                    dispNameStr = "—";
                }
                string livePort = d.Connection?.LastPortName ?? "";
                string port = string.IsNullOrEmpty(livePort) ? d.PortName : livePort;
                sb.AppendLine(
                    $"  [{i}] {port,-6}  role={roleStr,-8}  state={state}  " +
                    $"hidPos={d.LastHidPosition.ToString("F3", CultureInfo.InvariantCulture)}  " +
                    $"name='{dispNameStr}'  id={id}");
            }
            return sb.ToString().TrimEnd();
        }

        public static string BuildWheelIdentity(MozaData d)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Model:          {Blank(d.WheelModelName)}");
            sb.AppendLine($"FW (sw):        {Blank(d.WheelSwVersion)}");
            sb.AppendLine($"HW version:     {Blank(d.WheelHwVersion)}");
            sb.AppendLine($"HW sub:         {Blank(d.WheelHwSubVersion)}");
            sb.AppendLine($"Serial:         {Redact(d.WheelSerialNumber)}");
            sb.AppendLine($"Sub-devices:    {d.WheelSubDeviceCount}");
            sb.AppendLine($"Device presence:0x{d.WheelDevicePresence:X2}");
            sb.AppendLine($"Device type:    {Hex(d.WheelDeviceType)}");
            sb.AppendLine($"Capabilities:   {Hex(d.WheelCapabilities)}");
            sb.AppendLine($"MCU UID:        {RedactBytes(d.WheelMcuUid)}");
            sb.Append    ($"Identity-11:    {Hex(d.WheelIdentity11)}");
            return sb.ToString();
        }

        /// <summary>Diagnostics block for standalone dashboard target (CM2 etc.).
        /// Reports whether the open serial connection holds a Dashboard-PID port,
        /// the active telemetry target dev_id, and whether the dashboard pipeline
        /// is running in standalone mode. Empty when no Moza serial connection
        /// is open or when a wheel is driving the pipeline instead.</summary>
        public static string BuildStandaloneDashboardState(MozaPlugin plugin)
        {
            var conn = plugin?.Connection;
            if (conn == null) return "(no MOZA serial connection)";
            string pid = conn.DiscoveredPid ?? "(unknown)";
            string pidDesc = Protocol.MozaUsbIds.Describe(conn.DiscoveredPid);
            bool standalone = plugin?.ShouldUseStandaloneDashboardTarget() ?? false;
            byte target = plugin?.TelemetrySender?.TargetDeviceId ?? Protocol.MozaProtocol.DeviceWheel;
            string targetDesc = plugin?.TelemetrySender?.TargetDescription ?? $"0x{target:X2}";

            // Dedicated standalone-USB dashboard connection (CM2 0x0025 on its own port).
            var dashConn = plugin?.DashboardConnection;
            bool dashUsb = plugin?.DashboardUsbConnected ?? false;
            string dashLine = dashConn != null && dashConn.IsConnected
                ? $"{dashConn.LastPortName} {dashConn.DiscoveredPid} ({Protocol.MozaUsbIds.Describe(dashConn.DiscoveredPid)})"
                : "(not connected)";

            var sb = new StringBuilder();
            sb.AppendLine($"Wheelbase USB PID: {pid} ({pidDesc})");
            sb.AppendLine($"Dashboard conn:    {dashLine}");
            sb.AppendLine($"Dashboard USB:     {(dashUsb ? "yes" : "no")}");
            sb.AppendLine($"DashDetected:      {plugin?.IsDashDetected ?? false}");
            sb.AppendLine($"Standalone:        {standalone}");
            sb.Append    ($"Target dev_id:     {targetDesc}");
            return sb.ToString();
        }

        public static string BuildDisplayIdentity(MozaData d)
        {
            if (string.IsNullOrEmpty(d.DisplayModelName) && d.DisplayMcuUid.Length == 0)
                return "(display sub-device not probed or not present)";
            var sb = new StringBuilder();
            sb.AppendLine($"Model:          {Blank(d.DisplayModelName)}");
            sb.AppendLine($"FW (sw):        {Blank(d.DisplaySwVersion)}");
            sb.AppendLine($"HW version:     {Blank(d.DisplayHwVersion)}");
            sb.AppendLine($"Serial:         {Redact(d.DisplaySerialNumber)}");
            sb.AppendLine($"Sub-devices:    {d.DisplaySubDeviceCount}");
            sb.AppendLine($"Device presence:0x{d.DisplayDevicePresence:X2}");
            sb.AppendLine($"Device type:    {Hex(d.DisplayDeviceType)}");
            sb.AppendLine($"Capabilities:   {Hex(d.DisplayCapabilities)}");
            sb.AppendLine($"MCU UID:        {RedactBytes(d.DisplayMcuUid)}");
            sb.Append    ($"Identity-11:    {Hex(d.DisplayIdentity11)}");
            return sb.ToString();
        }

        public static string BuildDashboardState(MozaPlugin plugin)
        {
            var state = plugin.WheelStateForDiagnostics;
            if (state == null) return "(no configJson state received yet)";
            var sb = new StringBuilder();
            sb.AppendLine($"TitleId:        {state.TitleId}");
            sb.AppendLine($"displayVersion: {state.DisplayVersion}");
            sb.AppendLine($"resetVersion:   {state.ResetVersion}");
            sb.AppendLine($"sortTag:        {state.SortTag}");
            sb.AppendLine($"rootDirPath:    {Blank(state.RootDirPath)}");
            sb.AppendLine($"rootPath:       {Blank(state.RootPath)}");
            sb.AppendLine($"configJsonList ({state.ConfigJsonList.Count}): {JoinList(state.ConfigJsonList)}");
            sb.AppendLine($"imageRefMap:    {state.ImageRefMap.Count} entries");
            sb.AppendLine($"fontRefMap:     {state.FontRefMap.Count} entries");
            sb.AppendLine($"imagePath:      {state.ImagePath.Count} entries");
            sb.AppendLine($"captured at:    {state.CapturedAt:HH:mm:ss}");
            sb.AppendLine(Build28xRawLine(plugin));
            sb.AppendLine();
            sb.AppendLine($"-- Enabled dashboards ({state.EnabledDashboards.Count}) --");
            foreach (var d in state.EnabledDashboards)
            {
                sb.AppendLine($"  • {d.Title} / dirName={d.DirName} / id={TruncateId(d.Id)}");
                if (!string.IsNullOrEmpty(d.LastModified))
                    sb.AppendLine($"      lastModified: {d.LastModified}");
                if (d.IdealDeviceInfos.Count > 0)
                {
                    foreach (var info in d.IdealDeviceInfos)
                        sb.AppendLine($"      device: id={info.DeviceId} hw={info.HardwareVersion} product={info.ProductType}");
                }
            }
            sb.Append($"-- Disabled dashboards ({state.DisabledDashboards.Count}) --");
            foreach (var d in state.DisabledDashboards)
                sb.Append($"\n  • {d.Title} / {d.DirName}");
            return sb.ToString();
        }

        /// <summary>
        /// Wheel's most-recent 28:00 / 28:01 reply bytes raw, with age in ms.
        /// Semantics not decoded — captured for offline correlation.
        /// </summary>
        public static string Build28xRawLine(MozaPlugin plugin)
        {
            var d = plugin.Data;
            if (d == null) return "wheel 28:xx raw: (no data)";
            string b00 = d.Last28x00ByteValid
                ? $"0x{d.Last28x00Byte5:X2}" : "(none)";
            string b01 = d.Last28x01BytesValid
                ? $"0x{d.Last28x01Byte4:X2} 0x{d.Last28x01Byte5:X2}"
                : "(none)";
            string age;
            if (d.Last28xReplyTickMs == 0)
                age = "never";
            else
            {
                int dt = unchecked(Environment.TickCount - d.Last28xReplyTickMs);
                age = dt < 0 ? "?" : $"{dt} ms";
            }
            return $"wheel 28:xx raw: 28:00=[{b00}]  28:01=[{b01}]  age={age}";
        }

        public static string BuildTileServer(MozaPlugin plugin)
        {
            var tile = plugin.TileServerStateForDiagnostics;
            if (tile == null)
                return "(no inbound tile-server blob received — plugin PUSHES empty state on 0x03; wheel doesn't push back in current captures)";
            var sb = new StringBuilder();
            sb.AppendLine($"root:          {Blank(tile.Root)}");
            sb.AppendLine($"version:       {tile.Version}");
            sb.AppendLine($"any populated: {tile.AnyPopulated}");
            foreach (var kv in tile.Games)
            {
                var g = kv.Value;
                sb.Append($"\n[{kv.Key}] populated={g.Populated} map_version={g.MapVersion} " +
                          $"tile_size={g.TileSize} layers={g.LayersCount} name={Blank(g.Name)}");
            }
            return sb.ToString();
        }

        public static string BuildSessionState(MozaPlugin plugin)
        {
            var ts = plugin.TelemetrySender;
            if (ts == null && !plugin.TelemetryEnabledForDiagnostics)
                return "(telemetry not running)";
            var sb = new StringBuilder();
            sb.AppendLine($"Enabled:            {plugin.TelemetryEnabledForDiagnostics}");
            sb.AppendLine($"FramesSent:         {plugin.FramesSentForDiagnostics}");
            sb.AppendLine($"Phase:              {(ts?.Phase ?? global::MozaPlugin.Telemetry.PipelinePhase.Idle)}");
            var rec = ts?.Recovery;
            if (rec != null)
            {
                sb.AppendLine($"  IsParked:         {rec.IsParked}");
                sb.AppendLine($"  RecoveryInFlight: {rec.IsRecoveryInFlight}");
                if (rec.IsParked && !string.IsNullOrEmpty(rec.ParkReason))
                    sb.AppendLine($"  ParkReason:       {rec.ParkReason}");
            }
            var conn = plugin.Connection;
            if (conn != null)
            {
                var lf = conn.LastFailure;
                sb.AppendLine($"LastFailure:        {lf.Kind}"
                    + (lf.Kind == ConnectionFailureKind.None ? "" : $" — {lf.Message}"));
                sb.AppendLine($"ConsecOpenFails:    {conn.ConsecutiveOpenFailures}");
            }
            var budget = plugin.SerialBudgetForDiagnostics;
            var errs = plugin.SerialWireErrorsForDiagnostics;
            int budgetTargetBytes = WriteBudget.TargetBytesPerWindow;
            sb.AppendLine(
                $"Bandwidth:          out={budget.BytesLastSec,5} B/s ({budget.PercentBudget,3}% of {budgetTargetBytes}B target, peak={budget.PeakBurstBytes})");
            sb.AppendLine(
                $"WireErrors:         drops={errs.FramesDropped} cksumFail={errs.ChecksumFailures} resync={errs.FrameStartScanResyncs}");
            // Resync skip-size distribution. Helps tell single-byte stray
            // padding (USB / driver idle bytes — harmless) from multi-byte
            // gaps (wire corruption — worth investigating). drops=0
            // cksumFail=0 with a 1B-dominated histogram means the wire is
            // healthy and the resync count is just inter-frame noise.
            if (errs.FrameStartScanResyncs > 0 && errs.ResyncSkipHistogram != null)
            {
                var h = errs.ResyncSkipHistogram;
                string[] labels = { "1B", "2B", "3-4B", "5-8B", "9-16B", "17-32B", "33-64B", ">64B" };
                var hsb = new StringBuilder();
                bool first = true;
                for (int i = 0; i < h.Length; i++)
                {
                    if (h[i] == 0) continue;
                    if (!first) hsb.Append("  ");
                    hsb.Append(labels[i]).Append('=').Append(h[i]);
                    first = false;
                }
                sb.AppendLine($"  ResyncSkipDist:   {hsb}");
                if (errs.RecentResyncSamples != null && errs.RecentResyncSamples.Length > 0)
                {
                    sb.AppendLine($"  RecentResyncs:    (last {errs.RecentResyncSamples.Length}, newest first)");
                    for (int i = errs.RecentResyncSamples.Length - 1; i >= 0; i--)
                        sb.AppendLine($"    {errs.RecentResyncSamples[i]}");
                }
            }
            sb.AppendLine($"DisplayDetected:    {(ts?.DisplayDetected ?? plugin.IsDisplayDetected)}");
            sb.AppendLine($"DisplayModelName:   {Blank(ts?.DisplayModelName ?? plugin.DisplayModelName)}");
            sb.AppendLine($"WheelEra:           {plugin.ActiveTelemetryWheelEra}");
            if (ts != null)
            {
                sb.AppendLine($"WheelReportedSlot:  {ts.WheelReportedSlot}");
                sb.AppendLine($"LastEmittedKind4:   {ts.LastEmittedKind4Slot}");
                sb.AppendLine($"DisplayEngaged:     {ts.Watchdog?.DisplayEngagementText() ?? "(n/a)"}");
                var p = ts.Policy;
                sb.AppendLine($"PolicyEra:          {p.Era}{(p.IsAuto ? " (auto)" : "")}");
                sb.AppendLine($"ResolvedTierDefSes: 0x{ts.ResolveTierDefSession():X2}");
                sb.AppendLine($"Encoding:           {p.Encoding}");
                sb.AppendLine($"BlindRetransmit:    {p.BlindRetransmitTierDef}");
                sb.AppendLine($"UploadWireFormat:   {p.UploadWireFormat}");
                sb.AppendLine($"FlagByte:           0x{ts.FlagByte:X2}");
                sb.AppendLine($"UploadDashboard:    {ts.UploadDashboard}");
                sb.Append    ($"Profile:            {ts.Profile?.Name ?? "(none)"}");
            }

            var counts = plugin.SessionCountsForDiagnostics;
            if (counts != null && counts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("Session traffic (in/out chunks):");
                var keys = new List<byte>(counts.Keys);
                keys.Sort();
                foreach (var k in keys)
                {
                    var v = counts[k];
                    sb.AppendLine($"  0x{k:X2}:  in={v.In,-5} out={v.Out}");
                }
            }
            return sb.ToString();
        }

        public static string BuildWheelCatalog(MozaPlugin plugin)
        {
            var sb = new StringBuilder();
            var pd = plugin.CatalogParserDiagnostics;
            string activity = pd.LastActivityMsAgo < 0
                ? "never"
                : $"{pd.LastActivityMsAgo} ms ago";
            sb.AppendLine(
                $"Parser: buf={pd.BufferBytes}B (last parsed {pd.LastParsedBufferBytes}B) " +
                $"crcRejects={pd.CrcRejects} lastActivity={activity}");

            var catalog = plugin.WheelChannelCatalogForDiagnostics;
            if (catalog != null && catalog.Count > 0)
            {
                sb.AppendLine($"{catalog.Count} channels advertised by wheel:");
                for (int i = 0; i < catalog.Count; i++)
                {
                    string url = catalog[i] ?? "";
                    sb.AppendLine($"  [{i + 1,2}]  {url}");
                }
                return sb.ToString().TrimEnd();
            }

            // Fallback: derive from active subscription. The subscription was
            // built with the wheel's catalog, so URLs reflect what we sent.
            // Diag.Channels uses sequential idx (1..N across tiers/buckets) —
            // a URL appears multiple times when channels duplicate across
            // page-broadcast buckets. Dedup by URL preserving first-seen.
            var sub = plugin.SubscriptionForDiagnostics;
            if (sub != null && sub.Channels != null && sub.Channels.Count > 0)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ordered = new List<string>();
                foreach (var ch in sub.Channels)
                {
                    if (string.IsNullOrEmpty(ch.Url)) continue;
                    if (seen.Add(ch.Url)) ordered.Add(ch.Url);
                }
                if (ordered.Count > 0)
                {
                    sb.AppendLine($"{ordered.Count} channels (from last subscription — catalog parser empty):");
                    for (int i = 0; i < ordered.Count; i++)
                        sb.AppendLine($"  [{i + 1,2}]  {ordered[i]}");
                    return sb.ToString().TrimEnd();
                }
            }

            return "(no channel catalog received from wheel yet)";
        }

        public static string BuildSubscription(MozaPlugin plugin)
        {
            var sub = plugin.SubscriptionForDiagnostics;
            if (sub == null) return "(no subscription sent yet)";
            var sb = new StringBuilder();
            sb.AppendLine($"Sent on session {sub.SessionByte} format={sub.Format}  at {sub.CapturedAt:HH:mm:ss}");
            if (sub.PreambleBytes.Length > 0)
                sb.AppendLine($"Preamble ({sub.PreambleBytes.Length}B): {BitConverter.ToString(sub.PreambleBytes).Replace('-', ' ')}");
            sb.AppendLine($"Body ({sub.BodyBytes.Length}B): {BitConverter.ToString(sub.BodyBytes).Replace('-', ' ')}");
            if (sub.Channels.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Channels ({sub.Channels.Count}):");
                foreach (var ch in sub.Channels)
                    sb.AppendLine($"  idx={ch.Idx,2}  comp=0x{ch.Comp:X2}  width={ch.Width,3}  {ch.Url}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Render the most recent unsolicited firmware-debug frames
        /// (raw wire group 0x0E, subtype 0x05). These are ASCII log lines the
        /// wheel-bus firmware emits during normal operation — parameter
        /// writes, init traces, occasional warnings — captured by
        /// <see cref="FirmwareDebugLog"/> for visibility. Empty by default
        /// because nothing else in the plugin acts on these; when present
        /// they're useful for understanding what the firmware is doing
        /// across init / dashboard switches / setting writes.</summary>
        public static string BuildFirmwareDebug(MozaPlugin plugin)
        {
            var log = plugin.FirmwareDebugLogForDiagnostics;
            var entries = log.Snapshot();
            if (entries.Length == 0)
                return $"(no firmware-debug frames captured; total received={log.TotalReceived})";

            var sb = new StringBuilder();
            sb.AppendLine($"Recent frames: {entries.Length} shown / {log.TotalReceived} total received");
            // Render newest first so the most recent activity is at the top
            // of the section (and the oldest, least relevant lines slide off
            // the visible area first on long scrolls). Limit to last 64 so a
            // burst doesn't dominate the diagnostics view.
            int limit = Math.Min(entries.Length, 64);
            for (int i = entries.Length - 1; i >= entries.Length - limit; i--)
            {
                var e = entries[i];
                // Local-time stamp keeps the format consistent with the rest
                // of the diagnostics tab (manifest already shows UTC).
                string ts = e.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
                // Empty lines are continuation fragments (the firmware
                // sometimes splits a single log line across two 0x0E
                // frames); skip rendering them to keep the section readable
                // — they're still in the bundle's moza-log.txt for full
                // forensic context.
                if (e.Text.Length == 0) continue;
                sb.AppendLine($"  {ts} [{e.SourceName,-7}] {e.Text}");
            }
            return sb.ToString().TrimEnd();
        }

        public static string BuildSubscriptionResponse(MozaPlugin plugin)
        {
            var chunks = plugin.SubscriptionResponseForDiagnostics;
            if (chunks == null || chunks.Count == 0)
                return "(no inbound chunks captured on session 0x02 in 5s window after subscription)";
            var sb = new StringBuilder();
            sb.AppendLine($"{chunks.Count} chunks captured on session 0x02 after most-recent subscription:");
            int total = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                total += c.Length;
                int show = Math.Min(c.Length, 80);
                string hex = BitConverter.ToString(c, 0, show).Replace('-', ' ');
                string ellip = c.Length > show ? " …" : "";
                sb.AppendLine($"  [{i,2}] {c.Length,3}B: {hex}{ellip}");
            }
            sb.AppendLine();
            sb.AppendLine($"Concat ({total}B): {BuildConcatHex(chunks, 200)}");
            return sb.ToString().TrimEnd();
        }

        public static string BuildConcatHex(IReadOnlyList<byte[]> chunks, int max)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (var c in chunks)
            {
                foreach (var b in c)
                {
                    if (n++ >= max) { sb.Append(" …"); return sb.ToString(); }
                    sb.Append(b.ToString("X2"));
                    sb.Append(' ');
                }
            }
            return sb.ToString().TrimEnd();
        }
    }
}
