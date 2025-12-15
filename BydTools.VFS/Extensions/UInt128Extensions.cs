using System.Buffers.Binary;

namespace BydTools.VFS.Extensions;

/// <summary>
/// Extension methods for UInt128 to convert to hex strings.
/// </summary>
internal static class UInt128Extensions
{
    /// <summary>
    /// Converts a UInt128 value to a hexadecimal string in big-endian format.
    /// This is used for MD5 hash names in BLC/CHK files.
    /// </summary>
    /// <param name="value">The UInt128 value to convert.</param>
    /// <returns>A 32-character uppercase hexadecimal string.</returns>
    public static string ToHexStringBigEndian(this UInt128 value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Converts a UInt128 value to a hexadecimal string in little-endian format.
    /// </summary>
    /// <param name="value">The UInt128 value to convert.</param>
    /// <returns>A 32-character uppercase hexadecimal string.</returns>
    public static string ToHexStringLittleEndian(this UInt128 value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(bytes, value);
        return Convert.ToHexString(bytes);
    }
}



