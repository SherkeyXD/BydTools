using System.Buffers.Binary;

namespace BydTools.VFS.CriUsm;

/// <summary>
/// Demultiplexes CRI USM (Sofdec2) containers into separate video and audio streams.
/// Ported from VGMToolbox (CriUsmStream / MpegStream) with in-memory processing.
/// </summary>
public static class CriUsmDemuxer
{
    private static readonly byte[] CRID_SIG = "CRID"u8.ToArray();
    private static readonly byte[] SFV_SIG = "@SFV"u8.ToArray();
    private static readonly byte[] SFA_SIG = "@SFA"u8.ToArray();
    private static readonly byte[] SBT_SIG = "@SBT"u8.ToArray();
    private static readonly byte[] CUE_SIG = "@CUE"u8.ToArray();
    private static readonly byte[] ALP_SIG = "@ALP"u8.ToArray();

    private static readonly byte[] HEADER_END_MARKER =
    [
        0x23,
        0x48,
        0x45,
        0x41,
        0x44,
        0x45,
        0x52,
        0x20,
        0x45,
        0x4E,
        0x44,
        0x20,
        0x20,
        0x20,
        0x20,
        0x20,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x00,
    ];

    private static readonly byte[] METADATA_END_MARKER =
    [
        0x23,
        0x4D,
        0x45,
        0x54,
        0x41,
        0x44,
        0x41,
        0x54,
        0x41,
        0x20,
        0x45,
        0x4E,
        0x44,
        0x20,
        0x20,
        0x20,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x00,
    ];

    private static readonly byte[] CONTENTS_END_MARKER =
    [
        0x23,
        0x43,
        0x4F,
        0x4E,
        0x54,
        0x45,
        0x4E,
        0x54,
        0x53,
        0x20,
        0x45,
        0x4E,
        0x44,
        0x20,
        0x20,
        0x20,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x3D,
        0x00,
    ];

    private static readonly byte[] AIXF_SIG = "AIXF"u8.ToArray();
    private static readonly byte[] HCA_SIG = [0x48, 0x43, 0x41, 0x00];

    private const int SEARCH_BUFFER_SIZE = 71680;

    /// <summary>
    /// Demultiplexes USM data into separate video (.m2v) and audio (.adx/.hca/.aix) files.
    /// </summary>
    /// <returns>Paths of all output files, or empty array on failure.</returns>
    public static string[] Demux(byte[] usmData, string outputBasePath)
    {
        using var stream = new MemoryStream(usmData, writable: false);
        return Demux(stream, outputBasePath);
    }

    /// <summary>
    /// Demultiplexes a USM stream into separate video (.m2v) and audio (.adx/.hca/.aix) files.
    /// </summary>
    public static string[] Demux(Stream stream, string outputBasePath)
    {
        long fileSize = stream.Length;
        long currentOffset = FindPattern(stream, 0, CRID_SIG);
        if (currentOffset < 0)
            return [];

        var streamWriters = new Dictionary<uint, MemoryStream>();
        Span<byte> blockId = stackalloc byte[4];
        Span<byte> sizeField = stackalloc byte[4];

        try
        {
            while (currentOffset < fileSize)
            {
                stream.Position = currentOffset;
                if (stream.Read(blockId) < 4)
                    break;

                uint blockIdVal = BitConverter.ToUInt32(blockId);

                if (!IsKnownPacket(blockId))
                    break;

                stream.Position = currentOffset + 4;
                if (stream.Read(sizeField) < 4)
                    break;
                uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(sizeField);

                bool isAudio = blockId.SequenceEqual(SFA_SIG);
                bool isVideo = blockId.SequenceEqual(SFV_SIG);

                if (isAudio || isVideo)
                {
                    byte streamId = isAudio ? ReadByte(stream, currentOffset + 0xC) : (byte)0;
                    uint streamKey = (uint)(streamId | blockIdVal);

                    if (!streamWriters.TryGetValue(streamKey, out var ms))
                    {
                        ms = new MemoryStream();
                        streamWriters[streamKey] = ms;
                    }

                    int headerSkip = ReadUInt16BE(stream, currentOffset + 8);
                    int footerSkip = ReadUInt16BE(stream, currentOffset + 0xA);
                    int cutSize = (int)(blockSize - headerSkip - footerSkip);

                    if (cutSize > 0)
                    {
                        long dataStart = currentOffset + 8 + headerSkip;
                        stream.Position = dataStart;
                        var payload = GC.AllocateUninitializedArray<byte>(cutSize);
                        stream.ReadExactly(payload);
                        ms.Write(payload);
                    }
                }

                currentOffset += 8 + blockSize;
            }

            return FinalizeStreams(streamWriters, outputBasePath);
        }
        finally
        {
            foreach (var ms in streamWriters.Values)
                ms.Dispose();
        }
    }

