using System.Buffers.Binary;
using BydTools.Formats.Manifest;
using BydTools.Tests.TestHelpers;
using Xunit;

namespace BydTools.Tests;

public sealed class HgmmapManifestTests
{
    [Fact]
    public void DecompressBrotli_RoundTripsMultiChunkValidManifest()
    {
        byte[] raw = EndfieldTestBuilder.BuildValidHgmmapBinary(extraBytes: 150_000, versionText: "1.0.14.99");
        byte[] compressed = EndfieldTestBuilder.CompressBrotli(raw);

        using var output = new MemoryStream();
        HgmmapManifest.DecompressBrotli(compressed, output);

        Assert.Equal(raw, output.ToArray());
    }

    [Fact]
    public void DecompressBrotli_RejectsTruncatedBrotliStream()
    {
        // Reproduces the failure mode of a truncated prefix such as manifest.hgmmap.first1MiB.bin
        byte[] raw = EndfieldTestBuilder.BuildValidHgmmapBinary(extraBytes: 200_000);
        byte[] compressed = EndfieldTestBuilder.CompressBrotli(raw);
        byte[] truncated = compressed[..(int)(compressed.Length * 0.7)];

        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => HgmmapManifest.DecompressBrotli(truncated, output));
    }

    [Fact]
    public void DecompressBrotli_RejectsEmptyAndCorruptedInputs()
    {
        Assert.Throws<InvalidDataException>(() => HgmmapManifest.DecompressBrotli([], Stream.Null));

        byte[] garbage = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04];
        Assert.Throws<InvalidDataException>(() => HgmmapManifest.DecompressBrotli(garbage, Stream.Null));
    }

    [Fact]
    public void DecompressBrotli_RejectsInvalidManifestSignatures()
    {
        byte[] badHead1 = EndfieldTestBuilder.BuildValidHgmmapBinary();
        BinaryPrimitives.WriteUInt32LittleEndian(badHead1.AsSpan(0, 4), 0x12345678u);
        byte[] compressedBadHead1 = EndfieldTestBuilder.CompressBrotli(badHead1);
        Assert.Throws<InvalidDataException>(() =>
            HgmmapManifest.DecompressBrotli(compressedBadHead1, Stream.Null));

        byte[] badHead2 = EndfieldTestBuilder.BuildValidHgmmapBinary(versionText: "1.0.0.0");
        int head2Off = 8 + "1.0.0.0".Length * 2;
        BinaryPrimitives.WriteUInt32LittleEndian(badHead2.AsSpan(head2Off, 4), 0x87654321u);
        byte[] compressedBadHead2 = EndfieldTestBuilder.CompressBrotli(badHead2);
        Assert.Throws<InvalidDataException>(() =>
            HgmmapManifest.DecompressBrotli(compressedBadHead2, Stream.Null));
    }

    [Fact]
    public void DecompressBrotli_EnforcesMaxDecompressedBytesLimit()
    {
        byte[] raw = EndfieldTestBuilder.BuildValidHgmmapBinary(extraBytes: 4096);
        byte[] compressed = EndfieldTestBuilder.CompressBrotli(raw);

        Assert.Throws<InvalidDataException>(() =>
            HgmmapManifest.DecompressBrotli(compressed, Stream.Null, maxDecompressedBytes: 1024));
    }
}
