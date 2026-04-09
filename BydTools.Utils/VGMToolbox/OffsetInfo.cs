namespace BydTools.Utils.VGMToolbox;

/// <summary>
/// Describes an offset location, size, and byte order for data extraction.
/// </summary>
public class OffsetInfo
{
    /// <summary>Offset value as a string (can be hex "0x..." or decimal).</summary>
    public string OffsetValue { get; set; } = string.Empty;

    /// <summary>Size of the data to read (in bytes).</summary>
    public string Size { get; set; } = string.Empty;

    /// <summary>Byte order (BigEndian or LittleEndian).</summary>
    public string ByteOrder { get; set; } = string.Empty;
}
