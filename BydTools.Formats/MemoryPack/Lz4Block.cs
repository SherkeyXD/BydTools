namespace BydTools.Formats.MemoryPack;

/// <summary>
/// LZ4 block decoder (not the frame format). Used by Beyond StringPool blobs.
/// </summary>
static class Lz4Block
{
    public static byte[] Decompress(ReadOnlySpan<byte> src, int uncompressedSize)
    {
        if (uncompressedSize < 0)
            throw new InvalidDataException("LZ4 uncompressed size is negative.");
        var dst = new byte[uncompressedSize];
        int written = Decompress(src, dst);
        if (written != uncompressedSize)
        {
            throw new InvalidDataException(
                $"LZ4 block decoded {written} bytes, header declared {uncompressedSize}."
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
            int literal = token >> 4;
            if (literal == 15)
                literal += ReadLength(src, ref s);
            if ((uint)literal > (uint)(src.Length - s) || (uint)literal > (uint)(dst.Length - d))
                throw new InvalidDataException("LZ4 literal run exceeds the buffer.");
            src.Slice(s, literal).CopyTo(dst.Slice(d));
            s += literal;
            d += literal;
            if (s >= src.Length)
                break;

            if (s + 2 > src.Length)
                throw new InvalidDataException("LZ4 match offset is truncated.");
            int offset = src[s] | (src[s + 1] << 8);
            s += 2;
            if (offset <= 0 || offset > d)
                throw new InvalidDataException($"LZ4 match offset {offset} is outside the output.");

            int match = (token & 0x0F) + 4;
            if ((token & 0x0F) == 15)
                match += ReadLength(src, ref s);
            if ((uint)match > (uint)(dst.Length - d))
                throw new InvalidDataException("LZ4 match run exceeds the output.");
            int from = d - offset;
            for (int i = 0; i < match; i++)
                dst[d++] = dst[from++];
        }

        return d;
    }

    private static int ReadLength(ReadOnlySpan<byte> src, ref int s)
    {
        int length = 0;
        byte extra;
        do
        {
            if (s >= src.Length)
                throw new InvalidDataException("LZ4 length extension is truncated.");
            extra = src[s++];
            length += extra;
        } while (extra == 255);

        return length;
    }
}
