using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MozaPlugin.Telemetry.Sessions
{
    /// <summary>
    /// Tracks open 7c:00 sessions (both host- and device-initiated). Captures show
    /// a fixed role per session byte in 2025-11 firmware:
    ///
    ///   0x01 = host-opened mgmt (wheel identity / log stream)
    ///   0x02 = host-opened telemetry (tier def + fc:00 acks)
    ///   0x03 = host-opened aux config (tile-server / settings push)
    ///   0x04 = device-opened file transfer (mzdash upload + root dir listing)
    ///   0x06 = device-opened keepalive
    ///   0x08 = device-opened keepalive
    ///   0x09 = device-opened configJson RPC (dashboard state)
    ///   0x0A = device-opened keepalive
    ///
    /// The 2026-04 firmware had a different port assignment (global monotonic
    /// counter); the 2025-11 firmware keeps port == session for every session.
    /// Plugin assumes 2025-11 and treats session bytes as the role key.
    /// </summary>
    public enum SessionRole
    {
        Unknown,
        Management,   // 0x01
        Telemetry,    // 0x02
        AuxConfig,    // 0x03
        FileTransfer, // 0x04
        Keepalive,    // 0x06, 0x08, 0x0A
        ConfigJson,   // 0x09
    }

    public sealed class SessionInfo
    {
        public byte SessionByte { get; set; }
        public byte Port { get; set; }
        public SessionRole Role { get; set; } = SessionRole.Unknown;
        /// <summary>True if the device sent the type=0x81 open (wheel-initiated).</summary>
        public bool DeviceInitiated { get; set; }
        /// <summary>Last seen sequence number from the peer's side.</summary>
        public int LastAckedSeq { get; set; }
        /// <summary>Monotonic outbound chunk sequence for frames we send on this session.</summary>
        public int OutboundSeq { get; set; }
    }

    public sealed class SessionRegistry
    {
        private readonly ConcurrentDictionary<byte, SessionInfo> _sessions = new();

        public SessionInfo GetOrCreate(byte sessionByte)
        {
            return _sessions.GetOrAdd(sessionByte, b => new SessionInfo
            {
                SessionByte = b,
                Port = b,
                Role = InferRole(b),
            });
        }

        public bool TryGet(byte sessionByte, out SessionInfo info) =>
            _sessions.TryGetValue(sessionByte, out info!);

        public IReadOnlyCollection<SessionInfo> All => _sessions.Values.ToArray();

        public void Reset() => _sessions.Clear();

        private static SessionRole InferRole(byte b) => b switch
        {
            0x01 => SessionRole.Management,
            0x02 => SessionRole.Telemetry,
            0x03 => SessionRole.AuxConfig,
            0x04 => SessionRole.FileTransfer,
            0x06 or 0x08 or 0x0A => SessionRole.Keepalive,
            0x09 => SessionRole.ConfigJson,
            _ => SessionRole.Unknown,
        };
    }
}
