namespace BydTools.VFS;

public static class VFSDefine
{
    public const string ChaChaKeyPcBase64 = "6VsxesT4KFadI6hr8nHctT6Eb6dckk1nHbqOOPTKUuE=";
    public const string ChaChaKeyAndroidBase64 = "eU1cu+MYQiaYdVherRzV86pv/N/lIU/9gIk+5n5Vj4Y=";

    public static readonly byte[] PcKey = Convert.FromBase64String(ChaChaKeyPcBase64);
    public static readonly byte[] AndroidKey = Convert.FromBase64String(ChaChaKeyAndroidBase64);

    public const string VfsDirectoryName = "VFS";
    public const int ProtocolVersion = 3;
    public const int NonceLength = 12;
    public const int KeyLength = 32;

    public static byte[] ResolveKey(string? platform, string? keyBase64)
    {
        if (!string.IsNullOrWhiteSpace(keyBase64))
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(keyBase64);
            }
            catch (FormatException)
            {
                throw new ArgumentException("--key must be a valid Base64 string.");
            }

            if (key.Length != KeyLength)
            {
                throw new ArgumentException(
                    $"--key must decode to {KeyLength} bytes (got {key.Length})."
                );
            }

            return key;
        }

        return (platform ?? "pc").ToLowerInvariant() switch
        {
            "pc" => PcKey,
            "android" => AndroidKey,
            _ => throw new ArgumentException("--platform must be one of: pc, android."),
        };
    }
}
