using System.Collections.Concurrent;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BydTools.Formats.MemoryPack;

public enum JsonDataFileKind
{
    Decoded,
    PlainJson,
    Skipped,
    Failed,
}

public sealed class JsonDataFileResult
{
    public required string RelativePath { get; init; }
    public required JsonDataFileKind Kind { get; init; }
    public string? Error { get; init; }
}

public sealed class JsonDataDecodeReport
{
    public int Ok { get; set; }
    public int PlainJson { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public Dictionary<string, FolderCounts> ByFolder { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<JsonDataFileResult> Failures { get; } = [];

    public int Total => Ok + PlainJson + Skipped + Failed;
}

public sealed class FolderCounts
{
    public int Ok;
    public int PlainJson;
    public int Skipped;
    public int Failed;
}

/// <summary>
/// Turns a <c>vfs --blocktype JsonData</c> tree into plaintext JSON.
/// A leading <c>{</c> or <c>[</c> stays as UTF-8 JSON. USPC and SHPI are skipped.
/// </summary>
public static class JsonDataMemoryPack
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly (string PathSuffix, string TypeName)[] FolderHints =
    [
        ("Interactive/ModelViewStateControllerData", "MVSCModelViewStateControllerData"),
        ("NPC/MontageJson", "NPCMontageJson"),
        ("CharInteractPerformCfgs", "CharInteractPerformRuntimeCfg"),
        ("AtmosphericNpcData", "NpcAtmosphericDataTable"),
        ("SpawnerConfig", "SpawnerConfigData"),
        ("LevelScriptTemplateData", "LevelScriptTemplateData"),
        ("LevelScriptData", "LevelScriptData"),
        ("LevelConfig", "LevelConfig"),
        ("LevelData", "LevelData"),
        ("BuffData", "BuffData"),
        ("SkillData", "SkillData"),
        ("AnimationConfig", "AnimationConfigJson"),
        ("NavMesh", "LunaNavMeshAreaContainer"),
        ("GPUISystemConfig", "PrefabGroupSerializeData"),
    ];

    public static JsonDataDecodeReport DecodeTree(
        MemoryPackSchema schema,
        string inputDirectory,
        string outputDirectory,
        BeyondStringPool? pool = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? unionMaps = null,
        int jobs = 1,
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException($"JsonData directory not found: {inputDirectory}");
        if (jobs < 1)
            throw new ArgumentOutOfRangeException(nameof(jobs), "jobs must be positive.");

        Directory.CreateDirectory(outputDirectory);
        var decoder = new MemoryPackDecoder(schema, pool, unionMaps);
        string[] files = Directory.GetFiles(inputDirectory, "*.json", SearchOption.AllDirectories);
        var report = new JsonDataDecodeReport();
        var bag = new ConcurrentBag<JsonDataFileResult>();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = jobs,
            CancellationToken = cancellationToken,
        };
        Parallel.ForEach(files, options, path =>
        {
            string relative = Path.GetRelativePath(inputDirectory, path);
            bag.Add(DecodeOne(decoder, schema, path, relative, outputDirectory));
        });

        foreach (JsonDataFileResult result in bag)
        {
            string folder = TopFolder(result.RelativePath);
            if (!report.ByFolder.TryGetValue(folder, out FolderCounts? counts))
            {
                counts = new FolderCounts();
                report.ByFolder[folder] = counts;
            }

            switch (result.Kind)
            {
                case JsonDataFileKind.Decoded:
                    report.Ok++;
                    counts.Ok++;
                    break;
                case JsonDataFileKind.PlainJson:
                    report.PlainJson++;
                    counts.PlainJson++;
                    break;
                case JsonDataFileKind.Skipped:
                    report.Skipped++;
                    counts.Skipped++;
                    break;
                default:
                    report.Failed++;
                    counts.Failed++;
                    report.Failures.Add(result);
                    break;
            }
        }

        report.Failures.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return report;
    }

    public static JsonNode ReportJson(JsonDataDecodeReport report)
    {
        var folders = new JsonObject();
        foreach (var pair in report.ByFolder.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            folders[pair.Key] = new JsonObject
            {
                ["ok"] = pair.Value.Ok,
                ["plain_json"] = pair.Value.PlainJson,
                ["skipped"] = pair.Value.Skipped,
                ["failed"] = pair.Value.Failed,
            };
        }

        var failures = new JsonArray();
        foreach (JsonDataFileResult failure in report.Failures)
        {
            failures.Add((JsonNode)new JsonObject
            {
                ["path"] = failure.RelativePath,
                ["error"] = failure.Error,
            });
        }

        return new JsonObject
        {
            ["ok"] = report.Ok,
            ["plain_json"] = report.PlainJson,
            ["skipped"] = report.Skipped,
            ["failed"] = report.Failed,
            ["total"] = report.Total,
            ["by_folder"] = folders,
            ["failures"] = failures,
        };
    }

