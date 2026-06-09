using System;
using System.Collections.Generic;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Retry-with-backoff for one-shot commands awaiting a named response.
    /// Caller wires NoteResponse from the parser; thread-safe across send / retry / read.
    ///
    /// Retry lifecycle for a Tracked entry:
    /// <list type="number">
    /// <item>Exponential backoff per caller's <c>backoffMs</c> schedule (cap = last value), reused indefinitely.</item>
    /// <item>At <see cref="LongPendingWarnMs"/> (30 s) without a response: one-shot WARN log.</item>
    /// <item>At <see cref="SunsetAfterMs"/> (60 s) without a response: name is added to the sunset set, entry removed, one-shot INFO log. Future <see cref="Track"/> calls for that name silently no-op until <see cref="Clear"/> is called.</item>
    /// </list>
    /// Sunset captures the "hardware variant doesn't support this command" case
    /// — e.g. a wheel without an ambient LED strip never answers a brightness
    /// read. Without sunset the entry would re-emit every 10 s for the entire
    /// session; with sunset it stops after 60 s and PollStatus retracks become
    /// silent no-ops. <see cref="Clear"/> is called on serial disconnect and
    /// wheel hot-swap so a different hardware variant gets fresh attempts.
    ///
    /// Re-tracking the same <c>expectedName</c> while still pending overwrites
    /// the existing entry, which is the intended behaviour for callers that
    /// probe multiple device IDs with the same command name
    /// (<c>ProbeWheelDetection</c>).
    /// </summary>
    public sealed class PendingResponseTracker
    {
        private sealed class Entry
        {
            public string Name = "";
            public byte[] Frame = Array.Empty<byte>();
            public int[] BackoffMs = Array.Empty<int>();
            public int Attempts;
            public int LastSentTickCount;
            public long EnqueuedAtTicks;
            public bool LongPendingWarned;
        }

        // Threshold for the one-shot "still pending" warn log. Slow but not
        // necessarily wrong — caller decides whether to surface it.
        private const long LongPendingWarnMs = 30_000;
        // Threshold for permanently sunsetting a name on the current
        // connection. Set well beyond the warn so a late response (transient
        // USB stall, busy wheel) still has a window to land and clear the
        // entry naturally before sunset kicks in.
        private const long SunsetAfterMs = 60_000;

        private readonly Dictionary<string, Entry> _pending = new();
        // Names declared unsupported on this connection. Track() silently
        // skips these; Clear() empties the set.
        private readonly HashSet<string> _sunset = new();
        private readonly object _lock = new object();
        // Lock-free count mirror; NoteResponse early-outs when idle (~50-200 Hz path).
        private volatile int _pendingCount;
        private volatile int _sunsetCount;

        public int PendingCount => _pendingCount;

        /// <summary>Number of command names that have been sunset on the
        /// current connection (no response within <see cref="SunsetAfterMs"/>).
        /// Reset on <see cref="Clear"/>. A growing value across reconnects
        /// indicates the user's hardware variant doesn't implement those
        /// commands; not necessarily an error.</summary>
        public int SunsetCount => _sunsetCount;

        /// <summary>Wall-clock milliseconds the oldest still-pending entry has
        /// been waiting for its response. Zero when nothing is pending.
        /// Useful as a diagnostic: a value that grows without bound indicates
        /// the wheel never produced the expected named response.</summary>
        public long LongestPendingMs
        {
            get
            {
                if (_pendingCount == 0) return 0;
                long oldest = long.MaxValue;
                lock (_lock)
                {
                    foreach (var kv in _pending)
                    {
                        if (kv.Value.EnqueuedAtTicks < oldest)
                            oldest = kv.Value.EnqueuedAtTicks;
                    }
                }
                if (oldest == long.MaxValue) return 0;
                return (DateTime.UtcNow.Ticks - oldest) / TimeSpan.TicksPerMillisecond;
            }
        }

        /// <summary>Track an outbound frame awaiting a named response (caller
        /// has already Send'd). The <paramref name="maxAttempts"/> parameter
        /// is accepted for API stability but no longer enforced — retries are
        /// bounded by <see cref="SunsetAfterMs"/>, not attempt count.
        /// Silently no-ops if <paramref name="expectedName"/> has been sunset
        /// on this connection.</summary>
        public void Track(string expectedName, byte[] frame, int[] backoffMs, int maxAttempts)
        {
            if (string.IsNullOrEmpty(expectedName) || frame == null || backoffMs == null) return;
            if (backoffMs.Length == 0) return;
            lock (_lock)
            {
                if (_sunset.Contains(expectedName)) return;
                // Caller owns the backoff array (callers pass static-readonly
                // schedules). Frame buffer is cloned because callers may reuse
                // their build buffer across Sends.
                _pending[expectedName] = new Entry
                {
                    Name = expectedName,
                    Frame = (byte[])frame.Clone(),
                    BackoffMs = backoffMs,
                    Attempts = 1,
                    LastSentTickCount = Environment.TickCount,
                    EnqueuedAtTicks = DateTime.UtcNow.Ticks,
                };
                _pendingCount = _pending.Count;
            }
        }

        /// <summary>
        /// Note a response from the wheel matching <paramref name="responseName"/>.
        /// Clears the pending entry if present. Called from the response-dispatch
        /// path so retries stop immediately on first match.
        /// </summary>
        public void NoteResponse(string responseName)
        {
            if (string.IsNullOrEmpty(responseName)) return;
            // Fast-path: in steady state nothing is pending and this fires on
            // every response — skipping the lock + dictionary hash here is the
            // difference between ~0 and ~50–200 lock acquisitions/sec on the
            // read thread.
            if (_pendingCount == 0) return;
            lock (_lock)
            {
                if (_pending.Remove(responseName))
                    _pendingCount = _pending.Count;
            }
        }

        /// <summary>
        /// Walk the pending entries, re-emit frames whose backoff has
        /// elapsed, warn on entries pending past <see cref="LongPendingWarnMs"/>,
        /// and sunset entries pending past <see cref="SunsetAfterMs"/>.
        /// Called periodically from the telemetry tick.
        ///
        /// Backoff schedule: <c>gateMs = BackoffMs[min(Attempts-1, last)]</c>.
        /// Once <c>Attempts</c> exceeds the array length the cap (last entry)
        /// is reused — pass a schedule like
        /// <c>{200, 400, 800, 1600, 3200, 6400, 10000}</c> to get exponential
        /// growth saturating at 10 s per retry until the sunset threshold.
        /// </summary>
        /// <param name="resend">Callback invoked for each frame that should be
        /// re-emitted on the wire. Caller is responsible for the actual
        /// connection.Send call so this class stays transport-agnostic.</param>
        public void TickRetransmits(Action<byte[]> resend)
        {
            if (resend == null) return;
            // Fast-path mirror of NoteResponse — this fires every 250 ms even
            // when the tracker is idle.
            if (_pendingCount == 0) return;
            List<byte[]>? toResend = null;
            List<string>? newlyLong = null;
            List<string>? newlySunset = null;
            lock (_lock)
            {
                int now = Environment.TickCount;
                long nowTicks = DateTime.UtcNow.Ticks;
                List<string>? doomed = null;
                foreach (var kv in _pending)
                {
                    var e = kv.Value;
                    long ageMs = (nowTicks - e.EnqueuedAtTicks) / TimeSpan.TicksPerMillisecond;
                    if (ageMs >= SunsetAfterMs)
                    {
                        (doomed ??= new List<string>()).Add(kv.Key);
                        (newlySunset ??= new List<string>()).Add($"{e.Name} ({ageMs} ms, {e.Attempts - 1} retries)");
                        continue;
                    }
                    int gateMs = e.BackoffMs[Math.Min(e.Attempts - 1, e.BackoffMs.Length - 1)];
                    if (now - e.LastSentTickCount < gateMs) continue;
                    (toResend ??= new List<byte[]>()).Add(e.Frame);
                    e.Attempts++;
                    e.LastSentTickCount = now;
                    if (!e.LongPendingWarned && ageMs >= LongPendingWarnMs)
                    {
                        e.LongPendingWarned = true;
                        (newlyLong ??= new List<string>()).Add($"{e.Name} ({ageMs} ms, {e.Attempts - 1} retries)");
                    }
                }
                if (doomed != null)
                {
                    foreach (var k in doomed)
                    {
                        _pending.Remove(k);
                        _sunset.Add(k);
                    }
                    _pendingCount = _pending.Count;
                    _sunsetCount = _sunset.Count;
                }
            }
            if (toResend != null)
            {
                foreach (var f in toResend) resend(f);
            }
            if (newlyLong != null)
            {
                global::MozaPlugin.MozaLog.Warn(
                    $"[AZOM] PendingResponseTracker: {newlyLong.Count} entry(s) still unanswered after {LongPendingWarnMs / 1000} s: {string.Join(", ", newlyLong)}");
            }
            if (newlySunset != null)
            {
                global::MozaPlugin.MozaLog.Info(
                    $"[AZOM] PendingResponseTracker sunset {newlySunset.Count} name(s) (no response after {SunsetAfterMs / 1000} s; future Track() calls for these will no-op until disconnect): {string.Join(", ", newlySunset)}");
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _pending.Clear();
                _sunset.Clear();
                _pendingCount = 0;
                _sunsetCount = 0;
            }
        }
    }
}
