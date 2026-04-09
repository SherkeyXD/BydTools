namespace BydTools.VFS.CriUsm;

/// <summary>
/// Defines how packet sizes are determined in MPEG streams.
/// </summary>
public enum PacketSizeType
{
    /// <summary>Fixed static size.</summary>
    Static,

    /// <summary>Size specified in bytes following the header.</summary>
    SizeBytes,

    /// <summary>End of file marker.</summary>
    Eof
}
