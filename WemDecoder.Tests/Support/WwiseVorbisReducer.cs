using WemDecoder.Codecs.Vorbis;

namespace WemDecoder.Tests.Support;

internal enum CodebookStorage
{
    /// <summary>Standard codebooks, setup otherwise untouched (Wwise v38).</summary>
    Full,
    /// <summary>Wwise-reduced codebooks inline (v44).</summary>
    Inline,
    /// <summary>Ids into the standard library (v48-v56).</summary>
    Standard,
    /// <summary>Ids into the aoTuV 6.03 library (v62).</summary>
    AoTuV603,
}

/// <summary>The stripped Vorbis setup Wwise stores, plus what's needed to strip audio packets.</summary>
internal sealed record ReducedSetup(byte[] Packet, bool[] ModeBlockFlags, int ModeBits);

/// <summary>
/// Converts standard Vorbis setup and audio packets into Wwise's reduced forms:
/// the inverse of the rebuild under test, written from the Vorbis I specification.
/// </summary>
internal static class WwiseVorbisReducer
{
    private static readonly Lazy<Dictionary<string, uint>> StandardIds = new(() => IndexLibrary(CodebookLibrary.Standard));
    private static readonly Lazy<Dictionary<string, uint>> AoTuV603Ids = new(() => IndexLibrary(CodebookLibrary.AoTuV603));

    /// <returns>The reduced setup, or <see langword="null"/> when a codebook is missing from the requested library.</returns>
    public static ReducedSetup? TryReduceSetup(byte[] setup, int channels, CodebookStorage storage)
    {
        var r = new BitReader(setup);
        var w = new BitWriter();
        for (int i = 0; i < 7; i++)
            r.Read(8); // 0x05 "vorbis"

        uint codebookCount = r.Read(8) + 1;
        w.Write(8, codebookCount - 1);
        for (uint i = 0; i < codebookCount; i++)
        {
            long start = r.Position;
            switch (storage)
            {
                case CodebookStorage.Full:
                    ReduceCodebook(ref r, new BitWriter());
                    CopyBits(setup, start, r.Position - start, w);
                    break;
                case CodebookStorage.Inline:
                    ReduceCodebook(ref r, w);
                    break;
                default:
                    ReduceCodebook(ref r, new BitWriter());
                    var ids = storage == CodebookStorage.Standard ? StandardIds.Value : AoTuV603Ids.Value;
                    if (!ids.TryGetValue(BitsKey(setup, start, r.Position - start), out uint id))
                        return null;
                    w.Write(10, id);
                    break;
            }
        }

        uint timeCount = r.Read(6) + 1;
        for (uint i = 0; i < timeCount; i++)
            Expect(r.Read(16), 0, "time domain type");

        if (storage == CodebookStorage.Full)
        {
            // Everything after the time domain stays as-is, up to and including the framing bit.
            long tailStart = r.Position;
            var (blockFlags, modeBits) = ReadStandardTail(ref r, channels, writer: null);
            CopyBits(setup, tailStart, r.Position - tailStart, w);
            w.PadToByte();
            return new ReducedSetup(w.ToArray(), blockFlags, modeBits);
        }

        var (flags, bits) = ReadStandardTail(ref r, channels, w);
        w.PadToByte();
        return new ReducedSetup(w.ToArray(), flags, bits);
    }

    /// <summary>Drops the packet type bit and, for long windows, the previous/next window flags.</summary>
    public static byte[] ReduceAudioPacket(byte[] packet, ReducedSetup setup)
    {
        var r = new BitReader(packet);
        var w = new BitWriter(packet.Length + 1);
        Expect(r.Read(1), 0, "audio packet type");
        uint mode = r.Read(setup.ModeBits);
        w.Write(setup.ModeBits, mode);
        if (setup.ModeBlockFlags[mode])
            r.Read(2);

        CopyBits(packet, r.Position, packet.Length * 8L - r.Position, w);
        return w.ToArray();
    }

