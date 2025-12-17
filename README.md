# BydTools

> [!WARNING]
> This project was completed with AI assistance, low quality code may be everywhere.

## TODO

### VFS

- [ ] `Json` got some files that is not json (MemoryPack format, hard to unpack):
  - AnimationConfig
  - AtmosphericNpcData
  - Interactive
  - LevelConfig
  - LevelData
  - LevelScriptData
  - LevelScriptTemplateData
  - LipSync
  - NPC
  - NavMesh
  - NonGeneratedConfigs
  - SkillData
  - SpawnerConfig
- [x] `LuaScript` is encrypted(xxtea, key is dynamically generated)
- [ ] `Video` is usm encrypted and can be decrypted by [WannaCRI](https://github.com/donmai-me/WannaCRI)

### PCK

- [ ] find a way to restore filename and filepath(maybe AudioDialog.json and AudioCueTable.json)

## Usage

### BydTools.VFS

Dump specific types of files from the game's VFS.

```auto
Usage:
  BydTools.CLI.exe vfs --gamepath <game_path> [--blocktype <type>] [--output <output_dir>] [-h|--help]

Arguments:
  --gamepath   Game data directory that contains the VFS folder
  --blocktype  Block type to dump, supports name or numeric value, default is all
               Available types: InitialAudio, InitialBundle, BundleManifest, InitialExtendData, Audio, Bundle, DynamicStreaming, TableCfg, Video, IV, Streaming, Json, LuaScript, IFixPatch, ExtendData, AudioChinese, AudioEnglish, AudioJapanese, AudioKorean
  --output     Output directory, default is ./Assets next to the executable
  -h, --help   Show help information

Examples:
  BydTools.CLI vfs --gamepath /path/to/game --blocktype Bundle --output /path/to/output
  BydTools.CLI vfs --gamepath /path/to/game --blocktype 12
  BydTools.CLI vfs --gamepath /path/to/game
```

### BydTools.PCK

Extract files from PCK archives and convert audio files.

```auto
Usage:
  BydTools.CLI.exe pck --input <pck_file> --output <output_dir> [--mode <mode>] [-h|--help]

Arguments:
  --input, -i      Input PCK file path
  --output, -o     Output directory
  --mode, -m       Extract mode: raw, ogg (default: ogg)
                   raw: Extract wem/bnk/plg files without conversion
                   ogg: Convert files to ogg, keep unconvertible files raw
  --verbose, -v    Enable verbose output
  -h, --help       Show help information

Examples:
  BydTools.CLI pck --input /path/to/file.pck --output /path/to/output
  BydTools.CLI pck --input /path/to/file.pck --output /path/to/output --mode raw
  BydTools.CLI pck --input /path/to/file.pck --output /path/to/output --mode ogg --verbose
```

## Acknowledgements

- [isHarryh](https://github.com/isHarryh) for unpacking strategy and pck processing
- [rfi/BeyondTools](https://git.crepe.moe/rfi/BeyondTools) the original repo
- [AssetRipper/BnkExtractor](https://github.com/AssetRipper/BnkExtractor) for bnk and wem processing
- [Xiph.Org Foundation](https://www.xiph.org/) for ogg processing
- Friends from discord servers
