using System.Text.Json;

namespace BydTools.PCK;

/// <summary>
/// Resolves Wwise numeric file IDs to human-readable paths
/// using a JSON mapping file (e.g. AudioDialog.json).
/// <para>
/// Expected format: <c>{ "id": { "path": "Chinese\\Voice\\file.wem", ... }, ... }</c>
/// </para>
/// </summary>
public class PckMapper
{
    private readonly Dictionary<string, string> _idToPath;

    public int Count => _idToPath.Count;

    public PckMapper(string jsonPath)
    {
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("JSON mapping file not found", jsonPath);

        using var stream = File.OpenRead(jsonPath);
        _idToPath = ParseJson(stream);
    }

    public PckMapper(Stream stream)
    {
        _idToPath = ParseJson(stream);
    }

    /// <summary>
    /// Resolves a Wwise file ID to an output path.
    /// Returns <c>null</c> if the ID has no mapping.
    /// </summary>
    public string? GetMappedPath(string fileId)
    {
        if (_idToPath.TryGetValue(fileId, out string? path))
            return path;

        // Also try uint32 truncation for 64-bit IDs
        if (ulong.TryParse(fileId, out ulong id64))
        {
            uint id32 = (uint)(id64 & 0xFFFFFFFF);
            if (id32 != id64 && _idToPath.TryGetValue(id32.ToString(), out path))
                return path;
        }

        return null;
    }

    private static Dictionary<string, string> ParseJson(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("JSON root must be an object (id -> row)");

        var map = new Dictionary<string, string>(root.GetPropertyCount());

        foreach (var entry in root.EnumerateObject())
        {
            string id = entry.Name;

            if (entry.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (!entry.Value.TryGetProperty("path", out var pathElement))
                continue;

            string? rawPath = pathElement.GetString();
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            string normalized = rawPath.Replace('/', '\\').Trim();
            if (normalized.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
                normalized = normalized[..^4];

            map.TryAdd(id, normalized);
        }

        return map;
    }
}