    /// <summary>Floors, residues, mappings and modes; writes the reduced form when <paramref name="writer"/> is set.</summary>
    private static (bool[] BlockFlags, int ModeBits) ReadStandardTail(ref BitReader r, int channels, BitWriter? writer)
    {
        var w = writer ?? new BitWriter();

        uint floorCount = Copy(ref r, w, 6) + 1;
        for (uint i = 0; i < floorCount; i++)
        {
            Expect(r.Read(16), 1, "floor type");
            uint partitions = Copy(ref r, w, 5);
            // ww2ogg always reads class 0, so a floor without partitions has no Wwise form.
            if (partitions == 0 && writer is not null)
                throw new NotSupportedException("Floor without partitions.");

            var classes = new uint[partitions];
            int maximumClass = -1;
            for (int j = 0; j < partitions; j++)
            {
                classes[j] = Copy(ref r, w, 4);
                maximumClass = Math.Max(maximumClass, (int)classes[j]);
            }

            var dimensions = new uint[maximumClass + 1];
            for (int j = 0; j <= maximumClass; j++)
            {
                dimensions[j] = Copy(ref r, w, 3) + 1;
                uint subclasses = Copy(ref r, w, 2);
                if (subclasses != 0)
                    Copy(ref r, w, 8);
                for (int k = 0; k < 1 << (int)subclasses; k++)
                    Copy(ref r, w, 8);
            }

            Copy(ref r, w, 2);
            int rangeBits = (int)Copy(ref r, w, 4);
            for (int j = 0; j < partitions; j++)
            {
                for (int k = 0; k < dimensions[classes[j]]; k++)
                    Copy(ref r, w, rangeBits);
            }
        }

        uint residueCount = Copy(ref r, w, 6) + 1;
        for (uint i = 0; i < residueCount; i++)
        {
            uint type = r.Read(16);
            w.Write(2, type);
            Copy(ref r, w, 24);
            Copy(ref r, w, 24);
            Copy(ref r, w, 24);
            uint classifications = Copy(ref r, w, 6) + 1;
            Copy(ref r, w, 8);
            var cascade = new uint[classifications];
            for (int j = 0; j < classifications; j++)
            {
                uint low = Copy(ref r, w, 3);
                uint high = Copy(ref r, w, 1) != 0 ? Copy(ref r, w, 5) : 0;
                cascade[j] = high * 8 + low;
            }

            foreach (uint bits in cascade)
            {
                for (int k = 0; k < 8; k++)
                {
                    if ((bits & (1u << k)) != 0)
                        Copy(ref r, w, 8);
                }
            }
        }

        uint mappingCount = Copy(ref r, w, 6) + 1;
        for (uint i = 0; i < mappingCount; i++)
        {
            Expect(r.Read(16), 0, "mapping type");
            uint submaps = Copy(ref r, w, 1) != 0 ? Copy(ref r, w, 4) + 1 : 1;
            if (Copy(ref r, w, 1) != 0)
            {
                uint steps = Copy(ref r, w, 8) + 1;
                int bits = WwiseVorbisSetup.ILog((uint)channels - 1);
                for (uint j = 0; j < steps * 2; j++)
                    Copy(ref r, w, bits);
            }

            Copy(ref r, w, 2);
            if (submaps > 1)
            {
                for (int j = 0; j < channels; j++)
                    Copy(ref r, w, 4);
            }

            for (uint j = 0; j < submaps * 3; j++)
                Copy(ref r, w, 8);
        }

        uint modeCount = Copy(ref r, w, 6) + 1;
        var blockFlags = new bool[65];
        for (int i = 0; i < modeCount; i++)
        {
            blockFlags[i] = Copy(ref r, w, 1) != 0;
            Expect(r.Read(16), 0, "window type");
            Expect(r.Read(16), 0, "transform type");
            Copy(ref r, w, 8);
        }

        Expect(r.Read(1), 1, "framing bit");
        return (blockFlags, WwiseVorbisSetup.ILog(modeCount - 1));
    }

