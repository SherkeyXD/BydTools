# beyond-mp-schema

Exports the **Beyond.MemoryPack** side data Endfield (ZMD) needs so another tool can unpack VFS `JsonData`. The primary command is `export`.

This is not a full IL2CPP inspector and it does not turn MemoryPack blobs into plaintext JSON. [BydTools](https://github.com/SherkeyXD/BydTools) already decrypts the VFS (ChaCha20). It should consume the files this tool writes and do the deserialize itself.

Endfield does **not** use classic `[MemoryPackable]` custom attributes. Schema comes from `*ForMemoryPack` wrappers (mainly `MemoryPack.Beyond.dll`):

- Members are `__fieldName__` properties on the wrapper.
- **Canonical wire order** is that property metadata order (`wire_order`). It matches native `*ForMemoryPack.Deserialize` setter order, often alphabetical.
- Target instance fields (`members_from_target_fields`) are layout and offset reference only. `orders_differ` is true when the two name lists differ.
- Strings on the wire are Beyond StringPool `int32` ids (`-1` empty, `-2` null), not inline UTF-8.
- Packed configs live in VFS block `JsonData` after ChaCha20. Names still end in `.json`. A leading `{` or `[` is real JSON; anything else may be MemoryPack. `IMemoryPackSerialize` does not mean every shipped file is binary.

Example: `BambooRaftTaskData` declares `raftId` then `questConfigs`, but `wire_order` is `questConfigs` then `raftId`. `AABB` is `max` then `min`.

A reference PC export had **5025** wrappers, **41** `IMemoryPackSerialize` markers, and **934** wrappers whose wire order differs from target fields. This tree does not ship the game binaries. `cargo test` checks those rules on a synthetic metadata image and on `tests/fixtures/schemas-sample.json`.

## Build

Rust 1.83 or newer.

```text
cargo build --release
cargo test
```

Binary: `target/release/beyond-mp-schema`.

## Export (primary command)

```text
beyond-mp-schema export \
  --metadata Endfield_Data/il2cpp_data/Metadata/global-metadata.dat \
  --gameassembly GameAssembly.dll \
  --out out/memorypack
```

Or a PC install root (`GameAssembly.dll` plus `*_Data/il2cpp_data/Metadata/global-metadata.dat`):

```text
beyond-mp-schema export --game-root /path/to/Endfield --out out/memorypack
```

`--scan` skips the known MetadataRegistration RVA (`0x0DA60E20` from the image base; `0x18DA60E20` when the base is `0x180000000`) and scans `.rdata`. The known RVA is tried first.

Linux `GameAssembly.so` is rejected. This build reads the Windows PE64 DLL. No U8, Webview, or SparkBuffer output.

### Files written

| File | What it is | How BydTools should use it |
|------|------------|----------------------------|
| `schemas.json` | One record per `*ForMemoryPack` wrapper: `target_type`, `wrapper_type`, **`wire_order`**, `members_from_wrapper_props` (same order), `members_from_target_fields` (layout/offsets only), member `type`, formatter name, `orders_differ`, counts, `wire_order_rule` | After `vfs --blocktype JsonData`, deserialize each MemoryPack blob by walking `wire_order`. Ignore target field declaration order. |
| `type_index.json` | `by_type["Beyond.Gameplay.X"]` and `by_basename["X"]` for a virtual file `…/X.json`. Each member has `name`, `type`, and `kind` (`string`, `dictionary`, `list`, `vector3`, `enum`, `object`, …). `serialize_marker: true` marks an `IMemoryPackSerialize` root. Basename lists put markers first. | Map the JsonData basename to a wrapper, then read `entries[].wire`. `kind: string` is a pool id, not UTF-8. |
| `type.cs` | Compact C# dump of the same wrappers. **Declared field order is `wire_order`.** Generic `` `N `` markers are stripped for reading (`Dictionary`2<…>` → `Dictionary<…>`). | Human-readable companion to `schemas.json`. Not a compilable assembly: many nested types share the leaf name `Data`, and the class name is the wrapper leaf. |
| `README.md` | Counts for this run, the wire-order rule in Chinese and English, and the same handoff. | Keep next to the JSON when you hand the directory to BydTools. |

`generated_by` is `beyond-mp-schema`. Wrapper field names and the meaning of `wire_order` match the earlier full-inspector export. This tool does not emit `il2cpp.cs`, dummy DLLs, or IDA scripts.

## What BydTools does next

1. Dump JsonData (already implemented; do not point this tool at raw `.blc` / `.chk`):

```text
dotnet BydTools.CLI.dll vfs \
  --input <Endfield>/Endfield_Data/StreamingAssets \
  --platform pc \
  --blocktype JsonData \
  --output vfs-json
```

2. Sniff each file. Whitespace then `{` or `[` means UTF-8 JSON. Otherwise treat it as MemoryPack even when the name ends in `.json`. Skip U8/Webview `.bin` and Table SparkBuffer `.bytes`.

3. Map `Data/Json/**/{Name}.json` through `type_index.json` → `by_basename["{Name}"]`. If several types share a leaf, prefer `serialize_marker: true`.

4. Read the payload with `wire_order` (also written out as field order in `type.cs`):

```text
byte   memberCount     ; 255 = null object, 250 = wide (next int32 is the count)
then one value per wire_order entry
  bool                    1 byte
  int / uint / float      4 bytes
  long / ulong / double   8 bytes
  string                  int32 pool id (-1 empty, -2 null)
  enum                    int32 (usual underlying type)
  List`1 / T[]            int32 count (-1 null), then elements
  Dictionary`2            int32 count (-1 null), then key, value pairs
  nested *ForMemoryPack   same object header
UnityEngine.Vector2/3     packed float32 × 2 / 3 (no wrapper in this game)
Vector4 / Quaternion / Color   4× float32
```

`memberCount` is the wrapper's wire length for a plain object. Abstract bases such as `AbilityActionData` (`has_deserialize: false`) are formatter-driven; BydTools still has to follow the native formatter (a type tag, then a concrete wrapper). That logic is **not** implemented here. `schemas.json` lists every concrete `*ForMemoryPack` wrapper and its own `wire_order` so the reader can switch on the tag and then apply the matching wrapper.

5. Resolve pool ids from `Data/Json/StringPool/StringPool*.byte` (magic `USPC`, optional LZ4). Global id = `blockIndex * len(StringPool0) + localIndex`. `scripts/parse_stringpool.py` only prints that map.

## `decode` (experimental, not the contract)

`beyond-mp-schema decode` walks one simple payload using `wire_order` (object header, primitives, pool ids, lists, dictionaries, nested wrappers). It exists so the BambooRaft fixture can be checked. It does not unpack polymorphic formatters, and it is not how JsonData should be batch-converted. Extend BydTools for that.

## Union tag maps

Endfield has no classic MemoryPackUnion CustomAttributes. Offline jumptable extraction:
see `scripts/UNION_MAPS.md` and `scripts/extract_union_maps.py` (needs GameAssembly.dll + capstone).
Deliverable shape matches BydTools `--union-map`.
