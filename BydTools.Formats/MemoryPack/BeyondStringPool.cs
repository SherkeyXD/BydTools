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
    /// One USPC block. After an optional leading int32 (when bytes 4..7 are <c>USPC</c>),
    /// the 24-byte header is magic, version 1, flags (bit 0 = LZ4), count, rawLength, storedLength.
    /// The payload is innerCount, then int32 offsets relative to <c>4 + 4*count</c>.
    /// Each string is an int32 byte length followed by that many UTF-8 bytes.
    /// </summary>
    public static string[] ReadBlock(ReadOnlySpan<byte> file)
    {
        if (file.Length >= 8 && !IsUspc(file) && IsUspc(file.Slice(4, 4)))
            file = file.Slice(4);

        const int HeaderSize = 24;
        if (file.Length < HeaderSize || !IsUspc(file))
            throw new InvalidDataException("String pool block does not start with USPC.");

        int version = ReadInt32(file, 4);
        if (version != 1)
            throw new InvalidDataException($"USPC version {version} is not 1.");
        int flags = ReadInt32(file, 8);
        int count = ReadInt32(file, 12);
        int rawLength = ReadInt32(file, 16);
        int storedLength = ReadInt32(file, 20);
        if (count < 0 || rawLength < 4 || storedLength < 0)
            throw new InvalidDataException($"USPC header is invalid (count {count}, raw {rawLength}, stored {storedLength}).");
        if (file.Length < HeaderSize + storedLength)
            throw new InvalidDataException($"USPC block is shorter than the stored payload ({storedLength} bytes).");

        ReadOnlySpan<byte> stored = file.Slice(HeaderSize, storedLength);
        ReadOnlySpan<byte> raw;
        if ((flags & 1) != 0)
        {
            raw = Lz4Block.Decompress(stored, rawLength);
        }
        else
        {
            if (rawLength > storedLength)
                throw new InvalidDataException($"USPC raw length {rawLength} exceeds the stored payload ({storedLength}).");
            raw = stored.Slice(0, rawLength);
        }

        int innerCount = ReadInt32(raw, 0);
        if (innerCount != count)
            throw new InvalidDataException($"USPC payload count {innerCount} does not match the header count {count}.");
        long tableBytes = 4L + (long)count * 4;
        if (tableBytes > raw.Length)
            throw new InvalidDataException($"USPC offset table for {count} strings does not fit the payload.");

        int baseOffset = 4 + count * 4;
        var strings = new string[count];
        for (int i = 0; i < count; i++)
        {
            int relative = ReadInt32(raw, 4 + i * 4);
            int room = raw.Length - baseOffset;
            if (relative < 0 || relative > room - 4)
                throw new InvalidDataException($"USPC string {i} offset is outside the payload.");
            int at = baseOffset + relative;
            int byteLength = ReadInt32(raw, at);
            if (byteLength < 0 || at + 4L + byteLength > raw.Length)
                throw new InvalidDataException($"USPC string {i} length {byteLength} is outside the payload.");
            strings[i] = Encoding.UTF8.GetString(raw.Slice(at + 4, byteLength));
        }

        return strings;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset) => BitConverter.ToInt32(data.Slice(offset, 4));

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
