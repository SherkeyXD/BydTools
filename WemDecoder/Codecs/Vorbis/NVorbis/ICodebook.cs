// Derived from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License,
// Copyright (c) 2020 Andrew Ward. See THIRD-PARTY-NOTICES.md.

#nullable disable

namespace WemDecoder.Codecs.Vorbis.NVorbis
{
    internal interface ICodebook
    {
        void Init(IPacket packet, IHuffman huffman);

        int Dimensions { get; }
        int Entries { get; }
        int MapType { get; }

        float this[int entry, int dim] { get; }

        int DecodeScalar(IPacket packet);
    }
}
