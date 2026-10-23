using System.Buffers.Binary;
using System.Diagnostics;
using WemDecoder.Codecs.Vorbis;
using WemDecoder.Tests.Support;

namespace WemDecoder.Tests;

/// <summary>Runs only when <c>VGMSTREAM_CLI</c> points at a vgmstream-cli executable.</summary>
public sealed class VgmstreamTheoryAttribute : TheoryAttribute
{
    public VgmstreamTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VGMSTREAM_CLI")))
            Skip = "Set VGMSTREAM_CLI to a vgmstream-cli executable to compare against vgmstream.";
    }
}

/// <summary>Decodes the synthetic test files with vgmstream-cli and compares the results.</summary>
public class VgmstreamParityTests
{
    private static readonly Dictionary<string, (Func<byte[]> Build, double MinSnrDb)> Cases = CreateCases();

    public static TheoryData<string> CaseNames() => [.. Cases.Keys];

    [VgmstreamTheory]
    [MemberData(nameof(CaseNames))]
    public void MatchesVgmstream(string name)
    {
        var (build, minSnrDb) = Cases[name];
        byte[] wem = build();
        var (channels, sampleRate, expected) = DecodeWithVgmstream(wem);

        using (var reader = new WemReader(wem))
        {
            Assert.Equal(channels, reader.Channels);
            Assert.Equal(sampleRate, reader.SampleRate);
            Assert.Equal(expected.Length / channels, reader.SampleCount);
        }

        short[] actual = TestAudio.DecodeAll(wem);
        if (double.IsPositiveInfinity(minSnrDb))
        {
            Assert.Equal(expected, actual);
            return;
        }

        for (int c = 0; c < channels; c++)
        {
            double signal = 0, noise = 0;
            for (int i = c; i < expected.Length; i += channels)
            {
                double d = actual[i] - expected[i];
                signal += (double)expected[i] * expected[i];
                noise += d * d;
            }

            double snr = noise == 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / noise);
            Assert.True(snr >= minSnrDb, $"channel {c}: {snr:0.0} dB");
        }
    }

    private static Dictionary<string, (Func<byte[]>, double)> CreateCases()
    {
        var cases = new Dictionary<string, (Func<byte[]>, double)>();
        foreach (var (channels, bigEndian, fmtSize) in new[] { (1, false, 0x10), (2, true, 0x18), (6, false, 0x28) })
        {
            cases[$"pcm {channels}ch fmt 0x{fmtSize:X} {(bigEndian ? "RIFX" : "RIFF")}"] = (
                () => PcmTests.Build(channels, 44100, PcmTests.RandomSamples(channels * 3000, seed: channels), bigEndian, fmtSize),
                double.PositiveInfinity);
        }

        foreach (var (layoutName, layout) in OpusTests.Layouts)
        {
            cases[$"opus {layoutName}"] = (
                () => SyntheticOpusWem.Build(layout, TestAudio.Tones(layout.Channels, 24000, 48000)),
                40);
        }

        foreach (var row in VorbisTests.Layouts())
        {
            string fixture = (string)row[0];
            var version = Enum.Parse<WwiseVorbisVersion>((string)row[1]);
            bool bigEndian = (bool)row[2];
            cases[$"vorbis {fixture} {version}{(bigEndian ? " RIFX" : "")}"] = (
                () => SyntheticVorbisWem.TryBuild(VorbisStream.FromOgg(TestAudio.Fixture(fixture)), version, bigEndian)!,
                80);
        }

        return cases;
    }

    private static (int Channels, int SampleRate, short[] Samples) DecodeWithVgmstream(byte[] wem)
    {
        var directory = Directory.CreateTempSubdirectory("wemdecoder-parity-");
        try
        {
            string input = Path.Combine(directory.FullName, "audio.wem");
            string output = Path.Combine(directory.FullName, "audio.wav");
            File.WriteAllBytes(input, wem);

            var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("VGMSTREAM_CLI")!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in new[] { "-i", "-o", output, input })
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            _ = stdout.Result;
            Assert.True(process.ExitCode == 0 && File.Exists(output), $"vgmstream-cli failed: {stderr}");

            return ReadWav(File.ReadAllBytes(output));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static (int Channels, int SampleRate, short[] Samples) ReadWav(byte[] wav)
    {
        int channels = 0, sampleRate = 0;
        for (int offset = 12; offset + 8 <= wav.Length;)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4));
            var body = wav.AsSpan(offset + 8, Math.Min(size, wav.Length - offset - 8));
            if (wav.AsSpan(offset, 4).SequenceEqual("fmt "u8))
            {
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
            }
            else if (wav.AsSpan(offset, 4).SequenceEqual("data"u8))
            {
                var samples = new short[body.Length / 2];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(body[(i * 2)..]);
                return (channels, sampleRate, samples);
            }

            offset += 8 + size + (size & 1);
        }

        throw new InvalidDataException("No data chunk in vgmstream output.");
    }
}
