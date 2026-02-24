using System.Buffers.Binary;
using System.Text;

namespace BydTools.PCK;

/// <summary>
/// PCK (AKPK) archive parser with VFS encryption support.
/// Parses the header into structured sectors (languages, banks, sounds, externals)
/// and provides decrypted file data extraction.
/// </summary>
public class PckParser
{
    private const uint AKPK_MAGIC = 0x4B504B41; // "AKPK" little-endian
    private const uint CONST_M = 0x04E11C23;
    private const uint CONST_X = 0x9C5A0B29;

    private readonly Stream _file;
    private bool _isVfsEncrypted;

    public PckParser(Stream fileStream)
    {
        _file = fileStream ?? throw new ArgumentNullException(nameof(fileStream));
    }

    /// <summary>
    /// Whether the underlying PCK file uses VFS encryption.
    /// Only valid after <see cref="Parse"/> has been called.
    /// </summary>
    public bool IsVfsEncrypted => _isVfsEncrypted;

    /// <summary>
    /// XOR-deciphers data in-place using a counter-based key derived from <paramref name="seed"/>.
    /// </summary>
    public static void DecipherInplace(Span<byte> data, uint seed, int size, int offsetToFileStart)
    {
        if (size == 0)
            return;

        static uint GenerateKey(uint counter)
        {
            uint val = ((counter & 0xFF) ^ CONST_X) * CONST_M & 0xFFFFFFFF;
            val = (val ^ ((counter >> 8) & 0xFF)) * CONST_M & 0xFFFFFFFF;
            val = (val ^ ((counter >> 16) & 0xFF)) * CONST_M & 0xFFFFFFFF;
            val = (val ^ ((counter >> 24) & 0xFF)) * CONST_M & 0xFFFFFFFF;
            return val;
        }

        int pos = 0;
        uint baseCounter = seed + (uint)(offsetToFileStart >> 2);
        int alignedOffset = offsetToFileStart & 0b11;

        // Head: handle misaligned beginning
        if (alignedOffset > 0)
        {
            uint key = GenerateKey(baseCounter);
            Span<byte> keyBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(keyBytes, key);
            int bytesLeading = Math.Min(4 - alignedOffset, size);
            for (int i = 0; i < bytesLeading; i++)
                data[pos++] ^= keyBytes[alignedOffset + i];
            baseCounter++;
        }

        // Body: aligned 4-byte blocks
        int alignedSize = (size - pos) & ~0b11;
        int numBlocks = alignedSize / 4;
        Span<byte> bodyKeyBytes = stackalloc byte[4];
        for (int blockIdx = 0; blockIdx < numBlocks; blockIdx++)
        {
            uint key = GenerateKey(baseCounter + (uint)blockIdx);
            BinaryPrimitives.WriteUInt32LittleEndian(bodyKeyBytes, key);
            for (int i = 0; i < 4; i++)
                data[pos + i] ^= bodyKeyBytes[i];
            pos += 4;
        }

        // Tail: remaining bytes
        if (pos < size)
        {
            uint key = GenerateKey(baseCounter + (uint)numBlocks);
            Span<byte> keyBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(keyBytes, key);
            int bytesRemaining = size - pos;
            for (int i = 0; i < bytesRemaining; i++)
                data[pos + i] ^= keyBytes[i];
        }
    }

    /// <summary>
    /// Parses the PCK header into structured content (languages + file entries).
    /// </summary>
    public PckContent Parse()
    {
        byte[] header = ReadDecryptedHeader();
        using var ms = new MemoryStream(header);
        using var reader = new BinaryReader(ms);

        uint flag = reader.ReadUInt32(); // endianness: 1 = LE, 0x01000000 = BE
        bool bigEndian = flag == 0x01000000;
        if (bigEndian)
            throw new NotSupportedException("Big-endian PCK files are not supported");

        uint languagesSectorSize = reader.ReadUInt32();
        uint banksSectorSize = reader.ReadUInt32();
        uint soundsSectorSize = reader.ReadUInt32();

        // Externals sector exists when the three known sectors + overhead don't fill the header
        uint externalsSectorSize = 0;
        uint overhead = 4u + 4u + 4u + 4u; // flag + 3 sector-size fields
        if (
            languagesSectorSize + banksSectorSize + soundsSectorSize + overhead
            < (uint)header.Length
        )
            externalsSectorSize = reader.ReadUInt32();

        var languages = ParseLanguages(reader, languagesSectorSize);

        var entries = new List<PckFileEntry>();
        ParseSector(reader, banksSectorSize, PckSectorType.Bank, entries);
        ParseSector(reader, soundsSectorSize, PckSectorType.Sound, entries);
        if (externalsSectorSize > 0)
            ParseSector(reader, externalsSectorSize, PckSectorType.External, entries);

        return new PckContent(languages, entries);
    }

    /// <summary>
    /// Reads and (if VFS-encrypted) decrypts the raw file data for a given entry.
    /// </summary>
    public byte[] GetFileData(PckFileEntry entry)
    {
        _file.Seek(entry.Offset, SeekOrigin.Begin);
        byte[] data = new byte[entry.Size];
        _file.ReadExactly(data);

        if (_isVfsEncrypted)
            DecipherInplace(data, (uint)entry.FileId, data.Length, 0);

        return data;
    }

