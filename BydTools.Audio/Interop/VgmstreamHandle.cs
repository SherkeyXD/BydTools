using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BydTools.Audio.Interop;

internal sealed unsafe class VgmstreamHandle : IDisposable
{
    private nint _handle;

    private VgmstreamHandle(nint handle) => _handle = handle;

    public static VgmstreamHandle Create()
    {
        nint handle = LibVgmstream.Init();
        return handle == 0
            ? throw new InvalidOperationException("libvgmstream_init returned NULL.")
            : new VgmstreamHandle(handle);
    }

    public void Setup(LibVgmstream.NativeConfig config) => LibVgmstream.Setup(_handle, ref config);

    public int Open(nint streamfile) => LibVgmstream.OpenStream(_handle, streamfile, 0);

    public int Render() => LibVgmstream.Render(_handle);

    public ref LibVgmstream.NativeFormat Format
    {
        get
        {
            var format = ((LibVgmstream.NativeHandle*)_handle)->Format;
            if (format == 0)
                throw new InvalidOperationException("libvgmstream format is not available.");
            return ref Unsafe.AsRef<LibVgmstream.NativeFormat>((void*)format);
        }
    }

    public ref LibVgmstream.NativeDecoder Decoder
    {
        get
        {
            var decoder = ((LibVgmstream.NativeHandle*)_handle)->Decoder;
            if (decoder == 0)
                throw new InvalidOperationException("libvgmstream decoder is not available.");
            return ref Unsafe.AsRef<LibVgmstream.NativeDecoder>((void*)decoder);
        }
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
            LibVgmstream.Free(handle);
    }
}
