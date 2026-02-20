namespace BydTools.Wwise;

/// <summary>
/// Abstraction for WEM audio file conversion via vgmstream-cli.
/// </summary>
public interface IWemConverter
{
    /// <summary>
    /// Converts a WEM file to WAV via vgmstream-cli.
    /// </summary>
    /// <param name="wemPath">Path to the source WEM file.</param>
    /// <param name="outputPath">Path to the output WAV file.</param>
    void Convert(string wemPath, string outputPath);
}
