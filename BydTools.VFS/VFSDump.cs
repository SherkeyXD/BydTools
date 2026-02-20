using BydTools.Utils;
using BydTools.Utils.Crypto;
using BydTools.Utils.Extensions;
using BydTools.Utils.SparkBuffer;
using BydTools.Utils.xLua;

namespace BydTools.VFS;

/// <summary>
/// Provides functionality to dump files from VFS blocks.
/// Uses a post-processor strategy pattern for type-specific file handling.
/// </summary>
public class VFSDumper
{
    private readonly ILogger _logger;
    private readonly string? _luaMasterKey;
    private readonly Dictionary<EVFSBlockType, Func<byte[], string, bool>> _postProcessors;

    public VFSDumper(ILogger logger)
    {
        _logger = logger;
        _luaMasterKey = LuaDecipher.GetMasterKey();

        if (!string.IsNullOrEmpty(_luaMasterKey))
            _logger.Verbose("Lua master key initialized successfully");

        _postProcessors = new()
        {
            { EVFSBlockType.Table, TryProcessTableFile },
        };

        if (!string.IsNullOrEmpty(_luaMasterKey))
            _postProcessors[EVFSBlockType.Lua] = TryProcessLuaFile;
    }

    /// <summary>
    /// Maps EVFSBlockType to groupCfgHashName (directory name).
    /// These are pre-computed hash values as found in the game's VFS system.
    /// </summary>
    static readonly Dictionary<EVFSBlockType, string> blockHashMap = new()
    {
        { EVFSBlockType.InitAudio, "07A1BB91" },
        { EVFSBlockType.InitBundle, "0CE8FA57" },
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
        { EVFSBlockType.IFixPatchOut, "DAFE52C9" },
        { EVFSBlockType.ExtendData, "D6E622F7" },
        { EVFSBlockType.AudioChinese, "E1E7D7CE" },
        { EVFSBlockType.AudioEnglish, "A31457D0" },
        { EVFSBlockType.AudioJapanese, "F668D4EE" },
        { EVFSBlockType.AudioKorean, "E9D31017" },
    };

    /// <summary>
    /// Gets the block hash map.
    /// </summary>
    public static Dictionary<EVFSBlockType, string> BlockHashMap => blockHashMap;

    /// <summary>
    /// Decrypts a BLC file and parses its contents into a VFBlockMainInfo.
    /// </summary>
    private static VFBlockMainInfo DecryptAndParseBlc(string blcFilePath)
    {
        var blockFile = File.ReadAllBytes(blcFilePath);

        byte[] nonce = GC.AllocateUninitializedArray<byte>(VFSDefine.BLOCK_HEAD_LEN);
        Buffer.BlockCopy(blockFile, 0, nonce, 0, nonce.Length);

        using var chacha = new CSChaCha20(
            Convert.FromBase64String(VFSDefine.CHACHA_KEY),
            nonce,
            1
        );
        var decryptedBytes = chacha.DecryptBytes(blockFile[VFSDefine.BLOCK_HEAD_LEN..]);
        Buffer.BlockCopy(
            decryptedBytes,
            0,
            blockFile,
            VFSDefine.BLOCK_HEAD_LEN,
            decryptedBytes.Length
        );

        return new VFBlockMainInfo(blockFile, 0);
    }

    /// <summary>
    /// Reads and optionally decrypts a file's data from a chunk stream.
    /// </summary>
    private static byte[] ReadFileData(
        FileStream chunkFs,
        in FVFBlockFileInfo file,
        int version
    )
    {
        if (file.bUseEncrypt)
        {
            byte[] fileNonce = GC.AllocateUninitializedArray<byte>(VFSDefine.BLOCK_HEAD_LEN);
            Buffer.BlockCopy(
                BitConverter.GetBytes(version),
                0,
                fileNonce,
                0,
                sizeof(int)
            );
            Buffer.BlockCopy(
                BitConverter.GetBytes(file.ivSeed),
                0,
                fileNonce,
                sizeof(int),
                sizeof(long)
            );

            using var fileChacha = new CSChaCha20(
                Convert.FromBase64String(VFSDefine.CHACHA_KEY),
                fileNonce,
                1
            );

            var encryptedData = new byte[file.len];
            chunkFs.ReadExactly(encryptedData);
            return fileChacha.DecryptBytes(encryptedData);
        }

        var data = new byte[file.len];
        chunkFs.ReadExactly(data);
        return data;
    }

