namespace BydTools.Formats.MemoryPack;

/// <summary>
/// Layout of a beyond-mp-schema export directory. BydTools only consumes these files.
/// Stems are <c>schemas</c>, <c>type_index</c>, <c>union-map</c>, <c>enums</c>, <c>unmanaged</c>.
/// Each stem is <c>.msgpack.lz4</c> (transfer format) or <c>.json</c>.
/// </summary>
public static class MemoryPackExport
{
    public const string SchemasStem = "schemas";
    public const string TypeIndexStem = "type_index";
    public const string UnionMapStem = "union-map";
    public const string EnumsStem = "enums";
    public const string UnmanagedStem = "unmanaged";

    public const string SchemasFileName = "schemas.json";
    public const string TypeIndexFileName = "type_index.json";
    public const string UnionMapFileName = "union-map.json";
    public const string EnumsFileName = "enums.json";
    public const string UnmanagedFileName = "unmanaged.json";

    public readonly record struct Paths(string Schema, string TypeIndex, string? UnionMap);

    /// <summary>
    /// Resolves schema files from an export directory, with optional per-file overrides.
    /// When <paramref name="unionMap"/> is omitted, <c>union-map.msgpack.lz4</c> or
    /// <c>union-map.json</c> is loaded from the directory when present.
    /// </summary>
    public static Paths Resolve(string? schemaDir, string? schema, string? index, string? unionMap)
    {
        if (!string.IsNullOrWhiteSpace(schemaDir))
        {
            schemaDir = Path.GetFullPath(schemaDir);
            schema ??= SchemaFile.Find(schemaDir, SchemasStem)
                ?? Path.Combine(schemaDir, SchemasFileName);
            index ??= SchemaFile.Find(schemaDir, TypeIndexStem)
                ?? Path.Combine(schemaDir, TypeIndexFileName);
            if (string.IsNullOrWhiteSpace(unionMap))
                unionMap = SchemaFile.Find(schemaDir, UnionMapStem);
        }

        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(index))
            throw new ArgumentException("--schema-dir, or both --schema and --index, are required.");

        return new Paths(
            Path.GetFullPath(schema),
            Path.GetFullPath(index),
            string.IsNullOrWhiteSpace(unionMap) ? null : Path.GetFullPath(unionMap)
        );
    }

    public static MemoryPackSchema LoadSchema(string exportDirectory) =>
        MemoryPackSchema.Load(
            SchemaFile.Find(exportDirectory, SchemasStem)
                ?? Path.Combine(exportDirectory, SchemasFileName),
            SchemaFile.Find(exportDirectory, TypeIndexStem)
                ?? Path.Combine(exportDirectory, TypeIndexFileName)
        );

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? TryLoadUnionMap(string exportDirectory)
    {
        string? path = SchemaFile.Find(exportDirectory, UnionMapStem);
        return path == null ? null : JsonDataMemoryPack.LoadUnionMap(path);
    }
}
