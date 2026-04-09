namespace BydTools.VFS.CriUsm;

/// <summary>
/// Describes the size characteristics of a block type in an MPEG stream.
/// </summary>
public sealed class BlockSizeInfo
{
    /// <summary>How the block size is determined.</summary>
    public PacketSizeType SizeType { get; }

    /// <summary>The static size value or size field length.</summary>
    public int StaticSize { get; }

    /// <summary>Creates a new BlockSizeInfo instance.</summary>
    public BlockSizeInfo(PacketSizeType sizeType, int staticSize)
    {
        SizeType = sizeType;
        StaticSize = staticSize;
    }
}
