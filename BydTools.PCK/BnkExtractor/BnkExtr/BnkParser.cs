namespace BnkExtractor.BnkExtr;

/// <summary>
/// Minimal Wwise BNK parser — extracts embedded WEM file entries
/// by reading only the BKHD, DIDX, and DATA sections.
/// </summary>
public static class BnkParser
{
    /// <summary>
    /// A WEM file entry found inside a BNK.
    /// <paramref name="Offset"/> is absolute within the source BNK byte array.
    /// </summary>
    public record BnkWemEntry(uint Id, uint Offset, uint Size);

    /// <summary>
    /// Parses a BNK byte array and returns the embedded WEM entries.
    /// Scans sections in order; handles any section arrangement.
    /// </summary>
    public static List<BnkWemEntry> Parse(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data));

        long dataOffset = 0;
        var entries = new List<BnkWemEntry>();

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string sign = ReadSignature(reader);
            uint sectionSize = reader.ReadUInt32();
            long sectionStart = reader.BaseStream.Position;

            if (sign == "DIDX")
            {
                int wemCount = (int)(sectionSize / 12);
                for (int i = 0; i < wemCount; i++)
                {
                    uint id = reader.ReadUInt32();
                    uint offset = reader.ReadUInt32();
                    uint size = reader.ReadUInt32();
                    entries.Add(new BnkWemEntry(id, offset, size));
                }
            }
            else if (sign == "DATA")
            {
                dataOffset = reader.BaseStream.Position;
            }

            reader.BaseStream.Position = sectionStart + sectionSize;
        }

        if (dataOffset == 0 || entries.Count == 0)
            return [];

        for (int i = 0; i < entries.Count; i++)
            entries[i] = entries[i] with { Offset = (uint)(entries[i].Offset + dataOffset) };

        return entries;
    }

    /// <summary>
    /// Parses a BNK file on disk and writes the embedded WEM files
    /// to a subdirectory named after the BNK file.
    /// </summary>
    public static void ParseToFiles(string bnkFilePath)
    {
        byte[] data = File.ReadAllBytes(bnkFilePath);
        var entries = Parse(data);
        if (entries.Count == 0)
            return;

        string outputDir = Path.Combine(
            Path.GetDirectoryName(bnkFilePath) ?? ".",
            Path.GetFileNameWithoutExtension(bnkFilePath)
        );
        Directory.CreateDirectory(outputDir);

        foreach (var entry in entries)
        {
            string wemPath = Path.Combine(outputDir, $"{entry.Id}.wem");
            using var fs = File.Create(wemPath);
            fs.Write(data, (int)entry.Offset, (int)entry.Size);
        }
    }

    private static string ReadSignature(BinaryReader reader)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
            chars[i] = (char)reader.ReadByte();
        return new string(chars);
    }
}
