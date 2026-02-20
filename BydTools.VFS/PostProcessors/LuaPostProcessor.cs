using BydTools.Utils;
using BydTools.VFS.xLua;

namespace BydTools.VFS.PostProcessors;

/// <summary>
/// Decrypts encrypted Lua bytecode files.
/// </summary>
public sealed class LuaPostProcessor : IPostProcessor
{
    private readonly ILogger _logger;
    private readonly string _masterKey;

    private LuaPostProcessor(ILogger logger, string masterKey)
    {
        _logger = logger;
        _masterKey = masterKey;
    }

    /// <summary>
    /// Creates a <see cref="LuaPostProcessor"/> if the Lua master key can be derived.
    /// Returns <c>null</c> when the key is unavailable.
    /// </summary>
    public static LuaPostProcessor? TryCreate(ILogger logger)
    {
        var masterKey = LuaDecipher.GetMasterKey();
        if (string.IsNullOrEmpty(masterKey))
            return null;

        logger.Verbose("Lua master key initialized successfully");
        return new LuaPostProcessor(logger, masterKey);
    }

    public bool TryProcess(byte[] data, string outputPath)
    {
        try
        {
            var base64String = System.Text.Encoding.UTF8.GetString(data).Trim();
            var decryptedLua = LuaDecipher.DecryptLuaWithMasterKey(base64String, _masterKey);

            if (decryptedLua != null && LuaDecipher.IsValidLuaBytecode(decryptedLua))
            {
                var luaFilePath = Path.ChangeExtension(outputPath, ".lua");
                File.WriteAllBytes(luaFilePath, decryptedLua);
                _logger.Verbose(
                    $"  Decrypted Lua: {Path.GetFileName(outputPath)} -> {Path.GetFileName(luaFilePath)}"
                );
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.Verbose(
                $"  Lua decryption failed for {Path.GetFileName(outputPath)}: {ex.Message}"
            );
        }

        return false;
    }
}
