namespace WemDecoder.Codecs.Vorbis;

/// <summary>
/// Vorbis-style (LSB-first) bit writer over a growable buffer.
/// </summary>
internal sealed class BitWriter
{
    private byte[] _buffer;
    private long _position;

    public BitWriter(int capacity = 0x100)
    {
        _buffer = new byte[capacity];
    }

    /// <summary>Current position in bits.</summary>
    public long Position => _position;

    public int ByteLength => (int)((_position + 7) >> 3);

    public void Reset()
    {
        Array.Clear(_buffer, 0, ByteLength);
        _position = 0;
    }

    public void Write(int bits, uint value)
    {
        if (bits == 0)
            return;
        if (bits < 32)
            value &= (1u << bits) - 1;

        EnsureCapacity(_position + bits);
        while (bits > 0)
        {
            int index = (int)(_position >> 3);
            int shift = (int)(_position & 7);
            int take = Math.Min(8 - shift, bits);
            _buffer[index] |= (byte)((value & ((1u << take) - 1)) << shift);
            value >>= take;
            bits -= take;
            _position += take;
        }
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        int shift = (int)(_position & 7);
        if (shift == 0)
        {
            EnsureCapacity(_position + bytes.Length * 8L);
            bytes.CopyTo(_buffer.AsSpan((int)(_position >> 3)));
            _position += bytes.Length * 8L;
            return;
        }

        EnsureCapacity(_position + bytes.Length * 8L);
        int index = (int)(_position >> 3);
        foreach (byte b in bytes)
        {
            _buffer[index] |= (byte)(b << shift);
            _buffer[++index] = (byte)(b >> (8 - shift));
        }
        _position += bytes.Length * 8L;
    }

    public void PadToByte()
    {
        int remainder = (int)(_position & 7);
        if (remainder != 0)
            Write(8 - remainder, 0);
    }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, ByteLength);

    public byte[] ToArray() => WrittenSpan.ToArray();

    private void EnsureCapacity(long bits)
    {
        // One spare byte so unaligned writes can always touch the next byte.
        long needed = ((bits + 7) >> 3) + 1;
        if (needed <= _buffer.Length)
            return;

        long size = Math.Max(needed, _buffer.Length * 2L);
        Array.Resize(ref _buffer, (int)size);
    }
}
