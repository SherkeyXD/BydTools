namespace BydTools.Utils.VGMToolbox;

/// <summary>
/// Constants used by VGMToolbox file parsing utilities.
/// </summary>
public static class VgmtConstants
{
    /// <summary>Default chunk size for file read operations (71680 bytes).</summary>
    public const int FileReadChunkSize = 71680;

    /// <summary>Big-endian byte order identifier.</summary>
    public const string BigEndian = "BigEndian";

    /// <summary>Little-endian byte order identifier.</summary>
    public const string LittleEndian = "LittleEndian";
}