    /// <summary>
    /// Loads <c>{ "AbilityActionData": { "296": "PlayAnimationActionData", ... }, "ActionBase": { ... } }</c>.
    /// A flat <c>{ "296": "..." }</c> object is treated as AbilityActionData only (legacy).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> LoadUnionMap(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Union map must be a JSON object.");

        var maps = new Dictionary<string, IReadOnlyDictionary<int, string>>(StringComparer.Ordinal);
        bool looksNested = false;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object
                && prop.Value.EnumerateObject().Any(p => int.TryParse(p.Name, out _)))
            {
                // Nested union name → tags, or flat tags (names are integers).
                if (!int.TryParse(prop.Name, out _))
                    looksNested = true;
            }
        }

        if (looksNested)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;
                maps[prop.Name] = ReadTagObject(prop.Value);
            }
        }
        else
        {
            maps["AbilityActionData"] = ReadTagObject(root);
        }

        return maps;
    }

    private static Dictionary<int, string> ReadTagObject(JsonElement tags)
    {
        var map = new Dictionary<int, string>();
        if (tags.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Union map tags must be an object of tag to type name.");
        foreach (var prop in tags.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out int tag))
                throw new InvalidDataException($"Union tag \"{prop.Name}\" is not an integer.");
            string? name = prop.Value.GetString();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException($"Union tag {tag} has an empty type name.");
            map[tag] = name;
        }
        return map;
    }

    public static bool IsPlainJson(ReadOnlySpan<byte> data)
    {
        int i = 0;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        return i < data.Length && data[i] is (byte)'{' or (byte)'[';
    }

    private static JsonDataFileResult DecodeOne(
        MemoryPackDecoder decoder,
        MemoryPackSchema schema,
        string path,
        string relative,
        string outputDirectory
    )
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length == 0)
            {
                return new JsonDataFileResult
                {
                    RelativePath = relative,
                    Kind = JsonDataFileKind.Skipped,
                    Error = "Empty file.",
                };
            }

            if (IsPlainJson(data))
            {
                CopyTo(outputDirectory, relative, data);
                return new JsonDataFileResult { RelativePath = relative, Kind = JsonDataFileKind.PlainJson };
            }

            if (StartsWith(data, "USPC"u8) || StartsWith(data, "SHPI"u8))
            {
                return new JsonDataFileResult
                {
                    RelativePath = relative,
                    Kind = JsonDataFileKind.Skipped,
                    Error = "USPC/SHPI container.",
                };
            }

            if (!schema.TryBasename(Path.GetFileNameWithoutExtension(path), FolderHint(relative), out string? root, out string? error))
            {
                return new JsonDataFileResult
                {
                    RelativePath = relative,
                    Kind = JsonDataFileKind.Failed,
                    Error = error,
                };
            }

            string json = decoder.ToJson(data, root!);
            string destination = Path.Combine(outputDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, json);
            return new JsonDataFileResult { RelativePath = relative, Kind = JsonDataFileKind.Decoded };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or MemoryPackTypeException)
        {
            return new JsonDataFileResult
            {
                RelativePath = relative,
                Kind = JsonDataFileKind.Failed,
                Error = ex.Message,
            };
        }
    }

    private static string? FolderHint(string relativePath)
    {
        string directory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";
        foreach (var (suffix, typeName) in FolderHints)
        {
            if (directory.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(suffix + "/", StringComparison.OrdinalIgnoreCase)
                || directory.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase)
                || directory.Contains("/" + suffix + "/", StringComparison.OrdinalIgnoreCase))
                return typeName;
        }

        return null;
    }

    private static string TopFolder(string relativePath)
    {
        int slash = relativePath.IndexOfAny(['/', '\\']);
        return slash < 0 ? "(root)" : relativePath[..slash];
    }

    private static void CopyTo(string outputDirectory, string relative, byte[] data)
    {
        string destination = Path.Combine(outputDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, data);
    }

    private static bool StartsWith(byte[] data, ReadOnlySpan<byte> magic) =>
        data.AsSpan().StartsWith(magic);
}
