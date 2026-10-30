using System.Runtime.InteropServices;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Allocation-free IEEE-754 float32 ⇄ bits. net48 has no
    /// <c>BitConverter.SingleToInt32Bits</c>; an overlapping-field union
    /// reinterprets the value without the <c>GetBytes</c> scratch array.
    /// </summary>
    internal static class FloatBits
    {
        [StructLayout(LayoutKind.Explicit)]
        private struct Union
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public uint U;
        }

        public static uint ToUInt32(float value)
        {
            var u = default(Union);
            u.F = value;
            return u.U;
        }

        public static float ToSingle(uint bits)
        {
            var u = default(Union);
            u.U = bits;
            return u.F;
        }

        /// <summary>Write <paramref name="value"/> big-endian at <paramref name="offset"/>.</summary>
        public static void WriteF32BE(byte[] dst, int offset, float value)
        {
            uint bits = ToUInt32(value);
            dst[offset] = (byte)(bits >> 24);
            dst[offset + 1] = (byte)(bits >> 16);
            dst[offset + 2] = (byte)(bits >> 8);
            dst[offset + 3] = (byte)bits;
        }

        /// <summary>Read a big-endian float32 at <paramref name="offset"/>.</summary>
        public static float ReadF32BE(byte[] src, int offset)
            => ToSingle(((uint)src[offset] << 24) | ((uint)src[offset + 1] << 16)
                        | ((uint)src[offset + 2] << 8) | src[offset + 3]);
    }
}
