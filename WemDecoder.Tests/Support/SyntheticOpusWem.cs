using Concentus.Enums;
using Concentus.Structs;

namespace WemDecoder.Tests.Support;

/// <summary>
/// A Wwise Opus channel setup plus the stream layout vgmstream derives from it.
/// <see cref="Mapping"/> gives, per output channel (WAVE order), the coded channel decoders should pick.
/// </summary>
internal sealed record OpusLayout(int Channels, uint SpeakerMask, int ChannelType, int MappingFamily, int Streams, int Coupled, byte[] Mapping)
{
    public override string ToString() => $"{Channels}ch mask 0x{SpeakerMask:X} type {ChannelType} family {MappingFamily}";
}

/// <summary>Encodes test signals with Concentus and wraps them the way Wwise stores Opus (format 0x3041).</summary>
internal static class SyntheticOpusWem
{
    public const int SampleRate = 48000;
    private const int FrameSize = 960;

    /// <param name="layout">Channel setup.</param>
    /// <param name="input">Interleaved samples to encode.</param>
    /// <param name="declaredSamples">Sample count written to the header; defaults to the input length.</param>
    /// <param name="version">Codec version byte; Wwise only writes 1.</param>
    /// <param name="withSeekTable">Whether to include the packet size table.</param>
    public static byte[] Build(OpusLayout layout, float[] input, int? declaredSamples = null, byte version = 1, bool withSeekTable = true)
    {
        int channels = layout.Channels;
        int frames = input.Length / channels;

#pragma warning disable CS0618
        var encoder = OpusMSEncoder.Create(SampleRate, channels, layout.Streams, layout.Coupled, layout.Mapping, OpusApplication.OPUS_APPLICATION_AUDIO);
#pragma warning restore CS0618
        encoder.Bitrate = 64000 * (layout.Streams + layout.Coupled);
        int preSkip = encoder.Lookahead;

        int packetCount = (frames + preSkip + FrameSize - 1) / FrameSize;
        var padded = new float[packetCount * FrameSize * channels];
        input.CopyTo(padded, 0);

        var w = new WemBuilder();
        var seek = new MemoryStream();
        var data = new MemoryStream();
        var packet = new byte[1275 * 2 * layout.Streams + 8];
        for (int i = 0; i < packetCount; i++)
        {
            var pcm = padded.AsSpan(i * FrameSize * channels, FrameSize * channels);
            int size = encoder.EncodeMultistream(pcm, FrameSize, packet, packet.Length);
            seek.Write(w.U16((ushort)size));
            data.Write(packet, 0, size);
        }

        var extra = new byte[0x12];
        w.Put16(extra, FrameSize);
        w.Put32(extra.AsSpan(0x02), (uint)channels | ((uint)layout.ChannelType << 8) | (layout.SpeakerMask << 12));
        w.Put32(extra.AsSpan(0x06), (uint)(declaredSamples ?? frames));
        w.Put32(extra.AsSpan(0x0A), (uint)packetCount);
        w.Put16(extra.AsSpan(0x0E), (ushort)preSkip);
        extra[0x10] = version;
        extra[0x11] = (byte)layout.MappingFamily;

        w.Chunk("fmt ", w.Fmt(0x3041, channels, SampleRate, 0, 0, extra));
        if (withSeekTable)
            w.Chunk("seek", seek.ToArray());
        w.Chunk("data", data.ToArray());
        return w.Build();
    }
}
