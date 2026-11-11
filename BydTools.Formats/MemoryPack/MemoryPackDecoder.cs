using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace BydTools.Formats.MemoryPack;

/// <summary>
/// Decodes a Beyond.MemoryPack payload using <c>schemas.json</c> wire_order.
/// Object header: byte count, 255 null, 250 then int32 for wide counts.
/// memberCount 0 with a non-empty wire yields defaults; count below wire length
/// reads a prefix (MemoryPack versioning). Strings are StringPool int32 ids.
/// AbilityActionData / ActionBase are MemoryPack unions (tag, then concrete).
/// Concrete ability payloads are AbilityActionData members plus wrapper wire_order.
/// Valuetype payloads listed in unmanaged.json are memcpy layouts (no object header).
/// UnityEngine.AnimationCurve uses the native MemoryPack layout (count 3 + Keyframe[28]).
/// </summary>
public sealed class MemoryPackDecoder
{
    private readonly MemoryPackSchema _schema;
    private readonly BeyondStringPool? _pool;
    private readonly Dictionary<string, Dictionary<int, string>> _unionMaps;

    /// <summary>Optional field-consumption log for schema analysis. Null in production.</summary>
    public TextWriter? FieldTrace { get; set; }

    public MemoryPackDecoder(
        MemoryPackSchema schema,
        BeyondStringPool? pool = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? unionMaps = null)
    {
        _schema = schema;
        _pool = pool;
        _unionMaps = [];
        if (unionMaps == null)
            return;
        foreach (var (key, tags) in unionMaps)
            _unionMaps[key] = new Dictionary<int, string>(tags);
    }

    private bool TryMappedConcrete(string staticType, int tag, string? hint, out MpType concrete)
    {
        concrete = null!;
        string key = DataRoleKey(staticType, hint) ?? staticType;
        if (LookupUnionMap(key) is not { } map || !map.TryGetValue(tag, out string? mapped))
            return false;
        concrete = _schema.ResolveNamed(mapped);
        return true;
    }

    private static string? DataRoleKey(string staticType, string? hint)
    {
        if (staticType != "Data" || string.IsNullOrEmpty(hint))
            return null;
        string h = hint.ToLowerInvariant();
        if (h.Contains("finder", StringComparison.Ordinal))
            return "Finder.Data";
        if (h.Contains("validator", StringComparison.Ordinal))
            return "Validator.Data";
        if (h.Contains("postprocessor", StringComparison.Ordinal) || h.Contains("processor", StringComparison.Ordinal))
            return "PostProcessor.Data";
        return null;
    }

    private Dictionary<int, string>? LookupUnionMap(string staticType)
    {
        foreach (string key in UnionLookupKeys(staticType))
        {
            if (_unionMaps.TryGetValue(key, out var map))
                return map;
        }
        return null;
    }

    private static IEnumerable<string> UnionLookupKeys(string staticType)
    {
        yield return staticType;
        string dotted = staticType.Replace('+', '.');
        if (dotted != staticType)
            yield return dotted;
        string plused = staticType.Replace('.', '+');
        if (plused != staticType)
            yield return plused;

        int dot = staticType.LastIndexOf('.');
        if (dot >= 0)
            yield return staticType[(dot + 1)..];

        string nested = dot >= 0 ? staticType[(dot + 1)..] : staticType;
        if (nested.Contains('+'))
        {
            yield return nested;
            string[] parts = nested.Split('+');
            if (parts.Length >= 2)
                yield return parts[^2] + "+" + parts[^1];
            yield return parts[^1];
            if (parts.Length >= 2)
                yield return parts[^2] + "." + parts[^1];
        }
    }

    public JsonNode? Decode(ReadOnlySpan<byte> data, string rootType)
    {
        var cursor = new Cursor(data.ToArray());
        JsonNode? node = ReadValue(cursor, rootType, hint: null, depth: 0, sandbox: false);
        if (cursor.Remaining != 0)
        {
            throw new InvalidDataException(
                $"Decoded {rootType} and left {cursor.Remaining} trailing byte(s) at offset {cursor.Position}."
            );
        }

        return node;
    }

    public string ToJson(ReadOnlySpan<byte> data, string rootType)
    {
        JsonNode? node = Decode(data, rootType);
        return (node?.ToJsonString(JsonDataMemoryPack.JsonOptions) ?? "null") + "\n";
    }

