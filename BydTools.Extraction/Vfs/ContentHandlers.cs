using BydTools.Audio.Pck;
using BydTools.Extraction.Sink;
using BydTools.Formats.Lua;
using BydTools.Formats.MemoryPack;
using BydTools.Formats.SparkBuffer;
using BydTools.Formats.Usm;

namespace BydTools.Extraction.Vfs;

public enum HandleStatus
{
    Handled,
    Skipped,
    Failed,
}

public readonly record struct HandleResult(HandleStatus Status, string? Error = null)
{
    public static HandleResult Handled { get; } = new(HandleStatus.Handled);
    public static HandleResult Skipped { get; } = new(HandleStatus.Skipped);
    public static HandleResult Fail(string? error = null) => new(HandleStatus.Failed, error);
}

public interface IContentHandler
{
    bool CanHandle(string relativePath);
    HandleResult Handle(string relativePath, ReadOnlyMemory<byte> data, IOutputSink sink);
}

public sealed class SparkBufferHandler : IContentHandler
{
    public bool CanHandle(string relativePath) =>
        Path.GetExtension(relativePath).Equals(".bytes", StringComparison.OrdinalIgnoreCase);

    public HandleResult Handle(string relativePath, ReadOnlyMemory<byte> data, IOutputSink sink)
    {
        try
        {
            string json = SparkBuffer.ToJson(data.Span);
            using var output = sink.Create(Path.ChangeExtension(relativePath, ".json"));
            output.Write(System.Text.Encoding.UTF8.GetBytes(json));
            output.Complete();
            return HandleResult.Handled;
        }
        catch (Exception ex)
        {
            return HandleResult.Fail(ex.Message);
        }
    }
}

public sealed class JsonDataHandler : IContentHandler
{
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

    public HandleResult Handle(string relativePath, ReadOnlyMemory<byte> data, IOutputSink sink)
    {
        try
        {
            JsonDataFileResult result = JsonDataMemoryPack.DecodeBytes(
                _decoder,
                _schema,
                data.Span,
                relativePath,
                out byte[]? output
            );
            if (result.Kind is JsonDataFileKind.Decoded or JsonDataFileKind.PlainJson)
            {
                using var file = sink.Create(relativePath);
                file.Write(output ?? data.ToArray());
                file.Complete();
                return HandleResult.Handled;
            }

            return result.Kind == JsonDataFileKind.Failed
                ? HandleResult.Fail(result.Error)
                : HandleResult.Skipped;
        }
        catch (Exception ex)
        {
            return HandleResult.Fail(ex.Message);
        }
    }
}

public sealed class LuaHandler : IContentHandler
{
    public bool CanHandle(string relativePath) =>
        Path.GetExtension(relativePath).Equals(".lua", StringComparison.OrdinalIgnoreCase);

    public HandleResult Handle(string relativePath, ReadOnlyMemory<byte> data, IOutputSink sink)
    {
        try
        {
            byte[]? decrypted = LuaDecipher.Decrypt(data.Span);
            if (decrypted == null)
                return HandleResult.Fail("Lua decrypt returned no payload.");
            using var output = sink.Create(relativePath);
            output.Write(decrypted);
            output.Complete();
            return HandleResult.Handled;
        }
        catch (Exception ex)
        {
            return HandleResult.Fail(ex.Message);
        }
    }
}

public sealed class UsmHandler : IContentHandler
{
    public bool CanHandle(string relativePath) =>
        relativePath.EndsWith(".usm", StringComparison.OrdinalIgnoreCase);

    public HandleResult Handle(string relativePath, ReadOnlyMemory<byte> data, IOutputSink sink)
    {
        try
        {
            var streams = CriUsmDemuxer.Demux(data.Span);
            if (streams.Length == 0)
                return HandleResult.Skipped;
            string basePath = Path.ChangeExtension(relativePath, null) ?? relativePath;
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var stream in streams)
            {
                seen.TryGetValue(stream.Extension, out int index);
                string name = index == 0 ? basePath + stream.Extension : $"{basePath}_{index}{stream.Extension}";
                seen[stream.Extension] = index + 1;
                using var output = sink.Create(name);
                output.Write(stream.Data);
                output.Complete();
            }

            return HandleResult.Handled;
        }
        catch (Exception ex)
        {
            return HandleResult.Fail(ex.Message);
        }
    }
}

public sealed class PckDecryptHandler : IContentHandler
{
    public bool CanHandle(string relativePath) =>
        relativePath.EndsWith(".pck", StringComparison.OrdinalIgnoreCase);

    public HandleResult Handle(string relativePath, ReadOnlyMemory<byte> data, IOutputSink sink)
    {
        try
        {
            var archive = PckArchive.Parse(data.Span);
            byte[] plain = archive.ToPlainPck(data.Span);
            using var output = sink.Create(relativePath);
            output.Write(plain);
            output.Complete();
            return HandleResult.Handled;
        }
        catch (Exception ex)
        {
            return HandleResult.Fail(ex.Message);
        }
    }
}
