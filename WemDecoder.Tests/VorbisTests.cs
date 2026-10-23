using System.Buffers.Binary;
using WemDecoder.Codecs.Vorbis;
using WemDecoder.Tests.Support;

namespace WemDecoder.Tests;

public class VorbisTests
{
    /// <summary>Fixture, Wwise revision and endianness of every synthetic Vorbis WEM.</summary>
    public static TheoryData<string, string, bool> Layouts()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (string fixture in new[] { "mono.ogg", "stereo.ogg" })
        {
            foreach (var version in Enum.GetValues<WwiseVorbisVersion>())
                data.Add(fixture, version.ToString(), false);
        }

        foreach (string version in new[] { "V34", "V44", "V53", "V62" })
            data.Add("stereo.ogg", version, true);

        // libvorbis's 5.1 setup has no reduced Wwise form (an LFE floor without partitions,
        // codebooks outside Wwise's libraries), so only the layouts that keep setups intact.
        data.Add("surround.ogg", "V34", false);
        data.Add("surround.ogg", "V38", false);
        return data;
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void DecodesLikeTheOriginalStream(string fixture, string version, bool bigEndian)
    {
        byte[] ogg = TestAudio.Fixture(fixture);
        var stream = VorbisStream.FromOgg(ogg);
        byte[]? wem = SyntheticVorbisWem.TryBuild(stream, Enum.Parse<WwiseVorbisVersion>(version), bigEndian);
        Assert.NotNull(wem);

        using (var reader = new WemReader(wem))
        {
            Assert.Equal(WemCodec.Vorbis, reader.Codec);
            Assert.Equal(stream.Channels, reader.Channels);
            Assert.Equal(stream.SampleRate, reader.SampleRate);
            Assert.Equal(stream.SampleCount, reader.SampleCount);
        }

        Assert.Equal(TestAudio.DecodeOgg(ogg), TestAudio.DecodeAll(wem));
    }

    [Theory]
    [InlineData("mono.ogg", "V62")]
    [InlineData("stereo.ogg", "V62")]
    [InlineData("surround.ogg", "V34")]
    public void DecodesTheEncodedSignal(string fixture, string version)
    {
        var stream = VorbisStream.FromOgg(TestAudio.Fixture(fixture));
        byte[] wem = SyntheticVorbisWem.TryBuild(stream, Enum.Parse<WwiseVorbisVersion>(version))!;
        short[] decoded = TestAudio.DecodeAll(wem);

        int frames = (int)stream.SampleCount;
        float[] source = TestAudio.FixtureSignal(stream.Channels, frames, stream.SampleRate);
        // The encoder low-passes the 5.1 LFE channel (the last one), so it doesn't resemble the source.
        int fullBandChannels = stream.Channels == 6 ? 5 : stream.Channels;
        for (int c = 0; c < fullBandChannels; c++)
            Assert.True(TestAudio.SnrDb(decoded, source, stream.Channels, c, 0, frames) > 8, $"channel {c}");
    }

    [Theory]
    [InlineData("mono.ogg", "InlineCodebooks")]
    [InlineData("mono.ogg", "ExternalCodebooks")]
    [InlineData("mono.ogg", "AoTuV603Codebooks")]
    [InlineData("stereo.ogg", "InlineCodebooks")]
    [InlineData("stereo.ogg", "ExternalCodebooks")]
    [InlineData("stereo.ogg", "AoTuV603Codebooks")]
    public void RebuildsTheOriginalSetupExactly(string fixture, string setupKind)
    {
        var kind = Enum.Parse<WwiseSetupKind>(setupKind);
        var storage = kind switch
        {
            WwiseSetupKind.InlineCodebooks => CodebookStorage.Inline,
            WwiseSetupKind.ExternalCodebooks => CodebookStorage.Standard,
            _ => CodebookStorage.AoTuV603,
        };
        var stream = VorbisStream.FromOgg(TestAudio.Fixture(fixture));
        var reduced = WwiseVorbisReducer.TryReduceSetup(stream.Setup, stream.Channels, storage);
        Assert.NotNull(reduced);

        var file = new byte[2 + reduced.Packet.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(file, (ushort)reduced.Packet.Length);
        reduced.Packet.CopyTo(file, 2);
        var layout = new WwiseVorbisLayout(WwisePacketHeader.Type2, ModifiedPackets: true, kind);
        var headers = WwiseVorbisSetup.TryBuild(file, 0, layout, stream.Channels, stream.SampleRate,
            stream.ShortBlockExp, stream.LongBlockExp, bigEndian: false);

        Assert.NotNull(headers);
        Assert.Equal(stream.Setup, headers.Setup);
        Assert.Equal(reduced.ModeBits, headers.ModeBits);
        Assert.Equal(reduced.ModeBlockFlags, headers.ModeBlockFlags);
        Assert.True(WwiseVorbisSetup.IsValidIdentification(headers.Identification));
    }

    [Fact]
    public void PrefetchCopyDecodesTheAvailablePart()
    {
        byte[] ogg = TestAudio.Fixture("stereo.ogg");
        var stream = VorbisStream.FromOgg(ogg);
        byte[] full = SyntheticVorbisWem.TryBuild(stream, WwiseVorbisVersion.V62)!;
        byte[] prefetch = full[..(full.Length * 2 / 3)];

        long sampleCount;
        using (var reader = new WemReader(prefetch))
            sampleCount = reader.SampleCount;
        Assert.InRange(sampleCount, stream.SampleCount / 3, stream.SampleCount * 5 / 6);

        short[] expected = TestAudio.DecodeOgg(ogg);
        short[] actual = TestAudio.DecodeAll(prefetch);
        int compared = (int)(sampleCount / 2 * stream.Channels);
        Assert.Equal(expected.AsSpan(0, compared), actual.AsSpan(0, compared));
    }

    [Fact]
    public void RejectsAnUnreadableSetup()
    {
        var stream = VorbisStream.FromOgg(TestAudio.Fixture("stereo.ogg"));
        byte[] wem = SyntheticVorbisWem.TryBuild(stream, WwiseVorbisVersion.V62)!;
        int setup = IndexOf(wem, "data"u8) + 8 + 0x10 + 2;
        wem.AsSpan(setup, 32).Fill(0xFF);

        Assert.Throws<InvalidDataException>(() => new WemReader(wem));
    }

    [Fact]
    public void RejectsUnknownSetupLayouts()
    {
        var w = new WemBuilder();
        w.Chunk("fmt ", w.Fmt(0xFFFF, 2, 44100, 0, 0, new byte[0x20]));
        w.Chunk("data", new byte[0x100]);

        Assert.Throws<InvalidDataException>(() => new WemReader(w.Build()));
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> value) => data.AsSpan().IndexOf(value);
}
