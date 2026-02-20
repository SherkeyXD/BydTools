namespace BydTools.Wwise;

/// <summary>
/// Abstraction for WEM audio file conversion (WEM → OGG).
/// </summary>
public interface IWemConverter
{
    /// <summary>
    /// Converts a WEM file to OGG format.
    /// </summary>
    /// <param name="wemPath">Path to the source WEM file.</param>
    /// <returns>Path to the generated OGG file.</returns>
    string ConvertWem(string wemPath);

    /// <summary>
    /// Applies revorb granule-position fix to an OGG file.
    /// </summary>
    /// <param name="inputPath">Source OGG file.</param>
    /// <param name="outputPath">Destination OGG file (may be the same as input).</param>
    void RevorbOgg(string inputPath, string? outputPath = null);
}
