using System.Diagnostics;

namespace BydTools.Wwise;

/// <summary>
/// Converts Wwise WEM audio files to WAV via vgmstream-cli.
/// Supports all codecs that vgmstream can decode: Wwise Vorbis, Opus, PCM, ADPCM, PTADPCM, etc.
/// </summary>
public sealed class WemConverter : IWemConverter
{
    private static readonly Lazy<string?> _vgmstreamPath = new(FindVgmstream);

    /// <summary>
    /// Path to the vgmstream-cli executable, or <c>null</c> if not found.
    /// </summary>
    public static string? VgmstreamPath => _vgmstreamPath.Value;

    public void Convert(string wemPath, string outputPath)
    {
        if (!File.Exists(wemPath))
            throw new FileNotFoundException("WEM file not found", wemPath);

        string vgmstream =
            _vgmstreamPath.Value
            ?? throw new FileNotFoundException(
                "vgmstream-cli not found. Place vgmstream-cli next to the executable or add it to PATH."
            );

        var psi = new ProcessStartInfo
        {
            FileName = vgmstream,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputPath);
        psi.ArgumentList.Add(wemPath);

        using var proc =
            Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start vgmstream-cli");

        proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"vgmstream-cli exited with code {proc.ExitCode}: {Truncate(stderr)}"
            );

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidOperationException("vgmstream-cli produced no output");
    }

    private static string Truncate(string msg)
    {
        string trimmed = msg.Trim();
        return trimmed.Length > 300 ? trimmed[..300] + "..." : trimmed;
    }

    private static string? FindVgmstream()
    {
        string name = OperatingSystem.IsWindows() ? "vgmstream-cli.exe" : "vgmstream-cli";

        // 1. Next to the current executable
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir != null)
        {
            string local = Path.Combine(exeDir, name);
            if (File.Exists(local))
                return local;

            // Also check a vgmstream/ subdirectory
            string sub = Path.Combine(exeDir, "vgmstream", name);
            if (File.Exists(sub))
                return sub;
        }

        // 2. PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }
        }

        return null;
    }
}
