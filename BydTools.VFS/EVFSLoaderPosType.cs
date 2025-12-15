namespace BydTools.VFS;

/// <summary>
/// Specifies the loader position type for VFS assets.
/// Determines where and how assets are loaded from storage.
/// </summary>
public enum EVFSLoaderPosType : byte
{
    /// <summary>
    /// No specific loader position.
    /// </summary>
    None = 0,

    /// <summary>
    /// Asset loaded from persistent data path.
    /// </summary>
    PersistAsset = 1,

    /// <summary>
    /// Asset loaded from streaming assets path.
    /// </summary>
    StreamAsset = 2,

    /// <summary>
    /// Asset loaded from VFS.
    /// </summary>
    VFS = 10,

    /// <summary>
    /// Asset loaded from VFS with persistent asset fallback.
    /// </summary>
    VFS_PersistAsset = 11,

    /// <summary>
    /// Asset loaded from VFS with streaming asset fallback.
    /// </summary>
    VFS_StreamAsset = 12,

    /// <summary>
    /// Asset loaded from VFS build.
    /// </summary>
    VFS_Build = 13,
}
