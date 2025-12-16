namespace BnkExtractor.Ww2ogg;

// host-endian-neutral integer reading
// using an istream, pull off individual bits with get_bit (LSB first)
public class BitStream
{
    private BinaryReader @is;

    private byte bit_buffer;
    private uint bits_left;
    private uint totalBitsRead;

    public BitStream(BinaryReader _is)
    {
        this.@is = _is;
        this.bit_buffer = 0;
        this.bits_left = 0;
        this.totalBitsRead = 0;
    }

    public bool GetBit()
    {
        if (bits_left == 0)
        {
            bit_buffer = @is.ReadByte();
            bits_left = 8;
        }
        totalBitsRead++;
        bits_left--;
        return ((bit_buffer & (0x80 >> (int)bits_left)) != 0);
    }

    public uint GetTotalBitsRead()
    {
        return totalBitsRead;
    }
}
