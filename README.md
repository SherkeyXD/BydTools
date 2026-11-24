# BydTools

> [!CAUTION]
> 请不要通过任何渠道宣传本项目，该项目仅供学习交流，严禁用于商业用途，下载后请于24小时内删除
> Please do not promote this project through any channels. This project is for learning and communication purposes only. Commercial use is strictly prohibited. Please delete it within 24 hours after downloading.

---

> [!WARNING]
> AI codes are everywhere

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) as runtime
- WAV conversion is built in for PCM, Wwise Vorbis and Wwise Opus. Other codecs are written out as `.wem`. `raw` mode skips conversion.

## Usage

> [!NOTE]
> Dumping `Bundle` or extracting audio can write a large number of small files.

```text
Usage:
  BydTools <command> [options]

Commands:
  vfs   Dump files from VFS
  pck   Extract audio from VFS
```

### vfs

```text
BydTools vfs --input <path> --output <dir> --blocktype <type>[,type2,...]

Required:
  -i, --input <path>         Game data directory that contains the VFS folder
  -o, --output <dir>         Output directory
  -t, --blocktype <type>     Block type. Commas separate several types.

Options:
  -p, --persistent <path>    Persistent directory containing VFS. Preferred over --input.
  --key <base64>             ChaCha20 key (32 bytes). Overrides --platform.
  --platform <name>          pc (default) or android, used when --key is omitted.
  --jobs <n>                 Chunks read at once. Default: processor count.
  --raw                      Write JsonData, Bundle (.ab), and BundleManifest (.hgmmap) without post-VFS decoding.
  --schema-dir <dir>         beyond-mp-schema export (overrides the embed). Ignored with --raw.
  -d, --debug                Print block declarations without extracting.
  -v, --verbose              Per-chunk and per-file details.
  -h, --help

Block types:
  InitAudio, InitBundle, InitialExtendData, BundleManifest, IFixPatchOut,
  AuditStreaming, AuditDynamicStreaming, AuditIV, AuditAudio, AuditVideo,
  Bundle, Audio, Video, IV, Streaming, DynamicStreaming, Lua, Table, JsonData,
  ExtendData, HotfixAudio, Terrain, AudioChinese, AudioEnglish, AudioJapanese,
  AudioKorean, JsonLipChinese, JsonLipEnglish, JsonLipJapanese, JsonLipKorean
```

Post-VFS handling in `vfs`:

- `InitBundle` / `Bundle` (`.ab`): Endfield VFS bundles are decrypted and repacked as standard `UnityFS` bundles (standard `UnityFS`/`UnityWeb`/`UnityRaw`/`UnityArchive` files pass through unchanged). Use `--raw` to keep the original `.ab` bytes.
- `BundleManifest` (`.hgmmap`): Brotli-decompressed and written as `<name>.hgmmap.bin` (a validated binary manifest, not parsed JSON). Use `--raw` to keep the compressed `.hgmmap` file.

### pck

Extracts `.pck` audio and, in `wav` mode, converts WEM to WAV. Names come from the Table block's AudioDialog when that block is present.

```text
BydTools pck --input <path> --output <dir> --type <type>

Required:
  -i, --input <path>         Game data directory that contains the VFS folder
  -o, --output <dir>         Output directory
  -t, --type <type>          Audio block, or a groupCfgName found in this VFS.

Options:
  -p, --persistent <path>    Persistent directory containing VFS. Preferred over --input.
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

## License

This project is licensed under [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).

This project includes code ported from or inspired by the following open-source projects:

- Original BeyondTools
- [AnimeStudio](https://github.com/Escartem/AnimeStudio)
- [AnimeWwise](https://github.com/Escartem/AnimeWwise)
- [vgmstream](https://github.com/vgmstream/vgmstream), [ww2ogg](https://github.com/hcs64/ww2ogg), [NVorbis](https://github.com/NVorbis/NVorbis) and [Concentus](https://github.com/lostromb/concentus), via `WemDecoder/`
- [CSChaCha20](https://github.com/KaarloR/CSChaCha20)
- [XXTEA](https://github.com/xxtea/xxtea-dotnet)

See [NOTICES.md](NOTICES.md) for full details.
