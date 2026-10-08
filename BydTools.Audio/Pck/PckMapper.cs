using System.Text;
using System.Text.Json.Nodes;

namespace BydTools.Audio.Pck;

/// <summary>
/// Resolves Wwise ids to paths using an AudioDialog object.
/// Ids are FNV-1 64-bit hashes of <c>{soundType}/{language}/{path}</c>.
/// </summary>
public sealed class PckMapper
{
    private const ulong FnvOffset = 0xcbf29ce484222325;
    private const ulong FnvPrime = 0x100000001b3;
    private static readonly string[] KnownSoundTypes = ["voice", "music", "sfx"];

    private readonly Dictionary<ulong, string> _idToPath;

    public PckMapper(JsonObject root, string language)
    {
        _idToPath = Parse(root, language);
    }

    public int Count => _idToPath.Count;

    public string? GetMappedPath(ulong fileId)
    {
        if (_idToPath.TryGetValue(fileId, out string? path))
            return path;

        ulong low = (uint)fileId;
        if (low != fileId && _idToPath.TryGetValue(low, out path))
            return path;
        return null;
    }

    internal static ulong Fnv1_64(ReadOnlySpan<byte> data)
    {
        ulong hash = FnvOffset;
        foreach (byte b in data)
        {
            hash = unchecked(hash * FnvPrime);
            hash ^= b;
        }

        return hash;
    }

    private static Dictionary<ulong, string> Parse(JsonObject root, string language)
    {
        string lang = language.ToLowerInvariant();
        var map = new Dictionary<ulong, string>(root.Count * KnownSoundTypes.Length);
        foreach (var entry in root)
        {
            if (entry.Value is not JsonObject row)
                continue;
            if (!row.TryGetPropertyValue("path", out var pathNode) || pathNode is null)
                continue;
            string? rawPath = pathNode.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            string normalized = rawPath.Replace('\\', '/').Trim();
            foreach (string soundType in KnownSoundTypes)
            {
                string hashInput = $"{soundType}/{language}/{rawPath}".Replace('\\', '/').ToLowerInvariant();
                ulong hash = Fnv1_64(Encoding.UTF8.GetBytes(hashInput));
                string output = $"{soundType}/{lang}/{normalized}";
                if (output.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
                    output = output[..^4];
                map.TryAdd(hash, output);
            }
        }

        return map;
    }
}
