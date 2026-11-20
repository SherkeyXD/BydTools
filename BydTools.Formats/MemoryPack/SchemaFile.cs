using System.Buffers.Binary;
using System.Runtime.InteropServices;
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

    public delegate T MsgPackReaderFunc<out T>(ref MessagePackReader reader);

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

    public static bool IsJsonPayload(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            data = data[3..];
        int i = 0;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        return i < data.Length && data[i] is (byte)'{' or (byte)'[';
    }

    public static T Parse<T>(
        string path,
        Func<JsonElement, T> fromJson,
        MsgPackReaderFunc<T> fromMsgPack) =>
        Parse(File.ReadAllBytes(path), fromJson, fromMsgPack);

    public static T Parse<T>(
        ReadOnlyMemory<byte> data,
        Func<JsonElement, T> fromJson,
        MsgPackReaderFunc<T> fromMsgPack)
    {
        if (IsJsonPayload(data.Span))
        {
            using var doc = JsonDocument.Parse(data);
            return fromJson(doc.RootElement);
        }

        ReadOnlyMemory<byte> payload = UnwrapLz4Block(data);
        var reader = new MessagePackReader(payload);
        return fromMsgPack(ref reader);
    }

    /// <summary>
    /// MessagePack-CSharp <c>Lz4Block</c>: ext type 99, payload is MessagePack int32 uncompressed
    /// length then an LZ4 block. Plain MessagePack is returned unchanged.
    /// </summary>
    public static byte[] UnwrapLz4Block(byte[] data)
    {
        ReadOnlyMemory<byte> unwrapped = UnwrapLz4Block(data.AsMemory());
        return MemoryMarshal.TryGetArray(unwrapped, out ArraySegment<byte> segment)
            && ReferenceEquals(segment.Array, data)
            ? data
            : unwrapped.ToArray();
    }

    public static ReadOnlyMemory<byte> UnwrapLz4Block(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;
        if (span.Length < 4)
            return data;

        int extLength;
        int typeOff;
        int payloadOff;
        switch (span[0])
        {
            case 0xC7: // ext8
                extLength = span[1];
                typeOff = 2;
                payloadOff = 3;
                break;
            case 0xC8: // ext16
                extLength = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(1, 2));
                typeOff = 3;
                payloadOff = 4;
                break;
            case 0xC9: // ext32
                if (span.Length < 6)
                    return data;
                uint len32 = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(1, 4));
                if (len32 > int.MaxValue)
                    throw new InvalidDataException("Lz4Block MessagePack extension length exceeds Int32.");
                extLength = (int)len32;
                typeOff = 5;
                payloadOff = 6;
                break;
            default:
                return data;
        }

        if (typeOff >= span.Length || span[typeOff] != 99)
            return data;
        if (payloadOff + (long)extLength > span.Length)
            throw new InvalidDataException("Lz4Block MessagePack extension is truncated.");

        ReadOnlySpan<byte> extPayload = span.Slice(payloadOff, extLength);
        if (!TryReadMsgPackInt32(extPayload, out int uncompressed, out int headerBytes))
            throw new InvalidDataException("Lz4Block MessagePack extension is truncated or has invalid length header.");

        return Lz4Block.Decompress(extPayload[headerBytes..], uncompressed);
    }

    internal static string? ReadNullableString(ref MessagePackReader reader)
    {
        if (reader.TryReadNil())
            return null;
        return reader.ReadString();
    }

    internal static bool TryReadKeySpan(ref MessagePackReader reader, out ReadOnlySpan<byte> span)
    {
        if (reader.TryReadStringSpan(out span))
            return true;
        reader.Skip();
        span = default;
        return false;
    }

    internal static bool TryReadInt32(ref MessagePackReader reader, out int value)
    {
        if (reader.NextMessagePackType == MessagePackType.Integer)
        {
            value = reader.ReadInt32();
            return true;
        }

        reader.Skip();
        value = 0;
        return false;
    }

    internal static bool ReadBoolOrSkip(ref MessagePackReader reader)
    {
        if (reader.NextMessagePackType == MessagePackType.Boolean)
            return reader.ReadBoolean();

        reader.Skip();
        return false;
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
