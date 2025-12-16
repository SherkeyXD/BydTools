using System.Reflection;
using System.Runtime.InteropServices;
using BnkExtractor;
using NAudio.Lame;
using NAudio.Wave;
using NVorbis;

namespace BydTools.PCK;

/// <summary>
/// Provides audio format conversion functionality.
/// </summary>
public class AudioConverter
{
    static AudioConverter()
    {
        // 确保 NAudio.Lame 的原生 DLL 能被找到
        // 触发 Extractor 的静态构造函数以初始化 DLL 搜索路径
        _ = typeof(Extractor);
    }

    /// <summary>
    /// Converts OGG file to MP3.
    /// </summary>
    /// <param name="oggPath">Path to the OGG file.</param>
    /// <param name="mp3Path">Path to the output MP3 file.</param>
    /// <param name="bitrate">MP3 bitrate in kbps (default: 192).</param>
    public void ConvertOggToMp3(string oggPath, string mp3Path, int bitrate = 192)
    {
        if (!File.Exists(oggPath))
            throw new FileNotFoundException("OGG file not found", oggPath);

        using var reader = new VorbisReader(oggPath);
        var sampleRate = reader.SampleRate;
        var channels = reader.Channels;
        var waveFormat = new WaveFormat(sampleRate, 16, channels);

        using var writer = new LameMP3FileWriter(mp3Path, waveFormat, bitrate);
        var buffer = new float[4096 * channels];
        var pcmBuffer = new byte[buffer.Length * 2]; // 16-bit samples = 2 bytes per sample
        int samplesRead;

        while ((samplesRead = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            // Convert float samples to 16-bit PCM
            for (int i = 0; i < samplesRead; i++)
            {
                short sample = (short)(Math.Max(-1.0f, Math.Min(1.0f, buffer[i])) * 32767);
                pcmBuffer[i * 2] = (byte)(sample & 0xFF);
                pcmBuffer[i * 2 + 1] = (byte)(sample >> 8);
            }

            writer.Write(pcmBuffer, 0, samplesRead * 2 * channels);
        }
    }

    /// <summary>
    /// Converts WEM file to OGG.
    /// Note: This is a placeholder implementation. For full WEM to OGG conversion,
    /// you may need to use external tools like ww2ogg or implement the full conversion logic.
    /// </summary>
    /// <param name="wemPath">Path to the WEM file.</param>
    /// <param name="oggPath">Path to the output OGG file.</param>
    /// <param name="codebooksPath">Path to packed_codebooks.bin (required for WEM conversion).</param>
    public void ConvertWemToOgg(string wemPath, string oggPath, string? codebooksPath = null)
    {
        if (!File.Exists(wemPath))
            throw new FileNotFoundException("WEM file not found", wemPath);

        // Check if it's a RIFF WAV file (WEM files are RIFF-based)
        byte[] header = new byte[4];
        using (var fs = File.OpenRead(wemPath))
        {
            fs.ReadExactly(header);
        }

        if (!header.SequenceEqual("RIFF"u8))
        {
            throw new InvalidDataException("File does not appear to be a RIFF WEM file");
        }

        // For now, we'll provide a basic implementation that attempts to convert
        // This is a simplified version - full implementation would require parsing
        // the RIFF structure, extracting Vorbis packets, and reconstructing OGG

        // TODO: Implement full WEM to OGG conversion based on ww2ogg logic
        // This would involve:
        // 1. Parsing RIFF chunks (fmt, vorb, data)
        // 2. Extracting Vorbis setup and audio packets
        // 3. Rebuilding codebooks if needed
        // 4. Writing OGG pages with proper headers

        throw new NotImplementedException(
            "Full WEM to OGG conversion is not yet implemented. Please use external tools like ww2ogg for now."
        );
    }
}
