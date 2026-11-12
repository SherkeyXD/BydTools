using System.Text.Json;

namespace BydTools.Formats.MemoryPack;

public enum MpKind
{
    Bool,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,
    String,
    Enum,
    Vector2,
    Vector3,
    Vector4,
    Vector2Int,
    Vector3Int,
    List,
    Dictionary,
    DictionaryV2,
    Nullable,
    Object,
    AnimationCurve,
    Unmanaged,
}

public sealed class MpMember
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required MpKind Kind { get; init; }
    /// <summary>
    /// Native collection encoding for this field from schema <c>wire_order</c>.
    /// <c>memcpy</c> is i32 count then <c>count * payload_size</c> with no per-element header.
    /// </summary>
    public string? CollectionEncoding { get; init; }
}

/// <summary>One <c>*ForMemoryPack</c> wrapper. <see cref="Wire"/> is the exporter wire_order.</summary>
public sealed class MpType
{
    public required string TargetType { get; init; }
    public required string WrapperType { get; init; }
    public required bool HasDeserialize { get; init; }
    public required MpMember[] Wire { get; set; }
    public required MpMember[] Own { get; set; }
    public int? MinFieldOffset { get; init; }
    public bool SerializeMarker { get; set; }
    /// <summary>Exporter <c>parent_type</c>. Union fallback collects wrappers with this parent.</summary>
    public string? ParentType { get; init; }
    /// <summary>
    /// Nested MemoryPack <c>TFormatter</c> members (UTF-8 strings). Used when a
    /// field's <c>collection_encoding</c> is <c>memorypack</c> (<c>ReadValue&lt;T&gt;</c>).
    /// </summary>
    public MpMember[]? MemorypackWire { get; set; }
}

public sealed class MpUnmanagedField
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required int Offset { get; init; }
    public int? Size { get; init; }
}

/// <summary>Valuetype memcpy payload from <c>unmanaged.json</c> (no object header).</summary>
public sealed class MpUnmanagedLayout
{
    public required string TypeName { get; init; }
    public required int PayloadSize { get; init; }
    public required MpUnmanagedField[] Fields { get; init; }
    /// <summary>
    /// Native <c>T[]</c> / <c>List&lt;T&gt;</c> encoding from schema <c>unmanaged.json</c>.
    /// <c>memcpy</c> is i32 count then <c>count * PayloadSize</c> with no per-element object header.
    /// </summary>
    public string? CollectionEncoding { get; init; }
}

public sealed class MemoryPackSchema
{
    private readonly Dictionary<string, List<MpType>> _byTarget;
    private readonly Dictionary<string, MpType> _byWrapper;
    private readonly Dictionary<string, List<string>> _byBasename;
    private readonly Dictionary<string, bool> _marker = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _kinds;
    private readonly Dictionary<string, byte> _enumWidth = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MpUnmanagedLayout> _unmanaged = new(StringComparer.Ordinal);
    private readonly object _tagGate = new();
    private readonly Dictionary<string, MpType[]> _unionCandidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MpType>> _childrenByParent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MpType[]> _descendants = new(StringComparer.Ordinal);
    private MpType[]? _dataConcretes;
    private readonly List<(HashSet<string> Tokens, string Name, int? Wire)> _basenameTokenIndex = [];
    private readonly List<(HashSet<string> Tokens, int Wire, bool Marker, string Name)> _targetTokenIndex = [];

    private MemoryPackSchema(
        Dictionary<string, List<MpType>> byTarget,
        Dictionary<string, MpType> byWrapper,
        Dictionary<string, List<string>> byBasename,
        Dictionary<string, string> kinds
    )
    {
        _byTarget = byTarget;
        _byWrapper = byWrapper;
        _byBasename = byBasename;
        _kinds = kinds;
    }

    public static MemoryPackSchema Load(string schemasPath, string typeIndexPath)
    {
        if (!File.Exists(schemasPath))
            throw new FileNotFoundException("schemas.json / schemas.msgpack.lz4 was not found.", schemasPath);
        if (!File.Exists(typeIndexPath))
            throw new FileNotFoundException("type_index.json / type_index.msgpack.lz4 was not found.", typeIndexPath);

        using var schemaDoc = SchemaFile.Parse(schemasPath);
        using var indexDoc = SchemaFile.Parse(typeIndexPath);
        var schema = Load(schemaDoc, indexDoc);
        LoadEnumWidths(schema, schemasPath);
        LoadUnmanaged(schema, schemasPath);
        return schema;
    }

