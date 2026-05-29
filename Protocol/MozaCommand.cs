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
        {
            if (data == null || data.Length < byteCount)
                return 0;

            int value = 0;
            for (int i = 0; i < byteCount; i++)
                value = (value << 8) | data[i];
            return value;
        }

        public static float ParseFloatValue(byte[] data)
        {
            if (data == null || data.Length < 4)
                return 0f;

            // Big-endian to little-endian conversion
            var bytes = new byte[4];
            bytes[0] = data[3];
            bytes[1] = data[2];
            bytes[2] = data[1];
            bytes[3] = data[0];
            return BitConverter.ToSingle(bytes, 0);
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
