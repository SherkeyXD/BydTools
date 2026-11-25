using System.Buffers;
using BydTools.Core;
using BydTools.Core.IO;
using BydTools.Extraction.Sink;
using BydTools.Formats.Bundle;
using BydTools.Formats.MemoryPack;
using BydTools.Formats.Usm;
using BydTools.VFS;

namespace BydTools.Extraction.Vfs;

public sealed record VfsExtractResult(int Extracted, int Failed);

public enum VfsCheckStatus
{
    Passed,
    Skipped,
    Failed,
}

public sealed record VfsBlockCheckResult(
    string BlockName,
    byte BlockType,
    VfsCheckStatus Status,
    string? SourceFile,
    string? Reason = null
);

public sealed record VfsCheckResult(IReadOnlyList<VfsBlockCheckResult> Blocks)
{
    public int Passed => Blocks.Count(static b => b.Status == VfsCheckStatus.Passed);
    public int Skipped => Blocks.Count(static b => b.Status == VfsCheckStatus.Skipped);
    public int Failed => Blocks.Count(static b => b.Status == VfsCheckStatus.Failed);
}

public sealed class VfsExtractor
{
    private const long MaxBatchBytes = 16 * 1024 * 1024;
    private readonly ILogger _logger;
    private readonly MemoryPackSchema? _jsonSchema;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? _unionMaps;
    private readonly bool _rawJsonData;
    private readonly bool _rawBundles;
    private readonly bool _rawManifests;

    public VfsExtractor(
        ILogger logger,
        bool rawJsonData = false,
        MemoryPackSchema? jsonSchema = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? unionMaps = null,
        bool rawBundles = false,
        bool rawManifests = false)
    {
        _logger = logger;
        _rawJsonData = rawJsonData;
        _jsonSchema = jsonSchema;
        _unionMaps = unionMaps;
        _rawBundles = rawBundles;
        _rawManifests = rawManifests;
    }

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
        if (_logger.IsVerboseEnabled)
            WriteBlockInfo(info);
        _logger.Info("Extracting...");
        progress.AddTotal(totalBytes);

        var sink = new DirectoryOutputSink(outputDirectory);
        ContentKind kind = block.Descriptor?.Kind ?? ContentKind.Generic;
        BeyondStringPool? pool = kind == ContentKind.JsonData && !_rawJsonData
            ? TryLoadStringPool(block)
            : null;
        IContentHandler? handler = CreateHandler(kind, pool);

