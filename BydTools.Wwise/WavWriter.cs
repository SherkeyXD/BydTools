namespace BydTools.Wwise;

/// <summary>
/// Minimal WAV file writer for PCM16 interleaved audio.
/// </summary>
internal static class WavWriter
{
    public static void WriteHeader(
        BinaryWriter w,
        int channels,
        int sampleRate,
        int bitsPerSample,
        int dataBytes
    )
    {
        int blockAlign = channels * (bitsPerSample / 8);
        int avgBytesPerSec = sampleRate * blockAlign;

        // RIFF header
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes); // file size - 8
        w.Write("WAVE"u8);

        // fmt chunk
        w.Write("fmt "u8);
        w.Write(16); // chunk size
        w.Write((short)1); // PCM format
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(avgBytesPerSec);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);

        // data chunk header
        w.Write("data"u8);
        w.Write(dataBytes);
    }

    /// <summary>
    /// Creates a complete WAV file from raw PCM data.
    /// </summary>
    public static void Write(
        string path,
        int channels,
        int sampleRate,
        int bitsPerSample,
        byte[] pcmData
    )
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        WriteHeader(w, channels, sampleRate, bitsPerSample, pcmData.Length);
        w.Write(pcmData);
    }
}
