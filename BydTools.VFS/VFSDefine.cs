namespace BydTools.VFS;

/// <summary>
/// Constants used for VFS (Virtual File System) operations.
/// Based on reverse engineering of Arknights: Endfield CBT3.
/// </summary>
public static class VFSDefine
{
    /// <summary>
    /// ChaCha20 encryption key in Base64 format (Android / default).
    /// </summary>
    public const string CHACHA_KEY_ANDROID_BASE64 = "6VsxesT4KFadI6hr8nHctT6Eb6dckk1nHbqOOPTKUuE=";

    /// <summary>
    /// ChaCha20 encryption key in Base64 format (PC).
    /// </summary>
    public const string CHACHA_KEY_PC_BASE64 = "eU1cu+MYQiaYdVherRzV86pv/N/lIU/9gIk+5n5Vj4Y=";

    /// <summary>
    /// Pre-decoded default (PC) ChaCha20 key bytes.
    /// </summary>
    public static readonly byte[] DefaultChaChaKey = Convert.FromBase64String(CHACHA_KEY_PC_BASE64);

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
