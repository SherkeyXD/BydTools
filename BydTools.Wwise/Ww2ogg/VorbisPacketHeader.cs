namespace BydTools.Wwise.Ww2ogg;

public class VorbisPacketHeader
{
    private byte type;

    private static char[] vorbis_str = new char[] { 'v', 'o', 'r', 'b', 'i', 's' };

    public VorbisPacketHeader(byte t)
    {
        this.type = t;
    }

    public static BitOggStream WriteHeader(BitOggStream bstream, VorbisPacketHeader vph)
    {
        Bit_uint8 t = new(vph.type);
        Bit_uint.WriteBits(bstream, t);

        for (uint i = 0; i < 6; i++)
        {
            Bit_uint8 c = new(vorbis_str[i]);
            Bit_uint.WriteBits(bstream, c);
        }

        return bstream;
    }
}
