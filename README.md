# BydTools

> [!CAUTION]
> 请不要通过任何渠道宣传本项目，该项目仅供学习交流，严禁用于商业用途，下载后请于24小时内删除  
> Please do not promote this project through any channels. This project is for learning and communication purposes only. Commercial use is strictly prohibited. Please delete it within 24 hours after downloading.

---

> [!WARNING]
> AI codes are everywhere

## TODO

### VFS

- [x] `JsonData` MemoryPack decode (`decode-jsondata`), using `beyond-mp-schema` `wire_order`
  - Plain UTF-8 JSON (leading `{` / `[`) is copied through
  - StringPool ids (`-1` empty, `-2` null) resolve from `StringPool*.byte` when `--string-pool` is set
  - Tables such as BambooRaft and teleport validation decode fully
  - Remaining gaps (each failed file is one row in the coverage report):
    - With `union-map.json`, `AbilityActionData` and `ActionBase` tag errors clear. Common Action memberCounts now include the NodeBase prefix, `_nextID`, and the concrete fields. Headline ok count stays about the same until `Param`, bare `Data`, and `AnimationCurve` are addressed.
    - `AnimationClipAsyncInfo` memberCount 0 is an empty object (MemoryPack defaults). Montages that still fail do so later (`AnimationCurve` or trailing bytes). Count 0 and partial counts on other types are accepted outside union sandbox; a follow-up can tighten versioning if that over-accepts.
    - `UnityEngine.AnimationCurve` has no wrapper in the export
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
  decode-jsondata   Decode a JsonData dump (MemoryPack) to plaintext JSON
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

### decode-jsondata

Reads a directory produced by `vfs --blocktype JsonData`. Schemas come from the `beyond-mp-schema` export (`schemas.json` + `type_index.json`). Member order is `wire_order`, not the target field declaration order. Wrappers that omit private fields still accept a member count that matches those fields inserted in case-insensitive alphabetical order.

```text
BydTools decode-jsondata \
  --input vfs-json \
  --output vfs-json-text \
  --schema out/memorypack/schemas.json \
  --index out/memorypack/type_index.json \
  --string-pool vfs-json/Data/Json/StringPool
```

`--union-map` for an Endfield dump is `BydTools.Tests/Fixtures/MemoryPack/union-map.json` (447 `AbilityActionData` tags and 1364 `ActionBase` tags, recovered from GameAssembly formatter jumptables; tag 296 is `PlayAnimationActionData`). A flat `{ "296": "PlayAnimationActionData" }` file is still accepted as AbilityActionData only. `--report` overrides the default `<output>/memorypack-report.json`. Exit code `1` means the run finished and at least one file failed; the report still has ok / plain JSON / skipped / failed counts by top-level folder.

Basename `CommonSysTeleportValidationDataTable` matches the `TeleportValidationDataTable` suffix. Instance-id filenames use the folder hint (`BuffData/`, `SkillData/`, `LevelConfig/`, `LevelData/`, `LevelScriptData/`, `SpawnerConfig` → `SpawnerConfigData`, `AtmosphericNpcData` → `NpcAtmosphericDataTable`, `NPC/MontageJson` → `NPCMontageJson`, `Interactive/ModelViewStateControllerData` → `MVSCModelViewStateControllerData`, `CharInteractPerformCfgs` → `CharInteractPerformRuntimeCfg`, `AnimationConfig` → `AnimationConfigJson`, `NavMesh` → `LunaNavMeshAreaContainer`, `GPUISystemConfig` → `PrefabGroupSerializeData`). `GameplayConfig/NonGeneratedConfigs` uses the file basename. When several types share a leaf, an `IMemoryPackSerialize` root wins.

`GUIDProxy` is 16 bytes (four little-endian uint32s) with no object header; a leading 255 is null. Outside a union sandbox, memberCount 0 on a non-empty wire is an empty object, and a smaller count reads that prefix of `wire_order` (MemoryPack versioning). Inside a sandbox, count 0 is only accepted for `Beyond.Optional<T>`, `Param<T>`, and `AnimationClipAsyncInfo`, so fork-match does not treat every wrapper as a hit. `ActionBase` concretes are read as NodeBase's seven fields, then `_nextID`, then the concrete `_` fields and `wire_order` (confirmed on WaitForSeconds, Split, CallServer, SwitchInt, IfElse, StartDialog, and BlackScreenFadeIn). `UnityEngine.AnimationCurve` is not aliased to `Beyond.FAnimationCurve`. Bare `Data` is narrowed when the field name contains finder, validator, or processor.

### Failure classes

The report's `failures` array is the coverage list. Messages fall into a few classes:

| Class | What it means |
| --- | --- |
| `union tag N … matches K concrete types` | More than one wrapper fits and the tag is not in `--union-map`. `AbilityActionData` and `ActionBase` tags in `union-map.json` clear this class. Other unions (`PureGetter`, `DamageProcessorBase`, bare `Data`, …) are still fork-matched. |
| `union tag N … matched no concrete wrapper` | The tag was read, and every candidate rejected the following bytes. |
| `memberCount … does not match` | The header is not that wrapper's wire. Common Action graphs now use the IL-confirmed prefix (NodeBase fields, `_nextID`, concrete `_` fields, then `wire_order`). What remains is mostly `Param`, bare `Data`, and `AnimationCurve`, not empty Action wires. |
| `AnimationClipAsyncInfo` | memberCount 0 is the empty default and is accepted. Later montage fields (`AnimationCurve`, trailing bytes) are the next failures. |
| `AnimationCurve has no Beyond wrapper` | `UnityEngine.AnimationCurve` is not in the export. |
| `No type_index basename` | The file name and folder hint did not select a root type. |

```text
BydTools decode-jsondata ... --union-map BydTools.Tests/Fixtures/MemoryPack/union-map.json
```

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
