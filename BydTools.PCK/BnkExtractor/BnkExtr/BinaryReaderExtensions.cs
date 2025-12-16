using System.Text;

namespace BnkExtractor.BnkExtr
{
    internal static class BinaryReaderExtensions
    {
        public static string ReadStringToNull(this BinaryReader reader)
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                byte b = reader.ReadByte();
                if (b == 0)
                    break;
                else
                    sb.Append((char)b);
            }
            return sb.ToString();
        }
    }
}
