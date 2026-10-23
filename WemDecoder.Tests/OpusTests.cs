using WemDecoder.Tests.Support;

namespace WemDecoder.Tests;

public class OpusTests
{
    private const int Frames = 24000;

    /// <summary>
    /// Wwise channel configs and the decoder layout vgmstream gives them. The mapping lists, per output
    /// channel, the coded channel it comes from: Wwise's channel matrix combined with FFmpeg's Vorbis-order
    /// reordering for mapping family 1 (worked out by hand from vgmstream's and FFmpeg's tables).
    /// </summary>
    internal static readonly Dictionary<string, OpusLayout> Layouts = new()
    {
        ["mono"] = new(1, 0x4, 1, 0, 1, 0, [0]),
        ["stereo"] = new(2, 0x3, 1, 0, 1, 1, [0, 1]),
        ["3.0"] = new(3, 0x7, 1, 1, 2, 1, [0, 1, 2]),
        ["quad"] = new(4, 0x603, 1, 1, 2, 2, [0, 1, 2, 3]),
        ["5.0"] = new(5, 0x607, 1, 1, 3, 2, [0, 1, 4, 2, 3]),
        ["5.1"] = new(6, 0x60F, 1, 1, 4, 2, [0, 1, 4, 5, 2, 3]),
        ["5.1 back"] = new(6, 0x3F, 1, 1, 6, 0, [0, 1, 4, 5, 2, 3]),
        ["7.1"] = new(8, 0x63F, 1, 1, 5, 3, [0, 1, 6, 7, 4, 5, 2, 3]),
        // Mapping 255 keeps the coded order, which FFmpeg then shuffles as if it were Vorbis order.
        ["ambisonic 4ch"] = new(4, 0, 2, 255, 4, 0, [0, 1, 2, 3]),
        ["anonymous 6ch"] = new(6, 0, 2, 255, 6, 0, [0, 2, 1, 5, 3, 4]),
    };

    public static TheoryData<string> LayoutNames() => [.. Layouts.Keys];

    [Theory]
    [MemberData(nameof(LayoutNames))]
    public void DecodesEachChannelInPlace(string name)
    {
        var layout = Layouts[name];
        float[] input = TestAudio.Tones(layout.Channels, Frames, SyntheticOpusWem.SampleRate);
        byte[] wem = SyntheticOpusWem.Build(layout, input);

        using (var reader = new WemReader(wem))
        {
            Assert.Equal(WemCodec.Opus, reader.Codec);
            Assert.Equal(layout.Channels, reader.Channels);
            Assert.Equal(48000, reader.SampleRate);
            Assert.Equal(Frames, reader.SampleCount);
        }

        short[] output = TestAudio.DecodeAll(wem);
        Assert.Equal(Enumerable.Range(0, layout.Channels), TestAudio.DominantTones(output, layout.Channels, 48000));
        // Waveforms line up only if the pre-skip was applied.
        for (int c = 0; c < layout.Channels; c++)
            Assert.True(TestAudio.SnrDb(output, input, layout.Channels, c, Frames / 4, Frames / 2) > 15, $"channel {c}");
    }

    [Fact]
    public void PadsWithSilenceUpToTheDeclaredLength()
    {
        var layout = Layouts["stereo"];
        float[] input = TestAudio.Tones(2, Frames, 48000);
        short[] output = TestAudio.DecodeAll(SyntheticOpusWem.Build(layout, input, declaredSamples: Frames + 6000));

        Assert.Equal((Frames + 6000) * 2, output.Length);
        Assert.All(output[^(5000 * 2)..], sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void StopsAtTheDeclaredLength()
    {
        var layout = Layouts["stereo"];
        float[] input = TestAudio.Tones(2, Frames, 48000);
        short[] full = TestAudio.DecodeAll(SyntheticOpusWem.Build(layout, input));
        short[] cut = TestAudio.DecodeAll(SyntheticOpusWem.Build(layout, input, declaredSamples: 10000));

        Assert.Equal(full[..(10000 * 2)], cut);
    }

    [Fact]
    public void PrefetchCopyDecodesTheAvailablePart()
    {
        var layout = Layouts["stereo"];
        float[] input = TestAudio.Tones(2, Frames, 48000);
        byte[] wem = SyntheticOpusWem.Build(layout, input);
        short[] full = TestAudio.DecodeAll(wem);
        byte[] prefetch = wem[..(wem.Length / 2)];

        short[] partial = TestAudio.DecodeAll(prefetch);
        Assert.InRange(partial.Length / 2, Frames / 4, Frames * 3 / 4);
        Assert.Equal(full.AsSpan(0, partial.Length / 4), partial.AsSpan(0, partial.Length / 4));
    }

    [Fact]
    public void RejectsMissingSeekTable()
    {
        byte[] wem = SyntheticOpusWem.Build(Layouts["stereo"], new float[960 * 2], withSeekTable: false);
        Assert.Throws<InvalidDataException>(() => new WemReader(wem));
    }

    [Fact]
    public void RejectsUnknownCodecVersions()
    {
        byte[] wem = SyntheticOpusWem.Build(Layouts["stereo"], new float[960 * 2], version: 2);
        Assert.Throws<NotSupportedException>(() => new WemReader(wem));
    }

    [Fact]
    public void RejectsMultichannelWithoutChannelMapping()
    {
        var layout = new OpusLayout(3, 0x7, 0, 0, 3, 0, [0, 1, 2]);
        byte[] wem = SyntheticOpusWem.Build(layout, new float[960 * 3]);
        Assert.Throws<NotSupportedException>(() => new WemReader(wem));
    }

    [Fact]
    public void RejectsMoreThanEightChannelsWithMappingFamilyOne()
    {
        var layout = new OpusLayout(9, 0x1FF, 1, 1, 9, 0, [0, 1, 2, 3, 4, 5, 6, 7, 8]);
        byte[] wem = SyntheticOpusWem.Build(layout, new float[960 * 9]);
        Assert.Throws<NotSupportedException>(() => new WemReader(wem));
    }
}
