using System.Text.Json;

namespace BydTools.Utils.SparkBuffer
{
    public static class SparkManager
    {
        public static readonly JsonSerializerOptions jsonSerializerOptions = new() 
        { 
            WriteIndented = true
        };

        private static readonly Dictionary<int, BeanType> beanTypeMap = [];
        private static readonly Dictionary<int, EnumType> enumTypeMap = [];

        /// <summary>
        /// Clears all type definitions. Should be called before reading a new SparkBuffer file.
        /// </summary>
        public static void ClearTypeDefinitions()
        {
            beanTypeMap.Clear();
            enumTypeMap.Clear();
        }

        public static BeanType BeanTypeFromHash(int hash)
            => beanTypeMap[hash];

        public static EnumType EnumTypeFromHash(int hash)
            => enumTypeMap[hash];

        public static void ReadTypeDefinitions(BinaryReader reader)
        {
            var typeDefCount = reader.ReadInt32();
            while (typeDefCount-- > 0)
            {
                var sparkType = reader.ReadSparkType();
                reader.Align4Bytes();

                switch (sparkType)
                {
                    case SparkType.Enum:
                    {
                        var enumType = new EnumType(reader);
                        enumTypeMap.TryAdd(enumType.typeHash, enumType);
                        break;
                    }
                    case SparkType.Bean:
                    {
                        var beanType = new BeanType(reader);
                        beanTypeMap.TryAdd(beanType.typeHash, beanType);
                        break;
                    }
                    default:
#pragma warning disable CA2208
                        throw new ArgumentOutOfRangeException(nameof(sparkType), sparkType.ToString(), "Invalid spark type on type definition section");
#pragma warning restore CA2208
                }
            }
        }
    }
}

