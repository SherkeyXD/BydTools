namespace WemDecoder.Internal;

/// <summary>
/// Wwise RIFF header, parsed the way vgmstream's <c>parse_wwise</c> (meta/wwise.c) does.
/// </summary>
internal sealed class WemHeader
{
    private const uint IdRiff = 0x52494646; // "RIFF"
    private const uint IdRifx = 0x52494658; // "RIFX"
    private const uint IdWave = 0x57415645; // "WAVE"
    private const uint IdXwma = 0x58574D41; // "XWMA"
    private const uint IdOggS = 0x4F676753; // "OggS"

    private const uint ChunkFmt = 0x666D7420;  // "fmt "
    private const uint ChunkXma2 = 0x584D4132; // "XMA2"
    private const uint ChunkData = 0x64617461; // "data"
    private const uint ChunkVorb = 0x766F7262; // "vorb"
    private const uint ChunkWiiH = 0x57696948; // "WiiH"
    private const uint ChunkSeek = 0x7365656B; // "seek"
    private const uint ChunkSmpl = 0x736D706C; // "smpl"
    private const uint ChunkMeta = 0x6D657461; // "meta"
    private const uint ChunkFact = 0x66616374; // "fact"

    private WemHeader()
    {
    }

    public bool BigEndian { get; private init; }
    public int FileSize { get; private init; }

    public int FmtOffset { get; private set; }
    public long FmtSize { get; private set; }
    public int DataOffset { get; private set; }
    /// <summary>Declared size, which may extend past the end of the file for prefetch streams.</summary>
    public long DataSize { get; private set; }
    public int VorbOffset { get; private set; }
    public long VorbSize { get; private set; }
    public int SeekOffset { get; private set; }
    public int MetaOffset { get; private set; }

    public ushort Format { get; private set; }
    public int Channels { get; private set; }
    public int SampleRate { get; private set; }
    public int BlockSize { get; private set; }
    public int BitsPerSample { get; private set; }
    public int ExtraSize { get; private set; }
    public uint ChannelLayout { get; private set; }
    public int ChannelType { get; private set; }

    /// <summary>Truncated stream (a .bnk prefetch copy of the real .wem).</summary>
    public bool Prefetch { get; private set; }
    public WemCodec Codec { get; private set; }

