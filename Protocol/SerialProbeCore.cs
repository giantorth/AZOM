using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;

namespace MozaPlugin.Protocol
{
    /// <summary>Which MOZA device a serial probe targets.</summary>
    public enum ProbeKind { Base, Hub, Ab9 }

    /// <summary>How a single port probe ended.</summary>
    public enum ProbeOutcome
    {
        Responded,
        // Opened, nothing came back.
        NoReply,
        // Opened, bytes came back but not the expected response group.
        OtherReply,
        // Open() threw; ProbeResult.Error carries the exception.
        OpenFailed,
        // Opened, then a write/read threw.
        IoFailed,
        // Opened, then the caller abandoned the probe mid-poll.
        Abandoned,
        // Open() did not return before the caller's deadline.
        Blocked,
        // Not attempted (backoff window, prior probe still blocked, thread start failed).
        Skipped,
    }

    public readonly struct ProbeResult
    {
        public readonly ProbeOutcome Outcome;
        public readonly Exception? Error;
        public readonly string? Detail;

        public ProbeResult(ProbeOutcome outcome, Exception? error = null, string? detail = null)
        {
            Outcome = outcome;
            Error = error;
            Detail = detail;
        }

        public bool Responded => Outcome == ProbeOutcome.Responded;

        /// <summary>The port opened and was usable; false means it could not be probed at all.</summary>
        public bool Reachable => Outcome == ProbeOutcome.Responded || Outcome == ProbeOutcome.NoReply
                              || Outcome == ProbeOutcome.OtherReply || Outcome == ProbeOutcome.Abandoned;
    }

    /// <summary>
    /// Serial open+probe core used by <see cref="MozaSerialConnection"/>.
    ///
    /// <para>The one operation that can SEGFAULT Wine — <c>SerialPort.Open</c> on
    /// a not-yet-ready CDC-ACM port (the freshly-powered-base crash) — lives in
    /// <see cref="ProbeOnePort"/>. <see cref="MozaSerialConnection.ProbeWithTimeout"/>
    /// runs it on a throwaway background thread and abandons that thread at the
    /// deadline if <c>Open()</c> wedges, so a hung probe never blocks detection.</para>
    /// </summary>
    public static class SerialProbeCore
    {
        // ── Probe timing ─────────────────────────────────────────────────────
        // How long ProbeOnePort polls a port that opened but hasn't answered
        // before giving up. A non-answering port ALWAYS burns this in full, and
        // the handle is only released by the finally afterwards — so any caller
        // deadline must clear this value with room for Open() on top, or
        // abandoning the thread leaves the port held. See
        // MozaSerialConnection.ProbeJoinTimeoutMs, which derives itself from
        // this constant for exactly that reason.
        public const int TotalBudgetMs = 500;
        private const int ProbeRepeatMs = 200;
        private const int PollSliceMs = 25;

        // Pre-built probe frames. Base: grp 0x2B dev 0x13 cmd 2. Hub: grp 0x64
        // dev 0x12 cmd 3. AB9: grp 0x09 dev 0x12 (identity).
        private static readonly byte[] BaseProbeFrame = BuildProbe(new byte[] { 0x7E, 0x03, 0x2B, 0x13, 0x02, 0x00, 0x00, 0x00 });
        private static readonly byte[] HubProbeFrame  = BuildProbe(new byte[] { 0x7E, 0x03, 0x64, 0x12, 0x03, 0x00, 0x00, 0x00 });
        private static readonly byte[] Ab9ProbeFrame  = BuildProbe(new byte[] { 0x7E, 0x00, 0x09, 0x12, 0x00 });

