using System.IO;

namespace BnkExtractor.BnkExtr
{
    public class BankHeader : IReadable
    {
        public uint version;
        public uint id;

        public static int GetDataSize() => 8;

        public int GetByteSize() => 8;

        public void Read(BinaryReader reader)
        {
            version = reader.ReadUInt32();
            id = reader.ReadUInt32();
        }
    }
}
