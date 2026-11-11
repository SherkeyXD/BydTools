using System.Text.Json;
using MessagePack;

namespace BydTools.Formats.MemoryPack;

/// <summary>
/// beyond-schema-dumper export files: JSON, or MessagePack + LZ4 Block
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
    /// MessagePack-CSharp <c>Lz4Block</c>: ext type 99, payload is int32 uncompressed
    /// length then an LZ4 block. Plain MessagePack is returned unchanged.
    /// </summary>
    public static byte[] UnwrapLz4Block(byte[] data)
    {
        if (data.Length < 6)
            return data;

        int typeOff;
        int payloadOff;
        switch (data[0])
        {
            case 0xC7: // ext8
                typeOff = 2;
                payloadOff = 3;
                break;
            case 0xC8: // ext16
                typeOff = 3;
                payloadOff = 4;
                break;
            case 0xC9: // ext32
                typeOff = 5;
                payloadOff = 6;
                break;
            default:
                return data;
        }

        if (typeOff >= data.Length || data[typeOff] != 99)
            return data;
        if (payloadOff + 5 > data.Length || data[payloadOff] != 0xD2)
            throw new InvalidDataException("Lz4Block MessagePack extension is truncated.");

        int uncompressed = (data[payloadOff + 1] << 24)
            | (data[payloadOff + 2] << 16)
            | (data[payloadOff + 3] << 8)
            | data[payloadOff + 4];
        return Lz4Block.Decompress(data.AsSpan(payloadOff + 5), uncompressed);
    }
}
