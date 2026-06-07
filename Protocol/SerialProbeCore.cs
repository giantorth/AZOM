using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;

namespace MozaPlugin.Protocol
{
    /// <summary>Which MOZA device a serial probe targets.</summary>
    public enum ProbeKind { Base, Hub, Ab9 }

    /// <summary>
    /// Self-contained serial open+probe core used by <see cref="MozaSerialConnection"/>.
    ///
    /// <para>It keeps the wire constants below as local copies of the
    /// corresponding <c>MozaProtocol.*</c> values so the file stays free of
    /// other MozaPlugin dependencies. They are stable wire constants; keep them
    /// in sync (they have not changed since the protocol was first decoded).</para>
    ///
    /// <para>The one operation that can SEGFAULT Wine — <c>SerialPort.Open</c> on
    /// a not-yet-ready CDC-ACM port (the freshly-powered-base crash) — lives in
    /// <see cref="ProbeOnePort"/>. <see cref="MozaSerialConnection.ProbeWithTimeout"/>
    /// runs it on a throwaway background thread and abandons that thread at the
    /// deadline if <c>Open()</c> wedges, so a hung probe never blocks detection.</para>
    /// </summary>
    public static class SerialProbeCore
    {
        // ── Wire constants — MUST mirror MozaProtocol.* (stable) ──────────────
        public const byte MessageStart  = 0x7E;    // MozaProtocol.MessageStart
        public const int  BaudRate       = 115200; // MozaProtocol.BaudRate
        public const byte MagicValue     = 0x0D;   // MozaProtocol.MagicValue
        public const byte BaseRespGroup  = 0xAB;   // MozaProtocol.BaseRespGroup (0x2B|0x80)
        public const byte HubRespGroup   = 0xE4;   // MozaProtocol.HubRespGroup  (0x64|0x80)
        public const byte Ab9RespGroup   = 0x89;   // MozaProtocol.Ab9RespGroup  (0x09|0x80)

        // Pre-built probe frames. Base: grp 0x2B dev 0x13 cmd 2. Hub: grp 0x64
        // dev 0x12 cmd 3. AB9: grp 0x09 dev 0x12 (identity).
        private static readonly byte[] BaseProbeFrame = BuildProbe(new byte[] { 0x7E, 0x03, 0x2B, 0x13, 0x02, 0x00, 0x00, 0x00 });
        private static readonly byte[] HubProbeFrame  = BuildProbe(new byte[] { 0x7E, 0x03, 0x64, 0x12, 0x03, 0x00, 0x00, 0x00 });
        private static readonly byte[] Ab9ProbeFrame  = BuildProbe(new byte[] { 0x7E, 0x00, 0x09, 0x12, 0x00 });

        private static byte[] BuildProbe(byte[] frame)
        {
            frame[frame.Length - 1] = WireChecksum(frame, frame.Length - 1);
            return frame;
        }

        // Wire-level checksum: each 0x7E in body positions 2.. counts twice
        // (byte-stuffing doubles it on the wire). Mirror of
        // MozaProtocol.CalculateWireChecksum.
        private static byte WireChecksum(byte[] data, int length)
        {
            int sum = MagicValue;
            for (int i = 0; i < length; i++) sum += data[i];
            for (int i = 2; i < length; i++) if (data[i] == MessageStart) sum += MessageStart;
            return (byte)(sum & 0xFF);
        }

        /// <summary>The raw (un-stuffed) wire probe frame for a kind. Exposed so the
        /// in-assembly <c>MozaPortProbe</c> can run the same probe over an already-open
        /// <c>IMozaPort</c> (Wine by-id path) without duplicating the frame bytes.</summary>
        public static byte[] GetProbeFrame(ProbeKind kind)
        {
            switch (kind)
            {
                case ProbeKind.Hub: return HubProbeFrame;
                case ProbeKind.Ab9: return Ab9ProbeFrame;
                default: return BaseProbeFrame;
            }
        }

        /// <summary>The response group byte that confirms a kind (0xAB base / 0xE4 hub / 0x89 ab9).</summary>
        public static byte GetExpectedRespGroup(ProbeKind kind)
        {
            switch (kind)
            {
                case ProbeKind.Hub: return HubRespGroup;
                case ProbeKind.Ab9: return Ab9RespGroup;
                default: return BaseRespGroup;
            }
        }

