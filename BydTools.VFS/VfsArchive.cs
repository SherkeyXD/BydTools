using BydTools.Core;
using BydTools.Core.Crypto;

namespace BydTools.VFS;

public sealed class VfsBlock
{
    private readonly byte[] _key;

    internal VfsBlock(string directoryPath, VfsBlockInfo info, BlockDescriptor? descriptor, byte[] key)
    {
        DirectoryPath = directoryPath;
        Info = info;
        Descriptor = descriptor;
        _key = key;
    }

    public string DirectoryPath { get; }
    public VfsBlockInfo Info { get; }
    public BlockDescriptor? Descriptor { get; }

    public string DisplayName =>
        Descriptor?.Type.ToString()
        ?? (Enum.IsDefined(Info.BlockType) ? Info.BlockType.ToString() : Info.GroupCfgName);

    public string? ResolveChunkPath(VfsChunk chunk)
    {
        string path = Path.Combine(DirectoryPath, chunk.FileName);
        return File.Exists(path) ? path : null;
    }

    public byte[] ReadFile(string chunkPath, VfsFile file)
    {
        if (file.Length < 0 || file.Length > int.MaxValue)
            throw new InvalidDataException($"File '{file.Name}' has an invalid length ({file.Length}).");

        int length = (int)file.Length;
        byte[] data = new byte[length];
        using var handle = File.OpenHandle(
            chunkPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        int got = 0;
        while (got < length)
        {
            int n = RandomAccess.Read(handle, data.AsSpan(got), file.Offset + got);
            if (n == 0)
            {
                throw new EndOfStreamException(
                    $"Chunk ended before '{file.Name}' was fully read ({got}/{length})."
                );
            }

            got += n;
        }

        if (!file.Encrypted)
            return data;

        Span<byte> nonce = stackalloc byte[VFSDefine.NonceLength];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(nonce, VFSDefine.ProtocolVersion);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], file.IvSeed);
        using var chacha = new CSChaCha20(_key, nonce, 1);
        return chacha.DecryptBytes(data);
    }
}

/// <summary>
/// A game VFS directory. Blocks are discovered by decrypting every .blc,
/// so a catalog entry is not required to dump a block the client just added.
/// </summary>
public sealed class VfsArchive
{
    private VfsArchive(string root, IReadOnlyList<VfsBlock> blocks)
    {
        Root = root;
        Blocks = blocks;
    }

    public string Root { get; }
    public IReadOnlyList<VfsBlock> Blocks { get; }

    public static VfsArchive Open(string vfsDirectory, byte[] key, ILogger logger)
    {
        if (!Directory.Exists(vfsDirectory))
            throw new DirectoryNotFoundException($"VFS directory not found: {vfsDirectory}");

        var blocks = new List<VfsBlock>();
        Exception? firstError = null;
        foreach (var dir in Directory.GetDirectories(vfsDirectory).OrderBy(static d => d, StringComparer.Ordinal))
        {
            string dirName = Path.GetFileName(dir);
            string blcPath = Path.Combine(dir, dirName + ".blc");
            if (!File.Exists(blcPath))
            {
                logger.Verbose("  [{0}] No BLC file found, skipping.", dirName);
                continue;
            }

            try
            {
                var info = BlcReader.Read(blcPath, key);
                if (info.NonceVersion != VFSDefine.ProtocolVersion)
                {
                    logger.Info(
                        "Warning: VFS version {0} does not match expected {1}.",
                        info.NonceVersion,
                        VFSDefine.ProtocolVersion
                    );
                }

                BlockRegistry.TryGet(info.BlockType, out var byType);
                var byName = BlockRegistry.All.FirstOrDefault(d =>
                    d.Type.ToString().Equals(info.GroupCfgName, StringComparison.OrdinalIgnoreCase)
                );
                var descriptor = byName ?? (byType is null ? null : byType);
                if (
                    descriptor != null
                    && !descriptor.DirHash.Equals(info.GroupCfgHashName, StringComparison.OrdinalIgnoreCase)
                )
                {
                    logger.Info(
                        "Warning: {0} directory hash {1} differs from the catalog hash {2}.",
                        info.GroupCfgName,
                        info.GroupCfgHashName,
                        descriptor.DirHash
                    );
                }

                blocks.Add(new VfsBlock(dir, info, descriptor, key));
            }
            catch (Exception ex)
            {
                firstError ??= ex;
                logger.Error("  [{0}] Failed to parse BLC: {1}", dirName, ex.Message);
            }
        }

        if (blocks.Count == 0 && firstError != null)
            throw firstError;

        return new VfsArchive(vfsDirectory, blocks);
    }

    public VfsBlock? Find(string nameOrType)
    {
        if (BlockRegistry.TryParse(nameOrType, out var descriptor))
        {
            string typeName = descriptor.Type.ToString();
            return Blocks.FirstOrDefault(b =>
                b.Info.BlockType == descriptor.Type
                || b.Info.GroupCfgName.Equals(typeName, StringComparison.OrdinalIgnoreCase)
            );
        }

        return Blocks.FirstOrDefault(b =>
            b.Info.GroupCfgName.Equals(nameOrType, StringComparison.OrdinalIgnoreCase)
        );
    }
}
