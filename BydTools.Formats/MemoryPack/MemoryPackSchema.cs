using System.Diagnostics.CodeAnalysis;
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
    private readonly HashSet<string> _markers;
    private readonly Dictionary<string, string> _kinds;
    private readonly Dictionary<string, byte> _enumWidth;
    private readonly Dictionary<string, MpUnmanagedLayout> _unmanaged;
    private readonly object _tagGate = new();
    private readonly Dictionary<string, List<MpType>> _childrenByParent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MpType[]> _descendants = new(StringComparer.Ordinal);
    private readonly Lazy<MemoryPackUnionResolver> _defaultUnionResolver;

    internal MemoryPackSchema(
        Dictionary<string, List<MpType>> byTarget,
        Dictionary<string, MpType> byWrapper,
        Dictionary<string, List<string>> byBasename,
        Dictionary<string, string> kinds,
        HashSet<string> markers,
        Dictionary<string, byte> enumWidth,
        Dictionary<string, MpUnmanagedLayout> unmanaged)
    {
        _byTarget = byTarget;
        _byWrapper = byWrapper;
        _byBasename = byBasename;
        _kinds = kinds;
        _markers = markers;
        _enumWidth = enumWidth;
        _unmanaged = unmanaged;
        IndexDerivedTypes();
        RootResolver = new MemoryPackRootResolver(this);
        _defaultUnionResolver = new Lazy<MemoryPackUnionResolver>(
            () => new MemoryPackUnionResolver(this),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal IReadOnlyDictionary<string, List<MpType>> ByTarget => _byTarget;
    internal IReadOnlyDictionary<string, List<string>> ByBasename => _byBasename;

    public MemoryPackRootResolver RootResolver { get; }

    public static MemoryPackSchema Load(string schemasPath, string typeIndexPath) =>
        MemoryPackSchemaLoader.Load(schemasPath, typeIndexPath);

    internal static MemoryPackSchema LoadFromBytes(
        ReadOnlyMemory<byte> schemasData,
        ReadOnlyMemory<byte> indexData,
        ReadOnlyMemory<byte> enumsData = default,
        ReadOnlyMemory<byte> unmanagedData = default) =>
        MemoryPackSchemaLoader.LoadFromBytes(schemasData, indexData, enumsData, unmanagedData);

    public static MemoryPackSchema Load(
        JsonDocument schemaDoc,
        JsonDocument indexDoc,
        JsonDocument? enumsDoc = null,
        JsonDocument? unmanagedDoc = null) =>
        MemoryPackSchemaLoader.Load(schemaDoc, indexDoc, enumsDoc, unmanagedDoc);

    public bool TryEnumWidth(string type, out byte width) =>
        _enumWidth.TryGetValue(type, out width);

    public bool TryUnmanaged(string type, [NotNullWhen(true)] out MpUnmanagedLayout? layout)
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

    public IReadOnlyList<MpType> UnionCandidates(string staticType, string? hint) =>
        _defaultUnionResolver.Value.GetCandidates(staticType, hint);

    public bool TryBasename(string basename, string? folderHint, out string? targetType, out string? error) =>
        RootResolver.TryBasename(basename, folderHint, out targetType, out error);

    public bool TryIdentifyRoot(string relativePath, ReadOnlySpan<byte> data, out string? targetType, out string? error) =>
        RootResolver.TryIdentifyRoot(relativePath, data, out targetType, out error);

    public static int? PeekMemberCount(ReadOnlySpan<byte> data) =>
        MemoryPackRootResolver.PeekMemberCount(data);

    public MpKind KindOf(string type)
    {
        MpKind kind = Classify(type, _kinds);
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
        return Require(name, null);
    }

    public MpType Require(string targetType, string? hint) =>
        TryResolve(targetType, hint, out var type, out string? error)
            ? type!
            : throw new MemoryPackTypeException(error ?? $"No MemoryPack schema for {targetType}.");

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

    internal bool HasSerializeMarker(string name) =>
        _markers.Contains(name)
        || (_byTarget.TryGetValue(name, out var types) && types.Any(t => t.SerializeMarker));

    internal int? WireLength(string targetType)
    {
        if (!_byTarget.TryGetValue(targetType, out var list) || list.Count == 0)
            return null;
        MpType type = list.FirstOrDefault(t => t.SerializeMarker) ?? list[0];
        return type.Wire.Length;
    }

    internal static bool ParentNameMatches(string parent, string staticType)
    {
        if (parent == staticType)
            return true;
        return parent.EndsWith("." + staticType, StringComparison.Ordinal)
            || parent.EndsWith("+" + staticType, StringComparison.Ordinal)
            || staticType.EndsWith("." + parent, StringComparison.Ordinal)
            || staticType.EndsWith("+" + parent, StringComparison.Ordinal);
    }

    internal static string? UnmanagedShortName(string type)
    {
        if (type.Contains('`', StringComparison.Ordinal))
            return null;
        int sep = type.LastIndexOfAny(['.', '+']);
        return sep >= 0 ? type[(sep + 1)..] : null;
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

    private static IEnumerable<string> UnmanagedLookupKeys(string type)
    {
        yield return type;
        string dotted = type.Replace('+', '.');
        if (dotted != type)
            yield return dotted;
        string plused = type.Replace('.', '+');
        if (plused != type)
            yield return plused;
        if (type.IndexOfAny(['.', '+']) < 0 && UnmanagedShortName(type) is string shortName)
            yield return shortName;
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
}
