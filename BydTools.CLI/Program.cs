using BydTools.CLI.Commands;

namespace BydTools.CLI;

public static class Program
{
    private static CancellationTokenSource? _cancellation;
    private static int _cancelHooked;

    public static int Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        if (Interlocked.Exchange(ref _cancelHooked, 1) == 0)
        {
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                _cancellation?.Cancel();
            };
        }

        var commands = new ICommand[] { new VfsCommand(), new PckCommand() };
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            HelpFormatter.WriteRootHelp(commands);
            return 0;
        }

        var command = commands.FirstOrDefault(c =>
            c.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase)
        );
        if (command == null)
        {
            Logger.WriteError("Unknown command: {0}", args[0]);
            Console.Error.WriteLine();
            HelpFormatter.WriteRootHelp(commands);
            return 2;
        }

        try
        {
            return command.Execute(args[1..], cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Logger.WriteError("Cancelled.");
            return 1;
        }
    }
}
