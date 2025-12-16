using System.IO;
using BnkExtractor.Ww2ogg.Extensions;

namespace BnkExtractor.Ww2ogg;

/* Modern 2 or 6 byte header */
public class Packet
{
    private int _offset;
    private ushort _size;
    private uint _absolute_granule;
    private bool _no_granule;

    public Packet(BinaryReader i, int o, bool little_endian, bool no_granule = false)
    {
        this._offset = o;
        this._absolute_granule = 0;
        this._no_granule = no_granule;
        i.seekg(_offset);

        if (little_endian)
        {
            _size = EndianReadWriteMethods.Read16LE(i);
            if (!_no_granule)
            {
                _absolute_granule = EndianReadWriteMethods.Read32LE(i);
            }
        }
        else
        {
            _size = EndianReadWriteMethods.Read16BE(i);
            if (!_no_granule)
            {
                _absolute_granule = EndianReadWriteMethods.Read32BE(i);
            }
        }
    }

    public int header_size()
    {
        return _no_granule ? 2 : 6;
    }

    public int offset()
    {
        return _offset + header_size();
    }

    public ushort size()
    {
        return _size;
    }

    public uint granule()
    {
        return _absolute_granule;
    }

    public int next_offset()
    {
        return _offset + header_size() + _size;
    }
}
