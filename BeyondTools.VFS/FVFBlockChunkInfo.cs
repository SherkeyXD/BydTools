using System.Text.Json.Serialization;

namespace BeyondTools.VFS;

/// <summary>
/// Represents chunk information within a VFS block.
/// Each FVFBlockChunkInfo corresponds to a single CHK file.
/// </summary>
public struct FVFBlockChunkInfo
{
    /// <summary>
    /// File extension for chunk files.
    /// </summary>
    public const string FILE_EXTENSION = ".chk";

    /// <summary>
    /// MD5 hash used as the chunk filename (without extension).
    /// When converted to hex string, use big-endian format.
    /// </summary>
    public UInt128 md5Name;

    /// <summary>
    /// MD5 hash of the chunk content for integrity verification.
    /// </summary>
    public UInt128 contentMD5;

    /// <summary>
    /// Total length of this chunk in bytes.
    /// </summary>
    public long length;

    /// <summary>
    /// Block type of this chunk.
    /// </summary>
    public EVFSBlockType blockType;

    /// <summary>
    /// Array of file information within this chunk.
    /// Each file represents a virtual file stored in the CHK.
    /// </summary>
    [JsonIgnore]
    public FVFBlockFileInfo[] files;
}
