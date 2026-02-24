using BydTools.Utils.Crypto;
using BydTools.Utils.Extensions;

namespace BydTools.VFS;

/// <summary>
/// Shared low-level helpers for reading VFS blocks, chunks, and files.
/// Both VFSDumper and external consumers (e.g. PckCommand) use these
/// instead of duplicating the decrypt-and-parse logic.
/// </summary>
public static class VfsReader
{
    /// <summary>
    /// Decrypts and parses a BLC file for the given block type.
    /// Validates the parsed hash against the directory name.
    /// </summary>
    public static VFBlockMainInfo ReadBlockInfo(string vfsPath, EVFSBlockType blockType, byte[] key)
    {
        if (!VFSDumper.BlockHashMap.TryGetValue(blockType, out var hashName))
            throw new InvalidOperationException($"No hash mapping for block type {blockType}");

        var blcPath = Path.Combine(vfsPath, hashName, hashName + ".blc");
        if (!File.Exists(blcPath))
            throw new FileNotFoundException($"BLC file not found for {blockType}", blcPath);

        return DecryptBlc(blcPath, key);
    }

    /// <summary>
    /// Decrypts and parses a BLC file by its direct path (for discovery/debug scenarios
    /// where the block type is not known in advance).
    /// </summary>
    public static VFBlockMainInfo DecryptBlc(string blcFilePath, byte[] key)
    {
        var blockFile = File.ReadAllBytes(blcFilePath);

        byte[] nonce = new byte[VFSDefine.BLOCK_HEAD_LEN];
        Buffer.BlockCopy(blockFile, 0, nonce, 0, nonce.Length);

        using var chacha = new CSChaCha20(key, nonce, 1);
        var decrypted = chacha.DecryptBytes(blockFile[VFSDefine.BLOCK_HEAD_LEN..]);
        Buffer.BlockCopy(decrypted, 0, blockFile, VFSDefine.BLOCK_HEAD_LEN, decrypted.Length);

        var info = new VFBlockMainInfo(blockFile, 0);

        var expectedHash = Path.GetFileNameWithoutExtension(blcFilePath);
        if (!string.Equals(info.groupCfgHashName, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"BLC hash mismatch: parsed \"{info.groupCfgHashName}\", expected \"{expectedHash}\". "
                    + "The ChaCha20 key may be incorrect or outdated."
            );
        }

        return info;
    }

    /// <summary>
    /// Resolves the filesystem path for a chunk file, or null if it doesn't exist.
    /// </summary>
    public static string? ResolveChunkPath(
        string vfsPath,
        EVFSBlockType blockType,
        in FVFBlockChunkInfo chunk
    )
    {
        if (!VFSDumper.BlockHashMap.TryGetValue(blockType, out var hashName))
            return null;

        var chunkFileName =
            chunk.md5Name.ToHexStringLittleEndian() + FVFBlockChunkInfo.FILE_EXTENSION;
        var path = Path.Combine(vfsPath, hashName, chunkFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Reads (and optionally decrypts) a single file's data from an open chunk stream.
    /// </summary>
    public static byte[] ReadFileData(
        FileStream chunkFs,
        in FVFBlockFileInfo file,
        int version,
        byte[] key
    )
    {
        if (file.bUseEncrypt)
        {
            byte[] nonce = new byte[VFSDefine.BLOCK_HEAD_LEN];
            Buffer.BlockCopy(BitConverter.GetBytes(version), 0, nonce, 0, sizeof(int));
            Buffer.BlockCopy(
                BitConverter.GetBytes(file.ivSeed),
                0,
                nonce,
                sizeof(int),
                sizeof(long)
            );

            using var chacha = new CSChaCha20(key, nonce, 1);
            var encrypted = new byte[file.len];
            chunkFs.ReadExactly(encrypted);
            return chacha.DecryptBytes(encrypted);
        }

        var data = new byte[file.len];
        chunkFs.ReadExactly(data);
        return data;
    }
}
