using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BnkExtractor.Ww2ogg;

namespace BnkExtractor
{
    /// <summary>
    /// 提供 BNK/WEM -> OGG 的精简转换入口。
    /// </summary>
    public static class Extractor
    {
        static Extractor()
        {
            // 确保原生 DLL 能被找到：将应用程序目录和子目录添加到 DLL 搜索路径
            string appDir =
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                ?? AppContext.BaseDirectory;
            if (Directory.Exists(appDir))
            {
                // 在 Windows 上，SetDllDirectory 可以添加目录到 DLL 搜索路径
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        SetDllDirectory(appDir);

                        // 也检查 3rdParty 子目录（如果存在）
                        string thirdPartyDir = Path.Combine(appDir, "3rdParty");
                        if (Directory.Exists(thirdPartyDir))
                        {
                            // 将 3rdParty 目录也添加到搜索路径
                            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                            if (!currentPath.Contains(thirdPartyDir))
                            {
                                Environment.SetEnvironmentVariable(
                                    "PATH",
                                    $"{thirdPartyDir};{currentPath}",
                                    EnvironmentVariableTarget.Process
                                );
                            }
                        }

                        // 确保 PATH 包含应用程序目录，以便原生 DLL 能被找到
                        string currentPath2 = Environment.GetEnvironmentVariable("PATH") ?? "";
                        if (!currentPath2.Contains(appDir))
                        {
                            Environment.SetEnvironmentVariable(
                                "PATH",
                                $"{appDir};{currentPath2}",
                                EnvironmentVariableTarget.Process
                            );
                        }
                    }
                    catch
                    {
                        // 如果 SetDllDirectory 失败，DLL 应该仍然能从应用程序目录加载
                    }
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        /// <summary>
        /// 解析 BNK 文件并把内部的 WEM 文件解包出来。
        /// </summary>
        public static void ParseBnk(string filePath) =>
            BnkExtr.BnkParser.Parse(
                filePath,
                swapByteOrder: false,
                noDirectory: false,
                dumpObjectsTxt: false
            );

        /// <summary>
        /// 对 OGG 文件执行 revorb 以修正 granule 信息。
        /// </summary>
        public static void RevorbOgg(string inputFilePath, string? outputFilePath = null) =>
            Revorb.RevorbSharp.Convert(
                inputFilePath,
                outputFilePath ?? Path.ChangeExtension(inputFilePath, ".revorb.ogg")
            );

        /// <summary>
        /// 使用内置的 ww2ogg 逻辑将单个 WEM 转为 OGG。
        /// Codebooks 使用嵌入资源自动落地到临时文件。
        /// </summary>
        public static string ConvertWem(string wemPath)
        {
            if (!File.Exists(wemPath))
                throw new FileNotFoundException("WEM file not found", wemPath);

            string oggPath = Path.ChangeExtension(wemPath, "ogg");

            // 将嵌入的 codebooks 资源写到临时目录
            string tempDir = Path.Combine(Path.GetDirectoryName(wemPath) ?? ".", ".ww2ogg");
            Directory.CreateDirectory(tempDir);

            string codebooksPath = Path.Combine(tempDir, "packed_codebooks_aoTuV_603.bin");

            if (!File.Exists(codebooksPath))
            {
                Assembly assembly = Assembly.GetExecutingAssembly();

                // 尝试多个可能的资源名称（不同命名空间和路径）
                string[] possibleNames = new[]
                {
                    "BydTools.PCK.Resource.packed_codebooks_aoTuV_603.bin",
                    "BydTools.PCK.Resource.packed_codebooks.bin",
                    "Resource.packed_codebooks_aoTuV_603.bin",
                    "Resource.packed_codebooks.bin",
                    "BydTools.PCK.BnkExtractor.Ww2ogg.Codebooks.packed_codebooks_aoTuV_603.bin",
                    "BnkExtractor.Ww2ogg.Codebooks.packed_codebooks_aoTuV_603.bin",
                    "BydTools.PCK.BnkExtractor.Ww2ogg.Codebooks.packed_codebooks.bin",
                    "BnkExtractor.Ww2ogg.Codebooks.packed_codebooks.bin",
                };

                Stream? resourceStream = null;
                foreach (string name in possibleNames)
                {
                    resourceStream = assembly.GetManifestResourceStream(name);
                    if (resourceStream != null)
                        break;
                }

                // 如果还是找不到，尝试枚举所有资源名称
                if (resourceStream == null)
                {
                    string[] allResources = assembly.GetManifestResourceNames();
                    string? foundName = allResources.FirstOrDefault(n =>
                        n.Contains("packed_codebooks") && n.EndsWith(".bin")
                    );

                    if (foundName != null)
                        resourceStream = assembly.GetManifestResourceStream(foundName);
                }

                if (resourceStream == null)
                    throw new FileNotFoundException(
                        "Embedded ww2ogg codebooks resource not found."
                    );

                using FileStream fs = File.Create(codebooksPath);
                resourceStream.CopyTo(fs);
                resourceStream.Dispose();
            }

            Ww2oggOptions options = new()
            {
                InFilename = wemPath,
                OutFilename = oggPath,
                CodebooksFilename = codebooksPath,
            };

            Ww2oggConverter.Main(options);

            // 验证文件是否成功创建
            if (!File.Exists(oggPath))
            {
                throw new FileNotFoundException($"OGG file was not created: {oggPath}");
            }

            return oggPath;
        }
    }
}
