using System.Collections.Concurrent;
using BydTools.Audio.Bnk;
using BydTools.Audio.Naming;
using BydTools.Audio.Pck;
using BydTools.Audio.Wem;
using BydTools.Core;
using BydTools.Extraction.Sink;
using BydTools.Formats.SparkBuffer;
using BydTools.VFS;

namespace BydTools.Extraction.Audio;

public enum AudioExportMode
{
    Raw,
    Wav,
}

public sealed class AudioExportResult(int Extracted, int Converted, int Failed, IReadOnlyList<string> Failures)
{
    public int Extracted { get; } = Extracted;
    public int Converted { get; } = Converted;
    public int Failed { get; } = Failed;
    public IReadOnlyList<string> Failures { get; } = Failures;
}

/// <summary>
/// Exports one audio block. PCK archives are decrypted and converted one at a time,
/// and WEM jobs point into that archive's buffer instead of copying every file up front.
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
        progress ??= NullProgress.Instance;
        string? language = block.Descriptor?.AudioLanguage;
        PckMapper? mapper = null;
        if (mapNames && language != null)
            mapper = LoadMapper(archive, language);
        else if (mapNames)
            _logger.Info("Auto-mapping skipped: no language context for {0}", block.DisplayName);

        var sink = new DirectoryOutputSink(outputDirectory);
        int extracted = 0;
        int converted = 0;
        int failed = 0;
        int mapped = 0;
        int unmapped = 0;
        var failures = new ConcurrentBag<string>();
        int pckCount = 0;

        foreach (var chunk in block.Info.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? chunkPath = block.ResolveChunkPath(chunk);
            if (chunkPath == null)
            {
                _logger.Verbose("  Chunk not found, skipping");
                continue;
            }

            foreach (var file in chunk.Files)
            {
                if (!file.Name.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
                    continue;
                pckCount++;
                byte[] data;
                try
                {
                    data = block.ReadFile(chunkPath, file);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    failures.Add($"{file.Name}: {ex.Message}");
                    continue;
                }

                try
                {
                    ExportPck(
                        data,
                        file.Name,
                        block.DisplayName,
                        language,
                        mode,
                        mapper,
                        decoder,
                        jobs,
                        sink,
                        progress,
                        cancellationToken,
                        ref extracted,
                        ref converted,
                        ref failed,
                        ref mapped,
                        ref unmapped,
                        failures
                    );
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add($"{file.Name}: {ex.Message}");
                }
            }
        }

        if (pckCount == 0)
            _logger.Info("No PCK files found in block.");
        else
            _logger.Info("Name mapping: mapped={0}, unmapped={1}", mapped, unmapped);

        foreach (string message in failures)
            _logger.Verbose("  Failed: {0}", message);

        return new AudioExportResult(extracted, converted, failed, failures.ToArray());
    }

    private void ExportPck(
        byte[] data,
        string pckName,
        string blockFolder,
        string? blockLanguage,
        AudioExportMode mode,
        PckMapper? mapper,
        IWemDecoder? decoder,
        int jobs,
        DirectoryOutputSink sink,
        IProgressSink progress,
        CancellationToken cancellationToken,
        ref int extracted,
        ref int converted,
        ref int failed,
        ref int mapped,
        ref int unmapped,
        ConcurrentBag<string> failures
    )
    {
        _logger.Info("Parsing {0}...", pckName);
        var pck = PckArchive.Parse(data);
        if (pck.IsVfsEncrypted)
            pck.DecipherPayloads(data);
        _logger.Verbose("  {0} entries, {1} languages", pck.Entries.Count, pck.Languages.Count);

        string extension = mode == AudioExportMode.Wav ? ".wav" : ".wem";
        var jobsToRun = new List<AudioJob>();
        foreach (var entry in pck.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PckArchive.TrySlice(data, entry, out var slice) || slice.Length < 4)
                continue;

            try
            {
                if (slice[..4].SequenceEqual("BKHD"u8))
                {
                    var wems = BnkParser.Parse(slice);
                    foreach (var wem in wems)
                    {
                        bool isMapped = mapper?.GetMappedPath(wem.Id) != null;
                        if (isMapped)
                            mapped++;
                        else
                            unmapped++;
                        string relative = AudioNaming.ResolveBnkWem(
                            blockFolder,
                            blockLanguage,
                            entry.FileId,
                            wem.Id,
                            mapper,
                            extension,
                            pck.Languages,
                            entry.LanguageId
                        );
                        jobsToRun.Add(new AudioJob((int)entry.Offset + wem.Offset, wem.Size, relative));
                    }
                }
                else if (slice[..4].SequenceEqual("RIFF"u8) || slice[..4].SequenceEqual("RIFX"u8))
                {
                    bool isMapped = mapper?.GetMappedPath(entry.FileId) != null;
                    if (isMapped)
                        mapped++;
                    else
                        unmapped++;
                    string relative = AudioNaming.ResolveWem(
                        blockFolder,
                        blockLanguage,
                        entry.FileId,
                        mapper,
                        extension,
                        pck.Languages,
                        entry.LanguageId
                    );
                    jobsToRun.Add(new AudioJob((int)entry.Offset, (int)entry.Size, relative));
                }
                else if (slice[..4].SequenceEqual("PLUG"u8) && mode == AudioExportMode.Raw)
                {
                    string relative = AudioNaming.ResolveWem(
                        blockFolder,
                        blockLanguage,
                        entry.FileId,
                        mapper,
                        ".plg",
                        pck.Languages,
                        entry.LanguageId
                    );
                    Write(sink, relative, slice);
                    extracted++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                failures.Add($"{pckName}#{entry.FileId}: {ex.Message}");
            }
        }

        if (jobsToRun.Count == 0)
            return;

        progress.AddTotal(jobsToRun.Count);
        if (mode == AudioExportMode.Raw || decoder == null)
        {
            foreach (var job in jobsToRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Write(sink, job.RelativePath, data.AsSpan(job.Offset, job.Length));
                extracted++;
                progress.Advance(1);
            }

            return;
        }

        int localConverted = 0;
        int localFailed = 0;
        Parallel.ForEach(
            jobsToRun,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, jobs),
                CancellationToken = cancellationToken,
            },
            job =>
            {
                try
                {
                    using var output = sink.Create(job.RelativePath);
                    decoder.Decode(data.AsMemory(job.Offset, job.Length), output);
                    output.Complete();
                    Interlocked.Increment(ref localConverted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    try
                    {
                        string fallback = Path.ChangeExtension(job.RelativePath, ".wem");
                        Write(sink, fallback, data.AsSpan(job.Offset, job.Length));
                    }
                    catch (Exception writeEx) when (writeEx is not OperationCanceledException)
                    {
                        failures.Add(writeEx.Message);
                    }

                    failures.Add(ex.Message);
                    Interlocked.Increment(ref localFailed);
                }
                finally
                {
                    progress.Advance(1);
                }
            }
        );
        converted += localConverted;
        failed += localFailed;
        extracted += localConverted + localFailed;
    }

    private PckMapper? LoadMapper(VfsArchive archive, string language)
    {
        _logger.Info("Loading AudioDialog from Table block...");
        VfsBlock? table = archive.Find(nameof(EVFSBlockType.Table));
        if (table == null)
        {
            _logger.Info("  AudioDialog not found in Table block, skipping auto-map");
            return null;
        }

        foreach (var chunk in table.Info.Chunks)
        {
            string? chunkPath = table.ResolveChunkPath(chunk);
            if (chunkPath == null)
                continue;
            foreach (var file in chunk.Files)
            {
                if (!file.Name.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    byte[] data = table.ReadFile(chunkPath, file);
                    if (SparkBuffer.GetRootName(data) != "AudioDialog")
                        continue;
                    _logger.Info("  Found AudioDialog, parsing...");
                    var mapper = new PckMapper(SparkBuffer.ReadObject(data), language);
                    _logger.Info("  Mapped {0} entries (lang={1})", mapper.Count, language);
                    return mapper;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    continue;
                }
            }
        }

        _logger.Info("  AudioDialog not found in Table block, skipping auto-map");
        return null;
    }

    private static void Write(IOutputSink sink, string relativePath, ReadOnlySpan<byte> data)
    {
        using var output = sink.Create(relativePath);
        output.Write(data);
        output.Complete();
    }

    private readonly record struct AudioJob(int Offset, int Length, string RelativePath);
}
