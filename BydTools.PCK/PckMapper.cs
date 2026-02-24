using System.Text;
using System.Text.Json;

namespace BydTools.PCK;

/// <summary>
/// Resolves Wwise file IDs to human-readable paths
/// using an AudioDialog JSON file.
/// <para>
/// Expected format: <c>{ "id": { "path": "v1d0/.../file.wem", ... }, ... }</c>
/// </para>
/// <para>
/// PCK file IDs are FNV-1a 64-bit hashes of <c>{soundType}/{language}/{path}</c> (lowercased).
/// This mapper internally tries known prefixes <c>voice</c>, <c>music</c>, and <c>sfx</c>
/// for each AudioDialog row and records all resulting hashes as lookup keys.
/// </para>
/// </summary>
public class PckMapper
{
    private const ulong FnvOffset = 0xcbf29ce484222325;
    private const ulong FnvPrime = 0x100000001b3;
    private static readonly string[] KnownSoundTypes = ["voice", "music", "sfx"];

    private readonly Dictionary<string, string> _idToPath;

    public int Count => _idToPath.Count;

    public PckMapper(string jsonPath, string language)
    {
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("JSON mapping file not found", jsonPath);

        using var stream = File.OpenRead(jsonPath);
        _idToPath = ParseAudioDialog(stream, language);
    }

    public PckMapper(Stream stream, string language)
    {
        _idToPath = ParseAudioDialog(stream, language);
    }

    /// <summary>
    /// Resolves a Wwise file ID to an output path.
    /// Returns <c>null</c> if the ID has no mapping.
    /// </summary>
    public string? GetMappedPath(string fileId)
    {
        if (_idToPath.TryGetValue(fileId, out string? path))
            return path;

        if (ulong.TryParse(fileId, out ulong id64))
        {
            uint id32 = (uint)(id64 & 0xFFFFFFFF);
            if (id32 != id64 && _idToPath.TryGetValue(id32.ToString(), out path))
                return path;
        }

        return null;
    }

    internal static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong hash = FnvOffset;
        foreach (byte b in data)
        {
            hash = unchecked(hash * FnvPrime);
            hash ^= b;
        }
        return hash;
    }

    private static Dictionary<string, string> ParseAudioDialog(Stream stream, string language)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("JSON root must be an object (id -> row)");

        string lang = language.ToLowerInvariant();
        var map = new Dictionary<string, string>(root.GetPropertyCount() * KnownSoundTypes.Length);

        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (!entry.Value.TryGetProperty("path", out var pathElement))
                continue;

            string? rawPath = pathElement.GetString();
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            string normalizedPath = rawPath.Replace('/', '\\').Trim();
            foreach (string soundType in KnownSoundTypes)
            {
                string hashInput = $"{soundType}/{language}/{rawPath}".Replace('\\', '/')
                    .ToLowerInvariant();

                ulong hash = Fnv1a64(Encoding.UTF8.GetBytes(hashInput));
                string hashKey = hash.ToString();

                string outputPath = Path.Combine(soundType, lang, normalizedPath);
                if (outputPath.EndsWith(".wem", StringComparison.OrdinalIgnoreCase))
                    outputPath = outputPath[..^4];

                map.TryAdd(hashKey, outputPath);
            }
        }

        return map;
    }
}
