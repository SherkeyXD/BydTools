namespace BydTools.Audio.Wem;

internal static class WavWriter
{
    public static void WriteHeader(Stream stream, int channels, int sampleRate, int bitsPerSample, int dataBytes)
    {
        int blockAlign = channels * (bitsPerSample / 8);
        int avgBytesPerSec = sampleRate * blockAlign;
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)channels);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[28..], avgBytesPerSec);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)blockAlign);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(header[34..], (short)bitsPerSample);
        "data"u8.CopyTo(header[36..]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataBytes);
        stream.Write(header);
    }

    public static void PatchSizes(Stream stream, int dataBytes)
    {
        Span<byte> size = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(size, 36 + dataBytes);
        stream.Position = 4;
        stream.Write(size);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(size, dataBytes);
        stream.Position = 40;
        stream.Write(size);
    }
}
