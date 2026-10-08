using BydTools.Audio.Pck;
using BydTools.Extraction.Sink;
using BydTools.Formats.Lua;
using BydTools.Formats.SparkBuffer;
using BydTools.Formats.Usm;

namespace BydTools.Extraction.Vfs;

public enum HandleResult
{
    Handled,
    Skipped,
    Failed,
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
        catch (Exception)
        {
            return HandleResult.Failed;
        }
    }
}

public sealed class LuaHandler : IContentHandler
{
    public bool CanHandle(string relativePath) => true;

    public HandleResult Handle(string relativePath, ReadOnlyMemory<byte> data, IOutputSink sink)
    {
        try
        {
            byte[]? decrypted = LuaDecipher.Decrypt(data.Span);
            if (decrypted == null || !LuaDecipher.IsValid(decrypted))
                return HandleResult.Failed;
            using var output = sink.Create(Path.ChangeExtension(relativePath, ".lua"));
            output.Write(decrypted);
            output.Complete();
            return HandleResult.Handled;
        }
        catch (Exception)
        {
            return HandleResult.Failed;
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
        catch (Exception)
        {
            return HandleResult.Failed;
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
        catch (Exception)
        {
            return HandleResult.Failed;
        }
    }
}
