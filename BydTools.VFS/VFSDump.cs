using BydTools.Utils.Crypto;
using BydTools.Utils.Extensions;
using BydTools.Utils.SparkBuffer;

namespace BydTools.VFS;

/// <summary>
/// Provides functionality to dump files from VFS blocks.
/// </summary>
public class VFSDumper
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the VFSDumper class.
    /// </summary>
    /// <param name="logger">Optional logger for output. If null, uses Console directly.</param>
    public VFSDumper(ILogger? logger = null)
    {
        _logger = logger;
    }
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
        { EVFSBlockType.TableCfg, "TableCfg" },
        { EVFSBlockType.Video, "Video" },
        { EVFSBlockType.IV, "IV" },
        { EVFSBlockType.Streaming, "Streaming" },
        { EVFSBlockType.Json, "Json" },
        { EVFSBlockType.LuaScript, "LuaScript" },
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
        { EVFSBlockType.TableCfg, "42A8FCA6" },
        { EVFSBlockType.Video, "55FC21C6" },
        { EVFSBlockType.IV, "A63D7E6A" },
        { EVFSBlockType.Streaming, "C3442D43" },
        { EVFSBlockType.Json, "775A31D1" },
        { EVFSBlockType.LuaScript, "19E3AE45" },
        { EVFSBlockType.IFixPatch, "DAFE52C9" },
        { EVFSBlockType.ExtendData, "D6E622F7" },
        { EVFSBlockType.AudioChinese, "E1E7D7CE" },
        { EVFSBlockType.AudioEnglish, "A31457D0" },
        { EVFSBlockType.AudioJapanese, "F668D4EE" },
        { EVFSBlockType.AudioKorean, "E9D31017" },
    };

    /// <summary>
    /// Dumps files from a VFS block type.
    /// </summary>
    /// <param name="streamingAssetsPath">Path to the VFS directory.</param>
    /// <param name="dumpAssetType">The block type to dump.</param>
    /// <param name="outputDir">Output directory for dumped files.</param>
    public void DumpAssetByType(
        string streamingAssetsPath,
        EVFSBlockType dumpAssetType,
        string outputDir
    )
    {
        // Use the pre-computed hash to find the block directory
        if (!blockHashMap.TryGetValue(dumpAssetType, out var hashName))
        {
            if (_logger != null)
            {
                _logger.Error(
                    "Block type {0} has no known hash mapping!",
                    dumpAssetType.ToString()
                );
            }
            else
            {
                Console.Error.WriteLine(
                    "Block type {0} has no known hash mapping!",
                    dumpAssetType.ToString()
                );
            }
            return;
        }

        var blockDir = Path.Combine(streamingAssetsPath, hashName);
        if (!Directory.Exists(blockDir))
        {
            if (_logger != null)
            {
                _logger.Error(
                    "Block directory {0} not found for type {1}!",
                    hashName,
                    dumpAssetType.ToString()
                );
            }
            else
            {
                Console.Error.WriteLine(
                    "Block directory {0} not found for type {1}!",
                    hashName,
                    dumpAssetType.ToString()
                );
            }
            return;
        }

        var blockFilePath = Path.Combine(blockDir, hashName + ".blc");
        if (!File.Exists(blockFilePath))
        {
            if (_logger != null)
            {
                _logger.Error("BLC file not found: {0}", blockFilePath);
            }
            else
            {
                Console.Error.WriteLine("BLC file not found: {0}", blockFilePath);
            }
            return;
        }

        // Print input/output info
        if (_logger != null)
        {
            _logger.Info($"Input: {streamingAssetsPath}");
            _logger.Info($"Output: {outputDir}");
        }

        var blockFile = File.ReadAllBytes(blockFilePath);

        // Extract the nonce from the first BLOCK_HEAD_LEN bytes
        byte[] nonce = GC.AllocateUninitializedArray<byte>(VFSDefine.BLOCK_HEAD_LEN);
        Buffer.BlockCopy(blockFile, 0, nonce, 0, nonce.Length);

        // Decrypt the BLC file content (starting after the header)
        using var chacha = new CSChaCha20(Convert.FromBase64String(VFSDefine.CHACHA_KEY), nonce, 1);
        var decryptedBytes = chacha.DecryptBytes(blockFile[VFSDefine.BLOCK_HEAD_LEN..]);
        Buffer.BlockCopy(
            decryptedBytes,
            0,
            blockFile,
            VFSDefine.BLOCK_HEAD_LEN,
            decryptedBytes.Length
        );

        // Parse the VFBlockMainInfo structure.
        // Note: the version field is located at the beginning of the BLC file (offset 0).
        // XXE1 only decrypts the payload after BLOCK_HEAD_LEN, so the start offset should be 0 here.
        var vfBlockMainInfo = new VFBlockMainInfo(blockFile, 0);

        // Count files by type
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

        // Print found files info
        if (_logger != null)
        {
            var typeDetails = string.Join(", ", fileTypeCounts.Select(kvp => $"{kvp.Value} {kvp.Key}"));
            _logger.Info($"Found {totalFiles} files ({typeDetails})");
        }

        // Print verbose BLC structure info
        DumpBlockInfo(vfBlockMainInfo);

        // Track created directories to avoid duplicate messages
        var createdDirs = new HashSet<string>();

        // Print extracting message
        if (_logger != null)
        {
            _logger.Info("Extracting...");
        }

        int extractedCount = 0;
        foreach (var chunk in vfBlockMainInfo.allChunks)
        {
            // Convert MD5 to hex string for filename.
            // We write back the in-memory UInt128 as little-endian bytes to recover the original
            // 16-byte sequence so that the generated filename matches the .chk filename on disk.
            var chunkMd5Name =
                chunk.md5Name.ToHexStringLittleEndian() + FVFBlockChunkInfo.FILE_EXTENSION;
            var chunkFilePath = Path.Combine(blockDir, chunkMd5Name);

            if (!File.Exists(chunkFilePath))
            {
                if (_logger != null)
                {
                    _logger.Verbose($"  Chunk file not found: {chunkMd5Name}");
                }
                else
                {
                    Console.Error.WriteLine("  Chunk file not found: {0}", chunkMd5Name);
                }
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
                    // Only log directory creation once per directory
                    if (createdDirs.Add(fileDir) && _logger != null)
                    {
                        _logger.Info($"  Created directory: {fileDir}");
                    }
                }

                // Always seek to the file offset first
                chunkFs.Seek(file.offset, SeekOrigin.Begin);

                byte[] fileData;
                if (file.bUseEncrypt)
                {
                    // Build nonce for file decryption:
                    // - First 4 bytes: VFS_PROTO_VERSION (little-endian)
                    // - Next 8 bytes: ivSeed (little-endian)
                    byte[] fileNonce = GC.AllocateUninitializedArray<byte>(
                        VFSDefine.BLOCK_HEAD_LEN
                    );
                    Buffer.BlockCopy(
                        BitConverter.GetBytes(vfBlockMainInfo.version),
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

                    // Read encrypted data
                    var encryptedData = new byte[file.len];
                    chunkFs.ReadExactly(encryptedData);

                    // Decrypt
                    fileData = fileChacha.DecryptBytes(encryptedData);
                }
                else
                {
                    // Direct read without encryption
                    fileData = new byte[file.len];
                    chunkFs.ReadExactly(fileData);
                }

                // Check if we need to decrypt with SparkBuffer
                if (dumpAssetType == EVFSBlockType.TableCfg && 
                    Path.GetExtension(file.fileName).Equals(".bytes", StringComparison.OrdinalIgnoreCase))
                {
                    if (_logger != null)
                    {
                        _logger.Verbose($"  Attempting SparkBuffer decryption for: {file.fileName}");
                    }

                    try
                    {
                        // Try to decrypt with SparkBuffer
                        var decryptedJson = SparkBufferDumper.Decrypt(fileData);
                        if (!string.IsNullOrEmpty(decryptedJson))
                        {
                            // Change extension to .json
                            var jsonFilePath = Path.ChangeExtension(filePath, ".json");
                            File.WriteAllText(jsonFilePath, decryptedJson);
                            
                            if (_logger != null)
                            {
                                _logger.Info($"  ✓ Decrypted SparkBuffer: {file.fileName} -> {Path.GetFileName(jsonFilePath)}");
                            }
                            
                            extractedCount++;
                            continue;
                        }
                        else
                        {
                            if (_logger != null)
                            {
                                _logger.Verbose($"  SparkBuffer decryption returned empty for {file.fileName}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_logger != null)
                        {
                            _logger.Error($"  ✗ SparkBuffer decryption failed for {file.fileName}: {ex.Message}");
                        }
                        // Fall through to save original file
                    }
                }

                // Save file as-is
                File.WriteAllBytes(filePath, fileData);

                extractedCount++;
            }

            if (_logger != null)
            {
                _logger.Verbose(
                    "  Dumped {0} file(s) from chunk {1}",
                    chunk.files.Length,
                    chunkMd5Name
                );
            }
        }

        // Print completion message
        if (_logger != null)
        {
            _logger.Info($"Done, {extractedCount} files extracted.");
        }
    }

    /// <summary>
    /// Prints detailed debug information for a BLC file (VFBlockMainInfo).
    /// Only shown in verbose mode.
    /// </summary>
    private void DumpBlockInfo(VFBlockMainInfo info)
    {
        if (_logger != null)
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
    }

    /// <summary>
    /// Gets the block type name map.
    /// </summary>
    public static Dictionary<EVFSBlockType, string> BlockTypeNameMap => blockTypeNameMap;

    /// <summary>
    /// Gets the block hash map.
    /// </summary>
    public static Dictionary<EVFSBlockType, string> BlockHashMap => blockHashMap;
}
