// Derived from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License,
// Copyright (c) 2020 Andrew Ward. See THIRD-PARTY-NOTICES.md.
// Modified: header setup and the overlap/output loop only (no container, seeking, stats or tags);
// undecodable packets are skipped and the last window is never drained, as in libvorbis.

namespace WemDecoder.Codecs.Vorbis.NVorbis;

/// <summary>
/// Decodes a Vorbis stream given its three header packets, then audio packets.
/// </summary>
internal sealed class StreamDecoder
{
    private static ReadOnlySpan<byte> PacketSignatureStream => [0x01, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73, 0x00, 0x00, 0x00, 0x00];
    private static ReadOnlySpan<byte> PacketSignatureComments => [0x03, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73];
    private static ReadOnlySpan<byte> PacketSignatureBooks => [0x05, 0x76, 0x6f, 0x72, 0x62, 0x69, 0x73];

    private readonly Func<byte[]?> _nextPacket;

    private int _channels;
    private int _block0Size;
    private int _block1Size;
    private IMode[] _modes = [];
    private int _modeFieldBits;

    private float[][]? _nextPacketBuf;
    private float[][]? _prevPacketBuf;
    private int _prevPacketStart;
    private int _prevPacketEnd;
    private int _prevPacketStop;
    private bool _ended;

    /// <param name="identification">Identification header packet.</param>
    /// <param name="comment">Comment header packet.</param>
    /// <param name="setup">Setup header packet.</param>
    /// <param name="nextPacket">Returns the next audio packet, or <see langword="null"/> at the end of the stream.</param>
    /// <exception cref="InvalidDataException">The headers are invalid.</exception>
    public StreamDecoder(byte[] identification, byte[] comment, byte[] setup, Func<byte[]?> nextPacket)
    {
        _nextPacket = nextPacket;
        if (!LoadStreamHeader(new Packet(identification)) || !ValidateHeader(new Packet(comment), PacketSignatureComments))
            throw new InvalidDataException("Invalid Vorbis header.");
        LoadBooks(new Packet(setup));
    }

    public int Channels => _channels;

    public int SampleRate { get; private set; }

    #region Init

