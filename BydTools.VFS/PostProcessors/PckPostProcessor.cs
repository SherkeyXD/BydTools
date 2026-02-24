using BydTools.PCK;
using BydTools.Utils;

namespace BydTools.VFS.PostProcessors;

/// <summary>
/// Decrypts VFS-encrypted .pck payloads to plain AKPK files.
/// </summary>
public sealed class PckPostProcessor : IPostProcessor
{
    private readonly ILogger _logger;

    public PckPostProcessor(ILogger logger)
    {
        _logger = logger;
    }

    public bool TryProcess(byte[] data, string outputPath)
    {
        if (!outputPath.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var ms = new MemoryStream(data, writable: false);
            var parser = new PckParser(ms);
            byte[] decrypted = parser.GetDecryptedPckBytes();

            File.WriteAllBytes(outputPath, decrypted);
            if (parser.IsVfsEncrypted)
                _logger.Verbose("  Decrypted PCK: {0}", Path.GetFileName(outputPath));

            return true;
        }
        catch (Exception ex)
        {
            _logger.Verbose(
                "  PCK decryption failed for {0}: {1}",
                Path.GetFileName(outputPath),
                ex.Message
            );
            return false;
        }
    }
}
