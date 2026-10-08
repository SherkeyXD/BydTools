namespace BydTools.VFS;

/// <summary>
/// VFS block types. Member names match groupCfgName in BLC files.
/// Numeric values follow the release client and differ from CBT3.
/// </summary>
public enum EVFSBlockType : byte
{
    All = 0,
    InitAudio = 1,
    InitBundle = 2,
    InitialExtendData = 3,
    BundleManifest = 4,
    IFixPatchOut = 5,
    AuditStreaming = 6,
    AuditDynamicStreaming = 7,
    AuditIV = 8,
    AuditAudio = 9,
    AuditVideo = 10,
    Bundle = 11,
    Audio = 12,
    Video = 13,
    IV = 14,
    Streaming = 15,
    DynamicStreaming = 16,
    Lua = 17,
    Table = 18,
    JsonData = 19,
    ExtendData = 20,
    HotfixAudio = 21,
    Raw = 100,
    AudioChinese = 101,
    AudioEnglish = 102,
    AudioJapanese = 103,
    AudioKorean = 104,
}
