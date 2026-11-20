using System.Buffers;
using System.Globalization;
using System.Text;

namespace BydTools.Audio.Pck;

/// <summary>
/// Resolves Wwise ids to paths using an AudioDialog entry list.
/// Ids are FNV-1 64-bit hashes of <c>{soundType}/{language}/{path}</c>.
/// </summary>
public sealed class PckMapper
{
    private const ulong FnvOffset = 0xcbf29ce484222325;
    private const ulong FnvPrime = 0x100000001b3;
    private static readonly string[] KnownSoundTypes = ["voice", "music", "sfx"];

    private readonly Dictionary<ulong, string> _idToPath;

    public PckMapper(IEnumerable<string> audioPaths, string language)
    {
        _idToPath = Parse(audioPaths, language);
    }

    public int Count => _idToPath.Count;

    public string? GetMappedPath(ulong fileId)
    {
        if (_idToPath.TryGetValue(fileId, out string? path))
            return path;

        ulong low = (uint)fileId;
        if (low != fileId && _idToPath.TryGetValue(low, out path))
            return path;
        return null;
    }

    internal static ulong Fnv1_64(ReadOnlySpan<byte> data)
    {
        ulong hash = FnvOffset;
        foreach (byte b in data)
        {
            hash = unchecked(hash * FnvPrime);
            hash ^= b;
        }

        return hash;
    }

    private static Dictionary<ulong, string> Parse(IEnumerable<string> audioPaths, string language)
    {
        string langLower = language.ToLowerInvariant();
        byte[][] prefixUtf8 = KnownSoundTypes
            .Select(type => Encoding.UTF8.GetBytes($"{type}/{langLower}/"))
            .ToArray();

        var map = new Dictionary<ulong, string>();
        Span<char> charStack = stackalloc char[512];
        Span<char> lowerStack = stackalloc char[512];
        Span<byte> byteStack = stackalloc byte[512];

        foreach (string rawPath in audioPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            ReadOnlySpan<char> trimmed = rawPath.AsSpan().Trim();
            int len = trimmed.Length;
            char[]? rentedChars = null;
            Span<char> normalizedChars = len <= charStack.Length
                ? charStack[..len]
                : (rentedChars = ArrayPool<char>.Shared.Rent(len)).AsSpan(0, len);

            char[]? rentedLower = null;
            Span<char> lowerChars = len <= lowerStack.Length
                ? lowerStack[..len]
                : (rentedLower = ArrayPool<char>.Shared.Rent(len)).AsSpan(0, len);

            try
            {
                for (int i = 0; i < len; i++)
                    normalizedChars[i] = trimmed[i] == '\\' ? '/' : trimmed[i];

                ReadOnlySpan<char> outputTail = normalizedChars.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)
                    ? normalizedChars[..^4]
                    : normalizedChars;

                int lowerLen = normalizedChars.ToLowerInvariant(lowerChars);
                int maxUtf8 = Encoding.UTF8.GetMaxByteCount(lowerLen);
                byte[]? rentedBytes = null;
                Span<byte> pathUtf8 = maxUtf8 <= byteStack.Length
                    ? byteStack[..maxUtf8]
                    : (rentedBytes = ArrayPool<byte>.Shared.Rent(maxUtf8)).AsSpan(0, maxUtf8);

                try
                {
                    int utf8Len = Encoding.UTF8.GetBytes(lowerChars[..lowerLen], pathUtf8);
                    ReadOnlySpan<byte> pathBytes = pathUtf8[..utf8Len];

                    for (int s = 0; s < KnownSoundTypes.Length; s++)
                    {
                        ulong hash = Fnv1_64Append(prefixUtf8[s], pathBytes);
                        if (!map.ContainsKey(hash))
                            map.Add(hash, $"{KnownSoundTypes[s]}/{langLower}/{outputTail}");
                    }
                }
                finally
                {
                    if (rentedBytes != null)
                        ArrayPool<byte>.Shared.Return(rentedBytes);
                }
            }
            finally
            {
                if (rentedChars != null)
                    ArrayPool<char>.Shared.Return(rentedChars);
                if (rentedLower != null)
                    ArrayPool<char>.Shared.Return(rentedLower);
            }
        }

        return map;
    }

    private static ulong Fnv1_64Append(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> tail)
    {
        ulong hash = FnvOffset;
        foreach (byte b in prefix)
        {
            hash = unchecked(hash * FnvPrime);
            hash ^= b;
        }

        foreach (byte b in tail)
        {
            hash = unchecked(hash * FnvPrime);
            hash ^= b;
        }

        return hash;
    }
}