    private JsonNode? ReadValue(
        Cursor cursor,
        string type,
        string? hint,
        int depth,
        bool sandbox,
        string? collectionEncoding = null)
    {
        if (depth > 64)
            throw new InvalidDataException($"MemoryPack nesting is deeper than 64 at offset {cursor.Position}.");

        bool optional = type.StartsWith("Beyond.Optional`1<", StringComparison.Ordinal)
            || type.StartsWith("Beyond.Optional<", StringComparison.Ordinal);
        // Nested Optional<T> of a memcpy T is unmanaged only when the schema
        // field says so. Other Optional sites use the object-header formatter.
        // Depth 0 keeps the unmanaged path so Optional<Vector3> roots still decode.
        if (optional && depth > 0 && collectionEncoding != "memcpy")
            return ReadBeyondOptional(cursor, type, depth, sandbox);

        if (_schema.TryUnmanaged(type, out MpUnmanagedLayout? unmanaged))
        {
            // Arrays already peek ForMemoryPack object headers (wire length)
            // vs raw memcpy. Scalar unmanaged+wrapper mix is per-field:
            // `collection_encoding: formatter` (TrackingBounds in
            // TrackingRouteInfo) vs default memcpy (GameplayTag, FKeyframe,
            // TriggerShapeData). Peeking the first payload byte against wire
            // length collides with real memcpy data (AnimationConfig).
            bool wantFormatter = collectionEncoding == "formatter";
            if (wantFormatter
                && TryReadUnmanagedFormatter(cursor, type, depth, sandbox, out JsonNode? formatted)
                && formatted != null)
                return UnwrapUnmanagedNullable(type, formatted);
            JsonNode blob = ReadUnmanaged(cursor, unmanaged, depth, sandbox);
            return UnwrapUnmanagedNullable(type, blob);
        }
        if (optional)
            return ReadBeyondOptional(cursor, type, depth, sandbox);
        if (collectionEncoding == "memorypack")
            return ReadMemoryPackValue(cursor, type, depth, sandbox);
        if (IsTaggedUnion(type))
            return ReadTaggedUnion(cursor, type, hint, depth, sandbox);
        if (_schema.TryPolymorphicBase(type, out MpType? polyBase, out MpType[] derived)
            && polyBase != null
            && TryReadPolymorphic(cursor, polyBase, derived, depth, sandbox, out JsonNode? poly))
            return poly;

        MpKind kind = _schema.KindOf(type);
        switch (kind)
        {
            case MpKind.Bool:
                byte flag = cursor.U8();
                if (flag > 1)
                    throw new InvalidDataException($"Boolean {flag} at offset {cursor.Position - 1}.");
                return JsonValue.Create(flag != 0);
            case MpKind.Int8:
                return JsonValue.Create((sbyte)cursor.U8());
            case MpKind.UInt8:
                return JsonValue.Create((int)cursor.U8());
            case MpKind.Int16:
                return JsonValue.Create((short)cursor.U16());
            case MpKind.UInt16:
                return JsonValue.Create((int)cursor.U16());
            case MpKind.Int32:
                return JsonValue.Create(cursor.I32());
            case MpKind.Enum:
                return JsonValue.Create(ReadEnum(cursor, type));
            case MpKind.Int64:
                return JsonValue.Create(cursor.I64());
            case MpKind.UInt64:
                return JsonValue.Create(cursor.U64());
            case MpKind.Float32:
                return JsonValue.Create(cursor.F32());
            case MpKind.Float64:
                return JsonValue.Create(cursor.F64());
            case MpKind.String:
                return collectionEncoding == "utf8" ? ReadMemoryPackString(cursor) : ReadString(cursor);
            case MpKind.Vector2:
                return Vec(cursor.F32(), cursor.F32());
            case MpKind.Vector3:
                return Vec(cursor.F32(), cursor.F32(), cursor.F32());
            case MpKind.Vector4:
                return Vec(cursor.F32(), cursor.F32(), cursor.F32(), cursor.F32());
            case MpKind.Vector2Int:
                return Vec(cursor.I32(), cursor.I32());
            case MpKind.Vector3Int:
                return Vec(cursor.I32(), cursor.I32(), cursor.I32());
            case MpKind.List:
                return ReadList(cursor, ElementType(type), hint, depth, sandbox, collectionEncoding);
            case MpKind.Dictionary:
                return ReadDictionary(cursor, type, depth, sandbox, v2: false);
            case MpKind.DictionaryV2:
                return ReadDictionary(cursor, type, depth, sandbox, v2: true);
            case MpKind.Nullable:
                return ReadNullable(cursor, InnerType(type), depth, sandbox);
            case MpKind.AnimationCurve:
                return ReadUnityAnimationCurve(cursor);
            case MpKind.Unmanaged:
                if (_schema.TryUnmanaged(type, out unmanaged))
                    return ReadUnmanaged(cursor, unmanaged, depth, sandbox);
                goto default;
            default:
                // Wrapper FQCNs (BuffData modifier list elements) resolve via byWrapper.
                MpType resolved = _schema.ResolveNamed(type);
                return ReadObject(cursor, resolved, abilityPayload: false, depth, sandbox);
        }
    }

    /// <summary>
    /// AbilityActionData concretes, and bare <c>Data</c> leaves that share those wrappers,
    /// carry the four AbilityActionData fields before the leaf wire_order.
    /// </summary>
    private static bool UsesAbilityPayload(string staticType, MpType concrete) =>
        concrete.AbilityPayload != null
        && (staticType == "AbilityActionData"
            || staticType == "Data"
            || staticType.Contains("AbilityActionData", StringComparison.Ordinal));

