using System.Text.Json;
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
            root =>
            {
                ReadTypeIndexJson(root, kinds, byBasename, markers);
                return null;
            },
            (ref MessagePackReader reader) =>
            {
                ReadTypeIndexMsgPack(ref reader, kinds, byBasename, markers);
                return null;
            });

        var byTarget = new Dictionary<string, List<MpType>>(StringComparer.Ordinal);
        var byWrapper = new Dictionary<string, MpType>(StringComparer.Ordinal);
        SchemaFile.Parse<object?>(
            schemasData,
            root =>
            {
                ReadSchemasJson(root, kinds, markers, byTarget, byWrapper);
                return null;
            },
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
                root =>
                {
                    ApplyEnumsJson(enumWidths, root);
                    return null;
                },
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
                root =>
                {
                    ApplyUnmanagedJson(unmanaged, root);
                    return null;
                },
                (ref MessagePackReader reader) =>
                {
                    ApplyUnmanagedMsgPack(ref reader, unmanaged);
                    return null;
                });
        }

        return new MemoryPackSchema(byTarget, byWrapper, byBasename, kinds, markers, enumWidths, unmanaged);
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
        ReadTypeIndexJson(indexDoc.RootElement, kinds, byBasename, markers);

        var byTarget = new Dictionary<string, List<MpType>>(StringComparer.Ordinal);
        var byWrapper = new Dictionary<string, MpType>(StringComparer.Ordinal);
        ReadSchemasJson(schemaDoc.RootElement, kinds, markers, byTarget, byWrapper);

        var enumWidths = new Dictionary<string, byte>(StringComparer.Ordinal);
        var unmanaged = new Dictionary<string, MpUnmanagedLayout>(StringComparer.Ordinal);
        if (enumsDoc != null)
            ApplyEnumsJson(enumWidths, enumsDoc.RootElement);
        if (unmanagedDoc != null)
            ApplyUnmanagedJson(unmanaged, unmanagedDoc.RootElement);

        return new MemoryPackSchema(byTarget, byWrapper, byBasename, kinds, markers, enumWidths, unmanaged);
    }

    private static void ReadTypeIndexJson(
        JsonElement root,
        Dictionary<string, string> kinds,
        Dictionary<string, List<string>> byBasename,
        HashSet<string> markers)
    {
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

    private static void ReadSchemasJson(
        JsonElement root,
        Dictionary<string, string> kinds,
        HashSet<string> markers,
        Dictionary<string, List<MpType>> byTarget,
        Dictionary<string, MpType> byWrapper)
    {
        if (!root.TryGetProperty("wrappers", out var wrappers))
            throw new InvalidDataException("schemas export has no wrappers array.");

        foreach (var wrapper in wrappers.EnumerateArray())
        {
            string target = Required(wrapper, "target_type");
            string wrapperType = Required(wrapper, "wrapper_type");
            bool hasDeserialize = wrapper.TryGetProperty("has_deserialize", out var deser) && deser.ValueKind == JsonValueKind.True;
            var wire = ReadMembersJson(wrapper, "wire_order", kinds);
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
                MinFieldOffset = minOffset,
                SerializeMarker = markers.Contains(target),
                ParentType = parentType,
                MemorypackWire = ReadMembersJson(wrapper, "memorypack_wire", kinds) is { Length: > 0 } mp
                    ? mp
                    : null,
            };
            AddWrapper(byTarget, byWrapper, typeObj);
        }
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
                int? minOffset = null;
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
                    else if (prop.SequenceEqual("members_from_target_fields"u8))
                        minOffset = ReadMinOffsetMsgPack(ref reader);
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
                    MinFieldOffset = minOffset,
                    SerializeMarker = markers.Contains(target),
                    ParentType = parentType,
                    MemorypackWire = memorypackWire,
                });
            }
        }

        if (!foundWrappers)
            throw new InvalidDataException("schemas export has no wrappers array.");
    }

    private static int? ReadMinOffsetMsgPack(ref MessagePackReader reader)
    {
        if (reader.NextMessagePackType != MessagePackType.Array)
        {
            reader.Skip();
            return null;
        }

        int? minOffset = null;
        int count = reader.ReadArrayHeader();
        for (int i = 0; i < count; i++)
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

                if (prop.SequenceEqual("offset"u8) && SchemaFile.TryReadInt32(ref reader, out int offset))
                    minOffset = minOffset is int current ? Math.Min(current, offset) : offset;
                else if (!prop.SequenceEqual("offset"u8))
                    reader.Skip();
            }
        }

        return minOffset;
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
                CollectionEncoding = encoding,
            });
        }

        return [.. members];
    }

    private static MpMember[] ReadMembersJson(JsonElement wrapper, string property, Dictionary<string, string> kinds)
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
                Kind = MemoryPackSchema.Classify(type, kinds),
                CollectionEncoding = encoding,
            });
        }

        return members.ToArray();
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

    private static void ApplyEnumsJson(Dictionary<string, byte> enumWidths, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in root.EnumerateArray())
        {
            string? name = item.TryGetProperty("type_name", out var nameEl) ? nameEl.GetString() : null;
            int? width = item.TryGetProperty("width", out var widthEl) && widthEl.TryGetInt32(out int w) ? w : null;
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

    private static void ApplyUnmanagedJson(Dictionary<string, MpUnmanagedLayout> unmanaged, JsonElement root)
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
            AddUnmanagedLayout(unmanaged, name, payload, fields, encoding);
        }
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
            Fields = fields?.ToArray() ?? [],
            CollectionEncoding = encoding,
        };
        unmanaged[name] = layout;
        if (MemoryPackSchema.UnmanagedShortName(name) is string shortName)
            unmanaged.TryAdd(shortName, layout);
    }

    private static string Required(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.GetString() is not { } text)
            throw new InvalidDataException($"schemas.json wrapper is missing {name}.");
        return text;
    }
}
