using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BydTools.Formats.MemoryPack;

/// <summary>
/// Decodes a Beyond.MemoryPack payload using <c>schemas.json</c> wire_order.
/// Object header: byte count, 255 null, 250 then int32 for wide counts.
/// memberCount 0 with a non-empty wire yields defaults; count below wire length
/// reads a prefix (MemoryPack versioning). Strings are StringPool int32 ids.
/// AbilityActionData / ActionBase are MemoryPack unions (tag, then concrete).
/// Valuetype payloads listed in unmanaged.json are memcpy layouts (no object header).
/// UnityEngine.AnimationCurve uses the native MemoryPack layout (count 3 + Keyframe[28]).
/// </summary>
public sealed class MemoryPackDecoder
{
    private readonly MemoryPackSchema _schema;
    private readonly BeyondStringPool? _pool;
    private readonly MemoryPackUnionResolver _unions;
    private readonly ConcurrentDictionary<string, TypePlan> _plans = new(StringComparer.Ordinal);

    /// <summary>Optional field-consumption log for schema analysis. Null in production.</summary>
    public TextWriter? FieldTrace { get; set; }

    public MemoryPackDecoder(
        MemoryPackSchema schema,
        BeyondStringPool? pool = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? unionMaps = null)
    {
        _schema = schema;
        _pool = pool;
        _unions = new MemoryPackUnionResolver(schema, unionMaps);
    }

    private TypePlan GetPlan(string type) => _plans.GetOrAdd(type, BuildPlan);

    private TypePlan BuildPlan(string type)
    {
        bool isOptional = type.StartsWith("Beyond.Optional`1<", StringComparison.Ordinal)
            || type.StartsWith("Beyond.Optional<", StringComparison.Ordinal);

        _schema.TryUnmanaged(type, out MpUnmanagedLayout? unmanaged);
        MpType? formatterType = null;
        if (unmanaged != null && _schema.HasWrapper(type))
        {
            try
            {
                formatterType = _schema.ResolveNamed(type);
            }
            catch (MemoryPackTypeException)
            {
            }
        }

        if (isOptional)
        {
            MpType? optionalResolved = null;
            if (_schema.TryResolve(type, hint: null, out MpType? opt, out _))
                optionalResolved = opt;
            return new TypePlan
            {
                IsOptional = true,
                Unmanaged = unmanaged,
                UnmanagedFormatterType = formatterType,
                ResolvedObject = optionalResolved,
            };
        }

        if (unmanaged != null)
        {
            return new TypePlan
            {
                Unmanaged = unmanaged,
                UnmanagedFormatterType = formatterType,
            };
        }

        Dictionary<int, string>? unionMap = _unions.LookupUnionMap(type);
        bool isTaggedUnion = unionMap != null || MemoryPackUnionResolver.IsHardcodedTaggedUnion(type);
        if (isTaggedUnion)
        {
            return new TypePlan
            {
                IsTaggedUnion = true,
                DirectUnionMap = unionMap,
            };
        }

        if (_schema.TryPolymorphicBase(type, out MpType? polyBase, out MpType[] polyDerived)
            && polyBase != null)
        {
            return new TypePlan
            {
                PolyBase = polyBase,
                PolyDerived = polyDerived,
            };
        }

        MpKind? kind = null;
        string? kindError = null;
        try
        {
            kind = _schema.KindOf(type);
        }
        catch (MemoryPackTypeException ex)
        {
            kindError = ex.Message;
        }

        if (kind == null)
        {
            return new TypePlan
            {
                KindError = kindError,
            };
        }

        switch (kind.Value)
        {
            case MpKind.Enum:
                _schema.TryEnumWidth(type, out byte width);
                return new TypePlan
                {
                    Kind = MpKind.Enum,
                    EnumWidth = width,
                    EnumLeafWidth = InferEnumLeafWidth(type),
                };

            case MpKind.List:
            {
                string elem = ElementType(type);
                _schema.TryUnmanaged(elem, out MpUnmanagedLayout? elemLayout);
                MpType? elemFormatter = null;
                if (elemLayout != null && _schema.HasWrapper(elem))
                {
                    try
                    {
                        elemFormatter = _schema.ResolveNamed(elem);
                    }
                    catch (MemoryPackTypeException)
                    {
                    }
                }

                return new TypePlan
                {
                    Kind = MpKind.List,
                    ElementType = elem,
                    ElementUnmanaged = elemLayout,
                    ElementFormatterType = elemFormatter,
                };
            }

            case MpKind.Dictionary:
            case MpKind.DictionaryV2:
            {
                (string keyType, string valueType) = SplitPair(type);
                MpKind keyKind = _schema.KindOf(keyType);
                bool objectKeys = keyKind is MpKind.String or MpKind.Int8 or MpKind.UInt8
                    or MpKind.Int16 or MpKind.UInt16 or MpKind.Int32 or MpKind.UInt32
                    or MpKind.Int64 or MpKind.UInt64 or MpKind.Enum;
                bool padPairs = TryUnmanagedPairLayout(keyType, valueType, out int midPad, out int endPad);

                MpKind? valKind = null;
                MpType? valObj = null;
                try
                {
                    valKind = _schema.KindOf(valueType);
                    if (valKind == MpKind.Object)
                        valObj = _schema.ResolveNamed(valueType);
                }
                catch (MemoryPackTypeException)
                {
                }

                _schema.TryUnmanaged(valueType, out MpUnmanagedLayout? valUnmanaged);
                MpType? valFormatter = null;
                if (valUnmanaged != null && _schema.HasWrapper(valueType))
                {
                    try
                    {
                        valFormatter = _schema.ResolveNamed(valueType);
                    }
                    catch (MemoryPackTypeException)
                    {
                    }
                }

                return new TypePlan
                {
                    Kind = kind.Value,
                    DictKeyType = keyType,
                    DictValueType = valueType,
                    DictObjectKeys = objectKeys,
                    DictPadPairs = padPairs,
                    DictMidPad = midPad,
                    DictEndPad = endPad,
                    DictValueIsObject = valKind == MpKind.Object,
                    DictValueObject = valObj,
                    DictValueUnmanaged = valUnmanaged,
                    DictValueFormatterType = valFormatter,
                };
            }

            case MpKind.Nullable:
                return new TypePlan
                {
                    Kind = MpKind.Nullable,
                    ElementType = InnerType(type),
                };

            case MpKind.Object:
            {
                MpType? resolved = null;
                string? resolveError = null;
                try
                {
                    resolved = _schema.ResolveNamed(type);
                }
                catch (MemoryPackTypeException ex)
                {
                    resolveError = ex.Message;
                }

                return new TypePlan
                {
                    Kind = MpKind.Object,
                    ResolvedObject = resolved,
                    KindError = resolveError,
                };
            }

            default:
                return new TypePlan { Kind = kind.Value };
        }
    }

