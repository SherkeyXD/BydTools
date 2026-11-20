using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BydTools.Core.IO;

namespace BydTools.Formats.MemoryPack;

/// <summary>
/// Decodes a Beyond.MemoryPack payload using <c>schemas.json</c> wire_order directly to UTF-8 JSON.
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
    private readonly ConcurrentDictionary<MpType, TypePlan[]> _memberPlans = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<MpType, TypePlan[]> _memorypackMemberPlans = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<MpUnmanagedLayout, TypePlan[]> _unmanagedFieldPlans = new(ReferenceEqualityComparer.Instance);

    public MemoryPackDecoder(
        MemoryPackSchema schema,
        BeyondStringPool? pool = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? unionMaps = null)
    {
        _schema = schema;
        _pool = pool;
        _unions = new MemoryPackUnionResolver(schema, unionMaps);
    }

    public void WriteJson(ReadOnlySpan<byte> data, string rootType, ArrayBufferWriter<byte> output)
    {
        var reader = new SpanReader(data);
        using (var writer = new Utf8JsonWriter(output, JsonDataMemoryPack.WriterOptions))
        {
            WriteValue(ref reader, rootType, GetPlan(rootType), hint: null, depth: 0, sandbox: false, MpCollectionEncoding.None, writer);
            writer.Flush();
        }

        if (reader.Remaining != 0)
        {
            throw new InvalidDataException(
                $"Decoded {rootType} and left {reader.Remaining} trailing byte(s) at offset {reader.Position}."
            );
        }

        output.Write("\n"u8);
    }

    private TypePlan GetPlan(string type) => _plans.GetOrAdd(type, BuildPlan);

    private TypePlan[] GetMemberPlans(MpType type) =>
        _memberPlans.GetOrAdd(type, t => BuildMemberPlans(t.Wire));

    private TypePlan[] GetMemoryPackMemberPlans(MpType type) =>
        _memorypackMemberPlans.GetOrAdd(type, t => BuildMemberPlans(t.MemorypackWire!));

    private TypePlan[] GetUnmanagedFieldPlans(MpUnmanagedLayout layout) =>
        _unmanagedFieldPlans.GetOrAdd(layout, l =>
        {
            var plans = new TypePlan[l.Fields.Length];
            for (int i = 0; i < l.Fields.Length; i++)
                plans[i] = GetPlan(l.Fields[i].Type);
            return plans;
        });

    private TypePlan[] BuildMemberPlans(MpMember[] members)
    {
        var plans = new TypePlan[members.Length];
        for (int i = 0; i < members.Length; i++)
            plans[i] = GetPlan(members[i].Type);
        return plans;
    }

    private static UnmanagedWrapperKind GetWrapperKind(string type)
    {
        if (type.StartsWith("System.Nullable`1<", StringComparison.Ordinal)
            || type.StartsWith("System.Nullable<", StringComparison.Ordinal))
            return UnmanagedWrapperKind.Nullable;
        if (type.StartsWith("Beyond.Optional`1<", StringComparison.Ordinal)
            || type.StartsWith("Beyond.Optional<", StringComparison.Ordinal))
            return UnmanagedWrapperKind.Optional;
        return UnmanagedWrapperKind.None;
    }

    private static (int FlagIndex, int ValueIndex) FindWrapperFieldIndices(
        MpUnmanagedLayout? layout,
        UnmanagedWrapperKind kind)
    {
        if (layout == null || kind == UnmanagedWrapperKind.None)
            return (-1, -1);

        string flagName = kind == UnmanagedWrapperKind.Nullable ? "hasValue" : "enabled";
        int flagIndex = -1;
        int valueIndex = -1;
        for (int i = 0; i < layout.Fields.Length; i++)
        {
            string name = layout.Fields[i].Name;
            if (name == flagName)
                flagIndex = i;
            else if (kind == UnmanagedWrapperKind.Nullable && name == "value")
                valueIndex = i;
        }

        return (flagIndex, valueIndex);
    }

    private TypePlan BuildPlan(string type)
    {
        UnmanagedWrapperKind wrapperKind = GetWrapperKind(type);
        bool isOptional = wrapperKind == UnmanagedWrapperKind.Optional;

        _schema.TryUnmanaged(type, out MpUnmanagedLayout? unmanaged);
        if (isOptional)
        {
            _schema.TryResolve(type, hint: null, out MpType? opt, out _);
            (int flagIdx, int valIdx) = FindWrapperFieldIndices(unmanaged, wrapperKind);
            return new OptionalPlan(unmanaged, opt, wrapperKind, flagIdx, valIdx);
        }

        if (unmanaged != null)
        {
            MpType? formatterType = null;
            if (_schema.HasWrapper(type))
                _schema.TryResolveNamed(type, out formatterType);
            (int flagIdx, int valIdx) = FindWrapperFieldIndices(unmanaged, wrapperKind);
            return new UnmanagedPlan(unmanaged, formatterType, wrapperKind, flagIdx, valIdx);
        }

        Dictionary<int, string>? unionMap = _unions.LookupUnionMap(type);
        if (unionMap != null || MemoryPackUnionResolver.IsHardcodedTaggedUnion(type))
            return new TaggedUnionPlan(unionMap);

        if (_schema.TryPolymorphicBase(type, out MpType? polyBase, out MpType[] polyDerived)
            && polyBase != null)
            return new PolymorphicPlan(polyDerived);

        MpKind kind;
        try
        {
            kind = _schema.KindOf(type);
        }
        catch (MemoryPackTypeException ex)
        {
            return new ErrorPlan(ex.Message);
        }

        switch (kind)
        {
            case MpKind.Enum:
                _schema.TryEnumWidth(type, out byte width);
                return new EnumPlan(width, InferEnumLeafWidth(type));

            case MpKind.List:
            {
                string elem = ElementType(type);
                _schema.TryUnmanaged(elem, out MpUnmanagedLayout? elemLayout);
                MpType? elemFormatter = null;
                if (elemLayout != null && _schema.HasWrapper(elem))
                    _schema.TryResolveNamed(elem, out elemFormatter);

                return new ListPlan(elem, elemLayout, elemFormatter);
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
                        _schema.TryResolveNamed(valueType, out valObj);
                }
                catch (MemoryPackTypeException)
                {
                }

                _schema.TryUnmanaged(valueType, out MpUnmanagedLayout? valUnmanaged);
                MpType? valFormatter = null;
                if (valUnmanaged != null && _schema.HasWrapper(valueType))
                    _schema.TryResolveNamed(valueType, out valFormatter);

                return new DictionaryPlan(
                    IsV2: kind == MpKind.DictionaryV2,
                    keyType,
                    valueType,
                    objectKeys,
                    padPairs,
                    midPad,
                    endPad,
                    valKind == MpKind.Object,
                    valObj,
                    valUnmanaged,
                    valFormatter
                );
            }

            case MpKind.Nullable:
                return new NullablePlan(InnerType(type));

            case MpKind.Object:
                return _schema.TryResolveNamed(type, out MpType? resolved)
                    ? new ObjectPlan(resolved, null)
                    : new ObjectPlan(null, GetResolveError(type));

            default:
                return new PrimitivePlan(kind);
        }
    }

    private string? GetResolveError(string type)
    {
        _schema.TryResolve(type, hint: null, out _, out string? error);
        return error;
    }

    private void WriteValue(
        ref SpanReader reader,
        string type,
        TypePlan plan,
        string? hint,
        int depth,
        bool sandbox,
        MpCollectionEncoding collectionEncoding,
        Utf8JsonWriter? writer)
    {
        if (depth > 64)
            throw new InvalidDataException($"MemoryPack nesting is deeper than 64 at offset {reader.Position}.");

        if (plan is OptionalPlan optionalPlan)
        {
            if (collectionEncoding == MpCollectionEncoding.Memcpy && optionalPlan.Unmanaged != null)
            {
                WriteUnmanaged(
                    ref reader,
                    optionalPlan.Unmanaged,
                    optionalPlan.WrapperKind,
                    optionalPlan.FlagFieldIndex,
                    optionalPlan.ValueFieldIndex,
                    depth,
                    sandbox,
                    writer);
                return;
            }

            WriteBeyondOptional(ref reader, type, optionalPlan, depth, sandbox, writer);
            return;
        }

        if (plan is UnmanagedPlan unmanagedPlan)
        {
            if (collectionEncoding == MpCollectionEncoding.Formatter
                && TryWriteUnmanagedFormatter(ref reader, unmanagedPlan.FormatterType, unmanagedPlan.WrapperKind, depth, sandbox, writer))
                return;

            WriteUnmanaged(
                ref reader,
                unmanagedPlan.Layout,
                unmanagedPlan.WrapperKind,
                unmanagedPlan.FlagFieldIndex,
                unmanagedPlan.ValueFieldIndex,
                depth,
                sandbox,
                writer);
            return;
        }

        if (collectionEncoding == MpCollectionEncoding.Memorypack)
        {
            WriteMemoryPackValue(ref reader, type, depth, sandbox, writer);
            return;
        }

        if (plan is TaggedUnionPlan unionPlan)
        {
            WriteTaggedUnion(ref reader, type, unionPlan, hint, depth, sandbox, writer);
            return;
        }

        if (plan is PolymorphicPlan polyPlan
            && TryWritePolymorphic(ref reader, polyPlan, depth, sandbox, writer))
            return;

        switch (plan)
        {
            case ErrorPlan errorPlan:
                throw new MemoryPackTypeException(errorPlan.Message ?? $"No MemoryPack rule for \"{type}\".");
            case EnumPlan enumPlan:
            {
                int val = ReadEnum(ref reader, enumPlan);
                writer?.WriteNumberValue(val);
                break;
            }
            case ListPlan listPlan:
                WriteList(ref reader, listPlan, hint, depth, sandbox, collectionEncoding, writer);
                break;
            case DictionaryPlan dictPlan:
                WriteDictionary(ref reader, dictPlan, depth, sandbox, writer);
                break;
            case NullablePlan nullablePlan:
                WriteNullable(ref reader, nullablePlan, depth, sandbox, writer);
                break;
            case ObjectPlan objectPlan:
            {
                MpType resolved = objectPlan.ResolvedObject
                    ?? throw new MemoryPackTypeException(objectPlan.Error ?? $"No MemoryPack schema for {type}.");
                WriteObject(ref reader, resolved, extraType: null, extraTag: null, depth, sandbox, writer);
                break;
            }
            case PrimitivePlan primitivePlan:
                WritePrimitive(ref reader, primitivePlan.Kind, collectionEncoding, writer);
                break;
            default:
                throw new MemoryPackTypeException($"No MemoryPack rule for \"{type}\".");
        }
    }

    private void WritePrimitive(
        ref SpanReader reader,
        MpKind kind,
        MpCollectionEncoding collectionEncoding,
        Utf8JsonWriter? writer)
    {
        switch (kind)
        {
            case MpKind.Bool:
            {
                byte flag = reader.ReadByte();
                if (flag > 1)
                    throw new InvalidDataException($"Boolean {flag} at offset {reader.Position - 1}.");
                writer?.WriteBooleanValue(flag != 0);
                break;
            }
            case MpKind.Int8:
            {
                sbyte v = (sbyte)reader.ReadByte();
                writer?.WriteNumberValue(v);
                break;
            }
            case MpKind.UInt8:
            {
                int v = reader.ReadByte();
                writer?.WriteNumberValue(v);
                break;
            }
            case MpKind.Int16:
            {
                short v = reader.ReadInt16();
                writer?.WriteNumberValue(v);
                break;
            }
            case MpKind.UInt16:
            {
                int v = reader.ReadUInt16();
                writer?.WriteNumberValue(v);
                break;
            }
            case MpKind.Int32:
            {
                int v = reader.ReadInt32();
                writer?.WriteNumberValue(v);
                break;
            }
            case MpKind.UInt32:
            {
                uint v = reader.ReadUInt32();
                writer?.WriteNumberValue(v);
                break;
            }
            case MpKind.Int64:
            {
                long v = reader.ReadInt64();
                writer?.WriteNumberValue(v);
                break;
            }
            case MpKind.UInt64:
            {
                ulong v = reader.ReadUInt64();
                writer?.WriteNumberValue(v);
                break;
            }
            case MpKind.Float32:
            {
                float v = reader.ReadSingle();
                if (writer != null)
                    WriteSingle(writer, v);
                break;
            }
            case MpKind.Float64:
            {
                double v = reader.ReadDouble();
                if (writer != null)
                    WriteDouble(writer, v);
                break;
            }
            case MpKind.String:
            {
                string? s = collectionEncoding == MpCollectionEncoding.Utf8
                    ? ReadMemoryPackString(ref reader)
                    : ReadString(ref reader);
                if (writer != null)
                {
                    if (s == null)
                        writer.WriteNullValue();
                    else
                        writer.WriteStringValue(s);
                }
                break;
            }
            case MpKind.Vector2:
                WriteVec(writer, reader.ReadSingle(), reader.ReadSingle());
                break;
            case MpKind.Vector3:
                WriteVec(writer, reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                break;
            case MpKind.Vector4:
                WriteVec(writer, reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                break;
            case MpKind.Vector2Int:
                WriteVecInt(writer, reader.ReadInt32(), reader.ReadInt32());
                break;
            case MpKind.Vector3Int:
                WriteVecInt(writer, reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                break;
            case MpKind.AnimationCurve:
                WriteUnityAnimationCurve(ref reader, writer);
                break;
            default:
                throw new MemoryPackTypeException($"Unexpected primitive kind {kind}.");
        }
    }

    private void WriteUnmanaged(
        ref SpanReader reader,
        MpUnmanagedLayout layout,
        UnmanagedWrapperKind wrapperKind,
        int flagFieldIndex,
        int valueFieldIndex,
        int depth,
        bool sandbox,
        Utf8JsonWriter? writer)
    {
        if (reader.Remaining < layout.PayloadSize)
        {
            throw new InvalidDataException(
                $"{layout.TypeName} needs {layout.PayloadSize} bytes at offset {reader.Position}, have {reader.Remaining}."
            );
        }

        int origin = reader.Position;
        MpUnmanagedField[] fields = layout.Fields;
        TypePlan[] fieldPlans = GetUnmanagedFieldPlans(layout);

        if (wrapperKind != UnmanagedWrapperKind.None && flagFieldIndex >= 0)
        {
            MpUnmanagedField flagField = fields[flagFieldIndex];
            reader.Position = origin + flagField.Offset;
            bool hasFlag = ReadUnmanagedBool(ref reader);
            if (!hasFlag)
            {
                reader.Position = origin + layout.PayloadSize;
                writer?.WriteNullValue();
                return;
            }

            if (wrapperKind == UnmanagedWrapperKind.Nullable && valueFieldIndex >= 0)
            {
                MpUnmanagedField valueField = fields[valueFieldIndex];
                reader.Position = origin + valueField.Offset;
                WriteUnmanagedField(ref reader, valueField, fieldPlans[valueFieldIndex], depth, sandbox, writer);
                reader.Position = origin + layout.PayloadSize;
                return;
            }
        }

        writer?.WriteStartObject();
        for (int i = 0; i < fields.Length; i++)
        {
            MpUnmanagedField field = fields[i];
            reader.Position = origin + field.Offset;
            writer?.WritePropertyName(field.Name);
            WriteUnmanagedField(ref reader, field, fieldPlans[i], depth, sandbox, writer);
        }

        writer?.WriteEndObject();
        reader.Position = origin + layout.PayloadSize;
    }

    private static bool ReadUnmanagedBool(ref SpanReader reader) =>
        reader.ReadByte() != 0;

    private void WriteUnmanagedField(
        ref SpanReader reader,
        MpUnmanagedField field,
        TypePlan fieldPlan,
        int depth,
        bool sandbox,
        Utf8JsonWriter? writer)
    {
        if (fieldPlan is UnmanagedPlan unmanagedFieldPlan)
        {
            WriteUnmanaged(
                ref reader,
                unmanagedFieldPlan.Layout,
                unmanagedFieldPlan.WrapperKind,
                unmanagedFieldPlan.FlagFieldIndex,
                unmanagedFieldPlan.ValueFieldIndex,
                depth + 1,
                sandbox,
                writer);
            return;
        }

        int size = field.Size ?? 0;
        if (fieldPlan is EnumPlan enumPlan)
        {
            int val = ReadEnumWidth(ref reader, enumPlan, size);
            writer?.WriteNumberValue(val);
            return;
        }

        if (fieldPlan is PrimitivePlan primitivePlan)
        {
            switch (primitivePlan.Kind)
            {
                case MpKind.Bool:
                {
                    bool v = reader.ReadByte() != 0;
                    writer?.WriteBooleanValue(v);
                    return;
                }
                case MpKind.Float32:
                {
                    float v = reader.ReadSingle();
                    if (writer != null)
                        WriteSingle(writer, v);
                    return;
                }
                case MpKind.Float64:
                {
                    double v = reader.ReadDouble();
                    if (writer != null)
                        WriteDouble(writer, v);
                    return;
                }
                case MpKind.Int64:
                {
                    long v = reader.ReadInt64();
                    writer?.WriteNumberValue(v);
                    return;
                }
                case MpKind.UInt64:
                {
                    ulong v = reader.ReadUInt64();
                    writer?.WriteNumberValue(v);
                    return;
                }
                case MpKind.UInt32:
                {
                    uint v = reader.ReadUInt32();
                    writer?.WriteNumberValue(v);
                    return;
                }
                case MpKind.Int32:
                {
                    int v = reader.ReadInt32();
                    writer?.WriteNumberValue(v);
                    return;
                }
                case MpKind.Vector2:
                    WriteVec(writer, reader.ReadSingle(), reader.ReadSingle());
                    return;
                case MpKind.Vector3:
                    WriteVec(writer, reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    return;
                case MpKind.Vector4:
                    WriteVec(writer, reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    return;
                case MpKind.Vector2Int:
                    WriteVecInt(writer, reader.ReadInt32(), reader.ReadInt32());
                    return;
                case MpKind.Vector3Int:
                    WriteVecInt(writer, reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    return;
            }
        }

        switch (field.Type)
        {
            case "bool" or "System.Boolean":
            {
                bool v = reader.ReadByte() != 0;
                writer?.WriteBooleanValue(v);
                return;
            }
            case "float" or "System.Single":
            {
                float v = reader.ReadSingle();
                if (writer != null)
                    WriteSingle(writer, v);
                return;
            }
            case "double" or "System.Double":
            {
                double v = reader.ReadDouble();
                if (writer != null)
                    WriteDouble(writer, v);
                return;
            }
            case "long" or "System.Int64":
            {
                long v = reader.ReadInt64();
                writer?.WriteNumberValue(v);
                return;
            }
            case "ulong" or "System.UInt64":
            {
                ulong v = reader.ReadUInt64();
                writer?.WriteNumberValue(v);
                return;
            }
            case "uint" or "System.UInt32":
            {
                uint v = reader.ReadUInt32();
                writer?.WriteNumberValue(v);
                return;
            }
            case "int" or "System.Int32":
            {
                int v = reader.ReadInt32();
                writer?.WriteNumberValue(v);
                return;
            }
            default:
                switch (size)
                {
                    case 1:
                    {
                        int v = reader.ReadByte();
                        writer?.WriteNumberValue(v);
                        return;
                    }
                    case 2:
                    {
                        short v = reader.ReadInt16();
                        writer?.WriteNumberValue(v);
                        return;
                    }
                    case 8:
                    {
                        long v = reader.ReadInt64();
                        writer?.WriteNumberValue(v);
                        return;
                    }
                    default:
                    {
                        int v = reader.ReadInt32();
                        writer?.WriteNumberValue(v);
                        return;
                    }
                }
        }
    }

    private static int ReadEnumWidth(ref SpanReader reader, EnumPlan plan, int size)
    {
        if (size is 1 or 2 or 8)
        {
            return size switch
            {
                1 => reader.ReadByte(),
                2 => reader.ReadInt16(),
                _ => (int)reader.ReadInt64(),
            };
        }

        return ReadEnum(ref reader, plan);
    }

    private static int ReadEnum(ref SpanReader reader, EnumPlan plan)
    {
        byte width = plan.Width != 0 ? plan.Width : plan.LeafWidth;
        return width switch
        {
            1 => reader.ReadByte(),
            2 => reader.ReadInt16(),
            8 => (int)reader.ReadInt64(),
            _ => reader.ReadInt32(),
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

    private static void WriteUnityAnimationCurve(ref SpanReader reader, Utf8JsonWriter? writer)
    {
        int origin = reader.Position;
        int? count = ReadHeader(ref reader);
        if (count == null)
        {
            writer?.WriteNullValue();
            return;
        }

        if (count == 0)
        {
            if (writer != null)
            {
                writer.WriteStartObject();
                writer.WriteString("_type", "AnimationCurve");
                writer.WriteBoolean("_skipped", true);
                writer.WriteEndObject();
            }

            return;
        }

        if (count != 3)
        {
            throw new InvalidDataException(
                $"UnityEngine.AnimationCurve memberCount {count} at offset {origin} (expected 3)."
            );
        }

        int preWrapMode = reader.ReadInt32();
        int postWrapMode = reader.ReadInt32();
        int keyCount = reader.ReadInt32();

        if (writer != null)
        {
            writer.WriteStartObject();
            writer.WriteNumber("preWrapMode", preWrapMode);
            writer.WriteNumber("postWrapMode", postWrapMode);
            writer.WritePropertyName("keys");
        }

        if (keyCount == -1)
        {
            writer?.WriteNullValue();
        }
        else
        {
            if (keyCount < 0 || keyCount > 1_000_000)
                throw new InvalidDataException($"AnimationCurve key count {keyCount} at offset {reader.Position - 4}.");
            writer?.WriteStartArray();
            for (int i = 0; i < keyCount; i++)
            {
                if (reader.Remaining < 28)
                {
                    throw new InvalidDataException(
                        $"AnimationCurve keyframe needs 28 bytes at offset {reader.Position}."
                    );
                }

                float time = reader.ReadSingle();
                float value = reader.ReadSingle();
                float inTangent = reader.ReadSingle();
                float outTangent = reader.ReadSingle();
                int weightedMode = reader.ReadInt32();
                float inWeight = reader.ReadSingle();
                float outWeight = reader.ReadSingle();
                if (writer != null)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("time");
                    WriteSingle(writer, time);
                    writer.WritePropertyName("value");
                    WriteSingle(writer, value);
                    writer.WritePropertyName("inTangent");
                    WriteSingle(writer, inTangent);
                    writer.WritePropertyName("outTangent");
                    WriteSingle(writer, outTangent);
                    writer.WriteNumber("weightedMode", weightedMode);
                    writer.WritePropertyName("inWeight");
                    WriteSingle(writer, inWeight);
                    writer.WritePropertyName("outWeight");
                    WriteSingle(writer, outWeight);
                    writer.WriteEndObject();
                }
            }

            writer?.WriteEndArray();
        }

        writer?.WriteEndObject();
    }

    private void WriteBeyondOptional(
        ref SpanReader reader,
        string type,
        OptionalPlan plan,
        int depth,
        bool sandbox,
        Utf8JsonWriter? writer)
    {
        int origin = reader.Position;
        int? count = ReadHeader(ref reader);
        if (count == null || count == 0)
        {
            writer?.WriteNullValue();
            return;
        }

        if (count != 2)
        {
            throw new InvalidDataException(
                $"memberCount {count} at offset {origin} does not match {type} (wire 2)."
            );
        }

        reader.Position = origin;
        MpType resolved = plan.ResolvedObject ?? _schema.Require(type, hint: null);
        WriteObject(ref reader, resolved, extraType: null, extraTag: null, depth, sandbox, writer);
    }

    private bool TryWritePolymorphic(
        ref SpanReader reader,
        PolymorphicPlan plan,
        int depth,
        bool sandbox,
        Utf8JsonWriter? writer)
    {
        if (reader.Remaining == 0)
            return false;
        byte header = reader.Buffer[reader.Position];
        if (header == 255)
        {
            reader.ReadByte();
            writer?.WriteNullValue();
            return true;
        }

        MpType[] derived = plan.Derived;
        byte? afterTag = reader.Remaining >= 2 ? reader.Buffer[reader.Position + 1] : null;
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

        MpType? chosen;
        int? tag = null;
        if (taggedCount == 1)
        {
            tag = ReadUnionTag(ref reader);
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

        WriteObject(ref reader, chosen!, extraType: chosen!.TargetType, extraTag: tag, depth, sandbox, writer);
        return true;
    }

    private void WriteTaggedUnion(
        ref SpanReader reader,
        string staticType,
        TaggedUnionPlan plan,
        string? hint,
        int depth,
        bool sandbox,
        Utf8JsonWriter? writer)
    {
        int origin = reader.Position;
        int? tag = ReadUnionTag(ref reader);
        if (tag == null)
        {
            writer?.WriteNullValue();
            return;
        }

        if (_unions.TryMappedConcrete(staticType, plan.DirectUnionMap, tag.Value, hint, out MpType mappedType))
        {
            WriteObject(ref reader, mappedType, extraType: ShortName(mappedType), extraTag: tag.Value, depth, sandbox, writer);
            return;
        }

        int afterTag = reader.Position;
        byte? headerByte = afterTag < reader.Length ? reader.Buffer[afterTag] : null;
        IReadOnlyList<MpType> candidates = _unions.GetCandidates(staticType, hint);
        var matches = new List<(MpType Type, int End)>();
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

            var fork = reader.Fork(afterTag);
            try
            {
                if (!TryValidateObject(ref fork, candidate, depth + 1))
                    continue;
                matches.Add((candidate, fork.Position));
            }
            catch (Exception ex) when (ex is InvalidDataException or MemoryPackTypeException)
            {
                // This concrete type does not fit the bytes.
            }
        }

        var ends = new Dictionary<int, MpType>();
        var ambiguousEnds = new HashSet<int>();
        foreach (var match in matches)
        {
            if (!ends.TryAdd(match.End, match.Type))
                ambiguousEnds.Add(match.End);
        }

        if (ends.Count == 1 && ambiguousEnds.Count == 0)
        {
            MpType chosen = ends.Values.First();
            if (writer != null)
            {
                reader.Position = afterTag;
                WriteObject(ref reader, chosen, extraType: ShortName(chosen), extraTag: tag.Value, depth + 1, sandbox: true, writer);
            }
            else
            {
                reader.Position = ends.Keys.First();
            }

            return;
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

    private bool TryValidateObject(ref SpanReader reader, MpType type, int depth)
    {
        int origin = reader.Position;
        int? count = ReadHeader(ref reader);
        if (count == null)
            return false;
        if (count < 0)
            throw new InvalidDataException($"Negative member count {count} at offset {origin}.");

        if (count == 0 && type.Wire.Length > 0 && AllowsEmptyMemberCount(type, sandbox: true))
            return true;

        if (count != type.Wire.Length)
        {
            throw new InvalidDataException(
                $"memberCount {count} at offset {origin} does not match {ShortName(type)} "
                + $"(wire {type.Wire.Length})."
            );
        }

        MpMember[] members = type.Wire;
        TypePlan[] memberPlans = GetMemberPlans(type);
        for (int i = 0; i < members.Length; i++)
        {
            MpMember member = members[i];
            WriteValue(ref reader, member.Type, memberPlans[i], member.Name, depth + 1, sandbox: true, member.CollectionEncoding, writer: null);
        }

        return true;
    }

    private void WriteObject(
        ref SpanReader reader,
        MpType type,
        string? extraType,
        int? extraTag,
        int depth,
        bool sandbox,
        Utf8JsonWriter? writer)
    {
        int origin = reader.Position;
        int? count = ReadHeader(ref reader);
        if (count == null)
        {
            writer?.WriteNullValue();
            return;
        }

        if (count < 0)
            throw new InvalidDataException($"Negative member count {count} at offset {origin}.");

        MpMember[] members;
        int memberCount;
        if (count == type.Wire.Length)
        {
            members = type.Wire;
            memberCount = members.Length;
        }
        else if (count == 0 && type.Wire.Length > 0 && AllowsEmptyMemberCount(type, sandbox))
        {
            if (writer != null)
            {
                writer.WriteStartObject();
                if (extraType != null)
                    writer.WriteString("$type", extraType);
                if (extraTag != null)
                    writer.WriteNumber("$tag", extraTag.Value);
                writer.WriteEndObject();
            }

            return;
        }
        else if (count > 0 && count < type.Wire.Length && !sandbox)
        {
            members = type.Wire;
            memberCount = count.Value;
        }
        else
        {
            throw new InvalidDataException(
                $"memberCount {count} at offset {origin} does not match {ShortName(type)} "
                + $"(wire {type.Wire.Length})."
            );
        }

        TypePlan[] memberPlans = GetMemberPlans(type);
        writer?.WriteStartObject();
        bool sawType = false;
        for (int i = 0; i < memberCount; i++)
        {
            MpMember member = members[i];
            if (member.Name == "$type")
                sawType = true;
            writer?.WritePropertyName(member.Name);
            WriteValue(
                ref reader, member.Type, memberPlans[i], member.Name, depth + 1, sandbox, member.CollectionEncoding, writer);
        }

        if (writer != null)
        {
            if (extraType != null && !sawType)
                writer.WriteString("$type", extraType);
            if (extraTag != null)
                writer.WriteNumber("$tag", extraTag.Value);
            writer.WriteEndObject();
        }
    }

    private static bool AllowsEmptyMemberCount(MpType type, bool sandbox)
    {
        if (!sandbox)
            return true;
        return type.TargetType.StartsWith("Beyond.Optional`1<", StringComparison.Ordinal)
            || type.TargetType.StartsWith("Beyond.Gameplay.Actions.Param`1<", StringComparison.Ordinal);
    }

    private void WriteList(
        ref SpanReader reader,
        ListPlan plan,
        string? hint,
        int depth,
        bool sandbox,
        MpCollectionEncoding collectionEncoding,
        Utf8JsonWriter? writer)
    {
        int count = reader.ReadInt32();
        if (count == -1)
        {
            writer?.WriteNullValue();
            return;
        }

        if (count < 0 || count > 1_000_000)
            throw new InvalidDataException($"List count {count} at offset {reader.Position - 4}.");

        MpUnmanagedLayout? elemLayout = plan.ElementUnmanaged;
        MpCollectionEncoding encoding = collectionEncoding != MpCollectionEncoding.None
            ? collectionEncoding
            : elemLayout?.CollectionEncoding ?? MpCollectionEncoding.None;
        bool memcpyArray = elemLayout != null && encoding == MpCollectionEncoding.Memcpy;
        string element = plan.ElementType;
        TypePlan? elemPlan = elemLayout == null ? plan.GetElementPlan(this) : null;

        writer?.WriteStartArray();
        for (int i = 0; i < count; i++)
        {
            if (elemLayout != null && !memcpyArray
                && TryWriteUnmanagedFormatter(ref reader, plan.ElementFormatterType, UnmanagedWrapperKind.None, depth + 1, sandbox, writer))
            {
                continue;
            }

            if (elemLayout != null)
            {
                WriteUnmanaged(ref reader, elemLayout, UnmanagedWrapperKind.None, -1, -1, depth + 1, sandbox, writer);
            }
            else
            {
                WriteValue(ref reader, element, elemPlan!, hint, depth + 1, sandbox, MpCollectionEncoding.None, writer);
            }
        }

        writer?.WriteEndArray();
    }

    private bool TryWriteUnmanagedFormatter(
        ref SpanReader reader,
        MpType? formatterType,
        UnmanagedWrapperKind wrapperKind,
        int depth,
        bool sandbox,
        Utf8JsonWriter? writer)
    {
        if (reader.Remaining == 0 || formatterType == null)
            return false;

        int formatterCount = formatterType.Wire.Length;
        if (formatterCount <= 0 || reader.Buffer[reader.Position] != formatterCount)
            return false;

        if (wrapperKind == UnmanagedWrapperKind.None)
        {
            WriteObject(ref reader, formatterType, extraType: null, extraTag: null, depth, sandbox, writer);
            return true;
        }

        // Unwrap Nullable / Optional formatter wrapper when needed.
        int origin = reader.Position;
        int? count = ReadHeader(ref reader);
        if (count == null)
        {
            writer?.WriteNullValue();
            return true;
        }

        if (count != formatterType.Wire.Length)
        {
            reader.Position = origin;
            return false;
        }

        MpMember[] members = formatterType.Wire;
        TypePlan[] memberPlans = GetMemberPlans(formatterType);
        string flagName = wrapperKind == UnmanagedWrapperKind.Nullable ? "hasValue" : "enabled";

        if (wrapperKind == UnmanagedWrapperKind.Nullable && members.Length == 2 && members[0].Name == flagName && members[1].Name == "value")
        {
            bool hasValue = ReadMemberBool(ref reader, members[0], memberPlans[0], depth + 1, sandbox);
            if (!hasValue)
            {
                WriteValue(ref reader, members[1].Type, memberPlans[1], members[1].Name, depth + 1, sandbox, members[1].CollectionEncoding, writer: null);
                writer?.WriteNullValue();
            }
            else
            {
                WriteValue(ref reader, members[1].Type, memberPlans[1], members[1].Name, depth + 1, sandbox, members[1].CollectionEncoding, writer);
            }

            return true;
        }

        if (wrapperKind == UnmanagedWrapperKind.Optional && members.Length >= 1 && members[0].Name == flagName)
        {
            bool enabled = ReadMemberBool(ref reader, members[0], memberPlans[0], depth + 1, sandbox);
            if (!enabled)
            {
                for (int i = 1; i < members.Length; i++)
                {
                    MpMember m = members[i];
                    WriteValue(ref reader, m.Type, memberPlans[i], m.Name, depth + 1, sandbox, m.CollectionEncoding, writer: null);
                }

                writer?.WriteNullValue();
                return true;
            }

            if (writer != null)
            {
                writer.WriteStartObject();
                writer.WriteBoolean(members[0].Name, true);
                for (int i = 1; i < members.Length; i++)
                {
                    MpMember m = members[i];
                    writer.WritePropertyName(m.Name);
                    WriteValue(ref reader, m.Type, memberPlans[i], m.Name, depth + 1, sandbox, m.CollectionEncoding, writer);
                }

                writer.WriteEndObject();
            }
            else
            {
                for (int i = 1; i < members.Length; i++)
                {
                    MpMember m = members[i];
                    WriteValue(ref reader, m.Type, memberPlans[i], m.Name, depth + 1, sandbox, m.CollectionEncoding, writer: null);
                }
            }

            return true;
        }

        reader.Position = origin;
        WriteObject(ref reader, formatterType, extraType: null, extraTag: null, depth, sandbox, writer);
        return true;
    }

    private bool ReadMemberBool(ref SpanReader reader, MpMember member, TypePlan plan, int depth, bool sandbox)
    {
        if (plan is PrimitivePlan { Kind: MpKind.Bool })
        {
            byte flag = reader.ReadByte();
            if (flag > 1)
                throw new InvalidDataException($"Boolean {flag} at offset {reader.Position - 1}.");
            return flag != 0;
        }

        var tmp = new ArrayBufferWriter<byte>(8);
        using (var w = new Utf8JsonWriter(tmp))
        {
            WriteValue(ref reader, member.Type, plan, member.Name, depth, sandbox, member.CollectionEncoding, w);
            w.Flush();
        }

        return tmp.WrittenSpan.SequenceEqual("true"u8);
    }

    private static void MaybeConsumeDictionaryValuePresence(ref SpanReader reader, DictionaryPlan plan)
    {
        if (!plan.ValueIsObject || plan.ValueObject == null || reader.Remaining < 2)
            return;
        if (reader.Buffer[reader.Position] != 1)
            return;

        byte next = reader.Buffer[reader.Position + 1];
        int wireLength = plan.ValueObject.Wire.Length;
        if (next == 255 || next == 250 || (wireLength > 1 && next == wireLength))
            reader.ReadByte();
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

    private static void SkipPad(ref SpanReader reader, int pad)
    {
        if (pad <= 0)
            return;
        if (reader.Remaining < pad)
            throw new InvalidDataException($"Dictionary pair padding {pad} at offset {reader.Position}.");
        reader.Skip(pad);
    }

    private void WriteDictionary(
        ref SpanReader reader,
        DictionaryPlan plan,
        int depth,
        bool sandbox,
        Utf8JsonWriter? writer)
    {
        if (plan.IsV2)
        {
            byte header = reader.ReadByte();
            if (header == 255)
            {
                writer?.WriteNullValue();
                return;
            }
        }

        int count = reader.ReadInt32();
        if (count == -1)
        {
            writer?.WriteNullValue();
            return;
        }

        if (count < 0 || count > 1_000_000)
            throw new InvalidDataException($"Dictionary count {count} at offset {reader.Position - 4}.");

        string key = plan.KeyType;
        string value = plan.ValueType;
        TypePlan keyPlan = plan.GetKeyPlan(this);
        TypePlan valuePlan = plan.GetValuePlan(this);
        bool objectKeys = plan.ObjectKeys;
        bool padPairs = plan.PadPairs;
        int midPad = plan.MidPad;
        int endPad = plan.EndPad;

        if (objectKeys)
            writer?.WriteStartObject();
        else
            writer?.WriteStartArray();

        for (int i = 0; i < count; i++)
        {
            if (objectKeys)
            {
                if (writer != null)
                {
                    string keyText = ReadKeyText(ref reader, key, keyPlan, depth + 1, sandbox);
                    writer.WritePropertyName(keyText);
                }
                else
                {
                    WriteValue(ref reader, key, keyPlan, null, depth + 1, sandbox, MpCollectionEncoding.None, writer: null);
                }

                if (padPairs)
                    SkipPad(ref reader, midPad);
                if (!plan.IsV2)
                    MaybeConsumeDictionaryValuePresence(ref reader, plan);
                if (plan.ValueUnmanaged != null
                    && TryWriteUnmanagedFormatter(ref reader, plan.ValueFormatterType, UnmanagedWrapperKind.None, depth + 1, sandbox, writer))
                {
                    // Written by formatter
                }
                else
                {
                    WriteValue(ref reader, value, valuePlan, null, depth + 1, sandbox, MpCollectionEncoding.None, writer);
                }

                if (padPairs)
                    SkipPad(ref reader, endPad);
            }
            else
            {
                writer?.WriteStartArray();
                WriteValue(ref reader, key, keyPlan, null, depth + 1, sandbox, MpCollectionEncoding.None, writer);
                if (padPairs)
                    SkipPad(ref reader, midPad);
                if (!plan.IsV2)
                    MaybeConsumeDictionaryValuePresence(ref reader, plan);
                if (plan.ValueUnmanaged != null
                    && TryWriteUnmanagedFormatter(ref reader, plan.ValueFormatterType, UnmanagedWrapperKind.None, depth + 1, sandbox, writer))
                {
                    // Written by formatter
                }
                else
                {
                    WriteValue(ref reader, value, valuePlan, null, depth + 1, sandbox, MpCollectionEncoding.None, writer);
                }

                if (padPairs)
                    SkipPad(ref reader, endPad);
                writer?.WriteEndArray();
            }
        }

        if (objectKeys)
            writer?.WriteEndObject();
        else
            writer?.WriteEndArray();
    }

    private string ReadKeyText(ref SpanReader reader, string keyType, TypePlan keyPlan, int depth, bool sandbox)
    {
        switch (keyPlan)
        {
            case EnumPlan enumPlan:
                return ReadEnum(ref reader, enumPlan).ToString(CultureInfo.InvariantCulture);
            case PrimitivePlan primitivePlan:
                switch (primitivePlan.Kind)
                {
                    case MpKind.String:
                        return ReadString(ref reader) ?? "";
                    case MpKind.Int8:
                        return ((sbyte)reader.ReadByte()).ToString(CultureInfo.InvariantCulture);
                    case MpKind.UInt8:
                        return reader.ReadByte().ToString(CultureInfo.InvariantCulture);
                    case MpKind.Int16:
                        return reader.ReadInt16().ToString(CultureInfo.InvariantCulture);
                    case MpKind.UInt16:
                        return reader.ReadUInt16().ToString(CultureInfo.InvariantCulture);
                    case MpKind.Int32:
                        return reader.ReadInt32().ToString(CultureInfo.InvariantCulture);
                    case MpKind.UInt32:
                        return reader.ReadUInt32().ToString(CultureInfo.InvariantCulture);
                    case MpKind.Int64:
                        return reader.ReadInt64().ToString(CultureInfo.InvariantCulture);
                    case MpKind.UInt64:
                        return reader.ReadUInt64().ToString(CultureInfo.InvariantCulture);
                }
                break;
        }

        var tmp = new ArrayBufferWriter<byte>(32);
        using (var w = new Utf8JsonWriter(tmp))
        {
            WriteValue(ref reader, keyType, keyPlan, null, depth, sandbox, MpCollectionEncoding.None, w);
            w.Flush();
        }

        return Encoding.UTF8.GetString(tmp.WrittenSpan);
    }

    private void WriteNullable(
        ref SpanReader reader,
        NullablePlan plan,
        int depth,
        bool sandbox,
        Utf8JsonWriter? writer)
    {
        int origin = reader.Position;
        int? count = ReadHeader(ref reader);
        if (count == null || count == 0)
        {
            writer?.WriteNullValue();
            return;
        }

        if (count != 1)
            throw new InvalidDataException($"Nullable<{plan.ElementType}> memberCount {count} at offset {origin}.");

        WriteValue(ref reader, plan.ElementType, plan.GetElementPlan(this), null, depth + 1, sandbox, MpCollectionEncoding.None, writer);
    }

    private string? ReadString(ref SpanReader reader)
    {
        int id = reader.ReadInt32();
        if (id < -2)
            throw new InvalidDataException($"String pool id {id} at offset {reader.Position - 4}.");
        if (id == -2)
            return null;
        if (id == -1)
            return "";
        if (_pool != null && _pool.TryResolve(id, out string? text))
            return text;
        return "#" + id.ToString(CultureInfo.InvariantCulture);
    }

    private static string? ReadMemoryPackString(ref SpanReader reader)
    {
        int origin = reader.Position;
        int length = reader.ReadInt32();
        if (length == -1)
            return null;
        if (length < 0)
            throw new InvalidDataException($"MemoryPack string length {length} at offset {origin}.");
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }

    private void WriteMemoryPackValue(
        ref SpanReader reader,
        string type,
        int depth,
        bool sandbox,
        Utf8JsonWriter? writer)
    {
        MpType resolved = _schema.ResolveNamed(type);
        MpMember[] members = resolved.MemorypackWire
            ?? throw new MemoryPackTypeException(
                $"Missing memorypack_wire for {type} (nested ReadValue / TFormatter).");
        int origin = reader.Position;
        int? count = ReadHeader(ref reader);
        if (count == null || count == 0)
        {
            writer?.WriteNullValue();
            return;
        }

        if (count != members.Length && (count <= 0 || count > members.Length || sandbox))
        {
            throw new InvalidDataException(
                $"memberCount {count} at offset {origin} does not match memorypack_wire {members.Length} of {ShortName(resolved)}.");
        }

        int usedCount = count.Value;
        TypePlan[] memberPlans = GetMemoryPackMemberPlans(resolved);
        writer?.WriteStartObject();
        for (int i = 0; i < usedCount; i++)
        {
            MpMember member = members[i];
            writer?.WritePropertyName(member.Name);
            WriteValue(
                ref reader, member.Type, memberPlans[i], member.Name, depth + 1, sandbox, member.CollectionEncoding, writer);
        }

        writer?.WriteEndObject();
    }

    private static int? ReadHeader(ref SpanReader reader)
    {
        byte b = reader.ReadByte();
        if (b == 255)
            return null;
        if (b == 250)
            return reader.ReadInt32();
        return b;
    }

    private static int? ReadUnionTag(ref SpanReader reader)
    {
        byte b = reader.ReadByte();
        if (b == 255)
            return null;
        if (b == 250)
            return reader.ReadUInt16();
        return b;
    }

    private static void WriteSingle(Utf8JsonWriter writer, float value)
    {
        if (float.IsNaN(value))
            writer.WriteStringValue("NaN");
        else if (float.IsPositiveInfinity(value))
            writer.WriteStringValue("Infinity");
        else if (float.IsNegativeInfinity(value))
            writer.WriteStringValue("-Infinity");
        else
            writer.WriteNumberValue(value);
    }

    private static void WriteDouble(Utf8JsonWriter writer, double value)
    {
        if (double.IsNaN(value))
            writer.WriteStringValue("NaN");
        else if (double.IsPositiveInfinity(value))
            writer.WriteStringValue("Infinity");
        else if (double.IsNegativeInfinity(value))
            writer.WriteStringValue("-Infinity");
        else
            writer.WriteNumberValue(value);
    }

    private static void WriteVec(Utf8JsonWriter? writer, float x, float y)
    {
        if (writer == null)
            return;
        writer.WriteStartArray();
        WriteSingle(writer, x);
        WriteSingle(writer, y);
        writer.WriteEndArray();
    }

    private static void WriteVec(Utf8JsonWriter? writer, float x, float y, float z)
    {
        if (writer == null)
            return;
        writer.WriteStartArray();
        WriteSingle(writer, x);
        WriteSingle(writer, y);
        WriteSingle(writer, z);
        writer.WriteEndArray();
    }

    private static void WriteVec(Utf8JsonWriter? writer, float x, float y, float z, float w)
    {
        if (writer == null)
            return;
        writer.WriteStartArray();
        WriteSingle(writer, x);
        WriteSingle(writer, y);
        WriteSingle(writer, z);
        WriteSingle(writer, w);
        writer.WriteEndArray();
    }

    private static void WriteVecInt(Utf8JsonWriter? writer, int x, int y)
    {
        if (writer == null)
            return;
        writer.WriteStartArray();
        writer.WriteNumberValue(x);
        writer.WriteNumberValue(y);
        writer.WriteEndArray();
    }

    private static void WriteVecInt(Utf8JsonWriter? writer, int x, int y, int z)
    {
        if (writer == null)
            return;
        writer.WriteStartArray();
        writer.WriteNumberValue(x);
        writer.WriteNumberValue(y);
        writer.WriteNumberValue(z);
        writer.WriteEndArray();
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

    private enum UnmanagedWrapperKind : byte
    {
        None,
        Nullable,
        Optional,
    }

    private abstract class TypePlan;

    private sealed class OptionalPlan(
        MpUnmanagedLayout? unmanaged,
        MpType? resolvedObject,
        UnmanagedWrapperKind wrapperKind,
        int flagFieldIndex,
        int valueFieldIndex) : TypePlan
    {
        public MpUnmanagedLayout? Unmanaged { get; } = unmanaged;
        public MpType? ResolvedObject { get; } = resolvedObject;
        public UnmanagedWrapperKind WrapperKind { get; } = wrapperKind;
        public int FlagFieldIndex { get; } = flagFieldIndex;
        public int ValueFieldIndex { get; } = valueFieldIndex;
    }

    private sealed class UnmanagedPlan(
        MpUnmanagedLayout layout,
        MpType? formatterType,
        UnmanagedWrapperKind wrapperKind,
        int flagFieldIndex,
        int valueFieldIndex) : TypePlan
    {
        public MpUnmanagedLayout Layout { get; } = layout;
        public MpType? FormatterType { get; } = formatterType;
        public UnmanagedWrapperKind WrapperKind { get; } = wrapperKind;
        public int FlagFieldIndex { get; } = flagFieldIndex;
        public int ValueFieldIndex { get; } = valueFieldIndex;
    }

    private sealed class TaggedUnionPlan(Dictionary<int, string>? directUnionMap) : TypePlan
    {
        public Dictionary<int, string>? DirectUnionMap { get; } = directUnionMap;
    }

    private sealed class PolymorphicPlan(MpType[] derived) : TypePlan
    {
        public MpType[] Derived { get; } = derived;
    }

    private sealed class EnumPlan(byte width, byte leafWidth) : TypePlan
    {
        public byte Width { get; } = width;
        public byte LeafWidth { get; } = leafWidth;
    }

    private sealed class ListPlan(
        string elementType,
        MpUnmanagedLayout? elementUnmanaged,
        MpType? elementFormatterType) : TypePlan
    {
        private TypePlan? _elementPlan;
        public string ElementType { get; } = elementType;
        public MpUnmanagedLayout? ElementUnmanaged { get; } = elementUnmanaged;
        public MpType? ElementFormatterType { get; } = elementFormatterType;

        public TypePlan GetElementPlan(MemoryPackDecoder decoder)
        {
            TypePlan? plan = Volatile.Read(ref _elementPlan);
            if (plan != null)
                return plan;
            plan = decoder.GetPlan(ElementType);
            Volatile.Write(ref _elementPlan, plan);
            return plan;
        }
    }

    private sealed class DictionaryPlan(
        bool IsV2,
        string KeyType,
        string ValueType,
        bool ObjectKeys,
        bool PadPairs,
        int MidPad,
        int EndPad,
        bool ValueIsObject,
        MpType? ValueObject,
        MpUnmanagedLayout? ValueUnmanaged,
        MpType? ValueFormatterType) : TypePlan
    {
        private TypePlan? _keyPlan;
        private TypePlan? _valuePlan;
        public bool IsV2 { get; } = IsV2;
        public string KeyType { get; } = KeyType;
        public string ValueType { get; } = ValueType;
        public bool ObjectKeys { get; } = ObjectKeys;
        public bool PadPairs { get; } = PadPairs;
        public int MidPad { get; } = MidPad;
        public int EndPad { get; } = EndPad;
        public bool ValueIsObject { get; } = ValueIsObject;
        public MpType? ValueObject { get; } = ValueObject;
        public MpUnmanagedLayout? ValueUnmanaged { get; } = ValueUnmanaged;
        public MpType? ValueFormatterType { get; } = ValueFormatterType;

        public TypePlan GetKeyPlan(MemoryPackDecoder decoder)
        {
            TypePlan? plan = Volatile.Read(ref _keyPlan);
            if (plan != null)
                return plan;
            plan = decoder.GetPlan(KeyType);
            Volatile.Write(ref _keyPlan, plan);
            return plan;
        }

        public TypePlan GetValuePlan(MemoryPackDecoder decoder)
        {
            TypePlan? plan = Volatile.Read(ref _valuePlan);
            if (plan != null)
                return plan;
            plan = decoder.GetPlan(ValueType);
            Volatile.Write(ref _valuePlan, plan);
            return plan;
        }
    }

    private sealed class NullablePlan(string elementType) : TypePlan
    {
        private TypePlan? _elementPlan;
        public string ElementType { get; } = elementType;

        public TypePlan GetElementPlan(MemoryPackDecoder decoder)
        {
            TypePlan? plan = Volatile.Read(ref _elementPlan);
            if (plan != null)
                return plan;
            plan = decoder.GetPlan(ElementType);
            Volatile.Write(ref _elementPlan, plan);
            return plan;
        }
    }

    private sealed class ObjectPlan(MpType? resolvedObject, string? error) : TypePlan
    {
        public MpType? ResolvedObject { get; } = resolvedObject;
        public string? Error { get; } = error;
    }

    private sealed class PrimitivePlan(MpKind kind) : TypePlan
    {
        public MpKind Kind { get; } = kind;
    }

    private sealed class ErrorPlan(string? message) : TypePlan
    {
        public string? Message { get; } = message;
    }
}
