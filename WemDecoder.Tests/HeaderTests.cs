using WemDecoder.Tests.Support;

namespace WemDecoder.Tests;

public class HeaderTests
{
    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46 })]
    [InlineData(new byte[] { 0x4F, 0x67, 0x67, 0x53, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void RejectsNonRiffData(byte[] data)
    {
        Assert.Throws<InvalidDataException>(() => new WemReader(data));
    }

    [Theory]
    [InlineData(0x0002, "IMA ADPCM")]
    [InlineData(0x0161, "XWMA")]
    [InlineData(0xAAC0, "AAC")]
    [InlineData(0xFFF0, "DSP ADPCM")]
    [InlineData(0xFFFC, "ATRAC9")]
    [InlineData(0x3040, "Ogg Opus")]
    [InlineData(0x8311, "PTADPCM")]
    public void NamesUnsupportedCodecs(ushort format, string name)
    {
        var w = new WemBuilder();
        w.Chunk("fmt ", w.Fmt(format, 2, 48000, 0x24, 4, []));
        w.Chunk("data", new byte[0x100]);

        var exception = Assert.Throws<NotSupportedException>(() => new WemReader(w.Build()));
        Assert.Contains(name, exception.Message);
    }

    [Fact]
    public void RejectsXma2()
    {
        var w = new WemBuilder();
        w.Chunk("fmt ", w.Fmt(0x0165, 2, 48000, 0, 16, []));
        w.Chunk("XMA2", new byte[0x34]);
        w.Chunk("data", new byte[0x100]);

        Assert.Throws<NotSupportedException>(() => new WemReader(w.Build()));
    }

    [Fact]
    public void RejectsUnknownCodecs()
    {
        var w = new WemBuilder();
        w.Chunk("fmt ", w.Fmt(0x1234, 2, 48000, 4, 16, []));
        w.Chunk("data", new byte[0x100]);

        Assert.Throws<InvalidDataException>(() => new WemReader(w.Build()));
    }

    [Fact]
    public void RejectsFactChunks()
    {
        var w = new WemBuilder();
        w.Chunk("fmt ", w.Fmt(0x0001, 2, 48000, 4, 16, []));
        w.Chunk("fact", new byte[4]);
        w.Chunk("data", new byte[0x100]);

        Assert.Throws<InvalidDataException>(() => new WemReader(w.Build()));
    }

    [Fact]
    public void RejectsMissingChunks()
    {
        var noData = new WemBuilder();
        noData.Chunk("fmt ", noData.Fmt(0x0001, 2, 48000, 4, 16, []));
        Assert.Throws<InvalidDataException>(() => new WemReader(noData.Build()));

        var noFmt = new WemBuilder();
        noFmt.Chunk("data", new byte[0x100]);
        Assert.Throws<InvalidDataException>(() => new WemReader(noFmt.Build()));
    }

    [Fact]
    public void RejectsUnknownChunksPastTheEnd()
    {
        var w = new WemBuilder();
        w.Chunk("fmt ", w.Fmt(0x0001, 2, 48000, 4, 16, []));
        w.Chunk("data", new byte[0x100]);
        w.Chunk("junk", new byte[4], declaredSize: 0x1000);

        Assert.Throws<InvalidDataException>(() => new WemReader(w.Build()));
    }

    [Theory]
    [InlineData(0, 48000)]
    [InlineData(65, 48000)]
    [InlineData(2, 299)]
    [InlineData(2, 192001)]
    public void RejectsOutOfRangeFormats(int channels, int sampleRate)
    {
        var w = new WemBuilder();
        w.Chunk("fmt ", w.Fmt(0x0001, channels, sampleRate, (ushort)(channels * 2), 16, []));
        w.Chunk("data", new byte[130 * 4]);

        Assert.Throws<InvalidDataException>(() => new WemReader(w.Build()));
    }

    [Fact]
    public void ReaderReportsPositionAndRejectsUseAfterDispose()
    {
        byte[] wem = PcmTests.Build(2, 48000, PcmTests.RandomSamples(2 * 100, seed: 4), bigEndian: false, fmtSize: 0x18);
        var reader = new WemReader(wem);
        Assert.Equal(0, reader.Read(new short[1]));
        Assert.Equal(2 * 30, reader.Read(new short[61]));
        Assert.Equal(30, reader.Position);
        Assert.Equal(2 * 70, reader.Read(new short[1000]));
        Assert.Equal(100, reader.Position);

        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.Read(new short[2]));
    }
}
