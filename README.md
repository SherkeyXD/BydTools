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
    - On the full dump, ok moved from 1295 to 5685 after unmanaged `GameplayTag` / `AnimationClipAsyncInfo` / `FMontageTransitionInfo`, native `AnimationCurve` (count 3 and 28-byte keys, not an `FAnimationCurve` alias), `EffectActionData` base+wire, and `ActionHeader` wires for `OnLeaderEnterTriggerVolume`.
    - Full-dump v11 is ok **9919** (v10 was 9202, +717). SkillData +564. `BuffData` is **3160**/3394.
    - `DispelConfig` is 8 unmanaged bytes. `OrderPriorityConfig` inside `BuffIconConfig` is 12 unmanaged bytes (bool, pad, `priorityValue`, `priorityEnum`); a leading 0 is that bool, not memberCount 0. `LifeType` and `IdentifierType` are byte enums. `StackingType` and `AdditiveEffectType` are signed int16. Reading those as int32 shifted the next field and turned a later memberCount 3 into `Boolean 3`. Buff modifier lists use the concrete `*Modifier.Data` wrappers, and `BuffIconConfig` is 20 fields.
    - `ContinuousFindTargetAction.Data` is ability base + `FindTargetActionData` (14) + `findInterval` (19). `ShowHideActorData` is the 4 ability fields + its 6 leaf fields (10). `StartDialogAndTeleportAction` is `StartDialogAction` (15) plus a second `_dialogId` (16).
    - v10 wires: KeywordAction middles (Vulnerable/Enhanced 14, Weak/Slow/Speedup/Shelter 13), `EffectActionData` named fields accept the 20-field ability payload, `PlayRadioAndWait` copies `PlayRadio` (13), MVSC behaviors prefix `MVSCBehaviorData` (Effect 17), `PlayAnimationWithStep` is 30, and Char interact acts prefix ActionData/ActorAct (CharAnim 31).
    - `SelectorData.finderData` is Finder.Data. The schema collapses it to bare `Data`, so tag 7 must not use the Ability Data map (that tag is AddAIMarker). Finder tag 7 is HitBoxFinder, count 8 = TargetFinder (5) + leaf (3). Finder's highest tag is 0x19. Validator and PostProcessor fields use their own map keys the same way.
    - Still open after v11: LevelScript (~4800, `AbilitySystemReset` / `Param` / `GameCondition` are upstream cascades), Interactive (~998), and about 234 leftover BuffData files (`EffectLineCenterAction` and similar).
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

`--union-map` for an Endfield dump is `BydTools.Tests/Fixtures/MemoryPack/union-map.json`, recovered from GameAssembly formatter jumptables (tag 296 is `PlayAnimationActionData`). Keys: `AbilityActionData` 447, `Data` 447, `ActionBase` 1364, `ActionHeader` 240, `PureGetter` 1108, `AbstractBehaviorData` 21, `DamageProcessorBase` 11, `ConditionRuntimeBase` 50, `CalculationBase` 6, `Finder` (tag 7 is HitBoxFinder). Validator and PostProcessor fields look up those keys when the map has them. A flat `{ "296": "PlayAnimationActionData" }` file is still accepted as AbilityActionData only. Bare `Data` tags that name an ability-action wrapper use the same four base fields plus the leaf `wire_order` as `AbilityActionData`. `CreateBuffActionInput` is `BuffInput`'s three fields plus its own two. `GameplayTag` is a raw int32. `AnimationClipAsyncInfo` is 36 unmanaged bytes and `FMontageTransitionInfo` is 32. `UnityEngine.AnimationCurve` is memberCount 3, two wrap-mode int32s, then 28-byte keyframes (count 0 is a skipped placeholder); it is not aliased to `Beyond.FAnimationCurve`. `EffectActionData` uses the ability base plus `wire_order`. `OnLeaderEnterTriggerVolume` and similar script events use the ActionHeader wire (18), not ActionBase. Nested `NPC/MontageJson/...` paths use that folder hint. On the full JsonData dump those fixes moved ok from 1295 to 5685. `LevelInteractiveData` (and `LevelEnemyData`, `LevelNpcData`, `LevelFactoryRegionData`) is `LevelEntityData`'s 14-field wire plus its own `wire_order`, because the leaf fields start at offset 112. `CalculationBase` is a tagged union of types whose names end in `Calculation` (fork-matched when two concretes share a member count). Game condition subclasses whose first field is at offset 144 are `GameCondition`'s four members (`scopeMask`, `uniqueId`, `useCurrentScope`, `useGraphScope`) plus the subclass `_` fields and `wire_order`. `DispelConfig` is 8 raw bytes. `ContinuousFindTargetAction.Data` is 19 fields (ability base, `FindTargetActionData`, `findInterval`). `ShowHideActorData` is 10. `StartDialogAndTeleportAction` is 16. Full-dump v11 is ok 9919 (v10 was 9202) and `BuffData` is 3160/3394. `finderData` uses the Finder jumptable, not Ability Data: tag 7 is HitBoxFinder (count 8 = TargetFinder 5 + leaf 3). Still open: LevelScript (~4800), Interactive (~998), and about 234 BuffData files. `--report` overrides the default `<output>/memorypack-report.json`. Exit code `1` means the run finished and at least one file failed; the report still has ok / plain JSON / skipped / failed counts by top-level folder.