    /// <summary>
    /// Dumps files from a VFS block type.
    /// </summary>
    public void DumpAssetByType(
        string streamingAssetsPath,
        EVFSBlockType dumpAssetType,
        string outputDir
    )
    {
        if (!blockHashMap.TryGetValue(dumpAssetType, out var hashName))
        {
            _logger.Error(
                "Block type {0} has no known hash mapping!",
                dumpAssetType.ToString()
            );
            return;
        }

        var blockDir = Path.Combine(streamingAssetsPath, hashName);
        if (!Directory.Exists(blockDir))
        {
            _logger.Error(
                "Block directory {0} not found for type {1}!",
                hashName,
                dumpAssetType.ToString()
            );
            return;
        }

        var blockFilePath = Path.Combine(blockDir, hashName + ".blc");
        if (!File.Exists(blockFilePath))
        {
            _logger.Error("BLC file not found: {0}", blockFilePath);
            return;
        }

        _logger.Info($"Input: {streamingAssetsPath}");
        _logger.Info($"Output: {outputDir}");

        var vfBlockMainInfo = DecryptAndParseBlc(blockFilePath);

        // Count files by extension
        var fileTypeCounts = new Dictionary<string, int>();
        int totalFiles = 0;
        foreach (var chunk in vfBlockMainInfo.allChunks)
        {
            totalFiles += chunk.files.Length;
            foreach (var file in chunk.files)
            {
                var ext = Path.GetExtension(file.fileName).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext))
                    ext = "(no extension)";
                fileTypeCounts.TryGetValue(ext, out var count);
                fileTypeCounts[ext] = count + 1;
            }
        }

        var typeDetails = string.Join(
            ", ",
            fileTypeCounts.Select(kvp => $"{kvp.Value} {kvp.Key}")
        );
        _logger.Info($"Found {totalFiles} files ({typeDetails})");

        DumpBlockInfo(vfBlockMainInfo);

        var createdDirs = new HashSet<string>();
        _logger.Info("Extracting...");

        int extractedCount = 0;
        foreach (var chunk in vfBlockMainInfo.allChunks)
        {
            var chunkMd5Name =
                chunk.md5Name.ToHexStringLittleEndian() + FVFBlockChunkInfo.FILE_EXTENSION;
            var chunkFilePath = Path.Combine(blockDir, chunkMd5Name);

            if (!File.Exists(chunkFilePath))
            {
                _logger.Verbose($"  Chunk file not found: {chunkMd5Name}");
                continue;
            }

            using var chunkFs = File.OpenRead(chunkFilePath);

            foreach (var file in chunk.files)
            {
                var filePath = Path.Combine(outputDir, file.fileName);
                var fileDir = Path.GetDirectoryName(filePath);

                if (!string.IsNullOrEmpty(fileDir) && !Directory.Exists(fileDir))
                {
                    Directory.CreateDirectory(fileDir);
                    if (createdDirs.Add(fileDir))
                        _logger.Info($"  Created directory: {fileDir}");
                }

                chunkFs.Seek(file.offset, SeekOrigin.Begin);
                var fileData = ReadFileData(chunkFs, file, vfBlockMainInfo.version);

                if (
                    _postProcessors.TryGetValue(dumpAssetType, out var processor)
                    && processor(fileData, filePath)
                )
                {
                    extractedCount++;
                    continue;
                }

                File.WriteAllBytes(filePath, fileData);
                extractedCount++;
            }

            _logger.Verbose(
                "  Dumped {0} file(s) from chunk {1}",
                chunk.files.Length,
                chunkMd5Name
            );
        }

        _logger.Info($"Done, {extractedCount} files extracted.");
    }

    #region Post-processors

    private bool TryProcessTableFile(byte[] data, string outputPath)
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

    private bool TryProcessLuaFile(byte[] data, string outputPath)
    {
        try
        {
            var base64String = System.Text.Encoding.UTF8.GetString(data).Trim();
            var decryptedLua = LuaDecipher.DecryptLuaWithMasterKey(
                base64String,
                _luaMasterKey!
            );

            if (decryptedLua != null && LuaDecipher.IsValidLuaBytecode(decryptedLua))
            {
                var luaFilePath = Path.ChangeExtension(outputPath, ".lua");
                File.WriteAllBytes(luaFilePath, decryptedLua);
                _logger.Verbose(
                    $"  Decrypted Lua: {Path.GetFileName(outputPath)} -> {Path.GetFileName(luaFilePath)}"
                );
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.Verbose(
                $"  Lua decryption failed for {Path.GetFileName(outputPath)}: {ex.Message}"
            );
        }

        return false;
    }

    #endregion

    /// <summary>
    /// Prints detailed debug information for a BLC file (VFBlockMainInfo).
    /// Only shown in verbose mode.
    /// </summary>
    private void DumpBlockInfo(VFBlockMainInfo info)
    {
        _logger.Verbose("========== BLC INFO ==========");
        _logger.Verbose("GroupCfgName   : {0}", info.groupCfgName);
        _logger.Verbose("GroupCfgHash   : {0}", info.groupCfgHashName);
        _logger.Verbose("Version        : {0}", info.version);
        _logger.Verbose("BlockType      : {0} ({1})", info.blockType, (byte)info.blockType);
        _logger.Verbose("FileInfoNum    : {0}", info.groupFileInfoNum);
        _logger.Verbose("ChunksLength   : {0}", info.groupChunksLength);
        _logger.Verbose("ChunksCount    : {0}", info.allChunks.Length);
        _logger.Verbose("------------------------------");

        for (int i = 0; i < info.allChunks.Length; i++)
        {
            ref readonly var chunk = ref info.allChunks[i];
            _logger.Verbose("Chunk #{0}:", i);
            _logger.Verbose(
                "  md5Name      : {0}",
                chunk.md5Name.ToHexStringLittleEndian()
            );
            _logger.Verbose(
                "  contentMD5   : {0}",
                chunk.contentMD5.ToHexStringLittleEndian()
            );
            _logger.Verbose("  length       : {0}", chunk.length);
            _logger.Verbose(
                "  blockType    : {0} ({1})",
                chunk.blockType,
                (byte)chunk.blockType
            );
            _logger.Verbose("  filesCount   : {0}", chunk.files.Length);

            for (int j = 0; j < chunk.files.Length; j++)
            {
                ref readonly var file = ref chunk.files[j];
                _logger.Verbose("    File #{0}:", j);
                _logger.Verbose("      name        : {0}", file.fileName);
                _logger.Verbose("      nameHash    : 0x{0:X16}", file.fileNameHash);
                _logger.Verbose(
                    "      chunkMD5    : {0}",
                    file.fileChunkMD5Name.ToHexStringLittleEndian()
                );
                _logger.Verbose(
                    "      dataMD5     : {0}",
                    file.fileDataMD5.ToHexStringLittleEndian()
                );
                _logger.Verbose("      offset      : {0}", file.offset);
                _logger.Verbose("      len         : {0}", file.len);
                _logger.Verbose(
                    "      blockType   : {0} ({1})",
                    file.blockType,
                    (byte)file.blockType
                );
                _logger.Verbose("      useEncrypt  : {0}", file.bUseEncrypt);
                if (file.bUseEncrypt)
                {
                    _logger.Verbose("      ivSeed      : {0}", file.ivSeed);
                }
            }

            _logger.Verbose("------------------------------");
        }

        _logger.Verbose("======== END BLC INFO ========");
    }

    /// <summary>
    /// Scans all subdirectories under the VFS path, decrypts each BLC file,
    /// and prints the groupCfgName found in each block declaration.
    /// </summary>
    public void DebugScanBlocks(string streamingAssetsPath)
    {
        _logger.Info("Debug: scanning all block declarations under {0}", streamingAssetsPath);
        _logger.Info("");

        var dirs = Directory.GetDirectories(streamingAssetsPath);
        if (dirs.Length == 0)
        {
            _logger.Info("No subdirectories found.");
            return;
        }

        int found = 0;
        foreach (var dir in dirs.OrderBy(d => d))
        {
            var dirName = Path.GetFileName(dir);
            var blcPath = Path.Combine(dir, dirName + ".blc");

            if (!File.Exists(blcPath))
            {
                _logger.Verbose("  [{0}] No BLC file found, skipping.", dirName);
                continue;
            }

            try
            {
                var info = DecryptAndParseBlc(blcPath);

                var blockTypeByte = (byte)info.blockType;
                var typeLabel = Enum.IsDefined(info.blockType)
                    ? $"{info.blockType} ({blockTypeByte})"
                    : $"Unknown ({blockTypeByte})";

                _logger.Info(
                    "  [{0}] groupCfgName = {1}  |  blockType = {2}  |  chunks = {3}  |  files = {4}",
                    dirName,
                    info.groupCfgName,
                    typeLabel,
                    info.allChunks.Length,
                    info.groupFileInfoNum
                );
                found++;
            }
            catch (Exception ex)
            {
                _logger.Error("  [{0}] Failed to parse BLC: {1}", dirName, ex.Message);
            }
        }

        _logger.Info("");
        _logger.Info(
            "Debug: found {0} block declaration(s) in {1} subdirectories.",
            found,
            dirs.Length
        );
    }
}
