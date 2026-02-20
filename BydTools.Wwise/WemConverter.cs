using System.Reflection;
using System.Runtime.InteropServices;
using BydTools.Wwise.Ww2ogg;

namespace BydTools.Wwise;

/// <summary>
/// Converts Wwise WEM audio files to OGG via ww2ogg + revorb.
/// Manages native DLL loading and embedded codebook resources.
/// </summary>
public sealed class WemConverter : IWemConverter
{
    static WemConverter()
    {
        string appDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(appDir))
            appDir = ".";

        if (!Directory.Exists(appDir))
            return;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            SetDllDirectory(appDir);

            string thirdPartyDir = Path.Combine(appDir, "3rdParty");
            if (Directory.Exists(thirdPartyDir))
            {
                string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!currentPath.Contains(thirdPartyDir))
                    Environment.SetEnvironmentVariable("PATH", $"{thirdPartyDir};{currentPath}", EnvironmentVariableTarget.Process);
            }

            string currentPath2 = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!currentPath2.Contains(appDir))
                Environment.SetEnvironmentVariable("PATH", $"{appDir};{currentPath2}", EnvironmentVariableTarget.Process);
        }
        catch
        {
            // DLL should still load from application directory
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    public void RevorbOgg(string inputPath, string? outputPath = null) =>
        Revorb.RevorbSharp.Convert(inputPath, outputPath ?? Path.ChangeExtension(inputPath, ".revorb.ogg"));

    private static readonly Lazy<string> _codebooksPath = new(ExtractCodebooks);

    public string ConvertWem(string wemPath)
    {
        if (!File.Exists(wemPath))
            throw new FileNotFoundException("WEM file not found", wemPath);

        string oggPath = Path.ChangeExtension(wemPath, "ogg");

        Ww2oggOptions options = new()
        {
            InFilename = wemPath,
            OutFilename = oggPath,
            CodebooksFilename = _codebooksPath.Value,
        };

        Ww2oggConverter.Main(options);

        if (!File.Exists(oggPath))
            throw new FileNotFoundException($"OGG file was not created: {oggPath}");

        return oggPath;
    }

    /// <summary>
    /// Extracts the embedded codebooks resource to a temp file once.
    /// Thread-safe via Lazy&lt;T&gt;.
    /// </summary>
    private static string ExtractCodebooks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "BydTools.Wwise");
        Directory.CreateDirectory(tempDir);
        string codebooksPath = Path.Combine(tempDir, "packed_codebooks_aoTuV_603.bin");

        if (File.Exists(codebooksPath))
            return codebooksPath;

        Assembly assembly = Assembly.GetExecutingAssembly();
        string[] allResources = assembly.GetManifestResourceNames();
        string? resourceName = allResources.FirstOrDefault(n =>
            n.Contains("packed_codebooks") && n.EndsWith(".bin"));

        using Stream resourceStream = assembly.GetManifestResourceStream(resourceName!)
            ?? throw new FileNotFoundException("Embedded ww2ogg codebooks resource not found.");

        using FileStream fs = File.Create(codebooksPath);
        resourceStream.CopyTo(fs);

        return codebooksPath;
    }
}
