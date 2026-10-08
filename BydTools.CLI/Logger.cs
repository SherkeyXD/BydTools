namespace BydTools.CLI;

public sealed class Logger : BydTools.Core.ILogger
{
    private readonly bool _verbose;

    public Logger(bool verbose) => _verbose = verbose;

    public void Info(string message) => Console.WriteLine(message);

    public void Info(string format, params object[] args) => Console.WriteLine(format, args);

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

    public void Error(string message) => WriteError(message);

    public void Error(string format, params object[] args) => WriteError(format, args);

    public static void WriteError(string message) => Console.Error.WriteLine(message);

    public static void WriteError(string format, params object[] args) =>
        Console.Error.WriteLine(format, args);
}
