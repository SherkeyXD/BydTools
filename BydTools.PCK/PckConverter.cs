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
    /// 从 PCK 中提取 BNK/WEM，并转换为指定格式。
    /// 流程：
    /// 1. 先用 PckExtractor 把 PCK 里的 BNK/WEM 原样导出到临时目录；
    /// 2. 对每个 BNK 调用 BnkExtractor.Extractor.ParseBnk 解析出内部的 WEM；
    /// 3. 根据格式转换：WEM -> OGG
    /// </summary>
    /// <param name="pckPath">PCK 文件路径</param>
    /// <param name="outputDir">最终输出目录</param>
    /// <param name="format">输出格式：bnk、wem 或 ogg</param>
    public void ExtractAndConvert(string pckPath, string outputDir, string format = "ogg")
    {
        if (!File.Exists(pckPath))
            throw new FileNotFoundException("PCK file not found", pckPath);

        Directory.CreateDirectory(outputDir);

        // Print input/output info
        if (_logger != null)
        {
            _logger.Info($"Input: {pckPath}");
            _logger.Info($"Output: {outputDir}");
        }

        // 1. 先把 BNK/WEM 从 PCK 提取到临时目录
        string tempDir = Path.Combine(outputDir, ".pck_extract");
        Directory.CreateDirectory(tempDir);

        try
        {
            _extractor.ExtractFiles(
                pckPath,
                tempDir,
                extractWem: true,
                extractBnk: true,
                extractPlg: false,
                extractUnknown: false,
                printProgress: false
            );

            // Count files
            var bnkFiles = Directory.GetFiles(tempDir, "*.bnk", SearchOption.AllDirectories);
            var wemFiles = Directory.GetFiles(tempDir, "*.wem", SearchOption.AllDirectories);

            if (_logger != null)
            {
                _logger.Info($"Found {bnkFiles.Length} BNK files, {wemFiles.Length} WEM files");
            }

            // 如果格式为 bnk，直接提取 BNK 文件
            if (format.ToLowerInvariant() == "bnk")
            {
                if (_logger != null)
                {
                    _logger.Info("Extracting BNK files...");
                }

                int extractedCount = 0;
                foreach (var bnkFile in bnkFiles)
                {
                    string fileName = Path.GetFileName(bnkFile);
                    string finalOutputPath = Path.Combine(outputDir, fileName);
                    File.Copy(bnkFile, finalOutputPath, overwrite: true);
                    extractedCount++;
                }

                if (_logger != null)
                {
                    _logger.Info($"Done, {extractedCount} files extracted.");
                }
                else
                {
                    Console.WriteLine($"Extracted {extractedCount} BNK file(s)");
                }

                return;
            }

            // 2. 解析所有 BNK 为 WEM
            if (_logger != null)
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
                    else
                    {
                        Console.WriteLine(
                            $"Error parsing BNK {Path.GetFileName(bnkFile)}: {ex.Message}"
                        );
                    }
                }
            }

            // Re-count WEM files after BNK parsing
            wemFiles = Directory.GetFiles(tempDir, "*.wem", SearchOption.AllDirectories);

            // 3. 根据格式转换文件
            if (_logger != null)
            {
                if (format.ToLowerInvariant() == "ogg")
                {
                    _logger.Info("Converting WEM to OGG...");
                }
                else
                {
                    _logger.Info("Extracting WEM files...");
                }
            }

            int convertedCount = 0;

            foreach (var wemFile in wemFiles)
            {
                string fileName = Path.GetFileNameWithoutExtension(wemFile);
                try
                {
                    string finalOutputPath;

                    if (format.ToLowerInvariant() == "wem")
                    {
                        // 直接复制 WEM 文件
                        finalOutputPath = Path.Combine(outputDir, $"{fileName}.wem");
                        File.Copy(wemFile, finalOutputPath, overwrite: true);
                        convertedCount++;
                    }
                    else
                    {
                        // 先转换为 OGG
                        string sourceOgg;
                        try
                        {
                            sourceOgg = Extractor.ConvertWem(wemFile);
                        }
                        catch (BnkExtractor.Ww2ogg.Exceptions.ParseException ex)
                        {
                            // WEM 文件不是标准的 Wwise RIFF Vorbis 格式，无法转换
                            if (_logger != null)
                            {
                                _logger.Verbose(
                                    $"Skipping WEM {Path.GetFileName(wemFile)}: {ex.Message} (file may not be in Wwise RIFF Vorbis format)"
                                );
                            }
                            else
                            {
                                Console.WriteLine(
                                    $"Skipping WEM {Path.GetFileName(wemFile)}: {ex.Message} (file may not be in Wwise RIFF Vorbis format)"
                                );
                            }
                            continue;
                        }

                        // 验证源 OGG 文件是否存在
                        if (!File.Exists(sourceOgg))
                        {
                            throw new FileNotFoundException(
                                $"OGG file was not created: {sourceOgg}"
                            );
                        }

                        // 强制垃圾回收以确保文件句柄被释放
                        GC.Collect();
                        GC.WaitForPendingFinalizers();

                        // 转换为 OGG 格式
                        finalOutputPath = Path.Combine(outputDir, $"{fileName}.ogg");
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
                        catch (Exception revorbEx)
                        {
                            // Revorb 失败，输出警告但继续使用原始 OGG
                            if (_logger != null)
                            {
                                _logger.Verbose(
                                    $"Warning: Failed to revorb OGG for {Path.GetFileName(wemFile)}: {revorbEx.Message}. Using unrevorbed OGG file."
                                );
                            }
                            else
                            {
                                Console.WriteLine(
                                    $"Warning: Failed to revorb OGG for {Path.GetFileName(wemFile)}: {revorbEx.Message}. Using unrevorbed OGG file."
                                );
                            }
                            File.Copy(sourceOgg, finalOutputPath, overwrite: true);
                        }
                        convertedCount++;
                    }
                }
                catch (Exception ex)
                {
                    if (_logger != null)
                    {
                        _logger.Verbose(
                            $"Error converting WEM {Path.GetFileName(wemFile)}: {ex.Message}"
                        );
                    }
                    else
                    {
                        Console.WriteLine(
                            $"Error converting WEM {Path.GetFileName(wemFile)}: {ex.Message}"
                        );
                    }
                }
            }

            // Print completion message
            if (_logger != null)
            {
                _logger.Info($"Done, {convertedCount} files extracted.");
            }
            else
            {
                Console.WriteLine(
                    $"Converted {convertedCount} BNK/WEM file(s) to {format.ToUpperInvariant()}"
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
