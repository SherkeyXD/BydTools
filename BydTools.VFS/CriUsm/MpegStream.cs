using BydTools.Utils.VGMToolbox;

namespace BydTools.VFS.CriUsm;

/// <summary>
/// Base class for MPEG Program Stream (PS) demultiplexing.
/// Provides core functionality for parsing packetized elementary streams.
/// </summary>
public abstract class MpegStream
{
    // Standard MPEG-2 PS packet signatures
    private static readonly byte[] _packStartBytes = [0x00, 0x00, 0x01, 0xBA];
    private static readonly byte[] _packEndBytes = [0x00, 0x00, 0x01, 0xB9];

    /// <summary>Packet block ID definitions and their size characteristics.</summary>
    protected Dictionary<uint, BlockSizeInfo> BlockIds { get; } = new();

    /// <summary>Stream ID to file extension mapping.</summary>
    protected Dictionary<byte, string> StreamExtensions { get; } = new();

    /// <summary>Source file path.</summary>
    public string FilePath { get; }

    /// <summary>Audio file extension.</summary>
    public string AudioExtension { get; protected set; } = string.Empty;

    /// <summary>Video file extension.</summary>
    public string VideoExtension { get; protected set; } = string.Empty;

    /// <summary>Whether the same stream ID is used for multiple audio tracks.</summary>
    public bool UsesSameIdForMultipleAudioTracks { get; protected set; }

    /// <summary>Whether subtitle extraction is supported.</summary>
    public bool SupportsSubtitleExtraction { get; protected set; }

    /// <summary>Whether block sizes are little-endian.</summary>
    public bool LittleEndianBlockSizes { get; protected set; }

    /// <summary>Creates a new MpegStream instance.</summary>
    protected MpegStream(string path)
    {
        FilePath = path;
        UsesSameIdForMultipleAudioTracks = false;
        SupportsSubtitleExtraction = false;
        LittleEndianBlockSizes = false;

        // Initialize standard MPEG block IDs
        InitializeBlockIds();
    }

    private void InitializeBlockIds()
    {
        var packEndId = BitConverter.ToUInt32(_packEndBytes, 0);
        BlockIds[packEndId] = new BlockSizeInfo(PacketSizeType.Eof, -1);

        var packStartId = BitConverter.ToUInt32(_packStartBytes, 0);
        BlockIds[packStartId] = new BlockSizeInfo(PacketSizeType.Static, 0xE);

        // Initialize 0x00-0xAF range
        for (byte i = 0; i <= 0xAF; i++)
        {
            var idBytes = new byte[] { 0x00, 0x00, 0x01, i };
            var id = BitConverter.ToUInt32(idBytes, 0);
            BlockIds[id] = new BlockSizeInfo(PacketSizeType.Static, 0xE);
        }
    }

    /// <summary>Returns the packet start signature bytes.</summary>
    protected virtual byte[] GetPacketStartBytes() => _packStartBytes;

    /// <summary>Returns the packet end signature bytes.</summary>
    protected virtual byte[] GetPacketEndBytes() => _packEndBytes;

    /// <summary>Gets the audio packet header size at the current position.</summary>
    protected abstract int GetAudioHeaderSize(Stream stream, long offset);

    /// <summary>Gets the audio packet sub-header size.</summary>
    protected virtual int GetAudioSubHeaderSize(Stream stream, long offset, byte streamId) => 0;

    /// <summary>Gets the video packet header size at the current position.</summary>
    protected abstract int GetVideoHeaderSize(Stream stream, long offset);

    /// <summary>Gets the audio packet footer size.</summary>
    protected virtual int GetAudioFooterSize(Stream stream, long offset) => 0;

    /// <summary>Gets the video packet footer size.</summary>
    protected virtual int GetVideoFooterSize(Stream stream, long offset) => 0;

    /// <summary>Determines if the block ID represents an audio block.</summary>
    protected virtual bool IsAudioBlock(ReadOnlySpan<byte> blockId) =>
        blockId.Length >= 4 && blockId[3] >= 0xC0 && blockId[3] <= 0xDF;

