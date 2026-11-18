/*
 * XXTEA encryption algorithm library for .NET
 *
 * Encryption Algorithm Authors:
 *   David J. Wheeler
 *   Roger M. Needham
 *
 * Original Code Author: Ma Bingyao <mabingyao@gmail.com>
 * Modified for BydTools.Core
 *
 * The MIT License (MIT)
 * Copyright (c) 2008-2016 Ma Bingyao
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

namespace BydTools.Core.Crypto;

/// <summary>
/// XXTEA (Corrected Block TEA) decryption implementation.
/// </summary>
public static class XXTEA
{
    private const uint Delta = 0x9E3779B9;

    public static byte[]? Decrypt(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        if (data.IsEmpty)
            return [];

        return ToByteArray(DecryptWords(ToUInt32Array(data), ToUInt32Array(FixKey(key))));
    }

    private static uint MX(uint sum, uint y, uint z, int p, uint e, uint[] k) =>
        ((z >> 5 ^ y << 2) + (y >> 3 ^ z << 4)) ^ ((sum ^ y) + (k[p & 3 ^ e] ^ z));

    private static uint[] DecryptWords(uint[] v, uint[] k)
    {
        int n = v.Length - 1;
        if (n < 1)
            return v;

        uint z;
        uint y = v[0];
        int p;
        int q = 6 + 52 / (n + 1);
        unchecked
        {
            uint sum = (uint)(q * Delta);
            while (sum != 0)
            {
                uint e = sum >> 2 & 3;
                for (p = n; p > 0; p--)
                {
                    z = v[p - 1];
                    y = v[p] -= MX(sum, y, z, p, e, k);
                }
                z = v[n];
                y = v[0] -= MX(sum, y, z, p, e, k);
                sum -= Delta;
            }
        }
        return v;
    }

    private static byte[] FixKey(ReadOnlySpan<byte> key)
    {
        byte[] fixedKey = new byte[16];
        int copyLength = Math.Min(key.Length, 16);
        key[..copyLength].CopyTo(fixedKey);
        return fixedKey;
    }

    private static uint[] ToUInt32Array(ReadOnlySpan<byte> data)
    {
        int length = data.Length;
        int n = (length & 3) == 0 ? (length >> 2) : ((length >> 2) + 1);
        uint[] result = new uint[n];
        for (int i = 0; i < length; i++)
            result[i >> 2] |= (uint)data[i] << ((i & 3) << 3);
        return result;
    }

    private static byte[]? ToByteArray(uint[] data)
    {
        int n = data.Length << 2;
        int m = (int)data[^1];
        n -= 4;
        if (m < n - 3 || m > n)
            return null;

        byte[] result = new byte[m];
        for (int i = 0; i < m; i++)
            result[i] = (byte)(data[i >> 2] >> ((i & 3) << 3));
        return result;
    }
}
