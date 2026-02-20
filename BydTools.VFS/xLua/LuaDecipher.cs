using System;
using System.Linq;
using System.Text;
using BydTools.Utils.Crypto;

namespace BydTools.VFS.xLua;

/// <summary>
/// Provides functionality to decrypt encrypted Lua bytecode
/// </summary>
public static class LuaDecipher
{
    private static readonly string[] Keys = new[]
    {
        "cynb5",
        "paeky",
        "xmF5og",
        "ud35+e",
        "72iUy",
        "azWk3",
        "901lU",
        "dDfl2",
    };

    private const string InitialKey = "Assets/Beyond/InitialAssets/";
    private const string DefaultDecryptionKey = "Assets/Beyond/DynamicAssets/Gameplay/UI/Fonts/";

    public static byte[]? DecryptLua(string encryptedLuaBase64)
    {
        try
        {
            string? masterKey = GetMasterKey();
            if (string.IsNullOrEmpty(masterKey))
            {
                return null;
            }

            byte[] data = Convert.FromBase64String(encryptedLuaBase64);
            return XXTEA.Decrypt(data, Encoding.UTF8.GetBytes(masterKey));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts Lua bytecode from encrypted byte array
    /// </summary>
    /// <param name="encryptedData">Encrypted Lua bytecode</param>
    /// <returns>Decrypted Lua bytecode as byte array, or null if decryption fails</returns>
    public static byte[]? DecryptLua(byte[] encryptedData)
    {
        try
        {
            string? masterKey = GetMasterKey();
            if (string.IsNullOrEmpty(masterKey))
            {
                return null;
            }

            return XXTEA.Decrypt(encryptedData, Encoding.UTF8.GetBytes(masterKey));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts Lua bytecode with a custom key
    /// </summary>
    /// <param name="encryptedLuaBase64">Base64 encoded encrypted Lua bytecode</param>
    /// <param name="customKey">Custom decryption key</param>
    /// <returns>Decrypted Lua bytecode as byte array, or null if decryption fails</returns>
    public static byte[]? DecryptLuaWithKey(string encryptedLuaBase64, string customKey)
    {
        try
        {
            if (string.IsNullOrEmpty(customKey))
            {
                return null;
            }

            byte[] data = Convert.FromBase64String(encryptedLuaBase64);
            return XXTEA.Decrypt(data, Encoding.UTF8.GetBytes(customKey));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts Lua bytecode with a custom key
    /// </summary>
    /// <param name="encryptedData">Encrypted Lua bytecode</param>
    /// <param name="customKey">Custom decryption key</param>
    /// <returns>Decrypted Lua bytecode as byte array, or null if decryption fails</returns>
    public static byte[]? DecryptLuaWithKey(byte[] encryptedData, string customKey)
    {
        try
        {
            if (string.IsNullOrEmpty(customKey))
            {
                return null;
            }

            return XXTEA.Decrypt(encryptedData, Encoding.UTF8.GetBytes(customKey));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the master key used for decryption
    /// </summary>
    /// <returns>Master decryption key, or null if key cannot be obtained</returns>
    public static string? GetMasterKey()
    {
        if (Keys.Length <= 5)
        {
            return null;
        }

        string encryptedMasterKey = $"{Keys[1]}{Keys[5]}{Keys[3]}{Keys[2]}==";

        byte[]? masterKeyBytes = DecryptSubtraction(encryptedMasterKey, InitialKey);
        if (masterKeyBytes == null || masterKeyBytes.Length == 0)
        {
            return null;
        }

        return Encoding.UTF8.GetString(masterKeyBytes);
    }

    /// <summary>
    /// Decrypts Lua bytecode with a pre-computed master key
    /// </summary>
    /// <param name="encryptedLuaBase64">Base64 encoded encrypted Lua bytecode</param>
    /// <param name="masterKey">Pre-computed master key</param>
    /// <returns>Decrypted Lua bytecode as byte array, or null if decryption fails</returns>
    public static byte[]? DecryptLuaWithMasterKey(string encryptedLuaBase64, string masterKey)
    {
        try
        {
            byte[] data = Convert.FromBase64String(encryptedLuaBase64);
            return XXTEA.Decrypt(data, Encoding.UTF8.GetBytes(masterKey));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts Lua bytecode with a pre-computed master key
    /// </summary>
    /// <param name="encryptedData">Encrypted Lua bytecode</param>
    /// <param name="masterKey">Pre-computed master key</param>
    /// <returns>Decrypted Lua bytecode as byte array, or null if decryption fails</returns>
    public static byte[]? DecryptLuaWithMasterKey(byte[] encryptedData, string masterKey)
    {
        try
        {
            return XXTEA.Decrypt(encryptedData, Encoding.UTF8.GetBytes(masterKey));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts data using subtraction cipher
    /// </summary>
    /// <param name="encryptedText">Base64 encoded encrypted text</param>
    /// <param name="key">Decryption key (defaults to DefaultDecryptionKey if null or empty)</param>
    /// <returns>Decrypted data as byte array, or null if decryption fails</returns>
    private static byte[]? DecryptSubtraction(string encryptedText, string? key = null)
    {
        try
        {
            if (string.IsNullOrEmpty(key))
            {
                key = DefaultDecryptionKey;
            }

            byte[] data = Convert.FromBase64String(encryptedText);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            int keyLen = keyBytes.Length;

            if (keyLen == 0)
            {
                return data;
            }

            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(data[i] - keyBytes[i % keyLen]);
            }

            return data;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to decrypt Lua bytecode and returns decryption status
    /// </summary>
    /// <param name="encryptedLuaBase64">Base64 encoded encrypted Lua bytecode</param>
    /// <param name="decryptedData">Decrypted Lua bytecode (output parameter)</param>
    /// <returns>True if decryption succeeded, false otherwise</returns>
    public static bool TryDecryptLua(string encryptedLuaBase64, out byte[]? decryptedData)
    {
        decryptedData = DecryptLua(encryptedLuaBase64);
        return decryptedData != null && decryptedData.Length > 0;
    }

    /// <summary>
    /// Attempts to decrypt Lua bytecode and returns decryption status
    /// </summary>
    /// <param name="encryptedData">Encrypted Lua bytecode</param>
    /// <param name="decryptedData">Decrypted Lua bytecode (output parameter)</param>
    /// <returns>True if decryption succeeded, false otherwise</returns>
    public static bool TryDecryptLua(byte[] encryptedData, out byte[]? decryptedData)
    {
        decryptedData = DecryptLua(encryptedData);
        return decryptedData != null && decryptedData.Length > 0;
    }

    /// <summary>
    /// Validates if the decrypted data appears to be valid Lua bytecode or source code
    /// </summary>
    /// <param name="data">Decrypted data to validate</param>
    /// <returns>True if data appears to be valid Lua bytecode or source code</returns>
    public static bool IsValidLuaBytecode(byte[]? data)
    {
        if (data == null || data.Length < 4)
        {
            return false;
        }

        // Check for Lua bytecode signature (0x1B 'L' 'u' 'a')
        if (data[0] == 0x1B && data[1] == 0x4C && data[2] == 0x75 && data[3] == 0x61)
        {
            return true;
        }

        // Check if it's valid UTF-8 text (Lua source code)
        // Look for common Lua keywords at the start
        try
        {
            var text = Encoding.UTF8.GetString(data.Take(Math.Min(1000, data.Length)).ToArray());
            // Remove BOM and whitespace
            text = text.TrimStart('\uFEFF', '\r', '\n', ' ', '\t');

            // Check for common Lua patterns
            return text.StartsWith("local ")
                || text.StartsWith("function ")
                || text.StartsWith("return ")
                || text.StartsWith("require ")
                || text.StartsWith("--")
                || text.StartsWith("config ")
                || text.Contains("local ")
                || text.Contains("function(")
                || (data.Length > 10 && data.All(b => b < 128 || b >= 0xC0)); // Valid UTF-8 range
        }
        catch
        {
            return false;
        }
    }
}
