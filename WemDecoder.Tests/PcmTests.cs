using System.Buffers.Binary;
using WemDecoder.Tests.Support;

namespace WemDecoder.Tests;

public class PcmTests
{
    [Theory]
    [InlineData(1, false, 0x10)]
    [InlineData(2, false, 0x12)]
    [InlineData(2, false, 0x18)]
    [InlineData(2, true, 0x18)]
    [InlineData(6, false, 0x18)]
    [InlineData(2, false, 0x28)]
    public void DecodesSamplesExactly(int channels, bool bigEndian, int fmtSize)
    {
        short[] samples = RandomSamples(channels * 5000, seed: channels + fmtSize);
        byte[] wem = Build(channels, 44100, samples, bigEndian, fmtSize);

        using (var reader = new WemReader(wem))
        {
            Assert.Equal(WemCodec.Pcm, reader.Codec);
            Assert.Equal(channels, reader.Channels);
            Assert.Equal(44100, reader.SampleRate);
            Assert.Equal(5000, reader.SampleCount);
        }

        Assert.Equal(samples, TestAudio.DecodeAll(wem));
    }

    [Fact]
    public void IgnoresAPartialLastFrame()
    {
        short[] samples = RandomSamples(2 * 100, seed: 1);
        byte[] data = [.. Bytes(samples, bigEndian: false), 0x12, 0x34];
        byte[] wem = Build(2, 48000, data, bigEndian: false, fmtSize: 0x18);

        Assert.Equal(samples, TestAudio.DecodeAll(wem));
    }

    [Fact]
    public void PrefetchCopyDecodesTheAvailableSamples()
    {
        short[] samples = RandomSamples(2 * 3000, seed: 2);
        byte[] data = Bytes(samples, bigEndian: false);
        byte[] wem = Build(2, 48000, data, bigEndian: false, fmtSize: 0x18, declaredDataSize: (uint)data.Length * 4);

        Assert.Equal(samples, TestAudio.DecodeAll(wem));
    }

    [Fact]
    public void RejectsSlightlyTruncatedLargeFiles()
    {
        short[] samples = RandomSamples(2 * 20000, seed: 3);
        byte[] data = Bytes(samples, bigEndian: false);
        byte[] wem = Build(2, 48000, data, bigEndian: false, fmtSize: 0x18, declaredDataSize: (uint)data.Length + 0x100);

        Assert.Throws<InvalidDataException>(() => new WemReader(wem));
    }

    [Fact]
    public void RejectsOtherSampleSizes()
    {
        var w = new WemBuilder();
        w.Chunk("fmt ", w.Fmt(0x0001, 1, 44100, 1, 8, []));
        w.Chunk("data", new byte[100]);

        Assert.Throws<NotSupportedException>(() => new WemReader(w.Build()));
    }

    [Fact]
    public void RejectsUnexpectedFmtSizes()
    {
        var w = new WemBuilder();
        w.Chunk("fmt ", w.Fmt(0x0001, 1, 44100, 2, 16, [0, 0]));
        w.Chunk("data", new byte[100]);

        Assert.Throws<InvalidDataException>(() => new WemReader(w.Build()));
    }

    [Fact]
    public void RejectsEmptyData()
    {
        Assert.Throws<InvalidDataException>(() => new WemReader(Build(2, 48000, Array.Empty<short>(), false, 0x18)));
    }

    internal static byte[] Build(int channels, int sampleRate, short[] samples, bool bigEndian, int fmtSize) =>
        Build(channels, sampleRate, Bytes(samples, bigEndian), bigEndian, fmtSize);

    internal static byte[] Build(int channels, int sampleRate, byte[] data, bool bigEndian, int fmtSize, uint? declaredDataSize = null)
    {
        var w = new WemBuilder(bigEndian);
        ushort blockAlign = (ushort)(channels * 2);
        byte[] fmt = fmtSize switch
        {
            0x10 => w.Fmt(0x0001, channels, sampleRate, blockAlign, 16, [])[..0x10],
            0x12 => w.Fmt(0x0001, channels, sampleRate, blockAlign, 16, []),
            _ => w.Fmt(0xFFFE, channels, sampleRate, blockAlign, 16, ExtensibleExtra(w, channels, fmtSize - 0x12)),
        };

        w.Chunk("fmt ", fmt);
        w.Chunk("data", data, declaredDataSize);
        return w.Build();
    }

    internal static short[] RandomSamples(int count, int seed)
    {
        var random = new Random(seed);
        var samples = new short[count];
        for (int i = 0; i < count; i++)
            samples[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        return samples;
    }

    private static byte[] ExtensibleExtra(WemBuilder w, int channels, int size)
    {
        var extra = new byte[size];
        w.Put16(extra, 16);
        w.Put32(extra.AsSpan(2), WemBuilder.ChannelConfig(channels, (1u << channels) - 1));
        return extra;
    }

    private static byte[] Bytes(short[] samples, bool bigEndian)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            if (bigEndian)
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2), samples[i]);
            else
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        }

        return bytes;
    }
}