        List<ChunkBatch> batches = BuildBatches(block, info, totalFiles, jobs, progress);
        int extracted = 0;
        int failed = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, jobs),
            CancellationToken = cancellationToken,
        };

        Parallel.ForEach(
            batches,
            options,
            batch =>
            {
                int end = batch.StartIndex + batch.Count;
                using var chunkReader = block.OpenChunkReader(batch.Chunk);
                if (chunkReader == null)
                {
                    if (_logger.IsVerboseEnabled)
                        _logger.Verbose("  Chunk file not found: {0}", batch.Chunk.FileName);
                    for (int i = batch.StartIndex; i < end; i++)
                        progress.Advance(Math.Max(0, batch.Chunk.Files[i].Length));
                    return;
                }

                for (int i = batch.StartIndex; i < end; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    VfsFile file = batch.Chunk.Files[i];
                    string relative = file.Name;
                    try
                    {
                        ExtractFile(chunkReader, file, kind, handler, sink, strictHandler: false, ref relative);
                        Interlocked.Increment(ref extracted);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        if (_logger.IsVerboseEnabled)
                            _logger.Verbose("  Failed {0}: {1}", relative, ex.Message);
                    }
                    finally
                    {
                        progress.Advance(Math.Max(0, file.Length));
                    }
                }

                if (_logger.IsVerboseEnabled)
                {
                    _logger.Verbose(
                        "  Dumped {0} file(s) from chunk {1} [{2}..{3})",
                        batch.Count,
                        batch.Chunk.FileName,
                        batch.StartIndex,
                        end
                    );
                }
            }
        );

        _logger.Info(
            failed == 0
                ? $"Done, {extracted} files extracted."
                : $"Done, {extracted} files extracted. {failed} failed."
        );
        return new VfsExtractResult(extracted, failed);
    }

    public VfsCheckResult Check(
        VfsArchive archive,
        string outputDirectory,
        IProgressSink? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress ??= NullProgress.Instance;
        if (archive.Blocks.Count == 0)
        {
            _logger.Info("No block declarations found.");
            return new VfsCheckResult([]);
        }

        var blocks = archive.Blocks
            .OrderBy(static b => b.DisplayName, StringComparer.Ordinal)
            .ToArray();
        progress.AddTotal(blocks.Length);

        var results = new List<VfsBlockCheckResult>(blocks.Length);
        foreach (VfsBlock block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VfsBlockCheckResult blockResult = CheckBlock(block, outputDirectory, cancellationToken);
            results.Add(blockResult);
            progress.Advance(1);
        }

        var summary = new VfsCheckResult(results);
        _logger.Info(
            "Check complete: {0} passed, {1} skipped, {2} failed.",
            summary.Passed,
            summary.Skipped,
            summary.Failed);
        return summary;
    }

    private VfsBlockCheckResult CheckBlock(
        VfsBlock block,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        byte blockType = (byte)block.Info.BlockType;
        int declaredFiles = 0;
        var candidates = new List<(VfsChunk Chunk, VfsFile File)>();
        foreach (VfsChunk chunk in block.Info.Chunks)
        {
            declaredFiles += chunk.Files.Count;
            if (chunk.Files.Count == 0 || block.ResolveChunkPath(chunk) == null)
                continue;

            foreach (VfsFile file in chunk.Files)
            {
                if (file.Length >= 0)
                    candidates.Add((chunk, file));
            }
        }

        if (candidates.Count == 0)
        {
            string reason = declaredFiles == 0
                ? "block has no files"
                : "no chunk files found locally";
            _logger.Info("SKIP  {0} ({1}): {2}", block.DisplayName, blockType, reason);
            return new VfsBlockCheckResult(block.DisplayName, blockType, VfsCheckStatus.Skipped, null, reason);
        }

        ContentKind kind = block.Descriptor?.Kind ?? ContentKind.Generic;
        string sourceName = "";
        try
        {
            BeyondStringPool? pool = kind == ContentKind.JsonData && !_rawJsonData
                ? LoadStringPool(block, cancellationToken)
                : null;
            IContentHandler? handler = CreateHandler(kind, pool);

            var ordered = OrderCheckCandidates(candidates, kind, handler);
            (VfsChunk Chunk, VfsFile File) selected = SelectCheckCandidate(block, ordered, kind, handler, cancellationToken);
            sourceName = string.IsNullOrEmpty(selected.File.Name)
                ? $"0x{selected.File.NameHash:X16}"
                : selected.File.Name;

            string blockOutput = Path.Combine(outputDirectory, SanitizeDirectorySegment(block.DisplayName));
            var sink = new DirectoryOutputSink(blockOutput);
            using var chunkReader = block.OpenChunkReader(selected.Chunk)
                ?? throw new FileNotFoundException($"Chunk file not found: {selected.Chunk.FileName}");

            string exported = selected.File.Name;
            ExtractFile(chunkReader, selected.File, kind, handler, sink, strictHandler: true, ref exported);
            _logger.Info("PASS  {0} ({1}) -> {2}", block.DisplayName, blockType, exported);
            return new VfsBlockCheckResult(block.DisplayName, blockType, VfsCheckStatus.Passed, exported);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (string.IsNullOrEmpty(sourceName))
                _logger.Info("FAIL  {0} ({1}): {2}", block.DisplayName, blockType, ex.Message);
            else
                _logger.Info("FAIL  {0} ({1}) [{2}]: {3}", block.DisplayName, blockType, sourceName, ex.Message);
            return new VfsBlockCheckResult(
                block.DisplayName,
                blockType,
                VfsCheckStatus.Failed,
                string.IsNullOrEmpty(sourceName) ? null : sourceName,
                ex.Message);
        }
    }

    private static List<(VfsChunk Chunk, VfsFile File)> OrderCheckCandidates(
        List<(VfsChunk Chunk, VfsFile File)> candidates,
        ContentKind kind,
        IContentHandler? handler) =>
        candidates
            .OrderByDescending(c => CanHandleCandidate(c.File.Name, kind, handler))
            .ThenByDescending(c => !string.IsNullOrEmpty(c.File.Name) || kind == ContentKind.Video)
            .ThenByDescending(static c => c.File.Length > 0)
            .ThenByDescending(static c => c.File.Encrypted)
            .ThenBy(static c => c.File.Length)
            .ToList();

    private static bool CanHandleCandidate(string fileName, ContentKind kind, IContentHandler? handler)
    {
        if (handler == null)
            return true;
        if (string.IsNullOrEmpty(fileName) && kind == ContentKind.Video)
            return true;
        return handler.CanHandle(fileName);
    }

    private static (VfsChunk Chunk, VfsFile File) SelectCheckCandidate(
        VfsBlock block,
        List<(VfsChunk Chunk, VfsFile File)> ordered,
        ContentKind kind,
        IContentHandler? handler,
        CancellationToken cancellationToken)
    {
        const int maxProbeCount = 16;
        if (handler is JsonDataHandler)
        {
            (VfsChunk Chunk, VfsFile File)? plainFallback = null;
            int probed = 0;
            foreach (var candidate in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!handler.CanHandle(candidate.File.Name) || candidate.File.Length == 0 || candidate.File.Length > 4 * 1024 * 1024)
                    continue;
                if (++probed > maxProbeCount)
                    break;

                using var chunkReader = block.OpenChunkReader(candidate.Chunk);
                if (chunkReader == null)
                    continue;
                byte[] rented = chunkReader.RentAndReadFile(candidate.File, out int length);
                try
                {
                    var classification = JsonDataMemoryPack.Classify(rented.AsSpan(0, length));
                    if (classification == JsonDataClassification.MemoryPack)
                        return candidate;
                    if (classification == JsonDataClassification.PlainText && plainFallback == null)
                        plainFallback = candidate;
                }
                finally
                {
                    if (rented.Length > 0)
                        ArrayPool<byte>.Shared.Return(rented);
                }
            }

            if (plainFallback != null)
                return plainFallback.Value;
        }
        else if (handler is BundleHandler)
        {
            Span<byte> prefix = stackalloc byte[EndfieldBundle.MinimumHeaderBytes];
            int probed = 0;
            foreach (var candidate in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!handler.CanHandle(candidate.File.Name) || candidate.File.Length < 8)
                    continue;
                if (++probed > maxProbeCount)
                    break;

                using var chunkReader = block.OpenChunkReader(candidate.Chunk);
                if (chunkReader == null)
                    continue;
                int read = chunkReader.ReadPrefix(candidate.File, prefix);
                if (!EndfieldBundle.IsStandardUnityBundle(prefix[..read]))
                    return candidate;
            }
        }

        return ordered[0];
    }

    private void ExtractFile(
        VfsChunkReader chunkReader,
        VfsFile file,
        ContentKind kind,
        IContentHandler? handler,
        DirectoryOutputSink sink,
        bool strictHandler,
        ref string relative)
    {
        if (string.IsNullOrEmpty(relative) && kind == ContentKind.Video)
        {
            string usmName = ReadUsmName(chunkReader, file) ?? $"{file.NameHash:X16}.usm";
            relative = Path.Combine("Video", usmName);
            if (_logger.IsVerboseEnabled)
                _logger.Verbose("    Recovered USM name: {0}", relative);
        }

        if (handler != null && handler.CanHandle(relative))
        {
            byte[] rented = chunkReader.RentAndReadFile(file, out int length);
            try
            {
                Memory<byte> data = rented.AsMemory(0, length);
                if (!handler.Handle(relative, data, sink))
                {
                    if (strictHandler && handler is UsmHandler)
                        throw new InvalidDataException("USM demux produced no output streams.");
                    sink.WriteRaw(relative, data.Span);
                }
            }
            finally
            {
                if (rented.Length > 0)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }
        else
        {
            using var output = sink.Create(relative);
            chunkReader.CopyFileTo(file, output);
            output.Complete();
        }
    }

    private static BeyondStringPool? LoadStringPool(VfsBlock block, CancellationToken cancellationToken = default)
    {
        var files = new List<(string Name, byte[] Data)>();
        foreach (var chunk in block.Info.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!chunk.Files.Any(static file => BeyondStringPool.IndexOfFileName(file.Name) >= 0))
                continue;
            using var chunkReader = block.OpenChunkReader(chunk);
            if (chunkReader == null)
                continue;
            foreach (var file in chunk.Files)
            {
                if (BeyondStringPool.IndexOfFileName(file.Name) < 0)
                    continue;
                files.Add((file.Name, chunkReader.ReadFile(file)));
            }
        }

        return BeyondStringPool.TryLoad(files);
    }

    private static string SanitizeDirectorySegment(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars);
    }

    private List<ChunkBatch> BuildBatches(
        VfsBlock block,
        VfsBlockInfo info,
        int totalFiles,
        int jobs,
        IProgressSink progress)
    {
        int targetBatches = Math.Max(1, jobs) * 4;
        int maxFilesPerBatch = Math.Clamp(totalFiles / targetBatches, 16, 256);
        var batches = new List<ChunkBatch>(info.Chunks.Count);

        foreach (var chunk in info.Chunks)
        {
            if (chunk.Files.Count == 0)
                continue;

            if (block.ResolveChunkPath(chunk) == null)
            {
                if (_logger.IsVerboseEnabled)
                    _logger.Verbose("  Chunk file not found: {0}", chunk.FileName);
                long missingBytes = 0;
                foreach (var file in chunk.Files)
                    missingBytes += Math.Max(0, file.Length);
                if (missingBytes > 0)
                    progress.Advance(missingBytes);
                continue;
            }

            int start = 0;
            int count = 0;
            long bytes = 0;
            for (int i = 0; i < chunk.Files.Count; i++)
            {
                long len = Math.Max(0, chunk.Files[i].Length);
                if (count >= maxFilesPerBatch || (count > 0 && bytes + len > MaxBatchBytes))
                {
                    batches.Add(new ChunkBatch(chunk, start, count));
                    start = i;
                    count = 0;
                    bytes = 0;
                }

                count++;
                bytes += len;
            }

            if (count > 0)
                batches.Add(new ChunkBatch(chunk, start, count));
        }

        return batches;
    }

    private static string? ReadUsmName(VfsChunkReader chunkReader, VfsFile file)
    {
        const int initialSize = 4096;
        Span<byte> stackBuf = stackalloc byte[initialSize];
        int read = chunkReader.ReadPrefix(file, stackBuf);
        if (read < 12)
            return null;

        ReadOnlySpan<byte> header = stackBuf[..read];
        if (!UsmNameReader.TryGetHeaderLength(header, out int totalBytes) || totalBytes > file.Length)
            return null;

        if (totalBytes <= read)
            return UsmNameReader.TryGetName(header);

        if (totalBytes > 1024 * 1024)
            return null;

        byte[] rented = ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            int fullRead = chunkReader.ReadPrefix(file, rented.AsSpan(0, totalBytes));
            return UsmNameReader.TryGetName(rented.AsSpan(0, fullRead));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void WriteDebugReport(VfsArchive archive)
    {
        _logger.Info("Debug: scanning all block declarations under {0}", archive.Root);
        if (!string.IsNullOrWhiteSpace(archive.PersistentRoot))
            _logger.Info("  Persistent (preferred): {0}", archive.PersistentRoot);
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
            string sourceSuffix = string.IsNullOrWhiteSpace(archive.PersistentRoot)
                ? ""
                : $"  |  source = {(IsUnderDirectory(block.DirectoryPath, archive.PersistentRoot) ? "Persistent" : "StreamingAssets")}";
            _logger.Info(
                "  [{0}] groupCfgName = {1}  |  blockType = {2}  |  chunks = {3}  |  files = {4}{5}",
                info.GroupCfgHashName,
                info.GroupCfgName,
                typeLabel,
                info.Chunks.Count,
                info.GroupFileInfoNum,
                sourceSuffix
            );
            WriteBlockInfoIfEnabled(info);
        }

        _logger.Info("");
        _logger.Info("Debug: found {0} block declaration(s).", archive.Blocks.Count);
        _logger.Info("");
        _logger.Info("Block type mapping (groupCfgName -> directory hash):");
        foreach (var block in archive.Blocks.OrderBy(static b => b.Info.GroupCfgName, StringComparer.Ordinal))
            _logger.Info("  {0} -> {1}", block.Info.GroupCfgName, block.Info.GroupCfgHashName);
    }

    private IContentHandler? CreateHandler(ContentKind kind, BeyondStringPool? pool) =>
        kind switch
        {
            ContentKind.Table => new SparkBufferHandler(),
            ContentKind.Lua => new LuaHandler(),
            ContentKind.Video => new UsmHandler(),
            ContentKind.Audio => new PckDecryptHandler(),
            ContentKind.JsonData => _rawJsonData
                ? null
                : _jsonSchema == null
                    ? new JsonDataHandler(pool)
                    : new JsonDataHandler(_jsonSchema, _unionMaps, pool),
            ContentKind.Bundle => _rawBundles ? null : new BundleHandler(),
            ContentKind.BundleManifest => _rawManifests ? null : new BundleManifestHandler(),
            _ => null,
        };

    private BeyondStringPool? TryLoadStringPool(VfsBlock block)
    {
        try
        {
            return LoadStringPool(block);
        }
        catch (Exception ex)
        {
            _logger.Verbose("  String pool was not loaded: {0}", ex.Message);
            return null;
        }
    }

    private static bool IsUnderDirectory(string path, string root) =>
        PathChecks.IsSubPathOf(path, root);

    private void WriteBlockInfoIfEnabled(VfsBlockInfo info)
    {
        if (_logger.IsVerboseEnabled)
            WriteBlockInfo(info);
    }

    private void WriteBlockInfo(VfsBlockInfo info)
    {
        _logger.Verbose("========== BLC INFO ==========");
        _logger.Verbose("GroupCfgName   : {0}", info.GroupCfgName);
        _logger.Verbose("GroupCfgHash   : {0}", info.GroupCfgHashName);
        _logger.Verbose("NonceVersion   : {0}", info.NonceVersion);
        _logger.Verbose("CodeVersion    : {0}", info.CodeVersion);
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

    private readonly record struct ChunkBatch(VfsChunk Chunk, int StartIndex, int Count);
}
