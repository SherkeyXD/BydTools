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

    /// <summary>
    /// Converts WEM data from memory to WAV.
    /// Implementations that support in-process decoding (e.g. libvgmstream DLL)
    /// can avoid temp-file I/O entirely.
    /// </summary>
    /// <param name="wemData">Raw WEM file bytes.</param>
    /// <param name="wemName">Logical filename (must end with .wem) for format detection.</param>
    /// <param name="outputPath">Path to the output WAV file.</param>
    void Convert(byte[] wemData, string wemName, string outputPath);
}
