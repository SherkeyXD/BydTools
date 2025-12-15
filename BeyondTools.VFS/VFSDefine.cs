namespace BeyondTools.VFS;

/// <summary>
/// Constants used for VFS (Virtual File System) operations.
/// Based on reverse engineering of Arknights: Endfield CBT3.
/// </summary>
public static class VFSDefine
{
    /// <summary>
    /// ChaCha20 encryption key in Base64 format.
    /// Used for both BLC file decryption and CHK file outer-layer decryption.
    /// Key bytes: E9 5B 31 7A C4 F8 28 56 9D 23 A8 6B F2 71 DC B5 3E 84 6F A7 5C 92 4D 67 1D BA 8E 38 F4 CA 52 E1
    /// </summary>
    public const string CHACHA_KEY = "6VsxesT4KFadI6hr8nHctT6Eb6dckk1nHbqOOPTKUuE=";

    /// <summary>
    /// VFS directory name within StreamingAssets.
    /// </summary>
    public const string VFS_DIR = "VFS";

    /// <summary>
    /// VFS protocol version (must match the version in BLC files).
    /// </summary>
    public const int VFS_PROTO_VERSION = 3;

    /// <summary>
    /// Minimum size of a valid VFB (VFS Block) file header.
    /// </summary>
    public const int VFS_VFB_HEAD_LEN = 16;

    /// <summary>
    /// Size of the block header used as nonce for ChaCha20 encryption.
    /// For BLC: First 12 bytes are used as nonce.
    /// For CHK outer-layer encryption: 4 bytes version + 8 bytes ivSeed = 12 bytes nonce.
    /// </summary>
    public const int BLOCK_HEAD_LEN = 12;

    /// <summary>
    /// ChaCha20 key length in bytes (256 bits).
    /// </summary>
    public const int KEY_LEN = 32;
}
