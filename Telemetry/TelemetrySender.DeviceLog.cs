using System;
using System.Linq;
using System.Threading;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry.Dashboard;
using MozaPlugin.Telemetry.Era;
using MozaPlugin.Telemetry.Frames;
using MozaPlugin.Telemetry.Sessions;
using MozaPlugin.Telemetry.TestMode;
using MozaPlugin.Telemetry.TileServer;
using Timer = System.Timers.Timer;

namespace MozaPlugin.Telemetry
{
    public partial class TelemetrySender
    {

        // Scratch list for inbound FF records. Only ever touched on the serial
        // read thread (FeedFfRecords), which is single per connection even when
        // two senders share it.
        private readonly Sessions.FfRecordStream _ffStream = new Sessions.FfRecordStream();
        private readonly System.Collections.Generic.List<Sessions.FfRecord> _ffScratch
            = new System.Collections.Generic.List<Sessions.FfRecord>(4);

        /// <summary>
        /// Poll the display for log lines and ack whatever the last reply
        /// carried. Called from the tick body ABOVE the preamble branch and the
        /// no-tiers early return, so it runs whenever the session layer is up —
        /// the display's log has nothing to do with whether telemetry is
        /// transmitting, and a wheel with no bound dashboard still has one.
        ///
        /// Paced on the clock rather than a tick count because the tick is
        /// ~30 Hz here, and because the interval should not drift with
        /// <c>_baseTickMs</c>.
        ///
        /// Requests ride <see cref="ResolveFfSession"/> ONLY — the same session
        /// as the kind=2/7 init handshake. Never the tier-def session: that one
        /// carries typed sub-msgs (type=0x01 tier-def, 0x04 catalog URL, 0x05
        /// string value, 0x06 ack) and 0xFF is not a valid type there. A "send
        /// the first pull to both sessions for good measure" version of this
        /// wedged the wheel's session layer outright (bundle 6TD14R8V) — the
        /// stray FF record landed on sess=0x01 at seq 2 during cold start.
        ///
        /// Gated on Active so nothing is injected mid-cold-start, but called
        /// from ABOVE the tick's no-tiers early return, so a wheel with no
        /// bound dashboard still pulls — the log is independent of whether
        /// telemetry is actually transmitting.
        ///
        /// Fire-and-forget by design: the device answers only when it has new
        /// lines, so silence is the normal steady state (1 657 requests produced
        /// 174 payloads in the reference capture) and is not a fault.
        /// </summary>
        private void TickEmitDeviceLogPoll()
        {
            if (!DeviceLogPullEnabled) return;
            // Never emit during Starting/Preamble — the cold-start handshake and
            // tier-def own the session seq stream then.
            if (_state != TelemetryState.Active) return;
            // Don't consume the pending receipt if we can't actually send it.
            if (!ConnectionIsConnected) return;
            // Session must be open for an FF record to go anywhere.
            if (MgmtPort == 0 && FlagByte == 0) return;

            // Drain any receipt the read thread parked for us. Emitting it here
            // rather than inline in the inbound handler keeps Session0xSeqLock
            // off the serial read thread's ack path — a lock held there once
            // deadlocked telemetry (see docs/DEVELOPMENT.md § Threading model).
            int consumed = System.Threading.Interlocked.Exchange(ref _pendingLogReceipt, 0);
            if (consumed > 0)
            {
                // Ack on the session the payload actually arrived on, not on
                // wherever we'd send the next request.
                byte ackSession = (byte)System.Threading.Interlocked.CompareExchange(
                    ref _deviceLogAckSession, 0, 0);
                if (ackSession == 0) ackSession = ResolveFfSession();
                _propertyPushQueue.SendU32(
                    SessionPropertyPushBuilder.KindDeviceLogReceipt,
                    (uint)consumed,
                    ackSession,
                    // A receipt advances the device's read cursor by N; a later
                    // one does NOT supersede an earlier one the way a brightness
                    // value does, so it must stay in the retransmit queue.
                    coalesce: false);
            }

            long now = DateTime.UtcNow.Ticks;
            long due = System.Threading.Interlocked.Read(ref _deviceLogNextPullUtcTicks);
            if (due != 0 && now < due) return;
            System.Threading.Interlocked.Exchange(
                ref _deviceLogNextPullUtcTicks,
                now + DeviceLogPollIntervalMs * TimeSpan.TicksPerMillisecond);

            byte session = ResolveFfSession();
            _propertyPushQueue.SendU32(
                SessionPropertyPushBuilder.KindDeviceLogRequest,
                DeviceLogLinesPerPull,
                session);
            System.Threading.Interlocked.Increment(ref _deviceLogRequestsSent);
            System.Threading.Interlocked.Exchange(ref _deviceLogLastRequestSession, session);

            if (!_deviceLogFirstPullLogged)
            {
                _deviceLogFirstPullLogged = true;
                MozaLog.Debug(
                    $"[AZOM] Device log pull started on sess=0x{session:X2} " +
                    $"({DeviceLogLinesPerPull} lines, every {DeviceLogPollIntervalMs / 1000}s)");
            }
        }

