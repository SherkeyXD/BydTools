// Ported and adapted from Escartem/AnimeStudio (VFSUtils.cs / CryptoHelper.cs)
// Source: https://github.com/Escartem/AnimeStudio
// License: MIT
// Copyright (c) 2016 Radu; (c) 2016-2020 Perfare; (c) 2022-2024 Razmoth; (c) 2024-2026 Escartem

using System.Buffers.Binary;
using System.Numerics;

namespace BydTools.Formats.Bundle;

/// <summary>
/// Sparse custom-AES stream cipher used by Arknights: Endfield VFS asset bundles
/// on BlocksInfo and compressed data blocks.
/// </summary>
public static class EndfieldVfsCipher
{
    private const ulong XorKey = 0xF19AB7752CDD0196UL;

    private static ReadOnlySpan<byte> SBox =>
    [
        0xE4, 0xB9, 0x45, 0x07, 0x92, 0x82, 0x2F, 0x43, 0xF5, 0xC9, 0x22, 0x25, 0xA9, 0x4F, 0x46, 0x6D,
        0x4A, 0x71, 0x8B, 0x6C, 0x8C, 0xEB, 0xB2, 0xAC, 0xCF, 0x0C, 0x9E, 0x01, 0x38, 0x32, 0xD3, 0x93,
        0x98, 0x63, 0xDA, 0x96, 0xE5, 0xC4, 0xC3, 0x6B, 0x7F, 0x26, 0x72, 0xD7, 0x97, 0xD5, 0x80, 0xBC,
        0x5D, 0xBB, 0x55, 0x67, 0x10, 0x73, 0xB3, 0x8D, 0xE2, 0x35, 0x29, 0x47, 0xA8, 0x60, 0x3F, 0xC5,
        0xEF, 0x68, 0xEC, 0xBE, 0xAB, 0xC6, 0xB8, 0x5C, 0xD8, 0x15, 0x09, 0x54, 0xF3, 0x7A, 0x40, 0xA2,
        0x30, 0x0A, 0xDC, 0x53, 0xFA, 0xDB, 0xF1, 0x78, 0xDE, 0xAD, 0xF0, 0xB5, 0xC1, 0x81, 0x9F, 0x3E,
        0x83, 0x90, 0x31, 0xF2, 0xFB, 0x21, 0x28, 0x85, 0x06, 0xCA, 0xCD, 0x1E, 0xD4, 0x3C, 0xA0, 0xC8,
        0x23, 0x16, 0x6E, 0x89, 0x1D, 0xE7, 0xEE, 0x5E, 0x42, 0xBD, 0xCB, 0x13, 0x50, 0xA6, 0x4E, 0x49,
        0x58, 0xDF, 0x2C, 0x84, 0x87, 0xB6, 0x91, 0x52, 0xDD, 0x19, 0xF9, 0x2B, 0x4D, 0x77, 0xBA, 0x04,
        0xA5, 0x41, 0xCE, 0x94, 0x3D, 0x5F, 0xFC, 0x9B, 0x79, 0x9A, 0x7E, 0x65, 0x5A, 0xB1, 0x66, 0x34,
        0x56, 0xA7, 0x1A, 0xBF, 0xEA, 0x7D, 0x27, 0x0B, 0x59, 0x2E, 0xAE, 0x14, 0x33, 0xC0, 0x51, 0x39,
        0xC7, 0x3A, 0x2A, 0x9D, 0xF4, 0x7C, 0xCC, 0xD1, 0xD6, 0x70, 0x37, 0x0E, 0x75, 0x02, 0x1B, 0xE3,
        0xE9, 0x48, 0x0D, 0x24, 0x2D, 0xF7, 0xD2, 0xB7, 0xAF, 0xA3, 0xA1, 0x64, 0x7B, 0xED, 0xF8, 0x05,
        0x95, 0x3B, 0x74, 0xFD, 0x62, 0xD0, 0x0F, 0xFF, 0x4B, 0xAA, 0x88, 0x5B, 0x03, 0xB4, 0xE8, 0x9C,
        0xB0, 0x17, 0x1C, 0x76, 0x57, 0xE0, 0xA4, 0x44, 0x20, 0xD9, 0x8E, 0x11, 0x86, 0x69, 0x36, 0xFE,
        0x4C, 0x6F, 0x61, 0x6A, 0x8F, 0xE1, 0x18, 0x8A, 0x12, 0x99, 0xE6, 0x1F, 0x00, 0x08, 0xF6, 0xC2,
    ];

    private static ReadOnlySpan<byte> Key =>
    [
        0x3A, 0xF1, 0x8C, 0x47, 0xB2, 0x09, 0x6D, 0xEE, 0x51, 0x24, 0x90, 0x7C, 0x18, 0xD3, 0xA4, 0x62,
    ];

    private static ReadOnlySpan<byte> Iv =>
    [
        0xC7, 0x12, 0x5E, 0xA9, 0x04, 0xDB, 0x33, 0x88, 0xF2, 0x0E, 0x77, 0x49, 0x65, 0xBA, 0x1C, 0x93,
    ];

    private static readonly byte[] RoundKeys = ExpandKey();