    public static MemoryPackSchema Load(
        JsonDocument schemaDoc,
        JsonDocument indexDoc,
        JsonDocument? enumsDoc = null,
        JsonDocument? unmanagedDoc = null)
    {
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var byBasename = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var markers = new HashSet<string>(StringComparer.Ordinal);
        {
            var root = indexDoc.RootElement;
            if (root.TryGetProperty("by_type", out var byType))
            {
                foreach (var typeProp in byType.EnumerateObject())
                {
                    if (typeProp.Value.TryGetProperty("serialize_marker", out var marker) && marker.ValueKind == JsonValueKind.True)
                        markers.Add(typeProp.Name);
                    if (!typeProp.Value.TryGetProperty("entries", out var entries))
                        continue;
                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("wire", out var wire))
                            continue;
                        foreach (var member in wire.EnumerateArray())
                        {
                            if (!member.TryGetProperty("type", out var typeEl) || !member.TryGetProperty("kind", out var kindEl))
                                continue;
                            string? type = typeEl.GetString();
                            string? kind = kindEl.GetString();
                            if (type != null && kind != null)
                                kinds.TryAdd(type, kind);
                        }
                    }
                }
            }

            if (root.TryGetProperty("by_basename", out var basenames))
            {
                foreach (var prop in basenames.EnumerateObject())
                {
                    var names = new List<string>();
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        string? name = item.GetString();
                        if (name != null)
                            names.Add(name);
                    }

                    if (names.Count > 0)
                        byBasename[prop.Name] = names;
                }
            }
        }

        var byTarget = new Dictionary<string, List<MpType>>(StringComparer.Ordinal);
        var byWrapper = new Dictionary<string, MpType>(StringComparer.Ordinal);
        {
            if (!schemaDoc.RootElement.TryGetProperty("wrappers", out var wrappers))
                throw new InvalidDataException("schemas export has no wrappers array.");

            foreach (var wrapper in wrappers.EnumerateArray())
            {
                string target = Required(wrapper, "target_type");
                string wrapperType = Required(wrapper, "wrapper_type");
                bool hasDeserialize = wrapper.TryGetProperty("has_deserialize", out var deser) && deser.ValueKind == JsonValueKind.True;
                var wire = ReadMembers(wrapper, "wire_order", kinds);
                int? minOffset = null;
                if (wrapper.TryGetProperty("members_from_target_fields", out var fields))
                {
                    foreach (var field in fields.EnumerateArray())
                    {
                        if (field.TryGetProperty("offset", out var offsetEl) && offsetEl.TryGetInt32(out int offset))
                            minOffset = minOffset is int current ? Math.Min(current, offset) : offset;
                    }
                }

                string? parentType = wrapper.TryGetProperty("parent_type", out var parentEl)
                    ? parentEl.GetString()
                    : null;
                var typeObj = new MpType
                {
                    TargetType = target,
                    WrapperType = wrapperType,
                    HasDeserialize = hasDeserialize,
                    Wire = wire,
                    Own = wire,
                    MinFieldOffset = minOffset,
                    SerializeMarker = markers.Contains(target),
                    ParentType = parentType,
                    MemorypackWire = ReadMembers(wrapper, "memorypack_wire", kinds) is { Length: > 0 } mp
                        ? mp
                        : null,
                };
                if (!byTarget.TryGetValue(target, out var list))
                {
                    list = [];
                    byTarget[target] = list;
                }

                list.Add(typeObj);
                byWrapper[wrapperType] = typeObj;
            }
        }

        var schema = new MemoryPackSchema(byTarget, byWrapper, byBasename, kinds);
        foreach (var name in markers)
            schema._marker[name] = true;
        schema.IndexDerivedTypes();
        schema.BuildTokenIndexes();
        if (enumsDoc != null)
            ApplyEnums(schema, enumsDoc.RootElement);
        if (unmanagedDoc != null)
            ApplyUnmanaged(schema, unmanagedDoc.RootElement);
        return schema;
    }

    private static void LoadEnumWidths(MemoryPackSchema schema, string schemasPath)
    {
        string? dir = Path.GetDirectoryName(schemasPath);
        if (string.IsNullOrEmpty(dir))
            return;
        string? enumsPath = SchemaFile.Find(dir, MemoryPackExport.EnumsStem);
        if (enumsPath == null)
            return;
        using var doc = SchemaFile.Parse(enumsPath);
        ApplyEnums(schema, doc.RootElement);
    }

    private static void ApplyEnums(MemoryPackSchema schema, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in root.EnumerateArray())
        {
            string? name = item.TryGetProperty("type_name", out var nameEl) ? nameEl.GetString() : null;
            if (name == null || !item.TryGetProperty("width", out var widthEl) || !widthEl.TryGetInt32(out int width))
                continue;
            if (width is not (1 or 2 or 4 or 8))
                continue;
            byte w = (byte)width;
            schema._enumWidth[name] = w;
            int sep = name.LastIndexOfAny(['.', '+']);
            if (sep >= 0)
                schema._enumWidth.TryAdd(name[(sep + 1)..], w);
        }
    }

    public bool TryEnumWidth(string type, out byte width) =>
        _enumWidth.TryGetValue(type, out width);

    public bool TryUnmanaged(string type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MpUnmanagedLayout? layout)
    {
        foreach (string key in UnmanagedLookupKeys(type))
        {
            if (_unmanaged.TryGetValue(key, out layout))
                return true;
        }

        layout = null;
        return false;
    }

    public bool HasWrapper(string type)
    {
        if (_byTarget.ContainsKey(type) || _byWrapper.ContainsKey(type))
            return true;
        foreach (string key in UnmanagedLookupKeys(type))
        {
            if (_byTarget.ContainsKey(key) || _byWrapper.ContainsKey(key))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> UnmanagedLookupKeys(string type)
    {
        yield return type;
        string dotted = type.Replace('+', '.');
        if (dotted != type)
            yield return dotted;
        string plused = type.Replace('.', '+');
        if (plused != type)
            yield return plused;
        // Only unqualified names may use the leaf alias. A qualified query
        // such as Beyond.Gameplay.Core.Entity must not match
        // UnityEngine.HyperGryph.ECS.Entity via the shared leaf "Entity".
        if (type.IndexOfAny(['.', '+']) < 0 && UnmanagedShortName(type) is string shortName)
            yield return shortName;
    }

    /// <summary>
    /// Leaf name for non-generic types only. <c>Optional`1&lt;UnityEngine.Vector3&gt;</c>
    /// must not look up as <c>Vector3</c>.
    /// </summary>
    private static string? UnmanagedShortName(string type)
    {
        if (type.Contains('`', StringComparison.Ordinal))
            return null;
        int sep = type.LastIndexOfAny(['.', '+']);
        return sep >= 0 ? type[(sep + 1)..] : null;
    }

    private static void LoadUnmanaged(MemoryPackSchema schema, string schemasPath)
    {
        string? dir = Path.GetDirectoryName(schemasPath);
        if (string.IsNullOrEmpty(dir))
            return;
        string? path = SchemaFile.Find(dir, MemoryPackExport.UnmanagedStem);
        if (path == null)
            return;
        using var doc = SchemaFile.Parse(path);
        ApplyUnmanaged(schema, doc.RootElement);
    }

    private static void ApplyUnmanaged(MemoryPackSchema schema, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in root.EnumerateArray())
        {
            string? name = item.TryGetProperty("type_name", out var nameEl) ? nameEl.GetString() : null;
            if (name == null || !item.TryGetProperty("payload_size", out var sizeEl) || !sizeEl.TryGetInt32(out int payload) || payload <= 0)
                continue;
            var fields = new List<MpUnmanagedField>();
            if (item.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fieldsEl.EnumerateArray())
                {
                    string? fname = field.TryGetProperty("name", out var fn) ? fn.GetString() : null;
                    string? fty = field.TryGetProperty("type", out var ft) ? ft.GetString() : null;
                    if (fname == null || fty == null || !field.TryGetProperty("offset", out var offEl) || !offEl.TryGetInt32(out int offset))
                        continue;
                    int? size = field.TryGetProperty("size", out var szEl)
                        && szEl.ValueKind == JsonValueKind.Number
                        && szEl.TryGetInt32(out int sz)
                            ? sz
                            : null;
                    fields.Add(new MpUnmanagedField
                    {
                        Name = fname,
                        Type = fty,
                        Offset = offset,
                        Size = size,
                    });
                }
            }

            string? encoding = item.TryGetProperty("collection_encoding", out var encEl)
                ? encEl.GetString()
                : null;
            var layout = new MpUnmanagedLayout
            {
                TypeName = name,
                PayloadSize = payload,
                Fields = fields.ToArray(),
                CollectionEncoding = encoding,
            };
            schema._unmanaged[name] = layout;
            if (UnmanagedShortName(name) is string shortName)
                schema._unmanaged.TryAdd(shortName, layout);
        }
    }

    /// <summary>
    /// Concrete wrappers that may appear under a Beyond MemoryPack union formatter.
    /// Bare <c>Data</c> is narrowed by a finder/validator/processor field hint.
    /// Other bases use <c>parent_type</c> and same-namespace leaves.
    /// </summary>
    public IReadOnlyList<MpType> UnionCandidates(string staticType, string? hint)
    {
        if (staticType == "Data")
            return DataCandidates(hint);

        lock (_tagGate)
        {
            if (_unionCandidates.TryGetValue(staticType, out MpType[]? cached))
                return cached;
            MpType[] built = BuildNamespaceUnion(staticType);
            _unionCandidates[staticType] = built;
            return built;
        }
    }

    private MpType[] DataCandidates(string? hint)
    {
        lock (_tagGate)
        {
            _dataConcretes ??= _byTarget.TryGetValue("Data", out var list)
                ? list.Where(t => t.HasDeserialize).ToArray()
                : [];
        }

        if (string.IsNullOrEmpty(hint))
            return _dataConcretes;

        string h = hint.ToLowerInvariant();
        string? role = null;
        if (h.Contains("finder", StringComparison.Ordinal))
            role = "finder";
        else if (h.Contains("validator", StringComparison.Ordinal))
            role = "validator";
        else if (h.Contains("postprocessor", StringComparison.Ordinal) || h.Contains("processor", StringComparison.Ordinal))
            role = "processor";
        if (role == null)
            return _dataConcretes;

        MpType[] filtered = _dataConcretes
            .Where(t =>
            {
                string w = t.WrapperType.ToLowerInvariant();
                return role switch
                {
                    "finder" => w.Contains("finder", StringComparison.Ordinal)
                        && !w.Contains("validator", StringComparison.Ordinal)
                        && !w.Contains("postprocessor", StringComparison.Ordinal),
                    "validator" => w.Contains("validator", StringComparison.Ordinal),
                    _ => w.Contains("processor", StringComparison.Ordinal)
                        || w.Contains("filter", StringComparison.Ordinal)
                        || w.Contains("sort", StringComparison.Ordinal)
                        || w.Contains("convert", StringComparison.Ordinal)
                        || w.Contains("shuffle", StringComparison.Ordinal)
                        || w.Contains("replace", StringComparison.Ordinal),
                };
            })
            .ToArray();
        return filtered.Length > 0 ? filtered : _dataConcretes;
    }

    private MpType[] BuildNamespaceUnion(string staticType)
    {
        var list = new List<MpType>();
        foreach (var group in _byTarget.Values)
        {
            foreach (MpType type in group)
            {
                if (!type.HasDeserialize || type.TargetType == staticType)
                    continue;

                if (staticType.StartsWith("Beyond.Gameplay.Actions.", StringComparison.Ordinal))
                {
                    if (!type.TargetType.StartsWith("Beyond.Gameplay.Actions.", StringComparison.Ordinal))
                        continue;
                    if (type.TargetType.Contains("ActionSerializedMap", StringComparison.Ordinal)
                        || type.TargetType.Contains("ActionMapAssetRaw", StringComparison.Ordinal)
                        || type.TargetType.Contains("ActionNodePtr", StringComparison.Ordinal)
                        || type.TargetType.StartsWith("Beyond.Gameplay.Actions.Param`1", StringComparison.Ordinal)
                        || type.TargetType.StartsWith("Beyond.Gameplay.Actions.ParamOutput`1", StringComparison.Ordinal)
                        || type.TargetType.StartsWith("Beyond.Gameplay.Actions.Getter`1", StringComparison.Ordinal)
                        || type.TargetType.StartsWith("Beyond.Gameplay.Actions.List", StringComparison.Ordinal)
                        || type.TargetType.StartsWith("Beyond.Gameplay.Actions.GetInteractiveProperty`1", StringComparison.Ordinal)
                        || type.TargetType.StartsWith("Beyond.Gameplay.Actions.GetLevelScriptPropertyGeneric`1", StringComparison.Ordinal))
                        continue;
                    list.Add(type);
                    continue;
                }

                if (staticType.Contains("ModelViewStateController", StringComparison.Ordinal))
                {
                    if (type.TargetType.Contains("ModelViewStateController", StringComparison.Ordinal)
                        && type.TargetType.Contains("BehaviorData", StringComparison.Ordinal)
                        && type.Wire.Length > 0)
                        list.Add(type);
                    continue;
                }

                if (staticType.Contains("DamageProcessorBase", StringComparison.Ordinal))
                {
                    if (type.TargetType.Contains("DamageProcessor", StringComparison.Ordinal))
                        list.Add(type);
                    continue;
                }

                if (staticType.Contains("ConditionRuntimeBase", StringComparison.Ordinal)
                    && ((type.TargetType.Contains("Condition", StringComparison.Ordinal)
                            && type.TargetType.EndsWith("Runtime", StringComparison.Ordinal))
                        || type.WrapperType.Contains("ConditionRuntime", StringComparison.Ordinal)))
                {
                    list.Add(type);
                    continue;
                }

                // CalculationBase has no fields of its own. Concretes are *Calculation.
                if (staticType == "Beyond.Gameplay.Core.CalculationBase"
                    && type.TargetType.EndsWith("Calculation", StringComparison.Ordinal))
                    list.Add(type);

                if (!string.IsNullOrEmpty(type.ParentType)
                    && ParentNameMatches(type.ParentType, staticType)
                    && !list.Contains(type))
                    list.Add(type);
            }
        }

        return list.ToArray();
    }

    private static bool ParentNameMatches(string parent, string staticType)
    {
        if (parent == staticType)
            return true;
        return parent.EndsWith("." + staticType, StringComparison.Ordinal)
            || parent.EndsWith("+" + staticType, StringComparison.Ordinal)
            || staticType.EndsWith("." + parent, StringComparison.Ordinal)
            || staticType.EndsWith("+" + parent, StringComparison.Ordinal);
    }

    public MpKind KindOf(string type)
    {
        MpKind kind = Classify(type, _kinds);
        // Enums must stay enums. Closed Optional<Enum> layouts used to be
        // aliased as the inner leaf name ("AoiRadiusEnum") and hijacked every
        // enum field into an 8-byte Optional memcpy.
        if (kind is not MpKind.Object)
            return kind;
        if (TryUnmanaged(type, out _))
            return MpKind.Unmanaged;
        if (_byTarget.ContainsKey(type) || _byWrapper.ContainsKey(type))
            return MpKind.Object;
        foreach (string key in UnmanagedLookupKeys(type))
        {
            if (_enumWidth.ContainsKey(key))
                return MpKind.Enum;
            if (_byTarget.ContainsKey(key) || _byWrapper.ContainsKey(key))
                return MpKind.Object;
        }

        throw new MemoryPackTypeException($"No MemoryPack rule for \"{type}\".");
    }

    public MpType ResolveNamed(string name)
    {
        if (_byWrapper.TryGetValue(name, out MpType? wrapped))
            return wrapped;
        if (name.StartsWith("Beyond.MemoryPack.", StringComparison.Ordinal) && _byWrapper.TryGetValue(name, out wrapped))
            return wrapped;
        return Require(name, null);
    }

    public MpType Require(string targetType, string? hint)
    {
        return TryResolve(targetType, hint, out var type, out string? error)
            ? type!
            : throw new MemoryPackTypeException(error ?? $"No MemoryPack schema for {targetType}.");
    }

    public bool TryResolve(string targetType, string? hint, out MpType? type, out string? error)
    {
        type = null;
        error = null;
        if (!_byTarget.TryGetValue(targetType, out var list) || list.Count == 0)
        {
            error = $"No MemoryPack wrapper for \"{targetType}\".";
            return false;
        }

        if (list.Count == 1)
        {
            type = list[0];
            return true;
        }

        if (!string.IsNullOrEmpty(hint))
        {
            string needle = hint.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
            MpType? best = null;
            int bestScore = 0;
            bool tie = false;
            foreach (var candidate in list)
            {
                string leaf = candidate.WrapperType.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
                int score = needle.Length > 0 && leaf.Contains(needle, StringComparison.Ordinal) ? 60 + needle.Length : 0;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    tie = false;
                }
                else if (score == bestScore && score > 0)
                {
                    tie = true;
                }
            }

            if (bestScore > 0 && !tie)
            {
                type = best;
                return true;
            }

            if (tie)
            {
                error = $"Type name \"{targetType}\" is ambiguous for field \"{hint}\".";
                return false;
            }
        }

        var marker = list.FirstOrDefault(t => t.SerializeMarker);
        if (marker != null && list.Count(t => t.SerializeMarker) == 1)
        {
            type = marker;
            return true;
        }

        error = $"Type name \"{targetType}\" matches {list.Count} wrappers.";
        return false;
    }

    public bool TryBasename(string basename, string? folderHint, out string? targetType, out string? error)
    {
        targetType = null;
        error = null;
        if (_byBasename.TryGetValue(basename, out var exact) && exact.Count > 0)
        {
            targetType = Prefer(exact);
            return targetType != null || Fail(exact.Count, basename, out error);
        }

        if (basename.EndsWith("Table", StringComparison.Ordinal)
            && !basename.EndsWith("DataTable", StringComparison.Ordinal))
        {
            string dataTable = basename[..^5] + "DataTable";
            if (_byBasename.TryGetValue(dataTable, out var aliased) && aliased.Count > 0)
            {
                targetType = Prefer(aliased);
                return targetType != null || Fail(aliased.Count, dataTable, out error);
            }
        }

        string? suffix = LongestSuffix(basename);
        if (suffix != null && _byBasename.TryGetValue(suffix, out var suffixed))
        {
            targetType = Prefer(suffixed);
            return true;
        }

        if (!string.IsNullOrEmpty(folderHint) && _byBasename.TryGetValue(folderHint, out var hinted))
        {
            targetType = Prefer(hinted);
            return targetType != null || Fail(hinted.Count, folderHint, out error);
        }

        error = $"No type_index basename for \"{basename}\""
            + (folderHint == null ? "." : $" or folder hint \"{folderHint}\".");
        return false;
    }

    private string? Prefer(List<string> names)
    {
        string? marked = null;
        foreach (string name in names)
        {
            if (_marker.ContainsKey(name) || (_byTarget.TryGetValue(name, out var types) && types.Any(t => t.SerializeMarker)))
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
        foreach (string key in _byBasename.Keys)
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

    private void IndexDerivedTypes()
    {
        foreach (MpType type in _byWrapper.Values)
        {
            if (string.IsNullOrEmpty(type.ParentType))
                continue;
            if (!_childrenByParent.TryGetValue(type.ParentType, out List<MpType>? list))
            {
                list = [];
                _childrenByParent[type.ParentType] = list;
            }
            list.Add(type);
        }
    }

    /// <summary>
    /// Wrapper with <c>has_deserialize: false</c> and derived wrappers (<c>parent_type</c>).
    /// MemoryPack writes either a union tag then the unique child wire, or the child object header alone.
    /// </summary>
    public bool TryPolymorphicBase(string type, out MpType? baseType, out MpType[] derived)
    {
        baseType = null;
        derived = [];
        if (!TryResolve(type, hint: null, out MpType? resolved, out _) || resolved == null)
            return false;
        if (resolved.HasDeserialize)
            return false;
        MpType[] desc = Descendants(resolved.TargetType);
        if (desc.Length == 0)
            return false;
        baseType = resolved;
        derived = desc;
        return true;
    }

    public MpType[] Descendants(string staticType)
    {
        lock (_tagGate)
        {
            if (_descendants.TryGetValue(staticType, out MpType[]? cached))
                return cached;
            var list = new List<MpType>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(staticType);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                foreach (var (parent, children) in _childrenByParent)
                {
                    if (!ParentNameMatches(parent, current))
                        continue;
                    foreach (MpType child in children)
                    {
                        if (!seen.Add(child.TargetType))
                            continue;
                        list.Add(child);
                        queue.Enqueue(child.TargetType);
                    }
                }
            }

            MpType[] arr = [.. list];
            _descendants[staticType] = arr;
            return arr;
        }
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
            int? wire = WireLength(named);
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
            string? tokenHit = UniqueBasenameByTokens(segment, header);
            Consider(tokenHit);
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

    private string? UniqueWireMatch(List<string> names, int? header)
    {
        if (names.Count == 0)
            return null;
        if (header is not int count)
            return names.Count == 1 ? names[0] : Prefer(names);
        var hits = new List<string>();
        foreach (string name in names)
        {
            if (WireLength(name) == count)
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
            int score = overlap;
            if (marker)
                score += 100;
            hits.Add((score, overlap, name));
        }

        if (hits.Count == 0)
            return null;
        hits.Sort((a, b) => b.Score.CompareTo(a.Score) != 0 ? b.Score.CompareTo(a.Score) : b.Overlap.CompareTo(a.Overlap));
        if (hits.Count == 1 || hits[0].Score > hits[1].Score)
            return hits[0].Name;
        return null;
    }

    private int? WireLength(string targetType)
    {
        if (!_byTarget.TryGetValue(targetType, out var list) || list.Count == 0)
            return null;
        MpType type = list.FirstOrDefault(t => t.SerializeMarker) ?? list[0];
        return type.Wire.Length;
    }

    private static readonly HashSet<string> IdentityStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "data", "json", "config", "table", "system", "the", "for", "and", "of", "to", "in",
        "from", "with", "info", "type", "item", "base",
    };

    internal static HashSet<string> Tokens(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var token = new System.Text.StringBuilder();
        void Flush()
        {
            if (token.Length >= 2)
                set.Add(token.ToString());
            token.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch is '.' or '+' or '/' or '\\' or '_' or '`' or '<' or '>' or ',')
            {
                Flush();
                continue;
            }

            if (!char.IsLetterOrDigit(ch))
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

    /// <summary>
    /// GameCondition's serializable members are scopeMask, uniqueId, useCurrentScope, useGraphScope.
    /// Subclass instance fields begin at offset 144; wire_order omits the base. Intermediate bases
    /// (CompareOperator / SoilPersistentCount) contribute additional '_' fields. Empty leaves keep
    /// only the 4-field prefix.
    /// </summary>
    private void BuildTokenIndexes()
    {
        foreach (var (key, names) in _byBasename)
        {
            string? preferred = Prefer(names);
            if (preferred == null)
                continue;
            HashSet<string> tokens = Tokens(key);
            tokens.ExceptWith(IdentityStopwords);
            if (tokens.Count == 0)
                continue;
            _basenameTokenIndex.Add((tokens, preferred, WireLength(preferred)));
        }

        foreach (var (name, list) in _byTarget)
        {
            if (list.Count == 0)
                continue;
            MpType type = list[0];
            HashSet<string> nameTokens = Tokens(name);
            nameTokens.ExceptWith(IdentityStopwords);
            if (nameTokens.Count == 0)
                continue;
            _targetTokenIndex.Add((nameTokens, type.Wire.Length, type.SerializeMarker, name));
        }
    }

    private static MpMember[] ReadMembers(JsonElement wrapper, string property, Dictionary<string, string> kinds)
    {
        if (!wrapper.TryGetProperty(property, out var array))
            return [];
        var members = new List<MpMember>();
        foreach (var item in array.EnumerateArray())
        {
            string? name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            string? type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            if (name == null || type == null)
                continue;
            string? encoding = item.TryGetProperty("collection_encoding", out var encEl)
                ? encEl.GetString()
                : null;
            members.Add(new MpMember
            {
                Name = name,
                Type = type,
                Kind = Classify(type, kinds),
                CollectionEncoding = encoding,
            });
        }

        return members.ToArray();
    }

    internal static MpKind Classify(string type, Dictionary<string, string>? kinds = null)
    {
        switch (type)
        {
            case "bool" or "System.Boolean":
                return MpKind.Bool;
            case "sbyte" or "System.SByte":
                return MpKind.Int8;
            case "byte" or "System.Byte":
                return MpKind.UInt8;
            case "short" or "System.Int16":
                return MpKind.Int16;
            case "ushort" or "System.UInt16":
                return MpKind.UInt16;
            case "int" or "System.Int32" or "UnityEngine.LayerMask":
                return MpKind.Int32;
            case "uint" or "System.UInt32":
                return MpKind.UInt32;
            case "long" or "System.Int64":
                return MpKind.Int64;
            case "ulong" or "System.UInt64":
                return MpKind.UInt64;
            case "float" or "System.Single":
                return MpKind.Float32;
            case "double" or "System.Double":
                return MpKind.Float64;
            case "string" or "System.String":
                return MpKind.String;
            case "UnityEngine.Vector2":
                return MpKind.Vector2;
            case "UnityEngine.Vector3":
                return MpKind.Vector3;
            case "UnityEngine.Vector4" or "UnityEngine.Quaternion" or "UnityEngine.Color":
                return MpKind.Vector4;
            case "UnityEngine.Vector2Int":
                return MpKind.Vector2Int;
            case "UnityEngine.Vector3Int":
                return MpKind.Vector3Int;
            case "UnityEngine.AnimationCurve":
                return MpKind.AnimationCurve;
        }

        if (type.EndsWith("[]", StringComparison.Ordinal) || IsCollectionList(type))
            return MpKind.List;
        if (type.StartsWith("Beyond.SerializeFieldDictionary`2<", StringComparison.Ordinal))
            return MpKind.DictionaryV2;
        if (type.StartsWith("System.Collections.Generic.Dictionary`2<", StringComparison.Ordinal)
            || type.StartsWith("System.Collections.Generic.IDictionary`2<", StringComparison.Ordinal)
            || type.StartsWith("System.Collections.Generic.IReadOnlyDictionary`2<", StringComparison.Ordinal))
            return MpKind.Dictionary;
        if (type.StartsWith("System.Nullable`1<", StringComparison.Ordinal))
            return MpKind.Nullable;

        if (kinds != null && kinds.TryGetValue(type, out string? kind))
        {
            if (kind == "enum")
                return MpKind.Enum;
            if (kind == "unmanaged")
                return MpKind.Unmanaged;
            if (kind == "dictionary_v2")
                return MpKind.DictionaryV2;
            if (kind is "int8")
                return MpKind.Int8;
            if (kind is "uint8")
                return MpKind.UInt8;
            if (kind is "int16")
                return MpKind.Int16;
            if (kind is "uint16")
                return MpKind.UInt16;
            if (kind is "int32")
                return MpKind.Int32;
            if (kind is "uint32")
                return MpKind.UInt32;
            if (kind == "float32")
                return MpKind.Float32;
            if (kind == "bool")
                return MpKind.Bool;
            if (kind == "string")
                return MpKind.String;
        }

        return MpKind.Object;
    }

    private static bool IsCollectionList(string type)
    {
        if (type.StartsWith("System.Collections.Generic.List`1<", StringComparison.Ordinal)
            || type.StartsWith("System.Collections.Generic.HashSet`1<", StringComparison.Ordinal)
            || type.StartsWith("System.Collections.Generic.Queue`1<", StringComparison.Ordinal)
            || type.StartsWith("System.Collections.Generic.Stack`1<", StringComparison.Ordinal)
            || type.StartsWith("System.Collections.Generic.IList`1<", StringComparison.Ordinal)
            || type.StartsWith("System.Collections.Generic.ICollection`1<", StringComparison.Ordinal)
            || type.StartsWith("System.Collections.Generic.IReadOnlyList`1<", StringComparison.Ordinal)
            || type.StartsWith("System.Collections.Generic.IReadOnlyCollection`1<", StringComparison.Ordinal))
            return true;
        int tick = type.IndexOf('`');
        if (tick < 0)
            return false;
        int slash = type.LastIndexOf('.', tick);
        string leaf = slash < 0 ? type : type[(slash + 1)..];
        return leaf.StartsWith("HashSet`1<", StringComparison.Ordinal)
            || leaf.StartsWith("List`1<", StringComparison.Ordinal)
            || leaf.StartsWith("Queue`1<", StringComparison.Ordinal)
            || leaf.StartsWith("Stack`1<", StringComparison.Ordinal);
    }

    private static string Required(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.GetString() is not { } text)
            throw new InvalidDataException($"schemas.json wrapper is missing {name}.");
        return text;
    }
}