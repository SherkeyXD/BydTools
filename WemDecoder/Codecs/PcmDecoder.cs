using System.Buffers.Binary;
using WemDecoder.Internal;

namespace WemDecoder.Codecs;

internal sealed class PcmDecoder : SampleDecoder
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _channels;
    private readonly bool _bigEndian;
    private int _offset;
    private readonly int _end;

    private PcmDecoder(ReadOnlyMemory<byte> data, int channels, bool bigEndian, int offset, int end)
    {
        _data = data;
        _channels = channels;
        _bigEndian = bigEndian;
        _offset = offset;
        _end = end;
    }

    public static PcmDecoder Create(WemHeader header, ReadOnlyMemory<byte> file, out long sampleCount)
    {
        if (header.FmtSize is not (0x10 or 0x12 or 0x18 or 0x28))
            throw new InvalidDataException($"PCM: unexpected fmt size 0x{header.FmtSize:X}.");
        if (header.BitsPerSample != 16)
            throw new NotSupportedException($"PCM: {header.BitsPerSample}-bit samples are not supported.");

        long dataSize = header.Prefetch ? header.FileSize - header.DataOffset : header.DataSize;
        int frameSize = 2 * header.Channels;
        sampleCount = dataSize / frameSize;
        // vgmstream keeps header-only prefetch streams playable as a single silent sample.
        if (header.Prefetch && sampleCount == 0)
            sampleCount = 1;

        long end = Math.Min(header.DataOffset + dataSize, header.FileSize);
        long frames = Math.Max(0, end - header.DataOffset) / frameSize;
        return new PcmDecoder(file, header.Channels, header.BigEndian, header.DataOffset, (int)(header.DataOffset + frames * frameSize));
    }

    public override int Read(Span<short> destination)
    {
        int frames = Math.Min(destination.Length / _channels, (_end - _offset) / (2 * _channels));
        int count = frames * _channels;
        var source = _data.Span.Slice(_offset, count * 2);
        for (int i = 0; i < count; i++)
        {
            var bytes = source.Slice(i * 2, 2);
            destination[i] = _bigEndian ? BinaryPrimitives.ReadInt16BigEndian(bytes) : BinaryPrimitives.ReadInt16LittleEndian(bytes);
        }

        _offset += count * 2;
        return frames;
    }
}
