using System;
using System.Collections.Generic;
using System.Globalization;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// MOZA PID category — gates which connection class may claim the port.
    /// <c>Unknown</c> (zero) is the fallback for unregistered PIDs (both
    /// wheelbase + AB9 connections accept it as a probe candidate).
    /// </summary>
    public enum MozaDeviceCategory
    {
        Unknown = 0,
        Wheelbase,
        Pedals,
        Shifter,
        Handbrake,
        Hub,
        Dashboard,
        Ab9,
        MBooster,
    }

    /// <summary>
    /// MOZA Racing USB Product IDs (VID 0x346E). Canonical inventory:
    /// <c>docs/protocol/devices/usb-ids.md</c>. PID strings = "0x" + 4 hex upper.
    /// </summary>
    public static class MozaUsbIds
    {
        public const string PidWheelbaseR9     = "0x0006";
        public const string PidWheelbaseR12    = "0x0002";
        public const string PidAb9Shifter      = "0x1000";
        public const string PidWheelbaseR16R21 = "0x0000";
        public const string PidPedalsCrp       = "0x0001";
        public const string PidPedalsSrp       = "0x0003";
        public const string PidMBoosterPedals  = "0x0008";
        public const string PidWheelbaseR5     = "0x0004";
        public const string PidWheelbaseR3     = "0x0005";
        public const string PidShifterHgp      = "0x001E";
        public const string PidHandbrakeHbp    = "0x001F";
        public const string PidHub             = "0x0020";
        public const string PidDashboardCm2    = "0x0025";

        // Single source of truth. ushort key keeps lookups allocation-
        // free on the cache-refresh hot path in MozaPortDiscovery.
        // Friendly names match docs/protocol/devices/usb-ids.md — keep
        // them in sync.
        private static readonly IReadOnlyDictionary<ushort, InventoryEntry> Inventory =
            new Dictionary<ushort, InventoryEntry>
            {
                [0x0000] = new InventoryEntry(MozaDeviceCategory.Wheelbase, "R16 / R21"),
                [0x0001] = new InventoryEntry(MozaDeviceCategory.Pedals,    "CRP / CRP2"),
                [0x0002] = new InventoryEntry(MozaDeviceCategory.Wheelbase, "R9"),
                [0x0003] = new InventoryEntry(MozaDeviceCategory.Pedals,    "SRP"),
                [0x0004] = new InventoryEntry(MozaDeviceCategory.Wheelbase, "R5"),
                [0x0005] = new InventoryEntry(MozaDeviceCategory.Wheelbase, "R3 (unconfirmed)"),
                [0x0006] = new InventoryEntry(MozaDeviceCategory.Wheelbase, "R12 / R12v2"),
                [0x0008] = new InventoryEntry(MozaDeviceCategory.MBooster,  "mBooster Pedals"),
                [0x001E] = new InventoryEntry(MozaDeviceCategory.Shifter,   "HGP shifter (unconfirmed)"),
                [0x001F] = new InventoryEntry(MozaDeviceCategory.Handbrake, "HBP handbrake (unconfirmed)"),
                [0x0020] = new InventoryEntry(MozaDeviceCategory.Hub,       "Universal HUB"),
                [0x0025] = new InventoryEntry(MozaDeviceCategory.Dashboard, "CM2 Racing Dash"),
                [0x1000] = new InventoryEntry(MozaDeviceCategory.Ab9,       "AB9 active shifter"),
            };

        // -------------------------------------------------------------
        // Category lookup
        // -------------------------------------------------------------

        public static MozaDeviceCategory Categorize(ushort pid)
        {
            return Inventory.TryGetValue(pid, out var entry) ? entry.Category : MozaDeviceCategory.Unknown;
        }

        public static MozaDeviceCategory Categorize(string? pid)
        {
            return TryParsePid(pid, out var u) ? Categorize(u) : MozaDeviceCategory.Unknown;
        }

        /// <summary>Friendly device name for log lines / UI. Returns "Unknown Moza device" for any PID not in the inventory.</summary>
        public static string Describe(ushort pid)
        {
            return Inventory.TryGetValue(pid, out var entry) ? entry.Description : "Unknown Moza device";
        }

        public static string Describe(string? pid)
        {
            return TryParsePid(pid, out var u) ? Describe(u) : "Unknown Moza device";
        }

        // -------------------------------------------------------------
        // Per-category predicates. The string overloads keep existing
        // call sites compiling unchanged; the ushort overloads avoid a
        // parse on hot paths (e.g. PortInfo.Pid is already ushort).
        // -------------------------------------------------------------

        public static bool IsAb9Pid(string? pid)       => Categorize(pid) == MozaDeviceCategory.Ab9;
        public static bool IsAb9Pid(ushort pid)        => Categorize(pid) == MozaDeviceCategory.Ab9;
        public static bool IsWheelbasePid(string? pid) => Categorize(pid) == MozaDeviceCategory.Wheelbase;
        public static bool IsWheelbasePid(ushort pid)  => Categorize(pid) == MozaDeviceCategory.Wheelbase;
        public static bool IsPedalsPid(string? pid)    => Categorize(pid) == MozaDeviceCategory.Pedals;
        public static bool IsPedalsPid(ushort pid)     => Categorize(pid) == MozaDeviceCategory.Pedals;
        public static bool IsShifterPid(string? pid)   => Categorize(pid) == MozaDeviceCategory.Shifter;
        public static bool IsShifterPid(ushort pid)    => Categorize(pid) == MozaDeviceCategory.Shifter;
        public static bool IsHandbrakePid(string? pid) => Categorize(pid) == MozaDeviceCategory.Handbrake;
        public static bool IsHandbrakePid(ushort pid)  => Categorize(pid) == MozaDeviceCategory.Handbrake;
        public static bool IsHubPid(string? pid)       => Categorize(pid) == MozaDeviceCategory.Hub;
        public static bool IsHubPid(ushort pid)        => Categorize(pid) == MozaDeviceCategory.Hub;
        public static bool IsDashboardPid(string? pid) => Categorize(pid) == MozaDeviceCategory.Dashboard;
        public static bool IsDashboardPid(ushort pid)  => Categorize(pid) == MozaDeviceCategory.Dashboard;
        public static bool IsMBoosterPid(string? pid)  => Categorize(pid) == MozaDeviceCategory.MBooster;
        public static bool IsMBoosterPid(ushort pid)   => Categorize(pid) == MozaDeviceCategory.MBooster;

        /// <summary>True iff the PID is registered in the inventory. Use this to gate "unknown PID" fallback paths.</summary>
        public static bool IsKnownMozaPid(string? pid)
        {
            return TryParsePid(pid, out var u) && Inventory.ContainsKey(u);
        }

        public static bool IsKnownMozaPid(ushort pid)
        {
            return Inventory.ContainsKey(pid);
        }

        // -------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------

        // Accept "0xNNNN" (the canonical form produced by
        // MozaSerialConnection.FormatPid) and bare 4-hex strings, with
        // case-insensitive comparison. Anything else is treated as an
        // unparseable PID (caller decides the fallback).
        private static bool TryParsePid(string? pid, out ushort value)
        {
            value = 0;
            if (string.IsNullOrEmpty(pid)) return false;
            var s = pid!;
            if (s.Length >= 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
                s = s.Substring(2);
            return ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private readonly struct InventoryEntry
        {
            public readonly MozaDeviceCategory Category;
            public readonly string Description;

            public InventoryEntry(MozaDeviceCategory category, string description)
            {
                Category = category;
                Description = description;
            }
        }
    }
}
