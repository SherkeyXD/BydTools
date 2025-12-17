using BnkExtractor;

namespace BydTools.PCK;

/// <summary>
/// Provides functionality to extract audio-related files from PCK archives
/// and convert BNK/WEM contents to OGG using BnkExtractor.
/// </summary>
public class PckConverter
{
    private readonly PckExtractor _extractor;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the PckConverter class.
    /// </summary>
    /// <param name="logger">Optional logger for output. If null, uses Console directly.</param>
    public PckConverter(ILogger? logger = null)
    {
        _logger = logger;
        _extractor = new PckExtractor(_logger);
    }

    /// <summary>
    /// 从 PCK 中提取音频文件。
    /// 流程：
    /// - raw 模式：直接提取 wem/bnk/plg，不做任何转换
    /// - ogg 模式：提取所有文件，将 wem 和从 bnk 解析出的 wem 转换为 ogg（如果失败则保留原格式），plg 直接保存
    /// </summary>
    /// <param name="pckPath">PCK 文件路径</param>
    /// <param name="outputDir">最终输出目录</param>
    /// <param name="mode">提取模式：raw 或 ogg（默认）</param>
    public void ExtractAndConvert(string pckPath, string outputDir, string mode = "ogg")
    {
        if (!File.Exists(pckPath))
            throw new FileNotFoundException("PCK file not found", pckPath);

        Directory.CreateDirectory(outputDir);

        // Print input/output info
        if (_logger != null)
        {
            _logger.Info($"Input: {pckPath}");
            _logger.Info($"Output: {outputDir}");
            _logger.Info($"Mode: {mode}");
        }

        string normalizedMode = mode.ToLowerInvariant();
        if (normalizedMode != "raw" && normalizedMode != "ogg")
        {
            throw new ArgumentException(
                $"Invalid mode: {mode}. Must be 'raw' or 'ogg'",
                nameof(mode)
            );
        }

        // raw 模式：直接提取 wem/bnk/plg
        if (normalizedMode == "raw")
        {
            if (_logger != null)
            {
                _logger.Info("Extracting files in raw mode...");
            }

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

        // ogg 模式：需要转换
        string tempDir = Path.Combine(outputDir, ".pck_extract");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. 先把所有文件从 PCK 提取到临时目录
            _extractor.ExtractFiles(
                pckPath,
                tempDir,
                extractWem: true,
                extractBnk: true,
                extractPlg: true,
                extractUnknown: false
            );

            // Count files
            var bnkFiles = Directory.GetFiles(tempDir, "*.bnk", SearchOption.AllDirectories);
            var wemFiles = Directory.GetFiles(tempDir, "*.wem", SearchOption.AllDirectories);
            var plgFiles = Directory.GetFiles(tempDir, "*.plg", SearchOption.AllDirectories);

            if (_logger != null)
            {
                _logger.Info(
                    $"Found {bnkFiles.Length} BNK, {wemFiles.Length} WEM, {plgFiles.Length} PLG files"
                );
            }

            // 2. 解析所有 BNK 为 WEM
            if (_logger != null && bnkFiles.Length > 0)
            {
                _logger.Info("Processing BNK files...");
            }

            foreach (var bnkFile in bnkFiles)
            {
                try
                {
                    Extractor.ParseBnk(bnkFile);
                }
                catch (Exception ex)
                {
                    if (_logger != null)
                    {
                        _logger.Verbose(
                            $"Error parsing BNK {Path.GetFileName(bnkFile)}: {ex.Message}"
                        );
                    }
                }
            }

            // 重新统计 WEM 文件（包括从 BNK 解析出来的）
            wemFiles = Directory.GetFiles(tempDir, "*.wem", SearchOption.AllDirectories);

            // 3. 处理 WEM 文件：尝试转换为 OGG
            if (_logger != null && wemFiles.Length > 0)
            {
                _logger.Info("Converting WEM to OGG...");
            }

            int convertedCount = 0;
            int failedCount = 0;

            foreach (var wemFile in wemFiles)
            {
                string fileName = Path.GetFileNameWithoutExtension(wemFile);
                try
                {
                    // 尝试转换为 OGG
                    string sourceOgg;
                    try
                    {
                        sourceOgg = Extractor.ConvertWem(wemFile);
                    }
                    catch (BnkExtractor.Ww2ogg.Exceptions.ParseException)
                    {
                        // WEM 文件无法转换，保留原格式
                        string finalWemPath = Path.Combine(outputDir, $"{fileName}.wem");
                        File.Copy(wemFile, finalWemPath, overwrite: true);
                        failedCount++;
                        continue;
                    }

                    // 验证源 OGG 文件是否存在
                    if (!File.Exists(sourceOgg))
                    {
                        // 转换失败，保留原 WEM
                        string finalWemPath = Path.Combine(outputDir, $"{fileName}.wem");
                        File.Copy(wemFile, finalWemPath, overwrite: true);
                        failedCount++;
                        continue;
                    }

                    // 强制垃圾回收以确保文件句柄被释放
                    GC.Collect();
                    GC.WaitForPendingFinalizers();

                    // 尝试 revorb OGG 格式
                    string finalOutputPath = Path.Combine(outputDir, $"{fileName}.ogg");
                    try
                    {
                        Extractor.RevorbOgg(sourceOgg, finalOutputPath);

                        // 验证 revorb 后的文件是否存在且有效
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
                        // Revorb 失败，使用原始 OGG
                        File.Copy(sourceOgg, finalOutputPath, overwrite: true);
                    }
                    convertedCount++;
                }
                catch (Exception ex)
                {
                    if (_logger != null)
                    {
                        _logger.Verbose(
                            $"Error processing WEM {Path.GetFileName(wemFile)}: {ex.Message}"
                        );
                    }
                    failedCount++;
                }
            }

            // 4. 直接复制 PLG 文件
            if (plgFiles.Length > 0)
            {
                if (_logger != null)
                {
                    _logger.Info("Copying PLG files...");
                }

                foreach (var plgFile in plgFiles)
                {
                    string fileName = Path.GetFileName(plgFile);
                    string finalOutputPath = Path.Combine(outputDir, fileName);
                    File.Copy(plgFile, finalOutputPath, overwrite: true);
                }
            }

            // Print completion message
            if (_logger != null)
            {
                _logger.Info(
                    $"Done, {convertedCount} OGG files, {failedCount} WEM files (failed to convert), {plgFiles.Length} PLG files."
                );
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
