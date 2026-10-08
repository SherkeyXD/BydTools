namespace BydTools.Core;

/// <summary>Shared logger for library code. The CLI supplies the console implementation.</summary>
public interface ILogger
{
    void Info(string message);
    void Info(string format, params object[] args);
    void Verbose(string message);
    void Verbose(string format, params object[] args);
    void Error(string message);
    void Error(string format, params object[] args);
}
