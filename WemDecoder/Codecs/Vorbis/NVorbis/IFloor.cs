// Derived from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License,
// Copyright (c) 2020 Andrew Ward. See THIRD-PARTY-NOTICES.md.

#nullable disable

namespace WemDecoder.Codecs.Vorbis.NVorbis
{
    internal interface IFloor
    {
        void Init(IPacket packet, int channels, int block0Size, int block1Size, ICodebook[] codebooks);

        IFloorData Unpack(IPacket packet, int blockSize, int channel);

        void Apply(IFloorData floorData, int blockSize, float[] residue);
    }
}
