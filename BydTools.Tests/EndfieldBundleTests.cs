using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BydTools.Formats.Bundle;
using BydTools.Tests.TestHelpers;
using Xunit;

namespace BydTools.Tests;

public sealed class EndfieldBundleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(512)]
    [InlineData(4096)]
    [InlineData(8192)]
    public void VfsCipher_RoundTripsAllSizes(int length)
    {
        byte[] original = new byte[length];
        for (int i = 0; i < length; i++)
            original[i] = (byte)((i * 37 + 11) & 0xFF);

        byte[] buffer = original.ToArray();
        EndfieldVfsCipher.TransformBlock(buffer);
        if (length > 0)
            Assert.NotEqual(original, buffer);

        EndfieldVfsCipher.TransformBlock(buffer);
        Assert.Equal(original, buffer);
    }

    [Fact]
    public void VfsCipher_SparseSamplingLeavesTrailingBytesInLargeBlocksUntouched()
    {
        byte[] buffer = new byte[4096];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (byte)(i & 0xFF);
        byte[] original = buffer.ToArray();

        EndfieldVfsCipher.TransformBlock(buffer);

        // When length >= 4096, step == 1 so only byte 0 of each 16-byte block is XORed.
        for (int blk = 0; blk < 256; blk++)
        {
            for (int lane = 1; lane < 16; lane++)
                Assert.Equal(original[blk * 16 + lane], buffer[blk * 16 + lane]);
        }
    }

    [Fact]
    public void Lz4Inv_DecompressesLiteralsAndBackreferenceMatch()
    {
        byte[] prefix = "ABCD"u8.ToArray();
        byte[] encoded = EndfieldTestBuilder.EncodeLz4InvLiteralsAndSingleMatch(prefix, matchOffset: 4, matchLength: 24);
        byte[] decoded = Lz4InvBlock.Decompress(encoded, 28);

        byte[] expected = Enumerable.Repeat(prefix, 7).SelectMany(static b => b).ToArray();
        Assert.Equal(expected, decoded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Lz4Inv_RejectsInvalidSizesOrOffsets(int badOffset)
    {
        byte[] bad = [0x01, (byte)'A', (byte)(badOffset >> 8), (byte)badOffset];
        Assert.Throws<InvalidDataException>(() => Lz4InvBlock.Decompress(bad, 10));
    }

    [Fact]
    public void Lz4Inv_RejectsTruncatedOffset()
    {
        byte[] truncated = [0x01, (byte)'A', 0x00];
        Assert.Throws<InvalidDataException>(() => Lz4InvBlock.Decompress(truncated, 5));
    }

    [Fact]
    public void ParseHeader_UsesRotateLeft14For64BitSizeRegression()
    {
        // Specifically exercises high/low 64-bit bits where RotateRight(..., 18) corrupts
        // the decoded size (e.g. 0xe370...) while RotateLeft(..., 14) recovers exact length.
        byte[] cab = EndfieldTestBuilder.BuildSyntheticSerializedFileHeader("2021.3.34f5", 160);
        byte[] bundle = EndfieldTestBuilder.BuildEndfieldBundle(
            [new EndfieldTestBuilder.TestBlockSpec(cab, Flags: 5)],
            [new EndfieldTestBuilder.TestNodeSpec("CAB-test", 0, cab.Length)],
            flags: 0x00000243u,
            encFlags: 7u);

        EndfieldBundleHeader hdr = EndfieldBundle.ParseHeader(bundle);
        Assert.Equal(bundle.Length, hdr.Size);
        Assert.Equal(48, hdr.HeaderLength);

        // Verify the old AnimeStudio RotateRight(..., 18) would have failed on the same raw bytes.
        uint size2 = BinaryPrimitives.ReadUInt32BigEndian(bundle.AsSpan(18, 4));
        uint size1 = BinaryPrimitives.ReadUInt32BigEndian(bundle.AsSpan(34, 4));
        ulong combined = ((ulong)(size1 ^ size2 ^ 0xDAD76848u) << 32) | size2;
        ulong wrongRotateRight18 = BitOperations.RotateRight(combined, 18) ^ 0xA4F1A11747816520UL;
        Assert.NotEqual((ulong)bundle.Length, wrongRotateRight18);
    }

    [Theory]
    [InlineData(0x00000243u, 7u)]  // Encrypted + LZ4HC BlocksInfo + StartPadding
    [InlineData(0x00000240u, 7u)]  // Uncompressed BlocksInfo + StartPadding
    [InlineData(0x000000C3u, 6u)]  // BlocksInfoAtEnd + 40-byte header
    public void ConvertToStandardUnityFs_ProducesValidUnityFsForAllLayouts(uint flags, uint encFlags)
    {
        byte[] cab = EndfieldTestBuilder.BuildSyntheticSerializedFileHeader("2022.3.10f1", 220);
        byte[] resS = new byte[96];
        for (int i = 0; i < resS.Length; i++)
            resS[i] = (byte)(255 - i);

        byte[] bundle = EndfieldTestBuilder.BuildEndfieldBundle(
            [
                new EndfieldTestBuilder.TestBlockSpec(cab, Flags: 5),
                new EndfieldTestBuilder.TestBlockSpec(resS, Flags: 0),
            ],
            [
                new EndfieldTestBuilder.TestNodeSpec("CAB-11223344", 0, cab.Length, 4),
                new EndfieldTestBuilder.TestNodeSpec("CAB-11223344.resS", cab.Length, resS.Length, 0),
            ],
            flags: flags,
            encFlags: encFlags);

        Assert.True(EndfieldBundle.IsEndfieldVfsBundle(bundle));
        Assert.False(EndfieldBundle.IsStandardUnityBundle(bundle));

        using var output = new MemoryStream();
        EndfieldBundle.ConvertToStandardUnityFs(bundle, output);
        byte[] unityFs = output.ToArray();

        Assert.True(EndfieldBundle.IsStandardUnityBundle(unityFs));
        Assert.StartsWith("UnityFS\0", Encoding.ASCII.GetString(unityFs, 0, 8));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32BigEndian(unityFs.AsSpan(8, 4)));

        // Verify unityRevision extracted from CAB-11223344 ("2022.3.10f1")
        int revStart = 8 + 4 + "5.x.x\0".Length;
        int revEnd = Array.IndexOf(unityFs, (byte)0, revStart);
        Assert.Equal("2022.3.10f1", Encoding.ASCII.GetString(unityFs, revStart, revEnd - revStart));

        int metaStart = revEnd + 1;
        long totalSize = BinaryPrimitives.ReadInt64BigEndian(unityFs.AsSpan(metaStart, 8));
        uint compInfoSize = BinaryPrimitives.ReadUInt32BigEndian(unityFs.AsSpan(metaStart + 8, 4));
        uint uncompInfoSize = BinaryPrimitives.ReadUInt32BigEndian(unityFs.AsSpan(metaStart + 12, 4));
        uint outFlags = BinaryPrimitives.ReadUInt32BigEndian(unityFs.AsSpan(metaStart + 16, 4));

        Assert.Equal(unityFs.Length, totalSize);
        Assert.Equal(compInfoSize, uncompInfoSize);
        Assert.Equal(0x40u, outFlags);

        // Verify directory node names in output UnityFS BlocksInfo are accurately unmasked
        int infoStart = metaStart + 20;
        int pos = infoStart + 16;
        int outBlockCount = BinaryPrimitives.ReadInt32BigEndian(unityFs.AsSpan(pos, 4));
        pos += 4 + outBlockCount * 10;
        int outNodeCount = BinaryPrimitives.ReadInt32BigEndian(unityFs.AsSpan(pos, 4));
        pos += 4;
        var decodedNodePaths = new List<string>(outNodeCount);
        for (int i = 0; i < outNodeCount; i++)
        {
            pos += 20;
            int term = Array.IndexOf(unityFs, (byte)0, pos);
            Assert.True(term > pos);
            decodedNodePaths.Add(Encoding.UTF8.GetString(unityFs, pos, term - pos));
            pos = term + 1;
        }
        Assert.Equal(["CAB-11223344", "CAB-11223344.resS"], decodedNodePaths);

        int dataStart = metaStart + 20 + (int)uncompInfoSize;
        Assert.Equal(cab, unityFs.AsSpan(dataStart, cab.Length).ToArray());
        Assert.Equal(resS, unityFs.AsSpan(dataStart + cab.Length, resS.Length).ToArray());
    }

    [Fact]
    public void ConvertToStandardUnityFs_RejectsTruncatedAndCorruptedInputs()
    {
        byte[] cab = EndfieldTestBuilder.BuildSyntheticSerializedFileHeader("2021.3.34f5", 120);
        byte[] valid = EndfieldTestBuilder.BuildEndfieldBundle(
            [new EndfieldTestBuilder.TestBlockSpec(cab, Flags: 5)],
            [new EndfieldTestBuilder.TestNodeSpec("CAB-abc", 0, cab.Length)]);

        // 1. Too short
        Assert.Throws<InvalidDataException>(() =>
            EndfieldBundle.ConvertToStandardUnityFs(valid.AsSpan(0, 20), Stream.Null));

        // 2. Bad header checksum
        byte[] badMagic = valid.ToArray();
        badMagic[5] ^= 0xFF;
        Assert.False(EndfieldBundle.IsEndfieldVfsBundle(badMagic));
        Assert.Throws<InvalidDataException>(() =>
            EndfieldBundle.ConvertToStandardUnityFs(badMagic, Stream.Null));

        // 3. Truncated body
        Assert.Throws<InvalidDataException>(() =>
            EndfieldBundle.ConvertToStandardUnityFs(valid.AsSpan(0, valid.Length - 8), Stream.Null));

        // 4. Corrupted LZ4Inv data block (corrupt token + length prefix of data block)
        EndfieldBundleHeader hdr = EndfieldBundle.ParseHeader(valid);
        int dataStart = hdr.HeaderLength + (int)((hdr.CompressedBlocksInfoSize + 15) & ~15u);
        byte[] corruptData = valid.ToArray();
        corruptData[dataStart] ^= 0xFF;
        corruptData[dataStart + 1] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() =>
            EndfieldBundle.ConvertToStandardUnityFs(corruptData, Stream.Null));

        // 5. Unsupported compression type in flags
        byte[] badCompFlags = valid.ToArray();
        uint flags2 = BinaryPrimitives.ReadUInt32BigEndian(valid.AsSpan(10, 4));
        EndfieldTestBuilder.WriteEndfieldHeader(
            badCompFlags,
            BinaryPrimitives.ReadUInt32BigEndian(valid.AsSpan(0, 4)),
            hdr.Size,
            hdr.CompressedBlocksInfoSize,
            hdr.UncompressedBlocksInfoSize,
            (hdr.Flags & ~0x3Fu) | 1u, // LZMA (unsupported)
            hdr.EncFlags,
            flags2,
            0,
            0,
            0);
        Assert.Throws<InvalidDataException>(() =>
            EndfieldBundle.ConvertToStandardUnityFs(badCompFlags, Stream.Null));

        // 6. Out-of-bounds node range in BlocksInfo
        byte[] badNode = EndfieldTestBuilder.BuildEndfieldBundle(
            [new EndfieldTestBuilder.TestBlockSpec(cab, Flags: 0)],
            [new EndfieldTestBuilder.TestNodeSpec("CAB-oob", 0, cab.Length + 16)],
            flags: 0x00000240u,
            encFlags: 7u);
        Assert.Throws<InvalidDataException>(() =>
            EndfieldBundle.ConvertToStandardUnityFs(badNode, Stream.Null));
    }

    [Fact]
    public void IsStandardUnityBundle_RecognizesAllStandardSignatures()
    {
        Assert.True(EndfieldBundle.IsStandardUnityBundle("UnityFS\0rest"u8));
        Assert.True(EndfieldBundle.IsStandardUnityBundle("UnityWeb\0rest"u8));
        Assert.True(EndfieldBundle.IsStandardUnityBundle("UnityRaw\0rest"u8));
        Assert.True(EndfieldBundle.IsStandardUnityBundle("UnityArchive\0rest"u8));
        Assert.False(EndfieldBundle.IsStandardUnityBundle("UnityXX\0"u8));
    }
}
