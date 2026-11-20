// Ported and adapted from Escartem/AnimeStudio (VFSFile.cs / VFSUtils.cs)
// Source: https://github.com/Escartem/AnimeStudio
// License: MIT
// Copyright (c) 2016 Radu; (c) 2016-2020 Perfare; (c) 2022-2024 Razmoth; (c) 2024-2026 Escartem
//
// Note: 64-bit header size descrambling uses RotateLeft(..., 14) rather than AnimeStudio's
// RotateRight(..., 18), matching the 32-bit/64-bit field symmetry verified against game samples.

using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BydTools.Formats.MemoryPack;

namespace BydTools.Formats.Bundle;

public readonly record struct EndfieldBundleHeader(
    long Size,
    uint CompressedBlocksInfoSize,
    uint UncompressedBlocksInfoSize,
    uint Flags,
    uint EncFlags,
    int HeaderLength
);

internal readonly record struct BundleStorageBlock(
    uint UncompressedSize,
    uint CompressedSize,
    ushort Flags
);

internal readonly record struct BundleDirectoryNode(
    string Path,
    long Offset,
    long Size,
    uint Flags
);

/// <summary>
/// Parses Arknights: Endfield custom VFS asset bundles (.ab) and rewrites them into
/// standard uncompressed UnityFS v6 bundles consumable by standard Unity tooling.
/// </summary>
public static class EndfieldBundle
{
    public const int MinimumHeaderBytes = 41;

    private const uint ArchiveCompressionMask = 0x3Fu;
    private const uint ArchiveBlocksInfoAtEnd = 0x80u;
    private const uint ArchiveBlockInfoNeedPaddingAtStart = 0x200u;
    private const uint StandardUnityFsFlags = 0x40u; // BlocksAndDirectoryInfoCombined, Compression=None
    private const string DefaultUnityRevision = "2021.3.34f5";

    private const int MaxBlocksInfoBytes = 64 * 1024 * 1024;
    private const int MaxStorageBlocks = 1_000_000;
    private const int MaxDirectoryNodes = 1_000_000;
    private const int MaxNodePathBytes = 1024;
    private const int MaxBlockUncompressedBytes = 512 * 1024 * 1024;
    private const long MaxTotalUncompressedBytes = 2L * 1024 * 1024 * 1024;

    private static ReadOnlySpan<byte> UnityFsSignature => "UnityFS\0"u8;
    private static ReadOnlySpan<byte> UnityWebSignature => "UnityWeb\0"u8;
    private static ReadOnlySpan<byte> UnityRawSignature => "UnityRaw\0"u8;
    private static ReadOnlySpan<byte> UnityArchiveSignature => "UnityArchive\0"u8;

    public static bool IsStandardUnityBundle(ReadOnlySpan<byte> data) =>
        data.StartsWith(UnityFsSignature)
        || data.StartsWith(UnityWebSignature)
        || data.StartsWith(UnityRawSignature)
        || data.StartsWith(UnityArchiveSignature);

    public static bool IsEndfieldVfsBundle(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinimumHeaderBytes)
            return false;

