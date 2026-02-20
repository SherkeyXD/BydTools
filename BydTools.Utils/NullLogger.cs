namespace BydTools.Utils;

/// <summary>
/// No-op logger implementation that silently discards all messages.
/// Use when logging is not needed but a non-null ILogger is required.
/// </summary>
public sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();

    private NullLogger() { }

    public void Info(string message) { }

    public void Info(string format, params object[] args) { }

    public void InfoNoNewline(string message) { }

    public void Verbose(string message) { }

    public void Verbose(string format, params object[] args) { }

    public void VerboseNoNewline(string message) { }

    public void Error(string message) { }

    public void Error(string format, params object[] args) { }
}
