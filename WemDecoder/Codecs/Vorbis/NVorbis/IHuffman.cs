// Derived from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License,
// Copyright (c) 2020 Andrew Ward. See THIRD-PARTY-NOTICES.md.

#nullable disable

using System.Collections.Generic;

namespace WemDecoder.Codecs.Vorbis.NVorbis
{
    internal interface IHuffman
    {
        int TableBits { get; }
        IReadOnlyList<HuffmanListNode> PrefixTree { get; }
        IReadOnlyList<HuffmanListNode> OverflowList { get; }

        void GenerateTable(IReadOnlyList<int> value, int[] lengthList, int[] codeList);
    }
}
