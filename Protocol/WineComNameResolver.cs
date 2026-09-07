using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Runtime.InteropServices;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Resolves the Windows COM name Wine has assigned to a Linux tty.
    ///
    /// <para>Used for display, for SimHub's Arduino-scan veto, AND — since the
    /// transport comparison below — to choose how the port is opened. When the
    /// COM name resolves, <c>MozaSerialConnection.TryOpen</c> opens it through
    /// <see cref="SerialPortMozaPort"/> rather than the raw device path: sysfs
    /// still supplies the identity (which tty is the MOZA, by VID/PID), so no
    /// blind COM probing is involved, but the read path gets .NET SerialPort's
    /// 64 KB receive buffer instead of a raw handle whose reads were gated on a
    /// Wine <c>ClearCommError</c> ioctl. Measured on one rig, same wheel, 22
    /// minutes apart: v1.5.7 via COM reported resync=0; the device path reported
    /// resync 53-465 with clustered chunk loss. An unresolved name simply falls
    /// back to the device path, so this can never leave the port unopenable.</para>
    ///
    /// <para>Wine's mapping lives in <c>&lt;prefix&gt;/dosdevices/comNN</c>, a unix
    /// symlink to the tty node. The target string is not readable from inside the
    /// prefix (reparse tags need Wine's own <c>user.WINEREPARSE</c> xattr, which
    /// wineboot-created symlinks do not have; <c>QueryDosDevice</c> answers with
    /// <c>\Device\SerialN</c>, and <c>GetFinalPathNameByHandle</c> would require
    /// opening the tty). What DOES work read-only is stat: Wine follows the
    /// symlink, so <c>comNN</c> reports the timestamps and size of the device node
    /// it points at. Matching that tuple against a direct stat of
    /// <c>/dev/&lt;tty&gt;</c> identifies the pair without opening anything —
    /// device-node creation times differ at sub-second resolution.</para>
    ///
    /// <para>An ambiguous or failed match simply yields no label. That costs a
    /// display string and one scan-veto entry; it can never mis-route a
    /// connection.</para>
    /// </summary>
    internal static class WineComNameResolver
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WIN32_FILE_ATTRIBUTE_DATA
        {
            public uint FileAttributes;
            public FILETIME CreationTime;
            public FILETIME LastAccessTime;
            public FILETIME LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileAttributesExW(
            string lpFileName, int fInfoLevelId, out WIN32_FILE_ATTRIBUTE_DATA lpFileInformation);

        private const int GetFileExInfoStandard = 0;

        // Long enough that the 5 s reconnect tick and the 500 ms UI refresh share
        // one sweep; short enough to follow a replug.
        private static readonly long CacheTtlTicks = Stopwatch.Frequency * 5L;

        private static readonly object s_gate = new object();
        private static long s_timestamp;
        private static Dictionary<string, string> s_ttyToCom =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// COM name Wine assigned to <paramref name="ttyName"/> ("ttyACM2" →
        /// "COM35"), or null when unresolvable. Never throws.
        /// </summary>
        public static string? ResolveComName(string ttyName)
        {
            if (string.IsNullOrEmpty(ttyName)) return null;
            var map = GetMap();
            return map.TryGetValue(ttyName, out var com) ? com : null;
        }

        /// <summary>True when <paramref name="comName"/> is the label of a known MOZA tty.</summary>
        public static bool IsMozaComName(string comName)
        {
            if (string.IsNullOrEmpty(comName)) return false;
            foreach (var kv in GetMap())
                if (string.Equals(kv.Value, comName, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Force the next lookup to re-stat.</summary>
        public static void Invalidate()
        {
            lock (s_gate) s_timestamp = 0;
        }

        // Serialises Build(): SimHub's COM scanner calls in from parallel workers,
        // and each Build() walks sysfs and forks a shell per MOZA tty. Taken only
        // around the build; s_gate stays the short leaf lock for the map itself.
        private static readonly object s_buildGate = new object();

        private static Dictionary<string, string> GetMap()
        {
            if (TryGetFresh(out var fresh)) return fresh;

            lock (s_buildGate)
            {
                // Another caller may have rebuilt while we waited.
                if (TryGetFresh(out fresh)) return fresh;

                var built = Build();

                lock (s_gate)
                {
                    s_ttyToCom = built;
                    s_timestamp = Stopwatch.GetTimestamp();
                    return s_ttyToCom;
                }
            }
        }

        private static bool TryGetFresh(out Dictionary<string, string> map)
        {
            lock (s_gate)
            {
                long now = Stopwatch.GetTimestamp();
                map = s_ttyToCom;
                return s_timestamp != 0 && (now - s_timestamp) < CacheTtlTicks;
            }
        }

        /// <summary>
        /// Ask the host which <c>comNN</c> symlink points at one tty, and get the
        /// answer back in the process exit status.
        ///
        /// <para>Why not stat: <c>GetFileAttributesExW</c> does not answer for a
        /// character-device node under Wine (confirmed on a live rig), so the
        /// timestamp-identity approach can never match — do not reinstate it as the
        /// primary path. Why not a temp file: <see cref="WineNativeExec"/> reports
        /// only an exit status, and the obvious side channel is a shared file, but
        /// under Proton/pressure-vessel the spawn runs in a private mount namespace
        /// whose <c>/tmp</c> is NOT the one the unix drive exposes — a file written
        /// there is simply invisible. The exit status needs no shared filesystem at
        /// all: Wine numbers COM ports from 1, so N means comN and 0 means no
        /// match. Statuses are 8-bit, hence the 255 ceiling.</para>
        ///
        /// <para>The comparison is <c>[ "$f" -ef /dev/&lt;tty&gt; ]</c> — a POSIX
        /// <c>test</c> builtin that compares device+inode and follows symlinks, so
        /// the probe depends on no external binary. An earlier form shelled out to
        /// <c>readlink</c> and came back exit 0 (no match) inside the spawn while
        /// the identical command matched com34 in a normal shell.</para>
        /// </summary>
        private static string? ResolveComForTty(string prefix, string tty)
        {
            if (!WineNativeExec.Available) return null;
            if (string.IsNullOrEmpty(tty) || tty.IndexOfAny(ShellUnsafe) >= 0) return null;

            // for f in <prefix>/dosdevices/com*; do
            //   [ "$f" -ef "/dev/<tty>" ] && { n=${f##*/com}; exit "$n"; }
            // done; exit 0
            string script =
                "for f in \"" + prefix + "\"/dosdevices/com*; do "
                + "[ \"$f\" -ef \"/dev/" + tty + "\" ] && "
                + "{ n=${f##*/com}; exit \"$n\"; }; "
                + "done; exit 0";

            var r = WineNativeExec.Run(new[] { "sh", "-c", script }, timeoutMs: 3000);
            if (r.Outcome != NativeSpawnOutcome.Completed)
            {
                MozaLog.DebugIfChanged("wine-com-spawn",
                    $"[AZOM] COM label: com-link probe for {tty} did not complete "
                    + $"(outcome={r.Outcome} status=0x{r.Status:X})");
                return null;
            }
            // Exit status is the low byte of what sh returned.
            int n = r.Status & 0xFF;
            if (n <= 0 || n > 255)
            {
                MozaLog.DebugIfChanged("wine-com-none-" + tty,
                    $"[AZOM] COM label: no dosdevices/com* link points at /dev/{tty} "
                    + $"(searched {prefix}/dosdevices, sh status=0x{r.Status:X})");
                return null;
            }
            return "COM" + n.ToString(CultureInfo.InvariantCulture);
        }

        // Refuse to interpolate anything that could break out of the double-quoted
        // shell word. tty names are kernel-generated (ttyACM0, ttyUSB1) so this
        // never rejects a real device.
        private static readonly char[] ShellUnsafe =
            { '"', '\'', '\\', '$', '`', ';', '&', '|', '\n', '\r', '<', '>', '*', '?', ' ' };

        private static Dictionary<string, string> Build()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? prefix = WineHost.PrefixRoot;
            if (!WineHost.IsWine || prefix == null || WineHost.UnixRoot == null)
            {
                MozaLog.DebugIfChanged("wine-com-why",
                    $"[AZOM] COM label: not resolvable — isWine={WineHost.IsWine} "
                    + $"prefix={prefix ?? "(null)"} unixRoot={WineHost.UnixRoot ?? "(null)"}");
                return map;
            }

            var nodes = LinuxUsbEnumerator.Enumerate();
            if (nodes.Count == 0)
            {
                MozaLog.DebugIfChanged("wine-com-why",
                    "[AZOM] COM label: sysfs enumerated 0 MOZA ttys");
                return map;
            }

            // Exact route first, and only for the ttys sysfs identified as MOZA —
            // so a resolved label can never point at another vendor's device.
            for (int i = 0; i < nodes.Count; i++)
            {
                string t = nodes[i].TtyName;
                string? com = ResolveComForTty(prefix, t);
                if (com != null) map[t] = com;
            }
            if (map.Count > 0)
            {
                MozaLog.DebugIfChanged("wine-com-map",
                    $"[AZOM] COM labels: {DescribeMap(map)}");
                return map;
            }

            // Identity of each MOZA tty node, keyed by its stat tuple.
            var byIdentity = new Dictionary<string, string>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < nodes.Count; i++)
            {
                string tty = nodes[i].TtyName;
                string? devPath = WineHost.UnixPath("/dev/" + tty);
                if (devPath == null) continue;
                if (!TryStat(devPath, out string id))
                {
                    MozaLog.DebugIfChanged("wine-com-stat-" + tty,
                        $"[AZOM] COM label: stat failed on {devPath} (tty {tty}) — "
                        + "GetFileAttributesEx does not answer for this node");
                    continue;
                }
                if (byIdentity.ContainsKey(id)) ambiguous.Add(id);
                else byIdentity[id] = tty;
            }
            if (byIdentity.Count == 0)
            {
                MozaLog.DebugIfChanged("wine-com-why",
                    $"[AZOM] COM label: not resolvable — none of {nodes.Count} MOZA tty "
                    + "node(s) could be stat'd, so there is nothing to match COM names against");
                return map;
            }

            // Enumerate <prefix>/dosdevices directly rather than trusting
            // SerialPort.GetPortNames(). GetPortNames reads
            // HKLM\HARDWARE\DEVICEMAP\SERIALCOMM, and a Wine prefix frequently
            // has no such key AT ALL — verified absent from a live rig's
            // system.reg, which is why that rig reported "registry empty" and had
            // to brute-force probe COM names. With an empty array the match loop
            // below never runs and no COM name can ever resolve, however good the
            // identity comparison is. dosdevices IS Wine's authoritative COM->tty
            // mapping (com34 -> /dev/ttyACM1 on that same rig), so read it.
            var comNames = new List<string>();
            string? dosDir = WineHost.UnixPath(prefix + "/dosdevices");
            if (dosDir != null)
            {
                try
                {
                    foreach (string entry in System.IO.Directory.GetFileSystemEntries(dosDir))
                    {
                        string name = System.IO.Path.GetFileName(entry);
                        if (LooksLikeComName(name)) comNames.Add(name.ToUpperInvariant());
                    }
                }
                catch (Exception ex)
                {
                    MozaLog.DebugIfChanged("wine-com-dosdevices",
                        $"[AZOM] COM label: cannot enumerate {dosDir}: "
                        + $"{ex.GetType().Name}: {ex.Message}");
                }
            }
            if (comNames.Count == 0)
            {
                // Native Windows, or a prefix we could not read — fall back.
                try { comNames.AddRange(SerialPort.GetPortNames()); }
                catch (Exception ex)
                {
                    MozaLog.DebugIfChanged("wine-com-names",
                        $"[AZOM] COM label: GetPortNames failed: {ex.GetType().Name}: {ex.Message}");
                    return map;
                }
            }

            var claimedBy = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var com in comNames)
            {
                string? linkPath = WineHost.UnixPath(prefix + "/dosdevices/" + com.ToLowerInvariant());
                if (linkPath == null) continue;
                if (!TryStat(linkPath, out string id)) continue;
                if (!byIdentity.TryGetValue(id, out string tty)) continue;
                if (ambiguous.Contains(id)) continue;
                // Two COM entries resolving to one node means Wine has duplicate
                // symlinks; neither label is trustworthy, so drop both.
                if (claimedBy.ContainsKey(id))
                {
                    map.Remove(tty);
                    ambiguous.Add(id);
                    continue;
                }
                claimedBy[id] = com;
                map[tty] = com;
            }

            MozaLog.DebugIfChanged("wine-com-map",
                $"[AZOM] COM labels: {DescribeMap(map)} "
                + $"(from {comNames.Count} com entr(y/ies), {byIdentity.Count} MOZA node(s), "
                + $"{ambiguous.Count} ambiguous)");
            return map;
        }

        // CreationTime ONLY. Wine maps it from st_ctime, which udev sets when it
        // makes the node and which device I/O never touches — so it is identical
        // whether stat'd directly or through the dosdevices symlink, and stable
        // for the life of the node. FILETIME is 100 ns and udev creates each node
        // at a measurably different instant, so it still discriminates (measured
        // on a live rig: ttyACM0 and ttyACM1 ctimes differ).
        //
        // LastWriteTime and LastAccessTime are deliberately EXCLUDED: both track
        // activity on an open tty (measured: mtime and atime on ttyACM1 both sat
        // ~91000 s after its ctime while the port was live). Including either
        // made the two stats disagree for the SAME node whenever I/O happened
        // between them, which is why this resolver reported "(unresolved)" on a
        // rig whose dosdevices/com34 -> /dev/ttyACM1 link was present all along.
        // Ambiguous ctimes are already handled by dropping both candidates.
        private static bool TryStat(string path, out string identity)
        {
            identity = string.Empty;
            try
            {
                if (!GetFileAttributesExW(path, GetFileExInfoStandard, out var data))
                    return false;
                identity = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:X8}{1:X8}",
                    data.CreationTime.High, data.CreationTime.Low);
                return true;
            }
            catch { return false; }
        }

        /// <summary>"comNN" with at least one digit and nothing else.</summary>
        private static bool LooksLikeComName(string name)
        {
            if (name == null || name.Length < 4) return false;
            if (!name.StartsWith("com", StringComparison.OrdinalIgnoreCase)) return false;
            for (int i = 3; i < name.Length; i++)
                if (name[i] < '0' || name[i] > '9') return false;
            return true;
        }

        private static string DescribeMap(Dictionary<string, string> map)
        {
            if (map.Count == 0) return "(none resolved)";
            var sb = new System.Text.StringBuilder();
            foreach (var kv in map)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append("=").Append(kv.Value);
            }
            return sb.ToString();
        }
    }
}
