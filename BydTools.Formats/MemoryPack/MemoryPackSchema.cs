using System.Text.Json;

namespace BydTools.Formats.MemoryPack;

public enum MpKind
{
    Bool,
    Int32,
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
    Nullable,
    Object,
    AnimationCurve,
}

public sealed class MpMember
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required MpKind Kind { get; init; }
}

/// <summary>One <c>*ForMemoryPack</c> wrapper. <see cref="Wire"/> is the exporter wire_order.</summary>
public sealed class MpType
{
    public required string TargetType { get; init; }
    public required string WrapperType { get; init; }
    public required bool HasDeserialize { get; init; }
    public required MpMember[] Wire { get; init; }
    public required MpMember[] Own { get; set; }
    public MpMember[]? AbilityPayload { get; set; }
    public int? MinFieldOffset { get; init; }
    public bool SerializeMarker { get; set; }
}

public sealed class MemoryPackSchema
{
    private readonly Dictionary<string, List<MpType>> _byTarget;
    private readonly Dictionary<string, MpType> _byWrapper;
    private readonly Dictionary<string, List<string>> _byBasename;
    private readonly Dictionary<string, bool> _marker = new(StringComparer.Ordinal);
    private readonly MpType[] _abilityConcretes;
    private readonly Dictionary<string, string> _kinds;
    private readonly Dictionary<int, MpType> _abilityTags = [];
    private readonly object _tagGate = new();

    private MemoryPackSchema(
        Dictionary<string, List<MpType>> byTarget,
        Dictionary<string, MpType> byWrapper,
        Dictionary<string, List<string>> byBasename,
        Dictionary<string, string> kinds,
        MpType[] abilityConcretes
    )
    {
        _byTarget = byTarget;
        _byWrapper = byWrapper;
        _byBasename = byBasename;
        _kinds = kinds;
        _abilityConcretes = abilityConcretes;
    }

    public static MemoryPackSchema Load(string schemasPath, string typeIndexPath)
    {
        if (!File.Exists(schemasPath))
            throw new FileNotFoundException("schemas.json was not found.", schemasPath);
        if (!File.Exists(typeIndexPath))
            throw new FileNotFoundException("type_index.json was not found.", typeIndexPath);

        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var byBasename = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var markers = new HashSet<string>(StringComparer.Ordinal);
        using (var indexDoc = JsonDocument.Parse(File.ReadAllBytes(typeIndexPath)))
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
        using (var schemaDoc = JsonDocument.Parse(File.ReadAllBytes(schemasPath)))
        {
            if (!schemaDoc.RootElement.TryGetProperty("wrappers", out var wrappers))
                throw new InvalidDataException("schemas.json has no wrappers array.");

            var pending = new List<(MpType Type, List<(string Name, string Type)> Extras)>();
            foreach (var wrapper in wrappers.EnumerateArray())
            {
                string target = Required(wrapper, "target_type");
                string wrapperType = Required(wrapper, "wrapper_type");
                bool hasDeserialize = wrapper.TryGetProperty("has_deserialize", out var deser) && deser.ValueKind == JsonValueKind.True;
                var wire = ReadMembers(wrapper, "wire_order", kinds);
                var extras = new List<(string Name, string Type)>();
                int? minOffset = null;
                if (wrapper.TryGetProperty("members_from_target_fields", out var fields))
                {
                    var wireNames = new HashSet<string>(wire.Select(m => m.Name), StringComparer.Ordinal);
                    foreach (var field in fields.EnumerateArray())
                    {
                        string? name = field.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                        string? type = field.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                        if (field.TryGetProperty("offset", out var offsetEl) && offsetEl.TryGetInt32(out int offset))
                            minOffset = minOffset is int current ? Math.Min(current, offset) : offset;
                        if (name == null || type == null || wireNames.Contains(name) || IsBackingField(name))
                            continue;
                        extras.Add((name, type));
                        wireNames.Add(name);
                    }
                }

                var typeObj = new MpType
                {
                    TargetType = target,
                    WrapperType = wrapperType,
                    HasDeserialize = hasDeserialize,
                    Wire = wire,
                    Own = wire,
                    MinFieldOffset = minOffset,
                    SerializeMarker = markers.Contains(target),
                };
                pending.Add((typeObj, extras));
                if (!byTarget.TryGetValue(target, out var list))
                {
                    list = [];
                    byTarget[target] = list;
                }

                list.Add(typeObj);
                byWrapper[wrapperType] = typeObj;
            }

            foreach (var (type, extras) in pending)
            {
                if (extras.Count == 0)
                    continue;
                type.Own = type.Wire.Select(m => (m.Name, m.Type)).Concat(extras)
                    .OrderBy(m => m.Name.ToLowerInvariant(), StringComparer.Ordinal)
                    .Select(m => new MpMember
                    {
                        Name = m.Name,
                        Type = m.Type,
                        Kind = Classify(m.Type, kinds),
                    })
                    .ToArray();
            }
        }

        // AbilityActionData base prefix, then each concrete's own members.
        MpMember[] abilityBase = byTarget.TryGetValue("AbilityActionData", out var bases) && bases.Count == 1
            ? bases[0].Wire
            : [];
        var concretes = new List<MpType>();
        foreach (var type in byWrapper.Values)
        {
            if (!IsAbilityConcrete(type))
                continue;
            var names = new HashSet<string>(type.Own.Select(m => m.Name), StringComparer.Ordinal);
            type.AbilityPayload = abilityBase.Where(m => !names.Contains(m.Name)).Concat(type.Own).ToArray();
            concretes.Add(type);
        }

        // Blackboard* wrappers ship an empty wire_order; the closed generic base is the payload.
        AliasEmpty(byTarget, "BlackboardDouble", "BlackboardParamBase`2<float, double>");
        AliasEmpty(byTarget, "BlackboardInt", "BlackboardParamBase`2<int, int>");
        AliasEmpty(byTarget, "BlackboardString", "BlackboardParamBase`2<string, string>");

        var schema = new MemoryPackSchema(byTarget, byWrapper, byBasename, kinds, concretes.ToArray());
        foreach (var name in markers)
            schema._marker[name] = true;
        return schema;
    }

