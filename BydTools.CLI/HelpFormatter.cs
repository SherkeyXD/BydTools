using BydTools.CLI.Commands;
using Spectre.Console;

namespace BydTools.CLI;

public static class HelpFormatter
{
    public const string Executable = "BydTools";

    public static void WriteRootHelp(IEnumerable<ICommand> commands)
    {
        var rule = new Rule("[bold]BydTools[/]") { Justification = Justify.Left };
        AnsiConsole.Write(rule);
        AnsiConsole.MarkupLine("[dim]Arknights: Endfield data tools[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        AnsiConsole.MarkupLine($"  [cyan]{Executable}[/] [yellow]<command>[/] [[options]]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Commands:[/]");
        foreach (var command in commands)
            AnsiConsole.MarkupLine($"  [cyan]{command.Name,-12}[/] {Markup.Escape(command.Description)}");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Run [cyan]{Executable} <command> --help[/] for details.");
    }

    public static void WriteCommandHelp(string description, string usage, ArgParser parser, Action? extra = null)
    {
        var rule = new Rule($"[bold]{Markup.Escape(description)}[/]") { Justification = Justify.Left };
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        AnsiConsole.MarkupLine($"  [cyan]{Executable}[/] {Markup.Escape(usage)}");
        AnsiConsole.WriteLine();

        var required = parser.Options.Where(static o => o.Required).ToArray();
        var optional = parser.Options.Where(static o => !o.Required).ToArray();
        if (required.Length > 0)
        {
            AnsiConsole.MarkupLine("[bold]Required:[/]");
            WriteOptions(required);
            AnsiConsole.WriteLine();
        }

        if (optional.Length > 0)
        {
            AnsiConsole.MarkupLine("[bold]Options:[/]");
            WriteOptions(optional);
            AnsiConsole.WriteLine();
        }

        extra?.Invoke();
    }

    public static void WriteSection(string title)
    {
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(title)}[/]");
    }

    public static void WriteEnumValues<T>(string title, IEnumerable<T> values)
        where T : struct, Enum
    {
        WriteSection(title);
        foreach (var value in values)
            AnsiConsole.MarkupLine($"  [cyan]{value,-24}[/] [dim]({Convert.ToByte(value)})[/]");
        AnsiConsole.WriteLine();
    }

    private static void WriteOptions(IEnumerable<CommandOption> options)
    {
        foreach (var option in options)
        {
            string flags = option.ShortName == null
                ? $"--{option.LongName}"
                : $"--{option.LongName}, -{option.ShortName}";
            if (option.ValueName != null)
                flags += $" <{option.ValueName}>";
            AnsiConsole.MarkupLine($"  [cyan]{Markup.Escape(flags),-32}[/] {Markup.Escape(option.Description)}");
            foreach (string line in option.Continuation)
                AnsiConsole.MarkupLine($"  {"",-32} [dim]{Markup.Escape(line)}[/]");
        }
    }
}
