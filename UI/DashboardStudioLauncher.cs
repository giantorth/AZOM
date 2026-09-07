using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;
using MozaPlugin.Sdk;
using MozaPlugin.Telemetry.Dashboard;

namespace MozaPlugin.UI
{
    /// <summary>
    /// Discovers and launches MOZA's standalone dashboard editor,
    /// <c>&lt;PitHouseRoot&gt;\bin\MOZA Dashboard Studio.exe</c>.
    ///
    /// <para>The launch forms were captured from PitHouse's own Edit action
    /// (2026-09-05, Studio 1.0.6.14):</para>
    /// <code>
    /// "…\bin\MOZA Dashboard Studio.exe" "C:/…/_dashes/dashes/ETS2-ATS/ETS2-ATS.mzdash"
    /// "…\bin\MOZA Dashboard Studio.exe" "C:/…/_dashes/&lt;wheel-uid&gt;/radarrr/radarrr.mzdash"
    /// </code>
    /// The second is load-bearing: <c>radarrr</c> lives only in the per-wheel
    /// synced folder, NOT under Studio's configured <c>projectRoot</c> — so the
    /// positional path is arbitrary and Studio opens any mzdash anywhere on
    /// disk. Nothing here has to reconcile folders.
    ///
    /// <para>Creating a new dashboard uses <c>--create-by-idealDeviceInfos &lt;json&gt;</c>.
    /// <c>--update-preview-image</c> also exists but is a headless batch mode
    /// with no UI — never pass it.</para>
    ///
    /// <para>This class only ever READS MOZA-owned files (settings.ini, the
    /// registry). It never writes them.</para>
    /// </summary>
    internal static class DashboardStudioLauncher
    {
        public const string StudioExeFileName = "MOZA Dashboard Studio.exe";

        private const string MozaRegSubKey = @"Software\MOZA\PitHouse";
        private const string MozaRegValueName = "path";

        private static string LocalAppData =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        /// <summary>PitHouse's Qt settings file. Read-only for our purposes.</summary>
        public static string PitHouseSettingsIniPath =>
            Path.Combine(LocalAppData, "MOZA Pit House", "settings.ini");

        /// <summary>Where a default PitHouse install keeps Studio's projects.</summary>
        public static string DefaultProjectRoot =>
            Path.Combine(LocalAppData, "MOZA Pit House", "_dashes", "dashes");

        /// <summary>
        /// Where a default PitHouse install keeps the SHARED widget-image pool.
        /// Images sit at <c>&lt;imageRoot&gt;\MD5\&lt;32hex&gt;.png</c> and are
        /// shared by every dashboard — Studio does not copy them into the
        /// project folder.
        /// </summary>
        public static string DefaultImageRoot =>
            Path.Combine(LocalAppData, "MOZA Pit House", "_dashes", "images");

        // Probe result cache. Both the hit and the miss are cached so a missing
        // install doesn't re-stat four paths on every UI interaction; callers
        // pass force:true (the Refresh button) to pick up a mid-session install.
        private static string? s_exe;
        private static bool s_probed;
        private static readonly object s_probeGate = new object();

        // ── Discovery ───────────────────────────────────────────────────

        /// <summary>
        /// Locate <c>MOZA Dashboard Studio.exe</c>, or null when PitHouse isn't
        /// installed. Never returns the plugin's own CoAP stub — see the
        /// registry-hijack note on <see cref="ProbeStudioExe"/>.
        /// </summary>
        public static string? FindStudioExe(bool force = false)
        {
            lock (s_probeGate)
            {
                if (s_probed && !force) return s_exe;
                s_exe = ProbeStudioExe();
                s_probed = true;
                return s_exe;
            }
        }

        /// <summary>
        /// Probe order matters. <see cref="CoapStubManager"/> deliberately
        /// redirects <c>HKCU\Software\MOZA\PitHouse\path</c> to its own
        /// impersonation stub while SDK emulation runs, so the live registry
        /// value is frequently OURS, not the user's PitHouse. The saved
        /// original in <see cref="CoapStubManager.RegistryBackupPath"/>
        /// therefore wins, and any value that resolves inside our stub
        /// directory is rejected outright.
        /// </summary>
        private static string? ProbeStudioExe()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;

