using System.Reflection;
using MozaPlugin;

int checks = 0;
void Check(bool value) { if (!value) throw new Exception("LED forwarding check " + checks); checks++; }
var plugin = new MozaPlugin.MozaPlugin();
var payload = new byte[] { 1, 2, 3 };
Check(!plugin.WriteDashLedBitmask(7));
Check(!plugin.WriteDashRpmColor(2, 1, 2, 3));
Check(!plugin.WriteDashFlagColors(payload));
Check(!plugin.WriteCm2LiveLedColorChunk(payload, 2));
Check(!plugin.WriteCm2LiveLedBitmask(payload));
Check(!plugin.WriteCm2Config("mode", 1));
Check(!plugin.WriteCm2Config("colors", payload));
var sink = plugin._hardwareApplier = new FakeApplier();
void Sent(bool value, string method, params object[] args)
{
    Check(value && sink.Calls == checks - 6 && sink.Method == method && sink.Args.SequenceEqual(args));
}
Sent(plugin.WriteDashLedBitmask(7), "mask", 7);
Sent(plugin.WriteDashRpmColor(2, 1, 2, 3), "color", 2, (byte)1, (byte)2, (byte)3);
Sent(plugin.WriteDashFlagColors(payload), "flags", payload);
Sent(plugin.WriteCm2LiveLedColorChunk(payload, 2), "chunk", payload, 2);
Sent(plugin.WriteCm2LiveLedBitmask(payload), "live-mask", payload);
Sent(plugin.WriteCm2Config("mode", 1), "config-int", "mode", 1);
Sent(plugin.WriteCm2Config("colors", payload), "config-array", "colors", payload);
sink.Result = false;
Check(!plugin.WriteCm2LiveLedBitmask(payload));
Console.WriteLine($"LED compatibility: {checks} passed; no hardware used.");

namespace MozaPlugin
{
    public partial class MozaPlugin { internal FakeApplier _hardwareApplier; }
    internal sealed class FakeApplier
    {
        internal int Calls; internal string Method; internal object[] Args; internal bool Result = true;
        bool Record(string method, params object[] args) { Calls++; Method = method; Args = args; return Result; }
        internal bool WriteDashLedBitmask(int value) => Record("mask", value);
        internal bool WriteDashRpmColor(int index, byte r, byte g, byte b) => Record("color", index, r, g, b);
        internal bool WriteDashFlagColors(byte[] rgb) => Record("flags", rgb);
        internal bool WriteCm2LiveLedColorChunk(byte[] bytes, int index) => Record("chunk", bytes, index);
        internal bool WriteCm2LiveLedBitmask(byte[] bytes) => Record("live-mask", bytes);
        internal bool WriteCm2Config(string command, int value) => Record("config-int", command, value);
        internal bool WriteCm2Config(string command, byte[] bytes) => Record("config-array", command, bytes);
    }
}
