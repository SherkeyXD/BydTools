using System.Reflection;
using BydTools.VFS.Crypto;
using BydTools.VFS.Extensions;

namespace BydTools.VFS;

internal class VFSDump
{
    /// <summary>
    /// Maps EVFSBlockType to groupCfgName as used in BLC files.
    /// </summary>
    static readonly Dictionary<EVFSBlockType, string> blockTypeNameMap = new()
    {
        { EVFSBlockType.InitialAudio, "InitAudio" },
        { EVFSBlockType.InitialBundle, "InitBundle" },
        { EVFSBlockType.BundleManifest, "BundleManifest" },
        { EVFSBlockType.LowShader, "LowShader" },
        { EVFSBlockType.InitialExtendData, "InitialExtendData" },
        { EVFSBlockType.Audio, "Audio" },
        { EVFSBlockType.Bundle, "Bundle" },
        { EVFSBlockType.DynamicStreaming, "DynamicStreaming" },
        { EVFSBlockType.Table, "Table" },
        { EVFSBlockType.Video, "Video" },
        { EVFSBlockType.IV, "IV" },
        { EVFSBlockType.Streaming, "Streaming" },
        { EVFSBlockType.JsonData, "JsonData" },
        { EVFSBlockType.Lua, "Lua" },
        { EVFSBlockType.IFixPatch, "IFixPatchOut" },
        { EVFSBlockType.ExtendData, "ExtendData" },
        { EVFSBlockType.AudioChinese, "AudioChinese" },
        { EVFSBlockType.AudioEnglish, "AudioEnglish" },
        { EVFSBlockType.AudioJapanese, "AudioJapanese" },
        { EVFSBlockType.AudioKorean, "AudioKorean" },
    };

