using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace BydTools.VFS;

/// <summary>
/// Represents the main information structure of a VFS block (BLC file).
/// Each VFBlockMainInfo corresponds to a single BLC file.
/// </summary>
public class VFBlockMainInfo
{
    /// <summary>
    /// Parses VFBlockMainInfo from decrypted BLC file bytes.
    /// </summary>
    /// <param name="bytes">The decrypted byte array containing the BLC data.</param>
    /// <param name="offset">The starting offset (typically BLOCK_HEAD_LEN after decryption).</param>
    public VFBlockMainInfo(byte[] bytes, int offset = 0)
    {
        // Read version (4 bytes, little-endian)
        version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(int);

        // Skip 12 bytes of unknown data (CRC and other metadata).
        // As mentioned in the blog: "there are 12 unknown bytes after the version field in the decrypted BLC file".
        offset += 12;

        // Read groupCfgName (2 bytes length + UTF-8 string)
        ushort groupCfgNameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(ushort);
        groupCfgName = Encoding.UTF8.GetString(bytes.AsSpan(offset, groupCfgNameLength));
        offset += groupCfgNameLength;

        // groupCfgHashName is 8 bytes long but only the first 4 bytes are valid.
        // Read as little-endian uint32, format as uppercase hex to get the big-endian representation.
        groupCfgHashName = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset))
            .ToString("X8");
        offset += sizeof(long); // Skip full 8 bytes as per the structure

        // Read groupFileInfoNum (4 bytes, little-endian)
        groupFileInfoNum = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(int);

        // Read groupChunksLength (8 bytes, little-endian)
        groupChunksLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(long);

        // Read blockType (1 byte)
        blockType = (EVFSBlockType)bytes[offset++];

        // Read chunk count and allocate array
        var chunkCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        allChunks = GC.AllocateUninitializedArray<FVFBlockChunkInfo>(chunkCount);
        offset += sizeof(int);

        // Parse each chunk
        foreach (ref var chunk in allChunks.AsSpan())
        {
            // Read md5Name (16 bytes as UInt128, little-endian)
            chunk.md5Name = BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(offset));
            offset += Marshal.SizeOf<UInt128>();

            // Read contentMD5 (16 bytes as UInt128, little-endian)
            chunk.contentMD5 = BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(offset));
            offset += Marshal.SizeOf<UInt128>();

            // Read length (8 bytes, little-endian)
            chunk.length = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
            offset += sizeof(long);

            // Read blockType (1 byte)
            chunk.blockType = (EVFSBlockType)bytes[offset++];

            // Read file count and allocate array
            var fileCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            chunk.files = GC.AllocateUninitializedArray<FVFBlockFileInfo>(fileCount);
            offset += sizeof(int);

            // Parse each file in the chunk
            foreach (ref var file in chunk.files.AsSpan())
            {
                // Read fileName (2 bytes length + UTF-8 string)
                ushort fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(offset)
                );
                offset += sizeof(ushort);
                file.fileName = Encoding.UTF8.GetString(bytes.AsSpan(offset, fileNameLength));
                offset += fileNameLength;

                // Read fileNameHash (8 bytes, little-endian)
                file.fileNameHash = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
                offset += sizeof(long);

                // Read fileChunkMD5Name (16 bytes as UInt128, little-endian)
                file.fileChunkMD5Name = BinaryPrimitives.ReadUInt128LittleEndian(
                    bytes.AsSpan(offset)
                );
                offset += Marshal.SizeOf<UInt128>();

                // Read fileDataMD5 (16 bytes as UInt128, little-endian)
                file.fileDataMD5 = BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(offset));
                offset += Marshal.SizeOf<UInt128>();

                // Read offset (8 bytes, little-endian)
                file.offset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
                offset += sizeof(long);

                // Read len (8 bytes, little-endian)
                file.len = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
                offset += sizeof(long);

                // Read blockType (1 byte)
                file.blockType = (EVFSBlockType)bytes[offset++];

                // Read bUseEncrypt (1 byte, non-zero is true)
                file.bUseEncrypt = bytes[offset++] != 0;

                // If encrypted, read ivSeed (8 bytes, little-endian)
                if (file.bUseEncrypt)
                {
                    file.ivSeed = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
                    offset += sizeof(long);
                }
            }
        }
    }

    /// <summary>
    /// VFS protocol version (typically 3).
    /// </summary>
    public int version;

    /// <summary>
    /// The configuration name of this block group (e.g., "BundleManifest", "Audio").
    /// This matches the groupCfgName in the BLC file.
    /// </summary>
    public string groupCfgName;

    /// <summary>
    /// The hash name of this block group as a hex string (e.g., "1CDDBF1F").
    /// This is used as the directory name in the VFS folder.
    /// </summary>
    public string groupCfgHashName;

    /// <summary>
    /// The total number of virtual files in this block group.
    /// </summary>
    public int groupFileInfoNum;

    /// <summary>
    /// The total length of all chunks in this block group in bytes.
    /// </summary>
    public long groupChunksLength;

    /// <summary>
    /// The block type of this group.
    /// </summary>
    public EVFSBlockType blockType;

    /// <summary>
    /// Array of chunk information. Each chunk corresponds to a CHK file.
    /// </summary>
    public FVFBlockChunkInfo[] allChunks;
}
