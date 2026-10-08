using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BydTools.Core.IO;

namespace BydTools.Formats.SparkBuffer;

public enum SparkType : byte
{
    Bool,
    Byte,
    Int,
    Long,
    Float,
    Double,
    Enum,
    String,
    Bean,
    Array,
    Map,
}

/// <summary>
/// Decodes a SparkBuffer blob to JSON. Each call owns its type table, so
/// conversions can run in parallel.
/// </summary>
public static class SparkBuffer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string GetRootName(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);
        reader.ReadInt32();
        int rootOffset = reader.ReadInt32();
        reader.Seek(rootOffset);
        reader.ReadByte();
        return reader.ReadNullTerminatedUtf8();
    }

    public static string ToJson(ReadOnlySpan<byte> data) => ReadObject(data).ToJsonString(JsonOptions);

    public static JsonObject ReadObject(ReadOnlySpan<byte> data)
    {
        var node = ReadNode(data);
        return node as JsonObject
            ?? throw new InvalidDataException("SparkBuffer root is not an object.");
    }

    private static JsonNode? ReadNode(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);
        var schema = new SparkSchema();
        int typeDefOffset = reader.ReadInt32();
        int rootDefOffset = reader.ReadInt32();
        int dataOffset = reader.ReadInt32();

        reader.Seek(typeDefOffset);
        schema.Read(ref reader);

        reader.Seek(rootDefOffset);
        var root = ReadFieldHeader(ref reader);

        reader.Seek(dataOffset);
        return root.Type switch
        {
            SparkType.Bean => ReadBean(
                ref reader,
                schema,
                schema.Bean(root.TypeHash),
                pointer: false
            ),
            SparkType.Map => ReadMap(ref reader, schema, root),
            _ => throw new NotSupportedException($"Unsupported root type {root.Type}"),
        };
    }

    private static Field ReadFieldHeader(ref SpanReader reader)
    {
        var field = new Field { Type = (SparkType)reader.ReadByte(), Name = reader.ReadNullTerminatedUtf8() };
        if (field.Type is SparkType.Enum or SparkType.Bean)
        {
            reader.Align(4);
            field.TypeHash = reader.ReadInt32();
        }

        if (field.Type == SparkType.Map)
        {
            field.Type2 = (SparkType)reader.ReadByte();
            field.Type3 = (SparkType)reader.ReadByte();
            if (field.Type2 is SparkType.Enum or SparkType.Bean)
            {
                reader.Align(4);
                field.TypeHash = reader.ReadInt32();
            }

            if (field.Type3 is SparkType.Enum or SparkType.Bean)
            {
                reader.Align(4);
                field.TypeHash2 = reader.ReadInt32();
            }
        }

        return field;
    }

    private static JsonObject ReadMap(ref SpanReader reader, SparkSchema schema, Field typeDef)
    {
        var map = new JsonObject();
        int kvCount = reader.ReadInt32();
        if (kvCount < 0)
            throw new InvalidDataException("Negative SparkBuffer map size.");
        reader.Skip(checked(kvCount * 8));

        for (int i = 0; i < kvCount; i++)
        {
            string key = typeDef.Type2 switch
            {
                SparkType.String => ReadStringOffset(ref reader),
                SparkType.Int => reader.ReadInt32().ToString(),
                SparkType.Long => ReadLong(ref reader).ToString(),
                _ => throw new NotSupportedException($"Unsupported map key type {typeDef.Type2}"),
            };

            map[key] = typeDef.Type3 switch
            {
                SparkType.Bean => ReadBean(ref reader, schema, schema.Bean(typeDef.TypeHash2), pointer: true),
                SparkType.String => ReadStringOffset(ref reader),
                SparkType.Int => reader.ReadInt32(),
                SparkType.Float => reader.ReadSingle(),
                SparkType.Enum => ReadEnumName(ref reader, schema.Enum(typeDef.TypeHash2)),
                SparkType.Bool => ReadBool(ref reader),
                _ => throw new NotSupportedException($"Unsupported map value type {typeDef.Type3}"),
            };
        }

        return map;
    }

    private static JsonObject? ReadBean(ref SpanReader reader, SparkSchema schema, BeanType bean, bool pointer)
    {
        int? pointerOrigin = null;
        if (pointer)
        {
            int beanOffset = reader.ReadInt32();
            if (beanOffset == -1)
                return null;
            pointerOrigin = reader.Position;
            reader.Seek(beanOffset);
        }

        var obj = new JsonObject();
        for (int fieldIndex = 0; fieldIndex < bean.Fields.Length; fieldIndex++)
        {
            Field beanField = bean.Fields[fieldIndex];
            int? origin = null;
            if (beanField.Type == SparkType.Array)
            {
                int fieldOffset = reader.ReadInt32();
                if (fieldOffset == -1)
                {
                    obj[beanField.Name] = null;
                    continue;
                }

                origin = reader.Position;
                reader.Seek(fieldOffset);
            }

            switch (beanField.Type)
            {
                case SparkType.Array:
                    var array = new JsonArray();
                    int itemCount = reader.ReadInt32();
                    if (itemCount < 0)
                        throw new InvalidDataException("Negative SparkBuffer array size.");
                    for (int n = 0; n < itemCount; n++)
                    {
                        array.Add(
                            beanField.Type2 switch
                            {
                                SparkType.String => JsonValue.Create(ReadStringOffset(ref reader)),
                                SparkType.Bean => ReadBean(
                                    ref reader,
                                    schema,
                                    schema.Bean(beanField.TypeHash),
                                    pointer: true
                                ),
                                SparkType.Float => JsonValue.Create(reader.ReadSingle()),
                                SparkType.Long => JsonValue.Create(ReadLong(ref reader)),
                                SparkType.Int or SparkType.Enum => JsonValue.Create(reader.ReadInt32()),
                                SparkType.Bool => JsonValue.Create(reader.ReadBoolean()),
                                SparkType.Double => JsonValue.Create(ReadDouble(ref reader)),
                                _ => throw new NotSupportedException(
                                    $"Unsupported array type {beanField.Type2} at offset {reader.Position}"
                                ),
                            }
                        );
                    }

                    obj[beanField.Name] = array;
                    break;
                case SparkType.Int:
                case SparkType.Enum:
                    obj[beanField.Name] = reader.ReadInt32();
                    break;
                case SparkType.Long:
                    obj[beanField.Name] = ReadLong(ref reader);
                    break;
                case SparkType.Float:
                    obj[beanField.Name] = reader.ReadSingle();
                    break;
                case SparkType.Double:
                    obj[beanField.Name] = ReadDouble(ref reader);
                    break;
                case SparkType.String:
                    obj[beanField.Name] = ReadStringOffset(ref reader);
                    break;
                case SparkType.Bean:
                    obj[beanField.Name] = ReadBean(
                        ref reader,
                        schema,
                        schema.Bean(beanField.TypeHash),
                        pointer: true
                    );
                    break;
                case SparkType.Bool:
                    obj[beanField.Name] = reader.ReadBoolean();
                    if (fieldIndex + 1 < bean.Fields.Length && bean.Fields[fieldIndex + 1].Type != SparkType.Bool)
                        reader.Align(4);
                    break;
                case SparkType.Map:
                    int mapOffset = reader.ReadInt32();
                    int mapOrigin = reader.Position;
                    reader.Seek(mapOffset);
                    obj[beanField.Name] = ReadMap(ref reader, schema, beanField);
                    reader.Seek(mapOrigin);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Dumping bean field type {beanField.Type} isn't supported at offset {reader.Position}"
                    );
            }

            if (origin is not null)
                reader.Seek(origin.Value);
        }

        if (pointerOrigin is not null)
            reader.Seek(pointerOrigin.Value);
        return obj;
    }

    private static string ReadStringOffset(ref SpanReader reader)
    {
        int offset = reader.ReadInt32();
        if (offset == -1)
            return string.Empty;
        int saved = reader.Position;
        reader.Seek(offset);
        string value = reader.ReadNullTerminatedUtf8();
        reader.Seek(saved);
        return value;
    }

    private static string ReadEnumName(ref SpanReader reader, EnumType type)
    {
        int value = reader.ReadInt32();
        foreach (var item in type.Items)
        {
            if (item.Value == value)
                return item.Name;
        }

        return value.ToString();
    }

    private static bool ReadBool(ref SpanReader reader)
    {
        bool value = reader.ReadBoolean();
        reader.Align(4);
        return value;
    }

    private static long ReadLong(ref SpanReader reader)
    {
        reader.Align(8);
        return reader.ReadInt64();
    }

    private static double ReadDouble(ref SpanReader reader)
    {
        reader.Align(8);
        return reader.ReadDouble();
    }

    private struct Field
    {
        public string Name;
        public SparkType Type;
        public SparkType Type2;
        public SparkType Type3;
        public int TypeHash;
        public int TypeHash2;
    }

    private sealed class BeanType
    {
        public int TypeHash;
        public string Name = "";
        public Field[] Fields = [];
    }

    private sealed class EnumType
    {
        public int TypeHash;
        public string Name = "";
        public EnumItem[] Items = [];
    }

    private readonly struct EnumItem(string name, int value)
    {
        public string Name { get; } = name;
        public int Value { get; } = value;
    }

    private sealed class SparkSchema
    {
        private readonly Dictionary<int, BeanType> _beans = [];
        private readonly Dictionary<int, EnumType> _enums = [];

        public BeanType Bean(int hash) => _beans[hash];

        public EnumType Enum(int hash) => _enums[hash];

        public void Read(ref SpanReader reader)
        {
            int count = reader.ReadInt32();
            while (count-- > 0)
            {
                var sparkType = (SparkType)reader.ReadByte();
                reader.Align(4);
                switch (sparkType)
                {
                    case SparkType.Enum:
                    {
                        var enumType = ReadEnum(ref reader);
                        _enums.TryAdd(enumType.TypeHash, enumType);
                        break;
                    }
                    case SparkType.Bean:
                    {
                        var beanType = ReadBeanType(ref reader);
                        _beans.TryAdd(beanType.TypeHash, beanType);
                        break;
                    }
                    default:
                        throw new InvalidDataException(
                            $"Invalid spark type {sparkType} in the type definition section."
                        );
                }
            }
        }

        private static EnumType ReadEnum(ref SpanReader reader)
        {
            var enumType = new EnumType { TypeHash = reader.ReadInt32(), Name = reader.ReadNullTerminatedUtf8() };
            reader.Align(4);
            int count = reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException("Negative SparkBuffer enum size.");
            enumType.Items = new EnumItem[count];
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadNullTerminatedUtf8();
                reader.Align(4);
                enumType.Items[i] = new EnumItem(name, reader.ReadInt32());
            }

            return enumType;
        }

        private static BeanType ReadBeanType(ref SpanReader reader)
        {
            var bean = new BeanType { TypeHash = reader.ReadInt32(), Name = reader.ReadNullTerminatedUtf8() };
            reader.Align(4);
            int fieldCount = reader.ReadInt32();
            if (fieldCount < 0)
                throw new InvalidDataException("Negative SparkBuffer field count.");
            bean.Fields = new Field[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                var field = new Field
                {
                    Name = reader.ReadNullTerminatedUtf8(),
                    Type = (SparkType)reader.ReadByte(),
                };
                switch (field.Type)
                {
                    case SparkType.Bool:
                    case SparkType.Byte:
                    case SparkType.Int:
                    case SparkType.Long:
                    case SparkType.Float:
                    case SparkType.Double:
                    case SparkType.String:
                        break;
                    case SparkType.Enum:
                    case SparkType.Bean:
                        reader.Align(4);
                        field.TypeHash = reader.ReadInt32();
                        break;
                    case SparkType.Array:
                        field.Type2 = (SparkType)reader.ReadByte();
                        if (field.Type2 is SparkType.Enum or SparkType.Bean)
                        {
                            reader.Align(4);
                            field.TypeHash = reader.ReadInt32();
                        }

                        break;
                    case SparkType.Map:
                        field.Type2 = (SparkType)reader.ReadByte();
                        field.Type3 = (SparkType)reader.ReadByte();
                        if (field.Type2 is SparkType.Enum or SparkType.Bean)
                        {
                            reader.Align(4);
                            field.TypeHash = reader.ReadInt32();
                        }

                        if (field.Type3 is SparkType.Enum or SparkType.Bean)
                        {
                            reader.Align(4);
                            field.TypeHash2 = reader.ReadInt32();
                        }

                        break;
                    default:
                        throw new InvalidDataException(
                            $"Unsupported bean field type {field.Type} at offset {reader.Position}"
                        );
                }

                bean.Fields[i] = field;
            }

            return bean;
        }
    }
}