        private static byte[] BuildProbe(byte[] frame)
        {
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        /// <summary>
        /// Open <paramref name="portName"/>, send the probe for
        /// <paramref name="kind"/>, and poll for the expected response group.
        /// Returns how the probe ended; an Open() failure carries its exception
        /// so the caller can classify it. <b>This is the call that can crash Wine on a not-ready port</b>
        /// — never call it on a thread/process whose death would matter.
        /// <paramref name="log"/> is an optional diagnostic sink.
        ///
        /// <para><paramref name="shouldAbort"/> is polled between poll slices. It
        /// lets a caller that has already abandoned this thread cut the remaining
        /// budget short so the port is released in ~<see cref="PollSliceMs"/>
        /// rather than being held for the rest of it. Closing still happens on
        /// THIS thread in the finally — a cross-thread Close during a native
        /// Open is the Wine segfault, which is why the caller cannot just do it
        /// itself.</para>
        /// </summary>
        public static ProbeResult ProbeOnePort(
            string portName, ProbeKind kind, Action<string>? log = null,
            Func<bool>? shouldAbort = null)
        {
            byte[] msg;
            byte expectedRespGroup;
            switch (kind)
            {
                case ProbeKind.Base: msg = BaseProbeFrame; expectedRespGroup = MozaProtocol.BaseRespGroup; break;
                case ProbeKind.Hub:  msg = HubProbeFrame;  expectedRespGroup = MozaProtocol.HubRespGroup;  break;
                case ProbeKind.Ab9:  msg = Ab9ProbeFrame;  expectedRespGroup = MozaProtocol.Ab9RespGroup;  break;
                default: return new ProbeResult(ProbeOutcome.Skipped, detail: $"unknown probe kind {kind}");
            }

            SerialPort? probe = null;
            try
            {
                probe = new SerialPort(portName, MozaProtocol.BaudRate) { ReadTimeout = 300, WriteTimeout = 300 };
                probe.Open();
            }
            catch (Exception ex)
            {
                try { probe?.Dispose(); } catch { }
                return new ProbeResult(ProbeOutcome.OpenFailed, ex);
            }

            try
            {
                probe.DiscardInBuffer();

                // Re-probe periodically and poll in short slices (constants at
                // the top of the class) — boot-time debug-log bursts (group
                // 0x0E) drown a single probe-and-peek.
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
                        try { probe.Write(msg, 0, msg.Length); }
                        catch (Exception ex) { return new ProbeResult(ProbeOutcome.IoFailed, ex, "write"); }
                        nextProbeAt = waited + ProbeRepeatMs;
                    }

                    Thread.Sleep(PollSliceMs);
                    waited += PollSliceMs;

                    // Caller gave up on us — stop polling and let the finally
                    // close the handle now instead of at the end of the budget.
                    if (shouldAbort?.Invoke() == true)
                    {
                        log?.Invoke($"Probe {portName}: abandoned by caller after {waited}ms — releasing port");
                        return new ProbeResult(ProbeOutcome.Abandoned);
                    }

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
                            if (accum[i] != MozaProtocol.MessageStart) continue;
                            byte respGroup = accum[i + 2];
                            if (firstSeenGroup == 0xFF) firstSeenGroup = respGroup;
                            if (respGroup == expectedRespGroup) { responded = true; break; }
                        }
                        if (responded) break;
                    }
                }

                if (responded) return new ProbeResult(ProbeOutcome.Responded);
                if (firstSeenGroup == 0xFF) return new ProbeResult(ProbeOutcome.NoReply);
                log?.Invoke(
                    $"Probe {portName} {kind}: {accum.Count}B in {waited}ms, " +
                    $"no 0x{expectedRespGroup:X2} (first seen 0x{firstSeenGroup:X2})");
                return new ProbeResult(ProbeOutcome.OtherReply, detail: $"group 0x{firstSeenGroup:X2}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Probe {portName}: {ex.GetType().Name}");
                return new ProbeResult(ProbeOutcome.IoFailed, ex);
            }
            finally
            {
                try { probe.Close(); } catch { }
                try { probe.Dispose(); } catch { }
            }
        }
    }
}
