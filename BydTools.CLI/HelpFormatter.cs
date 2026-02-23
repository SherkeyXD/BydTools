using Spectre.Console;

namespace BydTools.CLI;

/// <summary>
/// Shared help output formatter for consistent CLI help style.
/// </summary>
static class HelpFormatter
{
    private const int FlagColumnWidth = 26;
    private const int TypeColumnWidth = 26;
    private const int TypeColumnsPerRow = 3;

    public static void WriteUsage(
        string command,
        string requiredArgs,
        string? optionalHint = "[options]"
    )
    {
        AnsiConsole.MarkupLine("[bold yellow]Usage:[/]");
        var parts = new List<string>
        {
            $"[bold]{Markup.Escape(Program.ExecutableName)}[/]",
            $"[green]{Markup.Escape(command)}[/]",
            Markup.Escape(requiredArgs),
        };
        if (optionalHint != null)
            parts.Add($"[grey]{Markup.Escape(optionalHint)}[/]");
        AnsiConsole.MarkupLine($"  {string.Join(" ", parts)}");
        AnsiConsole.WriteLine();
    }

    public static void WriteSectionHeader(string title)
    {
        AnsiConsole.MarkupLine($"[bold yellow]{Markup.Escape(title)}:[/]");
    }

    public static void WriteEntry(string flags, string description)
    {
        AnsiConsole.MarkupLine(
            $"  [green]{Markup.Escape(flags.PadRight(FlagColumnWidth))}[/]"
                + Markup.Escape(description)
        );
    }

    public static void WriteEntryContinuation(string text)
    {
        AnsiConsole.MarkupLine($"  {"".PadRight(FlagColumnWidth)}{Markup.Escape(text)}");
    }

    public static void WriteCommonOptions()
    {
        WriteEntry("-v, --verbose", "Enable verbose output");
        WriteEntry("-h, --help", "Show help information");
    }

    public static void WriteBlankLine() => AnsiConsole.WriteLine();

    /// <summary>
    /// Formats enum values as a multi-column display: "Name (Value)" padded into columns.
    /// </summary>
    public static void WriteEnumValues<T>(string sectionTitle, IEnumerable<T> values)
        where T : Enum
    {
        WriteSectionHeader(sectionTitle);

        var items = values.Select(v => $"{v} ({Convert.ToByte(v)})").ToList();

        for (int i = 0; i < items.Count; i += TypeColumnsPerRow)
        {
            var row = items
                .Skip(i)
                .Take(TypeColumnsPerRow)
                .Select(item => item.PadRight(TypeColumnWidth));
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(string.Join("", row))}[/]");
        }
    }
}
