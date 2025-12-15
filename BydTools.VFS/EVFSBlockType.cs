namespace BydTools.VFS
{
    /// <summary>
    /// VFS block types as defined in the game's VFS system.
    /// Each block type corresponds to a specific category of game assets.
    /// </summary>
    public enum EVFSBlockType : byte
    {
        /// <summary>
        /// Used for dumping all block types.
        /// </summary>
        All = 0,

        /// <summary>
        /// Initial audio assets (AK Package .pck files)
        /// Hash: 07A1BB91
        /// </summary>
        InitialAudio = 1,

        /// <summary>
        /// Initial bundle assets (Asset Bundle .ab files)
        /// Hash: 0CE8FA57
        /// </summary>
        InitialBundle = 2,

        /// <summary>
        /// Bundle manifest file (Manifest Map .hgmmap)
        /// Hash: 1CDDBF1F
        /// </summary>
        BundleManifest = 3,

        /// <summary>
        /// Low shader assets
        /// </summary>
        LowShader = 4,

        /// <summary>
        /// Initial extended data (.bin files)
        /// Hash: 3C9D9D2D
        /// </summary>
        InitialExtendData = 5,

        /// <summary>
        /// Main audio assets (AK Package .pck files)
        /// Hash: 24ED34CF
        /// </summary>
        Audio = 11,

        /// <summary>
        /// Main bundle assets (Asset Bundle .ab files)
        /// Hash: 7064D8E2
        /// </summary>
        Bundle = 12,

        /// <summary>
        /// Dynamic streaming data (.bytes files)
        /// Hash: 23D53F5D
        /// </summary>
        DynamicStreaming = 13,

        /// <summary>
        /// Table/data assets (.bytes files, encrypted)
        /// Hash: 42A8FCA6
        /// </summary>
        Table = 14,

        /// <summary>
        /// Video assets (Criware USM .usm files)
        /// Hash: 55FC21C6
        /// </summary>
        Video = 15,

        /// <summary>
        /// Irradiance Volume data (.bytes files)
        /// Hash: A63D7E6A
        /// </summary>
        IV = 16,

        /// <summary>
        /// Streaming data (.bytes files)
        /// Hash: C3442D43
        /// </summary>
        Streaming = 17,

        /// <summary>
        /// JSON data (.json files, encrypted)
        /// Hash: 775A31D1
        /// </summary>
        JsonData = 18,

        /// <summary>
        /// Lua scripts (.lua files, encrypted)
        /// Hash: 19E3AE45
        /// </summary>
        Lua = 19,

        /// <summary>
        /// IFix patch output (usually empty)
        /// Hash: DAFE52C9
        /// </summary>
        IFixPatch = 21,

        /// <summary>
        /// Extended data (.bin files)
        /// Hash: D6E622F7
        /// </summary>
        ExtendData = 22,

        /// <summary>
        /// Chinese audio assets (AK Package .pck files)
        /// Hash: E1E7D7CE
        /// </summary>
        AudioChinese = 30,

        /// <summary>
        /// English audio assets (AK Package .pck files)
        /// Hash: A31457D0
        /// </summary>
        AudioEnglish = 31,

        /// <summary>
        /// Japanese audio assets (AK Package .pck files)
        /// Hash: F668D4EE
        /// </summary>
        AudioJapanese = 32,

        /// <summary>
        /// Korean audio assets (AK Package .pck files)
        /// Hash: E9D31017
        /// </summary>
        AudioKorean = 33,

        /// <summary>
        /// Raw assets
        /// </summary>
        Raw = 100
    }
}
