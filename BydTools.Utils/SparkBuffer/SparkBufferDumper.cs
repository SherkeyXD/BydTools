using System.Text.Json.Nodes;

namespace BydTools.Utils.SparkBuffer
{
    /// <summary>
    /// Provides functionality to dump SparkBuffer binary files to JSON format.
    /// </summary>
    public class SparkBufferDumper
    {
        /// <summary>
        /// Decrypts SparkBuffer data from bytes and returns string representation.
        /// The output format depends on the SparkBuffer content structure (JSON format).
        /// </summary>
        /// <param name="data">The SparkBuffer binary data.</param>
        /// <returns>String representation of the SparkBuffer data.</returns>
        public static string Decrypt(byte[] data)
        {
            using var memoryStream = new MemoryStream(data);
            using var binaryReader = new BinaryReader(memoryStream);
            return Decrypt(binaryReader);
        }

        /// <summary>
        /// Decrypts SparkBuffer data from BinaryReader and returns string representation.
        /// The output format depends on the SparkBuffer content structure (JSON format).
        /// </summary>
        /// <param name="binaryReader">BinaryReader positioned at the start of the SparkBuffer file.</param>
        /// <returns>String representation of the SparkBuffer data.</returns>
        public static string Decrypt(BinaryReader binaryReader)
        {
            // Clear previous type definitions to avoid conflicts
            SparkManager.ClearTypeDefinitions();
            
            var typeDefOffset = binaryReader.ReadInt32();
            var rootDefOffset = binaryReader.ReadInt32();
            var dataOffset = binaryReader.ReadInt32();

            binaryReader.Seek(typeDefOffset);
            SparkManager.ReadTypeDefinitions(binaryReader);

            binaryReader.Seek(rootDefOffset);
            var rootDef = new BeanType.Field
            {
                type = binaryReader.ReadSparkType(),
                name = binaryReader.ReadSparkBufferString()
            };

            if (rootDef.type.IsEnumOrBeanType())
            {
                binaryReader.Align4Bytes();
                rootDef.typeHash = binaryReader.ReadInt32();
            }
            if (rootDef.type == SparkType.Map)
            {
                rootDef.type2 = binaryReader.ReadSparkType();
                rootDef.type3 = binaryReader.ReadSparkType();

                if (rootDef.type2.Value.IsEnumOrBeanType())
                {
                    binaryReader.Align4Bytes();
                    rootDef.typeHash = binaryReader.ReadInt32();
                }
                if (rootDef.type3.Value.IsEnumOrBeanType())
                {
                    binaryReader.Align4Bytes();
                    rootDef.typeHash2 = binaryReader.ReadInt32();
                }
            }

            binaryReader.Seek(dataOffset);
            switch (rootDef.type)
            {
                case SparkType.Bean:
                    var rootBeanType = SparkManager.BeanTypeFromHash((int)rootDef.typeHash!);
                    var beanDump = ReadBeanAsJObject(binaryReader, rootBeanType);
                    return beanDump!.ToJsonString(SparkManager.jsonSerializerOptions);
                case SparkType.Map:
                    var mapDump = ReadMapAsJObject(binaryReader, rootDef);
                    return mapDump!.ToJsonString(SparkManager.jsonSerializerOptions);
                default:
                    throw new NotSupportedException(string.Format("Unsupported root type {0}", rootDef.type));
            }
        }

        /// <summary>
        /// Reads the root definition name from a SparkBuffer without fully parsing it.
        /// </summary>
        /// <param name="binaryReader">BinaryReader positioned at the start of the SparkBuffer file.</param>
        /// <returns>The name of the root definition.</returns>
        public static string GetRootDefinitionName(BinaryReader binaryReader)
        {
            var originalPosition = binaryReader.BaseStream.Position;
            
            // Read offsets
            binaryReader.ReadInt32(); // typeDefOffset
            var rootDefOffset = binaryReader.ReadInt32();
            
            // Read root definition to get the name
            binaryReader.Seek(rootDefOffset);
            binaryReader.ReadSparkType(); // type
            var name = binaryReader.ReadSparkBufferString();
            
            // Reset to original position
            binaryReader.BaseStream.Position = originalPosition;
            
            return name;
        }

        private static JsonObject? ReadMapAsJObject(BinaryReader binaryReader, BeanType.Field typeDef)
        {
            var mapDump = new JsonObject();
            var kvCount = binaryReader.ReadInt32();
            binaryReader.Seek(kvCount * 8, SeekOrigin.Current);

            for (int i = 0; i < kvCount; i++)
            {
                var key = typeDef.type2 switch
                {
                    SparkType.String => binaryReader.ReadSparkBufferStringOffset(),
                    SparkType.Int => binaryReader.ReadInt32().ToString(),
                    SparkType.Long => binaryReader.ReadSparkBufferLong().ToString(),
                    _ => throw new NotSupportedException(string.Format("Unsupported map key type {0}", typeDef.type2)),
                };
                mapDump[key] = null;


                mapDump[key] = typeDef.type3 switch
                {
                    SparkType.Bean => ReadBeanAsJObject(binaryReader, SparkManager.BeanTypeFromHash((int)typeDef.typeHash2!), true),
                    SparkType.String => binaryReader.ReadSparkBufferStringOffset(),
                    SparkType.Int => binaryReader.ReadInt32(),
                    SparkType.Float => binaryReader.ReadSingle(),
                    SparkType.Enum => binaryReader.ReadSparkBufferEnum(SparkManager.EnumTypeFromHash(typeDef.typeHash2!.Value)),
                    SparkType.Bool => binaryReader.ReadSparkBufferBool(),
                    _ => throw new NotSupportedException(string.Format("Unsupported map value type {0}", typeDef.type3)),
                };
            }

            return mapDump;
        }

