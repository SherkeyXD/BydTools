using BnkExtractor.Ww2ogg.Exceptions;

namespace BnkExtractor.Ww2ogg;

// integer of a certain number of bits, to allow reading just that many
// bits from the Bit_stream
public class Bit_uint
{
    private uint total;
    private byte BIT_SIZE;

    protected Bit_uint(byte bitSize)
    {
        this.BIT_SIZE = bitSize;
        this.total = 0;
        if (BIT_SIZE > 32)
        {
            throw new TooManyBitsException();
        }
    }

    protected Bit_uint(byte bitSize, uint v)
    {
        BIT_SIZE = bitSize;
        this.total = v;
        if (BIT_SIZE > 32)
        {
            throw new TooManyBitsException();
        }
        if ((v >> (BIT_SIZE - 1)) > 1U)
        {
            throw new IntTooBigException();
        }
    }

    public Bit_uint CopyFrom(uint v)
    {
        if ((v >> (BIT_SIZE - 1)) > 1U)
        {
            throw new IntTooBigException();
        }
        total = v;
        return this;
    }

    public static implicit operator uint(Bit_uint ImpliedObject)
    {
        return ImpliedObject.total;
    }

    public static explicit operator int(Bit_uint ImpliedObject)
    {
        return (int)ImpliedObject.total;
    }

    public static BitStream ReadBits(BitStream bstream, Bit_uint bui)
    {
        bui.total = 0;
        for (uint i = 0; i < bui.BIT_SIZE; i++)
        {
            if (bstream.GetBit())
            {
                bui.total |= (1U << (int)i);
            }
        }
        return bstream;
    }

    public static BitOggStream WriteBits(BitOggStream bstream, Bit_uint bui)
    {
        for (uint i = 0; i < bui.BIT_SIZE; i++)
        {
            bstream.put_bit((bui.total & (1U << (int)i)) != 0);
        }
        return bstream;
    }

    public override string ToString()
    {
        return total.ToString();
    }
}

public class Bit_uint1 : Bit_uint
{
    public Bit_uint1()
        : base(1) { }

    public Bit_uint1(uint v)
        : base(1, v) { }

    /// <summary>
    /// Convert to boolean where 0 is false and 1 is true
    /// </summary>
    /// <param name="value">The value to be converted</param>
    public static implicit operator bool(Bit_uint1 value) => value != 0;
}

public class Bit_uint2 : Bit_uint
{
    public Bit_uint2()
        : base(2) { }

    public Bit_uint2(uint v)
        : base(2, v) { }
}

public class Bit_uint3 : Bit_uint
{
    public Bit_uint3()
        : base(3) { }

    public Bit_uint3(uint v)
        : base(3, v) { }
}

public class Bit_uint4 : Bit_uint
{
    public Bit_uint4()
        : base(4) { }

    public Bit_uint4(uint v)
        : base(4, v) { }
}

public class Bit_uint5 : Bit_uint
{
    public Bit_uint5()
        : base(5) { }

    public Bit_uint5(uint v)
        : base(5, v) { }
}

public class Bit_uint6 : Bit_uint
{
    public Bit_uint6()
        : base(6) { }

    public Bit_uint6(uint v)
        : base(6, v) { }
}

public class Bit_uint8 : Bit_uint
{
    public Bit_uint8()
        : base(8) { }

    public Bit_uint8(uint v)
        : base(8, v) { }
}

public class Bit_uint10 : Bit_uint
{
    public Bit_uint10()
        : base(10) { }

    public Bit_uint10(uint v)
        : base(10, v) { }
}

public class Bit_uint14 : Bit_uint
{
    public Bit_uint14()
        : base(14) { }

    public Bit_uint14(uint v)
        : base(14, v) { }
}

public class Bit_uint16 : Bit_uint
{
    public Bit_uint16()
        : base(16) { }

    public Bit_uint16(uint v)
        : base(16, v) { }
}

public class Bit_uint24 : Bit_uint
{
    public Bit_uint24()
        : base(24) { }

    public Bit_uint24(uint v)
        : base(24, v) { }
}

public class Bit_uint32 : Bit_uint
{
    public Bit_uint32()
        : base(32) { }

    public Bit_uint32(uint v)
        : base(32, v) { }
}