    private bool IsTaggedUnion(string type) =>
        LookupUnionMap(type) != null
        || type is "AbilityActionData"
            or "Beyond.Gameplay.Actions.ActionBase"
            or "Beyond.Gameplay.Actions.PureGetter"
            or "Beyond.Gameplay.Actions.ActionHeader"
            or "Beyond.Gameplay.Core.ModelViewStateController.AbstractBehaviorData"
            or "Beyond.Gameplay.Core.DamageProcessorBase"
            or "Beyond.Gameplay.Core.HealProcessorBase"
            or "HealProcessorBase"
            or "Beyond.Gameplay.Core.PoiseProcessorBase"
            or "PoiseProcessorBase"
            or "Beyond.Gameplay.ConditionRuntimeBase"
            or "Beyond.Gameplay.GameCondition"
            or "Beyond.Gameplay.Core.CalculationBase"
            or "CalculationBase"
            or "Finder.Data"
            or "Beyond.Gameplay.Core.Selector.Finder.Data"
            or "Beyond.Gameplay.Core.Selector+Finder+Data"
            or "Validator.Data"
            or "Beyond.Gameplay.Core.Selector.Validator.Data"
            or "Beyond.Gameplay.Core.Selector+Validator+Data"
            or "PostProcessor.Data"
            or "Beyond.Gameplay.Core.Selector.PostProcessor.Data"
            or "Beyond.Gameplay.Core.Selector+PostProcessor+Data"
            or "SpawnerPreprocess.Data"
            or "Beyond.Gameplay.SpawnerPreprocess.Data"
            or "Beyond.Gameplay.SpawnerPreprocess+Data"
            or "SpawnerActionData"
            or "Beyond.Gameplay.SpawnerActionData"
            or "Data";

    /// <summary>
    /// Valuetype memcpy payload from schema <c>unmanaged.json</c>. No object header.
    /// Field offsets include padding; the cursor always consumes <see cref="MpUnmanagedLayout.PayloadSize"/>.
    /// </summary>
    private JsonNode ReadUnmanaged(Cursor cursor, MpUnmanagedLayout layout, int depth, bool sandbox)
    {
        if (cursor.Remaining < layout.PayloadSize)
        {
            throw new InvalidDataException(
                $"{layout.TypeName} needs {layout.PayloadSize} bytes at offset {cursor.Position}, have {cursor.Remaining}."
            );
        }

        int origin = cursor.Position;
        var obj = new JsonObject();
        foreach (MpUnmanagedField field in layout.Fields.OrderBy(f => f.Offset))
        {
            cursor.Position = origin + field.Offset;
            obj[field.Name] = ReadUnmanagedField(cursor, field, depth, sandbox);
        }

        cursor.Position = origin + layout.PayloadSize;
        return obj;
    }

    /// Schema `unmanaged.json` stores `Nullable<T>` / memcpy `Optional<T>` as the
    /// IL2CPP blob (flag + ABI pad + `value`). Disabled Optional is JSON null;
    /// enabled Optional keeps `{ enabled, value }` (same shape as the object-header path).
    private static JsonNode? UnwrapUnmanagedNullable(string type, JsonNode blob)
    {
        bool nullable = type.StartsWith("System.Nullable`1<", StringComparison.Ordinal)
            || type.StartsWith("System.Nullable<", StringComparison.Ordinal);
        bool optional = type.StartsWith("Beyond.Optional`1<", StringComparison.Ordinal)
            || type.StartsWith("Beyond.Optional<", StringComparison.Ordinal);
        if (!nullable && !optional)
            return blob;
        if (blob is not JsonObject obj)
            return blob;
        string flagName = nullable ? "hasValue" : "enabled";
        if (obj[flagName] is JsonValue flag && flag.TryGetValue(out bool has) && !has)
            return null;
        if (nullable)
        {
            JsonNode? value = obj["value"];
            if (value != null)
                obj.Remove("value");
            return value ?? blob;
        }
        return blob;
    }

    private JsonNode? ReadUnmanagedField(Cursor cursor, MpUnmanagedField field, int depth, bool sandbox)
    {
        if (_schema.TryUnmanaged(field.Type, out MpUnmanagedLayout? nested))
            return ReadUnmanaged(cursor, nested, depth + 1, sandbox);

        int size = field.Size ?? 0;
        MpKind kind = _schema.KindOf(field.Type);
        if (kind == MpKind.Bool || field.Type is "bool" or "System.Boolean")
            return JsonValue.Create(cursor.U8() != 0);
        if (kind == MpKind.Float32 || field.Type is "float" or "System.Single")
            return JsonValue.Create(cursor.F32());
        if (kind == MpKind.Float64 || field.Type is "double" or "System.Double")
            return JsonValue.Create(cursor.F64());
        if (kind == MpKind.Int64 || field.Type is "long" or "System.Int64")
            return JsonValue.Create(cursor.I64());
        if (kind == MpKind.UInt64 || field.Type is "ulong" or "System.UInt64")
            return JsonValue.Create(cursor.U64());
        if (kind == MpKind.Vector2)
            return Vec(cursor.F32(), cursor.F32());
        if (kind == MpKind.Vector3)
            return Vec(cursor.F32(), cursor.F32(), cursor.F32());
        if (kind == MpKind.Vector4)
            return Vec(cursor.F32(), cursor.F32(), cursor.F32(), cursor.F32());
        if (kind == MpKind.Vector2Int)
            return Vec(cursor.I32(), cursor.I32());
        if (kind == MpKind.Vector3Int)
            return Vec(cursor.I32(), cursor.I32(), cursor.I32());
        if (kind == MpKind.Enum)
            return JsonValue.Create(ReadEnumWidth(cursor, field.Type, size));
        return size switch
        {
            1 => JsonValue.Create((int)cursor.U8()),
            2 => JsonValue.Create((short)cursor.U16()),
            8 => JsonValue.Create(cursor.I64()),
            _ => JsonValue.Create(cursor.I32()),
        };
    }

    private int ReadEnumWidth(Cursor cursor, string type, int size)
    {
        if (size is 1 or 2 or 8)
        {
            return size switch
            {
                1 => cursor.U8(),
                2 => (short)cursor.U16(),
                _ => (int)cursor.I64(),
            };
        }

        return ReadEnum(cursor, type);
    }

