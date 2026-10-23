using Concentus.Structs;
using WemDecoder.Internal;

namespace WemDecoder.Codecs.Opus;

/// <summary>
/// Wwise Opus (0x3041): raw Opus packets whose sizes are listed in the 'seek' chunk.
/// </summary>
/// <remarks>
/// vgmstream wraps the packets in Ogg Opus and decodes them with FFmpeg; this reproduces the
/// resulting OpusHead (pre-skip, stream layout) and FFmpeg's channel order.
/// </remarks>
internal sealed class WwiseOpusDecoder : SampleDecoder
{
    public const int SampleRate = 48000;
    private const int MaxFrameSize = 5760; // 120 ms

    // Wwise's "DeinterleaveAndRemap" order for mapping 1, per channel count.
    private static readonly byte[][] WwiseChannelMatrix =
    [
        [0],
        [0, 1],
        [0, 2, 1],
        [0, 1, 2, 3],
        [0, 4, 1, 2, 3],
        [0, 4, 1, 2, 3, 5],
        [0, 6, 1, 2, 3, 4, 5],
        [0, 6, 1, 2, 3, 4, 5, 7],
    ];

    // FFmpeg's ff_vorbis_channel_layout_offsets, applied to mapping family 1.
    private static readonly byte[][] VorbisChannelReorder =
    [
        [0],
        [0, 1],
        [0, 2, 1],
        [0, 1, 2, 3],
        [0, 2, 1, 3, 4],
        [0, 2, 1, 5, 3, 4],
        [0, 2, 1, 6, 5, 3, 4],
        [0, 2, 1, 7, 5, 6, 3, 4],
    ];

    private readonly ReadOnlyMemory<byte> _file;
    private readonly (int Offset, int Size)[] _packets;
    private readonly OpusMSDecoder _decoder;
    private readonly int _channels;
    private readonly float[] _pcm;
    private int _packetIndex;
    private int _pcmFrame;
    private int _pcmFrames;
    private int _skip;

    private WwiseOpusDecoder(ReadOnlyMemory<byte> file, (int, int)[] packets, OpusMSDecoder decoder, int channels, int skip)
    {
        _file = file;
        _packets = packets;
        _decoder = decoder;
        _channels = channels;
        _skip = skip;
        _pcm = new float[MaxFrameSize * channels];
    }

    /// <summary>Port of the OPUSWW case of vgmstream's <c>init_vgmstream_wwise_bnk</c>.</summary>
    public static WwiseOpusDecoder Create(WemHeader header, ReadOnlyMemory<byte> file, out long sampleCount)
    {
        var data = file.Span;
        bool be = header.BigEndian;
        int channels = header.Channels;

        if (header.BlockSize != 0 || header.BitsPerSample != 0)
            throw new InvalidDataException("Opus: unexpected block size or bits per sample.");
        if (header.SeekOffset == 0)
            throw new InvalidDataException("Opus: missing 'seek' chunk.");

        int fmt = header.FmtOffset;
        int numSamples = (int)Bytes.U32(data, fmt + 0x18, be);
        uint tableCount = Bytes.U32(data, fmt + 0x1C, be);
        int skip = Bytes.U16(data, fmt + 0x20, be);
        int mapping = Bytes.U8(data, fmt + 0x23);
        if (mapping == 1 && channels > 8)
            throw new NotSupportedException("Opus: channel mapping 1 with more than 8 channels.");
        if (Bytes.U8(data, fmt + 0x22) != 1)
            throw new NotSupportedException("Opus: unknown codec version.");

        long dataSize = header.DataSize;
        if (header.Prefetch)
        {
            numSamples = WemHeader.ScalePrefetchSamples(numSamples, header.FileSize - header.DataOffset, header.DataSize);
            dataSize = header.FileSize - header.DataOffset;
        }

        var (streams, coupled, channelMapping) = GetStreamLayout(header, mapping);
        var output = GetOutputMapping(channels, streams, coupled, channelMapping);
        if (output.Streams == 0)
            throw new InvalidDataException("Opus: invalid stream layout.");

        var packets = ReadPacketTable(data, header, tableCount, dataSize);

        OpusMSDecoder decoder;
        try
        {
            // The managed decoder is used directly: OpusCodecFactory would switch to a native
            // libopus when one is installed, making output machine-dependent.
#pragma warning disable CS0618
            decoder = new OpusMSDecoder(SampleRate, channels, output.Streams, output.Coupled, output.Mapping);
#pragma warning restore CS0618
        }
        catch (Exception ex) when (ex is ArgumentException or Concentus.OpusException)
        {
            throw new InvalidDataException("Opus: invalid stream layout.", ex);
        }

        sampleCount = numSamples;
        return new WwiseOpusDecoder(file, packets, decoder, channels, skip);
    }

