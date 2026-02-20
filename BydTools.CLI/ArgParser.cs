namespace BydTools.CLI;

/// <summary>
/// Minimal command-line argument parser with builder pattern.
/// Supports --long and -short aliases for both flags and valued options.
/// Uses dictionary-based lookup for O(1) argument resolution.
/// </summary>
class ArgParser
{
    private readonly record struct OptionDef(string LongName, bool IsFlag);

    private readonly Dictionary<string, OptionDef> _lookup = [];
    private readonly Dictionary<string, string?> _values = [];
    private readonly HashSet<string> _flags = [];
    private readonly List<string> _errors = [];

    public ArgParser AddOption(string longName, string? shortName = null)
    {
        var def = new OptionDef(longName, IsFlag: false);
        _lookup[$"--{longName}"] = def;
        if (shortName != null)
            _lookup[$"-{shortName}"] = def;
        return this;
    }

    public ArgParser AddFlag(string longName, string? shortName = null)
    {
        var def = new OptionDef(longName, IsFlag: true);
        _lookup[$"--{longName}"] = def;
        if (shortName != null)
            _lookup[$"-{shortName}"] = def;
        return this;
    }

    public bool TryParse(string[] args)
    {
        _values.Clear();
        _flags.Clear();
        _errors.Clear();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (!_lookup.TryGetValue(arg, out var def))
            {
                _errors.Add($"Unknown argument: {arg}");
                continue;
            }

            if (def.IsFlag)
            {
                _flags.Add(def.LongName);
            }
            else
            {
                if (i + 1 >= args.Length)
                {
                    _errors.Add($"Error: --{def.LongName} requires a value.");
                    continue;
                }
                _values[def.LongName] = args[++i];
            }
        }

        return _errors.Count == 0;
    }

    public string? GetValue(string longName) => _values.GetValueOrDefault(longName);

    public bool GetFlag(string longName) => _flags.Contains(longName);

    public IReadOnlyList<string> Errors => _errors;
}
