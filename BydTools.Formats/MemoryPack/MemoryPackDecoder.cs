using System.Globalization;
using System.Text.Json.Nodes;

namespace BydTools.Formats.MemoryPack;

/// <summary>
/// Decodes a Beyond.MemoryPack payload using <c>schemas.json</c> wire_order.
/// Object header: byte count, 255 null, 250 then int32 for wide counts.
/// memberCount 0 with a non-empty wire yields defaults; count below wire length
/// reads a prefix (MemoryPack versioning). Strings are StringPool int32 ids.
/// AbilityActionData / ActionBase are MemoryPack unions (tag, then concrete).
/// Concrete ability payloads are AbilityActionData members plus wrapper wire_order.
/// Unmanaged Beyond structs (GameplayTag, AnimationClipAsyncInfo, FMontageTransitionInfo)
/// are read as raw blittable layouts (no object header), matching parent WriteUnmanaged.
/// UnityEngine.AnimationCurve uses the native MemoryPack layout (count 3 + Keyframe[28]).
/// </summary>
public sealed class MemoryPackDecoder
{
    private readonly MemoryPackSchema _schema;
    private readonly BeyondStringPool? _pool;
    private readonly Dictionary<string, Dictionary<int, string>> _unionMaps;

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

    private bool TryMappedConcrete(string staticType, int tag, out MpType concrete)
    {
        concrete = null!;
        if (LookupUnionMap(staticType) is not { } map || !map.TryGetValue(tag, out string? mapped))
            return false;
        concrete = _schema.ResolveNamed(mapped);
        return true;
    }

