using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MozaPlugin.Sdk.Cbor;

namespace MozaPlugin.Sdk.PitHouseUdp.Handlers
{
    /// <summary>
    /// PacketId 4 — read steering lock. Body shape (observed from RSF):
    /// <code>
    ///   { "Head": { "PacketId": 4, "Version": "1.0.4", "ReplyPort": &lt;int&gt; },
    ///     "Payload": [ "MotGetSteer_MaximumAngle", "MotGetSteer_LimitAngle" ] }
    /// </code>
    /// Reply (sent to <c>OriginalSender.IP : ReplyPort</c>):
    /// <code>
    ///   { "Payload": { "MotGetSteer_MaximumAngle": &lt;int&gt;,
    ///                  "MotGetSteer_LimitAngle":   &lt;int&gt; } }
    /// </code>
    /// <para>
    /// Sources both values from the live <see cref="MozaData"/> cache —
    /// <c>MozaData.MaxAngle</c> and <c>MozaData.Limit</c>, populated by
    /// the standard <c>base-max-angle</c> / <c>base-limit</c> read
    /// pipeline. If those values haven't been read yet (cold-start
    /// race), the reply still goes out with zeros — matches PitHouse
    /// behaviour for an un-probed wheelbase.
    /// </para>
    /// </summary>
    internal sealed class SteerLockReadHandler : IPitHousePacketHandler
    {
        public int PacketId => 4;
        public string Name => "SteerLock read";

        private readonly MozaData _data;

        public SteerLockReadHandler(MozaData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public void Handle(PitHousePacket request, PitHouseReplyContext ctx)
        {
            if (ctx.ReplyPort == null || ctx.ReplyPort.Value <= 0)
            {
                ctx.Summary = "dropped — no ReplyPort";
                MozaLog.Debug("[PitHouseUdp] PacketId 4: no ReplyPort — dropping (RSF treats listen-port==0 as abort)");
                return;
            }

            // Sample the cache once so both fields reflect the same instant.
            // Volatile reads on _data.MaxAngle / _data.Limit are inexpensive
            // and Phase 1 has only one writer thread on these cells. The
            // wire stores half-degrees (1 raw = 2°); convert before
            // replying — RSF and other third-party clients display the
            // reply value as degrees.
            int maxDeg = _data.MaxAngle * 2;
            int limitDeg = _data.Limit * 2;

            byte[] reply = BuildReplyEnvelope(maxDeg, limitDeg);
            ctx.SendReply(reply);

            ctx.Summary = $"max={maxDeg}° limit={limitDeg}° → :{ctx.ReplyPort}";
            MozaLog.Debug(
                $"[PitHouseUdp] SteerLock read max={maxDeg}° limit={limitDeg}° → {ctx.OriginalSender.Address}:{ctx.ReplyPort}");
        }

        /// <summary>
        /// Build the reply CBOR by hand — our <see cref="CborWriter"/>
        /// helpers don't accept nested-map values, so we wrap the
        /// pre-encoded inner <c>Payload</c> map in a tiny outer envelope
        /// (one entry: key "Payload", value = inner-map bytes). Outer
        /// shape: `0xA1 0x67 "Payload" &lt;inner-map-bytes&gt;`.
        /// </summary>
        private static byte[] BuildReplyEnvelope(int maxAngle, int limitAngle)
        {
            // Inner map — uses the existing helper which preserves key
            // order matching the on-wire form. RSF parses by name so
            // order isn't strictly required, but consistency is cheap.
            byte[] innerMap = CborWriter.WriteMap(new[]
            {
                new KeyValuePair<string, object>("MotGetSteer_MaximumAngle", maxAngle),
                new KeyValuePair<string, object>("MotGetSteer_LimitAngle",   limitAngle),
            });

            // Outer: map of 1 pair { "Payload": innerMap }.
            //   0xA1               = major-type-5 (map), count=1 (low 5 bits)
            //   0x67               = major-type-3 (text), len=7
            //   "Payload" (7 ASCII bytes)
            //   <innerMap>         = pre-encoded map bytes
            using (var ms = new MemoryStream(2 + 7 + innerMap.Length))
            {
                ms.WriteByte(0xA1);
                ms.WriteByte(0x67);
                byte[] key = Encoding.ASCII.GetBytes("Payload");
                ms.Write(key, 0, key.Length);
                ms.Write(innerMap, 0, innerMap.Length);
                return ms.ToArray();
            }
        }
    }
}