    /// <summary>Reads one standard codebook and writes it in Wwise's reduced form.</summary>
    private static void ReduceCodebook(ref BitReader r, BitWriter w)
    {
        Expect(r.Read(24), 0x564342, "codebook sync");
        uint dimensions = r.Read(16);
        uint entries = r.Read(24);
        w.Write(4, dimensions);
        w.Write(14, entries);
        if (dimensions >= 16 || entries >= 1 << 14)
            throw new InvalidDataException("Codebook too large for the reduced form.");

        uint ordered = Copy(ref r, w, 1);
        if (ordered != 0)
        {
            uint length = Copy(ref r, w, 5) + 1;
            for (uint current = 0; current < entries; length++)
                current += Copy(ref r, w, WwiseVorbisSetup.ILog(entries - current));
        }
        else
        {
            uint sparse = r.Read(1);
            var lengths = new List<(bool Present, uint LengthLess1)>();
            for (uint i = 0; i < entries; i++)
            {
                bool present = sparse == 0 || r.Read(1) != 0;
                lengths.Add((present, present ? r.Read(5) : 0));
            }

            uint maxValue = lengths.Max(l => l.LengthLess1);
            int lengthBits = Math.Max(1, WwiseVorbisSetup.ILog(maxValue));
            w.Write(3, (uint)lengthBits);
            w.Write(1, sparse);
            foreach (var (present, lengthLess1) in lengths)
            {
                if (sparse != 0)
                    w.Write(1, present ? 1u : 0u);
                if (present)
                    w.Write(lengthBits, lengthLess1);
            }
        }

        uint lookupType = r.Read(4);
        if (lookupType > 1)
            throw new InvalidDataException("Lookup type 2 has no reduced form.");
        w.Write(1, lookupType);
        if (lookupType == 1)
        {
            Copy(ref r, w, 32);
            Copy(ref r, w, 32);
            int valueBits = (int)Copy(ref r, w, 4) + 1;
            Copy(ref r, w, 1);
            uint quantvals = WwiseVorbisSetup.BookMaptype1Quantvals(entries, dimensions);
            for (uint i = 0; i < quantvals; i++)
                Copy(ref r, w, valueBits);
        }
    }

    /// <summary>Maps each library codebook, rebuilt to standard form, to its id.</summary>
    private static Dictionary<string, uint> IndexLibrary(CodebookLibrary library)
    {
        var ids = new Dictionary<string, uint>();
        for (uint id = 0; id < library.Count; id++)
        {
            library.TryGet(id, out var codebook);
            var w = new BitWriter();
            var r = new BitReader(codebook);
            if (!WwiseVorbisSetup.TryRebuildCodebook(w, ref r, codebook.Length))
                throw new InvalidDataException($"Library codebook {id} does not rebuild.");
            ids.TryAdd(BitsKey(w.ToArray(), 0, w.Position), id);
        }

        return ids;
    }

    private static string BitsKey(ReadOnlySpan<byte> data, long start, long count)
    {
        var w = new BitWriter();
        CopyBits(data, start, count, w);
        return $"{count}:{Convert.ToHexString(w.WrittenSpan)}";
    }

    private static void CopyBits(ReadOnlySpan<byte> data, long start, long count, BitWriter w)
    {
        var r = new BitReader(data);
        SkipBits(ref r, start);
        for (; count > 0; count -= 32)
        {
            int bits = (int)Math.Min(32, count);
            w.Write(bits, r.Read(bits));
        }
    }

    private static void SkipBits(ref BitReader r, long count)
    {
        for (; count > 0; count -= 32)
            r.Read((int)Math.Min(32, count));
    }

    private static uint Copy(ref BitReader r, BitWriter w, int bits)
    {
        uint value = r.Read(bits);
        w.Write(bits, value);
        return value;
    }

    private static void Expect(uint actual, uint expected, string what)
    {
        if (actual != expected)
            throw new InvalidDataException($"Unexpected {what}: {actual}.");
    }
}
