namespace BydTools.VFS.PostProcessors;

/// <summary>
/// Post-processes extracted VFS file data before writing to disk.
/// Implementations handle format-specific decryption/conversion (e.g. SparkBuffer → JSON, Lua decryption).
/// </summary>
public interface IPostProcessor
{
    /// <summary>
    /// Attempts to post-process the file data.
    /// </summary>
    /// <param name="data">Raw file bytes extracted from a VFS chunk.</param>
    /// <param name="outputPath">Target output path (may be changed by the processor).</param>
    /// <returns><c>true</c> if the file was handled and written; <c>false</c> to fall back to raw write.</returns>
    bool TryProcess(byte[] data, string outputPath);
}