    private Dictionary<int, string>? LookupUnionMap(string staticType)
    {
        if (_unionMaps.TryGetValue(staticType, out var exact))
            return exact;
        int dot = staticType.LastIndexOf('.');
        if (dot >= 0 && _unionMaps.TryGetValue(staticType[(dot + 1)..], out var leaf))
            return leaf;
        return null;
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

    private JsonNode? ReadValue(Cursor cursor, string type, string? hint, int depth, bool sandbox)
    {
        if (depth > 64)
            throw new InvalidDataException($"MemoryPack nesting is deeper than 64 at offset {cursor.Position}.");

        if (type == "GUIDProxy" || type.EndsWith(".GUIDProxy", StringComparison.Ordinal))
            return ReadGuidProxy(cursor);
        if (type == "Beyond.Gameplay.Core.GameplayTag" || type == "GameplayTag")
            return ReadGameplayTag(cursor);
        if (type == "Beyond.Gameplay.Core.DispelConfig" || type == "DispelConfig")
            return ReadDispelConfig(cursor);
        if (type == "OrderPriorityConfig" || type.EndsWith(".OrderPriorityConfig", StringComparison.Ordinal))
            return ReadOrderPriorityConfig(cursor);
        if (type == "Beyond.Montage.AnimationClipAsyncInfo")
            return ReadAnimationClipAsyncInfo(cursor);
        if (type == "Beyond.Montage.FMontageTransitionInfo")
            return ReadFMontageTransitionInfo(cursor);
        if (type.StartsWith("Beyond.Optional`1<", StringComparison.Ordinal))
            return ReadBeyondOptional(cursor, type, depth, sandbox);
        if (IsTaggedUnion(type))
            return ReadTaggedUnion(cursor, type, hint, depth, sandbox);

        MpKind kind = _schema.KindOf(type);
        switch (kind)
        {
            case MpKind.Bool:
                byte flag = cursor.U8();
                if (flag > 1)
                    throw new InvalidDataException($"Boolean {flag} at offset {cursor.Position - 1}.");
                return JsonValue.Create(flag != 0);
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
                return ReadString(cursor);
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
                return ReadList(cursor, ElementType(type), hint, depth, sandbox);
            case MpKind.Dictionary:
                return ReadDictionary(cursor, type, depth, sandbox);
            case MpKind.Nullable:
                return ReadNullable(cursor, InnerType(type), depth, sandbox);
            case MpKind.AnimationCurve:
                return ReadUnityAnimationCurve(cursor);
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
        staticType == "AbilityActionData"
        || (staticType == "Data" && concrete.AbilityPayload != null);

    private static bool IsTaggedUnion(string type) =>
        type is "AbilityActionData"
            or "Beyond.Gameplay.Actions.ActionBase"
            or "Beyond.Gameplay.Actions.PureGetter"
            or "Beyond.Gameplay.Actions.ActionHeader"
            or "Beyond.Gameplay.Core.ModelViewStateController.AbstractBehaviorData"
            or "Beyond.Gameplay.Core.DamageProcessorBase"
            or "Beyond.Gameplay.ConditionRuntimeBase"
            or "Beyond.Gameplay.Core.CalculationBase"
            or "CalculationBase"
            or "Data";

    /// <summary>
    /// GUIDProxy is a 16-byte blittable payload (4× uint32) with no object header.
    /// A leading 255 is null when the field is a reference.
    /// </summary>
    private static JsonNode? ReadGuidProxy(Cursor cursor)
    {
        if (cursor.Remaining > 0 && cursor.Buffer[cursor.Position] == 255)
        {
            cursor.U8();
            return null;
        }

        return new JsonObject
        {
            ["val0"] = JsonValue.Create(unchecked((uint)cursor.I32())),
            ["val1"] = JsonValue.Create(unchecked((uint)cursor.I32())),
            ["val2"] = JsonValue.Create(unchecked((uint)cursor.I32())),
            ["val3"] = JsonValue.Create(unchecked((uint)cursor.I32())),
        };
    }

    /// <summary>
    /// GameplayTag is an unmanaged struct { int tagId }. Parents WriteUnmanaged a raw int32.
    /// </summary>
    private static JsonNode ReadGameplayTag(Cursor cursor) =>
        new JsonObject { ["tagId"] = JsonValue.Create(cursor.I32()) };

    /// <summary>
    /// DispelConfig is an unmanaged struct { bool canBeDispelled; pad3; DispelLevel } (8 bytes).
    /// BuffData reads it with a raw 8-byte move, not an object header.
    /// </summary>
    private static JsonNode ReadDispelConfig(Cursor cursor)
    {
        if (cursor.Remaining < 8)
        {
            throw new InvalidDataException(
                $"DispelConfig needs 8 bytes at offset {cursor.Position}, have {cursor.Remaining}."
            );
        }

        bool canBeDispelled = cursor.U8() != 0;
        cursor.U8();
        cursor.U8();
        cursor.U8();
        return new JsonObject
        {
            ["canBeDispelled"] = JsonValue.Create(canBeDispelled),
            ["dispelledLevel"] = JsonValue.Create(cursor.I32()),
        };
    }

    /// <summary>
    /// OrderPriorityConfig is a 12-byte unmanaged struct inside BuffIconConfig:
    /// bool useDirectoryValue, 3 pad, int priorityValue, int priorityEnum.
    /// A leading 0 is the bool, not an object memberCount.
    /// </summary>
    private static JsonNode ReadOrderPriorityConfig(Cursor cursor)
    {
        if (cursor.Remaining < 12)
        {
            throw new InvalidDataException(
                $"OrderPriorityConfig needs 12 bytes at offset {cursor.Position}, have {cursor.Remaining}."
            );
        }

        bool useDirectoryValue = cursor.U8() != 0;
        cursor.U8();
        cursor.U8();
        cursor.U8();
        return new JsonObject
        {
            ["useDirectoryValue"] = JsonValue.Create(useDirectoryValue),
            ["priorityValue"] = JsonValue.Create(cursor.I32()),
            ["priorityEnum"] = JsonValue.Create(cursor.I32()),
        };
    }

    /// <summary>
    /// Most MemoryPack enums are int32. These three match the managed layout and the
    /// BuffData samples: LifeType and IdentifierType are bytes, StackingType is int16.
    /// </summary>
    private static int ReadEnum(Cursor cursor, string type)
    {
        int dot = type.LastIndexOf('.');
        string leaf = dot >= 0 ? type[(dot + 1)..] : type;
        if (leaf is "LifeType" or "IdentifierType")
            return cursor.U8();
        if (leaf == "StackingType")
            return cursor.U16();
        return cursor.I32();
    }

    /// <summary>
    /// AnimationClipAsyncInfo is an unmanaged struct (36 bytes, declaration order).
    /// </summary>
    private static JsonNode ReadAnimationClipAsyncInfo(Cursor cursor)
    {
        if (cursor.Remaining < 36)
        {
            throw new InvalidDataException(
                $"AnimationClipAsyncInfo needs 36 bytes at offset {cursor.Position}, have {cursor.Remaining}."
            );
        }

        var obj = new JsonObject
        {
            ["montagePathHash"] = JsonValue.Create(cursor.I64()),
            ["length"] = JsonValue.Create(cursor.F32()),
            ["framerate"] = JsonValue.Create(cursor.F32()),
            ["averageSpeed"] = Vec(cursor.F32(), cursor.F32(), cursor.F32()),
            ["averageAngularSpeed"] = JsonValue.Create(cursor.F32()),
            ["isHumanoid"] = JsonValue.Create(cursor.U8() != 0),
            ["isLooping"] = JsonValue.Create(cursor.U8() != 0),
        };
        _ = cursor.U16(); // padding to unmanaged size 36
        return obj;
    }

    /// <summary>
    /// FMontageTransitionInfo is an unmanaged struct (32 bytes, declaration order with bool pad).
    /// </summary>
    private static JsonNode ReadFMontageTransitionInfo(Cursor cursor)
    {
        if (cursor.Remaining < 32)
        {
            throw new InvalidDataException(
                $"FMontageTransitionInfo needs 32 bytes at offset {cursor.Position}, have {cursor.Remaining}."
            );
        }

        bool fixedTime = cursor.U8() != 0;
        cursor.U8();
        cursor.U8();
        cursor.U8();
        return new JsonObject
        {
            ["bIsFixedTransitionTime"] = JsonValue.Create(fixedTime),
            ["transistionOffset"] = JsonValue.Create(cursor.F32()),
            ["transitionDuration"] = JsonValue.Create(cursor.F32()),
            ["transitionTime"] = JsonValue.Create(cursor.F32()),
            ["fixedTime"] = JsonValue.Create(cursor.F32()),
            ["fixedTransitionDuration"] = JsonValue.Create(cursor.F32()),
            ["normalizedTransitionTime"] = JsonValue.Create(cursor.F32()),
            ["exitTime"] = JsonValue.Create(cursor.F32()),
        };
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

    private JsonNode? ReadTaggedUnion(Cursor cursor, string staticType, string? hint, int depth, bool sandbox)
    {
        int origin = cursor.Position;
        int? tag = ReadUnionTag(cursor);
        if (tag == null)
            return null;

        if (TryMappedConcrete(staticType, tag.Value, out MpType mappedType))
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
        if (abilityPayload && type.AbilityPayload != null && count == type.AbilityPayload.Length)
            members = type.AbilityPayload;
        else if (abilityPayload && type.AbilityPayloadAlt != null && count == type.AbilityPayloadAlt.Length)
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
            obj[member.Name] = ReadValue(cursor, member.Type, member.Name, depth + 1, sandbox);
        return obj;
    }


    private static bool AllowsEmptyMemberCount(MpType type, bool sandbox)
    {
        if (!sandbox)
            return true;
        return type.TargetType.StartsWith("Beyond.Optional`1<", StringComparison.Ordinal)
            || type.TargetType.StartsWith("Beyond.Gameplay.Actions.Param`1<", StringComparison.Ordinal);
    }

    private JsonNode? ReadList(Cursor cursor, string element, string? hint, int depth, bool sandbox)
    {
        int count = cursor.I32();
        if (count == -1)
            return null;
        if (count < 0 || count > 1_000_000)
            throw new InvalidDataException($"List count {count} at offset {cursor.Position - 4}.");
        var array = new JsonArray();
        for (int i = 0; i < count; i++)
            array.Add(ReadValue(cursor, element, hint, depth + 1, sandbox));
        return array;
    }

    private JsonNode? ReadDictionary(Cursor cursor, string type, int depth, bool sandbox)
    {
        int count = cursor.I32();
        if (count == -1)
            return null;
        if (count < 0 || count > 1_000_000)
            throw new InvalidDataException($"Dictionary count {count} at offset {cursor.Position - 4}.");
        (string key, string value) = SplitPair(type);
        MpKind keyKind = _schema.KindOf(key);
        bool objectKeys = keyKind is MpKind.String or MpKind.Int32 or MpKind.Int64 or MpKind.UInt64 or MpKind.Enum;
        var obj = new JsonObject();
        var pairs = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            JsonNode? keyNode = ReadValue(cursor, key, null, depth + 1, sandbox);
            JsonNode? valueNode = ReadValue(cursor, value, null, depth + 1, sandbox);
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
