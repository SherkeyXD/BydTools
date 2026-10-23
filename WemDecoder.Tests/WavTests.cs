using System.Buffers.Binary;

namespace WemDecoder.Tests;

public class WavTests
{
    [Fact]
    public void WritesA16BitPcmWav()
    {
        short[] samples = PcmTests.RandomSamples(2 * 1234, seed: 5);
        byte[] wem = PcmTests.Build(2, 32000, samples, bigEndian: true, fmtSize: 0x18);

        byte[] wav = WemConverter.ConvertToWav(wem);

        Assert.Equal(44 + samples.Length * 2, wav.Length);
        Assert.Equal("RIFF"u8.ToArray(), wav[0..4]);
        Assert.Equal((uint)(wav.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal("WAVEfmt "u8.ToArray(), wav[8..16]);
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(32000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(32000u * 4, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(32)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal("data"u8.ToArray(), wav[36..40]);
        Assert.Equal((uint)samples.Length * 2, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)));
        for (int i = 0; i < samples.Length; i++)
            Assert.Equal(samples[i], BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2)));
    }

    [Fact]
    public void StreamAndArrayOutputsMatch()
    {
        byte[] wem = PcmTests.Build(1, 22050, PcmTests.RandomSamples(9000, seed: 6), bigEndian: false, fmtSize: 0x12);
        using var output = new MemoryStream();

        WemConverter.ConvertToWav(wem, output);

        Assert.Equal(WemConverter.ConvertToWav(wem), output.ToArray());
    }

    [Fact]
    public void LeavesTheStreamUntouchedOnInvalidInput()
    {
        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => WemConverter.ConvertToWav(new byte[16], output));
        Assert.Equal(0, output.Length);
    }
}
