using System.Text;

namespace BydTools.VFS.SparkBuffer
{
    public static class BinaryReaderExtensions
    {
        public static SparkType ReadSparkType(this BinaryReader reader) =>
            (SparkType)reader.ReadByte();

        public static long Seek(
            this BinaryReader reader,
            long pos,
            SeekOrigin seekOrigin = SeekOrigin.Begin
        ) => reader.BaseStream.Seek(pos, seekOrigin);

        public static string ReadSparkBufferString(this BinaryReader reader)
        {
            using MemoryStream buffer = new();
            while (true)
            {
                byte b = reader.ReadByte();
                if (b == 0)
                    break;
                buffer.WriteByte(b);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        public static string ReadSparkBufferStringOffset(this BinaryReader reader)
        {
            var stringOffset = reader.ReadInt32();
            if (stringOffset == -1)
                return string.Empty;

            var oldPosition = reader.BaseStream.Position;

            reader.Seek(stringOffset);
            var tmp = reader.ReadSparkBufferString();

            reader.BaseStream.Position = oldPosition;
            return tmp;
        }

        public static string ReadSparkBufferEnum(this BinaryReader reader, EnumType type)
        {
            var value = reader.ReadInt32();
            return type
                .enums.FirstOrDefault(
                    x => x.value == value,
                    new EnumType.EnumItem { name = value.ToString(), value = value }
                )
                .name;
        }

        public static bool ReadSparkBufferBool(this BinaryReader reader)
        {
            var value = reader.ReadBoolean();
            reader.Align4Bytes();
            return value;
        }

        public static long ReadSparkBufferLong(this BinaryReader reader)
        {
            reader.Align8Bytes();
            return reader.ReadInt64();
        }

        public static double ReadSparkBufferDouble(this BinaryReader reader)
        {
            reader.Align8Bytes();
            return reader.ReadDouble();
        }

        public static void Align4Bytes(this BinaryReader reader) =>
            reader.Seek(
                (reader.BaseStream.Position - 1) + (4 - ((reader.BaseStream.Position - 1) % 4))
            );

        public static void Align8Bytes(this BinaryReader reader) =>
            reader.Seek(
                (reader.BaseStream.Position - 1) + (8 - ((reader.BaseStream.Position - 1) % 8))
            );
    }
}
