using System;
using System.Linq;
using System.Threading;
using HidSharp;

// Usage: wine wine-hid-enum.exe [vidHex]   (default 346e; "all" lists every device)
internal static class Program
{
    private static int Main(string[] args)
    {
        string filter = args.Length > 0 ? args[0].ToLowerInvariant() : "346e";
        bool all = filter == "all";
        int vidFilter = all ? -1 : Convert.ToInt32(filter, 16);

        var devices = DeviceList.Local.GetHidDevices().ToList();
        Console.WriteLine($"HidSharp enumerated {devices.Count} HID device(s)");

        foreach (var dev in devices)
        {
            if (!all && dev.VendorID != vidFilter) continue;
            Console.WriteLine();
            Console.WriteLine($"VID {dev.VendorID:X4} PID {dev.ProductID:X4}  name='{Try(() => dev.GetFriendlyName())}'");
            Console.WriteLine($"  path   {Try(() => dev.DevicePath)}");
            Console.WriteLine($"  serial '{Try(() => dev.GetSerialNumber())}'  maxIn={Try(() => dev.GetMaxInputReportLength().ToString())}");

            try
            {
                var desc = dev.GetReportDescriptor();
                Console.WriteLine($"  descriptor OK: {desc.DeviceItems.Count} item(s), maxInput={desc.MaxInputReportLength}");
                foreach (var item in desc.DeviceItems)
                {
                    var usages = item.InputReports.SelectMany(r => r.DataItems)
                        .SelectMany(d => d.Usages.GetAllValues()).Distinct().Take(12)
                        .Select(u => $"0x{u:X8}");
                    Console.WriteLine($"    item usages: {string.Join(", ", usages)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  descriptor FAILED: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                var raw = dev.GetRawReportDescriptor();
                Console.WriteLine($"  raw descriptor {raw.Length} B: {BitConverter.ToString(raw.Take(48).ToArray())}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  raw descriptor FAILED: {ex.GetType().Name}: {ex.Message}");
            }

            if (!dev.TryOpen(out HidStream stream))
            {
                Console.WriteLine("  open FAILED (TryOpen=false)");
                continue;
            }
            ProbeLayout(dev, stream);
            using (stream)
            {
                stream.ReadTimeout = 500;
                int n = 0;
                var until = DateTime.UtcNow.AddSeconds(2);
                var buf = new byte[Math.Max(1, dev.GetMaxInputReportLength())];
                while (DateTime.UtcNow < until)
                {
                    try { int r = stream.Read(buf, 0, buf.Length); if (r > 0) n++; }
                    catch (TimeoutException) { }
                    catch (Exception ex) { Console.WriteLine($"  read FAILED: {ex.GetType().Name}: {ex.Message}"); break; }
                }
                Console.WriteLine($"  open OK, {n} report(s) in 2 s");
            }
        }
        return 0;
    }

    // The plugin's HidInputLayout: fields it sees, and 2 s of decoded reports.
    private static void ProbeLayout(HidDevice dev, HidStream stream)
    {
        try
        {
            using (var layout = MozaPlugin.Protocol.HidInputLayout.Open(dev.DevicePath ?? ""))
            {
                Console.WriteLine($"  layout: inputLength={layout.InputReportLength} values={layout.Values.Count} buttonRuns={layout.Buttons.Count}");
                foreach (var v in layout.Values)
                    Console.WriteLine($"    value 0x{v.FullUsage:X8} id={v.ReportId} link={v.LinkCollection} bits={v.BitSize} logical {v.LogicalMin}..{v.LogicalMax}");
                foreach (var b in layout.Buttons)
                    Console.WriteLine($"    buttons page {b.UsagePage:X4} usage {b.UsageMin:X2}-{b.UsageMax:X2} id={b.ReportId} link={b.LinkCollection}");

                var buf = new byte[layout.InputReportLength];
                var pressed = new ushort[Math.Max(1, layout.MaxPressed(0x0009))];
                var min = new int[layout.Values.Count];
                var max = new int[layout.Values.Count];
                for (int i = 0; i < min.Length; i++) { min[i] = int.MaxValue; max[i] = int.MinValue; }
                int reports = 0, fails = 0, maxPressed = 0;
                stream.ReadTimeout = 500;
                var until = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < until)
                {
                    int n;
                    try { n = stream.Read(buf, 0, buf.Length); } catch (TimeoutException) { continue; }
                    reports++;
                    for (int i = 0; i < layout.Values.Count; i++)
                    {
                        var f = layout.Values[i];
                        if (f.ReportId != buf[0]) continue;
                        if (!layout.TryGetValue(f, buf, out int v)) { fails++; continue; }
                        min[i] = Math.Min(min[i], v); max[i] = Math.Max(max[i], v);
                    }
                    foreach (var b in layout.Buttons)
                    {
                        if (b.UsagePage != 0x0009 || b.ReportId != buf[0]) continue;
                        maxPressed = Math.Max(maxPressed, layout.GetPressed(b, buf, pressed));
                    }
                }
                Console.WriteLine($"  decoded {reports} report(s), {fails} value failure(s), max buttons pressed {maxPressed} (move wheel/pedals/buttons during the run)");
                for (int i = 0; i < layout.Values.Count; i++)
                    if (min[i] != int.MaxValue)
                        Console.WriteLine($"    0x{layout.Values[i].FullUsage:X8} seen {min[i]}..{max[i]}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  layout FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Try(Func<string?> f)
    {
        try { return f() ?? "(null)"; }
        catch (Exception ex) { return $"<{ex.GetType().Name}: {ex.Message}>"; }
    }
}
