using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Runtime.InteropServices;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Resolves the Windows COM name Wine has assigned to a Linux tty, for
    /// display and for SimHub's Arduino-scan veto. **Label only** — nothing is
    /// ever opened by the result; the connection opens
    /// <see cref="MozaPortDiscovery.PortInfo.DevicePath"/> directly.
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

        private static Dictionary<string, string> GetMap()
        {
            lock (s_gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (s_timestamp != 0 && (now - s_timestamp) < CacheTtlTicks)
                    return s_ttyToCom;
            }

            var built = Build();

            lock (s_gate)
            {
                s_ttyToCom = built;
                s_timestamp = Stopwatch.GetTimestamp();
                return s_ttyToCom;
            }
        }

        private static Dictionary<string, string> Build()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? prefix = WineHost.PrefixRoot;
            if (!WineHost.IsWine || prefix == null || WineHost.UnixRoot == null) return map;

            var nodes = LinuxUsbEnumerator.Enumerate();
            if (nodes.Count == 0) return map;

            // Identity of each MOZA tty node, keyed by its stat tuple.
            var byIdentity = new Dictionary<string, string>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < nodes.Count; i++)
            {
                string tty = nodes[i].TtyName;
                string? devPath = WineHost.UnixPath("/dev/" + tty);
                if (devPath == null) continue;
                if (!TryStat(devPath, out string id)) continue;
                if (byIdentity.ContainsKey(id)) ambiguous.Add(id);
                else byIdentity[id] = tty;
            }
            if (byIdentity.Count == 0) return map;

            string[] comNames;
            try { comNames = SerialPort.GetPortNames(); }
            catch (Exception ex)
            {
                MozaLog.DebugIfChanged("wine-com-names",
                    $"[AZOM] COM label: GetPortNames failed: {ex.GetType().Name}: {ex.Message}");
                return map;
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
                $"[AZOM] COM labels: {DescribeMap(map)}");
            return map;
        }

        // Device nodes are size 0 with identical attributes, so the three
        // timestamps carry all the discriminating power. FILETIME is 100 ns, and
        // udev creates each node at a measurably different instant.
        private static bool TryStat(string path, out string identity)
        {
            identity = string.Empty;
            try
            {
                if (!GetFileAttributesExW(path, GetFileExInfoStandard, out var data))
                    return false;
                identity = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:X8}{1:X8}/{2:X8}{3:X8}/{4:X8}{5:X8}/{6:X8}{7:X8}",
                    data.CreationTime.High, data.CreationTime.Low,
                    data.LastWriteTime.High, data.LastWriteTime.Low,
                    data.LastAccessTime.High, data.LastAccessTime.Low,
                    data.FileSizeHigh, data.FileSizeLow);
                return true;
            }
            catch { return false; }
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
