using System.Globalization;
using System.Text;

namespace BydTools.Utils.VGMToolbox;

/// <summary>
/// Provides utilities for byte-level conversions, encoding transformations,
/// and multi-byte integer handling with endianness support.
/// </summary>
public static class ByteConverter
{
    // Code page constants
    private const int CodePageJapanese = 932;
    private const int CodePageCyrillic = 1251;

    #region String Encoding

    /// <summary>Decodes bytes using the specified code page.</summary>
    public static string Decode(byte[] data, int codePage) =>
        Encoding.GetEncoding(codePage).GetString(data);

    /// <summary>Decodes bytes using Japanese (Shift-JIS) encoding.</summary>
    public static string DecodeJapanese(byte[] data) => Decode(data, CodePageJapanese);

    /// <summary>Decodes bytes using Cyrillic (Windows-1251) encoding.</summary>
    public static string DecodeCyrillic(byte[] data) => Decode(data, CodePageCyrillic);

    /// <summary>Decodes bytes using ASCII encoding.</summary>
    public static string DecodeAscii(byte[] data) => Encoding.ASCII.GetString(data);

    /// <summary>Decodes bytes using UTF-16 LE encoding.</summary>
    public static string DecodeUtf16Le(byte[] data) => Encoding.Unicode.GetString(data);

    #endregion

    #region String Parsing

    /// <summary>
    /// Parses a numeric string that may be hex (0x prefix) or decimal,
    /// and may be negative. Returns the parsed long value.
    /// </summary>
    public static long ParseLong(string value)
    {
        var isNegative = false;
        var span = value.AsSpan();

        if (span.StartsWith("-"))
        {
            isNegative = true;
            span = span[1..];
        }

        long result;
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            result = long.Parse(span[2..], NumberStyles.HexNumber);
        }
        else
        {
            result = long.Parse(span, NumberStyles.Integer);
        }

        return isNegative ? -result : result;
    }

    #endregion

    #region Byte Manipulation

    /// <summary>Returns the high nibble (upper 4 bits) of a byte.</summary>
    public static byte GetHighNibble(byte value) => (byte)((value >> 4) & 0x0F);

    /// <summary>Returns the low nibble (lower 4 bits) of a byte.</summary>
    public static byte GetLowNibble(byte value) => (byte)(value & 0x0F);

    /// <summary>
    /// Converts a hex string to a byte array.
    /// Example: "AABBCC" -> [0xAA, 0xBB, 0xCC]
    /// </summary>
    public static byte[] HexStringToBytes(string hex)
    {
        var result = new byte[hex.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber);
        }
        return result;
    }

    #endregion

    #region Multi-byte Integer Conversion

    // Note: BitConverter is used for little-endian, manual reversal for big-endian

    public static int ToInt32(byte[] data, int offset, bool littleEndian) =>
        littleEndian ? BitConverter.ToInt32(data, offset) : BitConverter.ToInt32(data.AsSpan(offset, 4).ToArray().Reverse().ToArray(), 0);

    public static long ToInt64(byte[] data, int offset, bool littleEndian) =>
        littleEndian ? BitConverter.ToInt64(data, offset) : BitConverter.ToInt64(data.AsSpan(offset, 8).ToArray().Reverse().ToArray(), 0);

    public static ushort ToUInt16(byte[] data, int offset, bool littleEndian) =>
        littleEndian ? BitConverter.ToUInt16(data, offset) : BitConverter.ToUInt16(data.AsSpan(offset, 2).ToArray().Reverse().ToArray(), 0);

    public static uint ToUInt32(byte[] data, int offset, bool littleEndian) =>
        littleEndian ? BitConverter.ToUInt32(data, offset) : BitConverter.ToUInt32(data.AsSpan(offset, 4).ToArray().Reverse().ToArray(), 0);

    public static ulong ToUInt64(byte[] data, int offset, bool littleEndian) =>
        littleEndian ? BitConverter.ToUInt64(data, offset) : BitConverter.ToUInt64(data.AsSpan(offset, 8).ToArray().Reverse().ToArray(), 0);

    public static byte[] FromInt32(int value, bool littleEndian) =>
        littleEndian ? BitConverter.GetBytes(value) : BitConverter.GetBytes(value).Reverse().ToArray();

    public static byte[] FromInt64(long value, bool littleEndian) =>
        littleEndian ? BitConverter.GetBytes(value) : BitConverter.GetBytes(value).Reverse().ToArray();

    public static byte[] FromUInt16(ushort value, bool littleEndian) =>
        littleEndian ? BitConverter.GetBytes(value) : BitConverter.GetBytes(value).Reverse().ToArray();

    public static byte[] FromUInt32(uint value, bool littleEndian) =>
        littleEndian ? BitConverter.GetBytes(value) : BitConverter.GetBytes(value).Reverse().ToArray();

    public static byte[] FromUInt64(ulong value, bool littleEndian) =>
        littleEndian ? BitConverter.GetBytes(value) : BitConverter.GetBytes(value).Reverse().ToArray();

    #endregion
}