    /// <summary>
    /// Most MemoryPack enums are int32. LifeType and IdentifierType are bytes.
    /// StackingType and AdditiveEffectType are signed int16. Reading them as int32
    /// shifts the next field and turns a later memberCount 3 into Boolean 3.
    /// </summary>
    private int ReadEnum(Cursor cursor, string type)
    {
        if (_schema.TryEnumWidth(type, out byte width))
        {
            return width switch
            {
                1 => cursor.U8(),
                2 => (short)cursor.U16(),
                8 => (int)cursor.I64(),
                _ => cursor.I32(),
            };
        }

        int sep = type.LastIndexOfAny(['.', '+']);
        string leaf = sep >= 0 ? type[(sep + 1)..] : type;
        if (leaf is "LifeType" or "IdentifierType")
            return cursor.U8();
        if (leaf is "StackingType" or "AdditiveEffectType")
            return (short)cursor.U16();
        return cursor.I32();
    }

    /// <summary>
    /// UnityEngine.AnimationCurve MemoryPack: memberCount 3, preWrapMode, postWrapMode,
    /// then Keyframe[] where each key is a raw 28-byte unmanaged blob.
    /// </summary>
    private static JsonNode? ReadUnityAnimationCurve(Cursor cursor)
    {
        int origin = cursor.Position;
        int? count = ReadHeader(cursor);
        if (count == null)
            return null;
        // MemoryPack empty/default curve (versioning): unlock trailing decode.
        if (count == 0)
        {
            return new JsonObject
            {
                ["_type"] = "AnimationCurve",
                ["_skipped"] = true,
            };
        }
        if (count != 3)
        {
            throw new InvalidDataException(
                $"UnityEngine.AnimationCurve memberCount {count} at offset {origin} (expected 3)."
            );
        }

        int preWrapMode = cursor.I32();
        int postWrapMode = cursor.I32();
        int keyCount = cursor.I32();
        JsonNode? keys;
        if (keyCount == -1)
            keys = null;
        else
        {
            if (keyCount < 0 || keyCount > 1_000_000)
                throw new InvalidDataException($"AnimationCurve key count {keyCount} at offset {cursor.Position - 4}.");
            var array = new JsonArray();
            for (int i = 0; i < keyCount; i++)
            {
                if (cursor.Remaining < 28)
                {
                    throw new InvalidDataException(
                        $"AnimationCurve keyframe needs 28 bytes at offset {cursor.Position}."
                    );
                }

                array.Add((JsonNode)new JsonObject
                {
                    ["time"] = JsonValue.Create(cursor.F32()),
                    ["value"] = JsonValue.Create(cursor.F32()),
                    ["inTangent"] = JsonValue.Create(cursor.F32()),
                    ["outTangent"] = JsonValue.Create(cursor.F32()),
                    ["weightedMode"] = JsonValue.Create(cursor.I32()),
                    ["inWeight"] = JsonValue.Create(cursor.F32()),
                    ["outWeight"] = JsonValue.Create(cursor.F32()),
                });
            }

            keys = array;
        }

        return new JsonObject
        {
            ["preWrapMode"] = JsonValue.Create(preWrapMode),
            ["postWrapMode"] = JsonValue.Create(postWrapMode),
            ["keys"] = keys,
        };
    }

    private JsonNode? ReadBeyondOptional(Cursor cursor, string type, int depth, bool sandbox)
    {
        int origin = cursor.Position;
        int? count = ReadHeader(cursor);
        if (count == null)
            return null;
        // A disabled Optional is written as an empty object (memberCount 0).
        if (count == 0)
            return null;
        if (count != 2)
        {
            throw new InvalidDataException(
                $"memberCount {count} at offset {origin} does not match {type} (wire 2, merged 2)."
            );
        }

        cursor.Position = origin;
        return ReadObject(cursor, _schema.Require(type, hint: null), abilityPayload: false, depth, sandbox);
    }

    /// <summary>
    /// A <c>has_deserialize: false</c> wrapper with derived <c>parent_type</c> children.
    /// Files either write a union tag then the unique child memberCount, or the child header alone.
    /// </summary>
    private bool TryReadPolymorphic(
        Cursor cursor,
        MpType baseType,
        MpType[] derived,
        int depth,
        bool sandbox,
        out JsonNode? node)
    {
        node = null;
        if (cursor.Remaining == 0)
            return false;
        byte header = cursor.Buffer[cursor.Position];
        if (header == 255)
        {
            cursor.U8();
            return true;
        }

        byte? afterTag = cursor.Remaining >= 2 ? cursor.Buffer[cursor.Position + 1] : null;
        MpType[] tagged = afterTag is byte next
            ? derived.Where(d => d.Wire.Length == next).ToArray()
            : [];
        MpType[] untagged = derived.Where(d => d.Wire.Length == header).ToArray();
        MpType? chosen = null;
        int? tag = null;
        if (tagged.Length == 1)
        {
            tag = ReadUnionTag(cursor);
            chosen = tagged[0];
        }
        else if (untagged.Length == 1)
            chosen = untagged[0];
        else
            return false;

        node = ReadObject(cursor, chosen, abilityPayload: false, depth, sandbox);
        if (node is JsonObject obj)
        {
            if (!obj.ContainsKey("$type"))
                obj["$type"] = chosen.TargetType;
            if (tag != null)
                obj["$tag"] = tag.Value;
        }

        return true;
    }

