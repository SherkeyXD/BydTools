namespace BeyondTools.VFS;

/// <summary>
/// Represents file information for a virtual file within a VFS chunk.
/// Each FVFBlockFileInfo describes a file's location and encryption status within a CHK file.
/// </summary>
public struct FVFBlockFileInfo
{
    /// <summary>
    /// Virtual file path (e.g., "Data/Bundles/Windows/manifest.hgmmap").
    /// </summary>
    public string fileName;

    /// <summary>
    /// Hash of the file name for quick lookup.
    /// </summary>
    public long fileNameHash;

    /// <summary>
    /// MD5 hash identifying which chunk this file belongs to.
    /// Should match the parent chunk's md5Name.
    /// </summary>
    public UInt128 fileChunkMD5Name;

    /// <summary>
    /// MD5 hash of the file data for integrity verification.
    /// </summary>
    public UInt128 fileDataMD5;

    /// <summary>
    /// Byte offset of this file within the chunk (CHK file).
    /// </summary>
    public long offset;

    /// <summary>
    /// Length of the file data in bytes.
    /// </summary>
    public long len;

    /// <summary>
    /// Block type of this file.
    /// </summary>
    public EVFSBlockType blockType;

    /// <summary>
    /// Indicates whether this file is encrypted with outer-layer encryption.
    /// If true, the file needs to be decrypted using ChaCha20 with ivSeed.
    /// Note: This does not indicate whether the file content itself is encrypted
    /// (e.g., .ab files may have their own encryption).
    /// </summary>
    public bool bUseEncrypt;

    /// <summary>
    /// IV seed for ChaCha20 decryption when bUseEncrypt is true.
    /// The nonce is constructed as: [version (4 bytes LE)] + [ivSeed (8 bytes LE)]
    /// </summary>
    public long ivSeed;

    /// <summary>
    /// Indicates if this is a direct file reference.
    /// </summary>
    public bool bIsDirect;

    /// <summary>
    /// Loader position type for this file.
    /// </summary>
    public EVFSLoaderPosType loaderPosType;
}