            // 1. The user's original PitHouse path, saved before we hijacked it.
            string? hit = TryFromPitHouseExe(ReadRegistryBackupValue());
            if (hit != null) return hit;

            // 2. The live registry value — unless it's our own stub.
            hit = TryFromPitHouseExe(ReadRegistryPathValue());
            if (hit != null) return hit;

            // 3. Well-known install roots.
            foreach (var special in new[] { Environment.SpecialFolder.ProgramFilesX86,
                                            Environment.SpecialFolder.ProgramFiles })
            {
                string root;
                try { root = Environment.GetFolderPath(special); }
                catch { continue; }
                if (string.IsNullOrEmpty(root)) continue;
                string install = Path.Combine(root, "MOZA Pit House");
                hit = TryCandidate(Path.Combine(install, "bin", StudioExeFileName))
                   ?? TryCandidate(Path.Combine(install, StudioExeFileName));
                if (hit != null)
                {
                    MozaLog.Debug($"[AZOM] Dashboard Studio found at {hit}");
                    return hit;
                }
            }

            MozaLog.Debug("[AZOM] Dashboard Studio not found (PitHouse not installed?)");
            return null;
        }

        /// <summary>
        /// Given a path to <c>MOZA Pit House.exe</c>, look for Studio beside it.
        /// The observed layout puts both in the same <c>bin\</c>; the other two
        /// shapes cover a value pointing at the install root instead.
        /// </summary>
        private static string? TryFromPitHouseExe(string? pitHouseExe)
        {
            if (string.IsNullOrEmpty(pitHouseExe)) return null;
            string? dir;
            try { dir = Path.GetDirectoryName(NormalizePath(pitHouseExe!)); }
            catch { return null; }
            if (string.IsNullOrEmpty(dir)) return null;

            string? hit = TryCandidate(Path.Combine(dir!, StudioExeFileName))
                       ?? TryCandidate(Path.Combine(dir!, "bin", StudioExeFileName));
            if (hit == null)
            {
                try
                {
                    var parent = Path.GetDirectoryName(dir!);
                    if (!string.IsNullOrEmpty(parent))
                        hit = TryCandidate(Path.Combine(parent!, "bin", StudioExeFileName));
                }
                catch { /* unnormalizable — treat as no candidate */ }
            }
            if (hit != null) MozaLog.Debug($"[AZOM] Dashboard Studio found at {hit}");
            return hit;
        }

        private static string? TryCandidate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string full = NormalizePath(path!);
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Contents of the CoapStub registry backup — the user's real PitHouse
        /// exe path. An EMPTY file is meaningful: it records "no value existed
        /// before we wrote ours", so it is not a path. Values are stored with
        /// forward slashes, hence the shared normalizer.
        /// </summary>
        private static string? ReadRegistryBackupValue()
        {
            try
            {
                string backup = CoapStubManager.RegistryBackupPath;
                if (!File.Exists(backup)) return null;
                string value = File.ReadAllText(backup, Encoding.UTF8).Trim().Trim('"');
                if (value.Length == 0) return null;
                if (CoapStubManager.IsOwnStubPath(value)) return null;
                return value;
            }
            catch { return null; }
        }

