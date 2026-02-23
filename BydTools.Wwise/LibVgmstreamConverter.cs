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

    /// <summary>
    /// Whether libvgmstream.dll is loadable on this system.
    /// </summary>
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

    public void Convert(byte[] wemData, string wemName, string outputPath)
    {
        using var handle = VgmstreamHandle.Create();
        SetupHandle(handle);

        using var memSf = MemoryStreamfile.Create(wemData, wemName);

        int result = handle.OpenStream(memSf.Ptr);
        if (result < 0)
            throw new InvalidOperationException(
                $"libvgmstream failed to open stream (error {result})"
            );

        memSf.ReleaseStruct();

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

        long totalBytes = totalSamples * channels * sampleSize;
        if (totalBytes > int.MaxValue)
            throw new InvalidOperationException($"Stream too large: {totalBytes} bytes");

        using var ms = new MemoryStream((int)totalBytes);

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
                ms.Write(new ReadOnlySpan<byte>((void*)buf, bytes));
            }
        }

        using var fs = File.Create(outputPath);
        using var writer = new BinaryWriter(fs);
        WavWriter.WriteHeader(writer, channels, sampleRate, sampleSize * 8, (int)ms.Length);
        ms.Position = 0;
        ms.CopyTo(fs);
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
