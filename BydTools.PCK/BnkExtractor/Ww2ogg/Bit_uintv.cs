using BnkExtractor.Ww2ogg.Exceptions;

namespace BnkExtractor.Ww2ogg;

// integer of a run-time specified number of bits
// bits from the Bit_stream
public class Bit_uintv
{
    private uint size;
    private uint total;

    public Bit_uintv(uint s)
    {
        this.size = s;
        this.total = 0;
        if (s > 32)
        {
            throw new TooManyBitsException();
        }
    }

    public Bit_uintv(uint s, uint v)
    {
        this.size = s;
        this.total = v;
        if (size > 32)
        {
            throw new TooManyBitsException();
        }
        if ((v >> (int)(size - 1U)) > 1U)
        {
            throw new IntTooBigException();
        }
    }

    public Bit_uintv CopyFrom(uint v)
    {
        if ((v >> (int)(size - 1U)) > 1U)
        {
            throw new IntTooBigException();
        }
        total = v;
        return this;
    }

    public static implicit operator uint(Bit_uintv ImpliedObject)
    {
        return ImpliedObject.total;
    }

    public static BitStream ReadBits(BitStream bstream, Bit_uintv bui)
    {
        bui.total = 0;
        for (uint i = 0; i < bui.size; i++)
        {
            if (bstream.GetBit())
            {
                bui.total |= (1U << (int)i);
            }
        }
        return bstream;
    }

    public static BitOggStream WriteBits(BitOggStream bstream, Bit_uintv bui)
    {
        for (uint i = 0; i < bui.size; i++)
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
