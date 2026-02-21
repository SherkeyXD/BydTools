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
  -v, --verbose             Enable verbose output
  -h, --help                Show help information

Available block types:
  InitAudio, InitBundle, BundleManifest, InitialExtendData, Audio, Bundle,
  DynamicStreaming, Table, Video, IV, Streaming, JsonData, Lua, IFixPatchOut,
  ExtendData, AudioChinese, AudioEnglish, AudioJapanese, AudioKorean
```

### BydTools.PCK

Extract audio from VFS and convert WEM to WAV. Automatically maps filenames via AudioDialog.

```help
Usage:
  BydTools pck --input <path> --output <dir> --type <type> [options]

Required:
  -i, --input <path>        Game data directory that contains the VFS folder
  -o, --output <dir>        Output directory
  -t, --type <type>         Audio block type to extract

Options:
  -m, --mode <mode>         Extract mode (default: wav)
                            raw  Extract wem without conversion
                            wav  Convert to wav via vgmstream
  --no-map                  Disable automatic AudioDialog filename mapping
  -v, --verbose             Enable verbose output
  -h, --help                Show help information

Audio block types:
  InitAudio, Audio, AudioChinese, AudioEnglish, AudioJapanese, AudioKorean
```

## License

This project is licensed under [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).

This project includes code ported from or inspired by the following open-source projects:

- [AnimeStudio](https://github.com/Escartem/AnimeStudio) by Escartem — MIT
- [AnimeWwise](https://github.com/Escartem/AnimeWwise) by Escartem — CC BY-NC-SA 4.0
- [vgmstream](https://github.com/vgmstream/vgmstream) — ISC / MIT (bundled as native DLLs)
- [VGMToolbox](https://sourceforge.net/projects/vgmtoolbox/) — MIT (CRI USM demux)

See [NOTICES.md](NOTICES.md) for full details.