        /// <summary>Parse a probe-kind name (case-insensitive) — used by the
        /// helper to decode its argv.</summary>
        public static bool TryParseKind(string s, out ProbeKind kind)
        {
            switch ((s ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "base": kind = ProbeKind.Base; return true;
                case "hub":  kind = ProbeKind.Hub;  return true;
                case "ab9":  kind = ProbeKind.Ab9;  return true;
                default:     kind = ProbeKind.Base; return false;
            }
        }

        /// <summary>
        /// Open <paramref name="portName"/>, send the probe for
        /// <paramref name="kind"/>, and poll for the expected response group.
        /// Returns (responded, reachable); reachable=false means the open itself
        /// failed. <b>This is the call that can crash Wine on a not-ready port</b>
        /// — never call it on a thread/process whose death would matter.
        /// <paramref name="log"/> is an optional diagnostic sink.
        /// </summary>
        public static (bool responded, bool reachable) ProbeOnePort(
            string portName, ProbeKind kind, Action<string>? log = null)
        {
            byte[] msg;
            byte expectedRespGroup;
            switch (kind)
            {
                case ProbeKind.Base: msg = BaseProbeFrame; expectedRespGroup = BaseRespGroup; break;
                case ProbeKind.Hub:  msg = HubProbeFrame;  expectedRespGroup = HubRespGroup;  break;
                case ProbeKind.Ab9:  msg = Ab9ProbeFrame;  expectedRespGroup = Ab9RespGroup;  break;
                default: return (false, false);
            }

            SerialPort? probe = null;
            try
            {
                probe = new SerialPort(portName, BaudRate) { ReadTimeout = 300, WriteTimeout = 300 };
                probe.Open();
            }
            catch
            {
                try { probe?.Dispose(); } catch { }
                return (false, false); // open failed → not reachable
            }

            try
            {
                probe.DiscardInBuffer();

                // Re-probe periodically and poll in short slices — boot-time
                // debug-log bursts (group 0x0E) drown a single probe-and-peek.
                const int TotalBudgetMs = 500;
                const int ProbeRepeatMs = 200;
                const int PollSliceMs = 25;
                const int MaxAccumBytes = 4096;

                var accum = new List<byte>(512);
                byte firstSeenGroup = 0xFF;
                bool responded = false;

                int waited = 0;
                int nextProbeAt = 0;
                while (waited < TotalBudgetMs)
                {
                    if (waited >= nextProbeAt)
                    {
                        try { probe.Write(msg, 0, msg.Length); } catch { return (false, false); }
                        nextProbeAt = waited + ProbeRepeatMs;
                    }

                    Thread.Sleep(PollSliceMs);
                    waited += PollSliceMs;

                    int avail = probe.BytesToRead;
                    if (avail > 0)
                    {
                        int want = Math.Min(avail, MaxAccumBytes - accum.Count);
                        if (want > 0)
                        {
                            var tmp = new byte[want];
                            int n = probe.Read(tmp, 0, want);
                            for (int i = 0; i < n; i++) accum.Add(tmp[i]);
                        }
                        for (int i = 0; i + 2 < accum.Count; i++)
                        {
                            if (accum[i] != MessageStart) continue;
                            byte respGroup = accum[i + 2];
                            if (firstSeenGroup == 0xFF) firstSeenGroup = respGroup;
                            if (respGroup == expectedRespGroup) { responded = true; break; }
                        }
                        if (responded) break;
                    }
                }

                if (!responded && firstSeenGroup != 0xFF)
                {
                    log?.Invoke(
                        $"Probe {portName} {kind}: {accum.Count}B in {waited}ms, " +
                        $"no 0x{expectedRespGroup:X2} (first seen 0x{firstSeenGroup:X2})");
                }
                return (responded, true);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Probe {portName}: {ex.GetType().Name}");
                return (false, false);
            }
            finally
            {
                try { probe.Close(); } catch { }
                try { probe.Dispose(); } catch { }
            }
        }
    }
}
