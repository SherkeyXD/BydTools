using System;
using BnkExtractor.Ww2ogg.Extensions;

namespace BnkExtractor.Ww2ogg
{
    internal static class CppUtils
    {
        /// <summary>
        /// Compares two byte arrays
        /// </summary>
        /// <param name="valueCount">The number of values in the arrays</param>
        /// <returns>True if they're inequal</returns>
        /// <exception cref="ArgumentException"></exception>
        internal static bool memcmp(byte[] array1, byte[] array2, int valueCount)
        {
            if (array1.Length != valueCount)
                throw new ArgumentException(
                    nameof(array1),
                    $"Length does not match {nameof(valueCount)}"
                );
            if (array2.Length != valueCount)
                throw new ArgumentException(
                    nameof(array1),
                    $"Length does not match {nameof(valueCount)}"
                );
            for (int i = 0; i < valueCount; i++)
            {
                if (array1[i] != array2[i])
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Compares a string and a byte array
        /// </summary>
        /// <param name="str">A string that gets converted to bytes</param>
        /// <param name="valueCount">The number of values in the arrays</param>
        /// <returns>True if they're inequal</returns>
        internal static bool memcmp(byte[] bytes, string str, int valueCount) =>
            memcmp(bytes, str.ToByteArray(), valueCount);
    }
}
