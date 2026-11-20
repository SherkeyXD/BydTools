using System.Collections.Concurrent;
using System.Text.Json;
using BydTools.Audio.Bnk;
using BydTools.Audio.Naming;
using BydTools.Audio.Pck;
using BydTools.Audio.Wem;
using BydTools.Core;
using BydTools.Core.IO;
using BydTools.Extraction.Sink;
using BydTools.Formats.SparkBuffer;
using BydTools.VFS;

namespace BydTools.Extraction.Audio;

public enum AudioExportMode
{
    Raw,
    Wav,
}

public sealed record AudioExportResult(int Extracted, int Converted, int Failed);

/// <summary>
/// Exports one audio block. PCK archives are decrypted and converted in a bounded pipeline,
/// and WEM jobs point into each archive's buffer instead of copying every file up front.
/// </summary>
public sealed class AudioExportService
{
    private readonly ILogger _logger;

    public AudioExportService(ILogger logger) => _logger = logger;

    public AudioExportResult Export(
        VfsArchive archive,
        VfsBlock block,
        string outputDirectory,
        AudioExportMode mode,
        bool mapNames,
        IWemDecoder? decoder,
        int jobs,
        IProgressSink? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (mode == AudioExportMode.Wav && decoder == null)
            throw new ArgumentNullException(nameof(decoder), "A WEM decoder is required when mode is Wav.");

        progress ??= NullProgress.Instance;
        string? language = block.Descriptor?.AudioLanguage;
        PckMapper? mapper = null;
        if (mapNames && language != null)
            mapper = LoadMapper(archive, language, cancellationToken);
        else if (mapNames)
            _logger.Info("Auto-mapping skipped: no language context for {0}", block.DisplayName);

        var pckFiles = new List<(VfsChunk Chunk, VfsFile File)>();
        long totalPckBytes = 0;
        foreach (var chunk in block.Info.Chunks)
        {
            foreach (var file in chunk.Files)
            {
                if (!file.Name.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
                    continue;
                pckFiles.Add((chunk, file));
                if (file.Length > 0)
                    totalPckBytes += file.Length;
            }
        }

        if (pckFiles.Count == 0)
        {
            _logger.Info("No PCK files found in block.");
            return new AudioExportResult(0, 0, 0);
        }

        progress.AddTotal(totalPckBytes);
        var counters = new ExportCounters();
        var ctx = new PckExportContext(
            block.DisplayName,
            language,
            mode,
            mapper,
            decoder,
            jobs,
            new DirectoryOutputSink(outputDirectory),
            progress,
            counters,
            cancellationToken
        );

        if (mode == AudioExportMode.Raw)
            ExportRaw(block, pckFiles, ctx);
        else
            ExportWavPipelined(block, pckFiles, ctx);

        _logger.Info("Name mapping: mapped={0}, unmapped={1}", counters.Mapped, counters.Unmapped);
        if (_logger.IsVerboseEnabled)
        {
            foreach (string message in counters.Failures)
                _logger.Verbose("  Failed: {0}", message);
        }

        return new AudioExportResult(counters.Extracted, counters.Converted, counters.Failed);
    }

    private void ExportRaw(VfsBlock block, List<(VfsChunk Chunk, VfsFile File)> pckFiles, PckExportContext ctx)
    {
        var seenPaths = new HashSet<string>(PathChecks.FileSystemComparer);
        VfsChunk? currentChunk = null;
        VfsChunkReader? chunkReader = null;
        try
        {
            foreach (var (chunk, file) in pckFiles)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(chunk, currentChunk))
                {
                    chunkReader?.Dispose();
                    currentChunk = chunk;
                    chunkReader = block.OpenChunkReader(chunk);
                    if (chunkReader == null && _logger.IsVerboseEnabled)
                        _logger.Verbose("  Chunk not found, skipping");
                }

                if (chunkReader == null)
                {
                    ctx.Progress.Advance(Math.Max(0, file.Length));
                    continue;
                }

                byte[] data;
                try
                {
                    data = chunkReader.ReadFile(file);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ctx.Counters.AddFailure($"{file.Name}: {ex.Message}");
                    ctx.Progress.Advance(Math.Max(0, file.Length));
                    continue;
                }

                try
                {
                    List<AudioJob> jobsToRun = CollectJobs(data, file.Name, ctx, seenPaths);
                    long jobBytes = 0;
                    foreach (var job in jobsToRun)
                    {
                        ctx.CancellationToken.ThrowIfCancellationRequested();
                        ctx.Sink.WriteRaw(job.RelativePath, data.AsSpan(job.Offset, job.Length));
                        ctx.Counters.AddExtracted();
                        ctx.Progress.Advance(job.Length);
                        jobBytes += job.Length;
                    }

                    long remainder = Math.Max(0, file.Length - jobBytes);
                    if (remainder > 0)
                        ctx.Progress.Advance(remainder);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ctx.Counters.AddFailure($"{file.Name}: {ex.Message}");
                    ctx.Progress.Advance(Math.Max(0, file.Length));
                }
            }
        }
        finally
        {
            chunkReader?.Dispose();
        }
    }

    private void ExportWavPipelined(
        VfsBlock block,
        List<(VfsChunk Chunk, VfsFile File)> pckFiles,
        PckExportContext ctx)
    {
        int workerCount = Math.Max(1, ctx.Jobs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        CancellationToken ct = linkedCts.Token;
        using var pckBudget = new SemaphoreSlim(2, 2);
        using var workQueue = new BlockingCollection<WavWorkItem>(Math.Max(8, workerCount * 4));
        var seenPaths = new HashSet<string>(PathChecks.FileSystemComparer);

        Task producer = Task.Run(() =>
        {
            VfsChunk? currentChunk = null;
            VfsChunkReader? chunkReader = null;
            try
            {
                foreach (var (chunk, file) in pckFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(chunk, currentChunk))
                    {
                        chunkReader?.Dispose();
                        currentChunk = chunk;
                        chunkReader = block.OpenChunkReader(chunk);
                        if (chunkReader == null && _logger.IsVerboseEnabled)
                            _logger.Verbose("  Chunk not found, skipping");
                    }

                    if (chunkReader == null)
                    {
                        ctx.Progress.Advance(Math.Max(0, file.Length));
                        continue;
                    }

                    pckBudget.Wait(ct);
                    bool released = false;
                    try
                    {
                        byte[] data = chunkReader.ReadFile(file);
                        List<AudioJob> jobsToRun = CollectJobs(data, file.Name, ctx, seenPaths);
                        long jobBytes = 0;
                        foreach (var job in jobsToRun)
                            jobBytes += job.Length;

                        long overhead = Math.Max(0, file.Length - jobBytes);
                        if (overhead > 0)
                            ctx.Progress.Advance(overhead);

                        if (jobsToRun.Count == 0)
                        {
                            pckBudget.Release();
                            released = true;
                            continue;
                        }

                        var lease = new PckLease(jobsToRun.Count, pckBudget);
                        released = true; // Ownership transferred to lease.
                        int enqueued = 0;
                        try
                        {
                            foreach (var job in jobsToRun)
                            {
                                workQueue.Add(new WavWorkItem(lease, data, job.Offset, job.Length, job.RelativePath), ct);
                                enqueued++;
                            }
                        }
                        catch
                        {
                            int unqueued = jobsToRun.Count - enqueued;
                            for (int k = 0; k < unqueued; k++)
                                lease.Release();
                            throw;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (!released)
                            pckBudget.Release();
                        ctx.Counters.AddFailure($"{file.Name}: {ex.Message}");
                        ctx.Progress.Advance(Math.Max(0, file.Length));
                    }
                    catch
                    {
                        if (!released)
                            pckBudget.Release();
                        throw;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                linkedCts.Cancel();
                throw;
            }
            finally
            {
                chunkReader?.Dispose();
                workQueue.CompleteAdding();
            }
        }, ct);

        IWemDecoder decoder = ctx.Decoder!;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = workerCount,
            CancellationToken = ct,
        };

        try
        {
            Parallel.ForEach(
                Partitioner.Create(workQueue.GetConsumingEnumerable(ct), EnumerablePartitionerOptions.NoBuffering),
                options,
                item =>
                {
                    try
                    {
                        using var output = ctx.Sink.Create(item.RelativePath);
                        decoder.Decode(item.Data.AsMemory(item.Offset, item.Length), output);
                        output.Complete();
                        ctx.Counters.AddConverted();
                        ctx.Counters.AddExtracted();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ctx.Counters.AddFailure($"{item.RelativePath}: {ex.Message}");
                        string fallback = Path.ChangeExtension(item.RelativePath, ".wem");
                        try
                        {
                            ctx.Sink.WriteRaw(fallback, item.Data.AsSpan(item.Offset, item.Length));
                            ctx.Counters.AddExtracted();
                        }
                        catch (Exception writeEx) when (writeEx is not OperationCanceledException)
                        {
                            ctx.Counters.Failures.Add($"{fallback}: {writeEx.Message}");
                        }
                    }
                    finally
                    {
                        ctx.Progress.Advance(item.Length);
                        item.Lease.Release();
                    }
                }
            );
        }
        catch
        {
            linkedCts.Cancel();
            throw;
        }
        finally
        {
            producer.GetAwaiter().GetResult();
        }
    }

    private List<AudioJob> CollectJobs(
        byte[] data,
        string pckName,
        PckExportContext ctx,
        HashSet<string> seenPaths)
    {
        _logger.Info("Parsing {0}...", pckName);
        var pck = PckArchive.Parse(data);
        if (pck.IsVfsEncrypted)
            pck.DecipherPayloads(data);
        if (_logger.IsVerboseEnabled)
            _logger.Verbose("  {0} entries, {1} languages", pck.Entries.Count, pck.Languages.Count);

        string extension = ctx.Mode == AudioExportMode.Wav ? ".wav" : ".wem";
        var jobsToRun = new List<AudioJob>();

        foreach (var entry in pck.Entries)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            if (!PckArchive.TrySlice(data, entry, out var slice) || slice.Length < 4)
                continue;

            try
            {
                if (slice[..4].SequenceEqual("BKHD"u8))
                {
                    var wems = BnkParser.Parse(slice);
                    foreach (var wem in wems)
                    {
                        string relative = AudioNaming.ResolveBnkWem(
                            ctx.BlockFolder,
                            ctx.BlockLanguage,
                            entry.FileId,
                            wem.Id,
                            ctx.Mapper,
                            extension,
                            pck.Languages,
                            entry.LanguageId,
                            out bool isMapped
                        );
                        ctx.Counters.AddMapping(isMapped);
                        if (seenPaths.Add(relative))
                            jobsToRun.Add(new AudioJob((int)entry.Offset + wem.Offset, wem.Size, relative));
                        else if (_logger.IsVerboseEnabled)
                            _logger.Verbose("  Duplicate audio path skipped: {0}", relative);
                    }
                }
                else if (slice[..4].SequenceEqual("RIFF"u8) || slice[..4].SequenceEqual("RIFX"u8))
                {
                    string relative = AudioNaming.ResolveWem(
                        ctx.BlockFolder,
                        ctx.BlockLanguage,
                        entry.FileId,
                        ctx.Mapper,
                        extension,
                        pck.Languages,
                        entry.LanguageId,
                        out bool isMapped
                    );
                    ctx.Counters.AddMapping(isMapped);
                    if (seenPaths.Add(relative))
                        jobsToRun.Add(new AudioJob((int)entry.Offset, (int)entry.Size, relative));
                    else if (_logger.IsVerboseEnabled)
                        _logger.Verbose("  Duplicate audio path skipped: {0}", relative);
                }
                else if (slice[..4].SequenceEqual("PLUG"u8) && ctx.Mode == AudioExportMode.Raw)
                {
                    string relative = AudioNaming.ResolveWem(
                        ctx.BlockFolder,
                        ctx.BlockLanguage,
                        entry.FileId,
                        ctx.Mapper,
                        ".plg",
                        pck.Languages,
                        entry.LanguageId,
                        out _
                    );
                    if (seenPaths.Add(relative))
                    {
                        ctx.Sink.WriteRaw(relative, slice);
                        ctx.Counters.AddExtracted();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.Counters.AddFailure($"{pckName}#{entry.FileId}: {ex.Message}");
            }
        }

        return jobsToRun;
    }

    private PckMapper? LoadMapper(VfsArchive archive, string language, CancellationToken cancellationToken)
    {
        _logger.Info("Loading AudioDialog from Table block...");
        VfsBlock? table = archive.Find(nameof(EVFSBlockType.Table));
        if (table == null)
        {
            _logger.Info("  AudioDialog not found in Table block, skipping auto-map");
            return null;
        }

        var mapper = TryLoadAudioDialog(
            table,
            language,
            static file => file.Name.Contains("AudioDialog", StringComparison.OrdinalIgnoreCase),
            cancellationToken);
        mapper ??= TryLoadAudioDialog(
            table,
            language,
            static file => !file.Name.Contains("AudioDialog", StringComparison.OrdinalIgnoreCase),
            cancellationToken);
        if (mapper != null)
            return mapper;

        _logger.Info("  AudioDialog not found in Table block, skipping auto-map");
        return null;
    }

    private PckMapper? TryLoadAudioDialog(
        VfsBlock table,
        string language,
        Func<VfsFile, bool> predicate,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in table.Info.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var chunkReader = table.OpenChunkReader(chunk);
            if (chunkReader == null)
                continue;
            foreach (var file in chunk.Files)
            {
                if (!file.Name.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase) || !predicate(file))
                    continue;
                try
                {
                    byte[] data = chunkReader.ReadFile(file);
                    if (SparkBuffer.GetRootName(data) != "AudioDialog")
                        continue;
                    _logger.Info("  Found AudioDialog, parsing...");
                    var mapper = new PckMapper(ExtractAudioDialogPaths(data), language);
                    _logger.Info("  Mapped {0} entries (lang={1})", mapper.Count, language);
                    return mapper;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Verbose("  AudioDialog candidate {0} skipped: {1}", file.Name, ex.Message);
                    continue;
                }
            }
        }

        return null;
    }

    private static List<string> ExtractAudioDialogPaths(ReadOnlySpan<byte> sparkData)
    {
        using var stream = new MemoryStream();
        SparkBuffer.WriteJson(sparkData, stream);
        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("AudioDialog SparkBuffer root is not an object.");

        var paths = new List<string>();
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
                continue;
            if (!entry.Value.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
                continue;
            string? path = pathEl.GetString();
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }

        return paths;
    }

    private readonly record struct AudioJob(int Offset, int Length, string RelativePath);

    private readonly record struct WavWorkItem(
        PckLease Lease,
        byte[] Data,
        int Offset,
        int Length,
        string RelativePath);

    private sealed class PckLease(int count, SemaphoreSlim budget)
    {
        private int _remaining = count;

        public void Release()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
                budget.Release();
        }
    }

    private sealed record PckExportContext(
        string BlockFolder,
        string? BlockLanguage,
        AudioExportMode Mode,
        PckMapper? Mapper,
        IWemDecoder? Decoder,
        int Jobs,
        IOutputSink Sink,
        IProgressSink Progress,
        ExportCounters Counters,
        CancellationToken CancellationToken
    );

    private sealed class ExportCounters
    {
        private int _extracted;
        private int _converted;
        private int _failed;
        private int _mapped;
        private int _unmapped;

        public int Extracted => Volatile.Read(ref _extracted);
        public int Converted => Volatile.Read(ref _converted);
        public int Failed => Volatile.Read(ref _failed);
        public int Mapped => Volatile.Read(ref _mapped);
        public int Unmapped => Volatile.Read(ref _unmapped);
        public ConcurrentBag<string> Failures { get; } = [];

        public void AddExtracted() => Interlocked.Increment(ref _extracted);

        public void AddConverted() => Interlocked.Increment(ref _converted);

        public void AddMapping(bool mapped)
        {
            if (mapped)
                Interlocked.Increment(ref _mapped);
            else
                Interlocked.Increment(ref _unmapped);
        }

        public void AddFailure(string message)
        {
            Failures.Add(message);
            Interlocked.Increment(ref _failed);
        }
    }
}
