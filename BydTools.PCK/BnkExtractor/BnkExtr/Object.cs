namespace BnkExtractor.BnkExtr
{
    public class Object : IReadable
    {
        public ObjectType type;
        public uint size;
        public uint id;

        /// <summary>
        /// Suspiciously 9
        /// </summary>
        public int GetByteSize() => 9;

        public void Read(BinaryReader reader)
        {
            type = (ObjectType)reader.ReadSByte();
            reader.ReadBytes(3);
            size = reader.ReadUInt32();
            id = reader.ReadUInt32();
        }
    }
}
