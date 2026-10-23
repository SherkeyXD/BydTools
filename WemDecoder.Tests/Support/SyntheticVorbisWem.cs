using WemDecoder.Codecs.Vorbis;

namespace WemDecoder.Tests.Support;

/// <summary>A standard Vorbis stream split into its parts.</summary>
internal sealed record VorbisStream(
    byte[] Identification, byte[] Comment, byte[] Setup, IReadOnlyList<byte[]> AudioPackets,
    int Channels, int SampleRate, int ShortBlockExp, int LongBlockExp, long SampleCount)
{
    public static VorbisStream FromOgg(byte[] ogg)
    {
        var packets = OggReader.ReadPackets(ogg, out long granule);
        byte[] id = packets[0];
        return new VorbisStream(
            id, packets[1], packets[2], packets[3..],
            Channels: id[0x0B],
            SampleRate: BitConverter.ToInt32(id, 0x0C),
            ShortBlockExp: id[0x1C] & 0x0F,
            LongBlockExp: id[0x1C] >> 4,
            SampleCount: granule);
    }
}

/// <summary>
/// Repackages a standard Vorbis stream the way each Wwise revision stores it
/// (layouts as described by vgmstream's wwise.c).
/// </summary>
internal static class SyntheticVorbisWem
{
    private const int SeekTableSize = 0x10;

    /// <returns>The WEM, or <see langword="null"/> when the stream's codebooks aren't in the revision's library.</returns>
    public static byte[]? TryBuild(VorbisStream stream, WwiseVorbisVersion version, bool bigEndian = false)
    {
        var (headerSize, storage, modified, vorbSize) = version switch
        {
            WwiseVorbisVersion.V34 => (8, (CodebookStorage?)null, false, 0x28),
            WwiseVorbisVersion.V38 => (6, CodebookStorage.Full, false, 0x34),
            WwiseVorbisVersion.V44 => (6, CodebookStorage.Inline, false, 0x34),
            WwiseVorbisVersion.V48 => (6, CodebookStorage.Standard, false, 0x34),
            WwiseVorbisVersion.V52 => (6, CodebookStorage.Standard, false, 0x32),
            WwiseVorbisVersion.V53 => (2, CodebookStorage.Standard, true, 0x2A),
            WwiseVorbisVersion.V56 => (2, CodebookStorage.Standard, true, 0),
            _ => (2, CodebookStorage.AoTuV603, true, 0),
        };
        modified &= stream.ShortBlockExp != stream.LongBlockExp;

        var w = new WemBuilder(bigEndian);
        var data = new MemoryStream();
        data.Write(new byte[SeekTableSize]);

        ReducedSetup? setup = null;
        if (storage is null)
        {
            WritePacket(data, w, headerSize, stream.Identification);
            WritePacket(data, w, headerSize, stream.Comment);
            WritePacket(data, w, headerSize, stream.Setup);
        }
        else
        {
            setup = WwiseVorbisReducer.TryReduceSetup(stream.Setup, stream.Channels, storage.Value);
            if (setup is null)
                return null;
            WritePacket(data, w, headerSize, setup.Packet);
        }

        uint audioOffset = (uint)data.Length;
        foreach (byte[] packet in stream.AudioPackets)
            WritePacket(data, w, headerSize, modified ? WwiseVorbisReducer.ReduceAudioPacket(packet, setup!) : packet);

        // Fields shared by all layouts; offsets vary by revision.
        int dataOffsets = vorbSize is 0x28 or 0x34 or 0x32 ? 0x18 : 0x10;
        int blockOffsets = vorbSize switch { 0x28 => 0, 0x34 or 0x32 => 0x30, _ => 0x28 };
        var fields = new byte[vorbSize == 0 ? 0x2A : vorbSize];
        w.Put32(fields, (uint)stream.SampleCount);
        w.Put32(fields.AsSpan(dataOffsets), SeekTableSize);
        w.Put32(fields.AsSpan(dataOffsets + 4), audioOffset);
        if (blockOffsets != 0)
        {
            fields[blockOffsets] = (byte)stream.ShortBlockExp;
            fields[blockOffsets + 1] = (byte)stream.LongBlockExp;
        }

        if (vorbSize != 0)
        {
            w.Chunk("fmt ", w.Fmt(0xFFFF, stream.Channels, stream.SampleRate, 0, 0, []));
            w.Chunk("vorb", fields);
        }
        else
        {
            var extra = new byte[0x30];
            w.Put32(extra.AsSpan(2), WemBuilder.ChannelConfig(stream.Channels, (1u << stream.Channels) - 1));
            fields.CopyTo(extra.AsSpan(6));
            w.Chunk("fmt ", w.Fmt(0xFFFF, stream.Channels, stream.SampleRate, 0, 0, extra));
        }

        w.Chunk("data", data.ToArray());
        return w.Build();
    }

    private static void WritePacket(MemoryStream data, WemBuilder w, int headerSize, byte[] packet)
    {
        data.Write(headerSize == 8 ? w.U32((uint)packet.Length) : w.U16((ushort)packet.Length));
        if (headerSize > 2)
            data.Write(w.U32(0)); // granule, unused by decoders
        data.Write(packet);
    }
}
