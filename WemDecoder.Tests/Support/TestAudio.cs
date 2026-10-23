using WemDecoder.Codecs.Vorbis.NVorbis;
using WemDecoder.Internal;

namespace WemDecoder.Tests.Support;

internal static class TestAudio
{
    public static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static short[] DecodeAll(byte[] wem)
    {
        using var reader = new WemReader(wem);
        var samples = new short[reader.SampleCount * reader.Channels];
        int done = 0;
        int read;
        // Odd-sized reads exercise partial-buffer handling in the decoders.
        while ((read = reader.Read(samples.AsSpan(done, Math.Min(samples.Length - done, 1001 * reader.Channels)))) > 0)
            done += read;

        Assert.Equal(samples.Length, done);
        Assert.Equal(0, reader.Read(new short[reader.Channels]));
        return samples;
    }

    /// <summary>
    /// The original Ogg packets through the same Vorbis decoder, trimmed to the last granule
    /// and converted to 16-bit like vgmstream does.
    /// </summary>
    public static short[] DecodeOgg(byte[] ogg)
    {
        var stream = VorbisStream.FromOgg(ogg);
        int next = 0;
        var decoder = new StreamDecoder(stream.Identification, stream.Comment, stream.Setup,
            () => next < stream.AudioPackets.Count ? stream.AudioPackets[next++] : null);

        var floats = new float[stream.SampleCount * stream.Channels];
        int done = 0;
        int read;
        while (done < floats.Length && (read = decoder.Read(floats.AsSpan(done))) > 0)
            done += read;

        Assert.Equal(floats.Length, done);
        var samples = new short[floats.Length];
        SampleDecoder.FloatToPcm16(floats, samples);
        return samples;
    }

    /// <summary>The signal the Ogg fixtures were encoded from: per channel a tone and a chirp, plus clicks on channel 0.</summary>
    public static float[] FixtureSignal(int channels, int frames, int sampleRate)
    {
        var samples = new float[channels * frames];
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / sampleRate;
            for (int c = 0; c < channels; c++)
            {
                double tone = 0.25 * Math.Sin(2 * Math.PI * (220.0 * (c + 2) + 37.0 * c) * t);
                double chirp = 0.15 * Math.Sin(2 * Math.PI * (100.0 + 2000.0 * t) * t * (1 + 0.1 * c));
                double click = c == 0 && i % (sampleRate / 4) < 40 ? ((i & 1) != 0 ? 0.5 : -0.5) : 0;
                samples[i * channels + c] = (float)(tone + chirp + click);
            }
        }

        return samples;
    }

    /// <summary>Tones of different frequencies per channel, at a quarter of full scale.</summary>
    public static float[] Tones(int channels, int frames, int sampleRate)
    {
        var samples = new float[channels * frames];
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++)
                samples[i * channels + c] = (float)(0.25 * Math.Sin(2 * Math.PI * ToneFrequency(c) * i / sampleRate));
        }

        return samples;
    }

    public static double ToneFrequency(int channel) => 400 + 300 * channel;

    /// <summary>Which of the <see cref="Tones"/> frequencies dominates each channel.</summary>
    public static int[] DominantTones(ReadOnlySpan<short> samples, int channels, int sampleRate)
    {
        int frames = samples.Length / channels;
        int start = frames / 4;
        int count = frames / 2;
        var result = new int[channels];
        for (int c = 0; c < channels; c++)
        {
            double best = -1;
            for (int tone = 0; tone < channels; tone++)
            {
                double power = Goertzel(samples, channels, c, ToneFrequency(tone), sampleRate, start, count);
                if (power > best)
                    (best, result[c]) = (power, tone);
            }
        }

        return result;
    }

    /// <summary>Signal-to-noise ratio of <paramref name="actual"/> against <paramref name="expected"/> for one channel.</summary>
    public static double SnrDb(ReadOnlySpan<short> actual, ReadOnlySpan<float> expected, int channels, int channel, int start, int count)
    {
        double signal = 0, noise = 0;
        for (int i = start; i < start + count; i++)
        {
            double e = expected[i * channels + channel] * 32767.0;
            double d = actual[i * channels + channel] - e;
            signal += e * e;
            noise += d * d;
        }

        return 10 * Math.Log10(signal / noise);
    }

    private static double Goertzel(ReadOnlySpan<short> samples, int channels, int channel, double frequency, int sampleRate, int start, int count)
    {
        double coefficient = 2 * Math.Cos(2 * Math.PI * frequency / sampleRate);
        double s1 = 0, s2 = 0;
        for (int i = start; i < start + count; i++)
        {
            double s = samples[i * channels + channel] + coefficient * s1 - s2;
            s2 = s1;
            s1 = s;
        }

        return s1 * s1 + s2 * s2 - coefficient * s1 * s2;
    }
}
