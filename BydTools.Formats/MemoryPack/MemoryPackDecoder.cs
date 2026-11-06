using System.Globalization;
using System.Text.Json.Nodes;

namespace BydTools.Formats.MemoryPack;

/// <summary>
/// Decodes a Beyond.MemoryPack payload using <c>schemas.json</c> wire_order.
/// Object header: byte count, 255 null, 250 then int32 for wide counts.
/// Strings are StringPool int32 ids. AbilityActionData is a MemoryPack union
/// (tag, then the concrete object). Concrete ability payloads are the four
/// AbilityActionData members followed by that wrapper's wire_order.
/// </summary>
public sealed class MemoryPackDecoder
{
    private readonly MemoryPackSchema _schema;
    private readonly BeyondStringPool? _pool;
    private readonly Dictionary<int, string> _abilityMap;

    public MemoryPackDecoder(MemoryPackSchema schema, BeyondStringPool? pool = null, IReadOnlyDictionary<int, string>? abilityTags = null)
    {
        _schema = schema;
        _pool = pool;
        _abilityMap = abilityTags == null
            ? []
            : new Dictionary<int, string>(abilityTags);
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
            case MpKind.Enum:
                return JsonValue.Create(cursor.I32());
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
                throw new MemoryPackTypeException(
                    $"UnityEngine.AnimationCurve has no Beyond wrapper (offset {cursor.Position}). U8/Webview and AnimationCurve blobs are out of scope."
                );
            default:
                MpType resolved = _schema.Require(type, hint);
                return ReadObject(cursor, resolved, abilityPayload: false, depth, sandbox);
        }
    }

    private static bool IsTaggedUnion(string type) =>
        type is "AbilityActionData"
            or "Beyond.Gameplay.Actions.ActionBase"
            or "Beyond.Gameplay.Actions.PureGetter"
            or "Beyond.Gameplay.Actions.ActionHeader"
            or "Beyond.Gameplay.Core.ModelViewStateController.AbstractBehaviorData"
            or "Beyond.Gameplay.Core.DamageProcessorBase"
            or "Beyond.Gameplay.ConditionRuntimeBase"
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

        if (staticType == "AbilityActionData")
        {
            if (_abilityMap.TryGetValue(tag.Value, out string? mapped))
                return ReadConcrete(cursor, _schema.ResolveNamed(mapped), depth, sandbox);
            if (_schema.FindAbilityTag(tag.Value) is MpType cached)
                return ReadConcrete(cursor, cached, depth, sandbox);
        }

        int afterTag = cursor.Position;
        IReadOnlyList<MpType> candidates = _schema.UnionCandidates(staticType, hint);
        var matches = new List<(MpType Type, int End, JsonObject Value)>();
        foreach (MpType candidate in candidates)
        {
            var fork = cursor.Fork(afterTag);
            try
            {
                JsonNode? node = staticType == "AbilityActionData"
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
        else if (count == type.Wire.Length)
            members = type.Wire;
        else if (count == type.Own.Length)
            members = type.Own;
        else if (count == 0
            && type.Wire.Length > 0
            && (type.TargetType.StartsWith("Beyond.Optional`1<", StringComparison.Ordinal)
                || type.TargetType.StartsWith("Beyond.Gameplay.Actions.Param`1<", StringComparison.Ordinal)))
        {
            // Optional / graph Param default instance is written as memberCount 0.
            return new JsonObject();
        }
        else
        {
            throw new InvalidDataException(
                $"memberCount {count} at offset {origin} does not match {ShortName(type)} "
                + $"(wire {type.Wire.Length}, merged {type.Own.Length}"
                + (type.AbilityPayload == null ? ")." : $", ability {type.AbilityPayload.Length}).")
            );
        }

        var obj = new JsonObject();
        foreach (MpMember member in members)
            obj[member.Name] = ReadValue(cursor, member.Type, member.Name, depth + 1, sandbox);
        return obj;
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
