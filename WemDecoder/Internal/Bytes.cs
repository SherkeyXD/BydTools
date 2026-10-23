using System.Buffers.Binary;

namespace WemDecoder.Internal;

/// <summary>
/// Endian-aware reads that return all bits set when out of range, like vgmstream's read_uXX helpers.
/// </summary>
internal static class Bytes
{
    public static byte U8(ReadOnlySpan<byte> data, long offset) =>
        (ulong)offset < (ulong)data.Length ? data[(int)offset] : byte.MaxValue;

    public static ushort U16(ReadOnlySpan<byte> data, long offset, bool bigEndian)
    {
        if (offset < 0 || offset > data.Length - 2)
            return ushort.MaxValue;

        var bytes = data.Slice((int)offset, 2);
        return bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    public static uint U32(ReadOnlySpan<byte> data, long offset, bool bigEndian)
    {
        if (offset < 0 || offset > data.Length - 4)
            return uint.MaxValue;

        var bytes = data.Slice((int)offset, 4);
        return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
