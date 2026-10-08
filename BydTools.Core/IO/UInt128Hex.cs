using System.Buffers.Binary;

namespace BydTools.Core.IO;

public static class UInt128Hex
{
    public static string ToHexLittleEndian(this UInt128 value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }
}
