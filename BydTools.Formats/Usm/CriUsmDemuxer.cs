using System.Buffers;
using System.Buffers.Binary;

namespace BydTools.Formats.Usm;

/// <summary>
/// Demultiplexes a CRI USM container into elementary video and audio streams.
/// Names are not assigned here; the caller numbers streams of the same extension
/// in the order they first appear, and overwrites on a later run.
/// </summary>
public static class CriUsmDemuxer
{
    private static readonly byte[] Crid = "CRID"u8.ToArray();
    private static readonly byte[] Sfv = "@SFV"u8.ToArray();
    private static readonly byte[] Sfa = "@SFA"u8.ToArray();
    private static readonly byte[] Sbt = "@SBT"u8.ToArray();
    private static readonly byte[] Cue = "@CUE"u8.ToArray();
    private static readonly byte[] Alp = "@ALP"u8.ToArray();
    private static readonly byte[] Aixf = "AIXF"u8.ToArray();
    private static readonly byte[] Hca = [0x48, 0x43, 0x41, 0x00];

    private static readonly byte[] HeaderEnd =
    [
        0x23, 0x48, 0x45, 0x41, 0x44, 0x45, 0x52, 0x20, 0x45, 0x4E, 0x44, 0x20, 0x20, 0x20, 0x20,
        0x20, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D,
        0x3D, 0x00,
    ];

    private static readonly byte[] MetadataEnd =
    [
        0x23, 0x4D, 0x45, 0x54, 0x41, 0x44, 0x41, 0x54, 0x41, 0x20, 0x45, 0x4E, 0x44, 0x20, 0x20,
        0x20, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D,
        0x3D, 0x00,
    ];

    private static readonly byte[] ContentsEnd =
    [
        0x23, 0x43, 0x4F, 0x4E, 0x54, 0x45, 0x4E, 0x54, 0x53, 0x20, 0x45, 0x4E, 0x44, 0x20, 0x20,
        0x20, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D,
        0x3D, 0x00,
    ];

    public delegate void UsmStreamHandler(string extension, ReadOnlySpan<byte> payload);

    public static int Demux(ReadOnlySpan<byte> usm, UsmStreamHandler onStream)
    {
        int start = usm.IndexOf(Crid);
        if (start < 0)
            return 0;

        var order = new List<uint>();
        var sizes = new Dictionary<uint, long>();
        int pos = start;
        while (pos + 8 <= usm.Length)
        {
            var sig = usm.Slice(pos, 4);
            if (!IsKnown(sig))
                break;

            uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(usm.Slice(pos + 4, 4));
            if (blockSize > int.MaxValue - 8 || pos > usm.Length - 8 - (int)blockSize)
                break;

            bool isAudio = sig.SequenceEqual(Sfa);
            bool isVideo = sig.SequenceEqual(Sfv);
            if ((isAudio || isVideo) && blockSize >= 4)
            {
                byte streamId = isAudio && pos + 0xC < usm.Length ? usm[pos + 0xC] : (byte)0;
                uint blockId = BinaryPrimitives.ReadUInt32LittleEndian(sig);
                uint key = streamId | blockId;
                if (!sizes.TryGetValue(key, out long total))
                    order.Add(key);

                int headerSkip = BinaryPrimitives.ReadUInt16BigEndian(usm.Slice(pos + 8, 2));
                int footerSkip = BinaryPrimitives.ReadUInt16BigEndian(usm.Slice(pos + 0xA, 2));
                int cut = (int)blockSize - headerSkip - footerSkip;
                int dataStart = pos + 8 + headerSkip;
                if (cut > 0 && dataStart >= 0 && dataStart <= usm.Length - cut)
                    total += cut;

                sizes[key] = total;
            }

            pos += 8 + (int)blockSize;
        }

        int endPos = pos;
        int emitted = 0;
        foreach (uint key in order)
        {
            long totalSize = sizes[key];
            if (totalSize <= 0 || totalSize > int.MaxValue)
                continue;

            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)totalSize);
            try
            {
                int written = 0;
                pos = start;
                while (pos < endPos)
                {
                    var sig = usm.Slice(pos, 4);
                    uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(usm.Slice(pos + 4, 4));
                    bool isAudio = sig.SequenceEqual(Sfa);
                    bool isVideo = sig.SequenceEqual(Sfv);
                    if ((isAudio || isVideo) && blockSize >= 4)
                    {
                        byte streamId = isAudio && pos + 0xC < usm.Length ? usm[pos + 0xC] : (byte)0;
                        uint blockId = BinaryPrimitives.ReadUInt32LittleEndian(sig);
                        if ((streamId | blockId) == key)
                        {
                            int headerSkip = BinaryPrimitives.ReadUInt16BigEndian(usm.Slice(pos + 8, 2));
                            int footerSkip = BinaryPrimitives.ReadUInt16BigEndian(usm.Slice(pos + 0xA, 2));
                            int cut = (int)blockSize - headerSkip - footerSkip;
                            int dataStart = pos + 8 + headerSkip;
                            if (cut > 0 && dataStart >= 0 && dataStart <= usm.Length - cut)
                            {
                                usm.Slice(dataStart, cut).CopyTo(buffer.AsSpan(written));
                                written += cut;
                            }
                        }
                    }

                    pos += 8 + (int)blockSize;
                }

                ReadOnlySpan<byte> span = buffer.AsSpan(0, written);
                int headerEnd = span.IndexOf(HeaderEnd);
                int metadataEnd = span.IndexOf(MetadataEnd);
                int streamDataStart = headerEnd >= 0 || metadataEnd >= 0
                    ? Math.Max(headerEnd, metadataEnd) + MetadataEnd.Length
                    : 0;

                if ((uint)streamDataStart > (uint)span.Length)
                    continue;

                int contentEnd = span[streamDataStart..].IndexOf(ContentsEnd);
                int dataLength = contentEnd >= 0 ? contentEnd : span.Length - streamDataStart;
                if (dataLength <= 0)
                    continue;

                ReadOnlySpan<byte> payload = span.Slice(streamDataStart, dataLength);
                onStream(DetectExtension(key, payload), payload);
                emitted++;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return emitted;
    }

    private static bool IsKnown(ReadOnlySpan<byte> sig) =>
        sig.SequenceEqual(Crid)
        || sig.SequenceEqual(Sfv)
        || sig.SequenceEqual(Sfa)
        || sig.SequenceEqual(Sbt)
        || sig.SequenceEqual(Cue)
        || sig.SequenceEqual(Alp);

    private static string DetectExtension(uint streamKey, ReadOnlySpan<byte> payload)
    {
        Span<byte> block = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(block, streamKey & 0xFFFFFFF0);
        if (!block.SequenceEqual(Sfa))
            return ".m2v";
        if (payload.Length >= 4 && payload[..4].SequenceEqual(Aixf))
            return ".aix";
        if (payload.Length >= 1 && payload[0] == 0x80)
            return ".adx";
        if (payload.Length >= 4 && payload[..4].SequenceEqual(Hca))
            return ".hca";
        return ".bin";
    }
}
