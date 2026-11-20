using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using BydTools.Core.Crypto;
using BydTools.Core.IO;
using BydTools.Formats.Bundle;
using BydTools.Formats.Manifest;
using BydTools.VFS;

namespace BydTools.Tests.TestHelpers;

internal static class EndfieldTestBuilder
{
    public sealed record TestBlockSpec(byte[] Payload, ushort Flags = 5);

    public sealed record TestNodeSpec(string Path, long Offset, long Size, uint Flags = 4);

    public static byte[] EncodeStandardLz4LiteralsOnly(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        int lit = Math.Min(15, data.Length);
        ms.WriteByte((byte)(lit << 4));
        if (lit == 15)
        {
            int rem = data.Length - 15;
            while (rem >= 255)
            {
                ms.WriteByte(255);
                rem -= 255;
            }
            ms.WriteByte((byte)rem);
        }
        ms.Write(data);
        return ms.ToArray();
    }

    public static byte[] EncodeLz4InvLiteralsAndSingleMatch(
        ReadOnlySpan<byte> literalPrefix,
        int matchOffset,
        int matchLength)
    {
        if (matchLength < 4)
            throw new ArgumentOutOfRangeException(nameof(matchLength));

        using var ms = new MemoryStream();
        int litNibble = Math.Min(15, literalPrefix.Length);
        int encCount = matchLength - 4;
        int encNibble = Math.Min(15, encCount);

        byte token = (byte)(
            ((litNibble & 0b1100) << 2)
            | (litNibble & 0b0011)
            | ((encNibble & 0b1100) << 4)
            | ((encNibble & 0b0011) << 2)
        );
        ms.WriteByte(token);

        if (litNibble == 15)
        {
            int rem = literalPrefix.Length - 15;
            while (rem >= 255)
            {
                ms.WriteByte(255);
                rem -= 255;
            }
            ms.WriteByte((byte)rem);
        }

        ms.Write(literalPrefix);

        Span<byte> offBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(offBuf, (ushort)matchOffset);
        ms.Write(offBuf);

        if (encNibble == 15)
        {
            int rem = encCount - 15;
            while (rem >= 255)
            {
                ms.WriteByte(255);
                rem -= 255;
            }
            ms.WriteByte((byte)rem);
        }

        return ms.ToArray();
    }

