using MessagePack;

namespace BydTools.Formats.MemoryPack;

internal static class MemoryPackSchemaLoader
{
    public static MemoryPackSchema Load(string schemasPath, string typeIndexPath)
    {
        if (!File.Exists(schemasPath))
            throw new FileNotFoundException("schemas.json / schemas.msgpack.lz4 was not found.", schemasPath);
        if (!File.Exists(typeIndexPath))
            throw new FileNotFoundException("type_index.json / type_index.msgpack.lz4 was not found.", typeIndexPath);

        string? dir = Path.GetDirectoryName(schemasPath);
        byte[]? enumsData = null;
        byte[]? unmanagedData = null;
        if (!string.IsNullOrEmpty(dir))
        {
            if (SchemaFile.Find(dir, MemoryPackExport.EnumsStem) is string enumsPath)
                enumsData = File.ReadAllBytes(enumsPath);
            if (SchemaFile.Find(dir, MemoryPackExport.UnmanagedStem) is string unmanagedPath)
                unmanagedData = File.ReadAllBytes(unmanagedPath);
        }

        return LoadFromBytes(
            File.ReadAllBytes(schemasPath),
            File.ReadAllBytes(typeIndexPath),
            enumsData,
            unmanagedData);
    }

    public static MemoryPackSchema LoadFromBytes(
        ReadOnlyMemory<byte> schemasData,
        ReadOnlyMemory<byte> indexData,
        ReadOnlyMemory<byte> enumsData = default,
        ReadOnlyMemory<byte> unmanagedData = default)
    {
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var byBasename = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var markers = new HashSet<string>(StringComparer.Ordinal);
        SchemaFile.Parse<object?>(
            indexData,
            (ref MessagePackReader reader) =>
            {
                ReadTypeIndexMsgPack(ref reader, kinds, byBasename, markers);
                return null;
            });

        var byTarget = new Dictionary<string, List<MpType>>(StringComparer.Ordinal);
        var byWrapper = new Dictionary<string, MpType>(StringComparer.Ordinal);
        SchemaFile.Parse<object?>(
            schemasData,
            (ref MessagePackReader reader) =>
            {
                ReadSchemasMsgPack(ref reader, kinds, markers, byTarget, byWrapper);
                return null;
            });

        var enumWidths = new Dictionary<string, byte>(StringComparer.Ordinal);
        var unmanaged = new Dictionary<string, MpUnmanagedLayout>(StringComparer.Ordinal);
        if (!enumsData.IsEmpty)
        {
            SchemaFile.Parse<object?>(
                enumsData,
                (ref MessagePackReader reader) =>
                {
                    ApplyEnumsMsgPack(ref reader, enumWidths);
                    return null;
                });
        }

        if (!unmanagedData.IsEmpty)
        {
            SchemaFile.Parse<object?>(
                unmanagedData,
                (ref MessagePackReader reader) =>
                {
                    ApplyUnmanagedMsgPack(ref reader, unmanaged);
                    return null;
                });
        }

        return new MemoryPackSchema(byTarget, byWrapper, byBasename, kinds, markers, enumWidths, unmanaged);
    }

    private static void AddWrapper(
        Dictionary<string, List<MpType>> byTarget,
        Dictionary<string, MpType> byWrapper,
        MpType typeObj)
    {
        if (!byTarget.TryGetValue(typeObj.TargetType, out var list))
        {
            list = [];
            byTarget[typeObj.TargetType] = list;
        }

        list.Add(typeObj);
        byWrapper[typeObj.WrapperType] = typeObj;
    }

