using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace WemDecoder;

/// <summary>
/// Converts WEM files to 16-bit PCM WAV.
/// </summary>
public static class WemConverter
{
    private const int HeaderSize = 44;
    private const int FramesPerBlock = 4096;

    /// <summary>
    /// Decodes <paramref name="wem"/> and writes a 16-bit PCM WAV file to <paramref name="output"/>.
    /// </summary>
    /// <remarks>The output stream doesn't need to be seekable.</remarks>
    /// <exception cref="InvalidDataException">The data is not a valid WEM stream.</exception>
    /// <exception cref="NotSupportedException">The WEM uses a codec or layout this library doesn't decode.</exception>
    public static void ConvertToWav(ReadOnlyMemory<byte> wem, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        using var reader = new WemReader(wem);
        long dataBytes = reader.SampleCount * reader.Channels * sizeof(short);
        if (dataBytes > uint.MaxValue - (HeaderSize - 8))
            throw new InvalidDataException("Decoded audio is too large for a WAV file.");

        WriteHeader(output, reader.Channels, reader.SampleRate, (uint)dataBytes);

        short[] buffer = ArrayPool<short>.Shared.Rent(FramesPerBlock * reader.Channels);
        try
        {
            int count;
            while ((count = reader.Read(buffer)) > 0)
            {
                var samples = buffer.AsSpan(0, count);
                if (!BitConverter.IsLittleEndian)
                    BinaryPrimitives.ReverseEndianness(samples, samples);
                output.Write(MemoryMarshal.AsBytes(samples));
            }
        }
        finally
        {
            ArrayPool<short>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Decodes <paramref name="wem"/> to a 16-bit PCM WAV file in memory.
    /// </summary>
    /// <inheritdoc cref="ConvertToWav(ReadOnlyMemory{byte}, Stream)" path="/exception"/>
    public static byte[] ConvertToWav(ReadOnlyMemory<byte> wem)
    {
        using var output = new MemoryStream();
        ConvertToWav(wem, output);
        return output.ToArray();
    }

    private static void WriteHeader(Stream output, int channels, int sampleRate, uint dataBytes)
    {
        int blockAlign = channels * sizeof(short);
        Span<byte> header = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], dataBytes + (HeaderSize - 8));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1); // WAVE_FORMAT_PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], dataBytes);
        output.Write(header);
    }
}
