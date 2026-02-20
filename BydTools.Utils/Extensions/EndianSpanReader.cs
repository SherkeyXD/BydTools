using System.Buffers.Binary;
using System.Text;

namespace BydTools.Utils.Extensions;

/// <summary>
/// Extension methods on <see cref="ReadOnlySpan{T}"/> and <see cref="Span{T}"/> for reading
/// primitives and strings with explicit endianness.
/// Ported from BeyondToolsSRC with thread-safe design (no mutable static state).
/// </summary>
public static class EndianSpanReader
{
    public static ushort ReadUInt16LE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data);

    public static ushort ReadUInt16BE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt16BigEndian(data);

    public static short ReadInt16LE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadInt16LittleEndian(data);

    public static short ReadInt16BE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadInt16BigEndian(data);

    public static uint ReadUInt32LE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data);

    public static uint ReadUInt32BE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt32BigEndian(data);

    public static int ReadInt32LE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadInt32LittleEndian(data);

    public static int ReadInt32BE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadInt32BigEndian(data);

    public static long ReadInt64LE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadInt64LittleEndian(data);

    public static long ReadInt64BE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadInt64BigEndian(data);

    public static UInt128 ReadUInt128LE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt128LittleEndian(data);

    public static UInt128 ReadUInt128BE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt128BigEndian(data);

    public static float ReadSingleLE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadSingleLittleEndian(data);

    public static float ReadSingleBE(this ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadSingleBigEndian(data);

    public static string ReadStringToNull(this ReadOnlySpan<byte> data, int maxLength = 32767)
    {
        int limit = Math.Min(data.Length, maxLength);
        int count = data[..limit].IndexOf((byte)0);
        if (count < 0)
            count = limit;
        return Encoding.UTF8.GetString(data[..count]);
    }

    public static string ReadString(this ReadOnlySpan<byte> data, int length) =>
        Encoding.UTF8.GetString(data[..length]);

    // Span<byte> overloads that forward to ReadOnlySpan<byte>
    public static ushort ReadUInt16LE(this Span<byte> data) => ReadUInt16LE((ReadOnlySpan<byte>)data);
    public static ushort ReadUInt16BE(this Span<byte> data) => ReadUInt16BE((ReadOnlySpan<byte>)data);
    public static int ReadInt32LE(this Span<byte> data) => ReadInt32LE((ReadOnlySpan<byte>)data);
    public static int ReadInt32BE(this Span<byte> data) => ReadInt32BE((ReadOnlySpan<byte>)data);
    public static long ReadInt64LE(this Span<byte> data) => ReadInt64LE((ReadOnlySpan<byte>)data);
    public static long ReadInt64BE(this Span<byte> data) => ReadInt64BE((ReadOnlySpan<byte>)data);
    public static UInt128 ReadUInt128LE(this Span<byte> data) => ReadUInt128LE((ReadOnlySpan<byte>)data);
    public static UInt128 ReadUInt128BE(this Span<byte> data) => ReadUInt128BE((ReadOnlySpan<byte>)data);
    public static string ReadStringToNull(this Span<byte> data, int maxLength = 32767) =>
        ReadStringToNull((ReadOnlySpan<byte>)data, maxLength);
    public static string ReadString(this Span<byte> data, int length) =>
        ReadString((ReadOnlySpan<byte>)data, length);
}
