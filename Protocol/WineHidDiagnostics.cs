using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace MozaPlugin.Protocol
{
    /// <summary>Snapshot for the Diagnostics "Wine HID" lines.</summary>
    internal sealed class WineHidSnapshot
    {
        /// <summary>MOZA hidraw devices the reader consumes: PID → kernel HID name.</summary>
        public IReadOnlyDictionary<ushort, string> Hidraw { get; }
        public IReadOnlyList<ushort> OpenPids { get; }
        /// <summary>Present as hidraw but not open in the HID reader.</summary>
        public IReadOnlyList<ushort> DeadPids { get; }
        public string Winebus { get; }

        public WineHidSnapshot(IReadOnlyDictionary<ushort, string> hidraw, IReadOnlyList<ushort> open,
                               IReadOnlyList<ushort> dead, string winebus)
        {
            Hidraw = hidraw;
            OpenPids = open;
            DeadPids = dead;
            Winebus = winebus;
        }
    }

    /// <summary>
    /// Wine/Linux: compares the MOZA devices the kernel exposes as hidraw with
    /// the ones the HID reader has open, plus the prefix's winebus settings, so
    /// a support bundle shows which device never reached the reader.
    /// </summary>
    internal static class WineHidDiagnostics
    {
        private const string WinebusKey = @"System\CurrentControlSet\Services\winebus";

        /// <summary>Null when not Wine-on-Linux or the reader isn't running.</summary>
        public static WineHidSnapshot? Capture(MozaHidReader? reader)
        {
            if (!WineHost.IsWine || WineHost.UnixRoot == null || reader == null) return null;

            var hidraw = LinuxUsbEnumerator.EnumerateMozaHidraw()
                .Where(kv => MozaHidReader.ReadsPid(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var open = reader.OpenPidsSnapshot().OrderBy(p => p).ToList();
            var dead = hidraw.Keys.Where(p => !open.Contains(p)).OrderBy(p => p).ToList();
            return new WineHidSnapshot(hidraw, open, dead, DescribeWinebus());
        }

        private static string DescribeWinebus()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(WinebusKey))
                {
                    if (key == null) return "(key absent)";
                    string V(string name)
                    {
                        switch (key.GetValue(name))
                        {
                            case null: return "—";
                            case string[] multi: return "[" + string.Join(", ", multi.Where(s => s.Length > 0)) + "]";
                            case object v: return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "—";
                        }
                    }
                    return $"Enable SDL={V("Enable SDL")} Map Controllers={V("Map Controllers")} " +
                           $"DisableInput={V("DisableInput")} DisableHidraw={V("DisableHidraw")} " +
                           $"EnableHidraw={V("EnableHidraw")}";
                }
            }
            catch (Exception ex) { return $"(unreadable: {ex.GetType().Name})"; }
        }
    }
}
