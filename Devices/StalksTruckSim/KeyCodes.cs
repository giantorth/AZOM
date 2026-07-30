using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MozaPlugin.Devices.StalksTruckSim
{
    /// <summary>
    /// Key-string ↔ scan-code translation for truck-sim keyboard output. Settings
    /// store keys as strings: legacy friendly names ("P", "Minus", "[") or a
    /// scan-code literal ("sc:19", "sc:E04B") written by the capture UI. Codes are
    /// Set-1 make codes; extended (E0-prefixed) keys carry 0xE0 in the high byte.
    /// </summary>
    internal static class KeyCodes
    {
        private const string ScPrefix = "sc:";

        /// <summary>Resolve a stored key string to a scan code. Returns 0 if unknown/empty.</summary>
        public static ushort Parse(string? key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            var s = key!.Trim();
            if (s.StartsWith(ScPrefix, StringComparison.OrdinalIgnoreCase))
                return ushort.TryParse(s.Substring(ScPrefix.Length), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var v) ? v : (ushort)0;
            return Names.TryGetValue(s, out var n) ? n : (ushort)0;
        }

        /// <summary>Stored form of a scan code ("" for 0, else an sc: hex literal).</summary>
        public static string Encode(ushort code)
            => code == 0 ? "" : ScPrefix + code.ToString(code > 0xFF ? "X4" : "X2", CultureInfo.InvariantCulture);

        /// <summary>Scan code for a captured virtual key, with the E0 flag applied for
        /// nav-cluster / right-side keys. Returns 0 for unsupported keys.</summary>
        public static ushort FromVirtualKey(int vk)
        {
            if (vk <= 0 || RejectedVks.Contains(vk)) return 0;
            uint scan = MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC);
            if (scan == 0 || scan > 0xFF) return 0;
            return (ushort)(ExtendedVks.Contains(vk) ? 0xE000u | scan : scan);
        }

        /// <summary>Layout-aware display name for a scan code (never persisted).</summary>
        public static string DisplayName(ushort code)
        {
            if (code == 0) return "";
            bool ext = (code & 0xFF00) == 0xE000;
            int lParam = ((code & 0xFF) << 16) | (ext ? 1 << 24 : 0);
            var sb = new StringBuilder(64);
            try
            {
                if (GetKeyNameTextW(lParam, sb, sb.Capacity) > 0) return sb.ToString();
            }
            catch { }
            return Encode(code);
        }

        // Friendly names kept for legacy settings and hand-editing; the capture UI
        // always stores sc: literals. Names are US-positional (Set-1 codes).
        private static readonly Dictionary<string, ushort> Names =
            new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            {"A",0x1E},{"B",0x30},{"C",0x2E},{"D",0x20},{"E",0x12},{"F",0x21},{"G",0x22},
            {"H",0x23},{"I",0x17},{"J",0x24},{"K",0x25},{"L",0x26},{"M",0x32},{"N",0x31},
            {"O",0x18},{"P",0x19},{"Q",0x10},{"R",0x13},{"S",0x1F},{"T",0x14},{"U",0x16},
            {"V",0x2F},{"W",0x11},{"X",0x2D},{"Y",0x15},{"Z",0x2C},
            {"0",0x0B},{"1",0x02},{"2",0x03},{"3",0x04},{"4",0x05},{"5",0x06},
            {"6",0x07},{"7",0x08},{"8",0x09},{"9",0x0A},
            {"Minus",0x0C},{"-",0x0C},{"Equals",0x0D},{"=",0x0D},
            {"Comma",0x33},{",",0x33},{"Period",0x34},{".",0x34},
            {"Slash",0x35},{"/",0x35},{"Semicolon",0x27},{";",0x27},
            {"Apostrophe",0x28},{"'",0x28},{"LeftBracket",0x1A},{"[",0x1A},
            {"RightBracket",0x1B},{"]",0x1B},{"Backslash",0x2B},{"\\",0x2B},
            {"Grave",0x29},{"`",0x29},
            {"Space",0x39},{"Enter",0x1C},{"Tab",0x0F},{"Escape",0x01},{"Esc",0x01},
            {"F1",0x3B},{"F2",0x3C},{"F3",0x3D},{"F4",0x3E},{"F5",0x3F},{"F6",0x40},
            {"F7",0x41},{"F8",0x42},{"F9",0x43},{"F10",0x44},{"F11",0x57},{"F12",0x58},
            {"Backspace",0x0E},{"CapsLock",0x3A},{"ScrollLock",0x46},
            {"LeftShift",0x2A},{"RightShift",0x36},{"LeftCtrl",0x1D},{"LeftAlt",0x38},
            {"Numpad0",0x52},{"Numpad1",0x4F},{"Numpad2",0x50},{"Numpad3",0x51},
            {"Numpad4",0x4B},{"Numpad5",0x4C},{"Numpad6",0x4D},{"Numpad7",0x47},
            {"Numpad8",0x48},{"Numpad9",0x49},
            {"NumpadMultiply",0x37},{"NumpadMinus",0x4A},{"NumpadPlus",0x4E},{"NumpadPeriod",0x53},
            {"Up",0xE048},{"Down",0xE050},{"Left",0xE04B},{"Right",0xE04D},
            {"Home",0xE047},{"End",0xE04F},{"Insert",0xE052},{"Delete",0xE053},
            {"PageUp",0xE049},{"PageDown",0xE051},
            {"NumpadEnter",0xE01C},{"NumpadDivide",0xE035},
            {"RightCtrl",0xE01D},{"RightAlt",0xE038},
        };

        // VKs whose scan codes need the E0 prefix (nav cluster / right-side keys).
        private static readonly HashSet<int> ExtendedVks = new HashSet<int>
        {
            0x21 /*PgUp*/, 0x22 /*PgDn*/, 0x23 /*End*/, 0x24 /*Home*/,
            0x25 /*Left*/, 0x26 /*Up*/, 0x27 /*Right*/, 0x28 /*Down*/,
            0x2D /*Insert*/, 0x2E /*Delete*/, 0x5D /*Apps*/,
            0x6F /*NumpadDivide*/, 0xA3 /*RightCtrl*/, 0xA5 /*RightAlt*/,
        };

        // Win keys, plus keys with quirky scan-code synthesis (Pause/PrintScreen/NumLock).
        private static readonly HashSet<int> RejectedVks = new HashSet<int>
        {
            0x13 /*Pause*/, 0x2C /*PrintScreen*/, 0x90 /*NumLock*/,
            0x5B /*LWin*/, 0x5C /*RWin*/,
        };

        private const uint MAPVK_VK_TO_VSC = 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetKeyNameTextW(int lParam, StringBuilder lpString, int cchSize);
    }
}
