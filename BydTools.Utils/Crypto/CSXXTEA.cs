/*
 * XXTEA encryption algorithm library for .NET
 *
 * Encryption Algorithm Authors:
 *   David J. Wheeler
 *   Roger M. Needham
 *
 * Original Code Author: Ma Bingyao <mabingyao@gmail.com>
 * Modified for BydTools.Utils
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

using System;
using System.Text;

namespace BydTools.Utils.Crypto;

/// <summary>
/// XXTEA (Corrected Block TEA) encryption algorithm implementation
/// </summary>
public sealed class XXTEA
{
    private static readonly UTF8Encoding utf8 = new();
    private const uint Delta = 0x9E3779B9;

    private XXTEA() { }

    #region Public Encryption Methods

    /// <summary>
    /// Encrypts byte array data with byte array key
    /// </summary>
    /// <param name="data">Data to encrypt</param>
    /// <param name="key">Encryption key</param>
    /// <returns>Encrypted data or null if encryption fails</returns>
    public static byte[]? Encrypt(byte[] data, byte[] key)
    {
        if (data.Length == 0)
        {
            return data;
        }
        return ToByteArray(
            Encrypt(ToUInt32Array(data, true), ToUInt32Array(FixKey(key), false)),
            false
        );
    }

    /// <summary>
    /// Encrypts string data with byte array key
    /// </summary>
    /// <param name="data">Data to encrypt</param>
    /// <param name="key">Encryption key</param>
    /// <returns>Encrypted data or null if encryption fails</returns>
    public static byte[]? Encrypt(string data, byte[] key)
    {
        return Encrypt(utf8.GetBytes(data), key);
    }

    /// <summary>
    /// Encrypts byte array data with string key
    /// </summary>
    /// <param name="data">Data to encrypt</param>
    /// <param name="key">Encryption key</param>
    /// <returns>Encrypted data or null if encryption fails</returns>
    public static byte[]? Encrypt(byte[] data, string key)
    {
        return Encrypt(data, utf8.GetBytes(key));
    }

    /// <summary>
    /// Encrypts string data with string key
    /// </summary>
    /// <param name="data">Data to encrypt</param>
    /// <param name="key">Encryption key</param>
    /// <returns>Encrypted data or null if encryption fails</returns>
    public static byte[]? Encrypt(string data, string key)
    {
        return Encrypt(utf8.GetBytes(data), utf8.GetBytes(key));
    }

    /// <summary>
    /// Encrypts byte array data and returns Base64 encoded string
    /// </summary>
    /// <param name="data">Data to encrypt</param>
    /// <param name="key">Encryption key</param>
    /// <returns>Base64 encoded encrypted data or null if encryption fails</returns>
    public static string? EncryptToBase64String(byte[] data, byte[] key)
    {
        var encrypted = Encrypt(data, key);
        return encrypted != null ? Convert.ToBase64String(encrypted) : null;
    }

    /// <summary>
    /// Encrypts string data and returns Base64 encoded string
    /// </summary>
    /// <param name="data">Data to encrypt</param>
    /// <param name="key">Encryption key</param>
    /// <returns>Base64 encoded encrypted data or null if encryption fails</returns>
    public static string? EncryptToBase64String(string data, byte[] key)
    {
        var encrypted = Encrypt(data, key);
        return encrypted != null ? Convert.ToBase64String(encrypted) : null;
    }

    /// <summary>
    /// Encrypts byte array data with string key and returns Base64 encoded string
    /// </summary>
    /// <param name="data">Data to encrypt</param>
    /// <param name="key">Encryption key</param>
    /// <returns>Base64 encoded encrypted data or null if encryption fails</returns>
    public static string? EncryptToBase64String(byte[] data, string key)
    {
        var encrypted = Encrypt(data, key);
        return encrypted != null ? Convert.ToBase64String(encrypted) : null;
    }

    /// <summary>
    /// Encrypts string data with string key and returns Base64 encoded string
    /// </summary>
    /// <param name="data">Data to encrypt</param>
    /// <param name="key">Encryption key</param>
    /// <returns>Base64 encoded encrypted data or null if encryption fails</returns>
    public static string? EncryptToBase64String(string data, string key)
    {
        var encrypted = Encrypt(data, key);
        return encrypted != null ? Convert.ToBase64String(encrypted) : null;
    }

    #endregion

    #region Public Decryption Methods

    /// <summary>
    /// Decrypts byte array data with byte array key
    /// </summary>
    /// <param name="data">Data to decrypt</param>
    /// <param name="key">Decryption key</param>
    /// <returns>Decrypted data or null if decryption fails</returns>
    public static byte[]? Decrypt(byte[] data, byte[] key)
    {
        if (data.Length == 0)
        {
            return data;
        }
        return ToByteArray(
            Decrypt(ToUInt32Array(data, false), ToUInt32Array(FixKey(key), false)),
            true
        );
    }

    /// <summary>
    /// Decrypts byte array data with string key
    /// </summary>
    /// <param name="data">Data to decrypt</param>
    /// <param name="key">Decryption key</param>
    /// <returns>Decrypted data or null if decryption fails</returns>
    public static byte[]? Decrypt(byte[] data, string key)
    {
        return Decrypt(data, utf8.GetBytes(key));
    }

    /// <summary>
    /// Decrypts Base64 encoded string with byte array key
    /// </summary>
    /// <param name="data">Base64 encoded data to decrypt</param>
    /// <param name="key">Decryption key</param>
    /// <returns>Decrypted data or null if decryption fails</returns>
    public static byte[]? DecryptBase64String(string data, byte[] key)
    {
        return Decrypt(Convert.FromBase64String(data), key);
    }

    /// <summary>
    /// Decrypts Base64 encoded string with string key
    /// </summary>
    /// <param name="data">Base64 encoded data to decrypt</param>
    /// <param name="key">Decryption key</param>
    /// <returns>Decrypted data or null if decryption fails</returns>
    public static byte[]? DecryptBase64String(string data, string key)
    {
        return Decrypt(Convert.FromBase64String(data), key);
    }

    /// <summary>
    /// Decrypts byte array data and returns UTF8 string
    /// </summary>
    /// <param name="data">Data to decrypt</param>
    /// <param name="key">Decryption key</param>
    /// <returns>Decrypted UTF8 string or null if decryption fails</returns>
    public static string? DecryptToString(byte[] data, byte[] key)
    {
        var decrypted = Decrypt(data, key);
        return decrypted != null ? utf8.GetString(decrypted) : null;
    }

    /// <summary>
    /// Decrypts byte array data with string key and returns UTF8 string
    /// </summary>
    /// <param name="data">Data to decrypt</param>
    /// <param name="key">Decryption key</param>
    /// <returns>Decrypted UTF8 string or null if decryption fails</returns>
    public static string? DecryptToString(byte[] data, string key)
    {
        var decrypted = Decrypt(data, key);
        return decrypted != null ? utf8.GetString(decrypted) : null;
    }

    /// <summary>
    /// Decrypts Base64 encoded string and returns UTF8 string
    /// </summary>
    /// <param name="data">Base64 encoded data to decrypt</param>
    /// <param name="key">Decryption key</param>
    /// <returns>Decrypted UTF8 string or null if decryption fails</returns>
    public static string? DecryptBase64StringToString(string data, byte[] key)
    {
        var decrypted = DecryptBase64String(data, key);
        return decrypted != null ? utf8.GetString(decrypted) : null;
    }

    /// <summary>
    /// Decrypts Base64 encoded string with string key and returns UTF8 string
    /// </summary>
    /// <param name="data">Base64 encoded data to decrypt</param>
    /// <param name="key">Decryption key</param>
    /// <returns>Decrypted UTF8 string or null if decryption fails</returns>
    public static string? DecryptBase64StringToString(string data, string key)
    {
        var decrypted = DecryptBase64String(data, key);
        return decrypted != null ? utf8.GetString(decrypted) : null;
    }

    #endregion

    #region Private Methods

    private static uint MX(uint sum, uint y, uint z, int p, uint e, uint[] k)
    {
        return (z >> 5 ^ y << 2) + (y >> 3 ^ z << 4) ^ (sum ^ y) + (k[p & 3 ^ e] ^ z);
    }

    private static uint[] Encrypt(uint[] v, uint[] k)
    {
        int n = v.Length - 1;
        if (n < 1)
        {
            return v;
        }

        uint z = v[n],
            y,
            sum = 0,
            e;
        int p,
            q = 6 + 52 / (n + 1);
        unchecked
        {
            while (0 < q--)
            {
                sum += Delta;
                e = sum >> 2 & 3;
                for (p = 0; p < n; p++)
                {
                    y = v[p + 1];
                    z = v[p] += MX(sum, y, z, p, e, k);
                }
                y = v[0];
                z = v[n] += MX(sum, y, z, p, e, k);
            }
        }
        return v;
    }

    private static uint[] Decrypt(uint[] v, uint[] k)
    {
        int n = v.Length - 1;
        if (n < 1)
        {
            return v;
        }

        uint z,
            y = v[0],
            sum,
            e;
        int p,
            q = 6 + 52 / (n + 1);
        unchecked
        {
            sum = (uint)(q * Delta);
            while (sum != 0)
            {
                e = sum >> 2 & 3;
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

    private static byte[] FixKey(byte[] key)
    {
        if (key.Length == 16)
            return key;

        byte[] fixedKey = new byte[16];
        if (key.Length < 16)
        {
            key.CopyTo(fixedKey, 0);
        }
        else
        {
            Array.Copy(key, 0, fixedKey, 0, 16);
        }
        return fixedKey;
    }

    private static uint[] ToUInt32Array(byte[] data, bool includeLength)
    {
        int length = data.Length;
        int n = ((length & 3) == 0) ? (length >> 2) : ((length >> 2) + 1);
        uint[] result;

        if (includeLength)
        {
            result = new uint[n + 1];
            result[n] = (uint)length;
        }
        else
        {
            result = new uint[n];
        }

        for (int i = 0; i < length; i++)
        {
            result[i >> 2] |= (uint)data[i] << ((i & 3) << 3);
        }

        return result;
    }

    private static byte[]? ToByteArray(uint[] data, bool includeLength)
    {
        int n = data.Length << 2;

        if (includeLength)
        {
            int m = (int)data[data.Length - 1];
            n -= 4;
            if ((m < n - 3) || (m > n))
            {
                return null;
            }
            n = m;
        }

        byte[] result = new byte[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = (byte)(data[i >> 2] >> ((i & 3) << 3));
        }

        return result;
    }

    #endregion
}
