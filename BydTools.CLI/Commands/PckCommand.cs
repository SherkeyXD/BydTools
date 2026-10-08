using BydTools.Audio.Wem;
using BydTools.Extraction;
using BydTools.Extraction.Audio;
using BydTools.VFS;
using Spectre.Console;

namespace BydTools.CLI.Commands;

public sealed class PckCommand : ICommand
{
    public string Name => "pck";
    public string Description => "Extract audio from a VFS block and convert WEM to WAV";

    public int Execute(string[] args, CancellationToken cancellationToken)
    {
        var parser = CreateParser();
        if (!parser.Parse(args))
            return VfsCommand.Fail(2, parser.Errors[0]);
        if (parser.HasFlag("help"))
        {
            PrintHelp(parser);
            return 0;
        }

        string? input = parser.Get("input");
        string? output = parser.Get("output");
        string? typeArg = parser.Get("type");
        if (string.IsNullOrWhiteSpace(input))
            return VfsCommand.Fail(2, "--input is required.");
        if (string.IsNullOrWhiteSpace(output))
            return VfsCommand.Fail(2, "--output is required.");
        if (string.IsNullOrWhiteSpace(typeArg))
            return VfsCommand.Fail(2, "--type is required.");

        string modeArg = parser.Get("mode") ?? "wav";
        if (modeArg is not ("wav" or "raw"))
            return VfsCommand.Fail(2, "--mode must be one of: wav, raw.");
        var mode = modeArg == "raw" ? AudioExportMode.Raw : AudioExportMode.Wav;

        byte[] key;
        int jobs;
        try
        {
            key = VFSDefine.ResolveKey(parser.Get("platform"), parser.Get("key"));
            jobs = VfsCommand.ResolveJobs(parser.Get("jobs"));
        }
        catch (ArgumentException ex)
        {
            return VfsCommand.Fail(2, ex.Message);
        }

        string gamePath = Path.GetFullPath(input);
        if (!Directory.Exists(gamePath))
            return VfsCommand.Fail(1, "Game directory not found: \"{0}\"", gamePath);
        string vfsPath = Path.Combine(gamePath, VFSDefine.VfsDirectoryName);
        if (!Directory.Exists(vfsPath))
            return VfsCommand.Fail(1, "VFS directory not found under \"{0}\".", gamePath);

        var logger = new Logger(parser.HasFlag("verbose"));
        VfsArchive archive;
        try
        {
            archive = VfsArchive.Open(vfsPath, key, logger);
        }
        catch (Exception ex)
        {
            return VfsCommand.Fail(1, ex.Message);
        }

        var block = archive.Find(typeArg);
        if (block == null)
        {
            return BlockRegistry.TryParse(typeArg, out _)
                ? VfsCommand.Fail(1, "Block \"{0}\" was not found in this VFS.", typeArg)
                : VfsCommand.Fail(2, "Unknown block type: {0}", typeArg);
        }

        if (block.Descriptor is { Kind: not ContentKind.Audio })
            logger.Info("Warning: {0} is not an audio block. Only .pck files inside it will be exported.", block.DisplayName);

        IWemDecoder? decoder = null;
        if (mode == AudioExportMode.Wav)
        {
            decoder = WemDecoders.TryCreate(logger);
            if (decoder == null)
            {
                return VfsCommand.Fail(
                    1,
                    "No WEM decoder found. Place libvgmstream next to the program, or install vgmstream-cli."
                );
            }
        }

        string outputPath = Path.GetFullPath(output);
        try
        {
            AudioExportResult? result = null;
            AnsiConsole
                .Progress()
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
                    var task = ctx.AddTask("Exporting", maxValue: 0);
                    result = new AudioExportService(logger).Export(
                        archive,
                        block,
                        outputPath,
                        mode,
                        mapNames: !parser.HasFlag("no-map"),
                        decoder,
                        jobs,
                        new SpectreProgress(task),
                        cancellationToken
                    );
                });

            decoder?.Dispose();
            if (result == null)
                return 1;
            logger.Info(
                "Done. {0} files extracted, {1} converted, {2} failed.",
                result.Extracted,
                result.Converted,
                result.Failed
            );
            return result.Failed > 0 && result.Extracted == 0 ? 1 : 0;
        }
        catch (Exception ex) when (VfsCommand.IsCancel(ex))
        {
            decoder?.Dispose();
            return VfsCommand.Fail(1, "Cancelled.");
        }
        catch (Exception ex)
        {
            decoder?.Dispose();
            return VfsCommand.Fail(1, ex.Message);
        }
    }

    private static ArgParser CreateParser() =>
        new ArgParser()
            .Add("input", "i", "Game data directory that contains the VFS folder", "path", required: true)
            .Add("output", "o", "Output directory", "dir", required: true)
            .Add("type", "t", "Audio block type", "type", required: true)
            .Add("mode", "m", "Output mode", "mode", continuation: "wav (default) or raw.")
            .Add("key", null, "ChaCha20 key, Base64 of 32 bytes. Overrides --platform", "base64")
            .Add("platform", null, "Key to use when --key is omitted", "name", continuation: "pc (default) or android.")
            .Add("jobs", null, "How many WEM files to convert at once", "n", continuation: "Default: processor count.")
            .Add("no-map", null, "Skip AudioDialog name mapping")
            .Add("verbose", "v", "Print per-file details")
            .Add("help", "h", "Show help information");

    private static void PrintHelp(ArgParser parser)
    {
        HelpFormatter.WriteCommandHelp(
            "PCK audio extractor",
            "pck --input <path> --output <dir> --type <type>",
            parser,
            () =>
            {
                HelpFormatter.WriteSection("Audio block types");
                AnsiConsole.MarkupLine(
                    "  [cyan]{0}[/]",
                    Markup.Escape(string.Join(", ", BlockRegistry.Audio.Select(static d => d.Type.ToString())))
                );
                AnsiConsole.MarkupLine("  [dim]A block discovered in this VFS can also be passed by its groupCfgName.[/]");
                AnsiConsole.WriteLine();
            }
        );
    }
}
