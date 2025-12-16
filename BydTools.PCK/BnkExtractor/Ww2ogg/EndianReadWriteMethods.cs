namespace BnkExtractor.Ww2ogg;

internal static class EndianReadWriteMethods
{
    public static int ilog(uint v)
    {
        int ret = 0;
        while (v != 0)
        {
            ret++;
            v >>= 1;
        }
        return (ret);
    }

    public static uint Read32LE(byte[] b)
    {
        uint v = 0;
        for (int i = 3; i >= 0; i--)
        {
            v <<= 8;
            v |= b[i];
        }

        return v;
    }

    public static uint Read32LE(BinaryReader reader)
    {
        return Read32LE(reader.ReadBytes(4));
    }

    internal static void Write32LE(byte[] b, int offset, uint v)
    {
        if (b == null)
            throw new ArgumentNullException(nameof(b));
        if (offset < 0 || offset >= b.Length - 4)
            throw new ArgumentOutOfRangeException(nameof(offset));
        byte[] buffer = new byte[4];
        Write32LE(buffer, v);
        Array.Copy(buffer, 0, b, offset, 4);
    }

    public static void Write32LE(byte[] b, uint v)
    {
        for (int i = 0; i < 4; i++)
        {
            b[i] = (byte)(v & 0xFF);
            v >>= 8;
        }
    }

    public static void Write32LE(BinaryWriter writer, uint v)
    {
        byte[] b = new byte[4];

        Write32LE(b, v);

        writer.Write(b);
    }

    public static ushort Read16LE(byte[] b)
    {
        ushort v = 0;
        for (int i = 1; i >= 0; i--)
        {
            v <<= 8;
            v |= b[i];
        }

        return v;
    }

    public static ushort Read16LE(BinaryReader reader)
    {
        return Read16LE(reader.ReadBytes(2));
    }

    public static void Write16LE(byte[] b, ushort v)
    {
        for (int i = 0; i < 2; i++)
        {
            b[i] = (byte)(v & 0xFF);
            v >>= 8;
        }
    }

    public static void Write16LE(BinaryWriter writer, ushort v)
    {
        byte[] b = new byte[2];

        Write16LE(b, v);

        writer.Write(b);
    }

    public static uint Read32BE(byte[] b)
    {
        uint v = 0;
        for (int i = 0; i < 4; i++)
        {
            v <<= 8;
            v |= b[i];
        }

        return v;
    }

    public static uint Read32BE(BinaryReader reader)
    {
        return Read32BE(reader.ReadBytes(4));
    }

    public static void Write32BE(byte[] b, uint v)
    {
        for (int i = 3; i >= 0; i--)
        {
            b[i] = (byte)(v & 0xFF);
            v >>= 8;
        }
    }

    public static void Write32BE(BinaryWriter writer, uint v)
    {
        byte[] b = new byte[4];

        Write32BE(b, v);

        writer.Write(b);
    }

    public static ushort Read16BE(byte[] b)
    {
        ushort v = 0;
        for (int i = 0; i < 2; i++)
        {
            v <<= 8;
            v |= b[i];
        }

        return v;
    }

    public static ushort Read16BE(BinaryReader reader)
    {
        return Read16BE(reader.ReadBytes(2));
    }

    public static void Write16BE(byte[] b, ushort v)
    {
        for (int i = 1; i >= 0; i--)
        {
            b[i] = (byte)(v & 0xFF);
            v >>= 8;
        }
    }

    public static void Write16BE(BinaryWriter writer, ushort v)
    {
        byte[] b = new byte[2];

        Write16BE(b, v);

        writer.Write(b);
    }
}
