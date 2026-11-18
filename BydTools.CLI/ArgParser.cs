namespace BydTools.CLI;

public sealed record CommandOption(
    string LongName,
    string? ShortName,
    bool IsFlag,
    bool Required,
    string? ValueName,
    string Description,
    string[] Continuation
);

/// <summary>
/// Small argv parser. Options are declared once and drive parsing, validation, and help.
/// </summary>
public sealed class ArgParser
{
    private readonly List<CommandOption> _options = [];
    private readonly Dictionary<string, CommandOption> _lookup = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CommandOption> Options => _options;
    public Dictionary<string, string> Values { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Flags { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Errors { get; } = [];

    public ArgParser Add(
        string longName,
        string? shortName,
        string description,
        string? valueName = null,
        bool required = false,
        params string[] continuation
    )
    {
        var option = new CommandOption(
            longName,
            shortName,
            IsFlag: valueName == null,
            required,
            valueName,
            description,
            continuation
        );
        _options.Add(option);
        _lookup["--" + longName] = option;
        if (shortName != null)
            _lookup["-" + shortName] = option;
        return this;
    }

    public bool HasFlag(string longName) => Flags.Contains(longName);

    public string? Get(string longName) => Values.TryGetValue(longName, out var value) ? value : null;

    public bool Parse(string[] args)
    {
        Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Errors.Clear();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!_lookup.TryGetValue(arg, out var option))
            {
                Errors.Add($"Unknown argument: {arg}");
                continue;
            }

            if (option.IsFlag)
            {
                Flags.Add(option.LongName);
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
            {
                Errors.Add($"Missing value for {arg}");
                continue;
            }

            Values[option.LongName] = args[++i];
        }

        return Errors.Count == 0;
    }

    public bool ValidateRequired(Func<CommandOption, bool>? isRequiredOverride = null)
    {
        foreach (CommandOption option in _options)
        {
            bool required = isRequiredOverride?.Invoke(option) ?? option.Required;
            if (!required)
                continue;

            if (option.IsFlag)
            {
                if (!Flags.Contains(option.LongName))
                    Errors.Add($"--{option.LongName} is required.");
            }
            else if (!Values.TryGetValue(option.LongName, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                Errors.Add($"--{option.LongName} is required.");
            }
        }

        return Errors.Count == 0;
    }
}
