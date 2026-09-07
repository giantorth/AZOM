using System;
using System.Collections.Generic;

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

        public byte[]? BuildReadMessage(byte deviceId)
        {
            if (ReadGroup == 0xFF) // 0xFF sentinel = not readable
                return null;

            int payloadLength = CommandId.Length + PayloadBytes;
            var msg = new List<byte>
            {
                MozaProtocol.MessageStart,
                (byte)payloadLength,
                ReadGroup,
                deviceId
            };
            msg.AddRange(CommandId);
            msg.AddRange(new byte[PayloadBytes]);

            msg.Add(MozaProtocol.CalculateWireChecksum(msg.ToArray()));
            return msg.ToArray();
        }

        public byte[]? BuildWriteMessage(byte deviceId, byte[] payload)
        {
            if (WriteGroup == 0xFF)
                return null;

            int payloadLength = CommandId.Length + payload.Length;
            var msg = new List<byte>
            {
                MozaProtocol.MessageStart,
                (byte)payloadLength,
                WriteGroup,
                deviceId
            };
            msg.AddRange(CommandId);
            msg.AddRange(payload);
            msg.Add(MozaProtocol.CalculateWireChecksum(msg.ToArray()));
            return msg.ToArray();
        }

        /// <summary>
        /// Convenience: build a write message with an integer value, encoded big-endian.
        /// </summary>
        public byte[]? BuildWriteInt(byte deviceId, int value)
        {
            var payload = new byte[PayloadBytes];
            for (int i = PayloadBytes - 1; i >= 0; i--)
            {
                payload[i] = (byte)(value & 0xFF);
                value >>= 8;
            }
            return BuildWriteMessage(deviceId, payload);
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

            // Big-endian on the wire; assemble the bits directly (no scratch array).
            int bits = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        /// <summary>
        /// Convenience: build a write message with a float value, encoded big-endian IEEE 754.
        /// </summary>
        public byte[]? BuildWriteFloat(byte deviceId, float value)
        {
            var le = BitConverter.GetBytes(value);
            var payload = new byte[] { le[3], le[2], le[1], le[0] };
            return BuildWriteMessage(deviceId, payload);
        }
    }
}
