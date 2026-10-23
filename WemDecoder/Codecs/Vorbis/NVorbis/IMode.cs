// Derived from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License,
// Copyright (c) 2020 Andrew Ward. See THIRD-PARTY-NOTICES.md.

#nullable disable

namespace WemDecoder.Codecs.Vorbis.NVorbis
{
    internal interface IMode
    {
        void Init(IPacket packet, int channels, int block0Size, int block1Size, IMapping[] mappings);

        bool Decode(IPacket packet, float[][] buffer, out int packetStartindex, out int packetValidLength, out int packetTotalLength);

        int GetPacketSampleCount(IPacket packet);
    }
}
