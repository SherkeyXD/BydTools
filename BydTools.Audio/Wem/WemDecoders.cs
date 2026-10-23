using WemDecoder;

namespace BydTools.Audio.Wem;

public static class WemDecoders
{
    public static IWemDecoder Create() => new ManagedWemDecoder();
}

/// <summary>Decodes WEM with the managed <see cref="WemConverter"/>.</summary>
public sealed class ManagedWemDecoder : IWemDecoder
{
    public void Decode(ReadOnlyMemory<byte> wem, Stream wavOutput) => WemConverter.ConvertToWav(wem, wavOutput);

    public void Dispose()
    {
    }
}
