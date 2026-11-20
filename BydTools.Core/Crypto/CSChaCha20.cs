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

/// <summary>
/// ChaCha20 stream cipher (RFC 7539) for VFS BLC and entry decryption.
/// </summary>
public static class CSChaCha20
{
    public const int AllowedKeyLength = 32;
    public const int AllowedNonceLength = 12;
    public const int BlockSize = 64;

    private static ReadOnlySpan<byte> Sigma => "expand 32-byte k"u8;

    /// <summary>
    /// Decrypts (or encrypts) <paramref name="data"/> in place without heap allocations.
    /// </summary>
    public static void DecryptInPlace(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        uint counter,
        Span<byte> data
    )
    {
        if (key.Length != AllowedKeyLength)
            throw new ArgumentException($"Key length must be {AllowedKeyLength}. Actual: {key.Length}", nameof(key));
        if (nonce.Length != AllowedNonceLength)
            throw new ArgumentException($"Nonce length must be {AllowedNonceLength}. Actual: {nonce.Length}", nameof(nonce));
        if (data.IsEmpty)
            return;

        ReadOnlySpan<byte> sigma = Sigma;
        uint s0 = BinaryPrimitives.ReadUInt32LittleEndian(sigma);
        uint s1 = BinaryPrimitives.ReadUInt32LittleEndian(sigma[4..]);
        uint s2 = BinaryPrimitives.ReadUInt32LittleEndian(sigma[8..]);
        uint s3 = BinaryPrimitives.ReadUInt32LittleEndian(sigma[12..]);
        uint s4 = BinaryPrimitives.ReadUInt32LittleEndian(key);
        uint s5 = BinaryPrimitives.ReadUInt32LittleEndian(key[4..]);
        uint s6 = BinaryPrimitives.ReadUInt32LittleEndian(key[8..]);
        uint s7 = BinaryPrimitives.ReadUInt32LittleEndian(key[12..]);
        uint s8 = BinaryPrimitives.ReadUInt32LittleEndian(key[16..]);
        uint s9 = BinaryPrimitives.ReadUInt32LittleEndian(key[20..]);
        uint s10 = BinaryPrimitives.ReadUInt32LittleEndian(key[24..]);
        uint s11 = BinaryPrimitives.ReadUInt32LittleEndian(key[28..]);
        uint s12 = counter;
        uint s13 = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
        uint s14 = BinaryPrimitives.ReadUInt32LittleEndian(nonce[4..]);
        uint s15 = BinaryPrimitives.ReadUInt32LittleEndian(nonce[8..]);

        if (Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            DecryptVector128(s0, s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11, s12, s13, s14, s15, data);
            return;
        }

        DecryptScalar(s0, s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11, s12, s13, s14, s15, data);
    }

    private static void DecryptVector128(
        uint s0, uint s1, uint s2, uint s3,
        uint s4, uint s5, uint s6, uint s7,
        uint s8, uint s9, uint s10, uint s11,
        uint s12, uint s13, uint s14, uint s15,
        Span<byte> data)
    {
        Vector128<uint> r0 = Vector128.Create(s0, s1, s2, s3);
        Vector128<uint> r1 = Vector128.Create(s4, s5, s6, s7);
        Vector128<uint> r2 = Vector128.Create(s8, s9, s10, s11);
        Vector128<uint> r3 = Vector128.Create(s12, s13, s14, s15);

        Vector128<uint> diag1 = Vector128.Create(1u, 2u, 3u, 0u);
        Vector128<uint> diag2 = Vector128.Create(2u, 3u, 0u, 1u);
        Vector128<uint> diag3 = Vector128.Create(3u, 0u, 1u, 2u);

        int offset = 0;
        int fullBlocks = data.Length / BlockSize;
        int tailBytes = data.Length - fullBlocks * BlockSize;
        ref byte dst = ref MemoryMarshal.GetReference(data);

        for (int loop = 0; loop < fullBlocks; loop++)
        {
            Vector128<uint> x0 = r0;
            Vector128<uint> x1 = r1;
            Vector128<uint> x2 = r2;
            Vector128<uint> x3 = r3;

            for (int i = 0; i < 10; i++)
            {
                VectorQuarterRound(ref x0, ref x1, ref x2, ref x3);
                x1 = Vector128.Shuffle(x1, diag1);
                x2 = Vector128.Shuffle(x2, diag2);
                x3 = Vector128.Shuffle(x3, diag3);

                VectorQuarterRound(ref x0, ref x1, ref x2, ref x3);
                x1 = Vector128.Shuffle(x1, diag3);
                x2 = Vector128.Shuffle(x2, diag2);
                x3 = Vector128.Shuffle(x3, diag1);
            }

            x0 += r0;
            x1 += r1;
            x2 += r2;
            x3 += r3;

            nuint uoff = (nuint)offset;
            (Vector128.LoadUnsafe(ref dst, uoff) ^ x0.AsByte()).StoreUnsafe(ref dst, uoff);
            (Vector128.LoadUnsafe(ref dst, uoff + 16) ^ x1.AsByte()).StoreUnsafe(ref dst, uoff + 16);
            (Vector128.LoadUnsafe(ref dst, uoff + 32) ^ x2.AsByte()).StoreUnsafe(ref dst, uoff + 32);
            (Vector128.LoadUnsafe(ref dst, uoff + 48) ^ x3.AsByte()).StoreUnsafe(ref dst, uoff + 48);

            offset += BlockSize;
            uint nextCounter = unchecked(r3.GetElement(0) + 1);
            r3 = r3.WithElement(0, nextCounter);
            if (nextCounter == 0)
                r3 = r3.WithElement(1, unchecked(r3.GetElement(1) + 1));
        }

        if (tailBytes > 0)
        {
            Vector128<uint> x0 = r0;
            Vector128<uint> x1 = r1;
            Vector128<uint> x2 = r2;
            Vector128<uint> x3 = r3;

            for (int i = 0; i < 10; i++)
            {
                VectorQuarterRound(ref x0, ref x1, ref x2, ref x3);
                x1 = Vector128.Shuffle(x1, diag1);
                x2 = Vector128.Shuffle(x2, diag2);
                x3 = Vector128.Shuffle(x3, diag3);

                VectorQuarterRound(ref x0, ref x1, ref x2, ref x3);
                x1 = Vector128.Shuffle(x1, diag3);
                x2 = Vector128.Shuffle(x2, diag2);
                x3 = Vector128.Shuffle(x3, diag1);
            }

            x0 += r0;
            x1 += r1;
            x2 += r2;
            x3 += r3;

            Span<byte> keystream = stackalloc byte[BlockSize];
            ref byte ks = ref MemoryMarshal.GetReference(keystream);
            x0.AsByte().StoreUnsafe(ref ks, 0);
            x1.AsByte().StoreUnsafe(ref ks, 16);
            x2.AsByte().StoreUnsafe(ref ks, 32);
            x3.AsByte().StoreUnsafe(ref ks, 48);
            for (int i = 0; i < tailBytes; i++)
                data[offset + i] ^= keystream[i];
            keystream.Clear();
        }
    }