    private static string[] FinalizeStreams(
        Dictionary<uint, MemoryStream> streams,
        string outputBasePath
    )
    {
        var outputFiles = new List<string>(streams.Count);
        var outputDir = Path.GetDirectoryName(outputBasePath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        foreach (var (streamKey, ms) in streams)
        {
            ms.Position = 0;

            long headerEndOff = FindPattern(ms, 0, HEADER_END_MARKER);
            long metadataEndOff = FindPattern(ms, 0, METADATA_END_MARKER);

            long dataStart;
            if (headerEndOff >= 0 || metadataEndOff >= 0)
            {
                long furthest = Math.Max(headerEndOff, metadataEndOff);
                dataStart = furthest + METADATA_END_MARKER.Length;
            }
            else
            {
                dataStart = 0;
            }

            long contentEndOff = FindPattern(ms, dataStart, CONTENTS_END_MARKER);
            long dataLength =
                contentEndOff > dataStart ? contentEndOff - dataStart : ms.Length - dataStart;

            if (dataLength <= 0)
                continue;

            bool isAudio = IsAudioStreamKey(streamKey);
            string ext;
            if (isAudio)
            {
                byte[] sig = new byte[4];
                ms.Position = dataStart;
                ms.ReadExactly(sig);
                ext = DetectAudioExtension(sig);
            }
            else
            {
                ext = ".m2v";
            }

            string outputPath = GetUniqueOutputPath(outputBasePath, ext);

            using var fs = File.Create(outputPath);
            ms.Position = dataStart;
            var buffer = GC.AllocateUninitializedArray<byte>(64 * 1024);
            long remaining = dataLength;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = ms.Read(buffer, 0, toRead);
                if (read == 0)
                    break;
                fs.Write(buffer, 0, read);
                remaining -= read;
            }

            outputFiles.Add(outputPath);
        }

        return outputFiles.ToArray();
    }

    private static bool IsKnownPacket(ReadOnlySpan<byte> sig) =>
        sig.SequenceEqual(CRID_SIG)
        || sig.SequenceEqual(SFV_SIG)
        || sig.SequenceEqual(SFA_SIG)
        || sig.SequenceEqual(SBT_SIG)
        || sig.SequenceEqual(CUE_SIG)
        || sig.SequenceEqual(ALP_SIG);

    private static bool IsAudioStreamKey(uint streamKey)
    {
        Span<byte> blockBytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(blockBytes, streamKey & 0xFFFFFFF0);
        return blockBytes.SequenceEqual(SFA_SIG);
    }

    private static byte ReadByte(Stream stream, long offset)
    {
        stream.Position = offset;
        int b = stream.ReadByte();
        return b < 0 ? (byte)0 : (byte)b;
    }

    private static int ReadUInt16BE(Stream stream, long offset)
    {
        Span<byte> buf = stackalloc byte[2];
        stream.Position = offset;
        stream.ReadExactly(buf);
        return BinaryPrimitives.ReadUInt16BigEndian(buf);
    }

    private static string DetectAudioExtension(ReadOnlySpan<byte> signature)
    {
        if (signature.Length >= 4 && signature[..4].SequenceEqual(AIXF_SIG))
            return ".aix";
        if (signature.Length >= 1 && signature[0] == 0x80)
            return ".adx";
        if (signature.Length >= 4 && signature[..4].SequenceEqual(HCA_SIG))
            return ".hca";
        return ".bin";
    }

    private static string GetUniqueOutputPath(string basePath, string ext)
    {
        string path = basePath + ext;
        if (!File.Exists(path))
            return path;

        string dir = Path.GetDirectoryName(basePath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(basePath);
        int n = 1;
        do
        {
            path = Path.Combine(dir, $"{name}_{n}{ext}");
            n++;
        } while (File.Exists(path));

        return path;
    }

    internal static long FindPattern(Stream stream, long startOffset, ReadOnlySpan<byte> pattern)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(SEARCH_BUFFER_SIZE);
        long absoluteOffset = startOffset;

        while (absoluteOffset < stream.Length)
        {
            stream.Position = absoluteOffset;
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead < pattern.Length)
                break;

            for (int i = 0; i <= bytesRead - pattern.Length; i++)
            {
                if (buffer.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                    return absoluteOffset + i;
            }

            absoluteOffset += bytesRead - pattern.Length + 1;
        }

        return -1;
    }
}
