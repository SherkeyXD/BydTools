using System.Reflection;
using BydTools.CLI.Commands;

namespace BydTools.CLI;

class Program
{
    private static readonly ICommand[] Commands =
    [
        new VfsCommand(),
        new PckCommand(),
    ];

    internal static string ExecutableName { get; } =
        Path.GetFileNameWithoutExtension(
            Assembly.GetEntryAssembly()?.GetName().Name
                ?? Assembly.GetExecutingAssembly().GetName().Name
                ?? "BydTools.CLI"
        );

    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return;
        }

        var subcommand = args[0].ToLowerInvariant();

        if (subcommand is "-h" or "--help")
        {
            PrintHelp();
            return;
        }

        var command = Array.Find(Commands, c => c.Name == subcommand);
        if (command == null)
        {
            Console.Error.WriteLine("Unknown command: {0}", subcommand);
            PrintHelp();
            return;
        }

        command.Execute(args[1..]);
    }

    static void PrintHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  {0} <command> [options]", ExecutableName);
        Console.WriteLine();
        Console.WriteLine("Commands:");
        foreach (var cmd in Commands)
            Console.WriteLine("  {0,-8} {1}", cmd.Name, cmd.Description);
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -h, --help    Show help information");
    }
}
