using System.Buffers.Binary;
using System.Text;

namespace BydTools.Audio.Bnk;

public readonly record struct BnkWem(uint Id, int Offset, int Size);

public static class BnkParser
{
    public static List<BnkWem> Parse(ReadOnlySpan<byte> data)
    {
        int dataOffset = 0;
        var entries = new List<BnkWem>();
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            string sign = Encoding.ASCII.GetString(data.Slice(pos, 4));
            uint sectionSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4, 4));
            int sectionStart = pos + 8;
            if (sectionSize > data.Length - sectionStart)
                throw new InvalidDataException($"BNK section {sign} exceeds the file.");

            if (sign == "DIDX")
            {
                int count = (int)(sectionSize / 12);
                for (int i = 0; i < count; i++)
                {
                    int at = sectionStart + i * 12;
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at, 4));
                    uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at + 4, 4));
                    uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at + 8, 4));
                    entries.Add(new BnkWem(id, (int)offset, (int)size));
                }
            }
            else if (sign == "DATA")
            {
                dataOffset = sectionStart;
            }

            pos = sectionStart + (int)sectionSize;
        }

        if (dataOffset == 0 || entries.Count == 0)
            return [];

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            int absolute = entry.Offset + dataOffset;
            if (entry.Size < 0 || absolute < 0 || absolute > data.Length - entry.Size)
                throw new InvalidDataException($"BNK WEM {entry.Id} is outside the DATA section.");
            entries[i] = entry with { Offset = absolute };
        }

        return entries;
    }
}
