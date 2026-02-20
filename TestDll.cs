using System;
using System.Runtime.InteropServices;

[DllImport("libvgmstream", EntryPoint = "libvgmstream_get_version")]
static extern uint GetVersion();

[DllImport("libvgmstream", EntryPoint = "libvgmstream_init")]
static extern nint Init();

[DllImport("libvgmstream", EntryPoint = "libvgmstream_free")]
static extern void Free(nint lib);

[DllImport("libvgmstream", EntryPoint = "libvgmstream_setup")]
static extern void Setup(nint lib, ref NativeConfig cfg);

[DllImport("libvgmstream", EntryPoint = "libstreamfile_open_from_stdio", CharSet = CharSet.Ansi)]
static extern nint OpenSF(string filename);

[DllImport("libvgmstream", EntryPoint = "libvgmstream_open_stream")]
static extern int OpenStream(nint lib, nint sf, int subsong);

[StructLayout(LayoutKind.Sequential)]
struct NativeConfig
{
    public byte disable_config_override;
    public byte allow_play_forever;
    public byte play_forever;
    public byte ignore_loop;
    public byte force_loop;
    public byte really_force_loop;
    public byte ignore_fade;
    byte _pad0;
    public double loop_count;
    public double fade_time;
    public double fade_delay;
    public int stereo_track;
    public int auto_downmix_channels;
    public int force_sfmt;
}

try
{
    uint ver = GetVersion();
    Console.WriteLine($"libvgmstream version: {ver:X8}");

    nint lib = Init();
    Console.WriteLine($"Init handle: 0x{lib:X}");

    var cfg = new NativeConfig { ignore_loop = 1, force_sfmt = 1 };
    Setup(lib, ref cfg);
    Console.WriteLine("Setup done");

    // Test with Opus file
    string testFile = @"E:\EndGame\output_test\unmapped\10002378318055705610.wem";
    nint sf = OpenSF(testFile);
    Console.WriteLine($"OpenSF: 0x{sf:X}");

    if (sf != 0)
    {
        int result = OpenStream(lib, sf, 0);
        Console.WriteLine($"OpenStream result: {result}");
    }

    Free(lib);
    Console.WriteLine("Done");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
}
