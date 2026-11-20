using System.Diagnostics.CodeAnalysis;
using BydTools.Extraction;
using BydTools.VFS;

namespace BydTools.CLI.Commands;

internal sealed class VfsSession
{
    private VfsSession(VfsArchive archive, Logger logger, int jobs, bool verbose, string? outputPath)
    {
        Archive = archive;
        Logger = logger;
        Jobs = jobs;
        Verbose = verbose;
        OutputPath = outputPath;
    }

    public VfsArchive Archive { get; }
    public Logger Logger { get; }
    public int Jobs { get; }
    public bool Verbose { get; }
    public string? OutputPath { get; }

    public static bool TryOpen(
        ArgParser parser,
        bool requireOutput,
        [NotNullWhen(true)] out VfsSession? session,
        out int exitCode)
    {
        session = null;

        byte[] key;
        int jobs;
        try
        {
            key = CliHelpers.ResolveKey(parser.Get("platform"), parser.Get("key"));
            jobs = CliHelpers.ResolveJobs(parser.Get("jobs"));
        }
        catch (ArgumentException ex)
        {
            exitCode = CliHelpers.Fail(2, ex.Message);
            return false;
        }

        if (!CliHelpers.TryResolveVfsDirectory(parser.Get("input")!, out string vfsPath, out string? vfsError))
        {
            exitCode = CliHelpers.Fail(1, vfsError!);
            return false;
        }

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
            exitCode = CliHelpers.Fail(1, ex.Message);
            return false;
        }

        string? outputPath = requireOutput ? Path.GetFullPath(parser.Get("output")!) : null;
        session = new VfsSession(archive, logger, jobs, verbose, outputPath);
        exitCode = 0;
        return true;
    }

    public bool TryFindBlock(string token, [NotNullWhen(true)] out VfsBlock? block, out int exitCode)
    {
        block = Archive.Find(token);
        if (block != null)
        {
            exitCode = 0;
            return true;
        }

        bool known = BlockRegistry.TryParse(token, out _);
        exitCode = known
            ? CliHelpers.Fail(1, "Block \"{0}\" was not found in this VFS.", token)
            : CliHelpers.Fail(2, "Unknown block type: {0}", token);
        return false;
    }

    public int RunWithProgress(Func<Func<string, IProgressSink>, int> execute)
    {
        try
        {
            int exitCode = 0;
            CliHelpers.RunWithProgress(Logger, Verbose, createProgress => exitCode = execute(createProgress));
            return exitCode;
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
}
