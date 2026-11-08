using System;
using System.Collections.Generic;
using HidSharp;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// A chosen HID axis, as saved in settings: the device (matched by path
    /// first, else VID/PID), the axis usage, and the captured travel.
    /// Immutable once stored; replace the whole instance to change it.
    /// </summary>
    public sealed class HidAxisSelection
    {
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public string DeviceName { get; set; } = "";
        /// <summary>Last seen device path; preferred when several devices share VID/PID.</summary>
        public string DevicePath { get; set; } = "";
        /// <summary>Usage page &lt;&lt; 16 | usage.</summary>
        public uint Usage { get; set; }
        /// <summary>The axis' declared range, used until a travel is captured.</summary>
        public int LogicalMin { get; set; }
        public int LogicalMax { get; set; }
        /// <summary>Raw value with the pedal released / fully pressed; null = use the logical range.</summary>
        public int? RawReleased { get; set; }
        public int? RawPressed { get; set; }

        /// <summary>Position 0..1 (0 released) for a raw reading; NaN when the range is empty.</summary>
        public double Normalize(int raw)
        {
            double released = RawReleased ?? LogicalMin;
            double pressed = RawPressed ?? LogicalMax;
            double span = pressed - released;
            if (span == 0) return double.NaN;
            double v = (raw - released) / span;
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }

        public bool SameAxis(HidAxisSelection? other) =>
            other != null && other.VendorId == VendorId && other.ProductId == ProductId
            && other.Usage == Usage && string.Equals(other.DevicePath, DevicePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One axis a HID game controller reports.</summary>
    internal readonly struct HidAxisInfo
    {
        public readonly uint Usage;
        public readonly int LogicalMin;
        public readonly int LogicalMax;

        public HidAxisInfo(uint usage, int logicalMin, int logicalMax)
        {
            Usage = usage;
            LogicalMin = logicalMin;
            LogicalMax = logicalMax;
        }
    }

    /// <summary>A HID game controller (one top-level collection) and its axes.</summary>
    internal sealed class HidAxisDevice
    {
        public string Path { get; }
        public int VendorId { get; }
        public int ProductId { get; }
        public string Name { get; }
        public IReadOnlyList<HidAxisInfo> Axes { get; }

        public HidAxisDevice(string path, int vid, int pid, string name, IReadOnlyList<HidAxisInfo> axes)
        {
            Path = path;
            VendorId = vid;
            ProductId = pid;
            Name = name;
            Axes = axes;
        }

        public bool Matches(HidAxisSelection sel) => VendorId == sel.VendorId && ProductId == sel.ProductId;
    }

    /// <summary>Lists HID game controllers and their axes, decoded with <see cref="HidInputLayout"/>.</summary>
    internal static class HidAxisCatalog
    {
        private const ushort PageGenericDesktop = 0x01;
        private const ushort PageSimulation = 0x02;

        /// <summary>Joysticks, gamepads, multi-axis controllers, and anything with a simulation-page axis.</summary>
        public static List<HidAxisDevice> Enumerate()
        {
            var result = new List<HidAxisDevice>();
            IEnumerable<HidDevice> devices;
            try { devices = DeviceList.Local.GetHidDevices(); }
            catch (Exception ex)
            {
                MozaLog.Debug($"[AZOM] HID axis enumeration failed: {ex.Message}");
                return result;
            }

            foreach (var dev in devices)
            {
                string path;
                try { path = dev.DevicePath ?? ""; } catch { continue; }
                if (path.Length == 0) continue;
                try
                {
                    using (var layout = HidInputLayout.Open(path))
                    {
                        var axes = AxesOf(layout);
                        if (axes.Count == 0 || !IsGameController(layout, axes)) continue;
                        result.Add(new HidAxisDevice(path, dev.VendorID, dev.ProductID, NameOf(dev), axes));
                    }
                }
                catch
                {
                    // Devices that refuse even a zero-access open aren't controllers we can read.
                }
            }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        /// <summary>The device path currently matching <paramref name="sel"/>: its saved path if
        /// present, else the first device with the same VID/PID carrying the axis.</summary>
        public static string? Resolve(HidAxisSelection sel)
        {
            IEnumerable<HidDevice> devices;
            try { devices = DeviceList.Local.GetHidDevices(sel.VendorId, sel.ProductId); }
            catch { return null; }
            string? fallback = null;
            foreach (var dev in devices)
            {
                string path;
                try { path = dev.DevicePath ?? ""; } catch { continue; }
                if (path.Length == 0) continue;
                if (string.Equals(path, sel.DevicePath, StringComparison.OrdinalIgnoreCase)) return path;
                if (fallback != null) continue;
                try
                {
                    using (var layout = HidInputLayout.Open(path))
                        foreach (var v in layout.Values)
                            if (v.FullUsage == sel.Usage) { fallback = path; break; }
                }
                catch { }
            }
            return fallback;
        }

        internal static bool IsAxisUsage(uint fullUsage)
        {
            ushort page = (ushort)(fullUsage >> 16);
            ushort usage = (ushort)fullUsage;
            return (page == PageGenericDesktop && usage >= 0x30 && usage <= 0x38) || page == PageSimulation;
        }

        private static List<HidAxisInfo> AxesOf(HidInputLayout layout)
        {
            var axes = new List<HidAxisInfo>();
            var seen = new HashSet<uint>();
            foreach (var v in layout.Values)
                if (IsAxisUsage(v.FullUsage) && seen.Add(v.FullUsage))
                    axes.Add(new HidAxisInfo(v.FullUsage, v.LogicalMin, v.LogicalMax));
            return axes;
        }

        private static bool IsGameController(HidInputLayout layout, List<HidAxisInfo> axes)
        {
            if (layout.TopLevelUsagePage == PageGenericDesktop
                && (layout.TopLevelUsage == 0x04 || layout.TopLevelUsage == 0x05 || layout.TopLevelUsage == 0x08))
                return true;
            foreach (var a in axes)
                if ((a.Usage >> 16) == PageSimulation) return true;
            return false;
        }

        private static string NameOf(HidDevice dev)
        {
            try
            {
                string name = dev.GetFriendlyName();
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
            catch { }
            return $"{dev.VendorID:X4}:{dev.ProductID:X4}";
        }
    }
}
