using BydTools.Audio.Wem;
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
            return CliHelpers.Fail(2, parser.Errors[0]);
        if (parser.HasFlag("help"))
        {
            PrintHelp(parser);
            return 0;
        }

        if (!parser.ValidateRequired())
            return CliHelpers.Fail(2, parser.Errors[0]);

        string modeArg = parser.Get("mode") ?? "wav";
        if (modeArg is not ("wav" or "raw"))
            return CliHelpers.Fail(2, "--mode must be one of: wav, raw.");
        var mode = modeArg == "raw" ? AudioExportMode.Raw : AudioExportMode.Wav;

        byte[] key;
        int jobs;
        try
        {
            key = VFSDefine.ResolveKey(parser.Get("platform"), parser.Get("key"));
            jobs = CliHelpers.ResolveJobs(parser.Get("jobs"));
        }
        catch (ArgumentException ex)
        {
            return CliHelpers.Fail(2, ex.Message);
        }

        if (!CliHelpers.TryResolveVfsDirectory(parser.Get("input")!, out string vfsPath, out string? vfsError))
            return CliHelpers.Fail(1, vfsError!);

        bool verbose = parser.HasFlag("verbose");
        var logger = new Logger(verbose);
        VfsArchive archive;
        try
        {
            string? persistentVfs = CliHelpers.ResolvePersistentVfs(parser.Get("persistent"), logger);
            archive = VfsArchive.Open(vfsPath, key, logger, persistentVfs);
        }
        catch (Exception ex)
        {
            return CliHelpers.Fail(1, ex.Message);
        }

        string typeArg = parser.Get("type")!;
        var block = archive.Find(typeArg);
        if (block == null)
        {
            return BlockRegistry.TryParse(typeArg, out _)
                ? CliHelpers.Fail(1, "Block \"{0}\" was not found in this VFS.", typeArg)
                : CliHelpers.Fail(2, "Unknown block type: {0}", typeArg);
        }

        if (block.Descriptor is { Kind: not ContentKind.Audio })
            logger.Info("Warning: {0} is not an audio block. Only .pck files inside it will be exported.", block.DisplayName);

        using IWemDecoder? decoder = mode == AudioExportMode.Wav ? WemDecoders.Create() : null;

        string outputPath = Path.GetFullPath(parser.Get("output")!);
        bool mapNames = !parser.HasFlag("no-map");
        try
        {
            AudioExportResult? result = null;
            CliHelpers.RunWithProgress(logger, verbose, createProgress =>
            {
                result = new AudioExportService(logger).Export(
                    archive,
                    block,
                    outputPath,
                    mode,
                    mapNames,
                    decoder,
                    jobs,
                    createProgress("Exporting"),
                    cancellationToken
                );
            });

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
        catch (Exception ex) when (CliHelpers.IsCancel(ex))
        {
            return CliHelpers.Fail(1, "Cancelled.");
        }
        catch (Exception ex)
        {
            return CliHelpers.Fail(1, ex.Message);
        }
    }

    private static ArgParser CreateParser() =>
        new ArgParser()
            .AddInputAndOutput("Output directory")
            .Add("type", "t", "Audio block type", "type", required: true)
            .Add("mode", "m", "Output mode", "mode", continuation: "wav (default) or raw.")
            .AddCryptoAndJobs("How many WEM files to convert at once")
            .Add("no-map", null, "Skip AudioDialog name mapping")
            .Add("verbose", "v", "Print per-file details")
            .Add("help", "h", "Show help information");

    private static void PrintHelp(ArgParser parser)
    {
        HelpFormatter.WriteCommandHelp(
            "PCK audio extractor",
            "pck --input <path> --output <dir> --type <type> [--persistent <path>]",
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
