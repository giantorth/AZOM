using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MozaPlugin.Protocol
{
    internal enum LinuxNodeKind { Tty, Hidraw }

    /// <summary>One MOZA device node and whether the Linux user can open it.</summary>
    internal readonly struct LinuxNodeAccess
    {
        public readonly LinuxNodeKind Kind;
        /// <summary>Kernel node name, e.g. "ttyACM0" / "hidraw9".</summary>
        public readonly string Node;
        public readonly ushort Pid;
        /// <summary>USB product name, e.g. "MOZA R5 Base"; "" when unknown.</summary>
        public readonly string Name;
        /// <summary>Read+write allowed on the last check; null when unchecked.</summary>
        public readonly bool? Access;
        /// <summary>
        /// No access on this AND the previous check. A freshly created node is
        /// root-only until udev applies its ACL, so one denial is not a verdict.
        /// </summary>
        public readonly bool Denied;

        public LinuxNodeAccess(LinuxNodeKind kind, string node, ushort pid, string name, bool? access, bool denied)
        {
            Kind = kind;
            Node = node ?? string.Empty;
            Pid = pid;
            Name = name ?? string.Empty;
            Access = access;
            Denied = denied;
        }

        public string DevPath => "/dev/" + Node;
    }

    internal sealed class LinuxDeviceAccessSnapshot
    {
        public static readonly LinuxDeviceAccessSnapshot Empty =
            new LinuxDeviceAccessSnapshot(Array.Empty<LinuxNodeAccess>(), DateTime.MinValue, "not checked yet");

        public IReadOnlyList<LinuxNodeAccess> Nodes { get; }
        public DateTime CheckedUtc { get; }
        /// <summary>Why the last check could not decide; "" when it ran.</summary>
        public string Error { get; }

        public LinuxDeviceAccessSnapshot(IReadOnlyList<LinuxNodeAccess> nodes, DateTime checkedUtc, string error)
        {
            Nodes = nodes;
            CheckedUtc = checkedUtc;
            Error = error ?? string.Empty;
        }

        public bool IsTtyDenied(string tty) =>
            !string.IsNullOrEmpty(tty)
            && Nodes.Any(n => n.Kind == LinuxNodeKind.Tty && n.Denied
                              && string.Equals(n.Node, tty, StringComparison.Ordinal));
    }

    /// <summary>
    /// Wine/Linux: checks that the Linux user can read+write every MOZA tty and
    /// hidraw node. Without it, a tty open fails as access-denied (looks like
    /// another app holds the port) and winebus silently drops the hidraw device
    /// (no steering / pedal / button input) — both are missing udev rules, not
    /// something the plugin or Wine can work around.
    ///
    /// <para>The check is <c>[ -r ] &amp;&amp; [ -w ]</c> in a native shell
    /// (<see cref="WineNativeExec"/>): access(2) honours the uaccess ACL and
    /// opens nothing, so it never toggles DTR on a tty. Wine cannot answer this
    /// itself — its file attributes don't reflect the caller's permissions.</para>
    ///
    /// <para>Non-blocking: <see cref="Current"/> returns the last result and
    /// starts a background re-check once it is older than the refresh interval.</para>
    /// </summary>
    internal static class LinuxDeviceAccess
    {
        private const int RefreshIntervalMs = 5000;
        private const int SpawnTimeoutMs = 3000;

        // Exit 0 = read+write allowed, 1 = denied. The node is $1, never parsed.
        private const string AccessCheck = "[ -r \"$1\" ] && [ -w \"$1\" ]";

        private static volatile LinuxDeviceAccessSnapshot s_current = LinuxDeviceAccessSnapshot.Empty;
        private static long s_lastStartTicks;
        private static int s_inFlight;

        /// <summary>True when the check can run at all (Wine on Linux with native exec).</summary>
        public static bool Supported =>
            WineHost.IsWine && WineHost.UnixRoot != null && WineNativeExec.Available;

        /// <summary>Last result; kicks a background refresh when stale.</summary>
        public static LinuxDeviceAccessSnapshot Current
        {
            get
            {
                KickRefresh();
                return s_current;
            }
        }

        private static void KickRefresh()
        {
            if (!Supported) return;
            long now = Environment.TickCount;
            long last = Interlocked.Read(ref s_lastStartTicks);
            if (last != 0 && unchecked((int)(now - last)) < RefreshIntervalMs) return;
            if (Interlocked.CompareExchange(ref s_inFlight, 1, 0) != 0) return;
            Interlocked.Exchange(ref s_lastStartTicks, now == 0 ? 1 : now);
            try
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { s_current = Check(s_current); }
                    catch (Exception ex)
                    {
                        MozaLog.DebugIfChanged("linux-access-err",
                            $"[AZOM] Linux device access check failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally { Interlocked.Exchange(ref s_inFlight, 0); }
                });
            }
            catch
            {
                Interlocked.Exchange(ref s_inFlight, 0);
            }
        }

        private static LinuxDeviceAccessSnapshot Check(LinuxDeviceAccessSnapshot previous)
        {
            var found = new List<(LinuxNodeKind Kind, string Node, ushort Pid, string Name)>();
            foreach (var t in LinuxUsbEnumerator.Enumerate())
                if (t.TtyName.Length > 0)
                    found.Add((LinuxNodeKind.Tty, t.TtyName, t.Pid, t.Product));
            foreach (var h in LinuxUsbEnumerator.EnumerateMozaHidrawNodes())
                found.Add((LinuxNodeKind.Hidraw, h.Node, h.Pid, h.Name));

            var access = new bool?[found.Count];
            string error = string.Empty;
            for (int i = 0; i < found.Count; i++)
            {
                string node = "/dev/" + found[i].Node;
                var r = WineNativeExec.Run(new[] { "sh", "-c", AccessCheck, "sh", node }, SpawnTimeoutMs);
                // Anything but 0/1 (spawn NTSTATUS, sh 126/127, timeout) is no answer.
                if (r.Outcome == NativeSpawnOutcome.Completed && (r.Status == 0 || r.Status == 1))
                    access[i] = r.Status == 0;
                else
                    error = $"check of {node} did not run (outcome={r.Outcome} status=0x{r.Status:X})";
            }

            var nodes = new List<LinuxNodeAccess>(found.Count);
            for (int i = 0; i < found.Count; i++)
            {
                var f = found[i];
                bool wasDenied = previous.Nodes.Any(p => p.Kind == f.Kind && p.Access == false
                                                         && string.Equals(p.Node, f.Node, StringComparison.Ordinal));
                bool denied = access[i] == false && wasDenied;
                nodes.Add(new LinuxNodeAccess(f.Kind, f.Node, f.Pid, f.Name, access[i], denied));
            }

            var snap = new LinuxDeviceAccessSnapshot(nodes, DateTime.UtcNow, error);
            MozaLog.DebugIfChanged("linux-access", Describe(snap, includeAge: false),
                $"[AZOM] Linux device access: {Describe(snap, includeAge: false)}");
            return snap;
        }

        /// <summary>One-line summary for the log and the diagnostics dump.</summary>
        public static string Describe(LinuxDeviceAccessSnapshot snap, bool includeAge = true)
        {
            if (snap.CheckedUtc == DateTime.MinValue) return "(" + snap.Error + ")";
            string list = snap.Nodes.Count == 0
                ? "(no MOZA tty/hidraw nodes)"
                : string.Join(", ", snap.Nodes.Select(n =>
                    $"{n.DevPath} {(n.Access == null ? "unchecked" : n.Access == true ? "rw" : n.Denied ? "DENIED" : "denied-once")}"));
            if (snap.Error.Length > 0) list += "  [" + snap.Error + "]";
            if (includeAge)
                list += $"  (checked {(int)(DateTime.UtcNow - snap.CheckedUtc).TotalSeconds}s ago)";
            return list;
        }
    }
}
