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
        Console.WriteLine("Usage:");
        var parts = new List<string> { $"  {Program.ExecutableName}", command, requiredArgs };
        if (optionalHint != null)
            parts.Add(optionalHint);
        Console.WriteLine(string.Join(" ", parts));
        Console.WriteLine();
    }

    public static void WriteSectionHeader(string title)
    {
        Console.WriteLine($"{title}:");
    }

    public static void WriteEntry(string flags, string description)
    {
        Console.WriteLine($"  {flags.PadRight(FlagColumnWidth)}{description}");
    }

    public static void WriteEntryContinuation(string text)
    {
        Console.WriteLine($"  {"".PadRight(FlagColumnWidth)}{text}");
    }

    public static void WriteCommonOptions()
    {
        WriteEntry("-v, --verbose", "Enable verbose output");
        WriteEntry("-h, --help", "Show help information");
    }

    public static void WriteBlankLine() => Console.WriteLine();

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
            Console.WriteLine($"  {string.Join("", row)}");
        }
    }
}
