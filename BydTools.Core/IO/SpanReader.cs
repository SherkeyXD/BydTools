using System.Buffers.Binary;
using System.Text;

namespace BydTools.Core.IO;

/// <summary>
/// Bounds-checked cursor over a byte span. Seeking past the buffer throws
/// <see cref="InvalidDataException"/> instead of allocating or wrapping.
/// </summary>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _span;
    private int _position;

    public SpanReader(ReadOnlySpan<byte> span)
    {
        _span = span;
        _position = 0;
    }

    public int Position => _position;
    public int Length => _span.Length;
    public int Remaining => _span.Length - _position;

    public void Seek(int position)
    {
        if ((uint)position > (uint)_span.Length)
        {
            throw new InvalidDataException(
                $"Seek to {position} is outside the buffer ({_span.Length} bytes)."
            );
        }

        _position = position;
    }

    public void Skip(int count)
    {
        if (count < 0)
            throw new InvalidDataException($"Cannot skip {count} bytes at offset {_position}.");
        Seek(_position + count);
    }

    /// <summary>Advances to the next multiple of <paramref name="boundary"/> (2, 4, or 8).</summary>
    public void Align(int boundary)
    {
        int aligned = (_position + (boundary - 1)) & ~(boundary - 1);
        Seek(aligned);
    }

    public byte ReadByte() => Take(1)[0];

    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count);

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public uint ReadUInt32BigEndian() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public UInt128 ReadUInt128() => BinaryPrimitives.ReadUInt128LittleEndian(Take(16));

    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public bool ReadBoolean() => ReadByte() != 0;

    public string ReadUtf8(int length) => Encoding.UTF8.GetString(Take(length));

    public string ReadNullTerminatedUtf8()
    {
        int start = _position;
        int relative = _span[start..].IndexOf((byte)0);
        if (relative < 0)
            throw new InvalidDataException($"Unterminated string at offset {start}.");

        string value = Encoding.UTF8.GetString(_span.Slice(start, relative));
        _position = start + relative + 1;
        return value;
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || (uint)count > (uint)Remaining)
        {
            throw new InvalidDataException(
                $"Need {count} bytes at offset {_position}, buffer length is {_span.Length}."
            );
        }

        var slice = _span.Slice(_position, count);
        _position += count;
        return slice;
    }
}
