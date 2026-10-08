using BydTools.Extraction;
using BydTools.Extraction.Vfs;
using BydTools.VFS;
using Spectre.Console;

namespace BydTools.CLI.Commands;

public sealed class VfsCommand : ICommand
{
    public string Name => "vfs";
    public string Description => "Parse VFS block declarations and dump assets";

    public int Execute(string[] args, CancellationToken cancellationToken)
    {
        var parser = CreateParser();
        if (!parser.Parse(args))
            return Fail(2, parser.Errors[0]);
        if (parser.HasFlag("help"))
        {
            PrintHelp(parser);
            return 0;
        }

        string? input = parser.Get("input");
        if (string.IsNullOrWhiteSpace(input))
            return Fail(2, "--input is required.");

        string gamePath = Path.GetFullPath(input);
        if (!Directory.Exists(gamePath))
            return Fail(1, "Game directory not found: \"{0}\"", gamePath);

        string vfsPath = Path.Combine(gamePath, VFSDefine.VfsDirectoryName);
        if (!Directory.Exists(vfsPath))
            return Fail(1, "VFS directory not found under \"{0}\".", gamePath);

        byte[] key;
        int jobs;
        try
        {
            key = VFSDefine.ResolveKey(parser.Get("platform"), parser.Get("key"));
            jobs = ResolveJobs(parser.Get("jobs"));
        }
        catch (ArgumentException ex)
        {
            return Fail(2, ex.Message);
        }

        bool debug = parser.HasFlag("debug");
        string? output = parser.Get("output");
        string? blockTypeArg = parser.Get("blocktype");
        if (!debug)
        {
            if (string.IsNullOrWhiteSpace(output))
                return Fail(2, "--output is required.");
            if (string.IsNullOrWhiteSpace(blockTypeArg))
                return Fail(2, "--blocktype is required.");
        }

        var logger = new Logger(parser.HasFlag("verbose"));
        VfsArchive archive;
        try
        {
            archive = VfsArchive.Open(vfsPath, key, logger);
        }
        catch (Exception ex)
        {
            return Fail(1, ex.Message);
        }

        var extractor = new VfsExtractor(logger);
        if (debug)
        {
            extractor.WriteDebugReport(archive);
            return 0;
        }

        var blocks = new List<VfsBlock>();
        foreach (string token in blockTypeArg!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var block = archive.Find(token);
            if (block == null)
            {
                bool known = BlockRegistry.TryParse(token, out _);
                return known
                    ? Fail(1, "Block \"{0}\" was not found in this VFS.", token)
                    : Fail(2, "Unknown block type: {0}", token);
            }

            blocks.Add(block);
        }

        string outputPath = Path.GetFullPath(output!);
        try
        {
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
                    foreach (var block in blocks)
                    {
                        var task = ctx.AddTask(block.DisplayName, maxValue: 0);
                        extractor.Dump(
                            block,
                            outputPath,
                            jobs,
                            new SpectreProgress(task),
                            cancellationToken
                        );
                    }
                });
        }
        catch (Exception ex) when (IsCancel(ex))
        {
            return Fail(1, "Cancelled.");
        }
        catch (Exception ex)
        {
            return Fail(1, ex.Message);
        }

        return 0;
    }

    internal static ArgParser CreateParser() =>
        new ArgParser()
            .Add("input", "i", "Game data directory that contains the VFS folder", "path", required: true)
            .Add("output", "o", "Output directory for extracted files", "dir", required: true)
            .Add(
                "blocktype",
                "t",
                "Block type to extract",
                "type",
                required: true,
                "Separate multiple types with commas."
            )
            .Add("key", null, "ChaCha20 key, Base64 of 32 bytes. Overrides --platform", "base64")
            .Add("platform", null, "Key to use when --key is omitted", "name", continuation: "pc (default) or android.")
            .Add("jobs", null, "How many chunks to read at once", "n", continuation: "Default: processor count.")
            .Add("debug", "d", "Scan block declarations without extracting")
            .Add("verbose", "v", "Print per-chunk and per-file details")
            .Add("help", "h", "Show help information");

    private static void PrintHelp(ArgParser parser)
    {
        HelpFormatter.WriteCommandHelp(
            "VFS asset dumper",
            "vfs --input <path> --output <dir> --blocktype <type>",
            parser,
            () => HelpFormatter.WriteEnumValues("Available block types", BlockRegistry.All.Select(static d => d.Type))
        );
    }

    internal static int ResolveJobs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Environment.ProcessorCount;
        if (!int.TryParse(text, out int jobs) || jobs < 1)
            throw new ArgumentException("--jobs must be a positive integer.");
        return jobs;
    }

    internal static int Fail(int code, string format, params object[] args)
    {
        Logger.WriteError(args.Length == 0 ? format : string.Format(format, args));
        return code;
    }

    internal static bool IsCancel(Exception ex) =>
        ex is OperationCanceledException
        || (ex is AggregateException aggregate && aggregate.InnerExceptions.All(IsCancel));
}

internal sealed class SpectreProgress(ProgressTask task) : IProgressSink
{
    public void AddTotal(long amount) => task.MaxValue += amount;

    public void Advance(long amount) => task.Increment(amount);
}
