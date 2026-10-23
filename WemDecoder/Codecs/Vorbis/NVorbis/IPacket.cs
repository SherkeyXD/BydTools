// Derived from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License,
// Copyright (c) 2020 Andrew Ward. See THIRD-PARTY-NOTICES.md.
// Modified: reduced to the members the decoder uses.

namespace WemDecoder.Codecs.Vorbis.NVorbis;

/// <summary>A Vorbis packet read as an LSB-first bit stream.</summary>
internal interface IPacket
{
    /// <summary>Whether a read ran past the end of the packet.</summary>
    bool IsShort { get; }

    ulong TryPeekBits(int count, out int bitsRead);

    void SkipBits(int count);

    ulong ReadBits(int count);
}

internal static class PacketExtensions
{
    public static bool ReadBit(this IPacket packet) => packet.ReadBits(1) == 1;
}
