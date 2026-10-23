using System.Text;
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
