using System;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Immutable snapshot of the numeric data the FSR1 display driver is currently
    /// streaming for the active page — one <see cref="Fsr1VizRecord"/> per streamed
    /// record type, each carrying its fields' resolved spans, raw bytes, and post-
    /// scale/transform values. Published by <see cref="Fsr1DisplayDriver"/> (single
    /// writer, volatile reference) and read by the channel-mapping panel's viz strip.
    /// Plain carriers — no locks, matching the driver's volatile single-writer model.
    /// </summary>
    internal sealed class Fsr1VizSnapshot
    {
        public Fsr1VizRecord[] Records { get; }
        public Fsr1VizSnapshot(Fsr1VizRecord[] records) =>
            Records = records ?? Array.Empty<Fsr1VizRecord>();
    }

    /// <summary>One streamed record type's worth of data bytes [5..PayloadLen-1].</summary>
    internal sealed class Fsr1VizRecord
    {
        public byte Type { get; }
        public string Label { get; }
        public int PayloadLen { get; }
        public Fsr1VizField[] Fields { get; }

        public Fsr1VizRecord(byte type, string label, int payloadLen, Fsr1VizField[] fields)
        {
            Type = type;
            Label = label ?? "";
            PayloadLen = payloadLen;
            Fields = fields ?? Array.Empty<Fsr1VizField>();
        }
    }

    /// <summary>One field's resolved span, raw wire bytes, and post-scale value.</summary>
    internal sealed class Fsr1VizField
    {
        public string Label { get; }
        public int Start { get; }
        public int End { get; }
        public string Encoding { get; }
        public long Value { get; }
        public byte[] Bytes { get; }
        public bool IsSynthetic { get; }

        public Fsr1VizField(string label, int start, int end, string encoding,
                            long value, byte[] bytes, bool isSynthetic)
        {
            Label = label ?? "";
            Start = start;
            End = end;
            Encoding = encoding ?? "";
            Value = value;
            Bytes = bytes ?? Array.Empty<byte>();
            IsSynthetic = isSynthetic;
        }
    }
}
