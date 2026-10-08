using System.Buffers.Binary;

namespace BydTools.Audio.Pck;

/// <summary>Counter XOR used by the VFS layer on top of AKPK.</summary>
public static class PckCipher
{
    private const uint ConstM = 0x04E11C23;
    private const uint ConstX = 0x9C5A0B29;

    public static void DecipherInPlace(Span<byte> data, uint seed, int size, int offsetToFileStart)
    {
        if (size == 0)
            return;
        if ((uint)size > (uint)data.Length)
            throw new ArgumentOutOfRangeException(nameof(size));

        static uint GenerateKey(uint counter)
        {
            uint val = ((counter & 0xFF) ^ ConstX) * ConstM;
            val = (val ^ ((counter >> 8) & 0xFF)) * ConstM;
            val = (val ^ ((counter >> 16) & 0xFF)) * ConstM;
            val = (val ^ ((counter >> 24) & 0xFF)) * ConstM;
            return val;
        }

        int pos = 0;
        uint baseCounter = seed + (uint)(offsetToFileStart >> 2);
        int alignedOffset = offsetToFileStart & 0b11;

        if (alignedOffset > 0)
        {
            uint key = GenerateKey(baseCounter);
            Span<byte> keyBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(keyBytes, key);
            int leading = Math.Min(4 - alignedOffset, size);
            for (int i = 0; i < leading; i++)
                data[pos++] ^= keyBytes[alignedOffset + i];
            baseCounter++;
        }

        int alignedSize = (size - pos) & ~0b11;
        int blocks = alignedSize / 4;
        for (int block = 0; block < blocks; block++)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(
                data.Slice(pos, 4),
                word ^ GenerateKey(baseCounter + (uint)block)
            );
            pos += 4;
        }

        if (pos < size)
        {
            uint key = GenerateKey(baseCounter + (uint)blocks);
            Span<byte> keyBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(keyBytes, key);
            for (int i = 0; i < size - pos; i++)
                data[pos + i] ^= keyBytes[i];
        }
    }
}
