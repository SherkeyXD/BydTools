# BydTools

> [!CAUTION]
> 请不要通过任何渠道宣传本项目，该项目仅供学习交流，严禁用于商业用途，下载后请于24小时内删除  
> Please do not promote this project through any channels. This project is for learning and communication purposes only. Commercial use is strictly prohibited. Please delete it within 24 hours after downloading.

---

> [!WARNING]
> AI codes are everywhere

## TODO

### VFS

- [x] `JsonData` MemoryPack decode (in `vfs --blocktype JsonData`, like Lua decrypt)
  - Wire layouts are the embedded beyond-schema-dumper `*.msgpack.lz4` under `BydTools.Formats/MemoryPack/Resource/`
  - Plain UTF-8 JSON (leading `{` / `[`) is copied through
  - StringPool ids (`-1` empty, `-2` null) resolve from `StringPool*.byte` in the same block
  - `decode-jsondata` re-decodes a raw dump, or overrides the schema with `--schema-dir`
  - Full JsonData dump: **19501** ok / **0** fail / 3826 plain (23327 files)
  - U8, Webview, and Table SparkBuffer are out of scope

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) as runtime
- WAV conversion is built in for PCM, Wwise Vorbis and Wwise Opus. Other codecs are written out as `.wem`. `raw` mode skips conversion.

## Usage

> [!NOTE]
> Dumping `Bundle` or extracting audio can write a large number of small files.

Exit codes: `0` success, `2` bad arguments, `1` a runtime failure. Ctrl+C stops the run and deletes files that were only partly written.

```text
Usage:
  BydTools <command> [options]

Commands:
  vfs               Dump files from VFS
  pck               Extract audio from VFS
  decode-jsondata   Re-decode a raw JsonData dump (embedded schema by default)
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

`JsonData` `.json` files are decoded to plaintext with the embedded MemoryPack schema (same idea as Lua decrypt). `StringPool*.byte` in that block is used for string ids and then copied through. Table `.bytes` still become SparkBuffer JSON.

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

### decode-jsondata

`vfs --blocktype JsonData` already writes plaintext JSON. Use this command to re-decode a **raw** dump, or to point at a newer beyond-schema-dumper export. BydTools interprets `wire_order`; it does not export schemas. The default schema is the embedded `*.msgpack.lz4` in `BydTools.Formats/MemoryPack/Resource/`.

```text
BydTools decode-jsondata \
  --input raw-jsondata \
  --output vfs-json-text
```

`--schema-dir` overrides the embed and loads `schemas` / `type_index` / `union-map` / `unmanaged` / `enums` (`.msgpack.lz4` preferred, else `.json`). `--schema` / `--index` / `--union-map` override individual files. `--string-pool` defaults to `<input>/Data/Json/StringPool` when that folder exists.

To refresh the embed after a game update:

```text
beyond-schema-dumper export \
  --game-root /path/to/Endfield \
  --format msgpack \
  --out out/memorypack
```

Copy `schemas`, `type_index`, `union-map`, `unmanaged`, and `enums` `.msgpack.lz4` into `BydTools.Formats/MemoryPack/Resource/`.

A fixture map lives at `BydTools.Tests/Fixtures/MemoryPack/union-map.json`. A flat `{ "296": "PlayAnimationActionData" }` file is still accepted as AbilityActionData only. `--report` overrides the default `<output>/memorypack-report.json`. Exit code `1` means the run finished and at least one file failed; the report still has ok / plain JSON / skipped / failed counts by top-level folder.

Basename matching uses `type_index.json` (`by_basename`, then `{segment}Data` / `{segment}Json`, then path-token scoring). When several types share a leaf, an `IMemoryPackSerialize` root wins. Member order is `wire_order`, not the target field declaration order. Per-field `collection_encoding` (`memcpy` / `formatter` / `memorypack` / `utf8`) overrides the default unmanaged layout.

### Failure classes

A current Endfield JsonData dump decodes with an empty `failures` array. If a later dump or schema misses a rule, messages look like:

| Class | What it means |
| --- | --- |
| `union tag N … matches K concrete types` | More than one wrapper fits and the tag is not in `union-map.json`. |
| `union tag N … matched no concrete wrapper` | The tag was read, and every candidate rejected the following bytes. |
| `memberCount … does not match` | The header is not that wrapper's `wire_order` (or inherited / ability payload). |
| `Boolean` / illegal dictionary count | Usually a cascade from an earlier short or long read. |
| `No type_index basename` | The file name and folder hint did not select a root type. Pass a path under `Data/Json/…`, not a bare folder of files. |

String pool files are version-1 `USPC` blocks: magic, version, flags (bit 0 = LZ4), count, raw length, stored length, then the payload. Each string is an int32 byte length and that many UTF-8 bytes, reached through an offset table. Shipped `StringPool0.byte` starts with a little-endian `extraFileCount` and then `USPC`; `StringPool1.byte` and later start at `USPC`. `StringPoolHashIndex.byte` is `SHPI` and is ignored. Global id = `blockIndex * len(StringPool0) + localIndex`.

## License

This project is licensed under [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).

This project includes code ported from or inspired by the following open-source projects:

- [AnimeStudio](https://github.com/Escartem/AnimeStudio)
- [AnimeWwise](https://github.com/Escartem/AnimeWwise)
- [vgmstream](https://github.com/vgmstream/vgmstream), [ww2ogg](https://github.com/hcs64/ww2ogg), [NVorbis](https://github.com/NVorbis/NVorbis) and [Concentus](https://github.com/lostromb/concentus), via `WemDecoder/`
- [CSChaCha20](https://github.com/KaarloR/CSChaCha20)
- [XXTEA](https://github.com/xxtea/xxtea-dotnet)

See [NOTICES.md](NOTICES.md) for full details.
