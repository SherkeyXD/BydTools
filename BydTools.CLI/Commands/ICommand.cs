namespace BydTools.CLI.Commands;

/// <summary>
/// Represents a CLI subcommand that can be executed with parsed arguments.
/// </summary>
interface ICommand
{
    string Name { get; }
    string Description { get; }
    void PrintHelp(string exeName);
    void Execute(string[] args);
}
