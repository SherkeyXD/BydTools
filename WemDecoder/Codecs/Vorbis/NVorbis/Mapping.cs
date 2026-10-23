// Derived from NVorbis 0.10.5 (https://github.com/NVorbis/NVorbis), MIT License,
// Copyright (c) 2020 Andrew Ward. See THIRD-PARTY-NOTICES.md.
// Modified: residue is decoded per submap channel bundle, and channel coupling marks
// channels for residue decoding only, as in libvorbis' mapping0_inverse.

#nullable disable

using System;
using System.Linq;

namespace WemDecoder.Codecs.Vorbis.NVorbis
{
    internal class Mapping : IMapping
    {
        IMdct _mdct;
        int[] _couplingAngle;
        int[] _couplingMangitude;
        IResidue[] _submapResidue;
        int[][] _submapChannels;
        IFloor[] _channelFloor;

        public void Init(IPacket packet, int channels, IFloor[] floors, IResidue[] residues, IMdct mdct)
        {
            var submapCount = 1;
            if (packet.ReadBit())
            {
                submapCount += (int)packet.ReadBits(4);
            }

            // square polar mapping
            var couplingSteps = 0;
            if (packet.ReadBit())
            {
                couplingSteps = (int)packet.ReadBits(8) + 1;
            }

            var couplingBits = Utils.ilog(channels - 1);
            _couplingAngle = new int[couplingSteps];
            _couplingMangitude = new int[couplingSteps];
            for (var j = 0; j < couplingSteps; j++)
            {
                var magnitude = (int)packet.ReadBits(couplingBits);
                var angle = (int)packet.ReadBits(couplingBits);
                if (magnitude == angle || magnitude > channels - 1 || angle > channels - 1)
                {
                    throw new System.IO.InvalidDataException("Invalid magnitude or angle in mapping header!");
                }
                _couplingAngle[j] = angle;
                _couplingMangitude[j] = magnitude;
            }

            if (0 != packet.ReadBits(2))
            {
                throw new System.IO.InvalidDataException("Reserved bits not 0 in mapping header.");
            }

            var mux = new int[channels];
            if (submapCount > 1)
            {
                for (var c = 0; c < channels; c++)
                {
                    mux[c] = (int)packet.ReadBits(4);
                    if (mux[c] >= submapCount)
                    {
                        throw new System.IO.InvalidDataException("Invalid channel mux submap index in mapping header!");
                    }
                }
            }

            var submapFloor = new IFloor[submapCount];
            _submapResidue = new IResidue[submapCount];
            _submapChannels = new int[submapCount][];
            var channelIndices = Enumerable.Range(0, channels).ToArray();
            for (var j = 0; j < submapCount; j++)
            {
                packet.SkipBits(8); // unused placeholder
                var floorNum = (int)packet.ReadBits(8);
                if (floorNum >= floors.Length)
                {
                    throw new System.IO.InvalidDataException("Invalid floor number in mapping header!");
                }
                var residueNum = (int)packet.ReadBits(8);
                if (residueNum >= residues.Length)
                {
                    throw new System.IO.InvalidDataException("Invalid residue number in mapping header!");
                }

                submapFloor[j] = floors[floorNum];
                _submapResidue[j] = residues[residueNum];
                _submapChannels[j] = Array.FindAll(channelIndices, c => mux[c] == j);
            }

            _channelFloor = new IFloor[channels];
            for (var c = 0; c < channels; c++)
            {
                _channelFloor[c] = submapFloor[mux[c]];
            }

            _mdct = mdct;
        }

        public void DecodePacket(IPacket packet, int blockSize, int channels, float[][] buffer)
        {
            var halfBlockSize = blockSize >> 1;

            // recover the spectral envelope
            var floorData = new IFloorData[_channelFloor.Length];
            var nonzero = new bool[_channelFloor.Length];
            for (var i = 0; i < _channelFloor.Length; i++)
            {
                floorData[i] = _channelFloor[i].Unpack(packet, blockSize, i);
                nonzero[i] = floorData[i].ExecuteChannel;

                // pre-clear the residue buffers
                Array.Clear(buffer[i], 0, halfBlockSize);
            }

            // channel coupling can 'dirty' the nonzero listing
            for (var i = 0; i < _couplingAngle.Length; i++)
            {
                if (nonzero[_couplingAngle[i]] || nonzero[_couplingMangitude[i]])
                {
                    nonzero[_couplingAngle[i]] = true;
                    nonzero[_couplingMangitude[i]] = true;
                }
            }

            // decode each submap's channels into the residue buffers
            for (var i = 0; i < _submapResidue.Length; i++)
            {
                var bundle = _submapChannels[i];
                var doNotDecode = new bool[bundle.Length];
                var vectors = new float[bundle.Length][];
                for (var j = 0; j < bundle.Length; j++)
                {
                    doNotDecode[j] = !nonzero[bundle[j]];
                    vectors[j] = buffer[bundle[j]];
                }
                _submapResidue[i].Decode(packet, doNotDecode, blockSize, vectors);
            }

            // inverse coupling
            for (var i = _couplingAngle.Length - 1; i >= 0; i--)
            {
                var magnitude = buffer[_couplingMangitude[i]];
                var angle = buffer[_couplingAngle[i]];

                // we only have to do the first half; MDCT ignores the last half
                for (var j = 0; j < halfBlockSize; j++)
                {
                    float newM, newA;

                    var oldM = magnitude[j];
                    var oldA = angle[j];
                    if (oldM > 0)
                    {
                        if (oldA > 0)
                        {
                            newM = oldM;
                            newA = oldM - oldA;
                        }
                        else
                        {
                            newA = oldM;
                            newM = oldM + oldA;
                        }
                    }
                    else
                    {
                        if (oldA > 0)
                        {
                            newM = oldM;
                            newA = oldM + oldA;
                        }
                        else
                        {
                            newA = oldM;
                            newM = oldM - oldA;
                        }
                    }

                    magnitude[j] = newM;
                    angle[j] = newA;
                }
            }

            // apply floor / dot product / MDCT (only run if we have sound energy in that channel)
            for (var c = 0; c < _channelFloor.Length; c++)
            {
                if (floorData[c].ExecuteChannel)
                {
                    _channelFloor[c].Apply(floorData[c], blockSize, buffer[c]);
                    _mdct.Reverse(buffer[c], blockSize);
                }
                else
                {
                    // channels without a floor are silent, even if coupling decoded residue into them
                    Array.Clear(buffer[c], 0, blockSize);
                }
            }
        }
    }
}