        private static string? ReadRegistryPathValue()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(MozaRegSubKey, writable: false);
                if (key?.GetValue(MozaRegValueName) is not string value) return null;
                value = value.Trim().Trim('"');
                if (value.Length == 0) return null;
                if (CoapStubManager.IsOwnStubPath(value))
                {
                    MozaLog.Debug("[AZOM] Ignoring MOZA registry path: it points at our own CoAP stub.");
                    return null;
                }
                return value;
            }
            catch { return null; }
        }

        // ── Studio's project root (hint only) ───────────────────────────

        /// <summary>
        /// Where Studio saves a NEWLY created dashboard. Used only to tell the
        /// user where to find it — nothing in this feature depends on it, so a
        /// wrong answer degrades to a slightly-off hint, never broken behaviour.
        /// Reads <c>[DashboardStudio] projectRoot</c> from PitHouse's
        /// settings.ini; never writes it.
        /// </summary>
        public static string? ResolveProjectRoot() =>
            ResolveIniDirectory("projectRoot", DefaultProjectRoot);

        /// <summary>
        /// The SHARED widget-image pool, i.e. the directory holding
        /// <c>MD5\&lt;32hex&gt;.png</c>. Studio references images out of this
        /// pool rather than copying them into each project folder, so an upload
        /// that only looks beside the .mzdash finds nothing and the widget
        /// renders blank on the wheel.
        /// </summary>
        public static string? ResolveImageRoot() =>
            ResolveIniDirectory("imageRoot", DefaultImageRoot);

        private static string? ResolveIniDirectory(string key, string fallback)
        {
            string? fromIni = ReadIniValue(PitHouseSettingsIniPath, "DashboardStudio", key);
            if (!string.IsNullOrEmpty(fromIni))
            {
                try
                {
                    string full = NormalizePath(fromIni!);
                    if (Directory.Exists(full)) return full;
                }
                catch { /* fall through to the default */ }
            }
            try { if (Directory.Exists(fallback)) return fallback; }
            catch { /* fall through */ }
            return null;
        }

        /// <summary>
        /// Minimal INI reader. Deliberately splits on the FIRST '=' only — a
        /// Windows path can contain '=' in a folder name, so Split would
        /// truncate it.
        /// </summary>
        private static string? ReadIniValue(string iniPath, string section, string key)
        {
            try
            {
                if (!File.Exists(iniPath)) return null;
                bool inSection = false;
                foreach (var raw in File.ReadAllLines(iniPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        inSection = string.Equals(
                            line.Substring(1, line.Length - 2).Trim(), section,
                            StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inSection) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                        continue;
                    return line.Substring(eq + 1).Trim();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch { }
            return null;
        }

        /// <summary>
        /// PitHouse writes paths with DOUBLED backslashes and mixed separators,
        /// e.g. <c>C:\\Users\\me\\AppData\\Local/MOZA Pit House/_dashes/dashes</c>.
        /// Collapse the doubles FIRST — doing it after the slash substitution
        /// would turn every forward slash into a backslash and then eat
        /// legitimate separators.
        /// </summary>
        private static string NormalizePath(string raw)
        {
            string s = raw.Trim().Trim('"');
            s = s.Replace("\\\\", "\\");
            s = s.Replace('/', Path.DirectorySeparatorChar);
            try { return Path.GetFullPath(s); }
            catch { return s; }
        }

        // ── idealDeviceInfos ────────────────────────────────────────────

        // Shape of one --create-by-idealDeviceInfos element. Mapped to a local
        // DTO rather than attributing WheelDashboardDeviceInfo, which is
        // parse-side wire model and shouldn't carry serializer concerns.
        private sealed class IdealDeviceInfoDto
        {
            [JsonProperty("hardwareVersion")] public string HardwareVersion { get; set; } = "";
            [JsonProperty("productType")] public string ProductType { get; set; } = "";
            [JsonProperty("networkId")] public int NetworkId { get; set; }
            [JsonProperty("deviceId")] public int DeviceId { get; set; }
        }

        /// <summary>
        /// Serialize the connected display's descriptor for
        /// <c>--create-by-idealDeviceInfos</c>. Key order matches the literal
        /// embedded in the exe:
        /// <c>[{"hardwareVersion":"RS21-W08-HW SM-DU-V14","productType":"Display","networkId":1,"deviceId":8}]</c>
        /// </summary>
        public static string BuildIdealDeviceInfosJson(IReadOnlyList<WheelDashboardDeviceInfo> infos)
        {
            var list = new List<IdealDeviceInfoDto>(infos?.Count ?? 0);
            if (infos != null)
            {
                foreach (var i in infos)
                    list.Add(new IdealDeviceInfoDto
                    {
                        HardwareVersion = i.HardwareVersion ?? "",
                        ProductType = i.ProductType ?? "",
                        NetworkId = i.NetworkId,
                        DeviceId = i.DeviceId,
                    });
            }
            return JsonConvert.SerializeObject(list, Formatting.None);
        }

        // ── Launch ──────────────────────────────────────────────────────

        public enum LaunchOutcome { Started, NotFound, Failed }

        public readonly struct LaunchResult
        {
            public LaunchOutcome Outcome { get; }
            public string? ExePath { get; }
            public string? Error { get; }

            public LaunchResult(LaunchOutcome outcome, string? exePath, string? error)
            {
                Outcome = outcome;
                ExePath = exePath;
                Error = error;
            }
        }

        /// <summary>
        /// Open one dashboard for editing. <paramref name="mzdashPath"/> may be
        /// anywhere on disk — Studio does not require it to sit under its
        /// configured projectRoot (verified against PitHouse's own Edit action).
        /// </summary>
        public static LaunchResult LaunchEdit(string mzdashPath)
        {
            string arg;
            try { arg = ToForwardSlashes(Path.GetFullPath(mzdashPath)); }
            catch { arg = ToForwardSlashes(mzdashPath); }
            return Start(QuoteArg(arg), $"edit {arg}");
        }

        /// <summary>
        /// Start Studio's new-dashboard flow. Pass null to open it unseeded —
        /// never substitute the exe's built-in <c>RS21-W08-HW SM-DU-V14</c>
        /// literal, which describes one specific wheel's display and would give
        /// a different wheel the wrong canvas geometry with no way to fix it
        /// afterwards.
        /// </summary>
        public static LaunchResult LaunchCreate(string? idealDeviceInfosJson)
        {
            if (string.IsNullOrEmpty(idealDeviceInfosJson))
                return Start("", "create (unseeded)");
            return Start("--create-by-idealDeviceInfos " + QuoteArg(idealDeviceInfosJson!), "create");
        }

        /// <summary>
        /// Spawn Studio detached. No Process handle is retained, no JobObject,
        /// no Kill on teardown: Studio is the user's editor and must outlive a
        /// plugin reload.
        ///
        /// <para>Deliberately no "already running" short-circuit. Studio holds a
        /// single-instance lock AND a shared-memory command channel — that pair
        /// is how PitHouse hands a second dashboard to a live editor — so we
        /// launch unconditionally, exactly as PitHouse does, and let Studio
        /// route it.</para>
        /// </summary>
        private static LaunchResult Start(string arguments, string what)
        {
            string? exe = FindStudioExe();
            if (exe == null) return new LaunchResult(LaunchOutcome.NotFound, null, null);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                };
                Process.Start(psi)?.Dispose();
                MozaLog.Info($"[AZOM] Dashboard Studio launched: {what}");
                return new LaunchResult(LaunchOutcome.Started, exe, null);
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] Dashboard Studio launch failed ({what}): " +
                             $"{ex.GetType().Name}: {ex.Message}");
                return new LaunchResult(LaunchOutcome.Failed, exe, ex.Message);
            }
        }

        private static string ToForwardSlashes(string path) => path.Replace('\\', '/');

        /// <summary>
        /// Quote one argument per the CommandLineToArgvW rules Qt's
        /// QCoreApplication::arguments() ultimately parses. net48 has no
        /// ProcessStartInfo.ArgumentList, so this is hand-rolled — and it is
        /// load-bearing for the create-JSON, which carries a quote on every key.
        /// </summary>
        internal static string QuoteArg(string arg)
        {
            var sb = new StringBuilder(arg.Length + 8);
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    // Double the pending run, then escape the quote itself.
                    sb.Append('\\', backslashes * 2 + 1).Append('"');
                    backslashes = 0;
                    continue;
                }
                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            // A trailing run precedes the closing quote, so it must be doubled.
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }
    }
}
