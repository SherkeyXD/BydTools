using System;

namespace BnkExtractor.Ww2ogg.Extensions;

internal static class ArrayExtensions
{
    public static T[] Subset<T>(this T[] array, int offset, int count)
    {
        if (array == null)
            throw new ArgumentNullException(nameof(array));
        if (offset < 0 || offset >= array.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot be negative");
        if (array.Length - offset < count)
            throw new ArgumentOutOfRangeException(
                nameof(count),
                "Count must be less than or equal to the space available"
            );

        T[] result = new T[count];
        Array.Copy(array, offset, result, 0, count);
        return result;
    }
}
