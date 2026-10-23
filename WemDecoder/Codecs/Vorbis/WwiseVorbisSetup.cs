using System.Buffers.Binary;
using System.Numerics;
using WemDecoder.Internal;

namespace WemDecoder.Codecs.Vorbis;

/// <summary>Standard Vorbis header packets rebuilt from a Wwise stream.</summary>
internal sealed class WwiseVorbisHeaders
{
    public required byte[] Identification { get; init; }
    public required byte[] Comment { get; init; }
    public required byte[] Setup { get; init; }

    /// <summary>Block flag per mode; only filled in when the setup was rebuilt.</summary>
    public required bool[] ModeBlockFlags { get; init; }
    public int ModeBits { get; init; }
}

/// <summary>
/// Port of vgmstream's <c>vorbis_custom_setup_init_wwise</c> and the ww2ogg setup rebuild
/// (coding/vorbis_custom_utils_wwise.c, originally hcs's ww2ogg).
/// </summary>
internal static class WwiseVorbisSetup
{
    private const int MaxPacketRead = 0x8000;
    private const uint CodebookSync = 0x564342; // "BCV"

    private static ReadOnlySpan<byte> VorbisId => "vorbis"u8;

    public static WwiseVorbisHeaders? TryBuild(ReadOnlySpan<byte> file, long setupOffset, WwiseVorbisLayout layout,
        int channels, int sampleRate, int shortBlockExp, int longBlockExp, bool bigEndian)
    {
        if (layout.Setup == WwiseSetupKind.HeaderTriad)
        {
            var packets = new byte[3][];
            long offset = setupOffset;
            for (int i = 0; i < packets.Length; i++)
            {
                if (!TryReadPacket(file, offset, layout, bigEndian, out var packet))
                    return null;
                packets[i] = packet.ToArray();
                offset += layout.HeaderSize + packet.Length;
            }

            return new WwiseVorbisHeaders
            {
                Identification = packets[0],
                Comment = packets[1],
                Setup = packets[2],
                ModeBlockFlags = new bool[65],
            };
        }

        if (!TryReadPacket(file, setupOffset, layout, bigEndian, out var wwiseSetup))
            return null;

        var writer = new BitWriter(0x4000);
        var reader = new BitReader(wwiseSetup);
        var modeBlockFlags = new bool[65];
        if (!TryRebuildSetup(writer, ref reader, wwiseSetup.Length, layout.Setup, channels, modeBlockFlags, out int modeBits))
            return null;

        return new WwiseVorbisHeaders
        {
            Identification = BuildIdentification(channels, sampleRate, shortBlockExp, longBlockExp),
            Comment = BuildComment(),
            Setup = writer.ToArray(),
            ModeBlockFlags = modeBlockFlags,
            ModeBits = modeBits,
        };
    }

