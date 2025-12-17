using System.Buffers.Binary;

namespace BydTools.PCK;

/// <summary>
/// PCK (Arknights Endfield AK Sound Package) Parser.
/// </summary>
public class PckParser
{
    private const uint PLAIN_MAGIC = 0x4B504B41; // "AKPK" in little-endian

    private readonly Stream _file;

    /// <summary>
    /// Initializes a new instance of the PckParser class.
    /// </summary>
    /// <param name="fileStream">The file stream to parse.</param>
    public PckParser(Stream fileStream)
    {
        _file = fileStream ?? throw new ArgumentNullException(nameof(fileStream));
    }

    /// <summary>
    /// Deciphers the encrypted data in-place.
    /// </summary>
    /// <param name="data">Byte array to be deciphered.</param>
    /// <param name="seed">Decipher seed.</param>
    /// <param name="size">Total size of the data.</param>
    /// <param name="offsetToFileStart">The data's offset to the file start.</param>
    public static void DecipherInplace(Span<byte> data, uint seed, int size, int offsetToFileStart)
    {
        if (size == 0)
            return;

        const uint CONST_M = 0x04E11C23;
        const uint CONST_X = 0x9C5A0B29;

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
        int alignedSize = (size - pos) & ~0b11;
        int numBlocks = alignedSize / 4;

        // Head: misaligned beginning
        if (alignedOffset > 0)
        {
            uint key = GenerateKey(baseCounter);
            byte[] keyBytes = BitConverter.GetBytes(key);
            int bytesLeading = Math.Min(4 - alignedOffset, size);
            for (int i = 0; i < bytesLeading; i++)
            {
                data[pos] ^= keyBytes[alignedOffset + i];
                pos++;
            }
            baseCounter++;
        }

        // Body: aligned 4-bytes blocks
        for (int blockIdx = 0; blockIdx < numBlocks; blockIdx++)
        {
            uint key = GenerateKey(baseCounter + (uint)blockIdx);
            byte[] keyBytes = BitConverter.GetBytes(key);
            for (int i = 0; i < 4; i++)
            {
                data[pos + i] ^= keyBytes[i];
            }
            pos += 4;
        }

        // Tail: remaining bytes
        if (pos < size)
        {
            uint key = GenerateKey(baseCounter + (uint)numBlocks);
            byte[] keyBytes = BitConverter.GetBytes(key);
            int bytesRemaining = size - pos;
            for (int i = 0; i < bytesRemaining; i++)
            {
                data[pos + i] ^= keyBytes[i];
            }
        }
    }

