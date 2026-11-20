namespace BydTools.CLI;

public sealed class Logger : BydTools.Core.ILogger
{
    private readonly bool _verbose;
    private readonly Lock _lock = new();
    private readonly List<(bool IsError, string Message)> _buffered = [];
    private int _bufferDepth;

    public Logger(bool verbose) => _verbose = verbose;

    public IDisposable BeginBuffering()
    {
        lock (_lock)
            _bufferDepth++;
        return new BufferScope(this);
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
            if (_bufferDepth > 0)
            {
                _buffered.Add((isError, message));
                return;
            }

            WriteDirect(isError, message);
        }
    }

    private void EndBuffering()
    {
        lock (_lock)
        {
            if (_bufferDepth == 0 || --_bufferDepth > 0)
                return;

            foreach (var (isError, message) in _buffered)
                WriteDirect(isError, message);
            _buffered.Clear();
        }
    }

    private static void WriteDirect(bool isError, string message) =>
        (isError ? Console.Error : Console.Out).WriteLine(message);

    private sealed class BufferScope(Logger owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.EndBuffering();
        }
    }
}