    private static void DecryptScalar(
        uint s0, uint s1, uint s2, uint s3,
        uint s4, uint s5, uint s6, uint s7,
        uint s8, uint s9, uint s10, uint s11,
        uint s12, uint s13, uint s14, uint s15,
        Span<byte> data)
    {
        Span<byte> keystream = stackalloc byte[BlockSize];
        try
        {
            int offset = 0;
            int remaining = data.Length;
            while (remaining > 0)
            {
                uint x0 = s0, x1 = s1, x2 = s2, x3 = s3;
                uint x4 = s4, x5 = s5, x6 = s6, x7 = s7;
                uint x8 = s8, x9 = s9, x10 = s10, x11 = s11;
                uint x12 = s12, x13 = s13, x14 = s14, x15 = s15;

                for (int i = 0; i < 10; i++)
                {
                    QuarterRound(ref x0, ref x4, ref x8, ref x12);
                    QuarterRound(ref x1, ref x5, ref x9, ref x13);
                    QuarterRound(ref x2, ref x6, ref x10, ref x14);
                    QuarterRound(ref x3, ref x7, ref x11, ref x15);

                    QuarterRound(ref x0, ref x5, ref x10, ref x15);
                    QuarterRound(ref x1, ref x6, ref x11, ref x12);
                    QuarterRound(ref x2, ref x7, ref x8, ref x13);
                    QuarterRound(ref x3, ref x4, ref x9, ref x14);
                }

                BinaryPrimitives.WriteUInt32LittleEndian(keystream[0..], unchecked(x0 + s0));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[4..], unchecked(x1 + s1));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[8..], unchecked(x2 + s2));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[12..], unchecked(x3 + s3));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[16..], unchecked(x4 + s4));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[20..], unchecked(x5 + s5));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[24..], unchecked(x6 + s6));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[28..], unchecked(x7 + s7));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[32..], unchecked(x8 + s8));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[36..], unchecked(x9 + s9));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[40..], unchecked(x10 + s10));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[44..], unchecked(x11 + s11));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[48..], unchecked(x12 + s12));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[52..], unchecked(x13 + s13));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[56..], unchecked(x14 + s14));
                BinaryPrimitives.WriteUInt32LittleEndian(keystream[60..], unchecked(x15 + s15));

                s12 = unchecked(s12 + 1);
                if (s12 == 0)
                    s13 = unchecked(s13 + 1);

                int count = Math.Min(BlockSize, remaining);
                for (int i = 0; i < count; i++)
                    data[offset + i] ^= keystream[i];
                offset += count;
                remaining -= count;
            }
        }
        finally
        {
            keystream.Clear();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorQuarterRound(
        ref Vector128<uint> a,
        ref Vector128<uint> b,
        ref Vector128<uint> c,
        ref Vector128<uint> d)
    {
        a += b;
        d = RotateLeft(d ^ a, 16);

        c += d;
        b = RotateLeft(b ^ c, 12);

        a += b;
        d = RotateLeft(d ^ a, 8);

        c += d;
        b = RotateLeft(b ^ c, 7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> RotateLeft(Vector128<uint> value, int count) =>
        (value << count) | (value >>> (32 - count));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a = unchecked(a + b);
        d = RotateLeft(d ^ a, 16);

        c = unchecked(c + d);
        b = RotateLeft(b ^ c, 12);

        a = unchecked(a + b);
        d = RotateLeft(d ^ a, 8);

        c = unchecked(c + d);
        b = RotateLeft(b ^ c, 7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int count) =>
        unchecked((value << count) | (value >> (32 - count)));
}
