namespace BydTools.VFS.SparkBuffer
{
    public struct BeanType
    {
        public int typeHash;
        public string name;
        public Field[] fields;

        public BeanType(BinaryReader reader)
        {
            typeHash = reader.ReadInt32();
            name = reader.ReadSparkBufferString();
            reader.Align4Bytes();
            var fieldCount = reader.ReadInt32();
            fields = new Field[fieldCount];

            foreach (ref var field in fields.AsSpan())
            {
                field.name = reader.ReadSparkBufferString();
                field.type = reader.ReadSparkType();
                switch (field.type)
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
                        reader.Align4Bytes();
                        field.typeHash = reader.ReadInt32();
                        break;
                    case SparkType.Array:
                        field.type2 = reader.ReadSparkType();

                        if (field.type2.Value.IsEnumOrBeanType())
                        {
                            reader.Align4Bytes();
                            field.typeHash = reader.ReadInt32();
                        }
                        break;
                    case SparkType.Map:
                        field.type2 = reader.ReadSparkType();
                        field.type3 = reader.ReadSparkType();

                        if (field.type2.Value.IsEnumOrBeanType())
                        {
                            reader.Align4Bytes();
                            field.typeHash = reader.ReadInt32();
                        }
                        if (field.type3.Value.IsEnumOrBeanType())
                        {
                            reader.Align4Bytes();
                            field.typeHash2 = reader.ReadInt32();
                        }

                        break;
                    default:
                        throw new Exception(string.Format("Unsupported bean field type {0} at position {1}", field.type, reader.BaseStream.Position));
                }
            }
        }

        public struct Field
        {
            public string name;
            public SparkType type;

            /// <summary>
            /// <see cref="SparkType.Array"/> or <see cref="SparkType.Map"/> key type
            /// </summary>
            public SparkType? type2;

            /// <summary>
            /// <see cref="SparkType.Map"/> value type
            /// </summary>
            public SparkType? type3;

            /// <summary>
            /// <see cref="SparkType.Bean"/>, <see cref="SparkType.Enum"/>, <see cref="SparkType.Array"/>, or <see cref="SparkType.Map"/> key type hash
            /// </summary>
            public int? typeHash;

            /// <summary>
            /// <see cref="SparkType.Map"/> value type hash
            /// </summary>
            public int? typeHash2;
        }
    }
}
