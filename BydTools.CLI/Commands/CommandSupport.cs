using BydTools.Extraction;
using BydTools.VFS;
using Spectre.Console;

namespace BydTools.CLI.Commands;

internal static class CliHelpers
{
    public static ArgParser AddInputAndOutput(this ArgParser parser, string outputDescription) =>
        parser
            .Add("input", "i", "Game data directory that contains the VFS folder", "path", required: true)
            .Add("persistent", "p", "Persistent directory containing VFS; missing files fall back to --input", "path")
            .Add("output", "o", outputDescription, "dir", required: true);

    public static ArgParser AddCryptoAndJobs(this ArgParser parser, string jobsDescription) =>
        parser
            .Add("key", null, "ChaCha20 key, Base64 of 32 bytes. Overrides --platform", "base64")
            .Add("platform", null, "Key to use when --key is omitted", "name", continuation: "pc (default) or android.")
            .Add("jobs", null, jobsDescription, "n", continuation: "Default: processor count.");

    public static bool TryResolveVfsDirectory(string input, out string vfsPath, out string? error)
    {
        string gamePath = Path.GetFullPath(input);
        if (!Directory.Exists(gamePath))
        {
            vfsPath = "";
            error = $"Game directory not found: \"{gamePath}\"";
            return false;
        }

        vfsPath = Path.Combine(gamePath, VFSDefine.VfsDirectoryName);
        if (!Directory.Exists(vfsPath))
        {
            error = $"VFS directory not found under \"{gamePath}\".";
            return false;
        }

        error = null;
        return true;
    }

    public static string? ResolvePersistentVfs(string? persistent, Core.ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(persistent))
            return null;

        string persistentPath = Path.GetFullPath(persistent);
        string persistentVfs = Path.Combine(persistentPath, VFSDefine.VfsDirectoryName);
        if (!Directory.Exists(persistentPath))
        {
            logger.Info("Warning: Persistent directory not found: \"{0}\". Falling back to --input.", persistentPath);
        }
        else if (!Directory.Exists(persistentVfs))
        {
            string trimmed = persistentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.GetFileName(trimmed).Equals(VFSDefine.VfsDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                logger.Info(
                    "Warning: VFS directory not found under \"{0}\". Pass the Persistent directory rather than its VFS subfolder; falling back to --input.",
                    persistentPath
                );
            }
            else
            {
                logger.Info("Warning: VFS directory not found under \"{0}\". Falling back to --input.", persistentPath);
            }
        }

        return persistentVfs;
    }

    public static int ResolveJobs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Environment.ProcessorCount;
        if (!int.TryParse(text, out int jobs) || jobs < 1)
            throw new ArgumentException("--jobs must be a positive integer.");
        return jobs;
    }

    public static void RunWithProgress(
        Logger logger,
        bool verbose,
        Action<Func<string, IProgressSink>> execute)
    {
        if (verbose)
        {
            execute(static _ => NullProgress.Instance);
            return;
        }

        using (logger.BeginBuffering())
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
                .Start(ctx => execute(name => new SpectreProgress(ctx.AddTask(name, maxValue: 0))));
        }
    }

    public static int Fail(int code, string format, params object[] args)
    {
        Logger.WriteError(args.Length == 0 ? format : string.Format(format, args));
        return code;
    }

    public static bool IsCancel(Exception ex) =>
        ex is OperationCanceledException
        || (ex is AggregateException aggregate && aggregate.InnerExceptions.All(IsCancel));
}

internal sealed class SpectreProgress(ProgressTask task) : IProgressSink
{
    private readonly Lock _lock = new();

    public void AddTotal(long amount)
    {
        lock (_lock)
            task.MaxValue += amount;
    }

    public void Advance(long amount)
    {
        lock (_lock)
            task.Increment(amount);
    }
}
