using BydTools.Audio.Wem;
using Xunit;

namespace BydTools.Tests;

public class NativeLibraryTests
{
    [Fact]
    public void Bundled_libvgmstream_loads_when_present()
    {
        string fileName = OperatingSystem.IsWindows()
            ? "libvgmstream.dll"
            : OperatingSystem.IsMacOS()
                ? "libvgmstream.dylib"
                : "libvgmstream.so";
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, fileName)))
            return;

        Assert.True(LibVgmstreamDecoder.IsAvailable);
    }
}
