/*
 * Copyright (c) 2015, 2018 Scott Bennett
 *           (c) 2018-2023 Kaarlo Räihä
 *
 * Permission to use, copy, modify, and distribute this software for any
 * purpose with or without fee is hereby granted, provided that the above
 * copyright notice and this permission notice appear in all copies.
 *
 * THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
 * WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
 * ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
 * WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
 * ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
 * OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
 */

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace BydTools.Core.Crypto;

public enum SimdMode
{
    AutoDetect = 0,
    V128,
    V256,
    V512,
    None,
}

/// <summary>
/// ChaCha20 stream cipher (RFC 7539) for VFS BLC and entry decryption.
/// </summary>
public static class CSChaCha20
{
    public const int AllowedKeyLength = 32;
    public const int AllowedNonceLength = 12;
    public const int BlockSize = 64;
    private const int StateWords = 16;

    private static ReadOnlySpan<byte> Sigma => "expand 32-byte k"u8;

    /// <summary>
    /// Decrypts (or encrypts) <paramref name="data"/> in place without heap allocations.
    /// </summary>
    public static void DecryptInPlace(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        uint counter,
        Span<byte> data,
        SimdMode simdMode = SimdMode.AutoDetect
    )
    {
        if (key.Length != AllowedKeyLength)
            throw new ArgumentException($"Key length must be {AllowedKeyLength}. Actual: {key.Length}", nameof(key));
        if (nonce.Length != AllowedNonceLength)
            throw new ArgumentException($"Nonce length must be {AllowedNonceLength}. Actual: {nonce.Length}", nameof(nonce));
        if (data.IsEmpty)
            return;

        if (simdMode == SimdMode.AutoDetect)
            simdMode = DetectSimdMode();

        Span<uint> state = stackalloc uint[StateWords];
        Span<uint> working = stackalloc uint[StateWords];
        Span<byte> keystream = stackalloc byte[BlockSize];

        ReadOnlySpan<byte> sigma = Sigma;
        state[0] = BinaryPrimitives.ReadUInt32LittleEndian(sigma);
        state[1] = BinaryPrimitives.ReadUInt32LittleEndian(sigma[4..]);
        state[2] = BinaryPrimitives.ReadUInt32LittleEndian(sigma[8..]);
        state[3] = BinaryPrimitives.ReadUInt32LittleEndian(sigma[12..]);

        for (int i = 0; i < 8; i++)
            state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));

        state[12] = counter;
        state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
        state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[4..]);
        state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[8..]);

        try
        {
            int offset = 0;
            int fullBlocks = data.Length / BlockSize;
            int tailBytes = data.Length - fullBlocks * BlockSize;

            for (int loop = 0; loop < fullBlocks; loop++)
            {
                NextBlock(state, working, keystream);
                XorBlockInPlace(data.Slice(offset, BlockSize), keystream, simdMode);
                offset += BlockSize;
            }

            if (tailBytes > 0)
            {
                NextBlock(state, working, keystream);
                for (int i = 0; i < tailBytes; i++)
                    data[offset + i] ^= keystream[i];
            }
        }
        finally
        {
            state.Clear();
            working.Clear();
            keystream.Clear();
        }
    }

    private static SimdMode DetectSimdMode()
    {
        if (Vector512.IsHardwareAccelerated)
            return SimdMode.V512;
        if (Vector256.IsHardwareAccelerated)
            return SimdMode.V256;
        if (Vector128.IsHardwareAccelerated)
            return SimdMode.V128;
        return SimdMode.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NextBlock(Span<uint> state, Span<uint> x, Span<byte> tmp)
    {
        state.CopyTo(x);
        for (int i = 0; i < 10; i++)
        {
            QuarterRound(x, 0, 4, 8, 12);
            QuarterRound(x, 1, 5, 9, 13);
            QuarterRound(x, 2, 6, 10, 14);
            QuarterRound(x, 3, 7, 11, 15);

            QuarterRound(x, 0, 5, 10, 15);
            QuarterRound(x, 1, 6, 11, 12);
            QuarterRound(x, 2, 7, 8, 13);
            QuarterRound(x, 3, 4, 9, 14);
        }

        for (int i = 0; i < StateWords; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(tmp.Slice(4 * i, 4), unchecked(x[i] + state[i]));

        state[12] = unchecked(state[12] + 1);
        if (state[12] == 0)
            state[13] = unchecked(state[13] + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] = unchecked(x[a] + x[b]);
        x[d] = RotateLeft(x[d] ^ x[a], 16);

        x[c] = unchecked(x[c] + x[d]);
        x[b] = RotateLeft(x[b] ^ x[c], 12);

        x[a] = unchecked(x[a] + x[b]);
        x[d] = RotateLeft(x[d] ^ x[a], 8);

        x[c] = unchecked(x[c] + x[d]);
        x[b] = RotateLeft(x[b] ^ x[c], 7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int count) =>
        unchecked((value << count) | (value >> (32 - count)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void XorBlockInPlace(Span<byte> block, ReadOnlySpan<byte> keystream, SimdMode simdMode)
    {
        ref byte dst = ref MemoryMarshal.GetReference(block);
        ref byte src = ref MemoryMarshal.GetReference(keystream);
        if (simdMode == SimdMode.V512)
        {
            var result = Vector512.LoadUnsafe(ref dst) ^ Vector512.LoadUnsafe(ref src);
            result.StoreUnsafe(ref dst);
        }
        else if (simdMode == SimdMode.V256)
        {
            var r0 = Vector256.LoadUnsafe(ref dst) ^ Vector256.LoadUnsafe(ref src);
            r0.StoreUnsafe(ref dst);
            var r1 = Vector256.LoadUnsafe(ref dst, 32) ^ Vector256.LoadUnsafe(ref src, 32);
            r1.StoreUnsafe(ref dst, 32);
        }
        else if (simdMode == SimdMode.V128)
        {
            for (nuint i = 0; i < 64; i += 16)
            {
                var r = Vector128.LoadUnsafe(ref dst, i) ^ Vector128.LoadUnsafe(ref src, i);
                r.StoreUnsafe(ref dst, i);
            }
        }
        else
        {
            for (int i = 0; i < BlockSize; i += 4)
            {
                block[i] ^= keystream[i];
                block[i + 1] ^= keystream[i + 1];
                block[i + 2] ^= keystream[i + 2];
                block[i + 3] ^= keystream[i + 3];
            }
        }
    }
}