    /// <summary>
    /// Returns full PCK bytes with VFS-layer encryption removed.
    /// If the source is already plain AKPK, returns the original bytes.
    /// </summary>
    public byte[] GetDecryptedPckBytes()
    {
        _file.Seek(0, SeekOrigin.Begin);
        byte[] pckBytes = new byte[_file.Length];
        _file.ReadExactly(pckBytes);

        var content = Parse();
        if (!_isVfsEncrypted)
            return pckBytes;

        byte[] decryptedHeader = ReadDecryptedHeader();
        if (pckBytes.Length < 8 + decryptedHeader.Length)
            throw new InvalidDataException("PCK buffer is too small for decrypted header");

        BinaryPrimitives.WriteUInt32LittleEndian(pckBytes.AsSpan(0, 4), AKPK_MAGIC);
        decryptedHeader.CopyTo(pckBytes.AsSpan(8, decryptedHeader.Length));

        foreach (var entry in content.Entries)
        {
            if (entry.Offset < 0)
                continue;

            if (entry.Size > int.MaxValue || entry.Offset > int.MaxValue)
                continue;

            int offset = (int)entry.Offset;
            int size = (int)entry.Size;
            if (size == 0 || offset > pckBytes.Length - size)
                continue;

            DecipherInplace(pckBytes.AsSpan(offset, size), (uint)entry.FileId, size, 0);
        }

        return pckBytes;
    }

    // ── private helpers ──────────────────────────────────────────────

    /// <summary>
    /// Reads the PCK header from the stream, decrypting it if VFS-encrypted.
    /// Returns header content starting from the flag field.
    /// </summary>
    private byte[] ReadDecryptedHeader()
    {
        _file.Seek(0, SeekOrigin.Begin);

        Span<byte> prefix = stackalloc byte[8];
        _file.ReadExactly(prefix);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]);

        if (headerSize < 16)
            throw new InvalidDataException($"Header size too small: {headerSize}");

        byte[] headerContent = new byte[headerSize];
        _file.ReadExactly(headerContent);

        if (magic == AKPK_MAGIC)
        {
            _isVfsEncrypted = false;
            return headerContent;
        }

        // VFS encrypted: first 4 bytes of headerContent are the (encrypted) flag — skip them.
        // Decrypt everything after the flag.
        _isVfsEncrypted = true;
        byte[] decryptedPayload = new byte[headerSize - 4];
        Array.Copy(headerContent, 4, decryptedPayload, 0, decryptedPayload.Length);
        DecipherInplace(decryptedPayload, headerSize, decryptedPayload.Length, 0);

        // Reconstruct: flag(1, LE) + decrypted sector data
        byte[] result = new byte[4 + decryptedPayload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), 1);
        decryptedPayload.CopyTo(result.AsSpan(4));
        return result;
    }

    private static List<PckLanguage> ParseLanguages(BinaryReader reader, uint sectorSize)
    {
        if (sectorSize == 0)
            return [];

        long sectorStart = reader.BaseStream.Position;
        uint count = reader.ReadUInt32();
        var languages = new List<PckLanguage>((int)count);

        for (int i = 0; i < count; i++)
        {
            uint nameOffset = reader.ReadUInt32();
            uint langId = reader.ReadUInt32();

            long savedPos = reader.BaseStream.Position;
            reader.BaseStream.Position = sectorStart + nameOffset;

            // Detect encoding: if either of the first 2 bytes is 0x00 → UTF-16LE, else UTF-8
            byte b1 = reader.ReadByte();
            byte b2 = reader.ReadByte();
            reader.BaseStream.Position = sectorStart + nameOffset;

            string name;
            if (b1 == 0 || b2 == 0)
            {
                byte[] raw = reader.ReadBytes(0x20);
                name = Encoding.Unicode.GetString(raw);
            }
            else
            {
                byte[] raw = reader.ReadBytes(0x10);
                name = Encoding.UTF8.GetString(raw);
            }
            int nullIdx = name.IndexOf('\0');
            if (nullIdx >= 0)
                name = name[..nullIdx];

            languages.Add(new PckLanguage(langId, name));
            reader.BaseStream.Position = savedPos;
        }

        reader.BaseStream.Position = sectorStart + sectorSize;
        return languages;
    }

    /// <summary>
    /// Parses one file-entry sector (banks, sounds, or externals).
    /// Entry size is auto-detected: 20 bytes (normal) or 24 bytes (alt mode).
    /// </summary>
    private static void ParseSector(
        BinaryReader reader,
        uint sectorSize,
        PckSectorType sectorType,
        List<PckFileEntry> entries
    )
    {
        if (sectorSize == 0)
            return;

        long sectorStart = reader.BaseStream.Position;
        uint fileCount = reader.ReadUInt32();
        if (fileCount == 0)
        {
            reader.BaseStream.Position = sectorStart + sectorSize;
            return;
        }

        uint entrySize = (sectorSize - 4) / fileCount;
        bool altMode = entrySize >= 0x18; // 24-byte entries

        for (int i = 0; i < fileCount; i++)
        {
            ulong fileId;
            if (altMode && sectorType == PckSectorType.External)
            {
                // 64-bit file ID: low word first, high word second (LE)
                uint idLow = reader.ReadUInt32();
                uint idHigh = reader.ReadUInt32();
                fileId = idLow | ((ulong)idHigh << 32);
            }
            else
            {
                fileId = reader.ReadUInt32();
            }

            uint blockSize = reader.ReadUInt32();

            uint size;
            if (altMode && sectorType != PckSectorType.External)
            {
                // 64-bit size for alt-mode non-external entries
                size = (uint)reader.ReadInt64();
            }
            else
            {
                size = reader.ReadUInt32();
            }

            uint rawOffset = reader.ReadUInt32();
            uint languageId = reader.ReadUInt32();

            long offset = blockSize != 0 ? (long)rawOffset * blockSize : rawOffset;

            entries.Add(new PckFileEntry(fileId, size, offset, languageId, sectorType));
        }

        reader.BaseStream.Position = sectorStart + sectorSize;
    }
}