    private JsonNode? ReadTaggedUnion(Cursor cursor, string staticType, string? hint, int depth, bool sandbox)
    {
        int origin = cursor.Position;
        int? tag = ReadUnionTag(cursor);
        if (tag == null)
            return null;

        if (TryMappedConcrete(staticType, tag.Value, hint, out MpType mappedType))
        {
            JsonNode? mapped = UsesAbilityPayload(staticType, mappedType)
                ? ReadConcrete(cursor, mappedType, depth, sandbox)
                : ReadObject(cursor, mappedType, abilityPayload: false, depth, sandbox);
            if (mapped is JsonObject mappedObj)
            {
                if (!mappedObj.ContainsKey("$type"))
                    mappedObj["$type"] = ShortName(mappedType);
                mappedObj["$tag"] = tag.Value;
            }

            return mapped;
        }
        if (staticType == "AbilityActionData" && _schema.FindAbilityTag(tag.Value) is MpType cached)
            return ReadConcrete(cursor, cached, depth, sandbox);

        int afterTag = cursor.Position;
        IReadOnlyList<MpType> candidates = _schema.UnionCandidates(staticType, hint);
        var matches = new List<(MpType Type, int End, JsonObject Value)>();
        foreach (MpType candidate in candidates)
        {
            var fork = cursor.Fork(afterTag);
            try
            {
                JsonNode? node = UsesAbilityPayload(staticType, candidate)
                    ? ReadConcrete(fork, candidate, depth + 1, sandbox: true)
                    : ReadObject(fork, candidate, abilityPayload: false, depth + 1, sandbox: true);
                if (node is not JsonObject obj)
                    continue;
                matches.Add((candidate, fork.Position, obj));
            }
            catch (Exception ex) when (ex is InvalidDataException or MemoryPackTypeException)
            {
                // This concrete type does not fit the bytes.
            }
        }

        var ends = new Dictionary<int, (MpType Type, JsonObject Value)>();
        var ambiguousEnds = new HashSet<int>();
        foreach (var match in matches)
        {
            if (!ends.TryAdd(match.End, (match.Type, match.Value)))
                ambiguousEnds.Add(match.End);
        }

        if (ends.Count == 1 && ambiguousEnds.Count == 0)
        {
            var chosen = ends.Values.First();
            if (!sandbox && staticType == "AbilityActionData")
                _schema.RememberAbilityTag(tag.Value, chosen.Type);
            cursor.Position = ends.Keys.First();
            if (!chosen.Value.ContainsKey("$type"))
                chosen.Value["$type"] = ShortName(chosen.Type);
            chosen.Value["$tag"] = tag.Value;
            return chosen.Value;
        }

        string[] names = matches
            .Select(m => ShortName(m.Type))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        int? header = afterTag < cursor.Length ? cursor.Buffer[afterTag] : null;
        throw new MemoryPackUnionException(
            ends.Count == 0
                ? $"{staticType} union tag {tag.Value} at offset {origin} matched no concrete wrapper (header byte {header})."
                : $"{staticType} union tag {tag.Value} at offset {origin} matches {matches.Count} concrete types.",
            staticType,
            origin,
            tag,
            header,
            names
        );
    }

    private JsonNode? ReadConcrete(Cursor cursor, MpType type, int depth, bool sandbox)
    {
        JsonNode? node = ReadObject(cursor, type, abilityPayload: true, depth, sandbox);
        if (node is JsonObject obj && !obj.ContainsKey("$type"))
            obj["$type"] = ShortName(type);
        return node;
    }

    private JsonNode? ReadObject(Cursor cursor, MpType type, bool abilityPayload, int depth, bool sandbox)
    {
        int origin = cursor.Position;
        int? count = ReadHeader(cursor);
        if (count == null)
            return null;
        if (count < 0)
            throw new InvalidDataException($"Negative member count {count} at offset {origin}.");

        MpMember[] members;
        // Named EffectActionData / KeywordAction leaves carry AbilityPayload. Accept it
        // outside a union sandbox. Inside the sandbox only the union path opts in.
        bool allowAbility = abilityPayload || !sandbox;
        if (allowAbility && type.AbilityPayload != null && count == type.AbilityPayload.Length)
            members = type.AbilityPayload;
        else if (allowAbility && type.AbilityPayloadAlt != null && count == type.AbilityPayloadAlt.Length)
            members = type.AbilityPayloadAlt;
        else if (count == type.Wire.Length)
            members = type.Wire;
        else if (count == type.Own.Length)
            members = type.Own;
        else if (count == 0 && type.Wire.Length > 0 && AllowsEmptyMemberCount(type, sandbox))
        {
            // MemoryPack empty instance / Optional-Param defaults.
            // In union sandbox, only known empty-capable types may match on count 0 —
            // otherwise every wrapper with a non-empty wire falsely accepts the byte.
            return new JsonObject();
        }
        else if (count > 0 && count < type.Wire.Length && !sandbox)
        {
            // MemoryPack versioning: older payloads wrote a prefix of wire_order.
            // Disabled in union sandbox so partial fits do not create false matches.
            members = type.Wire[..count.Value];
        }
        else
        {
            string ability = type.AbilityPayload == null
                ? ""
                : $", ability {type.AbilityPayload.Length}"
                    + (type.AbilityPayloadAlt == null ? "" : $"/{type.AbilityPayloadAlt.Length}");
            throw new InvalidDataException(
                $"memberCount {count} at offset {origin} does not match {ShortName(type)} "
                + $"(wire {type.Wire.Length}, merged {type.Own.Length}{ability})."
            );
        }

        var obj = new JsonObject();
        foreach (MpMember member in members)
        {
            int start = cursor.Position;
            try
            {
                obj[member.Name] = ReadValue(
                    cursor, member.Type, member.Name, depth + 1, sandbox, member.CollectionEncoding);
            }
            catch (Exception ex) when (FieldTrace != null)
            {
                FieldTrace.WriteLine(
                    $"{new string(' ', depth * 2)}FAIL {ShortName(type)}.{member.Name} ({member.Type}) @{start} {ex.Message}"
                );
                throw;
            }
            FieldTrace?.WriteLine(
                $"{new string(' ', depth * 2)}{ShortName(type)}.{member.Name} ({member.Type}) {start}..{cursor.Position} +{cursor.Position - start}"
            );
        }
        return obj;
    }