    private static void ReadTypeIndexMsgPack(
        ref MessagePackReader reader,
        Dictionary<string, string> kinds,
        Dictionary<string, List<string>> byBasename,
        HashSet<string> markers)
    {
        if (reader.NextMessagePackType != MessagePackType.Map)
            throw new InvalidDataException("type_index export is not a map.");

        int rootCount = reader.ReadMapHeader();
        for (int i = 0; i < rootCount; i++)
        {
            if (!SchemaFile.TryReadKeySpan(ref reader, out var key))
            {
                reader.Skip();
                continue;
            }

            if (key.SequenceEqual("by_type"u8) && reader.NextMessagePackType == MessagePackType.Map)
            {
                int typeCount = reader.ReadMapHeader();
                for (int t = 0; t < typeCount; t++)
                {
                    string? typeName = SchemaFile.ReadNullableString(ref reader);
                    if (typeName == null || reader.NextMessagePackType != MessagePackType.Map)
                    {
                        reader.Skip();
                        continue;
                    }

                    int propCount = reader.ReadMapHeader();
                    for (int p = 0; p < propCount; p++)
                    {
                        if (!SchemaFile.TryReadKeySpan(ref reader, out var prop))
                        {
                            reader.Skip();
                            continue;
                        }

                        if (prop.SequenceEqual("serialize_marker"u8))
                        {
                            if (SchemaFile.ReadBoolOrSkip(ref reader))
                                markers.Add(typeName);
                        }
                        else if (prop.SequenceEqual("entries"u8) && reader.NextMessagePackType == MessagePackType.Array)
                        {
                            ReadTypeIndexEntriesMsgPack(ref reader, kinds);
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                }
            }
            else if (key.SequenceEqual("by_basename"u8) && reader.NextMessagePackType == MessagePackType.Map)
            {
                int baseCount = reader.ReadMapHeader();
                for (int b = 0; b < baseCount; b++)
                {
                    string? basename = SchemaFile.ReadNullableString(ref reader);
                    if (basename == null || reader.NextMessagePackType != MessagePackType.Array)
                    {
                        reader.Skip();
                        continue;
                    }

                    int arrCount = reader.ReadArrayHeader();
                    var names = new List<string>(arrCount);
                    for (int a = 0; a < arrCount; a++)
                    {
                        string? name = SchemaFile.ReadNullableString(ref reader);
                        if (name != null)
                            names.Add(name);
                    }

                    if (names.Count > 0)
                        byBasename[basename] = names;
                }
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static void ReadTypeIndexEntriesMsgPack(ref MessagePackReader reader, Dictionary<string, string> kinds)
    {
        int entryCount = reader.ReadArrayHeader();
        for (int e = 0; e < entryCount; e++)
        {
            if (reader.NextMessagePackType != MessagePackType.Map)
            {
                reader.Skip();
                continue;
            }

            int propCount = reader.ReadMapHeader();
            for (int p = 0; p < propCount; p++)
            {
                if (!SchemaFile.TryReadKeySpan(ref reader, out var prop))
                {
                    reader.Skip();
                    continue;
                }

                if (prop.SequenceEqual("wire"u8) && reader.NextMessagePackType == MessagePackType.Array)
                {
                    int wireCount = reader.ReadArrayHeader();
                    for (int w = 0; w < wireCount; w++)
                    {
                        if (reader.NextMessagePackType != MessagePackType.Map)
                        {
                            reader.Skip();
                            continue;
                        }

                        string? type = null;
                        string? kind = null;
                        int memberProps = reader.ReadMapHeader();
                        for (int mp = 0; mp < memberProps; mp++)
                        {
                            if (!SchemaFile.TryReadKeySpan(ref reader, out var mKey))
                            {
                                reader.Skip();
                                continue;
                            }

                            if (mKey.SequenceEqual("type"u8))
                                type = SchemaFile.ReadNullableString(ref reader);
                            else if (mKey.SequenceEqual("kind"u8))
                                kind = SchemaFile.ReadNullableString(ref reader);
                            else
                                reader.Skip();
                        }

                        if (type != null && kind != null)
                            kinds.TryAdd(type, kind);
                    }
                }
                else
                {
                    reader.Skip();
                }
            }
        }
    }

    private static void ReadSchemasMsgPack(
        ref MessagePackReader reader,
        Dictionary<string, string> kinds,
        HashSet<string> markers,
        Dictionary<string, List<MpType>> byTarget,
        Dictionary<string, MpType> byWrapper)
    {
        if (reader.NextMessagePackType != MessagePackType.Map)
            throw new InvalidDataException("schemas export is not a map.");

        bool foundWrappers = false;
        int rootCount = reader.ReadMapHeader();
        for (int i = 0; i < rootCount; i++)
        {
            if (!SchemaFile.TryReadKeySpan(ref reader, out var key))
            {
                reader.Skip();
                continue;
            }

            if (!key.SequenceEqual("wrappers"u8) || reader.NextMessagePackType != MessagePackType.Array)
            {
                reader.Skip();
                continue;
            }

            foundWrappers = true;
            int wrapperCount = reader.ReadArrayHeader();
            for (int w = 0; w < wrapperCount; w++)
            {
                if (reader.NextMessagePackType != MessagePackType.Map)
                {
                    reader.Skip();
                    continue;
                }

                string? target = null;
                string? wrapperType = null;
                bool hasDeserialize = false;
                MpMember[] wire = [];
                string? parentType = null;
                MpMember[]? memorypackWire = null;

                int propCount = reader.ReadMapHeader();
                for (int p = 0; p < propCount; p++)
                {
                    if (!SchemaFile.TryReadKeySpan(ref reader, out var prop))
                    {
                        reader.Skip();
                        continue;
                    }

                    if (prop.SequenceEqual("target_type"u8))
                        target = SchemaFile.ReadNullableString(ref reader);
                    else if (prop.SequenceEqual("wrapper_type"u8))
                        wrapperType = SchemaFile.ReadNullableString(ref reader);
                    else if (prop.SequenceEqual("has_deserialize"u8))
                        hasDeserialize = SchemaFile.ReadBoolOrSkip(ref reader);
                    else if (prop.SequenceEqual("wire_order"u8))
                        wire = ReadMembersMsgPack(ref reader, kinds);
                    else if (prop.SequenceEqual("parent_type"u8))
                        parentType = SchemaFile.ReadNullableString(ref reader);
                    else if (prop.SequenceEqual("memorypack_wire"u8))
                    {
                        MpMember[] mp = ReadMembersMsgPack(ref reader, kinds);
                        if (mp.Length > 0)
                            memorypackWire = mp;
                    }
                    else
                        reader.Skip();
                }

                if (target == null)
                    throw new InvalidDataException("schemas.json wrapper is missing target_type.");
                if (wrapperType == null)
                    throw new InvalidDataException("schemas.json wrapper is missing wrapper_type.");

                AddWrapper(byTarget, byWrapper, new MpType
                {
                    TargetType = target,
                    WrapperType = wrapperType,
                    HasDeserialize = hasDeserialize,
                    Wire = wire,
                    SerializeMarker = markers.Contains(target),
                    ParentType = parentType,
                    MemorypackWire = memorypackWire,
                });
            }
        }

        if (!foundWrappers)
            throw new InvalidDataException("schemas export has no wrappers array.");
    }

    private static MpMember[] ReadMembersMsgPack(ref MessagePackReader reader, Dictionary<string, string> kinds)
    {
        if (reader.NextMessagePackType != MessagePackType.Array)
        {
            reader.Skip();
            return [];
        }

        int count = reader.ReadArrayHeader();
        var members = new List<MpMember>(count);
        for (int i = 0; i < count; i++)
        {
            if (reader.NextMessagePackType != MessagePackType.Map)
            {
                reader.Skip();
                continue;
            }

            string? name = null;
            string? type = null;
            string? encoding = null;
            int propCount = reader.ReadMapHeader();
            for (int p = 0; p < propCount; p++)
            {
                if (!SchemaFile.TryReadKeySpan(ref reader, out var prop))
                {
                    reader.Skip();
                    continue;
                }

                if (prop.SequenceEqual("name"u8))
                    name = SchemaFile.ReadNullableString(ref reader);
                else if (prop.SequenceEqual("type"u8))
                    type = SchemaFile.ReadNullableString(ref reader);
                else if (prop.SequenceEqual("collection_encoding"u8))
                    encoding = SchemaFile.ReadNullableString(ref reader);
                else
                    reader.Skip();
            }

            if (name == null || type == null)
                continue;

            members.Add(new MpMember
            {
                Name = name,
                Type = type,
                Kind = MemoryPackSchema.Classify(type, kinds),
                CollectionEncoding = MpCollectionEncodingParser.Parse(encoding),
            });
        }

        return [.. members];
    }

    private static void ApplyEnumsMsgPack(ref MessagePackReader reader, Dictionary<string, byte> enumWidths)
    {
        if (reader.NextMessagePackType != MessagePackType.Array)
        {
            reader.Skip();
            return;
        }

        int count = reader.ReadArrayHeader();
        for (int i = 0; i < count; i++)
        {
            if (reader.NextMessagePackType != MessagePackType.Map)
            {
                reader.Skip();
                continue;
            }

            string? name = null;
            int? width = null;
            int propCount = reader.ReadMapHeader();
            for (int p = 0; p < propCount; p++)
            {
                if (!SchemaFile.TryReadKeySpan(ref reader, out var prop))
                {
                    reader.Skip();
                    continue;
                }

                if (prop.SequenceEqual("type_name"u8))
                    name = SchemaFile.ReadNullableString(ref reader);
                else if (prop.SequenceEqual("width"u8) && SchemaFile.TryReadInt32(ref reader, out int w))
                    width = w;
                else if (!prop.SequenceEqual("width"u8))
                    reader.Skip();
            }

            AddEnumWidth(enumWidths, name, width);
        }
    }

    private static void AddEnumWidth(Dictionary<string, byte> enumWidths, string? name, int? width)
    {
        if (name == null || width is not (1 or 2 or 4 or 8))
            return;
        byte w = (byte)width.Value;
        enumWidths[name] = w;
        int sep = name.LastIndexOfAny(['.', '+']);
        if (sep >= 0)
            enumWidths.TryAdd(name[(sep + 1)..], w);
    }

    private static void ApplyUnmanagedMsgPack(ref MessagePackReader reader, Dictionary<string, MpUnmanagedLayout> unmanaged)
    {
        if (reader.NextMessagePackType != MessagePackType.Array)
        {
            reader.Skip();
            return;
        }

        int count = reader.ReadArrayHeader();
        for (int i = 0; i < count; i++)
        {
            if (reader.NextMessagePackType != MessagePackType.Map)
            {
                reader.Skip();
                continue;
            }

            string? name = null;
            int payload = 0;
            string? encoding = null;
            List<MpUnmanagedField>? fields = null;
            int propCount = reader.ReadMapHeader();
            for (int p = 0; p < propCount; p++)
            {
                if (!SchemaFile.TryReadKeySpan(ref reader, out var prop))
                {
                    reader.Skip();
                    continue;
                }

                if (prop.SequenceEqual("type_name"u8))
                    name = SchemaFile.ReadNullableString(ref reader);
                else if (prop.SequenceEqual("payload_size"u8))
                    SchemaFile.TryReadInt32(ref reader, out payload);
                else if (prop.SequenceEqual("collection_encoding"u8))
                    encoding = SchemaFile.ReadNullableString(ref reader);
                else if (prop.SequenceEqual("fields"u8))
                    fields = ReadUnmanagedFieldsMsgPack(ref reader);
                else
                    reader.Skip();
            }

            AddUnmanagedLayout(unmanaged, name, payload, fields, encoding);
        }
    }

    private static List<MpUnmanagedField>? ReadUnmanagedFieldsMsgPack(ref MessagePackReader reader)
    {
        if (reader.NextMessagePackType != MessagePackType.Array)
        {
            reader.Skip();
            return null;
        }

        int count = reader.ReadArrayHeader();
        var fields = new List<MpUnmanagedField>(count);
        for (int i = 0; i < count; i++)
        {
            if (reader.NextMessagePackType != MessagePackType.Map)
            {
                reader.Skip();
                continue;
            }

            string? fname = null;
            string? fty = null;
            int? offset = null;
            int? size = null;
            int propCount = reader.ReadMapHeader();
            for (int p = 0; p < propCount; p++)
            {
                if (!SchemaFile.TryReadKeySpan(ref reader, out var prop))
                {
                    reader.Skip();
                    continue;
                }

                if (prop.SequenceEqual("name"u8))
                    fname = SchemaFile.ReadNullableString(ref reader);
                else if (prop.SequenceEqual("type"u8))
                    fty = SchemaFile.ReadNullableString(ref reader);
                else if (prop.SequenceEqual("offset"u8) && SchemaFile.TryReadInt32(ref reader, out int off))
                    offset = off;
                else if (prop.SequenceEqual("size"u8) && SchemaFile.TryReadInt32(ref reader, out int sz))
                    size = sz;
                else if (!prop.SequenceEqual("offset"u8) && !prop.SequenceEqual("size"u8))
                    reader.Skip();
            }

            if (fname != null && fty != null && offset != null)
            {
                fields.Add(new MpUnmanagedField
                {
                    Name = fname,
                    Type = fty,
                    Offset = offset.Value,
                    Size = size,
                });
            }
        }

        return fields;
    }

    private static void AddUnmanagedLayout(
        Dictionary<string, MpUnmanagedLayout> unmanaged,
        string? name,
        int payload,
        List<MpUnmanagedField>? fields,
        string? encoding)
    {
        if (name == null || payload <= 0)
            return;

        var layout = new MpUnmanagedLayout
        {
            TypeName = name,
            PayloadSize = payload,
            Fields = fields?.OrderBy(static f => f.Offset).ToArray() ?? [],
            CollectionEncoding = MpCollectionEncodingParser.Parse(encoding),
        };
        unmanaged[name] = layout;
        if (MemoryPackSchema.UnmanagedShortName(name) is string shortName)
            unmanaged.TryAdd(shortName, layout);
    }
}