        uint a = BinaryPrimitives.ReadUInt32BigEndian(data);
        uint b = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        return b == EndfieldVfsCipher.UnpackHeaderMagicChecksum(a);
    }

    public static EndfieldBundleHeader ParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinimumHeaderBytes)
        {
            throw new InvalidDataException(
                $"Endfield bundle is too short ({data.Length} bytes, minimum {MinimumHeaderBytes})."
            );
        }

        if (!IsEndfieldVfsBundle(data))
            throw new InvalidDataException("Invalid Endfield VFS bundle header checksum.");

        ushort cb2 = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2));
        uint flags2 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(10, 4));
        uint encFlagsRaw = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(14, 4));
        uint size2 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(18, 4));
        uint flags1 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(22, 4));
        ushort ub1 = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(26, 2));
        // data[28..32] unused header field
        ushort ub2 = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(32, 2));
        uint size1 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(34, 4));
        ushort cb1 = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(38, 2));
        // data[40] unused byte

        uint compressedBlocksInfoSize =
            BitOperations.RotateRight(Concat((ushort)(cb1 ^ cb2 ^ 0xA121), cb2), 18) ^ 0xF74324EEu;
        uint uncompressedBlocksInfoSize =
            BitOperations.RotateRight(Concat((ushort)(ub1 ^ ub2 ^ 0xA121), ub2), 18) ^ 0xF74324EEu;
        ulong rawSize =
            BitOperations.RotateLeft(Concat(size1 ^ size2 ^ 0xDAD76848u, size2), 14) ^ 0xA4F1A11747816520UL;

        uint encFlags = encFlagsRaw ^ flags2;
        uint flags = flags1 ^ flags2 ^ 0xA7F49310u;
        int headerLength = encFlags >= 7 ? 48 : 40;

        if (rawSize < (ulong)headerLength || rawSize > (ulong)data.Length || rawSize > long.MaxValue)
        {
            throw new InvalidDataException(
                $"Endfield bundle declared size {rawSize} is outside available buffer length {data.Length}."
            );
        }

        if (data.Length < headerLength)
        {
            throw new InvalidDataException(
                $"Endfield bundle truncated inside {headerLength}-byte header (got {data.Length} bytes)."
            );
        }

        return new EndfieldBundleHeader(
            (long)rawSize,
            compressedBlocksInfoSize,
            uncompressedBlocksInfoSize,
            flags,
            encFlags,
            headerLength
        );
    }

    public static void ConvertToStandardUnityFs(ReadOnlySpan<byte> data, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        EndfieldBundleHeader header = ParseHeader(data);
        if (header.CompressedBlocksInfoSize > MaxBlocksInfoBytes
            || header.UncompressedBlocksInfoSize > MaxBlocksInfoBytes)
        {
            throw new InvalidDataException(
                $"BlocksInfo size ({header.CompressedBlocksInfoSize} / {header.UncompressedBlocksInfoSize}) exceeds {MaxBlocksInfoBytes} bytes."
            );
        }

        bool blocksInfoAtEnd = (header.Flags & ArchiveBlocksInfoAtEnd) != 0;
        long blocksInfoOffset;
        long dataStart;
        long dataEnd;

        if (blocksInfoAtEnd)
        {
            blocksInfoOffset = header.Size - header.CompressedBlocksInfoSize;
            if (blocksInfoOffset < header.HeaderLength)
            {
                throw new InvalidDataException(
                    $"BlocksInfo at offset {blocksInfoOffset} overlaps {header.HeaderLength}-byte header."
                );
            }

            dataStart = header.HeaderLength;
            dataEnd = blocksInfoOffset;
        }
        else
        {
            blocksInfoOffset = header.HeaderLength;
            long paddedCb = (header.Flags & ArchiveBlockInfoNeedPaddingAtStart) != 0
                ? (header.CompressedBlocksInfoSize + 15L) & ~15L
                : header.CompressedBlocksInfoSize;
            dataStart = header.HeaderLength + paddedCb;
            dataEnd = header.Size;
            if (dataStart > dataEnd)
            {
                throw new InvalidDataException(
                    $"BlocksInfo end ({dataStart}) exceeds declared bundle size {header.Size}."
                );
            }
        }

        if (blocksInfoOffset + header.CompressedBlocksInfoSize > data.Length)
            throw new InvalidDataException("BlocksInfo extends past the end of the bundle.");

        ReadOnlySpan<byte> compressedBlocksInfo = data.Slice(
            (int)blocksInfoOffset,
            (int)header.CompressedBlocksInfoSize
        );
        byte[] blocksInfoBytes = DecodeBlocksInfoBytes(
            compressedBlocksInfo,
            (int)header.UncompressedBlocksInfoSize,
            header.Flags & ArchiveCompressionMask
        );

        ParseBlocksAndNodes(
            blocksInfoBytes,
            out BundleStorageBlock[] blocks,
            out BundleDirectoryNode[] nodes,
            out long totalUncompressedSize
        );

        long cursor = dataStart;
        foreach (BundleStorageBlock block in blocks)
        {
            long consumed = block.Flags switch
            {
                0 => block.UncompressedSize,
                5 => block.CompressedSize,
                _ => throw new InvalidDataException(
                    $"Unsupported Endfield bundle block compression flag {block.Flags}."
                ),
            };

            if (block.Flags == 0 && block.CompressedSize != block.UncompressedSize)
            {
                throw new InvalidDataException(
                    $"Uncompressed block has mismatched sizes ({block.CompressedSize} vs {block.UncompressedSize})."
                );
            }

            if (consumed > dataEnd - cursor)
            {
                throw new InvalidDataException(
                    $"Block payload ({consumed} bytes at offset {cursor}) exceeds data section end {dataEnd}."
                );
            }

            cursor += consumed;
        }

        byte[]? firstBlock = blocks.Length > 0
            ? DecompressSingleBlock(data, dataStart, blocks[0])
            : null;
        string unityRevision = DetectUnityRevision(firstBlock, nodes) ?? DefaultUnityRevision;

        byte[] standardBlocksInfo = BuildStandardBlocksInfo(blocks, nodes);
        byte[] standardHeader = BuildStandardHeader(
            unityRevision,
            standardBlocksInfo.Length,
            totalUncompressedSize
        );

        output.Write(standardHeader);
        output.Write(standardBlocksInfo);

        if (firstBlock != null)
        {
            output.Write(firstBlock);
            long offset = dataStart + (blocks[0].Flags == 0 ? blocks[0].UncompressedSize : blocks[0].CompressedSize);
            for (int i = 1; i < blocks.Length; i++)
            {
                BundleStorageBlock block = blocks[i];
                WriteDecompressedBlock(data, offset, block, output);
                offset += block.Flags == 0 ? block.UncompressedSize : block.CompressedSize;
            }
        }
    }

    private static byte[] DecodeBlocksInfoBytes(
        ReadOnlySpan<byte> compressed,
        int uncompressedSize,
        uint compressionType
    )
    {
        switch (compressionType)
        {
            case 0:
                if (compressed.Length != uncompressedSize)
                {
                    throw new InvalidDataException(
                        $"Uncompressed BlocksInfo length {compressed.Length} != declared {uncompressedSize}."
                    );
                }

                return compressed.ToArray();

            case 2: // LZ4
            case 3: // LZ4HC
            {
                byte[] decrypted = ArrayPool<byte>.Shared.Rent(compressed.Length);
                try
                {
                    Span<byte> decSpan = decrypted.AsSpan(0, compressed.Length);
                    compressed.CopyTo(decSpan);
                    EndfieldVfsCipher.TransformBlock(decSpan);
                    return Lz4Block.Decompress(decSpan, uncompressedSize);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(decrypted);
                }
            }

            default:
                throw new InvalidDataException(
                    $"Unsupported Endfield BlocksInfo compression type {compressionType}."
                );
        }
    }

    private static void ParseBlocksAndNodes(
        ReadOnlySpan<byte> raw,
        out BundleStorageBlock[] blocks,
        out BundleDirectoryNode[] nodes,
        out long totalUncompressedSize
    )
    {
        int pos = 0;
        if (raw.Length < 8)
            throw new InvalidDataException("BlocksInfo payload is truncated.");

        uint encBlocksCount = BinaryPrimitives.ReverseEndianness(
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(pos, 4)) ^ 0x8A7BF723u
        );
        pos += 4;
        ushort bLow = (ushort)encBlocksCount;
        ushort bHigh = (ushort)(encBlocksCount >> 16);
        uint blocksCount =
            BitOperations.RotateRight(Concat((ushort)(bLow ^ bHigh), bLow), 18) ^ 0x91CE0A4Fu;

        if (blocksCount > MaxStorageBlocks || (long)blocksCount * 10 > raw.Length - pos - 4)
        {
            throw new InvalidDataException(
                $"Endfield BlocksInfo block count {blocksCount} is invalid for {raw.Length} bytes."
            );
        }

        blocks = new BundleStorageBlock[blocksCount];
        totalUncompressedSize = 0;
        for (int i = 0; i < blocks.Length; i++)
        {
            ushort a = BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(pos, 2));
            ushort b = BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(pos + 2, 2));
            ushort c = BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(pos + 4, 2));
            ushort encFlags = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(pos + 6, 2)) ^ 0x9CD6);
            ushort d = BinaryPrimitives.ReadUInt16BigEndian(raw.Slice(pos + 8, 2));
            pos += 10;

            byte a0 = (byte)encFlags;
            byte a1 = (byte)(encFlags >> 8);
            ushort blockFlags = (ushort)(c ^ RotateLeft16(Concat((byte)(a0 ^ a1), a0), 14) ^ 0x523F);
            uint uncompSize =
                BitOperations.RotateRight(Concat((ushort)(a ^ c ^ 0xA121), c), 18) ^ 0xF74324EEu;
            uint compSize =
                BitOperations.RotateRight(Concat((ushort)(b ^ d ^ 0xA121), d), 18) ^ 0xF74324EEu;

            if (uncompSize > MaxBlockUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"Storage block {i} uncompressed size {uncompSize} exceeds limit {MaxBlockUncompressedBytes}."
                );
            }

            totalUncompressedSize += uncompSize;
            if (totalUncompressedSize > MaxTotalUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"Total uncompressed bundle size exceeds limit {MaxTotalUncompressedBytes}."
                );
            }

            blocks[i] = new BundleStorageBlock(uncompSize, compSize, blockFlags);
        }

        if (raw.Length - pos < 4)
            throw new InvalidDataException("BlocksInfo truncated before node count.");

        uint encNodesCount = BinaryPrimitives.ReverseEndianness(
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(pos, 4)) ^ 0x5DE50A6Bu
        );
        pos += 4;
        ushort nLow = (ushort)encNodesCount;
        ushort nHigh = (ushort)(encNodesCount >> 16);
        uint nodesCount =
            BitOperations.RotateRight(Concat((ushort)(nLow ^ nHigh), nLow), 18) ^ 0xE4C1D9F2u;

        if (nodesCount > MaxDirectoryNodes || (long)nodesCount * 21 > raw.Length - pos)
        {
            throw new InvalidDataException(
                $"Endfield BlocksInfo node count {nodesCount} is invalid for remaining {raw.Length - pos} bytes."
            );
        }

        nodes = new BundleDirectoryNode[nodesCount];
        Span<byte> nameBuffer = stackalloc byte[MaxNodePathBytes];
        for (int i = 0; i < nodes.Length; i++)
        {
            if (raw.Length - pos < 17)
                throw new InvalidDataException($"BlocksInfo truncated reading node {i} header.");

            uint a = BinaryPrimitives.ReadUInt32BigEndian(raw.Slice(pos, 4)) ^ 0x8E06A9F8u;
            uint b = BinaryPrimitives.ReadUInt32BigEndian(raw.Slice(pos + 4, 4));
            uint c = BinaryPrimitives.ReadUInt32BigEndian(raw.Slice(pos + 8, 4));
            uint d = BinaryPrimitives.ReadUInt32BigEndian(raw.Slice(pos + 12, 4));
            pos += 16;

            int nameLen = 0;
            bool terminated = false;
            while (pos < raw.Length)
            {
                byte bt = raw[pos++];
                if (bt == 0)
                {
                    terminated = true;
                    break;
                }

                if (nameLen >= MaxNodePathBytes)
                    throw new InvalidDataException($"Node {i} path exceeds {MaxNodePathBytes} bytes.");

                nameBuffer[nameLen] = (byte)(bt ^ ((nameLen ^ 0x97) & 0xFF));
                nameLen++;
            }

            if (!terminated)
                throw new InvalidDataException($"Node {i} path is missing null terminator.");

            if (raw.Length - pos < 4)
                throw new InvalidDataException($"BlocksInfo truncated reading node {i} size.");

            uint e = BinaryPrimitives.ReadUInt32BigEndian(raw.Slice(pos, 4));
            pos += 4;

            ushort a0 = (ushort)a;
            ushort a1 = (ushort)(a >> 16);
            uint nodeFlags =
                BitOperations.RotateRight(Concat((ushort)(a1 ^ a0), a0), 18) ^ 0xF13927C4u ^ b;
            ulong nodeOffset =
                BitOperations.RotateLeft(Concat(d ^ c ^ 0xDAD76848u, c), 14) ^ 0xA4F1A11747816520UL;
            ulong nodeSize =
                BitOperations.RotateLeft(Concat(b ^ e ^ 0xDAD76848u, e), 14) ^ 0xA4F1A11747816520UL;

            if (nodeOffset > (ulong)totalUncompressedSize
                || nodeSize > (ulong)totalUncompressedSize - nodeOffset)
            {
                throw new InvalidDataException(
                    $"Directory node {i} range [{nodeOffset}, +{nodeSize}) exceeds uncompressed bundle size {totalUncompressedSize}."
                );
            }

            string nodePath = Encoding.UTF8.GetString(nameBuffer[..nameLen]);
            nodes[i] = new BundleDirectoryNode(nodePath, (long)nodeOffset, (long)nodeSize, nodeFlags);
        }

        if (pos != raw.Length)
        {
            throw new InvalidDataException(
                $"BlocksInfo has {raw.Length - pos} unexpected trailing bytes."
            );
        }
    }

    private static byte[] DecompressSingleBlock(
        ReadOnlySpan<byte> data,
        long dataOffset,
        BundleStorageBlock block
    )
    {
        if (block.Flags == 0)
            return data.Slice((int)dataOffset, (int)block.UncompressedSize).ToArray();

        int compSize = (int)block.CompressedSize;
        int uncompSize = (int)block.UncompressedSize;
        byte[] compBuf = ArrayPool<byte>.Shared.Rent(compSize);
        try
        {
            Span<byte> compSpan = compBuf.AsSpan(0, compSize);
            data.Slice((int)dataOffset, compSize).CopyTo(compSpan);
            EndfieldVfsCipher.TransformBlock(compSpan);
            return Lz4InvBlock.Decompress(compSpan, uncompSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compBuf);
        }
    }

    private static void WriteDecompressedBlock(
        ReadOnlySpan<byte> data,
        long dataOffset,
        BundleStorageBlock block,
        Stream output
    )
    {
        if (block.Flags == 0)
        {
            output.Write(data.Slice((int)dataOffset, (int)block.UncompressedSize));
            return;
        }

        int compSize = (int)block.CompressedSize;
        int uncompSize = (int)block.UncompressedSize;
        byte[] compBuf = ArrayPool<byte>.Shared.Rent(compSize);
        byte[] uncompBuf = ArrayPool<byte>.Shared.Rent(uncompSize);
        try
        {
            Span<byte> compSpan = compBuf.AsSpan(0, compSize);
            Span<byte> uncompSpan = uncompBuf.AsSpan(0, uncompSize);
            data.Slice((int)dataOffset, compSize).CopyTo(compSpan);
            EndfieldVfsCipher.TransformBlock(compSpan);

            int written = Lz4InvBlock.Decompress(compSpan, uncompSpan);
            if (written != uncompSize)
            {
                throw new InvalidDataException(
                    $"LZ4Inv block decoded {written} bytes, header declared {uncompSize}."
                );
            }

            output.Write(uncompSpan);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compBuf);
            ArrayPool<byte>.Shared.Return(uncompBuf);
        }
    }

    private static string? DetectUnityRevision(byte[]? firstBlock, BundleDirectoryNode[] nodes)
    {
        if (firstBlock == null)
            return null;

        foreach (BundleDirectoryNode node in nodes)
        {
            if (node.Offset < 0 || node.Size < 48 || node.Offset + node.Size > firstBlock.Length)
                continue;
            if (node.Path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                || node.Path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ReadOnlySpan<byte> cab = firstBlock.AsSpan((int)node.Offset, (int)node.Size);
            uint ver = BinaryPrimitives.ReadUInt32BigEndian(cab.Slice(8, 4));
            int versionOffset = ver >= 22 ? 48 : 20;
            if (ver < 7 || cab.Length <= versionOffset)
                continue;

            int term = cab[versionOffset..].IndexOf((byte)0);
            if (term <= 0 || term > 32)
                continue;

            ReadOnlySpan<byte> rawVer = cab.Slice(versionOffset, term);
            bool ascii = true;
            foreach (byte ch in rawVer)
            {
                if (ch < 0x20 || ch > 0x7E)
                {
                    ascii = false;
                    break;
                }
            }

            if (ascii)
                return Encoding.ASCII.GetString(rawVer);
        }

        return null;
    }

    private static byte[] BuildStandardBlocksInfo(
        BundleStorageBlock[] blocks,
        BundleDirectoryNode[] nodes
    )
    {
        int size = 16 + 4 + blocks.Length * 10 + 4;
        foreach (BundleDirectoryNode node in nodes)
            size += 20 + Encoding.UTF8.GetByteCount(node.Path) + 1;

        var buffer = new byte[size];
        int pos = 16; // 16 zero bytes for uncompressedDataHash

        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(pos, 4), blocks.Length);
        pos += 4;

        foreach (BundleStorageBlock block in blocks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos, 4), block.UncompressedSize);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos + 4, 4), block.UncompressedSize);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(pos + 8, 2), 0);
            pos += 10;
        }

        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(pos, 4), nodes.Length);
        pos += 4;

        foreach (BundleDirectoryNode node in nodes)
        {
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(pos, 8), node.Offset);
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(pos + 8, 8), node.Size);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos + 16, 4), node.Flags);
            pos += 20;

            int written = Encoding.UTF8.GetBytes(node.Path, buffer.AsSpan(pos));
            pos += written;
            buffer[pos++] = 0;
        }

        return buffer;
    }

    private static byte[] BuildStandardHeader(
        string unityRevision,
        int blocksInfoSize,
        long totalUncompressedSize
    )
    {
        ReadOnlySpan<byte> unityVersion = "5.x.x\0"u8;
        int revBytes = Encoding.UTF8.GetByteCount(unityRevision) + 1;
        int headerLength = 8 + 4 + unityVersion.Length + revBytes + 20;
        long totalSize = headerLength + blocksInfoSize + totalUncompressedSize;

        var buffer = new byte[headerLength];
        int pos = 0;

        UnityFsSignature.CopyTo(buffer.AsSpan(pos));
        pos += UnityFsSignature.Length;

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos, 4), 6u);
        pos += 4;

        unityVersion.CopyTo(buffer.AsSpan(pos));
        pos += unityVersion.Length;

        pos += Encoding.UTF8.GetBytes(unityRevision, buffer.AsSpan(pos));
        buffer[pos++] = 0;

        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(pos, 8), totalSize);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos + 8, 4), (uint)blocksInfoSize);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos + 12, 4), (uint)blocksInfoSize);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos + 16, 4), StandardUnityFsFlags);

        return buffer;
    }

    private static ushort Concat(byte high, byte low) => (ushort)(((ushort)high << 8) | low);
    private static uint Concat(ushort high, ushort low) => ((uint)high << 16) | low;
    private static ulong Concat(uint high, uint low) => ((ulong)high << 32) | low;
    private static ushort RotateLeft16(ushort value, int count) =>
        (ushort)((value << count) | (value >> (16 - count)));
}
