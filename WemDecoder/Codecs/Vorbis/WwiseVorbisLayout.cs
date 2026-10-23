namespace WemDecoder.Codecs.Vorbis;

/// <summary>Wwise Vorbis bitstream revisions, in vgmstream's order (<c>wwise_version_t</c>).</summary>
internal enum WwiseVorbisVersion
{
    V34,
    V38,
    V44,
    V48,
    V52,
    V53,
    V56,
    V62,
}

/// <summary>Size of the per-packet header: u32 size + s32 granule, u16 size + s32 granule, or u16 size.</summary>
internal enum WwisePacketHeader
{
    Type8,
    Type6,
    Type2,
}

internal enum WwiseSetupKind
{
    /// <summary>The three standard Vorbis header packets are stored as-is.</summary>
    HeaderTriad,
    /// <summary>Standard codebooks with the rest of the setup unchanged.</summary>
    FullSetup,
    /// <summary>Wwise-reduced codebooks stored inline.</summary>
    InlineCodebooks,
    /// <summary>Codebook ids into <see cref="CodebookLibrary.Standard"/>.</summary>
    ExternalCodebooks,
    /// <summary>Codebook ids into <see cref="CodebookLibrary.AoTuV603"/>.</summary>
    AoTuV603Codebooks,
}

/// <summary>
/// How a given Wwise revision stores headers and packets (vgmstream's <c>setup_version_config</c>).
/// </summary>
/// <remarks>Modified audio packets lack the packet type bit and the long-window flags.</remarks>
internal readonly record struct WwiseVorbisLayout(WwisePacketHeader PacketHeader, bool ModifiedPackets, WwiseSetupKind Setup)
{
    public static WwiseVorbisLayout For(WwiseVorbisVersion version, int shortBlockExp, int longBlockExp)
    {
        var layout = version switch
        {
            WwiseVorbisVersion.V34 => new WwiseVorbisLayout(WwisePacketHeader.Type8, false, WwiseSetupKind.HeaderTriad),
            WwiseVorbisVersion.V38 => new WwiseVorbisLayout(WwisePacketHeader.Type6, false, WwiseSetupKind.FullSetup),
            WwiseVorbisVersion.V44 => new WwiseVorbisLayout(WwisePacketHeader.Type6, false, WwiseSetupKind.InlineCodebooks),
            WwiseVorbisVersion.V48 or WwiseVorbisVersion.V52 => new WwiseVorbisLayout(WwisePacketHeader.Type6, false, WwiseSetupKind.ExternalCodebooks),
            WwiseVorbisVersion.V53 or WwiseVorbisVersion.V56 => new WwiseVorbisLayout(WwisePacketHeader.Type2, true, WwiseSetupKind.ExternalCodebooks),
            _ => new WwiseVorbisLayout(WwisePacketHeader.Type2, true, WwiseSetupKind.AoTuV603Codebooks),
        };

        // With a single block size there are no window flags to strip.
        if (version >= WwiseVorbisVersion.V53 && shortBlockExp == longBlockExp)
            layout = layout with { ModifiedPackets = false };

        return layout;
    }

    public int HeaderSize => PacketHeader switch
    {
        WwisePacketHeader.Type8 => 8,
        WwisePacketHeader.Type6 => 6,
        _ => 2,
    };
}