    public static WemHeader Parse(ReadOnlySpan<byte> data)
    {
        uint magic = Bytes.U32(data, 0x00, bigEndian: true);
        if (magic != IdRiff && magic != IdRifx)
            throw new InvalidDataException("Not a RIFF/RIFX file.");

        uint form = Bytes.U32(data, 0x08, bigEndian: true);
        if (form != IdWave && form != IdXwma)
            throw new InvalidDataException("RIFF form type is not WAVE.");

        var header = new WemHeader { BigEndian = magic == IdRifx, FileSize = data.Length };
        bool be = header.BigEndian;
        bool hasXma2 = false;

        // Chunks are not padded to even sizes, unlike regular RIFF.
        long current = 0x0C;
        while (current < data.Length && data.Length - current >= 0x08)
        {
            uint type = Bytes.U32(data, current, bigEndian: true);
            uint size = Bytes.U32(data, current + 0x04, be);
            if (type == uint.MaxValue || size == uint.MaxValue)
                break;

            int offset = (int)(current + 0x08);
            current = offset + (long)size;

            switch (type)
            {
                case ChunkFmt:
                    header.FmtOffset = offset;
                    header.FmtSize = size;
                    break;
                case ChunkXma2:
                    hasXma2 = true;
                    break;
                case ChunkData:
                    header.DataOffset = offset;
                    header.DataSize = size;
                    break;
                case ChunkVorb:
                    header.VorbOffset = offset;
                    header.VorbSize = size;
                    break;
                case ChunkSeek:
                    header.SeekOffset = offset;
                    break;
                case ChunkMeta:
                    header.MetaOffset = offset;
                    break;
                case ChunkWiiH:
                case ChunkSmpl:
                    break;
                case ChunkFact:
                    throw new InvalidDataException("Unexpected 'fact' chunk; not a Wwise RIFF.");
                default:
                    if (offset + (long)size > data.Length)
                        throw new InvalidDataException("Chunk extends past the end of the file (bad extraction?).");
                    break;
            }
        }

        if (hasXma2)
            throw new NotSupportedException("WEM codec XMA2 is not supported.");

        if (header.FmtSize < 0x10)
            throw new InvalidDataException("Missing or truncated 'fmt ' chunk.");

        int fmt = header.FmtOffset;
        header.Format = Bytes.U16(data, fmt + 0x00, be);
        header.Channels = Bytes.U16(data, fmt + 0x02, be);
        header.SampleRate = (int)Bytes.U32(data, fmt + 0x04, be);
        header.BlockSize = Bytes.U16(data, fmt + 0x0C, be);
        header.BitsPerSample = Bytes.U16(data, fmt + 0x0E, be);
        if (header.FmtSize > 0x10 && header.Format != 0x0165 && header.Format != 0x0166)
            header.ExtraSize = Bytes.U16(data, fmt + 0x10, be);

        if (header.ExtraSize >= 0x06)
        {
            // Newer Wwise packs channel count, config type and speaker mask into this field.
            uint layout = Bytes.U32(data, fmt + 0x14, be);
            if ((layout & 0xFF) == header.Channels)
            {
                header.ChannelType = (int)((layout >> 8) & 0x0F);
                layout >>= 12;
            }
            header.ChannelLayout = layout;
        }

        if (header.DataOffset == 0)
            throw new InvalidDataException("Missing 'data' chunk.");

        header.Codec = header.Format switch
        {
            0x0001 or 0xFFFE => WemCodec.Pcm,
            0xFFFF => WemCodec.Vorbis,
            0x3041 => WemCodec.Opus,
            _ => throw UnsupportedCodec(header.Format),
        };

        long dataEnd = header.DataOffset + header.DataSize;
        if (dataEnd > header.FileSize)
        {
            // Real prefetch copies are much smaller than the stream; a slightly short file is a bad rip.
            if (dataEnd - header.FileSize < 0x5000 && header.FileSize > 0x10000)
                throw new InvalidDataException("Data chunk is slightly truncated (bad extraction?).");
            header.Prefetch = true;
        }

        if (header.Format == 0xFFFE && header.Prefetch && Bytes.U32(data, header.DataOffset, bigEndian: true) == IdOggS)
            throw new NotSupportedException("WEM with Ogg Opus data in a PCM header is not supported.");

        return header;
    }

    /// <summary>
    /// vgmstream's estimate for VBR prefetch streams: the sample count scaled by the fraction of data present.
    /// </summary>
    public static int ScalePrefetchSamples(int numSamples, long availableBytes, long totalBytes)
    {
        double scaled = numSamples * (double)availableBytes / totalBytes;
        return double.IsFinite(scaled) && scaled > int.MinValue && scaled < int.MaxValue ? (int)scaled : int.MinValue;
    }

    private static Exception UnsupportedCodec(ushort format)
    {
        string? name = format switch
        {
            0x0002 or 0x0069 => "IMA ADPCM",
            0x0161 or 0x0162 => "XWMA",
            0x0165 or 0x0166 => "XMA2",
            0xAAC0 => "AAC",
            0xFFF0 => "DSP ADPCM",
            0xFFFB => "HEVAG",
            0xFFFC => "ATRAC9",
            0x3039 => "Opus (Switch)",
            0x3040 => "Ogg Opus",
            0x8311 => "PTADPCM",
            _ => null,
        };

        return name is null
            ? new InvalidDataException($"Unknown WEM codec 0x{format:X4}.")
            : new NotSupportedException($"WEM codec {name} (0x{format:X4}) is not supported.");
    }
}
