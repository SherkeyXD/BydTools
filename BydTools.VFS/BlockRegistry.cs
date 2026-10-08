using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace BydTools.VFS;

public enum ContentKind
{
    Generic,
    Table,
    Lua,
    Video,
    Audio,
}

/// <summary>
/// One row of the block catalog. Directory hashes are hints: opening a VFS
/// scans real directories, and a mismatch is reported rather than trusted.
/// </summary>
public sealed record BlockDescriptor(
    EVFSBlockType Type,
    string DirHash,
    ContentKind Kind,
    string? AudioLanguage = null
);

public static class BlockRegistry
{
    private static readonly BlockDescriptor[] Entries =
    [
        new(EVFSBlockType.InitAudio, "07A1BB91", ContentKind.Audio, "Initial"),
        new(EVFSBlockType.InitBundle, "0CE8FA57", ContentKind.Generic),
        new(EVFSBlockType.BundleManifest, "1CDDBF1F", ContentKind.Generic),
        new(EVFSBlockType.InitialExtendData, "3C9D9D2D", ContentKind.Generic),
        new(EVFSBlockType.Audio, "24ED34CF", ContentKind.Audio, "Main"),
        new(EVFSBlockType.Bundle, "7064D8E2", ContentKind.Generic),
        new(EVFSBlockType.DynamicStreaming, "23D53F5D", ContentKind.Generic),
        new(EVFSBlockType.Table, "42A8FCA6", ContentKind.Table),
        new(EVFSBlockType.Video, "55FC21C6", ContentKind.Video),
        new(EVFSBlockType.IV, "A63D7E6A", ContentKind.Generic),
        new(EVFSBlockType.Streaming, "C3442D43", ContentKind.Generic),
        new(EVFSBlockType.JsonData, "775A31D1", ContentKind.Generic),
        new(EVFSBlockType.Lua, "19E3AE45", ContentKind.Lua),
        new(EVFSBlockType.IFixPatchOut, "DAFE52C9", ContentKind.Generic),
        new(EVFSBlockType.ExtendData, "D6E622F7", ContentKind.Generic),
        new(EVFSBlockType.AudioChinese, "E1E7D7CE", ContentKind.Audio, "Chinese"),
        new(EVFSBlockType.AudioEnglish, "A31457D0", ContentKind.Audio, "English"),
        new(EVFSBlockType.AudioJapanese, "F668D4EE", ContentKind.Audio, "Japanese"),
        new(EVFSBlockType.AudioKorean, "E9D31017", ContentKind.Audio, "Korean"),
        new(EVFSBlockType.AuditAudio, "1EBAF5C6", ContentKind.Audio, "Audit"),
        new(EVFSBlockType.AuditDynamicStreaming, "B9358E30", ContentKind.Generic),
        new(EVFSBlockType.AuditIV, "06223FE2", ContentKind.Generic),
        new(EVFSBlockType.AuditStreaming, "6432320A", ContentKind.Generic),
        new(EVFSBlockType.AuditVideo, "2E6CE44D", ContentKind.Video),
        new(EVFSBlockType.HotfixAudio, "F151B649", ContentKind.Audio, "Hotfix"),
    ];

    private static readonly FrozenDictionary<EVFSBlockType, BlockDescriptor> ByType =
        Entries.ToFrozenDictionary(static d => d.Type);

    public static IReadOnlyList<BlockDescriptor> All => Entries;

    public static IEnumerable<BlockDescriptor> Audio =>
        Entries.Where(static d => d.Kind == ContentKind.Audio);

    public static bool TryGet(EVFSBlockType type, [NotNullWhen(true)] out BlockDescriptor? descriptor) =>
        ByType.TryGetValue(type, out descriptor);

    /// <summary>
    /// Accepts an enum name or a numeric value, and only when that value is catalogued.
    /// <see cref="EVFSBlockType.All"/> and <see cref="EVFSBlockType.Raw"/> are rejected,
    /// as is any number the enum parser would otherwise accept.
    /// </summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out BlockDescriptor? descriptor)
    {
        if (Enum.TryParse<EVFSBlockType>(text, ignoreCase: true, out var named) && ByType.ContainsKey(named))
        {
            descriptor = ByType[named];
            return true;
        }

        if (byte.TryParse(text, out byte numeric))
        {
            var type = (EVFSBlockType)numeric;
            if (Enum.IsDefined(type) && ByType.TryGetValue(type, out descriptor))
                return true;
        }

        descriptor = null;
        return false;
    }
}
