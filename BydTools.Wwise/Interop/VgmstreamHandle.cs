using System.Runtime.InteropServices;

namespace BydTools.Wwise.Interop;

/// <summary>
/// Safe wrapper around a libvgmstream context handle.
/// Reads native struct fields via pointer offsets.
/// </summary>
internal sealed class VgmstreamHandle : IDisposable
{
    private nint _handle;

    public nint Ptr =>
        _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(VgmstreamHandle));

    public bool IsValid => _handle != 0;

    private VgmstreamHandle(nint handle) => _handle = handle;

    public static VgmstreamHandle Create()
    {
        nint h = LibVgmstream.Init();
        return h == 0
            ? throw new InvalidOperationException("libvgmstream_init returned NULL")
            : new VgmstreamHandle(h);
    }

    // ── format access ────────────────────────────────────────────

    /// <summary>Pointer to the libvgmstream_format_t struct.</summary>
    private nint FormatPtr => Marshal.ReadIntPtr(_handle, nint.Size); // offset 8 on x64

    /// <summary>Pointer to the libvgmstream_decoder_t struct.</summary>
    private nint DecoderPtr => Marshal.ReadIntPtr(_handle, nint.Size * 2); // offset 16 on x64

    public int Channels => Marshal.ReadInt32(FormatPtr, 0);
    public int SampleRate => Marshal.ReadInt32(FormatPtr, 4);
    public LibVgmstream.SampleFormat SampleFmt =>
        (LibVgmstream.SampleFormat)Marshal.ReadInt32(FormatPtr, 8);
    public int SampleSize => Marshal.ReadInt32(FormatPtr, 12);

    /// <summary>
    /// stream_samples field offset within libvgmstream_format_t.
    /// Layout: channels(4) + sample_rate(4) + sample_format(4) + sample_size(4)
    ///       + channel_layout(4) + subsong_index(4) + subsong_count(4) + input_channels(4)
    ///       = 32 bytes → stream_samples is at offset 32 (int64).
    /// </summary>
    public long StreamSamples => Marshal.ReadInt64(FormatPtr, 32);

    // ── decoder access ───────────────────────────────────────────

    public nint DecodedBuf => Marshal.ReadIntPtr(DecoderPtr, 0);
    public int DecodedSamples => Marshal.ReadInt32(DecoderPtr, nint.Size);
    public int DecodedBytes => Marshal.ReadInt32(DecoderPtr, nint.Size + 4);
    public bool Done => Marshal.ReadByte(DecoderPtr, nint.Size + 8) != 0;

    // ── operations ───────────────────────────────────────────────

    public void Setup(LibVgmstream.NativeConfig cfg) => LibVgmstream.Setup(_handle, ref cfg);

    public int OpenStream(nint streamfile, int subsong = 0) =>
        LibVgmstream.OpenStream(_handle, streamfile, subsong);

    public void CloseStream() => LibVgmstream.CloseStream(_handle);

    public int Render() => LibVgmstream.Render(_handle);

    // ── dispose ──────────────────────────────────────────────────

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0)
            LibVgmstream.Free(h);
    }
}
