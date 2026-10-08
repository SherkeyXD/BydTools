namespace BydTools.CLI.Commands;

public interface ICommand
{
    string Name { get; }
    string Description { get; }
    int Execute(string[] args, CancellationToken cancellationToken);
}
