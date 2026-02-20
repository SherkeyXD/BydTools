# BydTools

> [!CAUTION]
> 请不要通过任何渠道宣传本项目，该项目仅供学习交流，严禁用于商业用途，下载后请于24小时内删除  
> Please do not promote this project through any channels. This project is for learning and communication purposes only. Commercial use is strictly prohibited. Please delete it within 24 hours after downloading.

---

> [!WARNING]
> AI codes are everywhere

## TODO

### VFS

- [ ] `JsonData` (MemoryPack):
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

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) as runtime
- [ffmpeg](https://ffmpeg.org/) must be in `PATH`

## Usage

> [!NOTE]
> Some features may generate a large number of small files (e.g., `BydTools.VFS` extracting `Bundle` or `BydTools.PCK`), which could affect disk performance or write endurance.

```help
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

```help
Usage:
  BydTools vfs --input <path> --output <dir> --blocktype <type>[,type2,...] [options]

Required:
  -i, --input <path>        Game data directory that contains the VFS folder
  -o, --output <dir>        Output directory
  -t, --blocktype <type>    Block type to dump (name or numeric value)
                            Multiple types can be separated by comma, e.g. Bundle,Lua,Table

Options:
  --debug                   Scan subfolders and print block info (no extraction)
  --key <base64>            Custom ChaCha20 key in Base64
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

### BydTools.PCK

Extract files from Wwise PCK archives and convert WEM audio to WAV.

```help
Usage:
  BydTools pck --input <file> --output <dir> [options]

Required:
  -i, --input <file>        Input PCK file path
  -o, --output <dir>        Output directory

Options:
  -m, --mode <mode>         Extract mode (default: wav)
                            raw  Extract wem/bnk/plg without conversion
                            wav  Convert to wav via vgmstream
  --json <file>             JSON mapping file for ID-to-path naming
                            Format: { "id": { "path": "...", ... }, ... }
  -v, --verbose             Enable verbose output
  -h, --help                Show help information
```

## License

This project is licensed under [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).

This project includes code ported from or inspired by the following open-source projects:

- [AnimeStudio](https://github.com/Escartem/AnimeStudio) by Escartem — MIT
- [AnimeWwise](https://github.com/Escartem/AnimeWwise) by Escartem — CC BY-NC-SA 4.0
- [vgmstream](https://github.com/vgmstream/vgmstream) — ISC / MIT (bundled as native DLLs)
- [VGMToolbox](https://sourceforge.net/projects/vgmtoolbox/) — MIT (CRI USM demux)

See [NOTICES.md](NOTICES.md) for full details.
