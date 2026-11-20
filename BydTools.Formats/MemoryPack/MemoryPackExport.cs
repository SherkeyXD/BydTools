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

    public readonly record struct Paths(string Schema, string TypeIndex, string? UnionMap);

    /// <summary>
    /// Resolves schema files from an export directory.
    /// When <c>union-map.msgpack.lz4</c> or <c>union-map.json</c> is present, its path is included.
    /// </summary>
    public static Paths Resolve(string schemaDir)
    {
        if (string.IsNullOrWhiteSpace(schemaDir))
            throw new ArgumentException("Schema directory cannot be empty.", nameof(schemaDir));

        string fullDir = Path.GetFullPath(schemaDir);
        string schema = SchemaFile.Find(fullDir, SchemasStem)
            ?? Path.Combine(fullDir, SchemasFileName);
        string index = SchemaFile.Find(fullDir, TypeIndexStem)
            ?? Path.Combine(fullDir, TypeIndexFileName);
        string? unionMap = SchemaFile.Find(fullDir, UnionMapStem);
        return new Paths(
            Path.GetFullPath(schema),
            Path.GetFullPath(index),
            string.IsNullOrWhiteSpace(unionMap) ? null : Path.GetFullPath(unionMap)
        );
    }
}
