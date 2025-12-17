namespace BydTools.Utils.SparkBuffer
{
    public struct EnumType
    {
        public int typeHash;
        public string name;
        public EnumItem[] enums;

        public EnumType(BinaryReader reader)
        {
            typeHash = reader.ReadInt32();
            name = reader.ReadSparkBufferString();
            reader.Align4Bytes();
            var enumCount = reader.ReadInt32();
            enums = new EnumItem[enumCount];

            foreach (ref var enumItem in enums.AsSpan())
            {
                enumItem.name = reader.ReadSparkBufferString();
                reader.Align4Bytes();
                enumItem.value = reader.ReadInt32();
            }
        }

        public struct EnumItem
        {
            public string name;
            public int value;
        }
    }
}
