using BydTools.Utils;
using BydTools.VFS.SparkBuffer;

namespace BydTools.VFS.PostProcessors;

/// <summary>
/// Decrypts SparkBuffer .bytes files to JSON.
/// </summary>
public sealed class SparkBufferPostProcessor : IPostProcessor
{
    private readonly ILogger _logger;

    public SparkBufferPostProcessor(ILogger logger)
    {
        _logger = logger;
    }

    public bool TryProcess(byte[] data, string outputPath)
    {
        if (!Path.GetExtension(outputPath).Equals(".bytes", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var decryptedJson = SparkBufferDumper.Decrypt(data);
            if (!string.IsNullOrEmpty(decryptedJson))
            {
                var jsonFilePath = Path.ChangeExtension(outputPath, ".json");
                File.WriteAllText(jsonFilePath, decryptedJson);
                _logger.Verbose(
                    $"  Decrypted SparkBuffer: {Path.GetFileName(outputPath)} -> {Path.GetFileName(jsonFilePath)}"
                );
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.Verbose(
                $"  SparkBuffer decryption failed for {Path.GetFileName(outputPath)}: {ex.Message}"
            );
        }

        return false;
    }
}