    private static bool AllowsEmptyMemberCount(MpType type, bool sandbox)
    {
        if (!sandbox)
            return true;
        return type.TargetType.StartsWith("Beyond.Optional`1<", StringComparison.Ordinal)
            || type.TargetType.StartsWith("Beyond.Gameplay.Actions.Param`1<", StringComparison.Ordinal);
    }

    private JsonNode? ReadList(
        Cursor cursor,
        string element,
        string? hint,
        int depth,
        bool sandbox,
        string? collectionEncoding = null)
    {
        int count = cursor.I32();
        if (count == -1)
            return null;
        if (count < 0 || count > 1_000_000)
            throw new InvalidDataException($"List count {count} at offset {cursor.Position - 4}.");
        var array = new JsonArray();
        bool unmanagedElem = _schema.TryUnmanaged(element, out MpUnmanagedLayout? elemLayout);
        string? encoding = collectionEncoding ?? elemLayout?.CollectionEncoding;
        bool memcpyArray = unmanagedElem
            && elemLayout != null
            && string.Equals(encoding, "memcpy", StringComparison.Ordinal);

        for (int i = 0; i < count; i++)
        {
            if (unmanagedElem && elemLayout != null && !memcpyArray
                && TryReadUnmanagedFormatter(cursor, element, depth + 1, sandbox, out JsonNode? formatted))
            {
                array.Add(formatted);
            }
            else if (unmanagedElem && elemLayout != null)
                array.Add(ReadUnmanaged(cursor, elemLayout, depth + 1, sandbox));
            else
                array.Add(ReadValue(cursor, element, hint, depth + 1, sandbox));
        }

        return array;
    }

    /// <summary>
    /// A valuetype listed in unmanaged.json may still have a ForMemoryPack formatter.
    /// Native writes an object header (wire length) then members when the formatter is used,
    /// and a raw memcpy payload otherwise. The header byte is the discriminator.
    /// </summary>
    private bool TryReadUnmanagedFormatter(
        Cursor cursor,
        string type,
        int depth,
        bool sandbox,
        out JsonNode? node)
    {
        node = null;
        if (cursor.Remaining == 0 || !_schema.HasWrapper(type))
            return false;
        MpType resolved;
        try
        {
            resolved = _schema.ResolveNamed(type);
        }
        catch (MemoryPackTypeException)
        {
            return false;
        }

        int formatterCount = resolved.Wire.Length;
        if (formatterCount <= 0 || cursor.Buffer[cursor.Position] != formatterCount)
            return false;
        node = ReadObject(cursor, resolved, abilityPayload: false, depth, sandbox);
        return true;
    }

    /// <summary>
    /// Class dictionary values may be prefixed with a presence bool. Consume 0x01 only when
    /// the next byte matches that type's wire, own, or ability length (or 0 / 255 / 250).
    /// </summary>
    private void MaybeConsumeDictionaryValuePresence(Cursor cursor, string valueType)
    {
        if (cursor.Remaining < 2 || _schema.KindOf(valueType) != MpKind.Object)
            return;
        if (cursor.Buffer[cursor.Position] != 1)
            return;

        MpType resolved;
        try
        {
            resolved = _schema.ResolveNamed(valueType);
        }
        catch (MemoryPackTypeException)
        {
            return;
        }

        byte next = cursor.Buffer[cursor.Position + 1];
        int ability = resolved.AbilityPayload?.Length ?? -1;
        int abilityAlt = resolved.AbilityPayloadAlt?.Length ?? -1;
        // memberCount 1 collides with the first byte of i32 list count 1.
        // SubSpecialPerformEntryData (wire 1) values must not eat that 0x01.
        // next==0 collides with a StringPool id whose low byte is 0
        // (InteractiveTable+Data.templateId 0x00003600).
        static bool PlausibleHeader(int count, byte header) =>
            count > 1 && header == count;
        if (next == 255 || next == 250
            || PlausibleHeader(resolved.Wire.Length, next)
            || PlausibleHeader(resolved.Own.Length, next)
            || PlausibleHeader(ability, next)
            || PlausibleHeader(abilityAlt, next))
            cursor.U8();
    }