    public MpType? FindAbilityTag(int tag)
    {
        lock (_tagGate)
            return _abilityTags.TryGetValue(tag, out var type) ? type : null;
    }

    public void RememberAbilityTag(int tag, MpType type)
    {
        lock (_tagGate)
            _abilityTags.TryAdd(tag, type);
    }

    public IReadOnlyList<MpType> AbilityConcretes => _abilityConcretes;

    public MpKind KindOf(string type)
    {
        MpKind kind = Classify(type, _kinds);
        if (kind == MpKind.Object && !_byTarget.ContainsKey(type))
            return MpKind.Enum;
        return kind;
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

    private static void AliasEmpty(Dictionary<string, List<MpType>> byTarget, string derived, string baseName)
    {
        if (!byTarget.TryGetValue(derived, out var derivedList) || derivedList.Count != 1)
            return;
        if (!byTarget.TryGetValue(baseName, out var baseList) || baseList.Count != 1)
            return;
        var source = derivedList[0];
        if (source.Wire.Length != 0)
            return;
        var aliased = new MpType
        {
            TargetType = source.TargetType,
            WrapperType = source.WrapperType,
            HasDeserialize = source.HasDeserialize,
            Wire = baseList[0].Wire,
            Own = baseList[0].Own,
            AbilityPayload = source.AbilityPayload,
            MinFieldOffset = source.MinFieldOffset,
            SerializeMarker = source.SerializeMarker,
        };
        derivedList[0] = aliased;
    }

    private static bool IsAbilityConcrete(MpType type)
    {
        if (type.TargetType == "AbilityActionData")
            return false;
        string wrapper = type.WrapperType;
        if (wrapper.Contains("Beyond_Gameplay_Actions_", StringComparison.Ordinal)
            || wrapper.Contains("Beyond_Gameplay_AI_", StringComparison.Ordinal))
            return false;
        if (type.MinFieldOffset is int offset && offset < 32)
            return false;
        return type.TargetType == "Data" || type.TargetType.EndsWith("ActionData", StringComparison.Ordinal);
    }

    private static bool IsBackingField(string name) =>
        name.StartsWith('<') && name.Contains("k__BackingField", StringComparison.Ordinal);

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
            members.Add(new MpMember { Name = name, Type = type, Kind = Classify(type, kinds) });
        }

        return members.ToArray();
    }

    internal static MpKind Classify(string type, Dictionary<string, string>? kinds = null)
    {
        switch (type)
        {
            case "bool" or "System.Boolean":
                return MpKind.Bool;
            case "sbyte" or "byte" or "short" or "ushort" or "int" or "uint"
                or "System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16"
                or "System.Int32" or "System.UInt32" or "UnityEngine.LayerMask":
                return MpKind.Int32;
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

        if (type.EndsWith("[]", StringComparison.Ordinal) || type.StartsWith("System.Collections.Generic.List`1<", StringComparison.Ordinal))
            return MpKind.List;
        if (type.StartsWith("System.Collections.Generic.Dictionary`2<", StringComparison.Ordinal)
            || type.StartsWith("Beyond.SerializeFieldDictionary`2<", StringComparison.Ordinal))
            return MpKind.Dictionary;
        if (type.StartsWith("System.Nullable`1<", StringComparison.Ordinal))
            return MpKind.Nullable;

        if (kinds != null && kinds.TryGetValue(type, out string? kind))
        {
            if (kind == "enum")
                return MpKind.Enum;
            if (kind is "int32" or "uint32")
                return MpKind.Int32;
            if (kind == "float32")
                return MpKind.Float32;
            if (kind == "bool")
                return MpKind.Bool;
            if (kind == "string")
                return MpKind.String;
        }

        return MpKind.Object;
    }

    private static string Required(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.GetString() is not { } text)
            throw new InvalidDataException($"schemas.json wrapper is missing {name}.");
        return text;
    }
}
