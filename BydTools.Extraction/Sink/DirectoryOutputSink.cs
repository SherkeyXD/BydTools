namespace BydTools.Extraction.Sink;

public interface IOutputSink
{
    OutputFile Create(string relativePath);
}

/// <summary>
/// Writes to <c>path.partial</c> and moves into place only after <see cref="OutputFile.Complete"/>.
/// Disposing without completing deletes the partial file, including on cancellation.
/// </summary>
public sealed class OutputFile : FileStream
{
    private readonly string _partial;
    private readonly string _final;
    private bool _complete;
    private int _disposed;

    internal OutputFile(string partial, string final)
        : base(partial, FileMode.Create, FileAccess.Write, FileShare.None)
    {
        _partial = partial;
        _final = final;
    }

    public void Complete() => _complete = true;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_complete)
            File.Move(_partial, _final, overwrite: true);
        else if (File.Exists(_partial))
            File.Delete(_partial);
    }
}

public sealed class DirectoryOutputSink : IOutputSink
{
    private readonly string _root;

    public DirectoryOutputSink(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public OutputFile Create(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Refusing path \"{relativePath}\".");

        string full = Path.GetFullPath(Path.Combine(_root, relativePath));
        string prefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path \"{relativePath}\" escapes the output directory.");

        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        return new OutputFile(full + ".partial", full);
    }
}
