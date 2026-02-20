using BnkExtractor;
using BydTools.Utils;

namespace BydTools.PCK;

/// <summary>
/// Provides functionality to extract audio-related files from PCK archives
/// and convert BNK/WEM contents to OGG using BnkExtractor.
/// </summary>
public class PckConverter
{
    private readonly PckExtractor _extractor;
    private readonly ILogger _logger;

    public PckConverter(ILogger logger)
    {
        _logger = logger;
        _extractor = new PckExtractor(logger);
    }

    /// <summary>
    /// 从 PCK 中提取音频文件。
    /// 流程：
    /// - raw 模式：直接提取 wem/bnk/plg，不做任何转换
    /// - ogg 模式：提取所有文件，将 wem 和从 bnk 解析出的 wem 转换为 ogg（如果失败则保留原格式），plg 直接保存
    /// </summary>
    public void ExtractAndConvert(string pckPath, string outputDir, string mode = "ogg")
    {
        if (!File.Exists(pckPath))
            throw new FileNotFoundException("PCK file not found", pckPath);

        Directory.CreateDirectory(outputDir);

        _logger.Info($"Input: {pckPath}");
        _logger.Info($"Output: {outputDir}");
        _logger.Info($"Mode: {mode}");

        string normalizedMode = mode.ToLowerInvariant();
        if (normalizedMode != "raw" && normalizedMode != "ogg")
        {
            throw new ArgumentException(
                $"Invalid mode: {mode}. Must be 'raw' or 'ogg'",
                nameof(mode)
            );
        }

        if (normalizedMode == "raw")
        {
            _logger.Info("Extracting files in raw mode...");
            _extractor.ExtractFiles(
                pckPath,
                outputDir,
                extractWem: true,
                extractBnk: true,
                extractPlg: true,
                extractUnknown: false
            );
            return;
        }

        string tempDir = Path.Combine(outputDir, ".pck_extract");
        Directory.CreateDirectory(tempDir);

        try
        {
            _extractor.ExtractFiles(
                pckPath,
                tempDir,
                extractWem: true,
                extractBnk: true,
                extractPlg: true,
                extractUnknown: false
            );

            var bnkFiles = Directory.GetFiles(tempDir, "*.bnk", SearchOption.AllDirectories);
            var wemFiles = Directory.GetFiles(tempDir, "*.wem", SearchOption.AllDirectories);
            var plgFiles = Directory.GetFiles(tempDir, "*.plg", SearchOption.AllDirectories);

            _logger.Info(
                $"Found {bnkFiles.Length} BNK, {wemFiles.Length} WEM, {plgFiles.Length} PLG files"
            );

            if (bnkFiles.Length > 0)
                _logger.Info("Processing BNK files...");

            foreach (var bnkFile in bnkFiles)
            {
                try
                {
                    Extractor.ParseBnk(bnkFile);
                }
                catch (Exception ex)
                {
                    _logger.Verbose(
                        $"Error parsing BNK {Path.GetFileName(bnkFile)}: {ex.Message}"
                    );
                }
            }

            // Re-scan WEM files (includes those extracted from BNK)
            wemFiles = Directory.GetFiles(tempDir, "*.wem", SearchOption.AllDirectories);

            if (wemFiles.Length > 0)
                _logger.Info("Converting WEM to OGG...");

            int convertedCount = 0;
            int failedCount = 0;

            foreach (var wemFile in wemFiles)
            {
                string fileName = Path.GetFileNameWithoutExtension(wemFile);
                try
                {
                    string sourceOgg;
                    try
                    {
                        sourceOgg = Extractor.ConvertWem(wemFile);
                    }
                    catch (BnkExtractor.Ww2ogg.Exceptions.ParseException)
                    {
                        string finalWemPath = Path.Combine(outputDir, $"{fileName}.wem");
                        File.Copy(wemFile, finalWemPath, overwrite: true);
                        failedCount++;
                        continue;
                    }

                    if (!File.Exists(sourceOgg))
                    {
                        string finalWemPath = Path.Combine(outputDir, $"{fileName}.wem");
                        File.Copy(wemFile, finalWemPath, overwrite: true);
                        failedCount++;
                        continue;
                    }

                    GC.Collect();
                    GC.WaitForPendingFinalizers();

                    string finalOutputPath = Path.Combine(outputDir, $"{fileName}.ogg");
                    try
                    {
                        Extractor.RevorbOgg(sourceOgg, finalOutputPath);

                        if (
                            !File.Exists(finalOutputPath)
                            || new FileInfo(finalOutputPath).Length == 0
                        )
                        {
                            throw new InvalidOperationException(
                                "Revorb failed: output file is missing or empty"
                            );
                        }
                    }
                    catch
                    {
                        File.Copy(sourceOgg, finalOutputPath, overwrite: true);
                    }
                    convertedCount++;
                }
                catch (Exception ex)
                {
                    _logger.Verbose(
                        $"Error processing WEM {Path.GetFileName(wemFile)}: {ex.Message}"
                    );
                    failedCount++;
                }
            }

            if (plgFiles.Length > 0)
            {
                _logger.Info("Copying PLG files...");

                foreach (var plgFile in plgFiles)
                {
                    string fileName = Path.GetFileName(plgFile);
                    string finalOutputPath = Path.Combine(outputDir, fileName);
                    File.Copy(plgFile, finalOutputPath, overwrite: true);
                }
            }

            _logger.Info(
                $"Done, {convertedCount} OGG files, {failedCount} WEM files (failed to convert), {plgFiles.Length} PLG files."
            );
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
