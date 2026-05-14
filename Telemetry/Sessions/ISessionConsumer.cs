namespace MozaPlugin.Telemetry.Sessions
{
    /// <summary>
    /// A component that owns one or more serial sessions and processes their
    /// inbound frames exclusively. Sessions are claimed/released through
    /// <see cref="SessionDispatcher"/>; only the current owner receives frames.
    /// </summary>
    public interface ISessionConsumer
    {
        /// <summary>Inbound data chunk (type 0x01).</summary>
        void OnData(byte session, int seq, byte[] payload);

        /// <summary>FC:00 ack for a session this consumer owns.</summary>
        void OnAck(byte session, int ackSeq);

        /// <summary>Device-initiated session open (type 0x81).</summary>
        void OnOpen(byte session, int openSeq);

        /// <summary>Session close / end marker (type 0x00).</summary>
        void OnClose(byte session, int ackSeq);
    }
}
