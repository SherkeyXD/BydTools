// Derived from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License,
// Copyright (c) 2020 Andrew Ward. See THIRD-PARTY-NOTICES.md.

#nullable disable

namespace WemDecoder.Codecs.Vorbis.NVorbis
{
    internal interface IMapping
    {
        void Init(IPacket packet, int channels, IFloor[] floors, IResidue[] residues, IMdct mdct);

        void DecodePacket(IPacket packet, int blockSize, int channels, float[][] buffer);
    }
}
