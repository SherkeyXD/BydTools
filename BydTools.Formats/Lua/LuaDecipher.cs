using System.Text;
using System.Text.Unicode;
using BydTools.Core.Crypto;

namespace BydTools.Formats.Lua;

/// <summary>Decrypts the XXTEA-wrapped Lua shipped inside the Lua VFS block.</summary>
public static class LuaDecipher
{
    private static readonly byte[] MasterKey = Encoding.UTF8.GetBytes(DeriveMasterKey());

    public static byte[]? Decrypt(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data).Trim();
        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }

        return XXTEA.Decrypt(cipher, MasterKey);
    }

    public static bool IsValid(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return false;
        if (data[0] == 0x1B && data[1] == (byte)'L' && data[2] == (byte)'u' && data[3] == (byte)'a')
            return true;
        if (!IsUtf8(data))
            return false;

        int sample = Math.Min(data.Length, 1000);
        string text = Encoding.UTF8.GetString(data[..sample]).TrimStart('\uFEFF', '\r', '\n', ' ', '\t');
        return text.StartsWith("local ", StringComparison.Ordinal)
            || text.StartsWith("function ", StringComparison.Ordinal)
            || text.StartsWith("return ", StringComparison.Ordinal)
            || text.StartsWith("require ", StringComparison.Ordinal)
            || text.StartsWith("--", StringComparison.Ordinal)
            || text.StartsWith("config ", StringComparison.Ordinal)
            || text.Contains("local ", StringComparison.Ordinal)
            || text.Contains("function(", StringComparison.Ordinal);
    }

    private static bool IsUtf8(ReadOnlySpan<byte> data)
    {
        try
        {
            Encoding.UTF8.GetString(data);
            return Utf8.IsValid(data);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string DeriveMasterKey()
    {
        string[] keys = ["cynb5", "paeky", "xmF5og", "ud35+e", "72iUy", "azWk3", "901lU", "dDfl2"];
        const string initialKey = "Assets/Beyond/InitialAssets/";
        string packed = $"{keys[1]}{keys[5]}{keys[3]}{keys[2]}==";
        byte[] data = Convert.FromBase64String(packed);
        byte[] keyBytes = Encoding.UTF8.GetBytes(initialKey);
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(data[i] - keyBytes[i % keyBytes.Length]);
        return Encoding.UTF8.GetString(data);
    }
}
