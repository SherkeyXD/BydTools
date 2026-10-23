using WemDecoder.Codecs.Vorbis;

namespace WemDecoder.Tests;

public class VorbisBitstreamTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryLibraryCodebookRebuilds(bool aoTuV)
    {
        var library = aoTuV ? CodebookLibrary.AoTuV603 : CodebookLibrary.Standard;
        Assert.Equal(598, library.Count);

        for (uint id = 0; id < library.Count; id++)
        {
            Assert.True(library.TryGet(id, out var codebook));
            var reader = new BitReader(codebook);
            Assert.True(WwiseVorbisSetup.TryRebuildCodebook(new BitWriter(), ref reader, codebook.Length), $"codebook {id}");
        }

        Assert.False(library.TryGet((uint)library.Count, out _));
    }

    [Fact]
    public void BitsRoundTrip()
    {
        var random = new Random(1234);
        var values = new List<(int Bits, uint Value)>();
        var writer = new BitWriter(4);
        for (int i = 0; i < 2000; i++)
        {
            int bits = random.Next(0, 33);
            uint value = (uint)random.NextInt64(0, 1L << 32);
            writer.Write(bits, value);
            values.Add((bits, bits == 32 ? value : value & ((1u << bits) - 1)));
        }

        var reader = new BitReader(writer.WrittenSpan);
        foreach (var (bits, value) in values)
            Assert.Equal(value, reader.Read(bits));
        Assert.Equal(writer.Position, reader.Position);
        Assert.Equal(0u, reader.Read(32));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public void WriteBytesMatchesBitwiseWrites(int offset)
    {
        byte[] bytes = [0x00, 0xFF, 0x5A, 0xA5, 0x01, 0x80];
        var bulk = new BitWriter(1);
        var bitwise = new BitWriter(1);
        bulk.Write(offset, 0x55);
        bitwise.Write(offset, 0x55);

        bulk.WriteBytes(bytes);
        foreach (byte b in bytes)
            bitwise.Write(8, b);

        Assert.Equal(bitwise.Position, bulk.Position);
        Assert.Equal(bitwise.ToArray(), bulk.ToArray());
    }

    [Theory]
    [InlineData(1u, 1u, 1u)]
    [InlineData(81u, 4u, 3u)]
    [InlineData(80u, 4u, 2u)]
    [InlineData(625u, 4u, 5u)]
    [InlineData(1u << 24, 1u, 1u << 24)]
    [InlineData(4096u, 2u, 64u)]
    [InlineData(4095u, 2u, 63u)]
    public void QuantvalsMatchTremor(uint entries, uint dimensions, uint expected) =>
        Assert.Equal(expected, WwiseVorbisSetup.BookMaptype1Quantvals(entries, dimensions));
}