Basename `CommonSysTeleportValidationDataTable` matches the `TeleportValidationDataTable` suffix. Instance-id filenames use the folder hint (`BuffData/`, `SkillData/`, `LevelConfig/`, `LevelData/`, `LevelScriptData/`, `SpawnerConfig` → `SpawnerConfigData`, `AtmosphericNpcData` → `NpcAtmosphericDataTable`, `NPC/MontageJson` → `NPCMontageJson`, `Interactive/ModelViewStateControllerData` → `MVSCModelViewStateControllerData`, `CharInteractPerformCfgs` → `CharInteractPerformRuntimeCfg`, `AnimationConfig` → `AnimationConfigJson`, `NavMesh` → `LunaNavMeshAreaContainer`, `GPUISystemConfig` → `PrefabGroupSerializeData`). `GameplayConfig/NonGeneratedConfigs` uses the file basename. When several types share a leaf, an `IMemoryPackSerialize` root wins.

`GUIDProxy` is 16 bytes (four little-endian uint32s) with no object header; a leading 255 is null. Outside a union sandbox, memberCount 0 on a non-empty wire is an empty object, and a smaller count reads that prefix of `wire_order` (MemoryPack versioning). Inside a sandbox, count 0 is only accepted for `Beyond.Optional<T>`, `Param<T>`, and `AnimationClipAsyncInfo`, so fork-match does not treat every wrapper as a hit. `ActionBase` concretes are read as NodeBase's seven fields, then `_nextID`, then the concrete `_` fields and `wire_order` (confirmed on WaitForSeconds, Split, CallServer, SwitchInt, IfElse, StartDialog, and BlackScreenFadeIn). `GameplayTag`, `AnimationClipAsyncInfo`, and `FMontageTransitionInfo` are unmanaged (no object header). `UnityEngine.AnimationCurve` is its own layout, not an `FAnimationCurve` alias. Bare `Data` is narrowed when the field name contains finder, validator, or processor.

### Failure classes

The report's `failures` array is the coverage list. Messages fall into a few classes:

| Class | What it means |
| --- | --- |
| `union tag N … matches K concrete types` | More than one wrapper fits and the tag is not in `--union-map`. The checked-in map covers `AbilityActionData`, `Data`, `ActionBase`, `ActionHeader`, `PureGetter`, `AbstractBehaviorData`, `DamageProcessorBase`, and `ConditionRuntimeBase`. |
| `union tag N … matched no concrete wrapper` | The tag was read, and every candidate rejected the following bytes. |
| `memberCount … does not match` | The header is not that wrapper's wire. Actions use the NodeBase or ActionHeader prefix. Level entities use `LevelEntityData`. `ContinuousFindTarget` is 19, `ShowHideActorData` is 10, `StartDialogAndTeleportAction` is 16. What remains is mostly `AbilitySystemReset`, `ParamListForGraph`, and `Param`. |
| `CalculationBase union tag` | `CalculationBase` fork-matches `*Calculation`. Two concretes with the same member count stay ambiguous until a union map lists the tag. |
| `AnimationCurve memberCount` | Count 3 is pre/post wrap mode plus 28-byte keyframes. Count 0 is a skipped placeholder. Any other count is still a desync. |
| Boolean / dictionary garbage | Often a cascade from an earlier short read. |
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
