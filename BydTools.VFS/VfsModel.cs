using BydTools.Core.IO;

namespace BydTools.VFS;

public sealed record VfsFile(
    string Name,
    long NameHash,
    UInt128 ChunkMd5,
    UInt128 DataMd5,
    long Offset,
    long Length,
    EVFSBlockType BlockType,
    bool Encrypted,
    long IvSeed,
    EVFSFileTag FileTag
);

public sealed record VfsChunk(
    UInt128 Md5Name,
    UInt128 ContentMd5,
    long Length,
    EVFSBlockType BlockType,
    EVFSFileTag FileTag,
    IReadOnlyList<VfsFile> Files
)
{
    public string FileName => Md5Name.ToHexLittleEndian() + ".chk";
}

public sealed record VfsBlockInfo(
    int NonceVersion,
    int CodeVersion,
    int Version,
    string GroupCfgName,
    string GroupCfgHashName,
    int GroupFileInfoNum,
    long GroupChunksLength,
    EVFSBlockType BlockType,
    IReadOnlyList<VfsChunk> Chunks
);
