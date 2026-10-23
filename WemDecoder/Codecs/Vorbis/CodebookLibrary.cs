using System.Buffers.Binary;

namespace WemDecoder.Codecs.Vorbis;

/// <summary>
/// External Wwise Vorbis codebooks, in ww2ogg's packed format: all codebooks back to back,
/// followed by a table of u32 offsets whose last entry points at the table itself.
/// </summary>
internal sealed class CodebookLibrary
{
    private static readonly Lazy<CodebookLibrary> StandardLibrary = new(() => Load("packed_codebooks.bin"));
    private static readonly Lazy<CodebookLibrary> AoTuV603Library = new(() => Load("packed_codebooks_aoTuV_603.bin"));

    private readonly byte[] _data;
    private readonly int[] _offsets;

    private CodebookLibrary(byte[] data, int[] offsets)
    {
        _data = data;
        _offsets = offsets;
    }

    /// <summary>Codebooks used by Wwise before 2012 (vgmstream: <c>vcb_list_standard</c>).</summary>
    public static CodebookLibrary Standard => StandardLibrary.Value;

    /// <summary>aoTuV 6.03 codebooks used by later Wwise (vgmstream: <c>vcb_list_aotuv603</c>).</summary>
    public static CodebookLibrary AoTuV603 => AoTuV603Library.Value;

    public int Count => _offsets.Length - 1;

    public bool TryGet(uint id, out ReadOnlySpan<byte> codebook)
    {
        if (id >= Count)
        {
            codebook = default;
            return false;
        }

        codebook = _data.AsSpan(_offsets[id], _offsets[id + 1] - _offsets[id]);
        return true;
    }

    internal static CodebookLibrary Parse(byte[] data)
    {
        if (data.Length < 8)
            throw new InvalidDataException("Codebook library is too small.");

        int tableOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4)));
        if (tableOffset > data.Length - 4 || (data.Length - tableOffset) % 4 != 0)
            throw new InvalidDataException("Codebook library has an invalid offset table.");

        int count = (data.Length - tableOffset) / 4;
        var offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            offsets[i] = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(tableOffset + i * 4)));
            if (offsets[i] > tableOffset || (i > 0 && offsets[i] < offsets[i - 1]))
                throw new InvalidDataException("Codebook library has an invalid offset table.");
        }

        return new CodebookLibrary(data, offsets);
    }

    private static CodebookLibrary Load(string name)
    {
        using var stream = typeof(CodebookLibrary).Assembly.GetManifestResourceStream("WemDecoder.Resources." + name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return Parse(data);
    }
}
