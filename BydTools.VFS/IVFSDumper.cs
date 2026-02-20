namespace BydTools.VFS;

/// <summary>
/// Abstraction for VFS block extraction operations.
/// </summary>
public interface IVFSDumper
{
    /// <summary>
    /// Dumps files from a VFS block type to the specified output directory.
    /// </summary>
    void DumpAssetByType(string streamingAssetsPath, EVFSBlockType dumpAssetType, string outputDir);

    /// <summary>
    /// Scans all subdirectories under the VFS path and prints block declaration info.
    /// </summary>
    void DebugScanBlocks(string streamingAssetsPath);
}
