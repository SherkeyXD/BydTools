using System.Buffers.Binary;
using BydTools.Utils.Extensions;

namespace BydTools.VFS.CriUsm;

/// <summary>
/// Extracts embedded file names from CRI USM container headers.
/// When a Video asset in the VFS has an empty fileName, the real name
/// can sometimes be recovered from the CRID / CRIUSF_DIR_STREAM metadata.
/// Ported from BeyondToolsSRC's VFSDump.GetNameFromUsm.
/// </summary>
public static class UsmNameReader
{
    private const int CRID_SIGNATURE = 0x44495243; // "CRID" as little-endian int32
    private static readonly byte[] DIR_STREAM_TAG = "CRIUSF_DIR_STREAM"u8.ToArray();

    /// <summary>
    /// Attempts to extract a .usm filename from the CRID header block.
    /// </summary>
    /// <returns>The filename (without leading root), or <c>null</c> if not found.</returns>
    public static string? TryGetName(byte[] usmData)
    {
        if (usmData.Length < 12)
            return null;

        var span = usmData.AsSpan();

        int cridSign = BinaryPrimitives.ReadInt32LittleEndian(span);
        if (cridSign != CRID_SIGNATURE)
            return null;

        int blockSize = BinaryPrimitives.ReadInt32BigEndian(span[4..]);
        short payloadOffset = BinaryPrimitives.ReadInt16BigEndian(span[8..]);
        short paddingSize = BinaryPrimitives.ReadInt16BigEndian(span[10..]);

        int dataLen = blockSize - paddingSize - payloadOffset;
        if (dataLen <= 0 || 12 + dataLen > usmData.Length)
            return null;

        var buff = span.Slice(12, dataLen);

        int dirStreamIndex = buff.IndexOf(DIR_STREAM_TAG);
        if (dirStreamIndex < 0)
            return null;

        int offset = 18;
        while (dirStreamIndex + offset < buff.Length)
        {
            var str = buff[(dirStreamIndex + offset)..].ReadStringToNull();
            if (str.EndsWith(".usm", StringComparison.OrdinalIgnoreCase))
            {
                var root = Path.GetPathRoot(str);
                return string.IsNullOrEmpty(root) ? str : str[root.Length..];
            }
            offset += str.Length + 1;
        }

        return null;
    }
}
