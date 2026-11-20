using System.Buffers.Binary;

namespace BydTools.Formats.Usm;

/// <summary>
/// Recovers a file name from a CRI USM header when the VFS entry name is empty.
/// </summary>
public static class UsmNameReader
{
    private const int CridSignature = 0x44495243;
    private static readonly byte[] DirStreamTag = "CRIUSF_DIR_STREAM"u8.ToArray();

    public static bool TryGetHeaderLength(ReadOnlySpan<byte> header12, out int totalBytes)
    {
        totalBytes = 0;
        if (header12.Length < 12)
            return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(header12) != CridSignature)
            return false;

        int blockSize = BinaryPrimitives.ReadInt32BigEndian(header12[4..]);
        ushort payloadOffset = BinaryPrimitives.ReadUInt16BigEndian(header12[8..]);
        ushort paddingSize = BinaryPrimitives.ReadUInt16BigEndian(header12[10..]);
        long dataLength = (long)blockSize - paddingSize - payloadOffset;
        if (dataLength <= 0 || dataLength > int.MaxValue - 12)
            return false;

        totalBytes = 12 + (int)dataLength;
        return true;
    }

    public static string? TryGetName(ReadOnlySpan<byte> usm)
    {
        if (!TryGetHeaderLength(usm, out int totalBytes) || totalBytes > usm.Length)
            return null;

        var buff = usm.Slice(12, totalBytes - 12);
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
