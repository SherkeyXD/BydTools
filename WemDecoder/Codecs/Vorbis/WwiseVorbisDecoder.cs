using WemDecoder.Codecs.Vorbis.NVorbis;
using WemDecoder.Internal;

namespace WemDecoder.Codecs.Vorbis;

internal sealed class WwiseVorbisDecoder : SampleDecoder
{
    private const int MaxFramesPerRead = 4096;

    private readonly StreamDecoder _decoder;
    private readonly int _channels;
    private float[] _buffer = [];
    private bool _ended;

    private WwiseVorbisDecoder(StreamDecoder decoder, int channels)
    {
        _decoder = decoder;
        _channels = channels;
    }

    /// <summary>Port of the Vorbis case of vgmstream's <c>init_vgmstream_wwise_bnk</c>.</summary>
    public static WwiseVorbisDecoder Create(WemHeader header, ReadOnlyMemory<byte> file, out long sampleCount)
    {
        var data = file.Span;
        bool be = header.BigEndian;

        if (header.BlockSize != 0 || header.BitsPerSample != 0)
            throw new InvalidDataException("Vorbis: unexpected block size or bits per sample.");

        // Older Wwise (<= v52) keeps the codec setup in a 'vorb' chunk, newer in the fmt extradata.
        long extraOffset = header.VorbOffset != 0 ? header.VorbOffset : header.FmtOffset + 0x18;
        long extraSize = header.VorbOffset != 0 ? header.VorbSize : header.ExtraSize;

        (int dataOffsets, int blockOffsets, var version) = extraSize switch
        {
            0x2C or 0x28 => (0x18, 0, WwiseVorbisVersion.V34),
            0x34 => (0x18, 0x30, WwiseVorbisVersion.V48),
            0x32 => (0x18, 0x30, WwiseVorbisVersion.V52),
            0x2A => (0x10, 0x28, WwiseVorbisVersion.V53),
            0x30 => (0x10, 0x28, WwiseVorbisVersion.V62),
            _ => throw new InvalidDataException($"Vorbis: unknown extra size 0x{extraSize:X}."),
        };

        int numSamples = (int)Bytes.U32(data, extraOffset, be);
        uint seekSize = Bytes.U32(data, extraOffset + dataOffsets, be);
        uint audioOffset = Bytes.U32(data, extraOffset + dataOffsets + 0x04, be);
        int shortBlockExp = 0;
        int longBlockExp = 0;
        if (blockOffsets != 0)
        {
            shortBlockExp = Bytes.U8(data, extraOffset + blockOffsets);
            longBlockExp = Bytes.U8(data, extraOffset + blockOffsets + 0x01);
        }

        long startOffset = header.DataOffset;
        if (version == WwiseVorbisVersion.V48)
        {
            // v38 inlines full codebooks ("BCV" sync), v44 inlines reduced ones (a large setup).
            ushort setupSize = Bytes.U16(data, startOffset + seekSize, be);
            uint setupId = Bytes.U32(data, startOffset + seekSize + 0x06, bigEndian: true);
            if ((setupId & 0x00FFFFFF) == 0x00424356)
                version = WwiseVorbisVersion.V38;
            else if (setupSize > 0x200)
                version = WwiseVorbisVersion.V44;
        }

        long setupOffset = startOffset + seekSize;
        // Some files store metadata between the seek table and the setup.
        if (version >= WwiseVorbisVersion.V56 && header.MetaOffset != 0)
            setupOffset += Bytes.U32(data, header.MetaOffset + 0x04, be);

        long streamEnd = header.DataOffset + header.DataSize;
        uint dataSize = (uint)(header.DataSize - audioOffset);
        startOffset += audioOffset;

        // The codebook set isn't recorded in the header; the wrong one fails to rebuild.
        var decoder = TryOpen(file, header, version, shortBlockExp, longBlockExp, setupOffset, startOffset, streamEnd);
        if (decoder is null && version == WwiseVorbisVersion.V62)
            decoder = TryOpen(file, header, WwiseVorbisVersion.V56, shortBlockExp, longBlockExp, setupOffset, startOffset, streamEnd);
        if (decoder is null)
            throw new InvalidDataException("Vorbis: could not rebuild the Vorbis setup.");

        if (header.Prefetch)
            numSamples = WemHeader.ScalePrefetchSamples(numSamples, header.FileSize - startOffset, dataSize);

        sampleCount = numSamples;
        return decoder;
    }

    private static WwiseVorbisDecoder? TryOpen(ReadOnlyMemory<byte> file, WemHeader header, WwiseVorbisVersion version,
        int shortBlockExp, int longBlockExp, long setupOffset, long audioOffset, long streamEnd)
    {
        var layout = WwiseVorbisLayout.For(version, shortBlockExp, longBlockExp);
        var headers = WwiseVorbisSetup.TryBuild(file.Span, setupOffset, layout, header.Channels, header.SampleRate,
            shortBlockExp, longBlockExp, header.BigEndian);
        if (headers is null || !WwiseVorbisSetup.IsValidIdentification(headers.Identification))
            return null;

        var reader = new WwiseVorbisPacketReader(file, layout, header.BigEndian, audioOffset, streamEnd, headers);
        StreamDecoder decoder;
        try
        {
            decoder = new StreamDecoder(headers.Identification, headers.Comment, headers.Setup, reader.ReadNext);
        }
        catch (Exception)
        {
            return null;
        }

        if (decoder.Channels != header.Channels)
            return null;

        return new WwiseVorbisDecoder(decoder, decoder.Channels);
    }

    public override int Read(Span<short> destination)
    {
        if (_ended)
            return 0;

        int count = Math.Min(destination.Length / _channels, MaxFramesPerRead) * _channels;
        if (_buffer.Length < count)
            _buffer = new float[count];

        int read;
        try
        {
            read = _decoder.Read(_buffer.AsSpan(0, count));
        }
        catch (Exception)
        {
            // vgmstream never aborts mid-stream; the undecodable tail becomes silence.
            read = 0;
        }

        if (read <= 0)
        {
            _ended = true;
            return 0;
        }

        FloatToPcm16(_buffer.AsSpan(0, read), destination);
        return read / _channels;
    }
}
