using WemDecoder.Internal;

namespace WemDecoder.Codecs.Vorbis;

/// <summary>
/// Reads Wwise audio packets and rebuilds them into standard Vorbis packets
/// (vgmstream's <c>vorbis_custom_parse_packet_wwise</c> and <c>ww2ogg_generate_vorbis_packet</c>).
/// </summary>
internal sealed class WwiseVorbisPacketReader
{
    private const int MaxPacketRead = 0x8000;
    private const int MaxRebuiltPacket = 0xFFFF;

    private readonly ReadOnlyMemory<byte> _file;
    private readonly WwiseVorbisLayout _layout;
    private readonly bool _bigEndian;
    private readonly long _streamEnd;
    private readonly bool[] _modeBlockFlags;
    private readonly int _modeBits;
    private readonly BitWriter _writer = new(0x1000);
    private long _offset;
    private bool _previousBlockFlag;
    private bool _ended;

    public WwiseVorbisPacketReader(ReadOnlyMemory<byte> file, WwiseVorbisLayout layout, bool bigEndian, long audioOffset, long streamEnd, WwiseVorbisHeaders headers)
    {
        _file = file;
        _layout = layout;
        _bigEndian = bigEndian;
        _offset = audioOffset;
        _streamEnd = streamEnd;
        _modeBlockFlags = headers.ModeBlockFlags;
        _modeBits = headers.ModeBits;
    }

    /// <returns>The next rebuilt packet, or <see langword="null"/> at the end of the stream.</returns>
    public byte[]? ReadNext()
    {
        var file = _file.Span;
        int headerSize = _layout.HeaderSize;
        while (!_ended && _offset < _streamEnd)
        {
            if (_offset + headerSize > file.Length)
                break;

            uint packetSize = _layout.PacketHeader == WwisePacketHeader.Type8
                ? Bytes.U32(file, _offset, _bigEndian)
                : Bytes.U16(file, _offset, _bigEndian);
            long packetOffset = _offset + headerSize;
            _offset = packetOffset + packetSize;

            if (packetSize == 0)
                continue;

            // Modified packets also need the first byte of the next packet.
            long readSize = packetSize + (_layout.ModifiedPackets ? headerSize + 1 : 0);
            if (readSize > MaxPacketRead)
                continue;

            long available = Math.Min(readSize, file.Length - packetOffset);
            if (available < packetSize)
                break;

            var payload = file.Slice((int)packetOffset, (int)packetSize);
            byte[] packet;
            if (_layout.ModifiedPackets)
            {
                bool hasNext = available == readSize;
                byte nextFirstByte = hasNext ? file[(int)(packetOffset + packetSize + headerSize)] : (byte)0;
                packet = RebuildModified(payload, hasNext, nextFirstByte);
            }
            else
            {
                packet = payload.ToArray();
            }

            if (packet.Length < MaxRebuiltPacket)
                return packet;
        }

        _ended = true;
        return null;
    }

    /// <summary>
    /// Restores the packet type bit and, for long windows, the previous/next window flags.
    /// </summary>
    private byte[] RebuildModified(ReadOnlySpan<byte> payload, bool hasNext, byte nextFirstByte)
    {
        var w = _writer;
        w.Reset();
        var r = new BitReader(payload);

        w.Write(1, 0);
        uint mode = r.Read(_modeBits);
        w.Write(_modeBits, mode);
        uint remainder = r.Read(8 - _modeBits);

        bool blockFlag = _modeBlockFlags[mode];
        if (blockFlag)
        {
            // Without a next packet (end of file) the next window is assumed short.
            bool nextBlockFlag = hasNext && _modeBlockFlags[nextFirstByte & ((1 << _modeBits) - 1)];
            w.Write(1, _previousBlockFlag ? 1u : 0u);
            w.Write(1, nextBlockFlag ? 1u : 0u);
        }
        _previousBlockFlag = blockFlag;

        w.Write(8 - _modeBits, remainder);
        w.WriteBytes(payload[1..]);
        w.PadToByte();
        return w.ToArray();
    }
}
