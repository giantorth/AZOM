using System;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Builds the group-<c>0x42</c> fixed-schema display records for the FSR V1
    /// display wheel ("FSR1", firmware model-name <c>FSR</c>, hw <c>RS21-D03</c>),
    /// driven by <see cref="Fsr1DashboardCatalog"/>.
    ///
    /// This wheel does NOT use the standard tier-definition telemetry path. Instead
    /// the host pushes pre-computed display values as fixed-length records:
    /// <code>7E &lt;len&gt; 42 17 [type][b1][b2]&lt;fixed-layout data&gt; &lt;csum&gt;</code>
    /// Record types are enumerated once at startup with all-zero payloads
    /// ("declaration"); at runtime the live dashboards carry values at ~28 Hz.
    /// See docs/protocol/devices/wheel-0x17.md § Group 0x42.
    ///
    /// Frame STRUCTURE (types, lengths, the type-02 byte layout, checksum) is proven
    /// against the captures; field semantics live in the catalog. Pure / static so it
    /// is unit-testable without a connection — <see cref="TelemetrySender"/> owns the
    /// timer, value resolution, and send cadence.
    /// </summary>
    internal static class Fsr1DisplayEmitter
    {
        private const byte Group = 0x42;
        private const byte Dev = MozaProtocol.DeviceWheel; // 0x17

        /// <summary>Engine-running anchor value (group-0x42 0x4B flag byte).</summary>
        internal const byte EngineFlagValue = 0x4B;

        /// <summary>
        /// Startup declaration sweep: one all-zero frame per catalog record type
        /// (b1=b2=0). Byte-identical to the first-seen frames in the captures.
        /// </summary>
        internal static readonly byte[][] DeclarationSweep = BuildDeclarationSweep();

        /// <summary>Group-0x43 1-byte keepalive poll (<c>7E 01 43 17 00 E6</c>), ~1 Hz.</summary>
        internal static readonly byte[] Keepalive43 =
            BuildRaw(new byte[] { 0x7E, 0x01, 0x43, Dev, 0x00 });

        /// <summary>Highest valid dashboard/page index (0..18 observed).</summary>
        internal const int MaxDashboardIndex = 18;

        /// <summary>
        /// Host-initiated dashboard-select command — group <c>0x32</c>, cmd <c>0x81</c>,
        /// big-endian u32 page index (<c>7E 05 32 17 81 00 00 00 &lt;index&gt;</c>). The
        /// wheel switches its displayed dashboard to <paramref name="index"/> and acks
        /// with <c>B2 71 81 00 00 00 &lt;index&gt;</c>. Verified against
        /// <c>usb-capture/fsr1/dashboard change through pithouse…</c> (7/7). See
        /// docs/protocol/devices/wheel-0x17.md § Group 0x42 "Dashboard switching".
        /// </summary>
        internal static byte[] BuildSelect(int index)
        {
            if (index < 0) index = 0;
            if (index > MaxDashboardIndex) index = MaxDashboardIndex;
            return BuildRaw(new byte[]
            {
                0x7E, 0x05, 0x32, Dev, 0x81,
                (byte)((index >> 24) & 0xFF), (byte)((index >> 16) & 0xFF),
                (byte)((index >> 8) & 0xFF), (byte)(index & 0xFF),
            });
        }

        private static byte[][] BuildDeclarationSweep()
        {
            var all = Fsr1DashboardCatalog.Dashboards;
            var sweep = new byte[all.Length][];
            for (int i = 0; i < all.Length; i++)
                sweep[i] = BuildZeroRecord(all[i].RecordType, all[i].PayloadLen);
            return sweep;
        }

        /// <summary>Build an all-zero declaration frame (b1=b2=0) for a record type.</summary>
        private static byte[] BuildZeroRecord(byte type, int payloadLen)
        {
            var frame = NewFrame(type, payloadLen, b1: 0x00, b2: 0x00);
            Finish(frame);
            return frame;
        }

        /// <summary>All-zero declaration frame for one dashboard (sent on switch,
        /// mirroring PitHouse's re-declare-then-stream behaviour).</summary>
        internal static byte[] BuildDeclaration(Fsr1Dashboard dash) =>
            BuildZeroRecord(dash.RecordType, dash.PayloadLen);

        /// <summary>
        /// Diagnostic single-byte probe record: all data bytes are 0 except payload
        /// offset <paramref name="probeOffset"/>, set to <paramref name="value"/>
        /// (pass <c>probeOffset &lt; 5</c> for an all-zero anchored record). Streaming
        /// this while ramping <paramref name="value"/> and stepping the offset isolates
        /// one byte at a time: exactly one on-screen box animates, so the offset where
        /// the active box CHANGES is a field boundary and the run of consecutive offsets
        /// driving the SAME box is the field width. b1/b2 anchors are preserved so the
        /// wheel renders the dashboard's normal layout; offsets 3–4 stay 0 (padding).
        /// </summary>
        internal static byte[] BuildProbeRecord(Fsr1Dashboard dash, int probeOffset, int value)
        {
            if (dash == null) throw new ArgumentNullException(nameof(dash));
            var frame = NewFrame(dash.RecordType, dash.PayloadLen, dash.LiveB1, dash.LiveB2);
            if (probeOffset >= 5 && probeOffset < dash.PayloadLen)
                frame[4 + probeOffset] = (byte)(value & 0xFF);
            Finish(frame);
            return frame;
        }

        /// <summary>
        /// Build a live record for <paramref name="dash"/>. <paramref name="valueFor"/>
        /// returns each field's final wire integer (already resolved + scaled to the
        /// field's range); it is invoked once per field. <c>b1</c>/<c>b2</c> are the
        /// dashboard's per-config anchors (see <see cref="Fsr1Dashboard.LiveB1"/>/<see cref="Fsr1Dashboard.LiveB2"/>).
        /// </summary>
        internal static byte[] BuildRecord(Fsr1Dashboard dash, Func<Fsr1FieldDef, long> valueFor)
        {
            if (dash == null) throw new ArgumentNullException(nameof(dash));
            var frame = NewFrame(dash.RecordType, dash.PayloadLen, dash.LiveB1, dash.LiveB2);
            foreach (var f in dash.Fields)
                WriteField(frame, f, valueFor(f));
            Finish(frame);
            return frame;
        }

        /// <summary>
        /// Build a live record using a per-field <paramref name="layoutFor"/> resolver that
        /// returns the effective <c>(offsets, encoding, value)</c> — the driver supplies this
        /// so per-profile boundary/encoding overrides take effect. Falls through to the
        /// catalog layout for any field whose resolver returns a null offsets array.
        /// </summary>
        internal static byte[] BuildRecord(
            Fsr1Dashboard dash, Func<Fsr1FieldDef, (int[]? offsets, Fsr1Encoding enc, long value)> layoutFor)
            => BuildRecord(dash, dash?.Fields, layoutFor);

        /// <summary>
        /// As above, but packs an EXPLICIT field list rather than <c>dash.Fields</c> — the
        /// driver passes the composed list (catalog + synthetic split fields) so net-new
        /// fields are streamed too. <paramref name="fields"/> null falls back to the catalog.
        /// </summary>
        internal static byte[] BuildRecord(
            Fsr1Dashboard dash, System.Collections.Generic.IReadOnlyList<Fsr1FieldDef>? fields,
            Func<Fsr1FieldDef, (int[]? offsets, Fsr1Encoding enc, long value)> layoutFor)
        {
            if (dash == null) throw new ArgumentNullException(nameof(dash));
            var frame = NewFrame(dash.RecordType, dash.PayloadLen, dash.LiveB1, dash.LiveB2);
            var fs = fields ?? dash.Fields;
            int count = fs.Count;
            for (int i = 0; i < count; i++)
            {
                var f = fs[i];
                var (offsets, enc, value) = layoutFor(f);
                if (offsets == null) WriteField(frame, f, value);
                else WriteField(frame, offsets, enc, value);
            }
            Finish(frame);
            return frame;
        }

        /// <summary>
        /// Diagnostic field-span probe record: all data bytes are 0 except the contiguous
        /// span <paramref name="startOff"/>..<paramref name="endOff"/> (inclusive), each set
        /// to <paramref name="value"/>. Lights exactly the on-screen box(es) the edited
        /// field's CURRENT span feeds, so the user watches the box move/grow as the boundary
        /// steppers change the span. b1/b2 anchors are preserved; offsets 3–4 stay 0 (padding).
        /// </summary>
        internal static byte[] BuildProbeSpanRecord(Fsr1Dashboard dash, int startOff, int endOff, int value)
        {
            if (dash == null) throw new ArgumentNullException(nameof(dash));
            var frame = NewFrame(dash.RecordType, dash.PayloadLen, dash.LiveB1, dash.LiveB2);
            int max = dash.PayloadLen - 1;
            for (int o = startOff; o <= endOff; o++)
                if (o >= 5 && o <= max) frame[4 + o] = (byte)(value & 0xFF);
            Finish(frame);
            return frame;
        }

        // 7E | len | grp | dev | payload(payloadLen) | csum ; payload[0..2]=type,b1,b2
        private static byte[] NewFrame(byte type, int payloadLen, byte b1, byte b2)
        {
            var frame = new byte[4 + payloadLen + 1];
            frame[0] = 0x7E;
            frame[1] = (byte)payloadLen;
            frame[2] = Group;
            frame[3] = Dev;
            frame[4] = type;
            frame[5] = b1;
            frame[6] = b2;
            return frame;
        }

        private static void Finish(byte[] frame) =>
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);

        // payload offset N is at frame index 4+N. Def-keyed form uses the catalog layout.
        private static void WriteField(byte[] frame, Fsr1FieldDef f, long value) =>
            WriteField(frame, f.Offsets, f.Encoding, value);

        // Layout-explicit form: pack value into the given contiguous offsets with the given
        // encoding (used by the override path; offsets MUST match the encoding's width).
        private static void WriteField(byte[] frame, int[] offsets, Fsr1Encoding enc, long value)
        {
            long v = value;
            if (v < 0) v = 0;
            long cap = Fsr1DashboardCatalog.OutputMaxFor(enc, 0);
            if (v > cap) v = cap;
            int o0 = 4 + offsets[0];
            switch (enc)
            {
                case Fsr1Encoding.U8:
                    frame[o0] = (byte)(v & 0xFF);
                    break;
                case Fsr1Encoding.U16_BE:
                    frame[o0] = (byte)((v >> 8) & 0xFF);
                    frame[4 + offsets[1]] = (byte)(v & 0xFF);
                    break;
                case Fsr1Encoding.U16_LE:
                    frame[o0] = (byte)(v & 0xFF);
                    frame[4 + offsets[1]] = (byte)((v >> 8) & 0xFF);
                    break;
                case Fsr1Encoding.U24_BE:
                    frame[o0] = (byte)((v >> 16) & 0xFF);
                    frame[4 + offsets[1]] = (byte)((v >> 8) & 0xFF);
                    frame[4 + offsets[2]] = (byte)(v & 0xFF);
                    break;
            }
        }

        /// <summary>Append the wire checksum to a literal 7E…payload prefix.</summary>
        private static byte[] BuildRaw(byte[] prefix)
        {
            var frame = new byte[prefix.Length + 1];
            Array.Copy(prefix, frame, prefix.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }
    }
}