        private static JsonObject? ReadBeanAsJObject(BinaryReader binaryReader, BeanType beanType, bool pointer = false)
        {
            long? pointerOrigin = null;
            if (pointer)
            {
                var beanOffset = binaryReader.ReadInt32();
                if (beanOffset == -1)
                    return null;

                pointerOrigin = binaryReader.BaseStream.Position;
                binaryReader.Seek(beanOffset);
            }

            var dumpObj = new JsonObject();
            
            foreach (var (fieldIndex, beanField) in beanType.fields.Index())
            {
                long? origin = null;
                if (beanField.type == SparkType.Array)
                {
                    var fieldOffset = binaryReader.ReadInt32();
                    if (fieldOffset == -1)
                    {
                        dumpObj[beanField.name] = null;
                        continue;
                    }

                    origin = binaryReader.BaseStream.Position;
                    binaryReader.Seek(fieldOffset);
                }

                switch (beanField.type)
                {
                    case SparkType.Array:
                        var jArray = new JsonArray();

                        var itemCount = binaryReader.ReadInt32();
                        while (itemCount-- > 0)
                        {
                            switch (beanField.type2)
                            {
                                case SparkType.String:
                                    jArray.Add((JsonNode?)JsonValue.Create(binaryReader.ReadSparkBufferStringOffset()));
                                    break;
                                case SparkType.Bean:
                                    jArray.Add((JsonNode?)ReadBeanAsJObject(binaryReader, SparkManager.BeanTypeFromHash((int)beanField.typeHash!), true));
                                    break;
                                case SparkType.Float:
                                    jArray.Add((JsonNode?)JsonValue.Create(binaryReader.ReadSingle()));
                                    break;
                                case SparkType.Long:
                                    jArray.Add((JsonNode?)JsonValue.Create(binaryReader.ReadSparkBufferLong()));
                                    break;
                                case SparkType.Int:
                                case SparkType.Enum:
                                    jArray.Add((JsonNode?)JsonValue.Create(binaryReader.ReadInt32()));
                                    break;
                                case SparkType.Bool:
                                    jArray.Add((JsonNode?)JsonValue.Create(binaryReader.ReadBoolean()));
                                    break;
                                case SparkType.Double:
                                    jArray.Add((JsonNode?)JsonValue.Create(binaryReader.ReadSparkBufferDouble()));
                                    break;
                                default:
                                    throw new NotSupportedException(string.Format("Unsupported array type {0} on bean array field, position: {1}", beanField.type2, binaryReader.BaseStream.Position));
                            }
                        }

                        dumpObj[beanField.name] = jArray;
                        break;
                    case SparkType.Int:
                    case SparkType.Enum:
                        dumpObj[beanField.name] = binaryReader.ReadInt32();
                        break;
                    case SparkType.Long:
                        dumpObj[beanField.name] = binaryReader.ReadSparkBufferLong();
                        break;
                    case SparkType.Float:
                        dumpObj[beanField.name] = binaryReader.ReadSingle();
                        break;
                    case SparkType.Double:
                        dumpObj[beanField.name] = binaryReader.ReadSparkBufferDouble();
                        break;
                    case SparkType.String:
                        dumpObj[beanField.name] = binaryReader.ReadSparkBufferStringOffset();
                        break;
                    case SparkType.Bean:
                        dumpObj[beanField.name] = ReadBeanAsJObject(binaryReader, SparkManager.BeanTypeFromHash((int)beanField.typeHash!), true);
                        break;
                    case SparkType.Bool:
                        dumpObj[beanField.name] = binaryReader.ReadBoolean();
                        if (beanType.fields.Length > fieldIndex + 1 && beanType.fields[fieldIndex + 1].type != SparkType.Bool)
                            binaryReader.Align4Bytes();
                        break;
                    case SparkType.Map:
                        var mapOffset = binaryReader.ReadInt32();
                        var mapOrigin = binaryReader.BaseStream.Position;
                        binaryReader.Seek(mapOffset);
                        dumpObj[beanField.name] = ReadMapAsJObject(binaryReader, beanField);
                        binaryReader.Seek(mapOrigin);
                        break;
                    case SparkType.Byte:
                        throw new Exception(string.Format("Dumping bean field type {0} isn't supported, position: {1}", beanField.type, binaryReader.BaseStream.Position));
                }

                if (origin is not null)
                    binaryReader.BaseStream.Position = (long)origin;
            }

            if (pointerOrigin is not null)
                binaryReader.BaseStream.Position = (long)pointerOrigin;

            return dumpObj;
        }
    }
}