    public static byte[] BuildSyntheticSerializedFileHeader(string unityVersion, int bodySize = 128)
    {
        int total = Math.Max(48 + unityVersion.Length + 1 + 16, bodySize);
        var buf = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), 24);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), 22); // SerializedFile v22
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), 48);
        Encoding.ASCII.GetBytes(unityVersion, buf.AsSpan(48));
        buf[48 + unityVersion.Length] = 0;
        for (int i = 48 + unityVersion.Length + 1; i < total; i++)
            buf[i] = (byte)(i & 0xFF);
        return buf;
    }

    public static byte[] BuildEndfieldBundle(
        IReadOnlyList<TestBlockSpec> blocks,
        IReadOnlyList<TestNodeSpec> nodes,
        uint flags = 0x00000243u,
        uint encFlags = 7u,
        uint magicSeed = 0x12345678u,
        uint flags2 = 0x11223344u,
        ushort cb2 = 0x1357,
        ushort ub2 = 0x2468,
        uint size2 = 0x89ABCDEFu)
    {
        var encodedBlocks = new List<(uint UncompSize, uint CompSize, ushort Flags, byte[] Data)>(blocks.Count);
        foreach (var blk in blocks)
        {
            byte[] stored;
            if (blk.Flags == 0)
            {
                stored = blk.Payload.ToArray();
            }
            else if (blk.Flags == 5)
            {
                byte[] lz4Inv = EncodeLz4InvLiteralsOnly(blk.Payload);
                EndfieldVfsCipher.TransformBlock(lz4Inv);
                stored = lz4Inv;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(blk.Flags));
            }

            encodedBlocks.Add(((uint)blk.Payload.Length, (uint)stored.Length, blk.Flags, stored));
        }

        byte[] rawBlocksInfo = EncodeBlocksInfo(encodedBlocks, nodes);
        uint compType = flags & 0x3Fu;
        byte[] storedBlocksInfo;
        if (compType == 0)
        {
            storedBlocksInfo = rawBlocksInfo;
        }
        else if (compType is 2 or 3)
        {
            storedBlocksInfo = EncodeStandardLz4LiteralsOnly(rawBlocksInfo);
            EndfieldVfsCipher.TransformBlock(storedBlocksInfo);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }

        int hdrLen = encFlags >= 7 ? 48 : 40;
        bool atEnd = (flags & 0x80u) != 0;
        bool padStart = (flags & 0x200u) != 0;
        int paddedCb = (!atEnd && padStart)
            ? (storedBlocksInfo.Length + 15) & ~15
            : storedBlocksInfo.Length;

        long dataBytesLen = encodedBlocks.Sum(b => (long)b.Data.Length);
        long totalSize = hdrLen + (atEnd ? dataBytesLen + storedBlocksInfo.Length : paddedCb + dataBytesLen);

        var output = new byte[totalSize];
        WriteEndfieldHeader(
            output,
            magicSeed,
            totalSize,
            (uint)storedBlocksInfo.Length,
            (uint)rawBlocksInfo.Length,
            flags,
            encFlags,
            flags2,
            cb2,
            ub2,
            size2);

        if (atEnd)
        {
            int pos = hdrLen;
            foreach (var blk in encodedBlocks)
            {
                blk.Data.CopyTo(output.AsSpan(pos));
                pos += blk.Data.Length;
            }
            storedBlocksInfo.CopyTo(output.AsSpan(pos));
        }
        else
        {
            storedBlocksInfo.CopyTo(output.AsSpan(hdrLen));
            int pos = hdrLen + paddedCb;
            foreach (var blk in encodedBlocks)
            {
                blk.Data.CopyTo(output.AsSpan(pos));
                pos += blk.Data.Length;
            }
        }

        return output;
    }

    public static byte[] BuildSyntheticUnityFs(string unityVersion = "2021.3.34f5", int payloadBytes = 32)
    {
        using var ms = new MemoryStream();
        ms.Write("UnityFS\0"u8);
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(u32, 6);
        ms.Write(u32);
        ms.Write("5.x.x\0"u8);
        ms.Write(Encoding.ASCII.GetBytes(unityVersion));
        ms.WriteByte(0);
        ms.Write(new byte[20 + payloadBytes]);
        return ms.ToArray();
    }

    public static byte[] BuildValidHgmmapBinary(int extraBytes = 1024, string versionText = "1.0.0.0")
    {
        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, HgmmapManifest.Head1Magic);
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)versionText.Length);
        ms.Write(u32);
        ms.Write(Encoding.Unicode.GetBytes(versionText));

        BinaryPrimitives.WriteUInt32LittleEndian(u32, HgmmapManifest.Head2Magic);
        ms.Write(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 4);
        ms.Write(u32);
        ms.Write(Encoding.Unicode.GetBytes("test"));

        var tail = new byte[extraBytes];
        for (int i = 0; i < tail.Length; i++)
            tail[i] = (byte)((i * 13 + 7) & 0xFF);
        ms.Write(tail);
        return ms.ToArray();
    }

    public static byte[] CompressBrotli(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var brotli = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(data);
        return ms.ToArray();
    }

    public static void WriteSyntheticVfsBlock(
        string vfsRoot,
        BlockDescriptor descriptor,
        string groupCfgName,
        byte[] key,
        IReadOnlyList<(string RelativePath, byte[] Data)> files)
    {
        string blockDir = Path.Combine(vfsRoot, descriptor.DirHash);
        Directory.CreateDirectory(blockDir);

        var chunkMd5 = UInt128.Parse("00112233445566778899AABBCCDDEEFF", System.Globalization.NumberStyles.HexNumber);
        string chunkFileName = chunkMd5.ToHexLittleEndian();
        using var chkMs = new MemoryStream();
        var entries = new List<(string Name, long Offset, byte[] Stored)>();
        foreach ((string rel, byte[] raw) in files)
        {
            long offset = chkMs.Position;
            chkMs.Write(raw);
            entries.Add((rel, offset, raw));
        }

        byte[] chkBytes = chkMs.ToArray();
        File.WriteAllBytes(Path.Combine(blockDir, chunkFileName + ".chk"), chkBytes);

        using var blcPlain = new MemoryStream();
        using var w = new BinaryWriter(blcPlain, Encoding.UTF8, leaveOpen: true);

        w.Write(4); // codeVersion = 4
        w.Write(1); // version = 1
        byte[] nameBytes = Encoding.UTF8.GetBytes(groupCfgName);
        w.Write((ushort)nameBytes.Length);
        w.Write(nameBytes);

        uint dirHash = Convert.ToUInt32(descriptor.DirHash, 16);
        Span<byte> hashBe = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(hashBe, dirHash);
        w.Write(hashBe);
        w.Write(0); // skipped 4 bytes

        w.Write(entries.Count); // groupFileInfoNum
        w.Write((long)chkBytes.Length); // groupChunksLength
        w.Write((byte)descriptor.Type); // blockType
        w.Write(1); // chunkCount

        WriteUInt128(w, chunkMd5);
        WriteUInt128(w, chunkMd5);
        w.Write((long)chkBytes.Length);
        w.Write((byte)descriptor.Type);
        w.Write(0); // chunkTag (codeVersion == 4 -> 4-byte int32)
        w.Write(entries.Count);

        foreach ((string rel, long offset, byte[] raw) in entries)
        {
            byte[] relBytes = Encoding.UTF8.GetBytes(rel);
            w.Write((ushort)relBytes.Length);
            w.Write(relBytes);
            w.Write((long)rel.GetHashCode());
            WriteUInt128(w, chunkMd5);
            WriteUInt128(w, ComputeMd5U128(raw));
            w.Write(offset);
            w.Write((long)raw.Length);
            w.Write((byte)descriptor.Type);
            w.Write((byte)0); // encrypted = false
            w.Write(0); // fileTag (codeVersion == 4 -> 4-byte int32)
        }

        byte[] plain = blcPlain.ToArray();
        byte[] nonce = new byte[VFSDefine.NonceLength];
        BinaryPrimitives.WriteInt32LittleEndian(nonce, 1);
        CSChaCha20.DecryptInPlace(key, nonce, 1, plain);

        byte[] blc = new byte[nonce.Length + plain.Length];
        nonce.CopyTo(blc, 0);
        plain.CopyTo(blc, nonce.Length);
        File.WriteAllBytes(Path.Combine(blockDir, descriptor.DirHash + ".blc"), blc);
    }

    private static byte[] EncodeLz4InvLiteralsOnly(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        int litNibble = Math.Min(15, data.Length);
        byte token = (byte)(((litNibble & 0b1100) << 2) | (litNibble & 0b0011));
        ms.WriteByte(token);
        if (litNibble == 15)
        {
            int rem = data.Length - 15;
            while (rem >= 255)
            {
                ms.WriteByte(255);
                rem -= 255;
            }
            ms.WriteByte((byte)rem);
        }
        ms.Write(data);
        return ms.ToArray();
    }

    private static byte[] EncodeBlocksInfo(
        List<(uint UncompSize, uint CompSize, ushort Flags, byte[] Data)> blocks,
        IReadOnlyList<TestNodeSpec> nodes)
    {
        using var ms = new MemoryStream();
        Span<byte> buf4 = stackalloc byte[4];
        Span<byte> buf2 = stackalloc byte[2];

        // blocksCount
        uint bScrambled = BitOperations.RotateLeft((uint)blocks.Count ^ 0x91CE0A4Fu, 18);
        ushort bLow = (ushort)bScrambled;
        ushort bHigh = (ushort)(bScrambled >> 16);
        uint bCombined = ((uint)(ushort)(bLow ^ bHigh) << 16) | bLow;
        uint bLe = BinaryPrimitives.ReverseEndianness(bCombined) ^ 0x8A7BF723u;
        BinaryPrimitives.WriteUInt32LittleEndian(buf4, bLe);
        ms.Write(buf4);

        foreach (var blk in blocks)
        {
            uint uScr = BitOperations.RotateLeft(blk.UncompSize ^ 0xF74324EEu, 18);
            ushort c = (ushort)uScr;
            ushort a = (ushort)((uScr >> 16) ^ c ^ 0xA121);

            uint cScr = BitOperations.RotateLeft(blk.CompSize ^ 0xF74324EEu, 18);
            ushort d = (ushort)cScr;
            ushort b = (ushort)((cScr >> 16) ^ d ^ 0xA121);

            ushort fMask = (ushort)(blk.Flags ^ c ^ 0x523F);
            ushort fRot = (ushort)((fMask >> 14) | (fMask << 2));
            byte a0 = (byte)fRot;
            byte a1 = (byte)((fRot >> 8) ^ a0);
            ushort encFlags = (ushort)(((a1 << 8) | a0) ^ 0x9CD6);

            WriteUInt16Be(ms, buf2, a);
            WriteUInt16Be(ms, buf2, b);
            WriteUInt16Be(ms, buf2, c);
            WriteUInt16Be(ms, buf2, encFlags);
            WriteUInt16Be(ms, buf2, d);
        }

        // nodesCount
        uint nScrambled = BitOperations.RotateLeft((uint)nodes.Count ^ 0xE4C1D9F2u, 18);
        ushort nLow = (ushort)nScrambled;
        ushort nHigh = (ushort)(nScrambled >> 16);
        uint nCombined = ((uint)(ushort)(nLow ^ nHigh) << 16) | nLow;
        uint nLe = BinaryPrimitives.ReverseEndianness(nCombined) ^ 0x5DE50A6Bu;
        BinaryPrimitives.WriteUInt32LittleEndian(buf4, nLe);
        ms.Write(buf4);

        for (int i = 0; i < nodes.Count; i++)
        {
            TestNodeSpec node = nodes[i];
            ulong offScr = BitOperations.RotateRight((ulong)node.Offset ^ 0xA4F1A11747816520UL, 14);
            uint c = (uint)offScr;
            uint d = (uint)(offScr >> 32) ^ c ^ 0xDAD76848u;

            ulong szScr = BitOperations.RotateRight((ulong)node.Size ^ 0xA4F1A11747816520UL, 14);
            uint e = (uint)szScr;
            uint b = (uint)(szScr >> 32) ^ e ^ 0xDAD76848u;

            uint aRot = BitOperations.RotateLeft(node.Flags ^ 0xF13927C4u ^ b, 18);
            ushort a0 = (ushort)aRot;
            ushort a1 = (ushort)((aRot >> 16) ^ a0);
            uint a = (((uint)a1 << 16) | a0) ^ 0x8E06A9F8u;

            WriteUInt32Be(ms, buf4, a);
            WriteUInt32Be(ms, buf4, b);
            WriteUInt32Be(ms, buf4, c);
            WriteUInt32Be(ms, buf4, d);

            byte[] nameBytes = Encoding.UTF8.GetBytes(node.Path);
            for (int j = 0; j < nameBytes.Length; j++)
                ms.WriteByte((byte)(nameBytes[j] ^ ((j ^ 0x97) & 0xFF)));
            ms.WriteByte(0);

            WriteUInt32Be(ms, buf4, e);
        }

        return ms.ToArray();
    }

    public static void WriteEndfieldHeader(
        byte[] target,
        uint magicSeed,
        long size,
        uint cb,
        uint ub,
        uint flags,
        uint encFlags,
        uint flags2,
        ushort cb2,
        ushort ub2,
        uint size2)
    {
        uint x = magicSeed ^ 0x4A92F0CDu;
        uint magicB = ((4u * x) & 0xFFFF0000u) ^ BitOperations.RotateRight(x, 14) ^ 0xD8B1E637u;
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(0, 4), magicSeed);
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(4, 4), magicB);

        uint cbScr = BitOperations.RotateLeft(cb ^ 0xF74324EEu, 18);
        ushort cbLow = (ushort)cbScr;
        ushort cbHigh = (ushort)(cbScr >> 16);
        ushort cb1 = (ushort)(cbHigh ^ cb2 ^ 0xA121);
        // Since cbLow is directly stored in cb2 during decode, override cb2 with cbLow
        cb2 = cbLow;
        cb1 = (ushort)(cbHigh ^ cb2 ^ 0xA121);

        uint ubScr = BitOperations.RotateLeft(ub ^ 0xF74324EEu, 18);
        ub2 = (ushort)ubScr;
        ushort ub1 = (ushort)((ubScr >> 16) ^ ub2 ^ 0xA121);

        ulong szScr = BitOperations.RotateRight((ulong)size ^ 0xA4F1A11747816520UL, 14);
        size2 = (uint)szScr;
        uint size1 = (uint)(szScr >> 32) ^ size2 ^ 0xDAD76848u;

        uint encFlagsRaw = encFlags ^ flags2;
        uint flags1 = flags ^ flags2 ^ 0xA7F49310u;

        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(8, 2), cb2);
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(10, 4), flags2);
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(14, 4), encFlagsRaw);
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(18, 4), size2);
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(22, 4), flags1);
        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(26, 2), ub1);
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(28, 4), 0xCAFEBABEu);
        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(32, 2), ub2);
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(34, 4), size1);
        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(38, 2), cb1);
        target[40] = 0x5A;
    }

    private static void WriteUInt16Be(Stream ms, Span<byte> buf, ushort val)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buf, val);
        ms.Write(buf);
    }

    private static void WriteUInt32Be(Stream ms, Span<byte> buf, uint val)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buf, val);
        ms.Write(buf);
    }

    private static void WriteUInt128(BinaryWriter w, UInt128 value)
    {
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(buf, value);
        w.Write(buf);
    }

    private static UInt128 ComputeMd5U128(byte[] data)
    {
        byte[] hash = MD5.HashData(data);
        return BinaryPrimitives.ReadUInt128LittleEndian(hash);
    }
}
