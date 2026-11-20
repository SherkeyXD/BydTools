using System.Text.Encodings.Web;
using System.Text.Json;
using MessagePack;

namespace BydTools.Formats.MemoryPack;

public enum JsonDataClassification
{
    MemoryPack,
    PlainText,
    Skip,
}

/// <summary>
/// Turns JsonData MemoryPack bytes into plaintext JSON.
/// A leading <c>{</c> or <c>[</c> stays as UTF-8 JSON. USPC and SHPI are skipped.
/// </summary>
public static class JsonDataMemoryPack
{
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Loads <c>{ "AbilityActionData": { "296": "PlayAnimationActionData", ... }, "ActionBase": { ... } }</c>.
    /// A flat <c>{ "296": "..." }</c> object is treated as AbilityActionData only (legacy).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> LoadUnionMap(string path) =>
        SchemaFile.Parse(path, LoadUnionMapMsgPack);

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> LoadUnionMap(byte[] data) =>
        SchemaFile.Parse(data, LoadUnionMapMsgPack);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> LoadUnionMapMsgPack(
        ref MessagePackReader reader)
    {
        if (reader.NextMessagePackType != MessagePackType.Map)
            throw new InvalidDataException("Union map must be a JSON object.");

        var maps = new Dictionary<string, IReadOnlyDictionary<int, string>>(StringComparer.Ordinal);
        Dictionary<int, string>? flat = null;
        int count = reader.ReadMapHeader();
        for (int i = 0; i < count; i++)
        {
            string? key = SchemaFile.ReadNullableString(ref reader);
            if (key == null)
            {
                reader.Skip();
                continue;
            }

            if (reader.NextMessagePackType == MessagePackType.Map)
            {
                maps[key] = ReadTagMapMsgPack(ref reader);
            }
            else if (int.TryParse(key, out int tag) && reader.NextMessagePackType == MessagePackType.String)
            {
                string? name = SchemaFile.ReadNullableString(ref reader);
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidDataException($"Union tag {tag} has an empty type name.");
                flat ??= new Dictionary<int, string>();
                flat[tag] = name;
            }
            else
            {
                reader.Skip();
            }
        }

        if (maps.Count == 0 && flat != null)
            maps["AbilityActionData"] = flat;

        return maps;
    }

    private static Dictionary<int, string> ReadTagMapMsgPack(ref MessagePackReader reader)
    {
        int count = reader.ReadMapHeader();
        var map = new Dictionary<int, string>(count);
        for (int i = 0; i < count; i++)
        {
            string? tagText = SchemaFile.ReadNullableString(ref reader);
            if (tagText == null || !int.TryParse(tagText, out int tag))
                throw new InvalidDataException($"Union tag \"{tagText}\" is not an integer.");
            string? name = SchemaFile.ReadNullableString(ref reader);
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException($"Union tag {tag} has an empty type name.");
            map[tag] = name;
        }

        return map;
    }

    public static bool IsPlainJson(ReadOnlySpan<byte> data)
    {
        data = SkipBom(data);
        int i = SkipWs(data);
        if (i >= data.Length || data[i] is not ((byte)'{' or (byte)'['))
            return false;
        try
        {
            var reader = new Utf8JsonReader(data);
            while (reader.Read())
            {
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// UTF-8 / ASCII text that is not a JSON value (StringPoolCounting, notes).
    /// The first non-whitespace byte is a letter and the following run is printable.
    /// </summary>
    public static bool IsPlainText(ReadOnlySpan<byte> data)
    {
        int i = SkipWs(data);
        if (i >= data.Length)
            return false;
        byte first = data[i];
        if (first is (byte)'{' or (byte)'[')
            return false;
        if (first is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z'))
            return false;
        int end = Math.Min(data.Length, i + 32);
        bool sawSpace = false;
        for (int k = i; k < end; k++)
        {
            byte b = data[k];
            if (b is 9 or 10 or 13 or (byte)' ')
            {
                sawSpace = true;
                continue;
            }
            if (b < 32 || b > 126)
                return false;
        }

        return sawSpace && (end - i >= 16 || end == data.Length);
    }

    private static ReadOnlySpan<byte> SkipBom(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return data[3..];
        return data;
    }

    private static int SkipWs(ReadOnlySpan<byte> data)
    {
        int i = 0;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        return i;
    }

    public static JsonDataClassification Classify(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data.StartsWith("USPC"u8) || data.StartsWith("SHPI"u8))
            return JsonDataClassification.Skip;

        if (IsPlainJson(data) || IsPlainText(data))
            return JsonDataClassification.PlainText;

        return JsonDataClassification.MemoryPack;
    }
}
