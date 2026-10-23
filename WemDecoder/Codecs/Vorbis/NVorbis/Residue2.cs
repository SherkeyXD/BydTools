// Derived from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License,
// Copyright (c) 2020 Andrew Ward. See THIRD-PARTY-NOTICES.md.
// Modified: interleaves the given channel bundle rather than all channels, as in libvorbis' res2_inverse.

#nullable disable

using System;

namespace WemDecoder.Codecs.Vorbis.NVorbis
{
    // all channels in one pass, interleaved
    internal class Residue2 : Residue0
    {
        public override void Init(IPacket packet, int channels, ICodebook[] codebooks)
        {
            base.Init(packet, 1, codebooks);
        }

        public override void Decode(IPacket packet, bool[] doNotDecodeChannel, int blockSize, float[][] buffer)
        {
            // the bundle is coded as a single vector, so the block size has to be multiplied
            if (Array.IndexOf(doNotDecodeChannel, false) != -1)
            {
                DecodeVectors(packet, blockSize * buffer.Length, 1, buffer);
            }
        }

        protected override bool WriteVectors(ICodebook codebook, IPacket packet, float[][] residue, int channel, int offset, int partitionSize)
        {
            var channels = residue.Length;
            var chPtr = 0;

            offset /= channels;
            for (int c = 0; c < partitionSize;)
            {
                var entry = codebook.DecodeScalar(packet);
                if (entry == -1)
                {
                    return true;
                }
                for (var d = 0; d < codebook.Dimensions; d++, c++)
                {
                    residue[chPtr][offset] += codebook[entry, d];
                    if (++chPtr == channels)
                    {
                        chPtr = 0;
                        offset++;
                    }
                }
            }

            return false;
        }
    }
}
