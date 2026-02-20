using BydTools.Utils;
using BydTools.Wwise;

namespace BydTools.PCK;

/// <summary>
/// Extracts audio from PCK archives and converts WEM → OGG,
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
    ///   <item><b>ogg</b> — convert WEM (including BNK-embedded) to OGG; copy PLG as-is.</item>
    /// </list>
    /// </summary>
    public void ExtractAndConvert(
        string pckPath,
        string outputDir,
        string mode = "ogg",
        PckMapper? mapper = null
    )
    {
        if (!File.Exists(pckPath))
            throw new FileNotFoundException("PCK file not found", pckPath);

        Directory.CreateDirectory(outputDir);

        string normalizedMode = mode.ToLowerInvariant();
        if (normalizedMode is not ("raw" or "ogg"))
            throw new ArgumentException($"Invalid mode '{mode}'. Must be 'raw' or 'ogg'.", nameof(mode));

        _logger.Info($"Input:  {pckPath}");
        _logger.Info($"Output: {outputDir}");
        _logger.Info($"Mode:   {normalizedMode}");

        if (normalizedMode == "raw")
        {
            _extractor.ExtractFiles(pckPath, outputDir, mapper);
            return;
        }

        ConvertToOgg(pckPath, outputDir, mapper);
    }

    private void ConvertToOgg(string pckPath, string outputDir, PckMapper? mapper)
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

                    string outName = PckExtractor.ResolveOutputName(
                        entry.FileId, mapper, ".ogg", content.Languages, entry.LanguageId
                    );
                    wemJobs.Add(new ConvertJob(tempPath, outName));
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

            if (wemJobs.Count > 0)
                _logger.Info("Converting WEM to OGG...");

            int convertedCount = 0;
            int failedCount = 0;

            foreach (var job in wemJobs)
            {
                string finalPath = Path.Combine(outputDir, job.OutputName);
                EnsureDirectory(finalPath);

                try
                {
                    string sourceOgg = _wemConverter.ConvertWem(job.TempPath);

                    try
                    {
                        _wemConverter.RevorbOgg(sourceOgg, finalPath);

                        if (!File.Exists(finalPath) || new FileInfo(finalPath).Length == 0)
                            throw new InvalidOperationException("Revorb output missing or empty");
                    }
                    catch
                    {
                        File.Copy(sourceOgg, finalPath, overwrite: true);
                    }

                    convertedCount++;
                }
                catch (BydTools.Wwise.Ww2ogg.Exceptions.ParseException)
                {
                    string wemFallback = Path.ChangeExtension(finalPath, ".wem");
                    EnsureDirectory(wemFallback);
                    File.Copy(job.TempPath, wemFallback, overwrite: true);
                    failedCount++;
                }
                catch (Exception ex)
                {
                    _logger.Verbose($"Error converting {Path.GetFileName(job.TempPath)}: {ex.Message}");
                    failedCount++;
                }
            }

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

            _logger.Info(
                $"Done: {convertedCount} OGG, {failedCount} failed (kept as WEM), {plgJobs.Count} PLG"
            );
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
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

            string outName = PckExtractor.ResolveBnkWemName(
                bankEntry.FileId, wem.Id, mapper, ".ogg"
            );
            wemJobs.Add(new ConvertJob(tempPath, outName));
        }
    }

    private static void EnsureDirectory(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (dir != null)
            Directory.CreateDirectory(dir);
    }

    private record ConvertJob(string TempPath, string OutputName);
    private record CopyJob(string TempPath, string OutputName);
}
