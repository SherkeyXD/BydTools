using System.Text.Json;

namespace BydTools.Formats.MemoryPack;

/// <summary>
/// Embedded beyond-schema-dumper export (<c>*.msgpack.lz4</c> in
/// <c>BydTools.Formats/MemoryPack/Resource/</c>). Refresh by copying a
/// <c>--format msgpack</c> export of <c>schemas</c>, <c>type_index</c>,
/// <c>union-map</c>, <c>unmanaged</c>, and <c>enums</c>.
/// </summary>
public static class MemoryPackResources
{
    public const string Prefix = "BydTools.Formats.MemoryPack.Resource.";

    private static readonly Lazy<Bundle> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static MemoryPackSchema Schema => Loaded.Value.Schema;

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> UnionMaps =>
        Loaded.Value.UnionMaps;

    public static MemoryPackDecoder CreateDecoder(BeyondStringPool? pool = null) =>
        new(Schema, pool, UnionMaps);

    public static byte[] Read(string fileName)
    {
        string name = Prefix + fileName;
        using Stream? stream = typeof(MemoryPackResources).Assembly.GetManifestResourceStream(name);
        if (stream == null)
            throw new FileNotFoundException($"Embedded MemoryPack resource '{fileName}' was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Bundle Load()
    {
        using JsonDocument schemas = SchemaFile.Parse(Read("schemas.msgpack.lz4"));
        using JsonDocument index = SchemaFile.Parse(Read("type_index.msgpack.lz4"));
        using JsonDocument enums = SchemaFile.Parse(Read("enums.msgpack.lz4"));
        using JsonDocument unmanaged = SchemaFile.Parse(Read("unmanaged.msgpack.lz4"));
        return new Bundle(
            MemoryPackSchema.Load(schemas, index, enums, unmanaged),
            JsonDataMemoryPack.LoadUnionMap(Read("union-map.msgpack.lz4"))
        );
    }

    private readonly record struct Bundle(
        MemoryPackSchema Schema,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> UnionMaps
    );
}
