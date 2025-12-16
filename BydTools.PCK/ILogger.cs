namespace BydTools.PCK;

/// <summary>
/// Simple logger interface for PCK operations.
/// </summary>
public interface ILogger
{
    void Info(string message);
    void Info(string format, params object[] args);
    void InfoNoNewline(string message);
    void Verbose(string message);
    void Verbose(string format, params object[] args);
    void VerboseNoNewline(string message);
    void Error(string message);
    void Error(string format, params object[] args);
}

