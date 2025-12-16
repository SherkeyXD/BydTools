namespace BnkExtractor.BnkExtr
{
    public class Index : IReadable
    {
        public uint id;
        public uint offset;
        public uint size;

        public static int GetDataSize() => 12;

        public int GetByteSize() => 12;

        public void Read(BinaryReader reader)
        {
            id = reader.ReadUInt32();
            offset = reader.ReadUInt32();
            size = reader.ReadUInt32();
        }
    }
}
