// Derived from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License,
// Copyright (c) 2020 Andrew Ward. See THIRD-PARTY-NOTICES.md.

#nullable disable

namespace WemDecoder.Codecs.Vorbis.NVorbis
{
    internal interface IResidue
    {
        void Init(IPacket packet, int channels, ICodebook[] codebooks);
        void Decode(IPacket packet, bool[] doNotDecodeChannel, int blockSize, float[][] buffer);
    }
}
