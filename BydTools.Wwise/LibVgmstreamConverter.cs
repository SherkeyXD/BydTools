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

        // Configure: no looping, output PCM16
        var cfg = new LibVgmstream.NativeConfig
        {
            ignore_loop = 1,
            force_sfmt = (int)LibVgmstream.SampleFormat.Pcm16,
        };
        handle.Setup(cfg);

        // Open via stdio streamfile (vgmstream handles caching internally)
        nint sf = LibVgmstream.OpenStreamfileFromStdio(wemPath);
        if (sf == 0)
            throw new InvalidOperationException($"Failed to open streamfile: {wemPath}");

        try
        {
            int result = handle.OpenStream(sf);
            if (result < 0)
                throw new InvalidOperationException($"libvgmstream failed to open stream (error {result})");
        }
        finally
        {
            // streamfile can be closed after open — vgmstream re-opens internally
            CloseStreamfile(sf);
        }

        int channels = handle.Channels;
        int sampleRate = handle.SampleRate;
        int sampleSize = handle.SampleSize; // 2 for PCM16
        long totalSamples = handle.StreamSamples;

        if (channels <= 0 || sampleRate <= 0 || totalSamples <= 0)
            throw new InvalidOperationException("Invalid stream format");

        long totalBytes = totalSamples * channels * sampleSize;
        if (totalBytes > int.MaxValue)
            throw new InvalidOperationException($"Stream too large: {totalBytes} bytes");

        // Decode all samples
        using var ms = new MemoryStream((int)totalBytes);

        while (!handle.Done)
        {
            int renderResult = handle.Render();
            if (renderResult < 0)
                throw new InvalidOperationException($"libvgmstream render error: {renderResult}");

            int bytes = handle.DecodedBytes;
            if (bytes <= 0)
                continue;

            nint buf = handle.DecodedBuf;
            unsafe
            {
                ms.Write(new ReadOnlySpan<byte>((void*)buf, bytes));
            }
        }

        // Write WAV
        using var fs = File.Create(outputPath);
        using var writer = new BinaryWriter(fs);
        WavWriter.WriteHeader(writer, channels, sampleRate, sampleSize * 8, (int)ms.Length);
        ms.Position = 0;
        ms.CopyTo(fs);
    }

    private static void CloseStreamfile(nint sf)
    {
        // libstreamfile_t.close is at a known offset — but the simplest safe
        // approach is to call the inline helper equivalent. Since libstreamfile_close
        // is a static inline in the header (not exported), we read the function pointer
        // from the struct and call it.
        //
        // struct libstreamfile_t layout (x64):
        //   void* user_data;          // +0
        //   int (*read)(...);         // +8
        //   int64_t (*get_size)(...); // +16
        //   const char* (*get_name)(...); // +24
        //   struct* (*open)(...);     // +32
        //   void (*close)(...);       // +40
        nint closeFnPtr = Marshal.ReadIntPtr(sf, 40);
        if (closeFnPtr != 0)
        {
            // close(libstreamfile_t* libsf) — takes the struct pointer itself
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
