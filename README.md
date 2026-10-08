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
- WAV conversion uses `libvgmstream` when that library sits next to the program. Tagged releases include it for `win-x64`, `linux-x64`, and `osx-arm64`. A checkout without it uses `vgmstream-cli` on `PATH`. `raw` mode needs neither.

CI builds the library from the vgmstream commit in `scripts/vgmstream.rev`. To put it in `BydTools.Audio/3rdParty` locally, run `./scripts/build-libvgmstream.sh` or `./scripts/build-vgmstream-wem-min.ps1`. Pass `VGMSTREAM_ROOT` or `-VgmstreamRoot` to compile an existing checkout instead of cloning that commit. The first Unix build also compiles FFmpeg.

## Usage

> [!NOTE]
> Dumping `Bundle` or extracting audio can write a large number of small files.

Exit codes: `0` success, `2` bad arguments, `1` a runtime failure. Ctrl+C stops the run and deletes files that were only partly written.

```text
Usage:
  BydTools <command> [options]

Commands:
  vfs             Dump files from VFS
  pck             Extract audio from VFS
```

### vfs

```text
BydTools vfs --input <path> --output <dir> --blocktype <type>[,type2,...]

Required:
  -i, --input <path>         Game data directory that contains the VFS folder
  -o, --output <dir>         Output directory
  -t, --blocktype <type>     Block type. Commas separate several types.

Options:
  --key <base64>             ChaCha20 key (32 bytes). Overrides --platform.
  --platform <name>          pc (default) or android, used when --key is omitted.
  --jobs <n>                 Chunks read at once. Default: processor count.
  -d, --debug                Print block declarations without extracting.
  -v, --verbose              Per-chunk and per-file details.
  -h, --help

Block types:
  InitAudio, InitBundle, InitialExtendData, BundleManifest, IFixPatchOut,
  AuditStreaming, AuditDynamicStreaming, AuditIV, AuditAudio, AuditVideo,
  Bundle, Audio, Video, IV, Streaming, DynamicStreaming, Lua, Table, JsonData,
  ExtendData, HotfixAudio, AudioChinese, AudioEnglish, AudioJapanese, AudioKorean
```

A directory that is not in this list can still be dumped by its `groupCfgName`. `All`, `Raw`, and numbers that are not a real block type are rejected. `--debug` prints the blocks actually present, including ones the catalog does not know yet.

### pck

Extracts `.pck` audio and, in `wav` mode, converts WEM to WAV. Names come from the Table block's AudioDialog when that block is present.

```text
BydTools pck --input <path> --output <dir> --type <type>

Required:
  -i, --input <path>         Game data directory that contains the VFS folder
  -o, --output <dir>         Output directory
  -t, --type <type>          Audio block, or a groupCfgName found in this VFS.

Options:
  -m, --mode <mode>          wav (default) or raw.
  --key <base64>             ChaCha20 key (32 bytes). Overrides --platform.
  --platform <name>          pc (default) or android.
  --jobs <n>                 WEM files converted at once. Default: processor count.
  --no-map                   Skip AudioDialog name mapping.
  -v, --verbose
  -h, --help

Audio block types:
  InitAudio, Audio, AudioChinese, AudioEnglish, AudioJapanese, AudioKorean,
  AuditAudio, HotfixAudio
```

Files with no AudioDialog match go under `<block>/unmapped/<language>/`. The language is the one stored in the PCK when it is known, otherwise the block's own language (Main, Chinese, Hotfix, and so on). `PLUG` entries are written as `.plg` in `raw` mode and skipped in `wav` mode. Extracting a USM again overwrites the previous video and audio streams instead of creating `_2`, `_3`, and so on.

## License

This project is licensed under [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).

This project includes code ported from or inspired by the following open-source projects:

- [AnimeStudio](https://github.com/Escartem/AnimeStudio)
- [AnimeWwise](https://github.com/Escartem/AnimeWwise)
- [vgmstream](https://github.com/vgmstream/vgmstream)
- [CSChaCha20](https://github.com/KaarloR/CSChaCha20)
- [XXTEA](https://github.com/xxtea/xxtea-dotnet)

See [NOTICES.md](NOTICES.md) for full details.
