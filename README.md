# BydTools

> [!CAUTION]
> 请不要通过任何渠道宣传本项目，该项目仅供学习交流，严禁用于商业用途，下载后请于24小时内删除  
> Please do not promote this project through any channels. This project is for learning and communication purposes only. Commercial use is strictly prohibited. Please delete it within 24 hours after downloading.

---

> [!WARNING]
> AI codes are everywhere

## TODO

### VFS

- [ ] `JsonData` got some files that is not json (MemoryPack):
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
- [ ] `Video` is usm encrypted and can be decrypted by [WannaCRI](https://github.com/donmai-me/WannaCRI)

## Usage

### BydTools.VFS

Dump specific types of files from the game's VFS.

```auto
Usage:
  BydTools.CLI.exe vfs --gamepath <game_path> [--blocktype <type>] [--output <output_dir>] [-h|--help]

Arguments:
  --gamepath   Game data directory that contains the VFS folder
  --blocktype  Block type to dump, supports name or numeric value, default is all
               Available types: InitAudio, InitBundle, BundleManifest, InitialExtendData, Audio, Bundle, DynamicStreaming, Table, Video, IV, Streaming, JsonData, Lua, IFixPatchOut, ExtendData, AudioChinese, AudioEnglish, AudioJapanese, AudioKorean
  --output     Output directory, default is ./Assets next to the executable
  -h, --help   Show help information

Examples:
  BydTools.CLI vfs --gamepath /path/to/game --blocktype Bundle --output /path/to/output
  BydTools.CLI vfs --gamepath /path/to/game --blocktype Lua --output /path/to/lua
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

## License

This project is licensed under [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).

This project includes code ported from or inspired by the following open-source projects:

- [AnimeWwise](https://github.com/Escartem/AnimeWwise) by Escartem — CC BY-NC-SA 4.0
- [ww2ogg](https://github.com/hcs64/ww2ogg) by hcs — BSD-3-Clause
- [ReVorb](https://github.com/ItsBranK/ReVorb) by ItsBranK (original by Yirkha)

See [NOTICES.md](NOTICES.md) for full details.
