using System.Collections.Generic;

namespace MozaPlugin.Sdk.PitHouseUdp
{
    /// <summary>
    /// Decoded envelope of a PitHouse-style UDP control packet.
    /// Wire shape (CBOR map at the top level):
    /// <code>
    ///   { "Head": { "PacketId": int, "Version": "X.Y.Z", "ReplyPort": int? },
    ///     "Payload": object }
    /// </code>
    /// <para>
    /// Used by third-party wheel-config tools to set or read MOZA wheelbase
    /// parameters (steer lock, FFB, etc.) over plain UDP — a separate
    /// protocol surface from the CoAP server at port 40266. The first
    /// example we've decoded is the RSF launcher's steering-lock probe
    /// (PacketIds 3 and 4); other tools may use additional PacketIds we
    /// haven't seen yet. See
    /// <c>docs/protocol/identity/device-catalog-manifest.md</c> for the
    /// CoAP server's contrasting protocol shape.
    /// </para>
    /// </summary>
    internal sealed class PitHousePacket
    {
        public int PacketId { get; }
        public string Version { get; }
        public int? ReplyPort { get; }
        /// <summary>
        /// Decoded payload. Typically a <see cref="Dictionary{TKey,TValue}"/>
        /// (for writes) or a <see cref="List{T}"/> of field-name strings
        /// (for reads); occasionally absent. Decoder leaves it as the raw
        /// <see cref="object"/> the CBOR reader returned so each handler
        /// can validate against the shape it expects.
        /// </summary>
        public object? Payload { get; }

        public PitHousePacket(int packetId, string version, int? replyPort, object? payload)
        {
            PacketId = packetId;
            Version = version ?? string.Empty;
            ReplyPort = replyPort;
            Payload = payload;
        }
    }
}