    private bool TryMappedConcrete(string staticType, TypePlan plan, int tag, string? hint, out MpType concrete) =>
        _unions.TryMappedConcrete(staticType, plan.DirectUnionMap, tag, hint, out concrete);

    public JsonNode? Decode(ReadOnlySpan<byte> data, string rootType)
    {
        var cursor = new Cursor(data);
        JsonNode? node = ReadValue(ref cursor, rootType, hint: null, depth: 0, sandbox: false);
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

    public void WriteJson(ReadOnlySpan<byte> data, string rootType, Stream output)
    {
        JsonNode? node = Decode(data, rootType);
        if (node == null)
        {
            output.Write("null\n"u8);
            return;
        }

        using var writer = new Utf8JsonWriter(output, JsonDataMemoryPack.WriterOptions);
        node.WriteTo(writer, JsonDataMemoryPack.JsonOptions);
        writer.Flush();
        output.Write("\n"u8);
    }

    private JsonNode? ReadValue(
        ref Cursor cursor,
        string type,
        string? hint,
        int depth,
        bool sandbox,
        string? collectionEncoding = null)
    {
        if (depth > 64)
            throw new InvalidDataException($"MemoryPack nesting is deeper than 64 at offset {cursor.Position}.");

        TypePlan plan = GetPlan(type);
        if (plan.IsOptional)
        {
            if (collectionEncoding == "memcpy" && plan.Unmanaged != null)
            {
                return UnwrapUnmanagedNullable(
                    type, ReadUnmanaged(ref cursor, plan.Unmanaged, depth, sandbox));
            }

            return ReadBeyondOptional(ref cursor, type, plan, depth, sandbox);
        }

        if (plan.Unmanaged != null)
        {
            bool wantFormatter = collectionEncoding == "formatter";
            if (wantFormatter
                && TryReadUnmanagedFormatter(ref cursor, plan.UnmanagedFormatterType, depth, sandbox, out JsonNode? formatted)
                && formatted != null)
            {
                return UnwrapUnmanagedNullable(type, formatted);
            }

            JsonNode blob = ReadUnmanaged(ref cursor, plan.Unmanaged, depth, sandbox);
            return UnwrapUnmanagedNullable(type, blob);
        }

        if (collectionEncoding == "memorypack")
            return ReadMemoryPackValue(ref cursor, type, depth, sandbox);
        if (plan.IsTaggedUnion)
            return ReadTaggedUnion(ref cursor, type, plan, hint, depth, sandbox);
        if (plan.PolyBase != null
            && TryReadPolymorphic(ref cursor, plan.PolyBase, plan.PolyDerived!, depth, sandbox, out JsonNode? poly))
            return poly;

        if (plan.Kind is not MpKind kind)
            throw new MemoryPackTypeException(plan.KindError ?? $"No MemoryPack rule for \"{type}\".");

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
            case MpKind.UInt32:
                return JsonValue.Create(cursor.U32());
            case MpKind.Enum:
                return JsonValue.Create(ReadEnum(ref cursor, plan));
            case MpKind.Int64:
                return JsonValue.Create(cursor.I64());
            case MpKind.UInt64:
                return JsonValue.Create(cursor.U64());
            case MpKind.Float32:
                return JsonValue.Create(cursor.F32());
            case MpKind.Float64:
                return JsonValue.Create(cursor.F64());
            case MpKind.String:
                return collectionEncoding == "utf8" ? ReadMemoryPackString(ref cursor) : ReadString(ref cursor);
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
                return ReadList(ref cursor, plan, hint, depth, sandbox, collectionEncoding);
            case MpKind.Dictionary:
                return ReadDictionary(ref cursor, plan, depth, sandbox, v2: false);
            case MpKind.DictionaryV2:
                return ReadDictionary(ref cursor, plan, depth, sandbox, v2: true);
            case MpKind.Nullable:
                return ReadNullable(ref cursor, plan.ElementType!, depth, sandbox);
            case MpKind.AnimationCurve:
                return ReadUnityAnimationCurve(ref cursor);
            default:
            {
                MpType resolved = plan.ResolvedObject
                    ?? throw new MemoryPackTypeException(plan.KindError ?? $"No MemoryPack schema for {type}.");
                return ReadObject(ref cursor, resolved, depth, sandbox);
            }
        }
    }

