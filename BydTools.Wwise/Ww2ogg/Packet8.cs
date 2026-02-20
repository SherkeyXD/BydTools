using BydTools.Wwise.Ww2ogg.Extensions;

namespace BydTools.Wwise.Ww2ogg;

/* Old 8 byte header */
public class Packet8
{
    private int _offset;
    private uint _size;
    private uint _absolute_granule;

    public Packet8(BinaryReader i, int o, bool little_endian)
    {
        this._offset = o;
        this._absolute_granule = 0;
        i.seekg(_offset);

        if (little_endian)
        {
            _size = EndianReadWriteMethods.Read32LE(i);
            _absolute_granule = EndianReadWriteMethods.Read32LE(i);
        }
        else
        {
            _size = EndianReadWriteMethods.Read32BE(i);
            _absolute_granule = EndianReadWriteMethods.Read32BE(i);
        }
    }

    public int header_size()
    {
        return 8;
    }

    public int offset()
    {
        return _offset + header_size();
    }

    public uint size()
    {
        return _size;
    }

    public uint granule()
    {
        return _absolute_granule;
    }

    public int next_offset()
    {
        return (int)(_offset + header_size() + _size);
    }
}
