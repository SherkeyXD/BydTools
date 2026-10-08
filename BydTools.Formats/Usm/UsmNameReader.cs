using System.Buffers.Binary;

namespace BydTools.Formats.Usm;

/// <summary>
/// Recovers a file name from a CRI USM header when the VFS entry name is empty.
/// </summary>
public static class UsmNameReader
{
    private const int CridSignature = 0x44495243;
    private static readonly byte[] DirStreamTag = "CRIUSF_DIR_STREAM"u8.ToArray();

    public static string? TryGetName(ReadOnlySpan<byte> usm)
    {
        if (usm.Length < 12)
            return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(usm) != CridSignature)
            return null;

        int blockSize = BinaryPrimitives.ReadInt32BigEndian(usm[4..]);
        short payloadOffset = BinaryPrimitives.ReadInt16BigEndian(usm[8..]);
        short paddingSize = BinaryPrimitives.ReadInt16BigEndian(usm[10..]);
        int dataLength = blockSize - paddingSize - payloadOffset;
        if (dataLength <= 0 || 12 + dataLength > usm.Length)
            return null;

        var buff = usm.Slice(12, dataLength);
        int dirStreamIndex = buff.IndexOf(DirStreamTag);
        if (dirStreamIndex < 0)
            return null;

        int offset = 18;
        while (dirStreamIndex + offset < buff.Length)
        {
            var tail = buff[(dirStreamIndex + offset)..];
            int end = tail.IndexOf((byte)0);
            if (end < 0)
                end = tail.Length;
            string str = System.Text.Encoding.UTF8.GetString(tail[..end]);
            if (str.EndsWith(".usm", StringComparison.OrdinalIgnoreCase))
            {
                string? root = Path.GetPathRoot(str);
                return string.IsNullOrEmpty(root) ? str : str[root.Length..];
            }

            offset += str.Length + 1;
        }

        return null;
    }
}
