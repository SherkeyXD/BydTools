using System.Globalization;
using System.Text;

namespace BydTools.Formats.MemoryPack;

/// <summary>
/// Beyond StringPool blocks (<c>StringPool*.byte</c>, magic <c>USPC</c>).
/// <c>StringPool0.byte</c> may start with a little-endian <c>extraFileCount</c>
/// and only then <c>USPC</c>. Later blocks start at <c>USPC</c>.
/// Global id = <c>blockIndex * count(StringPool0) + localIndex</c>.
/// Ids <c>-1</c> and <c>-2</c> are the empty string and null, not pool slots.
/// </summary>
public sealed class BeyondStringPool
{
    private readonly string[][] _blocks;
    private readonly int _stride;

    public BeyondStringPool(IReadOnlyList<string[]> blocks)
    {
        if (blocks.Count == 0)
            throw new ArgumentException("String pool has no blocks.", nameof(blocks));
        _blocks = blocks.ToArray();
        _stride = blocks[0].Length;
        if (_stride <= 0)
            throw new InvalidDataException("StringPool0 has no strings; it is the global-id stride.");
    }

    public int BlockCount => _blocks.Length;
    public int Stride => _stride;

    public static BeyondStringPool Load(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"String pool directory not found: {directory}");

        var files = Directory
            .EnumerateFiles(directory, "StringPool*.byte", SearchOption.TopDirectoryOnly)
            .Select(path => (path, index: PoolIndex(path)))
            .Where(item => item.index >= 0)
            .OrderBy(item => item.index)
            .ToList();
        if (files.Count == 0)
            throw new InvalidDataException($"No StringPool*.byte files in \"{directory}\".");

        var blocks = new List<string[]>(files.Count);
        int expect = 0;
        foreach (var (path, index) in files)
        {
            if (index != expect)
            {
                throw new InvalidDataException(
                    $"String pool blocks must be contiguous from 0. Missing StringPool{expect}.byte."
                );
            }

            blocks.Add(ReadBlock(File.ReadAllBytes(path)));
            expect++;
        }

        return new BeyondStringPool(blocks);
    }

    /// <summary>
    /// One USPC block. Layout: magic, int32 uncompressed size, then that many raw bytes
    /// or an LZ4 block that expands to that size. The payload is int32 count, int32
    /// offsets[count] (relative to the string blob), then UTF-8 bytes.
    /// <c>StringPool0</c> may prefix that layout with a little-endian int32; when bytes 4..7 are <c>USPC</c>, that int32 is skipped.
    /// </summary>
    public static string[] ReadBlock(ReadOnlySpan<byte> file)
    {
        if (file.Length >= 8 && !IsUspc(file) && IsUspc(file.Slice(4, 4)))
            file = file.Slice(4);

        if (file.Length < 8 || !IsUspc(file))
            throw new InvalidDataException("String pool block does not start with USPC.");

        int uncompressed = BitConverter.ToInt32(file.Slice(4, 4));
        if (uncompressed < 4)
            throw new InvalidDataException($"USPC uncompressed size {uncompressed} is too small.");

        ReadOnlySpan<byte> payload;
        byte[]? owned = null;
        if (file.Length - 8 == uncompressed)
        {
            payload = file.Slice(8, uncompressed);
        }
        else
        {
            owned = Lz4Block.Decompress(file.Slice(8), uncompressed);
            payload = owned;
        }

        int count = BitConverter.ToInt32(payload);
        if (count < 0 || 4L + (long)count * 4 > payload.Length)
            throw new InvalidDataException($"USPC string count {count} does not fit the block.");

        int blob = 4 + count * 4;
        var strings = new string[count];
        for (int i = 0; i < count; i++)
        {
            int start = blob + BitConverter.ToInt32(payload.Slice(4 + i * 4, 4));
            int end = i + 1 < count
                ? blob + BitConverter.ToInt32(payload.Slice(4 + (i + 1) * 4, 4))
                : payload.Length;
            if (start < blob || end < start || end > payload.Length)
                throw new InvalidDataException($"USPC string {i} offset is outside the block.");
            int length = end - start;
            if (length > 0 && payload[end - 1] == 0)
                length--;
            strings[i] = Encoding.UTF8.GetString(payload.Slice(start, length));
        }

        return strings;
    }

    public bool TryResolve(int id, out string? value)
    {
        if (id == -1)
        {
            value = "";
            return true;
        }

        if (id == -2)
        {
            value = null;
            return true;
        }

        if (id < 0)
        {
            value = null;
            return false;
        }

        int block = id / _stride;
        int local = id - block * _stride;
        if ((uint)block >= (uint)_blocks.Length || (uint)local >= (uint)_blocks[block].Length)
        {
            value = null;
            return false;
        }

        value = _blocks[block][local];
        return true;
    }

    public string Format(int id)
    {
        if (TryResolve(id, out string? value))
            return value ?? "";
        return "#" + id.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsUspc(ReadOnlySpan<byte> span) =>
        span.Length >= 4
        && span[0] == (byte)'U'
        && span[1] == (byte)'S'
        && span[2] == (byte)'P'
        && span[3] == (byte)'C';

    private static int PoolIndex(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        const string prefix = "StringPool";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return -1;
        return int.TryParse(name.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            ? index
            : -1;
    }
}
