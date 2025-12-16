namespace BydTools.PCK;

/// <summary>
/// Provides functionality to extract files from PCK archives.
/// </summary>
public class PckExtractor
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the PckExtractor class.
    /// </summary>
    /// <param name="logger">Optional logger for output. If null, uses Console directly.</param>
    public PckExtractor(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extracts files from a PCK archive.
    /// </summary>
    /// <param name="pckPath">Path to the PCK file.</param>
    /// <param name="outputDir">Output directory.</param>
    /// <param name="extractWem">Whether to extract WEM files.</param>
    /// <param name="extractBnk">Whether to extract BNK files.</param>
    /// <param name="extractPlg">Whether to extract PLG files.</param>
    /// <param name="extractUnknown">Whether to extract unknown files.</param>
    /// <param name="printProgress">Whether to print progress.</param>
    public void ExtractFiles(
        string pckPath,
        string outputDir,
        bool extractWem = true,
        bool extractBnk = true,
        bool extractPlg = true,
        bool extractUnknown = true,
        bool printProgress = true
    )
    {
        if (!File.Exists(pckPath))
            throw new FileNotFoundException("PCK file not found", pckPath);

        Directory.CreateDirectory(outputDir);

        using var fileStream = File.OpenRead(pckPath);
        var parser = new PckParser(fileStream);

        if (printProgress && _logger != null)
        {
            _logger.VerboseNoNewline("(0.0%) Reading entries");
        }
        else if (printProgress)
        {
            Console.Write("(0.0%) Reading entries");
        }

        var entries = parser.GetEntries();
        int savedCount = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (printProgress && _logger != null)
            {
                _logger.VerboseNoNewline(
                    $"\r({(i + 1) * 100.0 / entries.Count:F1}%) [{i + 1}/{entries.Count}] Processing entry ID {entry.FileId}"
                );
            }
            else if (printProgress)
            {
                Console.Write(
                    $"\r({(i + 1) * 100.0 / entries.Count:F1}%) [{i + 1}/{entries.Count}] Processing entry ID {entry.FileId}"
                );
            }

            byte[] fileData = parser.GetFile(entry);
            if (fileData.Length < 4)
                continue;

            ReadOnlySpan<byte> magicBytes = fileData.AsSpan(0, 4);

            string? outputName = null;
            if (magicBytes.SequenceEqual("RIFF"u8))
            {
                if (extractWem)
                    outputName = $"{entry.FileId}.wem";
            }
            else if (magicBytes.SequenceEqual("BKHD"u8))
            {
                if (extractBnk)
                    outputName = $"{entry.FileId}.bnk";
            }
            else if (magicBytes.SequenceEqual("PLUG"u8))
            {
                if (extractPlg)
                    outputName = $"{entry.FileId}.plg";
            }
            else
            {
                if (extractUnknown)
                    outputName = $"{entry.FileId}.unknown";
            }

            if (outputName != null)
            {
                string outputPath = Path.Combine(outputDir, outputName);
                File.WriteAllBytes(outputPath, fileData);
                savedCount++;
            }
        }

        if (printProgress && _logger != null)
        {
            _logger.Verbose($"\nExtracted {savedCount} files to {Path.GetFullPath(outputDir)}");
        }
        else if (printProgress)
        {
            Console.WriteLine($"\nExtracted {savedCount} files to {Path.GetFullPath(outputDir)}");
        }
    }
}
