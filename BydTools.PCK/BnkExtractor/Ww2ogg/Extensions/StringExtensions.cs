namespace BnkExtractor.Ww2ogg.Extensions;

internal static class StringExtensions
{
    public static byte[] ToByteArray(this string _this)
    {
        byte[] bytes = new byte[_this.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)_this[i];
        }
        return bytes;
    }
}