    /// <summary>
    /// Gets header data of this PCK.
    /// </summary>
    /// <returns>The header data.</returns>
    public byte[] GetHeader()
    {
        _file.Seek(0, SeekOrigin.End);
        if (_file.Position < 8)
            throw new EndOfStreamException("No enough data for reading header");

        _file.Seek(0, SeekOrigin.Begin);
        byte[] headerMagicBytes = new byte[4];
        _file.ReadExactly(headerMagicBytes);
        uint headerMagic = BinaryPrimitives.ReadUInt32LittleEndian(headerMagicBytes);

        byte[] headerSizeBytes = new byte[4];
        _file.ReadExactly(headerSizeBytes);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(headerSizeBytes);

        byte[] headerContent = new byte[headerSize];
        _file.ReadExactly(headerContent);

        if (headerMagic == PLAIN_MAGIC)
        {
            return headerContent;
        }
        else
        {
            byte[] pData = new byte[headerSize - 4];
            Array.Copy(headerContent, 4, pData, 0, pData.Length);
            DecipherInplace(pData, headerSize, pData.Length, 0);

            byte[] result = new byte[4 + 4 + pData.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), PLAIN_MAGIC);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)pData.Length);
            Array.Copy(pData, 0, result, 8, pData.Length);
            return result;
        }
    }

    /// <summary>
    /// Represents a file entry in PCK.
    /// </summary>
    public record PckEntry(ulong FileId, uint One, uint Size, uint Offset, uint TypeFlag);

    /// <summary>
    /// Gets all file entries in this PCK.
    /// </summary>
    /// <returns>A list of file entries.</returns>
    public List<PckEntry> GetEntries()
    {
        byte[] header = GetHeader();
        if (
            header.Length < 4
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)) != PLAIN_MAGIC
        )
            throw new InvalidDataException("Invalid magic number");

        // Find first 0x00000001 field
        int firstOnePos = -1;
        for (int i = 0x24; i < header.Length - 4; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(i, 4)) == 1)
            {
                firstOnePos = i;
                break;
            }
        }

        if (firstOnePos == -1)
            throw new InvalidDataException("First 0x00000001 field not found");

        List<PckEntry> entries = new();
        int pos = firstOnePos - 8;

        for (int part = 0; part < 2; part++)
        {
            if (pos + 4 > header.Length)
                break;

            uint entriesCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos, 4));
            pos += 4;

            for (uint i = 0; i < entriesCount; i++)
            {
                if (pos + 20 > header.Length)
                    throw new EndOfStreamException("No enough data for reading entries");

                uint fileId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos, 4));
                uint one = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos + 4, 4));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos + 8, 4));
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos + 12, 4));
                uint typeFlag = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos + 16, 4));

                if (one == 1) // block with uint32 ID
                {
                    if (typeFlag == 0 || typeFlag == 1)
                    {
                        entries.Add(new PckEntry(fileId, one, size, offset, typeFlag));
                        pos += 20;
                        continue;
                    }
                }

                if (size == 1) // block with uint64 ID
                {
                    if (pos + 24 > header.Length)
                        throw new EndOfStreamException("No enough data for reading entries");

                    // For uint64 ID format, the layout is:
                    // fileId (pos+0) = fileId_low
                    // one (pos+4) = fileId_high
                    // size (pos+8) = 1 (marker)
                    // offset (pos+12) = actual_size
                    // typeFlag (pos+16) = actual_offset
                    // pos+20 = actual_typeFlag
                    uint actualSize = offset; // pos+12 contains the actual size
                    uint actualOffset = typeFlag; // pos+16 contains the actual offset
                    uint actualTypeFlag = BinaryPrimitives.ReadUInt32LittleEndian(
                        header.AsSpan(pos + 20, 4)
                    );
                    ulong fileId64 = fileId | ((ulong)one << 32);

                    if (actualTypeFlag == 0 || actualTypeFlag == 1)
                    {
                        entries.Add(new PckEntry(fileId64, 1, actualSize, actualOffset, actualTypeFlag));
                        pos += 24;
                        continue;
                    }
                }

                throw new InvalidDataException("Cannot determine entry info format");
            }

            if (pos + 4 > header.Length)
                break;
        }

        return entries;
    }

    /// <summary>
    /// Gets the file data for a given entry.
    /// </summary>
    /// <param name="entry">The entry to extract.</param>
    /// <returns>The file data.</returns>
    public byte[] GetFile(PckEntry entry)
    {
        if (entry.One != 1)
            throw new ArgumentException(
                $"Unexpected value (0x{entry.One:X8}) for 'one' field",
                nameof(entry)
            );

        if (entry.TypeFlag != 0 && entry.TypeFlag != 1)
            throw new ArgumentException(
                $"Unexpected value (0x{entry.TypeFlag:X8}) for 'type_flag' field",
                nameof(entry)
            );

        _file.Seek(entry.Offset, SeekOrigin.Begin);
        byte[] fileData = new byte[entry.Size];
        int bytesRead = _file.Read(fileData);
        if (bytesRead != entry.Size)
            throw new InvalidDataException("Read size mismatch");

        DecipherInplace(fileData, (uint)entry.FileId, fileData.Length, 0);
        return fileData;
    }
}
