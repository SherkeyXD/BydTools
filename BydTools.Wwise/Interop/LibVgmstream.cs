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

    [LibraryImport(
        DllName,
        EntryPoint = "libstreamfile_open_from_stdio",
        StringMarshalling = StringMarshalling.Utf8
    )]
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
