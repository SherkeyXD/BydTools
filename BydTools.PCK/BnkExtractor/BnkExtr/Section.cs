using System.IO;
using System.Text;

namespace BnkExtractor.BnkExtr
{
    public class Section : IReadable
    {
        /// <summary>
        /// 4 ascii characters
        /// </summary>
        public string sign;
        public uint size;

        public int GetByteSize() => 8;

        public void Read(BinaryReader reader)
        {
            StringBuilder sb = new StringBuilder(4);
            sb.Append((char)reader.ReadByte());
            sb.Append((char)reader.ReadByte());
            sb.Append((char)reader.ReadByte());
            sb.Append((char)reader.ReadByte());
            sign = sb.ToString();
            size = reader.ReadUInt32();
        }
    }
}
