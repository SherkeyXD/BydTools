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
        if (!CliHelpers.TryParseAndValidate(parser, args, PrintHelp, out int validationExit))
            return validationExit;

        string modeArg = parser.Get("mode") ?? "wav";
        if (modeArg is not ("wav" or "raw"))
            return CliHelpers.Fail(2, "--mode must be one of: wav, raw.");
        var mode = modeArg == "raw" ? AudioExportMode.Raw : AudioExportMode.Wav;

        if (!VfsSession.TryOpen(parser, requireOutput: true, out VfsSession? session, out int openExit))
            return openExit;

        if (!session.TryFindBlock(parser.Get("type")!, out VfsBlock? block, out int findExit))
            return findExit;

        if (block.Descriptor is { Kind: not ContentKind.Audio })
            session.Logger.Info("Warning: {0} is not an audio block. Only .pck files inside it will be exported.", block.DisplayName);

        using IWemDecoder? decoder = mode == AudioExportMode.Wav ? WemDecoders.Create() : null;
        string outputPath = session.OutputPath!;
        bool mapNames = !parser.HasFlag("no-map");

        return session.RunWithProgress(createProgress =>
        {
            AudioExportResult result = new AudioExportService(session.Logger).Export(
                session.Archive,
                block,
                outputPath,
                mode,
                mapNames,
                decoder,
                session.Jobs,
                createProgress("Exporting"),
                cancellationToken
            );

            session.Logger.Info(
                "Done. {0} files extracted, {1} converted, {2} failed.",
                result.Extracted,
                result.Converted,
                result.Failed
            );
            return result.Failed > 0 ? 1 : 0;
        });
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
