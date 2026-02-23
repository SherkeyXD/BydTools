using System.Runtime.InteropServices;
using BydTools.Wwise.Interop;

namespace BydTools.Wwise;

/// <summary>
/// Converts WEM files to WAV using libvgmstream.dll (in-process, no external CLI).
/// Thread-safe: each call creates its own vgmstream context.
/// </summary>
public sealed class LibVgmstreamConverter : IWemConverter
{
    private static bool? _available;

    public static bool IsAvailable
    {
        get
        {
            _available ??= ProbeLibrary();
            return _available.Value;
        }
    }

    public void Convert(string wemPath, string outputPath)
    {
        if (!File.Exists(wemPath))
            throw new FileNotFoundException("WEM file not found", wemPath);

        using var handle = VgmstreamHandle.Create();
        SetupHandle(handle);

        nint sf = LibVgmstream.OpenStreamfileFromStdio(wemPath);
        if (sf == 0)
            throw new InvalidOperationException($"Failed to open streamfile: {wemPath}");

        try
        {
            int result = handle.OpenStream(sf);
            if (result < 0)
                throw new InvalidOperationException(
                    $"libvgmstream failed to open stream (error {result})"
                );
        }
        finally
        {
            CloseStreamfile(sf);
        }

        DecodeAndWriteWav(handle, outputPath);
    }

    private static void SetupHandle(VgmstreamHandle handle)
    {
        var cfg = new LibVgmstream.NativeConfig
        {
            ignore_loop = 1,
            force_sfmt = (int)LibVgmstream.SampleFormat.Pcm16,
        };
        handle.Setup(cfg);
    }

    private static void DecodeAndWriteWav(VgmstreamHandle handle, string outputPath)
    {
        int channels = handle.Channels;
        int sampleRate = handle.SampleRate;
        int sampleSize = handle.SampleSize;
        long totalSamples = handle.StreamSamples;

        if (channels <= 0 || sampleRate <= 0 || totalSamples <= 0)
            throw new InvalidOperationException("Invalid stream format");

        int expectedBytes = (int)(totalSamples * channels * sampleSize);

        // Write WAV header then stream decoded data directly — no intermediate MemoryStream
        using var fs = File.Create(outputPath);
        using var writer = new BinaryWriter(fs);
        WavWriter.WriteHeader(writer, channels, sampleRate, sampleSize * 8, expectedBytes);

        int writtenBytes = 0;
        while (!handle.Done)
        {
            int renderResult = handle.Render();
            if (renderResult < 0)
                throw new InvalidOperationException(
                    $"libvgmstream render error: {renderResult}"
                );

            int bytes = handle.DecodedBytes;
            if (bytes <= 0)
                continue;

            nint buf = handle.DecodedBuf;
            unsafe
            {
                fs.Write(new ReadOnlySpan<byte>((void*)buf, bytes));
            }
            writtenBytes += bytes;
        }

        if (writtenBytes != expectedBytes)
        {
            fs.Seek(4, SeekOrigin.Begin);
            writer.Write(36 + writtenBytes);
            fs.Seek(40, SeekOrigin.Begin);
            writer.Write(writtenBytes);
        }
    }

    private static void CloseStreamfile(nint sf)
    {
        nint closeFnPtr = Marshal.ReadIntPtr(sf, 40);
        if (closeFnPtr != 0)
        {
            var closeFn = Marshal.GetDelegateForFunctionPointer<CloseDelegate>(closeFnPtr);
            closeFn(sf);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseDelegate(nint libsf);

    private static bool ProbeLibrary()
    {
        try
        {
            _ = LibVgmstream.GetVersion();
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
