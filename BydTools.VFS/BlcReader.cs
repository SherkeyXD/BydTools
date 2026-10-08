using System.Buffers.Binary;
using BydTools.Core.Crypto;
using BydTools.Core.IO;

namespace BydTools.VFS;

/// <summary>
/// Decrypts and parses a BLC block declaration. The directory hash is checked
/// before chunk tables are allocated, so a wrong key fails before a garbage
/// count can request a huge array.
/// </summary>
public static class BlcReader
{
    private const int MaxNameBytes = 10_000;
    private const int MaxChunks = 1_000_000;
    private const int MaxFilesPerChunk = 5_000_000;

    public static VfsBlockInfo Read(string blcPath, byte[] key)
    {
        var file = File.ReadAllBytes(blcPath);
        var expectedHash = Path.GetFileNameWithoutExtension(blcPath);
        return Parse(file, key, expectedHash);
    }

    public static VfsBlockInfo Parse(ReadOnlySpan<byte> file, byte[] key, string expectedHash)
    {
        if (file.Length < VFSDefine.NonceLength)
            throw new InvalidDataException("BLC file is smaller than the nonce.");

        int nonceVersion = BinaryPrimitives.ReadInt32LittleEndian(file);
        byte[] nonce = file[..VFSDefine.NonceLength].ToArray();
        byte[] cipher = file[VFSDefine.NonceLength..].ToArray();

        byte[] plain;
        using (var chacha = new CSChaCha20(key, nonce, 1))
            plain = chacha.DecryptBytes(cipher);

        if (plain.Length != cipher.Length)
            throw new InvalidDataException("ChaCha20 output length does not match the BLC payload.");

        byte[] reconstructed = new byte[file.Length];
        nonce.CopyTo(reconstructed, 0);
        plain.CopyTo(reconstructed, VFSDefine.NonceLength);

        try
        {
            return ParseDecrypted(reconstructed, nonceVersion, expectedHash);
        }
        catch (InvalidDataException ex) when (!ex.Message.Contains("hash mismatch", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                ex.Message + " The ChaCha20 key may be incorrect or the file is corrupt.",
                ex
            );
        }
    }

    private static VfsBlockInfo ParseDecrypted(byte[] file, int nonceVersion, string expectedHash)
    {
        var reader = new SpanReader(file);
        reader.Seek(VFSDefine.NonceLength);

        int codeVersion = reader.ReadInt32();
        int version = codeVersion;
        if (codeVersion > 10)
            codeVersion = 3;
        else
            version = reader.ReadInt32();

        ushort nameLength = reader.ReadUInt16();
        if (nameLength > MaxNameBytes)
            throw new InvalidDataException($"BLC name length {nameLength} at offset {reader.Position} is not plausible.");
        string groupCfgName = reader.ReadUtf8(nameLength);

        uint hash = reader.ReadUInt32BigEndian();
        reader.Skip(4);
        string groupCfgHashName = hash.ToString("X8");
        if (!groupCfgHashName.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"BLC hash mismatch: parsed \"{groupCfgHashName}\", expected \"{expectedHash}\". "
                    + "The ChaCha20 key may be incorrect or outdated."
            );
        }

        int groupFileInfoNum = reader.ReadInt32();
        long groupChunksLength = reader.ReadInt64();
        var blockType = (EVFSBlockType)reader.ReadByte();
        int chunkCount = reader.ReadInt32();
        if (chunkCount < 0 || chunkCount > MaxChunks)
            throw new InvalidDataException($"BLC chunk count {chunkCount} is not plausible.");

        var chunks = new VfsChunk[chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            var md5Name = reader.ReadUInt128();
            var contentMd5 = reader.ReadUInt128();
            long length = reader.ReadInt64();
            var chunkType = (EVFSBlockType)reader.ReadByte();
            var chunkTag = EVFSFileTag.Base;
            if (codeVersion > 3)
                chunkTag = (EVFSFileTag)(byte)reader.ReadInt32();

            int fileCount = reader.ReadInt32();
            if (fileCount < 0 || fileCount > MaxFilesPerChunk)
                throw new InvalidDataException($"BLC file count {fileCount} is not plausible.");

            var files = new VfsFile[fileCount];
            for (int j = 0; j < fileCount; j++)
            {
                ushort fileNameLength = reader.ReadUInt16();
                if (fileNameLength > MaxNameBytes)
                    throw new InvalidDataException($"File name length {fileNameLength} is not plausible.");
                string fileName = reader.ReadUtf8(fileNameLength);
                long nameHash = reader.ReadInt64();
                var chunkMd5 = reader.ReadUInt128();
                var dataMd5 = reader.ReadUInt128();
                long offset = reader.ReadInt64();
                long len = reader.ReadInt64();
                var fileType = (EVFSBlockType)reader.ReadByte();
                bool encrypted = reader.ReadByte() != 0;
                long ivSeed = encrypted ? reader.ReadInt64() : 0;
                var fileTag = EVFSFileTag.Base;
                if (codeVersion > 3)
                    fileTag = (EVFSFileTag)(byte)reader.ReadInt32();

                files[j] = new VfsFile(
                    fileName,
                    nameHash,
                    chunkMd5,
                    dataMd5,
                    offset,
                    len,
                    fileType,
                    encrypted,
                    ivSeed,
                    fileTag
                );
            }

            chunks[i] = new VfsChunk(md5Name, contentMd5, length, chunkType, chunkTag, files);
        }

        return new VfsBlockInfo(
            nonceVersion,
            codeVersion,
            version,
            groupCfgName,
            groupCfgHashName,
            groupFileInfoNum,
            groupChunksLength,
            blockType,
            chunks
        );
    }
}
