using BydTools.Core;
using BydTools.Core.IO;
using BydTools.Extraction.Sink;
using BydTools.Formats.Usm;
using BydTools.VFS;

namespace BydTools.Extraction.Vfs;

public sealed class VfsExtractResult(int Extracted, int Failed)
{
    public int Extracted { get; } = Extracted;
    public int Failed { get; } = Failed;
}

public sealed class VfsExtractor
{
    private readonly ILogger _logger;

    public VfsExtractor(ILogger logger) => _logger = logger;

    public VfsExtractResult Dump(
        VfsBlock block,
        string outputDirectory,
        int jobs,
        IProgressSink? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        progress ??= NullProgress.Instance;
        var info = block.Info;
        _logger.Info("--- {0} ({1}) ---", block.DisplayName, (byte)info.BlockType);

        int totalFiles = 0;
        var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var chunk in info.Chunks)
        {
            totalFiles += chunk.Files.Count;
            foreach (var file in chunk.Files)
            {
                string ext = Path.GetExtension(file.Name).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext))
                    ext = "(no extension)";
                typeCounts.TryGetValue(ext, out int count);
                typeCounts[ext] = count + 1;
                if (file.Length > 0)
                    totalBytes += file.Length;
            }
        }

        string details = string.Join(", ", typeCounts.Select(static pair => $"{pair.Value} {pair.Key}"));
        _logger.Info("Found {0} files ({1})", totalFiles, details);
        WriteBlockInfo(info);
        _logger.Info("Extracting...");
        progress.AddTotal(totalBytes);

        var sink = new DirectoryOutputSink(outputDirectory);
        IContentHandler? handler = CreateHandler(block.Descriptor?.Kind ?? ContentKind.Generic);
        int extracted = 0;
        int failed = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, jobs),
            CancellationToken = cancellationToken,
        };

        Parallel.ForEach(
            info.Chunks,
            options,
            chunk =>
            {
                string? chunkPath = block.ResolveChunkPath(chunk);
                if (chunkPath == null)
                {
                    _logger.Verbose("  Chunk file not found: {0}", chunk.FileName);
                    return;
                }

                foreach (var file in chunk.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        byte[] data = block.ReadFile(chunkPath, file);
                        string relative = file.Name;
                        if (string.IsNullOrEmpty(relative) && block.Descriptor?.Kind == ContentKind.Video)
                        {
                            string usmName = UsmNameReader.TryGetName(data) ?? $"{file.NameHash:X16}.usm";
                            relative = Path.Combine("Video", usmName);
                            _logger.Verbose("    Recovered USM name: {0}", relative);
                        }

                        bool handled = false;
                        if (handler != null && handler.CanHandle(relative))
                        {
                            HandleResult result = handler.Handle(relative, data, sink);
                            if (result == HandleResult.Handled)
                                handled = true;
                            else if (result == HandleResult.Failed)
                                Interlocked.Increment(ref failed);
                        }

                        if (!handled)
                        {
                            using var output = sink.Create(relative);
                            output.Write(data);
                            output.Complete();
                        }

                        Interlocked.Increment(ref extracted);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        _logger.Verbose("  Failed {0}: {1}", file.Name, ex.Message);
                    }
                    finally
                    {
                        progress.Advance(Math.Max(0, file.Length));
                    }
                }

                _logger.Verbose("  Dumped {0} file(s) from chunk {1}", chunk.Files.Count, chunk.FileName);
            }
        );

        _logger.Info(
            failed == 0
                ? $"Done, {extracted} files extracted."
                : $"Done, {extracted} files extracted. {failed} failed."
        );
        return new VfsExtractResult(extracted, failed);
    }

    public void WriteDebugReport(VfsArchive archive)
    {
        _logger.Info("Debug: scanning all block declarations under {0}", archive.Root);
        _logger.Info("");
        if (archive.Blocks.Count == 0)
        {
            _logger.Info("No block declarations found.");
            return;
        }

        foreach (var block in archive.Blocks)
        {
            var info = block.Info;
            string typeLabel = Enum.IsDefined(info.BlockType)
                ? $"{info.BlockType} ({(byte)info.BlockType})"
                : $"Unknown ({(byte)info.BlockType})";
            _logger.Info(
                "  [{0}] groupCfgName = {1}  |  blockType = {2}  |  chunks = {3}  |  files = {4}",
                info.GroupCfgHashName,
                info.GroupCfgName,
                typeLabel,
                info.Chunks.Count,
                info.GroupFileInfoNum
            );
            WriteBlockInfo(info);
        }

        _logger.Info("");
        _logger.Info("Debug: found {0} block declaration(s).", archive.Blocks.Count);
        _logger.Info("");
        _logger.Info("Block type mapping (groupCfgName -> directory hash):");
        foreach (var block in archive.Blocks.OrderBy(static b => b.Info.GroupCfgName, StringComparer.Ordinal))
            _logger.Info("  {0} -> {1}", block.Info.GroupCfgName, block.Info.GroupCfgHashName);
    }

    private static IContentHandler? CreateHandler(ContentKind kind) =>
        kind switch
        {
            ContentKind.Table => new SparkBufferHandler(),
            ContentKind.Lua => new LuaHandler(),
            ContentKind.Video => new UsmHandler(),
            ContentKind.Audio => new PckDecryptHandler(),
            _ => null,
        };

    private void WriteBlockInfo(VfsBlockInfo info)
    {
        _logger.Verbose("========== BLC INFO ==========");
        _logger.Verbose("GroupCfgName   : {0}", info.GroupCfgName);
        _logger.Verbose("GroupCfgHash   : {0}", info.GroupCfgHashName);
        _logger.Verbose("Version        : {0}", info.Version);
        _logger.Verbose("BlockType      : {0} ({1})", info.BlockType, (byte)info.BlockType);
        _logger.Verbose("FileInfoNum    : {0}", info.GroupFileInfoNum);
        _logger.Verbose("ChunksLength   : {0}", info.GroupChunksLength);
        _logger.Verbose("ChunksCount    : {0}", info.Chunks.Count);
        _logger.Verbose("------------------------------");
        for (int i = 0; i < info.Chunks.Count; i++)
        {
            var chunk = info.Chunks[i];
            _logger.Verbose("Chunk #{0}:", i);
            _logger.Verbose("  md5Name      : {0}", chunk.Md5Name.ToHexLittleEndian());
            _logger.Verbose("  contentMD5   : {0}", chunk.ContentMd5.ToHexLittleEndian());
            _logger.Verbose("  length       : {0}", chunk.Length);
            _logger.Verbose("  blockType    : {0} ({1})", chunk.BlockType, (byte)chunk.BlockType);
            _logger.Verbose("  fileTag      : {0} ({1})", chunk.FileTag, (byte)chunk.FileTag);
            _logger.Verbose("  filesCount   : {0}", chunk.Files.Count);
            for (int j = 0; j < chunk.Files.Count; j++)
            {
                var file = chunk.Files[j];
                _logger.Verbose("    File #{0}:", j);
                _logger.Verbose("      name        : {0}", file.Name);
                _logger.Verbose("      nameHash    : 0x{0:X16}", file.NameHash);
                _logger.Verbose("      chunkMD5    : {0}", file.ChunkMd5.ToHexLittleEndian());
                _logger.Verbose("      dataMD5     : {0}", file.DataMd5.ToHexLittleEndian());
                _logger.Verbose("      offset      : {0}", file.Offset);
                _logger.Verbose("      len         : {0}", file.Length);
                _logger.Verbose("      blockType   : {0} ({1})", file.BlockType, (byte)file.BlockType);
                _logger.Verbose("      useEncrypt  : {0}", file.Encrypted);
                if (file.Encrypted)
                    _logger.Verbose("      ivSeed      : {0}", file.IvSeed);
                _logger.Verbose("      fileTag     : {0} ({1})", file.FileTag, (byte)file.FileTag);
            }

            _logger.Verbose("------------------------------");
        }

        _logger.Verbose("======== END BLC INFO ========");
    }
}
