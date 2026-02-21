using System.Text.Json;
using BydTools.PCK;
using BydTools.Utils;
using BydTools.Utils.Crypto;
using BydTools.Utils.Extensions;
using BydTools.VFS;
using BydTools.VFS.SparkBuffer;
using BydTools.Wwise;

namespace BydTools.CLI.Commands;

sealed class PckCommand : ICommand
{
    public string Name => "pck";
    public string Description => "Extract audio from VFS";

    private static readonly string[] AudioBlockTypeNames =
    [
        nameof(EVFSBlockType.InitAudio),
        nameof(EVFSBlockType.Audio),
        nameof(EVFSBlockType.AudioChinese),
        nameof(EVFSBlockType.AudioEnglish),
        nameof(EVFSBlockType.AudioJapanese),
        nameof(EVFSBlockType.AudioKorean),
    ];

    public void PrintHelp(string exeName)
    {
        HelpFormatter.WriteUsage("pck", "--input <path> --output <dir> --type <type>");

        HelpFormatter.WriteSectionHeader("Required");
        HelpFormatter.WriteEntry(
            "-i, --input <path>",
            "Game data directory that contains the VFS folder"
        );
        HelpFormatter.WriteEntry("-o, --output <dir>", "Output directory");
        HelpFormatter.WriteEntry("-t, --type <type>", "Audio block type to extract");
        HelpFormatter.WriteBlankLine();

        HelpFormatter.WriteSectionHeader("Options");
        HelpFormatter.WriteEntry("-m, --mode <mode>", "Extract mode (default: wav)");
        HelpFormatter.WriteEntryContinuation("raw  Extract wem without conversion");
        HelpFormatter.WriteEntryContinuation("wav  Convert to wav via vgmstream");
        HelpFormatter.WriteEntry("--no-map", "Disable automatic AudioDialog filename mapping");
        HelpFormatter.WriteCommonOptions();
        HelpFormatter.WriteBlankLine();

        WriteAudioBlockTypes();
    }

    public void Execute(string[] args)
    {
        var parser = new ArgParser()
            .AddFlag("help", "h")
            .AddFlag("verbose", "v")
            .AddFlag("no-map")
            .AddOption("input", "i")
            .AddOption("output", "o")
            .AddOption("type", "t")
            .AddOption("mode", "m")
            .AddOption("key");

        if (!parser.TryParse(args))
        {
            foreach (var error in parser.Errors)
                Console.Error.WriteLine(error);
            PrintHelp(Program.ExecutableName);
            return;
        }

        if (parser.GetFlag("help"))
        {
            PrintHelp(Program.ExecutableName);
            return;
        }

        var gamePath = parser.GetValue("input");
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            Console.Error.WriteLine("Error: --input is required.");
            PrintHelp(Program.ExecutableName);
            return;
        }

