using System.Diagnostics;
using WemDecoder.Codecs;
using WemDecoder.Codecs.Opus;
using WemDecoder.Codecs.Vorbis;
using WemDecoder.Internal;

namespace WemDecoder;

/// <summary>
/// Decodes a Wwise WEM stream to interleaved 16-bit PCM.
/// </summary>
/// <remarks>
/// Output matches vgmstream with looping disabled: exactly <see cref="SampleCount"/> frames,
/// with silence wherever the stream is truncated or fails to decode.
/// Instances are not thread-safe; use one reader per stream.
/// </remarks>
public sealed class WemReader : IDisposable
{
    private const int MaxChannels = 64;
    private const int MinSampleRate = 300;
    private const int MaxSampleRate = 192000;
    private const long MaxSampleCount = 1_000_000_000;

    private readonly SampleDecoder _decoder;
    private bool _decoderEnded;
    private bool _disposed;

    /// <summary>
    /// Parses the WEM header and prepares the decoder.
    /// </summary>
    /// <param name="data">The complete WEM file. It must not change while the reader is in use.</param>
    /// <exception cref="InvalidDataException">The data is not a valid WEM stream.</exception>
    /// <exception cref="NotSupportedException">The WEM uses a codec or layout this library doesn't decode.</exception>
    public WemReader(ReadOnlyMemory<byte> data)
    {
        var header = WemHeader.Parse(data.Span);
        if (header.Channels is < 1 or > MaxChannels)
            throw new InvalidDataException($"Invalid channel count {header.Channels}.");

        long sampleCount;
        int sampleRate = header.SampleRate;
        switch (header.Codec)
        {
            case WemCodec.Pcm:
                _decoder = PcmDecoder.Create(header, data, out sampleCount);
                break;
            case WemCodec.Vorbis:
                _decoder = WwiseVorbisDecoder.Create(header, data, out sampleCount);
                break;
            case WemCodec.Opus:
                _decoder = WwiseOpusDecoder.Create(header, data, out sampleCount);
                sampleRate = WwiseOpusDecoder.SampleRate;
                break;
            default:
                throw new UnreachableException();
        }

        if (sampleCount is <= 0 or > MaxSampleCount || sampleRate is < MinSampleRate or > MaxSampleRate)
        {
            _decoder.Dispose();
            throw new InvalidDataException($"Invalid stream: {sampleCount} samples at {sampleRate} Hz.");
        }

        Codec = header.Codec;
        Channels = header.Channels;
        SampleRate = sampleRate;
        SampleCount = sampleCount;
    }

    /// <summary>Codec of the stream.</summary>
    public WemCodec Codec { get; }

    /// <summary>Number of interleaved channels.</summary>
    public int Channels { get; }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Total length in sample frames (samples per channel).</summary>
    public long SampleCount { get; }

    /// <summary>Number of sample frames read so far.</summary>
    public long Position { get; private set; }

    /// <summary>
    /// Reads interleaved samples into <paramref name="buffer"/>, filling whole frames only.
    /// </summary>
    /// <returns>Number of samples written (a multiple of <see cref="Channels"/>); 0 at the end of the stream.</returns>
    public int Read(Span<short> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int frames = (int)Math.Min(buffer.Length / Channels, SampleCount - Position);
        if (frames <= 0)
            return 0;

        var target = buffer[..(frames * Channels)];
        int done = 0;
        while (done < frames && !_decoderEnded)
        {
            int read = _decoder.Read(target[(done * Channels)..]);
            if (read == 0)
                _decoderEnded = true;
            done += read;
        }

        target[(done * Channels)..].Clear();
        Position += frames;
        return target.Length;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _decoder.Dispose();
    }
}