    private static bool ValidateHeader(IPacket packet, ReadOnlySpan<byte> expected)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != packet.ReadBits(8))
            {
                return false;
            }
        }
        return true;
    }

    private bool LoadStreamHeader(IPacket packet)
    {
        if (!ValidateHeader(packet, PacketSignatureStream))
        {
            return false;
        }

        _channels = (byte)packet.ReadBits(8);
        SampleRate = (int)packet.ReadBits(32);
        packet.SkipBits(96); // bitrates

        _block0Size = 1 << (int)packet.ReadBits(4);
        _block1Size = 1 << (int)packet.ReadBits(4);
        return true;
    }

    private void LoadBooks(IPacket packet)
    {
        if (!ValidateHeader(packet, PacketSignatureBooks))
        {
            throw new InvalidDataException("Invalid Vorbis setup header.");
        }

        var mdct = new Mdct();
        var huffman = new Huffman();

        // read the books
        var books = new ICodebook[packet.ReadBits(8) + 1];
        for (var i = 0; i < books.Length; i++)
        {
            books[i] = new Codebook();
            books[i].Init(packet, huffman);
        }

        // Vorbis never used this feature, so we just skip the appropriate number of bits
        var times = (int)packet.ReadBits(6) + 1;
        packet.SkipBits(16 * times);

        // read the floors
        var floors = new IFloor[packet.ReadBits(6) + 1];
        for (var i = 0; i < floors.Length; i++)
        {
            floors[i] = (int)packet.ReadBits(16) switch
            {
                0 => new Floor0(),
                1 => new Floor1(),
                _ => throw new InvalidDataException("Invalid floor type!"),
            };
            floors[i].Init(packet, _channels, _block0Size, _block1Size, books);
        }

        // read the residues
        var residues = new IResidue[packet.ReadBits(6) + 1];
        for (var i = 0; i < residues.Length; i++)
        {
            residues[i] = (int)packet.ReadBits(16) switch
            {
                0 => new Residue0(),
                1 => new Residue1(),
                2 => new Residue2(),
                _ => throw new InvalidDataException("Invalid residue type!"),
            };
            residues[i].Init(packet, _channels, books);
        }

        // read the mappings
        var mappings = new IMapping[packet.ReadBits(6) + 1];
        for (var i = 0; i < mappings.Length; i++)
        {
            if (packet.ReadBits(16) != 0)
            {
                throw new InvalidDataException("Invalid mapping type!");
            }
            mappings[i] = new Mapping();
            mappings[i].Init(packet, _channels, floors, residues, mdct);
        }

        // read the modes
        _modes = new IMode[packet.ReadBits(6) + 1];
        for (var i = 0; i < _modes.Length; i++)
        {
            _modes[i] = new Mode();
            _modes[i].Init(packet, _channels, _block0Size, _block1Size, mappings);
        }

        // verify the closing bit
        if (!packet.ReadBit()) throw new InvalidDataException("Book packet did not end on correct bit!");

        // save off the number of bits to read to determine packet mode
        _modeFieldBits = Utils.ilog(_modes.Length - 1);
    }

    #endregion

    #region Decoding

    /// <summary>
    /// Reads interleaved samples; <paramref name="buffer"/>'s length must be a multiple of <see cref="Channels"/>.
    /// </summary>
    /// <returns>The number of samples written, 0 at the end of the stream.</returns>
    public int Read(Span<float> buffer)
    {
        var idx = 0;
        while (idx < buffer.Length)
        {
            // if we don't have any more valid data in the current packet, read in the next packet
            if (_prevPacketStart == _prevPacketEnd && !ReadNextPacket())
            {
                break;
            }

            var copyLen = Math.Min((buffer.Length - idx) / _channels, _prevPacketEnd - _prevPacketStart);
            for (; copyLen > 0; _prevPacketStart++, copyLen--)
            {
                for (var ch = 0; ch < _channels; ch++)
                {
                    buffer[idx++] = _prevPacketBuf![ch][_prevPacketStart];
                }
            }
        }

        return idx;
    }

    /// <returns><see langword="false"/> at the end of the stream.</returns>
    private bool ReadNextPacket()
    {
        while (!_ended)
        {
            var data = _nextPacket();
            if (data == null)
            {
                _ended = true;
                break;
            }

            // decode the next packet now so we can start overlapping with it
            var curPacket = DecodePacket(new Packet(data), out var startIndex, out var validLen, out var totalLen);
            if (curPacket == null)
            {
                continue;
            }

            // start overlapping (if we don't have an previous packet data, just loop and the previous packet logic will handle things appropriately)
            if (_prevPacketBuf != null)
            {
                // overlap the first samples in the packet with the previous packet, then loop
                OverlapBuffers(_prevPacketBuf, curPacket, _prevPacketStart, _prevPacketStop, startIndex, _channels);
                _prevPacketStart = startIndex;
            }
            else
            {
                // first packet, so it doesn't have any good data before the valid length
                _prevPacketStart = validLen;
            }

            // keep the old buffer so the GC doesn't have to reallocate every packet
            _nextPacketBuf = _prevPacketBuf;

            // save off our current packet's data for the next pass
            _prevPacketEnd = validLen;
            _prevPacketStop = totalLen;
            _prevPacketBuf = curPacket;
            if (_prevPacketStart < _prevPacketEnd)
            {
                return true;
            }
        }

        return false;
    }

    private float[][]? DecodePacket(IPacket packet, out int packetStartindex, out int packetValidLength, out int packetTotalLength)
    {
        packetStartindex = 0;
        packetValidLength = 0;
        packetTotalLength = 0;

        // make sure the packet starts with a 0 bit as per the spec
        if (packet.ReadBit())
        {
            return null;
        }

        var modeIndex = (int)packet.ReadBits(_modeFieldBits);
        if (modeIndex >= _modes.Length)
        {
            return null;
        }

        if (_nextPacketBuf == null)
        {
            _nextPacketBuf = new float[_channels][];
            for (var i = 0; i < _channels; i++)
            {
                _nextPacketBuf[i] = new float[_block1Size];
            }
        }

        return _modes[modeIndex].Decode(packet, _nextPacketBuf, out packetStartindex, out packetValidLength, out packetTotalLength)
            ? _nextPacketBuf
            : null;
    }

    private static void OverlapBuffers(float[][] previous, float[][] next, int prevStart, int prevLen, int nextStart, int channels)
    {
        for (; prevStart < prevLen; prevStart++, nextStart++)
        {
            for (var c = 0; c < channels; c++)
            {
                next[c][nextStart] += previous[c][prevStart];
            }
        }
    }

    #endregion
}
