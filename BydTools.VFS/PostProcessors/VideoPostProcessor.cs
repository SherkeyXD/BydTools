using BydTools.Utils;
using BydTools.VFS.CriUsm;

namespace BydTools.VFS.PostProcessors;

/// <summary>
/// Post-processor for Video (USM) assets.
/// Demultiplexes CRI USM containers into separate video (.m2v) and audio (.adx/.hca/.aix) streams.
/// </summary>
public sealed class VideoPostProcessor : IPostProcessor
{
    private readonly ILogger _logger;

    public VideoPostProcessor(ILogger logger) => _logger = logger;

    public bool TryProcess(byte[] data, string outputPath)
    {
        if (!outputPath.EndsWith(".usm", StringComparison.OrdinalIgnoreCase))
            return false;

        string basePath = Path.ChangeExtension(outputPath, null);

        try
        {
            var files = CriUsmDemuxer.Demux(data, basePath);
            if (files.Length == 0)
            {
                _logger.Verbose("    USM demux produced no output, falling back to raw write");
                return false;
            }

            foreach (var file in files)
                _logger.Verbose("    Demuxed: {0}", Path.GetFileName(file));

            return true;
        }
        catch (Exception ex)
        {
            _logger.Verbose("    USM demux failed: {0}", ex.Message);
            return false;
        }
    }
}
