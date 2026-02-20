using BydTools.Utils;

namespace BydTools.CLI;

/// <summary>
/// Console logger with support for verbose mode.
/// </summary>
public class Logger : ILogger
{
    private readonly bool _verbose;

    public Logger(bool verbose = false)
    {
        _verbose = verbose;
    }

    public void Info(string message) => Console.WriteLine(message);

    public void Info(string format, params object[] args) => Console.WriteLine(format, args);

    public void InfoNoNewline(string message) => Console.Write(message);

    public void Verbose(string message)
    {
        if (_verbose)
            Console.WriteLine(message);
    }

    public void Verbose(string format, params object[] args)
    {
        if (_verbose)
            Console.WriteLine(format, args);
    }

    public void VerboseNoNewline(string message)
    {
        if (_verbose)
            Console.Write(message);
    }

    public void Error(string message) => Console.Error.WriteLine(message);

    public void Error(string format, params object[] args) => Console.Error.WriteLine(format, args);
}
