using Spectre.Console;

namespace BydTools.CLI;

public sealed class Logger : BydTools.Core.ILogger
{
    private readonly bool _verbose;
    private readonly Lock _lock = new();
    private readonly List<string> _bufferedErrors = [];
    private int _liveDepth;

    public Logger(bool verbose) => _verbose = verbose;

    public bool IsVerboseEnabled => _verbose;

    public IDisposable BeginLiveProgress()
    {
        lock (_lock)
            _liveDepth++;
        return new LiveScope(this);
    }

    public void Info(string message) => Write(isError: false, message);

    public void Info(string format, params object[] args) =>
        Write(isError: false, args.Length == 0 ? format : string.Format(format, args));

    public void Verbose(string message)
    {
        if (_verbose)
            Write(isError: false, message);
    }

    public void Verbose(string format, params object[] args)
    {
        if (_verbose)
            Write(isError: false, args.Length == 0 ? format : string.Format(format, args));
    }

    public void Error(string message) => Write(isError: true, message);

    public void Error(string format, params object[] args) =>
        Write(isError: true, args.Length == 0 ? format : string.Format(format, args));

    public static void WriteError(string message) => Console.Error.WriteLine(message);

    public static void WriteError(string format, params object[] args) =>
        Console.Error.WriteLine(format, args);

    private void Write(bool isError, string message)
    {
        lock (_lock)
        {
            if (_liveDepth > 0)
            {
                if (isError)
                    _bufferedErrors.Add(message);
                else
                    AnsiConsole.WriteLine(message);
                return;
            }

            (isError ? Console.Error : Console.Out).WriteLine(message);
        }
    }

    private void EndLiveProgress()
    {
        lock (_lock)
        {
            if (_liveDepth == 0 || --_liveDepth > 0)
                return;

            foreach (string message in _bufferedErrors)
                Console.Error.WriteLine(message);
            _bufferedErrors.Clear();
        }
    }

    private sealed class LiveScope(Logger owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndLiveProgress();
        }
    }
}