        var outputDir = parser.GetValue("output");
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            Console.Error.WriteLine("Error: --output is required.");
            PrintHelp(Program.ExecutableName);
            return;
        }

        var typeStr = parser.GetValue("type");
        if (string.IsNullOrWhiteSpace(typeStr))
        {
            Console.Error.WriteLine("Error: --type is required.");
            HelpFormatter.WriteBlankLine();
            WriteAudioBlockTypes();
            return;
        }

        if (
            !Enum.TryParse<EVFSBlockType>(typeStr, ignoreCase: true, out var blockType)
            || !AudioBlockTypeNames.Contains(blockType.ToString())
        )
        {
            Console.Error.WriteLine($"Error: '{typeStr}' is not a valid audio block type.");
            HelpFormatter.WriteBlankLine();
            WriteAudioBlockTypes();
            return;
        }

        var mode = parser.GetValue("mode") ?? "wav";
        if (mode is not ("raw" or "wav"))
        {
            Console.Error.WriteLine("Error: --mode must be one of: raw, wav");
            PrintHelp(Program.ExecutableName);
            return;
        }

        var vfsPath = Path.Combine(gamePath, VFSDefine.VFS_DIR);
        if (!Directory.Exists(vfsPath))
        {
            Console.Error.WriteLine(
                $"Error: VFS directory ({VFSDefine.VFS_DIR}) not found under \"{gamePath}\"."
            );
            return;
        }

        byte[] chaChaKey = VFSDefine.DefaultChaChaKey;
        var keyBase64 = parser.GetValue("key");
        if (!string.IsNullOrWhiteSpace(keyBase64))
        {
            try
            {
                chaChaKey = Convert.FromBase64String(keyBase64);
            }
            catch (FormatException)
            {
                Console.Error.WriteLine("Error: --key must be a valid Base64 string.");
                return;
            }
            if (chaChaKey.Length != VFSDefine.KEY_LEN)
            {
                Console.Error.WriteLine(
                    $"Error: --key must decode to {VFSDefine.KEY_LEN} bytes (got {chaChaKey.Length})."
                );
                return;
            }
        }

        try
        {
            var logger = new Logger(parser.GetFlag("verbose"));
            bool autoMap = !parser.GetFlag("no-map");
            string? language = GetLanguageForBlockType(blockType);

            IWemConverter? wemConverter = null;
            if (mode == "wav")
            {
                wemConverter = ResolveConverter(logger);
                if (wemConverter == null)
                    return;
            }

            Console.WriteLine($"Input:  {vfsPath}");
            Console.WriteLine($"Output: {outputDir}");
            Console.WriteLine($"Type:   {blockType}");
            Console.WriteLine($"Mode:   {mode}");

            PckMapper? mapper = null;
            if (autoMap && language != null)
            {
                mapper = LoadAudioDialogMapper(vfsPath, chaChaKey, language, logger);
            }
            else if (autoMap && language == null)
            {
                logger.Info(
                    $"Auto-mapping skipped: no language context for {blockType}"
                );
            }

            Directory.CreateDirectory(outputDir);
            ExtractAudioBlock(
                vfsPath,
                chaChaKey,
                blockType,
                outputDir,
                mode,
                mapper,
                wemConverter,
                logger
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static void WriteAudioBlockTypes()
    {
        HelpFormatter.WriteSectionHeader("Audio block types");
        Console.WriteLine($"  {string.Join(", ", AudioBlockTypeNames)}");
    }

    private static string? GetLanguageForBlockType(EVFSBlockType type) =>
        type switch
        {
            EVFSBlockType.AudioChinese => "chinese",
            EVFSBlockType.AudioEnglish => "english",
            EVFSBlockType.AudioJapanese => "japanese",
            EVFSBlockType.AudioKorean => "korean",
            _ => null,
        };

    private static PckMapper? LoadAudioDialogMapper(
        string vfsPath,
        byte[] key,
        string language,
        ILogger logger
    )
    {
        logger.Info("Loading AudioDialog from Table block...");

        var tableInfo = ReadBlockInfo(vfsPath, EVFSBlockType.Table, key);

        foreach (var chunk in tableInfo.allChunks)
        {
            var chunkFile = ResolveChunkPath(vfsPath, EVFSBlockType.Table, chunk);
            if (chunkFile == null)
                continue;

            using var chunkFs = File.OpenRead(chunkFile);
            foreach (var file in chunk.files)
            {
                if (!file.fileName.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
                    continue;

                chunkFs.Seek(file.offset, SeekOrigin.Begin);
                var data = ReadFileData(chunkFs, file, tableInfo.version, key);

                using var ms = new MemoryStream(data);
                using var br = new BinaryReader(ms);

                string rootName;
                try
                {
                    rootName = SparkBufferDumper.GetRootDefinitionName(br);
                }
                catch
                {
                    continue;
                }

                if (rootName != "AudioDialog")
                    continue;

                logger.Info("  Found AudioDialog, parsing...");
                var json = SparkBufferDumper.Decrypt(data);
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
                var mapper = new PckMapper(stream, language);
                logger.Info($"  Mapped {mapper.Count} entries (lang={language})");
                return mapper;
            }
        }

        logger.Info("  AudioDialog not found in Table block, skipping auto-map");
        return null;
    }

    private static void ExtractAudioBlock(
        string vfsPath,
        byte[] key,
        EVFSBlockType blockType,
        string outputDir,
        string mode,
        PckMapper? mapper,
        IWemConverter? wemConverter,
        ILogger logger
    )
    {
        var blockInfo = ReadBlockInfo(vfsPath, blockType, key);
        logger.Info($"--- {blockType} ---");

        var pckFiles = new List<(string Name, byte[] Data)>();
        foreach (var chunk in blockInfo.allChunks)
        {
            var chunkFile = ResolveChunkPath(vfsPath, blockType, chunk);
            if (chunkFile == null)
            {
                logger.Verbose($"  Chunk not found, skipping");
                continue;
            }

            using var chunkFs = File.OpenRead(chunkFile);
            foreach (var file in chunk.files)
            {
                if (!file.fileName.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
                    continue;

                chunkFs.Seek(file.offset, SeekOrigin.Begin);
                var data = ReadFileData(chunkFs, file, blockInfo.version, key);
                pckFiles.Add((file.fileName, data));
            }
        }

        if (pckFiles.Count == 0)
        {
            logger.Info("No PCK files found in block.");
            return;
        }

        logger.Info($"Found {pckFiles.Count} PCK file(s)");

        int totalSaved = 0;
        int totalFailed = 0;

        foreach (var (pckName, pckData) in pckFiles)
        {
            logger.Info($"Processing {pckName}...");

            using var pckStream = new MemoryStream(pckData);
            var pckParser = new PckParser(pckStream);
            var content = pckParser.Parse();

            logger.Verbose(
                $"  {content.Entries.Count} entries, {content.Languages.Count} languages"
            );

            int saved = 0;
            int failed = 0;

            foreach (var entry in content.Entries)
            {
                byte[] fileData = pckParser.GetFileData(entry);
                if (fileData.Length < 4)
                    continue;

                ReadOnlySpan<byte> magic = fileData.AsSpan(0, 4);
                if (!magic.SequenceEqual("RIFF"u8) && !magic.SequenceEqual("RIFX"u8))
                {
                    if (magic.SequenceEqual("BKHD"u8))
                        ExtractBnkWems(fileData, entry, outputDir, mapper, mode, wemConverter);
                    continue;
                }

                string outName = ResolveOutputName(
                    entry.FileId,
                    mapper,
                    mode == "wav" ? ".wav" : ".wem",
                    content.Languages,
                    entry.LanguageId
                );
                string outPath = Path.Combine(outputDir, outName);
                EnsureDirectory(outPath);

                try
                {
                    if (mode == "wav" && wemConverter != null)
                    {
                        string tempWem = Path.Combine(
                            Path.GetTempPath(),
                            $"{entry.FileId}.wem"
                        );
                        try
                        {
                            File.WriteAllBytes(tempWem, fileData);
                            wemConverter.Convert(tempWem, outPath);
                        }
                        finally
                        {
                            try { File.Delete(tempWem); } catch { }
                        }
                    }
                    else
                    {
                        File.WriteAllBytes(outPath, fileData);
                    }
                    saved++;
                }
                catch (Exception ex)
                {
                    logger.Verbose($"  Failed {entry.FileId}: {ex.Message}");
                    if (mode == "wav")
                    {
                        string fallback = Path.ChangeExtension(outPath, ".wem");
                        EnsureDirectory(fallback);
                        try { File.WriteAllBytes(fallback, fileData); } catch { }
                    }
                    failed++;
                }
            }

            logger.Info($"  Extracted {saved} files" + (failed > 0 ? $", {failed} failed" : ""));
            totalSaved += saved;
            totalFailed += failed;
        }

        logger.Info(
            $"Done: {totalSaved} files extracted"
                + (totalFailed > 0 ? $", {totalFailed} failed" : "")
        );
    }

    private static void ExtractBnkWems(
        byte[] bnkData,
        PckFileEntry bankEntry,
        string outputDir,
        PckMapper? mapper,
        string mode,
        IWemConverter? wemConverter
    )
    {
        var wemEntries = BnkParser.Parse(bnkData);
        foreach (var wem in wemEntries)
        {
            byte[] wemData = new byte[wem.Size];
            Array.Copy(bnkData, wem.Offset, wemData, 0, wem.Size);

            string ext = mode == "wav" ? ".wav" : ".wem";
            string outName = ResolveBnkWemName(bankEntry.FileId, wem.Id, mapper, ext);
            string outPath = Path.Combine(outputDir, outName);
            EnsureDirectory(outPath);

            if (mode == "wav" && wemConverter != null)
            {
                string tempWem = Path.Combine(Path.GetTempPath(), $"{wem.Id}.wem");
                try
                {
                    File.WriteAllBytes(tempWem, wemData);
                    wemConverter.Convert(tempWem, outPath);
                }
                catch
                {
                    string fallback = Path.ChangeExtension(outPath, ".wem");
                    EnsureDirectory(fallback);
                    try { File.WriteAllBytes(fallback, wemData); } catch { }
                }
                finally
                {
                    try { File.Delete(tempWem); } catch { }
                }
            }
            else
            {
                File.WriteAllBytes(outPath, wemData);
            }
        }
    }

    // ── VFS reading helpers ─────────────────────────────────────────

    private static VFBlockMainInfo ReadBlockInfo(
        string vfsPath,
        EVFSBlockType blockType,
        byte[] key
    )
    {
        if (!VFSDumper.BlockHashMap.TryGetValue(blockType, out var hashName))
            throw new InvalidOperationException($"No hash mapping for block type {blockType}");

        var blcPath = Path.Combine(vfsPath, hashName, hashName + ".blc");
        if (!File.Exists(blcPath))
            throw new FileNotFoundException($"BLC file not found for {blockType}", blcPath);

        var blockFile = File.ReadAllBytes(blcPath);

        byte[] nonce = new byte[VFSDefine.BLOCK_HEAD_LEN];
        Buffer.BlockCopy(blockFile, 0, nonce, 0, nonce.Length);

        using var chacha = new CSChaCha20(key, nonce, 1);
        var decrypted = chacha.DecryptBytes(blockFile[VFSDefine.BLOCK_HEAD_LEN..]);
        Buffer.BlockCopy(decrypted, 0, blockFile, VFSDefine.BLOCK_HEAD_LEN, decrypted.Length);

        return new VFBlockMainInfo(blockFile, 0);
    }

    private static string? ResolveChunkPath(
        string vfsPath,
        EVFSBlockType blockType,
        in FVFBlockChunkInfo chunk
    )
    {
        if (!VFSDumper.BlockHashMap.TryGetValue(blockType, out var hashName))
            return null;

        var chunkFileName =
            chunk.md5Name.ToHexStringLittleEndian() + FVFBlockChunkInfo.FILE_EXTENSION;
        var path = Path.Combine(vfsPath, hashName, chunkFileName);
        return File.Exists(path) ? path : null;
    }

    private static byte[] ReadFileData(
        FileStream chunkFs,
        in FVFBlockFileInfo file,
        int version,
        byte[] key
    )
    {
        if (file.bUseEncrypt)
        {
            byte[] nonce = new byte[VFSDefine.BLOCK_HEAD_LEN];
            Buffer.BlockCopy(BitConverter.GetBytes(version), 0, nonce, 0, sizeof(int));
            Buffer.BlockCopy(BitConverter.GetBytes(file.ivSeed), 0, nonce, sizeof(int), sizeof(long));

            using var chacha = new CSChaCha20(key, nonce, 1);
            var encrypted = new byte[file.len];
            chunkFs.ReadExactly(encrypted);
            return chacha.DecryptBytes(encrypted);
        }

        var data = new byte[file.len];
        chunkFs.ReadExactly(data);
        return data;
    }

    // ── Output name resolution ──────────────────────────────────────

    private static string ResolveOutputName(
        ulong fileId,
        PckMapper? mapper,
        string extension,
        List<PckLanguage>? languages,
        uint languageId
    )
    {
        string? mapped = mapper?.GetMappedPath(fileId.ToString());
        if (mapped != null)
            return Path.ChangeExtension(mapped, extension);

        if (languageId != 0 && languages != null)
        {
            var lang = languages.Find(l => l.Id == languageId);
            if (lang != null)
                return Path.Combine("unmapped", lang.Name, $"{fileId}{extension}");
        }

        return Path.Combine("unmapped", $"{fileId}{extension}");
    }

    private static string ResolveBnkWemName(
        ulong bankFileId,
        uint wemId,
        PckMapper? mapper,
        string extension
    )
    {
        string? mapped = mapper?.GetMappedPath(wemId.ToString());
        if (mapped != null)
            return Path.ChangeExtension(mapped, extension);

        return Path.Combine("unmapped", $"{bankFileId}_{wemId}{extension}");
    }

    private static void EnsureDirectory(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (dir != null)
            Directory.CreateDirectory(dir);
    }

    private static IWemConverter? ResolveConverter(ILogger logger)
    {
        if (LibVgmstreamConverter.IsAvailable)
        {
            logger.Verbose("Engine: libvgmstream (DLL)");
            return new LibVgmstreamConverter();
        }

        if (WemConverter.VgmstreamPath != null)
        {
            logger.Verbose($"Engine: vgmstream-cli ({WemConverter.VgmstreamPath})");
            return new WemConverter();
        }

        Console.Error.WriteLine(
            "Error: vgmstream not found. Place libvgmstream.dll (preferred) "
                + "or vgmstream-cli next to the executable, or add to PATH."
        );
        return null;
    }
}
