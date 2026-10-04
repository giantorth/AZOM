using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MozaPlugin.Protocol
{
    /// <summary>One input value (axis) as described by <c>HidP_GetValueCaps</c>.</summary>
    internal readonly struct HidValueField
    {
        public readonly ushort UsagePage;
        public readonly ushort Usage;
        public readonly byte ReportId;
        public readonly ushort LinkCollection;
        public readonly ushort BitSize;
        public readonly int LogicalMin;
        public readonly int LogicalMax;

        public HidValueField(ushort page, ushort usage, byte reportId, ushort link, ushort bitSize, int min, int max)
        {
            UsagePage = page;
            Usage = usage;
            ReportId = reportId;
            LinkCollection = link;
            BitSize = bitSize;
            LogicalMin = min;
            LogicalMax = max;
        }

        /// <summary>Full usage id: page &lt;&lt; 16 | usage.</summary>
        public uint FullUsage => ((uint)UsagePage << 16) | Usage;
    }

    /// <summary>One run of buttons as described by <c>HidP_GetButtonCaps</c>.</summary>
    internal readonly struct HidButtonField
    {
        public readonly ushort UsagePage;
        public readonly ushort UsageMin;
        public readonly ushort UsageMax;
        public readonly byte ReportId;
        public readonly ushort LinkCollection;

        public HidButtonField(ushort page, ushort min, ushort max, byte reportId, ushort link)
        {
            UsagePage = page;
            UsageMin = min;
            UsageMax = max;
            ReportId = reportId;
            LinkCollection = link;
        }
    }

    /// <summary>
    /// Input-report layout of one HID top-level collection, read from the OS
    /// HID parser (<c>hid.dll</c> preparsed data) and used to decode raw input
    /// reports. This is the platform's own parser, so it behaves the same on
    /// Windows and Wine — unlike HidSharp's descriptor reconstruction, which
    /// fails on some Wine-exposed devices ("Unable to reconstruct the report
    /// descriptor"). Reports are the raw bytes from <c>HidStream.Read</c>
    /// (report ID first, 0 when the device has none).
    /// </summary>
    internal sealed class HidInputLayout : IDisposable
    {
        private const int HidP_Input = 0;
        private const int HIDP_STATUS_SUCCESS = 0x00110000;
        // sizeof(HIDP_VALUE_CAPS) == sizeof(HIDP_BUTTON_CAPS) == 72; HIDP_CAPS == 64.
        private const int CapsStructSize = 72;
        private const int HidpCapsSize = 64;

        private const uint OPEN_EXISTING = 3;
        private const uint FILE_SHARE_READ_WRITE = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa,
                                                        uint disposition, uint flags, IntPtr template);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);

        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsed, byte[] caps);

        [DllImport("hid.dll")]
        private static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort length, IntPtr preparsed);

        [DllImport("hid.dll")]
        private static extern int HidP_GetButtonCaps(int type, byte[] caps, ref ushort length, IntPtr preparsed);

        [DllImport("hid.dll")]
        private static extern uint HidP_MaxUsageListLength(int type, ushort usagePage, IntPtr preparsed);

        [DllImport("hid.dll")]
        private static extern int HidP_GetUsageValue(int type, ushort usagePage, ushort linkCollection, ushort usage,
                                                     out uint value, IntPtr preparsed, byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        private static extern int HidP_GetUsages(int type, ushort usagePage, ushort linkCollection, ushort[] usages,
                                                 ref uint usageLength, IntPtr preparsed, byte[] report, uint reportLength);

        private IntPtr _preparsed;

        /// <summary>Input report length in bytes, report ID included.</summary>
        public int InputReportLength { get; }
        public IReadOnlyList<HidValueField> Values { get; }
        public IReadOnlyList<HidButtonField> Buttons { get; }

        private HidInputLayout(IntPtr preparsed, int inputLength, List<HidValueField> values, List<HidButtonField> buttons)
        {
            _preparsed = preparsed;
            InputReportLength = inputLength;
            Values = values;
            Buttons = buttons;
        }

        /// <summary>Reads the layout for a HID device path. Throws with the failing call on error.</summary>
        public static HidInputLayout Open(string devicePath)
        {
            IntPtr preparsed;
            // Zero access: HidD_* metadata calls need no read/write rights, and
            // this never contends with the HidStream that does the reading.
            using (var handle = CreateFile(devicePath, 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile on HID device failed");
                if (!HidD_GetPreparsedData(handle, out preparsed))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_GetPreparsedData failed");
            }

            try
            {
                var caps = new byte[HidpCapsSize];
                Check(HidP_GetCaps(preparsed, caps), "HidP_GetCaps");
                // HIDP_CAPS: Usage, UsagePage, InputReportByteLength, ..., USHORT Reserved[17],
                // NumberLinkCollectionNodes (44), NumberInputButtonCaps (46), NumberInputValueCaps (48).
                int inputLength = BitConverter.ToUInt16(caps, 4);
                ushort buttonCount = BitConverter.ToUInt16(caps, 46);
                ushort valueCount = BitConverter.ToUInt16(caps, 48);

                var values = new List<HidValueField>();
                if (valueCount > 0)
                {
                    var raw = new byte[valueCount * CapsStructSize];
                    Check(HidP_GetValueCaps(HidP_Input, raw, ref valueCount, preparsed), "HidP_GetValueCaps");
                    for (int i = 0; i < valueCount; i++)
                        AddValueCaps(raw, i * CapsStructSize, values);
                }

                var buttons = new List<HidButtonField>();
                if (buttonCount > 0)
                {
                    var raw = new byte[buttonCount * CapsStructSize];
                    Check(HidP_GetButtonCaps(HidP_Input, raw, ref buttonCount, preparsed), "HidP_GetButtonCaps");
                    for (int i = 0; i < buttonCount; i++)
                    {
                        int o = i * CapsStructSize;
                        bool isRange = raw[o + 12] != 0;
                        ushort min = BitConverter.ToUInt16(raw, o + 56);
                        ushort max = isRange ? BitConverter.ToUInt16(raw, o + 58) : min;
                        buttons.Add(new HidButtonField(
                            BitConverter.ToUInt16(raw, o), min, max, raw[o + 2], BitConverter.ToUInt16(raw, o + 6)));
                    }
                }

                return new HidInputLayout(preparsed, inputLength, values, buttons);
            }
            catch
            {
                HidD_FreePreparsedData(preparsed);
                throw;
            }
        }

        // HIDP_VALUE_CAPS: UsagePage(0) ReportID(2) LinkCollection(6) IsRange(12)
        // BitSize(18) ReportCount(20) LogicalMin(40) LogicalMax(44) Range/NotRange.Usage(56/58).
        private static void AddValueCaps(byte[] raw, int o, List<HidValueField> values)
        {
            // ReportCount > 1 is a usage-value array (HidP_GetUsageValueArray),
            // never one of the axes we read.
            if (BitConverter.ToUInt16(raw, o + 20) != 1) return;
            ushort page = BitConverter.ToUInt16(raw, o);
            byte reportId = raw[o + 2];
            ushort link = BitConverter.ToUInt16(raw, o + 6);
            ushort bits = BitConverter.ToUInt16(raw, o + 18);
            int min = BitConverter.ToInt32(raw, o + 40);
            int max = BitConverter.ToInt32(raw, o + 44);
            // A descriptor that encodes an unsigned maximum in too few bytes
            // reads back negative (e.g. 0..-1 for a 16-bit axis); the field is unsigned.
            if (max < min && min >= 0 && bits > 0 && bits < 32) max = (int)((1L << bits) - 1);
            bool isRange = raw[o + 12] != 0;
            ushort first = BitConverter.ToUInt16(raw, o + 56);
            ushort last = isRange ? BitConverter.ToUInt16(raw, o + 58) : first;
            for (int u = first; u <= last; u++)
                values.Add(new HidValueField(page, (ushort)u, reportId, link, bits, min, max));
        }

        private static void Check(int status, string call)
        {
            if (status != HIDP_STATUS_SUCCESS)
                throw new InvalidOperationException($"{call} returned 0x{status:X8}");
        }

        /// <summary>Logical value of <paramref name="field"/> in <paramref name="report"/>,
        /// sign-extended when the field's logical range is signed.</summary>
        public bool TryGetValue(in HidValueField field, byte[] report, out int value)
        {
            value = 0;
            if (_preparsed == IntPtr.Zero) return false;
            if (HidP_GetUsageValue(HidP_Input, field.UsagePage, field.LinkCollection, field.Usage,
                                   out uint raw, _preparsed, report, (uint)InputReportLength) != HIDP_STATUS_SUCCESS)
                return false;
            if (field.LogicalMin < 0 && field.BitSize > 0 && field.BitSize < 32)
            {
                int shift = 32 - field.BitSize;
                value = ((int)(raw << shift)) >> shift;
            }
            else
            {
                value = unchecked((int)raw);
            }
            return true;
        }

        /// <summary>Usage ids pressed on <paramref name="field"/>'s page/collection;
        /// returns the count written to <paramref name="pressed"/>, or -1 when this
        /// report doesn't carry that button run.</summary>
        public int GetPressed(in HidButtonField field, byte[] report, ushort[] pressed)
        {
            if (_preparsed == IntPtr.Zero) return -1;
            uint count = (uint)pressed.Length;
            if (HidP_GetUsages(HidP_Input, field.UsagePage, field.LinkCollection, pressed, ref count,
                               _preparsed, report, (uint)InputReportLength) != HIDP_STATUS_SUCCESS)
                return -1;
            return (int)count;
        }

        /// <summary>Upper bound on usages <see cref="GetPressed"/> can return for a page.</summary>
        public int MaxPressed(ushort usagePage)
        {
            if (_preparsed == IntPtr.Zero) return 0;
            return (int)HidP_MaxUsageListLength(HidP_Input, usagePage, _preparsed);
        }

        public void Dispose()
        {
            var p = _preparsed;
            _preparsed = IntPtr.Zero;
            if (p != IntPtr.Zero) HidD_FreePreparsedData(p);
        }
    }
}
