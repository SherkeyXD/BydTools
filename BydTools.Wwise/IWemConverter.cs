namespace BydTools.Wwise;

/// <summary>
/// Abstraction for converting Wwise WEM audio files to WAV.
/// </summary>
public interface IWemConverter
{
    /// <summary>
    /// Converts a WEM file to WAV.
    /// </summary>
    /// <param name="wemPath">Path to the source WEM file.</param>
    /// <param name="outputPath">Path to the output WAV file.</param>
    void Convert(string wemPath, string outputPath);
}
