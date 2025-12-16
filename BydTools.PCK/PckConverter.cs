using BnkExtractor;
using BnkExtractor.Ww2ogg.Exceptions;

namespace BydTools.PCK;

/// <summary>
/// Provides functionality to extract audio-related files from PCK archives
/// and convert BNK/WEM contents to OGG using BnkExtractor.
/// </summary>
public class PckConverter
{
    private readonly PckExtractor _extractor;

    /// <summary>
    /// Initializes a new instance of the PckConverter class.
    /// </summary>
    public PckConverter()
    {
        _extractor = new PckExtractor();
    }

    /// <summary>
    /// 从 PCK 中提取 BNK/WEM，并转换为指定格式。
    /// 流程：
    /// 1. 先用 PckExtractor 把 PCK 里的 BNK/WEM 原样导出到临时目录；
    /// 2. 对每个 BNK 调用 BnkExtractor.Extractor.ParseBnk 解析出内部的 WEM；
    /// 3. 根据格式转换：WEM -> OGG -> (可选) MP3
    /// </summary>
    /// <param name="pckPath">PCK 文件路径</param>
    /// <param name="outputDir">最终输出目录</param>
    /// <param name="format">输出格式：wem, ogg, 或 mp3</param>
    public void ExtractAndConvert(string pckPath, string outputDir, string format = "ogg")
    {
        if (!File.Exists(pckPath))
            throw new FileNotFoundException("PCK file not found", pckPath);

        Directory.CreateDirectory(outputDir);

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

            // 2. 解析所有 BNK 为 WEM
            var bnkFiles = Directory.GetFiles(tempDir, "*.bnk", SearchOption.AllDirectories);
            foreach (var bnkFile in bnkFiles)
            {
                try
                {
                    Extractor.ParseBnk(bnkFile);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Error parsing BNK {Path.GetFileName(bnkFile)}: {ex.Message}"
                    );
                }
            }

            // 3. 根据格式转换文件
            var wemFiles = Directory.GetFiles(tempDir, "*.wem", SearchOption.AllDirectories);
            int convertedCount = 0;
            var audioConverter = new AudioConverter();

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
                            Console.WriteLine(
                                $"Skipping WEM {Path.GetFileName(wemFile)}: {ex.Message} (file may not be in Wwise RIFF Vorbis format)"
                            );
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

                        // 根据目标格式处理
                        if (format.ToLowerInvariant() == "mp3")
                        {
                            // OGG -> MP3
                            finalOutputPath = Path.Combine(outputDir, $"{fileName}.mp3");

                            // 先 revorb OGG（如果可能）
                            string revorbedOgg = Path.Combine(tempDir, $"{fileName}_revorb.ogg");
                            try
                            {
                                Extractor.RevorbOgg(sourceOgg, revorbedOgg);
                                audioConverter.ConvertOggToMp3(revorbedOgg, finalOutputPath);
                            }
                            catch
                            {
                                // Revorb 失败，直接使用原始 OGG
                                audioConverter.ConvertOggToMp3(sourceOgg, finalOutputPath);
                            }
                        }
                        else // format == "ogg"
                        {
                            // 尝试 revorb，如果失败则直接使用原始 OGG
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
                                Console.WriteLine(
                                    $"Warning: Failed to revorb OGG for {Path.GetFileName(wemFile)}: {revorbEx.Message}. Using unrevorbed OGG file."
                                );
                                File.Copy(sourceOgg, finalOutputPath, overwrite: true);
                            }
                        }
                        convertedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Error converting WEM {Path.GetFileName(wemFile)}: {ex.Message}"
                    );
                }
            }

            Console.WriteLine(
                $"Converted {convertedCount} BNK/WEM file(s) to {format.ToUpperInvariant()}"
            );
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
