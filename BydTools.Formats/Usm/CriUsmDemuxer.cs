using System.Buffers.Binary;

namespace BydTools.Formats.Usm;

public readonly record struct UsmStream(string Extension, byte[] Data);

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

    public static UsmStream[] Demux(ReadOnlySpan<byte> usm)
    {
        int start = usm.IndexOf(Crid);
        if (start < 0)
            return [];

        var order = new List<uint>();
        var streams = new Dictionary<uint, MemoryStream>();
        int pos = start;
        try
        {
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
                if (isAudio || isVideo)
                {
                    byte streamId = isAudio && pos + 0xC < usm.Length ? usm[pos + 0xC] : (byte)0;
                    uint blockId = BinaryPrimitives.ReadUInt32LittleEndian(sig);
                    uint key = streamId | blockId;
                    if (!streams.TryGetValue(key, out var ms))
                    {
                        ms = new MemoryStream();
                        streams[key] = ms;
                        order.Add(key);
                    }

                    int headerSkip = BinaryPrimitives.ReadUInt16BigEndian(usm.Slice(pos + 8, 2));
                    int footerSkip = BinaryPrimitives.ReadUInt16BigEndian(usm.Slice(pos + 0xA, 2));
                    int cut = (int)blockSize - headerSkip - footerSkip;
                    int dataStart = pos + 8 + headerSkip;
                    if (cut > 0 && dataStart >= 0 && dataStart <= usm.Length - cut)
                        ms.Write(usm.Slice(dataStart, cut));
                }

                pos += 8 + (int)blockSize;
            }

            var output = new List<UsmStream>(order.Count);
            foreach (uint key in order)
            {
                byte[] elementary = streams[key].ToArray();
                var span = elementary.AsSpan();
                int headerEnd = span.IndexOf(HeaderEnd);
                int metadataEnd = span.IndexOf(MetadataEnd);
                int dataStart;
                if (headerEnd >= 0 || metadataEnd >= 0)
                    dataStart = Math.Max(headerEnd, metadataEnd) + MetadataEnd.Length;
                else
                    dataStart = 0;

                if ((uint)dataStart > (uint)span.Length)
                    continue;

                int contentEnd = span[dataStart..].IndexOf(ContentsEnd);
                int dataLength = contentEnd >= 0 ? contentEnd : span.Length - dataStart;
                if (dataLength <= 0)
                    continue;

                var payload = span.Slice(dataStart, dataLength).ToArray();
                output.Add(new UsmStream(DetectExtension(key, payload), payload));
            }

            return output.ToArray();
        }
        finally
        {
            foreach (var ms in streams.Values)
                ms.Dispose();
        }
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