        /// <summary>Whether the device-log pull is on. Gates BOTH directions —
        /// with it off the inbound reassembler must not run either, or the
        /// plugin still buffers + scans every catalog-session chunk and still
        /// files device log text into bug-report bundles.</summary>
        private static bool DeviceLogPullEnabled
            => MozaPlugin.Instance?.Settings?.EnableDeviceLogPull == true;

        /// <summary>Feed one inbound session-data chunk to the FF-record
        /// reassembler and route any completed records. Serial read thread.
        /// This is a second reader of the same bytes the channel-catalog parser
        /// consumes — it never mutates that parser's state.</summary>
        internal void FeedFfRecords(byte session, int seq, byte[] chunkPayload)
        {
            if (chunkPayload == null || chunkPayload.Length <= 4) return;
            if (!DeviceLogPullEnabled) return;
            _ffScratch.Clear();
            _ffStream.Append(session, seq, chunkPayload, _ffScratch);
            for (int i = 0; i < _ffScratch.Count; i++)
            {
                var rec = _ffScratch[i];
                // kind=14 is bidirectional: 4-byte value = a request (ours,
                // echoed back), anything larger = the log payload.
                if (rec.Kind == SessionPropertyPushBuilder.KindDeviceLogRequest
                    && rec.Value.Length > 4)
                    OnDeviceLogPayload(rec.Session, rec.Value);
            }
        }

        /// <summary>Handle a b2h kind=14 device-log payload. Serial read thread:
        /// parses and stores, but parks the kind=15 receipt for the tick thread
        /// to emit.</summary>
        private void OnDeviceLogPayload(byte session, byte[] value)
        {
            System.Threading.Interlocked.Increment(ref _deviceLogPayloadsReceived);
            var result = Diagnostics.DeviceLogParser.Parse(value);
            if (!result.Decoded)
            {
                // Nothing to ack: without the device's own count we can't
                // advance its cursor, so it will re-send this same block. Warn
                // (not Debug) — it means the pull is stuck for this connection.
                if (!_deviceLogDecodeFailLogged)
                {
                    _deviceLogDecodeFailLogged = true;
                    MozaLog.Warn(
                        $"[AZOM] Device log payload on sess=0x{session:X2} did not decode " +
                        $"({value?.Length ?? 0}B) — log pull is stalled for this connection");
                }
                return;
            }

            if (result.Lines.Length > 0)
                MozaPlugin.Instance?.DeviceLogForDiagnostics?.Record(DeviceLogSourceName, result.Lines);

            // Ack the count the DEVICE declared, not the number of lines we
            // managed to walk, and ack it on the session the payload arrived on.
            // The device clears by its own count, so acking a short walk would
            // leave the remainder queued to be re-sent on every subsequent pull
            // — a permanent stall.
            if (result.DeclaredCount > 0)
            {
                System.Threading.Interlocked.Exchange(ref _deviceLogAckSession, session);
                System.Threading.Interlocked.Add(ref _pendingLogReceipt, result.DeclaredCount);
            }

            string detail = result.Lines.Length != result.DeclaredCount
                ? $"decoded {result.Lines.Length} of {result.DeclaredCount} declared line(s)"
                : $"{result.DeclaredCount} line(s)";
            MozaLog.Debug($"[AZOM] Device log ({DeviceLogSourceName}): {detail} on sess=0x{session:X2}");
        }

        /// <summary>Label recorded against this sender's log lines. A rig can
        /// run a wheel screen and a CM2 dash concurrently, each with its own
        /// logger feeding the shared store.</summary>
        private string DeviceLogSourceName
            => IsStandaloneDashboardTarget || TargetDeviceId == MozaProtocol.DeviceDash
                ? "dash"
                : "wheel";

        /// <summary>Reset device-log pull state. Called on both start and stop —
        /// on stop so up to 512 kB of per-session reassembly buffers aren't held
        /// by an idle sender.</summary>
        internal void ResetDeviceLogPull()
        {
            _ffStream.Clear();
            _deviceLogDecodeFailLogged = false;
            _deviceLogFirstPullLogged = false;
            System.Threading.Interlocked.Exchange(ref _deviceLogRequestsSent, 0);
            System.Threading.Interlocked.Exchange(ref _deviceLogPayloadsReceived, 0);
            System.Threading.Interlocked.Exchange(ref _deviceLogLastRequestSession, 0);
            System.Threading.Interlocked.Exchange(ref _pendingLogReceipt, 0);
            System.Threading.Interlocked.Exchange(ref _deviceLogAckSession, 0);
            // 0 = due now, so a reconnect re-pulls the backlog immediately.
            System.Threading.Interlocked.Exchange(ref _deviceLogNextPullUtcTicks, 0);
        }
    }
}