    /// <summary>
    /// Valuetype memcpy payload from schema <c>unmanaged.json</c>. No object header.
    /// Field offsets include padding; the cursor always consumes <see cref="MpUnmanagedLayout.PayloadSize"/>.
    /// </summary>
    private JsonNode ReadUnmanaged(ref Cursor cursor, MpUnmanagedLayout layout, int depth, bool sandbox)
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
            obj[field.Name] = ReadUnmanagedField(ref cursor, field, depth, sandbox);
        }

        cursor.Position = origin + layout.PayloadSize;
        return obj;
    }

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

    private JsonNode? ReadUnmanagedField(ref Cursor cursor, MpUnmanagedField field, int depth, bool sandbox)
    {
        TypePlan fieldPlan = GetPlan(field.Type);
        if (fieldPlan.Unmanaged != null)
            return ReadUnmanaged(ref cursor, fieldPlan.Unmanaged, depth + 1, sandbox);

        int size = field.Size ?? 0;
        if (fieldPlan.Kind is MpKind kind)
        {
            switch (kind)
            {
                case MpKind.Bool:
                    return JsonValue.Create(cursor.U8() != 0);
                case MpKind.Float32:
                    return JsonValue.Create(cursor.F32());
                case MpKind.Float64:
                    return JsonValue.Create(cursor.F64());
                case MpKind.Int64:
                    return JsonValue.Create(cursor.I64());
                case MpKind.UInt64:
                    return JsonValue.Create(cursor.U64());
                case MpKind.UInt32:
                    return JsonValue.Create(cursor.U32());
                case MpKind.Int32:
                    return JsonValue.Create(cursor.I32());
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
                case MpKind.Enum:
                    return JsonValue.Create(ReadEnumWidth(ref cursor, fieldPlan, size));
            }
        }

        return field.Type switch
        {
            "bool" or "System.Boolean" => JsonValue.Create(cursor.U8() != 0),
            "float" or "System.Single" => JsonValue.Create(cursor.F32()),
            "double" or "System.Double" => JsonValue.Create(cursor.F64()),
            "long" or "System.Int64" => JsonValue.Create(cursor.I64()),
            "ulong" or "System.UInt64" => JsonValue.Create(cursor.U64()),
            "uint" or "System.UInt32" => JsonValue.Create(cursor.U32()),
            "int" or "System.Int32" => JsonValue.Create(cursor.I32()),
            _ => size switch
            {
                1 => JsonValue.Create((int)cursor.U8()),
                2 => JsonValue.Create((short)cursor.U16()),
                8 => JsonValue.Create(cursor.I64()),
                _ => JsonValue.Create(cursor.I32()),
            },
        };
    }

    private static int ReadEnumWidth(ref Cursor cursor, TypePlan plan, int size)
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

        return ReadEnum(ref cursor, plan);
    }

    private static int ReadEnum(ref Cursor cursor, TypePlan plan)
    {
        byte width = plan.EnumWidth != 0 ? plan.EnumWidth : plan.EnumLeafWidth;
        return width switch
        {
            1 => cursor.U8(),
            2 => (short)cursor.U16(),
            8 => (int)cursor.I64(),
            _ => cursor.I32(),
        };
    }

    private static byte InferEnumLeafWidth(string type)
    {
        int sep = type.LastIndexOfAny(['.', '+']);
        string leaf = sep >= 0 ? type[(sep + 1)..] : type;
        if (leaf is "LifeType" or "IdentifierType")
            return 1;
        if (leaf is "StackingType" or "AdditiveEffectType")
            return 2;
        return 4;
    }

    private static JsonNode? ReadUnityAnimationCurve(ref Cursor cursor)
    {
        int origin = cursor.Position;
        int? count = ReadHeader(ref cursor);
        if (count == null)
            return null;
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

    private JsonNode? ReadBeyondOptional(ref Cursor cursor, string type, TypePlan plan, int depth, bool sandbox)
    {
        int origin = cursor.Position;
        int? count = ReadHeader(ref cursor);
        if (count == null)
            return null;
        if (count == 0)
            return null;
        if (count != 2)
        {
            throw new InvalidDataException(
                $"memberCount {count} at offset {origin} does not match {type} (wire 2)."
            );
        }

        cursor.Position = origin;
        MpType resolved = plan.ResolvedObject ?? _schema.Require(type, hint: null);
        return ReadObject(ref cursor, resolved, depth, sandbox);
    }

    private bool TryReadPolymorphic(
        ref Cursor cursor,
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
        MpType? taggedChosen = null;
        int taggedCount = 0;
        if (afterTag is byte next)
        {
            foreach (MpType d in derived)
            {
                if (d.Wire.Length == next)
                {
                    taggedChosen = d;
                    taggedCount++;
                    if (taggedCount > 1)
                        break;
                }
            }
        }

        MpType? chosen = null;
        int? tag = null;
        if (taggedCount == 1)
        {
            tag = ReadUnionTag(ref cursor);
            chosen = taggedChosen;
        }
        else
        {
            MpType? untaggedChosen = null;
            int untaggedCount = 0;
            foreach (MpType d in derived)
            {
                if (d.Wire.Length == header)
                {
                    untaggedChosen = d;
                    untaggedCount++;
                    if (untaggedCount > 1)
                        break;
                }
            }

            if (untaggedCount != 1)
                return false;
            chosen = untaggedChosen;
        }

        node = ReadObject(ref cursor, chosen!, depth, sandbox);
        if (node is JsonObject obj)
        {
            if (!obj.ContainsKey("$type"))
                obj["$type"] = chosen!.TargetType;
            if (tag != null)
                obj["$tag"] = tag.Value;
        }

        return true;
    }

    private JsonNode? ReadTaggedUnion(ref Cursor cursor, string staticType, TypePlan plan, string? hint, int depth, bool sandbox)
    {
        int origin = cursor.Position;
        int? tag = ReadUnionTag(ref cursor);
        if (tag == null)
            return null;

        if (TryMappedConcrete(staticType, plan, tag.Value, hint, out MpType mappedType))
        {
            JsonNode? mapped = ReadConcrete(ref cursor, mappedType, depth, sandbox);
            if (mapped is JsonObject mappedObj)
            {
                if (!mappedObj.ContainsKey("$type"))
                    mappedObj["$type"] = ShortName(mappedType);
                mappedObj["$tag"] = tag.Value;
            }

            return mapped;
        }

        int afterTag = cursor.Position;
        byte? headerByte = afterTag < cursor.Length ? cursor.Buffer[afterTag] : null;
        IReadOnlyList<MpType> candidates = _unions.GetCandidates(staticType, hint);
        var matches = new List<(MpType Type, int End, JsonObject Value)>();
        foreach (MpType candidate in candidates)
        {
            if (headerByte is byte hb)
            {
                if (hb == 255)
                    continue;
                if (hb == 0)
                {
                    if (candidate.Wire.Length > 0 && !AllowsEmptyMemberCount(candidate, sandbox: true))
                        continue;
                }
                else if (hb < 250 && hb != candidate.Wire.Length)
                {
                    continue;
                }
            }

            var fork = cursor.Fork(afterTag);
            try
            {
                JsonNode? node = ReadObject(ref fork, candidate, depth + 1, sandbox: true);
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
        int? header = headerByte;
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

    private JsonNode? ReadConcrete(ref Cursor cursor, MpType type, int depth, bool sandbox)
    {
        JsonNode? node = ReadObject(ref cursor, type, depth, sandbox);
        if (node is JsonObject obj && !obj.ContainsKey("$type"))
            obj["$type"] = ShortName(type);
        return node;
    }

    private JsonNode? ReadObject(ref Cursor cursor, MpType type, int depth, bool sandbox)
    {
        int origin = cursor.Position;
        int? count = ReadHeader(ref cursor);
        if (count == null)
            return null;
        if (count < 0)
            throw new InvalidDataException($"Negative member count {count} at offset {origin}.");

        MpMember[] members;
        if (count == type.Wire.Length)
            members = type.Wire;
        else if (count == 0 && type.Wire.Length > 0 && AllowsEmptyMemberCount(type, sandbox))
        {
            return new JsonObject();
        }
        else if (count > 0 && count < type.Wire.Length && !sandbox)
        {
            members = type.Wire[..count.Value];
        }
        else
        {
            throw new InvalidDataException(
                $"memberCount {count} at offset {origin} does not match {ShortName(type)} "
                + $"(wire {type.Wire.Length})."
            );
        }

        var obj = new JsonObject();
        foreach (MpMember member in members)
        {
            int start = cursor.Position;
            try
            {
                obj[member.Name] = ReadValue(
                    ref cursor, member.Type, member.Name, depth + 1, sandbox, member.CollectionEncoding);
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
        ref Cursor cursor,
        TypePlan plan,
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
        MpUnmanagedLayout? elemLayout = plan.ElementUnmanaged;
        string? encoding = collectionEncoding ?? elemLayout?.CollectionEncoding;
        bool memcpyArray = elemLayout != null
            && string.Equals(encoding, "memcpy", StringComparison.Ordinal);
        string element = plan.ElementType!;

        for (int i = 0; i < count; i++)
        {
            if (elemLayout != null && !memcpyArray
                && TryReadUnmanagedFormatter(ref cursor, plan.ElementFormatterType, depth + 1, sandbox, out JsonNode? formatted))
            {
                array.Add(formatted);
            }
            else if (elemLayout != null)
                array.Add(ReadUnmanaged(ref cursor, elemLayout, depth + 1, sandbox));
            else
                array.Add(ReadValue(ref cursor, element, hint, depth + 1, sandbox));
        }

        return array;
    }

    private bool TryReadUnmanagedFormatter(
        ref Cursor cursor,
        MpType? formatterType,
        int depth,
        bool sandbox,
        out JsonNode? node)
    {
        node = null;
        if (cursor.Remaining == 0 || formatterType == null)
            return false;

        int formatterCount = formatterType.Wire.Length;
        if (formatterCount <= 0 || cursor.Buffer[cursor.Position] != formatterCount)
            return false;
        node = ReadObject(ref cursor, formatterType, depth, sandbox);
        return true;
    }

    private static void MaybeConsumeDictionaryValuePresence(ref Cursor cursor, TypePlan plan)
    {
        if (!plan.DictValueIsObject || plan.DictValueObject == null || cursor.Remaining < 2)
            return;
        if (cursor.Buffer[cursor.Position] != 1)
            return;

        byte next = cursor.Buffer[cursor.Position + 1];
        int wireLength = plan.DictValueObject.Wire.Length;
        if (next == 255 || next == 250 || (wireLength > 1 && next == wireLength))
            cursor.U8();
    }

    private bool TryUnmanagedPairLayout(string keyType, string valueType, out int midPad, out int endPad)
    {
        midPad = 0;
        endPad = 0;
        try
        {
            if (_schema.KindOf(keyType) == MpKind.Enum || _schema.HasWrapper(valueType))
                return false;
        }
        catch (MemoryPackTypeException)
        {
            return false;
        }

        if (!TryBlittableLayout(keyType, out int keySize, out int keyAlign)
            || !TryBlittableLayout(valueType, out int valueSize, out int valueAlign))
            return false;
        int afterKey = Align(0, keyAlign) + keySize;
        int valueStart = Align(afterKey, valueAlign);
        int afterValue = valueStart + valueSize;
        int pairEnd = Align(afterValue, Math.Max(keyAlign, valueAlign));
        midPad = valueStart - afterKey;
        endPad = pairEnd - afterValue;
        return midPad > 0 || endPad > 0;
    }

    private bool TryBlittableLayout(string type, out int size, out int align)
    {
        size = 0;
        align = 1;
        if (_schema.TryUnmanaged(type, out MpUnmanagedLayout? layout) && layout.PayloadSize > 0)
        {
            size = layout.PayloadSize;
            align = 1;
            foreach (MpUnmanagedField field in layout.Fields)
            {
                if (TryBlittableLayout(field.Type, out _, out int fieldAlign) && fieldAlign > align)
                    align = fieldAlign;
                else if (field.Size is int fieldSize)
                {
                    int guessed = fieldSize >= 8 ? 8 : fieldSize >= 4 ? 4 : fieldSize >= 2 ? 2 : 1;
                    if (guessed > align)
                        align = guessed;
                }
            }

            return true;
        }

        MpKind kind;
        try
        {
            kind = _schema.KindOf(type);
        }
        catch (MemoryPackTypeException)
        {
            return false;
        }

        switch (kind)
        {
            case MpKind.Bool or MpKind.Int8 or MpKind.UInt8:
                size = 1;
                align = 1;
                return true;
            case MpKind.Int16 or MpKind.UInt16:
                size = 2;
                align = 2;
                return true;
            case MpKind.Int32 or MpKind.UInt32 or MpKind.Enum or MpKind.Float32:
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

    private static void SkipPad(ref Cursor cursor, int pad)
    {
        if (pad <= 0)
            return;
        if (cursor.Remaining < pad)
            throw new InvalidDataException($"Dictionary pair padding {pad} at offset {cursor.Position}.");
        cursor.Position += pad;
    }

    private JsonNode? ReadDictionary(ref Cursor cursor, TypePlan plan, int depth, bool sandbox, bool v2)
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
        string key = plan.DictKeyType!;
        string value = plan.DictValueType!;
        bool objectKeys = plan.DictObjectKeys;
        bool padPairs = plan.DictPadPairs;
        int midPad = plan.DictMidPad;
        int endPad = plan.DictEndPad;
        var obj = objectKeys ? new JsonObject() : null;
        var pairs = objectKeys ? null : new JsonArray();
        for (int i = 0; i < count; i++)
        {
            JsonNode? keyNode = ReadValue(ref cursor, key, null, depth + 1, sandbox);
            if (padPairs)
                SkipPad(ref cursor, midPad);
            if (!v2)
                MaybeConsumeDictionaryValuePresence(ref cursor, plan);
            JsonNode? valueNode = plan.DictValueUnmanaged != null
                && TryReadUnmanagedFormatter(ref cursor, plan.DictValueFormatterType, depth + 1, sandbox, out JsonNode? formatted)
                ? formatted
                : ReadValue(ref cursor, value, null, depth + 1, sandbox);
            if (padPairs)
                SkipPad(ref cursor, endPad);
            if (!objectKeys)
            {
                var pair = new JsonArray();
                pair.Add(keyNode);
                pair.Add(valueNode);
                pairs!.Add((JsonNode)pair);
                continue;
            }

            string text = keyNode switch
            {
                JsonValue v when v.TryGetValue(out string? s) => s ?? "",
                JsonValue v when v.TryGetValue(out uint n) => n.ToString(CultureInfo.InvariantCulture),
                JsonValue v when v.TryGetValue(out int n) => n.ToString(CultureInfo.InvariantCulture),
                JsonValue v when v.TryGetValue(out long n) => n.ToString(CultureInfo.InvariantCulture),
                JsonValue v when v.TryGetValue(out ulong n) => n.ToString(CultureInfo.InvariantCulture),
                _ => keyNode?.ToJsonString() ?? "",
            };
            obj![text] = valueNode;
        }

        return objectKeys ? obj : pairs;
    }

    private JsonNode? ReadNullable(ref Cursor cursor, string inner, int depth, bool sandbox)
    {
        int origin = cursor.Position;
        int? count = ReadHeader(ref cursor);
        if (count == null)
            return null;
        if (count == 0)
            return null;
        if (count != 1)
            throw new InvalidDataException($"Nullable<{inner}> memberCount {count} at offset {origin}.");
        return ReadValue(ref cursor, inner, null, depth + 1, sandbox);
    }

    private JsonNode? ReadString(ref Cursor cursor)
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

    private static JsonNode? ReadMemoryPackString(ref Cursor cursor)
    {
        int origin = cursor.Position;
        int length = cursor.I32();
        if (length == -1)
            return null;
        if (length < 0)
            throw new InvalidDataException($"MemoryPack string length {length} at offset {origin}.");
        return JsonValue.Create(Encoding.UTF8.GetString(cursor.Take(length)));
    }

    private JsonNode? ReadMemoryPackValue(ref Cursor cursor, string type, int depth, bool sandbox)
    {
        MpType resolved = _schema.ResolveNamed(type);
        MpMember[] members = resolved.MemorypackWire
            ?? throw new MemoryPackTypeException(
                $"Missing memorypack_wire for {type} (nested ReadValue / TFormatter).");
        int origin = cursor.Position;
        int? count = ReadHeader(ref cursor);
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
                ref cursor, member.Type, member.Name, depth + 1, sandbox, member.CollectionEncoding);
        }

        return obj;
    }

    private static int? ReadHeader(ref Cursor cursor)
    {
        byte b = cursor.U8();
        if (b == 255)
            return null;
        if (b == 250)
            return cursor.I32();
        return b;
    }

    private static int? ReadUnionTag(ref Cursor cursor)
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

    private sealed class TypePlan
    {
        public bool IsOptional { get; init; }
        public MpUnmanagedLayout? Unmanaged { get; init; }
        public MpType? UnmanagedFormatterType { get; init; }
        public bool IsTaggedUnion { get; init; }
        public Dictionary<int, string>? DirectUnionMap { get; init; }
        public MpType? PolyBase { get; init; }
        public MpType[]? PolyDerived { get; init; }
        public MpKind? Kind { get; init; }
        public string? KindError { get; init; }
        public byte EnumWidth { get; init; }
        public byte EnumLeafWidth { get; init; }
        public MpType? ResolvedObject { get; init; }
        public string? ElementType { get; init; }
        public MpUnmanagedLayout? ElementUnmanaged { get; init; }
        public MpType? ElementFormatterType { get; init; }
        public string? DictKeyType { get; init; }
        public string? DictValueType { get; init; }
        public bool DictObjectKeys { get; init; }
        public bool DictPadPairs { get; init; }
        public int DictMidPad { get; init; }
        public int DictEndPad { get; init; }
        public bool DictValueIsObject { get; init; }
        public MpType? DictValueObject { get; init; }
        public MpUnmanagedLayout? DictValueUnmanaged { get; init; }
        public MpType? DictValueFormatterType { get; init; }
    }

    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _data;
        public int Position;

        public Cursor(ReadOnlySpan<byte> data, int position = 0)
        {
            _data = data;
            Position = position;
        }

        public readonly int Length => _data.Length;
        public readonly int Remaining => _data.Length - Position;
        public readonly ReadOnlySpan<byte> Buffer => _data;

        public readonly Cursor Fork(int position) => new(_data, position);

        public byte U8() => Take(1)[0];

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || Position > _data.Length - count)
            {
                throw new InvalidDataException(
                    $"Need {count} bytes at offset {Position}, buffer length is {_data.Length}."
                );
            }

            var slice = _data.Slice(Position, count);
            Position += count;
            return slice;
        }
    }
}