    /// <summary>
    /// Decrypts (or encrypts, as the underlying primitive is an XOR stream) a block
    /// buffer in place using the VFS sparse sampling scheme.
    /// </summary>
    public static void TransformBlock(Span<byte> buffer)
    {
        if (buffer.Length == 0)
            return;

        if (buffer.Length <= 256)
        {
            TransformStream(buffer);
            return;
        }

        int numBlocksFloor = buffer.Length / 16;
        int step = numBlocksFloor > 256 ? 1 : 256 / numBlocksFloor;
        int sampledBlocks = Math.Min(numBlocksFloor, 256);
        int sampleLen = sampledBlocks * step;

        Span<byte> sample = stackalloc byte[256];
        Span<byte> active = sample[..sampleLen];

        for (int i = 0; i < sampledBlocks; i++)
            buffer.Slice(i * 16, step).CopyTo(active.Slice(i * step, step));

        TransformStream(active);

        for (int i = 0; i < sampledBlocks; i++)
            active.Slice(i * step, step).CopyTo(buffer.Slice(i * 16, step));
    }

    internal static void TransformStream(Span<byte> data)
    {
        Span<byte> previous = stackalloc byte[16];
        Iv.CopyTo(previous);

        Span<byte> keystream = stackalloc byte[16];
        Span<byte> nextIv = stackalloc byte[16];

        for (int offset = 0; offset < data.Length; offset += 16)
        {
            EncryptBlock(previous, keystream);

            int count = Math.Min(16, data.Length - offset);
            for (int i = 0; i < count; i++)
                data[offset + i] ^= keystream[i];

            if (offset + 16 >= data.Length)
                break;

            int shiftCount = 0;
            for (int i = 0; i < 16; i++)
            {
                ulong shiftSrc = XorKey >> (shiftCount & 0x38);
                byte temp = (byte)(keystream[i] ^ (31 * i) ^ (byte)shiftSrc);
                shiftCount += 8;
                temp = (byte)((temp >> 5) | (temp << 3));
                nextIv[i] = SBox[temp];
            }

            nextIv.CopyTo(previous);
        }
    }

    private static void EncryptBlock(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> state = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            state[i] = (byte)(input[i] ^ RoundKeys[i]);

        for (int round = 1; round < 10; round++)
        {
            for (int i = 0; i < 16; i++)
                state[i] = SBox[state[i]];

            ShiftRows(state);
            MixColumns(state);

            int roundOffset = round * 16;
            for (int i = 0; i < 16; i++)
                state[i] ^= RoundKeys[roundOffset + i];
        }

        for (int i = 0; i < 16; i++)
            state[i] = SBox[state[i]];

        ShiftRows(state);

        int lastOffset = 10 * 16;
        for (int i = 0; i < 16; i++)
            output[i] = (byte)(state[i] ^ RoundKeys[lastOffset + i]);
    }

    private static void ShiftRows(Span<byte> s)
    {
        (s[1], s[5], s[9], s[13]) = (s[5], s[9], s[13], s[1]);
        (s[2], s[6], s[10], s[14]) = (s[10], s[14], s[2], s[6]);
        (s[3], s[7], s[11], s[15]) = (s[15], s[3], s[7], s[11]);
    }

    private static void MixColumns(Span<byte> s)
    {
        for (int col = 0; col < 16; col += 4)
        {
            byte a0 = s[col];
            byte a1 = s[col + 1];
            byte a2 = s[col + 2];
            byte a3 = s[col + 3];
            byte t = (byte)(a0 ^ a1 ^ a2 ^ a3);
            s[col] ^= (byte)(t ^ XTime((byte)(a0 ^ a1)));
            s[col + 1] ^= (byte)(t ^ XTime((byte)(a1 ^ a2)));
            s[col + 2] ^= (byte)(t ^ XTime((byte)(a2 ^ a3)));
            s[col + 3] ^= (byte)(t ^ XTime((byte)(a3 ^ a0)));
        }
    }

    private static byte XTime(byte a) => (byte)((a & 0x80) != 0 ? (a << 1) ^ 0x1B : a << 1);

    private static byte[] ExpandKey()
    {
        ReadOnlySpan<byte> rCon = [0x00, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1B, 0x36];
        var expanded = new byte[11 * 16];
        Key.CopyTo(expanded);

        int rconIdx = 1;
        for (int word = 4; word < 44; word++)
        {
            int dst = word * 4;
            int prev = dst - 4;
            int baseWord = dst - 16;

            byte w0 = expanded[prev];
            byte w1 = expanded[prev + 1];
            byte w2 = expanded[prev + 2];
            byte w3 = expanded[prev + 3];

            if (word % 4 == 0)
            {
                (w0, w1, w2, w3) = (
                    (byte)(SBox[w1] ^ rCon[rconIdx++]),
                    SBox[w2],
                    SBox[w3],
                    SBox[w0]
                );
            }

            expanded[dst] = (byte)(expanded[baseWord] ^ w0);
            expanded[dst + 1] = (byte)(expanded[baseWord + 1] ^ w1);
            expanded[dst + 2] = (byte)(expanded[baseWord + 2] ^ w2);
            expanded[dst + 3] = (byte)(expanded[baseWord + 3] ^ w3);
        }

        return expanded;
    }

    internal static uint UnpackHeaderMagicChecksum(uint a)
    {
        uint c1 = (4u * (a ^ 0x4A92F0CDu)) & 0xFFFF0000u;
        uint c2 = BitOperations.RotateRight(a ^ 0x4A92F0CDu, 14);
        return c1 ^ c2 ^ 0xD8B1E637u;
    }
}
