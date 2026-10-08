namespace BydTools.Audio.Wem;

public interface IWemDecoder : IDisposable
{
    void Decode(ReadOnlyMemory<byte> wem, Stream wavOutput);
}
