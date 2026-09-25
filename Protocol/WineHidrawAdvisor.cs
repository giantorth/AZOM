using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace MozaPlugin.Protocol
{
    internal enum WineHidrawState
    {
        /// <summary>Not Wine-on-Linux, or nothing to judge yet.</summary>
        NotApplicable,
        /// <summary>Every MOZA hidraw device the reader consumes is open.</summary>
        Healthy,
        /// <summary>A dead device has no <c>EnableHidraw</c> entry — fixable.</summary>
        MissingEntries,
        /// <summary>Entries were written this session; wineserver must restart.</summary>
        PendingRestart,
        /// <summary>Dead device already has an entry — cause unknown, diagnostics only.</summary>
        Unresolved,
    }

    internal sealed class WineHidrawStatus
    {
        public static readonly WineHidrawStatus None = new WineHidrawStatus(
            WineHidrawState.NotApplicable, new Dictionary<ushort, string>(), Array.Empty<ushort>(),
            Array.Empty<ushort>(), Array.Empty<ushort>(), "");

        public WineHidrawState State { get; }
        /// <summary>Consumed MOZA hidraw devices: PID → kernel HID name.</summary>
        public IReadOnlyDictionary<ushort, string> Hidraw { get; }
        public IReadOnlyList<ushort> OpenPids { get; }
        public IReadOnlyList<ushort> DeadPids { get; }
        public IReadOnlyList<ushort> MissingPids { get; }
        public string Winebus { get; }

        public WineHidrawStatus(WineHidrawState state, IReadOnlyDictionary<ushort, string> hidraw, IReadOnlyList<ushort> open,
                                IReadOnlyList<ushort> dead, IReadOnlyList<ushort> missing, string winebus)
        {
            State = state;
            Hidraw = hidraw;
            OpenPids = open;
            DeadPids = dead;
            MissingPids = missing;
            Winebus = winebus;
        }

        public string DeviceName(ushort pid) =>
            Hidraw.TryGetValue(pid, out var n) && n.Length > 0 ? n : WineHidrawAdvisor.Entry(pid);
    }

    /// <summary>
    /// Wine/Linux: detects MOZA devices the kernel exposes as hidraw that the
    /// HID reader never opened, and fixes the usual cause — winebus hands the
    /// device to SDL unless its exact <c>vid:pid</c> is listed in
    /// <c>HKLM\System\CurrentControlSet\Services\winebus\EnableHidraw</c>.
    /// winebus reads that list only when the wineserver starts, so a fix needs
    /// every program in the prefix closed before SimHub is started again.
    /// </summary>
    internal static class WineHidrawAdvisor
    {
        private const string WinebusKey = @"System\CurrentControlSet\Services\winebus";
        private const string EnableHidrawValue = "EnableHidraw";

        // HID reader's first enumeration + a hot-plug scan must have had time to run.
        private static readonly TimeSpan StartupSettling = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan DeadDebounce = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(5);

        private static readonly object s_gate = new object();
        private static WineHidrawStatus s_last = WineHidrawStatus.None;
        private static DateTime s_lastEvalUtc = DateTime.MinValue;
        private static DateTime? s_deadSinceUtc;
        private static bool s_wroteThisSession;
        private static WineHidrawState s_loggedState = WineHidrawState.NotApplicable;

        /// <summary>Error from the last failed fix attempt, "" otherwise.</summary>
        public static string LastFixError { get; private set; } = "";

        public static WineHidrawStatus Evaluate(MozaHidReader? reader, DateTime startupUtc, DateTime nowUtc)
        {
            if (!WineHost.IsWine || WineHost.UnixRoot == null || reader == null)
                return WineHidrawStatus.None;

            lock (s_gate)
            {
                if (nowUtc - s_lastEvalUtc < Recheck) return s_last;
                s_lastEvalUtc = nowUtc;
                s_last = Compute(reader, startupUtc, nowUtc);
                if (s_last.State != s_loggedState)
                {
                    s_loggedState = s_last.State;
                    if (s_last.State != WineHidrawState.Healthy)
                        MozaLog.Info($"[AZOM] Wine HID: {s_last.State} dead=[{Join(s_last.DeadPids)}] " +
                                     $"missing=[{Join(s_last.MissingPids)}] winebus: {s_last.Winebus}");
                }
                return s_last;
            }
        }

        private static WineHidrawStatus Compute(MozaHidReader reader, DateTime startupUtc, DateTime nowUtc)
        {
            var hidraw = LinuxUsbEnumerator.EnumerateMozaHidraw()
                .Where(kv => MozaHidReader.ReadsPid(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var open = reader.OpenPidsSnapshot().OrderBy(p => p).ToList();
            var dead = hidraw.Keys.Where(p => !open.Contains(p)).OrderBy(p => p).ToList();
            string winebus = DescribeWinebus();

            if (dead.Count == 0)
            {
                s_deadSinceUtc = null;
                return new WineHidrawStatus(WineHidrawState.Healthy, hidraw, open, dead, Array.Empty<ushort>(), winebus);
            }

            if (s_deadSinceUtc == null) s_deadSinceUtc = nowUtc;
            if (nowUtc - startupUtc < StartupSettling || nowUtc - s_deadSinceUtc.Value < DeadDebounce)
                return new WineHidrawStatus(WineHidrawState.NotApplicable, hidraw, open, dead, Array.Empty<ushort>(), winebus);

            var entries = ReadEnableHidraw();
            var missing = dead.Where(p => !entries.Contains(Entry(p), StringComparer.OrdinalIgnoreCase)).ToList();
            var state = missing.Count > 0 ? WineHidrawState.MissingEntries
                      : s_wroteThisSession ? WineHidrawState.PendingRestart
                      : WineHidrawState.Unresolved;
            return new WineHidrawStatus(state, hidraw, open, dead, missing, winebus);
        }

        /// <summary>
        /// Appends the missing <c>346e:pppp</c> entries to <c>EnableHidraw</c>,
        /// keeping every existing entry, and verifies by reading back.
        /// </summary>
        public static bool TryApplyFix(IReadOnlyList<ushort> pids)
        {
            lock (s_gate)
            {
                try
                {
                    using (var key = Registry.LocalMachine.CreateSubKey(WinebusKey, writable: true))
                    {
                        if (key == null) throw new InvalidOperationException("winebus key unavailable");
                        var list = ReadEntries(key);
                        foreach (var pid in pids)
                            if (!list.Contains(Entry(pid), StringComparer.OrdinalIgnoreCase))
                                list.Add(Entry(pid));
                        key.SetValue(EnableHidrawValue, list.ToArray(), RegistryValueKind.MultiString);

                        var readBack = ReadEntries(key);
                        var absent = pids.Where(p => !readBack.Contains(Entry(p), StringComparer.OrdinalIgnoreCase)).ToList();
                        if (absent.Count > 0)
                            throw new InvalidOperationException($"entries missing after write: {Join(absent)}");
                        MozaLog.Info($"[AZOM] Wine HID: EnableHidraw now [{string.Join(", ", readBack)}]");
                    }
                    s_wroteThisSession = true;
                    LastFixError = "";
                    return true;
                }
                catch (Exception ex)
                {
                    LastFixError = ex.Message;
                    MozaLog.Warn($"[AZOM] Wine HID: EnableHidraw write failed: {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
                finally
                {
                    s_lastEvalUtc = DateTime.MinValue; // re-evaluate on the next tick
                }
            }
        }

        public static string Entry(ushort pid) => $"346e:{pid:x4}";

        private static string Join(IEnumerable<ushort> pids) =>
            string.Join(",", pids.Select(p => p.ToString("X4", CultureInfo.InvariantCulture)));

        private static List<string> ReadEnableHidraw()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(WinebusKey))
                    return key == null ? new List<string>() : ReadEntries(key);
            }
            catch { return new List<string>(); }
        }

        private static List<string> ReadEntries(RegistryKey key)
        {
            switch (key.GetValue(EnableHidrawValue))
            {
                case string[] multi:
                    return multi.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                case string single when single.Trim().Length > 0:
                    return new List<string> { single.Trim() };
                default:
                    return new List<string>();
            }
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
                        var v = key.GetValue(name);
                        return v == null ? "—" : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "—";
                    }
                    return $"Enable SDL={V("Enable SDL")} Map Controllers={V("Map Controllers")} " +
                           $"DisableInput={V("DisableInput")} DisableHidraw={V("DisableHidraw")} " +
                           $"EnableHidraw=[{string.Join(", ", ReadEntries(key))}]";
                }
            }
            catch (Exception ex) { return $"(unreadable: {ex.GetType().Name})"; }
        }
    }
}
