using System.Runtime.InteropServices;

namespace BydTools.Audio.Interop;

internal static partial class LibVgmstream
{
    private const string DllName = "libvgmstream";

    [LibraryImport(DllName, EntryPoint = "libvgmstream_get_version")]
    public static partial uint GetVersion();

    [LibraryImport(DllName, EntryPoint = "libvgmstream_init")]
    public static partial nint Init();

    [LibraryImport(DllName, EntryPoint = "libvgmstream_free")]
    public static partial void Free(nint lib);

    [LibraryImport(DllName, EntryPoint = "libvgmstream_setup")]
    public static partial void Setup(nint lib, ref NativeConfig cfg);

    [LibraryImport(DllName, EntryPoint = "libvgmstream_open_stream")]
    public static partial int OpenStream(nint lib, nint libsf, int subsong);

    [LibraryImport(DllName, EntryPoint = "libvgmstream_render")]
    public static partial int Render(nint lib);

    public enum SampleFormat : int
    {
        Pcm16 = 1,
        Pcm24 = 2,
        Pcm32 = 3,
        Float = 4,
    }

    /// <summary>Matches libvgmstream_config_t from API 1.x.</summary>
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

    /// <summary>Leading fields of libvgmstream_t. Later fields are not read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeHandle
    {
        public nint Priv;
        public nint Format;
        public nint Decoder;
    }

    /// <summary>
    /// Leading fields of libvgmstream_format_t. stream_samples is int64 at offset 32.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeFormat
    {
        public int Channels;
        public int SampleRate;
        public int SampleFormat;
        public int SampleSize;
        public uint ChannelLayout;
        public int SubsongIndex;
        public int SubsongCount;
        public int InputChannels;
        public long StreamSamples;
    }

    /// <summary>Leading fields of libvgmstream_decoder_t.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeDecoder
    {
        public nint Buffer;
        public int BufferSamples;
        public int BufferBytes;
        public byte Done;
    }
}
