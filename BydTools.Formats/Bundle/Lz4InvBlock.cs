// Ported and adapted from Escartem/AnimeStudio (LZ4Inv.cs)
// Source: https://github.com/Escartem/AnimeStudio
// License: MIT
// Copyright (c) 2016 Radu; (c) 2016-2020 Perfare; (c) 2022-2024 Razmoth; (c) 2024-2026 Escartem

namespace BydTools.Formats.Bundle;

/// <summary>
/// Modified LZ4 block decoder (bit-interleaved token nibbles and big-endian match offset)
/// used by Arknights: Endfield VFS bundle data blocks (compression type 5).
/// </summary>
public static class Lz4InvBlock
{
    public static byte[] Decompress(ReadOnlySpan<byte> src, int uncompressedSize)
    {
        if (uncompressedSize < 0)
            throw new InvalidDataException("LZ4Inv uncompressed size is negative.");
        var dst = new byte[uncompressedSize];
        int written = Decompress(src, dst);
        if (written != uncompressedSize)
        {
            throw new InvalidDataException(
                $"LZ4Inv block decoded {written} bytes, header declared {uncompressedSize}."
            );
        }

        return dst;
    }

    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int s = 0;
        int d = 0;
        while (s < src.Length)
        {
            byte token = src[s++];
            int litBits = token & 0b00110011;
            int encBits = (token & 0b11001100) >> 2;
            int literal = (litBits & 0b11) | (litBits >> 2);
            int encCount = (encBits & 0b11) | (encBits >> 2);

            if (literal == 15)
                literal += ReadLength(src, ref s, dst.Length - d - literal);
            if ((uint)literal > (uint)(src.Length - s) || (uint)literal > (uint)(dst.Length - d))
                throw new InvalidDataException("LZ4Inv literal run exceeds the buffer.");

            src.Slice(s, literal).CopyTo(dst.Slice(d));
            s += literal;
            d += literal;
            if (s >= src.Length)
                break;

            if (s + 2 > src.Length)
                throw new InvalidDataException("LZ4Inv match offset is truncated.");
            int offset = (src[s] << 8) | src[s + 1];
            s += 2;
            if (offset <= 0 || offset > d)
                throw new InvalidDataException($"LZ4Inv match offset {offset} is outside the output.");

            if (encCount == 15)
                encCount += ReadLength(src, ref s, dst.Length - d - (encCount + 4));
            int match = encCount + 4;
            if ((uint)match > (uint)(dst.Length - d))
                throw new InvalidDataException("LZ4Inv match run exceeds the output.");

            int from = d - offset;
            if (offset >= match)
            {
                dst.Slice(from, match).CopyTo(dst.Slice(d));
                d += match;
            }
            else if (offset == 1)
            {
                dst.Slice(d, match).Fill(dst[from]);
                d += match;
            }
            else
            {
                for (int i = 0; i < match; i++)
                    dst[d++] = dst[from++];
            }
        }

        return d;
    }

    private static int ReadLength(ReadOnlySpan<byte> src, ref int s, int maxAdditional)
    {
        int length = 0;
        byte extra;
        do
        {
            if (s >= src.Length)
                throw new InvalidDataException("LZ4Inv length extension is truncated.");
            extra = src[s++];
            if (extra > maxAdditional - length)
                throw new InvalidDataException("LZ4Inv run exceeds the output.");
            length += extra;
        } while (extra == 255);

        return length;
    }
}