    /// <summary>
    /// Maps EVFSBlockType to groupCfgHashName (directory name).
    /// These are pre-computed hash values as found in the game's VFS system.
    /// </summary>
    static readonly Dictionary<EVFSBlockType, string> blockHashMap = new()
    {
        { EVFSBlockType.InitialAudio, "07A1BB91" },
        { EVFSBlockType.InitialBundle, "0CE8FA57" },
        { EVFSBlockType.BundleManifest, "1CDDBF1F" },
        { EVFSBlockType.InitialExtendData, "3C9D9D2D" },
        { EVFSBlockType.Audio, "24ED34CF" },
        { EVFSBlockType.Bundle, "7064D8E2" },
        { EVFSBlockType.DynamicStreaming, "23D53F5D" },
        { EVFSBlockType.Table, "42A8FCA6" },
        { EVFSBlockType.Video, "55FC21C6" },
        { EVFSBlockType.IV, "A63D7E6A" },
        { EVFSBlockType.Streaming, "C3442D43" },
        { EVFSBlockType.JsonData, "775A31D1" },
        { EVFSBlockType.Lua, "19E3AE45" },
        { EVFSBlockType.IFixPatch, "DAFE52C9" },
        { EVFSBlockType.ExtendData, "D6E622F7" },
        { EVFSBlockType.AudioChinese, "E1E7D7CE" },
        { EVFSBlockType.AudioEnglish, "A31457D0" },
        { EVFSBlockType.AudioJapanese, "F668D4EE" },
        { EVFSBlockType.AudioKorean, "E9D31017" },
    };

    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return;
        }

        // Manual command line parsing to support:
        // BydTools.VFS.exe --gamepath "xxx" --blocktype "xxx" --output "xxx"
        string? gamePath = null;
        string? blockTypeString = null;
        string? outputDir = null;
        bool showHelp = false;
        bool showVersion = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;

                case "-v":
                case "--version":
                    showVersion = true;
                    break;

                case "--gamepath":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Error: --gamepath requires a value.");
                        return;
                    }
                    gamePath = args[++i];
                    break;

                case "--blocktype":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Error: --blocktype requires a value.");
                        return;
                    }
                    blockTypeString = args[++i];
                    break;

                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Error: --output requires a value.");
                        return;
                    }
                    outputDir = args[++i];
                    break;

                default:
                    Console.Error.WriteLine("Unknown argument: {0}", arg);
                    PrintHelp();
                    return;
            }
        }

        if (showVersion)
        {
            PrintVersion();
            if (!showHelp)
                return;
        }

        if (showHelp)
        {
            PrintHelp();
            return;
        }

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            Console.Error.WriteLine("Error: --gamepath is required.");
            PrintHelp();
            return;
        }

        // Default output directory
        outputDir ??= Path.Combine(AppContext.BaseDirectory, "Assets");

        // Parse blocktype, support name or numeric value, default is All
        EVFSBlockType dumpAssetType = EVFSBlockType.All;
        if (!string.IsNullOrWhiteSpace(blockTypeString))
        {
            if (!Enum.TryParse<EVFSBlockType>(blockTypeString, ignoreCase: true, out dumpAssetType))
            {
                // Try parse as numeric value
                if (byte.TryParse(blockTypeString, out var btValue) &&
                    Enum.IsDefined(typeof(EVFSBlockType), btValue))
                {
                    dumpAssetType = (EVFSBlockType)btValue;
                }
                else
                {
                    Console.Error.WriteLine("Error: failed to parse blocktype \"{0}\".", blockTypeString);
                    Console.Error.WriteLine("Available types: {0}", string.Join(", ", blockHashMap.Keys));
                    return;
                }
            }
        }

        var streamingAssetsPath = Path.Combine(gamePath, VFSDefine.VFS_DIR);
        if (!Directory.Exists(streamingAssetsPath))
        {
            Console.Error.WriteLine("Error: VFS directory ({1}) not found under \"{0}\".", gamePath, VFSDefine.VFS_DIR);
            return;
        }

        if (dumpAssetType == EVFSBlockType.All)
        {
            foreach (var type in blockHashMap.Keys)
            {
                DumpAssetByType(streamingAssetsPath, type, outputDir);
            }
        }
        else
        {
            DumpAssetByType(streamingAssetsPath, dumpAssetType, outputDir);
        }
    }

    private static void DumpAssetByType(string streamingAssetsPath, EVFSBlockType dumpAssetType, string outputDir)
    {
        Console.WriteLine("Dumping {0} files...", dumpAssetType.ToString());

        // Use the pre-computed hash to find the block directory
        if (!blockHashMap.TryGetValue(dumpAssetType, out var hashName))
        {
            Console.Error.WriteLine("Block type {0} has no known hash mapping!", dumpAssetType.ToString());
            return;
        }

        var blockDir = Path.Combine(streamingAssetsPath, hashName);
        if (!Directory.Exists(blockDir))
        {
            Console.Error.WriteLine("Block directory {0} not found for type {1}!", hashName, dumpAssetType.ToString());
            return;
        }

        var blockFilePath = Path.Combine(blockDir, hashName + ".blc");
        if (!File.Exists(blockFilePath))
        {
            Console.Error.WriteLine("BLC file not found: {0}", blockFilePath);
            return;
        }

        var blockFile = File.ReadAllBytes(blockFilePath);

        // Extract the nonce from the first BLOCK_HEAD_LEN bytes
        byte[] nonce = GC.AllocateUninitializedArray<byte>(VFSDefine.BLOCK_HEAD_LEN);
        Buffer.BlockCopy(blockFile, 0, nonce, 0, nonce.Length);

        // Decrypt the BLC file content (starting after the header)
        using var chacha = new CSChaCha20(Convert.FromBase64String(VFSDefine.CHACHA_KEY), nonce, 1);
        var decryptedBytes = chacha.DecryptBytes(blockFile[VFSDefine.BLOCK_HEAD_LEN..]);
        Buffer.BlockCopy(decryptedBytes, 0, blockFile, VFSDefine.BLOCK_HEAD_LEN, decryptedBytes.Length);

        // Parse the VFBlockMainInfo structure.
        // Note: the version field is located at the beginning of the BLC file (offset 0).
        // XXE1 only decrypts the payload after BLOCK_HEAD_LEN, so the start offset should be 0 here.
        var vfBlockMainInfo = new VFBlockMainInfo(blockFile, 0);

        // Print the whole BLC structure for debugging to verify parsing.
        DumpBlockInfo(vfBlockMainInfo);

        foreach (var chunk in vfBlockMainInfo.allChunks)
        {
            // Convert MD5 to hex string for filename.
            // We write back the in-memory UInt128 as little-endian bytes to recover the original
            // 16-byte sequence so that the generated filename matches the .chk filename on disk.
            var chunkMd5Name = chunk.md5Name.ToHexStringLittleEndian() + FVFBlockChunkInfo.FILE_EXTENSION;
            var chunkFilePath = Path.Combine(blockDir, chunkMd5Name);

            if (!File.Exists(chunkFilePath))
            {
                Console.Error.WriteLine("  Chunk file not found: {0}", chunkMd5Name);
                continue;
            }

            using var chunkFs = File.OpenRead(chunkFilePath);

            foreach (var file in chunk.files)
            {
                var filePath = Path.Combine(outputDir, file.fileName);
                var fileDir = Path.GetDirectoryName(filePath);

                if (!string.IsNullOrEmpty(fileDir) && !Directory.Exists(fileDir))
                    Directory.CreateDirectory(fileDir);

                // Always seek to the file offset first
                chunkFs.Seek(file.offset, SeekOrigin.Begin);

                if (file.bUseEncrypt)
                {
                    // Build nonce for file decryption:
                    // - First 4 bytes: VFS_PROTO_VERSION (little-endian)
                    // - Next 8 bytes: ivSeed (little-endian)
                    byte[] fileNonce = GC.AllocateUninitializedArray<byte>(VFSDefine.BLOCK_HEAD_LEN);
                    Buffer.BlockCopy(BitConverter.GetBytes(vfBlockMainInfo.version), 0, fileNonce, 0, sizeof(int));
                    Buffer.BlockCopy(BitConverter.GetBytes(file.ivSeed), 0, fileNonce, sizeof(int), sizeof(long));

                    using var fileChacha = new CSChaCha20(Convert.FromBase64String(VFSDefine.CHACHA_KEY), fileNonce, 1);

                    // Read encrypted data
                    var encryptedData = new byte[file.len];
                    chunkFs.ReadExactly(encryptedData);

                    // Decrypt and write
                    var decryptedData = fileChacha.DecryptBytes(encryptedData);
                    File.WriteAllBytes(filePath, decryptedData);
                }
                else
                {
                    // Direct copy without encryption
                    using var fileFs = File.Create(filePath);
                    chunkFs.CopyBytes(fileFs, file.len);
                }
            }

            Console.WriteLine("  Dumped {0} file(s) from chunk {1}", chunk.files.Length, chunkMd5Name);
        }

        Console.WriteLine("  Completed dumping {0}!", dumpAssetType.ToString());
    }

    /// <summary>
    /// Prints detailed debug information for a BLC file (VFBlockMainInfo).
    /// </summary>
    private static void DumpBlockInfo(VFBlockMainInfo info)
    {
        Console.WriteLine("========== BLC INFO ==========");
        Console.WriteLine("GroupCfgName   : {0}", info.groupCfgName);
        Console.WriteLine("GroupCfgHash   : {0}", info.groupCfgHashName);
        Console.WriteLine("Version        : {0}", info.version);
        Console.WriteLine("BlockType      : {0} ({1})", info.blockType, (byte)info.blockType);
        Console.WriteLine("FileInfoNum    : {0}", info.groupFileInfoNum);
        Console.WriteLine("ChunksLength   : {0}", info.groupChunksLength);
        Console.WriteLine("ChunksCount    : {0}", info.allChunks.Length);
        Console.WriteLine("------------------------------");

        for (int i = 0; i < info.allChunks.Length; i++)
        {
            ref readonly var chunk = ref info.allChunks[i];
            Console.WriteLine("Chunk #{0}:", i);
            Console.WriteLine("  md5Name      : {0}", chunk.md5Name.ToHexStringLittleEndian());
            Console.WriteLine("  contentMD5   : {0}", chunk.contentMD5.ToHexStringLittleEndian());
            Console.WriteLine("  length       : {0}", chunk.length);
            Console.WriteLine("  blockType    : {0} ({1})", chunk.blockType, (byte)chunk.blockType);
            Console.WriteLine("  filesCount   : {0}", chunk.files.Length);

            for (int j = 0; j < chunk.files.Length; j++)
            {
                ref readonly var file = ref chunk.files[j];
                Console.WriteLine("    File #{0}:", j);
                Console.WriteLine("      name        : {0}", file.fileName);
                Console.WriteLine("      nameHash    : 0x{0:X16}", file.fileNameHash);
                Console.WriteLine("      chunkMD5    : {0}", file.fileChunkMD5Name.ToHexStringLittleEndian());
                Console.WriteLine("      dataMD5     : {0}", file.fileDataMD5.ToHexStringLittleEndian());
                Console.WriteLine("      offset      : {0}", file.offset);
                Console.WriteLine("      len         : {0}", file.len);
                Console.WriteLine("      blockType   : {0} ({1})", file.blockType, (byte)file.blockType);
                Console.WriteLine("      useEncrypt  : {0}", file.bUseEncrypt);
                if (file.bUseEncrypt)
                {
                    Console.WriteLine("      ivSeed      : {0}", file.ivSeed);
                }
            }

            Console.WriteLine("------------------------------");
        }

        Console.WriteLine("======== END BLC INFO ========");
    }

    private static void PrintHelp()
    {
        var exeName = Path.GetFileName(Assembly.GetEntryAssembly()?.Location ?? "BydTools.VFS.exe");
        Console.WriteLine("Usage:");
        Console.WriteLine("  BydTools.VFS.exe --gamepath <game_path> [--blocktype <type>] [--output <output_dir>] [-h|--help] [-v|--version]", exeName);
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  --gamepath   Game data directory that contains the VFS folder");
        Console.WriteLine("  --blocktype  Block type to dump, supports name or numeric value, default is all");
        Console.WriteLine("               Available types: {0}", string.Join(", ", blockHashMap.Keys));
        Console.WriteLine("  --output     Output directory, default is ./Assets next to the executable");
        Console.WriteLine("  -h, --help   Show help information");
        Console.WriteLine("  -v, --version   Show version information");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  BydTools.VFS.exe --gamepath \"D:\\\\Game\" --blocktype Bundle --output \"D:\\\\DumpedAssets\"", exeName);
        Console.WriteLine("  BydTools.VFS.exe --gamepath \"D:\\\\Game\" --blocktype 12", exeName);
        Console.WriteLine("  BydTools.VFS.exe --gamepath \"D:\\\\Game\"", exeName);
    }

    private static void PrintVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = asm.GetName().Version?.ToString() ?? "unknown";
        Console.WriteLine("{0} v{1}", asm.GetName().Name, version);
    }
}
