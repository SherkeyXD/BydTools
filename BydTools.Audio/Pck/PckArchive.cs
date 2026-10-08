using System.Buffers.Binary;
using System.Text;

namespace BydTools.Audio.Pck;

/// <summary>
/// Parsed AKPK archive. <see cref="IsVfsEncrypted"/> is known as soon as
/// <see cref="Parse"/> returns; entry payloads stay in the original buffer
/// until <see cref="DecipherPayloads"/> is called.
/// </summary>
public sealed class PckArchive
{
    private const uint AkpkMagic = 0x4B504B41;
    private readonly byte[] _decryptedHeader;

    private PckArchive(
        bool encrypted,
        byte[] decryptedHeader,
        IReadOnlyList<PckLanguage> languages,
        IReadOnlyList<PckFileEntry> entries
    )
    {
        IsVfsEncrypted = encrypted;
        _decryptedHeader = decryptedHeader;
        Languages = languages;
        Entries = entries;
    }

    public bool IsVfsEncrypted { get; }
    public IReadOnlyList<PckLanguage> Languages { get; }
    public IReadOnlyList<PckFileEntry> Entries { get; }

    public static PckArchive Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < 8)
            throw new InvalidDataException("PCK file is too small.");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(file);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        if (headerSize < 16 || headerSize > file.Length - 8)
            throw new InvalidDataException($"PCK header size {headerSize} is not plausible.");

        bool encrypted = magic != AkpkMagic;
        var headerContent = file.Slice(8, (int)headerSize);
        byte[] decryptedHeader;
        if (!encrypted)
        {
            decryptedHeader = headerContent.ToArray();
        }
        else
        {
            byte[] payload = headerContent[4..].ToArray();
            PckCipher.DecipherInPlace(payload, headerSize, payload.Length, 0);
            decryptedHeader = new byte[4 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(decryptedHeader, 1);
            payload.CopyTo(decryptedHeader, 4);
        }

        var reader = new SpanReader(decryptedHeader);
        uint flag = reader.ReadUInt32();
        if (flag == 0x01000000)
            throw new NotSupportedException("Big-endian PCK files are not supported.");

        uint languagesSize = reader.ReadUInt32();
        uint banksSize = reader.ReadUInt32();
        uint soundsSize = reader.ReadUInt32();
        uint overhead = 16;
        uint externalsSize = 0;
        if (languagesSize + banksSize + soundsSize + overhead < (uint)decryptedHeader.Length)
            externalsSize = reader.ReadUInt32();

        var languages = ParseLanguages(ref reader, languagesSize, decryptedHeader.Length);
        var entries = new List<PckFileEntry>();
        ParseSector(ref reader, banksSize, PckSectorType.Bank, entries, decryptedHeader.Length);
        ParseSector(ref reader, soundsSize, PckSectorType.Sound, entries, decryptedHeader.Length);
        if (externalsSize > 0)
            ParseSector(ref reader, externalsSize, PckSectorType.External, entries, decryptedHeader.Length);

        return new PckArchive(encrypted, decryptedHeader, languages, entries);
    }

    public void DecipherPayloads(Span<byte> file)
    {
        if (!IsVfsEncrypted)
            return;
        foreach (var entry in Entries)
        {
            if (!TrySlice(file, entry, out var slice))
                continue;
            PckCipher.DecipherInPlace(slice, (uint)entry.FileId, slice.Length, 0);
        }
    }

    public byte[] ToPlainPck(ReadOnlySpan<byte> file)
    {
        byte[] copy = file.ToArray();
        if (!IsVfsEncrypted)
            return copy;

        BinaryPrimitives.WriteUInt32LittleEndian(copy, AkpkMagic);
        if (copy.Length < 8 + _decryptedHeader.Length)
            throw new InvalidDataException("PCK buffer is too small for the decrypted header.");
        _decryptedHeader.CopyTo(copy, 8);
        DecipherPayloads(copy);
        return copy;
    }

    public static bool TrySlice(Span<byte> file, PckFileEntry entry, out Span<byte> slice)
    {
        slice = default;
        if (entry.Size > int.MaxValue || entry.Offset < 0 || entry.Offset > file.Length)
            return false;
        if (entry.Size > file.Length - entry.Offset)
            return false;
        slice = file.Slice((int)entry.Offset, (int)entry.Size);
        return true;
    }

    private static List<PckLanguage> ParseLanguages(ref SpanReader reader, uint sectorSize, int limit)
    {
        if (sectorSize == 0)
            return [];
        int sectorStart = reader.Position;
        if (sectorSize > limit - sectorStart)
            throw new InvalidDataException("PCK language sector exceeds the header.");
        uint count = reader.ReadUInt32();
        var languages = new List<PckLanguage>((int)Math.Min(count, 1024));
        for (uint i = 0; i < count; i++)
        {
            uint nameOffset = reader.ReadUInt32();
            uint langId = reader.ReadUInt32();
            int saved = reader.Position;
            int namePos = sectorStart + (int)nameOffset;
            reader.Seek(namePos);
            if (reader.Remaining < 2)
                throw new InvalidDataException("PCK language name is truncated.");
            byte b1 = reader.ReadByte();
            byte b2 = reader.ReadByte();
            reader.Seek(namePos);
            int rawLength = b1 == 0 || b2 == 0 ? 0x20 : 0x10;
            if (reader.Remaining < rawLength)
                throw new InvalidDataException("PCK language name is truncated.");
            var raw = reader.ReadBytes(rawLength);
            string name = (b1 == 0 || b2 == 0 ? Encoding.Unicode : Encoding.UTF8).GetString(raw);
            int nul = name.IndexOf('\0');
            if (nul >= 0)
                name = name[..nul];
            languages.Add(new PckLanguage(langId, name));
            reader.Seek(saved);
        }

        reader.Seek(sectorStart + (int)sectorSize);
        return languages;
    }

    private static void ParseSector(
        ref SpanReader reader,
        uint sectorSize,
        PckSectorType sectorType,
        List<PckFileEntry> entries,
        int limit
    )
    {
        if (sectorSize == 0)
            return;
        int sectorStart = reader.Position;
        if (sectorSize > limit - sectorStart)
            throw new InvalidDataException("PCK sector exceeds the header.");
        uint fileCount = reader.ReadUInt32();
        if (fileCount == 0)
        {
            reader.Seek(sectorStart + (int)sectorSize);
            return;
        }

        if ((sectorSize - 4) % fileCount != 0)
            throw new InvalidDataException("PCK sector size is not a multiple of its entry count.");
        uint entrySize = (sectorSize - 4) / fileCount;
        bool altMode = entrySize >= 0x18;

        for (uint i = 0; i < fileCount; i++)
        {
            ulong fileId;
            if (altMode && sectorType == PckSectorType.External)
            {
                uint idLow = reader.ReadUInt32();
                uint idHigh = reader.ReadUInt32();
                fileId = idLow | ((ulong)idHigh << 32);
            }
            else
            {
                fileId = reader.ReadUInt32();
            }

            uint blockSize = reader.ReadUInt32();
            uint size =
                altMode && sectorType != PckSectorType.External
                    ? (uint)reader.ReadInt64()
                    : reader.ReadUInt32();
            uint rawOffset = reader.ReadUInt32();
            uint languageId = reader.ReadUInt32();
            long offset = blockSize != 0 ? (long)rawOffset * blockSize : rawOffset;
            entries.Add(new PckFileEntry(fileId, size, offset, languageId, sectorType));
        }

        reader.Seek(sectorStart + (int)sectorSize);
    }

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _span;
        private int _position;

        public SpanReader(ReadOnlySpan<byte> span) => _span = span;

        public int Position => _position;
        public int Remaining => _span.Length - _position;

        public void Seek(int position)
        {
            if ((uint)position > (uint)_span.Length)
                throw new InvalidDataException($"PCK seek to {position} is outside the header.");
            _position = position;
        }

        public uint ReadUInt32()
        {
            var slice = Take(4);
            return BinaryPrimitives.ReadUInt32LittleEndian(slice);
        }

        public long ReadInt64()
        {
            var slice = Take(8);
            return BinaryPrimitives.ReadInt64LittleEndian(slice);
        }

        public byte ReadByte() => Take(1)[0];

        public ReadOnlySpan<byte> ReadBytes(int count) => Take(count);

        private ReadOnlySpan<byte> Take(int count)
        {
            if ((uint)count > (uint)Remaining)
                throw new InvalidDataException($"PCK header ended at offset {_position}.");
            var slice = _span.Slice(_position, count);
            _position += count;
            return slice;
        }
    }
}
