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
    /// <summary>Where the current port list came from.</summary>
    public enum MozaDiscoverySource
    {
        /// <summary>Nothing usable — the serial-probe fallback is the only option.</summary>
        None = 0,
        /// <summary>Windows <c>Enum\USB</c> registry walk (native Windows).</summary>
        Registry,
        /// <summary>Linux sysfs through Wine's unix drive (Wine/Proton).</summary>
        Sysfs,
    }

    /// <summary>
    /// Process-wide MOZA port discovery. Two sources fill the same
    /// <see cref="PortInfo"/> shape so every consumer is platform-agnostic:
    /// the Windows registry (usbser.sys layout, cross-referenced against
    /// <see cref="SerialPort.GetPortNames"/> to drop ghost entries), and Linux
    /// sysfs via <see cref="LinuxUsbEnumerator"/> under Wine/Proton, where the
    /// registry has no <c>Enum\USB</c> tree at all. Replaces the prior WMI
    /// reflection path; the serial-probe fallback in
    /// <see cref="MozaSerialConnection"/> kicks in only when neither source
    /// produced anything (<see cref="IsAuthoritative"/> false).
    /// </summary>
    public sealed class MozaPortDiscovery
    {
        public static MozaPortDiscovery Instance { get; } = new MozaPortDiscovery();

        public const ushort MozaVid = 0x346E;

        private const string EnumUsbPath = @"SYSTEM\CurrentControlSet\Enum\USB";
        private const string EnumHidPath = @"SYSTEM\CurrentControlSet\Enum\HID";

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
            // Windows Container ID GUID — identical across every interface/function
            // of one physical composite device (CDC + HID). Empty if the registry
            // key had none. Used to pair an mBooster's HID axis stream to its CDC
            // lane deterministically even when Windows assigns the two interfaces
            // unrelated instance IDs (see docs/protocol/devices/mbooster.md).
            public readonly string ContainerId;          // "{4d36e978-...}"
            public readonly MozaDeviceCategory Category; // derived from Pid via MozaUsbIds.Categorize
            // Openable device path, e.g. @"Z:\dev\ttyACM2". Set only by the sysfs
            // source: under Wine the COM name cannot be resolved to a tty (the
            // dosdevices symlink target is unreadable from inside the prefix), so
            // the connection opens this path directly via WineDevicePathMozaPort.
            // Empty on Windows, where PortName is the thing to open.
            public readonly string DevicePath;
            // USB iSerialNumber. The per-unit half of the durable id; empty when
            // the device reports none, or on Windows (the registry walk doesn't
            // read it — Windows already has a stable InstanceId).
            public readonly string Serial;

            public PortInfo(string portName, ushort vid, ushort pid, string friendlyName, string instanceId,
                            string containerId = "", string devicePath = "", string serial = "")
            {
                PortName = portName;
                Vid = vid;
                Pid = pid;
                FriendlyName = friendlyName ?? string.Empty;
                InstanceId = instanceId ?? string.Empty;
                ContainerId = containerId ?? string.Empty;
                DevicePath = devicePath ?? string.Empty;
                Serial = serial ?? string.Empty;
                Category = MozaUsbIds.Categorize(pid);
            }
        }

        private readonly object _cacheLock = new object();
        private long _cacheTimestamp;                                  // 0 = uninitialised
        private IReadOnlyList<PortInfo> _cachedPorts = Array.Empty<PortInfo>();
        private string _lastSummary = "(not yet enumerated)";
        private MozaDiscoverySource _lastSource = MozaDiscoverySource.None;
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

            // Wine/Proton has no Enum\USB tree, so the registry walk is dead
            // there; sysfs carries the same identity. Native Windows never
            // reaches the sysfs branch (WineHost.UnixRoot is null).
            bool useSysfs = LinuxUsbEnumerator.Available;
            var ports = useSysfs ? EnumerateFromSysfs() : EnumerateFromRegistry();
            var source = ports.Count > 0
                ? (useSysfs ? MozaDiscoverySource.Sysfs : MozaDiscoverySource.Registry)
                : MozaDiscoverySource.None;
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
                _lastSource = source;

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
                        $"[AZOM] Unknown Moza PID 0x{p.Pid.ToString("X4", CultureInfo.InvariantCulture)} on " +
                        $"{p.PortName} — not in usb-ids inventory. Will be probed with every known protocol; " +
                        $"please report so docs/protocol/devices/usb-ids.md can be updated.");
                }
            }

            // First successful enumeration logs at Info so the user sees one
            // line in their support-bundle log confirming detection worked.
            // Subsequent enumerations log at Debug to avoid flooding.
            string sourceLabel = useSysfs
                ? $"sysfs ({WineHost.Describe()})"
                : "registry";
            if (ports.Count > 0 && Interlocked.Exchange(ref _hasLoggedFirstSuccess, 1) == 0)
                MozaLog.Info($"[AZOM] MOZA detection: source={sourceLabel}, {summary}");
            else
                MozaLog.DebugIfChanged("port-discovery", $"[AZOM] MOZA detection: source={sourceLabel}, {summary}");

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

        /// <summary>Which source filled the most recent enumeration.</summary>
        public MozaDiscoverySource Source
        {
            get { Enumerate(); lock (_cacheLock) return _lastSource; }
        }

        /// <summary>
        /// True when a real device source answered, i.e. the port list is the
        /// authoritative answer and the serial-probe fallback has nothing to add.
        /// False means neither the registry nor sysfs produced anything, which is
        /// the only case where blind probing is still worth doing.
        /// </summary>
        public bool IsAuthoritative => Source != MozaDiscoverySource.None;

        /// <summary>
        /// Stable per-unit identity that survives re-enumeration, replug and COM/tty
        /// renumbering — the key lanes persist instead of a port name. Prefers the USB
        /// serial number; falls back to the bus/instance path for devices that report
        /// none.
        /// </summary>
        public static string DurableId(in PortInfo info)
        {
            string head = $"{info.Vid:X4}:{info.Pid:X4}";
            if (!string.IsNullOrEmpty(info.Serial)) return $"{head}:{info.Serial}";
            if (!string.IsNullOrEmpty(info.InstanceId)) return $"{head}:bus={info.InstanceId}";
            return $"{head}:port={info.PortName}";
        }

        /// <summary>Find the currently-present device carrying <paramref name="durableId"/>.</summary>
        public bool TryGetByDurableId(string durableId, out PortInfo info)
        {
            info = default;
            if (string.IsNullOrEmpty(durableId)) return false;
            var all = Enumerate();
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(DurableId(all[i]), durableId, StringComparison.OrdinalIgnoreCase))
                {
                    info = all[i];
                    return true;
                }
            }
            return false;
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

        /// <summary>
        /// Read the Windows Container ID for a HID device from its HidSharp
        /// <c>DevicePath</c> (<c>\\?\HID#VID_xxxx&amp;PID_xxxx&amp;MI_xx#&lt;instance&gt;#{guid}</c>).
        /// The Container ID is identical across every interface/function of one
        /// physical composite device, so it pairs an mBooster's HID axis stream
        /// to its CDC lane even when Windows assigns the two interfaces
        /// unrelated instance IDs (see docs/protocol/devices/mbooster.md "HID
        /// identity reconciliation"). Returns "" if the path is malformed, the
        /// registry key is absent, or the value is missing (e.g. under Wine).
        /// </summary>
        public string GetHidContainerId(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return string.Empty;
            // Split on '#': [\\?\HID, VID_..&PID_..&MI_.., <instance>, {guid}]
            var parts = devicePath.Split('#');
            if (parts.Length < 3 || string.IsNullOrEmpty(parts[1]) || string.IsNullOrEmpty(parts[2]))
                return string.Empty;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"{EnumHidPath}\{parts[1]}\{parts[2]}", writable: false);
                return (key?.GetValue("ContainerID") as string) ?? string.Empty;
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] HID ContainerID read failed for '{devicePath}': {ex.GetType().Name}: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>Force the next <see cref="Enumerate"/> call to re-walk the registry.</summary>
        public void Invalidate()
        {
            lock (_cacheLock)
            {
                _cacheTimestamp = 0;
                _cachedPorts = Array.Empty<PortInfo>();
            }
            // Derived from the same enumeration — drop it together or the labels
            // outlive the ports they name.
            WineComNameResolver.Invalidate();
        }

        /// <summary>Human-readable single-line summary of the most recent enumeration. UI binding.</summary>
        public string LastEnumerationSummary
        {
            get { lock (_cacheLock) return _lastSummary; }
        }

        /// <summary>
        /// Wine/Proton source. <c>PortName</c> becomes the tty name ("ttyACM2") —
        /// it stays the process-wide dedup/display key — and <c>DevicePath</c>
        /// carries what to open. <c>InstanceId</c>/<c>ContainerId</c> both carry
        /// the USB bus path, which has exactly the Container ID semantic (one
        /// value shared by every interface of one physical device), so the
        /// mBooster CDC-to-HID pairing keeps working.
        /// </summary>
        private static IReadOnlyList<PortInfo> EnumerateFromSysfs()
        {
            var nodes = LinuxUsbEnumerator.Enumerate();
            var results = new List<PortInfo>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                string friendly = n.Product.Length > 0
                    ? $"{n.Product} ({n.TtyName})"
                    : $"MOZA CDC ({n.TtyName})";
                results.Add(new PortInfo(
                    n.TtyName, n.Vid, n.Pid, friendly,
                    instanceId: n.BusPath,
                    // Prefer the USB serial: it is the one identity the HID side
                    // can also read under Wine, which is what pairs an mBooster's
                    // axis stream to this CDC lane. Bus path when there is none.
                    containerId: n.Serial.Length > 0 ? n.Serial : n.BusPath,
                    devicePath: n.DevicePath,
                    serial: n.Serial));
            }
            return results;
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
                MozaLog.Debug($"[AZOM] SerialPort.GetPortNames failed: {ex.GetType().Name}: {ex.Message}");
                liveCom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var results = new List<PortInfo>();
            try
            {
                using var enumKey = Registry.LocalMachine.OpenSubKey(EnumUsbPath, writable: false);
                if (enumKey == null)
                {
                    MozaLog.Debug($"[AZOM] Registry: {EnumUsbPath} not found");
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
                        // ContainerID groups all interfaces of one physical device;
                        // read from the instance key (REG_SZ). Absent on some driver
                        // stacks / under Wine — empty string is the graceful default.
                        var containerId = (instanceKey.GetValue("ContainerID") as string) ?? string.Empty;
                        results.Add(new PortInfo(portName!, MozaVid, pid, friendly, instanceName, containerId));
                    }
                }
            }
            catch (SecurityException ex)
            {
                MozaLog.Debug($"[AZOM] Registry access denied: {ex.Message}");
            }
            catch (IOException ex)
            {
                MozaLog.Debug($"[AZOM] Registry IO failure: {ex.Message}");
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] Registry enumeration failed: {ex.GetType().Name}: {ex.Message}");
            }

            return results;
        }

        // Match any MOZA USB device-ID key "VID_346E&PID_xxxx" optionally
        // followed by an '&'-delimited suffix — the bare single-interface form
        // (e.g. mBooster Pedals PID 0x0008), the composite child "…&MI_00", and
        // the revision-bearing forms Windows emits on some USB topologies
        // (deep hub chains, etc.): "…&REV_0100", "…&REV_0100&MI_00". We do not
        // enumerate exact suffix shapes — the authoritative CDC gate is
        // downstream in EnumerateFromRegistry (Service=="usbser" + Device
        // Parameters\PortName presence + live-COM check), which rejects the
        // composite parent (binds usbccgp, no PortName) and any non-serial
        // interface regardless of how the key is spelled.
        private static bool TryParseMozaCdcKey(string keyName, out ushort pid)
        {
            pid = 0;
            if (string.IsNullOrEmpty(keyName)) return false;

            const string vidPrefix = "VID_346E&PID_";
            int afterPid = vidPrefix.Length + 4;
            if (keyName.Length < afterPid) return false;
            if (string.Compare(keyName, 0, vidPrefix, 0, vidPrefix.Length,
                               StringComparison.OrdinalIgnoreCase) != 0) return false;

            // Anything after the 4-hex PID must start with '&' so we don't
            // accept a longer (malformed) PID field as a match.
            if (keyName.Length > afterPid && keyName[afterPid] != '&') return false;

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
