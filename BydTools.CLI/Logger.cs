using BydTools.Utils;
using Spectre.Console;

namespace BydTools.CLI;

/// <summary>
/// Console logger with support for verbose mode, powered by Spectre.Console.
/// </summary>
public class Logger : ILogger
{
    private static readonly IAnsiConsole _stderr = AnsiConsole.Create(
        new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) }
    );

    private readonly bool _verbose;

    public Logger(bool verbose = false)
    {
        _verbose = verbose;
    }

    public void Info(string message) =>
        AnsiConsole.MarkupLine(Markup.Escape(message));

    public void Info(string format, params object[] args) =>
        AnsiConsole.MarkupLine(Markup.Escape(string.Format(format, args)));

    public void InfoNoNewline(string message) =>
        AnsiConsole.Markup(Markup.Escape(message));

    public void Verbose(string message)
    {
        if (_verbose)
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
    }

    public void Verbose(string format, params object[] args)
    {
        if (_verbose)
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(string.Format(format, args))}[/]");
    }

    public void VerboseNoNewline(string message)
    {
        if (_verbose)
            AnsiConsole.Markup($"[grey]{Markup.Escape(message)}[/]");
    }

    public void Error(string message) =>
        _stderr.MarkupLine($"[red]{Markup.Escape(message)}[/]");

    public void Error(string format, params object[] args) =>
        _stderr.MarkupLine($"[red]{Markup.Escape(string.Format(format, args))}[/]");

    /// <summary>
    /// Writes a styled error message to stderr with a red "error:" prefix.
    /// Use for CLI-level validation/argument errors.
    /// </summary>
    public static void WriteError(string message) =>
        _stderr.MarkupLine($"[red bold]error:[/] {Markup.Escape(message)}");
}
