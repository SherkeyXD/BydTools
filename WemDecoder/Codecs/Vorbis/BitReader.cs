namespace WemDecoder.Codecs.Vorbis;

/// <summary>
/// Vorbis-style (LSB-first) bit reader. Bits past the end of the buffer read as zero.
/// </summary>
internal ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private long _position;

    public BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    /// <summary>Current position in bits.</summary>
    public readonly long Position => _position;

    public uint Read(int bits)
    {
        uint value = 0;
        int done = 0;
        while (done < bits)
        {
            long index = _position >> 3;
            int shift = (int)(_position & 7);
            int take = Math.Min(8 - shift, bits - done);
            uint current = index < _data.Length ? _data[(int)index] : 0u;
            value |= ((current >> shift) & ((1u << take) - 1)) << done;
            done += take;
            _position += take;
        }
        return value;
    }
}
