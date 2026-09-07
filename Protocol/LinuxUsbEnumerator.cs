using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MozaPlugin.Protocol
{
    /// <summary>One MOZA CDC interface as seen through Linux sysfs.</summary>
    internal readonly struct LinuxUsbSerialNode
    {
        public readonly ushort Vid;
        public readonly ushort Pid;
        public readonly string Serial;          // USB iSerialNumber, "" when the device reports none
        public readonly string Product;         // "MOZA R5 Base"
        public readonly string Manufacturer;    // "Gudsen"
        public readonly string BusPath;         // "1-1.3" — stable per physical device while plugged
        public readonly int InterfaceNumber;    // bInterfaceNumber of the interface owning the tty
        public readonly string TtyName;         // "ttyACM2"
        public readonly string DevicePath;      // @"Z:\dev\ttyACM2" — what we actually open

        public LinuxUsbSerialNode(ushort vid, ushort pid, string serial, string product,
                                  string manufacturer, string busPath, int interfaceNumber,
                                  string ttyName, string devicePath)
        {
            Vid = vid;
            Pid = pid;
            Serial = serial ?? string.Empty;
            Product = product ?? string.Empty;
            Manufacturer = manufacturer ?? string.Empty;
            BusPath = busPath ?? string.Empty;
            InterfaceNumber = interfaceNumber;
            TtyName = ttyName ?? string.Empty;
            DevicePath = devicePath ?? string.Empty;
        }
    }

    /// <summary>
    /// Enumerates MOZA USB CDC interfaces from Linux sysfs, reached through
    /// Wine's unix drive (<see cref="WineHost.UnixRoot"/>). This is the Wine/
    /// Proton counterpart of <c>MozaPortDiscovery</c>'s registry walk: it yields
    /// the same identity (VID, PID, serial, product, interface number) that
    /// <c>HKLM\SYSTEM\...\Enum\USB</c> yields on Windows, plus the openable
    /// device path.
    ///
    /// <para>Every path used here is deliberately free of <c>':'</c>. sysfs
    /// interface directories are named <c>1-1.3:1.0</c>, and a colon inside a
    /// path component is hostile to Win32/.NET path normalisation; the one place
    /// we have to touch such a directory (disambiguating two identical devices)
    /// is isolated and falls back to ordinal pairing.</para>
    ///
    /// <para>sysfs files advertise a 4096-byte length and return fewer bytes, so
    /// reads MUST go through <c>File.ReadAllText</c>/StreamReader —
    /// <c>File.ReadAllBytes</c> throws on the short read.</para>
    /// </summary>
    internal static class LinuxUsbEnumerator
    {
        public const ushort MozaVid = MozaPortDiscovery.MozaVid;

        /// <summary>True when sysfs is reachable, i.e. this enumerator can run.</summary>
        public static bool Available
        {
            get
            {
                if (!WineHost.IsWine) return false;
                var tty = WineHost.UnixPath("/sys/class/tty");
                if (tty == null) return false;
                try { return Directory.Exists(tty); }
                catch { return false; }
            }
        }

        /// <summary>
        /// Every MOZA (VID 0x346E) CDC interface currently present, in tty-name
        /// order. Returns an empty list — never throws — when sysfs is
        /// unreachable or unreadable.
        /// </summary>
        public static IReadOnlyList<LinuxUsbSerialNode> Enumerate()
        {
            var results = new List<LinuxUsbSerialNode>();
            string? classTty = WineHost.UnixPath("/sys/class/tty");
            if (classTty == null) return results;

            string[] ttyDirs;
            try { ttyDirs = Directory.GetDirectories(classTty); }
            catch (Exception ex)
            {
                MozaLog.DebugIfChanged("sysfs-tty",
                    $"[AZOM] sysfs: cannot list {classTty}: {ex.GetType().Name}: {ex.Message}");
                return results;
            }

            // Candidate tty nodes: USB CDC-ACM and usb-serial bridges. Anything
            // else (ttyS*, virtual consoles) never carries a USB device link.
            var ttyNames = new List<string>();
            foreach (var dir in ttyDirs)
            {
                string name = LeafName(dir);
                if (name.StartsWith("ttyACM", StringComparison.Ordinal)
                    || name.StartsWith("ttyUSB", StringComparison.Ordinal))
                    ttyNames.Add(name);
            }
            if (ttyNames.Count == 0) return results;
            ttyNames.Sort(StringComparer.Ordinal);

            // MOZA ttys, with the VID/PID and interface number read straight off
            // the interface the tty hangs from. Non-MOZA ttys are dropped here —
            // this is what keeps an Arduino or another vendor's CDC device from
            // ever being opened.
            var mozaTtys = new List<(string Tty, ushort Pid, int IfNum)>();
            foreach (var tty in ttyNames)
            {
                string ifaceDir = Path.Combine(classTty, tty, "device");
                if (!TryReadUevent(ifaceDir, out ushort vid, out ushort pid)) continue;
                if (vid != MozaVid) continue;
                int ifNum = ReadHexByte(Path.Combine(ifaceDir, "bInterfaceNumber"), -1);
                mozaTtys.Add((tty, pid, ifNum));
            }
            if (mozaTtys.Count == 0) return results;

            var devices = EnumerateUsbDevices();

            foreach (var t in mozaTtys)
            {
                var matches = new List<UsbDevice>();
                foreach (var d in devices)
                    if (d.Vid == MozaVid && d.Pid == t.Pid) matches.Add(d);

                UsbDevice dev = UsbDevice.Empty;
                if (matches.Count == 1)
                {
                    dev = matches[0];
                }
                else if (matches.Count > 1)
                {
                    // Two physically identical devices (the real case: a pair of
                    // mBoosters, PID 0x0008). Resolve through the interface
                    // directory that owns this tty; ordinal pairing is the
                    // fallback when the colon-bearing path is unusable.
                    if (!TryResolveOwningDevice(matches, t.Tty, out dev))
                        TryResolveByOrdinal(matches, mozaTtys, t.Tty, out dev);
                }

                string? devicePath = WineHost.UnixPath("/dev/" + t.Tty);
                results.Add(new LinuxUsbSerialNode(
                    MozaVid, t.Pid,
                    dev.Serial, dev.Product, dev.Manufacturer, dev.BusPath,
                    t.IfNum, t.Tty, devicePath ?? string.Empty));
            }

            return results;
        }

        private readonly struct UsbDevice
        {
            public readonly string BusPath;
            public readonly ushort Vid;
            public readonly ushort Pid;
            public readonly string Serial;
            public readonly string Product;
            public readonly string Manufacturer;

            public UsbDevice(string busPath, ushort vid, ushort pid, string serial,
                             string product, string manufacturer)
            {
                BusPath = busPath ?? string.Empty;
                Vid = vid;
                Pid = pid;
                Serial = serial ?? string.Empty;
                Product = product ?? string.Empty;
                Manufacturer = manufacturer ?? string.Empty;
            }

            public static UsbDevice Empty =>
                new UsbDevice(string.Empty, 0, 0, string.Empty, string.Empty, string.Empty);
        }

        // /sys/bus/usb/devices holds both devices ("1-1.3") and interfaces
        // ("1-1.3:1.0"). Only the colon-free entries are devices, and only
        // devices carry idVendor/serial — so the filter doubles as the
        // colon-avoidance rule.
        private static List<UsbDevice> EnumerateUsbDevices()
        {
            var list = new List<UsbDevice>();
            string? root = WineHost.UnixPath("/sys/bus/usb/devices");
            if (root == null) return list;

            string[] entries;
            try { entries = Directory.GetDirectories(root); }
            catch (Exception ex)
            {
                MozaLog.DebugIfChanged("sysfs-usb",
                    $"[AZOM] sysfs: cannot list {root}: {ex.GetType().Name}: {ex.Message}");
                return list;
            }

            foreach (var entry in entries)
            {
                string name = LeafName(entry);
                if (name.IndexOf(':') >= 0) continue;   // interface, not a device
                ushort vid = ReadHexUshort(Path.Combine(entry, "idVendor"), 0);
                if (vid != MozaVid) continue;
                ushort pid = ReadHexUshort(Path.Combine(entry, "idProduct"), 0);
                list.Add(new UsbDevice(
                    name, vid, pid,
                    ReadText(Path.Combine(entry, "serial")),
                    ReadText(Path.Combine(entry, "product")),
                    ReadText(Path.Combine(entry, "manufacturer"))));
            }
            return list;
        }

        // Which of these identical devices owns <tty>? Ask each device's
        // interface children whether they carry it. This is the only path in
        // the file with a ':' in it, so it is fully isolated — and it is the
        // only one that must go through raw Win32: under SimHub's AppDomain
        // .NET's FileIOPermission path emulation rejects a ':' outside the
        // drive with NotSupportedException, which Directory.Exists swallows
        // into a silent false. Wine itself resolves the path correctly
        // (GetFileAttributesW returns the directory bit), so the P/Invoke is
        // what makes this walk work at all rather than always falling through
        // to ordinal pairing.
        private static bool TryResolveOwningDevice(List<UsbDevice> candidates, string tty, out UsbDevice found)
        {
            found = UsbDevice.Empty;
            string? root = WineHost.UnixPath("/sys/bus/usb/devices");
            if (root == null) return false;

            foreach (var c in candidates)
            {
                try
                {
                    string devDir = Path.Combine(root, c.BusPath);
                    foreach (var ifaceDir in Directory.GetDirectories(devDir))
                    {
                        string ifaceName = LeafName(ifaceDir);
                        if (ifaceName.IndexOf(':') < 0) continue;
                        // Concatenate, don't Path.Combine — keep the colon path
                        // away from every managed path API on the way to Win32.
                        if (DirectoryExistsRaw(ifaceDir + "\\tty\\" + tty))
                        {
                            found = c;
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MozaLog.DebugIfChanged("sysfs-iface",
                        $"[AZOM] sysfs: interface walk for {c.BusPath} failed: {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }
            return false;
        }

        // Last resort for identical devices: pair the Nth tty of that PID with
        // the Nth device of that PID, both in sorted order. Wrong only if the
        // kernel enumerated them out of bus order, and it costs nothing but a
        // swapped per-device settings row.
        private static bool TryResolveByOrdinal(
            List<UsbDevice> candidates,
            List<(string Tty, ushort Pid, int IfNum)> allTtys,
            string tty,
            out UsbDevice found)
        {
            found = UsbDevice.Empty;
            var sameP = new List<string>();
            foreach (var t in allTtys)
                if (t.Pid == candidates[0].Pid) sameP.Add(t.Tty);
            sameP.Sort(StringComparer.Ordinal);
            int idx = sameP.IndexOf(tty);
            if (idx < 0) return false;

            var sorted = new List<UsbDevice>(candidates);
            sorted.Sort((a, b) => string.CompareOrdinal(a.BusPath, b.BusPath));
            if (idx >= sorted.Count) return false;
            found = sorted[idx];
            MozaLog.DebugIfChanged("sysfs-ordinal",
                $"[AZOM] sysfs: {sorted.Count} identical PID 0x{candidates[0].Pid:X4} devices — " +
                $"paired {tty} with {found.BusPath} by ordinal");
            return true;
        }

        // uevent on a USB interface carries "PRODUCT=<vid>/<pid>/<bcd>" with the
        // fields in lowercase hex and NOT zero-padded ("346e/4/100").
        private static bool TryReadUevent(string ifaceDir, out ushort vid, out ushort pid)
        {
            vid = 0;
            pid = 0;
            string text = ReadText(Path.Combine(ifaceDir, "uevent"));
            if (text.Length == 0) return false;

            foreach (var rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("PRODUCT=", StringComparison.Ordinal)) continue;
                var parts = line.Substring("PRODUCT=".Length).Split('/');
                if (parts.Length < 2) return false;
                return ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
                    && ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
            }
            return false;
        }

        private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string path);

        /// <summary>Directory.Exists for a path .NET's permission emulation
        /// refuses to normalise (a ':' outside the drive). Wine resolves it
        /// correctly; only the managed layer objects.</summary>
        private static bool DirectoryExistsRaw(string path)
        {
            try
            {
                uint attr = GetFileAttributesW(path);
                return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY) != 0;
            }
            catch { return false; }
        }

        private static readonly char[] s_pathSeps = { '\\', '/' };

        // Path.GetFileName treats ':' as the VOLUME separator, so on a sysfs
        // interface dir it returns "1.0" rather than "1-0:1.0" — silently
        // defeating every colon-based filter in this file. Split on directory
        // separators only.
        private static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            int i = path.LastIndexOfAny(s_pathSeps);
            return i < 0 ? path : path.Substring(i + 1);
        }

        private static string ReadText(string path)
        {
            try
            {
                // Not ReadAllBytes: sysfs reports 4096 bytes and returns fewer.
                return File.ReadAllText(path).Trim();
            }
            catch { return string.Empty; }
        }

        private static ushort ReadHexUshort(string path, ushort fallback)
        {
            string s = ReadText(path);
            return s.Length > 0
                   && ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort v)
                ? v : fallback;
        }

        private static int ReadHexByte(string path, int fallback)
        {
            string s = ReadText(path);
            return s.Length > 0
                   && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)
                ? v : fallback;
        }
    }
}
