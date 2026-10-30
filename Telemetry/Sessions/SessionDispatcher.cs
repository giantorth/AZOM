using System;
using System.Collections.Generic;
using MozaPlugin.Diagnostics;

namespace MozaPlugin.Telemetry.Sessions
{
    /// <summary>
    /// Central dispatch for session-layer frames. Each session byte maps to
    /// at most one <see cref="ISessionConsumer"/> owner. Frames for unclaimed
    /// sessions are silently dropped. This eliminates the collision bugs caused
    /// by multiple consumers processing the same session's data.
    /// </summary>
    public sealed class SessionDispatcher
    {
        private readonly object _lock = new();
        private readonly Dictionary<byte, ISessionConsumer> _owners = new();

        /// <summary>
        /// Claim exclusive ownership of a session. If already owned by another
        /// consumer, the previous owner is evicted (logged).
        /// </summary>
        public void Claim(byte session, ISessionConsumer owner)
        {
            lock (_lock)
            {
                if (_owners.TryGetValue(session, out var prev) && prev != owner)
                {
                    MozaLog.Debug(
                        $"[AZOM] SessionDispatcher: session 0x{session:X2} " +
                        $"transferred {prev.GetType().Name} → {owner.GetType().Name}");
                }
                _owners[session] = owner;
            }
        }

        /// <summary>Release ownership. Only releases if current owner matches.</summary>
        public void Release(byte session, ISessionConsumer owner)
        {
            lock (_lock)
            {
                if (_owners.TryGetValue(session, out var cur) && cur == owner)
                    _owners.Remove(session);
            }
        }

        /// <summary>Release all sessions owned by a specific consumer.</summary>
        public void ReleaseAll(ISessionConsumer owner)
        {
            lock (_lock)
            {
                var toRemove = new List<byte>();
                foreach (var kvp in _owners)
                    if (kvp.Value == owner) toRemove.Add(kvp.Key);
                foreach (var s in toRemove) _owners.Remove(s);
            }
        }

        /// <summary>Check who owns a session (null if unclaimed).</summary>
        public ISessionConsumer? GetOwner(byte session)
        {
            lock (_lock)
                return _owners.TryGetValue(session, out var owner) ? owner : null;
        }

        // All four Dispatch* helpers run on the serial read thread. An
        // exception in a consumer callback (malformed payload that trips an
        // assertion, a reassembler-state edge case) would otherwise unwind the
        // read loop and the wheel goes silent for the rest of the session.
        // Wrap per-callback so one buggy consumer can't take down inbound
        // dispatch for every other session. The try/catch is inlined per method
        // so a dispatch allocates no closure.
        private static void LogConsumerThrew(string kind, byte session, Exception ex)
        {
            MozaLog.Warn(
                $"[AZOM] SessionDispatcher: session 0x{session:X2} {kind} consumer threw: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }

        /// <summary>Dispatch a data chunk (type 0x01) to the session owner.</summary>
        public void DispatchData(byte session, int seq, byte[] payload)
        {
            ISessionConsumer? owner;
            lock (_lock) _owners.TryGetValue(session, out owner);
            if (owner == null) return;
            try { owner.OnData(session, seq, payload); }
            catch (Exception ex) { LogConsumerThrew("OnData", session, ex); }
        }

        /// <summary>Dispatch an FC:00 ack to the session owner.</summary>
        public void DispatchAck(byte session, int ackSeq)
        {
            ISessionConsumer? owner;
            lock (_lock) _owners.TryGetValue(session, out owner);
            if (owner == null) return;
            try { owner.OnAck(session, ackSeq); }
            catch (Exception ex) { LogConsumerThrew("OnAck", session, ex); }
        }

        /// <summary>Dispatch a device-init (type 0x81) to the session owner.</summary>
        public void DispatchOpen(byte session, int openSeq)
        {
            ISessionConsumer? owner;
            lock (_lock) _owners.TryGetValue(session, out owner);
            if (owner == null) return;
            try { owner.OnOpen(session, openSeq); }
            catch (Exception ex) { LogConsumerThrew("OnOpen", session, ex); }
        }

        /// <summary>Dispatch an end marker (type 0x00) to the session owner.</summary>
        public void DispatchClose(byte session, int ackSeq)
        {
            ISessionConsumer? owner;
            lock (_lock) _owners.TryGetValue(session, out owner);
            if (owner == null) return;
            try { owner.OnClose(session, ackSeq); }
            catch (Exception ex) { LogConsumerThrew("OnClose", session, ex); }
        }

        /// <summary>Clear all ownership (used on disconnect/reset).</summary>
        public void Reset()
        {
            lock (_lock) _owners.Clear();
        }
    }
}
