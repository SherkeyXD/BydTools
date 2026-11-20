using System.Text;

namespace BydTools.Formats.MemoryPack;

/// <summary>
/// Identifies the MemoryPack root type for a JsonData file from its relative path,
/// basename, token similarity, and leading wire member count.
/// </summary>
public sealed class MemoryPackRootResolver
{
    private static readonly HashSet<string> IdentityStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "data", "json", "config", "table", "system", "the", "for", "and", "of", "to", "in",
        "from", "with", "info", "type", "item", "base",
    };

    private readonly MemoryPackSchema _schema;
    private readonly List<(HashSet<string> Tokens, string Name, int? Wire)> _basenameTokenIndex = [];
    private readonly List<(HashSet<string> Tokens, int Wire, bool Marker, string Name)> _targetTokenIndex = [];

    internal MemoryPackRootResolver(MemoryPackSchema schema)
    {
        _schema = schema;
        BuildTokenIndexes();
    }

    public bool TryBasename(string basename, string? folderHint, out string? targetType, out string? error)
    {
        targetType = null;
        error = null;
        if (_schema.ByBasename.TryGetValue(basename, out var exact) && exact.Count > 0)
        {
            targetType = Prefer(exact);
            return targetType != null || Fail(exact.Count, basename, out error);
        }

        if (basename.EndsWith("Table", StringComparison.Ordinal)
            && !basename.EndsWith("DataTable", StringComparison.Ordinal))
        {
            string dataTable = basename[..^5] + "DataTable";
            if (_schema.ByBasename.TryGetValue(dataTable, out var aliased) && aliased.Count > 0)
            {
                targetType = Prefer(aliased);
                return targetType != null || Fail(aliased.Count, dataTable, out error);
            }
        }

        string? suffix = LongestSuffix(basename);
        if (suffix != null && _schema.ByBasename.TryGetValue(suffix, out var suffixed))
        {
            targetType = Prefer(suffixed);
            return true;
        }

        if (!string.IsNullOrEmpty(folderHint) && _schema.ByBasename.TryGetValue(folderHint, out var hinted))
        {
            targetType = Prefer(hinted);
            return targetType != null || Fail(hinted.Count, folderHint, out error);
        }

        error = $"No type_index basename for \"{basename}\""
            + (folderHint == null ? "." : $" or folder hint \"{folderHint}\".");
        return false;
    }

    public bool TryIdentifyRoot(string relativePath, ReadOnlySpan<byte> data, out string? targetType, out string? error)
    {
        targetType = null;
        error = null;
        string normalized = relativePath.Replace('\\', '/');
        string basename = Path.GetFileNameWithoutExtension(normalized);
        int? header = PeekMemberCount(data);
        var candidates = new List<string>();

        void Consider(string? name)
        {
            if (string.IsNullOrEmpty(name) || candidates.Contains(name))
                return;
            candidates.Add(name);
        }

        if (TryBasename(basename, folderHint: null, out string? named, out _) && named != null)
        {
            int? wire = _schema.WireLength(named);
            if (header is null || wire is null || wire == header || header < wire)
            {
                targetType = named;
                return true;
            }

            Consider(named);
        }

        string directory = Path.GetDirectoryName(normalized) ?? "";
        foreach (string segment in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length < 4 || segment is "Data" or "Json")
                continue;
            if (TryBasename(segment, folderHint: null, out string? fromFolder, out _))
                Consider(fromFolder);
            if (TryBasename(segment + "Data", folderHint: null, out string? fromData, out _))
                Consider(fromData);
            if (TryBasename(segment + "Json", folderHint: null, out string? fromJson, out _))
                Consider(fromJson);
            Consider(UniqueBasenameByTokens(segment, header));
        }

        string? matching = UniqueWireMatch(candidates, header);
        if (matching != null)
        {
            targetType = matching;
            return true;
        }

        string? scored = ScorePathTypes(relativePath, header);
        if (scored != null)
        {
            targetType = scored;
            return true;
        }

        if (candidates.Count == 1)
        {
            targetType = candidates[0];
            return true;
        }

        error = $"No type_index basename for \"{basename}\".";
        return false;
    }

    public static int? PeekMemberCount(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return null;
        byte b = data[0];
        if (b == 255)
            return null;
        if (b == 250)
            return data.Length >= 5 ? BitConverter.ToInt32(data[1..]) : null;
        return b;
    }

    internal static HashSet<string> Tokens(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var token = new StringBuilder();
        void Flush()
        {
            if (token.Length >= 2)
                set.Add(token.ToString());
            token.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch is '.' or '+' or '/' or '\\' or '_' or '`' or '<' or '>' or ',' || !char.IsLetterOrDigit(ch))
            {
                Flush();
                continue;
            }

            if (token.Length > 0 && char.IsUpper(ch))
            {
                bool prevLower = char.IsLower(token[^1]);
                bool nextLower = i + 1 < text.Length && char.IsLower(text[i + 1]);
                if (prevLower || nextLower)
                    Flush();
            }

            token.Append(ch);
        }

        Flush();
        return set;
    }

    private string? Prefer(List<string> names)
    {
        string? marked = null;
        foreach (string name in names)
        {
            if (_schema.HasSerializeMarker(name))
            {
                if (marked != null && !string.Equals(marked, name, StringComparison.Ordinal))
                    return null;
                marked = name;
            }
        }

        if (marked != null)
            return marked;
        return names.Count == 1 ? names[0] : null;
    }

    private static bool Fail(int count, string name, out string? error)
    {
        error = $"Basename \"{name}\" matches {count} types and more than one is an IMemoryPackSerialize root.";
        return false;
    }

    private string? LongestSuffix(string basename)
    {
        string? best = null;
        foreach (string key in _schema.ByBasename.Keys)
        {
            if (key.Length < 10 || key.Length >= basename.Length)
                continue;
            if (!basename.EndsWith(key, StringComparison.Ordinal))
                continue;
            if (best == null || key.Length > best.Length)
                best = key;
        }

        return best;
    }

    private string? UniqueWireMatch(List<string> names, int? header)
    {
        if (names.Count == 0)
            return null;
        if (header is not int count)
            return names.Count == 1 ? names[0] : Prefer(names);
        var hits = new List<string>();
        foreach (string name in names)
        {
            if (_schema.WireLength(name) == count)
                hits.Add(name);
        }

        if (hits.Count == 1)
            return hits[0];
        if (hits.Count > 1)
            return Prefer(hits);
        return names.Count == 1 ? names[0] : Prefer(names);
    }

    private string? UniqueBasenameByTokens(string segment, int? header)
    {
        HashSet<string> tokens = Tokens(segment);
        tokens.ExceptWith(IdentityStopwords);
        if (tokens.Count == 0)
            return null;
        var hits = new List<(int Score, string Name)>();
        foreach (var (keyTokens, preferred, wire) in _basenameTokenIndex)
        {
            int overlap = 0;
            foreach (string token in tokens)
            {
                if (keyTokens.Contains(token))
                    overlap += token.Length * token.Length;
            }
            if (overlap == 0)
                continue;
            if (header is int count && wire is int w && w != count)
                overlap -= 50;
            hits.Add((overlap, preferred));
        }

        if (hits.Count == 0)
            return null;
        hits.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (hits.Count == 1 || hits[0].Score > hits[1].Score)
            return hits[0].Name;
        return null;
    }

    private string? ScorePathTypes(string relativePath, int? header)
    {
        if (header is not int count)
            return null;
        HashSet<string> pathTokens = Tokens(relativePath);
        pathTokens.ExceptWith(IdentityStopwords);
        if (pathTokens.Count == 0)
            return null;
        var hits = new List<(int Score, int Overlap, string Name)>();
        foreach (var (nameTokens, wire, marker, name) in _targetTokenIndex)
        {
            if (wire != count)
                continue;
            int overlap = 0;
            foreach (string token in pathTokens)
            {
                if (nameTokens.Contains(token))
                    overlap += token.Length * token.Length;
            }
            if (overlap == 0)
                continue;
            int score = overlap + (marker ? 100 : 0);
            hits.Add((score, overlap, name));
        }

        if (hits.Count == 0)
            return null;
        hits.Sort((a, b) => b.Score.CompareTo(a.Score) != 0 ? b.Score.CompareTo(a.Score) : b.Overlap.CompareTo(a.Overlap));
        if (hits.Count == 1 || hits[0].Score > hits[1].Score)
            return hits[0].Name;
        return null;
    }

    private void BuildTokenIndexes()
    {
        foreach (var (key, names) in _schema.ByBasename)
        {
            string? preferred = Prefer(names);
            if (preferred == null)
                continue;
            HashSet<string> tokens = Tokens(key);
            tokens.ExceptWith(IdentityStopwords);
            if (tokens.Count == 0)
                continue;
            _basenameTokenIndex.Add((tokens, preferred, _schema.WireLength(preferred)));
        }

        foreach (var (name, list) in _schema.ByTarget)
        {
            if (list.Count == 0)
                continue;
            MpType type = list.FirstOrDefault(t => t.SerializeMarker) ?? list[0];
            HashSet<string> nameTokens = Tokens(name);
            nameTokens.ExceptWith(IdentityStopwords);
            if (nameTokens.Count == 0)
                continue;
            _targetTokenIndex.Add((nameTokens, type.Wire.Length, type.SerializeMarker, name));
        }
    }
}
