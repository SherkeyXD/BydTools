using System.Collections.Concurrent;
using BydTools.PCK;
using BydTools.Utils;
using BydTools.VFS;
using BydTools.VFS.SparkBuffer;
using BydTools.Wwise;
using Spectre.Console;

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
                Logger.WriteError(error);
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
            Logger.WriteError("--input is required.");
            PrintHelp(Program.ExecutableName);
            return;
        }

        var outputDir = parser.GetValue("output");
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            Logger.WriteError("--output is required.");
            PrintHelp(Program.ExecutableName);
            return;
        }

        var typeStr = parser.GetValue("type");
        if (string.IsNullOrWhiteSpace(typeStr))
        {
            Logger.WriteError("--type is required.");
            HelpFormatter.WriteBlankLine();
            WriteAudioBlockTypes();
            return;
        }

        if (
            !Enum.TryParse<EVFSBlockType>(typeStr, ignoreCase: true, out var blockType)
            || !AudioBlockTypeNames.Contains(blockType.ToString())
        )
        {
            Logger.WriteError($"'{typeStr}' is not a valid audio block type.");
            HelpFormatter.WriteBlankLine();
            WriteAudioBlockTypes();
            return;
        }

        var mode = parser.GetValue("mode") ?? "wav";
        if (mode is not ("raw" or "wav"))
        {
            Logger.WriteError("--mode must be one of: raw, wav");
            PrintHelp(Program.ExecutableName);
            return;
        }

        var vfsPath = Path.Combine(gamePath, VFSDefine.VFS_DIR);
        if (!Directory.Exists(vfsPath))
        {
            Logger.WriteError(
                $"VFS directory ({VFSDefine.VFS_DIR}) not found under \"{gamePath}\"."
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
                Logger.WriteError("--key must be a valid Base64 string.");
                return;
            }
            if (chaChaKey.Length != VFSDefine.KEY_LEN)
            {
                Logger.WriteError(
                    $"--key must decode to {VFSDefine.KEY_LEN} bytes (got {chaChaKey.Length})."
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

            AnsiConsole.MarkupLine($"[bold]Input:[/]  {Markup.Escape(vfsPath)}");
            AnsiConsole.MarkupLine($"[bold]Output:[/] {Markup.Escape(outputDir)}");
            AnsiConsole.MarkupLine($"[bold]Type:[/]   {Markup.Escape(blockType.ToString())}");
            AnsiConsole.MarkupLine($"[bold]Mode:[/]   {Markup.Escape(mode)}");

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
            Logger.WriteError(ex.Message);
            Environment.Exit(1);
        }
    }

    private static void WriteAudioBlockTypes()
    {
        HelpFormatter.WriteSectionHeader("Audio block types");
        AnsiConsole.MarkupLine(
            $"  [grey]{Markup.Escape(string.Join(", ", AudioBlockTypeNames))}[/]"
        );
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

        var tableInfo = VfsReader.ReadBlockInfo(vfsPath, EVFSBlockType.Table, key);

        foreach (var chunk in tableInfo.allChunks)
        {
            var chunkFile = VfsReader.ResolveChunkPath(vfsPath, EVFSBlockType.Table, chunk);
            if (chunkFile == null)
                continue;

            using var chunkFs = File.OpenRead(chunkFile);
            foreach (var file in chunk.files)
            {
                if (!file.fileName.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
                    continue;

                chunkFs.Seek(file.offset, SeekOrigin.Begin);
                var data = VfsReader.ReadFileData(chunkFs, file, tableInfo.version, key);

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
        var blockInfo = VfsReader.ReadBlockInfo(vfsPath, blockType, key);
        AnsiConsole.Write(
            new Rule($"[blue]{Markup.Escape(blockType.ToString())}[/]").LeftJustified()
        );

        var pckFiles = new List<(string Name, byte[] Data)>();
        foreach (var chunk in blockInfo.allChunks)
        {
            var chunkFile = VfsReader.ResolveChunkPath(vfsPath, blockType, chunk);
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
                var data = VfsReader.ReadFileData(chunkFs, file, blockInfo.version, key);
                pckFiles.Add((file.fileName, data));
            }
        }

        if (pckFiles.Count == 0)
        {
            logger.Info("No PCK files found in block.");
            return;
        }

        logger.Info($"Found {pckFiles.Count} PCK file(s)");

        string ext = mode == "wav" ? ".wav" : ".wem";
        var jobs = new List<AudioJob>();

        foreach (var (pckName, pckData) in pckFiles)
        {
            logger.Info($"Parsing {pckName}...");

            using var pckStream = new MemoryStream(pckData);
            var pckParser = new PckParser(pckStream);
            var content = pckParser.Parse();

            logger.Verbose(
                $"  {content.Entries.Count} entries, {content.Languages.Count} languages"
            );

            foreach (var entry in content.Entries)
            {
                byte[] fileData = pckParser.GetFileData(entry);
                if (fileData.Length < 4)
                    continue;

                ReadOnlySpan<byte> magic = fileData.AsSpan(0, 4);

                if (magic.SequenceEqual("BKHD"u8))
                {
                    CollectBnkJobs(fileData, entry, outputDir, mapper, ext, jobs);
                }
                else if (magic.SequenceEqual("RIFF"u8) || magic.SequenceEqual("RIFX"u8))
                {
                    string outName = ResolveOutputName(
                        entry.FileId,
                        mapper,
                        ext,
                        content.Languages,
                        entry.LanguageId
                    );
                    jobs.Add(new AudioJob(fileData, Path.Combine(outputDir, outName)));
                }
            }
        }

        AnsiConsole.MarkupLine(
            $"Collected [blue]{jobs.Count}[/] audio files, processing [blue]{Markup.Escape(mode)}[/]..."
        );

        if (mode == "raw" || wemConverter == null)
        {
            AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn()
                )
                .Start(ctx =>
                {
                    var task = ctx.AddTask("Extracting", maxValue: jobs.Count);
                    foreach (var job in jobs)
                    {
                        EnsureDirectory(job.OutputPath);
                        File.WriteAllBytes(job.OutputPath, job.Data);
                        task.Increment(1);
                    }
                });
            AnsiConsole.MarkupLine($"[green]Done:[/] {jobs.Count} files extracted.");
            return;
        }

        int converted = 0;
        int failed = 0;
        var failMessages = new ConcurrentBag<string>();

        AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn()
            )
            .Start(ctx =>
            {
                var task = ctx.AddTask("Converting", maxValue: jobs.Count);

                Parallel.ForEach(
                    jobs,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                    },
                    job =>
                    {
                        EnsureDirectory(job.OutputPath);

                        string wemName =
                            Path.GetFileNameWithoutExtension(job.OutputPath) + ".wem";

                        try
                        {
                            wemConverter.Convert(job.Data, wemName, job.OutputPath);
                            Interlocked.Increment(ref converted);
                        }
                        catch (Exception ex)
                        {
                            string fallback = Path.ChangeExtension(job.OutputPath, ".wem");
                            EnsureDirectory(fallback);
                            try
                            {
                                File.WriteAllBytes(fallback, job.Data);
                            }
                            catch { }

                            failMessages.Add(ex.Message);
                            Interlocked.Increment(ref failed);
                        }

                        task.Increment(1);
                    }
                );
            });

        foreach (var msg in failMessages)
            logger.Verbose($"  Failed: {msg}");

        AnsiConsole.MarkupLine(
            $"[green]Done:[/] {converted} converted"
                + (failed > 0 ? $", [yellow]{failed} failed[/] (saved as .wem)" : "")
        );
    }

    private static void CollectBnkJobs(
        byte[] bnkData,
        PckFileEntry bankEntry,
        string outputDir,
        PckMapper? mapper,
        string extension,
        List<AudioJob> jobs
    )
    {
        var wemEntries = BnkParser.Parse(bnkData);
        foreach (var wem in wemEntries)
        {
            byte[] wemData = new byte[wem.Size];
            Array.Copy(bnkData, wem.Offset, wemData, 0, wem.Size);

            string outName = ResolveBnkWemName(bankEntry.FileId, wem.Id, mapper, extension);
            jobs.Add(new AudioJob(wemData, Path.Combine(outputDir, outName)));
        }
    }

    private record AudioJob(byte[] Data, string OutputPath);

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

        Logger.WriteError(
            "vgmstream not found. Place libvgmstream.dll (preferred) "
                + "or vgmstream-cli next to the executable, or add to PATH."
        );
        return null;
    }
}
