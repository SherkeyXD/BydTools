// Derived from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License,
// Copyright (c) 2020 Andrew Ward. See THIRD-PARTY-NOTICES.md.
// Modified: decodes the given channel bundle, skipping channels without energy, as in libvorbis' res0/res1_inverse.

#nullable disable

using System;
using System.Collections.Generic;
using System.IO;

namespace WemDecoder.Codecs.Vorbis.NVorbis
{
    // each channel gets its own pass, one dimension at a time
    internal class Residue0 : IResidue
    {
        static int icount(int v)
        {
            var ret = 0;
            while (v != 0)
            {
                ret += (v & 1);
                v >>= 1;
            }
            return ret;
        }

        int _begin;
        int _end;
        int _partitionSize;
        int _classifications;
        int _maxStages;

        ICodebook[][] _books;
        ICodebook _classBook;

        int[] _cascade;
        int[][] _decodeMap;


        virtual public void Init(IPacket packet, int channels, ICodebook[] codebooks)
        {
            // this is pretty well stolen directly from libvorbis...  BSD license
            _begin = (int)packet.ReadBits(24);
            _end = (int)packet.ReadBits(24);
            _partitionSize = (int)packet.ReadBits(24) + 1;
            _classifications = (int)packet.ReadBits(6) + 1;
            _classBook = codebooks[(int)packet.ReadBits(8)];

            _cascade = new int[_classifications];
            var acc = 0;
            for (int i = 0; i < _classifications; i++)
            {
                var low_bits = (int)packet.ReadBits(3);
                if (packet.ReadBit())
                {
                    _cascade[i] = (int)packet.ReadBits(5) << 3 | low_bits;
                }
                else
                {
                    _cascade[i] = low_bits;
                }
                acc += icount(_cascade[i]);
            }

            var bookNums = new int[acc];
            for (var i = 0; i < acc; i++)
            {
                bookNums[i] = (int)packet.ReadBits(8);
                if (codebooks[bookNums[i]].MapType == 0) throw new InvalidDataException();
            }

            var entries = _classBook.Entries;
            var dim = _classBook.Dimensions;
            var partvals = 1;
            while (dim > 0)
            {
                partvals *= _classifications;
                if (partvals > entries) throw new InvalidDataException();
                --dim;
            }

            // now the lookups
            _books = new ICodebook[_classifications][];

            acc = 0;
            var maxstage = 0;
            int stages;
            for (int j = 0; j < _classifications; j++)
            {
                stages = Utils.ilog(_cascade[j]);
                _books[j] = new ICodebook[stages];
                if (stages > 0)
                {
                    maxstage = Math.Max(maxstage, stages);
                    for (int k = 0; k < stages; k++)
                    {
                        if ((_cascade[j] & (1 << k)) > 0)
                        {
                            _books[j][k] = codebooks[bookNums[acc++]];
                        }
                    }
                }
            }
            _maxStages = maxstage;

            _decodeMap = new int[partvals][];
            for (int j = 0; j < partvals; j++)
            {
                var val = j;
                var mult = partvals / _classifications;
                _decodeMap[j] = new int[_classBook.Dimensions];
                for (int k = 0; k < _classBook.Dimensions; k++)
                {
                    var deco = val / mult;
                    val -= deco * mult;
                    mult /= _classifications;
                    _decodeMap[j][k] = deco;
                }
            }

        }

        virtual public void Decode(IPacket packet, bool[] doNotDecodeChannel, int blockSize, float[][] buffer)
        {
            // only the channels with energy are coded
            var vectors = new List<float[]>(buffer.Length);
            for (var ch = 0; ch < buffer.Length; ch++)
            {
                if (!doNotDecodeChannel[ch])
                {
                    vectors.Add(buffer[ch]);
                }
            }

            if (vectors.Count > 0)
            {
                DecodeVectors(packet, blockSize, vectors.Count, vectors.ToArray());
            }
        }

        protected void DecodeVectors(IPacket packet, int blockSize, int vectorCount, float[][] buffer)
        {
            // this is pretty well stolen directly from libvorbis...  BSD license
            var end = _end < blockSize / 2 ? _end : blockSize / 2;
            var n = end - _begin;

            if (n > 0)
            {
                var partitionCount = n / _partitionSize;

                var partitionWords = (partitionCount + _classBook.Dimensions - 1) / _classBook.Dimensions;
                var partWordCache = new int[vectorCount, partitionWords][];

                for (var stage = 0; stage < _maxStages; stage++)
                {
                    for (int partitionIdx = 0, entryIdx = 0; partitionIdx < partitionCount; entryIdx++)
                    {
                        if (stage == 0)
                        {
                            for (var ch = 0; ch < vectorCount; ch++)
                            {
                                var idx = _classBook.DecodeScalar(packet);
                                if (idx >= 0 && idx < _decodeMap.Length)
                                {
                                    partWordCache[ch, entryIdx] = _decodeMap[idx];
                                }
                                else
                                {
                                    partitionIdx = partitionCount;
                                    stage = _maxStages;
                                    break;
                                }
                            }
                        }
                        for (var dimensionIdx = 0; partitionIdx < partitionCount && dimensionIdx < _classBook.Dimensions; dimensionIdx++, partitionIdx++)
                        {
                            var offset = _begin + partitionIdx * _partitionSize;
                            for (var ch = 0; ch < vectorCount; ch++)
                            {
                                var idx = partWordCache[ch, entryIdx][dimensionIdx];
                                if ((_cascade[idx] & (1 << stage)) != 0)
                                {
                                    var book = _books[idx][stage];
                                    if (book != null)
                                    {
                                        if (WriteVectors(book, packet, buffer, ch, offset, _partitionSize))
                                        {
                                            // bad packet...  exit now and try to use what we already have
                                            partitionIdx = partitionCount;
                                            stage = _maxStages;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        virtual protected bool WriteVectors(ICodebook codebook, IPacket packet, float[][] residue, int channel, int offset, int partitionSize)
        {
            var res = residue[channel];
            var steps = partitionSize / codebook.Dimensions;
            var entryCache = new int[steps];

            for (var i = 0; i < steps; i++)
            {
                if ((entryCache[i] = codebook.DecodeScalar(packet)) == -1)
                {
                    return true;
                }
            }
            for (var dim = 0; dim < codebook.Dimensions; dim++)
            {
                for (var step = 0; step < steps; step++, offset++)
                {
                    res[offset] += codebook[entryCache[step], dim];
                }
            }
            return false;
        }
    }
}
