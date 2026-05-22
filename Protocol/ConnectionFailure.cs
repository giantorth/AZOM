using System;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Classification of why a serial-connection attempt (or live link) failed.
    /// Read by the UI hint-builder so port-in-use can be surfaced distinctly
    /// from generic disconnect.
    /// </summary>
    public enum ConnectionFailureKind
    {
        None,
        // Open returned ERROR_ACCESS_DENIED / sharing violation. Most often
        // means another process (MOZA PitHouse, debug tool) holds the port.
        AccessDenied,
        // Port vanished between registry enumeration and Open — typically a
        // hot-unplug or a Wine pty teardown.
        PortVanished,
        // Any other Open failure not matching the buckets above.
        OpenFailedOther,
        // Reserved: Open succeeded but read/write subsequently failed. The
        // post-open IO-failure branch can flip to this kind once a richer
        // surface is wired; today the broad disconnect path is sufficient.
        IoFailureAfterOpen,
    }

    /// <summary>
    /// Snapshot of the most recent connection failure on a single
    /// <see cref="MozaSerialConnection"/>. <see cref="UtcTimestamp"/> is
    /// <see cref="DateTime.MinValue"/> when no failure has been recorded.
    /// </summary>
    public readonly struct ConnectionFailureInfo
    {
        public readonly ConnectionFailureKind Kind;
        public readonly string? PortName;
        public readonly string? Message;
        public readonly DateTime UtcTimestamp;

        public ConnectionFailureInfo(ConnectionFailureKind kind, string? portName, string? message, DateTime utcTimestamp)
        {
            Kind = kind;
            PortName = portName;
            Message = message;
            UtcTimestamp = utcTimestamp;
        }

        public static ConnectionFailureInfo None => default;
    }
}
