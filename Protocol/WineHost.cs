using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Runtime platform latch: are we running under Wine/Proton, and if so where
    /// is the Linux filesystem mounted inside the prefix?
    ///
    /// <para>Detection is the standard Wine idiom — <c>ntdll!wine_get_version</c>
    /// only exists in Wine's ntdll. (This file re-adds the ~15-line idiom deleted
    /// in 59ebd99 with the out-of-process probe helper; see
    /// <c>docs/linux-cold-start-fix.md</c> Layer 2.)</para>
    ///
    /// <para>Callers must gate Linux-specific paths on <see cref="IsWine"/> AND a
    /// non-null <see cref="UnixRoot"/>, never on <see cref="IsWine"/> alone —
    /// Wine on macOS has no <c>/sys</c> and has to fall through to the COM-name
    /// path.</para>
    /// </summary>
    internal static class WineHost
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = false)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = false)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        private delegate IntPtr WineGetVersionDelegate();
        private delegate void WineGetHostVersionDelegate(out IntPtr sysname, out IntPtr release);

        // kernel32!wine_get_unix_file_name — DOS path to unix path. Returns a
        // heap pointer Wine owns; we only read it.
        private delegate IntPtr WineGetUnixFileNameDelegate([MarshalAs(UnmanagedType.LPWStr)] string dos);

        private static readonly object s_gate = new object();
        private static bool s_probed;
        private static bool s_isWine;
        private static string s_wineVersion = string.Empty;
        private static string s_hostSystem = string.Empty;
        private static string s_hostRelease = string.Empty;
        private static string? s_unixRoot;
        private static string? s_prefixRoot;

        /// <summary>True when the process is running on Wine (incl. Proton).</summary>
        public static bool IsWine { get { Probe(); return s_isWine; } }

        /// <summary>Wine version string (e.g. "11.15"), empty on native Windows.</summary>
        public static string WineVersion { get { Probe(); return s_wineVersion; } }

        /// <summary>Host uname sysname (e.g. "Linux"), empty when unavailable.</summary>
        public static string HostSystem { get { Probe(); return s_hostSystem; } }

        /// <summary>Host uname release (e.g. "7.1.8-1-cachyos"), empty when unavailable.</summary>
        public static string HostRelease { get { Probe(); return s_hostRelease; } }

        /// <summary>
        /// True when Proton (or umu) launched us. Cosmetic — bug-report context
        /// only; nothing branches on it.
        /// </summary>
        public static bool IsProton =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROTONPATH"));

        /// <summary>
        /// Drive root that maps the Linux filesystem (Wine's default is
        /// <c>Z:\</c>), e.g. <c>"Z:"</c>. Null on native Windows, on a Wine host
        /// with no <c>/sys</c>, or when the user removed the unix drive.
        /// </summary>
        public static string? UnixRoot { get { Probe(); return s_unixRoot; } }

        /// <summary>
        /// The Wine prefix directory as a unix path (the parent of
        /// <c>drive_c</c>), e.g. <c>/home/u/.wine</c>. Null when unavailable.
        /// </summary>
        public static string? PrefixRoot { get { Probe(); return s_prefixRoot; } }

        /// <summary>
        /// Compose a DOS path for a unix path, e.g. <c>/sys/class/tty</c> →
        /// <c>Z:\sys\class\tty</c>. Null when <see cref="UnixRoot"/> is null.
        /// </summary>
        public static string? UnixPath(string unixPath)
        {
            string? root = UnixRoot;
            if (root == null || string.IsNullOrEmpty(unixPath)) return null;
            char sep = System.IO.Path.DirectorySeparatorChar;
            string rel = unixPath.Replace('/', sep);
            if (rel.Length == 0 || rel[0] != sep) rel = sep + rel;
            return root + rel;
        }

        /// <summary>One-line platform summary for the log and the diagnostics dump.</summary>
        public static string Describe()
        {
            Probe();
            if (!s_isWine) return "native Windows";
            var sb = new System.Text.StringBuilder();
            sb.Append(s_isWine && IsProton ? "Proton/Wine" : "Wine");
            if (s_wineVersion.Length > 0) { sb.Append(' '); sb.Append(s_wineVersion); }
            if (s_hostSystem.Length > 0)
            {
                sb.Append(" on ");
                sb.Append(s_hostSystem);
                if (s_hostRelease.Length > 0) { sb.Append(' '); sb.Append(s_hostRelease); }
            }
            sb.Append(", unix root ");
            sb.Append(s_unixRoot ?? "(none)");
            return sb.ToString();
        }

        private static void Probe()
        {
            if (s_probed) return;
            lock (s_gate)
            {
                if (s_probed) return;
                try { ProbeInner(); }
                catch (Exception ex)
                {
                    MozaLog.Debug($"[AZOM] Wine detection failed: {ex.GetType().Name}: {ex.Message}");
                }
                s_probed = true;
            }
        }

        private static void ProbeInner()
        {
            IntPtr ntdll = GetModuleHandle("ntdll.dll");
            if (ntdll == IntPtr.Zero) return;

            IntPtr pVersion = GetProcAddress(ntdll, "wine_get_version");
            if (pVersion == IntPtr.Zero) return;
            s_isWine = true;

            try
            {
                var getVersion = (WineGetVersionDelegate)Marshal.GetDelegateForFunctionPointer(
                    pVersion, typeof(WineGetVersionDelegate));
                s_wineVersion = Marshal.PtrToStringAnsi(getVersion()) ?? string.Empty;
            }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] wine_get_version: {ex.Message}"); }

            try
            {
                IntPtr pHost = GetProcAddress(ntdll, "wine_get_host_version");
                if (pHost != IntPtr.Zero)
                {
                    var getHost = (WineGetHostVersionDelegate)Marshal.GetDelegateForFunctionPointer(
                        pHost, typeof(WineGetHostVersionDelegate));
                    getHost(out IntPtr sysname, out IntPtr release);
                    s_hostSystem = Marshal.PtrToStringAnsi(sysname) ?? string.Empty;
                    s_hostRelease = Marshal.PtrToStringAnsi(release) ?? string.Empty;
                }
            }
            catch (Exception ex) { MozaLog.Debug($"[AZOM] wine_get_host_version: {ex.Message}"); }

            s_unixRoot = FindUnixRoot();
            s_prefixRoot = FindPrefixRoot();
        }

        // The unix drive is Z: by default in every Wine/Proton prefix, but the
        // user can remove or remap it. Require BOTH /proc and /sys/class/tty so
        // a random mount that happens to have a "sys" folder can't win.
        private static string? FindUnixRoot()
        {
            if (TestRoot("Z:")) return "Z:";
            for (char c = 'A'; c <= 'Z'; c++)
            {
                if (c == 'Z') continue;
                string root = c + ":";
                if (TestRoot(root)) return root;
            }
            return null;
        }

        private static bool TestRoot(string root)
        {
            try
            {
                return Directory.Exists(root + @"\sys\class\tty")
                    && Directory.Exists(root + @"\proc");
            }
            catch { return false; }
        }

        // wine_get_unix_file_name("C:\") gives <prefix>/drive_c; the prefix is
        // its parent. Only the dosdevices COM-label resolver needs this.
        private static string? FindPrefixRoot()
        {
            try
            {
                IntPtr kernel32 = GetModuleHandle("kernel32.dll");
                if (kernel32 == IntPtr.Zero) return null;
                IntPtr p = GetProcAddress(kernel32, "wine_get_unix_file_name");
                if (p == IntPtr.Zero) return null;

                var toUnix = (WineGetUnixFileNameDelegate)Marshal.GetDelegateForFunctionPointer(
                    p, typeof(WineGetUnixFileNameDelegate));
                IntPtr res = toUnix(@"C:\");
                if (res == IntPtr.Zero) return null;
                string driveC = Marshal.PtrToStringAnsi(res) ?? string.Empty;
                if (driveC.Length == 0) return null;

                driveC = driveC.TrimEnd('/');
                int slash = driveC.LastIndexOf('/');
                if (slash <= 0) return null;
                return driveC.Substring(0, slash);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] wine_get_unix_file_name: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }
}
