using System.Collections.Frozen;
using BydTools.Utils;
using BydTools.Utils.Crypto;
using BydTools.Utils.Extensions;
using BydTools.VFS.CriUsm;
using BydTools.VFS.PostProcessors;

namespace BydTools.VFS;

/// <summary>
/// Extracts files from VFS blocks with pluggable post-processing.
/// </summary>
public class VFSDumper : IVFSDumper
{
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<EVFSBlockType, IPostProcessor> _postProcessors;
    private readonly byte[] _chaChaKey;

    public VFSDumper(
        ILogger logger,
        IReadOnlyDictionary<EVFSBlockType, IPostProcessor> postProcessors,
        byte[]? customKey = null)
    {
        _logger = logger;
        _postProcessors = postProcessors;
        _chaChaKey = customKey ?? VFSDefine.DefaultChaChaKey;
    }

    /// <summary>
    /// Maps EVFSBlockType to groupCfgHashName (directory name).
    /// Frozen for thread-safety and optimized read performance.
    /// </summary>
    private static readonly FrozenDictionary<EVFSBlockType, string> blockHashMap = new Dictionary<EVFSBlockType, string>
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
    }.ToFrozenDictionary();

    public static IReadOnlyDictionary<EVFSBlockType, string> BlockHashMap => blockHashMap;

    private VFBlockMainInfo DecryptAndParseBlc(string blcFilePath)
    {
        var blockFile = File.ReadAllBytes(blcFilePath);

        byte[] nonce = GC.AllocateUninitializedArray<byte>(VFSDefine.BLOCK_HEAD_LEN);
        Buffer.BlockCopy(blockFile, 0, nonce, 0, nonce.Length);

        using var chacha = new CSChaCha20(_chaChaKey, nonce, 1);
        var decryptedBytes = chacha.DecryptBytes(blockFile[VFSDefine.BLOCK_HEAD_LEN..]);
        Buffer.BlockCopy(
            decryptedBytes,
            0,
            blockFile,
            VFSDefine.BLOCK_HEAD_LEN,
            decryptedBytes.Length
        );

        var info = new VFBlockMainInfo(blockFile, 0);

        var expectedHash = Path.GetFileNameWithoutExtension(blcFilePath);
        if (!string.Equals(info.groupCfgHashName, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"BLC hash mismatch: parsed \"{info.groupCfgHashName}\", expected \"{expectedHash}\". " +
                "The ChaCha20 key may be incorrect or outdated.");
        }

        return info;
    }

    private byte[] ReadFileData(
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

            using var fileChacha = new CSChaCha20(_chaChaKey, fileNonce, 1);

            var encryptedData = new byte[file.len];
            chunkFs.ReadExactly(encryptedData);
            return fileChacha.DecryptBytes(encryptedData);
        }

        var data = new byte[file.len];
        chunkFs.ReadExactly(data);
        return data;
    }

    public void DumpAssetByType(
        string streamingAssetsPath,
        EVFSBlockType dumpAssetType,
        string outputDir
    )
    {
        if (!blockHashMap.TryGetValue(dumpAssetType, out var hashName))
        {
            _logger.Error("Block type {0} has no known hash mapping!", dumpAssetType.ToString());
            return;
        }

        var blockDir = Path.Combine(streamingAssetsPath, hashName);
        if (!Directory.Exists(blockDir))
        {
            _logger.Error("Block directory {0} not found for type {1}!", hashName, dumpAssetType.ToString());
            return;
        }

        var blockFilePath = Path.Combine(blockDir, hashName + ".blc");
        if (!File.Exists(blockFilePath))
        {
            _logger.Error("BLC file not found: {0}", blockFilePath);
            return;
        }

        _logger.Info("--- {0} ({1}) ---", dumpAssetType, (byte)dumpAssetType);

        var vfBlockMainInfo = DecryptAndParseBlc(blockFilePath);

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

        var typeDetails = string.Join(", ", fileTypeCounts.Select(kvp => $"{kvp.Value} {kvp.Key}"));
        _logger.Info($"Found {totalFiles} files ({typeDetails})");

        DumpBlockInfo(vfBlockMainInfo);

        var createdDirs = new HashSet<string>();
        _logger.Info("Extracting...");

        _postProcessors.TryGetValue(dumpAssetType, out var processor);

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
                chunkFs.Seek(file.offset, SeekOrigin.Begin);
                var fileData = ReadFileData(chunkFs, file, vfBlockMainInfo.version);

                var fileName = file.fileName;
                if (string.IsNullOrEmpty(fileName) && dumpAssetType == EVFSBlockType.Video)
                {
                    var usmName = UsmNameReader.TryGetName(fileData)
                        ?? $"{file.fileNameHash:X16}.usm";
                    fileName = $"Video/{usmName}";
                    _logger.Verbose("    Recovered USM name: {0}", fileName);
                }

                var filePath = Path.Combine(outputDir, fileName);
                var fileDir = Path.GetDirectoryName(filePath);

                if (!string.IsNullOrEmpty(fileDir) && !Directory.Exists(fileDir))
                {
                    Directory.CreateDirectory(fileDir);
                    if (createdDirs.Add(fileDir))
                        _logger.Info($"  Created directory: {fileDir}");
                }

                if (processor != null && processor.TryProcess(fileData, filePath))
                {
                    extractedCount++;
                    continue;
                }

                File.WriteAllBytes(filePath, fileData);
                extractedCount++;
            }

            _logger.Verbose("  Dumped {0} file(s) from chunk {1}", chunk.files.Length, chunkMd5Name);
        }

        _logger.Info($"Done, {extractedCount} files extracted.");
    }

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
            _logger.Verbose("  md5Name      : {0}", chunk.md5Name.ToHexStringLittleEndian());
            _logger.Verbose("  contentMD5   : {0}", chunk.contentMD5.ToHexStringLittleEndian());
            _logger.Verbose("  length       : {0}", chunk.length);
            _logger.Verbose("  blockType    : {0} ({1})", chunk.blockType, (byte)chunk.blockType);
            _logger.Verbose("  filesCount   : {0}", chunk.files.Length);

            for (int j = 0; j < chunk.files.Length; j++)
            {
                ref readonly var file = ref chunk.files[j];
                _logger.Verbose("    File #{0}:", j);
                _logger.Verbose("      name        : {0}", file.fileName);
                _logger.Verbose("      nameHash    : 0x{0:X16}", file.fileNameHash);
                _logger.Verbose("      chunkMD5    : {0}", file.fileChunkMD5Name.ToHexStringLittleEndian());
                _logger.Verbose("      dataMD5     : {0}", file.fileDataMD5.ToHexStringLittleEndian());
                _logger.Verbose("      offset      : {0}", file.offset);
                _logger.Verbose("      len         : {0}", file.len);
                _logger.Verbose("      blockType   : {0} ({1})", file.blockType, (byte)file.blockType);
                _logger.Verbose("      useEncrypt  : {0}", file.bUseEncrypt);
                if (file.bUseEncrypt)
                    _logger.Verbose("      ivSeed      : {0}", file.ivSeed);
            }

            _logger.Verbose("------------------------------");
        }

        _logger.Verbose("======== END BLC INFO ========");
    }

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
        var blockTypeMap = new Dictionary<string, string>();

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

                blockTypeMap[info.groupCfgName] = dirName;
                DumpBlockInfo(info);
                found++;
            }
            catch (Exception ex)
            {
                _logger.Error("  [{0}] Failed to parse BLC: {1}", dirName, ex.Message);
            }
        }

        _logger.Info("");
        _logger.Info("Debug: found {0} block declaration(s) in {1} subdirectories.", found, dirs.Length);

        if (blockTypeMap.Count > 0)
        {
            _logger.Info("");
            _logger.Info("Block type mapping (groupCfgName -> directory hash):");
            foreach (var (name, hash) in blockTypeMap.OrderBy(kv => kv.Key))
                _logger.Info("  {0} -> {1}", name, hash);
        }
    }
}