    /// <summary>Determines if the block ID represents a video block.</summary>
    protected virtual bool IsVideoBlock(ReadOnlySpan<byte> blockId) =>
        blockId.Length >= 4 && blockId[3] >= 0xE0 && blockId[3] <= 0xEF;

    /// <summary>Gets the file extension for audio files.</summary>
    protected virtual string GetAudioExtension(Stream stream, long offset) => AudioExtension;

    /// <summary>Gets the file extension for video files.</summary>
    protected virtual string GetVideoExtension(Stream stream, long offset) => VideoExtension;

    /// <summary>Gets the stream ID from the current position.</summary>
    protected virtual byte GetStreamId(Stream stream, long offset) => 0;

    /// <summary>Gets the start offset for parsing.</summary>
    protected virtual long GetStartOffset(Stream stream, long offset) => 0;

    /// <summary>Performs final tasks after demuxing.</summary>
    protected virtual string[] PostProcess(
        Stream source,
        Dictionary<uint, FileStream> outputs,
        bool addHeader) => Array.Empty<string>();

    /// <summary>
    /// Demultiplexes the MPEG stream into separate audio and video files.
    /// </summary>
    public string[] Demux(bool video, bool audio, bool addHeader = false)
    {
        using var stream = File.OpenRead(FilePath);
        return Demux(stream, video, audio, addHeader);
    }

    /// <summary>
    /// Demultiplexes the MPEG stream from a provided stream.
    /// </summary>
    public string[] Demux(Stream stream, bool video, bool audio, bool addHeader = false)
    {
        var options = new DemuxOptions
        {
            ExtractVideo = video,
            ExtractAudio = audio,
            AddHeader = addHeader
        };

        return DemuxInternal(stream, options);
    }

    private string[] DemuxInternal(Stream stream, DemuxOptions options)
    {
        long fileSize = stream.Length;
        long currentOffset = GetStartOffset(stream, 0);

        // Find first pack start
        currentOffset = ParseUtils.FindNextOffset(stream, currentOffset, GetPacketStartBytes());

        if (currentOffset == -1)
        {
            throw new FormatException($"Cannot find pack header in: {Path.GetFileName(FilePath)}");
        }

        var outputs = new Dictionary<uint, FileStream>();
        byte[] blockIdBytes = new byte[4];

        try
        {
            while (currentOffset < fileSize)
            {
                // Read block ID
                stream.Position = currentOffset;
                stream.ReadExactly(blockIdBytes, 0, 4);
                uint blockId = BitConverter.ToUInt32(blockIdBytes, 0);

                if (!BlockIds.TryGetValue(blockId, out var blockInfo))
                {
                    // Unknown block - skip ahead
                    currentOffset++;
                    continue;
                }

                // Process based on block type
                switch (blockInfo.SizeType)
                {
                    case PacketSizeType.Eof:
                        // End of file marker
                        currentOffset = fileSize;
                        break;

                    case PacketSizeType.Static:
                        currentOffset += blockInfo.StaticSize;
                        break;

                    case PacketSizeType.SizeBytes:
                        // Read size from following bytes
                        byte[] sizeBytes = new byte[blockInfo.StaticSize];
                        stream.ReadExactly(sizeBytes, 0, blockInfo.StaticSize);

                        if (!LittleEndianBlockSizes)
                        {
                            Array.Reverse(sizeBytes);
                        }

                        int blockSize = blockInfo.StaticSize switch
                        {
                            4 => BitConverter.ToInt32(sizeBytes, 0),
                            2 => BitConverter.ToUInt16(sizeBytes, 0),
                            1 => sizeBytes[0],
                            _ => throw new InvalidDataException($"Unsupported block size field: {blockInfo.StaticSize}")
                        };

                        currentOffset += 4 + blockInfo.StaticSize + blockSize;
                        break;
                }
            }
        }
        finally
        {
            foreach (var fs in outputs.Values)
            {
                fs.Dispose();
            }
        }

        return PostProcess(stream, outputs, options.AddHeader);
    }

    /// <summary>Demultiplexing options.</summary>
    protected struct DemuxOptions
    {
        public bool ExtractVideo;
        public bool ExtractAudio;
        public bool AddHeader;
    }
}
