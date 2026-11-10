# Beyond MemoryPack union tag maps

Generated: 2026-11-06 Asia/Shanghai

## How tags were recovered

Offline from `/home/box/Games/endfield/GameAssembly.dll` (Endfield does **not** ship classic `[MemoryPackUnion]` CustomAttributes; zmd-il2 documents AttributeDataRange as MethodDef indexes).

1. RVAs for `*ForMemoryPackFormatter.Deserialize` from `zmd-il2/out/cs/il2cpp.cs`.
2. Native switch: `cmp tag, MAX; ja; jmp [ImageBase + table + tag*4]`.
3. Large unions (ActionBase / ActionHeader / PureGetter / …) start cases as 5-byte `jmp` stubs — follow to the real body.
4. First rip-relative **TypeInfo** metadata token (`usage = 1`, bit0 set).
5. Token index → `MetadataRegistration.types[index]` → `Il2CppType.data` TypeDefIndex.
6. TypeDefIndex → `beyond-mp-schema/.../type_index.json` entry.

Regenerate: `/workspace/venv-mp/bin/python /workspace/union-maps/extract_union_maps.py`
(also copied to `beyond-mp-schema/scripts/extract_union_maps.py`).

### Exported names (BydTools `ResolveNamed`)

| Union key | Naming |
|---|---|
| **AbilityActionData** | unique `by_type` target → short leaf; ambiguous `Data` → full `wrapper_type` |
| **Data** | same jumptable as AbilityActionData, **prefer_full** wrapper / FQCN (bare `Data` static type) |
| **ActionBase**, **ActionHeader**, **PureGetter**, **AbstractBehaviorData**, **DamageProcessorBase**, **ConditionRuntimeBase** | prefer_full FQCN / wrapper |

`LookupUnionMap` matches exact key or last-segment leaf (`ActionHeader` for `Beyond.Gameplay.Actions.ActionHeader`).

## Coverage

| Union | max tag | resolved | unique names |
|---|---:|---:|---:|
| AbilityActionData | 446 | 447 | 447 |
| Data (alias) | 446 | 447 | 447 |
| ActionBase | 1363 | 1364 | 1364 |
| ActionHeader | 239 | 240 | 240 |
| PureGetter | 1107 | 1108 | 1108 |
| AbstractBehaviorData | 20 | 21 | 21 |
| DamageProcessorBase | 10 | 11 | 11 |
| ConditionRuntimeBase | 49 | 50 | 50 |

Confidence: **high** for tags inside each jumptable. Sanity: AbilityAction tag **296** → `PlayAnimationActionData`; ActionHeader **196** → `OnLeaderEnterTriggerVolume`.

## Focused decode delta (Skill/Buff/LevelScript/NPC ×400)

Input: `/workspace/focus-v5-in` (1600 files). Same binary + schemas; only `--union-map` changed.

| | AA+AB only | full union-map.json |
|---|---:|---:|
| ok | 120 | 117 |
| failed | 1080 | 1083 |
| **union_tag errors** | **184** | **2** |
| Data union | 88 | 0 |
| ActionHeader union | 73 | 1 |
| PureGetter union | 23 | 1 |
| memberCount | 565 | 697 |
| AnimationCurve | 161 | 161 |

Union-tag −182. Headline ok −3: three SkillData files progress past Data/Header tags then fail later (`AnimEventReceiver` wire, string-pool id, boolean). Remaining union misses: PureGetter tag **1195** and ActionHeader tag **254** (both **above** extracted max — second switch or different encoding).

## Gaps

- Tags above jumptable max (PureGetter 1195, ActionHeader 254).
- After unions resolve, Skill/Buff/LevelScript still hit `memberCount` / `AnimationCurve` / nested layouts.
- CreateBuffActionInput schema wire is 2; native formatter expects **5** (BuffInput base 3 + own 2) — see DECODE_FIXES.md.
- AnimationClipAsyncInfo still ~2829 non-zero memberCount mismatches on NPC montages (mis-sync before the field, not count-0).

## Files

- `union-map.json` — pass as `--union-map`
- `ability-action-union-map.json` / `action-base-union-map.json`
- `union-map.full.json` — per-tag typedef / type_index evidence
- `NOTES.json` — machine-readable coverage
- `extract_union_maps.py` — regenerator
- `focus-decode-delta.json` — focused Skill/Buff/LevelScript/NPC numbers
