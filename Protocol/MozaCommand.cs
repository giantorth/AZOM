using System;

namespace MozaPlugin.Protocol
{
    public class MozaCommand
    {
        public string Name { get; }
        public string DeviceType { get; }
        public byte ReadGroup { get; }
        public byte WriteGroup { get; }
        public byte[] CommandId { get; }
        public int PayloadBytes { get; }
        public string PayloadType { get; }

        public MozaCommand(string name, string deviceType, byte readGroup, byte writeGroup,
            byte[] commandId, int payloadBytes, string payloadType)
        {
            Name = name;
            DeviceType = deviceType;
            ReadGroup = readGroup;
            WriteGroup = writeGroup;
            CommandId = commandId;
            PayloadBytes = payloadBytes;
            PayloadType = payloadType;
        }

        /// <summary>Offset of the value payload in a built frame (after <c>7E len group dev cmd…</c>).</summary>
        internal int PayloadOffset => 4 + CommandId.Length;

        // Exact-size frame with header + command id filled and a zeroed payload;
        // the trailing checksum slot is set by SealFrame.
        private byte[] NewFrame(byte group, byte deviceId, int payloadLength)
        {
            var frame = new byte[4 + CommandId.Length + payloadLength + 1];
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)(CommandId.Length + payloadLength);
            frame[2] = group;
            frame[3] = deviceId;
            Buffer.BlockCopy(CommandId, 0, frame, 4, CommandId.Length);
            return frame;
        }

        /// <summary>
        /// Write frame with a zeroed <paramref name="payloadLength"/>-byte payload at
        /// <see cref="PayloadOffset"/>, for callers that fill it in place; finish with
        /// <see cref="SealFrame"/>. Null when the command is not writable.
        /// </summary>
        internal byte[]? NewWriteFrame(byte deviceId, int payloadLength)
            => WriteGroup == 0xFF ? null : NewFrame(WriteGroup, deviceId, payloadLength);

        /// <summary>Set the trailing wire checksum of a frame from <see cref="NewWriteFrame"/>.</summary>
        internal static byte[] SealFrame(byte[] frame)
        {
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        public byte[]? BuildReadMessage(byte deviceId)
        {
            if (ReadGroup == 0xFF) // 0xFF sentinel = not readable
                return null;

            // The PayloadBytes zeros are the array's default.
            return SealFrame(NewFrame(ReadGroup, deviceId, PayloadBytes));
        }

        public byte[]? BuildWriteMessage(byte deviceId, byte[] payload)
        {
            var frame = NewWriteFrame(deviceId, payload.Length);
            if (frame == null)
                return null;

            Buffer.BlockCopy(payload, 0, frame, PayloadOffset, payload.Length);
            return SealFrame(frame);
        }

        /// <summary>
        /// Convenience: build a write message with an integer value, encoded big-endian.
        /// </summary>
        public byte[]? BuildWriteInt(byte deviceId, int value)
        {
            var frame = NewWriteFrame(deviceId, PayloadBytes);
            if (frame == null)
                return null;

            int o = PayloadOffset;
            for (int i = PayloadBytes - 1; i >= 0; i--)
            {
                frame[o + i] = (byte)(value & 0xFF);
                value >>= 8;
            }
            return SealFrame(frame);
        }

        public static int ParseIntValue(byte[] data, int byteCount)
            => data == null ? 0 : ParseIntValue(data, 0, byteCount);

        /// <summary>Big-endian integer of <paramref name="byteCount"/> bytes at
        /// <paramref name="offset"/> — lets the response parser read straight from
        /// the wire frame without slicing a value array per message.</summary>
        public static int ParseIntValue(byte[] data, int offset, int byteCount)
        {
            if (data == null || byteCount <= 0 || offset < 0 || data.Length - offset < byteCount)
                return 0;

            int value = 0;
            for (int i = 0; i < byteCount; i++)
                value = (value << 8) | data[offset + i];
            return value;
        }

        public static float ParseFloatValue(byte[] data)
            => data == null ? 0f : ParseFloatValue(data, 0);

        /// <summary>Big-endian IEEE 754 single at <paramref name="offset"/>.</summary>
        public static float ParseFloatValue(byte[] data, int offset)
        {
            if (data == null || offset < 0 || data.Length - offset < 4)
                return 0f;

            return FloatBits.ReadF32BE(data, offset);
        }

        /// <summary>
        /// Convenience: build a write message with a float value, encoded big-endian IEEE 754.
        /// </summary>
        public byte[]? BuildWriteFloat(byte deviceId, float value)
        {
            var frame = NewWriteFrame(deviceId, 4);
            if (frame == null)
                return null;

            FloatBits.WriteF32BE(frame, PayloadOffset, value);
            return SealFrame(frame);
        }
    }
}
