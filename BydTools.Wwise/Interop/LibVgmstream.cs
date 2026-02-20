using System.Runtime.InteropServices;

namespace BydTools.Wwise.Interop;

/// <summary>
/// P/Invoke bindings for libvgmstream's public C API.
/// Requires libvgmstream.dll built with -DBUILD_SHARED_LIBS=ON.
/// </summary>
internal static partial class LibVgmstream
{
    private const string DllName = "libvgmstream";

    // ── version ──────────────────────────────────────────────────

    [LibraryImport(DllName, EntryPoint = "libvgmstream_get_version")]
    public static partial uint GetVersion();

    // ── lifecycle ────────────────────────────────────────────────

    [LibraryImport(DllName, EntryPoint = "libvgmstream_init")]
    public static partial nint Init();

    [LibraryImport(DllName, EntryPoint = "libvgmstream_free")]
    public static partial void Free(nint lib);

    // ── config ───────────────────────────────────────────────────

    [LibraryImport(DllName, EntryPoint = "libvgmstream_setup")]
    public static partial void Setup(nint lib, ref NativeConfig cfg);

    // ── stream ───────────────────────────────────────────────────

    [LibraryImport(DllName, EntryPoint = "libvgmstream_open_stream")]
    public static partial int OpenStream(nint lib, nint libsf, int subsong);

    [LibraryImport(DllName, EntryPoint = "libvgmstream_close_stream")]
    public static partial void CloseStream(nint lib);

    // ── decode ───────────────────────────────────────────────────

    [LibraryImport(DllName, EntryPoint = "libvgmstream_render")]
    public static partial int Render(nint lib);

    [LibraryImport(DllName, EntryPoint = "libvgmstream_fill")]
    public static partial int Fill(nint lib, nint buf, int bufSamples);

    // ── streamfile ───────────────────────────────────────────────

    [LibraryImport(DllName, EntryPoint = "libstreamfile_open_from_stdio", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint OpenStreamfileFromStdio(string filename);

    [LibraryImport(DllName, EntryPoint = "libstreamfile_open_buffered")]
    public static partial nint OpenStreamfileBuffered(nint extLibsf);

    // ── log ──────────────────────────────────────────────────────

    [LibraryImport(DllName, EntryPoint = "libvgmstream_set_log")]
    public static partial void SetLog(int level, nint callback);

    // ── structs ──────────────────────────────────────────────────

    public enum SampleFormat : int
    {
        Pcm16 = 1,
        Pcm24 = 2,
        Pcm32 = 3,
        Float = 4,
    }

    /// <remarks>
    /// Mirrors libvgmstream_format_t. Only the fields we need are mapped;
    /// total struct size must match native for pointer arithmetic to work.
    /// We read fields via Marshal.Read* from the raw pointer instead.
    /// </remarks>
    public static class Format
    {
        // Offsets within libvgmstream_format_t (x64, assumes standard packing)
        public const int OffsetChannels = 0;        // int
        public const int OffsetSampleRate = 4;      // int
        public const int OffsetSampleFormat = 8;    // int (enum)
        public const int OffsetSampleSize = 12;     // int
        public const int OffsetStreamSamples = 24;  // int64 (after channel_layout u32 + subsong_index int + subsong_count int + input_channels int = 16 bytes from offset 16 → 24? let me recalc)
    }

    /// <remarks>
    /// Mirrors libvgmstream_decoder_t.
    /// </remarks>
    public static class Decoder
    {
        public const int OffsetBuf = 0;             // void*
        public const int OffsetBufSamples = 8;      // int (after pointer on x64)
        public const int OffsetBufBytes = 12;       // int
        public const int OffsetDone = 16;           // bool (C99 _Bool = 1 byte, but may be aligned)
    }

    /// <remarks>
    /// Mirrors libvgmstream_t — the handle struct.
    /// </remarks>
    public static class Handle
    {
        public const int OffsetPriv = 0;            // void*
        public const int OffsetFormat = 8;          // const libvgmstream_format_t*
        public const int OffsetDecoder = 16;        // libvgmstream_decoder_t*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeConfig
    {
        public byte disable_config_override;
        public byte allow_play_forever;
        public byte play_forever;
        public byte ignore_loop;
        public byte force_loop;
        public byte really_force_loop;
        public byte ignore_fade;
        private byte _pad0;
        public double loop_count;
        public double fade_time;
        public double fade_delay;
        public int stereo_track;
        public int auto_downmix_channels;
        public int force_sfmt;
    }
}
