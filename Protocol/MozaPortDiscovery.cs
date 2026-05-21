using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Security;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Process-wide MOZA port discovery via the Windows registry (usbser.sys layout),
    /// cross-referenced against <see cref="SerialPort.GetPortNames"/> to drop ghost
    /// entries. Replaces the prior WMI reflection path; serial-probe fallback in
    /// <see cref="MozaSerialConnection"/> kicks in only when the registry is empty.
    /// </summary>
    public sealed class MozaPortDiscovery
    {
        public static MozaPortDiscovery Instance { get; } = new MozaPortDiscovery();

        public const ushort MozaVid = 0x346E;

        private const string EnumUsbPath = @"SYSTEM\CurrentControlSet\Enum\USB";

        // Cache TTL — short enough to pick up plug/unplug between reconnect
        // ticks (5 s), long enough that wheelbase + AB9 managers running
        // back-to-back on the same tick share one registry walk.
        private static readonly long CacheTtlTicks = Stopwatch.Frequency * 2L;

        public readonly struct PortInfo
        {
            public readonly string PortName;             // e.g. "COM5"
            public readonly ushort Vid;                  // 0x346E
            public readonly ushort Pid;                  // 0x1000
            public readonly string FriendlyName;         // "USB Serial Device (COM5)"
            public readonly string InstanceId;           // "a&399b951f&0&0000"
            public readonly MozaDeviceCategory Category; // derived from Pid via MozaUsbIds.Categorize

            public PortInfo(string portName, ushort vid, ushort pid, string friendlyName, string instanceId)
            {
                PortName = portName;
                Vid = vid;
                Pid = pid;
                FriendlyName = friendlyName ?? string.Empty;
                InstanceId = instanceId ?? string.Empty;
                Category = MozaUsbIds.Categorize(pid);
            }
        }

        private readonly object _cacheLock = new object();
        private long _cacheTimestamp;                                  // 0 = uninitialised
        private IReadOnlyList<PortInfo> _cachedPorts = Array.Empty<PortInfo>();
        private string _lastSummary = "(not yet enumerated)";
        private int _hasLoggedFirstSuccess; // 0 or 1, atomic
        // Unknown-PID first-sighting log gate. Guarded by _cacheLock so a
        // concurrent cache refresh doesn't double-log the same PID.
        private readonly HashSet<ushort> _loggedUnknownPids = new HashSet<ushort>();

        private MozaPortDiscovery() { }

        /// <summary>Enumerate MOZA CDC ACM ports (cached for <see cref="CacheTtlTicks"/>).</summary>
        public IReadOnlyList<PortInfo> Enumerate()
        {
            lock (_cacheLock)
            {
                long now = Stopwatch.GetTimestamp();
                if (_cacheTimestamp != 0 && (now - _cacheTimestamp) < CacheTtlTicks)
                    return _cachedPorts;
            }

            var ports = EnumerateFromRegistry();
            var summary = SummarizePorts(ports);

            // Collect first-sighting unknown PIDs while holding the lock,
            // then emit log lines after releasing it (MozaLog can call
            // into SimHub on the same thread; don't run user-supplied
            // code under our own lock).
            List<PortInfo>? newUnknown = null;
            lock (_cacheLock)
            {
                _cachedPorts = ports;
                _cacheTimestamp = Stopwatch.GetTimestamp();
                _lastSummary = summary;

                for (int i = 0; i < ports.Count; i++)
                {
                    var p = ports[i];
                    if (p.Category != MozaDeviceCategory.Unknown) continue;
                    if (_loggedUnknownPids.Add(p.Pid))
                    {
                        (newUnknown ??= new List<PortInfo>()).Add(p);
                    }
                }
            }

            if (newUnknown != null)
            {
                for (int i = 0; i < newUnknown.Count; i++)
                {
                    var p = newUnknown[i];
                    MozaLog.Info(
                        $"[Moza] Unknown Moza PID 0x{p.Pid.ToString("X4", CultureInfo.InvariantCulture)} on " +
                        $"{p.PortName} — not in usb-ids inventory. Will be probed with every known protocol; " +
                        $"please report so docs/protocol/devices/usb-ids.md can be updated.");
                }
            }

            // First successful enumeration logs at Info so the user sees one
            // line in their support-bundle log confirming detection worked.
            // Subsequent enumerations log at Debug to avoid flooding.
            if (ports.Count > 0 && Interlocked.Exchange(ref _hasLoggedFirstSuccess, 1) == 0)
                MozaLog.Info($"[Moza] MOZA detection: source=registry, {summary}");
            else
                MozaLog.Debug($"[Moza] MOZA detection: source=registry, {summary}");

            return ports;
        }

        /// <summary>
        /// Convenience wrapper around <see cref="Enumerate"/> that returns only
        /// ports whose PID satisfies <paramref name="pidFilter"/>. Null filter
        /// returns all ports.
        /// </summary>
        public IReadOnlyList<PortInfo> EnumerateMatching(Func<ushort, bool>? pidFilter)
        {
            var all = Enumerate();
            if (pidFilter == null) return all;
            var matched = new List<PortInfo>(all.Count);
            for (int i = 0; i < all.Count; i++)
                if (pidFilter(all[i].Pid)) matched.Add(all[i]);
            return matched;
        }

        public bool TryGetByPort(string portName, out PortInfo info)
        {
            info = default;
            if (string.IsNullOrEmpty(portName)) return false;
            var all = Enumerate();
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].PortName, portName, StringComparison.OrdinalIgnoreCase))
                {
                    info = all[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>Force the next <see cref="Enumerate"/> call to re-walk the registry.</summary>
        public void Invalidate()
        {
            lock (_cacheLock)
            {
                _cacheTimestamp = 0;
                _cachedPorts = Array.Empty<PortInfo>();
            }
        }

        /// <summary>Human-readable single-line summary of the most recent enumeration. UI binding.</summary>
        public string LastEnumerationSummary
        {
            get { lock (_cacheLock) return _lastSummary; }
        }

        private static IReadOnlyList<PortInfo> EnumerateFromRegistry()
        {
            // Live COM port set — drops ghost registry entries left by previous
            // USB-port attachments. SerialPort.GetPortNames reads the same
            // SERIALCOMM table the kernel populates with currently-mounted ports.
            HashSet<string> liveCom;
            try
            {
                liveCom = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Moza] SerialPort.GetPortNames failed: {ex.GetType().Name}: {ex.Message}");
                liveCom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var results = new List<PortInfo>();
            try
            {
                using var enumKey = Registry.LocalMachine.OpenSubKey(EnumUsbPath, writable: false);
                if (enumKey == null)
                {
                    MozaLog.Debug($"[Moza] Registry: {EnumUsbPath} not found");
                    return results;
                }

                foreach (var deviceKeyName in enumKey.GetSubKeyNames())
                {
                    if (!TryParseMozaCdcKey(deviceKeyName, out var pid))
                        continue;

                    using var deviceKey = enumKey.OpenSubKey(deviceKeyName, writable: false);
                    if (deviceKey == null) continue;

                    foreach (var instanceName in deviceKey.GetSubKeyNames())
                    {
                        using var instanceKey = deviceKey.OpenSubKey(instanceName, writable: false);
                        if (instanceKey == null) continue;

                        // Only accept the standard usbser CDC driver — guards
                        // against future MOZA devices that bind a different
                        // service (WinUSB, custom driver) and shouldn't be
                        // treated as a serial pipe.
                        var service = instanceKey.GetValue("Service") as string;
                        if (!string.Equals(service, "usbser", StringComparison.OrdinalIgnoreCase))
                            continue;

                        using var paramsKey = instanceKey.OpenSubKey("Device Parameters", writable: false);
                        if (paramsKey == null) continue;

                        var portName = paramsKey.GetValue("PortName") as string;
                        if (string.IsNullOrEmpty(portName)) continue;

                        // Filter ghosts: PortName is in the registry but the
                        // COM is not currently mounted.
                        if (!liveCom.Contains(portName!)) continue;

                        var friendly = (instanceKey.GetValue("FriendlyName") as string) ?? string.Empty;
                        results.Add(new PortInfo(portName!, MozaVid, pid, friendly, instanceName));
                    }
                }
            }
            catch (SecurityException ex)
            {
                MozaLog.Debug($"[Moza] Registry access denied: {ex.Message}");
            }
            catch (IOException ex)
            {
                MozaLog.Debug($"[Moza] Registry IO failure: {ex.Message}");
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Moza] Registry enumeration failed: {ex.GetType().Name}: {ex.Message}");
            }

            return results;
        }

        // Match keys named "VID_346E&PID_xxxx&MI_00" (composite CDC child)
        // OR "VID_346E&PID_xxxx" (single-interface CDC device, e.g. mBooster
        // Pedals PID 0x0008). Both forms are admitted; the downstream
        // Service=="usbser" + Device Parameters\PortName presence checks in
        // EnumerateFromRegistry filter out non-CDC composite parents (which
        // bind usbccgp and have no PortName).
        private static bool TryParseMozaCdcKey(string keyName, out ushort pid)
        {
            pid = 0;
            if (string.IsNullOrEmpty(keyName)) return false;

            const string vidPrefix = "VID_346E&PID_";
            const string miSuffix = "&MI_00";
            if (keyName.Length < vidPrefix.Length + 4) return false;
            if (string.Compare(keyName, 0, vidPrefix, 0, vidPrefix.Length,
                               StringComparison.OrdinalIgnoreCase) != 0) return false;

            int afterPid = vidPrefix.Length + 4;
            if (keyName.Length == afterPid)
            {
                // Single-interface CDC form: "VID_346E&PID_xxxx".
            }
            else if (keyName.Length == afterPid + miSuffix.Length &&
                     string.Compare(keyName, afterPid, miSuffix, 0, miSuffix.Length,
                                    StringComparison.OrdinalIgnoreCase) == 0)
            {
                // Composite CDC child form: "VID_346E&PID_xxxx&MI_00".
            }
            else
            {
                return false;
            }

            var pidHex = keyName.Substring(vidPrefix.Length, 4);
            return ushort.TryParse(pidHex, NumberStyles.HexNumber,
                                   CultureInfo.InvariantCulture, out pid);
        }

        private static string SummarizePorts(IReadOnlyList<PortInfo> ports)
        {
            if (ports.Count == 0) return "ports=[]";
            var sb = new StringBuilder("ports=[");
            for (int i = 0; i < ports.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var p = ports[i];
                sb.Append(p.PortName);
                sb.Append(":0x");
                sb.Append(p.Pid.ToString("X4", CultureInfo.InvariantCulture));
                sb.Append('(');
                sb.Append(MozaUsbIds.Describe(p.Pid));
                sb.Append(')');
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