    /// <summary>Stream and channel configuration as vgmstream passes it to its OpusHead.</summary>
    private static (int Streams, int Coupled, byte[] Mapping) GetStreamLayout(WemHeader header, int mapping)
    {
        int channels = header.Channels;
        var channelMapping = new byte[channels];

        if (header.ChannelType == 1 && mapping == 1)
        {
            // Coupled (stereo) streams per speaker layout, as in Wwise's ChannelConfigToMapping.
            int coupled = header.ChannelLayout switch
            {
                0x63F => 3,                   // 7.1 surround
                0x60F or 0x607 or 0x603 => 2, // 5.1 surround, 5.0 surround, quad (side)
                0x007 or 0x003 => 1,          // 2.1 (FL FR FC), stereo
                _ => 0,
            };

            for (int i = 0; i < channels; i++)
                channelMapping[i] = WwiseChannelMatrix[channels - 1][i];
            return (channels - coupled, coupled, channelMapping);
        }

        if (header.ChannelType != 0 && mapping == 255)
        {
            for (int i = 0; i < channels; i++)
                channelMapping[i] = (byte)i;
            return (channels, 0, channelMapping);
        }

        if (channels > 2)
            throw new NotSupportedException($"Opus: unknown channel mapping {mapping} for {channels} channels.");
        return (0, 0, channelMapping);
    }

    /// <summary>
    /// The decoder layout FFmpeg derives from that OpusHead, with its channel reordering folded into the mapping.
    /// </summary>
    private static (int Streams, int Coupled, byte[] Mapping) GetOutputMapping(int channels, int streams, int coupled, byte[] channelMapping)
    {
        // vgmstream writes mapping family 0 (implicit mono/stereo) unless there are several streams.
        if (channels <= 2 && streams <= 1)
            return (1, channels - 1, channels == 1 ? [0] : [0, 1]);

        if (channels > 8)
            throw new NotSupportedException("Opus: more than 8 channels with mapping family 1.");
        if (streams < 1 || coupled > streams || streams + coupled > 255)
            return (0, 0, []);

        bool mappingSet = Array.Exists(channelMapping, m => m != 0);
        var output = new byte[channels];
        for (int i = 0; i < channels; i++)
        {
            int source = VorbisChannelReorder[channels - 1][i];
            output[i] = mappingSet ? channelMapping[source] : (byte)source;
            if (output[i] != 255 && output[i] >= streams + coupled)
                return (0, 0, []);
        }

        return (streams, coupled, output);
    }

    private static (int, int)[] ReadPacketTable(ReadOnlySpan<byte> data, WemHeader header, uint tableCount, long dataSize)
    {
        long offset = header.DataOffset;
        long end = offset + dataSize;
        if (end > header.FileSize)
            throw new InvalidDataException("Opus: data extends past the end of the file.");

        var packets = new List<(int, int)>();
        for (uint i = 0; offset < end; i++)
        {
            int size = i < tableCount ? Bytes.U16(data, header.SeekOffset + i * 2L, bigEndian: false) : 0;
            if (size == 0)
                throw new InvalidDataException("Opus: seek table does not cover the data.");

            // A prefetch copy can cut the last packet short.
            if (offset + size > header.FileSize)
                break;

            packets.Add(((int)offset, (int)size));
            offset += size;
        }

        return [.. packets];
    }

    public override int Read(Span<short> destination)
    {
        int frames = destination.Length / _channels;
        int written = 0;
        while (written < frames)
        {
            if (_pcmFrames == 0 && !DecodeNextPacket())
                break;

            int count = Math.Min(frames - written, _pcmFrames);
            FloatToPcm16(_pcm.AsSpan(_pcmFrame * _channels, count * _channels), destination.Slice(written * _channels, count * _channels));
            _pcmFrame += count;
            _pcmFrames -= count;
            written += count;
        }

        return written;
    }

    private bool DecodeNextPacket()
    {
        while (_packetIndex < _packets.Length)
        {
            var (offset, size) = _packets[_packetIndex++];
            int decoded;
            try
            {
                decoded = _decoder.DecodeMultistream(_file.Span.Slice(offset, size), _pcm, MaxFrameSize, false);
            }
            catch (Exception)
            {
                // FFmpeg rejects the packet and vgmstream moves on to the next one.
                continue;
            }

            // OpusHead pre-skip: FFmpeg drops these samples from the start of the stream.
            int skipped = Math.Min(_skip, decoded);
            _skip -= skipped;
            _pcmFrame = skipped;
            _pcmFrames = decoded - skipped;
            if (_pcmFrames > 0)
                return true;
        }

        return false;
    }
}
