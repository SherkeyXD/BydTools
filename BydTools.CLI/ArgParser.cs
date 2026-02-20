namespace BydTools.CLI;

/// <summary>
/// Minimal command-line argument parser with builder pattern.
/// Supports --long and -short aliases for both flags and valued options.
/// </summary>
class ArgParser
{
    private readonly record struct OptionDef(string LongName, string? ShortName, bool IsFlag);

    private readonly List<OptionDef> _definitions = [];
    private readonly Dictionary<string, string?> _values = [];
    private readonly HashSet<string> _flags = [];
    private readonly List<string> _errors = [];

    public ArgParser AddOption(string longName, string? shortName = null)
    {
        _definitions.Add(new OptionDef(longName, shortName, IsFlag: false));
        return this;
    }

    public ArgParser AddFlag(string longName, string? shortName = null)
    {
        _definitions.Add(new OptionDef(longName, shortName, IsFlag: true));
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
            var def = FindDefinition(arg);

            if (def == null)
            {
                _errors.Add($"Unknown argument: {arg}");
                continue;
            }

            if (def.Value.IsFlag)
            {
                _flags.Add(def.Value.LongName);
            }
            else
            {
                if (i + 1 >= args.Length)
                {
                    _errors.Add($"Error: --{def.Value.LongName} requires a value.");
                    continue;
                }
                _values[def.Value.LongName] = args[++i];
            }
        }

        return _errors.Count == 0;
    }

    public string? GetValue(string longName) => _values.GetValueOrDefault(longName);

    public bool GetFlag(string longName) => _flags.Contains(longName);

    public IReadOnlyList<string> Errors => _errors;

    private OptionDef? FindDefinition(string arg)
    {
        foreach (var def in _definitions)
        {
            if (arg == $"--{def.LongName}")
                return def;
            if (def.ShortName != null && arg == $"-{def.ShortName}")
                return def;
        }
        return null;
    }
}