    /// <summary>
    /// The identification checks libvorbis applies in <c>_vorbis_unpack_info</c>, which NVorbis skips.
    /// </summary>
    public static bool IsValidIdentification(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 0x1E || packet[0] != 0x01 || !packet.Slice(1, 6).SequenceEqual(VorbisId))
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(packet[0x07..]) != 0)
            return false;

        int channels = packet[0x0B];
        int rate = BinaryPrimitives.ReadInt32LittleEndian(packet[0x0C..]);
        int block0 = 1 << (packet[0x1C] & 0x0F);
        int block1 = 1 << (packet[0x1C] >> 4);
        return channels >= 1 && rate >= 1 && block0 >= 64 && block1 >= block0 && block1 <= 8192 && (packet[0x1D] & 1) == 1;
    }

    private static bool TryReadPacket(ReadOnlySpan<byte> file, long offset, WwiseVorbisLayout layout, bool bigEndian, out ReadOnlySpan<byte> packet)
    {
        packet = default;
        if (offset < 0 || offset + layout.HeaderSize > file.Length)
            return false;

        uint size = layout.PacketHeader == WwisePacketHeader.Type8
            ? Bytes.U32(file, offset, bigEndian)
            : Bytes.U16(file, offset, bigEndian);
        long start = offset + layout.HeaderSize;
        if (size == 0 || size > MaxPacketRead || start + size > file.Length)
            return false;

        packet = file.Slice((int)start, (int)size);
        return true;
    }

    private static byte[] BuildIdentification(int channels, int sampleRate, int shortBlockExp, int longBlockExp)
    {
        var packet = new byte[0x1E];
        packet[0x00] = 0x01;
        VorbisId.CopyTo(packet.AsSpan(0x01));
        packet[0x0B] = (byte)channels;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0x0C), sampleRate);
        packet[0x1C] = (byte)((longBlockExp << 4) | shortBlockExp);
        packet[0x1D] = 0x01;
        return packet;
    }

    private static byte[] BuildComment()
    {
        var packet = new byte[0x19];
        packet[0x00] = 0x03;
        VorbisId.CopyTo(packet.AsSpan(0x01));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x07), 9);
        "vgmstream"u8.CopyTo(packet.AsSpan(0x0B));
        packet[0x18] = 0x01;
        return packet;
    }

    /// <summary>
    /// Port of <c>ww2ogg_generate_vorbis_setup</c>: re-expands the reduced Wwise setup into the six standard parts.
    /// </summary>
    private static bool TryRebuildSetup(BitWriter w, ref BitReader r, int packetSize, WwiseSetupKind kind, int channels,
        bool[] modeBlockFlags, out int modeBits)
    {
        modeBits = 0;

        w.Write(8, 0x05);
        foreach (byte b in VorbisId)
            w.Write(8, b);

        // Codebooks
        uint codebookCountLess1 = r.Read(8);
        w.Write(8, codebookCountLess1);
        uint codebookCount = codebookCountLess1 + 1;

        switch (kind)
        {
            case WwiseSetupKind.FullSetup:
                for (uint i = 0; i < codebookCount; i++)
                {
                    if (!TryCopyCodebook(w, ref r))
                        return false;
                }
                break;

            case WwiseSetupKind.InlineCodebooks:
                for (uint i = 0; i < codebookCount; i++)
                {
                    if (!TryRebuildCodebook(w, ref r, 0))
                        return false;
                }
                break;

            default:
                var library = kind == WwiseSetupKind.AoTuV603Codebooks ? CodebookLibrary.AoTuV603 : CodebookLibrary.Standard;
                for (uint i = 0; i < codebookCount; i++)
                {
                    uint id = r.Read(10);
                    if (!library.TryGet(id, out var codebook))
                        return false;

                    var codebookReader = new BitReader(codebook);
                    if (!TryRebuildCodebook(w, ref codebookReader, codebook.Length))
                        return false;
                }
                break;
        }

        // Time domain transforms (removed by Wwise): one placeholder.
        w.Write(6, 0);
        w.Write(16, 0);

        if (kind == WwiseSetupKind.FullSetup)
        {
            // The rest of the setup is unchanged.
            long totalBits = packetSize * 8L;
            while (r.Position < totalBits)
                w.Write(1, r.Read(1));
        }
        else
        {
            if (!TryRebuildFloors(w, ref r, codebookCount, out uint floorCount)
                || !TryRebuildResidues(w, ref r, codebookCount, out uint residueCount)
                || !TryRebuildMappings(w, ref r, channels, floorCount, residueCount, out uint mappingCount)
                || !TryRebuildModes(w, ref r, mappingCount, modeBlockFlags, out int modeCount))
            {
                return false;
            }

            modeBits = ILog((uint)modeCount - 1);
        }

        // Framing flag
        w.Write(1, 1);
        w.PadToByte();
        return true;
    }

    private static bool TryRebuildFloors(BitWriter w, ref BitReader r, uint codebookCount, out uint floorCount)
    {
        uint floorCountLess1 = r.Read(6);
        w.Write(6, floorCountLess1);
        floorCount = floorCountLess1 + 1;

        Span<uint> partitionClasses = stackalloc uint[32];
        Span<uint> classDimensions = stackalloc uint[17];
        for (uint i = 0; i < floorCount; i++)
        {
            // Always floor type 1.
            w.Write(16, 1);

            uint partitions = r.Read(5);
            w.Write(5, partitions);

            partitionClasses.Clear();
            uint maximumClass = 0;
            for (int j = 0; j < partitions; j++)
            {
                uint partitionClass = r.Read(4);
                w.Write(4, partitionClass);
                partitionClasses[j] = partitionClass;
                maximumClass = Math.Max(maximumClass, partitionClass);
            }

            classDimensions.Clear();
            for (int j = 0; j <= maximumClass; j++)
            {
                uint dimensionsLess1 = r.Read(3);
                w.Write(3, dimensionsLess1);
                classDimensions[j] = dimensionsLess1 + 1;

                uint subclasses = r.Read(2);
                w.Write(2, subclasses);
                if (subclasses != 0)
                {
                    uint masterbook = r.Read(8);
                    w.Write(8, masterbook);
                    if (masterbook >= codebookCount)
                        return false;
                }

                for (int k = 0; k < (1 << (int)subclasses); k++)
                {
                    uint subclassBookPlus1 = r.Read(8);
                    w.Write(8, subclassBookPlus1);
                    int subclassBook = (int)subclassBookPlus1 - 1;
                    if (subclassBook >= 0 && subclassBook >= codebookCount)
                        return false;
                }
            }

            uint multiplierLess1 = r.Read(2);
            w.Write(2, multiplierLess1);

            uint rangeBits = r.Read(4);
            w.Write(4, rangeBits);

            for (int j = 0; j < partitions; j++)
            {
                uint classNumber = partitionClasses[j];
                for (int k = 0; k < classDimensions[(int)classNumber]; k++)
                    w.Write((int)rangeBits, r.Read((int)rangeBits));
            }
        }

        return true;
    }

    private static bool TryRebuildResidues(BitWriter w, ref BitReader r, uint codebookCount, out uint residueCount)
    {
        uint residueCountLess1 = r.Read(6);
        w.Write(6, residueCountLess1);
        residueCount = residueCountLess1 + 1;

        Span<uint> cascade = stackalloc uint[65];
        for (uint i = 0; i < residueCount; i++)
        {
            uint residueType = r.Read(2);
            w.Write(16, residueType);
            if (residueType > 2)
                return false;

            w.Write(24, r.Read(24)); // begin
            w.Write(24, r.Read(24)); // end
            w.Write(24, r.Read(24)); // partition size - 1
            uint classificationsLess1 = r.Read(6);
            w.Write(6, classificationsLess1);
            uint classbook = r.Read(8);
            w.Write(8, classbook);
            uint classifications = classificationsLess1 + 1;
            if (classbook >= codebookCount)
                return false;

            cascade.Clear();
            for (int j = 0; j < classifications; j++)
            {
                uint highBits = 0;
                uint lowBits = r.Read(3);
                w.Write(3, lowBits);
                uint bitFlag = r.Read(1);
                w.Write(1, bitFlag);
                if (bitFlag != 0)
                {
                    highBits = r.Read(5);
                    w.Write(5, highBits);
                }
                cascade[j] = highBits * 8 + lowBits;
            }

            for (int j = 0; j < classifications; j++)
            {
                for (int k = 0; k < 8; k++)
                {
                    if ((cascade[j] & (1u << k)) != 0)
                    {
                        uint book = r.Read(8);
                        w.Write(8, book);
                        if (book >= codebookCount)
                            return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool TryRebuildMappings(BitWriter w, ref BitReader r, int channels, uint floorCount, uint residueCount, out uint mappingCount)
    {
        uint mappingCountLess1 = r.Read(6);
        w.Write(6, mappingCountLess1);
        mappingCount = mappingCountLess1 + 1;

        for (uint i = 0; i < mappingCount; i++)
        {
            // Always mapping type 0, the only one.
            w.Write(16, 0);

            uint submapsFlag = r.Read(1);
            w.Write(1, submapsFlag);
            uint submaps = 1;
            if (submapsFlag != 0)
            {
                uint submapsLess1 = r.Read(4);
                w.Write(4, submapsLess1);
                submaps = submapsLess1 + 1;
            }

            uint squarePolarFlag = r.Read(1);
            w.Write(1, squarePolarFlag);
            if (squarePolarFlag != 0)
            {
                uint couplingStepsLess1 = r.Read(8);
                w.Write(8, couplingStepsLess1);
                uint couplingSteps = couplingStepsLess1 + 1;
                int couplingBits = ILog((uint)channels - 1);
                for (uint j = 0; j < couplingSteps; j++)
                {
                    uint magnitude = r.Read(couplingBits);
                    w.Write(couplingBits, magnitude);
                    uint angle = r.Read(couplingBits);
                    w.Write(couplingBits, angle);
                    if (angle == magnitude || magnitude >= channels || angle >= channels)
                        return false;
                }
            }

            // A reserved field that Wwise kept.
            uint reserved = r.Read(2);
            w.Write(2, reserved);
            if (reserved != 0)
                return false;

            if (submaps > 1)
            {
                for (int j = 0; j < channels; j++)
                {
                    uint mux = r.Read(4);
                    w.Write(4, mux);
                    if (mux >= submaps)
                        return false;
                }
            }

            for (uint j = 0; j < submaps; j++)
            {
                w.Write(8, r.Read(8)); // unused time configuration
                uint floorNumber = r.Read(8);
                w.Write(8, floorNumber);
                if (floorNumber >= floorCount)
                    return false;

                uint residueNumber = r.Read(8);
                w.Write(8, residueNumber);
                if (residueNumber >= residueCount)
                    return false;
            }
        }

        return true;
    }

    private static bool TryRebuildModes(BitWriter w, ref BitReader r, uint mappingCount, bool[] modeBlockFlags, out int modeCount)
    {
        uint modeCountLess1 = r.Read(6);
        w.Write(6, modeCountLess1);
        modeCount = (int)modeCountLess1 + 1;

        for (int i = 0; i < modeCount; i++)
        {
            uint blockFlag = r.Read(1);
            w.Write(1, blockFlag);
            modeBlockFlags[i] = blockFlag != 0;

            w.Write(16, 0); // window type
            w.Write(16, 0); // transform type

            uint mapping = r.Read(8);
            w.Write(8, mapping);
            if (mapping >= mappingCount)
                return false;
        }

        return true;
    }

    /// <summary>Port of <c>ww2ogg_codebook_library_copy</c>: a standard codebook, copied as-is.</summary>
    private static bool TryCopyCodebook(BitWriter w, ref BitReader r)
    {
        uint id = r.Read(24);
        w.Write(24, id);
        uint dimensions = r.Read(16);
        w.Write(16, dimensions);
        uint entries = r.Read(24);
        w.Write(24, entries);
        if (id != CodebookSync)
            return false;

        uint ordered = r.Read(1);
        w.Write(1, ordered);
        if (ordered != 0)
        {
            uint initialLength = r.Read(5);
            w.Write(5, initialLength);
            if (!TryCopyOrderedLengths(w, ref r, entries, initialLength))
                return false;
        }
        else
        {
            uint sparse = r.Read(1);
            w.Write(1, sparse);
            for (uint i = 0; i < entries; i++)
            {
                bool present = true;
                if (sparse != 0)
                {
                    uint presentFlag = r.Read(1);
                    w.Write(1, presentFlag);
                    present = presentFlag != 0;
                }

                if (present)
                    w.Write(5, r.Read(5));
            }
        }

        uint lookupType = r.Read(4);
        w.Write(4, lookupType);
        return TryCopyLookup(w, ref r, lookupType, entries, dimensions);
    }

    /// <summary>
    /// Port of <c>ww2ogg_codebook_library_rebuild</c>: expands a Wwise-reduced codebook.
    /// A non-zero <paramref name="codebookSize"/> (library codebooks) must be consumed exactly.
    /// </summary>
    internal static bool TryRebuildCodebook(BitWriter w, ref BitReader r, int codebookSize)
    {
        w.Write(24, CodebookSync);
        uint dimensions = r.Read(4);
        w.Write(16, dimensions);
        uint entries = r.Read(14);
        w.Write(24, entries);

        uint ordered = r.Read(1);
        w.Write(1, ordered);
        if (ordered != 0)
        {
            uint initialLength = r.Read(5);
            w.Write(5, initialLength);
            if (!TryCopyOrderedLengths(w, ref r, entries, initialLength))
                return false;
        }
        else
        {
            uint codewordLengthBits = r.Read(3);
            uint sparse = r.Read(1);
            w.Write(1, sparse);
            if (codewordLengthBits == 0 || codewordLengthBits > 5)
                return false;

            for (uint i = 0; i < entries; i++)
            {
                bool present = true;
                if (sparse != 0)
                {
                    uint presentFlag = r.Read(1);
                    w.Write(1, presentFlag);
                    present = presentFlag != 0;
                }

                if (present)
                    w.Write(5, r.Read((int)codewordLengthBits));
            }
        }

        uint lookupType = r.Read(1);
        w.Write(4, lookupType);
        if (!TryCopyLookup(w, ref r, lookupType, entries, dimensions))
            return false;

        // A fully used last byte still counts one extra byte in the stored size.
        long consumed = r.Position / 8 + 1;
        return codebookSize == 0 || consumed == codebookSize;
    }

    private static bool TryCopyOrderedLengths(BitWriter w, ref BitReader r, uint entries, uint initialLength)
    {
        // libvorbis rejects lengths above 32, which also bounds this loop on corrupt data.
        uint length = initialLength + 1;
        uint current = 0;
        while (current < entries)
        {
            if (length > 32)
                return false;

            int bits = ILog(entries - current);
            uint number = r.Read(bits);
            w.Write(bits, number);
            current += number;
            length++;
        }

        return current <= entries;
    }

    private static bool TryCopyLookup(BitWriter w, ref BitReader r, uint lookupType, uint entries, uint dimensions)
    {
        if (lookupType == 0)
            return true;
        if (lookupType != 1)
            return false;

        w.Write(32, r.Read(32)); // min
        w.Write(32, r.Read(32)); // max
        uint valueLength = r.Read(4);
        w.Write(4, valueLength);
        w.Write(1, r.Read(1)); // sequence flag

        if (dimensions == 0)
            return false;

        uint quantvals = BookMaptype1Quantvals(entries, dimensions);
        int valueBits = (int)valueLength + 1;
        for (uint i = 0; i < quantvals; i++)
            w.Write(valueBits, r.Read(valueBits));

        return true;
    }

    /// <summary><c>_book_maptype1_quantvals</c> from Tremor.</summary>
    internal static uint BookMaptype1Quantvals(uint entries, uint dimensions)
    {
        int bits = ILog(entries);
        int shift = bits == 0 ? 0 : (int)((uint)(bits - 1) * (dimensions - 1) / dimensions);
        uint vals = shift >= 32 ? 0 : entries >> shift;

        while (true)
        {
            ulong acc = Power(vals, dimensions, entries);
            ulong acc1 = Power(vals + 1, dimensions, entries);
            if (acc <= entries && acc1 > entries)
                return vals;

            if (acc > entries)
                vals--;
            else
                vals++;
        }
    }

    /// <summary><paramref name="value"/> to the <paramref name="exponent"/>, saturated just above <paramref name="limit"/>.</summary>
    private static ulong Power(uint value, uint exponent, uint limit)
    {
        ulong result = 1;
        for (uint i = 0; i < exponent; i++)
        {
            result *= value;
            if (result > limit)
                return (ulong)limit + 1;
        }
        return result;
    }

    internal static int ILog(uint value) => 32 - BitOperations.LeadingZeroCount(value);
}
