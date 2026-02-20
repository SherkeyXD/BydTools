using BydTools.Utils;
using BydTools.Wwise;

namespace BydTools.PCK;

/// <summary>
/// Extracts audio from PCK archives and converts WEM to WAV via vgmstream,
/// with optional filename mapping via <see cref="PckMapper"/>.
/// </summary>
public class PckConverter
{
    private readonly ILogger _logger;
    private readonly IWemConverter _wemConverter;
    private readonly PckExtractor _extractor;

    public PckConverter(ILogger logger, IWemConverter wemConverter)
        : this(logger, wemConverter, new PckExtractor(logger)) { }

    public PckConverter(ILogger logger, IWemConverter wemConverter, PckExtractor extractor)
    {
        _logger = logger;
        _wemConverter = wemConverter;
        _extractor = extractor;
    }

    /// <summary>
    /// Extracts and converts audio files from a PCK archive.
    /// <list type="bullet">
    ///   <item><b>raw</b> — extract WEM/BNK/PLG without conversion.</item>
    ///   <item><b>wav</b> — convert WEM to WAV via vgmstream; failures kept as WEM.</item>
    /// </list>
    /// </summary>
    public void ExtractAndConvert(
        string pckPath,
        string outputDir,
        string mode = "wav",
        PckMapper? mapper = null
    )
    {
        if (!File.Exists(pckPath))
            throw new FileNotFoundException("PCK file not found", pckPath);

        Directory.CreateDirectory(outputDir);

        string normalizedMode = mode.ToLowerInvariant();
        if (normalizedMode is not ("raw" or "wav"))
            throw new ArgumentException($"Invalid mode '{mode}'. Must be 'raw' or 'wav'.", nameof(mode));

        _logger.Info($"Input:  {pckPath}");
        _logger.Info($"Output: {outputDir}");
        _logger.Info($"Mode:   {normalizedMode}");

        if (normalizedMode == "raw")
        {
            _extractor.ExtractFiles(pckPath, outputDir, mapper);
            return;
        }

        ConvertToWav(pckPath, outputDir, mapper);
    }

    private void ConvertToWav(string pckPath, string outputDir, PckMapper? mapper)
    {
        string tempDir = Path.Combine(outputDir, ".pck_extract");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var fileStream = File.OpenRead(pckPath);
            var parser = new PckParser(fileStream);
            var content = parser.Parse();

            _logger.Info($"Parsed {content.Entries.Count} entries");

            var wemJobs = new List<ConvertJob>();
            var plgJobs = new List<CopyJob>();

            foreach (var entry in content.Entries)
            {
                byte[] fileData = parser.GetFileData(entry);
                if (fileData.Length < 4)
                    continue;

                ReadOnlySpan<byte> magic = fileData.AsSpan(0, 4);

                if (magic.SequenceEqual("BKHD"u8))
                {
                    CollectBnkWems(fileData, entry, tempDir, mapper, wemJobs);
                }
                else if (magic.SequenceEqual("RIFF"u8) || magic.SequenceEqual("RIFX"u8))
                {
                    string key = entry.FileId.ToString();
                    string tempPath = Path.Combine(tempDir, $"{key}.wem");
                    File.WriteAllBytes(tempPath, fileData);

                    WemCodec codec = WemFormatReader.DetectCodec(fileData);
                    string outName = PckExtractor.ResolveOutputName(
                        entry.FileId, mapper, ".wav", content.Languages, entry.LanguageId
                    );
                    wemJobs.Add(new ConvertJob(tempPath, outName, codec));
                }
                else if (magic.SequenceEqual("PLUG"u8))
                {
                    string key = entry.FileId.ToString();
                    string tempPath = Path.Combine(tempDir, $"{key}.plg");
                    File.WriteAllBytes(tempPath, fileData);

                    string outName = PckExtractor.ResolveOutputName(
                        entry.FileId, mapper, ".plg", content.Languages, entry.LanguageId
                    );
                    plgJobs.Add(new CopyJob(tempPath, outName));
                }
            }

            _logger.Info($"Found {wemJobs.Count} WEM, {plgJobs.Count} PLG files");

            var codecGroups = wemJobs.GroupBy(j => j.Codec).OrderByDescending(g => g.Count());
            foreach (var g in codecGroups)
                _logger.Verbose($"  {WemFormatReader.GetCodecName(g.Key)}: {g.Count()}");

            if (wemJobs.Count > 0)
                _logger.Verbose("Converting WEM to WAV...");

            int converted = 0, failed = 0;
            int done = 0;
            int total = wemJobs.Count;
            int lastPercent = -1;

            Parallel.ForEach(wemJobs, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
            },
            job =>
            {
                string finalPath = Path.Combine(outputDir, job.OutputName);
                EnsureDirectory(finalPath);

                try
                {
                    _wemConverter.Convert(job.TempPath, finalPath);
                    Interlocked.Increment(ref converted);
                }
                catch (Exception ex)
                {
                    string wemFallback = Path.ChangeExtension(finalPath, ".wem");
                    EnsureDirectory(wemFallback);
                    try { File.Copy(job.TempPath, wemFallback, overwrite: true); } catch { }

                    _logger.Verbose(
                        $"[{WemFormatReader.GetCodecName(job.Codec)}] " +
                        $"{Path.GetFileName(job.TempPath)}: {ex.Message}");
                    Interlocked.Increment(ref failed);
                }

                int current = Interlocked.Increment(ref done);
                int percent = current * 100 / total;
                if (percent != lastPercent && percent % 10 == 0)
                {
                    lastPercent = percent;
                    _logger.Verbose($"  Progress: {current}/{total} ({percent}%)");
                }
            });

            if (plgJobs.Count > 0)
            {
                _logger.Info("Copying PLG files...");
                foreach (var job in plgJobs)
                {
                    string finalPath = Path.Combine(outputDir, job.OutputName);
                    EnsureDirectory(finalPath);
                    File.Copy(job.TempPath, finalPath, overwrite: true);
                }
            }

            _logger.Info($"Done: {converted} WAV, {failed} failed, {plgJobs.Count} PLG");
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    private static void CollectBnkWems(
        byte[] bnkData,
        PckFileEntry bankEntry,
        string tempDir,
        PckMapper? mapper,
        List<ConvertJob> wemJobs
    )
    {
        var wemEntries = BnkParser.Parse(bnkData);

        foreach (var wem in wemEntries)
        {
            string key = $"{bankEntry.FileId}_{wem.Id}";
            string tempPath = Path.Combine(tempDir, $"{key}.wem");

            using var fs = File.Create(tempPath);
            fs.Write(bnkData, (int)wem.Offset, (int)wem.Size);

            WemCodec codec = WemCodec.Unknown;
            if (wem.Size >= 22)
                codec = WemFormatReader.DetectCodec(bnkData.AsSpan((int)wem.Offset, (int)wem.Size));

            string outName = PckExtractor.ResolveBnkWemName(
                bankEntry.FileId, wem.Id, mapper, ".wav"
            );
            wemJobs.Add(new ConvertJob(tempPath, outName, codec));
        }
    }

    private static void CleanupTempDir(string tempDir)
    {
        if (!Directory.Exists(tempDir))
            return;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(500);
            }
            catch
            {
                break;
            }
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(tempDir))
            {
                try { File.Delete(file); } catch { }
            }
            Directory.Delete(tempDir, recursive: true);
        }
        catch { }
    }

    private static void EnsureDirectory(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (dir != null)
            Directory.CreateDirectory(dir);
    }

    private record ConvertJob(string TempPath, string OutputName, WemCodec Codec);
    private record CopyJob(string TempPath, string OutputName);
}