    /// <summary>
    /// MemoryPack <c>WriteUnmanaged</c> of a sequential key/value pair uses the
    /// C# ABI size, not packed field widths. <c>Dictionary&lt;ulong, int&gt;</c>
    /// is 16 bytes/entry (8+4+4 pad). Evidence: RunePuzzleData.anchorPointToIndexMap.
    /// Enum keys and ForMemoryPack values are written by formatters, not as a
    /// pair struct — padding those (EnemyHurtShakeIntensity byte + header-2
    /// config) desynchronizes EnemyAnimExtraData.
    /// </summary>
    private int UnmanagedPairPadding(string keyType, string valueType)
    {
        if (_schema.KindOf(keyType) == MpKind.Enum || _schema.HasWrapper(valueType))
            return 0;
        if (!TryBlittableLayout(keyType, out int keySize, out int keyAlign)
            || !TryBlittableLayout(valueType, out int valueSize, out int valueAlign))
            return 0;
        int offset = Align(0, keyAlign) + keySize;
        offset = Align(offset, valueAlign) + valueSize;
        int pair = Align(offset, Math.Max(keyAlign, valueAlign));
        int packed = keySize + valueSize;
        return pair > packed ? pair - packed : 0;
    }

    private bool TryBlittableLayout(string type, out int size, out int align)
    {
        size = 0;
        align = 1;
        if (_schema.TryUnmanaged(type, out MpUnmanagedLayout? layout) && layout.PayloadSize > 0)
        {
            size = layout.PayloadSize;
            align = size >= 8 ? 8 : size >= 4 ? 4 : size >= 2 ? 2 : 1;
            return true;
        }

        switch (_schema.KindOf(type))
        {
            case MpKind.Bool or MpKind.Int8 or MpKind.UInt8:
                size = 1;
                align = 1;
                return true;
            case MpKind.Int16 or MpKind.UInt16:
                size = 2;
                align = 2;
                return true;
            case MpKind.Int32 or MpKind.Enum or MpKind.Float32:
                size = 4;
                align = 4;
                return true;
            case MpKind.Int64 or MpKind.UInt64 or MpKind.Float64:
                size = 8;
                align = 8;
                return true;
            case MpKind.Vector2 or MpKind.Vector2Int:
                size = 8;
                align = 4;
                return true;
            case MpKind.Vector3 or MpKind.Vector3Int:
                size = 12;
                align = 4;
                return true;
            case MpKind.Vector4:
                size = 16;
                align = 4;
                return true;
            default:
                return false;
        }
    }

    private static int Align(int value, int align) =>
        align <= 1 ? value : (value + align - 1) / align * align;

    private JsonNode? ReadDictionary(Cursor cursor, string type, int depth, bool sandbox, bool v2)
    {
        if (v2)
        {
            byte header = cursor.U8();
            if (header == 255)
                return null;
        }

        int count = cursor.I32();
        if (count == -1)
            return null;
        if (count < 0 || count > 1_000_000)
            throw new InvalidDataException($"Dictionary count {count} at offset {cursor.Position - 4}.");
        (string key, string value) = SplitPair(type);
        MpKind keyKind = _schema.KindOf(key);
        bool objectKeys = keyKind is MpKind.String or MpKind.Int8 or MpKind.UInt8
            or MpKind.Int16 or MpKind.UInt16 or MpKind.Int32 or MpKind.Int64 or MpKind.UInt64 or MpKind.Enum;
        int pairPad = UnmanagedPairPadding(key, value);
        var obj = new JsonObject();
        var pairs = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            JsonNode? keyNode = ReadValue(cursor, key, null, depth + 1, sandbox);
            // dictionary-v2 already has a container header. A per-value 0x01
            // prefix was an obsolete guess and collides with memberCount 1.
            if (!v2)
                MaybeConsumeDictionaryValuePresence(cursor, value);
            JsonNode? valueNode = _schema.TryUnmanaged(value, out _)
                && TryReadUnmanagedFormatter(cursor, value, depth + 1, sandbox, out JsonNode? formatted)
                ? formatted
                : ReadValue(cursor, value, null, depth + 1, sandbox);
            if (pairPad > 0)
            {
                if (cursor.Remaining < pairPad)
                    throw new InvalidDataException($"Dictionary pair padding {pairPad} at offset {cursor.Position}.");
                cursor.Position += pairPad;
            }
            if (!objectKeys)
            {
                var pair = new JsonArray();
                pair.Add(keyNode);
                pair.Add(valueNode);
                pairs.Add((JsonNode)pair);
                continue;
            }

            string text = keyNode switch
            {
                JsonValue v when v.TryGetValue(out string? s) => s ?? "",
                JsonValue v when v.TryGetValue(out int n) => n.ToString(CultureInfo.InvariantCulture),
                JsonValue v when v.TryGetValue(out long n) => n.ToString(CultureInfo.InvariantCulture),
                JsonValue v when v.TryGetValue(out ulong n) => n.ToString(CultureInfo.InvariantCulture),
                _ => keyNode?.ToJsonString() ?? "",
            };
            obj[text] = valueNode;
        }

