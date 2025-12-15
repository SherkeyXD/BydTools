namespace BydTools.PCK;

/// <summary>
/// Provides functionality to extract and convert files from PCK archives.
/// </summary>
public class PckConverter
{
    private readonly PckExtractor _extractor;
    private readonly AudioConverter _audioConverter;

    /// <summary>
    /// Initializes a new instance of the PckConverter class.
    /// </summary>
    public PckConverter()
    {
        _extractor = new PckExtractor();
        _audioConverter = new AudioConverter();
    }

    /// <summary>
    /// Extracts files from PCK and converts audio files to the specified format.
    /// </summary>
    /// <param name="pckPath">Path to the PCK file.</param>
    /// <param name="outputDir">Output directory.</param>
    /// <param name="format">Output format: "wem", "ogg", or "mp3".</param>
    /// <param name="codebooksPath">Path to packed_codebooks.bin (required for WEM to OGG conversion).</param>
    public void ExtractAndConvert(
        string pckPath,
        string outputDir,
        string format = "wem",
        string? codebooksPath = null
    )
    {
        if (!File.Exists(pckPath))
            throw new FileNotFoundException("PCK file not found", pckPath);

        Directory.CreateDirectory(outputDir);

        // First, extract WEM files to a temporary directory
        string tempDir = Path.Combine(outputDir, ".temp");
        Directory.CreateDirectory(tempDir);

        try
        {
            _extractor.ExtractFiles(
                pckPath,
                tempDir,
                extractWem: true,
                extractBnk: false,
                extractPlg: false,
                extractUnknown: false,
                printProgress: false
            );

            // Convert WEM files based on format
            var wemFiles = Directory.GetFiles(tempDir, "*.wem");
            int convertedCount = 0;

            foreach (var wemFile in wemFiles)
            {
                string fileName = Path.GetFileNameWithoutExtension(wemFile);
                string outputFile;

                try
                {
                    switch (format.ToLowerInvariant())
                    {
                        case "wem":
                            // Just copy WEM file
                            outputFile = Path.Combine(outputDir, Path.GetFileName(wemFile));
                            File.Copy(wemFile, outputFile, overwrite: true);
                            convertedCount++;
                            break;

                        case "ogg":
                            outputFile = Path.Combine(outputDir, $"{fileName}.ogg");
                            _audioConverter.ConvertWemToOgg(wemFile, outputFile, codebooksPath);
                            convertedCount++;
                            break;

                        case "mp3":
                            // First convert to OGG, then to MP3
                            string tempOgg = Path.Combine(tempDir, $"{fileName}.ogg");
                            _audioConverter.ConvertWemToOgg(wemFile, tempOgg, codebooksPath);
                            outputFile = Path.Combine(outputDir, $"{fileName}.mp3");
                            _audioConverter.ConvertOggToMp3(tempOgg, outputFile);
                            File.Delete(tempOgg);
                            convertedCount++;
                            break;

                        default:
                            throw new ArgumentException(
                                $"Unsupported format: {format}",
                                nameof(format)
                            );
                    }
                }
                catch (NotImplementedException)
                {
                    // WEM to OGG conversion not implemented, skip
                    Console.WriteLine(
                        $"Warning: Skipping {Path.GetFileName(wemFile)} - WEM to OGG conversion not yet implemented"
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Error converting {Path.GetFileName(wemFile)}: {ex.Message}"
                    );
                }
            }

            Console.WriteLine($"Converted {convertedCount} file(s) to {format} format");
        }
        finally
        {
            // Clean up temporary directory
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
