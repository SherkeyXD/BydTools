// Derived from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License,
// Copyright (c) 2020 Andrew Ward. See THIRD-PARTY-NOTICES.md.
// Modified: DataPacket's bit reader over a byte array, without container metadata.

namespace WemDecoder.Codecs.Vorbis.NVorbis;

internal sealed class Packet(byte[] data) : IPacket
{
    private int _position;
    private ulong _bitBucket;
    private int _bitCount;
    private byte _overflowBits;

    public bool IsShort { get; private set; }

    public ulong ReadBits(int count)
    {
        if (count == 0)
            return 0UL;

        var value = TryPeekBits(count, out _);
        SkipBits(count);
        return value;
    }

    public ulong TryPeekBits(int count, out int bitsRead)
    {
        if (count < 0 || count > 64)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0)
        {
            bitsRead = 0;
            return 0UL;
        }

        while (_bitCount < count)
        {
            var val = ReadNextByte();
            if (val == -1)
            {
                bitsRead = _bitCount;
                return _bitBucket;
            }
            _bitBucket = (ulong)(val & 0xFF) << _bitCount | _bitBucket;
            _bitCount += 8;

            if (_bitCount > 64)
            {
                _overflowBits = (byte)(val >> (72 - _bitCount));
            }
        }

        var value = _bitBucket;
        if (count < 64)
        {
            value &= (1UL << count) - 1;
        }

        bitsRead = count;
        return value;
    }

    public void SkipBits(int count)
    {
        if (count <= 0)
            return;

        if (_bitCount > count)
        {
            // we still have bits left over...
            if (count > 63)
            {
                _bitBucket = 0;
            }
            else
            {
                _bitBucket >>= count;
            }
            if (_bitCount > 64)
            {
                var overflowCount = _bitCount - 64;
                _bitBucket |= (ulong)_overflowBits << (_bitCount - count - overflowCount);

                if (overflowCount > count)
                {
                    // ugh, we have to keep bits in overflow
                    _overflowBits >>= count;
                }
            }

            _bitCount -= count;
        }
        else if (_bitCount == count)
        {
            _bitBucket = 0UL;
            _bitCount = 0;
        }
        else
        {
            // we have to move more bits than we have available...
            count -= _bitCount;
            _bitCount = 0;
            _bitBucket = 0;

            while (count > 8)
            {
                if (ReadNextByte() == -1)
                {
                    count = 0;
                    IsShort = true;
                    break;
                }
                count -= 8;
            }

            if (count > 0)
            {
                var temp = ReadNextByte();
                if (temp == -1)
                {
                    IsShort = true;
                }
                else
                {
                    _bitBucket = (ulong)(temp >> count);
                    _bitCount = 8 - count;
                }
            }
        }
    }

    private int ReadNextByte() => _position < data.Length ? data[_position++] : -1;
}