        return objectKeys ? obj : pairs;
    }

    private JsonNode? ReadNullable(Cursor cursor, string inner, int depth, bool sandbox)
    {
        int origin = cursor.Position;
        int? count = ReadHeader(cursor);
        if (count == null)
            return null;
        // MemoryPack versioning: memberCount 0 is the empty/default value (null).
        if (count == 0)
            return null;
        if (count != 1)
            throw new InvalidDataException($"Nullable<{inner}> memberCount {count} at offset {origin}.");
        return ReadValue(cursor, inner, null, depth + 1, sandbox);
    }

    private JsonNode? ReadString(Cursor cursor)
    {
        int id = cursor.I32();
        if (id < -2)
            throw new InvalidDataException($"String pool id {id} at offset {cursor.Position - 4}.");
        if (id == -2)
            return null;
        if (id == -1)
            return JsonValue.Create("");
        if (_pool != null && _pool.TryResolve(id, out string? text))
            return JsonValue.Create(text);
        return JsonValue.Create("#" + id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Standard MemoryPack string: i32 length (-1 = null) then UTF-8 bytes.</summary>
    private static JsonNode? ReadMemoryPackString(Cursor cursor)
    {
        int origin = cursor.Position;
        int length = cursor.I32();
        if (length == -1)
            return null;
        if (length < 0)
            throw new InvalidDataException($"MemoryPack string length {length} at offset {origin}.");
        byte[] bytes = cursor.Bytes(length);
        return JsonValue.Create(Encoding.UTF8.GetString(bytes));
    }

    /// <summary>
    /// Nested <c>ReadValue&lt;T&gt;</c> using the target's MemoryPack <c>TFormatter</c>
    /// (UTF-8 strings), not the Beyond wrapper wire.
    /// </summary>
    private JsonNode? ReadMemoryPackValue(Cursor cursor, string type, int depth, bool sandbox)
    {
        MpType resolved = _schema.ResolveNamed(type);
        MpMember[] members = resolved.MemorypackWire
            ?? throw new MemoryPackTypeException(
                $"Missing memorypack_wire for {type} (nested ReadValue / TFormatter).");
        int origin = cursor.Position;
        int? count = ReadHeader(cursor);
        if (count == null)
            return null;
        if (count == 0)
            return null;
        if (count != members.Length && (count <= 0 || count > members.Length || sandbox))
        {
            throw new InvalidDataException(
                $"memberCount {count} at offset {origin} does not match memorypack_wire {members.Length} of {ShortName(resolved)}.");
        }

        MpMember[] used = count == members.Length ? members : members[..count.Value];
        var obj = new JsonObject();
        foreach (MpMember member in used)
        {
            obj[member.Name] = ReadValue(
                cursor, member.Type, member.Name, depth + 1, sandbox, member.CollectionEncoding);
        }

        return obj;
    }

    private static int? ReadHeader(Cursor cursor)
    {
        byte b = cursor.U8();
        if (b == 255)
            return null;
        if (b == 250)
            return cursor.I32();
        return b;
    }

    private static int? ReadUnionTag(Cursor cursor)
    {
        byte b = cursor.U8();
        if (b == 255)
            return null;
        if (b == 250)
            return cursor.U16();
        return b;
    }

    private static JsonArray Vec(params float[] values)
    {
        var array = new JsonArray();
        foreach (float value in values)
            array.Add((JsonNode?)JsonValue.Create(value));
        return array;
    }

    private static JsonArray Vec(params int[] values)
    {
        var array = new JsonArray();
        foreach (int value in values)
            array.Add((JsonNode?)JsonValue.Create(value));
        return array;
    }

    private static string ElementType(string type)
    {
        if (type.EndsWith("[]", StringComparison.Ordinal))
            return type[..^2];
        return InnerType(type);
    }

    private static string InnerType(string type)
    {
        int start = type.IndexOf('<');
        if (start < 0 || !type.EndsWith('>'))
            throw new MemoryPackTypeException($"Cannot split generic \"{type}\".");
        return type[(start + 1)..^1];
    }

    private static (string Key, string Value) SplitPair(string type)
    {
        string inner = InnerType(type);
        int depth = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char ch = inner[i];
            if (ch == '<')
                depth++;
            else if (ch == '>')
                depth--;
            else if (ch == ',' && depth == 0)
                return (inner[..i].Trim(), inner[(i + 1)..].Trim());
        }

        throw new MemoryPackTypeException($"Cannot split dictionary \"{type}\".");
    }

    private static string ShortName(MpType type)
    {
        if (type.TargetType != "Data")
            return type.TargetType;
        string leaf = type.WrapperType;
        int dot = leaf.LastIndexOf('.');
        if (dot >= 0)
            leaf = leaf[(dot + 1)..];
        const string suffix = "ForMemoryPack";
        return leaf.EndsWith(suffix, StringComparison.Ordinal) ? leaf[..^suffix.Length] : leaf;
    }

    private sealed class Cursor
    {
        private readonly byte[] _data;
        public Cursor(byte[] data) => _data = data;
        private Cursor(byte[] data, int position)
        {
            _data = data;
            Position = position;
        }

        public int Position { get; set; }
        public int Length => _data.Length;
        public int Remaining => _data.Length - Position;
        public byte[] Buffer => _data;

        public Cursor Fork(int position) => new(_data, position);

        public byte U8() => Take(1)[0];

        public byte[] Bytes(int count) => Take(count).ToArray();

        public int I32() => BitConverter.ToInt32(Take(4));

        public ushort U16() => BitConverter.ToUInt16(Take(2));

        public long I64() => BitConverter.ToInt64(Take(8));

        public ulong U64() => BitConverter.ToUInt64(Take(8));

        public float F32() => BitConverter.ToSingle(Take(4));

        public double F64() => BitConverter.ToDouble(Take(8));

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || Position > _data.Length - count)
            {
                throw new InvalidDataException(
                    $"Need {count} bytes at offset {Position}, buffer length is {_data.Length}."
                );
            }

            var slice = _data.AsSpan(Position, count);
            Position += count;
            return slice;
        }
    }
}
