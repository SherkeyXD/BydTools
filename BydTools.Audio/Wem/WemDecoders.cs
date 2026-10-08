using System.Diagnostics;
using System.Runtime.InteropServices;
using BydTools.Audio.Interop;
using BydTools.Core;

namespace BydTools.Audio.Wem;

public static class WemDecoders
{
    public static IWemDecoder? TryCreate(ILogger logger)
    {
        if (LibVgmstreamDecoder.IsAvailable)
        {
            logger.Verbose("Engine: libvgmstream (DLL)");
            return new LibVgmstreamDecoder();
        }

        string? cli = VgmstreamCliDecoder.Find();
        if (cli != null)
        {
            logger.Verbose("Engine: vgmstream-cli ({0})", cli);
            return new VgmstreamCliDecoder(cli);
        }

        return null;
    }
}

public sealed class LibVgmstreamDecoder : IWemDecoder
{
    private static readonly Lazy<bool> Available = new(Probe);

    public static bool IsAvailable => Available.Value;

    public unsafe void Decode(ReadOnlyMemory<byte> wem, Stream wavOutput)
    {
        using var handle = VgmstreamHandle.Create();
        var config = new LibVgmstream.NativeConfig
        {
            ignore_loop = 1,
            force_sfmt = (int)LibVgmstream.SampleFormat.Pcm16,
        };
        handle.Setup(config);

        using (var streamfile = MemoryStreamfile.Create(wem))
        {
            int opened = handle.Open(streamfile.Native);
            if (opened < 0)
                throw new InvalidOperationException($"libvgmstream failed to open the stream (error {opened}).");
        }

        ref var format = ref handle.Format;
        if (format.Channels <= 0 || format.SampleRate <= 0 || format.StreamSamples <= 0)
            throw new InvalidOperationException("Invalid stream format.");

        int expected = checked((int)(format.StreamSamples * format.Channels * format.SampleSize));
        WavWriter.WriteHeader(wavOutput, format.Channels, format.SampleRate, format.SampleSize * 8, expected);

        int written = 0;
        while (handle.Decoder.Done == 0)
        {
            int rendered = handle.Render();
            if (rendered < 0)
                throw new InvalidOperationException($"libvgmstream render error: {rendered}");

            ref var decoder = ref handle.Decoder;
            if (decoder.BufferBytes <= 0 || decoder.Buffer == 0)
                continue;
            wavOutput.Write(new ReadOnlySpan<byte>((void*)decoder.Buffer, decoder.BufferBytes));
            written += decoder.BufferBytes;
        }

        if (written != expected)
            WavWriter.PatchSizes(wavOutput, written);
    }

    public void Dispose() { }

    private static bool Probe()
    {
        try
        {
            uint version = LibVgmstream.GetVersion();
            return version >> 24 == 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}

public sealed class VgmstreamCliDecoder : IWemDecoder
{
    private readonly string _executable;
    private readonly string _tempDirectory;

    public VgmstreamCliDecoder(string executable)
    {
        _executable = executable;
        _tempDirectory = Path.Combine(Path.GetTempPath(), "bydtools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public static string? Find()
    {
        string name = OperatingSystem.IsWindows() ? "vgmstream-cli.exe" : "vgmstream-cli";
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null)
        {
            string local = Path.Combine(exeDir, name);
            if (File.Exists(local))
                return local;
            string nested = Path.Combine(exeDir, "vgmstream", name);
            if (File.Exists(nested))
                return nested;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv == null)
            return null;
        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException) { }
        }

        return null;
    }

    public void Decode(ReadOnlyMemory<byte> wem, Stream wavOutput)
    {
        string wemPath = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".wem");
        string wavPath = Path.ChangeExtension(wemPath, ".wav");
        File.WriteAllBytes(wemPath, wem.Span);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = _executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add(wavPath);
            start.ArgumentList.Add(wemPath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start vgmstream-cli.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            string error = stderr.GetAwaiter().GetResult();
            stdout.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                string trimmed = error.Trim();
                if (trimmed.Length > 300)
                    trimmed = trimmed[..300] + "...";
                throw new InvalidOperationException($"vgmstream-cli exited with code {process.ExitCode}: {trimmed}");
            }

            using var wav = File.OpenRead(wavPath);
            if (wav.Length == 0)
                throw new InvalidOperationException("vgmstream-cli produced no output.");
            wav.CopyTo(wavOutput);
        }
        finally
        {
            TryDelete(wemPath);
            TryDelete(wavPath);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException) { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
    }
}
