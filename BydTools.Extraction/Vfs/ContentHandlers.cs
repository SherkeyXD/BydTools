using System.Buffers;
using BydTools.Audio.Pck;
using BydTools.Extraction.Sink;
using BydTools.Formats.Bundle;
using BydTools.Formats.Lua;
using BydTools.Formats.Manifest;
using BydTools.Formats.MemoryPack;
using BydTools.Formats.SparkBuffer;
using BydTools.Formats.Usm;

namespace BydTools.Extraction.Vfs;

public interface IContentHandler
{
    bool CanHandle(string relativePath);
    bool Handle(string relativePath, Memory<byte> data, IOutputSink sink);
}

public sealed class SparkBufferHandler : IContentHandler
{
    public bool CanHandle(string relativePath) =>
        Path.GetExtension(relativePath).Equals(".bytes", StringComparison.OrdinalIgnoreCase);

    public bool Handle(string relativePath, Memory<byte> data, IOutputSink sink)
    {
        using var output = sink.Create(Path.ChangeExtension(relativePath, ".json"));
        SparkBuffer.WriteJson(data.Span, output);
        output.Complete();
        return true;
    }
}

public sealed class JsonDataHandler : IContentHandler
{
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_buffer;

    private readonly MemoryPackSchema _schema;
    private readonly MemoryPackDecoder _decoder;

    public JsonDataHandler(BeyondStringPool? pool = null)
        : this(MemoryPackResources.Schema, MemoryPackResources.UnionMaps, pool)
    {
    }

    public JsonDataHandler(
        MemoryPackSchema schema,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>? unionMaps,
        BeyondStringPool? pool = null)
    {
        _schema = schema;
        _decoder = new MemoryPackDecoder(schema, pool, unionMaps);
    }

    public bool CanHandle(string relativePath) =>
        Path.GetExtension(relativePath).Equals(".json", StringComparison.OrdinalIgnoreCase);

    public bool Handle(string relativePath, Memory<byte> data, IOutputSink sink)
    {
        ReadOnlySpan<byte> span = data.Span;
        switch (JsonDataMemoryPack.Classify(span))
        {
            case JsonDataClassification.Skip:
                return false;
            case JsonDataClassification.PlainText:
                sink.WriteRaw(relativePath, span);
                return true;
            default:
                if (!_schema.TryIdentifyRoot(relativePath, span, out string? root, out string? error))
                    throw new InvalidDataException(error ?? $"Failed to identify MemoryPack root for \"{relativePath}\".");

                var buffer = t_buffer ??= new ArrayBufferWriter<byte>(16 * 1024);
                buffer.Clear();
                try
                {
                    _decoder.WriteJson(span, root!, buffer);
                    sink.WriteRaw(relativePath, buffer.WrittenSpan);
                }
                finally
                {
                    if (buffer.Capacity > 4 * 1024 * 1024)
                        t_buffer = null;
                    else
                        buffer.Clear();
                }

                return true;
        }
    }
}

public sealed class LuaHandler : IContentHandler
{
    public bool CanHandle(string relativePath) =>
        Path.GetExtension(relativePath).Equals(".lua", StringComparison.OrdinalIgnoreCase);

    public bool Handle(string relativePath, Memory<byte> data, IOutputSink sink)
    {
        byte[] decrypted = LuaDecipher.Decrypt(data.Span)
            ?? throw new InvalidDataException("Lua decrypt returned no payload.");
        sink.WriteRaw(relativePath, decrypted);
        return true;
    }
}

public sealed class UsmHandler : IContentHandler
{
    public bool CanHandle(string relativePath) =>
        relativePath.EndsWith(".usm", StringComparison.OrdinalIgnoreCase);

    public bool Handle(string relativePath, Memory<byte> data, IOutputSink sink)
    {
        string basePath = Path.ChangeExtension(relativePath, null) ?? relativePath;
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int emitted = CriUsmDemuxer.Demux(data.Span, (ext, payload) =>
        {
            seen.TryGetValue(ext, out int index);
            string name = index == 0 ? basePath + ext : $"{basePath}_{index}{ext}";
            seen[ext] = index + 1;
            sink.WriteRaw(name, payload);
        });
        return emitted > 0;
    }
}

public sealed class PckDecryptHandler : IContentHandler
{
    public bool CanHandle(string relativePath) =>
        relativePath.EndsWith(".pck", StringComparison.OrdinalIgnoreCase);

    public bool Handle(string relativePath, Memory<byte> data, IOutputSink sink)
    {
        var archive = PckArchive.Parse(data.Span);
        archive.DecipherInPlace(data.Span);
        sink.WriteRaw(relativePath, data.Span);
        return true;
    }
}

public sealed class BundleHandler : IContentHandler
{
    public bool CanHandle(string relativePath) =>
        Path.GetExtension(relativePath).Equals(".ab", StringComparison.OrdinalIgnoreCase);

    public bool Handle(string relativePath, Memory<byte> data, IOutputSink sink)
    {
        if (EndfieldBundle.IsStandardUnityBundle(data.Span))
            return false;

        if (!EndfieldBundle.IsEndfieldVfsBundle(data.Span))
            throw new InvalidDataException("Unrecognized .ab bundle header.");

        using var output = sink.Create(relativePath);
        EndfieldBundle.ConvertToStandardUnityFs(data.Span, output);
        output.Complete();
        return true;
    }
}

public sealed class BundleManifestHandler : IContentHandler
{
    public bool CanHandle(string relativePath) =>
        Path.GetExtension(relativePath).Equals(".hgmmap", StringComparison.OrdinalIgnoreCase);

    public bool Handle(string relativePath, Memory<byte> data, IOutputSink sink)
    {
        using var output = sink.Create(relativePath + ".bin");
        HgmmapManifest.DecompressBrotli(data.Span, output);
        output.Complete();
        return true;
    }
}
