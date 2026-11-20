using BydTools.CLI.Commands;

namespace BydTools.CLI;

public static class Program
{
    public static int Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            if (!cancellation.IsCancellationRequested)
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            }
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
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

            return command.Execute(args[1..], cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Logger.WriteError("Cancelled.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
