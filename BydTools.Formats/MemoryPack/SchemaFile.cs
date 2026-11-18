using System.Buffers.Binary;
using System.Text.Json;
using MessagePack;

namespace BydTools.Formats.MemoryPack;

/// <summary>
/// beyond-mp-schema export files: JSON, or MessagePack + LZ4 Block
/// (MessagePack-CSharp extension type 99) as <c>*.msgpack.lz4</c>.
/// </summary>
public static class SchemaFile
{
    public const string JsonExtension = ".json";
    public const string MsgPackExtension = ".msgpack.lz4";

    /// <summary>
    /// Prefers <c>{stem}.msgpack.lz4</c> when both exist (the transfer format).
    /// </summary>
    public static string? Find(string directory, string stem)
    {
        string packed = Path.Combine(directory, stem + MsgPackExtension);
        if (File.Exists(packed))
            return packed;
        string json = Path.Combine(directory, stem + JsonExtension);
        return File.Exists(json) ? json : null;
    }

    public static JsonDocument Parse(string path) => Parse(File.ReadAllBytes(path));

    public static JsonDocument Parse(byte[] data)
    {
        if (JsonDataMemoryPack.IsPlainJson(data))
            return JsonDocument.Parse(data);
        return JsonDocument.Parse(MessagePackSerializer.ConvertToJson(UnwrapLz4Block(data)));
    }

    /// <summary>
    /// MessagePack-CSharp <c>Lz4Block</c>: ext type 99, payload is MessagePack int32 uncompressed
    /// length then an LZ4 block. Plain MessagePack is returned unchanged.
    /// </summary>
    public static byte[] UnwrapLz4Block(byte[] data)
    {
        if (data.Length < 4)
            return data;

        int extLength;
        int typeOff;
        int payloadOff;
        switch (data[0])
        {
            case 0xC7: // ext8
                extLength = data[1];
                typeOff = 2;
                payloadOff = 3;
                break;
            case 0xC8: // ext16
                extLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(1, 2));
                typeOff = 3;
                payloadOff = 4;
                break;
            case 0xC9: // ext32
                if (data.Length < 6)
                    return data;
                uint len32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(1, 4));
                if (len32 > int.MaxValue)
                    throw new InvalidDataException("Lz4Block MessagePack extension length exceeds Int32.");
                extLength = (int)len32;
                typeOff = 5;
                payloadOff = 6;
                break;
            default:
                return data;
        }

        if (typeOff >= data.Length || data[typeOff] != 99)
            return data;
        if (payloadOff + (long)extLength > data.Length)
            throw new InvalidDataException("Lz4Block MessagePack extension is truncated.");

        ReadOnlySpan<byte> extPayload = data.AsSpan(payloadOff, extLength);
        if (!TryReadMsgPackInt32(extPayload, out int uncompressed, out int headerBytes))
            throw new InvalidDataException("Lz4Block MessagePack extension is truncated or has invalid length header.");

        return Lz4Block.Decompress(extPayload[headerBytes..], uncompressed);
    }

    private static bool TryReadMsgPackInt32(ReadOnlySpan<byte> data, out int value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (data.IsEmpty)
            return false;

        byte tag = data[0];
        if (tag <= 0x7F)
        {
            value = tag;
            consumed = 1;
            return true;
        }

        if (tag >= 0xE0)
        {
            value = (sbyte)tag;
            consumed = 1;
            return true;
        }

        switch (tag)
        {
            case 0xCC when data.Length >= 2:
                value = data[1];
                consumed = 2;
                return true;
            case 0xCD when data.Length >= 3:
                value = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1, 2));
                consumed = 3;
                return true;
            case 0xCE when data.Length >= 5:
                uint u32 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(1, 4));
                if (u32 > int.MaxValue)
                    return false;
                value = (int)u32;
                consumed = 5;
                return true;
            case 0xD0 when data.Length >= 2:
                value = (sbyte)data[1];
                consumed = 2;
                return true;
            case 0xD1 when data.Length >= 3:
                value = BinaryPrimitives.ReadInt16BigEndian(data.Slice(1, 2));
                consumed = 3;
                return true;
            case 0xD2 when data.Length >= 5:
                value = BinaryPrimitives.ReadInt32BigEndian(data.Slice(1, 4));
                consumed = 5;
                return true;
            default:
                return false;
        }
    }
}
