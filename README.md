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

```
Usage:
  BydTools <command> [options]

Commands:
  vfs                       Dump files from VFS
  pck                       Extract files from PCK

Options:
  -h, --help                Show help information
```

### BydTools.VFS

Dump specific types of files from the game's VFS.

```
Usage:
  BydTools vfs --gamepath <path> --blocktype <type>[,type2,...] [options]

Required:
  --gamepath <path>         Game data directory that contains the VFS folder
  --blocktype <type>        Block type to dump (name or numeric value)
                            Multiple types can be separated by comma, e.g. Bundle,Lua,Table

Options:
  --output <dir>            Output directory (default: ./Assets)
  --debug                   Scan subfolders and print block info (no extraction)
  -v, --verbose             Enable verbose output
  -h, --help                Show help information

Available block types:
  InitAudio (1)             InitBundle (2)            BundleManifest (3)
  InitialExtendData (5)     Audio (11)                Bundle (12)
  DynamicStreaming (13)     Table (14)                Video (15)
  IV (16)                   Streaming (17)            JsonData (18)
  Lua (19)                  IFixPatchOut (21)         ExtendData (22)
  AudioChinese (30)         AudioEnglish (31)         AudioJapanese (32)
  AudioKorean (33)
```

**Examples:**

```bash
BydTools vfs --gamepath /path/to/game --blocktype Bundle --output /path/to/output
BydTools vfs --gamepath /path/to/game --blocktype Lua
BydTools vfs --gamepath /path/to/game --blocktype Bundle,Lua,Table
BydTools vfs --gamepath /path/to/game --blocktype 12
BydTools vfs --gamepath /path/to/game --blocktype Table --debug
```

### BydTools.PCK

Extract files from PCK archives and convert audio files.

```
Usage:
  BydTools pck --input <file> --output <dir> [options]

Required:
  -i, --input <file>        Input PCK file path
  -o, --output <dir>        Output directory

Options:
  -m, --mode <mode>         Extract mode (default: ogg)
                            raw  Extract wem/bnk/plg without conversion
                            ogg  Convert to ogg, keep unconvertible as raw
  -v, --verbose             Enable verbose output
  -h, --help                Show help information
```

**Examples:**

```bash
BydTools pck --input /path/to/file.pck --output /path/to/output
BydTools pck -i /path/to/file.pck -o /path/to/output --mode raw
BydTools pck -i /path/to/file.pck -o /path/to/output -m ogg --verbose
```

## License

This project is licensed under [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).

This project includes code ported from or inspired by the following open-source projects:

- [AnimeStudio](https://github.com/Escartem/AnimeStudio) by Escartem — MIT
- [AnimeWwise](https://github.com/Escartem/AnimeWwise) by Escartem — CC BY-NC-SA 4.0
- [ww2ogg](https://github.com/hcs64/ww2ogg) by hcs — BSD-3-Clause
- [ReVorb](https://github.com/ItsBranK/ReVorb) by ItsBranK (original by Yirkha)

See [NOTICES.md](NOTICES.md) for full details.
