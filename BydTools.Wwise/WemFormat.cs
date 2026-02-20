using System.Buffers.Binary;

namespace BydTools.Wwise;

/// <summary>
/// Wwise codec identifiers found in the WEM RIFF fmt chunk.
/// Values from Audiokinetic Wwise SDK, cross-referenced with vgmstream.
/// </summary>
public enum WemCodec : ushort
{
    Unknown = 0,
    Pcm = 0x0001,
    Adpcm = 0x0002,
    PcmFloat = 0x0003,
    AdpcmOld = 0x0069,
    Wma2 = 0x0161,
    WmaPro = 0x0162,
    Xma2Chunk = 0x0165,
    Xma2Fmt = 0x0166,
    Aac = 0xAAC0,
    OpusNx = 0x3039,
    Opus = 0x3040,
    OpusWw = 0x3041,
    Dsp = 0xFFF0,
    Hevag = 0xFFFB,
    Atrac9 = 0xFFFC,
    PcmAuthoring = 0xFFFE,
    Vorbis = 0xFFFF,
    Ptadpcm = 0x8311,
}

/// <summary>
/// Parsed metadata from a WEM file's RIFF header.
/// </summary>
public record WemInfo(
    WemCodec Codec,
    int Channels,
    int SampleRate,
    int AvgBytesPerSec,
    int BlockAlign,
    int BitsPerSample
)
{
    public bool IsOpus => Codec is WemCodec.Opus or WemCodec.OpusNx or WemCodec.OpusWw;
    public bool IsVorbis => Codec == WemCodec.Vorbis;
    public bool IsPcm => Codec is WemCodec.Pcm or WemCodec.PcmAuthoring or WemCodec.PcmFloat;
    public bool IsAdpcm => Codec is WemCodec.Adpcm or WemCodec.AdpcmOld or WemCodec.Ptadpcm;
}

/// <summary>
/// Reads minimal metadata from WEM file data without full parsing.
/// </summary>
public static class WemFormatReader
{
    /// <summary>
    /// Detects the codec from raw WEM file bytes.
    /// Returns <see cref="WemCodec.Unknown"/> if the format cannot be determined.
    /// </summary>
    public static WemCodec DetectCodec(ReadOnlySpan<byte> data)
    {
        if (data.Length < 22)
            return WemCodec.Unknown;

        if (!data[..4].SequenceEqual("RIFF"u8) && !data[..4].SequenceEqual("RIFX"u8))
            return WemCodec.Unknown;

        // Walk RIFF chunks to find "fmt "
        int pos = 12; // skip RIFF header (4 magic + 4 size + 4 "WAVE"/"XWMA")
        while (pos + 8 <= data.Length)
        {
            var chunkId = data.Slice(pos, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4, 4));

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (pos + 8 + 2 > data.Length)
                    return WemCodec.Unknown;
                ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 8, 2));
                return Enum.IsDefined(typeof(WemCodec), formatTag)
                    ? (WemCodec)formatTag
                    : WemCodec.Unknown;
            }

            pos += 8 + (int)chunkSize;
            if (pos % 2 != 0) pos++; // RIFF chunks are word-aligned
        }

        return WemCodec.Unknown;
    }

    /// <summary>
    /// Parses full WEM metadata from file bytes.
    /// Returns <c>null</c> if the header cannot be parsed.
    /// </summary>
    public static WemInfo? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28)
            return null;

        if (!data[..4].SequenceEqual("RIFF"u8) && !data[..4].SequenceEqual("RIFX"u8))
            return null;

        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            var chunkId = data.Slice(pos, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4, 4));

            if (chunkId.SequenceEqual("fmt "u8))
            {
                var fmt = data.Slice(pos + 8, (int)Math.Min(chunkSize, (uint)(data.Length - pos - 8)));
                if (fmt.Length < 16)
                    return null;

                ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[..2]);
                int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..4]);
                int sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..8]);
                int avgBytesPerSec = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[8..12]);
                int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..14]);
                int bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..16]);

                var codec = Enum.IsDefined(typeof(WemCodec), formatTag)
                    ? (WemCodec)formatTag
                    : WemCodec.Unknown;

                return new WemInfo(codec, channels, sampleRate, avgBytesPerSec, blockAlign, bitsPerSample);
            }

            pos += 8 + (int)chunkSize;
            if (pos % 2 != 0) pos++;
        }

        return null;
    }

    /// <summary>
    /// Returns a human-readable description for a codec.
    /// </summary>
    public static string GetCodecName(WemCodec codec) => codec switch
    {
        WemCodec.Pcm => "PCM",
        WemCodec.PcmAuthoring => "PCM (Authoring)",
        WemCodec.PcmFloat => "PCM Float",
        WemCodec.Adpcm => "IMA ADPCM",
        WemCodec.AdpcmOld => "IMA ADPCM (old)",
        WemCodec.Ptadpcm => "PTADPCM",
        WemCodec.Vorbis => "Wwise Vorbis",
        WemCodec.Opus => "Opus",
        WemCodec.OpusNx => "Opus NX",
        WemCodec.OpusWw => "Wwise Opus",
        WemCodec.Aac => "AAC",
        WemCodec.Wma2 => "WMAv2",
        WemCodec.WmaPro => "WMA Pro",
        WemCodec.Xma2Chunk => "XMA2",
        WemCodec.Xma2Fmt => "XMA2",
        WemCodec.Dsp => "DSP",
        WemCodec.Hevag => "HEVAG",
        WemCodec.Atrac9 => "ATRAC9",
        _ => $"Unknown (0x{(ushort)codec:X4})",
    };
}
