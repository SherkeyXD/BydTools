using BydTools.Audio.Pck;
using BydTools.Extraction.Sink;
using BydTools.Formats.Bundle;
using BydTools.Formats.Lua;
using BydTools.Formats.Manifest;
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
            using var output = sink.Create(Path.ChangeExtension(relativePath, ".json"));
            SparkBuffer.WriteJson(data.Span, output);
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
            JsonDataFileResult result = JsonDataMemoryPack.DecodeToStream(
                _decoder,
                _schema,
                data,
                relativePath,
                write =>
                {
                    using var file = sink.Create(relativePath);
                    write(file);
                    file.Complete();
                }
            );
            if (result.Kind is JsonDataFileKind.Decoded or JsonDataFileKind.PlainJson)
                return HandleResult.Handled;

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
            string basePath = Path.ChangeExtension(relativePath, null) ?? relativePath;
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int emitted = CriUsmDemuxer.Demux(data.Span, (ext, payload) =>
            {
                seen.TryGetValue(ext, out int index);
                string name = index == 0 ? basePath + ext : $"{basePath}_{index}{ext}";
                seen[ext] = index + 1;
                using var output = sink.Create(name);
                output.Write(payload);
                output.Complete();
            });
            return emitted == 0 ? HandleResult.Skipped : HandleResult.Handled;
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

public sealed class BundleHandler : IContentHandler
{
    public bool CanHandle(string relativePath) =>
        Path.GetExtension(relativePath).Equals(".ab", StringComparison.OrdinalIgnoreCase);

    public HandleResult Handle(string relativePath, ReadOnlyMemory<byte> data, IOutputSink sink)
    {
        try
        {
            if (EndfieldBundle.IsStandardUnityBundle(data.Span))
                return HandleResult.Skipped;

            if (!EndfieldBundle.IsEndfieldVfsBundle(data.Span))
                return HandleResult.Fail("Unrecognized .ab bundle header.");

            using var output = sink.Create(relativePath);
            EndfieldBundle.ConvertToStandardUnityFs(data.Span, output);
            output.Complete();
            return HandleResult.Handled;
        }
        catch (Exception ex)
        {
            return HandleResult.Fail(ex.Message);
        }
    }
}

public sealed class BundleManifestHandler : IContentHandler
{
    public bool CanHandle(string relativePath) =>
        Path.GetExtension(relativePath).Equals(".hgmmap", StringComparison.OrdinalIgnoreCase);

    public HandleResult Handle(string relativePath, ReadOnlyMemory<byte> data, IOutputSink sink)
    {
        try
        {
            using var output = sink.Create(relativePath + ".bin");
            HgmmapManifest.DecompressBrotli(data.Span, output);
            output.Complete();
            return HandleResult.Handled;
        }
        catch (Exception ex)
        {
            return HandleResult.Fail(ex.Message);
        }
    }
}
