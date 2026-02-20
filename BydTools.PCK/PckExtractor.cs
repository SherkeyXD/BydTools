using BydTools.Utils;

namespace BydTools.PCK;

/// <summary>
/// Provides functionality to extract files from PCK archives.
/// </summary>
public class PckExtractor
{
    private readonly ILogger _logger;

    public PckExtractor(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extracts files from a PCK archive.
    /// </summary>
    public void ExtractFiles(
        string pckPath,
        string outputDir,
        bool extractWem = true,
        bool extractBnk = true,
        bool extractPlg = true,
        bool extractUnknown = true
    )
    {
        if (!File.Exists(pckPath))
            throw new FileNotFoundException("PCK file not found", pckPath);

        Directory.CreateDirectory(outputDir);

        using var fileStream = File.OpenRead(pckPath);
        var parser = new PckParser(fileStream);

        var entries = parser.GetEntries();
        int savedCount = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

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
    }
}
